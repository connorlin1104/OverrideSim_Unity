using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Removing a robot from the project — all of it, in one place.
//
// Setting a robot up scatters it: a prefab in Assets/Robots, an entry in the model catalog, a bundle
// in StreamingAssets (and possibly a staged copy under Build/RobotBundles), and a button layout in
// this device's PlayerPrefs. Deleting just the prefab leaves the rest, and the leftovers are not
// inert. The catalog entry survives holding a reference to an asset that no longer exists, which
// deserializes to null — and RobotSpawner reads `entry.prefab != null`, sees false, and falls
// through to SpawnFromBundle, which loads the OLD robot out of the OLD bundle.
//
// That is worth spelling out because of how it presents: you delete a robot, rebuild it correctly,
// press Play, and watch the previous version spawn. It looks exactly like the editor caching
// something, which is the one diagnosis that sends you looking in the wrong place. It cost an
// evening once. This tool exists so the half-deleted state is not reachable by accident.
//
// WHAT IT DOES NOT TOUCH, deliberately:
//   - The source model under Assets/Models/Submitted. That file is the only thing a rebuild can
//     start from and nothing upstream can regenerate it now that CAD is not accepted — see
//     Docs/Pipeline-Dry-Run.md. Deleting a robot is a routine, repeatable act; losing its source
//     is not, so the two are kept apart.
//   - The model store, for the same reason: it holds a copy of that same file.
//   - Anything already published to the bucket. Those objects are addressed by a hash of the owner
//     code and only gsutil can remove them. This tool has no credentials and should not have any.
//
// Usage: Tools > RoboSim > Robot > Delete Robot.
// Batch: -executeMethod DeleteRobotWindow.RunBatchDelete -robot <catalog id>
public class DeleteRobotWindow : EditorWindow
{
    private const string Title = "Delete Robot";

    private int selected;
    private Vector2 scroll;

    [MenuItem("Tools/RoboSim/Robot/Delete Robot", false, 12)]
    private static void Open() => GetWindow<DeleteRobotWindow>(true, Title, true).minSize = new Vector2(460f, 380f);

    // Everything one robot owns. Built before anything is deleted so the window can show the whole
    // list and the caller can decide against it — a confirmation that only says "are you sure"
    // without saying what goes is not a confirmation.
    public class Plan
    {
        public string id = string.Empty;
        public string displayName = string.Empty;
        public string prefabPath = string.Empty;          // "" when the prefab is already gone
        public bool hasCatalogEntry;
        public readonly List<string> assetFiles = new List<string>();   // under Assets/, so AssetDatabase deletes them
        public readonly List<string> looseFiles = new List<string>();   // outside Assets/, so File.Delete
        public readonly List<string> prefKeys = new List<string>();
        public long bytes;

        public bool IsEmpty => string.IsNullOrEmpty(prefabPath) && !hasCatalogEntry &&
                               assetFiles.Count == 0 && looseFiles.Count == 0 && prefKeys.Count == 0;
    }

