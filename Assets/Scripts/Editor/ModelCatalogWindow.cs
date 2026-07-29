using UnityEditor;
using UnityEngine;

// The authoring surface for the robot catalog: remove an entry, and publish a robot's default
// button layout.
//
// Both of these used to live in the game, behind an "Edit Models" button on the home screen. That
// was a mistake in two different ways and it is worth writing down, because the shape of the
// mistake is easy to repeat:
//
//   - DELETE was live in player builds. There, the catalog is a read-only ScriptableObject, so the
//     removal was an in-memory edit that looked permanent and came back on the next launch. In the
//     Editor it was worse than that: the same tap ran EditorUtility.SetDirty + SaveAssets and wrote
//     the removal of a shipped public robot straight into the committed asset, with no confirmation
//     and no undo, from a red row you were one mis-tap away from.
//   - PUBLISHING DEFAULTS was already #if UNITY_EDITOR, i.e. a control that existed in one build
//     configuration and not the other. Making it a real Editor window is the same capability
//     without a compile-time hole in the UI.
//
// Editor-only by construction — this file is under Assets/Scripts/Editor, so it cannot exist in a
// build at all, rather than relying on an #if somebody could forget.
//
// Usage: Tools > RoboSim > Robot > Model Catalog.
public class ModelCatalogWindow : EditorWindow
{
    private const string UndoName = "Edit Robot Catalog";

    private RobotModelCatalog catalog;
    private Vector2 scroll;
    private string status;

    [MenuItem("Tools/RoboSim/Robot/Model Catalog", false, 4)]
    private static void Open()
    {
        ModelCatalogWindow window = GetWindow<ModelCatalogWindow>(false, "Model Catalog", true);
        window.minSize = new Vector2(560f, 380f);
        window.catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(RoboSimPaths.RobotModelCatalog);
        window.status = null;
        window.Show();
    }

    private void OnGUI()
    {
        if (catalog == null) catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(RoboSimPaths.RobotModelCatalog);
        if (catalog == null)
        {
            EditorGUILayout.HelpBox($"No robot catalog at {RoboSimPaths.RobotModelCatalog}. Run " +
                "Tools > RoboSim > Scenes > Build Home Screen to create one.", MessageType.Error);
            return;
        }

        EditorGUILayout.HelpBox(
            "Removing a model deletes only its CATALOG ENTRY. The prefab, its FBX and its meshes " +
            "stay on disk, and re-importing or re-rigging that robot will add the entry straight " +
            "back (UrdfPostProcessor.UpsertCatalogEntry). To retire a robot for good, delete its " +
            "prefab as well.\n\n" +
            "\"Make Current Bindings the Default\" publishes whatever THIS machine currently has " +
            "bound for that robot as the layout every fresh install starts with.", MessageType.None);

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);

        // catalog.models, not VisibleModels: this is authoring, so private robots must be listed.
        // Everything player-facing filters by VisibleModels, and RobotVisibilityValidation enforces
        // that — this window is the deliberate exception.
        if (catalog.models == null || catalog.models.Count == 0)
        {
            EditorGUILayout.LabelField("The catalog is empty.");
            return;
        }

