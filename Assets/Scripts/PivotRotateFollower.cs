using System.Collections.Generic;
using UnityEngine;

// A DR4B support arm that ROTATES about its connection to an assigned pivot (a sprocket / stationary
// connection point) as the lift raises, and STAYS attached to that connection.
//
// The rotation center is the HINGE = the point on the arm nearest the pivot (captured at rest). Because
// the hinge is a point on the arm, the arm pivots about it and its connection end stays put — it can't
// drift off the pivot the way rotating about a distant CAD origin or an off-center bounds does. If the
// pivot itself moves (a rising second sprocket), the hinge tracks that movement so the arm rides with it.
//
// PARALLELOGRAM 4-BARS (rotateBarsIndividually): a DR4B side isn't one bar — it's two parallel bars (an
// upper and a lower/support bar) whose pins are at DIFFERENT points. Rotating the whole group rigidly
// about one hinge keeps only the bar whose base sits at that hinge attached; the other bar's base swings
// off its connection. So when this group holds a small set of separate bars, each bar is instead rotated
// by the SAME angle about ITS OWN base (its own end nearest the pivot). The bars stay parallel and every
// bar keeps its own connection — the true four-bar motion. A big head/scoring assembly (many mixed parts,
// more than maxBars) is NOT a linkage, so it falls back to rotating rigidly as one.
//
// Per-frame pose (rotate a bar about its own moved hinge h):
//   q      = AngleAxis(armAngleDeg * controller.Progress + offsetDeg, axis)
//   h      = hingeRest + (pivotLive - pivotRest)          // connection point, carried by the pivot's move
//   newPos = h + q * (RestPosW - hingeRest)
//   newRot = q * RestRotW
//
// armAngleDeg is how far the arms rotate at full lift; the two stages get opposite signs (the "reverse").
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

    [Header("Parallelogram (4-bar) mode")]
    [Tooltip("This group is a set of parallel bars (a DR4B side has an upper + a lower/support bar with DIFFERENT pins): " +
             "rotate EACH bar about ITS OWN base by the same angle, so the support bars keep their own connection instead of " +
             "swinging off. Auto-falls back to rotating the whole group as one if it can't find a small set of bars (a big head " +
             "assembly with more mesh parts than Max Bars).")]
    public bool rotateBarsIndividually = true;
    [Tooltip("Most sub-bars to split a group into for per-bar mode. A group with more separate mesh parts than this is treated " +
             "as one rigid body (a scoring/head assembly, not a 4-bar linkage), not split.")]
    public int maxBars = 8;

    private Vector3 hingeRestChassisPos;   // the connection point (arm end nearest the pivot), at rest — whole-group mode
    private Vector3 pivotRestChassisPos;    // the pivot's location, at rest
    private bool pivotCaptured;

    // One bar of a parallelogram side, captured at rest in the chassis frame (per-bar mode).
    private class BarRest { public Transform tf; public Vector3 posC; public Quaternion rotC; public Vector3 hingeC; }
    private List<BarRest> bars;   // null => whole-group (single rigid) mode

    public override void CaptureRest(Transform chassisT)
    {
        base.CaptureRest(chassisT);
        if (chassis == null) return;
        Vector3 pivotLoc = PivotLocationWorld();
        pivotRestChassisPos = chassis.InverseTransformPoint(pivotLoc);
        pivotCaptured = true;

        // Try to split into individual bars. Only do it for a small set (a real linkage); a big head
        // assembly stays one rigid body.
        bars = null;
        if (rotateBarsIndividually)
        {
            List<Transform> barTfs = new List<Transform>();
            CollectBars(transform, barTfs);
            if (barTfs.Count >= 2 && barTfs.Count <= Mathf.Max(2, maxBars))
            {
                bars = new List<BarRest>(barTfs.Count);
                foreach (Transform bt in barTfs)
                    bars.Add(new BarRest
                    {
                        tf = bt,
                        posC = chassis.InverseTransformPoint(bt.position),
                        rotC = Quaternion.Inverse(chassis.rotation) * bt.rotation,
                        hingeC = chassis.InverseTransformPoint(ArmHingeWorld(bt, pivotLoc)),
                    });
            }
        }

        // Whole-group hinge (used when bars == null).
        hingeRestChassisPos = chassis.InverseTransformPoint(ArmHingeWorld(transform, pivotLoc));
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
        Vector3 pivotMove = PivotLocationWorld() - chassis.TransformPoint(pivotRestChassisPos);

        if (bars != null)
        {
            // Per-bar: same angle q, but each bar rotates about ITS OWN base — the parallelogram stays closed.
            foreach (BarRest b in bars)
            {
                if (b.tf == null) continue;
                Vector3 restPosW = chassis.TransformPoint(b.posC);
                Quaternion restRotW = chassis.rotation * b.rotC;
                Vector3 hingeRestW = chassis.TransformPoint(b.hingeC);
                Vector3 hingeLive = hingeRestW + pivotMove;
                b.tf.SetPositionAndRotation(hingeLive + q * (restPosW - hingeRestW), q * restRotW);
            }
            return;
        }

        // Whole group rotates rigidly about one hinge.
        Vector3 gHingeRestW = chassis.TransformPoint(hingeRestChassisPos);
        Vector3 gHingeLive = gHingeRestW + pivotMove;
        transform.SetPositionAndRotation(gHingeLive + q * (RestPosW - gHingeRestW), q * RestRotW);
    }

    // The topmost sub-transforms that each hold exactly ONE mesh — the individual bars of the assembly.
    // Descends through grouping nodes (an assembly of several bars) but stops AT each single-mesh bar so
    // the whole bar (mesh + any inserts under it) rotates as one rigid piece.
    private static void CollectBars(Transform t, List<Transform> acc)
    {
        int meshes = t.GetComponentsInChildren<MeshFilter>(true).Length;
        if (meshes == 0) return;
        if (meshes == 1 || t.childCount == 0) { acc.Add(t); return; }
        foreach (Transform c in t) CollectBars(c, acc);
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

    // The hinge = the point on ARM's geometry nearest the pivot (its connection end). Falls back to the
    // pivot location if the arm has no renderers.
    private static Vector3 ArmHingeWorld(Transform arm, Vector3 pivotLoc)
    {
        Renderer[] rs = arm.GetComponentsInChildren<Renderer>(true);
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
