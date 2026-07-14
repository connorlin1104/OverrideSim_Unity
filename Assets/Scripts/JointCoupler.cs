using UnityEngine;

// Slaves a FOLLOWER revolute ArticulationBody joint to a DRIVER joint's motion, so parts that are
// linked by chain/gear/sprocket in real life move together in sim — the missing primitive behind
// chained rollers/intakes and a double-reverse-four-bar (DR4B) lift. One motor (a MotorActuator on
// the driver) is the only thing a button drives; every follower tracks it through this component.
//
// Two modes:
//  - Velocity ("spin together" — chained rollers, intakes): the follower matches the driver's spin
//    RATE times the ratio. Baked as a torque-limited velocity drive (like MotorActuator). Use on a
//    Continuous (free-spinning) follower so it never hits a travel limit.
//  - Position ("linkage" — a DR4B, geared arm pairs): the follower tracks the driver's ANGLE times
//    the ratio (plus an offset). Baked as a stiff position-target drive (like PneumaticActuator).
//    For a DR4B the follower must sit BELOW the driver in the articulation tree so its rotation
//    compounds on the driver's — the Link Coupled Joints tool warns when it doesn't.
//
// Units: an ArticulationBody's jointPosition/jointVelocity/maxJointVelocity are radians (and rad/s);
// a revolute drive's target/limits are DEGREES (and deg/s) — same convention MotorActuator and
// RobotMotorController use. We convert with Rad2Deg at every boundary.
//
// The per-step update runs in FixedUpdate at play time. Like MotorActuator it never touches the
// joint anchors — the authoring tool's ConfigureJointLink already encoded the axis in anchorRotation.
// A coupled follower is a passive linkage: it carries NO MotorActuator/PneumaticActuator and is NOT
// in RobotMechanisms, so ButtonRouter never fights it for the drive.
//
// Usage: added and configured by Tools > RoboSim > Robot > Mechanisms > Link Coupled Joints.
[DisallowMultipleComponent]
public class JointCoupler : MonoBehaviour
{
    public enum CoupleMode
    {
        Velocity, // follower spins at driver's rate x ratio (chained rollers)
        Position, // follower tracks driver's angle x ratio + offset (DR4B linkage)
    }

    [Tooltip("The joint this coupler drives. Defaults to the ArticulationBody on this GameObject.")]
    public ArticulationBody follower;
    [Tooltip("The joint whose motion is copied — the powered driver (or another follower).")]
    public ArticulationBody driver;

    [Tooltip("Velocity = spin together (rollers). Position = track angle as a linkage (DR4B).")]
    public CoupleMode mode = CoupleMode.Velocity;
    [Tooltip("follower : driver. 1 = same speed/angle; 0.5 = half; a negative value reverses (mirror).")]
    public float ratio = 1f;
    [Tooltip("Position mode only: degrees added to the tracked angle (the linkage's neutral offset).")]
    public float offsetDeg = 0f;

    [Header("Drive Settings")]
    [Tooltip("Velocity mode: velocity-tracking gain (damping). MUST be > 0 or the drive makes no torque.")]
    public float velocityDriveDamping = 1000f;
    [Tooltip("Position mode: position spring gain — high, so the linkage holds its commanded angle.")]
    public float positionStiffness = 20000f;
    [Tooltip("Position mode: velocity damping — enough to kill ringing without feeling sluggish.")]
    public float positionDamping = 500f;
    [Tooltip("Drive force limit — the follower's stall torque, so a jammed linkage stalls like real hardware.")]
    public float forceLimit = 700f;
    [Tooltip("rad/s cap used when the driver's own maxJointVelocity is unknown (0).")]
    public float maxFollowerJointVelocity = 40f;

    void Awake()
    {
        if (follower == null) follower = GetComponent<ArticulationBody>();
        if (follower == null || follower.jointType != ArticulationJointType.RevoluteJoint)
        {
            Debug.LogWarning("JointCoupler: follower must be a revolute ArticulationBody.", this);
            return;
        }
        if (driver == null)
        {
            Debug.LogWarning("JointCoupler: no driver assigned — the follower won't move.", this);
            return;
        }
        BakeDrive();
    }

    // Bakes the follower's xDrive for the current mode. Public so the authoring tool can bake the
    // SAME drive at edit time (edit-mode Physics.Simulate validation never runs Awake), keeping edit
    // and play behavior identical — the same reason MotorActuator/PneumaticActuator expose their bake.
    public void BakeDrive()
    {
        if (follower == null) return;

        // xDrive is a struct: copy, modify, assign back — and this preserves the lower/upper limits
        // the authoring tool set (Position mode relies on them spanning the commanded arc).
        ArticulationDrive d = follower.xDrive;
        if (mode == CoupleMode.Velocity)
        {
            d.driveType = ArticulationDriveType.Velocity;
            d.stiffness = 0f;
            d.damping = velocityDriveDamping;
            d.forceLimit = forceLimit;
        }
        else
        {
            d.driveType = ArticulationDriveType.Target;
            d.stiffness = positionStiffness;
            d.damping = positionDamping;
            d.forceLimit = forceLimit;
        }
        follower.xDrive = d;

        // maxJointVelocity is rad/s; the default clamps a follower below a fast driver. Clear the
        // driver's cap times the ratio so a spun-up follower can actually keep pace.
        float driverMax = driver != null && driver.maxJointVelocity > 0f
            ? driver.maxJointVelocity : maxFollowerJointVelocity;
        follower.maxJointVelocity = driverMax * Mathf.Max(1f, Mathf.Abs(ratio)) * 1.1f;
    }

    void FixedUpdate() => ApplyStep();

    // One coupling step: copy the driver's motion onto the follower's drive. Public so a headless
    // edit-mode harness (Physics.Simulate, which never calls FixedUpdate) can step couplers manually,
    // the same way PhysicsSmokeTest drives wheel drives directly.
    public void ApplyStep()
    {
        if (driver == null || follower == null) return;

        if (mode == CoupleMode.Velocity)
        {
            ArticulationReducedSpace v = driver.jointVelocity;
            if (v.dofCount == 0) return;                       // driver has no DOF (e.g. welded)
            float degPerSec = v[0] * Mathf.Rad2Deg * ratio;    // rad/s -> deg/s
            follower.SetDriveTargetVelocity(ArticulationDriveAxis.X, degPerSec);
        }
        else
        {
            ArticulationReducedSpace p = driver.jointPosition;
            if (p.dofCount == 0) return;
            float targetDeg = p[0] * Mathf.Rad2Deg * ratio + offsetDeg; // rad -> deg
            follower.SetDriveTarget(ArticulationDriveAxis.X, targetDeg);
        }
    }

    void OnDisable()
    {
        // Release like MotorActuator does, so a disabled coupler stops commanding spin.
        if (follower != null && mode == CoupleMode.Velocity)
            follower.SetDriveTargetVelocity(ArticulationDriveAxis.X, 0f);
    }
}
