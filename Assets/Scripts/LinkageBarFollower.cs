using UnityEngine;

// A transform-only follower that makes a plain bar (or tray) track a lift's powered DRIVER joint,
// WITHOUT being an ArticulationBody itself. It reads the driver's joint angle each LateUpdate and
// poses this object relative to its authored rest pose — the same "read a mechanism's motion and fake
// the visual" trick IntakePull uses for the intake stack.
//
// Two uses:
//  • RotateAboutAxis — completes the visible DR4B "scissor": the extra parallel/mirror bars that
//    aren't the one load-bearing coupled follower. Rotating them here (instead of coupling more real
//    joints) gives the full linkage look with no anchor/ratio/limit tuning and, crucially, no stiff
//    drives fighting each other over a double-constrained parallelogram (that jitters). A plain
//    transform is invisible to physics and to IntakePull's self-heal (which only flees real
//    ArticulationBody links).
//  • TranslateAlongWorldAxis — the "articulated cheat" tray: instead of a coupled revolute, the tray
//    is translated straight up as the driver sweeps, so it rises dead-straight and stays perfectly
//    level. Parent this tray under the CHASSIS and the held stack rides it with no LiftCarriage
//    exemption needed (there's no moving ArticulationBody between the anchors and the chassis).
//
// Cosmetic bars should have their colliders disabled/removed — they are pure visuals and must not
// perturb the sim.
//
// Added and configured by Tools > RoboSim > Robot > Mechanisms > Build DR4B Lift.
public class LinkageBarFollower : MonoBehaviour
{
    public enum FollowMode
    {
        RotateAboutAxis,        // cosmetic scissor bars
        TranslateAlongWorldAxis // the articulated-cheat tray
    }

    [Tooltip("The lift's powered driver joint (a revolute ArticulationBody). Its angle drives this follower. Auto-found in parents if empty.")]
    public ArticulationBody driver;

    [Tooltip("Rotate = a cosmetic scissor bar. Translate = the articulated-cheat tray (rises straight, stays level).")]
    public FollowMode mode = FollowMode.RotateAboutAxis;

    [Tooltip("follower : driver. 1 = matches the driver's swept angle; negative mirrors it (opposite side of a DR4B).")]
    public float ratio = 1f;

    [Header("Rotate mode")]
    [Tooltip("Degrees added to the tracked angle — the bar's neutral offset when the driver is at rest.")]
    public float offsetDeg = 0f;
    [Tooltip("Hinge axis in THIS bar's local space (the pin it pivots on).")]
    public Vector3 localPivotAxis = Vector3.right;

    [Header("Translate mode")]
    [Tooltip("Rise direction in WORLD space (usually up).")]
    public Vector3 worldAxis = Vector3.up;
    [Tooltip("World units of lift per radian the driver sweeps. e.g. liftHeight / (sweep in radians).")]
    public float unitsPerRadian = 0f;

    private Quaternion restLocalRot;
    private Vector3 restLocalPos;
    private float driverRestRad;

    void Awake()
    {
        restLocalRot = transform.localRotation;
        restLocalPos = transform.localPosition;
        if (driver == null) driver = GetComponentInParent<ArticulationBody>();
        driverRestRad = DriverRad();
    }

    // The driver's joint angle in radians (revolute jointPosition is radians). 0 before the
    // articulation is built (edit time / first frame), which just leaves the follower at rest.
    private float DriverRad()
    {
        if (driver == null) return 0f;
        ArticulationReducedSpace p = driver.jointPosition;
        return p.dofCount > 0 ? p[0] : 0f;
    }

    // LateUpdate so we read the driver's SETTLED pose after physics, matching how IntakePull's runtime
    // markers repin. The tray path also runs here; the stack glides to the hold point's world position
    // each FixedUpdate, so a one-step visual lag on the tray is imperceptible.
    void LateUpdate()
    {
        if (driver == null) return;
        float delta = DriverRad() - driverRestRad;

        if (mode == FollowMode.RotateAboutAxis)
        {
            Vector3 axis = localPivotAxis.sqrMagnitude > 1e-8f ? localPivotAxis.normalized : Vector3.right;
            float deg = delta * Mathf.Rad2Deg * ratio + offsetDeg;
            transform.localRotation = restLocalRot * Quaternion.AngleAxis(deg, axis);
        }
        else // TranslateAlongWorldAxis
        {
            Vector3 dir = worldAxis.sqrMagnitude > 1e-8f ? worldAxis.normalized : Vector3.up;
            Vector3 rest = transform.parent != null
                ? transform.parent.TransformPoint(restLocalPos)
                : restLocalPos;
            transform.position = rest + dir * (delta * unitsPerRadian * ratio);
        }
    }
}