        scroll = EditorGUILayout.BeginScrollView(scroll);
        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry == null) continue;
            DrawEntry(entry);
        }
        EditorGUILayout.EndScrollView();
    }

    // Run `action` once OnGUI has returned.
    //
    // IMGUI runs OnGUI twice per frame — a Layout pass that counts the controls and a Repaint pass
    // that draws them — and both must produce the SAME sequence of layout groups. Removing an entry
    // changes how many boxes exist, and DisplayDialog pumps events mid-pass; doing either from
    // inside a button branch makes the two passes disagree, which is what Unity is complaining
    // about when it logs "EndLayoutGroup: BeginLayoutGroup must be called first" and "you are
    // pushing more GUIClips than you are popping". delayCall runs after OnGUI has finished, where
    // there is no IMGUI state left to corrupt.
    private void Defer(System.Action action)
    {
        EditorApplication.delayCall += () =>
        {
            if (this == null) return; // window closed between the click and the callback
            action();
            Repaint();
        };
    }

    private void DrawEntry(RobotModelCatalog.Entry entry)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField(entry.displayName, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(entry.id, EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Prefab", entry.prefab, typeof(GameObject), false);

            int mechanisms = entry.mechanisms != null ? entry.mechanisms.Count : 0;
            int defaults = entry.HasDefaultButtonMap ? entry.defaultButtonMap.assignments.Count : 0;
            EditorGUILayout.LabelField(
                $"{(entry.visibility == RobotModelCatalog.Visibility.Public ? "Public" : "Private")}  ·  " +
                $"{mechanisms} mechanism(s)  ·  " +
                (defaults > 0 ? $"{defaults} default button(s)" : "no shipped default layout"),
                EditorStyles.miniLabel);

            // Deferred, never called inline — see Defer.
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Make Current Bindings the Default"))
                    Defer(() => PublishDefaults(entry));
                if (GUILayout.Button("Remove from Catalog", GUILayout.Width(170f)))
                    Defer(() => Remove(entry));
            }
        }
    }

    // Publishes this machine's current layout for the robot as the shipped default.
    //
    // Until this existed there was no shipped layer at all: MechanismAutoDetect writes button
    // assignments into the AUTHORING machine's PlayerPrefs, which never reach a build, so a fresh
    // install picked a robot and got a completely unbound controller.
    private void PublishDefaults(RobotModelCatalog.Entry entry)
    {
        ButtonMap current = ControllerMapSettings.Load(entry.id);
        if (current.assignments.Count == 0)
        {
            status = $"'{entry.displayName}' has nothing bound on this machine, so there is no " +
                     "layout to publish. Bind it in Configure Controller first (Play the home " +
                     "screen, pick the robot, Configure Controller), then come back.";
            return;
        }

        Undo.RecordObject(catalog, UndoName);
        // Clone, don't assign: the loaded map is mutated in place by the config screen, so handing
        // the same object to the asset would let a later edit silently rewrite the shipped default.
        entry.defaultButtonMap = ControllerMapSettings.Clone(current);

        // Keep THIS machine in step with what it just published. ControllerMapSettings.SeedDefault
        // decides whether a device's saved map is "untouched" by comparing it against this stamp;
        // without the write, the authoring machine reads as deliberately customised and never picks
        // up a later change to the default it published itself.
        PlayerPrefs.SetString(ControllerMapSettings.SeedPrefKey(entry.id),
            JsonUtility.ToJson(entry.defaultButtonMap));
        PlayerPrefs.Save();

        EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        status = $"'{entry.displayName}': {current.assignments.Count} button(s) are now the default " +
                 "for every player.";
        Debug.Log($"Model Catalog: published {current.assignments.Count} default button(s) for " +
                  $"'{entry.displayName}' ({entry.id}).", catalog);
    }

    private void Remove(RobotModelCatalog.Entry entry)
    {
        if (!EditorUtility.DisplayDialog("Remove from Catalog",
                $"Remove '{entry.displayName}' ({entry.id}) from the robot catalog?\n\n" +
                "Only the catalog entry goes. The prefab and its meshes stay on disk, and " +
                "re-importing or re-rigging this robot will add the entry back.",
                "Remove", "Cancel"))
            return;

        Undo.RecordObject(catalog, UndoName);
        int removed = catalog.models.RemoveAll(e => e != null && e.id == entry.id);
        if (removed == 0) return;

        // The saved bindings would otherwise sit in PlayerPrefs forever under an id nothing reads.
        // Offered rather than done, because an entry removed by accident is trivially re-imported
        // while a layout someone spent ten minutes on is not.
        if (EditorUtility.DisplayDialog("Saved Bindings",
                $"Also clear this machine's saved button layout for '{entry.id}'?",
                "Clear", "Keep"))
        {
            PlayerPrefs.DeleteKey(ControllerMapSettings.PrefKey(entry.id));
            PlayerPrefs.DeleteKey(ControllerMapSettings.SeedPrefKey(entry.id));
            PlayerPrefs.Save();
        }

        EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        status = $"Removed '{entry.displayName}' from the catalog. The prefab is untouched.";
        Debug.Log($"Model Catalog: removed catalog entry '{entry.id}'.", catalog);
    }
}
