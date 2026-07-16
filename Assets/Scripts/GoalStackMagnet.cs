using System.Collections.Generic;
using UnityEngine;

// Magnetic stacking for a goal: pulls a cup/pin that lands on (or near) the goal into a visually
// perfect pose — centered on the stack axis, standing upright, at the right height for its place in
// the stack — and HOLDS it there so imperfect collider meshes can't tilt it or let it get nudged out.
//
// The hold is a STRONG MAGNET, not a lock: seated pieces stay fully dynamic (gravity on, colliders
// on — their weight rests on the goal floor / the piece below, exactly like before). Each physics
// step the magnet velocity-TRACKS the piece toward its slot: desired velocity proportional to the
// remaining error, with the per-step correction capped. That cap is the magnet's strength — casual
// bumps self-correct, but a hard sustained shove out-accelerates it, and once the piece drifts past
// releaseRadius it unseats and is ordinary physics again (deliberate ram-descoring works). Pieces
// above an unseated piece re-target one slot down automatically.
//
// Capture range is deliberately SMALL: only a slow-moving piece already at the next open slot (the
// small radius/vertical window below) is grabbed, so a clear miss never teleports in. Pieces held by
// the intake are kinematic and therefore never captured; conversely IntakePull.Capture calls
// ReleaseIfSeated so grabbing a piece OFF the stack (descoring via intake) releases it cleanly — and
// if anything else makes a seated piece kinematic, the magnet notices and lets go on its own.
//
// Attached to every goal (with its GoalStackAnchor child) by
// Tools > RoboSim > Field & Pieces > Add Goal Stack Magnets.
public class GoalStackMagnet : MonoBehaviour
{
    // Per-piece-type stack geometry, matched by name prefix (longest match wins). The editor tool
    // bakes defaults from the piece meshes; tune per goal in the Inspector if a type sits wrong.
    [System.Serializable]
    public class PieceProfile
    {
        [Tooltip("Piece name prefix this applies to, e.g. 'Cup' or 'Pin' (matched at the START of the piece's name).")]
        public string namePrefix;
        [Tooltip("Height of this piece's CENTER OF MASS above the surface it stands on (world units) — where the magnet holds it. ~half the piece's standing height.")]
        public float restHeight = 0.8f;
        [Tooltip("How much this piece raises the NEXT slot (world units). Its full standing height, reduced if pieces nest into each other.")]
        public float stackAdvance = 1.6f;
    }

    [Tooltip("Base of the stack: position = where the first piece stands (top of the goal pocket floor), up = the stack axis. Created and aimed by the Add Goal Stack Magnets tool.")]
    public Transform stackAnchor;

    [Header("Capture (small on purpose — a clear miss must stay out)")]
    [Tooltip("How far off the stack axis (world units, horizontal) a piece's center may be and still get captured into the next slot.")]
    public float captureRadius = 0.6f;
    [Tooltip("How far BELOW the next slot (world units, along the stack axis) a piece's center may be and still get captured.")]
    public float captureVerticalWindow = 0.9f;
    [Tooltip("How far ABOVE the next slot the capture window reaches. Tall on purpose: these goals are STAKES — a ring piece must be caught near the top of the post and guided down around it, or it just deflects off the post top and slides away. Baked from the goal's own height by the Add Goal Stack Magnets tool.")]
    public float captureHeight = 3.5f;
    [Tooltip("A piece moving SIDEWAYS (across the stack axis) faster than this (world units/sec) is not captured — it's flying past the goal, not landing on it.")]
    public float maxCaptureLinearSpeed = 3f;
    [Tooltip("A piece FALLING along the stack axis up to this speed (world units/sec) is still captured — a drop into the goal is fast (gravity is ~98) but is exactly what should score. Rising pieces are held to the sideways limit instead.")]
    public float maxCaptureFallSpeed = 20f;
    [Tooltip("A piece tumbling faster than this (rad/sec) is not captured.")]
    public float maxCaptureAngularSpeed = 4f;

