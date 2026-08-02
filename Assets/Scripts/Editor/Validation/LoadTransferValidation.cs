using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// The visual weight transfer: does the body lean the right way, by the right amount, without
// touching the physics or clipping through the floor.
//
// This is a cosmetic feature, which makes it MORE dangerous than a physical one, not less. A drive
// change that felt wrong would be noticed in a lap of the field; a render pose that quietly leaks
// into the simulation would show up as a robot that tips differently than it measures, months
// later, with nothing pointing back here. So the first assertion below is the important one: run
// the identical input twice, once with the lean at its default and once with it switched off, and
// require the two physics trajectories to match to the last representable digit.
//
// The rest are properties of the maths that can be checked without a robot at all — a steady state,
// an overshoot that exists only below critical damping, a frame rate that does not change the
// answer, and a pivot that keeps the loaded wheels on the floor. Every one is stated independently
// of how RobotMotorController computes it: the steady state comes from setting the derivatives of
// the ODE to zero, the no-overshoot bound from what critical damping means, and the floor check
// from measuring the wheel line before and after rather than from the pivot the code chose.
public static class LoadTransferValidation
{
    // Enough steps at 100 Hz for a 12 rad/s spring to be done ringing (about 2 s).
    private const int SettleSteps = 200;
    private const float StepSeconds = 0.01f;

    // Steady state is exact in the limit, so this is "has it arrived", not a tolerance on the
    // formula. A 12 rad/s spring is inside a thousandth of a degree long before 2 s.
    private const float SettledDeg = 0.01f;

    // A slammed reversal must produce a lean a player can actually see. Below this the feature is
    // present, tested, and invisible — which is the failure mode a cosmetic change is most likely
    // to reach and least likely to be noticed in.
    private const float MinVisibleLeanDeg = 0.75f;

    [MenuItem("Tools/RoboSim/Validate/Load Transfer Is Visual Only")]
    public static void Validate()
        => ValidationUtil.RunInteractive("Load Transfer", Run);

    public static void RunBatchValidate()
        => ValidationUtil.RunBatch("Load Transfer", Run);

    private static string Run()
    {
        var lines = new List<string>();
        int checks = 0;

        checks += SpringSettlesOnItsTarget(lines);
        checks += OnlyAnUnderdampedSpringOvershoots(lines);
        checks += TheFrameRateDoesNotChangeTheLean(lines);
        checks += ZeroLeanIsTheIdentity(lines);
        checks += LeaningNeverPushesAWheelThroughTheFloor(lines);
        checks += NoseUpMeansNoseUp(lines);
        checks += TheLeanChangesNothingPhysical(lines);

        lines.Insert(0, $"Load transfer: {checks} checks passed.\n");
        return string.Join("\n", lines);
    }

    // --- The spring ------------------------------------------------------------------------------

    // Setting deg' = deg'' = 0 in  deg'' = -w^2 (deg - target) - 2*zeta*w*deg'  leaves deg == target
    // for any positive frequency and any damping. So the destination is known without running it.
    private static int SpringSettlesOnItsTarget(List<string> lines)
    {
        int checks = 0;
        foreach (float target in new[] { 2.5f, -2.5f, 0.8f })
        {
            foreach (float zeta in new[] { 0.4f, 0.55f, 1f })
            {
                float deg = 0f, rate = 0f;
                for (int i = 0; i < SettleSteps; i++)
                    RobotMotorController.LeanStep(ref deg, ref rate, target, 12f, zeta, 8f, StepSeconds);

                ValidationUtil.Assert(Mathf.Abs(deg - target) < SettledDeg,
                    $"a lean spring at zeta {zeta} settled on {deg:0.000} deg instead of the " +
                    $"{target:0.000} it was asked for — the body would come to rest leaning even " +
                    "with the robot sitting still");
                ValidationUtil.Assert(Mathf.Abs(rate) < 1f,
                    $"a lean spring at zeta {zeta} settled at {deg:0.000} deg but is still moving " +
                    $"at {rate:0.00} deg/s, so it has not settled at all");
                checks += 2;
            }
        }
        lines.Add($"  spring settles exactly on its target at every damping ({checks} checks)");
        return checks;
    }

    // Critical damping is DEFINED as the least damping that returns without crossing the target, so
    // a step response at zeta >= 1 may not exceed it and one below 1 must. That is the whole reason
    // the default is 0.55: the overshoot is the "momentarily" in momentarily shifting weight.
    private static int OnlyAnUnderdampedSpringOvershoots(List<string> lines)
    {
        const float target = 2.5f;

        float under = PeakOfAStep(target, 0.55f);
        float critical = PeakOfAStep(target, 1f);

        ValidationUtil.Assert(under > target * 1.02f,
            $"the default lean damping (0.55) peaked at {under:0.000} deg against a {target:0.000} " +
            "target, so it slides into place with no rebound — the body would never look like it " +
            "was still settling from the stop");
        ValidationUtil.Assert(critical <= target + 1e-3f,
            $"a critically damped lean peaked at {critical:0.000} deg against a {target:0.000} " +
            "target, which a critically damped second-order system cannot do — the integrator is " +
            "adding energy");
        lines.Add($"  overshoot at zeta 0.55: {under / target:0.00}x, and none at zeta 1.0 (2 checks)");
        return 2;
    }

