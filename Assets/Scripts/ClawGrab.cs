using System.Collections.Generic;
using UnityEngine;

// Makes a claw's closed jaws actually HOLD what's between them.
//
// Why this is needed at all: the field's game pieces run at Minimum friction (0.2) so they slide
// freely in the goals, which means squeezing them between two jaws can never grip — real friction
// clamping just squirts the piece out. So the grab is a constraint, not a friction model: while the
// clamp cylinder is fired, any piece inside the mouth trigger is locked to the claw and rides it.
//
// A grabbed piece EASES to the hold point rather than freezing where it was caught: a real grab is
// rarely square on, and pinning a piece at the pose it happened to be in — half inside a jaw, or
// mid-topple with a corner through the floor — leaves the physics engine to resolve an overlap it
// can't, which is what sent robots skidding and cartwheeling. The first piece glides to the hold
// point; anything grabbed after it keeps its arrangement RELATIVE to the first, so a cup sitting on a
// pin still lands pin-on-top through a 180° flip. Because the hold point is a child of the flipping
// link, the flip carries them with zero extra bookkeeping.
//
// On the way OUT, the piece goes solid to the world at once but keeps ignoring the claw for a beat
// (releaseGrace). Letting go of something still between the jaws hands the solver an overlap it can
// only resolve by shoving, and it shoves the light thing — so the piece squirts out as if kicked.
// Muting those pairs alone lets it fall out under gravity, hitting the field normally the whole time.
//
// It also STANDS EACH PIECE UP as it comes in (autoUpright), measured per piece from its own mesh.
// The field's pins share one mesh but every instance carries a different child rotation — a pin lying
// flat where the match loader dropped it and one standing in a goal are the same asset at different
// attitudes — so carrying them at the pose they were caught in means an identical grab looks right
// half the time and sideways the other half. Same fix, same reason, as IntakePull's Auto Upright.
//
// While held, a piece stops colliding with everything (passThroughWhileHeld) — the same ghosting
// IntakePull uses, and for the same reason: nothing good comes of a kinematic body being shoved
// through the world by a claw that outweighs it 700:1. Turning it off falls back to muting only the
// piece-vs-claw pairs, which keeps a carried cup solid to the field and goals at the cost of the
// wedging above. Either way it goes fully solid again the moment it's dropped.
//
// Usage: added and configured by Tools > RoboSim > Robot > Mechanisms > Build Claw (roles) on a
// trigger box between the jaws. Riding the clamp's PneumaticActuator means it shares that mechanism's
// button automatically — closing the claw IS grabbing.
[DefaultExecutionOrder(50)] // after the physics step, before the cosmetic followers at 100
public class ClawGrab : MonoBehaviour
{
    [Tooltip("The clamp piston. Pieces are held while it's fired and dropped when it opens.")]
    public PneumaticActuator clampPneumatic;
    [Tooltip("Grab when the piston RETRACTS instead of extends — for jaws that sit sprung open.")]
    public bool grabWhenRetracted;
    [Tooltip("The frame held pieces ride in. A child of the flipping link, so the flip carries them.")]
    public Transform holdPoint;
    [Tooltip("Most pieces the claw can hold at once. 2 covers the usual cup-stacked-on-pin grab.")]
    public int maxHeld = 2;

    [Tooltip("The claw's own links. With pass-through off, a held piece stops colliding with these " +
             "(and only these) so it can't fight the jaws gripping it.")]
    public GameObject[] clawParts;

    [Header("Carry")]
    [Tooltip("A held piece stops colliding with EVERYTHING until it's dropped. Keeps a piece caught " +
             "at an awkward angle from wedging through the CAD or shoving the robot around. Off = it " +
             "only ignores the claw itself and stays solid to the field.")]
    public bool passThroughWhileHeld = true;

    [Tooltip("How fast a grabbed piece slides into the hold point, in world units per second " +
             "(1 unit = 100 mm). 0 pins it where it was caught.")]
    public float snapSpeed = 20f;
    [Tooltip("How fast a grabbed piece turns to face the hold point, in degrees per second.")]
    public float snapTurnSpeed = 540f;

