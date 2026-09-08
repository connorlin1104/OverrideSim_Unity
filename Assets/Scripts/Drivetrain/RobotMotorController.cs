using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Motor-torque drivetrain controller for the ArticulationBody-rigged robot.
//
// Unlike a velocity-teleport controller — which force-sets Rigidbody velocities and never yields
// to contacts — this drives the wheel links' revolute joints with torque-limited velocity drives,
// so the robot's speed emerges from motor strength vs. load: it can stall against a wall, get
// slowed by heavy pieces, and shove things with real contact forces instead of teleport-pushes.
//
// DRIVE FEEL. Five things shape it:
//
//   1. The motor curve. See DrivetrainTuning — with the old forceLimit 700 / damping 1000 the
//      drive was a bang-bang torque source for 99.95% of every acceleration, so half stick pulled
//      exactly as hard as full stick. Deriving damping from the free speed makes torque fall off
//      like a real motor's, so small inputs are genuinely gentler.
//   2. The command ramp (Slew). Keyboard W/A/S/D is exactly 0 or +-1, so without a rate limit a
//      100 ms tap of the turn key DEMANDS full authority and swings the robot ~12 degrees. Ramping
//      the command makes a short input a small input (~3 degrees) without making a deliberate turn
//      feel sluggish.
//   3. The brake. Centre stick is the brake pedal, like a car: released sticks command ZERO wheel
//      speed under the coast torque — 0.2 of the tyres' grip, the all-omni number, which rolls a
//      240 RPM robot on for about 0.28 m (see DrivetrainTuning) — and once a wheel AND THE ROBOT
//      are ONE BRAKE-STEP from stopped it parks under full Drive authority at target 0. Both,
//      because a wheel pinned at full motor torque while the chassis is still travelling is not a
//      parked robot resisting a shove, it is a locked wheel skidding. The gate is derived,
//      not pinned: a PhysX velocity drive handed stall torque deletes whatever speed is left in a
//      single step, so a gate anywhere above one brake-step of speed is a cliff. See ParkGateDegPerSec.
//        Note this is NOT the retired "Coast When You Let Go" checkbox coming back. That offered
//      the driver a choice between two drivetrains, one of which was wrong, and it took 60 ms to
//      engage so a stick swept through centre triggered it. This is one drivetrain with one brake,
//      engaged instantly, every time.
//   4. ONE AUTHORITY RULE, continuous in the stick. A wheel that is being ACCELERATED gets stall
//      torque and the motor curve does the rest. A wheel that is being BACK-DRIVEN — commanded
//      slower than it spins, or against it — gets a limit that runs from the coast torque at centre
//      stick up to stall torque at full stick: how hard the motor may slow a wheel is how far the
//      driver has thrown a stick, which is the voltage a real controller is applying. The inner
//      side of an arc, a pivot entered from speed and a slammed reversal all land at the top of that
//      ramp; an eased-off throttle lands part way up; letting go lands at the bottom, which is the
//      brake above, bit for bit. See DriveForceLimit, including why the top is stall and not the
//      tyre's grip.
//        This replaced three things that were each a patch on the same tyre: a steering exemption
//      that handed every wheel stall torque while the turn stick was held (and kept doing so for a
//      sixth of a second after a release, which is part of why a released spin died on the spot),
//      a separate "plow" ramp for reversals only, and a cap on back-driven wheels that one prefab
//      carried at 2. The tyre is fixed at the tyre now — WheelTyreModel: an omni grips along its
//      rolling direction and rolls freely across it — so a turn no longer has to scrub six tyres
//      sideways at full grip, and none of those compensations has anything left to compensate.
//
//   5. THE MIX. Throttle and turn are combined by scaling both together when they overflow a wheel
//      (MixArcade), so the balance the driver asked for survives and only the magnitude gives way.
//      It replaced a turn-priority mix that paid for the turn out of the throttle, and whose corner
//      case was the fault Connor reported on 2026-09-06: at full throttle a full turn stick
//      commanded the inner rail to a DEAD STOP, the authority rule enforced it at stall torque, and
//      three wheels skidded while the robot pivoted about them. "the back right wheel still goes
//      still faster than others ... the left side would all work."
//
//   Tipping is front-to-back ONLY, and that is physics now rather than a torque. A slammed
//      reversal can still put a raised lift on its nose, which is real. A sideways load meets omni
//      tyres that slide at a twentieth of their forward grip, so nothing sideways can push on this
//      robot hard enough to lay it over; a traction pair (tractionPair) gives two wheels their
//      full sideways grip back, still far short of what it would take. The roll-relief torque that
//      used to hold the frame level went with the scrub that made it necessary.
//
// Sign convention: the rig tool aligns every wheel link's local +X with the wrapper's +X
// (robot right), so a positive joint rotation about +X spins the tire such that its contact
// point at the bottom moves backward — which drives the robot FORWARD on BOTH sides (same
// rule the right-hand-rule cross product v = w x r gives in Unity's axes). The invert bools
// exist because an empirically flipped wheel mesh/axle can still reverse a side in practice.
// (Whether root +Z is forward is a separate question, answered from the wheels — MeasureDriveAxes.)
//
// Usage: added and fully wired (wheel arrays + input actions) by
// Tools > RoboSim > Robot > Mechanisms > Rig Drivetrain. Nothing to set up by hand.
public class RobotMotorController : MonoBehaviour
{
    [Header("Wheel Links (set by the Rig Drivetrain Articulation tool)")]
    public ArticulationBody[] leftWheels;
    public ArticulationBody[] rightWheels;

    [Header("Input Actions")]
    public InputActionReference leftJoystickAction;
    public InputActionReference rightJoystickAction;

    [Header("Motor Settings")]
    [Tooltip("Free-spin wheel speed at full stick, in RPM. New robots start on a VEX 200/240 RPM " +
             "green-cartridge drivetrain, which is what most competition bots actually run; raise " +
             "it per robot for a blue-cartridge speed build.")]
    public float maxWheelRpm = 240f;
    [Tooltip("LEGACY — ignored unless Auto Tune Drive is off. Drive force limit (motor stall torque). " +
             "The 700 the shipped prefabs carry is ~5x the traction budget, which is what made the " +
             "throttle an on/off switch; DrivetrainTuning now derives this from the robot instead.")]
    public float wheelStallTorque = 700f;
    [Tooltip("LEGACY — ignored unless Auto Tune Drive is off. Velocity drives use damping as the " +
             "velocity-tracking gain, and it MUST be > 0 or the drive produces no torque at all. At " +
             "the old 1000 it saturated forceLimit for 99.7% of every acceleration; DrivetrainTuning " +
             "sets it to stallTorque/freeSpeed so torque falls off like a real motor's.")]
    public float velocityDriveDamping = 1000f;
    [Tooltip("Coulomb friction on each wheel's axle (ArticulationBody.jointFriction) — bearing and " +
             "gearbox drag. Small, but kept because a real axle isn't frictionless.")]
    public float wheelRollingResistance = 0.3f;
    [Tooltip("Velocity-proportional spin loss on each wheel (ArticulationBody.angularDamping). " +
             "Measured contribution is under 1% of top speed — trim, not a tuning knob.")]
    public float wheelSpinDamping = 0.5f;
    [Tooltip("How much of full wheel speed the turn stick commands while the robot is MOVING at full " +
             "throttle. Lower = calmer turning at speed, and more of the throttle kept through an arc; " +
             "straight-line speed is unaffected. Standing still the stick commands Pivot Turn Rate " +
             "instead, and the two blend with the throttle.")]
    [Range(0.1f, 1f)]
    public float turnRate = DefaultTurnRate;
    [Tooltip("How much of full wheel speed the turn stick commands when the robot is STANDING STILL. " +
             "A pivot drives both sides at once, so this buys roughly twice the spin the same number " +
             "would give at speed; it blends down to Turn Rate as the throttle rises. Raise it for a " +
             "snappier point turn, lower it if the robot over-rotates on a short input. This is a " +
             "COMMAND, not an outcome — how far the robot actually comes round also depends on how " +
             "freely its wheels slide sideways (see WheelTyreModel).")]
    [Range(0.1f, 1f)]
    public float pivotTurnRate = DefaultPivotTurnRate;
    [Tooltip("Flip if the left side empirically drives backward (see sign convention in the file header).")]
    public bool invertLeft;
    [Tooltip("Flip if the right side empirically drives backward.")]
    public bool invertRight;
    [Tooltip("Solver iterations for the robot's articulation. ArticulationBody.solverIterations is NOT serialized, so setting it in the editor silently reverts to the project default (6) in play mode — it must be applied at runtime, here.")]
    public int solverIterations = 16;
    [Tooltip("Solver velocity iterations for the robot's articulation (project default is 1; see solverIterations).")]
    public int solverVelocityIterations = 8;

    // NOTE for everything below: these are NEW field names on purpose. The four shipped prefabs
    // serialize the old wheelStallTorque 700 / velocityDriveDamping 1000, and a prefab's saved
    // value always beats a changed C# default — but a field that isn't in the prefab's YAML at all
    // deserializes to its C# default. So new names are what let a retune reach every existing
    // robot without touching a single prefab.
    [Header("Drive Feel")]
    [Tooltip("Derive the wheel drive's stall torque and damping from this robot's own mass, wheel " +
             "radius, wheel count and gearing at Awake, ignoring Wheel Stall Torque / Velocity Drive " +
             "Damping above. On by default: the shipped prefabs pair their 700 stall torque with a " +
             "damping of 1000, which saturates the force limit over almost the whole speed range and " +
             "makes the throttle an on/off switch. Turn OFF only to hand-tune one robot.")]
    public bool autoTuneDrive = true;
    [Tooltip("Stall force at full stick as a multiple of the tyres' grip. 3 puts the traction " +
             "crossover at a third of stick travel: proportional below it, full authority above, " +
             "and a full-throttle launch spins the tyres the way a real one does. (It used to be the " +
             "only way the robot could turn at all; the tyre model took that job.)")]
    [Range(1f, 6f)]
    public float driveForceTractionMultiple = DrivetrainTuning.DefaultDriveForceTractionMultiple;
    [Tooltip("How hard the motors may brake on ALL-OMNI wheels, as a fraction of the tyres' grip. " +
             "This is also the brake pedal: centred sticks stop the robot with exactly this torque. " +
             "Low, because omni rollers have no sideways grip and a small contact patch forwards — " +
             "an all-omni robot rolls on when you let go, and that roll-out is the drift it has. " +
             "Every robot brakes on this number, traction pair or not: where a traction wheel " +
             "differs is its sideways grip, and that lives in the tyre, not the brake.")]
    [Range(0.1f, 1.5f)]
    public float omniBrakeFraction = DrivetrainTuning.DefaultOmniBrakeFraction;
    [Tooltip("Which pair of drive wheels, if any, are TRACTION wheels — rubber that grips sideways as " +
             "hard as it grips forwards — rather than omnis. None = an all-omni drive, which slides " +
             "when hit from the side and drifts through a turn (see WheelTyreModel). A pair keeps its " +
             "full sideways grip, so a sideways hit costs something and a turn holds its line. " +
             "Front / Middle / Rear are read off the wheels' positions along the drive axis at Awake; " +
             "a rail with an even number of wheels has no Middle. Set it from Set Up Imported Robot " +
             "or the Robot Setup Overview.")]
    public TractionPair tractionPair = TractionPair.None;

