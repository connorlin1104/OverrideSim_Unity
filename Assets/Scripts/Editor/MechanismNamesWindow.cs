using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// One table for renaming what the player sees in Configure Controller: every robot's name, and
// every mechanism's name, across the whole catalog, with no scene open.
//
// Why this exists: the names are derived from URDF link names, so they come out as things like
// "SpinningPart", "LeftSideToggle" and "PlasticClaw Flip" — which tell a player nothing about what
// the button does. Fixing one previously meant opening a scene that contains the robot, finding it
// in Robot Setup Overview, and using the inline field there. That is fine for one; it is miserable
// for twenty across four robots, which is what a round of feedback actually produces.
//
// DISPLAY ONLY. Nothing here touches a mechanism's id, so no saved ButtonMap can be orphaned and
// no binding migration is needed — that is exactly what makes it safe to do in bulk. To change an
// id (which means renaming the underlying part), use Robot Setup Overview > Edit…, which owns
// MechanismEditWindow.MigrateBindings.
//
// THE ORDER MATTERS: the prefab's RobotMechanisms registry is written FIRST and the catalog is
// mirrored from it second. UrdfPostProcessor.UpsertCatalogEntry replaces entry.mechanisms wholesale
// from the registry, so a catalog-only edit is silently erased the next time anyone re-imports the
// robot or re-runs a mechanism builder.
//
// Usage: Tools > RoboSim > Robot > Mechanism Names.
public class MechanismNamesWindow : EditorWindow
{
    private const string UndoName = "Rename Mechanisms";

    // Caption geometry, from BuildHomeScene.CreateConfigButton: the text under each controller
    // button is 220 units wide at 20pt over 3 lines, and it cannot be widened (the width was tuned
    // so the two inner diamond captions don't collide). LiberationSans averages a bit under half an
    // em per glyph for mixed-case text, so ~22 characters per line is a fair working budget. It is
    // an estimate, not a measurement — hence a warning, never a block.
    private const float CaptionWidth = 220f;
    private const float CaptionFontSize = 20f;
    private const float AverageGlyphEm = 0.5f;
    private static int CaptionCharBudget => Mathf.FloorToInt(CaptionWidth / (CaptionFontSize * AverageGlyphEm));

    private RobotModelCatalog catalog;
    private Vector2 scroll;

    // Pending edits, keyed so nothing is written until Apply. Robot names by entry id; mechanism
    // names by "entryId|mechanismId".
    private readonly Dictionary<string, string> robotEdits = new Dictionary<string, string>();
    private readonly Dictionary<string, string> mechanismEdits = new Dictionary<string, string>();

    [MenuItem("Tools/RoboSim/Robot/Mechanism Names", false, 3)]
    private static void Open()
    {
        MechanismNamesWindow window = GetWindow<MechanismNamesWindow>(false, "Mechanism Names", true);
        window.minSize = new Vector2(560f, 400f);
        window.catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(RoboSimPaths.RobotModelCatalog);
        window.robotEdits.Clear();
        window.mechanismEdits.Clear();
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
            "Renames what players see in Configure Controller. Display text only — ids, bindings and " +
            "control styles are untouched, so nothing can come unbound.\n\n" +
            "To change a mechanism's id (by renaming its part), use Robot Setup Overview > Edit… " +
            "instead; that path migrates the bindings across.", MessageType.None);

