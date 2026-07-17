using System.Collections.Generic;
using UnityEngine;

// Magnetic stacking on a CUP (or any piece used as a base): while the cup is sitting still and
// standing upright, it pulls a piece resting on top of it into a clean, centered, upright pose and
// holds it there — the same "strong magnet, not a lock" feel as GoalStackMagnet, but the base is a
// movable piece instead of a fixed goal, so the hold pose tracks the cup as it settles.
//
// RESTING-ONLY by design (the behavior chosen for this feature): the hold engages only while the
// cup itself is nearly stationary AND standing within maxCupTiltDeg of upright. The instant the cup
// is picked up, thrown, knocked over, or made kinematic by the intake, every piece it holds is
// released back to ordinary physics — so carrying a cup never drags a piece around on an invisible
// string, and grabbing the cup (or the piece) with the intake is clean.
//
// Self-contained: its own static claim set means one cup can't steal a piece another cup already
// holds, and a piece the intake has made kinematic is never captured (and is dropped if held). It
// shares nothing with GoalStackMagnet, so it can't regress the goal behavior. A piece resting on a
// cup that is itself on a goal may be held by BOTH toward the same pose — harmless (both corrections
// aim at the same spot). Attach with Tools > RoboSim > Field & Pieces > Add Cup Stack Magnets.
public class PieceStackMagnet : MonoBehaviour
{
    // What can stack ON this cup, matched by name prefix (longest match wins). Baked from the piece
    // meshes by the Add Cup Stack Magnets tool; tune per cup in the Inspector.
    [System.Serializable]
    public class PieceProfile
    {
        [Tooltip("Piece name prefix this applies to, e.g. 'Cup' or 'Pin' (matched at the START of the piece's name).")]
        public string namePrefix;
        [Tooltip("Height of this piece's CENTER OF MASS above the cup top where the magnet holds it (world units).")]
        public float restHeight = 0.8f;
        [Tooltip("How much this piece raises the NEXT slot above it (world units).")]
        public float stackAdvance = 1.2f;
    }

    [Header("Base (baked from the cup mesh by Add Cup Stack Magnets)")]
    [Tooltip("Top-center of this cup in its OWN local frame — where the first stacked piece sits. Transformed each step so it tracks the cup as it moves/rotates.")]
    public Vector3 localBaseOffset;
    [Tooltip("The cup's standing axis in its OWN local frame — the stack goes up along this.")]
    public Vector3 localUpAxis = Vector3.up;

    [Header("Rest gate (hold ONLY while the cup is settled + upright)")]
    [Tooltip("The cup counts as at rest when its own speed is below this (world units/sec). Above it, held pieces are released.")]
    public float restLinearSpeed = 0.6f;
    [Tooltip("...and its spin is below this (rad/sec).")]
    public float restAngularSpeed = 1.2f;
    [Tooltip("...and its standing axis is within this many degrees of world up (a cup on its side won't stack sideways).")]
    public float maxCupTiltDeg = 35f;

    [Header("Capture (small — only a piece already settling onto the cup)")]
    [Tooltip("How far off the stack axis (world units) a piece's center may be and still be captured near the TOP of the capture window — the wide mouth of the funnel.")]
    public float captureRadius = 0.5f;
    [Tooltip("Capture radius AT the seated slot (world units) — the tight bottom of the funnel. A piece must be nearly centered on the cup to be grabbed, so one merely nudged against the side isn't stuck to it; the allowed radius widens to Capture Radius higher up.")]
    public float seatedCaptureRadius = 0.25f;
    [Tooltip("How far ABOVE the slot a piece's center may be and still be captured.")]
    public float captureAbove = 0.7f;
    [Tooltip("How far BELOW the slot a piece's center may be and still be captured.")]
    public float captureBelow = 0.3f;
    [Tooltip("A piece moving sideways faster than this (world units/sec) is flying past, not landing — not captured.")]
    public float maxCaptureLinearSpeed = 3f;
    [Tooltip("A piece falling straight down up to this speed (world units/sec) is still captured (a drop onto the cup is fast).")]
    public float maxCaptureFallSpeed = 20f;
    [Tooltip("A piece tumbling faster than this (rad/sec) is not captured.")]
    public float maxCaptureAngularSpeed = 4f;

    [Header("Hold strength (the magnet)")]
    public float maxPullSpeed = 8f;
    public float pullGain = 8f;
    [Tooltip("THE magnet strength: the most velocity correction applied per physics step (world units/sec). ~gravity-per-step (2 at 100 Hz) so casual bumps self-correct but a hard shove still knocks the piece off.")]
    public float maxPullPerStep = 2f;
    public float maxTiltSpeed = 6f;
    public float tiltGain = 6f;
    public float maxTiltCorrectionPerStep = 0.8f;
    [Tooltip("A held piece whose center drifts this far (world units) from its slot has been forced off — released back to ordinary physics.")]
    public float releaseRadius = 0.9f;
    [Tooltip("Extra vertical clearance between stacked pieces (world units): the magnet holds each piece this much ABOVE where its collider would otherwise rest, so stacked meshes keep a hair of separation instead of clipping. Raise it if pieces still overlap.")]
    public float stackClearance = 0.15f;

