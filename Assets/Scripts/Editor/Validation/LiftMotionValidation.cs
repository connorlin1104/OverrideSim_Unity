using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// A LIFT COMING DOWN MUST COST THE ROBOT NO MORE THAN THE SAME LIFT GOING UP.
//
// "When the cascade is coming down, the whole bot is shaking" is a report about the DESCENT
// specifically, and nothing in this project could see it, for two separate reasons:
//
//   1. Every lift in every harness only ever went UP. TipOverValidation ramps the stages from
//      lowerLimit to upperLimit, settles, and then measures a turn. There has never been a step in
//      any test where a lift came back down.
//   2. Those harnesses drive the STAGES DIRECTLY — b.SetDriveTarget(X, lerp(lower, upper, t)) — and
//      never run CascadeLift at all. The real lift does not work that way: one hidden revolute
//      driver produces a 0->1 Progress and CascadeLift maps that onto every stage through
//      StageFraction, which with oneAtATime moves the bars in SEQUENCE, one slot each. A harness
//      that ramps all stages together in lockstep is exercising a lift the player never operates,
//      so "the lift is smooth" was a statement about a different machine.
//
// So this drives the REAL components — MotorActuator.Configure + SetInput on the driver, and
// CascadeLift.ApplyStep every step, the same calls FixedUpdate makes — and holds the robot still
// while the lift runs. A robot standing on a flat floor with nothing but its own lift moving should
// barely move at all, so any motion of the CHASSIS here is the thing being complained about,
// measured directly rather than inferred.
//
// THE PRIMARY ASSERTION IS UP-VERSUS-DOWN, not an absolute. Same robot, same lift, same stage
// drives, same floor, same speed — the only difference is the direction of travel and which way
// gravity points relative to it. That makes the ascent its own control, and it is the one number
// here that cannot be dismissed as a badly-chosen threshold.
public static class LiftMotionValidation
{
    private const int SettleSteps = 200;
    private const int MaxTravelSteps = 600;      // 6 s — three times the default 2 s raise
    private const float DoneProgress = 0.99f;
    private const float RestProgress = 0.01f;

    // How much rougher a descent may be than the ascent of the same lift. A PERCEPTUAL/engineering
    // bar, and the number to argue with — not derived. Some asymmetry is legitimate: going up the
    // drive lifts a load it fully controls, coming down gravity is doing part of the work and the
    // drive is braking rather than pulling. Twice as rough is where "it is a bit different" becomes
    // "the whole bot is shaking".
    private const float MaxDescentShakeMultiple = 2f;

    // Below this the chassis is not meaningfully moving and the ratio above is noise over noise:
    // a robot standing still reads a few tenths of a degree per second of solver churn no matter
    // what, and 0.3 / 0.1 = 3x would fail a lift that is doing nothing wrong.
    private const float ShakeNoiseFloorDegPerSec = 3f;

    [MenuItem("Tools/RoboSim/Validate/Validate Lift Motion", false, 22)]
    private static void RunInteractive()
        => ValidationUtil.RunInteractive("Lowering The Lift Does Not Shake The Robot", Run);

    public static void RunBatchValidate()
        => ValidationUtil.RunBatch("Lowering The Lift Does Not Shake The Robot", Run);

    // EVERY robot is measured before ANY of them is allowed to fail.
    //
    // Asserting inside the per-robot loop aborts the run at the first offender, and the first
    // offender is whichever prefab the AssetDatabase happened to return first — so a fleet-wide
    // problem reads as one robot's problem, and the robot actually being complained about may never
    // be measured at all. The first run of this file failed on 654V_v2 and never reached 654V_v3,
    // which is the one with the cascade that prompted it.
    public static string Run()
    {
        var lines = new System.Text.StringBuilder();
        var failures = new List<string>();
        int checks = 0, tested = 0, failed = 0;

        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;
            // Only robots with a cascade are measurable here, so `tested` is 2, not the 4 robots in
            // the project. Reporting "across 2 robot(s)" read as 2-of-4 and hid that this is 2 of 2
            // — every robot this file can test currently fails it.
            if (prefab.GetComponentInChildren<CascadeLift>(true) == null) continue;
            tested++;
            int before = failures.Count;
            checks += OneRobot(prefab, lines, failures);
            if (failures.Count > before) failed++;
        }

