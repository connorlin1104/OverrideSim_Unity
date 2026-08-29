using UnityEngine;

// Latching detent for the field rollers: pulls the roller onto the nearest of its 3 colour faces
// (120 deg apart) and HOLDS it there, the way the real roller's spring hook holds a face. Two
// regimes, and the detent is engaged in both — there is no speed at which it lets go:
//   - within latchAngleDeg of a face the per-step correction cap is holdCorrectionPerStep. This is
//     the HOOK: a robot's roller mechanism has to break it at every face, and a roller left off a
//     face is dragged back under it.
//   - outside that window the cap is maxCorrectionPerStep, the between-face pull. It fights the
//     robot on the way off one face and helps it onto the next, symmetrically, like a cam detent.
// The previous model disengaged entirely above a release speed (4 rad/s) and damped the roller down
// to it before the detent did anything: a roller spun at 15 rad/s coasted ~525 deg on damping alone
// and then landed on whatever face it happened to be nearest. Rollers are not supposed to coast.
//
// The correction is velocity-TRACKING, not a raw torque: each step it moves the roller's spin rate
// toward a target rate proportional to the remaining angle error (clamped), so it can't wind up and
// oscillate the way an undamped angle-proportional torque does. hinge.angle (the joint's own 1D
// tracker) is used for the error to avoid the 3D Euler flipping bugs; hinge.velocity would drift from
// Dot(angularVelocity, axis) only if the frame moved, and these rollers are fixed to the field.
//
// SCENE = CODE. These fields are serialized, and a scene copy wins over the C# default — the scene
// once sat at maxCorrectionPerStep 0.2 while the code said 0.35, and nothing said why. Retune here,
// then run Tools > RoboSim > Field & Pieces > Attach or Tune Roller Detents (it copies every field
// onto the 4 scene rollers) and Build Lite Field Scene; FieldFeatureValidation and the Lite build's
// verify both fail if a scene roller disagrees with this file.
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(HingeJoint))]
public class RollerSnap : MonoBehaviour
{
    // The roller has three colour faces, so the stops are a third of a turn apart.
    public const float FaceSpacingDeg = 120f;

    [Header("Detent Configuration")]
    [Tooltip("How aggressively the roller seeks the nearest face: angle error (deg) is turned into a target spin rate at this gain (rad/s per rad of error). The e-folding time of the approach is 1/this: at 10, a 30 deg error is under 2 deg about 0.3 s later.")]
    [SerializeField] private float snapStrength = 10f;
    [Tooltip("Cap on the detent's seek speed (rad/s) so a face is approached at a controlled rate instead of whipping around. At 6 a released roller crosses the 60 deg to the next face in about a fifth of a second.")]
    [SerializeField] private float maxSnapSpeed = 6f;
    [Tooltip("The BETWEEN-FACE pull: the most spin-rate correction applied per physics step (rad/s) while the roller is more than Latch Angle away from a face. This is the number a driver feels: a bump kicks the roller to about (speed / corner radius) rad/s, and this pull decides whether that carries it over the 60 deg midpoint to the next face or drags it back. MEASURED (FieldFeatureValidation's speed table, a 7 kg bumper on the North roller's top corner) at 0.6: 1 and 2 u/s refused, 3, 4 and 6 u/s click exactly one face and stop there. Lower it and nudges start to click; raise it and only hard hits do.")]
    [SerializeField] private float maxCorrectionPerStep = 0.6f;
    [Tooltip("Half-width (deg) of the window around each face inside which the detent uses Hold Correction instead of Max Correction — the reach of the hook.")]
    [SerializeField] private float latchAngleDeg = 8f;
    [Tooltip("THE HOOK: the most spin-rate correction applied per physics step (rad/s) while the roller is within Latch Angle of a face — how firmly a face is held against a steady lean, and how hard a roller arriving on a face is stopped. As a torque it is I * this / dt: the North roller is mass 1 with its tensor from three 6 x 0.04 x 0.5 panel boxes 0.15 off the axle, so I ~ 0.044 and 3.0 is ~13 units of torque. MEASURED at 3.0 (FieldFeatureValidation): teleported 30 deg off a face it is back within 2 deg in 0.30 s; flicked to 15 rad/s it lands on a face after 72 deg in 0.49 s; a 2 kg ball dropped on it rolls it exactly two faces and stops. Note a wheel pressing a flat face from a fixed direction cannot spin this three-faced roller at ANY setting (it pins ~14 deg past the face, the off-centre normal force restoring it faster than friction turns it) — robots click it over by bumping a corner, which Max Correction governs.")]
    [SerializeField] private float holdCorrectionPerStep = 3f;
    [Tooltip("Rotates all 3 detent stops (deg) so they line up with the color faces. 0 = the pose the roller was authored in counts as a face; nudge per roller if a face sits slightly off at rest.")]
    [SerializeField] private float angleOffsetDeg = 0f;
    [Tooltip("Written to the Rigidbody's angular damping at Start. With no release speed there is nothing to decay below any more; this is what stops a roller flicked hard from carrying momentum through the between-face pull and skipping faces.")]
    [SerializeField] private float freeSpinDamping = 3f;