    [Header("Load Transfer (visual)")]
    [Tooltip("How far the body leans under acceleration and braking, in degrees at the traction " +
             "limit. VISUAL ONLY — it moves nothing the physics can feel. 0 disables it.")]
    [Range(0f, 8f)]
    public float loadTransferPitchDeg = 2.5f;
    [Tooltip("How quickly the lean follows the throttle, in rad/s. This is a suspension frequency: " +
             "a car's pitch mode is around 1.5-2 Hz, which is 10-13 here.")]
    public float loadTransferFrequency = 12f;
    [Tooltip("Damping ratio of that lean. Below 1 it overshoots and settles back, which is the " +
             "'momentarily' in weight transfer — 1.0 slides into place with no rebound at all.")]
    [Range(0.2f, 1.5f)]
    public float loadTransferDamping = 0.55f;

    [Header("Input Shaping")]
    [Tooltip("Stick travel ignored around centre, then rescaled so full stick still reaches 1.0. " +
             "Keyboard W/A/S/D is exactly 0 or +-1, so this only affects sticks (including drifty " +
             "gamepads).")]
    [Range(0f, 0.3f)]
    public float inputDeadzone = 0.08f;
    [Tooltip("Bends the stick curve toward cubic: 0 = linear, 1 = fully cubic. Finer control near " +
             "centre with the same authority at the ends. Analog only — keyboard never leaves the ends.")]
    [Range(0f, 1f)]
    public float throttleExpo = 0.35f;
    [Range(0f, 1f)]
    public float turnExpo = 0.55f;
    [Tooltip("How fast the throttle COMMAND may rise, in stick-units per second (4 = 0.25 s from a " +
             "standstill to full). Falls are quicker than rises so backing off still feels immediate.")]
    public float throttleRisePerSec = 4f;
    public float throttleFallPerSec = 8f;
    [Tooltip("Same for the turn command, and the fix for 'I tapped D and it spun for half a second': " +
             "at 3 per second a 100 ms tap reaches 0.30 of full turn instead of 1.00, so a short " +
             "input is a small input.")]
    public float turnRisePerSec = 3f;
    public float turnFallPerSec = 6f;

    private Vector2 leftStickInput;
    private Vector2 rightStickInput;

    // Test/autonomy hook: while set, FixedUpdate uses these instead of the stick reads.
    private bool manualInput;
    private float manualThrottle;
    private float manualTurn;

    // Rate-limited commands — what the driver has actually asked for so far, as opposed to where
    // the stick is right now.
    private float throttleCommand;
    private float turnCommand;

    private ArticulationBody[] allWheels = new ArticulationBody[0];
    private DrivetrainTuning.Result tuning;

    // The articulation root, cached: the load-transfer pose reads its motion every step, and
    // GetComponent 100 times a second is the kind of thing that shows up on a phone.
    private ArticulationBody rootBody;

    // Which of allWheels are the traction pair, resolved once in Initialise from tractionPair and
    // the wheels' positions along the measured drive axis. All false on an all-omni drive.
    private bool[] tractionFlags = new bool[0];

    // Load transfer. The two contact lines the body leans about and the acceleration that counts as
    // a full lean, all measured once in Initialise; the lean itself and where it came from, per step.
    private Vector3 frontPivotOffset;      // root-local, on the contact plane at the front axle
    private Vector3 rearPivotOffset;       // ...and at the rear
    private float fullLeanAccel;          // the traction limit, in world units/s^2
    private float lastForwardSpeed;
    private float leanDeg;                // nose-UP positive
    private float leanRateDegPerSec;
    private bool leanPosed;               // the transform currently carries the lean
    private Vector3 posedPosition;
    private Quaternion posedRotation;
    private Vector3 physicsPosition;
    private Quaternion physicsRotation;

    // How far the body is leaning right now, nose-UP positive. Read-only and render-only: nothing
    // in the physics reads it. Exposed so ChassisLeanValidation can watch a stop happen.
    public float LeanDegrees => leanDeg;

    // The last per-side command the mix produced, as a fraction of full wheel speed (left - right is
    // the differential). Read-only, and exposed for the same reason LeanDegrees is: it lets
    // DriveFeelValidation assert that the DRIVE really applies the pivot blend, not merely that
    // TurnRateFor computes it. The two MixArcade calls below are exactly what a refactor drops
    // silently, and arithmetic on a static method cannot notice.
    public float LeftCommand { get; private set; }
    public float RightCommand { get; private set; }

    // Which way this robot actually drives, in world space, measured from its wheels in Initialise.
    // Public because every harness that drives a robot in a straight line and then measures anything
    // directional has to agree with the controller about which way that was — TipOverValidation's
    // lateral balance and RobotBalanceWindow's two margins were both wrong for exactly that reason.
    public Vector3 DriveForwardWorld => DriveForward;
    public Vector3 DriveRightWorld => DriveRight;

    // The force limit this wheel's drive was last given, for the probes that print per wheel.
    public float ForceLimitOf(ArticulationBody wheel)
    {
        for (int i = 0; i < allWheels.Length && i < wheelForceLimit.Length; i++)
            if (allWheels[i] == wheel) return wheelForceLimit[i];
        return float.NaN;
    }

    // The motor model this robot was tuned to at Initialise — read-only, for the validators and
    // probes that need brakeG, topSpeed or the stall torque without re-deriving them.
    public DrivetrainTuning.Result Tuning => tuning;

    // The force limit each wheel's drive currently carries, so the per-step decision below only
    // writes an ArticulationDrive struct when it actually changes. Same reason MotorActuator keeps
    // its hold flag: this runs 100 times a second across every wheel.
    //
    // A float, because a back-driven wheel's limit slides between the coast torque and stall torque
    // with how far the stick has been thrown. The epsilon below is what keeps that from becoming six
    // marshalled struct writes per step on stick noise.
    private float[] wheelForceLimit = new float[0];
    private float forceLimitEpsilon;


    // WHICH WAY THIS ROBOT ACTUALLY DRIVES, in the root's own frame. Measured from the wheels in
    // Initialise; see MeasureDriveAxes for why it cannot be assumed.
    private Vector3 driveRightLocal = Vector3.right;
    private Vector3 driveForwardLocal = Vector3.forward;

    // The same two, in world space, valid the moment they are read.
    private Vector3 DriveRight => rootBody != null
        ? rootBody.transform.TransformDirection(driveRightLocal) : Vector3.right;
    private Vector3 DriveForward => rootBody != null
        ? rootBody.transform.TransformDirection(driveForwardLocal) : Vector3.forward;

    // How many entries at the start of allWheels are left-side wheels. Awake fills allWheels left
    // side first, and the braking-quadrant check needs to know which command each wheel was given.
    private int leftWheelCount;

    public enum TractionPair { None, Front, Middle, Rear }

    // THESE DEFAULTS DO NOT REACH EVERY ROBOT — check the prefabs when you move one. A saved value
    // beats a changed default, and the shipped prefabs are not consistent about which they serialize:
    // turnRate is written in all four, pivotTurnRate in 654V_v2 and 654V_v3 but not in 654V_v1 or
    // 360RpmDrivetrain. (The note that used to sit here claimed pivotTurnRate was absent from all of
    // them; it was true when written on 08-30 and stopped being true when v2 and v3 were next saved.)
    // Changing a constant alone therefore ships two robots on the new feel and two on the old.
    //
    // 0.65 / 0.35 (2026-09-06). Both came down together, and the pivot's reason is not a taste change:
    // the command never moved, the PHYSICS underneath it did. pivotTurnRate went to 1.0 on 08-31
    // because Connor wanted a standing pivot "much faster", and against the scrubbing isotropic tyre
    // of the day it bought 331 deg in 1.5 s. WheelTyreModel then deleted the scrub, and the same
    // command started producing 520 deg in the same window — "the full speed pivot is wayyy too fast",
    // 09-06. 0.65 puts the ROBOT back where he approved it (~225 deg/s) rather than the number.
    // 0.35 is the other half of "the forward should be more overpowering": with the proportional mix
    // it leaves a full-throttle full-turn arc at 3/4 speed instead of half. See TurnRateFor, MixArcade.
    public const float DefaultPivotTurnRate = 0.65f;

    // Named for the same reason pivotTurnRate is: DriveFeelValidation used to hardcode 0.5 beside a
    // field it could not see change.
    public const float DefaultTurnRate = 0.35f;

    // Player prefs, snapshotted at Awake (see DriveFeelSettings for why they aren't read live).
    private float driveSensitivity = DriveFeelSettings.DefaultDriveSensitivity;
    private float turnSensitivity = DriveFeelSettings.DefaultTurnSensitivity;

    // How hard this robot may brake. It used to be picked by a Settings checkbox — "My Robot Has
    // Traction Wheels" — which asked the player a question about their hardware that the sim can't
    // read off the model and that they had no reason to answer correctly. The box is gone, and with
    // it the traction branch: every robot now brakes like the all-omni drive almost all of them are,
    // which is what the box already defaulted to.
    public float BrakeFraction => omniBrakeFraction;