    private static float PeakOfAStep(float target, float zeta)
    {
        float deg = 0f, rate = 0f, peak = 0f;
        for (int i = 0; i < SettleSteps; i++)
        {
            RobotMotorController.LeanStep(ref deg, ref rate, target, 12f, zeta, 8f, StepSeconds);
            peak = Mathf.Max(peak, deg);
        }
        return peak;
    }

    // Same simulated second, five times the steps. A response that changed with the step is one
    // that would lean further on a phone than on a desktop, which is the bug this rules out.
    private static int TheFrameRateDoesNotChangeTheLean(List<string> lines)
    {
        float coarse = 0f, coarseRate = 0f;
        for (int i = 0; i < 100; i++)
            RobotMotorController.LeanStep(ref coarse, ref coarseRate, 2.5f, 12f, 0.55f, 8f, 0.01f);

        float fine = 0f, fineRate = 0f;
        for (int i = 0; i < 500; i++)
            RobotMotorController.LeanStep(ref fine, ref fineRate, 2.5f, 12f, 0.55f, 8f, 0.002f);

        ValidationUtil.Assert(Mathf.Abs(coarse - fine) < 0.05f,
            $"one second of the same lean came out {coarse:0.000} deg at 100 Hz and {fine:0.000} " +
            "at 500 Hz — the integrator is step-dependent, so how far the robot leans would depend " +
            "on the device it is running on");
        lines.Add($"  1 s of lean: {coarse:0.000} deg at 100 Hz, {fine:0.000} deg at 500 Hz (1 check)");
        return 1;
    }

    // --- The pose --------------------------------------------------------------------------------

    private static readonly Vector3 FrontPivot = new Vector3(0f, -0.8f, 1.3f);
    private static readonly Vector3 RearPivot = new Vector3(0f, -0.8f, -1.3f);

    // No lean, no change — and exactly no change, because "visual only" has to survive being
    // switched off. A pose that drifted by an epsilon per frame would walk the robot across the
    // field over a match.
    private static int ZeroLeanIsTheIdentity(List<string> lines)
    {
        var position = new Vector3(3f, 1.2f, -7f);
        Quaternion rotation = Quaternion.Euler(4f, 37f, -2f);

        RobotMotorController.LeanedPose(position, rotation, 0f, FrontPivot, RearPivot,
            out Vector3 leanedPosition, out Quaternion leanedRotation);

        ValidationUtil.Assert(leanedPosition == position,
            $"a zero lean moved the robot from {position} to {leanedPosition}");
        ValidationUtil.Assert(Quaternion.Angle(leanedRotation, rotation) < 1e-4f,
            $"a zero lean turned the robot by {Quaternion.Angle(leanedRotation, rotation):0.0000} deg");
        lines.Add("  a zero lean is exactly the identity (2 checks)");
        return 2;
    }

    // The reason the pivot is a contact line and not the middle of the robot. Both wheel lines are
    // measured before and after, in world Y, with no reference to which pivot the code picked: if
    // either drops, the lean is putting a wheel through the floor.
    private static int LeaningNeverPushesAWheelThroughTheFloor(List<string> lines)
    {
        var position = new Vector3(0f, 1.5f, 0f);
        Quaternion rotation = Quaternion.identity;
        float worst = 0f;
        int checks = 0;

        for (float lean = -4f; lean <= 4.001f; lean += 0.25f)
        {
            RobotMotorController.LeanedPose(position, rotation, lean, FrontPivot, RearPivot,
                out Vector3 leanedPosition, out Quaternion leanedRotation);

            float frontBefore = (position + rotation * FrontPivot).y;
            float rearBefore = (position + rotation * RearPivot).y;
            float frontAfter = (leanedPosition + leanedRotation * FrontPivot).y;
            float rearAfter = (leanedPosition + leanedRotation * RearPivot).y;

            float drop = Mathf.Min(frontAfter - frontBefore, rearAfter - rearBefore);
            worst = Mathf.Min(worst, drop);
            ValidationUtil.Assert(drop > -1e-4f,
                $"leaning {lean:0.00} deg drops a wheel line {-drop * 100f:0.00} mm below the floor " +
                "it was resting on — the lean is pivoting about the light end instead of the loaded " +
                "one, and the tyres will visibly sink into the tile");
            checks++;
        }

        lines.Add($"  no wheel line drops at any lean in +-4 deg (worst {worst * 100f:0.000} mm, " +
                  $"{checks} checks)");
        return checks;
    }