    [Tooltip("RECOMMENDED. Stand each grabbed piece up in the claw by aligning its longest mesh axis " +
             "with the hold point's up — measured PER PIECE, so a pin lying on its side and one " +
             "standing upright are both carried the same way round. Off = the piece keeps whatever " +
             "attitude it happened to be caught in.")]
    public bool autoUpright = true;
    [Tooltip("Override the axis measured as 'along the piece', in MESH local space. Leave zero to " +
             "take the longest side of the mesh bounds, which is right for cups and pins.")]
    public Vector3 uprightMeshAxis = Vector3.zero;

    [Tooltip("Seconds a just-dropped piece keeps ignoring THE CLAW (only) after release. It is solid " +
             "to the field and everything else immediately — this exists because a piece let go while " +
             "still between the jaws is an overlap the solver resolves by kicking it. Cleared early " +
             "the moment the piece is clear of the claw anyway.")]
    public float releaseGrace = 0.25f;

    [Header("Diagnostics")]
    public bool logEvents = true;

    // One captured piece and everything needed to carry it: where its visible middle is relative to
    // its pivot, which way is "along" it, and — for anything grabbed after the first — the
    // arrangement it was caught in relative to that first piece.
    private class Held
    {
        public Rigidbody rb;
        public Vector3 localCom;         // pivot -> center offset, in the piece's own frame
        public Vector3 localUpAxis;      // the piece's long axis, in its own frame (zero = unmeasurable)
        public Quaternion holdRot;       // attitude when caught, relative to the hold point
        public Vector3 relPos;           // for pieces after the first: pose relative to that piece,
        public Quaternion relRot;        //   so a cup sitting on a pin stays sitting on it
        public bool wasKinematic;
        public bool wasDetectCollisions;
        public RigidbodyInterpolation wasInterpolation;
        // The exact pairs muted at capture, so release restores those and nothing else.
        public List<Collider> mutedPiece = new List<Collider>();
        public List<Collider> mutedClaw = new List<Collider>();

        // Where the piece's VISIBLE middle is, given a transform pose — and the inverse, the transform
        // pose that puts its middle somewhere. The field's Cup*/Pin* pieces were split from one FBX
        // without re-centering, so each keeps the CAD origin as its pivot, 9-15 world units off the
        // mesh. Aim the pivot at the claw and the piece appears to teleport across the field; aim the
        // CENTER and it lands in the jaws where you're looking.
        public Vector3 CenterOf(Vector3 position, Quaternion rotation) => position + rotation * localCom;
        public Vector3 PositionFor(Vector3 center, Quaternion rotation) => center - rotation * localCom;
    }

    private readonly List<Held> held = new List<Held>();
    private readonly List<Held> heldScratch = new List<Held>();
    private readonly HashSet<Rigidbody> inMouth = new HashSet<Rigidbody>();
    private readonly List<Rigidbody> mouthScratch = new List<Rigidbody>();
    private readonly List<Collider> clawColliders = new List<Collider>();

    // A piece that has just been let go: solid to the world again, but still ignoring the CLAW for a
    // moment. Dropping a piece that is physically inside the jaws otherwise leaves the solver an
    // overlap it can only resolve by shoving — and it shoves the light thing, so the piece squirts out
    // as if kicked. Muting just those pairs for a beat lets it fall out naturally instead, without
    // making it ghostly to the field the way pass-through does.
    private class Releasing
    {
        public Rigidbody rb;
        public float until;
        public List<Collider> mutedPiece;
        public List<Collider> mutedClaw;
    }

    private readonly List<Releasing> releasing = new List<Releasing>();
    private readonly List<Releasing> releasingScratch = new List<Releasing>();
    private bool wasGrabbing;

    private Transform HoldTf => holdPoint != null ? holdPoint : transform;