    void Awake() => Initialise();

    // --- Built-in self-overlap ----------------------------------------------------------------

    // Stop the robot's own parts fighting each other.
    //
    // A robot is one articulation, and PhysX collides any two links that are not parent and child.
    // GeneratePartColliders wraps every component in its own tight box, and on a real assembly
    // neighbouring components are bolted THROUGH one another — so a handful of those boxes
    // interpenetrate in the robot's rest pose and never stop. That is not a collision, it is a
    // permanent contact the solver pushes apart every single step.
    //
    // WHAT IT COSTS, measured on a clean floor at full throttle into a full-stick turn:
    //   654V_v3 had its 34 g goal aligner permanently 6.5 mm inside TWO drive wheels, plus two
    //   outtake links inside each other. Clearing four pairs took the roll direction from 115
    //   reversals in 3 s to ZERO, the accumulated rocking from 7.4 degrees to 0.0, and the speed it
    //   kept through the turn from 1.83 u/s to 5.86. That chatter is what "wobbling all over the
    //   place" is, and the two dragged wheels are why the same robot turned so badly.
    //   654V_v1 had five pairs: its turn went from 62 to 162 degrees and 0.76 to 5.98 u/s.
    //   654V_v2 and 360RpmDrivetrain have none, and measure bit-for-bit identically either way —
    //   which is the control that says this changes nothing on a robot that was already clean.
    //
    // ONLY pairs that already overlap in the REST POSE are ignored, and that scoping is the whole
    // argument for doing it at all: parts built into each other are parts real hardware bolts
    // together, and bolted parts do not push each other apart. Anything that collides only once a
    // mechanism moves — a claw closing onto the frame — still collides, because it does not overlap
    // here. Robots arrive from player CAD (see the upload pipeline), so this has to be automatic
    // rather than a note in a README. Pass a report list (RaisedLiftOverlapProbe does) to get the
    // offending pairs back, so the geometry can be corrected at the source.
    public static int IgnoreBuiltInSelfOverlaps(ArticulationBody root, List<string> report = null)
    {
        if (root == null) return 0;
        Physics.SyncTransforms();

        var colliders = new List<Collider>();
        foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
            if (c != null && c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy) colliders.Add(c);

        // The owning link of each collider, resolved once. GetComponentInParent walks the hierarchy
        // and this is an O(n^2) pass over a few hundred colliders.
        var owners = new ArticulationBody[colliders.Count];
        for (int i = 0; i < colliders.Count; i++)
            owners[i] = colliders[i].GetComponentInParent<ArticulationBody>(true);

        // The link each one is JOINTED to — its nearest ancestor body, which is not the same thing
        // as its nearest ancestor. See the exemption below.
        var jointParents = new Dictionary<ArticulationBody, ArticulationBody>();
        foreach (ArticulationBody body in root.GetComponentsInChildren<ArticulationBody>(true))
        {
            ArticulationBody parent = null;
            for (Transform t = body.transform.parent; t != null && parent == null; t = t.parent)
                parent = t.GetComponent<ArticulationBody>();
            jointParents[body] = parent;
        }

        int ignored = 0;
        for (int i = 0; i < colliders.Count; i++)
        {
            for (int j = i + 1; j < colliders.Count; j++)
            {
                ArticulationBody a = owners[i], b = owners[j];
                if (a == null || b == null || a == b) continue;
                // Parent and child of the same JOINT are never collided by PhysX anyway. This used
                // to ask whether one was a hierarchy descendant of the other, which is a different
                // question and was only accidentally the same one: every link was jointed straight
                // to the chassis, so descendant and child coincided. The moment a link went in
                // BETWEEN — the wheel droop links, WheelDroopRig — the chassis and the wheels became
                // grandparent and grandchild, PhysX started colliding them, and this exemption
                // silently skipped exactly the pairs that needed clearing: 654V_v2 threw itself onto
                // its back inside 1.5 s, gaining height as it went.
                if (jointParents.TryGetValue(a, out ArticulationBody aParent) && aParent == b) continue;
                if (jointParents.TryGetValue(b, out ArticulationBody bParent) && bParent == a) continue;
                if (!OverlapsAtRest(colliders[i], colliders[j], out float depth)) continue;

                Physics.IgnoreCollision(colliders[i], colliders[j], true);
                ignored++;
                report?.Add($"{a.name}/{colliders[i].name} <-> {b.name}/{colliders[j].name} " +
                            $"({depth * 100f:0.0} mm)");
            }
        }
        return ignored;
    }

    // Size every droop spring from the mass this robot actually measures, the way the wheel drives
    // are sized. The rig tool bakes a value too — edit-mode simulation never runs Awake — but a
    // robot that has since gained a mechanism, or arrived from a player's CAD at a different weight,
    // must not be left holding a spring tuned for someone else's robot: sag scales with the share of
    // the weight each wheel carries, so the wrong stiffness is the wrong ride height and the wrong
    // split. Travel and the stops are geometry and stay exactly as the rig built them.
    // ROBOSIM_DROOP_OFF=1 locks every droop joint rigid, so any batch validator can be run against
    // the drivetrain as it was before the travel existed. The A/B switch, as ROBOSIM_TYRE_OFF is for
    // the tyre: two changes landed together here and only a switch can say which one moved a number.
    private static bool DroopDisabled =>
        System.Environment.GetEnvironmentVariable("ROBOSIM_DROOP_OFF") == "1";

    private void BakeDroopSprings(ArticulationBody root)
    {
        if (root == null || allWheels.Length == 0) return;
        DrivetrainTuning.DroopSpring(DrivetrainTuning.MeasureTotalMass(root), allWheels.Length,
            Physics.gravity.y, out float stiffness, out float damping);

        foreach (ArticulationBody wheel in allWheels)
        {
            Transform parent = wheel != null ? wheel.transform.parent : null;
            ArticulationBody droop = parent != null ? parent.GetComponent<ArticulationBody>() : null;
            if (droop == null || droop.jointType != ArticulationJointType.PrismaticJoint) continue;
            if (!droop.name.StartsWith(WheelDroopNamePrefix)) continue;

            if (DroopDisabled)
            {
                droop.linearLockX = ArticulationDofLock.LockedMotion;
                continue;
            }

            ArticulationDrive d = droop.xDrive;
            d.stiffness = stiffness;
            d.damping = damping;
            droop.xDrive = d;
        }
    }

    // A wheel on a droop link has to be allowed to move up into the frame.
    //
    // PhysX never collides the two links of a joint, which is why a wheel bolted straight to the
    // chassis can sit inside the frame rails as it does on every real robot. WheelDroopRig puts a
    // link BETWEEN them, and that exemption does not reach across it: the wheel and the chassis
    // become grandchild and grandparent, and PhysX starts collidng them. Clearing the pairs that
    // already overlap at rest is not enough either, because the whole point of the droop joint is
    // that the wheel MOVES — a few millimetres up, straight into frame it did not overlap when it
    // was parked.
    //
    // So the exemption is restored explicitly across the droop: a wheel link and the link its droop
    // joints to never collide, whatever the travel does. They are millimetres apart by construction
    // and mechanically one assembly; nothing is being hidden that a real robot would feel.
    public static int IgnoreAcrossDroop(ArticulationBody root)
    {
        if (root == null) return 0;
        int cleared = 0;
        foreach (ArticulationBody droop in root.GetComponentsInChildren<ArticulationBody>(true))
        {
            if (droop == null || droop == root) continue;
            if (droop.jointType != ArticulationJointType.PrismaticJoint) continue;
            if (!droop.name.StartsWith(WheelDroopNamePrefix)) continue;

            ArticulationBody above = null;
            for (Transform t = droop.transform.parent; t != null && above == null; t = t.parent)
                above = t.GetComponent<ArticulationBody>();
            if (above == null) continue;

            foreach (Collider below in droop.GetComponentsInChildren<Collider>(true))
            {
                if (below == null || below.isTrigger) continue;
                foreach (Collider other in above.GetComponentsInChildren<Collider>(true))
                {
                    if (other == null || other.isTrigger) continue;
                    // Only colliders the LINK above owns: anything under another body of its own is
                    // that body's business, and jointing across it is not what this is about.
                    if (other.GetComponentInParent<ArticulationBody>(true) != above) continue;
                    Physics.IgnoreCollision(below, other, true);
                    cleared++;
                }
            }
        }
        return cleared;
    }

    // The name the drivetrain rig gives a droop link. Kept here rather than reaching into the editor
    // assembly, which the runtime cannot see; WheelDroopRig.DroopNamePrefix is pinned to it.
    public const string WheelDroopNamePrefix = "WheelDroop_";

    // Below this, an "overlap" is two boxes touching at a shared face, not one part inside another.
    // 0.001 units is 0.1 mm at this project's scale.
    public const float RestPoseOverlapEpsilon = 0.001f;

    // Do these two colliders interpenetrate where the robot stands right now, by more than a
    // touching face? The AABB reject comes first because ComputePenetration is a real geometry
    // query and the pass above asks it of tens of thousands of pairs.
    public static bool OverlapsAtRest(Collider a, Collider b, out float depth)
    {
        depth = 0f;
        if (a == null || b == null || !a.bounds.Intersects(b.bounds)) return false;
        if (!Physics.ComputePenetration(a, a.transform.position, a.transform.rotation,
                b, b.transform.position, b.transform.rotation, out _, out depth)) return false;
        return depth > RestPoseOverlapEpsilon;
    }

    public static bool OverlapsAtRest(Collider a, Collider b) => OverlapsAtRest(a, b, out _);

