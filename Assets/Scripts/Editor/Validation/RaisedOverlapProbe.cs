using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// TEMPORARY probe, not a validator. Answers one question:
//
//   RobotMotorController.Initialise scans for parts jammed inside each other and tells PhysX to
//   ignore those pairs. It runs ONCE, at Awake, with every lift DOWN. Nothing re-runs it. So a pair
//   that only overlaps once the lift is RAISED is never ignored — in the harness or in the game —
//   and 66 Hz contact chatter between two links that cannot separate is exactly what "raised is
//   very rough, not flat" describes.
//
// Prints, per robot, what is penetrating at the raised pose that was clear at rest.
public static class RaisedOverlapProbe
{
    private const int SettleSteps = 200;
    private const int LiftRampSteps = 200;

    [MenuItem("Tools/RoboSim/Validate/Probes/Raised Lift Overlaps", false, 71)]
    private static void RunInteractive() => ValidationUtil.RunInteractive("Probe Raised Overlaps", Run);

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Probe Raised Overlaps", Run);

    private static string Run()
    {
        // Physics.simulationMode is a PROJECT setting, not a scope-local one: in the editor the setter
        // writes through to ProjectSettings/DynamicsManager.asset and it is saved to disk. Leaving it on
        // Script means the shipped game never steps physics at all — nothing settles, no mechanism moves,
        // the robot hangs wherever it spawned. Restore it no matter how this exits.
        SimulationMode previousMode = Physics.simulationMode;
        try { return Probe(); }
        finally { Physics.simulationMode = previousMode; }
    }

    private static string Probe()
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine("overlaps present at the RAISED pose that the rest-pose scan never saw:");

        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab.GetComponent<RobotMotorController>() == null) continue;

            // The shared rig, rather than the copy this file used to carry — that copy had dropped
            // the floor-material assertion, so it was measuring against PhysX's default friction.
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);

            // Exactly what the game does: clear the rest-pose overlaps, once.
            var restPairs = new List<string>();
            motor.Initialise();
            RobotMotorController.IgnoreBuiltInSelfOverlaps(root, restPairs);

            var lifts = new List<ArticulationBody>();
            foreach (ArticulationBody b in root.GetComponentsInChildren<ArticulationBody>(true))
                if (b != root && b.jointType == ArticulationJointType.PrismaticJoint
                    && b.linearLockX != ArticulationDofLock.LockedMotion
                    && b.xDrive.upperLimit > b.xDrive.lowerLimit) lifts.Add(b);

            Physics.simulationMode = SimulationMode.Script;
            Step(motor, SettleSteps);
            for (int i = 0; i <= LiftRampSteps; i++)
            {
                float t = i / (float)LiftRampSteps;
                foreach (ArticulationBody b in lifts)
                    b.SetDriveTarget(ArticulationDriveAxis.X,
                        Mathf.Lerp(b.xDrive.lowerLimit, b.xDrive.upperLimit, t));
                Step(motor, 1);
            }
            Step(motor, SettleSteps);

            // Now ask again. Anything reported here overlaps ONLY when raised: the pairs that
            // overlapped at rest are already on PhysX's ignore list and no longer report.
            var raisedPairs = new List<string>();
            int raised = RobotMotorController.IgnoreBuiltInSelfOverlaps(root, raisedPairs);

            report.AppendLine($"  {prefab.name}: {restPairs.Count} at rest (cleared), " +
                              $"{raised} NEW once raised over {lifts.Count} lift joint(s)");
            foreach (string pair in raisedPairs) report.AppendLine($"      {pair}");
        }
        return report.ToString().TrimEnd();
    }

    private static void Step(RobotMotorController motor, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            motor.SetManualInput(0f, 0f);
            motor.ApplyStep(ValidationUtil.StepSeconds);
            Physics.Simulate(ValidationUtil.StepSeconds);
        }
    }
}