    // For the editor pass, which bakes the damping onto the Rigidbody at attach time so the value is
    // live even before Start runs (e.g. in edit-mode Physics.Simulate, where Start never fires).
    public float FreeSpinDamping => freeSpinDamping;

    // For the validator, which measures "off a face" against the same stops the detent uses.
    public float AngleOffsetDeg => angleOffsetDeg;

    private Rigidbody rb;
    private HingeJoint hinge;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        hinge = GetComponent<HingeJoint>();
        rb.angularDamping = freeSpinDamping;
    }

    void FixedUpdate() => StepDetent(Time.fixedDeltaTime);

    // Public + dt-parameterized so the edit-mode physics smoke test can drive it between
    // Physics.Simulate steps (MonoBehaviours don't tick in edit-mode simulation). The correction is
    // a per-step velocity change, so dt does not enter the arithmetic; the parameter is the harness
    // contract shared with the magnets.
    public void StepDetent(float dt)
    {
        // Self-resolve so the edit-mode smoke test (where Start never runs) can call this directly.
        if (rb == null) rb = GetComponent<Rigidbody>();
        if (hinge == null) hinge = GetComponent<HingeJoint>();
        if (rb == null || hinge == null) return;   // fail-safe if components were stripped

        Vector3 axis = (transform.rotation * hinge.axis).normalized;
        float axisVel = Vector3.Dot(rb.angularVelocity, axis);   // spin rate about the axle (rad/s)

        // Nearest face via the hinge's own 1D angle tracker (no Euler flipping).
        // The tracker reads NaN until PhysX has actually stepped this joint (an untouched sleeping
        // roller) — skip those steps or the NaN propagates into a rejected AddTorque every frame.
        float currentAngle = hinge.angle;
        if (float.IsNaN(currentAngle)) return;
        float targetAngle = Mathf.Round((currentAngle - angleOffsetDeg) / FaceSpacingDeg) * FaceSpacingDeg + angleOffsetDeg;
        float errorDeg = Mathf.DeltaAngle(currentAngle, targetAngle);

        // Seek rate proportional to the remaining error, capped — then move the actual spin rate
        // toward it by at most the regime's cap. At the face (error ~0) this actively brakes, and
        // inside the latch window it brakes with the hook's full strength: a roller driven through a
        // face is slowed by holdCorrectionPerStep every step it spends inside the window, and one
        // spun and released is stopped by it outright.
        float desiredVel = Mathf.Clamp(errorDeg * Mathf.Deg2Rad * snapStrength, -maxSnapSpeed, maxSnapSpeed);
        float cap = Mathf.Abs(errorDeg) <= latchAngleDeg ? holdCorrectionPerStep : maxCorrectionPerStep;
        float correction = Mathf.Clamp(desiredVel - axisVel, -cap, cap);
        rb.AddTorque(axis * correction, ForceMode.VelocityChange);
    }
}