    // Every id worth offering: catalog entries, plus prefabs in Assets/Robots that no entry claims.
    // The second half matters — a robot whose entry was hand-deleted still has a prefab and possibly
    // a bundle, and that is precisely the mess this is for.
    //
    // "Claims" is the entry's own prefab reference, never a name match. An id and its prefab's
    // filename routinely slug apart ("360rpm-drivetrain" vs "360rpmdrivetrain"), and matching by
    // name listed the same robot twice — with the extra row deleting only the prefab and leaving
    // the dangling entry this window exists to prevent.
    public static List<string> DeletableIds()
    {
        var ids = new List<string>();
        var claimed = new HashSet<string>();
        RobotModelCatalog catalog = RoboSimPaths.LoadRobotCatalog();
        if (catalog != null && catalog.models != null)
            foreach (RobotModelCatalog.Entry entry in catalog.models)
            {
                if (entry == null) continue;
                if (!string.IsNullOrEmpty(entry.id) && !ids.Contains(entry.id)) ids.Add(entry.id);
                if (entry.prefab != null)
                    claimed.Add(AssetDatabase.GetAssetPath(entry.prefab).Replace('\\', '/'));
            }

        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            if (claimed.Contains(path.Replace('\\', '/'))) continue;
            string id = UrdfPostProcessor.Slugify(Path.GetFileNameWithoutExtension(path));
            if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    public static Plan Build(string id)
    {
        var plan = new Plan { id = id, displayName = id };
        if (string.IsNullOrEmpty(id)) return plan;

        RobotModelCatalog catalog = RoboSimPaths.LoadRobotCatalog();
        RobotModelCatalog.Entry entry =
            catalog == null || catalog.models == null
                ? null
                : catalog.models.Find(e => e != null && e.id == id);

        var bundleIds = new List<string> { id };
        if (entry != null)
        {
            plan.hasCatalogEntry = true;
            if (!string.IsNullOrEmpty(entry.displayName)) plan.displayName = entry.displayName;
            if (entry.prefab != null) plan.prefabPath = AssetDatabase.GetAssetPath(entry.prefab);

            // The bundle id is normally the catalog id, but it is stored separately and a robot that
            // was renamed can carry an older one. Sweep for both or the rename leaks a bundle.
            if (entry.bundle != null && !string.IsNullOrEmpty(entry.bundle.id) &&
                !bundleIds.Contains(entry.bundle.id))
                bundleIds.Add(entry.bundle.id);
        }

        // A dangling catalog reference resolves to null, so fall back to finding the prefab by name.
        if (string.IsNullOrEmpty(plan.prefabPath))
            foreach (string path in RoboSimPaths.RobotPrefabPaths())
                if (UrdfPostProcessor.Slugify(Path.GetFileNameWithoutExtension(path)) == id)
                {
                    plan.prefabPath = path;
                    break;
                }

        if (!string.IsNullOrEmpty(plan.prefabPath)) plan.bytes += SizeOf(plan.prefabPath);

        foreach (string bundleId in bundleIds)
        {
            string pattern = RobotBundleFormat.Sanitize(bundleId) + "-*" + RobotBundleFormat.Extension;
            Collect(Path.Combine(BuildRobotBundles.StreamingRoot, RobotBundleFormat.StreamingFolder),
                    pattern, plan, plan.assetFiles);
            Collect(BuildRobotBundles.StagingFolder, pattern, plan, plan.looseFiles);
        }

        foreach (string key in new[] { ControllerMapSettings.PrefKey(id), ControllerMapSettings.SeedPrefKey(id) })
            if (PlayerPrefs.HasKey(key)) plan.prefKeys.Add(key);

        return plan;
    }

    private static void Collect(string folder, string pattern, Plan plan, List<string> into)
    {
        if (!Directory.Exists(folder)) return;
        foreach (string file in Directory.GetFiles(folder, pattern, SearchOption.AllDirectories))
        {
            string normalized = file.Replace('\\', '/');
            if (into.Contains(normalized)) continue;
            into.Add(normalized);
            plan.bytes += SizeOf(normalized);
        }
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0L; }
    }