        ValidationUtil.Assert(tested > 0,
            "no robot with a CascadeLift was found, so nothing was checked — this file exists to " +
            "measure a cascade going down and it measured nothing");

        ValidationUtil.Assert(failures.Count == 0,
            $"{failures.Count} failure(s) across {failed} of {tested} robot(s) with a cascade " +
            "lowering the lift " +
            "(one robot can contribute more than one):" +
            "\n    " + string.Join("\n    ", failures) +
            "\n  The robot is standing still on a flat floor in every one of these, so this is the " +
            "lift shaking the robot, not the robot moving. Descent is the direction where GRAVITY " +
            "does the work and the stage drive has to BRAKE rather than pull, so the drive force " +
            "changes sign as the stage alternately outruns and lags its target. Check the stage " +
            "masses first — CascadeLift.stageStiffness is 20000 against stage masses that are " +
            "mostly just MechanismBuildUtil.MinLiftMass, and a stiff drive decelerating a " +
            "near-massless bar is the classic shape of this — then CascadeLift.stageDamping, which " +
            "is the braking gain specifically.\n  Full measurements:\n" + lines.ToString().TrimEnd());

        return $"Lowering The Lift Does Not Shake The Robot: PASSED ({checks} checks) on {tested} " +
               $"robot(s).\n{lines.ToString().TrimEnd()}";
    }

    private struct Sweep
    {
        public float peakAngularDegPerSec;   // worst instantaneous chassis rotation rate
        public float peakLinearUnitsPerSec;  // the chassis should not be travelling at all
        public int reversals;                // pitch-rate sign changes: shaking, not leaning
        public int steps;
        public float endProgress;
        public bool reachedEnd;
    }

    private static int OneRobot(GameObject prefab, System.Text.StringBuilder lines,
        List<string> failures)
    {
        SimulationMode previousMode = Physics.simulationMode;
        try
        {
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
            CascadeLift lift = root.GetComponentInChildren<CascadeLift>(true);
            motor.Initialise();

            // Exactly what Awake does for these two, and nothing more: edit-mode Physics.Simulate
            // never calls it, and a validator that re-implemented the setup would be testing its own
            // copy of the rule. Both are public and idempotent for this reason.
            MotorActuator driver = lift.driver != null ? lift.driver.GetComponent<MotorActuator>() : null;
            ValidationUtil.Assert(driver != null,
                $"'{prefab.name}' has a CascadeLift whose driver joint has no MotorActuator, so the " +
                "lift has nothing to drive it — the buttons would do nothing in game.");
            driver.Configure();
            lift.BakeDrives();

            Physics.simulationMode = SimulationMode.Script;
            StepDriven(motor, lift, driver, 0f, SettleSteps);

            Sweep up = RunSweep(root, motor, lift, driver, +1f, p => p >= DoneProgress);
            StepDriven(motor, lift, driver, 0f, SettleSteps);
            Sweep down = RunSweep(root, motor, lift, driver, -1f, p => p <= RestProgress);
            StepDriven(motor, lift, driver, 0f, SettleSteps);

            // A descent that never happened cannot be smooth. Checked before the comparison, because
            // a lift that stops moving reads as perfectly steady and would PASS the ratio below.
            ValidationUtil.Assert(up.reachedEnd,
                $"'{prefab.name}' never finished raising: progress reached {up.endProgress:0.00} after " +
                $"{up.steps * ValidationUtil.StepSeconds:0.0} s of holding the up button. Nothing below this means " +
                "anything — check the driver's sweep and CascadeLift.raiseSeconds.");
            ValidationUtil.Assert(down.reachedEnd,
                $"'{prefab.name}' never came back down: progress stopped at {down.endProgress:0.00} " +
                $"after {down.steps * ValidationUtil.StepSeconds:0.0} s of holding the down button. The lift is " +
                "stuck out, which a driver reads as the mechanism jamming.");

            // THE COMPARISON. The ascent is the control.
            float ratio = up.peakAngularDegPerSec > ShakeNoiseFloorDegPerSec
                ? down.peakAngularDegPerSec / up.peakAngularDegPerSec
                : (down.peakAngularDegPerSec > ShakeNoiseFloorDegPerSec ? float.PositiveInfinity : 1f);

            if (down.peakAngularDegPerSec > ShakeNoiseFloorDegPerSec
                && ratio > MaxDescentShakeMultiple)
                failures.Add(
                    $"'{prefab.name}': peaks {down.peakAngularDegPerSec:0.0} deg/s lowering against " +
                    $"{up.peakAngularDegPerSec:0.0} raising ({ratio:0.0}x, limit " +
                    $"{MaxDescentShakeMultiple}x), with {down.reversals} direction changes against " +
                    $"{up.reversals}");

            if (down.peakLinearUnitsPerSec > MaxStandingSpeed)
                failures.Add(
                    $"'{prefab.name}': MOVED while lowering with no drive input — the chassis reached " +
                    $"{down.peakLinearUnitsPerSec:0.00} u/s (limit {MaxStandingSpeed}), against " +
                    $"{up.peakLinearUnitsPerSec:0.00} u/s raising. A lift coming down is pushing the " +
                    "robot across the floor, so the stages are driving against something — look for a " +
                    "stage or a rider contacting the chassis partway through the travel. " +
                    "IgnoreBuiltInSelfOverlaps only clears overlaps present AT SPAWN, and a lift " +
                    "sweeps through poses that did not exist when it ran.");

            lines.AppendLine(
                $"  '{prefab.name}': raising peaks {up.peakAngularDegPerSec:0.0} deg/s " +
                $"({up.reversals} direction changes, {up.steps * ValidationUtil.StepSeconds:0.0} s), " +
                $"lowering peaks {down.peakAngularDegPerSec:0.0} deg/s " +
                $"({down.reversals} direction changes, {down.steps * ValidationUtil.StepSeconds:0.0} s) " +
                $"= {ratio:0.0}x; chassis drifted at most " +
                $"{Mathf.Max(up.peakLinearUnitsPerSec, down.peakLinearUnitsPerSec):0.00} u/s");
            return 4;
        }
        finally { Physics.simulationMode = previousMode; }
    }

    // A robot told to stand still should stand still. Anything above this is the mechanism shoving
    // the whole machine around, which is a different fault from the chassis merely rocking.
    private const float MaxStandingSpeed = 0.5f;

    private static Sweep RunSweep(ArticulationBody root, RobotMotorController motor, CascadeLift lift,
        MotorActuator driver, float input, System.Func<float, bool> finished)
    {
        var s = new Sweep();
        Transform t = root.transform;
        float prevRate = 0f;

        for (int i = 0; i < MaxTravelSteps; i++)
        {
            driver.SetInput(input);
            StepDriven(motor, lift, driver, input, 1);
            s.steps++;

            float angular = root.angularVelocity.magnitude * Mathf.Rad2Deg;
            s.peakAngularDegPerSec = Mathf.Max(s.peakAngularDegPerSec, angular);
            s.peakLinearUnitsPerSec = Mathf.Max(s.peakLinearUnitsPerSec,
                Vector3.ProjectOnPlane(root.linearVelocity, Vector3.up).magnitude);

            // Direction changes about the robot's own lateral axis: a chassis that pitches one way
            // and back repeatedly is shaking, which is what gets reported, while a steady lean of
            // the same size is not. Same shape as TipOverValidation's roll-reversal metric.
            float rate = Vector3.Dot(root.angularVelocity, t.right) * Mathf.Rad2Deg;
            if (i > 0 && Mathf.Sign(rate) != Mathf.Sign(prevRate)
                && Mathf.Abs(rate) > ShakeNoiseFloorDegPerSec) s.reversals++;
            prevRate = rate;

            s.endProgress = lift.Progress;
            if (finished(lift.Progress)) { s.reachedEnd = true; break; }
        }

        driver.SetInput(0f);
        return s;
    }

    // The lift's own per-step work alongside the drivetrain's, because both run in FixedUpdate in
    // the game and only one of them running is a robot that does not exist.
    private static void StepDriven(RobotMotorController motor, CascadeLift lift, MotorActuator driver,
        float liftInput, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            motor.SetManualInput(0f, 0f);
            motor.ApplyStep(ValidationUtil.StepSeconds);
            lift.ApplyStep();
            RobotPhysicsValidation.Step(1);
        }
    }
}
