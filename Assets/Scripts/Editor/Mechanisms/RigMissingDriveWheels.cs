using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Finds drive wheels a rigged robot never got a link for, and rigs them.
//
// A drivetrain can ship one wheel short and nothing anywhere says so. The wheel is still in the
// hierarchy, still drawn, still in the right place — it simply has no ArticulationBody, no rolling
// sphere, and no entry in RobotMotorController's arrays. Darwinbot shipped exactly that: six wheels,
// five rigged, 2 on the left rail against 3 on the right.
//
// What the driver feels is nothing like "a wheel is missing":
//   • TURNING GOES WEIRD. Per-wheel stall torque is the traction budget divided by the wheel COUNT,
//     so a 2-wheel rail makes two thirds of what a 3-wheel rail makes at every stick position. The
//     robot pulls toward the short side driving straight, and the two turn directions are not the
//     same manoeuvre any more.
//   • THE ROBOT RIDES CROOKED. The missed wheel has no collider — Generate Part Colliders finds
//     wheels with the same classifier, so it skipped the same part, and the screws and standoffs
//     around it were all skipped as fasteners. That corner of the robot rests on nothing, the
//     chassis settles onto the five wheels that are left, and it sits at an angle it never had in
//     the editor.
//   • WHEELS SPIN WITH NOTHING UNDER THEM. Tilted onto five contact points, the wheels at the high
//     corner leave the floor and free-spin, while the loaded ones do all the work.
//
// So this tool exists to make the repair one step instead of a hand rebuild, and it is written to
// be safe to run on every robot: a correctly-rigged one reports "nothing to do".
//
// It does NOT re-rig. Rig Drivetrain rebuilds the whole articulation, which on a robot that already
// has mechanisms, tuned anchors and a starting pose is a much bigger blast radius than the problem.
// This adds the missing links to the rig that is already there, through the same
// AddWheelsToDrivetrain path as the interactive escape hatch, and re-derives the drive tuning after
// so the wheel count change lands everywhere it should.
//
// Usage: Tools > RoboSim > Robot > Mechanisms > Rig Missing Drive Wheels (sweeps every robot prefab)
// Batch:  Unity -batchmode -quit -projectPath . -executeMethod RigMissingDriveWheels.RunBatch
public static class RigMissingDriveWheels
{
    private const string Title = "Rig Missing Drive Wheels";

    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Rig Missing Drive Wheels", false, 6)]
    private static void RunInteractive()
    {
        string report = Run(out int fixedRobots, out int addedWheels, out int total);
        EditorUtility.DisplayDialog(Title,
            total == 0
                ? $"No robot prefabs with a RobotMotorController under {RoboSimPaths.RobotsFolder}."
                : addedWheels == 0
                    ? $"Checked {total} robot prefab(s) — every wheel is already rigged.\n\n{report}"
                    : $"Rigged {addedWheels} missing wheel(s) across {fixedRobots} robot(s).\n\n{report}\n\n" +
                      "Now re-run Validate Drivetrain Rig, then Validate Moving Turn: the rails were " +
                      "uneven, so every turn measurement taken before this was of a different robot.",
            "OK");
    }

    // Batch entry for -executeMethod. Throws (nonzero exit) when there are no robot prefabs at all,
    // which means the folder moved rather than "nothing to do" — a clean sweep that fixes nothing is
    // the expected steady state and exits 0.
    public static void RunBatch()
    {
        string report = Run(out int fixedRobots, out int addedWheels, out int total);
        if (total == 0)
            throw new System.InvalidOperationException(
                $"{Title}: no robot prefabs with a RobotMotorController under {RoboSimPaths.RobotsFolder}.");
        Debug.Log($"{Title}: {addedWheels} wheel(s) rigged across {fixedRobots} of {total} robot(s).\n{report}");
    }

    // Sweeps every robot prefab. Returns a per-robot report; counts robots changed and wheels added.
    private static string Run(out int fixedRobots, out int addedWheels, out int total)
    {
        fixedRobots = 0;
        addedWheels = 0;
        total = 0;
        var report = new StringBuilder();

        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            // LoadPrefabContents gives an isolated, fully-instantiated copy — component references
            // inside it resolve, which the drive re-tune needs, and the open scene is untouched.
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                RobotMotorController motor = root.GetComponent<RobotMotorController>();
                if (motor == null) continue; // not a drivable robot
                total++;
                string name = Path.GetFileNameWithoutExtension(path);

                List<Transform> missing = RobotPartClassifier.FindUnriggedWheels(root);
                if (missing.Count == 0)
                {
                    report.AppendLine($"  {name}: {RailCounts(motor)} — nothing to do.");
                    continue;
                }

                var parts = new List<GameObject>();
                foreach (Transform t in missing) parts.Add(t.gameObject);

                string before = RailCounts(motor);
                // useUndo: false — a prefab opened with LoadPrefabContents lives outside the undo
                // system and registering against it logs errors.
                int added = RigDrivetrainArticulation.AddWheelsToDrivetrain(root, parts, useUndo: false);
                if (added == 0)
                {
                    report.AppendLine($"  {name}: found {missing.Count} unrigged wheel(s) but rigged none " +
                                      "— they had no renderers, or were already wired.");
                    continue;
                }

                PrefabUtility.SaveAsPrefabAsset(root, path);
                fixedRobots++;
                addedWheels += added;
                report.AppendLine($"  {name}: {before} -> {RailCounts(motor)}, rigged " +
                                  $"{Describe(missing)}.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        if (total == 0) report.AppendLine($"  (no robot prefabs found under {RoboSimPaths.RobotsFolder})");
        return report.ToString().TrimEnd();
    }

    private static string RailCounts(RobotMotorController motor)
        => $"{Count(motor.leftWheels)} left / {Count(motor.rightWheels)} right";

    private static int Count(ArticulationBody[] wheels)
    {
        if (wheels == null) return 0;
        int n = 0;
        foreach (ArticulationBody w in wheels) if (w != null) n++;
        return n;
    }

    // Names the parts, because a name-matched wheel is the one thing here a human should eyeball.
    private static string Describe(List<Transform> parts)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('\'').Append(parts[i].name).Append('\'');
        }
        return sb.ToString();
    }
}