    void Awake()
    {
        // The mouth must be a trigger or OnTriggerEnter never fires and the claw silently never grabs.
        Collider mouth = GetComponent<Collider>();
        if (mouth == null)
            Debug.LogWarning("ClawGrab: no Collider on the mouth object — nothing can be detected. " +
                             "Re-run Build Claw (roles).", this);
        else if (!mouth.isTrigger)
            Debug.LogWarning($"ClawGrab: the mouth collider on '{name}' is not a trigger, so it will " +
                             "shove pieces away instead of catching them. Tick Is Trigger.", this);
        if (clampPneumatic == null)
            Debug.LogWarning("ClawGrab: no clamp piston assigned — the claw will never grab.", this);

        CacheClawColliders();
    }

    void OnDisable()
    {
        // Never leave a piece kinematic or half-muted because the robot despawned mid-grab. The grace
        // is ended outright rather than left pending: a disabled component gets no FixedUpdate, so a
        // piece would keep ignoring the claw forever.
        ReleaseAll();
        EndAllReleases();
    }

    private void CacheClawColliders()
    {
        clawColliders.Clear();
        if (clawParts == null) return;
        foreach (GameObject part in clawParts)
        {
            if (part == null) continue;
            foreach (Collider c in part.GetComponentsInChildren<Collider>(true))
            {
                // SOLID colliders only. Physics.IgnoreCollision suppresses trigger events too, and
                // this very mouth is a descendant of the claw links — muting it would stop
                // OnTriggerExit/Enter firing for the piece we're holding, so the claw could never
                // notice it leaving or pick it up again.
                if (Usable(c) && !c.isTrigger) clawColliders.Add(c);
            }
        }
    }

    void OnTriggerEnter(Collider other)
    {
        // attachedRigidbody is null for the robot's own ArticulationBodies, so the claw can never
        // grab itself — only loose pieces have Rigidbodies.
        Rigidbody rb = other.attachedRigidbody;
        if (rb != null && GamePiece.IsPiece(rb.gameObject)) inMouth.Add(rb);
    }

    void OnTriggerExit(Collider other)
    {
        Rigidbody rb = other.attachedRigidbody;
        if (rb != null) inMouth.Remove(rb);
    }

    void FixedUpdate()
    {
        bool grabbing = IsGrabbing();

        if (grabbing)
        {
            // Capture whatever is sitting in the mouth, up to capacity.
            if (held.Count < maxHeld)
            {
                mouthScratch.Clear();
                mouthScratch.AddRange(inMouth);
                foreach (Rigidbody rb in mouthScratch)
                {
                    if (held.Count >= maxHeld) break;
                    Capture(rb);
                }
            }

            // Ease each held piece toward where it should ride and put it there. Doing this every
            // step (rather than parenting) means the piece tracks the claw through the flip and while
            // driving; recomputing the target from the LIVE hold point each step is what lets the
            // glide converge even while the robot is moving.
            heldScratch.Clear();
            heldScratch.AddRange(held);
            foreach (Held h in heldScratch)
                if (h.rb == null) { Unmute(h.mutedPiece, h.mutedClaw); held.Remove(h); }

            if (held.Count > 0)
            {
                Transform hold = HoldTf;
                float dt = Time.fixedDeltaTime;
                Quaternion primary = PrimaryRotation(hold);
                Vector3 primaryCenter = hold.position;

                foreach (Held h in held)
                {
                    // The first piece defines the attitude; the rest keep the arrangement they were
                    // caught in relative to it, so uprighting a pin carries its cup round with it
                    // instead of leaving the two intersecting.
                    bool isPrimary = ReferenceEquals(h, held[0]);
                    Quaternion wantRot = isPrimary ? primary : primary * h.relRot;
                    Vector3 wantCenter = isPrimary ? primaryCenter : primaryCenter + primary * h.relPos;

                    Quaternion nextRot = snapTurnSpeed > 0f
                        ? Quaternion.RotateTowards(h.rb.rotation, wantRot, snapTurnSpeed * dt)
                        : wantRot;
                    Vector3 curCenter = h.CenterOf(h.rb.position, h.rb.rotation);
                    Vector3 nextCenter = snapSpeed > 0f
                        ? Vector3.MoveTowards(curCenter, wantCenter, snapSpeed * dt)
                        : wantCenter;

                    // Place the pivot so the piece's CENTER lands on the carried pose, using the same
                    // rotation we're about to apply — so turning the piece can't swing its mesh off,
                    // even though the pivot may sit well outside it.
                    h.rb.MovePosition(h.PositionFor(nextCenter, nextRot));
                    h.rb.MoveRotation(nextRot);
                }
            }
        }
        else if (held.Count > 0)
        {
            ReleaseAll();
        }

        // Runs whether or not anything is held: a piece released last step is still in its grace.
        TickReleasing();

        if (logEvents && grabbing != wasGrabbing)
            Debug.Log($"ClawGrab: claw {(grabbing ? "closed" : "opened")} — holding {held.Count}.", this);
        wasGrabbing = grabbing;
    }

