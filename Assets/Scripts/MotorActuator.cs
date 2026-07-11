using UnityEngine;

// Hold-to-run motor driver for a single non-drivetrain ArticulationBody joint (arm, lift,
// intake...). Same torque-limited velocity-drive model as the drivetrain wheels in
// RobotMotorController: stiffness 0 (no position spring), damping as the velocity-tracking
// gain, forceLimit as the stall torque — so a loaded arm slows and stalls instead of
// teleporting through obstacles.
//
// SetInput applies the drive target immediately rather than in FixedUpdate: drive target
// velocities persist across physics steps, and this is what lets edit-mode batch validation
// (Physics.Simulate, where Awake/Update never run) drive a joint whose drive parameters were
// baked at edit time by the URDF post-processor.
//
// Works for both revolute joints (maxRpm, drive speaks degrees/s) and prismatic joints
// (maxLinearSpeed in world units/s). Never touches the joint anchors — for URDF imports the
// importer's anchorRotation encodes the joint axis, and re-seeding it is the known way to
// break a joint.
//
// Usage: added and wired by the URDF post-processor for every non-wheel revolute/continuous
// joint; ButtonRouter calls SetInput from the mapped controller buttons.
public class MotorActuator : MonoBehaviour
{
    [Tooltip("The joint link to drive. Defaults to the ArticulationBody on this GameObject.")]
    public ArticulationBody body;

    [Header("Motor Settings")]
    [Tooltip("Free-spin speed at full input for revolute joints, in RPM (100 RPM = a typical geared V5 arm — tune per mechanism).")]
    public float maxRpm = 100f;
    [Tooltip("Full-input speed for prismatic joints, in world units/s (1 unit = 0.1 m).")]
    public float maxLinearSpeed = 2f;
    [Tooltip("Drive force limit — the motor's stall torque (revolute) or stall force (prismatic) in scaled units.")]
    public float stallTorque = 700f;
    [Tooltip("Velocity drives use damping as the velocity-tracking gain. MUST be > 0 or the drive produces no torque at all.")]
    public float velocityDriveDamping = 1000f;
    [Tooltip("Flip if the mechanism empirically runs backward for 'forward' input.")]
    public bool invert;

    // Last commanded input in [-1, 1]; ButtonRouter uses it to skip redundant drive writes.
    public float CurrentInput { get; private set; }

    private bool IsPrismatic => body != null && body.jointType == ArticulationJointType.PrismaticJoint;

    void Awake()
    {
        if (body == null) body = GetComponent<ArticulationBody>();
        if (body == null)
        {
            Debug.LogWarning("MotorActuator: no ArticulationBody assigned or found on this GameObject.", this);
            return;
        }

        // Bake the motor model into the joint's X drive (struct: copy, modify, assign back).
        // The URDF post-processor bakes the same values at edit time; re-baking here keeps the
        // component correct when hand-added to a joint in the editor.
        ArticulationDrive d = body.xDrive;
        d.driveType = ArticulationDriveType.Velocity;
        d.stiffness = 0f;
        d.damping = velocityDriveDamping;
        d.forceLimit = stallTorque;
        body.xDrive = d;

        // maxJointVelocity is rad/s for revolute joints, units/s for prismatic. Cap slightly
        // above the full-input target so the drive can actually reach it.
        body.maxJointVelocity = IsPrismatic
            ? maxLinearSpeed * 1.1f
            : maxRpm * Mathf.PI * 2f / 60f * 1.1f;
    }

    // input in [-1, 1]: +1 full forward, -1 full reverse, 0 brake (velocity target 0 with
    // damping > 0 actively resists motion, like a motor holding against backdrive).
    public void SetInput(float input)
    {
        if (body == null) return;
        CurrentInput = Mathf.Clamp(input, -1f, 1f);
        float sign = invert ? -1f : 1f;
        // Revolute drive targets are in DEGREES per second: rpm x 360/60 = rpm x 6.
        float target = IsPrismatic
            ? CurrentInput * maxLinearSpeed
            : CurrentInput * maxRpm * 6f;
        body.SetDriveTargetVelocity(ArticulationDriveAxis.X, target * sign);
    }

    void OnDisable()
    {
        SetInput(0f);
    }
}
