using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using UnityEditor.SceneManagement;

// Makes field materials transparent (the cup's clear half, the matchloader's clear guard, ...).
//
// Why a tool at all: every field/piece material is EMBEDDED inside OverrideFieldVersion3.fbx
// (Use Embedded Materials) — embedded materials are read-only, so "make it transparent" can't be
// done on the material itself. Instead this clones the material into a project asset
// (Assets/Materials/Transparent/<name>_Transparent.mat), flips the clone's URP/Lit surface to
// Transparent (alpha blend, no depth write, Transparent queue, render both faces, no shadow
// casting), and re-points renderers at the clone. Idempotent: one clone per source material,
// reused on every later run.
//
// Three menu items under Tools > RoboSim > Field & Pieces:
//   • Make Selected Renderers Transparent — swaps the materials of the selected objects (children
//     included). Works in the scene and in Prefab Mode.
//   • Replace Material With Transparent Variant (Everywhere) — takes the selected renderer's
//     material and swaps THAT MATERIAL on every renderer in the scene AND inside the 4 match-load
//     prefabs. Use this for the cup half: all same-color halves share one embedded material, so
//     selecting one cup's clear half fixes every cup at once (spawned match loads included).
//   • Restore Selected Renderers Opaque — swaps transparent variants back to their originals.
public static class TransparentMaterialTool
{
    private const string FolderRoot = "Assets/Materials";
    public const string FolderPath = "Assets/Materials/Transparent";
    private const string PrefabFolder = "Assets/Models/MatchLoadPreFabs"; // same set TunePiecePhysics touches
    private const string Suffix = "_Transparent";
    // Default see-through amount for a freshly-created variant. Low so clear plastic reads as a faint
    // tint, not a white film; fine-tune per material afterward with Tune Transparency (less white).
    private const float DefaultAlpha = 0.2f;

    // --- Menu: selected renderers only -----------------------------------------------------------

    [MenuItem("Tools/RoboSim/Field & Pieces/Make Selected Renderers Transparent", false, 40)]
    private static void MakeSelectedTransparent()
    {
        int swapped = 0;
        foreach (Renderer renderer in SelectedRenderers())
            swapped += SwapToVariants(renderer);
        ReportSelection(swapped, "made transparent",
            "Select the object(s) whose material should go transparent (e.g. the cup's clear half) first.");
    }

    [MenuItem("Tools/RoboSim/Field & Pieces/Make Selected Renderers Transparent", true)]
    [MenuItem("Tools/RoboSim/Field & Pieces/Replace Material With Transparent Variant (Everywhere)", true)]
    [MenuItem("Tools/RoboSim/Field & Pieces/Restore Selected Renderers Opaque", true)]
    private static bool HasSelection() => Selection.gameObjects.Length > 0;

    // --- Menu: everywhere the selected material is used ------------------------------------------

