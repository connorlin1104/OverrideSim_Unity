using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

// Dial in the look of the transparent material variants AFTER the fact — the see-through amount
// (alpha) and whether the white specular/reflection sheen is killed — without deleting and
// recreating them. Apply to just the variants you've selected (so the cup halves and the
// matchloader shell can differ) or to every variant at once.
//
// Why this exists: the first pass created the variants at half alpha with the default glossy
// surface, which reads as a milky white film. This retunes those same .mat assets in place.
public class TransparentTunerWindow : EditorWindow
{
    private float alpha = 0.12f;
    private bool killSheen = true;

    [MenuItem("Tools/RoboSim/Field & Pieces/Tune Transparency (less white)", false, 43)]
    private static void Open()
    {
        var window = GetWindow<TransparentTunerWindow>(true, "Tune Transparency");
        window.minSize = new Vector2(380f, 260f);
        window.Show();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Transparent look", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Lower alpha = more see-through (less white film — the colors behind show through).\n" +
            "'Kill white sheen' removes the specular/reflection glare that makes clear plastic read " +
            "as white from most angles.\n\n" +
            "Select the variant material(s) in Assets/Materials/Transparent — or the objects using " +
            "them — then Apply to Selected. Or Apply to All to hit every variant at once (cups and " +
            "the matchloader share this if you do).", MessageType.Info);

        alpha = EditorGUILayout.Slider(new GUIContent("Alpha (see-through)",
            "0.02 = almost invisible, 1 = solid. Matchloader shell wants very low (~0.1); cups a bit higher."),
            alpha, 0.02f, 1f);
        killSheen = EditorGUILayout.Toggle(new GUIContent("Kill white sheen",
            "Turn off specular highlights + environment reflections so the surface stops looking white."),
            killSheen);

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Apply to Selected", GUILayout.Height(32f)))
                Apply(GatherSelectedVariants());
            if (GUILayout.Button("Apply to All Variants", GUILayout.Height(32f)))
                Apply(new List<Material>(TransparentMaterialTool.AllVariants()));
        }
    }

    private static void Apply(List<Material> variants)
    {
        if (variants.Count == 0)
        {
            EditorUtility.DisplayDialog("Tune Transparency",
                "No transparent variant materials found. Select the variant .mat assets (in " +
                "Assets/Materials/Transparent) or objects using them, or use Apply to All Variants.", "OK");
            return;
        }

        var window = GetWindow<TransparentTunerWindow>();
        foreach (Material m in variants)
            TransparentMaterialTool.ApplyTransparent(m, window.alpha, window.killSheen);
        AssetDatabase.SaveAssets();

        Debug.Log($"Tune Transparency: alpha {window.alpha:0.00}, killSheen {window.killSheen} " +
                  $"applied to {variants.Count} variant(s).");
        EditorUtility.DisplayDialog("Tune Transparency",
            $"Updated {variants.Count} transparent variant(s) to alpha {window.alpha:0.00}" +
            (window.killSheen ? " with the white sheen killed." : ".") +
            "\nChange lands in the Scene view immediately (no rebuild needed).", "OK");
    }

    // Every transparent variant reachable from the current selection: variant .mat assets picked
    // directly, plus the variants used by any selected renderer.
    private static List<Material> GatherSelectedVariants()
    {
        var set = new HashSet<Material>();
        foreach (Object o in Selection.objects)
            if (o is Material m && TransparentMaterialTool.IsVariant(m)) set.Add(m);
        foreach (GameObject go in Selection.gameObjects)
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                foreach (Material m in r.sharedMaterials)
                    if (m != null && TransparentMaterialTool.IsVariant(m)) set.Add(m);
        return new List<Material>(set);
    }
}
