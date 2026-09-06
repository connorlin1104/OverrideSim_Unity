using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

// The tyre's own validator and probes — see WheelTyreModel for what the tyre is.
//
// THE SPIKE, kept as the first check for good. A tyre that sets friction per contact through
// Physics.ContactModifyEvent only exists if that event fires for articulation-link colliders under
// edit-mode Physics.Simulate, and only matters if the number it sets reaches the solver. So the
// first check pushes a robot sideways with the tyre ON and then OFF — a floating box of the robot's
// own mass, driven at half the robot's weight, because the sustained-push case has to arrive through
// a CONTACT (that is what feeds the external-force term) and a force written straight onto the root
// would not exercise it — and demands a large slide in one case and none in the other. Either half
// failing is the tautology guard: a tyre that "works" against a shove nothing could resist proves
// nothing, and a tyre that changes nothing is not wired up.
//
// THE TRACE. mu is set inside the solver callback, so it cannot be watched from outside; the entry
// remembers the last value it set and the trace prints it per wheel per 50 ms through the five
// manoeuvres the design was argued on: a straight launch, a straight release, a standing pivot, a
// moving arc, and a sustained sideways push. The one number asserted is DITHER — the largest change
// in a wheel's mu between consecutive steps once a manoeuvre has settled — because the failure mode
// a per-contact friction rule invites is a two-step limit cycle (low mu, the wheel over-spins,
// high mu, it is yanked back), and that is invisible in every average and unmistakable step to step.
//
// PROBE: WHEEL RELEASE. Connor, 2026-09-05: "moving forwards and once I let go it should still go
// forward a bit more, but some wheels don't follow that" — and on 654V_v2 it is one wheel, the back
// right. The prefabs carry no per-wheel difference (same mass, radius, damping, drive on every
// link), so whatever singles a wheel out happens at run time, and the one thing that differs per
// wheel at run time is the LOAD it carries: the brake is split evenly by wheel count, so a wheel
// carrying a fifth of its even share locks under a brake the others roll through. This prints, per
// wheel, every 50 ms through a full-throttle run and its release: spin, normal load, and the step at
// which the wheel crossed the park gate or read as locked while the chassis was still moving. The
// per-SIDE means every other probe printed averaged exactly this away, twice.
public static class WheelTyreValidation
{
    private const int SettleSteps = 100;
    private const int DriveSteps = 150;        // 1.5 s at full throttle: terminal speed on every robot
    private const int ReleaseSteps = 250;      // 2.5 s of centred sticks
    private const int SampleEvery = 5;         // one row per 50 ms
    private const int SteadyWindow = 50;       // the last 0.5 s of the drive: the loads to compare
    private const float LockFraction = 0.1f;   // below this share of its rail's mean spin...
    private const float MovingChassis = 1f;    // ...while the chassis still does this, a wheel is locked

    // The spike's push: half the robot's weight, for half a second, through a box of its own mass.
    private const float PushWeightFraction = 0.5f;
    private const int PushSteps = 50;
    private const float MinOmniSlide = 1f;     // u, with the tyre on
    private const float MaxGrippedSlide = 0.2f; // u, with it off — the same push, resisted at 0.8

    // What a settled manoeuvre may do, per second and per step, in the things a driver can feel: a
    // wheel snatching (spin sign flips with a real change of speed behind them, MovingTurnValidation's
    // floor), the yaw rate jumping, the chassis lurching. The friction number itself is printed, not
    // asserted — on 654V_v3 it alternates ~9 times a second on the wheel the yaw centre hunts around
    // while all three of these stay flat, which is the measurement that decided it is benign.
    private const float MaxSpinReversalsPerSecond = 2f;
    private const float WheelRateNoiseFloor = 30f;      // deg/s of change in one step, as MovingTurnValidation
    private const float SnatchSpin = 100f;              // deg/s: one side of a snatch is a wheel really turning
    private const float MaxYawRateJitterFraction = 0.10f; // of the settled mean yaw rate, per step
    private const float MuFlipProminence = 0.2f;
    private const string SpikeRobot = "654V_v2";

    [MenuItem("Tools/RoboSim/Validate/Validate Wheel Tyre", false, 14)]
    public static void Validate() => ValidationUtil.RunInteractive("Wheel Tyre", Run);

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Wheel Tyre", Run);

    [MenuItem("Tools/RoboSim/Validate/Probe Wheel Release (per-wheel loads)", false, 60)]
    public static void ProbeRelease()
        => ValidationUtil.RunInteractive("Wheel Release Probe", RunProbeRelease);

    public static void RunBatchProbeRelease()
        => ValidationUtil.RunBatch("Wheel Release Probe", RunProbeRelease);

    [MenuItem("Tools/RoboSim/Validate/Probe Wheel Tyre Trace (mu per wheel)", false, 61)]
    public static void ProbeTrace() => ValidationUtil.RunInteractive("Wheel Tyre Trace", RunProbeTrace);

    public static void RunBatchProbeTrace() => ValidationUtil.RunBatch("Wheel Tyre Trace", RunProbeTrace);

    private static string Run()
    {
        bool previous = WheelTyreModel.FrictionEnabled;
        try
        {
            var lines = new StringBuilder();
            int checks = 0;
            checks += ContactModificationReachesTheWheels(lines, out float omniSlide);
            checks += AForwardPushIsHeld(lines);
            checks += ATractionPairResistsTheShove(lines, omniSlide);
            checks += TheTractionPairResolvesByDriveAxis(lines);
            checks += TheStandingPivotIsFaster(lines);
            checks += TheMovingArcTurns(lines);
            checks += AReleasedSpinCarriesOn(lines);
            checks += EveryWheelRollsOutTogether(lines);
            checks += StraightLineGripIsUnchanged(lines);
            checks += NoWheelDithers(lines);
            return $"Wheel Tyre: PASSED ({checks} checks)\n{lines.ToString().TrimEnd()}";
        }
        finally { WheelTyreModel.FrictionEnabled = previous; }
    }

    // --- The spike ------------------------------------------------------------------------------

    private static int ContactModificationReachesTheWheels(StringBuilder lines, out float omniSlide)
    {
        GameObject prefab = FindRobot(SpikeRobot);
        float on = SustainedPush(prefab, tyreOn: true, out string onDetail);
        float off = SustainedPush(prefab, tyreOn: false, out string offDetail);
        omniSlide = on;
        lines.AppendLine($"  sustained sideways push, '{prefab.name}': tyre ON slid {on:0.00} u ({onDetail}); " +
                         $"tyre OFF slid {off:0.00} u ({offDetail})");

        ValidationUtil.Assert(off <= MaxGrippedSlide,
            $"'{prefab.name}' slid {off:0.00} u under a push of {PushWeightFraction:0.0#} of its weight with the " +
            $"tyre OFF, more than {MaxGrippedSlide:0.0#} — so the push is one the isotropic wheels cannot " +
            "resist either, and a slide with the tyre on would prove nothing. Check the pusher's force and " +
            $"that the robot is standing on the rig floor. ({offDetail})");
        ValidationUtil.Assert(on >= MinOmniSlide,
            $"'{prefab.name}' slid only {on:0.00} u under the same push with the tyre ON (needs " +
            $"{MinOmniSlide:0.0#}). Either Physics.ContactModifyEvent is not firing for the wheel colliders " +
            "under edit-mode Physics.Simulate, or the friction it sets is not reaching the solver, or the " +
            "external-force term is not seeing the pusher's contact — read the per-wheel mu in the trace " +
            $"probe before touching anything else. ({onDetail}; tyre OFF: {offDetail})");
        return 2;
    }