    // Everything Awake does, minus the input actions — public for the same reason
    // Dr4bBallast.BakeDrive and JointCoupler.BakeDrive are: edit-mode Physics.Simulate never runs
    // Awake, so a validator that skips this simulates a robot whose wheels have no drive baked, no
    // tuning computed, and the project's default solver iterations instead of 16/8.
    //
    // Pair it with ApplyStep. A harness that calls neither is not testing the drivetrain — it is
    // testing whatever numbers happen to be serialized on the prefab, which is how the turn check
    // came to report 0.1 degrees of roll on a robot that wobbles in play.
    public void Initialise()
    {
        // Firm contacts against the mass-1 pieces. solverIterations is a runtime-only
        // property (not serialized), so the rig tool's edit-time values never survive into
        // play mode — this is the authoritative place to set them.
        ArticulationBody root = GetComponent<ArticulationBody>();
        rootBody = root;
        if (root != null)
        {
            root.solverIterations = solverIterations;
            root.solverVelocityIterations = solverVelocityIterations;
            IgnoreBuiltInSelfOverlaps(root);
            IgnoreAcrossDroop(root);
        }

        // Snapshot the player's feel prefs once. Entering the field scene always re-runs Awake, so
        // a change in Settings still lands on the next Drive.
        driveSensitivity = DriveFeelSettings.DriveSensitivity;
        turnSensitivity = DriveFeelSettings.TurnSensitivity;

        var wheels = new System.Collections.Generic.List<ArticulationBody>();
        if (leftWheels != null) foreach (ArticulationBody w in leftWheels) if (w != null) wheels.Add(w);
        leftWheelCount = wheels.Count;
        if (rightWheels != null) foreach (ArticulationBody w in rightWheels) if (w != null) wheels.Add(w);
        allWheels = wheels.ToArray();
        wheelForceLimit = new float[allWheels.Length];

        // Measure the robot, then derive the motor model from it, so a heavier or differently
        // geared robot is tuned correctly without anyone editing a prefab. Diagnostics come back
        // in the same struct and are what DriveFeelValidation asserts on.
        tuning = DrivetrainTuning.Compute(
            DrivetrainTuning.MeasureTotalMass(root),
            DrivetrainTuning.MeasureWheelRadius(allWheels),
            allWheels.Length,
            maxWheelRpm,
            DrivetrainTuning.MeasureFriction(allWheels),
            Physics.gravity.y,
            driveForceTractionMultiple,
            BrakeFraction);

        if (!autoTuneDrive)
        {
            // Escape hatch: keep the serialized numbers for stall/damping, but still take the
            // derived coast torque and joint-velocity cap — those are new concepts with no legacy
            // value to preserve.
            tuning.stallTorque = wheelStallTorque;
            tuning.damping = velocityDriveDamping;
        }

        // Mirrors the bake below: every wheel leaves Awake carrying stallTorque, so that is what the
        // change tracker starts from. Half a percent of stall is the noise floor — below it a
        // difference is a drifting stick, not a decision worth a marshalled struct write.
        // Sized against brakeTorque, not stallTorque. It was 0.5% of stall, which is 7.5% of the
        // brake — a fine deadband when the only two values written were "brake" and "stall", and far
        // too coarse now that a load share moves the value continuously: the brake range would have
        // had about thirteen distinct settings in it. See SetForceLimit, which quantises onto this.
        BakeDroopSprings(root);

        forceLimitEpsilon = Mathf.Max(tuning.brakeTorque * 0.05f, 1e-4f);
        for (int i = 0; i < wheelForceLimit.Length; i++) wheelForceLimit[i] = tuning.stallTorque;

        // Bake the motor model into every wheel joint's X drive. Velocity drives need
        // stiffness 0 (no position spring) and damping > 0 (the velocity gain); forceLimit
        // is what makes this behave like a torque-limited motor instead of a hard constraint.
        foreach (ArticulationBody wheel in allWheels)
        {
            ArticulationDrive d = wheel.xDrive;
            d.driveType = ArticulationDriveType.Velocity;
            d.forceLimit = tuning.stallTorque;
            d.damping = tuning.damping;
            d.stiffness = 0f;
            wheel.xDrive = d;

            // maxJointVelocity is in rad/s (drives speak degrees, joint limits speak radians).
            // Cap above the free-spin target so the drive can reach it — and so a coasting or
            // back-driven wheel isn't clamped, which would read as an invisible brake.
            wheel.maxJointVelocity = tuning.maxJointVelocity;

            // Drivetrain "imperfection": a real dt has losses, so a wheel never quite hits its
            // full commanded speed. jointFriction is Coulomb drag on the axle; angularDamping
            // bleeds a little top speed proportional to spin. Set here (not just in the rig tool)
            // so they apply uniformly to every robot at play, including ones rigged before these
            // knobs existed. Set both to 0 for the old frictionless behavior.
            wheel.jointFriction = wheelRollingResistance;
            wheel.angularDamping = wheelSpinDamping;
        }

        // FIRST: everything after it measures along the driving axis, and until this runs there is
        // no reason to believe the root's +Z is it.
        MeasureDriveAxes();
        MeasureLoadTransfer();
        ResolveTractionPair();
        RegisterTyres();
    }

    private void ResolveTractionPair()
    {
        tractionFlags = ResolveTractionPair(tractionPair, allWheels, leftWheelCount, DriveForward,
            out string refused);
        if (!string.IsNullOrEmpty(refused))
            Debug.LogWarning($"RobotMotorController on '{name}': {refused} Driving as all-omni.", this);
    }

    // Which of `wheels` are the traction pair, from where the wheels sit along the drive axis.
    // Front and Rear are the extremes of each rail; Middle is the median wheel of each rail and
    // only exists on a rail with an odd number of wheels — on a 4-wheel rail the two candidates
    // can sit half a millimetre apart, and a coin flip is not a wheel choice, so Middle is refused
    // there and `refused` says why. Public and static so the editor can show which links it will
    // pick without a live controller; the wheel list is left rail first, as allWheels is.
    public static bool[] ResolveTractionPair(TractionPair pair, IList<ArticulationBody> wheels,
        int leftWheelCount, Vector3 forward, out string refused)
    {
        refused = "";
        int n = wheels != null ? wheels.Count : 0;
        var flags = new bool[n];
        if (pair == TractionPair.None || n == 0) return flags;

        for (int side = 0; side < 2; side++)
        {
            int start = side == 0 ? 0 : leftWheelCount;
            int end = side == 0 ? Mathf.Min(leftWheelCount, n) : n;
            var rail = new List<int>();
            for (int i = start; i < end; i++) if (wheels[i] != null) rail.Add(i);
            if (rail.Count == 0) continue;
            rail.Sort((a, b) => Vector3.Dot(wheels[a].transform.position, forward)
                .CompareTo(Vector3.Dot(wheels[b].transform.position, forward)));

            int pick;
            if (pair == TractionPair.Front) pick = rail[rail.Count - 1];
            else if (pair == TractionPair.Rear) pick = rail[0];
            else
            {
                if (rail.Count % 2 == 0)
                {
                    refused = $"the {(side == 0 ? "left" : "right")} rail has {rail.Count} wheels, so " +
                              "it has no middle wheel — a traction pair on this drive is Front or Rear.";
                    return new bool[n];
                }
                pick = rail[rail.Count / 2];
            }
            flags[pick] = true;
        }
        return flags;
    }

    // --- The tyre ---------------------------------------------------------------------------------

    // Hand every drive wheel to WheelTyreModel: its axle (the joint's twist axis, the same vector
    // MeasureDriveAxes votes with), whether it is a traction wheel, and the robot's weight for the
    // external-shove scale. Unregistered in OnDisable, and by the validators' bare-floor spawn.
    private void RegisterTyres()
    {
        if (rootBody == null || allWheels.Length == 0) return;
        var axles = new Vector3[allWheels.Length];
        for (int i = 0; i < allWheels.Length; i++)
        {
            ArticulationBody wheel = allWheels[i];
            axles[i] = wheel != null
                ? wheel.transform.rotation * wheel.anchorRotation * Vector3.right : Vector3.right;
        }
        float weight = DrivetrainTuning.MeasureTotalMass(rootBody) * Mathf.Abs(Physics.gravity.y);
        WheelTyreModel.Register(this, rootBody, allWheels, axles, tractionFlags, weight);
    }

    // --- Which way is forward ---------------------------------------------------------------------

    // ROOT +Z IS NOT THE DRIVING AXIS, and assuming it was is the most expensive bug in this file.
    //
    // The convention in the header holds on 654V_v1 and 360RpmDrivetrain — both measured driving at
    // 13.8 and 16.6 u/s exactly along transform.forward. It is FALSE on 654V_v2 and 654V_v3, which
    // travel 9.3 u/s PERPENDICULAR to their own transform.forward. Robots arrive from player CAD, so
    // the root's authored orientation is wherever the exporter left it, and nothing has ever forced
    // it to agree with the drivetrain.
    //
    // WHAT THAT BROKE, on the two robots that carry a cascade, while it was assumed:
    //   - the roll-relief torque of the time rolled about "forward". On those two that is the PITCH
    //     axis, so it was cancelling exactly the front-to-back tipping it was documented to leave
    //     alone — "the front and back isn't really that tippy", in one line of code.
    //   - StepLoadTransfer reads longitudinal acceleration as dot(velocity, forward), so it measured
    //     ~0 however hard they braked and the body never leaned.
    //   - the half-track was taken along right, i.e. along the wheelbase.
    //
    // MEASURED FROM THE WHEELS, because they are the only thing that knows. The rig aligns every
    // wheel link's local +X with robot RIGHT (see the header's sign convention), so the mean wheel
    // axle IS the right axis, and forward is right x up. That works on any imported robot, which no
    // convention and no geometry heuristic does — RobotBalanceWindow picked the axis by "the
    // wheelbase is the longer spread" and got it backwards on all four, because every one of these
    // robots is wider than it is long.
    //
    // THE SIGN COMES FROM THE AXLE TOO, and this is where the first version of this function still
    // got it wrong. Having measured the line off the wheels it then turned it to face the root's own
    // +X — the very assumption the rest of this comment exists to reject — and v2 and v3 came back
    // with a forward pointing exactly backwards down a correct line. The sign is not a convention to
    // be recovered from the root; it is a fact about the drivetrain, and the drivetrain states it:
    // a positive drive spins the wheel about +axle by the right-hand rule, which drags the contact
    // patch backwards and sends the robot along axle x up. Measured, on all four: dot(travel,
    // axle x up) = +1.00, v2 and v3 included.
    //
    // Everything reading these axes today is sign-symmetric — the relief's angle, rate and torque
    // all flip together, and the lean flips its sign, its pivot and its axis together — so fixing
    // the sign moves nothing on screen. That is precisely why it had to be fixed from the outside by
    // a test that drives the robot: the line was wrong loudly and the sign was wrong silently, and
    // the next thing to read DriveForwardWorld would have been the one to find out.
    //
    // The result is orthonormalised against world up so a wheel mounted a degree out of true does
    // not tilt the roll axis into the ground.
    private void MeasureDriveAxes()
    {
        driveRightLocal = Vector3.right;
        driveForwardLocal = Vector3.forward;
        if (rootBody == null) return;
        if (!MeasureDriveAxesWorld(allWheels, leftWheelCount, invertLeft, invertRight,
                out Vector3 right, out Vector3 forward)) return;

        // Measured in WORLD space and stored in the root's frame. World is where the flattening has
        // to happen — the robot is upright on the floor when this runs, so world up is the normal
        // the axles should be square to — and the root's frame is where the answer has to live, so
        // it rides along once the robot turns.
        Transform t = rootBody.transform;
        driveRightLocal = t.InverseTransformDirection(right);
        driveForwardLocal = t.InverseTransformDirection(forward);
    }