    private bool IsGrabbing()
        => clampPneumatic != null && clampPneumatic.IsExtended != grabWhenRetracted;

    // How the carried stack is held. Auto Upright stands the first piece along the hold point's own up
    // axis — measured per piece, so a pin scooped off the floor on its side and one taken standing are
    // carried identically. Because the hold point is a child of the flipping link, "up" turns over
    // with the claw and a flipped stack lands the other way round, which is the whole point of a flip.
    // Falls back to the attitude the piece was caught in when Auto Upright is off, or when the piece
    // has no mesh to measure.
    private Quaternion PrimaryRotation(Transform hold)
    {
        Held first = held.Count > 0 ? held[0] : null;
        if (first == null) return hold.rotation;
        return autoUpright && first.localUpAxis.sqrMagnitude > 1e-6f
            ? Quaternion.FromToRotation(first.localUpAxis, hold.up)
            : hold.rotation * first.holdRot;
    }

    // Lock one piece to the claw at its current pose.
    private void Capture(Rigidbody rb)
    {
        if (rb == null || IsHeld(rb)) return;

        // A piece seated on a goal by GoalStackMagnet must leave that stack the moment the claw takes
        // it, or the magnet keeps counting (and re-seating) a piece the claw is carrying away.
        GoalStackMagnet.ReleaseIfSeated(rb);

        // Snatching back something dropped a moment ago: end its grace cleanly, so the capture below
        // starts from normal collision rather than inheriting a half-expired set of muted pairs.
        EndRelease(rb);

        Transform hold = HoldTf;
        Held h = new Held
        {
            rb = rb,
            // Read the center of mass BEFORE anything else is touched: for these off-pivot field
            // pieces it IS the pivot->center offset, and PhysX recomputes it back to the pivot the
            // moment a piece's colliders stop counting — which would throw the offset away.
            localCom = rb.centerOfMass,
            localUpAxis = autoUpright ? ComputeUpAxis(rb) : Vector3.zero,
            holdRot = Quaternion.Inverse(hold.rotation) * rb.rotation,
            wasKinematic = rb.isKinematic,
            wasDetectCollisions = rb.detectCollisions,
            wasInterpolation = rb.interpolation,
        };

        // Anything grabbed after the first records the arrangement it was caught in, measured against
        // that piece — the cup-sitting-on-a-pin case, which has to survive being stood up and flipped.
        if (held.Count > 0)
        {
            Held first = held[0];
            Quaternion firstInv = Quaternion.Inverse(first.rb != null ? first.rb.rotation : hold.rotation);
            Vector3 firstCenter = first.rb != null
                ? first.CenterOf(first.rb.position, first.rb.rotation) : hold.position;
            h.relRot = firstInv * rb.rotation;
            h.relPos = firstInv * (h.CenterOf(rb.position, rb.rotation) - firstCenter);
        }

        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate; // smooth the carry between physics steps

        if (passThroughWhileHeld)
        {
            // One flag beats disabling colliders one by one: it takes the whole body out of collision
            // AND trigger detection for as long as it's held, and restores in a single assignment
            // without having to remember which colliders were enabled at capture.
            rb.detectCollisions = false;
        }
        else
        {
            // Mute ONLY piece-vs-claw contacts, leaving the piece solid to the field, goals and other
            // pieces.
            MutePairs(rb, h.mutedPiece, h.mutedClaw);
        }

        // Deliberately NOT removed from inMouth here: with pass-through OFF the piece never leaves the
        // trigger, so OnTriggerEnter won't fire for it a second time, and dropping it would mean a
        // piece released inside the jaws could never be re-grabbed without backing the robot off it
        // first. (With pass-through ON, losing detectCollisions raises OnTriggerExit and the set
        // clears itself — then re-enters on release, which is exactly right.) IsHeld() is what
        // prevents double capture either way; inMouth stays pure trigger occupancy.
        held.Add(h);
        if (logEvents)
            Debug.Log($"ClawGrab: grabbed '{rb.name}' ({held.Count}/{maxHeld}) — it now rides the claw, " +
                      $"gliding its centre (pivot->centre offset {h.localCom.magnitude:0.#}u) to the hold point." +
                      (autoUpright
                          ? (h.localUpAxis.sqrMagnitude > 1e-6f
                              ? " Auto-upright ON."
                              : " Auto-upright ON but NO MESH found to measure — this piece keeps the " +
                                "attitude it was caught in.")
                          : ""), this);
    }

