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
//    compounds on the driver's. No tool authors this mode today (the DR4B builder moved to the
//    transform followers in Dr4bMoveFollower/PivotRotateFollower); it's kept for future linkages.
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
// Usage: added and configured by Tools > RoboSim > Robot > Mechanisms > Build Chain.
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

    [Header("Midpoint flip (Position mode, optional)")]
    [Tooltip("Once the DRIVER swings past the midpoint of its travel, add an extra turn (usually 180) to " +
             "the follower — for a claw on a swing arm that must face the OTHER way on the back half of " +
             "the arc. It rides ON TOP of the normal angle tracking (leveling), and ramps in over Flip " +
             "Seconds so it's a quick snap, not an instant jump.")]
    public bool flipPastMidpoint;
    [Tooltip("How far the extra flip turns the follower once triggered, in degrees.")]
    public float flipDegrees = 180f;
    [Tooltip("Where in the driver's travel the flip fires, as a fraction FROM the driver's rest pose to " +
             "its far end. 0.5 = the exact midpoint of the swing.")]
    [Range(0.05f, 0.95f)] public float flipFraction = 0.5f;
    [Tooltip("Seconds the flip takes to swing in once it fires — the 'quick' part. 0 = an instant jump.")]
    public float flipTravelSeconds = 0.3f;

    // Runtime flip state: the driver's angle at startup (so the swing is measured from where it rests),
    // whether the flip is currently engaged (with a hysteresis band so it doesn't chatter at the
    // threshold), and the ramped-in offset actually applied this step.
    private float restAngleDeg;
    private bool restCaptured;
    private bool flipEngaged;
    private float currentFlipDeg;

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
        float followerMax = driverMax * Mathf.Max(1f, Mathf.Abs(ratio)) * 1.1f;
        // The midpoint flip drives its own quick sweep on top of the tracking, and it can be much faster
        // than the arm — a 180 in 0.3 s is ~10 rad/s, well past a slow arm motor's cap. Lift the ceiling
        // to whichever is faster, or the "quick" flip crawls behind a geared-down arm.
        if (flipPastMidpoint && flipTravelSeconds > 1e-3f)
        {
            float flipRateRad = Mathf.Abs(flipDegrees) * Mathf.Deg2Rad / flipTravelSeconds;
            followerMax = Mathf.Max(followerMax, flipRateRad * 1.2f);
        }
        follower.maxJointVelocity = followerMax;
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
            float driverDeg = p[0] * Mathf.Rad2Deg;
            float targetDeg = driverDeg * ratio + offsetDeg; // rad -> deg
            if (flipPastMidpoint) targetDeg += FlipOffset(driverDeg);
            follower.SetDriveTarget(ArticulationDriveAxis.X, targetDeg);
        }
    }

    // The extra flip offset (degrees) to add on top of the tracked angle. It engages once the driver has
    // swung past `flipFraction` of the way from its rest pose to its far limit, and ramps toward the full
    // flip over `flipTravelSeconds` so a claw snaps through 180 quickly at the top of the arc rather than
    // teleporting in a single frame. A small hysteresis band keeps it from chattering right at the
    // threshold. Public so the edit-mode harness can step it the same way it steps ApplyStep.
    public float FlipOffset(float driverDeg)
    {
        float lo = driver.xDrive.lowerLimit, hi = driver.xDrive.upperLimit;
        if (hi - lo > 1e-3f)
        {
            // Capture the rest angle the first time we get a real (stepped) reading — a revolute's
            // jointPosition can read NaN before the first physics step.
            if (!restCaptured && !float.IsNaN(driverDeg)) { restAngleDeg = driverDeg; restCaptured = true; }
            // Measure travel from rest toward whichever limit is farther, so the fraction runs 0 at rest
            // to 1 fully swung whichever way the arm is set up to move.
            float far = Mathf.Abs(hi - restAngleDeg) >= Mathf.Abs(lo - restAngleDeg) ? hi : lo;
            float span = far - restAngleDeg;
            float frac = Mathf.Abs(span) > 1e-3f ? (driverDeg - restAngleDeg) / span : 0f;
            if (!flipEngaged && frac > flipFraction + 0.03f) flipEngaged = true;
            else if (flipEngaged && frac < flipFraction - 0.03f) flipEngaged = false;
        }
        float goal = flipEngaged ? flipDegrees : 0f;
        float rate = flipTravelSeconds > 1e-3f ? Mathf.Abs(flipDegrees) / flipTravelSeconds : 1e7f;
        currentFlipDeg = Mathf.MoveTowards(currentFlipDeg, goal, rate * Mathf.Max(Time.fixedDeltaTime, 1e-4f));
        return currentFlipDeg;
    }

    void OnDisable()
    {
        // Release like MotorActuator does, so a disabled coupler stops commanding spin.
        if (follower != null && mode == CoupleMode.Velocity)
            follower.SetDriveTargetVelocity(ArticulationDriveAxis.X, 0f);
    }
}