    // The same measurement with no controller behind it: the editor's traction-pair picker needs
    // it on a prefab that has never been Initialised. World-space right and forward, or false when
    // the wheels cannot say (no wheels, an even split of axle signs, axles pointing up) — in which
    // case the caller keeps the convention. `wheels` is left rail first, as allWheels is.
    public static bool MeasureDriveAxesWorld(IList<ArticulationBody> wheels, int leftWheelCount,
        bool invertLeft, bool invertRight, out Vector3 right, out Vector3 forward)
    {
        right = Vector3.right;
        forward = Vector3.forward;
        if (wheels == null || wheels.Count == 0) return false;

        Vector3 sum = Vector3.zero;
        for (int i = 0; i < wheels.Count; i++)
        {
            ArticulationBody wheel = wheels[i];
            if (wheel == null) continue;

            // The JOINT's twist axis, not the mesh's local +X: on a URDF import the axle can live
            // entirely in anchorRotation with the transform left as the exporter wrote it. The two
            // agree on all four shipped robots, so this costs nothing and covers the case that isn't.
            Vector3 axle = wheel.transform.rotation * wheel.anchorRotation * Vector3.right;

            // An inverted side spins the other way for the same stick, so the robot travels the
            // other way, so forward IS the other way. The list is filled left side first.
            bool inverted = i < leftWheelCount ? invertLeft : invertRight;
            sum += inverted ? -axle : axle;
        }

        // Summed raw and never folded onto a reference wheel. A mirrored wheel then costs one vote
        // instead of dragging every other wheel onto its own sign, which is what folding-onto-the-
        // first does when the first one is the mirrored one. An even split leaves nothing to decide
        // with — that robot cannot drive straight either — so this keeps the convention and says
        // nothing rather than picking a side.
        if (sum.sqrMagnitude < 1e-6f) return false;

        // The axles carry whatever camber and mounting error the CAD has, and a roll axis with a
        // vertical component would lever the robot into the floor.
        Vector3 flat = Vector3.ProjectOnPlane(sum.normalized, Vector3.up);
        if (flat.sqrMagnitude < 1e-6f) return false;   // axles vertical: nothing sane to derive

        right = flat.normalized;
        forward = Vector3.Cross(right, Vector3.up).normalized;
        return true;
    }

    // --- Load transfer --------------------------------------------------------------------------

    // Accelerate a car and the weight moves to the back; brake and it moves to the front. That
    // already happens here, for free and correctly: drive torque reaches the floor at the contact
    // patch and the mass sits a couple of hundred mm above it, so PhysX shifts normal force between
    // the axles every step. What is missing is being able to SEE it.
    //
    // A real chassis shows weight transfer as a couple of degrees of squat and dive, and all of that
    // comes from compliance — tyre sidewalls, suspension, frame flex. This robot has none: rigid
    // sphere wheels and one rigid link. So the load genuinely moves and nothing rotates, right up
    // until it rotates ALL THE WAY. Flat, flat, flat, over.
    //
    // WHY THIS IS NOT A TORQUE. It was the obvious first idea and it cannot work. A pitch torque on
    // a rigid robot does nothing at all while the rear wheels still carry load, and the instant it
    // exceeds m*g*halfWheelbase it lifts them off the floor. There is no soft regime in between to
    // put two degrees of dive into — the same rigidity that removed the cue removes every physical
    // way of adding it back. So this is a render pose and nothing else: no force, no torque, no
    // collider moved, and it is applied AFTER every FixedUpdate has been and gone.
    //
    // WHY IT LEANS ABOUT A CONTACT LINE rather than the middle. Dive on a car drops the nose because
    // the front springs compress. Nothing here compresses, so pivoting about the centre would drive
    // the front wheels through the floor. Leaning about the loaded end's contact line instead —
    // front under braking, rear under power — lifts the light end and leaves the heavy one planted,
    // which is both what a rigid robot on the edge of tipping actually does and the only version
    // that cannot clip.
    //
    // It does not touch tipping. TipOverValidation and every threshold in RobotBalanceWindow are
    // measured off the physics pose, which this never writes to.
    private void MeasureLoadTransfer()
    {
        frontPivotOffset = rearPivotOffset = Vector3.zero;
        fullLeanAccel = 0f;
        leanDeg = leanRateDegPerSec = 0f;
        leanPosed = false;
        if (rootBody == null || allWheels.Length == 0) return;

        Transform t = rootBody.transform;
        float radius = DrivetrainTuning.MeasureWheelRadius(allWheels);

        // The two ends of the wheelbase and the floor under them, as WORLD-unit offsets along the
        // robot's own axes — not InverseTransformPoint, which would divide by the root's lossy
        // scale and put both pivots a tenth of the way to where they belong on this 10x project.
        // Held in the robot's frame rather than in world space so they ride along with it.
        float front = float.NegativeInfinity, rear = float.PositiveInfinity;
        float lowest = float.PositiveInfinity;
        foreach (ArticulationBody wheel in allWheels)
        {
            if (wheel == null) continue;
            Vector3 offset = wheel.transform.position - t.position;
            float along = Vector3.Dot(offset, DriveForward);
            front = Mathf.Max(front, along);
            rear = Mathf.Min(rear, along);
            lowest = Mathf.Min(lowest, Vector3.Dot(offset, t.up));
        }
        if (float.IsInfinity(front)) return;

        // Built from the MEASURED forward, not from local +Z, and in world units so that rotating it
        // by the root's rotation lands it where the wheels actually are.
        float contact = lowest - radius;
        frontPivotOffset = driveForwardLocal * front + Vector3.up * contact;
        rearPivotOffset = driveForwardLocal * rear + Vector3.up * contact;

        // Full lean at the friction cone, which is the hardest this robot can ever accelerate or
        // stop. Sizing it off the tyres rather than a chosen number means a grippier robot leans
        // further before it saturates, exactly as it should.
        fullLeanAccel = Mathf.Max(tuning.tractionG * Mathf.Abs(Physics.gravity.y), 1e-3f);
    }

    // Where the lean is heading, and how it gets there. Runs on the physics step so the response is
    // identical at 30 fps and 120: nothing about weight transfer should depend on the frame rate.
    private void StepLoadTransfer(float dt)
    {
        if (rootBody == null || dt <= 0f) return;

        // Measured off the body, not off the throttle. A robot shoved by another robot, dragged to a
        // stop by a wall or spinning its wheels on a slick patch is having its weight moved around
        // just as much as one under power, and only the velocity knows about any of that.
        float forwardSpeed = Vector3.Dot(rootBody.linearVelocity, DriveForward);
        float accel = (forwardSpeed - lastForwardSpeed) / dt;
        lastForwardSpeed = forwardSpeed;

        if (loadTransferPitchDeg <= 0f || fullLeanAccel <= 0f)
        {
            leanDeg = leanRateDegPerSec = 0f;
            return;
        }

        // Accelerating forward loads the rear and lifts the nose, so nose-up is the positive sign
        // and the target follows the acceleration directly.
        float target = loadTransferPitchDeg * Mathf.Clamp(accel / fullLeanAccel, -1f, 1f);
        LeanStep(ref leanDeg, ref leanRateDegPerSec, target,
            loadTransferFrequency, loadTransferDamping, loadTransferPitchDeg, dt);
    }

    // One step of the second-order lag that turns "the robot is decelerating" into "the body is
    // still settling from the stop it just made". Pure and static so ChassisLeanValidation can
    // exercise it with no robot, no scene and no physics step.
    //
    // Semi-implicit: the rate is integrated first and the angle uses the NEW rate, which is what
    // keeps a 12 rad/s spring stable on a 100 Hz step instead of slowly gaining energy.
    public static void LeanStep(ref float deg, ref float degPerSec, float targetDeg,
        float frequency, float damping, float maxDeg, float dt)
    {
        float omega = Mathf.Max(frequency, 0f);
        float zeta = Mathf.Max(damping, 0f);
        degPerSec += (-omega * omega * (deg - targetDeg) - 2f * zeta * omega * degPerSec) * dt;
        deg += degPerSec * dt;

        // The overshoot is the point, so the clamp sits above the steady-state travel rather than on
        // it. It exists to bound a spring that has been handed a silly frequency, not to shape the
        // response — at any sane setting the lean never reaches it.
        float bound = Mathf.Abs(maxDeg) * MaxLeanOvershoot;
        if (deg > bound) { deg = bound; degPerSec = Mathf.Min(degPerSec, 0f); }
        else if (deg < -bound) { deg = -bound; degPerSec = Mathf.Max(degPerSec, 0f); }
    }

    // How far past the steady-state lean the overshoot is allowed to go.
    public const float MaxLeanOvershoot = 1.6f;

