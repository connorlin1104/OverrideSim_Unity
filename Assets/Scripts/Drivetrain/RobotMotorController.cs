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
//      killed every turn the driver was still carrying. The stop is now sized by what the wheels
//      ARE: all-omni rolls on 3.5x further (rollers have no sideways grip and little forwards), a
//      robot with a set of traction wheels bites at the old 0.56 g. See DrivetrainTuning's two
//      brake fractions and WheelTypeSettings.
//        Note this is NOT the retired "Coast When You Let Go" checkbox coming back. That offered
//      the driver a choice between two drivetrains, one of which was wrong, and it took 60 ms to
//      engage so a stick swept through centre triggered it. This is one drivetrain whose brake is
//      sized by the hardware, engaged instantly, every time.
//   4. The braking quadrant. Asking for a direction (or a speed) the wheels are already spinning
//      against is not the same as accelerating: a real motor driven backwards against its own
//      rotation is current-limited and much weaker there. Without that distinction a reversal got
//      the full 3x-traction drive force, the tyres simply slipped, and the robot changed direction
//      with no sense of carrying any momentum at all. STEERING is exempt — the inner wheel of a
//      moving turn is commanded slower than it spins by construction, and braking it is what used
//      to stop the robot turning at speed. See DecideAuthority.
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
             "Used unless the player ticks 'My Robot Has Traction Wheels'.")]
    [Range(0.1f, 1.5f)]
    public float omniBrakeFraction = DrivetrainTuning.DefaultOmniBrakeFraction;
    [Tooltip("The same limit for a robot that runs A SET OF TRACTION WHEELS, chosen instead of Omni " +
             "Brake Fraction when the player ticks 'My Robot Has Traction Wheels'. Rubber with a real " +
             "contact patch can put a hard stop down, so this is where the firm, stays-put pull-up " +
             "lives. Still under the friction cone, so the motor rather than the ground is the limit.")]
    [Range(0.1f, 1.5f)]
    public float tractionBrakeFraction = DrivetrainTuning.DefaultTractionBrakeFraction;

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

    // Which authority each wheel's drive currently carries, so the per-step decision below only
    // writes an ArticulationDrive struct when it actually changes. Same reason MotorActuator keeps
    // its hold flag: this runs 100 times a second across every wheel.
    private DriveAuthority[] wheelAuthority = new DriveAuthority[0];

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

    // Which brake fraction this robot's wheels can actually put down. Not a feel preference — it is
    // a statement about what the robot is built from, which is why it changes the physics and why
    // there is no slider for it, only the two numbers above and a box that picks between them.
    public float BrakeFraction => WheelTypeSettings.TractionWheels
        ? tractionBrakeFraction : omniBrakeFraction;

    void Awake()
    {
        // Firm contacts against the mass-1 pieces. solverIterations is a runtime-only
        // property (not serialized), so the rig tool's edit-time values never survive into
        // play mode — this is the authoritative place to set them.
        ArticulationBody root = GetComponent<ArticulationBody>();
        if (root != null)
        {
            root.solverIterations = solverIterations;
            root.solverVelocityIterations = solverVelocityIterations;
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
        wheelAuthority = new DriveAuthority[allWheels.Length];

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
    }

    void OnEnable()
    {
        if (leftJoystickAction != null) leftJoystickAction.action.Enable();
        else Debug.LogWarning("RobotMotorController: 'Left Joystick Action' is not assigned in the Inspector.", this);

        if (rightJoystickAction != null) rightJoystickAction.action.Enable();
        else Debug.LogWarning("RobotMotorController: 'Right Joystick Action' is not assigned in the Inspector.", this);
    }

    void OnDisable()
    {
        if (leftJoystickAction != null) leftJoystickAction.action.Disable();
        if (rightJoystickAction != null) rightJoystickAction.action.Disable();
    }

    void FixedUpdate()
    {
        // Read where it is consumed. Input System events are still processed once per rendered
        // frame by default, so this returns the same value an Update read would — the gain is that
        // the slew integrator below sees the stick at the instant it integrates it, rather than a
        // value latched an unknown fraction of a frame ago.
        if (leftJoystickAction != null) leftStickInput = leftJoystickAction.action.ReadValue<Vector2>();
        if (rightJoystickAction != null) rightStickInput = rightJoystickAction.action.ReadValue<Vector2>();

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
        float dt = Time.fixedDeltaTime;
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

        UpdateBrakingQuadrant(leftDegPerSec, rightDegPerSec);
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

    private void UpdateBrakingQuadrant(float leftDegPerSec, float rightDegPerSec)
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
            float commandDegPerSec = i < leftWheelCount ? leftDegPerSec : rightDegPerSec;

            // jointVelocity is in rad/s (joint state speaks radians); the drive target is deg/s.
            float spinDegPerSec = wheel.jointVelocity.dofCount > 0
                ? wheel.jointVelocity[0] * Mathf.Rad2Deg : 0f;

            SetAuthority(i, DecideAuthority(commandDegPerSec, spinDegPerSec, movingDegPerSec,
                turnCommand, TurnAuthorityThreshold));
        }
    }

    // --- Drive authority -------------------------------------------------------------------------
    // Same struct-swap shape as MotorActuator.EnterHold/ExitHold, including the per-wheel flag that
    // keeps it from rewriting six drives on every one of the 100 physics steps a second.
    //
    // Brake keeps the velocity drive (at target 0) rather than switching the drive off, so the
    // stop is progressive — torque = damping * speed error, clamped to brakeTorque — and hands
    // back to Drive at target 0 below the moving gate, which is the parking hold.

    private void SetAuthority(int index, DriveAuthority authority)
    {
        if (wheelAuthority[index] == authority) return;
        ArticulationBody wheel = allWheels[index];
        if (wheel == null) return;

        ArticulationDrive d = wheel.xDrive;
        d.forceLimit = authority == DriveAuthority.Brake ? tuning.brakeTorque : tuning.stallTorque;
        wheel.xDrive = d;
        wheelAuthority[index] = authority;
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

        bool sameDirection = spinDegPerSec * commandDegPerSec > 0f;
        bool backDriven = !sameDirection
                          || Mathf.Abs(commandDegPerSec) < Mathf.Abs(spinDegPerSec);
        bool moving = Mathf.Abs(spinDegPerSec) > movingGateDegPerSec;
        return backDriven && moving ? DriveAuthority.Brake : DriveAuthority.Drive;
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
