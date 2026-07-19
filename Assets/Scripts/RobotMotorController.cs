using UnityEngine;
using UnityEngine.InputSystem;

// Motor-torque drivetrain controller for the ArticulationBody-rigged robot.
//
// Unlike RobotDriveController — which force-sets Rigidbody velocities and never yields to
// contacts — this drives the wheel links' revolute joints with torque-limited velocity drives,
// so the robot's speed emerges from motor strength vs. load: it can stall against a wall, get
// slowed by heavy pieces, and shove things with real contact forces instead of teleport-pushes.
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
    [Tooltip("Drive force limit — the motor's stall torque in scaled units. Real V5 motor ~1.17 N·m, scaled by mass ~6x and length² ~100x for the 10x-scale world: ~700.")]
    public float wheelStallTorque = 700f;
    [Tooltip("Velocity drives use damping as the velocity-tracking gain. MUST be > 0 or the drive produces no torque at all (stiffness stays 0 for pure velocity control).")]
    public float velocityDriveDamping = 1000f;
    [Tooltip("Rolling resistance on each wheel's spin axis (Coulomb joint friction). A real drivetrain is never frictionless — this makes the robot coast to a stop instead of gliding forever. Applied to every wheel at Awake, so it affects all robots (this is the durable knob: the rig tool bakes a value but Awake here is authoritative). 0 = frictionless.")]
    public float wheelRollingResistance = 0.3f;
    [Tooltip("Velocity-proportional spin loss on each wheel — bleeds a little top speed so a 360 RPM drivetrain doesn't feel unrealistically quick, without changing the arcade feel. Higher = slower / heavier. Dial this with wheelRollingResistance to taste.")]
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

    private Vector2 leftStickInput;
    private Vector2 rightStickInput;

    // Test/autonomy hook: while set, FixedUpdate uses these instead of the stick reads.
    private bool manualInput;
    private float manualThrottle;
    private float manualTurn;

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

        // Bake the motor model into every wheel joint's X drive. Velocity drives need
        // stiffness 0 (no position spring) and damping > 0 (the velocity gain); forceLimit
        // is what makes this behave like a torque-limited motor instead of a hard constraint.
        foreach (ArticulationBody wheel in AllWheels())
        {
            ArticulationDrive d = wheel.xDrive;
            d.driveType = ArticulationDriveType.Velocity;
            d.forceLimit = wheelStallTorque;
            d.damping = velocityDriveDamping;
            d.stiffness = 0f;
            wheel.xDrive = d;

            // maxJointVelocity is in rad/s (drives speak degrees, joint limits speak radians).
            // Cap slightly above the free-spin target so the drive can actually reach it.
            wheel.maxJointVelocity = maxWheelRpm * Mathf.PI * 2f / 60f * 1.1f;

            // Drivetrain "imperfection": a real dt has losses, so a wheel neither hits its full
            // commanded speed nor coasts forever. jointFriction is Coulomb drag on the axle (the
            // coast-to-a-stop feel); angularDamping bleeds a little top speed proportional to spin.
            // Set here (not just in the rig tool) so it applies uniformly to every robot at play,
            // including ones rigged before these knobs existed. Set both to 0 for the old
            // frictionless behavior.
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

    void Update()
    {
        // Read values from the virtual on-screen joysticks (guarded until they're wired up)
        if (leftJoystickAction != null) leftStickInput = leftJoystickAction.action.ReadValue<Vector2>();
        if (rightJoystickAction != null) rightStickInput = rightJoystickAction.action.ReadValue<Vector2>();
    }

    void FixedUpdate()
    {
        // Arcade Drive (Left Stick controls Forward/Backward, Right Stick controls Turning)
        float throttle = manualInput ? manualThrottle : leftStickInput.y;
        float turn = (manualInput ? manualTurn : rightStickInput.x) * turnRate;

        // "Reverse Drive Direction" (Settings): flip which end is "front". That's a 180° rotation of
        // the control frame, so BOTH the forward axis and the steering axis invert — negating throttle
        // alone would mirror-image the steering when driving from the new front. Read live from
        // PlayerPrefs so no spawner/instance wiring is needed.
        if (ReverseDriveSettings.Reversed) { throttle = -throttle; turn = -turn; }

        float left = Mathf.Clamp(throttle + turn, -1f, 1f);
        float right = Mathf.Clamp(throttle - turn, -1f, 1f);

        // Revolute drive target velocities are in DEGREES per second: rpm x 360/60 = rpm x 6.
        float fullStickDegPerSec = maxWheelRpm * 6f;
        ApplySide(leftWheels, left * fullStickDegPerSec * (invertLeft ? -1f : 1f));
        ApplySide(rightWheels, right * fullStickDegPerSec * (invertRight ? -1f : 1f));
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

    private System.Collections.Generic.IEnumerable<ArticulationBody> AllWheels()
    {
        if (leftWheels != null)
            foreach (ArticulationBody w in leftWheels) if (w != null) yield return w;
        if (rightWheels != null)
            foreach (ArticulationBody w in rightWheels) if (w != null) yield return w;
    }
}