    // The rendered pose: the physics pose, leaned about whichever contact line is carrying the load.
    // Static and pure for the same reason LeanStep is — the property that matters (the pivot does
    // not move, so nothing is ever pushed through the floor) is checkable without a robot.
    public static void LeanedPose(Vector3 position, Quaternion rotation, float leanDeg,
        Vector3 frontPivotOffset, Vector3 rearPivotOffset, Vector3 rightLocal,
        out Vector3 leanedPosition, out Quaternion leanedRotation)
    {
        // Nose-up leans about the REAR contact line, nose-down about the FRONT one: always the end
        // the weight has moved onto, so the other end is what rises.
        Vector3 pivot = position + rotation * (leanDeg >= 0f ? rearPivotOffset : frontPivotOffset);

        // About the robot's MEASURED right axis, passed in rather than taken as local +X for the
        // same reason the pivots are: on 654V_v2 and 654V_v3, local +X is the driving direction, so
        // leaning about it would ROLL the robot rather than pitch it. Negative is nose-up, because
        // the right-hand rule about right takes forward to down.
        Quaternion lean = Quaternion.AngleAxis(-leanDeg, rotation * rightLocal);
        leanedPosition = lean * (position - pivot) + pivot;
        leanedRotation = lean * rotation;
    }

    // Applied after every LateUpdate and immediately before the frame is drawn, which is the whole
    // trick: the chase camera reads the robot in its LateUpdate and therefore always sees the true
    // physics pose. Lean the robot before the camera has looked at it and the camera follows the
    // lean, cancelling most of it and wobbling the aim for the rest.
    // Put the physics pose back BEFORE the step, not just before the next frame.
    //
    // PoseForRender leans the root transform for the camera and undoes it on the next render. That
    // was enough while every link below the chassis was revolute: a wheel's joint coordinate is an
    // ANGLE about its own axle, and shifting the chassis a couple of millimetres does not write to
    // it. The droop links (WheelDroopRig) are prismatic and their coordinate IS a vertical offset —
    // exactly what a lean about a pivot ahead of or behind the wheels produces — so a FixedUpdate
    // landing between two renders reads the leaned pose and the cosmetic lean becomes real physics.
    // Measured by ChassisLeanValidation: 2.7 mm on 654V_v1 and 2.1 on v3, against a 0.8 mm floor for
    // re-running the identical simulation, and gone the moment the droop joints are locked.
    private void UnposeBeforePhysics()
    {
        if (!leanPosed || rootBody == null) return;
        Transform t = rootBody.transform;
        if (t.position != posedPosition || t.rotation != posedRotation) return;  // physics moved on
        t.SetPositionAndRotation(physicsPosition, physicsRotation);
        leanPosed = false;
    }

    private void PoseForRender()
    {
        if (rootBody == null || !isActiveAndEnabled) return;
        Transform t = rootBody.transform;

        // PhysX writes this transform once per physics step, and a frame can contain no steps at
        // all above 100 fps. So: if it still reads exactly as we left it, nothing has overwritten
        // our lean and the pose we cached is the physics one. If it has changed, physics has been
        // through since and the transform IS the physics pose.
        if (leanPosed && t.position == posedPosition && t.rotation == posedRotation)
            t.SetPositionAndRotation(physicsPosition, physicsRotation);

        physicsPosition = t.position;
        physicsRotation = t.rotation;
        leanPosed = false;
        if (Mathf.Abs(leanDeg) < 0.001f) return;

        LeanedPose(physicsPosition, physicsRotation, leanDeg, frontPivotOffset, rearPivotOffset,
            driveRightLocal, out Vector3 leanedPosition, out Quaternion leanedRotation);
        t.SetPositionAndRotation(leanedPosition, leanedRotation);

        posedPosition = t.position;
        posedRotation = t.rotation;
        leanPosed = true;
    }

    void OnEnable()
    {
        if (leftJoystickAction != null) leftJoystickAction.action.Enable();
        else Debug.LogWarning("RobotMotorController: 'Left Joystick Action' is not assigned in the Inspector.", this);

        if (rightJoystickAction != null) rightJoystickAction.action.Enable();
        else Debug.LogWarning("RobotMotorController: 'Right Joystick Action' is not assigned in the Inspector.", this);

        Application.onBeforeRender += PoseForRender;
    }

    void OnDisable()
    {
        if (leftJoystickAction != null) leftJoystickAction.action.Disable();
        if (rightJoystickAction != null) rightJoystickAction.action.Disable();

        Application.onBeforeRender -= PoseForRender;
        WheelTyreModel.Unregister(this);

        // Hand the transform back exactly as physics left it. A robot despawned or disabled mid-lean
        // would otherwise keep the last frame's couple of degrees forever, and PhysX — which never
        // reads this transform — would never correct it.
        if (leanPosed && rootBody != null)
        {
            Transform t = rootBody.transform;
            if (t.position == posedPosition && t.rotation == posedRotation)
                t.SetPositionAndRotation(physicsPosition, physicsRotation);
            leanPosed = false;
        }
    }

    void FixedUpdate()
    {
        // Read where it is consumed. Input System events are still processed once per rendered
        // frame by default, so this returns the same value an Update read would — the gain is that
        // the slew integrator below sees the stick at the instant it integrates it, rather than a
        // value latched an unknown fraction of a frame ago.
        if (leftJoystickAction != null) leftStickInput = leftJoystickAction.action.ReadValue<Vector2>();
        if (rightJoystickAction != null) rightStickInput = rightJoystickAction.action.ReadValue<Vector2>();

        ApplyStep(Time.fixedDeltaTime);
    }

    // One drivetrain step: targets, slew, mix, drive, brake. Split out of FixedUpdate so an
    // edit-mode harness can step the REAL control path — SetManualInput, then ApplyStep(dt) before
    // each Physics.Simulate(dt) — instead of writing wheel drives directly and missing the slew,
    // MixArcade's scaling and the authority rule entirely.
    //
    // Reading the sticks stays in FixedUpdate: there is no input device behind a validator, and
    // manualInput is the path a scripted routine is supposed to take anyway.
    public void ApplyStep(float dt)
    {
        // Before anything reads the transform: put back the pose PhysX last wrote. PoseForRender
        // leans the root for the camera and undoes it on the next rendered frame, which leaves the
        // leaned pose in place for any step that lands in between. Harmless while every link below
        // the chassis was revolute — a joint ANGLE about an axle does not care that the chassis
        // moved 2 mm — and not harmless at all once the droop links made that offset a prismatic
        // joint COORDINATE. In FixedUpdate this only fixed the play path; edit-mode harnesses call
        // ApplyStep directly and never run FixedUpdate, which is why 654V_v3 still leaked 2.2 mm.
        UnposeBeforePhysics();

        // Arcade Drive (Left Stick controls Forward/Backward, Right Stick controls Turning).
        float throttleTarget;
        float turnTarget;
        if (manualInput)
        {
            // Autonomy/test input is already a command, not a stick, so it skips the deadzone,
            // expo and the player's sensitivity prefs — a scripted routine must not drive
            // differently on a device where someone dropped Turn Sensitivity to 30%. It still
            // goes through the slew and the brake, because that IS the drivetrain.
            throttleTarget = Mathf.Clamp(manualThrottle, -1f, 1f);
            turnTarget = Mathf.Clamp(manualTurn, -1f, 1f);
        }
        else
        {
            throttleTarget = Shape(leftStickInput.y, inputDeadzone, throttleExpo) * driveSensitivity;
            // Clamped because Turn Sensitivity may reach 1.5: an unclamped 1.5 target would skew
            // the slew's notion of "how far from done" and overdrive the mix's turn budget.
            turnTarget = Mathf.Clamp(
                Shape(rightStickInput.x, inputDeadzone, turnExpo) * turnSensitivity, -1f, 1f);
        }

        // "Reverse Drive Direction" (Settings): flip which end is "front". That's a 180° rotation of
        // the control frame, so BOTH the forward axis and the steering axis invert — negating throttle
        // alone would mirror-image the steering when driving from the new front. Read live from
        // PlayerPrefs so no spawner/instance wiring is needed. Applied to the TARGET, before the
        // slew, so flipping it mid-drive ramps across instead of snapping.
        if (ReverseDriveSettings.Reversed) { throttleTarget = -throttleTarget; turnTarget = -turnTarget; }

        // There is deliberately NO special neutral path: centred sticks are just targets of zero.
        // The slew decays the commands, the wheels are commanded 0 deg/s, the authority rule limits
        // the pull-up to brakeTorque while they spin (the stick throw is zero), and below the moving
        // gate they park under Drive authority at target 0. Centre stick IS the brake pedal — no
        // dwell, no release, nothing for a stick swept through centre to accidentally trigger.
        throttleCommand = Slew(throttleCommand, throttleTarget,
            throttleRisePerSec, throttleFallPerSec, dt);
        turnCommand = Slew(turnCommand, turnTarget, turnRisePerSec, turnFallPerSec, dt);

        // The turn rate blends with the throttle it is mixed against.
        MixArcade(throttleCommand, turnCommand * TurnRateFor(throttleCommand, pivotTurnRate, turnRate),
            out float left, out float right);
        LeftCommand = left;
        RightCommand = right;

        // Revolute drive target velocities are in DEGREES per second: rpm x 360/60 = rpm x 6.
        float fullStickDegPerSec = maxWheelRpm * 6f;
        float leftDegPerSec = left * fullStickDegPerSec * (invertLeft ? -1f : 1f);
        float rightDegPerSec = right * fullStickDegPerSec * (invertRight ? -1f : 1f);
        ApplySide(leftWheels, leftDegPerSec);
        ApplySide(rightWheels, rightDegPerSec);

        // How far the driver has thrown EITHER stick, from the shaped targets rather than the slewed
        // commands: it is what a back-driven wheel's authority scales with, and it has to follow the
        // hand with no ramp at all. A 0.8 g stop is over in 0.18 s while a full reversal takes
        // 0.375 s to get through the slew, so a limit keyed on the slewed command would arrive after
        // the robot had already stopped. Shape() returns exactly 0 inside the deadzone and manual
        // input is clamped, so "centred" is exactly 0 on both paths, whatever Reverse Drive or the
        // sensitivity sliders did to the sign and the scale.
        float stickThrow = Mathf.Max(Mathf.Abs(throttleTarget), Mathf.Abs(turnTarget));

        // What the world pushed this robot sideways with last step, handed to its tyres for this one.
        WheelTyreModel.PublishExternalLateral(this, DriveRight, dt);

        UpdateBrakingQuadrant(leftDegPerSec, rightDegPerSec, stickThrow, fullStickDegPerSec, dt);

        // Reads the robot's motion and applies nothing. Stepped here rather than per frame so the
        // lean is identical at every frame rate, and harmless in an edit-mode harness: nothing is
        // posed until PoseForRender runs, and that only runs while rendering.
        StepLoadTransfer(dt);
    }