    [Header("Hold strength (the magnet)")]
    [Tooltip("How fast the magnet pulls a captured piece toward its slot (world units/sec) — the seek speed while it glides the last bit in.")]
    public float maxPullSpeed = 8f;
    [Tooltip("Error → seek-speed gain (per second). Higher = the last few centimeters snap in faster.")]
    public float pullGain = 8f;
    [Tooltip("THE magnet strength once a piece is SEATED: the most velocity correction applied per physics step (world units/sec). Must roughly match gravity-per-step (~2 at 100 Hz under -98 gravity) so a piece balanced on a stacked rim can't slowly slide off; casual bumps are undone; a shove that out-accelerates this knocks the piece off (ram-descoring).")]
    public float maxPullPerStep = 2f;
    [Tooltip("Pull-in strength while a freshly-captured piece is still traveling to its slot (world units/sec per step). Much stronger than the seated hold — a piece dropped into the goal arrives hot (~15 u/s) and must be caught decisively, or one ricochet off the snug pocket flings it away. Once it first arrives, the seated strength above takes over.")]
    public float pullInPerStep = 4f;
    [Tooltip("Glide speed while pulling a captured piece to its slot (world units/sec). Deliberately slower than the seated seek speed so a piece lands SOFTLY on an occupied stack — colliding with a held piece at speed deflects hard enough to escape the magnet.")]
    public float pullInSpeed = 4f;
    [Tooltip("A captured piece that hasn't reached its slot within this many seconds is let go (something is blocking it) instead of being dragged around forever.")]
    public float pullInTimeout = 3f;
    [Tooltip("How fast the piece is stood upright (rad/sec) while seated.")]
    public float maxTiltSpeed = 6f;
    [Tooltip("Tilt error → uprighting-speed gain (per second).")]
    public float tiltGain = 6f;
    [Tooltip("The most spin-rate correction applied per physics step (rad/sec) — the rotational half of the magnet strength.")]
    public float maxTiltCorrectionPerStep = 0.8f;
    [Tooltip("A seated piece whose center drifts this far (world units) from its slot has been forced off — the magnet releases it back to ordinary physics.")]
    public float releaseRadius = 1.2f;

    [Header("Stack")]
    [Tooltip("Most pieces this goal holds; further pieces are simply not captured (they stay loose on top).")]
    public int maxStack = 6;
    [Tooltip("Per-piece-type rest height / stack spacing, matched by name prefix. Baked from the piece meshes by the Add Goal Stack Magnets tool; tune here.")]
    public List<PieceProfile> pieceProfiles = new List<PieceProfile>();

    // One seated piece, bottom-first. The slot of stack[i] sits above the stackAdvance of everything
    // below it, so removing a piece automatically re-targets the ones above one slot down.
    private class Seated
    {
        public Rigidbody rb;
        public Vector3 localUpAxis;   // the piece's standing axis in its rigidbody-local frame
        public PieceProfile profile;
        public bool arrived;          // reached its slot once → the gentler seated hold applies
        public float pullInTime;      // seconds spent in the pull-in phase (timeout guard)
    }

    // How far off the slot a timed-out pull-in may settle and still count as seated (world units).
    private const float NearEnough = 0.35f;

    private readonly List<Seated> stack = new List<Seated>();
    // Generous: the scan sphere sees every wall collider of the goal plus every 12-collider piece
    // near it, and OverlapSphereNonAlloc silently DROPS overflow — a too-small scratch array made
    // falling pieces invisible to capture next to busy goals.
    private readonly Collider[] overlapScratch = new Collider[256];

    // All live magnets + every rigidbody seated on (or being pulled into) ANY goal, so two goals
    // never fight over one piece and the intake can release a piece wherever it's seated.
    private static readonly List<GoalStackMagnet> All = new List<GoalStackMagnet>();
    private static readonly HashSet<Rigidbody> Claimed = new HashSet<Rigidbody>();

    void OnEnable() => All.Add(this);