    // Spawn, settle, plant a floating box of the robot's own mass against its right side (or behind
    // it), drive the box at the robot for PushSteps, and report how far the robot's centre of mass
    // went along the push. `pair` is written onto the controller before Initialise, the way a prefab
    // would carry it. `roll` is the largest lean off level the push produced.
    private static float SustainedPush(GameObject prefab, bool tyreOn, out string detail,
        RobotMotorController.TractionPair pair = RobotMotorController.TractionPair.None,
        bool fromBehind = false)
        => SustainedPush(prefab, tyreOn, out detail, out _, out _, pair, fromBehind);

    private static float SustainedPush(GameObject prefab, bool tyreOn, out string detail, out float roll,
        out float rolling, RobotMotorController.TractionPair pair, bool fromBehind)
    {
        SimulationMode previous = Physics.simulationMode;
        try
        {
            WheelTyreModel.FrictionEnabled = tyreOn;
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
            motor.tractionPair = pair;
            motor.Initialise();
            Physics.simulationMode = SimulationMode.Script;
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);

            ArticulationBody[] bodies = root.GetComponentsInChildren<ArticulationBody>(true);
            ArticulationBody[] wheels = RobotPhysicsValidation.FindWheels(root, out _, out _);
            float mass = DrivetrainTuning.MeasureTotalMass(root);
            float weight = mass * Mathf.Abs(Physics.gravity.y);
            Vector3 right = motor.DriveRightWorld;
            Vector3 fwd = motor.DriveForwardWorld;

            // Sideways: a box on the RIGHT flank pushing toward -right — a CONTACT, because that is
            // what feeds the external-force term. From behind: the same force applied at the centre
            // of mass instead. A box at the rear lands on a rear wheel's tread, and a rolling tread
            // under a pushing face is a contact whose solver treatment differs once the pair is
            // modifiable (measured 30% more resistance, tyre on, mu 0.80 throughout) — a contrived
            // contact that says nothing about the rolling direction this case is about.
            Vector3 push = fromBehind ? fwd : -right;
            Rigidbody pusher = fromBehind ? null : MakePusher(root, right, fwd, mass);
            Vector3 start = Com(bodies);
            Vector3 pusherStart = pusher != null ? pusher.position : Vector3.zero;
            int modifyBefore = WheelTyreModel.ModifyCallbacks, eventsBefore = WheelTyreModel.ContactEvents;
            float maxMu = -1f, minMu = 2f, maxExt = 0f;
            roll = 0f;
            float radius = DrivetrainTuning.MeasureWheelRadius(wheels);
            float dt = ValidationUtil.StepSeconds;
            Vector3 lastCom = start;
            float rollingSum = 0f; int rollingSamples = 0;
            for (int i = 0; i < PushSteps; i++)
            {
                if (pusher != null) pusher.AddForce(push * (PushWeightFraction * weight), ForceMode.Force);
                else root.AddForceAtPosition(push * (PushWeightFraction * weight), Com(bodies), ForceMode.Force);
                TipOverValidation.StepDriven(motor, 0f, 0f, 1);
                foreach (ArticulationBody w in wheels)
                {
                    float mu = WheelTyreModel.PeekLastMu(w);
                    if (mu >= 0f) { maxMu = Mathf.Max(maxMu, mu); minMu = Mathf.Min(minMu, mu); }
                }
                maxExt = Mathf.Max(maxExt, WheelTyreModel.PeekExternalLateral(motor));
                roll = Mathf.Max(roll, Vector3.Angle(root.transform.up, Vector3.up));

                // Rolling or sliding: the wheels' surface speed against the chassis speed, over the
                // second half of the push. 1 = the wheels turn with the ground, 0 = they do not.
                Vector3 com = Com(bodies);
                float speed = Planar(com - lastCom).magnitude / dt;
                lastCom = com;
                if (i >= PushSteps / 2 && speed > 0.5f)
                {
                    float surface = 0f;
                    foreach (ArticulationBody w in wheels) surface += Mathf.Abs(Spin(w)) * Mathf.Deg2Rad * radius;
                    rollingSum += surface / Mathf.Max(wheels.Length, 1) / speed;
                    rollingSamples++;
                }
            }
            rolling = rollingSamples > 0 ? rollingSum / rollingSamples : 0f;
            float slid = Vector3.Dot(Com(bodies) - start, push);
            float pusherTravel = pusher != null ? Vector3.Dot(pusher.position - pusherStart, push) : slid;
            detail = $"mu {(minMu <= 1f ? $"{minMu:0.00}..{maxMu:0.00}" : "never set")}, external lateral force up to " +
                     $"{maxExt / Mathf.Max(weight, 1e-3f):0.00} of weight, {WheelTyreModel.RegisteredWheelCount} wheels registered, " +
                     $"{WheelTyreModel.ModifyCallbacks - modifyBefore} modify callbacks / {WheelTyreModel.ContactEvents - eventsBefore} contact events " +
                     $"in {PushSteps} steps, pusher travelled {pusherTravel:0.00} u, wheels rolling {rolling:0.00} of the motion";
            return slid;
        }
        finally { Physics.simulationMode = previous; }
    }

    // A frictionless, weightless box the size of the robot's side, one finger off its right flank,
    // free only to move sideways. Dynamic, not kinematic: a kinematic pusher cannot be stopped and
    // would shove an isotropic robot just as far, which is exactly the comparison this must not lose.
    private static Rigidbody MakePusher(ArticulationBody root, Vector3 right, Vector3 fwd, float mass)
    {
        Bounds b = new Bounds(root.transform.position, Vector3.zero);
        bool any = false;
        foreach (Collider c in root.GetComponentsInChildren<Collider>(false))
        {
            if (c == null || !c.enabled || c.isTrigger) continue;
            if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
        }
        float extentRight = 0f;
        foreach (Collider c in root.GetComponentsInChildren<Collider>(false))
        {
            if (c == null || !c.enabled || c.isTrigger) continue;
            Bounds cb = c.bounds;
            foreach (Vector3 corner in Corners(cb))
                extentRight = Mathf.Max(extentRight, Vector3.Dot(corner - b.center, right));
        }

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "Pusher";
        float height = Mathf.Max(0.6f, b.size.y * 0.5f);
        float length = Mathf.Max(3f, Mathf.Abs(Vector3.Dot(b.size, fwd)) + 1f);
        go.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        go.transform.localScale = new Vector3(0.6f, height, length);
        Vector3 centre = b.center + right * (extentRight + 0.05f + 0.3f);
        centre.y = b.min.y + height * 0.5f + 0.05f;
        go.transform.position = centre;

        var mat = new PhysicsMaterial("Pusher") { dynamicFriction = 0f, staticFriction = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum, bounciness = 0f };
        go.GetComponent<Collider>().sharedMaterial = mat;

        Rigidbody rb = go.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.useGravity = false;
        rb.constraints = RigidbodyConstraints.FreezeRotation | RigidbodyConstraints.FreezePositionY;
        rb.position = centre;
        rb.rotation = go.transform.rotation;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        return rb;
    }

    private static IEnumerable<Vector3> Corners(Bounds b)
    {
        for (int i = 0; i < 8; i++)
            yield return new Vector3((i & 1) == 0 ? b.min.x : b.max.x, (i & 2) == 0 ? b.min.y : b.max.y,
                (i & 4) == 0 ? b.min.z : b.max.z);
    }

    // The same force from BEHIND, at the centre of mass. This is the other half of "anisotropic":
    // pushed sideways the robot SLIDES, wheels still, at the lateral coefficient; pushed along its
    // rolling direction it is HELD — the parked wheels grip at the full 0.8, the push is under the
    // friction cone, and the robot does not move — exactly as with the tyre off. (Through a box
    // instead of a force the same push rolls the robot away: the box's first touch knocks the
    // wheels past the park gate and a moving wheel with the sticks centred is on the coast brake by
    // design. That is legitimate too, but it measures the brake, not the tyre.)
    private const float MaxSlidingFraction = 0.3f;   // wheel surface speed over chassis speed, sideways
    private const float MaxForwardPushChange = 0.05f; // u, tyre on against off

    private static int AForwardPushIsHeld(StringBuilder lines)
    {
        GameObject prefab = FindRobot(SpikeRobot);
        float on = SustainedPush(prefab, tyreOn: true, out string onDetail, fromBehind: true);
        float off = SustainedPush(prefab, tyreOn: false, out string offDetail, fromBehind: true);
        SustainedPush(prefab, tyreOn: true, out _, out _, out float rollingSideways,
            RobotMotorController.TractionPair.None, fromBehind: false);
        lines.AppendLine($"  the same push from behind, '{prefab.name}': moved {on:0.00} u with the tyre, {off:0.00} without " +
                         $"(held); wheels turning {rollingSideways:0.00} of the motion when pushed sideways (sliding)");
        ValidationUtil.Assert(Mathf.Abs(on) <= MaxGrippedSlide,
            $"'{prefab.name}' moved {on:0.00} u when pushed from BEHIND with the tyre on (limit {MaxGrippedSlide:0.0#}) — " +
            $"the tyre has lost its grip ALONG the rolling direction, not just across it. ({onDetail})");
        ValidationUtil.Assert(Mathf.Abs(on - off) <= MaxForwardPushChange,
            $"'{prefab.name}' pushed from behind moved {on:0.00} u with the tyre and {off:0.00} without — a push along " +
            $"the rolling direction has no lateral motion and must be untouched by the tyre. ({onDetail}; off: {offDetail})");
        ValidationUtil.Assert(rollingSideways <= MaxSlidingFraction,
            $"'{prefab.name}' pushed sideways had its wheels turning {rollingSideways:0.00} of the way — a sideways slide " +
            "must not spin the wheels");
        return 3;
    }

    // Tick the box: the middle pair keeps its full sideways grip, so the same push slides the robot
    // less than half as far — and still does not lay it over.
    private static int ATractionPairResistsTheShove(StringBuilder lines, float omniSlide)
    {
        GameObject prefab = FindRobot(SpikeRobot);
        float withPair = SustainedPush(prefab, tyreOn: true, out string detail, out float roll, out _,
            RobotMotorController.TractionPair.Middle, fromBehind: false);
        lines.AppendLine($"  sideways push with a MIDDLE traction pair, '{prefab.name}': slid {withPair:0.00} u " +
                         $"against {omniSlide:0.00} all-omni, rolled {roll:0.0} deg ({detail})");
        ValidationUtil.Assert(withPair <= omniSlide * 0.5f,
            $"'{prefab.name}' slid {withPair:0.00} u with a middle traction pair against {omniSlide:0.00} u " +
            "all-omni — the pair is not resisting the shove. Check tractionPair reaches Initialise before " +
            $"the tyres register, and that the resolver picked two wheels. ({detail})");
        ValidationUtil.Assert(roll < 5f,
            $"'{prefab.name}' rolled {roll:0.0} deg under a sideways push with a traction pair — nothing " +
            "sideways may lay this robot over, traction wheels or not.");
        return 2;
    }

    // Front, Middle and Rear are read off the wheels' positions along the DRIVE axis the controller
    // measures from the axles, never off the root's own forward — which points sideways on 654V_v2
    // and v3. Pinned on the shipped prefabs, with the root-forward answer shown to differ on the two
    // robots where it would be wrong (the guard that proves the axis matters).
    private static int TheTractionPairResolvesByDriveAxis(StringBuilder lines)
    {
        int checks = 0;
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            RobotMotorController motor = prefab.GetComponent<RobotMotorController>();
            if (motor == null) continue;
            var wheels = new List<ArticulationBody>();
            if (motor.leftWheels != null) foreach (ArticulationBody w in motor.leftWheels) if (w != null) wheels.Add(w);
            int leftCount = wheels.Count;
            if (motor.rightWheels != null) foreach (ArticulationBody w in motor.rightWheels) if (w != null) wheels.Add(w);

            ValidationUtil.Assert(RobotMotorController.MeasureDriveAxesWorld(wheels, leftCount, motor.invertLeft,
                    motor.invertRight, out _, out Vector3 forward),
                $"'{prefab.name}': the wheel axles do not give a drive axis");

            bool[] front = RobotMotorController.ResolveTractionPair(RobotMotorController.TractionPair.Front, wheels, leftCount, forward, out _);
            bool[] rear = RobotMotorController.ResolveTractionPair(RobotMotorController.TractionPair.Rear, wheels, leftCount, forward, out _);
            bool[] middle = RobotMotorController.ResolveTractionPair(RobotMotorController.TractionPair.Middle, wheels, leftCount, forward, out string refused);

            // One wheel per rail, and Front is ahead of Rear along the drive axis on both rails.
            for (int side = 0; side < 2; side++)
            {
                int start = side == 0 ? 0 : leftCount, end = side == 0 ? leftCount : wheels.Count;
                int f = -1, r = -1, m = -1;
                for (int i = start; i < end; i++) { if (front[i]) f = i; if (rear[i]) r = i; if (middle[i]) m = i; }
                ValidationUtil.Assert(f >= 0 && r >= 0 && f != r,
                    $"'{prefab.name}': Front/Rear must each pick one distinct wheel on the {(side == 0 ? "left" : "right")} rail");
                ValidationUtil.Assert(Vector3.Dot(wheels[f].transform.position - wheels[r].transform.position, forward) > 0.1f,
                    $"'{prefab.name}': the Front pick must sit ahead of the Rear pick along the drive axis");
                int railCount = end - start;
                if (railCount % 2 == 1)
                    ValidationUtil.Assert(m >= 0 && m != f && m != r,
                        $"'{prefab.name}': a {railCount}-wheel rail must have a Middle wheel distinct from Front and Rear");
                else
                    ValidationUtil.Assert(m < 0 && refused.Length > 0,
                        $"'{prefab.name}': a {railCount}-wheel rail must refuse Middle and say why");
                checks += 3;
            }

            // The guard: on the two robots whose root forward is sideways, resolving on root.forward
            // must pick DIFFERENT wheels — otherwise this check could not tell the axes apart.
            Vector3 rootForward = Vector3.ProjectOnPlane(prefab.transform.forward, Vector3.up).normalized;
            if (Mathf.Abs(Vector3.Dot(rootForward, forward)) < 0.5f)
            {
                bool[] wrong = RobotMotorController.ResolveTractionPair(RobotMotorController.TractionPair.Front, wheels, leftCount, rootForward, out _);
                bool differs = false;
                for (int i = 0; i < wheels.Count; i++) differs |= wrong[i] != front[i];
                ValidationUtil.Assert(differs,
                    $"'{prefab.name}': resolving on root.forward picked the same Front wheels as the drive axis, " +
                    "so this check cannot tell the two axes apart");
                checks++;
            }

            var names = new List<string>();
            for (int i = 0; i < wheels.Count; i++) if (middle[i]) names.Add(Short(wheels[i].name));
            lines.AppendLine($"  '{prefab.name}': Middle -> {(names.Count > 0 ? string.Join(" + ", names) : "refused: " + refused)}");
        }
        ValidationUtil.Assert(checks > 0, "no robot prefab with a RobotMotorController was found");
        return checks;
    }

    // --- Manoeuvres, tyre ON against tyre OFF on the same robot --------------------------------------

    private const int PivotSteps = 150;
    private const int ArcRunUpSteps = 100;
    private const int ArcSteps = 150;
    private const float MinPivotYawDeg = 300f;          // in 1.5 s from rest, every robot
    private const float MinPivotGain = 1.3f;            // tyre ON over OFF: 360Rpm gains 1.44x, the 654Vs 1.6-2.5x
    private const float MinArcYawFraction = 0.4f;       // of the same robot's standing pivot yaw
    private const float MinCarriedYawDeg = 30f;         // after releasing a full pivot
    private const float MaxCarriedYawDeg = 400f;        // a 360 RPM pivot carries ~270 deg at 0.16 g
    private const float StoppedYawRate = 20f;           // deg/s
    private const float MaxGateSpreadSeconds = 0.15f;
    private const float MaxStraightLineChange = 0.02f;  // launch distance / roll-out, tyre on vs off

    private static int TheStandingPivotIsFaster(StringBuilder lines)
    {
        int checks = 0;
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab.GetComponent<RobotMotorController>() == null) continue;
            float on = PivotYaw(prefab, tyreOn: true);
            float off = PivotYaw(prefab, tyreOn: false);
            lines.AppendLine($"  standing pivot, '{prefab.name}': {on:0} deg in {PivotSteps * ValidationUtil.StepSeconds:0.0} s " +
                             $"with the tyre, {off:0} without");
            ValidationUtil.Assert(on >= MinPivotYawDeg,
                $"'{prefab.name}' pivoted only {on:0} deg in {PivotSteps * ValidationUtil.StepSeconds:0.0} s (needs {MinPivotYawDeg:0})");
            ValidationUtil.Assert(on >= off * MinPivotGain,
                $"'{prefab.name}' pivoted {on:0} deg with the tyre against {off:0} without — less than " +
                $"{MinPivotGain:0.0}x, so the tyre is not freeing the wheels away from the yaw centre");
            checks += 2;
        }
        ValidationUtil.Assert(checks > 0, "no robot prefab with a RobotMotorController was found");
        return checks;
    }

    private static int TheMovingArcTurns(StringBuilder lines)
    {
        int checks = 0;
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab.GetComponent<RobotMotorController>() == null) continue;
            float pivot = PivotYaw(prefab, tyreOn: true);
            float arc = ArcYaw(prefab, tyreOn: true, out float speed);
            lines.AppendLine($"  moving arc (throttle 0.5 + full turn from full speed), '{prefab.name}': {arc:0} deg " +
                             $"at {speed:0.0} u/s, against a standing pivot of {pivot:0}");
            ValidationUtil.Assert(arc >= pivot * MinArcYawFraction,
                $"'{prefab.name}' turned {arc:0} deg in a moving arc against {pivot:0} standing — forward " +
                "speed is still stopping the robot turning");
            checks++;
        }
        ValidationUtil.Assert(checks > 0, "no robot prefab with a RobotMotorController was found");
        return checks;
    }

    private static int AReleasedSpinCarriesOn(StringBuilder lines)
    {
        int checks = 0;
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab.GetComponent<RobotMotorController>() == null) continue;
            float carried = CarriedYaw(prefab, out float stopSeconds);
            lines.AppendLine($"  released from a full pivot, '{prefab.name}': carried {carried:0} deg and stopped in {stopSeconds:0.00} s");
            ValidationUtil.Assert(carried >= MinCarriedYawDeg,
                $"'{prefab.name}' carried only {carried:0} deg after the turn stick was released (needs " +
                $"{MinCarriedYawDeg:0}) — the spin is being killed instead of braked. Check that a release " +
                "puts every wheel on the coast torque at once (stick throw 0), not on stall torque.");
            ValidationUtil.Assert(carried <= MaxCarriedYawDeg,
                $"'{prefab.name}' carried {carried:0} deg after release (limit {MaxCarriedYawDeg:0}) — the brake is not biting");
            checks += 2;
        }
        ValidationUtil.Assert(checks > 0, "no robot prefab with a RobotMotorController was found");
        return checks;
    }

    private static int EveryWheelRollsOutTogether(StringBuilder lines)
    {
        int checks = 0;
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab.GetComponent<RobotMotorController>() == null) continue;
            foreach (float turn in new[] { 0f, 0.3f })
            {
                Release r = ReleaseRun(prefab, turn, tyreOn: true);
                lines.AppendLine($"  release after full throttle{(turn > 0f ? $" + turn {turn:0.0#}" : "")}, '{prefab.name}': " +
                                 $"gate crossings spread {r.gateSpreadSeconds:0.00} s, locked wheels: {(r.locked.Length > 0 ? r.locked : "none")}");
                // The spread is only a claim for the straight release: out of an arc the inner side is
                // slower by design and crosses the gate earlier. The lock check holds for both.
                if (turn == 0f)
                    ValidationUtil.Assert(r.gateSpreadSeconds <= MaxGateSpreadSeconds,
                        $"'{prefab.name}': the wheels crossed the park gate {r.gateSpreadSeconds:0.00} s apart on release " +
                        $"(limit {MaxGateSpreadSeconds:0.00}) — some wheels stop before the others");
                ValidationUtil.Assert(r.locked.Length == 0,
                    $"'{prefab.name}': {r.locked} read as locked (below a tenth of the rail's spin) while the chassis " +
                    "was still moving after release");
                checks += turn == 0f ? 2 : 1;
            }
        }
        ValidationUtil.Assert(checks > 0, "no robot prefab with a RobotMotorController was found");
        return checks;
    }

    // The tyre is keyed on lateral motion only, so a straight line must be untouched by it: the same
    // launch and the same roll-out, tyre on and off, to two percent.
    private static int StraightLineGripIsUnchanged(StringBuilder lines)
    {
        int checks = 0;
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab.GetComponent<RobotMotorController>() == null) continue;
            StraightLine on = StraightRun(prefab, tyreOn: true);
            StraightLine off = StraightRun(prefab, tyreOn: false);
            lines.AppendLine($"  straight line, '{prefab.name}': launch {on.launch:0.00} / {off.launch:0.00} u, " +
                             $"roll-out {on.rollout:0.00} / {off.rollout:0.00} u (tyre on / off)");
            ValidationUtil.Assert(Mathf.Abs(on.launch - off.launch) <= Mathf.Max(off.launch, 0.1f) * MaxStraightLineChange,
                $"'{prefab.name}': the tyre changed the 1 s launch from {off.launch:0.00} to {on.launch:0.00} u — a straight " +
                "line has no lateral motion and must be untouched");
            ValidationUtil.Assert(Mathf.Abs(on.rollout - off.rollout) <= Mathf.Max(off.rollout, 0.1f) * MaxStraightLineChange,
                $"'{prefab.name}': the tyre changed the roll-out from {off.rollout:0.00} to {on.rollout:0.00} u");
            checks += 2;
        }
        ValidationUtil.Assert(checks > 0, "no robot prefab with a RobotMotorController was found");
        return checks;
    }

    // --- Manoeuvre rigs ------------------------------------------------------------------------------

    private static float PivotYaw(GameObject prefab, bool tyreOn)
    {
        SimulationMode previous = Physics.simulationMode;
        try
        {
            WheelTyreModel.FrictionEnabled = tyreOn;
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
            motor.Initialise();
            Physics.simulationMode = SimulationMode.Script;
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);
            return AccumulateYaw(root, motor, 0f, 1f, PivotSteps, out _);
        }
        finally { Physics.simulationMode = previous; }
    }

    private static float ArcYaw(GameObject prefab, bool tyreOn, out float meanSpeed)
    {
        SimulationMode previous = Physics.simulationMode;
        try
        {
            WheelTyreModel.FrictionEnabled = tyreOn;
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
            motor.Initialise();
            Physics.simulationMode = SimulationMode.Script;
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);
            TipOverValidation.StepDriven(motor, 1f, 0f, ArcRunUpSteps);
            return AccumulateYaw(root, motor, 0.5f, 1f, ArcSteps, out meanSpeed);
        }
        finally { Physics.simulationMode = previous; }
    }

    private static float CarriedYaw(GameObject prefab, out float stopSeconds)
    {
        SimulationMode previous = Physics.simulationMode;
        try
        {
            WheelTyreModel.FrictionEnabled = true;
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
            motor.Initialise();
            Physics.simulationMode = SimulationMode.Script;
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);
            AccumulateYaw(root, motor, 0f, 1f, PivotSteps, out _);

            float dt = ValidationUtil.StepSeconds;
            float lastYaw = root.transform.eulerAngles.y;
            float carried = 0f;
            stopSeconds = 3f;
            for (int i = 0; i < 300; i++)
            {
                TipOverValidation.StepDriven(motor, 0f, 0f, 1);
                float yawNow = root.transform.eulerAngles.y;
                float delta = Mathf.DeltaAngle(lastYaw, yawNow);
                lastYaw = yawNow;
                carried += Mathf.Abs(delta);
                if (Mathf.Abs(delta) / dt < StoppedYawRate) { stopSeconds = i * dt; break; }
            }
            return carried;
        }
        finally { Physics.simulationMode = previous; }
    }

    private static float AccumulateYaw(ArticulationBody root, RobotMotorController motor, float throttle, float turn,
        int steps, out float meanSpeed)
    {
        ArticulationBody[] bodies = root.GetComponentsInChildren<ArticulationBody>(true);
        float dt = ValidationUtil.StepSeconds;
        float lastYaw = root.transform.eulerAngles.y;
        float yaw = 0f, speedSum = 0f;
        Vector3 lastCom = Com(bodies);
        for (int i = 0; i < steps; i++)
        {
            TipOverValidation.StepDriven(motor, throttle, turn, 1);
            float yawNow = root.transform.eulerAngles.y;
            yaw += Mathf.Abs(Mathf.DeltaAngle(lastYaw, yawNow));
            lastYaw = yawNow;
            Vector3 com = Com(bodies);
            speedSum += Planar(com - lastCom).magnitude / dt;
            lastCom = com;
        }
        meanSpeed = speedSum / Mathf.Max(steps, 1);
        return yaw;
    }

    private struct Release { public float gateSpreadSeconds; public string locked; }

    private static Release ReleaseRun(GameObject prefab, float turnDuringDrive, bool tyreOn)
    {
        SimulationMode previous = Physics.simulationMode;
        try
        {
            WheelTyreModel.FrictionEnabled = tyreOn;
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
            motor.Initialise();
            Physics.simulationMode = SimulationMode.Script;
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);
            TipOverValidation.StepDriven(motor, 1f, turnDuringDrive, DriveSteps);

            ArticulationBody[] wheels = RobotPhysicsValidation.FindWheels(root, out ArticulationBody[] left, out _);
            ArticulationBody[] bodies = root.GetComponentsInChildren<ArticulationBody>(true);
            int n = wheels.Length;
            var isLeft = new bool[n];
            for (int w = 0; w < n; w++) isLeft[w] = Array.IndexOf(left, wheels[w]) >= 0;
            float dt = ValidationUtil.StepSeconds;
            float gate = RobotMotorController.ParkGateDegPerSec(motor.maxWheelRpm * 6f, motor.Tuning.brakeG,
                motor.Tuning.topSpeed, Physics.gravity.y, dt);

            var firstBelowGate = new int[n];
            var lockedNames = new List<string>();
            for (int w = 0; w < n; w++) firstBelowGate[w] = -1;
            Vector3 lastCom = Com(bodies);
            for (int i = 0; i < ReleaseSteps; i++)
            {
                TipOverValidation.StepDriven(motor, 0f, 0f, 1);
                Vector3 com = Com(bodies);
                float speed = Planar(com - lastCom).magnitude / dt;
                lastCom = com;
                float leftMean = RailMean(wheels, isLeft, true), rightMean = RailMean(wheels, isLeft, false);
                for (int w = 0; w < n; w++)
                {
                    float spin = Mathf.Abs(Spin(wheels[w]));
                    if (firstBelowGate[w] < 0 && spin <= gate) firstBelowGate[w] = i;
                    float railMean = isLeft[w] ? leftMean : rightMean;
                    if (speed > MovingChassis && railMean > gate && spin < LockFraction * railMean
                        && !lockedNames.Contains(Short(wheels[w].name)))
                        lockedNames.Add(Short(wheels[w].name));
                }
            }
            int earliest = int.MaxValue, latest = -1;
            for (int w = 0; w < n; w++)
            {
                int at = firstBelowGate[w] < 0 ? ReleaseSteps : firstBelowGate[w];
                earliest = Mathf.Min(earliest, at); latest = Mathf.Max(latest, at);
            }
            return new Release
            {
                gateSpreadSeconds = n > 0 ? (latest - earliest) * dt : 0f,
                locked = string.Join(" ", lockedNames),
            };
        }
        finally { Physics.simulationMode = previous; }
    }

    private struct StraightLine { public float launch, rollout; }

    private static StraightLine StraightRun(GameObject prefab, bool tyreOn)
    {
        SimulationMode previous = Physics.simulationMode;
        try
        {
            WheelTyreModel.FrictionEnabled = tyreOn;
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
            motor.Initialise();
            Physics.simulationMode = SimulationMode.Script;
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);
            ArticulationBody[] bodies = root.GetComponentsInChildren<ArticulationBody>(true);

            Vector3 start = Com(bodies);
            TipOverValidation.StepDriven(motor, 1f, 0f, 100);
            float launch = Planar(Com(bodies) - start).magnitude;
            TipOverValidation.StepDriven(motor, 1f, 0f, DriveSteps - 100);
            Vector3 release = Com(bodies);
            TipOverValidation.StepDriven(motor, 0f, 0f, ReleaseSteps);
            float rollout = Planar(Com(bodies) - release).magnitude;
            return new StraightLine { launch = launch, rollout = rollout };
        }
        finally { Physics.simulationMode = previous; }
    }

    // --- The trace and its one assertion ----------------------------------------------------------

    private static int NoWheelDithers(StringBuilder lines)
    {
        int checks = 0;
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab.GetComponent<RobotMotorController>() == null) continue;
            string report = Trace(prefab, out Roughness r);
            lines.AppendLine(report);
            Debug.Log(report);   // before the asserts, so a failure still leaves the trace in the log
            ValidationUtil.Assert(r.spinReversalsPerSecond <= MaxSpinReversalsPerSecond,
                $"'{prefab.name}': a wheel snatched {r.spinReversalsPerSecond:0.0} times a second once settled " +
                $"({r.spinWhere}), more than {MaxSpinReversalsPerSecond:0.0#} — read the trace above for which " +
                "manoeuvre and wheel, and whether its friction is flickering with its lateral velocity.");
            ValidationUtil.Assert(r.yawJitterFraction <= MaxYawRateJitterFraction,
                $"'{prefab.name}': the yaw rate jumped {r.yawJitterFraction:P0} of its settled mean in one step " +
                $"({r.yawWhere}), more than {MaxYawRateJitterFraction:P0} — the turn is rough at the chassis.");
            checks += 2;
        }
        ValidationUtil.Assert(checks > 0, "no robot prefab with a RobotMotorController was found");
        return checks;
    }

    private struct Roughness
    {
        public float spinReversalsPerSecond, yawJitterFraction, chassisJerk;
        public string spinWhere, yawWhere, chassisWhere;
    }

    private static string RunProbeTrace()
    {
        bool previous = WheelTyreModel.FrictionEnabled;
        try
        {
            WheelTyreModel.FrictionEnabled = true;
            string filter = Environment.GetEnvironmentVariable("ROBOSIM_PROBE_ROBOT");
            var lines = new StringBuilder();
            int robots = 0;
            foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
            {
                if (prefab.GetComponent<RobotMotorController>() == null) continue;
                if (!string.IsNullOrEmpty(filter) && prefab.name != filter) continue;
                lines.AppendLine(Trace(prefab, out _));
                robots++;
            }
            ValidationUtil.Assert(robots > 0, "no robot prefab with a RobotMotorController was found");
            return $"Wheel Tyre Trace: {robots} robot(s)\n{lines.ToString().TrimEnd()}";
        }
        finally { WheelTyreModel.FrictionEnabled = previous; }
    }

    private struct Manoeuvre
    {
        public string label;
        public float throttle, turn;
        public int steps;
        public bool push;
    }

    private static string Trace(GameObject prefab, out Roughness roughness)
    {
        SimulationMode previous = Physics.simulationMode;
        bool wasOn = WheelTyreModel.FrictionEnabled;
        try
        {
            WheelTyreModel.FrictionEnabled = true;
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
            motor.Initialise();
            Physics.simulationMode = SimulationMode.Script;
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);

            ArticulationBody[] bodies = root.GetComponentsInChildren<ArticulationBody>(true);
            ArticulationBody[] wheels = RobotPhysicsValidation.FindWheels(root, out _, out _);
            int n = wheels.Length;
            float dt = ValidationUtil.StepSeconds;
            float mass = DrivetrainTuning.MeasureTotalMass(root);
            float weight = mass * Mathf.Abs(Physics.gravity.y);

            var sb = new StringBuilder();
            sb.AppendLine($"'{prefab.name}': {n} wheels; rows: t  chassis u/s  yaw deg/s  Fext/weight | per wheel: mu / spin deg/s / lateral u/s / force limit " +
                          $"(stall {motor.Tuning.stallTorque:0}, grip {motor.Tuning.gripTorque:0}, brake {motor.Tuning.brakeTorque:0})");
            var header = new StringBuilder("    wheel                       ");
            for (int w = 0; w < n; w++) header.Append($"{Short(wheels[w].name),16} ");
            sb.AppendLine(header.ToString());

            var plan = new[]
            {
                new Manoeuvre { label = "straight launch", throttle = 1f, turn = 0f, steps = 100 },
                new Manoeuvre { label = "straight release", throttle = 0f, turn = 0f, steps = 150 },
                new Manoeuvre { label = "standing pivot", throttle = 0f, turn = 1f, steps = 150 },
                new Manoeuvre { label = "release from pivot", throttle = 0f, turn = 0f, steps = 150 },
                new Manoeuvre { label = "straight run-up", throttle = 1f, turn = 0f, steps = 100 },
                new Manoeuvre { label = "moving arc (0.5 + full turn)", throttle = 0.5f, turn = 1f, steps = 150 },
                new Manoeuvre { label = "release from arc", throttle = 0f, turn = 0f, steps = 150 },
                new Manoeuvre { label = "straight run-up", throttle = 1f, turn = 0f, steps = 100 },
                new Manoeuvre { label = "moving spin (full + full turn, MovingTurnValidation's case)", throttle = 1f, turn = 1f, steps = 150 },
                new Manoeuvre { label = "release from spin", throttle = 0f, turn = 0f, steps = 100 },
                new Manoeuvre { label = "sustained sideways push", throttle = 0f, turn = 0f, steps = 100, push = true },
            };

            roughness = new Roughness { spinWhere = "", yawWhere = "", chassisWhere = "" };
            float dither = 0f; string worst = "";
            float maxJump = 0f; string jumpWhere = "";
            var lastMu = new float[n];
            var extremum = new float[n];      // the last turning point of each wheel's mu
            var rising = new bool[n];
            var flips = new int[n];           // grip<->slide alternations in the current manoeuvre
            var lastSpin = new float[n];
            var spinFlips = new int[n];
            float lastYawRate = 0f, lastSpeed = 0f, yawSum = 0f;
            int yawSamples = 0;
            Vector3 lastCom = Com(bodies);
            float lastYaw = root.transform.eulerAngles.y;
            Rigidbody pusher = null;
            int step = 0;

            foreach (Manoeuvre m in plan)
            {
                sb.AppendLine($"    -- {m.label} --");
                if (m.push && pusher == null)
                    pusher = MakePusher(root, motor.DriveRightWorld, motor.DriveForwardWorld, mass);
                for (int w = 0; w < n; w++) { lastMu[w] = -1f; extremum[w] = -1f; flips[w] = 0; spinFlips[w] = 0; lastSpin[w] = Spin(wheels[w]); }
                yawSum = 0f; yawSamples = 0;
                float settledYawJitter = 0f, settledJerk = 0f;
                for (int i = 0; i < m.steps; i++, step++)
                {
                    if (m.push) pusher.AddForce(-motor.DriveRightWorld * (PushWeightFraction * weight), ForceMode.Force);
                    TipOverValidation.StepDriven(motor, m.throttle, m.turn, 1);
                    Vector3 com = Com(bodies);
                    float speed = Planar(com - lastCom).magnitude / dt;
                    lastCom = com;
                    float yawNow = root.transform.eulerAngles.y;
                    float yawRate = Mathf.DeltaAngle(lastYaw, yawNow) / dt;
                    lastYaw = yawNow;
                    float ext = WheelTyreModel.PeekExternalLateral(motor) / Mathf.Max(weight, 1e-3f);

                    // The physical readings, once the manoeuvre has had 0.3 s to settle: wheel snatches,
                    // yaw-rate jumps, chassis lurches.
                    if (i >= 30)
                    {
                        settledYawJitter = Mathf.Max(settledYawJitter, Mathf.Abs(yawRate - lastYawRate));
                        settledJerk = Mathf.Max(settledJerk, Mathf.Abs(speed - lastSpeed));
                        yawSum += Mathf.Abs(yawRate); yawSamples++;
                        // A snatch is a locked wheel breaking free or a turning one being caught: a
                        // sign flip with a real change of speed behind it AND a real speed on one side.
                        // An unloaded wheel twitching a few tens of deg/s either side of zero while the
                        // robot slides is neither, and nobody can feel it.
                        for (int w = 0; w < n; w++)
                        {
                            float spin = Spin(wheels[w]);
                            if (Mathf.Sign(spin) != Mathf.Sign(lastSpin[w]) && Mathf.Abs(spin - lastSpin[w]) > WheelRateNoiseFloor
                                && Mathf.Max(Mathf.Abs(spin), Mathf.Abs(lastSpin[w])) > SnatchSpin)
                                spinFlips[w]++;
                        }
                    }
                    for (int w = 0; w < n; w++) lastSpin[w] = Spin(wheels[w]);
                    lastYawRate = yawRate; lastSpeed = speed;

                    // Two readings of the same trace, once the manoeuvre has had 0.3 s to settle.
                    // The largest single-step jump is printed for the record; what is ASSERTED is the
                    // alternation count — a wheel whose friction turns round by more than 0.2 again and
                    // again is the grip/slide relay this tyre must not have, and a relay can be slow.
                    for (int w = 0; w < n; w++)
                    {
                        float mu = WheelTyreModel.PeekLastMu(wheels[w]);
                        if (mu >= 0f && i >= 30)
                        {
                            if (lastMu[w] >= 0f)
                            {
                                float jump = Mathf.Abs(mu - lastMu[w]);
                                if (jump > maxJump)
                                {
                                    maxJump = jump;
                                    jumpWhere = $"{m.label}, {Short(wheels[w].name)} at +{i * dt:0.00} s, {lastMu[w]:0.00} -> {mu:0.00}";
                                }
                            }
                            if (extremum[w] < 0f) { extremum[w] = mu; rising[w] = true; }
                            else if (rising[w])
                            {
                                if (mu > extremum[w]) extremum[w] = mu;
                                else if (extremum[w] - mu > MuFlipProminence) { rising[w] = false; extremum[w] = mu; flips[w]++; }
                            }
                            else
                            {
                                if (mu < extremum[w]) extremum[w] = mu;
                                else if (mu - extremum[w] > MuFlipProminence) { rising[w] = true; extremum[w] = mu; flips[w]++; }
                            }
                        }
                        lastMu[w] = mu;
                    }

                    if (i == m.steps - 1)
                    {
                        float seconds = Mathf.Max((m.steps - 30) * dt, dt);
                        var perWheel = new List<string>();
                        var snatches = new List<string>();
                        for (int w = 0; w < n; w++)
                        {
                            float perSecond = flips[w] / seconds;
                            perWheel.Add($"{Short(wheels[w].name)} {perSecond:0.0}/s");
                            if (perSecond > dither) { dither = perSecond; worst = $"{m.label}, {Short(wheels[w].name)}"; }
                            float snatchRate = spinFlips[w] / seconds;
                            snatches.Add($"{Short(wheels[w].name)} {snatchRate:0.0}/s");
                            if (snatchRate > roughness.spinReversalsPerSecond)
                            {
                                roughness.spinReversalsPerSecond = snatchRate;
                                roughness.spinWhere = $"{m.label}, {Short(wheels[w].name)}";
                            }
                        }
                        // Yaw jitter only means something while a turn is HELD: a release decays to
                        // nothing, and a small jump against a small mean is not roughness.
                        float meanYaw = yawSamples > 0 ? yawSum / yawSamples : 0f;
                        float yawFrac = m.turn != 0f && !m.push && meanYaw > 30f ? settledYawJitter / meanYaw : 0f;
                        if (yawFrac > roughness.yawJitterFraction) { roughness.yawJitterFraction = yawFrac; roughness.yawWhere = m.label; }
                        if (settledJerk > roughness.chassisJerk) { roughness.chassisJerk = settledJerk; roughness.chassisWhere = m.label; }
                        sb.AppendLine($"    settled: wheel snatches [{string.Join(" ", snatches)}]  yaw-rate jump {settledYawJitter:0} deg/s per step " +
                                      $"({yawFrac:P0} of mean {meanYaw:0})  chassis jerk {settledJerk:0.00} u/s per step  " +
                                      $"mu alternations [{string.Join(" ", perWheel)}]");
                    }

                    if (i % SampleEvery == 0)
                    {
                        var row = new StringBuilder($"    {step * dt,5:0.00}  {speed,5:0.0}  {yawRate,6:0}  {ext,5:0.00} | ");
                        for (int w = 0; w < n; w++)
                        {
                            float mu = WheelTyreModel.PeekLastMu(wheels[w]);
                            string muText = mu >= 0f ? $"{mu:0.00}" : " -- ";
                            row.Append($"{muText}/{Spin(wheels[w]),5:0}/{WheelTyreModel.PeekLastLateral(wheels[w]),5:0.0}/{motor.ForceLimitOf(wheels[w]),3:0} ");
                        }
                        sb.AppendLine(row.ToString());
                    }
                }
            }
            sb.AppendLine($"    roughness: worst wheel snatch rate {roughness.spinReversalsPerSecond:0.0}/s" +
                          (roughness.spinWhere.Length > 0 ? $" ({roughness.spinWhere})" : "") +
                          $"; yaw-rate jitter {roughness.yawJitterFraction:P0} ({roughness.yawWhere}); chassis jerk " +
                          $"{roughness.chassisJerk:0.00} u/s ({roughness.chassisWhere}); mu alternations up to {dither:0.0}/s" +
                          (dither > 0f ? $" ({worst})" : "") +
                          $"; largest single-step mu change {maxJump:0.00}" + (maxJump > 0f ? $" ({jumpWhere})" : ""));
            return sb.ToString().TrimEnd();
        }
        finally
        {
            Physics.simulationMode = previous;
            WheelTyreModel.FrictionEnabled = wasOn;
        }
    }

    private static GameObject FindRobot(string name)
    {
        GameObject first = null;
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab.GetComponent<RobotMotorController>() == null) continue;
            if (prefab.name == name) return prefab;
            first ??= prefab;
        }
        ValidationUtil.Assert(first != null, "no robot prefab with a RobotMotorController was found");
        return first;
    }

    // --- Probe: wheel release -----------------------------------------------------------------------

    private static string RunProbeRelease()
    {
        string filter = Environment.GetEnvironmentVariable("ROBOSIM_PROBE_ROBOT");
        var lines = new StringBuilder();
        int robots = 0;
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab.GetComponent<RobotMotorController>() == null) continue;
            if (!string.IsNullOrEmpty(filter) && prefab.name != filter) continue;
            lines.AppendLine(ProbeOne(prefab, turnDuringDrive: 0f));
            lines.AppendLine(ProbeOne(prefab, turnDuringDrive: 0.3f));
            robots++;
        }
        ValidationUtil.Assert(robots > 0, "no robot prefab with a RobotMotorController was found");
        return $"Wheel Release Probe: {robots} robot(s), tyre friction " +
               $"{(WheelTyreModel.FrictionEnabled ? "ON" : "OFF")}\n{lines.ToString().TrimEnd()}";
    }
    private static string ProbeOne(GameObject prefab, float turnDuringDrive)
    {
        SimulationMode previous = Physics.simulationMode;
        try
        {
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
            motor.Initialise();
            Physics.simulationMode = SimulationMode.Script;
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);

            ArticulationBody[] wheels = RobotPhysicsValidation.FindWheels(root,
                out ArticulationBody[] left, out ArticulationBody[] right);
            ArticulationBody[] bodies = root.GetComponentsInChildren<ArticulationBody>(true);
            int n = wheels.Length;
            var isLeft = new bool[n];
            for (int w = 0; w < n; w++) isLeft[w] = Array.IndexOf(left, wheels[w]) >= 0;

            DrivetrainTuning.Result tune = motor.Tuning;
            float dt = ValidationUtil.StepSeconds;
            float gate = RobotMotorController.ParkGateDegPerSec(motor.maxWheelRpm * 6f, tune.brakeG,
                tune.topSpeed, Physics.gravity.y, dt);
            float weight = DrivetrainTuning.MeasureTotalMass(root) * Mathf.Abs(Physics.gravity.y);
            float evenShare = weight / Mathf.Max(n, 1);

            // Where each wheel sits along the drive axis, relative to the wheel centroid, so a load
            // can be read against a position ("the far rear wheel carries nothing").
            Vector3 fwd = motor.DriveForwardWorld;
            Vector3 centroid = Vector3.zero;
            foreach (ArticulationBody w in wheels) centroid += w.transform.position;
            centroid /= Mathf.Max(1, n);

            var sb = new StringBuilder();
            sb.AppendLine($"'{prefab.name}' ({(turnDuringDrive == 0f ? "straight" : $"arc, turn {turnDuringDrive:0.0#} held through the drive")}): " +
                          $"{n} wheels, park gate {gate:0} deg/s, weight {weight:0} " +
                          $"(even share {evenShare:0} per wheel), brakeG {tune.brakeG:0.00}");
            var header = new StringBuilder("    wheel        ");
            for (int w = 0; w < n; w++)
                header.Append($"{Short(wheels[w].name),6}({(isLeft[w] ? 'L' : 'R')}{Along(wheels[w], centroid, fwd),+5:0.0;-0.0}) ");
            sb.AppendLine(header.ToString());
            sb.AppendLine("    rows: t  chassis u/s | per wheel: spin deg/s / load (force)");

            var steadyLoad = new float[n];
            var firstBelowGate = new int[n];
            var firstLocked = new int[n];
            var lockedAtSpeed = new float[n];
            for (int w = 0; w < n; w++) { firstBelowGate[w] = -1; firstLocked[w] = -1; }

            Vector3 lastCom = Com(bodies);
            int step = 0;
            for (int phase = 0; phase < 2; phase++)
            {
                bool driving = phase == 0;
                int steps = driving ? DriveSteps : ReleaseSteps;
                sb.AppendLine(driving ? "    -- full throttle --" : "    -- released --");
                for (int i = 0; i < steps; i++, step++)
                {
                    // Read the loads BEFORE the controller consumes them for this step.
                    var load = new float[n];
                    for (int w = 0; w < n; w++) load[w] = WheelTyreModel.PeekNormalImpulse(wheels[w]) / dt;

                    TipOverValidation.StepDriven(motor, driving ? 1f : 0f, driving ? turnDuringDrive : 0f, 1);
                    Vector3 com = Com(bodies);
                    float speed = Planar(com - lastCom).magnitude / dt;
                    lastCom = com;

                    if (driving && i >= steps - SteadyWindow)
                        for (int w = 0; w < n; w++) steadyLoad[w] += load[w] / SteadyWindow;

                    if (!driving)
                    {
                        float leftMean = RailMean(wheels, isLeft, true);
                        float rightMean = RailMean(wheels, isLeft, false);
                        for (int w = 0; w < n; w++)
                        {
                            float spin = Mathf.Abs(Spin(wheels[w]));
                            if (firstBelowGate[w] < 0 && spin <= gate) firstBelowGate[w] = i;
                            float railMean = isLeft[w] ? leftMean : rightMean;
                            if (firstLocked[w] < 0 && speed > MovingChassis && railMean > gate
                                && spin < LockFraction * railMean)
                            {
                                firstLocked[w] = i;
                                lockedAtSpeed[w] = speed;
                            }
                        }
                    }

                    if (i % SampleEvery == 0)
                    {
                        var row = new StringBuilder($"    {step * dt,5:0.00}  {speed,5:0.0} | ");
                        for (int w = 0; w < n; w++)
                            row.Append($"{Spin(wheels[w]),6:0}/{load[w],4:0} ");
                        sb.AppendLine(row.ToString());
                    }
                }
            }

            sb.AppendLine("    summary per wheel (steady load vs even share; release: step below gate, step locked):");
            for (int w = 0; w < n; w++)
            {
                string locked = firstLocked[w] < 0 ? "never locked"
                    : $"LOCKED at +{firstLocked[w] * dt:0.00} s (chassis {lockedAtSpeed[w]:0.0} u/s)";
                string gated = firstBelowGate[w] < 0 ? "never below gate"
                    : $"below gate at +{firstBelowGate[w] * dt:0.00} s";
                sb.AppendLine($"      {Short(wheels[w].name),-5} {(isLeft[w] ? 'L' : 'R')} " +
                              $"along {Along(wheels[w], centroid, fwd),+5:0.00;-0.00}  " +
                              $"load {steadyLoad[w],5:0} ({steadyLoad[w] / Mathf.Max(evenShare, 1e-3f),4:0%} of even)  " +
                              $"gap {GapToFloor(wheels[w]) * 1000f,+4:0;-0} mm  {gated}, {locked}");
            }
            return sb.ToString().TrimEnd();
        }
        finally { Physics.simulationMode = previous; }
    }

    private static float RailMean(ArticulationBody[] wheels, bool[] isLeft, bool left)
    {
        float sum = 0f; int count = 0;
        for (int w = 0; w < wheels.Length; w++)
            if (isLeft[w] == left) { sum += Mathf.Abs(Spin(wheels[w])); count++; }
        return count > 0 ? sum / count : 0f;
    }

    private static float Along(ArticulationBody wheel, Vector3 centroid, Vector3 fwd)
        => Vector3.Dot(wheel.transform.position - centroid, fwd);

    private static Vector3 Planar(Vector3 v) => new Vector3(v.x, 0f, v.z);

    // Mass-weighted link positions: the composite centre of mass without worldCenterOfMass, which
    // is PhysX state and not to be trusted in edit mode.
    private static Vector3 Com(ArticulationBody[] bodies)
    {
        Vector3 weighted = Vector3.zero; float total = 0f;
        foreach (ArticulationBody b in bodies)
        {
            if (b == null || b.mass <= 0f) continue;
            weighted += b.transform.position * b.mass; total += b.mass;
        }
        return total > 0f ? weighted / total : Vector3.zero;
    }

    private static float Spin(ArticulationBody wheel)
        => wheel != null && wheel.jointVelocity.dofCount > 0 ? wheel.jointVelocity[0] * Mathf.Rad2Deg : 0f;

    // Lowest point of the wheel's own sphere above the rig floor's top face (y = 0).
    private static float GapToFloor(ArticulationBody wheel)
    {
        float lowest = float.PositiveInfinity;
        foreach (SphereCollider sphere in wheel.GetComponentsInChildren<SphereCollider>(true))
        {
            Vector3 centre = sphere.transform.TransformPoint(sphere.center);
            Vector3 lossy = sphere.transform.lossyScale;
            float scale = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Max(Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));
            lowest = Mathf.Min(lowest, centre.y - sphere.radius * scale);
        }
        return float.IsPositiveInfinity(lowest) ? float.NaN : lowest;
    }

    private static string Short(string name)
        => name.StartsWith("WheelLink_") ? name.Substring("WheelLink_".Length) : name;
}