    // --- Braking quadrant ------------------------------------------------------------------------

    // Which wheel gets how much force this step. The slewed side command decides WHICH quadrant a
    // wheel is in (accelerated or back-driven), the raw stick throw decides HOW HARD a back-driven
    // one may pull, and the moving gate decides when a slowing wheel hands over to the parking
    // hold. DriveForceLimit is the rule; ParkGateDegPerSec is the gate.
    //
    // With centre stick as the brake pedal, the back-driven quadrant IS the brake: released sticks
    // decay the command to zero, every spinning wheel is back-driven with the stick throw at zero,
    // and the robot pulls up under brakeTorque. Below the moving gate a wheel returns to Drive
    // authority with target 0 — the parking hold — so a parked robot resists a shove with the motor
    // curve instead of chattering on the brake.
    //
    // THE GATE IS DERIVED, AND THE DERIVATION IS THE WHOLE POINT (2026-08-19). It used to be a flat
    // 0.15 of maxJointVelocity, which is free speed x 1.25 — so a wheel was released from the brake
    // at 18.75% of free speed. Connor's report: "for some of it it does [feel like omnis], but the
    // end part it just comes to a sudden stop", on a 360 RPM robot.
    //
    // WHAT ACTUALLY HAPPENS BELOW THE GATE, and it is not what the damping suggests. It is tempting
    // to reason that the parking hold applies damping * speedError and is therefore gentle at low
    // speed. Measured, it is not: a PhysX articulation velocity drive is solved IMPLICITLY, so it is
    // effectively a velocity CONSTRAINT bounded by forceLimit, not a spring pulling at
    // damping * error. Hand it stallTorque and it deletes whatever speed is left in a SINGLE step.
    // Traced on the 360 RPM robot with a released stick, one 10 ms step either side of the old gate:
    //     0.959 -> 0.771 u/s   0.19 g   <- coast, rock steady for the whole stop
    //     0.771 -> 0.023 u/s   0.76 g   <- the parking hold, at the traction limit, in one step
    // The 0.76 g is not a tune, it is 0.77 u/s divided by one physics step. The wheels lock and the
    // robot skids to a halt. That is the sudden stop, and the force LIMIT is the only lever on it —
    // damping never enters into it.
    //
    // So the gate is the speed at which the swap stops being observable: the speed the BRAKE ITSELF
    // removes in one physics step, brakeG * g * dt. At or below it the wheel is stopping this step
    // whichever authority holds it, so handing it stallTorque changes nothing a driver could feel,
    // and the parking hold gets its full authority to resist a shove from rest. Above it the brake
    // keeps the wheel and the deceleration stays at brakeG, by construction, all the way down.
    //
    // Which makes the whole stop flat at brakeG with no step anywhere in it — the last one included,
    // because the last step is exactly one brake-step of speed by definition of the gate.
    //
    // Derived rather than pinned as a fraction because it has to track all four things it is made
    // of: retune the brake fraction, re-gear the robot, change gravity or move off 100 Hz and a
    // hard-coded fraction silently re-opens the cliff. dt is the step being applied, not
    // Time.fixedDeltaTime, so an edit-mode harness stepping at its own rate gets the same answer.
    public static float ParkGateDegPerSec(float fullStickDegPerSec, float brakeG, float topSpeed,
        float gravity, float dt)
    {
        if (topSpeed <= 1e-6f) return 0f;
        return Mathf.Max(fullStickDegPerSec, 0f)
             * Mathf.Clamp01(ParkGateSpeed(brakeG, gravity, dt) / topSpeed);
    }

    // The same gate in the units the CHASSIS is measured in: the speed the brake removes in one
    // physics step. ParkGateDegPerSec is this number scaled into wheel deg/s, so a wheel and its
    // robot cross their gates together by construction rather than by two tunings agreeing.
    public static float ParkGateSpeed(float brakeG, float gravity, float dt)
        => Mathf.Max(brakeG, 0f) * Mathf.Abs(gravity) * Mathf.Max(dt, 0f);

    private void UpdateBrakingQuadrant(float leftDegPerSec, float rightDegPerSec, float stickThrow,
        float fullStickDegPerSec, float dt)
    {
        if (allWheels.Length == 0) return;
        float movingDegPerSec = ParkGateDegPerSec(fullStickDegPerSec, tuning.brakeG,
            tuning.topSpeed, Physics.gravity.y, dt);
        bool chassisParked = rootBody == null
            || Planar(rootBody.linearVelocity).magnitude <= ParkGateSpeed(tuning.brakeG, Physics.gravity.y, dt);

        for (int i = 0; i < allWheels.Length; i++)
        {
            ArticulationBody wheel = allWheels[i];
            if (wheel == null) continue;

            // allWheels is filled left-side first by Awake, so leftWheelCount is the boundary.
            // Taking the command from here rather than reading xDrive.targetVelocity back keeps
            // this honest about what was just asked for, with no marshalling round-trip per wheel.
            float commandDegPerSec = i < leftWheelCount ? leftDegPerSec : rightDegPerSec;

            // jointVelocity is in rad/s (joint state speaks radians); the drive target is deg/s.
            float spinDegPerSec = wheel.jointVelocity.dofCount > 0
                ? wheel.jointVelocity[0] * Mathf.Rad2Deg : 0f;

            SetForceLimit(i, DriveForceLimit(commandDegPerSec, spinDegPerSec, movingDegPerSec,
                stickThrow, tuning.brakeTorque, tuning.stallTorque, chassisParked));
        }
    }

    // SHARING A RAIL'S TORQUE BY THE LOAD EACH WHEEL CARRIES — built, measured, removed. Do not
    // rebuild it without reading this.
    //
    // The reasoning was sound and the asymmetry it aimed at is real: brakeTorque is mu*m*g*f*r/N, one
    // N-th of what it takes to stop the ROBOT, and a rigid chassis on a rail of three unevenly spaced
    // wheels rests on two of them — 654V_v3's right rail measures 220% / 56% / 1% of the even share
    // and repeats to the percent every run. So a wheel carrying almost nothing was being handed a
    // robot-sized brake. WheelTyreModel.ConsumeNormalImpulse exists for this and nothing else.
    //
    // It cost far more than it bought. A contact impulse is a per-step quantity and it is violently
    // noisy — the same wheel reads 0, then 699, then 244 on consecutive samples of a turn — so using
    // it as a torque multiplier is a force limit dithering at 100 Hz. 654V_v1's moving turn went
    // 455 -> 155 degrees, its "spinning, then throttle" case 453 -> 9, and mean slip 0.45 -> 1.85 u/s
    // with 14 wheel direction changes where there had been none. Filtering the load onto a 0.2 s time
    // constant recovered most of the yaw (317 / 421) but still left slip at 3.66 against 0.45: better,
    // never as good, and still a robot sliding where it used to grip.
    //
    // And the fault it was built for turned out to be the parking hold instead. Once DriveForceLimit
    // asked whether the ROBOT was parked rather than one wheel, every symptom went: 654V_v3 on the
    // field, released out of a full turn, went from its right rail crossing the park gate at +0.00 s
    // with four wheels LOCKED, to all six crossing together at +0.51 s with none locked. With the
    // share removed on top of that, MovingTurnValidation is bit-for-bit the pre-change baseline.
    // A wheel carrying no load is not, in the end, a wheel that needs less brake — it is a wheel
    // whose contact cannot transmit the brake it is given, which the contact already handles.

    // --- Force limits ---------------------------------------------------------------------------
    // Same struct-swap shape as MotorActuator.EnterHold/ExitHold, including the per-wheel tracker
    // that keeps it from rewriting six drives on every one of the 100 physics steps a second.
    //
    // The brake keeps the velocity drive (at its target) rather than switching the drive off, so
    // the stop is progressive — clamped to brakeTorque — and hands back to full authority at
    // target 0 below the moving gate, which is the parking hold.

    private void SetForceLimit(int index, float forceLimit)
    {
        // Quantise onto the gate's own grid before comparing. The gate exists so six xDrive structs
        // are not marshalled on every one of the 100 physics steps a second; with a per-wheel load
        // share the raw value now jitters with contact noise on every step, so without a grid it
        // would write constantly, and with the old bare deadband a slow drift could crawl past it a
        // hair at a time and never write at all. Rounding both sides fixes both: noise inside a
        // bucket is free, a real change crosses one.
        forceLimit = Mathf.Round(forceLimit / forceLimitEpsilon) * forceLimitEpsilon;
        if (Mathf.Abs(wheelForceLimit[index] - forceLimit) < forceLimitEpsilon * 0.5f) return;
        ArticulationBody wheel = allWheels[index];
        if (wheel == null) return;

        ArticulationDrive d = wheel.xDrive;
        d.forceLimit = forceLimit;
        wheel.xDrive = d;
        wheelForceLimit[index] = forceLimit;
    }

    // --- Input shaping -------------------------------------------------------------------------
    // Pure and static so DriveFeelValidation can exercise them headlessly, with no robot, no
    // scene and no physics step. Public rather than internal because the validator lives in the
    // Editor assembly, which internal wouldn't reach.

    // Deadzone with RESCALING, then an odd-symmetric expo curve. Without the rescale an 0.08
    // deadzone would quietly cap the stick at 0.92; with it, full stick still maps to exactly 1.
    public static float Shape(float value, float deadzone, float expo)
    {
        float magnitude = Mathf.Abs(value);
        float dz = Mathf.Clamp(deadzone, 0f, 0.95f);
        if (magnitude <= dz) return 0f;

        magnitude = (magnitude - dz) / (1f - dz);
        magnitude = Mathf.Lerp(magnitude, magnitude * magnitude * magnitude, Mathf.Clamp01(expo));
        return Mathf.Sign(value) * Mathf.Clamp01(magnitude);
    }

