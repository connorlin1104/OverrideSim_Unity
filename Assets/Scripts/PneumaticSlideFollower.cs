using UnityEngine;

// Cosmetic driver for a SLIDE-ONLY pneumatic cylinder part — the claw's flip and clamp cylinders.
//
// The real motion lives elsewhere: a driven revolute ArticulationBody (the flipping assembly, or the
// first clamp half) swings under its PneumaticActuator. This part has NO joint of its own — it just
// slides along a fixed centerline each frame by an amount proportional to that joint's progress, so
// the mechanism reads as "the cylinder fired and the claw moved" instead of the claw swinging past a
// frozen cylinder.
//
// WHY BOTH HALVES OF THE CYLINDER MOVE: most claws don't bolt the barrel down — firing pushes the rod
// one way and kicks the body the other, so the assembly's center of mass stays roughly put. That's
// modelled by giving the two parts SIGNED travel along the same axis:
//
//     rod.slideUnits  = +(1 - recoil) * stroke
//     body.slideUnits = -recoil       * stroke
//
// recoil 0 = barrel bolted down (only the rod moves), 0.5 = balanced (the cylinder's midpoint holds
// still), 1 = the barrel does all the moving. The two are always stroke apart, so the rod extends by
// the real cylinder travel however the recoil is split.
//
// Everything is expressed in the part's PARENT frame, never world — that is what lets a clamp
// cylinder parented under the flipping link ride the 180° flip with no extra bookkeeping.
//
// Contrast with PneumaticCylinderFollower: that one is the doinker's model, where the cylinder
// swivels about a chassis mount and re-aims at a clevis on the moving metal. This one never rotates.
//
// Usage: added and configured by Tools > RoboSim > Robot > Mechanisms > Build Claw (roles).
[DefaultExecutionOrder(100)] // after the physics step has moved the driven joint this frame
public class PneumaticSlideFollower : MonoBehaviour
{
    [Tooltip("Unit slide direction in this part's PARENT frame — the cylinder's centerline, baked at build time.")]
    public Vector3 slideAxisLocal = Vector3.right;
    [Tooltip("SIGNED distance this part travels at full actuation, in world units. Positive slides along the " +
             "axis (the rod extending); negative slides against it (the body recoiling).")]
    public float slideUnits;

    [Header("Progress source")]
    [Tooltip("The driven joint whose angle says how far the cylinder has fired.")]
    public ArticulationBody progressBody;
    [Tooltip("Joint angle (radians) at retracted / extended — progress = (angle - low) / (high - low).")]
    public float progressLowRad;
    public float progressHighRad;

    private bool captured;
    private Vector3 restLocalPos;

    void Start() => Capture();

    // Snapshots the authored (retracted) pose this part slides away from. Public so a headless
    // edit-mode harness — which never runs Start — can prime it before stepping Physics.Simulate.
    public void Capture()
    {
        restLocalPos = transform.localPosition;
        captured = true;
    }

    void LateUpdate()
    {
        if (!captured) Capture();
        Vector3 axis = slideAxisLocal.sqrMagnitude > 1e-10f ? slideAxisLocal.normalized : Vector3.right;
        transform.localPosition = restLocalPos + axis * (slideUnits * Progress01());
    }

    // 0 at the retracted angle, 1 at the extended one. An ArticulationBody's jointPosition is in
    // RADIANS while its drive target/limits are in degrees (the Unity quirk this whole codebase
    // converts at every boundary), and the reduced space it reports is empty/NaN until PhysX has
    // stepped the joint at least once — both guarded here.
    public float Progress01()
    {
        if (progressBody == null) return 0f;
        ArticulationReducedSpace jp = progressBody.jointPosition;
        if (jp.dofCount == 0) return 0f;
        float a = jp[0];
        float range = progressHighRad - progressLowRad;
        if (float.IsNaN(a) || Mathf.Abs(range) < 1e-6f) return 0f;
        return Mathf.Clamp01((a - progressLowRad) / range);
    }
}
