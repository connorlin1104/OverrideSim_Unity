using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// The assets the editor tools share: where they live, and how to reach them.
//
// The paths came first — they were re-declared as private consts in over a dozen files under four
// different names, which is exactly how a moved scene breaks half the tools and not the other half.
// The accessors underneath landed here for the same reason, having been copied into fourteen files
// (the robot-prefab sweep) and seven (the catalog lookup) before anyone counted them.
internal static class RoboSimPaths
{
    // The full competition field scene — what the batch validators open and the scene tools edit.
    public const string MainScene = "Assets/Scenes/SampleScene.unity";

    // The stripped-down field (one of each feature), built from MainScene by Build Lite Field Scene.
    // Any scene fix applied to the full field has to reach this one too, or the cheap field quietly
    // keeps the bug the expensive one was fixed for.
    public const string LiteScene = "Assets/Scenes/LiteScene.unity";

    // The generated home screen — the scene the app boots into, and the fallback Reopen Last Scene
    // On Launch falls back to. Was a private const in three separate files, which is the same
    // hazard this file was written for.
    public const string HomeScene = "Assets/Scenes/HomeScene.unity";

    // The RobotModelCatalog ScriptableObject listing every playable robot.
    public const string RobotModelCatalog = "Assets/Settings/RobotModelCatalog.asset";

    // Where the playable robot prefabs live.
    public const string RobotsFolder = "Assets/Robots";

    // The match-load piece prefabs the loaders spawn.
    public const string MatchLoadPrefabsFolder = "Assets/Models/MatchLoadPreFabs";

    // Every robot prefab, by asset path. The FindAssets call underneath was written out at eighteen
    // sites across fourteen files — twice as identically-bodied private iterators under two
    // different names (TipOverValidation.RobotPaths, ApplyDriveTuningTool.PrefabPaths).
    //
    // The IsValidFolder guard matters: FindAssets on a folder that does not exist logs a console
    // error and returns nothing, so a renamed Robots folder used to read as "no robots found" —
    // which several validators then reported as a pass over zero robots.
    public static IEnumerable<string> RobotPrefabPaths()
    {
        if (!AssetDatabase.IsValidFolder(RobotsFolder)) yield break;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { RobotsFolder }))
            yield return AssetDatabase.GUIDToAssetPath(guid);
    }

    // The same list, loaded. Callers still do their own filtering — GetComponent<RobotMotorController>()
    // for "is this driveable", GetComponentInChildren<CascadeLift>() for "does it have a lift" — because
    // what counts as a robot differs per tool and hiding that behind this would be the more confusing
    // shortcut.
    public static IEnumerable<GameObject> RobotPrefabs()
    {
        foreach (string path in RobotPrefabPaths())
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null) yield return prefab;
        }
    }

    public static RobotModelCatalog LoadRobotCatalog() =>
        AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(RobotModelCatalog);

    // Does the catalog already list this robot? The builder validators ask BEFORE registering a
    // synthetic test robot, so their cleanup removes only what the run added and never a real entry.
    // Was a private copy in seven files.
    public static bool HasCatalogEntry(string id)
    {
        RobotModelCatalog catalog = LoadRobotCatalog();
        return catalog != null && catalog.models != null &&
               catalog.models.Exists(e => e != null && e.id == id);
    }

    // The other half of that pair, in five files. Writes only when something was actually removed —
    // a validator that dirties and re-saves the catalog on every run turns a passing suite into a
    // permanent working-tree change.
    public static void RemoveCatalogEntry(string id)
    {
        RobotModelCatalog catalog = LoadRobotCatalog();
        if (catalog == null || catalog.models == null) return;
        if (catalog.models.RemoveAll(e => e != null && e.id == id) == 0) return;
        EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
    }
}
