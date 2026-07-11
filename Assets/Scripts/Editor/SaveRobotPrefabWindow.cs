using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Saves a set-up robot as a reusable prefab under Assets/Robots/ and links it to its home-screen
// catalog entry, so the model picker's RobotSpawner actually spawns it. This is the last step of
// bringing in a new URDF robot: Set Up Imported Robot creates the catalog *entry* (so the robot
// lists in the picker) but not the *prefab* the picker spawns. This generalizes what Build Robot
// Prefabs & Spawner does for the built-in 360 drivetrain to any set-up robot.
//
// Usage: select the set-up robot root, then Tools > RoboSim > Robot > Save As Robot Prefab.
public class SaveRobotPrefabWindow : EditorWindow
{
    private const string Title = "Save As Robot Prefab";

    [SerializeField] private GameObject robotRoot;
    [SerializeField] private bool removeInlineCopy = true;

    [MenuItem("Tools/RoboSim/Robot/Save As Robot Prefab", false, 3)]
    private static void ShowWindow()
    {
        SaveRobotPrefabWindow window = GetWindow<SaveRobotPrefabWindow>(Title);
        window.minSize = new Vector2(440f, 240f);
        window.Show();
    }

    private void OnEnable()
    {
        if (robotRoot == null) robotRoot = Selection.activeGameObject;
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Saves a SET-UP robot as a prefab in Assets/Robots/ and links it to its home-screen " +
            "catalog entry, so the model picker can spawn it. Run Set Up Imported Robot first.",
            MessageType.Info);

        robotRoot = (GameObject)EditorGUILayout.ObjectField("Robot Root", robotRoot, typeof(GameObject), true);
        if (robotRoot == null)
        {
            EditorGUILayout.HelpBox("Select the robot's root GameObject.", MessageType.Warning);
            return;
        }
        if (robotRoot.GetComponent<RobotMotorController>() == null)
        {
            EditorGUILayout.HelpBox(
                $"'{robotRoot.name}' is not a set-up robot (no RobotMotorController). Run " +
                "Tools > RoboSim > Robot > Set Up Imported Robot first.", MessageType.Error);
            return;
        }

        EditorGUILayout.LabelField("Prefab path", SaveRobotPrefab.PrefabPathFor(robotRoot.name));
        EditorGUILayout.LabelField("Catalog id", UrdfPostProcessor.Slugify(robotRoot.name));
        removeInlineCopy = EditorGUILayout.Toggle(new GUIContent("Remove Inline Copy",
            "Delete this scene instance after saving. Recommended: SampleScene spawns the selected " +
            "robot at Play via RobotSpawner, so an inline copy gives you two robots. Nothing is lost " +
            "— everything is preserved in the prefab."), removeInlineCopy);

        EditorGUILayout.Space();
        if (!GUILayout.Button("Save Prefab & Link to Picker", GUILayout.Height(30))) return;

        string path = SaveRobotPrefab.PrefabPathFor(robotRoot.name);
        if (System.IO.File.Exists(path) &&
            !EditorUtility.DisplayDialog(Title, $"Overwrite the existing prefab at\n{path}?", "Overwrite", "Cancel"))
            return;

        try
        {
            string summary = SaveRobotPrefab.Run(robotRoot, removeInlineCopy);
            robotRoot = null; // may have been destroyed by removeInlineCopy
            EditorUtility.DisplayDialog(Title, summary, "OK");
        }
        catch (System.Exception e)
        {
            EditorUtility.DisplayDialog(Title, e.Message, "OK");
            Debug.LogException(e);
        }
    }
}

// Core save + link, split out so the headless validator can drive it without the window.
public static class SaveRobotPrefab
{
    private const string RobotsFolder = "Assets/Robots";
    private const string CatalogPath = "Assets/Settings/RobotModelCatalog.asset";

    public static string PrefabPathFor(string robotName) => $"{RobotsFolder}/{SanitizeFileName(robotName)}.prefab";

