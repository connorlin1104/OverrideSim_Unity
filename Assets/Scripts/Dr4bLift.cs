using System.Collections.Generic;
using UnityEngine;

// Controller for a role-based Double-Reverse-Four-Bar (DR4B) VISUAL lift. ONE real revolute joint —
// the driven sprocket (a MotorActuator mechanism, e.g. "lift" on L1/L2) — supplies the angle theta;
// every other part of the linkage is a transform-only follower posed from theta (see Dr4bFollower /
// TranslateUpFollower / PivotRotateFollower). No coupled physics joints, no closed loops, no stiff
// drives — so it cannot destabilize the way real coupled DR4B joints did (a stiff spring on a light
// bar exploded numerically and pinned the CPU). The imported model groups are NEVER reparented; they
// are script-posed in place.
//
// Each LateUpdate (post-physics, so it reads the driver's settled angle — same timing as IntakePull's
// markers) it recomputes once, then applies TRANSLATORS FIRST (moving C-channel, both sprockets,
// connect pivots, tray) then ROTATORS (the support arms) — so an arm rotates about the LIVE, already-
// risen position of its sprocket pivot.
//
// Wired by Tools > RoboSim > Robot > Mechanisms > Build DR4B Lift (roles).
[DisallowMultipleComponent]
public class Dr4bLift : MonoBehaviour
{
    [Tooltip("The driven sprocket's revolute joint — the ONE real motor. theta = its jointPosition (radians).")]
    public ArticulationBody driver;
    [Tooltip("The rigid robot base the linkage is posed relative to. Auto-resolved to the topmost ArticulationBody ancestor.")]
    public Transform chassis;

    [Header("Movement (two additive stages)")]
    // Internal: the driver's travel used to normalize the 0->1 progress. Set by the builder to a fixed
    // value; the user tunes the arm angles + rise instead, so this is hidden from the Inspector.
    [HideInInspector] public float sweepDeg = 70f;
    [Tooltip("STAGE 1 rise: how far the first stage lifts (up) at full sweep, world units (1 unit = 0.1 m). Applies to everything that moves.")]
    public float stage1Rise = 12f;
    [Tooltip("STAGE 1 forward: how far the first stage drifts forward (toward the stationary channel) at full sweep. Signed.")]
    public float stage1Forward = 4f;
    [Tooltip("STAGE 2 rise: the opposing/crane stage, ADDED on top for the second-stage + scoring parts. Up at full sweep.")]
    public float stage2Rise = 8f;
    [Tooltip("STAGE 2 forward: the second stage's forward reach at full sweep (default further forward). Signed — negative pulls it back.")]
    public float stage2Forward = 4f;
    [Tooltip("Move along the chassis's up/forward axes (so it tracks the bot) instead of world axes.")]
    public bool riseAlongChassisUp = true;

    [Header("Hinge axis")]
    [Tooltip("Derive the arm hinge axis from the drivetrain (right-vs-left wheels) at startup.")]
    public bool autoLateralAxis = true;
    [Tooltip("Arm hinge axis in the CHASSIS's local frame (baked from the drivetrain when Auto is on), so it stays correct as the bot turns.")]
    public Vector3 lateralAxisChassisLocal = Vector3.right;

    [Header("Followers (wired by the DR4B builder)")]
    public List<Dr4bMoveFollower> translators = new List<Dr4bMoveFollower>();
    public List<PivotRotateFollower> rotators = new List<PivotRotateFollower>();

    // Per-frame outputs (recomputed once per frame; read by the followers).
    public float DeltaRad { get; private set; }
    public float ArmAngleDeg { get; private set; }
    public float Progress { get; private set; }        // 0 at rest -> 1 at full lift; scales arm angles + movement
    public Vector3 Stage1Move { get; private set; }   // up + forward, first stage
    public Vector3 Stage2Move { get; private set; }   // added on top for second-stage + scoring parts
    public Vector3 AxisWorld { get; private set; }
    public Transform Chassis => chassis;