    private void ReleaseAll()
    {
        if (held.Count == 0) return;
        heldScratch.Clear();
        heldScratch.AddRange(held);
        foreach (Held h in heldScratch)
        {
            if (h.rb == null) { Unmute(h.mutedPiece, h.mutedClaw); continue; }

            // Solid to the world again FIRST, so the pair mute below is applied to colliders in their
            // normal state...
            h.rb.detectCollisions = h.wasDetectCollisions;
            h.rb.isKinematic = h.wasKinematic;
            h.rb.interpolation = h.wasInterpolation;

            // ...and the claw alone stays muted for a beat. Pass-through never established these
            // pairs (it took the whole body out of collision instead), so they're built here; the
            // targeted path already has them and keeps them.
            if (h.mutedPiece.Count == 0) MutePairs(h.rb, h.mutedPiece, h.mutedClaw);

            // Drop it, don't throw it. MovePosition gives a kinematic body a velocity, and the glide
            // runs at snapSpeed — so a piece let go mid-approach would inherit that and rocket off.
            // A claw opening is a release, so zero is both safer and what it should look like.
            if (!h.rb.isKinematic)
            {
                h.rb.linearVelocity = Vector3.zero;
                h.rb.angularVelocity = Vector3.zero;
            }

            releasing.Add(new Releasing
            {
                rb = h.rb,
                until = Time.fixedTime + Mathf.Max(0f, releaseGrace),
                mutedPiece = h.mutedPiece,
                mutedClaw = h.mutedClaw,
            });
            if (logEvents)
                Debug.Log($"ClawGrab: released '{h.rb.name}' — solid again, ignoring the claw for up " +
                          $"to {releaseGrace:0.##}s so the jaws can't kick it out.", this);
        }
        held.Clear();
    }

    // Give each just-dropped piece back its collision with the claw, as soon as it is clear of the
    // claw or the grace runs out — whichever comes first. Nothing here holds a piece up: it has been
    // falling under gravity and hitting the field the whole time.
    private void TickReleasing()
    {
        if (releasing.Count == 0) return;

        bool haveClaw = TryClawBounds(out Bounds claw);
        releasingScratch.Clear();
        releasingScratch.AddRange(releasing);
        foreach (Releasing r in releasingScratch)
        {
            bool clear = haveClaw && r.rb != null && !claw.Contains(r.rb.worldCenterOfMass);
            if (r.rb != null && Time.fixedTime < r.until && !clear) continue;

            Unmute(r.mutedPiece, r.mutedClaw);
            releasing.Remove(r);
            if (logEvents && r.rb != null)
                Debug.Log($"ClawGrab: '{r.rb.name}' collides with the claw again " +
                          $"({(clear ? "it is clear of the jaws" : "grace expired")}).", this);
        }
    }

