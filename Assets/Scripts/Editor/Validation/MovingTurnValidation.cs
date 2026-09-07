using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// "When it is just turning by itself and it was previously completely stopped, it is smooth. But
// when it starts going forwards and immediately goes into a spin it gets the rough jumping thing
// again, and when it is smooth turning and you add some forward movement it becomes rough again."
//
// That report hands over the control experiment along with the bug, which is rare and worth
// building the whole file around: the SAME robot, the SAME lift height, the SAME turn stick, rough
// in one case and smooth in the other. So the pass mark here is not an absolute number I would have
// had to invent — it is the spin-from-rest, measured on the same robot moments earlier.
//
// WHAT IS BEING MEASURED, and why not the chassis. Every roll metric in TipOverValidation read
// ~zero on these robots while a roll-relief torque held the frame level, and a chassis-roll metric
// could not see this even without it. What a driver calls "rough jumping" with a
// raised lift is the WHEELS breaking and regaining grip, so that is what this counts: how often
// each wheel's spin direction reverses, and how hard the tyre is being dragged against the floor
// (slip = the speed the tyre surface is turning at, against the speed the ground is actually going
// past underneath it). A wheel held at a speed the robot's momentum will not let it reach is a
// wheel that alternately locks and lets go, a hundred times a second.
//
// WHERE TO LOOK IF THIS FAILS. A full turn stick at full throttle asks the inner rail for a speed
// well below the one the robot's momentum is carrying it at, and DriveForceLimit hands a held stick
// full authority to enforce that — so the inner rail is braked, hard, and that braking IS the turn.
// Two things have been the answer before. The TYRE: six isotropic spheres scrubbing sideways at full
// grip, fixed in WheelTyreModel. The MIX: turn priority used to pay for the turn out of the throttle
// and command the inner rail to a literal dead stop, which turned the arc into a spin about three
// skidding wheels; MixArcade scales both together now, so the same stick brakes the inner rail less
// and the yaw here is roughly half what it was — smaller numbers, less slip, and by design.
//
// This file is what any change to the tyre, the mix or the authority rule has to answer to, and it
// is sensitive: a load-proportional torque split was tried on 2026-09-06 and read here as 654V_v1
// turning 0 degrees where it had turned 455. See RobotMotorController's note on why that is gone.
public static class MovingTurnValidation
{
    private const int SettleSteps = 60;
    private const int AccelSteps = 150;      // 1.5 s: enough to reach terminal speed in a straight line
    private const int TurnSteps = 200;       // 2 s of held turn, which is where the metrics come from

    // A wheel reversing direction with less than this much change of speed in one step is
    // stationary noise, not a wheel snatching: 30 deg/s per step is 2% of a 240 RPM free speed.
    private const float WheelRateNoiseFloor = 30f;     // deg/s, per step

    // How much worse a moving turn is allowed to be than the same robot's spin from rest. Two is
    // generous on purpose: a moving turn genuinely does scrub more than a stationary one, because
    // the tyres are having to fight forward momentum as well as yaw the robot. This is a PERCEPTUAL
    // bar, not a derived one — it is the number to argue with when this fails.
    private const float MaxRoughnessMultiple = 2f;

    [MenuItem("Tools/RoboSim/Validate/Validate Moving Turn", false, 13)]
    public static void Validate()
        => ValidationUtil.RunInteractive("Moving Turn", Run);

    public static void RunBatchValidate()
        => ValidationUtil.RunBatch("Moving Turn", Run);

    // How the wheels behaved through one held turn.
    private struct Turn
    {
        public string label;
        public int wheelReversals;         // spin-direction changes summed over every wheel
        public float meanAbsSlip;          // tyre surface speed against the ground under it, u/s
        public float peakAbsSlip;
        public float peakVerticalSpeed;    // the "jumping": how hard the chassis is coming off the floor
        public float yawDeg;               // ...and it still has to actually turn
        public float meanSpeed;
        public string deadestWheel;        // the wheel that turned least, BY NAME — see Measure
        public float deadestWheelFraction; // ...as a fraction of the wheel that turned most
    }

    private static string Run()
    {
        var lines = new System.Text.StringBuilder();
        var failures = new List<string>();
        int tested = 0, checks = 0, failed = 0;

        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;
            tested++;
            // Count robots that FAILED, not robots measured. Reporting `tested` here sent two
            // separate investigations looking for a fourth broken robot that does not exist:
            // 4 robots are measured, 3 fail, and 654V_v3 passes cleanly.
            int before = failures.Count;
            checks += OneRobot(prefab, lines, failures);
            if (failures.Count > before) failed++;
        }

        ValidationUtil.Assert(tested > 0,
            $"no robot prefab with a RobotMotorController under {RoboSimPaths.RobotsFolder} — " +
            "nothing was measured");