    private float restRad;
    private int lastFrame = -1;

    void Awake()
    {
        if (chassis == null) chassis = ResolveChassis();
        restRad = DriverRad();
        if (autoLateralAxis && chassis != null)
        {
            Vector3 lat = DrivetrainLateralWorld();
            if (lat.sqrMagnitude > 1e-6f) lateralAxisChassisLocal = chassis.InverseTransformDirection(lat).normalized;
        }
        // The controller drives capture (Awake order between components is undefined, so don't let the
        // followers capture themselves too early — they'd miss the chassis or capture a mid-spawn pose).
        foreach (Dr4bMoveFollower t in translators) if (t != null) t.CaptureRest(chassis);
        foreach (PivotRotateFollower r in rotators) if (r != null) r.CaptureRest(chassis);
    }

    private float DriverRad()
    {
        if (driver == null) return 0f;
        ArticulationReducedSpace p = driver.jointPosition;
        return p.dofCount > 0 ? p[0] : 0f;   // radians
    }

    private void Recompute()
    {
        if (lastFrame == Time.frameCount) return;
        lastFrame = Time.frameCount;
        DeltaRad = DriverRad() - restRad;
        ArmAngleDeg = DeltaRad * Mathf.Rad2Deg;
        float sweepRad = Mathf.Max(1e-4f, Mathf.Abs(sweepDeg) * Mathf.Deg2Rad);
        Vector3 up = (riseAlongChassisUp && chassis != null) ? chassis.up : Vector3.up;
        Vector3 fwd = chassis != null ? chassis.forward : Vector3.forward;
        float norm = DeltaRad / sweepRad;   // 0 at rest, 1 at full sweep
        Progress = Mathf.Clamp01(norm);     // arms + movement scale by this
        Stage1Move = (up * stage1Rise + fwd * stage1Forward) * norm;   // first stage: up + forward
        Stage2Move = (up * stage2Rise + fwd * stage2Forward) * norm;   // second stage: added for the subset
        Vector3 axis = chassis != null ? chassis.TransformDirection(lateralAxisChassisLocal) : lateralAxisChassisLocal;
        AxisWorld = axis.sqrMagnitude > 1e-8f ? axis.normalized : Vector3.right;
    }

    void LateUpdate()
    {
        if (chassis == null || driver == null) return;
        Recompute();
        foreach (Dr4bMoveFollower t in translators) if (t != null) t.Apply(this);   // sprockets/channels/tray FIRST
        foreach (PivotRotateFollower r in rotators) if (r != null) r.Apply(this);    // arms read the moved pivots
    }

    // The robot's rigid base — same rule as IntakePull.ResolveStableChassis (topmost AB, else the
    // RobotMechanisms holder, else the hierarchy root).
    private Transform ResolveChassis()
    {
        ArticulationBody top = null;
        foreach (ArticulationBody ab in GetComponentsInParent<ArticulationBody>(true)) top = ab;
        if (top != null) return top.transform;
        RobotMechanisms rm = GetComponentInParent<RobotMechanisms>();
        if (rm != null) return rm.transform;
        return transform.root;
    }

    private Vector3 DrivetrainLateralWorld()
    {
        RobotMotorController mc = chassis != null ? chassis.GetComponentInChildren<RobotMotorController>(true) : null;
        if (mc == null) mc = GetComponentInParent<RobotMotorController>();
        if (mc == null) return chassis != null ? chassis.right : Vector3.right;
        Vector3 lat = Centroid(mc.rightWheels) - Centroid(mc.leftWheels);
        return lat.sqrMagnitude > 1e-6f ? lat : (chassis != null ? chassis.right : Vector3.right);
    }

    private static Vector3 Centroid(ArticulationBody[] arr)
    {
        if (arr == null) return Vector3.zero;
        Vector3 s = Vector3.zero; int n = 0;
        foreach (ArticulationBody a in arr) if (a != null) { s += a.transform.position; n++; }
        return n > 0 ? s / n : Vector3.zero;
    }
}