    [Header("Stack")]
    [Tooltip("Most pieces this cup holds; further pieces stay loose on top.")]
    public int maxStack = 3;
    [Tooltip("Per-piece-type rest height / spacing. Baked from the piece meshes by the Add Cup Stack Magnets tool; tune here.")]
    public List<PieceProfile> pieceProfiles = new List<PieceProfile>();

    private class Seated { public Rigidbody rb; public Vector3 localUp; public PieceProfile profile; }
    private readonly List<Seated> stack = new List<Seated>();
    private readonly Collider[] overlapScratch = new Collider[128];
    private Rigidbody body;

    // Every piece any cup is currently holding — so two cups never fight over one piece, and a cup
    // never grabs a piece that is already spoken for.
    private static readonly HashSet<Rigidbody> Claimed = new HashSet<Rigidbody>();
    public static bool IsClaimed(Rigidbody rb) => rb != null && Claimed.Contains(rb);
    public int SeatedCount => stack.Count;

    void Awake() => body = GetComponent<Rigidbody>();

    void OnDisable()
    {
        foreach (Seated s in stack) if (s.rb != null) Claimed.Remove(s.rb);
        stack.Clear();
    }

    void FixedUpdate() => StepMagnet(Time.fixedDeltaTime);

    // Public + dt-parameterized so the edit-mode smoke test can drive it between Physics.Simulate
    // steps (MonoBehaviours don't tick in edit-mode simulation).
    public void StepMagnet(float dt)
    {
        if (body == null) body = GetComponent<Rigidbody>();

        Vector3 up = transform.TransformDirection(localUpAxis);
        up = up.sqrMagnitude > 1e-6f ? up.normalized : transform.up;

        bool resting = body != null && !body.isKinematic
            && body.linearVelocity.sqrMagnitude <= restLinearSpeed * restLinearSpeed
            && body.angularVelocity.sqrMagnitude <= restAngularSpeed * restAngularSpeed
            && Vector3.Angle(up, Vector3.up) <= maxCupTiltDeg;

        // The cup left its resting state — let go of everything and stop holding until it settles.
        if (!resting)
        {
            if (stack.Count > 0)
            {
                foreach (Seated s in stack) if (s.rb != null) Claimed.Remove(s.rb);
                stack.Clear();
            }
            return;
        }

        Vector3 basePos = transform.TransformPoint(localBaseOffset);

        float baseHeight = 0f;
        for (int i = 0; i < stack.Count; i++)
        {
            Seated s = stack[i];
            if (s.rb == null || s.rb.isKinematic)                 // gone, or grabbed by the intake
            {
                if (s.rb != null) Claimed.Remove(s.rb);
                stack.RemoveAt(i--);
                continue;
            }
            Vector3 slot = basePos + up * (baseHeight + s.profile.restHeight + stackClearance);
            if ((slot - s.rb.worldCenterOfMass).sqrMagnitude > releaseRadius * releaseRadius) // forced off
            {
                Claimed.Remove(s.rb);
                stack.RemoveAt(i--);
                continue;
            }
            HoldOnSlot(s, slot, up);
            baseHeight += s.profile.stackAdvance + stackClearance;
        }

        if (stack.Count >= maxStack) return;
        TryCapture(basePos + up * baseHeight, up);
    }

    // Velocity-track the piece toward its slot and its spin toward upright, each capped per step.
    // Purely velocity-space, so contacts + gravity keep working (the piece's weight rests on the cup).
    private void HoldOnSlot(Seated s, Vector3 slot, Vector3 up)
    {
        Rigidbody rb = s.rb;
        // ONE-DIRECTIONAL hold: pull fully toward the slot sideways AND firmly LIFT a piece that has
        // sunk below its slot, but NEVER pull one DOWN into the piece below — holding each piece up at
        // its clean baked height keeps stacked meshes apart; the downward half was the clipping.
        // Still capped by maxPullSpeed / maxPullPerStep, so it stays gentle.
        Vector3 toSlot = slot - rb.worldCenterOfMass;
        float along = Vector3.Dot(toSlot, up);
        Vector3 lateral = toSlot - up * along;
        Vector3 desiredVel = Vector3.ClampMagnitude((lateral + up * Mathf.Max(along, 0f)) * pullGain, maxPullSpeed);
        rb.AddForce(Vector3.ClampMagnitude(desiredVel - rb.linearVelocity, maxPullPerStep), ForceMode.VelocityChange);

        if (s.localUp.sqrMagnitude < 1e-6f) return;
        Vector3 currentUp = rb.rotation * s.localUp;
        float tiltDeg = Vector3.Angle(currentUp, up);
        Vector3 tiltAxis = Vector3.Cross(currentUp, up);
        Vector3 desiredAng = tiltAxis.sqrMagnitude > 1e-8f
            ? Vector3.ClampMagnitude(tiltAxis.normalized * (tiltDeg * Mathf.Deg2Rad * tiltGain), maxTiltSpeed)
            : Vector3.zero;
        rb.AddTorque(Vector3.ClampMagnitude(desiredAng - rb.angularVelocity, maxTiltCorrectionPerStep), ForceMode.VelocityChange);
    }