    void OnDisable()
    {
        All.Remove(this);
        foreach (Seated s in stack)
            if (s.rb != null) Claimed.Remove(s.rb);
        stack.Clear();
    }

    void FixedUpdate() => StepMagnet(Time.fixedDeltaTime);

    // Un-seat a piece from whichever goal holds it (no physics change needed — seated pieces are
    // ordinary dynamic bodies). Called by IntakePull.Capture so descoring by intake is clean.
    // Returns true if the piece was seated somewhere.
    public static bool ReleaseIfSeated(Rigidbody rb)
    {
        if (rb == null || !Claimed.Contains(rb)) return false;
        foreach (GoalStackMagnet magnet in All)
        {
            for (int i = 0; i < magnet.stack.Count; i++)
            {
                if (magnet.stack[i].rb != rb) continue;
                magnet.stack.RemoveAt(i);
                Claimed.Remove(rb);
                return true;
            }
        }
        Claimed.Remove(rb); // claimed but on no live magnet (stale) — just clear it
        return false;
    }

    public int SeatedCount => stack.Count;

    // Human-readable stack contents ("Cup7@0.43, Pin3@1.29"), for diagnostics and the smoke test.
    public string DescribeStack()
    {
        var sb = new System.Text.StringBuilder();
        float h = 0f;
        foreach (Seated s in stack)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(s.rb != null ? s.rb.name : "<gone>").Append('@').Append((h + (s.profile?.restHeight ?? 0f)).ToString("0.00"));
            h += s.profile?.stackAdvance ?? 0f;
        }
        return sb.Length > 0 ? sb.ToString() : "(empty)";
    }

    public static bool IsClaimed(Rigidbody rb) => rb != null && Claimed.Contains(rb);

    // Public + dt-parameterized so the edit-mode physics smoke test can drive it between
    // Physics.Simulate steps (MonoBehaviours don't tick in edit-mode simulation).
    public void StepMagnet(float dt)
    {
        if (stackAnchor == null) return;

        Vector3 up = stackAnchor.up;
        float baseHeight = 0f; // total stackAdvance of the pieces below the one being processed

        // 1) Hold every seated piece on its slot (and drop the ones that left).
        for (int i = 0; i < stack.Count; i++)
        {
            Seated s = stack[i];
            if (s.rb == null) { stack.RemoveAt(i--); continue; }            // destroyed
            if (s.rb.isKinematic)                                            // grabbed by the intake/a tool
            {
                Claimed.Remove(s.rb);
                stack.RemoveAt(i--);
                continue;
            }

            Vector3 slot = stackAnchor.position + up * (baseHeight + s.profile.restHeight);
            Vector3 posError = slot - s.rb.worldCenterOfMass;

            // Pull-in phase gets a leash long enough to cover the whole descent from the post top;
            // the seated phase releases at the tuned radius — that IS ram-descoring.
            float leash = s.arrived ? releaseRadius : captureHeight + releaseRadius;
            if (posError.sqrMagnitude > leash * leash)
            {
                Claimed.Remove(s.rb);
                stack.RemoveAt(i--);
                continue;
            }
            if (!s.arrived)
            {
                s.pullInTime += dt;
                if (posError.sqrMagnitude < 0.01f) s.arrived = true;   // reached the slot (0.1u)
                else if (s.pullInTime > pullInTimeout)
                {
                    // A HAIR off counts as seated (the physical rest pose can sit slightly off the
                    // computed slot — restHeight is approximate). Anything further — e.g. a ring
                    // jammed diagonally on the stake — is released so a fresh capture can retry,
                    // rather than adopting the crooked pose as "seated".
                    if (posError.sqrMagnitude < NearEnough * NearEnough)
                        s.arrived = true;
                    else
                    {
                        Claimed.Remove(s.rb);
                        stack.RemoveAt(i--);
                        continue;
                    }
                }
            }

            HoldOnSlot(s, slot, up);
            baseHeight += s.profile.stackAdvance;
        }

        // 2) Look for a new piece settling onto the next open slot.
        if (stack.Count >= maxStack) return;
        Vector3 nextSlot = stackAnchor.position + up * baseHeight; // slot surface; restHeight is per-piece
        TryCapture(nextSlot, up);
    }

    // The magnet itself: move the piece's velocity toward "seek the slot" and its spin toward
    // "stand upright", each capped per step. Purely velocity-space, so contacts and gravity keep
    // working — the piece's weight still rests on whatever is under it.
    private void HoldOnSlot(Seated s, Vector3 slot, Vector3 up)
    {
        Rigidbody rb = s.rb;
        float pullCap = s.arrived ? maxPullPerStep : pullInPerStep;
        float tiltCap = s.arrived ? maxTiltCorrectionPerStep : maxTiltCorrectionPerStep * 4f;

        Vector3 desiredVel;
        if (s.arrived)
        {
            desiredVel = Vector3.ClampMagnitude((slot - rb.worldCenterOfMass) * pullGain, maxPullSpeed);
        }
        else
        {
            // Peg-in-hole pull-in: center on the stack axis FIRST, descend only in proportion to
            // how centered the piece is. A ring that descends while off-axis jams diagonally on
            // the stake — and once wedged, pulling it toward the slot only wedges it harder.
            Vector3 rel = rb.worldCenterOfMass - slot;
            float height = Vector3.Dot(rel, up);
            Vector3 lateral = rel - up * height;

            Vector3 lateralVel = Vector3.ClampMagnitude(-lateral * pullGain, pullInSpeed);
            float alignment = Mathf.Clamp01(1f - lateral.magnitude / Mathf.Max(0.05f, captureRadius * 0.5f));
            float verticalVel = height > 0f
                ? -pullInSpeed * alignment                                     // descend once centered
                : Mathf.Min(-height * pullGain, pullInSpeed);                  // below the slot: come up
            desiredVel = lateralVel + up * verticalVel;
        }
        Vector3 velCorrection = Vector3.ClampMagnitude(desiredVel - rb.linearVelocity, pullCap);
        rb.AddForce(velCorrection, ForceMode.VelocityChange);

        if (s.localUpAxis.sqrMagnitude < 1e-6f) return; // no mesh was measurable — hold position only

        // Stand the measured axis along the stack axis. FromToRotation's axis is perpendicular to
        // both, so the desired spin has no component about the stack axis — but tracking against the
        // CURRENT angular velocity also brakes free spin, which is what "held" should look like.
        Vector3 currentUp = rb.rotation * s.localUpAxis;
        float tiltDeg = Vector3.Angle(currentUp, up);
        Vector3 tiltAxis = Vector3.Cross(currentUp, up);
        Vector3 desiredAngVel = tiltAxis.sqrMagnitude > 1e-8f
            ? Vector3.ClampMagnitude(tiltAxis.normalized * (tiltDeg * Mathf.Deg2Rad * tiltGain), maxTiltSpeed)
            : Vector3.zero;
        Vector3 angCorrection = Vector3.ClampMagnitude(desiredAngVel - rb.angularVelocity, tiltCap);
        rb.AddTorque(angCorrection, ForceMode.VelocityChange);
    }

    // Capture = a slow, free piece whose center is inside the small window around the next slot.
    private void TryCapture(Vector3 nextSlot, Vector3 up)
    {
        // Superset sphere for the broad scan (the exact cylinder gates are below). The gate region
        // reaches captureHeight above the slot (the post top) and restHeight above the surface.
        float maxRest = 0f;
        foreach (PieceProfile p in pieceProfiles)
            if (p != null && p.restHeight > maxRest) maxRest = p.restHeight;
        float scanRadius = captureRadius + Mathf.Max(captureHeight, captureVerticalWindow) + maxRest;
        int hits = Physics.OverlapSphereNonAlloc(nextSlot, scanRadius, overlapScratch);
        for (int i = 0; i < hits; i++)
        {
            Rigidbody rb = overlapScratch[i] != null ? overlapScratch[i].attachedRigidbody : null;
            if (rb == null || rb.isKinematic || Claimed.Contains(rb)) continue;   // kinematic = held by the intake
            if (!GamePiece.IsPiece(rb.gameObject)) continue;

            // Speed gates, split along the stack axis: a piece FALLING into the goal is fast but
            // welcome (it would otherwise ricochet off the snug low-friction pocket and slide away
            // before it ever slowed under a single gate); a piece crossing the goal sideways — or
            // launched upward through it — is flying, not scoring, and must not be yanked in.
            Vector3 velocity = rb.linearVelocity;
            float verticalVel = Vector3.Dot(velocity, up);
            float horizontalSpeed = (velocity - up * verticalVel).magnitude;
            if (horizontalSpeed > maxCaptureLinearSpeed) continue;
            if (verticalVel > maxCaptureLinearSpeed || -verticalVel > maxCaptureFallSpeed) continue;
            if (rb.angularVelocity.sqrMagnitude > maxCaptureAngularSpeed * maxCaptureAngularSpeed) continue;

            PieceProfile profile = MatchProfile(rb.name);
            if (profile == null) continue;

            // Split the offset from the piece's SLOT (surface + its rest height) into along-axis
            // and off-axis parts and gate each — a tight TALL cylinder: the same small radius all
            // the way up the post (a miss still stays out), reaching high enough to catch a ring
            // at the post top and guide it down around the stake.
            Vector3 delta = rb.worldCenterOfMass - (nextSlot + up * profile.restHeight);
            float vertical = Vector3.Dot(delta, up);
            float horizontal = (delta - up * vertical).magnitude;
            if (vertical > captureHeight || vertical < -captureVerticalWindow || horizontal > captureRadius) continue;

            // The catch: kill the excess speed the moment the magnet claims it — a piece falling at
            // ~15+ u/s would otherwise ricochet off the pocket (or the held piece below) faster
            // than any pull can recover.
            rb.linearVelocity = Vector3.ClampMagnitude(velocity, pullInSpeed);
            rb.angularVelocity = Vector3.ClampMagnitude(rb.angularVelocity, maxTiltSpeed);

            stack.Add(new Seated { rb = rb, localUpAxis = ComputeUpAxis(rb), profile = profile });
            Claimed.Add(rb);
            return; // one capture per step keeps the stack ordered
        }
    }

    // Longest matching name-prefix profile, e.g. a "PinRed..." matches "Pin". Null = unknown type.
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

    // The piece's "standing" axis in its RIGIDBODY-local frame: the longest axis of the mesh's
    // local bounds, mapped through the mesh child's rotation. Same measurement IntakePull's
    // auto-upright uses — per instance, because the field's pins share one mesh baked at many
    // different child rotations. Zero if there's no mesh to measure (then only position is held).
    private static Vector3 ComputeUpAxis(Rigidbody rb)
    {
        MeshFilter mf = rb.GetComponentInChildren<MeshFilter>();
        Mesh mesh = mf != null ? mf.sharedMesh : null;
        if (mesh == null) return Vector3.zero;

        Vector3 s = mesh.bounds.size;
        Vector3 axisMeshLocal = (s.x >= s.y && s.x >= s.z) ? Vector3.right
                              : (s.y >= s.z) ? Vector3.up : Vector3.forward;
        Vector3 world = mf.transform.rotation * axisMeshLocal;
        return (Quaternion.Inverse(rb.rotation) * world).normalized;
    }

#if UNITY_EDITOR
    // Scene-view gizmo: the capture cylinder at the next open slot + a tick per occupied slot.
    void OnDrawGizmosSelected()
    {
        if (stackAnchor == null) return;
        Gizmos.color = new Color(1f, 0.3f, 0.9f, 0.8f);
        Gizmos.DrawLine(stackAnchor.position, stackAnchor.position + stackAnchor.up * 6f);
        Gizmos.DrawWireSphere(stackAnchor.position, captureRadius);
    }
#endif
}