    // Saves `root` as a prefab and links it to its catalog entry (id = Slugify(root.name)). When
    // removeInlineCopy is true the scene instance is deleted afterward (SampleScene spawns the
    // selected robot via RobotSpawner, so an inline copy would double-spawn). Throws on failure.
    public static string Run(GameObject root, bool removeInlineCopy)
    {
        if (root == null) throw new System.ArgumentNullException(nameof(root));
        if (root.GetComponent<RobotMotorController>() == null)
            throw new System.InvalidOperationException(
                $"'{root.name}' is not a set-up robot (no RobotMotorController). Run Set Up Imported Robot first.");

        string robotName = root.name;
        string id = UrdfPostProcessor.Slugify(robotName);
        string path = PrefabPathFor(robotName);
        EnsureRobotsFolder();

        UnityEngine.SceneManagement.Scene scene = root.scene;

        GameObject prefab = removeInlineCopy
            ? PrefabUtility.SaveAsPrefabAsset(root, path)
            : PrefabUtility.SaveAsPrefabAssetAndConnect(root, path, InteractionMode.UserAction);
        if (prefab == null)
            throw new System.InvalidOperationException($"Failed to save the prefab at {path}.");

        string linkNote = LinkCatalog(id, prefab);

        if (removeInlineCopy)
        {
            Object.DestroyImmediate(root);
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
        }

        return $"Saved '{robotName}' as a prefab:\n  {path}\n\n{linkNote}" +
               (removeInlineCopy
                   ? "\n\nRemoved the inline copy — save the scene to keep it out."
                   : "\n\nKept the scene instance (now a prefab instance).");
    }

    // Points the named catalog entry's prefab at `prefab`. Returns a human-readable note.
    private static string LinkCatalog(string id, GameObject prefab)
    {
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
        if (catalog == null)
            return $"No catalog at {CatalogPath}; the picker won't list it until one exists.";
        RobotModelCatalog.Entry entry = catalog.models?.Find(e => e != null && e.id == id);
        if (entry == null)
            return $"No catalog entry '{id}' to link to — run Set Up Imported Robot (or the robot was " +
                   "renamed after setup). The prefab is saved; link it by hand in the catalog.";
        entry.prefab = prefab;
        EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        return $"Linked it to the '{id}' picker entry — the model picker now spawns it.";
    }

    private static void EnsureRobotsFolder()
    {
        if (!AssetDatabase.IsValidFolder(RobotsFolder))
            AssetDatabase.CreateFolder("Assets", "Robots");
    }

    // A safe file-name stem from the robot name: letters/digits/_/- kept, everything else collapses
    // to a single '_'. Falls back to "Robot" if nothing survives.
    private static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
        }
        string s = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(s) ? "Robot" : s;
    }

    // Headless validation for -executeMethod: imports testbot, sets it up, saves + links the prefab,
    // and asserts the prefab exists (as a full robot) and the catalog entry points at it. Cleans up
    // the prefab, catalog entry, and importer artifacts it created.
    public static void RunBatchValidate()
    {
        const string urdfAssetPath = "Assets/TestRobots/testbot.urdf";
        const string catalogEntryId = "testbot";
        string prefabPath = PrefabPathFor("testbot");

        bool hadCatalogEntry = HasCatalogEntry(catalogEntryId);
        bool hadPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null;
        try
        {
            GameObject robot = UrdfPostProcessor.ImportUrdfIntoScratchScene(urdfAssetPath);
            UrdfPostProcessor.PostProcess(robot, 10f, true, "wheel");

            string summary = Run(robot, removeInlineCopy: true);

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                throw new System.InvalidOperationException(
                    $"Save-prefab validation FAILED: no prefab at {prefabPath}.");
            if (prefab.GetComponent<RobotMotorController>() == null)
                throw new System.InvalidOperationException(
                    "Save-prefab validation FAILED: the prefab has no RobotMotorController (not a full robot).");

            RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
            RobotModelCatalog.Entry entry = catalog != null
                ? catalog.models.Find(e => e != null && e.id == catalogEntryId) : null;
            if (catalog != null && (entry == null || entry.prefab != prefab))
                throw new System.InvalidOperationException(
                    "Save-prefab validation FAILED: the catalog entry's prefab is not linked to the saved prefab.");

            Debug.Log($"Save-prefab validation PASSED: {summary.Replace('\n', ' ')}");
        }
        finally
        {
            if (!hadPrefab) AssetDatabase.DeleteAsset(prefabPath);
            AssetDatabase.DeleteAsset("Assets/TestRobots/Materials");
            AssetDatabase.DeleteAsset("Assets/TestRobots/meshes");
            if (!hadCatalogEntry)
            {
                RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
                if (catalog != null && catalog.models != null &&
                    catalog.models.RemoveAll(e => e != null && e.id == catalogEntryId) > 0)
                    EditorUtility.SetDirty(catalog);
            }
            const string samplePath = "Assets/Scenes/SampleScene.unity";
            if (System.IO.File.Exists(samplePath))
                EditorSceneManager.OpenScene(samplePath, OpenSceneMode.Single);
            else
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            AssetDatabase.SaveAssets();
        }
    }

    private static bool HasCatalogEntry(string id)
    {
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
        return catalog != null && catalog.models != null && catalog.models.Exists(e => e != null && e.id == id);
    }
}