    // Asymmetric rate limit: `rise` while the command grows away from zero, `fall` while it
    // shrinks back toward it.
    //
    // The crossing-zero case is the one a naive MoveTowards gets wrong. A reversal has to spend
    // part of the step falling and part rising, so the leftover DISTANCE is converted back into
    // time through the fall rate and out again through the rise rate. That is what makes the
    // result identical whether it's stepped at 100 Hz or 10 Hz, which in turn is what makes it
    // testable — and a full reversal takes exactly 1/fall + 1/rise seconds.
    public static float Slew(float current, float target, float rise, float fall, float dt)
    {
        if (current == target) return target;

        float riseRate = Mathf.Max(rise, 0f);
        float fallRate = Mathf.Max(fall, 0f);
        float step = Mathf.Max(dt, 0f);

        if (current == 0f || current * target > 0f)
        {
            bool growing = Mathf.Abs(target) > Mathf.Abs(current);
            return Mathf.MoveTowards(current, target, (growing ? riseRate : fallRate) * step);
        }

        // Opposite signs, or heading to exactly zero: fall to zero first.
        float toZero = Mathf.Abs(current);
        float fallStep = fallRate * step;
        if (fallStep < toZero) return Mathf.MoveTowards(current, 0f, fallStep);
        if (fallRate <= 0f) return 0f; // can't reach zero at all; don't divide by it either
        float leftoverSeconds = (fallStep - toZero) / fallRate;
        return Mathf.MoveTowards(0f, target, leftoverSeconds * riseRate);
    }

    // How much of full wheel speed the turn stick is worth at this throttle: pivotRate standing
    // still, movingRate at full throttle, linear between.
    //
    // WHY IT BLENDS. Connor, 2026-08-30: "while turning while going forward and backwards is pretty
    // similar [to real life], but if the bot is still and just a rotary motion it should be much
    // faster, cuz the drive train is using both sides to do the turning." One turn rate served both
    // cases and got the pivot wrong by half, because a pivot drives both sides against each other and
    // a turn at speed does not. The rates have moved since (0.65 / 0.35, see DefaultPivotTurnRate) —
    // the blend is the part that answers Connor's report, not the particular pair of numbers.
    //
    // THE IDENTITY THAT USED TO BE HERE IS GONE ON PURPOSE (2026-09-06). At 1.0 / 0.5 against the
    // turn-priority mix, a full turn stick reproduced a plain clamped arcade drive at every throttle,
    // and that was written up as the argument that a pivot and a full-throttle turn were one drive
    // rather than two settings. It was true, and it was also the same sentence as "at full throttle
    // the inner side is commanded to a dead stop" — the clamp and the dead stop are one fact seen
    // twice. MixArcade is proportional now and the rates are 0.65 / 0.35, so the identity no longer
    // holds and nothing pins it. What is still true, and is what the identity was really standing in
    // for, is that ONE blend covers the whole throttle range: there is no mode switch between
    // pivoting and turning at speed, just a lerp.
    //
    // |throttle| is clamped, not trusted: a target past 1 must land on the moving rate, never beyond.
    public static float TurnRateFor(float throttle, float pivotRate, float movingRate)
        => Mathf.Lerp(pivotRate, movingRate, Mathf.Clamp01(Mathf.Abs(throttle)));

    // PROPORTIONAL arcade mix: build the two side commands, then scale BOTH down together if either
    // has overflowed. Throttle and turn keep their ratio; neither is ever spent to pay for the other.
    //
    // Inside the unit square nothing overflows and this is the identity, exactly as every mix before
    // it. What changes is the corner.
    //
    // WHAT THIS REPLACED, and why the replacement is not a step backwards to the mix before THAT.
    // There have been three:
    //   1. clamp(throttle ± turn) — the naive mix. At full throttle the outer wheel is already at
    //      free speed, the clamp eats the difference, and up to half the commanded differential
    //      silently disappears: "the forward momentum doesn't allow turning".
    //   2. TURN priority — the throttle gave way instead, so the differential survived at every
    //      throttle. It fixed (1) and introduced its own: at full throttle with a full turn stick the
    //      overflow ate the ENTIRE throttle and the inner side was commanded to a DEAD STOP. The
    //      authority rule then enforces that stop with stall torque, because a held stick is full
    //      authority by design — so three wheels on the inner rail are pinned at zero and skidding
    //      while the robot travels. Measured on 654V_v3 on the field, full throttle + full turn:
    //      outer rail 574 deg/s, inner rail -22 deg/s under 381-478 units of load. Connor, 2026-09-06:
    //      "the back right wheel still goes still faster than others ... the left side would all
    //      work", and "the rotation overpowers the forward and just turns". Both are this line.
    //   3. This. Scaling both sides keeps the inner wheel turning (0.48 rather than 0.00 at full
    //      throttle and a full turn stick, at turnRate 0.35), so the robot arcs on six rolling wheels
    //      instead of pivoting about a locked rail.
    //
    // The differential no longer survives the corner unscaled — that was (2)'s defining property and
    // it is deliberately given up, because it is the same sentence as "the inner side stops dead".
    // What survives instead is the RATIO: there is a single k in (0,1] with left + right == 2*k*throttle
    // and left - right == 2*k*turn. The driver's steering-to-forward balance is what is preserved, at
    // every stick position; only the overall scale gives way. DriveFeelValidation.MixPreservesTurn
    // pins exactly that.
    public static void MixArcade(float throttle, float turn, out float left, out float right)
    {
        turn = Mathf.Clamp(turn, -1f, 1f);
        left = throttle + turn;
        right = throttle - turn;
        float peak = Mathf.Max(Mathf.Abs(left), Mathf.Abs(right));
        if (peak <= 1f) return;
        left /= peak;
        right /= peak;
    }

    // THE AUTHORITY RULE — how much force a wheel's drive may use this step.
    //
    // Accelerating: stall torque; the motor curve does the rest. Back-driven — commanded slower than
    // it spins, or against it — and still moving: a limit that runs from the coast torque at centre
    // stick to stall torque at full stick, on how far the driver has thrown EITHER stick. That is
    // the one rule the three old cases were approximations of. A release is a coast: the brake
    // pedal, bit for bit what it was. The inner side of an arc, or a pivot entered from speed, is a
    // held stick, so its wheels get the authority to be slowed or reversed against the robot's
    // momentum. A slammed reversal is the same thing at the top of the ramp. Continuous, so there
    // is no threshold a stick can sit just under, and no exemption a slew can hold open after the
    // hand has let go.
    //
    // WHY THE TOP IS STALL AND NOT THE TYRE'S GRIP — tried, measured, wrong. Capping a back-driven
    // wheel at one share of grip reads well (a wheel held harder than the floor holds it locks and
    // snatches) and it does calm the inner side. But the OUTER side of a turn at speed is over-spun
    // by the ground the moment the robot yaws, so it is "back-driven" too, and at the same cap it
    // brakes exactly as hard as the inner side does: the two moments cancel and a full-stick turn
    // added at full speed produces NO yaw at all — 654V_v1, v2 and v3 all read 0 degrees at full
    // speed, while the same turn entered from rest still worked. At stall the outer side is not
    // capped where it matters and the moving turn is 329-455 degrees in 2 s. The snatch a locked
    // inner wheel makes is the tyre's business (static = dynamic friction, see WheelTyreModel).
    //
    // THE PARKING HOLD IS ABOUT THE ROBOT BEING PARKED, NOT ONE WHEEL (2026-09-06). Below the moving
    // gate a wheel is stopping this step whichever authority holds it, so handing it stall torque
    // changes nothing a driver can feel — that is the whole argument for the hold, and it is only
    // sound while the ROBOT is stopping too. Read per wheel it says something else: a wheel that has
    // reached zero on a chassis still travelling at speed gets pinned at full motor torque, which is
    // not a parked robot resisting a shove, it is a locked wheel skidding. Measured on 654V_v3 on the
    // field, a released full-throttle turn: wheels pinned below a tenth of their rail's spin with the
    // chassis still doing 2.7-6.4 u/s. So both have to be below the gate — the wheel in deg/s, the
    // robot in u/s, one derivation apart (ParkGateSpeed). A stopped wheel on a moving robot stays on
    // the brake ramp instead, which at centre stick is the coast torque: a hold, not a lock.
    public static float DriveForceLimit(float commandDegPerSec, float spinDegPerSec,
        float movingGateDegPerSec, float stickThrow, float brakeTorque, float stallTorque,
        bool chassisParked)
    {
        bool parked = chassisParked && Mathf.Abs(spinDegPerSec) <= movingGateDegPerSec;
        if (parked || !BackDriven(commandDegPerSec, spinDegPerSec)) return stallTorque;
        return Mathf.Lerp(brakeTorque, stallTorque, Mathf.Clamp01(stickThrow));
    }

    // The wheel is turning the motor rather than the other way round: commanded into reverse,
    // commanded to a dead stop, or commanded SLOWER than it is turning. One definition, in one
    // place — it is the whole of "which quadrant".
    public static bool BackDriven(float commandDegPerSec, float spinDegPerSec)
    {
        bool sameDirection = spinDegPerSec * commandDegPerSec > 0f;
        return !sameDirection || Mathf.Abs(commandDegPerSec) < Mathf.Abs(spinDegPerSec);
    }

    // Autonomy/test hook: drive without input devices (e.g. scripted routines, play-mode tests).
    public void SetManualInput(float throttle, float turn)
    {
        manualThrottle = throttle;
        manualTurn = turn;
        manualInput = true;
    }

    // Horizontal speed only: a robot settling onto its wheels after a spawn drop, or riding over a
    // tile seam, has vertical velocity that has nothing to do with whether it is travelling.
    private static Vector3 Planar(Vector3 v) => new Vector3(v.x, 0f, v.z);

    private static void ApplySide(ArticulationBody[] wheels, float degPerSec)
    {
        if (wheels == null) return;
        foreach (ArticulationBody wheel in wheels)
        {
            if (wheel != null) wheel.SetDriveTargetVelocity(ArticulationDriveAxis.X, degPerSec);
        }
    }

}
