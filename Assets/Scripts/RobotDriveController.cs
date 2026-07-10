using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class RobotDriveController : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 15f;
    public float turnSpeed = 120f; // degrees per second at full stick

    [Header("Turn Pivots (local space — set by the Fix Robot Drive Collider tool)")]
    [SerializeField] private Vector3 leftPivotOffset;   // Left drivetrain rail center
    [SerializeField] private Vector3 rightPivotOffset;  // Right drivetrain rail center
    [SerializeField] private Vector3 centerOffset;      // Chassis center (used when driving straight)

    [Header("Ground")]
    [Tooltip("World Y of the floor surface. On the flat field the robot's wheels are pinned here so it can't climb over pieces.")]
    [SerializeField] private float floorY = 0.72f;

    [Header("Input Actions")]
    [SerializeField] private InputActionReference leftJoystickAction;
    [SerializeField] private InputActionReference rightJoystickAction;

    private Rigidbody rb;
    private Collider[] cols;
    private Vector2 leftStickInput;
    private Vector2 rightStickInput;

    // Below this stick magnitude we don't consider the robot to be turning.
    private const float TurnDeadzone = 0.05f;

    void Awake()
    {
        // RequireComponent guarantees this exists, so grab it early (before OnEnable/Start)
        rb = GetComponent<Rigidbody>();
        cols = GetComponentsInChildren<Collider>();

        // Speculative CCD + interpolation stop the robot from tunneling through walls AND game
        // pieces when driving at speed. We must use ContinuousSpeculative (not ContinuousDynamic):
        // ContinuousDynamic only sweeps against static colliders and other Continuous* bodies, so
        // against the pins/cups — which are plain Discrete dynamic bodies — it silently degrades to
        // discrete detection and the fast robot skips right through them between physics steps.
        // ContinuousSpeculative sweeps against ALL colliders regardless of their mode, so it catches
        // the pieces too without us having to reconfigure every piece's rigidbody.
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        // The robot force-sets its own velocity every FixedUpdate (see below) and never yields to
        // contacts, so when it drives into a light pin at full speed the solver has to eject the
        // piece within a single step. With the default iteration counts (6/1) a deep overlap
        // sometimes gets resolved by squirting the piece out the far side — it looks like the robot
        // "phased through" it. More iterations enforce the contact harder so the piece is shoved
        // aside instead of tunneling. Cheap: it's one rigidbody, not every piece.
        rb.solverIterations = 16;
        rb.solverVelocityIterations = 8;

        // The robot's height is pinned to the floor each frame (see FixedUpdate), so gravity would
        // only fight that pin — and its downward weight was helping crush pieces into the floor.
        rb.useGravity = false;

        // Center of mass stays put; we produce the inner-wheel pivot by choosing the linear
        // velocity that holds the pivot point still (see FixedUpdate), which is far more stable
        // than shoving the center of mass around each frame.
        rb.centerOfMass = centerOffset;
    }

    void OnEnable()
    {
        if (leftJoystickAction != null) leftJoystickAction.action.Enable();
        else Debug.LogWarning("RobotDriveController: 'Left Joystick Action' is not assigned in the Inspector.", this);

        if (rightJoystickAction != null) rightJoystickAction.action.Enable();
        else Debug.LogWarning("RobotDriveController: 'Right Joystick Action' is not assigned in the Inspector.", this);
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
        float forwardInput = leftStickInput.y;
        float turnInput = rightStickInput.x;

        bool turning = turnInput > TurnDeadzone || turnInput < -TurnDeadzone;

        // Straight-line drive velocity (planar).
        Vector3 driveVel = transform.forward * (forwardInput * moveSpeed);

        // Inner-wheel pivot: spin about the drivetrain rail on the side we're turning toward.
        // Instead of moving the center of mass, we set the exact linear velocity that keeps the
        // pivot point stationary while the body rotates (v_pivot = v_com + w x (pivot - com) = 0),
        // so the robot cleanly rotates about that wheel instead of lurching across the field.
        Vector3 angVel = Vector3.zero;
        Vector3 pivotVel = Vector3.zero;
        if (turning)
        {
            angVel = new Vector3(0f, turnInput * turnSpeed * Mathf.Deg2Rad, 0f);
            Vector3 pivotLocal = turnInput > 0f ? rightPivotOffset : leftPivotOffset;
            Vector3 pivotWorld = transform.TransformPoint(pivotLocal);
            pivotVel = Vector3.Cross(angVel, rb.worldCenterOfMass - pivotWorld);
        }

        // Drive by setting velocity (not MovePosition) so walls physically stop the robot instead
        // of it being forced through them. Preserve the vertical component so gravity still applies.
        Vector3 planar = driveVel + pivotVel;
        rb.linearVelocity = new Vector3(planar.x, rb.linearVelocity.y, planar.z);
        rb.angularVelocity = angVel;

        // Keep the robot upright, and lock its heading when driving straight so the off-center
        // chassis can't drift sideways. Free the yaw axis only while actively turning.
        RigidbodyConstraints held = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        rb.constraints = turning ? held : held | RigidbodyConstraints.FreezeRotationY;

        // Pin the robot's wheels to the floor plane. On the flat field this keeps it from riding
        // up over pieces (the reason it "climbed" cups/pins): shift it so its lowest collider point
        // sits at the floor, and cancel any vertical velocity.
        float lowest = LowestColliderY();
        if (!float.IsInfinity(lowest))
        {
            rb.position += new Vector3(0f, floorY - lowest, 0f);
            Vector3 lv = rb.linearVelocity; lv.y = 0f; rb.linearVelocity = lv;
        }
    }

    private float LowestColliderY()
    {
        float lowest = float.PositiveInfinity;
        if (cols != null)
            foreach (Collider c in cols) lowest = Mathf.Min(lowest, c.bounds.min.y);
        return lowest;
    }
}