        ValidationUtil.Assert(failures.Count == 0,
            $"{failures.Count} failure(s) across {failed} of {tested} robot(s) turning while moving " +
            "(one robot can contribute more than one):\n    " + string.Join("\n    ", failures) +
            "\n  Every one of these is measured against THE SAME ROBOT spinning from a standstill " +
            "with the same lift height and the same turn stick, so it is not a claim that turning " +
            "is rough — it is a claim that adding forward motion makes it rough. See the file " +
            "header for the two places that treat a moving turn differently from a standing one." +
            "\n  Full measurements:\n" + lines.ToString().TrimEnd());

        return $"A Moving Turn Is As Smooth As A Standing One: PASSED ({checks} checks) on {tested} " +
               $"robot(s).\n{lines.ToString().TrimEnd()}";
    }

    private static int OneRobot(GameObject prefab, System.Text.StringBuilder lines,
        List<string> failures)
    {
        // The control and the two complaints, in the order the report described them.
        Turn standing = Measure(prefab, "spin from rest", accelFirst: false, throttleInTurn: 0f);
        Turn entered = Measure(prefab, "forward, then spin", accelFirst: true, throttleInTurn: 1f);
        Turn added = Measure(prefab, "spinning, then throttle", accelFirst: false, throttleInTurn: 1f);

        lines.AppendLine($"'{prefab.name}':");
        Report(lines, standing);
        Report(lines, entered);
        Report(lines, added);

        int checks = 0;
        foreach (Turn moving in new[] { entered, added })
        {
            // A turn that did not happen is not a smooth turn. Checked first, because a robot whose
            // wheels are locked solid scores ZERO reversals and no slip and would sail through the
            // comparison below.
            if (moving.yawDeg < standing.yawDeg * 0.25f)
                failures.Add($"'{prefab.name}' ({moving.label}): turned {moving.yawDeg:0} deg against " +
                             $"{standing.yawDeg:0} from rest — adding throttle is not making the turn " +
                             "rough so much as stopping it happening");
            else
                Compare(prefab, standing, moving, failures);
            checks += 2;
        }
        return checks;
    }

    // The standing reference for the reversal ratio never reads below this. A robot whose spin from
    // rest is perfectly clean scores 0, and a ratio against 0 would demand EXACTLY zero moving
    // reversals — one snatch in two seconds across six wheels, and the bar would call a clean turn
    // rough. Four is a handful of sign flips, still far below any turn that is actually chattering.
    private const float MinReversalReference = 4f;

    private static void Compare(GameObject prefab, Turn standing, Turn moving, List<string> failures)
    {
        float reversals = Ratio(moving.wheelReversals,
            Mathf.Max(standing.wheelReversals, MinReversalReference));
        float slip = Ratio(moving.meanAbsSlip, standing.meanAbsSlip);

        if (reversals > MaxRoughnessMultiple || slip > MaxRoughnessMultiple)
            failures.Add(
                $"'{prefab.name}' ({moving.label}): {moving.wheelReversals} wheel direction changes " +
                $"against {standing.wheelReversals} from rest ({reversals:0.0}x) and mean slip " +
                $"{moving.meanAbsSlip:0.00} u/s against {standing.meanAbsSlip:0.00} ({slip:0.0}x), " +
                $"limit {MaxRoughnessMultiple:0.0}x; chassis lifting at {moving.peakVerticalSpeed:0.00} " +
                $"u/s against {standing.peakVerticalSpeed:0.00}");
    }

    // Guarded so a control that measured nothing reads as "no worse" rather than infinitely worse.
    private static float Ratio(float moving, float standing)
        => standing > 1e-3f ? moving / standing : (moving > 1e-3f ? float.PositiveInfinity : 1f);

    private static void Report(System.Text.StringBuilder lines, Turn t)
        => lines.AppendLine(
            $"    {t.label,-24} {t.wheelReversals,4} wheel direction changes · slip mean " +
            $"{t.meanAbsSlip:0.00} peak {t.peakAbsSlip:0.00} u/s · chassis lift " +
            $"{t.peakVerticalSpeed:0.00} u/s · turned {t.yawDeg:0} deg at {t.meanSpeed:0.0} u/s · " +
            $"deadest wheel {t.deadestWheel} at {t.deadestWheelFraction:0%} of the busiest");

    // One robot, lift raised, one held turn — either entered at speed or from a standstill.
    private static Turn Measure(GameObject prefab, string label, bool accelFirst, float throttleInTurn)
    {
        SimulationMode previousMode = Physics.simulationMode;
        try
        {
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
            motor.Initialise();

            Physics.simulationMode = SimulationMode.Script;
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);
            TipOverValidation.RaiseLifts(root, motor);
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);

            // "Starts going forwards and immediately goes into a spin" versus "was previously
            // completely stopped". This one flag is the entire difference between the two cases.
            if (accelFirst) TipOverValidation.StepDriven(motor, 1f, 0f, AccelSteps);

            ArticulationBody[] wheels = RobotPhysicsValidation.FindWheels(root, out _, out _);
            float radius = DrivetrainTuning.MeasureWheelRadius(wheels);
            var lastSpin = new float[wheels.Length];
            var spinSum = new float[wheels.Length];
            for (int w = 0; w < wheels.Length; w++) lastSpin[w] = Spin(wheels[w]);

            var result = new Turn { label = label };
            float lastYaw = root.transform.eulerAngles.y;
            float slipSum = 0f, speedSum = 0f;
            int slipSamples = 0;

            for (int i = 0; i < TurnSteps; i++)
            {
                TipOverValidation.StepDriven(motor, throttleInTurn, 1f, 1);

                // Recomputed each step: the robot is yawing, so a rolling direction captured before
                // the turn would be pointing somewhere else entirely two seconds later. It is the
                // DRIVE axis the controller measured off the wheel axles, not root.forward: 654V_v2
                // and v3 drive perpendicular to their own transform.forward (see MeasureDriveAxes),
                // so reading the root here measured the wrong component of the contact velocity on
                // two of the four robots.
                Vector3 rollAxis = Vector3.ProjectOnPlane(motor.DriveForwardWorld, Vector3.up).normalized;

                result.peakVerticalSpeed =
                    Mathf.Max(result.peakVerticalSpeed, Mathf.Abs(root.linearVelocity.y));
                speedSum += new Vector2(root.linearVelocity.x, root.linearVelocity.z).magnitude;

                // Accumulated per step. Two seconds of full-stick turn passes 180 degrees on every
                // one of these robots, and a start-to-end DeltaAngle would wrap and report a robot
                // that spun one and a half times as having barely moved — which would read as the
                // exact opposite of what happened, on the one gate that decides whether the rest of
                // the numbers mean anything.
                float yawNow = root.transform.eulerAngles.y;
                result.yawDeg += Mathf.Abs(Mathf.DeltaAngle(lastYaw, yawNow));
                lastYaw = yawNow;

                for (int w = 0; w < wheels.Length; w++)
                {
                    if (wheels[w] == null) continue;

                    // Direction changes in the wheel's own spin. A tyre tracking its command turns
                    // steadily; one snatching at the floor keeps swapping direction.
                    // A reversal is a sign flip with a real CHANGE of speed behind it — the floor is
                    // compared against the change in one step, in deg/s. It used to divide the change
                    // by the step first, so it was really 0.3 deg/s: a wheel held at zero by its drive
                    // jittering +-1 deg/s counted as snatching a hundred times a second.
                    float spin = Spin(wheels[w]);
                    if (i > 0 && Mathf.Sign(spin) != Mathf.Sign(lastSpin[w])
                        && Mathf.Abs(spin - lastSpin[w]) > WheelRateNoiseFloor) result.wheelReversals++;
                    lastSpin[w] = spin;
                    spinSum[w] += Mathf.Abs(spin);

                    // Slip: how fast the tyre surface is moving against the ground under it. The
                    // contact point's own world velocity is what the ground sees, and it already
                    // contains the robot's translation AND its yaw about that point, so nothing
                    // needs subtracting — doing it by hand would double-count the yaw.
                    //
                    // Rolling direction comes from the CONTROLLER's measured drive axis, not the
                    // wheel link: reading it off each wheel would let one mis-authored link quietly
                    // change what "slip" means for that wheel alone.
                    float surface = spin * Mathf.Deg2Rad * radius;
                    Vector3 contact = wheels[w].transform.position - Vector3.up * radius;
                    Vector3 ground = root.GetPointVelocity(contact);
                    float alongRoll = Vector3.Dot(ground, rollAxis);

                    float slip = Mathf.Abs(surface - alongRoll);
                    slipSum += slip;
                    slipSamples++;
                    result.peakAbsSlip = Mathf.Max(result.peakAbsSlip, slip);
                }
            }

            result.meanAbsSlip = slipSamples > 0 ? slipSum / slipSamples : 0f;
            result.meanSpeed = speedSum / TurnSteps;

            // PER WHEEL, ALWAYS — everything above this line is a total or a mean over all wheels,
            // and a mean is exactly how a dead wheel hides: one wheel at +5 deg/s beside two at
            // +1440 reads as a healthy +960. TurnAfterInteractionProbe learned this twice and says so
            // at its own per-wheel loop; this file went on averaging anyway, and the fault Connor
            // reported on 2026-09-06 was three wheels on one rail held at zero. So the quietest wheel
            // is named, against the busiest, and it costs one line of output.
            float busiest = 0f, quietest = float.PositiveInfinity;
            for (int w = 0; w < wheels.Length; w++)
            {
                if (wheels[w] == null) continue;
                busiest = Mathf.Max(busiest, spinSum[w]);
                if (spinSum[w] < quietest) { quietest = spinSum[w]; result.deadestWheel = wheels[w].name; }
            }
            result.deadestWheelFraction = busiest > 1e-3f ? quietest / busiest : 1f;
            return result;
        }
        finally { Physics.simulationMode = previousMode; }
    }

    private static float Spin(ArticulationBody wheel)
        => wheel != null && wheel.jointVelocity.dofCount > 0
            ? wheel.jointVelocity[0] * Mathf.Rad2Deg : 0f;
}
