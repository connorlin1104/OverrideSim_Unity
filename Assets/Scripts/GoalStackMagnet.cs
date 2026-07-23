using System.Collections.Generic;
using UnityEngine;

// Magnetic stacking for a goal: pulls a cup/pin that lands on (or near) the goal onto the stack —
// centered on the stack axis, at the right height for its place — and HOLDS it there so imperfect
// collider meshes can't let it get nudged out. By default (keepDroppedOrientation) it holds each piece
// in the ATTITUDE IT WAS DROPPED IN and seats pieces in the order they land (lowest first), so a stack
// looks the way you built it; the old mode instead stood every piece bolt-upright to a fixed pose.
//
// The hold is a STRONG MAGNET, not a weld: seated pieces stay dynamic (gravity on, still solid to the
// robot — you can knock the whole stack), but each physics step the magnet velocity-corrects the piece
// toward its slot with a per-step cap. That cap is the magnet's strength. While a piece is still
// gliding IN it is soft (so it lands gently); once SEATED the hold is stiff — it aims the piece exactly
// back onto its slot within a single step (rigidHoldPerStep), so a bump barely registers. A ram that
// shoves a piece faster than the cap, long enough to carry it past releaseRadius, still unseats it and
// it's ordinary physics again (deliberate ram-descoring — you have to hit hard, high up). Pieces above
// an unseated piece re-target one slot down.
//
// RIGID SEATED HOLD (rigidSeatedHold, default on): once a piece is seated it STOPS COLLIDING WITH THE
// OTHER PIECES IN ITS OWN STACK, and the magnet then snaps it to its exact slot (position AND attitude)
// from EVERY direction — including pulling a bumped piece back DOWN. Without that, a bump could shove
// one ring up through the ring above it (the meshes are imperfect and low-friction, so the solver can't
// keep them apart), which is the "pieces phase through each other" people saw. Muting the intra-stack
// pairs is what lets the hold be two-directional without a downward pull clipping a piece into the one
// beneath it — so the whole stack reads as one rigid column, hard to tip like a real one. It stays solid
// to the robot and a hard ram still drops a piece; only piece-vs-piece-in-the-same-stack contact is gone.
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
    [Tooltip("How far off the stack axis (world units, horizontal) a piece's center may be and still get captured NEAR THE POST TOP — the wide mouth of the capture funnel, where a ring dropping onto the stake is caught and guided down.")]
    public float captureRadius = 0.6f;
    [Tooltip("Capture radius AT the seated slot (world units) — the TIGHT bottom of the funnel. A piece near the base must be nearly centered on the stake to be grabbed, so a ring merely leaning against the SIDE of the post is left loose and can be knocked off instead of sticking. The allowed radius widens linearly from this at the slot up to Capture Radius at the post top.")]
    public float seatedCaptureRadius = 0.25f;
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
    [Tooltip("Small uniform lift of the whole stack off the pocket floor (world units) so the bottom piece doesn't sink into the goal mesh. Piece-to-piece spacing itself comes from each type's Stack Advance (already nested), so this does NOT widen the gaps between pieces.")]
    public float stackClearance = 0.15f;

    [Header("Rigid seated hold")]
    [Tooltip("Lock seated pieces into a rigid column: a piece that has settled stops colliding with the " +
             "OTHER pieces in its stack, and the magnet then holds it to its exact slot from all " +
             "directions (a bump can't shove one ring up through the ring above it). It stays solid to " +
             "the robot and a hard ram still knocks a piece off. Off = the older soft one-directional magnet.")]
    public bool rigidSeatedHold = true;
    [Tooltip("How firmly a SEATED piece is locked to its slot in rigid mode: the most per-step velocity/spin " +
             "correction applied (world units/sec and rad/sec). Big on purpose — a seated piece snaps back to " +
             "its slot within a step, so bumps barely register and the stack reads as one solid column. A ram " +
             "that shoves a piece faster than this, long enough to clear Release Radius, still knocks it off " +
             "(that's descoring — you have to hit it hard, high up). Lower it to make pieces easier to tip.")]
    public float rigidHoldPerStep = 25f;

    [Header("Orientation")]
    [Tooltip("Hold each piece the way it was DROPPED instead of standing it upright — a ring goes onto " +
             "the stake in the attitude it fell in, and pieces seat in the order they land (lowest " +
             "first), so a cup that fell under a pin stays under it. The magnet still centres pieces on " +
             "the stake and spaces them, it just no longer rotates them to a fixed pose. Off = the old " +
             "look: every piece is turned so its long axis stands along the stack.")]
    public bool keepDroppedOrientation = true;

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
        public List<Collider> colliders; // this piece's solid colliders, cached so rigid-mode muting is stable
        public Quaternion heldRotation;  // attitude captured at seating — held as-is when keepDroppedOrientation
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
        // Route through Unseat so every muted intra-stack collision pair is restored before we let go.
        while (stack.Count > 0) Unseat(stack.Count - 1);
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
                magnet.Unseat(i);
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
            if (s.rb == null) { Unseat(i--); continue; }                    // destroyed
            if (s.rb.isKinematic)                                            // grabbed by the intake/a tool
            {
                Unseat(i--);
                continue;
            }

            // Spacing comes from the per-type profile the tool baked (already nested for how these
            // pieces sit on the stake). keepDroppedOrientation only affects the ATTITUDE the piece is
            // held in, not where along the stack it seats.
            Vector3 slot = stackAnchor.position + up * (baseHeight + s.profile.restHeight + stackClearance);
            Vector3 posError = slot - s.rb.worldCenterOfMass;

            // Pull-in phase gets a leash long enough to cover the whole descent from the post top;
            // the seated phase releases at the tuned radius — that IS ram-descoring.
            float leash = s.arrived ? releaseRadius : captureHeight + releaseRadius;
            if (posError.sqrMagnitude > leash * leash)
            {
                Unseat(i--);
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
                        Unseat(i--);
                        continue;
                    }
                }
            }

            HoldOnSlot(s, slot, up, dt);
            baseHeight += s.profile.stackAdvance; // the nested spacing baked per type — clearance is a one-time lift, not per piece
        }

        // 2) Look for a new piece settling onto the next open slot.
        if (stack.Count >= maxStack) return;
        Vector3 nextSlot = stackAnchor.position + up * baseHeight; // slot surface; restHeight is per-piece
        TryCapture(nextSlot, up);
    }

    // The magnet itself: move the piece's velocity toward "seek the slot" and its spin toward the
    // orientation it should hold — the attitude it was DROPPED in (keepDroppedOrientation, default) or
    // standing upright (old mode) — each capped per step. Purely velocity-space, so contacts and gravity
    // keep working — the piece's weight still rests on whatever is under it.
    private void HoldOnSlot(Seated s, Vector3 slot, Vector3 up, float dt)
    {
        Rigidbody rb = s.rb;
        float step = Mathf.Max(dt, 1e-4f);
        bool rigidHold = s.arrived && rigidSeatedHold;
        float pullCap = !s.arrived ? pullInPerStep : (rigidSeatedHold ? rigidHoldPerStep : maxPullPerStep);
        float tiltCap = rigidHold ? rigidHoldPerStep : (s.arrived ? maxTiltCorrectionPerStep : maxTiltCorrectionPerStep * 4f);

        Vector3 desiredVel;
        if (s.arrived)
        {
            Vector3 toSlot = slot - rb.worldCenterOfMass;
            if (rigidSeatedHold)
            {
                // RIGID hold: seated pieces don't collide with each other (muted at capture), so the
                // magnet can seek the slot from EVERY direction — including pulling a bumped piece back
                // DOWN — without the two meshes fighting. Aim to sit EXACTLY on the slot next step
                // (toSlot/step), capped at rigidHoldPerStep: any bump is erased within a step, so the
                // stack reads as one rigid column. The cap is also the descore threshold — a ram that
                // shoves the piece faster than this, long enough to clear releaseRadius, still drops it.
                desiredVel = Vector3.ClampMagnitude(toSlot / step, rigidHoldPerStep);
            }
            else
            {
                // Older soft, ONE-DIRECTIONAL hold: pull toward the slot sideways AND firmly LIFT a piece
                // that has sunk below its slot, but NEVER pull one DOWN into the piece beneath it. With
                // collisions on, the downward half of the pull was the clipping.
                float along = Vector3.Dot(toSlot, up);
                Vector3 lateral = toSlot - up * along;
                desiredVel = Vector3.ClampMagnitude((lateral + up * Mathf.Max(along, 0f)) * pullGain, maxPullSpeed);
            }
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

        // In the rigid seated hold the spin is snapped back to its target in a step too (angle/step),
        // so a bump can't tilt a piece into the muted neighbour above or below it; otherwise it drifts
        // back at the gentler tiltGain rate.
        float angGain = rigidHold ? (1f / step) : tiltGain;
        float angSpeedCap = rigidHold ? rigidHoldPerStep : maxTiltSpeed;

        Vector3 desiredAngVel;
        if (keepDroppedOrientation)
        {
            // Hold the attitude the piece was DROPPED in — never stand it upright (that mid-air turn to a
            // fixed pose is exactly what looked wrong). Drive the spin toward the frozen capture
            // orientation so it stops tumbling but keeps precisely the way it fell in.
            Quaternion delta = s.heldRotation * Quaternion.Inverse(rb.rotation);
            delta.ToAngleAxis(out float angleDeg, out Vector3 axis);
            if (angleDeg > 180f) { angleDeg = 360f - angleDeg; axis = -axis; } // shortest arc back to held
            desiredAngVel = (angleDeg > 1e-3f && axis.sqrMagnitude > 1e-8f && !float.IsInfinity(axis.x))
                ? Vector3.ClampMagnitude(axis.normalized * (angleDeg * Mathf.Deg2Rad * angGain), angSpeedCap)
                : Vector3.zero;
        }
        else
        {
            if (s.localUpAxis.sqrMagnitude < 1e-6f) return; // no mesh was measurable — hold position only

            // Stand the measured axis along the stack axis. FromToRotation's axis is perpendicular to
            // both, so the desired spin has no component about the stack axis — but tracking against the
            // CURRENT angular velocity also brakes free spin, which is what "held" should look like.
            Vector3 currentUp = rb.rotation * s.localUpAxis;
            float tiltDeg = Vector3.Angle(currentUp, up);
            Vector3 tiltAxis = Vector3.Cross(currentUp, up);
            desiredAngVel = tiltAxis.sqrMagnitude > 1e-8f
                ? Vector3.ClampMagnitude(tiltAxis.normalized * (tiltDeg * Mathf.Deg2Rad * angGain), angSpeedCap)
                : Vector3.zero;
        }
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

        // Of everything eligible in the window, seat the LOWEST piece — the one nearest this
        // (bottom-most) open slot. Capture is one-per-step and fills bottom-up, so taking the lowest
        // each time is what keeps a stack in the order it was dropped: a cup that landed under a pin
        // takes the lower slot and the pin lands on top of it, not the reverse. (The old code grabbed
        // whichever the physics scan happened to list first, which is why the order sometimes flipped.)
        Rigidbody best = null;
        PieceProfile bestProfile = null;
        float bestVertical = float.MaxValue;
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
            if (vertical > captureHeight || vertical < -captureVerticalWindow) continue;
            // Funnel, not a cylinder: the allowed off-axis distance is TIGHT at the seated slot and
            // widens to captureRadius at the post top. A ring dropping onto the stake is still caught
            // up high and guided down; a piece leaning against the SIDE of the post near the base is
            // off-axis at a low height, falls outside the narrow bottom, and stays loose (knockable).
            float funnelT = Mathf.Clamp01(vertical / Mathf.Max(0.01f, captureHeight));
            if (horizontal > Mathf.Lerp(seatedCaptureRadius, captureRadius, funnelT)) continue;

            if (vertical < bestVertical) { bestVertical = vertical; best = rb; bestProfile = profile; }
        }

        if (best == null) return;

        // The catch: kill the excess speed the moment the magnet claims it — a piece falling at
        // ~15+ u/s would otherwise ricochet off the pocket (or the held piece below) faster than any
        // pull can recover.
        best.linearVelocity = Vector3.ClampMagnitude(best.linearVelocity, pullInSpeed);
        best.angularVelocity = Vector3.ClampMagnitude(best.angularVelocity, maxTiltSpeed);

        Seated seated = new Seated
        {
            rb = best,
            localUpAxis = ComputeUpAxis(best),
            profile = bestProfile,
            // Freeze the attitude RIGHT NOW, in the pose it was dropped in — then hold it as-is
            // (keepDroppedOrientation), so nothing turns after capture.
            heldRotation = best.rotation,
        };
        CacheColliders(seated);
        stack.Add(seated);
        Claimed.Add(best);
        MuteSeatedAgainstStack(seated); // rigid mode: stop it clipping the pieces already on the stack
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

    // --- Rigid seated hold: seated pieces in the same stack don't collide with each other ---

    private static bool Usable(Collider c) => c != null && c.enabled && c.gameObject.activeInHierarchy;

    // Solid colliders only — triggers aren't stacking contact, and the trigger sensors mustn't be muted.
    private static void CacheColliders(Seated s)
    {
        s.colliders = new List<Collider>();
        if (s.rb == null) return;
        foreach (Collider c in s.rb.GetComponentsInChildren<Collider>(true))
            if (Usable(c) && !c.isTrigger) s.colliders.Add(c);
    }

    private static void SetIgnorePair(Seated a, Seated b, bool ignore)
    {
        if (a?.colliders == null || b?.colliders == null) return;
        foreach (Collider ca in a.colliders)
        {
            if (!Usable(ca)) continue;
            foreach (Collider cb in b.colliders)
            {
                if (!Usable(cb)) continue;
                Physics.IgnoreCollision(ca, cb, ignore);
            }
        }
    }

    // Mute the just-seated piece against every other piece already on this stack (rigid mode only).
    private void MuteSeatedAgainstStack(Seated s)
    {
        if (!rigidSeatedHold) return;
        foreach (Seated other in stack)
            if (!ReferenceEquals(other, s)) SetIgnorePair(s, other, true);
    }

    // THE single removal path: restore the leaving piece's collisions with the rest of the stack,
    // unclaim it, and drop it. Everything that un-seats a piece (destroyed, grabbed, drifted off,
    // timed out, goal disabled, intake descore) goes through here so no muted pair is ever left stuck.
    // Passing false always is safe — un-ignoring a pair that was never ignored is a no-op in PhysX.
    private void Unseat(int index)
    {
        if (index < 0 || index >= stack.Count) return;
        Seated s = stack[index];
        if (s != null)
        {
            foreach (Seated other in stack)
                if (!ReferenceEquals(other, s)) SetIgnorePair(s, other, false);
            if (s.rb != null) Claimed.Remove(s.rb);
        }
        stack.RemoveAt(index);
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
