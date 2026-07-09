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

    [Header("Input Actions")]
    [SerializeField] private InputActionReference leftJoystickAction;
    [SerializeField] private InputActionReference rightJoystickAction;

    private Rigidbody rb;
    private Vector2 leftStickInput;
    private Vector2 rightStickInput;

    // Below this stick magnitude we don't consider the robot to be turning.
    private const float TurnDeadzone = 0.05f;

    void Awake()
    {
        // RequireComponent guarantees this exists, so grab it early (before OnEnable/Start)
        rb = GetComponent<Rigidbody>();
        // Ensure the robot doesn't randomly tip over easily
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

        // Continuous detection + interpolation stop the robot from tunneling through
        // (and getting flung by) walls when driving at speed.
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        // Turning rotates the body about its center of mass, so we steer the pivot by
        // moving the center of mass between the two drivetrain rails (see FixedUpdate).
        // Start centered for driving straight. The pivot offsets are filled in by the
        // Fix Robot Drive Collider editor tool; until then they're zero (pivots at origin).
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

        // Drive by setting velocity (not MovePosition) so walls physically stop the robot
        // instead of it being forced through them. Preserve the vertical component so
        // gravity still applies.
        Vector3 desired = transform.forward * (forwardInput * moveSpeed);
        rb.linearVelocity = new Vector3(desired.x, rb.linearVelocity.y, desired.z);

        // Turn by spinning about the inner drivetrain rail: move the center of mass to the
        // rail on the side we're turning toward, then apply angular velocity. Physics
        // rotation pivots about the center of mass, so the robot pivots on that wheel
        // (and arcs when also driving forward). Centered when going straight.
        bool turning = turnInput > TurnDeadzone || turnInput < -TurnDeadzone;

        if (turnInput > TurnDeadzone) rb.centerOfMass = rightPivotOffset;
        else if (turnInput < -TurnDeadzone) rb.centerOfMass = leftPivotOffset;
        else rb.centerOfMass = centerOffset;

        // Lock the heading (freeze yaw) when driving straight so the off-center chassis dragging
        // on the ground can't leak a bit of rotation and drift the robot sideways. Free the yaw
        // axis only while actively turning.
        RigidbodyConstraints keepUpright = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        rb.constraints = turning ? keepUpright : keepUpright | RigidbodyConstraints.FreezeRotationY;

        // If the robot turns the wrong way, negate this term (and the pivot sides still match).
        rb.angularVelocity = new Vector3(0f, turnInput * turnSpeed * Mathf.Deg2Rad, 0f);
    }
}
