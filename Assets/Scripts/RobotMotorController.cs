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
//   3. Coast. A velocity drive held at target 0 with full stall torque is not "off" — it is a
//      locked-wheel skid brake, which is why the robot used to stop dead the instant you released
//      and never felt like it rolled on omni wheels. Releasing the sticks now drops the drives to
//      a rolling-resistance-sized torque (Crr * m * g, a real coefficient) and lets it glide.
//   4. The braking quadrant. Asking for a direction the wheels are already spinning against is
//      not the same as accelerating: a real motor driven backwards against its own rotation is
//      current-limited and much weaker there. Without that distinction a reversal got the full
//      3x-traction drive force, the tyres simply slipped, and the robot changed direction with no
//      sense of carrying any momentum at all.
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
             "gearbox drag, not tyre rolling resistance, which is Wheel Rolling Resistance Crr below. " +
             "Worth a couple of percent of the coast drag; kept because a real axle isn't frictionless.")]
    public float wheelRollingResistance = 0.3f;
    [Tooltip("Velocity-proportional spin loss on each wheel (ArticulationBody.angularDamping). " +
             "Measured contribution is under 1% of top speed and ~4% of coast drag, so treat it as " +
             "trim, not a tuning knob — the coast is set by Wheel Rolling Resistance Crr.")]
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
    [Tooltip("Rolling resistance of the tyre on the field, as the coefficient Crr in F = Crr*m*g. " +
             "This is what the robot coasts down against when you let go — a property of the wheel " +
             "and the surface, so a taller-geared robot correctly rolls further rather than always " +
             "taking the same time. Estimated; plausible band 0.05-0.12. See DrivetrainTuning.")]
    [Range(0.01f, 0.25f)]
    public float wheelRollingResistanceCrr = DrivetrainTuning.DefaultRollingResistance;
    [Tooltip("How hard the motors may brake when you ask for a direction the wheels are already " +
             "spinning against, as a fraction of the tyres' grip. Below 1 because a motor in its " +
             "braking quadrant is current-limited, nowhere near stall torque — that is what makes a " +
             "hard reversal feel like it carries momentum instead of stopping on a coin.")]
    [Range(0.2f, 1.5f)]
    public float brakeTractionFraction = DrivetrainTuning.DefaultBrakeTractionFraction;

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

    private enum DriveAuthority { Drive, Coast, Brake }

    // Fixed steps the sticks must sit at neutral before the wheels are RELEASED.
    //
    // Without this, sweeping a stick from full forward to full reverse passes through the deadzone,
    // the neutral test fires for those few steps, and the drivetrain both drops to coast authority
    // and throws away the accumulated command — handing the driver a free, fully-rearmed reversal
    // at the exact moment they should be fighting the robot's momentum.
    //
    // 6 steps = 60 ms at the project's 100 Hz. Long enough that no deliberate flick through an 0.08
    // deadzone gets past it, short enough that a genuine release still feels immediate — and the
    // cost of being wrong is small either way, because during the dwell the wheels are already in
    // the braking quadrant (command below actual speed) and so limited to brakeTorque, not stall.
    private const int CoastDwellSteps = 6;
    private int neutralSteps;

    // Player prefs, snapshotted at Awake (see DriveFeelSettings for why they aren't read live).
    private float driveSensitivity = DriveFeelSettings.DefaultDriveSensitivity;
    private float turnSensitivity = DriveFeelSettings.DefaultTurnSensitivity;

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
            wheelRollingResistanceCrr,
            brakeTractionFraction);

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
        neutralSteps = 0;
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

            // Drivetrain "imperfection": a real dt has losses, so a wheel neither hits its full
            // commanded speed nor coasts forever. jointFriction is Coulomb drag on the axle;
            // angularDamping bleeds a little top speed proportional to spin. These used to be
            // three orders of magnitude below the 700-unit braking torque and so never mattered;
            // against the coast torque they finally do. Set here (not just in the rig tool) so
            // they apply uniformly to every robot at play, including ones rigged before these
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
            // goes through the slew and the coast, because that IS the drivetrain.
            throttleTarget = Mathf.Clamp(manualThrottle, -1f, 1f);
            turnTarget = Mathf.Clamp(manualTurn, -1f, 1f);
        }
        else
        {
            throttleTarget = Shape(leftStickInput.y, inputDeadzone, throttleExpo) * driveSensitivity;
            turnTarget = Shape(rightStickInput.x, inputDeadzone, turnExpo) * turnSensitivity;
        }

        // "Reverse Drive Direction" (Settings): flip which end is "front". That's a 180° rotation of
        // the control frame, so BOTH the forward axis and the steering axis invert — negating throttle
        // alone would mirror-image the steering when driving from the new front. Read live from
        // PlayerPrefs so no spawner/instance wiring is needed. Applied to the TARGET, before the
        // slew, so flipping it mid-drive ramps across instead of snapping.
        if (ReverseDriveSettings.Reversed) { throttleTarget = -throttleTarget; turnTarget = -turnTarget; }

        // Neutral means RELEASE, not "drive to zero" — but only once the sticks have STAYED there.
        // Tested on the TARGET rather than the slewed command so a genuine release coasts promptly
        // instead of first ramping a phantom demand down; gated by a dwell so a stick swept from
        // forward to reverse doesn't get a free release (and a wiped command) on the way past
        // centre. See CoastDwellSteps.
        bool neutralNow = Mathf.Abs(throttleTarget) < 1e-4f && Mathf.Abs(turnTarget) < 1e-4f;
        neutralSteps = neutralNow ? Mathf.Min(neutralSteps + 1, CoastDwellSteps) : 0;

        if (neutralSteps >= CoastDwellSteps)
        {
            throttleCommand = 0f;
            turnCommand = 0f;
            SetAuthority(DriveAuthority.Coast);
            ApplySide(leftWheels, 0f);
            ApplySide(rightWheels, 0f);
            return;
        }

        float dt = Time.fixedDeltaTime;
        throttleCommand = Slew(throttleCommand, throttleTarget,
            throttleRisePerSec, throttleFallPerSec, dt);
        turnCommand = Slew(turnCommand, turnTarget, turnRisePerSec, turnFallPerSec, dt);

        float turn = turnCommand * turnRate;
        float left = Mathf.Clamp(throttleCommand + turn, -1f, 1f);
        float right = Mathf.Clamp(throttleCommand - turn, -1f, 1f);

        // Revolute drive target velocities are in DEGREES per second: rpm x 360/60 = rpm x 6.
        float fullStickDegPerSec = maxWheelRpm * 6f;
        float leftDegPerSec = left * fullStickDegPerSec * (invertLeft ? -1f : 1f);
        float rightDegPerSec = right * fullStickDegPerSec * (invertRight ? -1f : 1f);
        ApplySide(leftWheels, leftDegPerSec);
        ApplySide(rightWheels, rightDegPerSec);

        UpdateBrakingQuadrant(leftDegPerSec, rightDegPerSec);
    }

    // --- Braking quadrant ------------------------------------------------------------------------

    // Give a wheel full stall torque only when it is being ACCELERATED. When the commanded
    // direction opposes the direction it is actually spinning, the motor is being back-driven —
    // the braking quadrant — where a real V5 is limited by its current draw and its own back-EMF
    // and can nowhere near make stall torque. Without this the sim handed a reversal 3x the tyres'
    // grip, the tyres slipped, and the robot stopped at the friction limit in ~0.12 m regardless of
    // how fast it had been going: a direction change that cost the driver nothing.
    //
    // Gated on the wheel actually MOVING (BrakeSpeedFraction of free speed), and that gate is
    // load-bearing rather than an optimisation. In a skid-steer point turn the inner wheels are
    // commanded opposite to the outer ones, so they sit in the braking quadrant for the entry
    // transient — and driveForceTractionMultiple is 3 precisely because the sim's isotropic omni
    // wheels have to break traction laterally to turn at all. Cutting authority there would gut
    // the turn. Below the gate the wheel keeps full torque and the turn starts normally; the brake
    // limit only bites on a genuine reversal from speed.
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

            // Back-driven covers three cases that are all the same thing electrically: commanded
            // into reverse, commanded to a dead stop, or simply commanded SLOWER than it is
            // currently turning. In every one the wheel is turning the motor rather than the other
            // way round, which is the braking quadrant. Getting the third case wrong is what would
            // have made the release itself violent: easing off puts the command below the actual
            // speed, and at full stall torque that alone asks for 2.4 g of retardation — the tyres
            // just slip and the robot stops dead, which is the original complaint.
            bool sameDirection = spinDegPerSec * commandDegPerSec > 0f;
            bool backDriven = !sameDirection
                              || Mathf.Abs(commandDegPerSec) < Mathf.Abs(spinDegPerSec);
            bool moving = Mathf.Abs(spinDegPerSec) > movingDegPerSec;
            SetAuthority(i, backDriven && moving ? DriveAuthority.Brake : DriveAuthority.Drive);
        }
    }

    // --- Drive authority -------------------------------------------------------------------------
    // Same struct-swap shape as MotorActuator.EnterHold/ExitHold, including the per-wheel flag that
    // keeps it from rewriting six drives on every one of the 100 physics steps a second.
    //
    // Coast keeps the velocity drive rather than zeroing it, so the last of the roll still bleeds
    // off smoothly instead of the wheel free-spinning forever; the forceLimit is what turns the
    // drive from a motor into rolling resistance.

    private void SetAuthority(DriveAuthority authority)
    {
        for (int i = 0; i < allWheels.Length; i++) SetAuthority(i, authority);
    }

    private void SetAuthority(int index, DriveAuthority authority)
    {
        if (wheelAuthority[index] == authority) return;
        ArticulationBody wheel = allWheels[index];
        if (wheel == null) return;

        ArticulationDrive d = wheel.xDrive;
        d.forceLimit = authority switch
        {
            DriveAuthority.Coast => tuning.coastTorque,
            DriveAuthority.Brake => tuning.brakeTorque,
            _ => tuning.stallTorque,
        };
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
