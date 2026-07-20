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

    [Header("Diagnostics")]
    [Tooltip("Show a marker at the hold point in Play, so you can see where the claw carries things.")]
    public bool showRuntimeMarkers = true;
    public float markerSize = 0.5f;
    public bool logEvents = true;

    // One captured piece, riding in the hold point's frame: `local*` is where it is now, `target*`
    // where it's heading. The two start apart (it was caught wherever it was caught) and the glide
    // closes the gap.
    private class Held
    {
        public Rigidbody rb;
        public Vector3 localPos;         // CENTER pose relative to the hold point, now
        public Quaternion localRot;
        public Vector3 targetPos;        // where in that frame it settles
        public Quaternion targetRot;
        public Vector3 localCom;         // pivot -> center offset, in the piece's own frame
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
    private GameObject marker;
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

    void OnEnable()
    {
        if (showRuntimeMarkers) EnsureMarker();
    }

    void OnDisable()
    {
        // Never leave a piece kinematic or half-muted because the robot despawned mid-grab.
        ReleaseAll();
        if (marker != null) { Destroy(marker); marker = null; }
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

            // Ease each held piece toward its slot and put it there. Doing this every step (rather
            // than parenting) means the piece tracks the claw through the flip and while driving.
            heldScratch.Clear();
            heldScratch.AddRange(held);
            Transform hold = HoldTf;
            float dt = Time.fixedDeltaTime;
            foreach (Held h in heldScratch)
            {
                if (h.rb == null) { Unmute(h); held.Remove(h); continue; }

                // Glide in the hold point's LOCAL frame, so the approach isn't fighting the claw's own
                // motion — a piece grabbed while the robot is driving still converges.
                if (snapSpeed > 0f)
                    h.localPos = Vector3.MoveTowards(h.localPos, h.targetPos, snapSpeed * dt);
                if (snapTurnSpeed > 0f)
                    h.localRot = Quaternion.RotateTowards(h.localRot, h.targetRot, snapTurnSpeed * dt);

                // Place the pivot so the piece's CENTER lands on the carried pose, using the same
                // rotation we're about to apply — so turning the piece can't swing its mesh off, even
                // though the pivot may sit well outside it.
                Quaternion worldRot = hold.rotation * h.localRot;
                h.rb.MovePosition(h.PositionFor(hold.TransformPoint(h.localPos), worldRot));
                h.rb.MoveRotation(worldRot);
            }
        }
        else if (held.Count > 0)
        {
            ReleaseAll();
        }

        if (logEvents && grabbing != wasGrabbing)
            Debug.Log($"ClawGrab: claw {(grabbing ? "closed" : "opened")} — holding {held.Count}.", this);
        wasGrabbing = grabbing;
    }

    void LateUpdate()
    {
        if (marker != null) marker.transform.position = HoldTf.position;
    }

    private bool IsGrabbing()
        => clampPneumatic != null && clampPneumatic.IsExtended != grabWhenRetracted;

    // Lock one piece to the claw at its current pose.
    private void Capture(Rigidbody rb)
    {
        if (rb == null || IsHeld(rb)) return;

        // A piece seated on a goal by GoalStackMagnet must leave that stack the moment the claw takes
        // it, or the magnet keeps counting (and re-seating) a piece the claw is carrying away.
        GoalStackMagnet.ReleaseIfSeated(rb);

        Transform hold = HoldTf;
        Held h = new Held
        {
            rb = rb,
            // Read the center of mass BEFORE anything else is touched: for these off-pivot field
            // pieces it IS the pivot->center offset, and PhysX recomputes it back to the pivot the
            // moment a piece's colliders stop counting — which would throw the offset away.
            localCom = rb.centerOfMass,
            localRot = Quaternion.Inverse(hold.rotation) * rb.rotation,
            wasKinematic = rb.isKinematic,
            wasDetectCollisions = rb.detectCollisions,
            wasInterpolation = rb.interpolation,
        };
        h.localPos = hold.InverseTransformPoint(h.CenterOf(rb.position, rb.rotation));

        // Where it settles. The first piece goes to the hold point itself; later ones keep the
        // arrangement they were caught in RELATIVE to the first, which is what preserves a
        // cup-stacked-on-a-pin instead of collapsing both into the same spot.
        if (held.Count == 0)
        {
            h.targetPos = Vector3.zero;
            h.targetRot = Quaternion.identity;
        }
        else
        {
            Held first = held[0];
            Quaternion firstInv = Quaternion.Inverse(first.localRot);
            h.targetPos = firstInv * (h.localPos - first.localPos);
            h.targetRot = firstInv * h.localRot;
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
            // pieces. Physics.IgnoreCollision errors on a collider that's disabled or on an inactive
            // object, so both sides are filtered — and the exact pairs are remembered, because a
            // collider's state can change while the piece is held and re-deriving the list at release
            // would miss (or invent) pairs.
            foreach (Collider pieceCol in rb.GetComponentsInChildren<Collider>(true))
            {
                if (!Usable(pieceCol) || pieceCol.isTrigger) continue;
                h.mutedPiece.Add(pieceCol);
            }
            foreach (Collider clawCol in clawColliders)
            {
                if (!Usable(clawCol)) continue;
                foreach (Collider pieceCol in h.mutedPiece) Physics.IgnoreCollision(pieceCol, clawCol, true);
                h.mutedClaw.Add(clawCol);
            }
        }

        // Deliberately NOT removed from inMouth here: with pass-through OFF the piece never leaves the
        // trigger, so OnTriggerEnter won't fire for it a second time, and dropping it would mean a
        // piece released inside the jaws could never be re-grabbed without backing the robot off it
        // first. (With pass-through ON, losing detectCollisions raises OnTriggerExit and the set
        // clears itself — then re-enters on release, which is exactly right.) IsHeld() is what
        // prevents double capture either way; inMouth stays pure trigger occupancy.
        held.Add(h);
        if (logEvents)
            Debug.Log($"ClawGrab: grabbed '{rb.name}' ({held.Count}/{maxHeld}) — it now rides the claw.", this);
    }

    private void ReleaseAll()
    {
        if (held.Count == 0) return;
        heldScratch.Clear();
        heldScratch.AddRange(held);
        foreach (Held h in heldScratch)
        {
            Unmute(h);
            if (h.rb == null) continue;

            h.rb.detectCollisions = h.wasDetectCollisions;
            h.rb.isKinematic = h.wasKinematic;
            h.rb.interpolation = h.wasInterpolation;

            // Drop it, don't throw it. MovePosition gives a kinematic body a velocity, and the glide
            // runs at snapSpeed — so a piece let go mid-approach would inherit that and rocket off.
            // A claw opening is a release, so zero is both safer and what it should look like.
            if (!h.rb.isKinematic)
            {
                h.rb.linearVelocity = Vector3.zero;
                h.rb.angularVelocity = Vector3.zero;
            }
            if (logEvents) Debug.Log($"ClawGrab: released '{h.rb.name}'.", this);
        }
        held.Clear();
    }

    // Restore exactly the piece-vs-claw pairs this grab muted. A pair whose collider has since been
    // destroyed, disabled or deactivated is skipped — IgnoreCollision would error on it, and PhysX
    // already forgot the pair when the collider went away.
    private void Unmute(Held h)
    {
        if (h == null) return;
        foreach (Collider pieceCol in h.mutedPiece)
        {
            if (!Usable(pieceCol)) continue;
            foreach (Collider clawCol in h.mutedClaw)
            {
                if (!Usable(clawCol)) continue;
                Physics.IgnoreCollision(pieceCol, clawCol, false);
            }
        }
        h.mutedPiece.Clear();
        h.mutedClaw.Clear();
    }

    // Physics.IgnoreCollision requires both colliders live, enabled and on active objects.
    private static bool Usable(Collider c) => c != null && c.enabled && c.gameObject.activeInHierarchy;

    private bool IsHeld(Rigidbody rb)
    {
        foreach (Held h in held) if (h.rb == rb) return true;
        return false;
    }

    // A plain unlit sphere at the hold point, so "where does the claw carry things" is visible in Play
    // without opening the inspector — same diagnostic the intake grew for the same reason.
    private void EnsureMarker()
    {
        if (marker != null) return;
        marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.name = "ClawHoldMarker";
        Destroy(marker.GetComponent<Collider>());
        marker.transform.localScale = Vector3.one * markerSize;
        Renderer r = marker.GetComponent<Renderer>();
        if (r != null) r.material.color = new Color(0.2f, 1f, 0.5f, 1f);
    }
}
