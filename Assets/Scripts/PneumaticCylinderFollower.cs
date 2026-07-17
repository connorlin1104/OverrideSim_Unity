using UnityEngine;

// Cosmetic driver for a rotary (piston-driven) pivot mechanism — the "doinker / flipper" cylinder.
//
// The MOVING METAL is the real revolute joint (an ArticulationBody built by the pneumatic tool); it
// swings about its own hinge. This cylinder has NO joint of its own — it is chassis geometry that just
// re-aims each frame so it keeps pointing at a connection point rigidly attached to the moving metal.
// That makes the mechanism read as "the cylinder pushes the arm" instead of the arm swinging past a
// frozen cylinder.
//
// Two pivots, exactly as the real linkage has them:
//   • mount      — where the cylinder is pinned to the chassis (it swivels about this point).
//   • connection — the clevis on the moving metal the rod pushes (a child of the arm, so it rides it).
//
// The swivel is pure geometry (a look-at from mount toward connection), so the aim matches the metal's
// swing with zero tuning. On the ROD (extend=true) the part also slides out along that aim by the chosen
// cylinder stroke (maxExtendUnits), scaled by the metal's swing progress — so "how far the rod extends"
// is the real cylinder size, not a guess. The BODY (extend=false) only swivels.
[DefaultExecutionOrder(100)] // after the physics step has moved the arm this frame
public class PneumaticCylinderFollower : MonoBehaviour
{
    [Tooltip("Where the cylinder is pinned to the chassis (its mount). This part swivels about this point.")]
    public Transform mount;
    [Tooltip("A point rigidly attached to the moving metal (a child of the arm) — the clevis the rod pushes. " +
             "This part always aims here, so it follows the arm as it swings.")]
    public Transform connection;
    [Tooltip("ROD: also slide along the aim axis as the metal swings (the piston visibly extends and retracts). " +
             "OFF = BODY: swivel about the mount only.")]
    public bool extend;
    [Tooltip("ROD: flip which way the rod slides. Some CAD models the solid rod tube on the mount side, so the " +
             "default 'slide outward' reads backwards (fired looks retracted). Tick this to invert it.")]
    public bool flipExtend;

    [Header("Rod travel (rod only)")]
    [Tooltip("Rod's slide distance at full swing, in world units (the chosen cylinder stroke). 0 = fall back to " +
             "geometry (slide by however far the connection moved).")]
    public float maxExtendUnits;
    [Tooltip("The metal's driven joint — its angle gives the swing progress that scales the rod's slide.")]
    public ArticulationBody progressBody;
    [Tooltip("Metal joint angle (radians) at retracted / extended — progress = (angle-low)/(high-low).")]
    public float progressLowRad;
    public float progressHighRad;

    private bool captured;
    private Vector3 restDirLocalMount;    // rest mount->connection direction, in the mount's frame
    private Vector3 restPosLocalMount;    // cylinder rest position, in the mount's frame
    private Quaternion restRotLocalMount; // cylinder rest rotation, in the mount's frame
    private float restLen;                // rest mount->connection distance (rod slide reference)

    void Start() => Capture();

    private void Capture()
    {
        if (mount == null || connection == null) return;
        Vector3 dir = connection.position - mount.position;
        if (dir.sqrMagnitude < 1e-10f) return;
        restDirLocalMount = mount.InverseTransformDirection(dir);
        restPosLocalMount = mount.InverseTransformPoint(transform.position);
        restRotLocalMount = Quaternion.Inverse(mount.rotation) * transform.rotation;
        restLen = dir.magnitude;
        captured = true;
    }

    void LateUpdate()
    {
        if (!captured) { Capture(); if (!captured) return; }
        if (mount == null || connection == null) return;

        Vector3 restDirW = mount.TransformDirection(restDirLocalMount);
        Vector3 liveDirW = connection.position - mount.position;
        if (restDirW.sqrMagnitude < 1e-10f || liveDirW.sqrMagnitude < 1e-10f) return;

        // Swivel about the mount so the axis re-aims from the rest direction to the live one.
        Quaternion delta = Quaternion.FromToRotation(restDirW, liveDirW);
        Vector3 basePosW = mount.TransformPoint(restPosLocalMount);
        Quaternion baseRotW = mount.rotation * restRotLocalMount;
        Vector3 pos = mount.position + delta * (basePosW - mount.position);

        // ROD: slide along the aim axis so it extends as the metal swings. Prefer the real cylinder stroke
        // (maxExtendUnits) scaled by swing progress — that's the "how far the rod extends" the size picker
        // sets. With no stroke/joint, fall back to geometry: slide by however far the connection moved.
        if (extend)
        {
            float liveLen = liveDirW.magnitude;
            float slide = (maxExtendUnits > 0f && progressBody != null)
                ? maxExtendUnits * SwingProgress()
                : liveLen - restLen;
            if (flipExtend) slide = -slide;
            pos += slide * (liveDirW / liveLen);
        }

        transform.SetPositionAndRotation(pos, delta * baseRotW);
    }

    // 0 at the retracted angle, 1 at the extended angle. The metal's ArticulationBody jointPosition is in
    // radians (Unity quirk: drive targets/limits are degrees, jointPosition is radians). Guards the
    // NaN/empty reduced-space the joint reports before PhysX first steps it.
    private float SwingProgress()
    {
        ArticulationReducedSpace jp = progressBody.jointPosition;
        if (jp.dofCount == 0) return 0f;
        float a = jp[0];
        float range = progressHighRad - progressLowRad;
        if (float.IsNaN(a) || Mathf.Abs(range) < 1e-6f) return 0f;
        return Mathf.Clamp01((a - progressLowRad) / range);
    }
}
