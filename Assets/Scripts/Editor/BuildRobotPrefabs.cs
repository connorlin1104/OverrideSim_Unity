using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Scene = UnityEngine.SceneManagement.Scene;

// Turns the field robot into a prefab and makes the home-screen model picker swap it in via a
// RobotSpawner, instead of the robot being placed directly in SampleScene.
//
// Incremental / idempotent: each run does only the work that isn't already done.
//   - The 360 prefab is created ONCE, by migrating the inline SampleScene robot; if the prefab
//     already exists it is left untouched.
//   - The catalog entry's prefab link is set only if it isn't already pointing at the prefab.
//   - The RobotSpawner is created only if SampleScene doesn't already have one.
//   - If everything is already in place, the scene is not re-saved (no churn).
//
// Usage: Tools > RoboSim > Robot > Build Robot Prefabs & Spawner.
public static class BuildRobotPrefabs
{
    private const string RobotsFolder = "Assets/Robots";
    private const string CatalogPath = "Assets/Settings/RobotModelCatalog.asset";
    private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";

    private const string DrivetrainPrefabPath = RobotsFolder + "/360RpmDrivetrain.prefab";
    private const string DrivetrainCatalogId = "360rpm-drivetrain";

    // Fallback spawn pose (the inline robot's authored pose) for the case where there is neither
    // an inline robot to read it from nor an existing spawner to keep.
    private static readonly Vector3 DefaultSpawnPosition = new Vector3(15.99f, 0.974f, 7.91f);

    [MenuItem("Tools/RoboSim/Robot/Build Robot Prefabs & Spawner", false, 4)]
    private static void BuildInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.Log("Build Robot Prefabs: cancelled at the save prompt; nothing changed.");
            return;
        }
        try
        {
            Build();
        }
        catch (System.Exception e)
        {
            EditorUtility.DisplayDialog("Build Robot Prefabs & Spawner", e.Message, "OK");
            throw;
        }
    }

    // Batch entry point for -executeMethod (throws on failure -> nonzero exit).
    public static void RunBatch() => Build();

    private static void Build()
    {
        EnsureRobotsFolder();
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);

        Scene scene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
        GameObject inlineRobot = FindRobotRoot(scene);
        GameObject existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DrivetrainPrefabPath);
        bool changed = false;

        // 1) Ensure the 360 prefab exists. It is built ONCE by migrating the inline robot.
        if (existingPrefab == null)
        {
            if (inlineRobot == null)
                throw new System.InvalidOperationException(
                    $"No {DrivetrainPrefabPath} and no inline robot (RobotMotorController) in " +
                    $"{SampleScenePath} to build it from. Re-add the robot to the scene, or restore the prefab.");
            existingPrefab = PrefabUtility.SaveAsPrefabAsset(inlineRobot, DrivetrainPrefabPath);
            if (existingPrefab == null)
                throw new System.InvalidOperationException($"Failed to save {DrivetrainPrefabPath}.");
        }

        // 2) Ensure the catalog entry links the prefab (only writes if not already linked).
        changed |= SetCatalogPrefabIfNeeded(catalog, DrivetrainCatalogId, existingPrefab);

        // 3) Ensure SampleScene has a spawner and no leftover inline robot.
        RobotSpawner spawner = FindSpawner(scene);
        if (inlineRobot != null)
        {
            // First-time migration: remember the pose, drop the inline robot, install the spawner.
            Vector3 spawnPosition = inlineRobot.transform.position;
            Vector3 spawnEuler = inlineRobot.transform.rotation.eulerAngles;
            Object.DestroyImmediate(inlineRobot);
            EnsureSpawner(scene, spawner, catalog, spawnPosition, spawnEuler);
            changed = true;
        }
        else if (spawner == null)
        {
            EnsureSpawner(scene, null, catalog, DefaultSpawnPosition, Vector3.zero);
            changed = true;
        }

        if (changed)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new System.InvalidOperationException($"Failed to save {SampleScenePath}.");
            Debug.Log("Build Robot Prefabs & Spawner: field robot is a prefab and SampleScene spawns " +
                      "the selected model. Updated the pieces that needed it.");
        }
        else
        {
            Debug.Log("Build Robot Prefabs & Spawner: already set up — nothing to do.");
        }
    }

    // The robot root is the scene-root GameObject carrying the RobotMotorController.
    private static GameObject FindRobotRoot(Scene scene)
    {
        return scene.GetRootGameObjects()
            .FirstOrDefault(go => go.GetComponent<RobotMotorController>() != null);
    }

    private static RobotSpawner FindSpawner(Scene scene)
    {
        return scene.GetRootGameObjects()
            .Select(go => go.GetComponent<RobotSpawner>())
            .FirstOrDefault(s => s != null);
    }

    // Create the spawner if missing (reuse the passed one if present), and wire the catalog + pose.
    private static void EnsureSpawner(Scene scene, RobotSpawner existing, RobotModelCatalog catalog,
        Vector3 spawnPosition, Vector3 spawnEuler)
    {
        RobotSpawner spawner = existing;
        if (spawner == null)
        {
            GameObject go = new GameObject("RobotSpawner");
            spawner = go.AddComponent<RobotSpawner>();
        }

        SerializedObject so = new SerializedObject(spawner);
        so.FindProperty("catalog").objectReferenceValue = catalog;
        so.FindProperty("spawnPosition").vector3Value = spawnPosition;
        so.FindProperty("spawnEuler").vector3Value = spawnEuler;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // Links the prefab into the named catalog entry only if it isn't already linked. Returns true
    // if it changed the catalog.
    private static bool SetCatalogPrefabIfNeeded(RobotModelCatalog catalog, string id, GameObject prefab)
    {
        if (catalog == null)
        {
            Debug.LogWarning($"Build Robot Prefabs: no catalog at {CatalogPath}; prefab not linked for '{id}'.");
            return false;
        }
        RobotModelCatalog.Entry entry = catalog.models?.Find(e => e != null && e.id == id);
        if (entry == null)
        {
            Debug.LogWarning($"Build Robot Prefabs: no catalog entry '{id}' to link the prefab to.");
            return false;
        }
        if (entry.prefab == prefab) return false; // already linked — nothing to do

        entry.prefab = prefab;
        EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        return true;
    }

    private static void EnsureRobotsFolder()
    {
        if (!AssetDatabase.IsValidFolder(RobotsFolder))
            AssetDatabase.CreateFolder("Assets", "Robots");
    }
}