    private void TryCapture(Vector3 slotBase, Vector3 up)
    {
        float maxRest = 0f;
        foreach (PieceProfile p in pieceProfiles) if (p != null && p.restHeight > maxRest) maxRest = p.restHeight;
        float scanRadius = captureRadius + Mathf.Max(captureAbove, captureBelow) + maxRest;
        int hits = Physics.OverlapSphereNonAlloc(slotBase, scanRadius, overlapScratch);
        for (int i = 0; i < hits; i++)
        {
            Rigidbody rb = overlapScratch[i] != null ? overlapScratch[i].attachedRigidbody : null;
            if (rb == null || rb == body || rb.isKinematic || Claimed.Contains(rb)) continue;
            if (!GamePiece.IsPiece(rb.gameObject)) continue;

            Vector3 velocity = rb.linearVelocity;
            float verticalVel = Vector3.Dot(velocity, up);
            float horizontalSpeed = (velocity - up * verticalVel).magnitude;
            if (horizontalSpeed > maxCaptureLinearSpeed) continue;
            if (verticalVel > maxCaptureLinearSpeed || -verticalVel > maxCaptureFallSpeed) continue;
            if (rb.angularVelocity.sqrMagnitude > maxCaptureAngularSpeed * maxCaptureAngularSpeed) continue;

            PieceProfile profile = MatchProfile(rb.name);
            if (profile == null) continue;
            Vector3 delta = rb.worldCenterOfMass - (slotBase + up * profile.restHeight);
            float vertical = Vector3.Dot(delta, up);
            float horizontal = (delta - up * vertical).magnitude;
            if (vertical > captureAbove || vertical < -captureBelow) continue;
            // Funnel: tight at the slot, widening upward — only grab a piece that's centered as it
            // settles onto the cup, not one nudged against its side.
            float funnelT = Mathf.Clamp01(vertical / Mathf.Max(0.01f, captureAbove));
            if (horizontal > Mathf.Lerp(seatedCaptureRadius, captureRadius, funnelT)) continue;

            rb.linearVelocity = Vector3.ClampMagnitude(velocity, maxPullSpeed); // the catch
            stack.Add(new Seated { rb = rb, localUp = ComputeUpAxis(rb), profile = profile });
            Claimed.Add(rb);
            return; // one capture per step keeps the stack ordered
        }
    }

    private PieceProfile MatchProfile(string pieceName)
    {
        PieceProfile best = null;
        foreach (PieceProfile p in pieceProfiles)
        {
            if (p == null || string.IsNullOrEmpty(p.namePrefix) || !pieceName.StartsWith(p.namePrefix)) continue;
            if (best == null || p.namePrefix.Length > best.namePrefix.Length) best = p;
        }
        return best;
    }

    // The piece's standing axis in its rigidbody-local frame (longest mesh-bounds axis), same
    // measurement GoalStackMagnet / IntakePull auto-upright use. Zero if there's no mesh to measure.
    public static Vector3 ComputeUpAxis(Rigidbody rb)
    {
        MeshFilter mf = rb.GetComponentInChildren<MeshFilter>();
        Mesh mesh = mf != null ? mf.sharedMesh : null;
        if (mesh == null) return Vector3.zero;
        Vector3 s = mesh.bounds.size;
        Vector3 axis = (s.x >= s.y && s.x >= s.z) ? Vector3.right : (s.y >= s.z) ? Vector3.up : Vector3.forward;
        return (Quaternion.Inverse(rb.rotation) * (mf.transform.rotation * axis)).normalized;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        Vector3 up = transform.TransformDirection(localUpAxis.sqrMagnitude > 1e-6f ? localUpAxis : Vector3.up).normalized;
        Vector3 basePos = transform.TransformPoint(localBaseOffset);
        Gizmos.color = new Color(0.3f, 0.9f, 1f, 0.85f);
        Gizmos.DrawLine(basePos, basePos + up * 2f);
        Gizmos.DrawWireSphere(basePos, captureRadius);
    }
#endif
}
