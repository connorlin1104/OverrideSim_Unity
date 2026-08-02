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
// DRIVE FEEL. Four things shape it, and all four were missing until the drivetrain was retuned:
//
//   1. The motor curve. See DrivetrainTuning — with the old forceLimit 700 / damping 1000 the
//      drive was a bang-bang torque source for 99.95% of every acceleration, so half stick pulled
//      exactly as hard as full stick. Deriving damping from the free speed makes torque fall off
//      like a real motor's, so small inputs are genuinely gentler.
//   2. The command ramp (Slew). Keyboard W/A/S/D is exactly 0 or +-1, so without a rate limit a
//      100 ms tap of the turn key DEMANDS full authority and swings the robot ~12 degrees. Ramping
//      the command makes a short input a small input (~3 degrees) without making a deliberate turn
//      feel sluggish.
//   3. The brake, and HOW MUCH OF IT THE WHEELS CAN TAKE. Centre stick is the brake pedal, like a
//      car: released sticks command ZERO wheel speed under braking-quadrant torque, and once the
//      wheels are slow the robot parks (Drive authority at target 0). What changed since is the
//      strength. A single 0.56 g stop was applied to every robot, which is a traction-wheel
//      number — on the all-omni drives almost everyone runs it pulled the robot up in 0.08 m and
//      killed every turn the driver was still carrying. Every robot now brakes on the all-omni
//      number instead, rolling on 3.5x further (rollers have no sideways grip and little forwards).
//      See DrivetrainTuning's brake fractions and BrakeFraction below.
//        Note this is NOT the retired "Coast When You Let Go" checkbox coming back. That offered
//      the driver a choice between two drivetrains, one of which was wrong, and it took 60 ms to
//      engage so a stick swept through centre triggered it. This is one drivetrain with one brake,
//      engaged instantly, every time.
//   4. The braking quadrant. Asking for a direction (or a speed) the wheels are already spinning
//      against is not the same as accelerating: a real motor driven backwards against its own
//      rotation is current-limited and much weaker there. Without that distinction a reversal got
//      the full 3x-traction drive force, the tyres simply slipped, and the robot changed direction
//      with no sense of carrying any momentum at all. STEERING is exempt — the inner wheel of a
//      moving turn is commanded slower than it spins by construction, and braking it is what used
//      to stop the robot turning at speed. See DecideAuthority.
//        That quadrant then has two ends, which one constant used to serve badly. A COAST — centre
//      stick, an eased-off throttle, the inner wheel of a turn — keeps the wheel-type brake torque
//      it was tuned with. A PLOW — the stick thrown through centre and held hard the other way —
//      ramps to the traction limit, because that is what a motor at full reverse current does and
//      because at a fifth of it no robot could ever be made to tip, however tall its lift. The ramp
//      reads the RAW stick, not the slewed command, or full authority arrives after the stop is
//      already over. See BrakeForceLimit and UpdateBrakingQuadrant.
//
//   Tipping is front-to-back ONLY. That asymmetry is deliberate and it is a design rule, not a
//      tuning accident: a driver should be able to put a raised lift on its nose by braking too
//      hard, because on a real robot they can — but nothing SIDEWAYS should ever be able to lay
//      this robot over, because on a real robot with omni rollers it essentially cannot. The plow
//      above is the first half; ApplyRollRelief is the second, cancelling roll about the robot's
//      own forward axis and leaving pitch mathematically untouched.
//
// Sign convention: the rig tool aligns every wheel link's local +X with the wrapper's +X
// (robot right), so a positive joint rotation about +X spins the tire such that its contact
// point at the bottom moves backward — which drives the robot FORWARD on BOTH sides (same
// rule the right-hand-rule cross product v = w x r gives in Unity's axes). The invert bools
// exist because an empirically flipped wheel mesh/axle can still reverse a side in practice.
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
    [Tooltip("Free-spin wheel speed at full stick, in RPM (VEX 360 RPM drivetrain).")]
    public float maxWheelRpm = 360f;
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
    [Tooltip("How much of full wheel speed the turn stick commands. At 1 a full turn spins the wheels as fast as full throttle does, which pivots the robot faster than a driver can catch. Lower = calmer turning; straight-line speed is unaffected.")]
    [Range(0.1f, 1f)]
    public float turnRate = 0.5f;
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
    [Tooltip("Stall force at full stick as a multiple of the tyres' grip. Above 1 on purpose — the " +
             "sim's omni wheels grip sideways as hard as forwards, so a skid-steer turn has to break " +
             "traction; at 1.0 the robot measurably cannot turn at all. 3 puts the traction crossover " +
             "at a third of stick travel: proportional below it, full authority above.")]
    [Range(1f, 6f)]
    public float driveForceTractionMultiple = DrivetrainTuning.DefaultDriveForceTractionMultiple;
    [Tooltip("How hard the motors may brake on ALL-OMNI wheels, as a fraction of the tyres' grip. " +
             "This is also the brake pedal: centred sticks stop the robot with exactly this torque. " +
             "Low, because omni rollers have no sideways grip and a small contact patch forwards — " +
             "an all-omni robot rolls on when you let go, and that roll-out is the drift it has. " +
             "Every robot brakes on this number — see Brake Fraction.")]
    [Range(0.1f, 1.5f)]
    public float omniBrakeFraction = DrivetrainTuning.DefaultOmniBrakeFraction;
    [Tooltip("PARKED. The same limit for a robot that runs A SET OF TRACTION WHEELS: rubber with a " +
             "real contact patch can put a hard stop down, so this is where the firm, stays-put " +
             "pull-up lives. Nothing selects it since the wheel-type box left Settings — the number " +
             "is kept, and still validated against the omni one, so switching back is one line.")]
    [Range(0.1f, 1.5f)]
    public float tractionBrakeFraction = DrivetrainTuning.DefaultTractionBrakeFraction;
    [Tooltip("How hard the motors may brake when the stick is slammed the OTHER WAY, as a fraction " +
             "of the tyres' grip — the far end of a ramp whose near end is the brake fraction above. " +
             "Unlike those, this is not sized by the wheels: an omni grips forwards as well as a " +
             "traction wheel does, it is sideways that it gives up, so a straight-line slam is the " +
             "one input where both wheel types spend everything. At 1.0 a full reversal skids at " +
             "mu*g and puts a raised lift on its nose; drop it to soften that without touching the " +
             "coast.")]
    [Range(0.2f, 2f)]
    public float plowFraction = DrivetrainTuning.DefaultPlowTractionFraction;

    [Header("Roll Relief")]
    [Tooltip("How much SIDEWAYS tipping is cancelled. 1 = the robot cannot go over on its side at " +
             "all, from any cause: steering, a scrub, a side impact, a wheel riding up on something. " +
             "Pitch is untouched — front-to-back tipping under braking and acceleration is real and " +
             "stays exactly as it was. 0 disables this entirely.")]
    [Range(0f, 1f)]
    public float rollRelief = 1f;
    [Tooltip("How stiffly the relief holds the robot level, in rad/s. Critically damped, so this is " +
             "just 'how fast'. 12 settles a degree of roll in about a quarter second without " +
             "fighting the 100 Hz step.")]
    public float rollReliefFrequency = 12f;

    [Tooltip("How much of the tyres' grip a wheel may be given while it is being BACK-DRIVEN — " +
             "commanded slower than it is actually turning, which is the inner wheel of every moving " +
             "turn. Read as a multiple of the traction limit, like Drive Force Traction Multiple, and " +
             "capped by it: set it to that value (3) and this does nothing at all. Lower it and the " +
             "inner wheel stops being able to lock itself against the robot's momentum, which is what " +
             "makes a moving turn rough — but take it to 1 and the robot cannot yaw at speed at all, " +
             "because the sim's isotropic wheels have to break lateral grip on every tyre to turn.")]
    [Range(1f, 3f)]
    public float backDriveTractionMultiple = DrivetrainTuning.DefaultBackDriveTractionMultiple;

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

    // The articulation root, cached: the roll relief reads its attitude and pushes back on it every
    // step, and GetComponent 100 times a second is the kind of thing that shows up on a phone.
    private ArticulationBody rootBody;

    // Roll inertia about the robot's own forward axis, and the largest roll torque the relief is
    // allowed to ask for. Both measured once, in Initialise — see MeasureRollResistance.
    private float rollInertia;
    private float maxRollReliefTorque;

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
    // in the physics reads it. Exposed so LoadTransferValidation can watch a stop happen.
    public float LeanDegrees => leanDeg;

    // Which way this robot actually drives, in world space, measured from its wheels in Initialise.
    // Public because every harness that drives a robot in a straight line and then measures anything
    // directional has to agree with the controller about which way that was — TipOverValidation's
    // lateral balance and RobotBalanceWindow's two margins were both wrong for exactly that reason.
    public Vector3 DriveForwardWorld => DriveForward;
    public Vector3 DriveRightWorld => DriveRight;

    // The force limit each wheel's drive currently carries, so the per-step decision below only
    // writes an ArticulationDrive struct when it actually changes. Same reason MotorActuator keeps
    // its hold flag: this runs 100 times a second across every wheel.
    //
    // A float rather than the DriveAuthority enum it used to be, because the brake is no longer one
    // of two constants — it slides between the coast torque and the plow torque with how far the
    // stick has been thrown. The epsilon below is what keeps that from becoming six marshalled
    // struct writes per step on stick noise.
    private float[] wheelForceLimit = new float[0];
    private float forceLimitEpsilon;

    // The most a back-driven wheel may be given, derived from backDriveTractionMultiple in Initialise.
    private float backDriveTorque;

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

    public enum DriveAuthority { Drive, Brake }

    // Turn commands at or above this keep every wheel at full Drive authority. The inner wheel of
    // a moving turn is commanded slower than it is spinning — the braking quadrant by the numbers —
    // but demoting it to brakeTorque (23% of stall) is what used to stop the robot turning at
    // speed. Sized just above the shaped stick's noise floor; a genuinely held turn crosses it
    // within a step or two of the slew. Public const so DriveFeelValidation notices a nudge.
    public const float TurnAuthorityThreshold = 0.05f;

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
    // rather than a note in a README; RobotSelfOverlapValidation reports the pairs so the geometry
    // can be corrected at the source.
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

        int ignored = 0;
        for (int i = 0; i < colliders.Count; i++)
        {
            for (int j = i + 1; j < colliders.Count; j++)
            {
                ArticulationBody a = owners[i], b = owners[j];
                if (a == null || b == null || a == b) continue;
                // Parent and child of the same joint are never collided by PhysX anyway.
                if (a.transform.IsChildOf(b.transform) || b.transform.IsChildOf(a.transform)) continue;
                // Cheap reject first: ComputePenetration is a real geometry query and there are
                // tens of thousands of pairs.
                if (!colliders[i].bounds.Intersects(colliders[j].bounds)) continue;

                if (!Physics.ComputePenetration(
                        colliders[i], colliders[i].transform.position, colliders[i].transform.rotation,
                        colliders[j], colliders[j].transform.position, colliders[j].transform.rotation,
                        out _, out float depth)) continue;
                if (depth <= RestPoseOverlapEpsilon) continue;

                Physics.IgnoreCollision(colliders[i], colliders[j], true);
                ignored++;
                report?.Add($"{a.name}/{colliders[i].name} <-> {b.name}/{colliders[j].name} " +
                            $"({depth * 100f:0.0} mm)");
            }
        }
        return ignored;
    }

    // Below this, an "overlap" is two boxes touching at a shared face, not one part inside another.
    // 0.001 units is 0.1 mm at this project's scale.
    public const float RestPoseOverlapEpsilon = 0.001f;

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
            BrakeFraction,
            plowFraction);

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
        // stallTorque is peakForce*radius/wheels and peakForce is tractionForce*multiple, so dividing
        // by the multiple gives exactly one traction limit's worth of torque at this wheel — which
        // makes backDriveTractionMultiple read in the same units as the drive one, off the same
        // measurement, with no second derivation to drift out of step with it.
        backDriveTorque = Mathf.Min(
            tuning.stallTorque * backDriveTractionMultiple
                / Mathf.Max(driveForceTractionMultiple, 1e-3f),
            tuning.stallTorque);

        forceLimitEpsilon = Mathf.Max(tuning.stallTorque * 0.005f, 1e-4f);
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

        // FIRST: both of the next two measure along the driving axis, and until this runs there is
        // no reason to believe the root's +Z is it.
        MeasureDriveAxes();
        MeasureRollResistance();
        MeasureLoadTransfer();
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
    // WHAT THAT BROKE, on the two robots that carry a cascade:
    //   - ApplyRollRelief rolls about the forward axis. On those two that is the PITCH axis, so the
    //     relief was cancelling exactly the front-to-back tipping it is documented to leave alone —
    //     "the front and back isn't really that tippy", in one line of code.
    //   - StepLoadTransfer reads longitudinal acceleration as dot(velocity, forward), so it measured
    //     ~0 however hard they braked and the body never leaned.
    //   - MeasureRollResistance took half-track along right, i.e. along the wheelbase.
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
        if (rootBody == null || allWheels.Length == 0) return;

        // Measured in WORLD space and stored in the root's frame. World is where the flattening has
        // to happen — the robot is upright on the floor when this runs, so world up is the normal
        // the axles should be square to — and the root's frame is where the answer has to live, so
        // it rides along once the robot turns.
        Transform t = rootBody.transform;
        Vector3 sum = Vector3.zero;
        for (int i = 0; i < allWheels.Length; i++)
        {
            ArticulationBody wheel = allWheels[i];
            if (wheel == null) continue;

            // The JOINT's twist axis, not the mesh's local +X: on a URDF import the axle can live
            // entirely in anchorRotation with the transform left as the exporter wrote it. The two
            // agree on all four shipped robots, so this costs nothing and covers the case that isn't.
            Vector3 axle = wheel.transform.rotation * wheel.anchorRotation * Vector3.right;

            // An inverted side spins the other way for the same stick, so the robot travels the
            // other way, so forward IS the other way. allWheels is filled left side first.
            bool inverted = i < leftWheelCount ? invertLeft : invertRight;
            sum += inverted ? -axle : axle;
        }

        // Summed raw and never folded onto a reference wheel. A mirrored wheel then costs one vote
        // instead of dragging every other wheel onto its own sign, which is what folding-onto-the-
        // first does when the first one is the mirrored one. An even split leaves nothing to decide
        // with — that robot cannot drive straight either — so this keeps the convention and says
        // nothing rather than picking a side.
        if (sum.sqrMagnitude < 1e-6f) return;

        // The axles carry whatever camber and mounting error the CAD has, and a roll axis with a
        // vertical component would lever the robot into the floor.
        Vector3 right = Vector3.ProjectOnPlane(sum.normalized, Vector3.up);
        if (right.sqrMagnitude < 1e-6f) return;   // axles vertical: nothing sane to derive
        right.Normalize();

        driveRightLocal = t.InverseTransformDirection(right);
        driveForwardLocal = t.InverseTransformDirection(Vector3.Cross(right, Vector3.up).normalized);
    }

    // --- Roll relief ------------------------------------------------------------------------------

    // Nothing sideways may put this robot over. Front-to-back still can, and should.
    //
    // WHY THIS IS NOT CHEATING, and why the fix belongs here rather than in the tyre model. The sim
    // has no omni rollers: every wheel is an isotropic mu-0.8 sphere that grips sideways exactly as
    // hard as it grips forwards. A real omni's rollers make sideways travel nearly free, so a real
    // skid-steer turn scrubs almost nothing. Ours has to break full lateral traction on all six
    // wheels to yaw at all — which is also why driveForceTractionMultiple has to be 3 — and that
    // lateral force lands at the contact patch, a long way below the centre of mass. The roll moment
    // it makes is a pure artifact of the wheel model. Cancelling it is restoring the real robot, not
    // protecting the sim one.
    //
    // WHY IT IS NOT GATED ON STEERING. It was, and that was too narrow a reading of the same
    // argument. The isotropic-sphere tyre is wrong in every direction at once, not only while the
    // turn stick is held: a straight-line drive that scrubs, a shove from another robot, a wheel
    // riding up on a piece or a goal rim all put lateral load through the same contact patches and
    // roll the robot for the same fictional reason. Gating on turnCommand fixed the one case the
    // harness happened to drive and left every other one live, which is why the sim still rocked in
    // play while the turn test read 0.0 degrees. The requirement is the simple one — sideways cannot
    // tip this robot — so the relief is simply always on.
    //
    // ONLY ROLL. The torque is applied about the robot's own FORWARD axis, so pitch cannot be
    // touched by construction: a slammed reversal still puts a raised lift on its nose, which is
    // real and is what the plow exists to produce. Braking and acceleration tip this robot exactly
    // as hard as they did before this existed — TipOverValidation pins both halves of that.
    private void ApplyRollRelief(float dt)
    {
        if (rootBody == null || rollRelief <= 0f || rollInertia <= 0f || dt <= 0f) return;

        Transform t = rootBody.transform;
        Vector3 axis = DriveForward;   // measured from the wheels — NEVER t.forward, see MeasureDriveAxes

        // Roll = how far the robot's up has rotated about its own forward axis away from the most
        // upright it could be at this heading. Projecting world up onto the plane normal to the roll
        // axis is what keeps PITCH out of the measurement: nose-down attitude moves both vectors
        // together and reads as zero roll, which is the whole point.
        Vector3 upRef = Vector3.ProjectOnPlane(Vector3.up, axis);
        if (upRef.sqrMagnitude < 1e-6f) return;   // nose straight up or down: roll is undefined
        float rollDeg = Vector3.SignedAngle(upRef, Vector3.ProjectOnPlane(t.up, axis), axis);

        // Clamped before it becomes a torque, so the angle term saturates instead of growing with
        // how far over the robot already is: a PD term proportional to 90 degrees would fire it
        // across the field trying to right itself. Past this angle the relief still pushes — it is
        // now the only thing standing between a shove and a robot on its side — but it pushes with
        // a bounded torque, so a robot that somehow gets there is levered back rather than launched.
        float rollRad = Mathf.Clamp(rollDeg, -MaxRelievedRollDeg, MaxRelievedRollDeg) * Mathf.Deg2Rad;
        float rollRate = Vector3.Dot(rootBody.angularVelocity, axis);

        // Critically damped PD: omega^2 on the angle, 2*omega on the rate.
        float omega = Mathf.Max(rollReliefFrequency, 0f);
        float angularAccel = -(omega * omega * rollRad + 2f * omega * rollRate);
        float torque = Mathf.Clamp(rollInertia * angularAccel * rollRelief,
            -maxRollReliefTorque, maxRollReliefTorque);

        rootBody.AddTorque(axis * torque, ForceMode.Force);
    }

    // Past this the angle term stops growing. See ApplyRollRelief.
    public const float MaxRelievedRollDeg = 15f;

    // The relief may never ask for more than this multiple of the robot's own static overturning
    // moment. Above 1 so it can actually arrest a roll rather than merely balance one, but finite,
    // because an unbounded corrective torque on the root is a robot that flips itself the other way.
    public const float MaxRollReliefOverturnMultiple = 3f;

    // What it takes to roll this robot, measured off its own geometry rather than assumed.
    //
    // Inertia is sum(m * d^2) with d the distance of each link from the roll axis — the forward line
    // through the robot's centre. That ignores each link's own spin inertia, which is the right
    // trade: the term that matters for roll is how far the mass sits from the centreline, and this
    // is a gain scale with a hard clamp behind it, not a dynamics model.
    //
    // The clamp is sized from the half-track, because m*g*halfTrack IS the torque that holds the
    // robot down on its outside wheels — the moment it would take to lift the inside pair off the
    // floor. Sizing the ceiling off the robot means a 7 kg robot and a 12 kg one both get a relief
    // proportional to what tipping them actually costs.
    private void MeasureRollResistance()
    {
        rollInertia = 0f;
        maxRollReliefTorque = 0f;
        if (rootBody == null) return;

        Transform t = rootBody.transform;
        Vector3 origin = t.position;
        Vector3 axis = DriveForward;
        Vector3 across = DriveRight;

        float mass = 0f;
        foreach (ArticulationBody body in rootBody.GetComponentsInChildren<ArticulationBody>(true))
        {
            if (body == null || body.mass <= 0f) continue;
            Vector3 offset = body.transform.position - origin;
            float d = Vector3.ProjectOnPlane(offset, axis).magnitude;
            rollInertia += body.mass * d * d;
            mass += body.mass;
        }

        // Half-track from the wheels themselves: the mean |lateral offset| of every wheel link.
        float halfTrack = 0f;
        int counted = 0;
        foreach (ArticulationBody wheel in allWheels)
        {
            if (wheel == null) continue;
            halfTrack += Mathf.Abs(Vector3.Dot(wheel.transform.position - origin, across));
            counted++;
        }
        if (counted > 0) halfTrack /= counted;

        maxRollReliefTorque = mass * Mathf.Abs(Physics.gravity.y) * halfTrack
                              * MaxRollReliefOverturnMultiple;
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
    // still settling from the stop it just made". Pure and static so LoadTransferValidation can
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
    // MixArcade's turn priority, DecideAuthority's turn exemption and the plow entirely.
    //
    // Reading the sticks stays in FixedUpdate: there is no input device behind a validator, and
    // manualInput is the path a scripted routine is supposed to take anyway.
    public void ApplyStep(float dt)
    {
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
        // The slew decays the commands, the wheels are commanded 0 deg/s, the braking quadrant
        // limits the pull-up to brakeTorque while they spin, and below the moving gate they park
        // under Drive authority at target 0. Centre stick IS the brake pedal — no dwell, no
        // release, nothing for a stick swept through centre to accidentally trigger.
        throttleCommand = Slew(throttleCommand, throttleTarget,
            throttleRisePerSec, throttleFallPerSec, dt);
        turnCommand = Slew(turnCommand, turnTarget, turnRisePerSec, turnFallPerSec, dt);

        MixArcade(throttleCommand, turnCommand * turnRate, out float left, out float right);

        // Revolute drive target velocities are in DEGREES per second: rpm x 360/60 = rpm x 6.
        float fullStickDegPerSec = maxWheelRpm * 6f;
        float leftDegPerSec = left * fullStickDegPerSec * (invertLeft ? -1f : 1f);
        float rightDegPerSec = right * fullStickDegPerSec * (invertRight ? -1f : 1f);
        ApplySide(leftWheels, leftDegPerSec);
        ApplySide(rightWheels, rightDegPerSec);

        // The same mix again on the RAW targets — what the driver's hands are asking for at this
        // instant, before the slew. Only the brake reads these; see UpdateBrakingQuadrant for why it
        // must not read the slewed command instead.
        //
        // Through MixArcade and the same per-side inversion, not straight off throttleTarget,
        // because what the brake needs is the sign of the command AT THIS WHEEL. Comparing a stick
        // axis against a joint velocity gets the reversal test exactly backwards on any robot whose
        // side is inverted, and inverted sides are the reason those bools exist.
        MixArcade(throttleTarget, turnTarget * turnRate, out float leftRaw, out float rightRaw);
        float leftTargetDegPerSec = leftRaw * fullStickDegPerSec * (invertLeft ? -1f : 1f);
        float rightTargetDegPerSec = rightRaw * fullStickDegPerSec * (invertRight ? -1f : 1f);

        UpdateBrakingQuadrant(leftDegPerSec, rightDegPerSec,
            leftTargetDegPerSec, rightTargetDegPerSec, fullStickDegPerSec);

        // After the drives, because it reads the attitude the last step produced and pushes back on
        // it — an external torque for this step, alongside the wheel torques, not instead of them.
        ApplyRollRelief(dt);

        // Reads the same attitude and applies nothing. Stepped here rather than per frame so the
        // lean is identical at every frame rate, and harmless in an edit-mode harness: nothing is
        // posed until PoseForRender runs, and that only runs while rendering.
        StepLoadTransfer(dt);
    }

    // --- Braking quadrant ------------------------------------------------------------------------

    // Give a wheel full stall torque only when it is being ACCELERATED. When the commanded speed
    // opposes or trails the wheel's actual spin, the motor is being back-driven — the braking
    // quadrant — where a real V5 is limited by its current draw and its own back-EMF and can
    // nowhere near make stall torque. Without this the sim handed a stop 3x the tyres' grip, the
    // tyres slipped, and the robot stopped at the friction limit in ~0.12 m regardless of how fast
    // it had been going: a stop that cost the driver nothing.
    //
    // With centre stick as the brake pedal, this quadrant IS the brake: released sticks decay the
    // command to zero, every spinning wheel is back-driven, and the robot pulls up under
    // brakeTorque. Below the moving gate (BrakeSpeedFraction of free speed) a wheel returns to
    // Drive authority with target 0 — the parking hold — so a parked robot resists a shove with
    // the motor curve instead of chattering on the brake.
    private const float BrakeSpeedFraction = 0.15f;

    // WHY THE BRAKE READS THE PRE-SLEW TARGET. The slew is what makes a keyboard tap a small input,
    // and a full reversal takes 1/throttleFallPerSec + 1/throttleRisePerSec = 0.375 s to get through
    // it. A 0.8 g stop from top speed is over in 0.18 s. Scale the plow torque by the SLEWED command
    // and full braking authority arrives after the robot has already stopped — the peak force lands
    // on a robot that is no longer moving, and nothing ever pitches.
    //
    // That is not just a tuning inconvenience, it is the wrong model. The slew shapes the speed
    // TARGET, which is a statement about how fast the driver wants to end up going. A current-limited
    // brake is a statement about how much voltage the controller is applying RIGHT NOW, and that
    // follows the stick with no ramp at all. So the drive target keeps the slew and the brake limit
    // does not.
    private void UpdateBrakingQuadrant(float leftDegPerSec, float rightDegPerSec,
        float leftTargetDegPerSec, float rightTargetDegPerSec, float fullStickDegPerSec)
    {
        if (allWheels.Length == 0) return;
        float movingDegPerSec = tuning.maxJointVelocity * Mathf.Rad2Deg * BrakeSpeedFraction;

        for (int i = 0; i < allWheels.Length; i++)
        {
            ArticulationBody wheel = allWheels[i];
            if (wheel == null) continue;

            // allWheels is filled left-side first by Awake, so leftWheelCount is the boundary.
            // Taking the command from here rather than reading xDrive.targetVelocity back keeps
            // this honest about what was just asked for, with no marshalling round-trip per wheel.
            bool isLeft = i < leftWheelCount;
            float commandDegPerSec = isLeft ? leftDegPerSec : rightDegPerSec;
            float targetDegPerSec = isLeft ? leftTargetDegPerSec : rightTargetDegPerSec;

            // jointVelocity is in rad/s (joint state speaks radians); the drive target is deg/s.
            float spinDegPerSec = wheel.jointVelocity.dofCount > 0
                ? wheel.jointVelocity[0] * Mathf.Rad2Deg : 0f;

            // WHICH quadrant is still decided by the slewed command, so the steering exemption and
            // the parking hold behave exactly as before. Only HOW HARD the brake pulls, once it is
            // in the braking quadrant, comes off the raw target.
            DriveAuthority authority = DecideAuthority(commandDegPerSec, spinDegPerSec,
                movingDegPerSec, turnCommand, TurnAuthorityThreshold);

            float limit = authority == DriveAuthority.Drive
                ? tuning.stallTorque
                : BrakeForceLimit(targetDegPerSec, spinDegPerSec, fullStickDegPerSec,
                    tuning.brakeTorque, tuning.plowTorque);

            // NO WHEEL GETS MORE FORCE THAN ITS TYRE CAN CARRY WHILE IT IS BEING BACK-DRIVEN.
            //
            // Past the traction limit the extra force does not slow the wheel down, it LOCKS it, and
            // a locked wheel dragged across the floor either skids steadily or breaks and regains
            // grip every step or two. Both are what a driver calls rough.
            //
            // The case this is about is the steering exemption directly above. MixArcade gives the
            // turn priority, so full throttle with full turn shaves the throttle away entirely and
            // mixes to 1.0 and 0.0 — the inner side commanded to a DEAD STOP while the robot is
            // still doing 5-6 u/s. DecideAuthority then exempts it from the braking quadrant because
            // the turn stick is held, and hands it stallTorque: three times the tyres' grip, to
            // enforce a stop the robot's own momentum is fighting. Measured on the shipped robots
            // before this line existed, against the same robot spinning from a standstill:
            //   654V_v3   28 wheel direction changes -> 75, mean slip 3.62 -> 6.31 u/s
            //   654V_v1    4 -> 70
            //   654V_v2    7 -> 1, but mean slip 2.79 -> 6.73 — the steady-skid version of the same
            //              thing, a wheel locked hard enough that it never gets to snatch at all
            // which is exactly the report: smooth spinning from rest, rough the moment it is moving.
            //
            // This is NOT the old "cap the inner wheel at brakeTorque", which is what gutted moving
            // turns and is why the exemption was added. plowTorque is the traction limit itself —
            // several times brakeTorque — so the inner wheel keeps every newton the ground will
            // actually accept, and loses only the part that was never going anywhere but into
            // locking it.
            //
            // Outside a turn this is a no-op by construction: a back-driven moving wheel is already
            // on Brake authority, and BrakeForceLimit's ramp tops out at plowTorque.
            if (Mathf.Abs(spinDegPerSec) > movingDegPerSec
                && BackDriven(commandDegPerSec, spinDegPerSec))
                limit = Mathf.Min(limit, backDriveTorque);

            SetForceLimit(i, limit);
        }
    }

    // --- Drive authority -------------------------------------------------------------------------
    // Same struct-swap shape as MotorActuator.EnterHold/ExitHold, including the per-wheel flag that
    // keeps it from rewriting six drives on every one of the 100 physics steps a second.
    //
    // Brake keeps the velocity drive (at target 0) rather than switching the drive off, so the
    // stop is progressive — torque = damping * speed error, clamped to brakeTorque — and hands
    // back to Drive at target 0 below the moving gate, which is the parking hold.

    private void SetForceLimit(int index, float forceLimit)
    {
        if (Mathf.Abs(wheelForceLimit[index] - forceLimit) <= forceLimitEpsilon) return;
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

    // Renormalized arcade mix with TURN priority. The naive mix clamps throttle±turn to ±1, which
    // at full throttle hands the outer wheel a command it is already meeting and quietly eats up
    // to half the differential — "the forward momentum doesn't allow turning", in code. Here the
    // THROTTLE gives way instead: the differential (left - right == 2*turn) survives at every
    // throttle, and shaving the throttle also pulls the outer wheel down off the top of the motor
    // curve, where it actually has torque left to steer with.
    public static void MixArcade(float throttle, float turn, out float left, out float right)
    {
        turn = Mathf.Clamp(turn, -1f, 1f);
        float overflow = Mathf.Abs(throttle) + Mathf.Abs(turn) - 1f;
        if (overflow > 0f) throttle = Mathf.Sign(throttle) * (Mathf.Abs(throttle) - overflow);
        left = throttle + turn;
        right = throttle - turn;
    }

    // The per-wheel torque authority. Back-driven covers three cases that are all the same thing
    // electrically: commanded into reverse, commanded to a dead stop, or simply commanded SLOWER
    // than it is currently turning — in every one the wheel is turning the motor rather than the
    // other way round, which is the braking quadrant.
    //
    // The steering override comes first because that third case is also the inner wheel of every
    // moving turn. driveForceTractionMultiple is 3 precisely because the sim's isotropic omni
    // wheels must break traction laterally to yaw at all; capping the inner side at brakeTorque
    // (0.23 of stall) while the lateral-scrub budget was sized for 3x is what gutted moving
    // turns. With the sticks straight (turnCommand ~ 0) the decision is exactly what it was
    // before the override existed.
    public static DriveAuthority DecideAuthority(float commandDegPerSec, float spinDegPerSec,
        float movingGateDegPerSec, float turnCommand, float turnThreshold)
    {
        if (Mathf.Abs(turnCommand) >= turnThreshold) return DriveAuthority.Drive;

        bool moving = Mathf.Abs(spinDegPerSec) > movingGateDegPerSec;
        return BackDriven(commandDegPerSec, spinDegPerSec) && moving
            ? DriveAuthority.Brake : DriveAuthority.Drive;
    }

    // The wheel is turning the motor rather than the other way round. One definition, in one place,
    // because UpdateBrakingQuadrant's traction cap has to agree with the authority decision exactly
    // — a wheel this said was back-driven and that one did not would get stall torque to enforce a
    // speed it is being dragged away from, which is the bug the cap exists to stop.
    public static bool BackDriven(float commandDegPerSec, float spinDegPerSec)
    {
        bool sameDirection = spinDegPerSec * commandDegPerSec > 0f;
        return !sameDirection || Mathf.Abs(commandDegPerSec) < Mathf.Abs(spinDegPerSec);
    }

    // How hard a wheel already in the braking quadrant may actually pull.
    //
    // DecideAuthority lumps three things together, because electrically they are the same thing: a
    // wheel commanded into reverse, one commanded to a dead stop, and one merely commanded SLOWER
    // than it is turning. Mechanically they are not. The last two are a COAST — the driver has taken
    // their foot off — and they keep brakeTorque, which is what the two wheel-type brake fractions
    // were sized against and what every measured roll-out distance is. The first is a PLOW: the
    // driver has deliberately thrown the stick through centre and out the other side, and a real
    // motor commanded hard against its own rotation puts down far more than a coast does.
    //
    // So this interpolates from one to the other with how far the stick has been thrown INTO the
    // spin. Centre stick is the case to check the boundary on: targetDegPerSec is then exactly 0,
    // the product is 0, `>= 0f` is true, and brakeTorque comes back untouched. The brake pedal is
    // bit-for-bit what it was.
    //
    // The sign test is `spin * target < 0` — strictly opposing. A same-sign-but-smaller target is
    // the trailing case, which is also the inner wheel of every moving turn, and handing that a plow
    // torque would re-break moving turns the same way capping it at brakeTorque once did.
    public static float BrakeForceLimit(float targetDegPerSec, float spinDegPerSec,
        float fullStickDegPerSec, float brakeTorque, float plowTorque)
    {
        if (spinDegPerSec * targetDegPerSec >= 0f) return brakeTorque;
        if (fullStickDegPerSec <= 1e-3f) return brakeTorque;

        float stickThrow = Mathf.Clamp01(Mathf.Abs(targetDegPerSec) / fullStickDegPerSec);
        return Mathf.Lerp(brakeTorque, plowTorque, stickThrow);
    }

    // Autonomy/test hook: drive without input devices (e.g. scripted routines, play-mode tests).
    public void SetManualInput(float throttle, float turn)
    {
        manualThrottle = throttle;
        manualTurn = turn;
        manualInput = true;
    }

    public void ClearManualInput()
    {
        manualInput = false;
        manualThrottle = 0f;
        manualTurn = 0f;
    }

    private static void ApplySide(ArticulationBody[] wheels, float degPerSec)
    {
        if (wheels == null) return;
        foreach (ArticulationBody wheel in wheels)
        {
            if (wheel != null) wheel.SetDriveTargetVelocity(ArticulationDriveAxis.X, degPerSec);
        }
    }

}
