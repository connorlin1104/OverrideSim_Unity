using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Re-bakes every robot prefab's wheel drives from DrivetrainTuning.
//
// Why this is needed at all: a robot's serialized xDrive values are written when it is rigged, so
// the four robots rigged before the drivetrain retune still carry the old forceLimit 700 /
// damping 1000. At PLAY that doesn't matter — RobotMotorController.Awake re-derives and overwrites
// everything, so gameplay is already correct. It matters in EDIT mode, where Awake never runs:
// PhysicsSmokeTest drives the wheels straight off the serialized values, so without this sweep the
// smoke test would keep measuring a drivetrain no player ever feels.
//
// Deliberately leaves maxWheelRpm, turnRate and the invert flags alone — those are genuine
// per-robot properties (240 / 240 / 300 / 360 RPM across the four), and DrivetrainTuning consumes
// gearing as an INPUT rather than overriding it.
//
// Idempotent: running it twice writes the same numbers and reports "unchanged".
public class ApplyDriveTuningTool
{
    [MenuItem("Tools/RoboSim/Robot/Advanced/Apply Drive Tuning (All Prefabs)", false, 8)]
    private static void ApplyInteractive()
    {
        string report = Run(out int changed, out int total);
        EditorUtility.DisplayDialog("Apply Drive Tuning",
            $"{changed} of {total} robot prefab(s) updated.\n\n{report}\n\n" +
            "Now re-run Tools > RoboSim > Validation > Validate Robot Physics: the drive test measures " +
            "these serialized values, so its distances will have moved.", "OK");
    }

    // Batch entry point for -executeMethod. Throws (nonzero exit) if no robot prefabs were found,
    // which in CI means the folder moved rather than "nothing to do".
    public static void RunBatch()
    {
        string report = Run(out int changed, out int total);
        if (total == 0)
            throw new System.InvalidOperationException(
                $"Apply Drive Tuning: no robot prefabs with a RobotMotorController under {RoboSimPaths.RobotsFolder}.");
        Debug.Log($"Apply Drive Tuning: {changed} of {total} prefab(s) updated.\n{report}");
    }

    private static string Run(out int changed, out int total)
    {
        changed = 0;
        total = 0;
        var report = new StringBuilder();

        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            // LoadPrefabContents gives an isolated, fully-instantiated copy: component references
            // inside it resolve normally (which DrivetrainTuning's measuring needs), and nothing
            // touches the open scene.
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                RobotMotorController motor = root.GetComponent<RobotMotorController>();
                if (motor == null) continue; // not a drivable robot; skip silently
                total++;

                if (!TryFirstWheel(motor, out ArticulationBody sample))
                {
                    report.AppendLine($"  {Path.GetFileNameWithoutExtension(path)}: no wheels wired — skipped.");
                    continue;
                }
                float beforeForce = sample.xDrive.forceLimit;
                float beforeDamping = sample.xDrive.damping;

                // useUndo: false — a prefab opened with LoadPrefabContents lives outside the undo
                // system, and registering undo against it logs errors.
                DrivetrainTuning.Result tuning =
                    RigDrivetrainArticulation.ApplyDriveTuning(root, useUndo: false);

                bool moved = !Mathf.Approximately(beforeForce, tuning.stallTorque)
                          || !Mathf.Approximately(beforeDamping, tuning.damping);
                if (moved)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    changed++;
                }
                report.AppendLine($"  {Path.GetFileNameWithoutExtension(path)} " +
                                  $"({motor.maxWheelRpm:0.} RPM): " +
                                  (moved
                                      ? $"{beforeForce:0.#}/{beforeDamping:0.#} -> " +
                                        $"{tuning.stallTorque:0.#}/{tuning.damping:0.##}, " +
                                        $"95% of top speed in {tuning.secondsTo95:0.00} s"
                                      : "unchanged"));
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        if (total == 0) report.AppendLine($"  (no robot prefabs found under {RoboSimPaths.RobotsFolder})");
        return report.ToString().TrimEnd();
    }

    private static bool TryFirstWheel(RobotMotorController motor, out ArticulationBody wheel)
    {
        wheel = null;
        if (motor.leftWheels != null)
            foreach (ArticulationBody w in motor.leftWheels) if (w != null) { wheel = w; return true; }
        if (motor.rightWheels != null)
            foreach (ArticulationBody w in motor.rightWheels) if (w != null) { wheel = w; return true; }
        return false;
    }
}
