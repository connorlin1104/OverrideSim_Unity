using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Adds (or refreshes) the DR4B mass proxy on every robot prefab that has a DR4B lift.
//
// Why this exists rather than "just re-run Build DR4B Lift": Build starts with a full Clean and
// then rebuilds the linkage from the window's ROLE ASSIGNMENT — which parts are first-stage movers,
// which are arms, which are scoring. That assignment lives in the builder window, not in the robot,
// so a headless re-Build would rebuild the lift from empty slots and produce a different lift. This
// reads the wiring off the already-built Dr4bLift component, whose translators and rotators lists
// are exactly the parts the lift moves, and adds the ballast around them. Same reasoning as
// ApplyDriveTuningTool: bring already-built robots into line without re-authoring them.
//
// Idempotent: EnsureBallast finds and reconfigures an existing Dr4bBallast rather than stacking a
// second one, so running it twice writes the same numbers.
//
// Usage: Tools > RoboSim > Robot > Advanced > Apply DR4B Ballast (All Prefabs), or headless
//   Unity -batchmode -nographics -quit -projectPath . -executeMethod ApplyDr4bBallastTool.RunBatch
public class ApplyDr4bBallastTool
{
    [MenuItem("Tools/RoboSim/Robot/Advanced/Apply DR4B Ballast (All Prefabs)", false, 9)]
    private static void ApplyInteractive()
    {
        string report = Run(out int changed, out int total);
        EditorUtility.DisplayDialog("Apply DR4B Ballast",
            $"{changed} of {total} DR4B robot(s) updated.\n\n{report}\n\n" +
            "Now re-run Tools > RoboSim > Robot > Mass & Balance: a DR4B used to report no lift " +
            "travel at all, because it had no link that moved.", "OK");
    }

    // Batch entry for -executeMethod. A robot folder with no DR4B in it is a legitimate result
    // (the cascades have no DR4B), so unlike ApplyDriveTuningTool this does not throw on zero.
    public static void RunBatch()
    {
        string report = Run(out int changed, out int total);
        Debug.Log($"Apply DR4B Ballast: {changed} of {total} DR4B robot(s) updated.\n{report}");
    }

    private static string Run(out int changed, out int total)
    {
        changed = 0;
        total = 0;
        var report = new StringBuilder();

        foreach (string path in PrefabPaths())
        {
            // An isolated, fully-instantiated copy, so the component references EnsureBallast walks
            // (translators, rotators, each follower's pivot) resolve normally and nothing touches
            // whatever scene happens to be open.
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                Dr4bLift lift = root.GetComponentInChildren<Dr4bLift>(true);
                if (lift == null) continue;   // no DR4B on this robot; not an error
                total++;

                RobotMechanisms registry = root.GetComponent<RobotMechanisms>();
                string name = Path.GetFileNameWithoutExtension(path);
                if (registry == null)
                {
                    report.AppendLine($"  {name}: has a Dr4bLift but no RobotMechanisms — skipped.");
                    continue;
                }

                Transform chassis = lift.Chassis != null ? lift.Chassis : registry.transform;

                // useUndo: false — a prefab opened with LoadPrefabContents lives outside the undo
                // system, and registering undo against it logs errors.
                string note = Dr4bLiftSetup.EnsureBallast(registry, chassis, lift, useUndo: false);

                PrefabUtility.SaveAsPrefabAsset(root, path);
                changed++;
                report.AppendLine($"  {name}: {note}");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        if (total == 0) report.AppendLine($"  (no robot prefabs with a DR4B under {RoboSimPaths.RobotsFolder})");
        return report.ToString().TrimEnd();
    }

    private static IEnumerable<string> PrefabPaths()
    {
        if (!AssetDatabase.IsValidFolder(RoboSimPaths.RobotsFolder)) yield break;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { RoboSimPaths.RobotsFolder }))
            yield return AssetDatabase.GUIDToAssetPath(guid);
    }
}
