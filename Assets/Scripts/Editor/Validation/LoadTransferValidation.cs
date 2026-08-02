using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// The visual weight transfer: does the body lean the right way, by the right amount, without
// touching the physics or clipping through the floor.
//
// This is a cosmetic feature, which makes it MORE dangerous than a physical one, not less. A drive
// change that felt wrong would be noticed in a lap of the field; a render pose that quietly leaks
// into the simulation would show up as a robot that tips differently than it measures, months
// later, with nothing pointing back here. So the important assertion below runs the identical input
// three times — lean on, lean off, lean on again — and requires switching the lean on to move the
// physics no further than merely re-running the same simulation does.
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

    // 2 s each way, matching TipOverValidation's accel phase: comfortably past 95% of top speed on
    // the fastest robot here, and long enough for the slowest to finish stopping and start back.
    private const int DriveSteps = 200;

    // Steady state is exact in the limit, so this is "has it arrived", not a tolerance on the
    // formula. A 12 rad/s spring is inside a thousandth of a degree long before 2 s.
    private const float SettledDeg = 0.01f;

    // A slammed reversal must produce a lean a player can actually see. Below this the feature is
    // present, tested, and invisible — which is the failure mode a cosmetic change is most likely
    // to reach and least likely to be noticed in.
    private const float MinVisibleLeanDeg = 0.75f;

    // The floor under the measured run-to-run noise, so a run that happens to come out perfectly
    // repeatable does not then demand perfection of the comparison. 0.01 units is a hundredth of a
    // millimetre of real robot — far below anything that could matter and far above float noise.
    private const float RerunNoiseFloor = 0.01f;

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
        checks += ForwardIsWhereTheRobotActuallyGoes(lines);
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

        RobotMotorController.LeanedPose(position, rotation, 0f, FrontPivot, RearPivot, Vector3.right,
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
            RobotMotorController.LeanedPose(position, rotation, lean, FrontPivot, RearPivot, Vector3.right,
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

        RobotMotorController.LeanedPose(position, rotation, 2.5f, FrontPivot, RearPivot, Vector3.right,
            out Vector3 upPosition, out Quaternion upRotation);
        RobotMotorController.LeanedPose(position, rotation, -2.5f, FrontPivot, RearPivot, Vector3.right,
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

    // The check that would have saved a day. Drive each robot in a straight line and require the
    // direction it actually went to be the direction the controller thinks is forward.
    //
    // RobotMotorController used to take root +Z as the driving axis on the strength of a comment.
    // That is true of 654V_v1 and 360RpmDrivetrain and FALSE of 654V_v2 and 654V_v3, which travel
    // perpendicular to their own transform.forward — so the roll relief was rolling them about their
    // PITCH axis, cancelling exactly the front-to-back tipping it is documented to preserve, and the
    // load transfer measured no acceleration however hard they braked. Nothing failed loudly. The
    // axis is measured from the wheels' axles now, and this is what says it stayed measured.
    private static int ForwardIsWhereTheRobotActuallyGoes(List<string> lines)
    {
        var failures = new List<string>();
        int tested = 0;

        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { RoboSimPaths.RobotsFolder }))
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;
            tested++;

            SimulationMode previousMode = Physics.simulationMode;
            try
            {
                ArticulationBody root = TipOverValidation.SpawnOnBareFloor(prefab, out RobotMotorController motor);
                motor.Initialise();
                Physics.simulationMode = SimulationMode.Script;
                TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);
                TipOverValidation.StepDriven(motor, 1f, 0f, DriveSteps);

                Vector3 travel = root.linearVelocity;
                travel.y = 0f;
                if (travel.magnitude < 1f)
                {
                    failures.Add($"'{prefab.name}' never reached 1 u/s in a straight line, so which " +
                                 "way it drives could not be measured at all");
                    continue;
                }

                // Signed, because a robot driving backwards on full forward throttle is its own bug
                // (that is what invertLeft/invertRight exist for) and must not read as a good axis.
                float agreement = Vector3.Dot(travel.normalized, motor.DriveForwardWorld);
                lines.Add($"  '{prefab.name}': drove {travel.magnitude:0.0} u/s at " +
                          $"{Mathf.Acos(Mathf.Clamp(agreement, -1f, 1f)) * Mathf.Rad2Deg:0.0} deg from " +
                          "the forward axis it measured for itself");

                // The two ways this fails are not the same failure, and saying so is the point of
                // the check. Off the LINE is loud: the roll relief rolls about the pitch axis and
                // cancels the front-to-back tipping it is documented to leave alone, the load
                // transfer reads ~0 acceleration however hard the robot brakes, and the half-track
                // is really the wheelbase. BACKWARDS down a correct line is silent: everything
                // reading these axes today flips with them and nothing moves on screen — but
                // DriveForwardWorld is public, and the next thing to read it inherits the bug.
                if (agreement < MinAxisAgreement)
                {
                    float off = Mathf.Acos(Mathf.Clamp(agreement, -1f, 1f)) * Mathf.Rad2Deg;
                    failures.Add(agreement <= -MinAxisAgreement
                        ? $"'{prefab.name}' drives BACKWARDS along the forward axis MeasureDriveAxes " +
                          $"derived from its wheels ({off:0} deg). The line is right and only the " +
                          "sign is wrong, so today's readers — which all flip with it — look fine " +
                          "and hide it; the axis is public and the next reader will not"
                        : $"'{prefab.name}' drives {off:0} deg away from the forward axis " +
                          "MeasureDriveAxes derived from its wheels, so the roll relief rolls it " +
                          "about the wrong axis, the load transfer reads the wrong acceleration, " +
                          "and the half-track is really the wheelbase");
                }
            }
            finally { Physics.simulationMode = previousMode; }
        }

        ValidationUtil.Assert(tested > 0,
            $"no robot prefab with a RobotMotorController under {RoboSimPaths.RobotsFolder}");
        ValidationUtil.Assert(failures.Count == 0,
            $"{failures.Count} of {tested} robot(s) do not drive the way they think they do:\n  - " +
            string.Join("\n  - ", failures));
        return tested;
    }

    // cos(15 deg). Generous: the question is whether the axis is RIGHT or ninety degrees out, and
    // wheel mounting error in imported CAD is worth a couple of degrees on its own.
    private const float MinAxisAgreement = 0.966f;

    // The one that matters, measured against its own noise floor.
    //
    // The first version of this asserted the two trajectories were IDENTICAL, and it failed on two
    // robots while printing two positions that agreed to every digit it showed. That was my error,
    // not the feature's: PhysX is not deterministic across separate scene builds, so re-running the
    // very same simulation lands somewhere microscopically different. An assertion that assumes
    // determinism it does not have is a flaky test whichever way it happens to land.
    //
    // So run it THREE times: once with the lean on, once with it off, and once more with it on. The
    // third run is the control, and the distance between the two identical runs is what re-running
    // alone costs. Switching the lean on then has to cost no more than that. The claim being pinned
    // is the honest one — turning this feature on perturbs the physics no more than running the
    // simulation again does — and it needs no invented tolerance, because the run measures its own.
    //
    // The 2x is slop for the fact that both figures are single draws from the same noise process,
    // not a fudge on the size of the effect: a render pose that had genuinely reached the simulation
    // would compound over 300 steps into millimetres, orders of magnitude clear of either.
    private static int TheLeanChangesNothingPhysical(List<string> lines)
    {
        var failures = new List<string>();
        int tested = 0, checks = 0;

        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { RoboSimPaths.RobotsFolder }))
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;

            List<Vector3> leaning = SlamAReversal(prefab, 2.5f, out Slam slam);
            List<Vector3> level = SlamAReversal(prefab, 0f, out _);
            List<Vector3> control = SlamAReversal(prefab, 2.5f, out _);
            float peakLean = slam.peakLeanDeg;
            tested++;

            float noise = FurthestApart(leaning, control);
            float applied = FurthestApart(leaning, level);
            float allowed = Mathf.Max(2f * noise, RerunNoiseFloor);

            if (applied > allowed)
                failures.Add($"'{prefab.name}': switching the lean on moved the physics " +
                             $"{applied * 100f:0.000} mm, against {noise * 100f:0.000} mm for merely " +
                             $"re-running the identical simulation (allowed {allowed * 100f:0.000}) — " +
                             "the render pose is reaching the simulation");
            else if (peakLean < MinVisibleLeanDeg)
                failures.Add($"'{prefab.name}' never leaned more than {peakLean:0.00} deg through a " +
                             $"full slammed reversal (want at least {MinVisibleLeanDeg:0.00}) — the " +
                             "weight transfer is switched on, correct, and invisible. It braked at " +
                             $"{slam.peakDecel:0} u/s^2 from {slam.topSpeed:0.0} u/s ({slam.topPlanarSpeed:0.0} in any direction), which is " +
                             $"{slam.peakDecel / Mathf.Max(slam.fullLeanAccel, 1e-3f) * 100f:0} percent " +
                             $"of the {slam.fullLeanAccel:0} u/s^2 that asks for a full lean, and the " +
                             $"whole stop lasted {slam.decelSeconds * 1000f:0} ms against a spring that " +
                             "takes about 150 ms to respond." +
                             (Mathf.Abs(slam.topPlanarSpeed) > 1f
                                 && Mathf.Abs(slam.topSpeed) < 0.25f * Mathf.Abs(slam.topPlanarSpeed)
                                 ? "\n      READ THOSE TWO SPEEDS AGAIN: this robot is travelling at " +
                                   "full speed PERPENDICULAR to its own transform.forward, so root +Z " +
                                   "is not its driving axis. StepLoadTransfer takes longitudinal " +
                                   "acceleration as dot(velocity, forward) and therefore measures ~0 " +
                                   "no matter how hard it brakes. The same assumption is in " +
                                   "ApplyRollRelief, which rolls about forward — on this robot that " +
                                   "is the PITCH axis, so the relief is cancelling exactly the " +
                                   "front-to-back tipping it is documented to leave alone. Fix the " +
                                   "axis (derive it from the wheels' axles), not this test."
                                 : ""));
            else
                lines.Add($"  '{prefab.name}': peak lean {peakLean:0.00} deg at {slam.peakDecel:0} " +
                          $"u/s^2 from {slam.topSpeed:0.0} u/s ({slam.topPlanarSpeed:0.0} in any direction) ({slam.decelSeconds * 1000f:0} ms of " +
                          $"braking) · lean on vs off moved the physics {applied * 100f:0.000} mm over " +
                          $"{leaning.Count} steps, against {noise * 100f:0.000} mm of run-to-run noise");
            checks += 2;
        }

        ValidationUtil.Assert(tested > 0,
            $"no robot prefab with a RobotMotorController was found under {RoboSimPaths.RobotsFolder}, " +
            "so the only check that can tell a render pose from a physical one never ran");
        ValidationUtil.Assert(failures.Count == 0,
            $"{failures.Count} of {tested} robot(s):\n  - " + string.Join("\n  - ", failures) +
            // A failure that throws away the measurements it just took makes the next run guess at
            // what the first one saw. The robots that PASSED are the control for the ones that did not.
            (lines.Count > 0 ? "\n  The robots that passed:\n" + string.Join("\n", lines) : ""));
        return checks;
    }

    // The worst the two trajectories ever get from each other, in world units.
    private static float FurthestApart(List<Vector3> a, List<Vector3> b)
    {
        float worst = 0f;
        for (int i = 0; i < a.Count && i < b.Count; i++)
            worst = Mathf.Max(worst, Vector3.Distance(a[i], b[i]));
        return worst;
    }

    // What the reversal actually did, measured off the ROBOT rather than off the component being
    // tested. If the lean comes out too small, the question is immediately whether the input was
    // small or the response was — and that has to be answered without asking the thing under test.
    private struct Slam
    {
        public float peakLeanDeg;
        public float topSpeed;         // along the robot's forward axis, before the stick was thrown
        public float topPlanarSpeed;   // ...and ignoring direction, so "did not move" is separable
                                       // from "moved, but not the way its nose is pointing"
        public float peakDecel;        // u/s^2, differentiated from the robot's own velocity
        public float decelSeconds;     // how long the stop lasted, which bounds what any spring can do
        public float fullLeanAccel;    // the traction limit: the deceleration that asks for a full lean
    }

    // Full throttle until it is up to speed, then the stick thrown the other way and held.
    private static List<Vector3> SlamAReversal(GameObject prefab, float leanDeg, out Slam slam)
    {
        SimulationMode previousMode = Physics.simulationMode;
        try
        {
            ArticulationBody root = TipOverValidation.SpawnOnBareFloor(prefab, out RobotMotorController motor);

            // Set BEFORE Initialise: MeasureLoadTransfer runs there, and a robot whose lean is off
            // should be measured with it off from the first step, not switched off afterwards.
            motor.loadTransferPitchDeg = leanDeg;
            motor.Initialise();

            // A cascade whose drives were never baked cannot hold its own stages up, and edit-mode
            // Physics.Simulate never runs Awake to bake them. 654V_v2 and 654V_v3 — the only two
            // robots here with a CascadeLift — then sat on the floor unable to drive at all and
            // reported a top speed of 0.0 u/s, which reads as "the weight transfer does not work"
            // and is actually "the robot never moved". Same two calls LiftMotionValidation makes,
            // for the same reason; both are public and idempotent so a harness can run the real
            // path rather than a copy of it.
            CascadeLift lift = root.GetComponentInChildren<CascadeLift>(true);
            if (lift != null)
            {
                MotorActuator driver = lift.driver != null
                    ? lift.driver.GetComponent<MotorActuator>() : null;
                driver?.Configure();
                lift.BakeDrives();
            }

            Physics.simulationMode = SimulationMode.Script;

            // Settle THROUGH the controller, and for as long as TipOverValidation does.
            //
            // Both halves of that were wrong here first time round, and the way they were wrong is
            // worth keeping: 60 bare Physics.Simulate steps left 654V_v2 and 654V_v3 unable to drive
            // at all, so they reported a top speed of 0.0 u/s and — quite correctly — no lean. Read
            // without the input measurement beside it, that looks exactly like a broken feature
            // rather than a broken fixture, which is why the failure message reports the braking it
            // saw as well as the lean it did not.
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);

            // Lift UP, like every other dynamic fixture in the project.
            //
            // Not a workaround: with the cascade STOWED, 654V_v2 and 654V_v3 could not drive at all
            // here — 0.0 u/s after two full seconds of throttle — while the identical spawn, settle
            // and throttle reaches 6.5 u/s in MovingTurnValidation, whose only meaningful difference
            // is that it raises the lifts first. Baking the cascade drives did not change it either.
            // Something about the stowed pose is holding these two robots down, and it is worth its
            // own investigation; it is not what this file is about, and raising the lift is both the
            // established fixture and the more demanding case for weight transfer, since it puts the
            // centre of mass 223 mm up instead of 80.
            TipOverValidation.RaiseLifts(root, motor);
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);

            slam = new Slam
            {
                fullLeanAccel = DrivetrainTuning.MeasureFriction(
                    PhysicsSmokeTest.FindWheels(root, out _, out _)) * Mathf.Abs(Physics.gravity.y),
            };

            var track = new List<Vector3>();
            Drive(root, motor, +1f, track, ref slam, braking: false);
            slam.topSpeed = Forward(root, motor);
            slam.topPlanarSpeed = new Vector2(root.linearVelocity.x, root.linearVelocity.z).magnitude;
            Drive(root, motor, -1f, track, ref slam, braking: true);
            return track;
        }
        finally { Physics.simulationMode = previousMode; }
    }

    private static void Drive(ArticulationBody root, RobotMotorController motor, float throttle,
        List<Vector3> track, ref Slam slam, bool braking)
    {
        float last = Forward(root, motor);
        bool stillSlowing = braking;

        for (int i = 0; i < DriveSteps; i++)
        {
            // Re-asserted every step, exactly as TipOverValidation.StepDriven does. Stepped by hand
            // rather than calling it because the measurements below have to land between the
            // controller's step and the physics step, which that helper does not expose.
            motor.SetManualInput(throttle, 0f);
            motor.ApplyStep(StepSeconds);
            PhysicsSmokeTest.Step(1);
            track.Add(root.transform.position);
            slam.peakLeanDeg = Mathf.Max(slam.peakLeanDeg, Mathf.Abs(motor.LeanDegrees));

            float now = Forward(root, motor);
            if (braking)
            {
                slam.peakDecel = Mathf.Max(slam.peakDecel, Mathf.Abs(now - last) / StepSeconds);
                // The stop is over the moment the robot stops travelling the way it came in. Past
                // that it is accelerating the other way, which is a different event.
                if (stillSlowing && Mathf.Sign(now) == Mathf.Sign(slam.topSpeed) && Mathf.Abs(now) > 0.05f)
                    slam.decelSeconds += StepSeconds;
                else stillSlowing = false;
            }
            last = now;
        }
    }

    // Off the MEASURED axis, never root.transform.forward — which is what this instrument used to
    // do, and it is the same bug the check twenty lines up exists to catch. It read v2 and v3
    // slamming to a stop from "0.0 u/s" while they were plainly doing 9.3, so every accel this
    // reports on those two was ~0 and the failure message blamed the spring. A ruler built on the
    // assumption under test cannot referee it.
    private static float Forward(ArticulationBody root, RobotMotorController motor)
        => Vector3.Dot(root.linearVelocity, motor.DriveForwardWorld);
}