    // End one piece's grace early — it's being re-grabbed, or the claw is going away.
    private void EndRelease(Rigidbody rb)
    {
        for (int i = releasing.Count - 1; i >= 0; i--)
        {
            if (releasing[i].rb != rb) continue;
            Unmute(releasing[i].mutedPiece, releasing[i].mutedClaw);
            releasing.RemoveAt(i);
        }
    }

    private void EndAllReleases()
    {
        foreach (Releasing r in releasing) Unmute(r.mutedPiece, r.mutedClaw);
        releasing.Clear();
    }

    // The claw's own world extent, used to tell whether a dropped piece has cleared the jaws.
    private bool TryClawBounds(out Bounds bounds)
    {
        bounds = default;
        bool has = false;
        foreach (Collider c in clawColliders)
        {
            if (!Usable(c)) continue;
            if (!has) { bounds = c.bounds; has = true; }
            else bounds.Encapsulate(c.bounds);
        }
        return has;
    }

    // Mute every piece-vs-claw pair, remembering exactly which ones. Physics.IgnoreCollision errors on
    // a collider that's disabled or on an inactive object, so both sides are filtered — and the pairs
    // are remembered rather than re-derived later, because a collider's state can change in between
    // and re-deriving would miss (or invent) pairs.
    private void MutePairs(Rigidbody rb, List<Collider> pieceOut, List<Collider> clawOut)
    {
        foreach (Collider pieceCol in rb.GetComponentsInChildren<Collider>(true))
        {
            if (!Usable(pieceCol) || pieceCol.isTrigger) continue;
            pieceOut.Add(pieceCol);
        }
        foreach (Collider clawCol in clawColliders)
        {
            if (!Usable(clawCol)) continue;
            foreach (Collider pieceCol in pieceOut) Physics.IgnoreCollision(pieceCol, clawCol, true);
            clawOut.Add(clawCol);
        }
    }

    // Restore exactly the piece-vs-claw pairs this grab muted. A pair whose collider has since been
    // destroyed, disabled or deactivated is skipped — IgnoreCollision would error on it, and PhysX
    // already forgot the pair when the collider went away.
    private void Unmute(List<Collider> mutedPiece, List<Collider> mutedClaw)
    {
        if (mutedPiece == null || mutedClaw == null) return;
        foreach (Collider pieceCol in mutedPiece)
        {
            if (!Usable(pieceCol)) continue;
            foreach (Collider clawCol in mutedClaw)
            {
                if (!Usable(clawCol)) continue;
                Physics.IgnoreCollision(pieceCol, clawCol, false);
            }
        }
        mutedPiece.Clear();
        mutedClaw.Clear();
    }

    // Physics.IgnoreCollision requires both colliders live, enabled and on active objects.
    private static bool Usable(Collider c) => c != null && c.enabled && c.gameObject.activeInHierarchy;

    private bool IsHeld(Rigidbody rb)
    {
        foreach (Held h in held) if (h.rb == rb) return true;
        return false;
    }

    // The axis that runs ALONG this piece, in its own local frame. Measured from the longest side of
    // the mesh bounds through the MESH's rotation, not the body's: the field's pins share one mesh but
    // each instance sits at a different child rotation, which is exactly why one lying flat and one
    // standing up otherwise get carried differently. Zero when there's no mesh to measure.
    private Vector3 ComputeUpAxis(Rigidbody rb)
    {
        MeshFilter mf = rb.GetComponentInChildren<MeshFilter>();
        Transform meshTf = mf != null ? mf.transform : null;
        Mesh mesh = mf != null ? mf.sharedMesh : null;
        if (meshTf == null) return Vector3.zero;

        Vector3 axisMeshLocal;
        if (uprightMeshAxis.sqrMagnitude > 1e-6f)
            axisMeshLocal = uprightMeshAxis.normalized;
        else if (mesh != null)
        {
            Vector3 size = mesh.bounds.size;
            axisMeshLocal = (size.x >= size.y && size.x >= size.z) ? Vector3.right
                          : (size.y >= size.z) ? Vector3.up : Vector3.forward;
        }
        else return Vector3.zero;

        return (Quaternion.Inverse(rb.rotation) * (meshTf.rotation * axisMeshLocal)).normalized;
    }
}