        int pending = robotEdits.Count + mechanismEdits.Count;

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(pending == 0))
            {
                if (GUILayout.Button(pending == 0 ? "Apply" : $"Apply {pending} change(s)",
                    GUILayout.Height(24)))
                {
                    ApplyAll();
                    GUIUtility.ExitGUI();
                }
                if (GUILayout.Button("Discard", GUILayout.Height(24), GUILayout.Width(90)))
                {
                    robotEdits.Clear();
                    mechanismEdits.Clear();
                    GUIUtility.ExitGUI();
                }
            }
        }

        EditorGUILayout.Space();
        scroll = EditorGUILayout.BeginScrollView(scroll);
        // catalog.models, not VisibleModels: this is authoring, so private robots must be listed
        // too. Everything player-facing has to keep using VisibleModels — RobotVisibilityValidation
        // is there to catch a slip.
        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry == null || string.IsNullOrEmpty(entry.id)) continue;
            DrawEntry(entry);
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawEntry(RobotModelCatalog.Entry entry)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                string current = robotEdits.TryGetValue(entry.id, out string pendingName)
                    ? pendingName : entry.displayName;
                string typed = EditorGUILayout.DelayedTextField(
                    new GUIContent("Robot", "The name in the model picker, and in the " +
                                            "'Controller — <name>' header."), current);
                RecordEdit(robotEdits, entry.id, typed, entry.displayName);
                GUILayout.Label(entry.id, EditorStyles.miniLabel, GUILayout.Width(160));
            }

            if (entry.prefab == null)
            {
                EditorGUILayout.HelpBox(
                    "This entry has no prefab, so mechanism names can only be written to the catalog — " +
                    "and the next re-import of the robot would overwrite them from its registry. " +
                    "Link a prefab first.", MessageType.Warning);
            }

            if (entry.mechanisms == null || entry.mechanisms.Count == 0)
            {
                EditorGUILayout.LabelField("No mechanisms — drivetrain only.", EditorStyles.miniLabel);
                return;
            }

            EditorGUI.indentLevel++;
            foreach (RobotModelCatalog.MechanismInfo mechanism in entry.mechanisms)
            {
                if (mechanism == null || string.IsNullOrEmpty(mechanism.id)) continue;
                DrawMechanism(entry, mechanism);
            }
            EditorGUI.indentLevel--;
        }
    }

    private void DrawMechanism(RobotModelCatalog.Entry entry, RobotModelCatalog.MechanismInfo mechanism)
    {
        string key = entry.id + "|" + mechanism.id;
        string saved = string.IsNullOrWhiteSpace(mechanism.displayName) ? mechanism.id : mechanism.displayName;
        string current = mechanismEdits.TryGetValue(key, out string pendingName) ? pendingName : saved;

        using (new EditorGUILayout.HorizontalScope())
        {
            string typed = EditorGUILayout.DelayedTextField(current);
            RecordEdit(mechanismEdits, key, typed, saved);
            GUILayout.Label(mechanism.type == RobotMechanisms.TypePneumatic ? "pneumatic" : "motor",
                EditorStyles.miniLabel, GUILayout.Width(64));
            GUILayout.Label(mechanism.id, EditorStyles.miniLabel, GUILayout.Width(150));
        }

        // Show the name where it actually has to fit. The controller diagram's caption is the tight
        // one, and a button can carry several mechanisms stacked on separate lines, so a long name
        // doesn't wrap — it gets clipped.
        string worstCaption = mechanism.type == RobotMechanisms.TypePneumatic
            ? ControllerMapSettings.ModeCaption(ControllerMapSettings.ModeRetract)
            : ControllerMapSettings.ModeCaption(ControllerMapSettings.ModeToggleReverse);
        string preview = $"{current} {worstCaption}";
        int budget = CaptionCharBudget;

        EditorGUI.indentLevel++;
        if (preview.Length > budget)
        {
            EditorGUILayout.HelpBox(
                $"On the controller: \"{preview}\" — about {preview.Length - budget} character(s) too " +
                $"long for the {CaptionWidth:0}-wide caption, so it will be clipped. " +
                $"Roughly {budget - worstCaption.Length - 1} characters fit.", MessageType.Warning);
        }
        else
        {
            EditorGUILayout.LabelField($"On the controller: \"{preview}\"", EditorStyles.miniLabel);
        }
        EditorGUI.indentLevel--;
    }

    // Stage an edit, or drop it again if the value has been typed back to what is already saved —
    // so the Apply count only ever reflects real changes.
    private static void RecordEdit(Dictionary<string, string> edits, string key, string typed, string saved)
    {
        if (string.IsNullOrWhiteSpace(typed)) return; // a blank name would render as bare " FWD"
        typed = typed.Trim();
        if (typed == saved) edits.Remove(key);
        else edits[key] = typed;
    }

    // --- Apply ---------------------------------------------------------------------------------

    private void ApplyAll()
    {
        Undo.RecordObject(catalog, UndoName);
        var report = new System.Text.StringBuilder();
        int renamed = 0;

        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry == null || string.IsNullOrEmpty(entry.id)) continue;

            if (robotEdits.TryGetValue(entry.id, out string newRobotName))
            {
                report.AppendLine($"  robot '{entry.displayName}' -> '{newRobotName}'");
                entry.displayName = newRobotName;
                renamed++;
            }

            // Collect this robot's mechanism renames so the prefab is opened at most once.
            var byMechanismId = new Dictionary<string, string>();
            foreach (RobotModelCatalog.MechanismInfo mechanism in entry.mechanisms ?? new List<RobotModelCatalog.MechanismInfo>())
            {
                if (mechanism == null || string.IsNullOrEmpty(mechanism.id)) continue;
                if (mechanismEdits.TryGetValue(entry.id + "|" + mechanism.id, out string newName))
                    byMechanismId[mechanism.id] = newName;
            }
            if (byMechanismId.Count == 0) continue;

            // Registry FIRST — see the header. If the prefab write fails, the catalog is left
            // alone too, so the two can't end up disagreeing.
            string missing = WriteToPrefab(entry, byMechanismId);
            if (missing != null)
            {
                report.AppendLine($"  {entry.displayName}: {missing}");
                continue;
            }

            foreach (RobotModelCatalog.MechanismInfo mechanism in entry.mechanisms)
            {
                if (mechanism == null || !byMechanismId.TryGetValue(mechanism.id, out string newName)) continue;
                report.AppendLine($"  {entry.displayName}: '{mechanism.displayName}' -> '{newName}'");
                mechanism.displayName = newName;
                renamed++;
            }
        }

        EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        robotEdits.Clear();
        mechanismEdits.Clear();

        Debug.Log($"{UndoName}: {renamed} rename(s).\n{report}", catalog);
        EditorUtility.DisplayDialog("Mechanism Names",
            renamed == 0
                ? "Nothing was renamed — see the Console for why."
                : $"Renamed {renamed} item(s).\n\n{report}",
            "OK");
    }

    // Writes the new display names into the prefab's RobotMechanisms registry. Returns null on
    // success, or a human-readable reason it couldn't.
    private static string WriteToPrefab(RobotModelCatalog.Entry entry, Dictionary<string, string> byMechanismId)
    {
        if (entry.prefab == null)
            return "no prefab linked, so the registry can't be updated — the catalog would be " +
                   "overwritten on the next re-import. Skipped.";

        string path = AssetDatabase.GetAssetPath(entry.prefab);
        if (string.IsNullOrEmpty(path))
            return "its prefab isn't an asset on disk. Skipped.";

        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            RobotMechanisms registry = root.GetComponent<RobotMechanisms>();
            if (registry == null)
                return $"'{Path.GetFileName(path)}' has no RobotMechanisms registry. Skipped.";

            var notFound = new List<string>();
            foreach (KeyValuePair<string, string> rename in byMechanismId)
            {
                RobotMechanisms.Mechanism mechanism = registry.Find(rename.Key);
                if (mechanism == null) { notFound.Add(rename.Key); continue; }
                mechanism.displayName = rename.Value;
            }

            // A mechanism the catalog lists but the prefab no longer has is stale metadata, not a
            // rename target. Renaming it in the catalog alone would look like it worked and then
            // vanish, so say so instead.
            if (notFound.Count == byMechanismId.Count)
                return $"none of those mechanisms are in the prefab any more ({string.Join(", ", notFound)}) — " +
                       "re-run the robot's setup to refresh the catalog. Skipped.";

            PrefabUtility.SaveAsPrefabAsset(root, path);
            return null;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
