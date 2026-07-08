using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
public class RobotDriveController : MonoBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 15f;
    public float turnSpeed = 10f;

    [Header("Input Actions")]
    [SerializeField] private InputActionReference leftJoystickAction;
    [SerializeField] private InputActionReference rightJoystickAction;

    private Rigidbody rb;
    private Vector2 leftStickInput;
    private Vector2 rightStickInput;

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

        // Pivot turns around the chassis center. The imported drivetrain is offset from
        // this object's origin, so the auto-computed center of mass sits off to one side;
        // anchor it to the chassis BoxCollider's center instead (added by the Fix Robot
        // Collider editor tool). Falls back to the implicit COM if no box is present yet.
        BoxCollider chassis = GetComponent<BoxCollider>();
        if (chassis != null) rb.centerOfMass = chassis.center;
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

        // Turn Left/Right
        float turn = turnInput * turnSpeed;
        Quaternion turnRotation = Quaternion.Euler(0f, turn * Time.fixedDeltaTime, 0f);
        rb.MoveRotation(rb.rotation * turnRotation);
    }
}
