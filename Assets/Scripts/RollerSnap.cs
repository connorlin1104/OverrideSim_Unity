using UnityEngine;

// Magnetic detent for the field rollers: pulls the roller to the nearest of its 3 color-face stages
// (120 deg apart), like the real field's spring detents. A roller nudged off a face settles back onto
// it; a robot actively spinning the roller overcomes the detent (the per-step correction is capped and
// the whole detent disengages above releaseSpeed), then the roller lands cleanly on the next face.
//
// The correction is velocity-TRACKING, not a raw torque: each step it moves the roller's spin rate
// toward a target rate proportional to the remaining angle error (clamped), so it can't wind up and
// oscillate the way an undamped angle-proportional torque does. hinge.angle (the joint's own 1D
// tracker) is used for the error to avoid the 3D Euler flipping bugs; hinge.velocity would drift from
// Dot(angularVelocity, axis) only if the frame moved, and these rollers are fixed to the field.
[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(HingeJoint))]
public class RollerSnap : MonoBehaviour
{
    [Header("Detent Configuration")]
    [Tooltip("How aggressively the roller seeks the nearest face: angle error (deg) is turned into a target spin rate at this gain (rad/s per rad of error). Higher = snappier settle.")]
    [SerializeField] private float snapStrength = 6f;
    [Tooltip("Cap on the detent's seek speed (rad/s) so a face is approached at a controlled rate instead of whipping around.")]
    [SerializeField] private float maxSnapSpeed = 2f;
    [Tooltip("THE detent strength: the most spin-rate correction applied per physics step (rad/s). A robot roller mechanism must beat this to turn the roller off a face; raise it to make faces stickier.")]
    [SerializeField] private float maxCorrectionPerStep = 0.35f;
    [Tooltip("While the roller spins faster than this (rad/s) the detent disengages entirely, so it never fights a robot actively spinning it.")]
    [SerializeField] private float releaseSpeed = 4f;
    [Tooltip("Rotates all 3 detent stops (deg) so they line up with the color faces. 0 = the pose the roller was authored in counts as a face; nudge per roller if a face sits slightly off at rest.")]
    [SerializeField] private float angleOffsetDeg = 0f;
    [Tooltip("Written to the Rigidbody's angular damping at Start, so a freely-spun roller decays below Release Speed and gets caught by a detent instead of spinning forever.")]
    [SerializeField] private float freeSpinDamping = 1.2f;

    // For the editor pass, which bakes the damping onto the Rigidbody at attach time so the value is
    // live even before Start runs (e.g. in edit-mode Physics.Simulate, where Start never fires).
    public float FreeSpinDamping => freeSpinDamping;

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
    // Physics.Simulate steps (MonoBehaviours don't tick in edit-mode simulation).
    public void StepDetent(float dt)
    {
        // Self-resolve so the edit-mode smoke test (where Start never runs) can call this directly.
        if (rb == null) rb = GetComponent<Rigidbody>();
        if (hinge == null) hinge = GetComponent<HingeJoint>();
        if (rb == null || hinge == null) return;   // fail-safe if components were stripped

        Vector3 axis = (transform.rotation * hinge.axis).normalized;
        float axisVel = Vector3.Dot(rb.angularVelocity, axis);   // spin rate about the axle (rad/s)
        if (Mathf.Abs(axisVel) > releaseSpeed) return;           // being driven — stay out of the way

        // Nearest 120-degree face via the hinge's own 1D angle tracker (no Euler flipping).
        // The tracker reads NaN until PhysX has actually stepped this joint (an untouched sleeping
        // roller) — skip those steps or the NaN propagates into a rejected AddTorque every frame.
        float currentAngle = hinge.angle;
        if (float.IsNaN(currentAngle)) return;
        float targetAngle = Mathf.Round((currentAngle - angleOffsetDeg) / 120f) * 120f + angleOffsetDeg;
        float errorDeg = Mathf.DeltaAngle(currentAngle, targetAngle);

        // Seek rate proportional to the remaining error, capped — then move the actual spin rate
        // toward it by at most maxCorrectionPerStep. At the face (error ~0) this actively brakes.
        float desiredVel = Mathf.Clamp(errorDeg * Mathf.Deg2Rad * snapStrength, -maxSnapSpeed, maxSnapSpeed);
        float correction = Mathf.Clamp(desiredVel - axisVel, -maxCorrectionPerStep, maxCorrectionPerStep);
        rb.AddTorque(axis * correction, ForceMode.VelocityChange);
    }
}
