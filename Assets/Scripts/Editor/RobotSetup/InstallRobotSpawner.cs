using System.IO;
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
//   - The RobotSpawner is created only if SampleScene doesn't already have one — but its asset
//     references are re-wired on EVERY run, in both field scenes. That second half is the point:
//     wiring used to be applied only on the run that CREATED the spawner, so a reference added to
//     the tool afterwards reached no existing scene and looked wired in code while being null in
//     the scene. uploadConfig sat that way through the whole life of the download route.
//   - If everything is already in place, no scene is re-saved (no churn).
//
// Usage: Tools > RoboSim > Robot > Advanced > Build Robot Prefabs & Spawner.
public static class InstallRobotSpawner
{
    private const string DrivetrainPrefabPath = RoboSimPaths.RobotsFolder + "/360RpmDrivetrain.prefab";
    private const string DrivetrainCatalogId = "360rpm-drivetrain";

    // Fallback spawn pose (the inline robot's authored pose) for the case where there is neither
    // an inline robot to read it from nor an existing spawner to keep.
    private static readonly Vector3 DefaultSpawnPosition = new Vector3(15.99f, 0.974f, 7.91f);

    [MenuItem("Tools/RoboSim/Robot/Advanced/Build Robot Prefabs & Spawner", false, 7)]
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
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(RoboSimPaths.RobotModelCatalog);

        // The lite field carries a COPY of this spawner, made when it was pruned out of SampleScene,
        // so a reference fixed here is still missing there until Build Lite Field Scene is re-run —
        // and nothing prompts anyone to re-run it. Fix it in place instead. Building that scene is
        // still Build Lite Field Scene's job; this only re-wires one that already exists. Done
        // BEFORE the main scene is opened so that open doubles as putting the editor back.
        bool liteChanged = RewireLiteFieldSpawner(catalog);

        Scene scene = EditorSceneManager.OpenScene(RoboSimPaths.MainScene, OpenSceneMode.Single);
        GameObject inlineRobot = FindRobotRoot(scene);
        GameObject existingPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DrivetrainPrefabPath);
        bool changed = false;

        // 1) Ensure the 360 prefab exists. It is built ONCE by migrating the inline robot.
        if (existingPrefab == null)
        {
            if (inlineRobot == null)
                throw new System.InvalidOperationException(
                    $"No {DrivetrainPrefabPath} and no inline robot (RobotMotorController) in " +
                    $"{RoboSimPaths.MainScene} to build it from. Re-add the robot to the scene, or restore the prefab.");
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
        else
        {
            // The scene already HAS a spawner, which used to end the tool's involvement — the two
            // branches above are the only ones that ever wired anything, so every reference added to
            // EnsureSpawner after a scene was built reached exactly the scenes that did not exist
            // yet. That is how uploadConfig came to be wired in code and null in both field scenes
            // for the whole life of the download route. Re-wiring here is what makes re-running this
            // tool the fix for that, rather than a report that nothing needs doing.
            changed |= WireReferences(spawner, catalog);
        }

        if (changed)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new System.InvalidOperationException($"Failed to save {RoboSimPaths.MainScene}.");
        }

        if (changed || liteChanged)
        {
            Debug.Log("Build Robot Prefabs & Spawner: field robot is a prefab and the field scenes " +
                      "spawn the selected model. Updated the pieces that needed it" +
                      (liteChanged ? " (including the lite field's spawner references)." : "."));
        }
        else
        {
            Debug.Log("Build Robot Prefabs & Spawner: already set up — nothing to do.");
        }
    }

    // Re-wires the lite field's spawner and saves that scene if anything changed. Returns whether it
    // wrote anything. A missing lite scene is not a failure — it is simply one that has not been
    // built. The caller opens SampleScene straight after, which is what puts the editor back.
    private static bool RewireLiteFieldSpawner(RobotModelCatalog catalog)
    {
        if (!File.Exists(RoboSimPaths.LiteScene)) return false;

        Scene lite = EditorSceneManager.OpenScene(RoboSimPaths.LiteScene, OpenSceneMode.Single);
        RobotSpawner spawner = FindSpawner(lite);
        if (spawner == null || !WireReferences(spawner, catalog)) return false;

        EditorSceneManager.MarkSceneDirty(lite);
        if (!EditorSceneManager.SaveScene(lite))
            throw new System.InvalidOperationException($"Failed to save {RoboSimPaths.LiteScene}.");
        return true;
    }

    // The robot root is the scene-root GameObject carrying the RobotMotorController.
    private static GameObject FindRobotRoot(Scene scene)
    {
        return scene.GetRootGameObjects()
            .FirstOrDefault(go => go.GetComponent<RobotMotorController>() != null);
    }

    // Searches children too: the spawner is a scene root in both field scenes today, but a nested
    // one would otherwise read as "no spawner here" and get a second one built alongside it.
    private static RobotSpawner FindSpawner(Scene scene)
    {
        return scene.GetRootGameObjects()
            .Select(go => go.GetComponentInChildren<RobotSpawner>(true))
            .FirstOrDefault(s => s != null);
    }

    // Create the spawner if missing (reuse the passed one if present), and wire the references +
    // pose. The pose is written HERE and not in WireReferences, because it is only ever known at
    // install time — a re-wire of an existing spawner must not overwrite the pose that scene was
    // authored with.
    private static void EnsureSpawner(Scene scene, RobotSpawner existing, RobotModelCatalog catalog,
        Vector3 spawnPosition, Vector3 spawnEuler)
    {
        RobotSpawner spawner = existing;
        if (spawner == null)
        {
            GameObject go = new GameObject("RobotSpawner");
            spawner = go.AddComponent<RobotSpawner>();
        }

        WireReferences(spawner, catalog);

        SerializedObject so = new SerializedObject(spawner);
        so.FindProperty("spawnPosition").vector3Value = spawnPosition;
        so.FindProperty("spawnEuler").vector3Value = spawnEuler;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // The assets a spawner needs to find a robot at all, wired on both a fresh spawner and one that
    // already existed. Returns whether anything actually changed, so a scene whose spawner is
    // already correct is not dirtied and re-saved on every run.
    private static bool WireReferences(RobotSpawner spawner, RobotModelCatalog catalog)
    {
        SerializedObject so = new SerializedObject(spawner);

        // Only used by robots that are downloaded rather than compiled in. Wired here rather than
        // left to be remembered: a missing config doesn't fail until someone selects a bundled
        // robot, and then it fails as "that robot didn't load", which points at the wrong thing.
        bool changed = SetIfDifferent(so, "catalog", catalog)
                       | SetIfDifferent(so, "uploadConfig", RoboSimPaths.LoadUploadConfig());

        if (changed) so.ApplyModifiedPropertiesWithoutUndo();
        return changed;
    }

    private static bool SetIfDifferent(SerializedObject so, string propertyName, Object value)
    {
        SerializedProperty property = so.FindProperty(propertyName);
        if (property == null || property.objectReferenceValue == value) return false;
        property.objectReferenceValue = value;
        return true;
    }

    // Links the prefab into the named catalog entry only if it isn't already linked. Returns true
    // if it changed the catalog.
    private static bool SetCatalogPrefabIfNeeded(RobotModelCatalog catalog, string id, GameObject prefab)
    {
        if (catalog == null)
        {
            Debug.LogWarning($"Build Robot Prefabs: no catalog at {RoboSimPaths.RobotModelCatalog}; prefab not linked for '{id}'.");
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
        if (!AssetDatabase.IsValidFolder(RoboSimPaths.RobotsFolder))
            AssetDatabase.CreateFolder("Assets", "Robots");
    }
}