    // Removes everything in the plan. The catalog entry goes FIRST so the project never contains an
    // entry pointing at a prefab that has already been deleted, which is the exact broken state this
    // tool is here to prevent — including if something below throws halfway through.
    public static string Execute(Plan plan)
    {
        if (plan == null || plan.IsEmpty) return "Nothing to delete.";

        var report = new StringBuilder();
        report.AppendLine($"Deleted {plan.displayName} — {Megabytes(plan.bytes)} reclaimed.");
        report.AppendLine();

        if (plan.hasCatalogEntry)
        {
            RoboSimPaths.RemoveCatalogEntry(plan.id);
            report.AppendLine($"  catalog entry '{plan.id}'");
        }

        if (!string.IsNullOrEmpty(plan.prefabPath) && AssetDatabase.DeleteAsset(plan.prefabPath))
            report.AppendLine("  " + plan.prefabPath);

        foreach (string path in plan.assetFiles)
            if (AssetDatabase.DeleteAsset(path)) report.AppendLine("  " + path);

        foreach (string path in plan.looseFiles)
        {
            try
            {
                File.Delete(path);
                report.AppendLine("  " + path);
            }
            catch (IOException e)
            {
                report.AppendLine($"  COULD NOT DELETE {path} — {e.Message}");
            }
        }

        if (plan.prefKeys.Count > 0)
        {
            foreach (string key in plan.prefKeys) PlayerPrefs.DeleteKey(key);
            PlayerPrefs.Save();
            report.AppendLine($"  {plan.prefKeys.Count} PlayerPrefs button-map key(s)");
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        report.AppendLine();
        report.AppendLine("Left alone: the source model under Assets/Models/Submitted, the model " +
                          "store, and anything already published to the bucket (remove that with gsutil).");
        return report.ToString();
    }

    private void OnGUI()
    {
        List<string> ids = DeletableIds();
        if (ids.Count == 0)
        {
            EditorGUILayout.HelpBox("No robots in the catalog and none in " + RoboSimPaths.RobotsFolder + ".",
                MessageType.Info);
            return;
        }

        selected = Mathf.Clamp(selected, 0, ids.Count - 1);
        selected = EditorGUILayout.Popup("Robot", selected, ids.ToArray());

        Plan plan = Build(ids[selected]);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("This will remove", EditorStyles.boldLabel);

        scroll = EditorGUILayout.BeginScrollView(scroll);
        if (plan.IsEmpty)
        {
            EditorGUILayout.LabelField("nothing — no prefab, entry, bundle or saved layout found.");
        }
        else
        {
            if (plan.hasCatalogEntry) EditorGUILayout.LabelField("• catalog entry '" + plan.id + "'");
            if (!string.IsNullOrEmpty(plan.prefabPath)) EditorGUILayout.LabelField("• " + plan.prefabPath);
            foreach (string path in plan.assetFiles) EditorGUILayout.LabelField("• " + path);
            foreach (string path in plan.looseFiles) EditorGUILayout.LabelField("• " + path);
            foreach (string key in plan.prefKeys) EditorGUILayout.LabelField("• PlayerPrefs " + key);
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.LabelField("Reclaims", Megabytes(plan.bytes));

        EditorGUILayout.HelpBox(
            "The source model in Assets/Models/Submitted is kept, and so is the model store copy — " +
            "a rebuild has to start from one of them. Bundles already published to the bucket are " +
            "not reachable from here; remove those with gsutil.", MessageType.Info);

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(plan.IsEmpty))
        {
            if (GUILayout.Button("Delete " + plan.displayName, GUILayout.Height(30)) &&
                EditorUtility.DisplayDialog(Title,
                    $"Delete {plan.displayName} and everything set up for it?\n\n" +
                    Summary(plan) + "\n\nThis cannot be undone.", "Delete", "Cancel"))
            {
                string report = Execute(plan);
                Debug.Log(report);
                EditorUtility.DisplayDialog(Title, report, "OK");
                selected = 0;
            }
        }
    }

    private static string Summary(Plan plan)
    {
        int files = plan.assetFiles.Count + plan.looseFiles.Count +
                    (string.IsNullOrEmpty(plan.prefabPath) ? 0 : 1);
        var parts = new List<string>();
        if (plan.hasCatalogEntry) parts.Add("1 catalog entry");
        if (files > 0) parts.Add($"{files} file(s), {Megabytes(plan.bytes)}");
        if (plan.prefKeys.Count > 0) parts.Add($"{plan.prefKeys.Count} saved button layout(s)");
        return string.Join("\n", parts.ToArray());
    }

    private static string Megabytes(long bytes) => (bytes / (1024f * 1024f)).ToString("0.0") + " MB";

    public static void RunBatchDelete()
    {
        string id = Argument("-robot");
        if (string.IsNullOrEmpty(id))
        {
            Debug.LogError("Delete Robot: pass -robot <catalog id>.");
            EditorApplication.Exit(1);
            return;
        }

        Plan plan = Build(id);
        if (plan.IsEmpty)
        {
            Debug.LogError($"Delete Robot: nothing found for '{id}'.");
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log(Execute(plan));
    }

    private static string Argument(string flag)
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }
}
