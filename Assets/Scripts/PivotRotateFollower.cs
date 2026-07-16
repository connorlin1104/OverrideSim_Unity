using UnityEngine;

// A DR4B support arm that ROTATES about its connection to an assigned pivot (a sprocket / stationary
// connection point) as the lift raises, and STAYS attached to that connection.
//
// The rotation center is the HINGE = the point on THIS arm nearest the pivot (captured at rest). Because
// the hinge is a point on the arm, the arm pivots about it and its connection end stays put — it can't
// drift off the pivot the way rotating about a distant CAD origin or an off-center bounds does. If the
// pivot itself moves (a rising second sprocket), the hinge tracks that movement so the arm rides with it.
//
// Per-frame pose (rotate about the moved hinge h):
//   q      = AngleAxis(armAngleDeg * controller.Progress + offsetDeg, axis)
//   h      = hingeRest + (pivotLive - pivotRest)          // connection point, carried by the pivot's move
//   newPos = h + q * (RestPosW - hingeRest)
//   newRot = q * RestRotW
//
// armAngleDeg is how far this arm rotates at full lift; the two stages get opposite signs (the "reverse").
// Flip the sign if a side swings the wrong way.
public class PivotRotateFollower : Dr4bFollower
{
    [Tooltip("The sprocket / connection point this arm pivots about. May itself be a moving follower.")]
    public Transform pivot;
    [Tooltip("How many DEGREES this arm rotates at full lift. Signed — negative swings the other way.")]
    public float armAngleDeg = 40f;
    [Tooltip("Degrees added at rest — the arm's neutral offset.")]
    public float offsetDeg = 0f;
    [Tooltip("Locate the pivot at its VISUAL center (renderer bounds) instead of its transform origin — for CAD parts whose origin is off the mesh. Empties (no renderer) always use their transform position.")]
    public bool pivotAtVisualCenter = true;
    [Tooltip("Use the controller's drivetrain-derived lateral axis (recommended). Off = use the local axis below on the pivot.")]
    public bool useControllerAxis = true;
    [Tooltip("Hinge axis in the PIVOT's local frame, used only when 'Use Controller Axis' is off.")]
    public Vector3 localAxisOnPivot = Vector3.right;

    private Vector3 hingeRestChassisPos;   // the connection point (arm end nearest the pivot), at rest
    private Vector3 pivotRestChassisPos;    // the pivot's location, at rest
    private bool pivotCaptured;

    public override void CaptureRest(Transform chassisT)
    {
        base.CaptureRest(chassisT);
        if (chassis == null) return;
        Vector3 pivotLoc = PivotLocationWorld();
        Vector3 hinge = HingeWorld(pivotLoc);
        hingeRestChassisPos = chassis.InverseTransformPoint(hinge);
        pivotRestChassisPos = chassis.InverseTransformPoint(pivotLoc);
        pivotCaptured = true;
    }

    public override void Apply(Dr4bLift lift)
    {
        if (lift == null) return;
        if (!captured || !pivotCaptured) CaptureRest(lift.Chassis);
        if (!captured) return;

        Vector3 axis = useControllerAxis
            ? lift.AxisWorld
            : (pivot != null ? pivot.TransformDirection(localAxisOnPivot).normalized : localAxisOnPivot.normalized);
        if (axis.sqrMagnitude < 1e-8f) axis = Vector3.right;

        Quaternion q = Quaternion.AngleAxis(armAngleDeg * lift.Progress + offsetDeg, axis);
        Vector3 hingeRestW = chassis.TransformPoint(hingeRestChassisPos);
        Vector3 pivotMove = PivotLocationWorld() - chassis.TransformPoint(pivotRestChassisPos);
        Vector3 hingeLive = hingeRestW + pivotMove;                 // the connection, carried by the pivot
        transform.SetPositionAndRotation(hingeLive + q * (RestPosW - hingeRestW), q * RestRotW);
    }

    // The pivot's location: its mesh bounds center (the visible pivot) if it has renderers, else its
    // transform position. Used to find the arm's hinge end and to track the pivot's movement.
    private Vector3 PivotLocationWorld()
    {
        if (pivot == null)
            return chassis != null ? chassis.TransformPoint(pivotRestChassisPos) : transform.position;
        if (pivotAtVisualCenter && TryBoundsCenter(pivot, out Vector3 c)) return c;
        return pivot.position;
    }

    // The hinge = the point on THIS arm's geometry nearest the pivot (its connection end). Falls back
    // to the pivot location if the arm has no renderers.
    private Vector3 HingeWorld(Vector3 pivotLoc)
    {
        Renderer[] rs = GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0) return pivotLoc;
        Bounds b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return b.ClosestPoint(pivotLoc);
    }

    private static bool TryBoundsCenter(Transform t, out Vector3 center)
    {
        center = Vector3.zero;
        Renderer[] rs = t.GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0) return false;
        Bounds b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        center = b.center;
        return true;
    }
}