    [MenuItem("Tools/RoboSim/Field & Pieces/Replace Material With Transparent Variant (Everywhere)", false, 41)]
    private static void ReplaceEverywhere()
    {
        // The selection defines WHICH materials to replace; the replacement then covers the scene
        // and the match-load prefabs, because all pieces of one color share one embedded material.
        var targets = new HashSet<Material>();
        foreach (Renderer renderer in SelectedRenderers())
            foreach (Material m in renderer.sharedMaterials)
                if (m != null && !IsVariant(m)) targets.Add(m);
        if (targets.Count == 0)
        {
            EditorUtility.DisplayDialog("Replace Material With Transparent Variant",
                "Select an object using the material to replace (e.g. one cup's clear half) first.", "OK");
            return;
        }

        var variants = new Dictionary<Material, Material>();
        foreach (Material target in targets) variants[target] = EnsureTransparentVariant(target);

        int sceneSlots = 0;
        foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include))
            sceneSlots += SwapMapped(renderer, variants, useUndo: true);
        if (sceneSlots > 0) EditorSceneManager.MarkAllScenesDirty();

        int prefabSlots = ReplaceInPrefabs(variants);

        Debug.Log($"Replace Material With Transparent Variant: {targets.Count} material(s) → " +
                  $"{sceneSlots} scene renderer slot(s) + {prefabSlots} match-load prefab slot(s) swapped. " +
                  $"Variants live in {FolderPath}.");
        EditorUtility.DisplayDialog("Replace Material With Transparent Variant",
            $"Swapped {sceneSlots} scene slot(s) and {prefabSlots} match-load prefab slot(s) across " +
            $"{targets.Count} material(s). Save the scene to keep it.", "OK");
    }

    // --- Menu: restore ----------------------------------------------------------------------------

    [MenuItem("Tools/RoboSim/Field & Pieces/Restore Selected Renderers Opaque", false, 42)]
    private static void RestoreSelectedOpaque()
    {
        int restored = 0;
        foreach (Renderer renderer in SelectedRenderers())
        {
            Material[] materials = renderer.sharedMaterials;
            bool changed = false;
            for (int i = 0; i < materials.Length; i++)
            {
                Material original = FindOriginal(materials[i]);
                if (original == null) continue;
                materials[i] = original;
                changed = true;
                restored++;
            }
            if (changed) Assign(renderer, materials, useUndo: true);
        }
        ReportSelection(restored, "restored to the original opaque material",
            "Select object(s) currently using a transparent variant first.");
    }

    // --- Core: one idempotent transparent clone per source material ------------------------------

    private static Material EnsureTransparentVariant(Material src)
    {
        EnsureFolder();
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(src, out string srcGuid, out long srcFileId);
        string srcKey = $"{srcGuid}:{srcFileId}";

        // One variant per SOURCE MATERIAL, not per name: two different embedded materials can share
        // a name, and handing the second one the first one's clone would silently recolor it. The
        // recorded origin (importer userData) disambiguates; a genuine collision gets a numbered path.
        string path = $"{FolderPath}/{Sanitize(src.name)}{Suffix}.mat";
        for (int n = 2; ; n++)
        {
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing == null) break; // free path — create the variant here
            string origin = AssetImporter.GetAtPath(path)?.userData;
            if (string.IsNullOrEmpty(origin) || origin == srcKey) return existing;
            path = $"{FolderPath}/{Sanitize(src.name)}{Suffix}_{n}.mat";
        }

        Material clone = new Material(src); // copies base map/color and every other property
        clone.name = src.name + Suffix;

        // Flip the clone's URP/Lit surface to a clean, low-white transparent (shared recipe).
        ApplyTransparent(clone, DefaultAlpha, killSheen: true);

        AssetDatabase.CreateAsset(clone, path);

        // Remember which embedded material this came from (guid:fileID in the importer's userData),
        // so Restore can find the read-only original again reliably, not just by name.
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(src, out string guid, out long fileId))
        {
            AssetImporter importer = AssetImporter.GetAtPath(path);
            importer.userData = $"{guid}:{fileId}";
            importer.SaveAndReimport();
        }
        AssetDatabase.SaveAssets();
        return AssetDatabase.LoadAssetAtPath<Material>(path);
    }

    // The transparent look, shared by variant creation and the Tune Transparency retune tool.
    // alpha = how much of the surface's OWN color shows (low = mostly see-through, little white
    // film). killSheen removes URP/Lit's specular highlight + environment reflection and flattens
    // the surface — that glare is what makes clear plastic read as bright white from most angles.
    internal static void ApplyTransparent(Material m, float alpha, bool killSheen)
    {
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);   // 0=opaque, 1=transparent
        if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);       // 0=alpha blend
        if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
        if (m.HasProperty("_AlphaClip")) m.SetFloat("_AlphaClip", 0f);
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", (float)CullMode.Off); // thin shells: show both faces
        m.DisableKeyword("_ALPHATEST_ON");
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)RenderQueue.Transparent;
        m.SetShaderPassEnabled("ShadowCaster", false); // clear plastic shouldn't cast a solid shadow

        // The white "film" is mostly URP/Lit's specular + reflection glare on a smooth surface;
        // turning both off and flattening the surface leaves a faint colored tint you see through.
        if (killSheen)
        {
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_SpecularHighlights")) m.SetFloat("_SpecularHighlights", 0f);
            if (m.HasProperty("_EnvironmentReflections")) m.SetFloat("_EnvironmentReflections", 0f);
            m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            m.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
        }
        else
        {
            if (m.HasProperty("_SpecularHighlights")) m.SetFloat("_SpecularHighlights", 1f);
            if (m.HasProperty("_EnvironmentReflections")) m.SetFloat("_EnvironmentReflections", 1f);
            m.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
            m.DisableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
        }

        // The see-through amount = the base color's alpha (URP reads _BaseColor; some shaders also
        // key the legacy _Color, so set both).
        SetColorAlpha(m, "_BaseColor", alpha);
        SetColorAlpha(m, "_Color", alpha);
        EditorUtility.SetDirty(m);
    }

    private static void SetColorAlpha(Material m, string prop, float alpha)
    {
        if (!m.HasProperty(prop)) return;
        Color c = m.GetColor(prop);
        c.a = alpha;
        m.SetColor(prop, c);
    }

    // Every transparent variant material currently in the variants folder (for the retune tool).
    internal static IEnumerable<Material> AllVariants()
    {
        if (!AssetDatabase.IsValidFolder(FolderPath)) yield break;
        foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { FolderPath }))
        {
            Material m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (m != null) yield return m;
        }
    }

    // The original a variant was cloned from: resolved from the guid:fileID recorded at creation,
    // with a name-match fallback for variants created before a rename. Null when m isn't a variant.
    private static Material FindOriginal(Material m)
    {
        if (!IsVariant(m)) return null;
        string variantPath = AssetDatabase.GetAssetPath(m);
        string userData = AssetImporter.GetAtPath(variantPath)?.userData;
        if (!string.IsNullOrEmpty(userData))
        {
            string[] parts = userData.Split(':');
            if (parts.Length == 2 && long.TryParse(parts[1], out long wantedId))
            {
                string srcPath = AssetDatabase.GUIDToAssetPath(parts[0]);
                foreach (Object sub in AssetDatabase.LoadAllAssetsAtPath(srcPath))
                {
                    if (sub is Material candidate &&
                        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out _, out long id) &&
                        id == wantedId)
                        return candidate;
                }
            }
        }

        // Fallback: same-named material anywhere in the project (embedded FBX materials included).
        string originalName = m.name.EndsWith(Suffix) ? m.name.Substring(0, m.name.Length - Suffix.Length) : m.name;
        foreach (string guid in AssetDatabase.FindAssets($"t:Material {originalName}"))
        {
            foreach (Object sub in AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GUIDToAssetPath(guid)))
                if (sub is Material candidate && candidate.name == originalName && !IsVariant(candidate))
                    return candidate;
        }
        return null;
    }

    // Headless retune for the editor-closed pass: flatten every existing variant to a low-white
    // transparent (kills the specular/reflection sheen, drops alpha). The interactive Tune
    // Transparency window does the same with a chosen alpha and a selection.
    public static void RetuneAllVariantsBatch() => RetuneAll(0.2f, killSheen: true);

    public static void RetuneAll(float alpha, bool killSheen)
    {
        int n = 0;
        foreach (Material m in AllVariants())
        {
            ApplyTransparent(m, alpha, killSheen);
            Debug.Log($"  retuned {m.name} -> alpha {alpha:0.00}");
            n++;
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"Retune Transparent Variants: {n} variant(s) updated (alpha {alpha:0.00}, killSheen {killSheen}).");
        if (n == 0)
            Debug.LogWarning("Retune Transparent Variants: no variants found in " + FolderPath +
                             " — nothing to retune (make some transparent first).");
    }

    internal static bool IsVariant(Material m) =>
        m != null && AssetDatabase.GetAssetPath(m).StartsWith(FolderPath);

    // --- Renderer plumbing ------------------------------------------------------------------------

    // Every renderer under the current selection (children included) — scene objects and Prefab
    // Mode contents both arrive through Selection.gameObjects.
    private static IEnumerable<Renderer> SelectedRenderers()
    {
        var seen = new HashSet<Renderer>();
        foreach (GameObject go in Selection.gameObjects)
            foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(true))
                if (seen.Add(renderer))
                    yield return renderer;
    }

    private static int SwapToVariants(Renderer renderer)
    {
        Material[] materials = renderer.sharedMaterials;
        int swapped = 0;
        for (int i = 0; i < materials.Length; i++)
        {
            if (materials[i] == null || IsVariant(materials[i])) continue; // already transparent
            materials[i] = EnsureTransparentVariant(materials[i]);
            swapped++;
        }
        if (swapped > 0) Assign(renderer, materials, useUndo: true);
        return swapped;
    }

    private static int SwapMapped(Renderer renderer, Dictionary<Material, Material> variants, bool useUndo)
    {
        Material[] materials = renderer.sharedMaterials;
        int swapped = 0;
        for (int i = 0; i < materials.Length; i++)
        {
            if (materials[i] == null || !variants.TryGetValue(materials[i], out Material variant)) continue;
            materials[i] = variant;
            swapped++;
        }
        if (swapped > 0) Assign(renderer, materials, useUndo);
        return swapped;
    }

    private static void Assign(Renderer renderer, Material[] materials, bool useUndo)
    {
        if (useUndo) Undo.RecordObject(renderer, "Swap Transparent Material");
        renderer.sharedMaterials = materials;
        EditorUtility.SetDirty(renderer);
        if (renderer.gameObject.scene.IsValid())
            EditorSceneManager.MarkSceneDirty(renderer.gameObject.scene);
    }

    // Swap inside the match-load prefab assets so spawned pieces match the field (same prefab
    // loop TunePiecePhysics uses).
    private static int ReplaceInPrefabs(Dictionary<Material, Material> variants)
    {
        int swapped = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { PrefabFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            int here = 0;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                here += SwapMapped(renderer, variants, useUndo: false);
            if (here > 0) PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
            swapped += here;
        }
        return swapped;
    }

    // --- Small helpers ----------------------------------------------------------------------------

    private static void EnsureFolder()
    {
        if (!AssetDatabase.IsValidFolder(FolderRoot)) AssetDatabase.CreateFolder("Assets", "Materials");
        if (!AssetDatabase.IsValidFolder(FolderPath)) AssetDatabase.CreateFolder(FolderRoot, "Transparent");
    }

    private static string Sanitize(string name)
    {
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static void ReportSelection(int count, string action, string emptyHint)
    {
        if (count > 0)
        {
            Debug.Log($"TransparentMaterialTool: {count} material slot(s) {action}.");
            EditorUtility.DisplayDialog("Transparent Materials",
                $"{count} material slot(s) {action}. Save the scene (or prefab) to keep it.", "OK");
        }
        else
        {
            EditorUtility.DisplayDialog("Transparent Materials", emptyHint, "OK");
        }
    }
}
