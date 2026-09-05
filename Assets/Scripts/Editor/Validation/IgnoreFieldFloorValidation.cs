using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// IgnoreFieldFloor does its whole job by NAME, and a name that matches nothing fails silently: the
// robot climbs its own roller again and nothing anywhere says so. This is the guard.
//
// It checks the two halves that can drift apart — that the shipped field still has a collider called
// what the component is looking for, and that every component on every robot is looking for that
// same thing.
public static class IgnoreFieldFloorValidation
{
    [MenuItem("Tools/RoboSim/Validate/Ignore Field Floor", false, 46)]
    public static void Validate() => ValidationUtil.RunInteractive("Ignore Field Floor", Run);

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Ignore Field Floor", Run);

    private static string Run()
    {
        var failures = new List<string>();
        int checks = 0, components = 0;

        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(RoboSimPaths.MainScene,
            UnityEditor.SceneManagement.OpenSceneMode.Single);

        var floorNames = new HashSet<string>();
        foreach (Collider c in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
            floorNames.Add(c.gameObject.name);

        checks++;
        if (!floorNames.Contains(IgnoreFieldFloor.DefaultFloorName))
            failures.Add($"the field has no collider named '{IgnoreFieldFloor.DefaultFloorName}' — " +
                         "IgnoreFieldFloor would mute nothing at all, and any low roller becomes a " +
                         "wheel again the moment the robot pitches");

        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab == null) continue;
            foreach (IgnoreFieldFloor ignore in prefab.GetComponentsInChildren<IgnoreFieldFloor>(true))
            {
                components++;
                checks++;
                if (!floorNames.Contains(ignore.floorColliderName))
                    failures.Add($"{prefab.name}/{ignore.name}: floorColliderName is " +
                                 $"'{ignore.floorColliderName}', which nothing in the field is called");

                checks++;
                if (ignore.GetComponentsInChildren<Collider>(true).Length == 0)
                    failures.Add($"{prefab.name}/{ignore.name}: has no colliders, so there is " +
                                 "nothing for it to mute — it is on the wrong link");
            }
        }

        if (failures.Count > 0)
            return $"Ignore Field Floor: FAILED ({failures.Count} of {checks} checks)\n    " +
                   string.Join("\n    ", failures);

        return $"Ignore Field Floor: PASSED ({checks} checks). {components} link(s) across the " +
               "robots are muted against the field floor, and the field still calls its floor what " +
               "they are looking for.";
    }
}