    // Accelerate and the nose comes up; brake and it goes down. Checked on a point out at the front
    // of the robot rather than on the quaternion, because what a player sees is the nose height.
    private static int NoseUpMeansNoseUp(List<string> lines)
    {
        var position = new Vector3(0f, 1.5f, 0f);
        Quaternion rotation = Quaternion.Euler(0f, 25f, 0f);   // an arbitrary heading, not axis-aligned
        Vector3 noseLocal = new Vector3(0f, 0f, 2f);

        float level = (position + rotation * noseLocal).y;

        RobotMotorController.LeanedPose(position, rotation, 2.5f, FrontPivot, RearPivot,
            out Vector3 upPosition, out Quaternion upRotation);
        RobotMotorController.LeanedPose(position, rotation, -2.5f, FrontPivot, RearPivot,
            out Vector3 downPosition, out Quaternion downRotation);

        float up = (upPosition + upRotation * noseLocal).y;
        float down = (downPosition + downRotation * noseLocal).y;

        ValidationUtil.Assert(up > level,
            $"a positive lean is documented as nose-UP but put the nose at {up:0.000} against a " +
            $"level {level:0.000} — accelerating would dip the nose, which is backwards");
        ValidationUtil.Assert(down < level,
            $"a negative lean put the nose at {down:0.000} against a level {level:0.000} — braking " +
            "would raise the nose, which is backwards");
        lines.Add($"  nose {(up - level) * 100f:0.0} mm up under power, " +
                  $"{(level - down) * 100f:0.0} mm down under braking (2 checks)");
        return 2;
    }

    // --- The robot -------------------------------------------------------------------------------

    // The one that matters. Drive the same slammed reversal twice, identical in every respect except
    // whether the lean is switched on, and require the two physics trajectories to land on top of
    // each other at every single step.
    //
    // The comparison is Vector3's own ==, which is a 1e-5 tolerance on the difference — a ten-
    // thousandth of a millimetre at this project's scale, so it will not fire on float noise and
    // will fire on anything that has actually reached the simulation. Divergence compounds: a
    // render pose that leaked into one contact would be millimetres apart by the end of the slam,
    // not microns.
    private static int TheLeanChangesNothingPhysical(List<string> lines)
    {
        var failures = new List<string>();
        int tested = 0, checks = 0;

        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { RoboSimPaths.RobotsFolder }))
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;

            List<Vector3> leaning = SlamAReversal(prefab, 2.5f, out float peakLean);
            List<Vector3> level = SlamAReversal(prefab, 0f, out _);
            tested++;

            int differed = -1;
            for (int i = 0; i < leaning.Count && i < level.Count; i++)
                if (leaning[i] != level[i]) { differed = i; break; }

            if (differed >= 0)
                failures.Add($"'{prefab.name}' diverged at step {differed}: {leaning[differed]} with " +
                             $"the lean on against {level[differed]} with it off — the render pose is " +
                             "reaching the simulation");
            else if (peakLean < MinVisibleLeanDeg)
                failures.Add($"'{prefab.name}' never leaned more than {peakLean:0.00} deg through a " +
                             $"full slammed reversal (want at least {MinVisibleLeanDeg:0.00}) — the " +
                             "weight transfer is switched on, correct, and invisible");
            else
                lines.Add($"  '{prefab.name}': peak lean {peakLean:0.00} deg, physics identical " +
                          $"across {leaning.Count} steps");
            checks += 2;
        }

        ValidationUtil.Assert(tested > 0,
            $"no robot prefab with a RobotMotorController was found under {RoboSimPaths.RobotsFolder}, " +
            "so the only check that can tell a render pose from a physical one never ran");
        ValidationUtil.Assert(failures.Count == 0,
            $"{failures.Count} of {tested} robot(s):\n  - " + string.Join("\n  - ", failures));
        return checks;
    }

    // Full throttle until it is up to speed, then the stick thrown the other way and held.
    private static List<Vector3> SlamAReversal(GameObject prefab, float leanDeg, out float peakLean)
    {
        SimulationMode previousMode = Physics.simulationMode;
        try
        {
            ArticulationBody root = TipOverValidation.SpawnOnBareFloor(prefab, out RobotMotorController motor);

            // Set BEFORE Initialise: MeasureLoadTransfer runs there, and a robot whose lean is off
            // should be measured with it off from the first step, not switched off afterwards.
            motor.loadTransferPitchDeg = leanDeg;
            motor.Initialise();

            Physics.simulationMode = SimulationMode.Script;
            PhysicsSmokeTest.Step(60);   // settle onto the floor before anyone touches the sticks

            var track = new List<Vector3>();
            peakLean = 0f;
            peakLean = Drive(root, motor, +1f, track, peakLean);
            peakLean = Drive(root, motor, -1f, track, peakLean);
            return track;
        }
        finally { Physics.simulationMode = previousMode; }
    }

    private static float Drive(ArticulationBody root, RobotMotorController motor, float throttle,
        List<Vector3> track, float peakLean)
    {
        motor.SetManualInput(throttle, 0f);
        for (int i = 0; i < 150; i++)
        {
            motor.ApplyStep(StepSeconds);
            PhysicsSmokeTest.Step(1);
            track.Add(root.transform.position);
            peakLean = Mathf.Max(peakLean, Mathf.Abs(motor.LeanDegrees));
        }
        return peakLean;
    }
}
