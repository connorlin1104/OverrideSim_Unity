using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Kinematic-glide intake with a capacity cap and lock-to-bot storage.
//
// Lifecycle per piece: a piece touching the mouth trigger while the intake runs forward is CAPTURED
// (up to maxHeld). The instant it's captured it's made KINEMATIC (so gravity, momentum, and knocks stop
// affecting it) and its colliders switch off (passThroughWhileHeld, so it passes THROUGH the wheels/
// frame instead of jamming). Each physics step it's moved in a straight line toward its assigned stack
// slot at glideSpeed — because it's kinematic it can't overshoot or orbit; it glides in and then STAYS
// exactly on the slot, riding rigidly with the bot no matter how fast you drive. When the intake is
// full, extra pieces simply aren't captured (they bump the intake's still-solid colliders).
//
// This replaced an earlier FORCE-based pull that sprang pieces toward the slot with AddForce. That
// orbited a moving target, fought gravity, and only "arrived" inside a tiny radius it kept overshooting,
// so pieces floated around the robot forever instead of settling. A kinematic glide is the fix.
//
// Stacking is BOTTOM-FED, like a real intake: a piece enters at the mouth (slot 0, the bottom) and shoves
// the current stack UP a slot, so the FIRST piece intaked rides on TOP. Ejecting removes the bottom slot
// and the next piece refills the bottom, so a partial stack keeps its top piece put across eject/refill.
//
// Reverse plays the intake BACKWARDS: held pieces are LAUNCHED out one at a time (bottom of the stack
// first, spaced by ejectInterval so they don't come out as a clump that overlaps and jams) — each sent
// flying outward in WORLD space as a free dynamic body (so it separates from the bot instead of clinging
// as you drive). A launched piece stays ghosted just long enough to travel ejectClearance clear of the
// rollers, then turns solid in the air. Because it leaves `held` immediately, its slot frees up right away
// (the intake can grab again at once) and it can never get stuck back on the stack. An intake KEEPS what
// it has picked up: letting go of the button stops it grabbing more, it doesn't dump the stack on the
// floor — reverse to eject, which is the deliberate act. (dropWhenIdle turns the old momentary behaviour
// back on for a mechanism that really should spill when you let go.)
//
// REVERSE HAS TWO FLAVOURS, and the second one is how most real scoring mechanisms work. Launching is
// right for a roller intake that has to spit a piece clear of itself. But a basket or claw carried up on
// an arm/chain scores by reversing and simply LETTING GO — the cup or pin becomes an ordinary physical
// object again and falls out under gravity. reverseDropsInPlace is that: no launch velocity, no ghost
// window (a piece that barely moves would stay ghosted straight through the goal it is meant to land on,
// so it turns solid immediately), and no outward shove on loose pieces sitting in the mouth — that shove
// would kick away the very piece just dropped. The one thing to watch is a hold point buried inside
// plastic: the piece turns solid interpenetrating it and PhysX pops it out. Place hold points where a
// piece can actually fall.
//
// TWO INTAKES CAN HAND A PIECE OVER, and they have to, because a carried piece is INVISIBLE. The instant
// an intake captures a piece the piece goes kinematic with its colliders switched OFF, so it fires no
// trigger and no overlap query can find it — a second intake looking for something to grab sees nothing
// there at all. That is why a bot that gathers at the floor and scores from an arm could not load its own
// scoring mechanism: the floor intake's stack had stopped counting as pieces. So every carried piece is
// registered in `carriers` (which intake is holding what) — that is how a piece stays KNOWN while it is
// non-physical. Hold the second intake's button with its mouth over where the first one is carrying and
// the piece is handed across: it stays kinematic and ghosted the whole way, so there is no step where it
// can drop or bounce, and the taker inherits the two facts it could no longer measure for itself (the
// pre-ghost centre of mass and the pre-capture kinematic flag). takeFromOtherIntakes turns this off, and
// a short cooldown stops two overlapping mouths trading one piece back and forth every step.
//
// As pieces come in they're rotated so they stop tumbling and stack cleanly, IN THE SLOT ANCHOR'S
// FRAME: rotate a slot marker and the piece in that slot rotates with it — tilt it and the piece
// tilts, twist it and the piece twists. By default (autoUpright) each piece is also stood UP by
// geometry — its longest mesh axis is aligned to the slot's up, measured per instance, so pins that
// are each baked at a different child tilt all end up standing (the field's pins share ONE mesh but
// sit at ~a dozen different rotations, so no single per-type angle could fix them). The attitude is
// solved ONCE at capture and replayed anchor-relative from then on — re-solving each step made the
// shortest-arc answer jump around as the bot moved (the same bug ClawGrab's header documents).
// Stack slots default to a straight line (stackAxis × slotSpacing) but can be overridden per slot by
// slotAnchors — draggable/rotatable Transforms that place AND angle each slot.
//
// NO ANCHOR MAY HANG OFF A FREE-SPINNING LINK. The mouth, the hold point and the stack slots must not
// be children of the roller itself — a spinning target whirls around at Play and drags pieces to random
// points. The Build Intake tool mounts them beside the roller, and this component also SELF-HEALS at
// play start: any anchor sitting under a free-spinning link is re-anchored to the chassis, with a warning
// (stabilizeHoldPoint). A LIMITED link is a different thing entirely and is left alone — bolt the whole
// intake to a pivoting arm, a wrist or a lift stage and every anchor rides it, which is the only way an
// intake on an arm can work at all. Select the mouth to see the editor-only gizmos; the Build Intake window adds
// full drag/rotate handles. World is 10x scale, gravity ~-98, pieces mass 1 — but slot spacing and
// glide speed are all in WORLD units.
//
// Pieces are aimed by their CENTER OF MASS, not their transform pivot. The field's Cup*/Pin* pieces were
// split from one field FBX without re-centering, so each keeps the CAD origin as its pivot — ~9-15 world
// units from the actual mesh. Aiming the pivot at the hold point would leave the visible mesh that far
// off. `Held.localCom` (captured at grab, BEFORE colliders are ghosted, since disabling colliders makes
// PhysX recompute the COM back to the pivot) is the pivot→center offset we use to place the mesh exactly.
public class IntakePull : MonoBehaviour
{
    [Tooltip("The intake's motor. Its CurrentInput drives the intake: forward = grab/pull in, reverse = eject. Auto-found on this object's parents if empty.")]
    public MotorActuator intakeMotor;

    [Header("Lift interlock & scoring")]
    [Tooltip("The DR4B lift (optional). While it's RAISED, BOTH intake and outtake are disabled (a grabbed piece would just float up, and the stack leaves via Score, not the mouth); the Score button only drops while it's raised. Wired by Build DR4B Lift.")]
    public Dr4bLift lift;
    [Tooltip("Lift progress (0..1) above which the lift counts as 'raised' for the interlock.")]
    [Range(0f, 1f)] public float liftRaisedThreshold = 0.15f;
    [Tooltip("Button that DROPS the held stack to SCORE (only while the lift is raised). Set by Build DR4B Lift.")]
    public InputActionReference scoreAction;

    [Tooltip("Where captured pieces glide to and stack. The Build Intake tool creates an IntakeHoldPoint you can drag AND rotate (its rotation sets how the seated piece sits); if empty it falls back to this object's position.")]
    public Transform holdPoint;

    [Header("Direction")]
    [Tooltip("Flip if the button that should GRAB pieces instead spits them out.")]
    public bool reverseDirection;
    [Tooltip("Ignore |input| below this so a barely-held button doesn't intake.")]
    [Range(0f, 1f)] public float inputThreshold = 0.05f;

    [Header("Capacity & storage")]
    [Tooltip("How many pieces the intake holds before it's full and stops grabbing.")]
    public int maxHeld = 3;
    [Tooltip("Gap between stacked pieces, in WORLD units, along the stack axis. Slot 0 is the hold point itself.")]
    public float slotSpacing = 1.5f;
    [Tooltip("Direction (local to the hold point) that stored pieces stack along. Spacing is scale-independent.")]
    public Vector3 stackAxis = Vector3.up;
    [Tooltip("Optional per-slot anchors — drag one Transform per stack position to lay out THIS model's stack exactly (angled or flat). Slot 0 is the hold point; ROTATING an anchor also sets how the piece in that slot sits. Empty/missing entries fall back to the stackAxis line. The Build Intake tool creates these as draggable IntakeSlot points.")]
    public Transform[] slotAnchors;

    [Header("Handoff between intakes")]
    [Tooltip("ON (default): this intake can take a piece straight out of ANOTHER intake — hold this one's button with its mouth over where the other one is carrying, and the piece is handed across. That is how a bot gathers at the floor and then loads a scoring mechanism: a carried piece is kinematic with its colliders off, so it fires no trigger and this intake would otherwise be blind to it. OFF: this intake only picks up loose pieces off the field.")]
    public bool takeFromOtherIntakes = true;

    [Header("Hold behavior")]
    // A NEW field name on purpose, and inverted. The old flag was keepHeldWhenIdle, default OFF, and
    // both shipped robots have `keepHeldWhenIdle: 0` in their YAML — a prefab's saved value always
    // beats a changed C# default, so flipping the default would have reached no existing intake. A
    // field that isn't in the prefab's YAML at all deserializes to its C# default, which is what lets
    // this land on every robot without touching a prefab. (Same trick as the drive-feel retune.)
    [Tooltip("OFF (default): the intake keeps what it has picked up when you let go of the button — reverse to eject. ON: momentary, so releasing the button drops the stack on the floor.")]
    public bool dropWhenIdle = false;
    [Tooltip("While a piece is held, switch OFF its colliders so it passes through the wheels/frame and can't shove the bot. Restored on release.")]
    public bool passThroughWhileHeld = true;

    [Header("Glide (all in WORLD units — world is 10x scale)")]
    [Tooltip("How fast a captured piece glides to its slot (world units/sec). It's kinematic, so this can't overshoot; higher = snappier. Reverse-eject reuses this speed to glide pieces back out the mouth.")]
    public float glideSpeed = 24f;
    [Tooltip("Also rotate a captured piece to its slot's seated orientation as it comes in, so it stops tumbling. Rotating a slot anchor rotates how its piece sits.")]
    public bool rotateToHold = true;
    [Tooltip("How fast a captured piece rotates to its seated orientation (degrees/sec).")]
    public float rotateSpeed = 720f;
    [Tooltip("RECOMMENDED. Additionally stand each captured piece UP along its slot's up axis by aligning its longest mesh axis — computed PER PIECE from geometry, so it handles pieces whose tilt is baked differently into every instance (e.g. the field's pins: they share one mesh but each sits at a different child rotation). Off = the piece keeps the attitude it was caught at, still riding the slot's frame.")]
    public bool autoUpright = true;
    [Tooltip("Advanced: which axis of the MESH is the piece's 'up' (its long/standing axis). Leave (0,0,0) to auto-pick the longest mesh-bounds axis. Set e.g. (0,1,0) if auto-pick stands a piece up the wrong way.")]
    public Vector3 uprightMeshAxis = Vector3.zero;

    [Header("Eject")]
    [Tooltip("ON: reverse just LETS GO — each piece turns back into a physical object exactly where it sits and GRAVITY does the rest, the way a real scoring mechanism dumps (reverse, and the cup/pin falls out). Nothing is launched and nothing loose is shoved, so Eject Speed / Clearance / Acceleration below are ignored. OFF (default): pieces are thrown out through the mouth. Use ON for a basket or claw carried on an arm or chain, OFF for a roller intake that has to spit pieces clear of itself.")]
    public bool reverseDropsInPlace;
    [Tooltip("Seconds between releasing one piece and the next while reverse is held — pieces leave ONE AT A TIME (bottom of the stack first) so they don't clump together, overlap and jam. Tap reverse for just one. Set 0 to dump the whole stack at once.")]
    public float ejectInterval = 0.2f;
    [Tooltip("The world velocity each piece is launched with on eject (world units/sec). Kept separate from Glide Speed so eject stays snappy even if intake glide is slow. World is 10x scale. Ignored when Reverse Drops In Place is on — a drop has no launch.")]
    public float ejectSpeed = 40f;
    [Tooltip("Keep an ejected piece ghosted (phasing through the frame) until it has flown this many WORLD units from where it launched, THEN it turns solid. Raise it if pieces re-solidify too soon and clip/jam on the bot; lower it if they phase through things too long. World is 10x scale. Ignored when Reverse Drops In Place is on — a dropped piece turns solid at once, so it can't fall through the goal.")]
    public float ejectClearance = 6f;
    [Tooltip("Extra outward shove given to loose (uncaptured) pieces sitting in the mouth on reverse (acceleration; must beat gravity ~98 to arc out). Ignored when Reverse Drops In Place is on — reverse then only lets go, it never pushes.")]
    public float ejectAcceleration = 300f;

    [Header("Stability & debug")]
    [Tooltip("At play start, if the mouth, hold point or any stack slot hangs off a FREE-SPINNING link (the roller itself), re-anchor it to the rigid chassis so it can't whirl around. Also logs a warning telling you to fix the prefab. A limited arm/wrist/lift link is left alone — an intake mounted on one is meant to ride it.")]
    public bool stabilizeHoldPoint = true;
    [Tooltip("Log a startup diagnostic (where the hold point actually is + its hierarchy path) and one line per capture/arrive/release/eject. Turn off once it's working.")]
    public bool logEvents = true;

    // One held piece: its stack slot, whether it has finished gliding in, its pre-capture kinematic state
    // (so a piece that was somehow kinematic before is restored correctly on release), its seat
    // attitude relative to the slot anchor — solved once at capture, replayed every step — and when it
    // was taken off another intake (NegativeInfinity for a piece grabbed off the field, so the handoff
    // cooldown only ever applies to a piece that actually arrived by handoff).
    private class Held { public Rigidbody rb; public int slot; public bool arrived; public bool wasKinematic; public Vector3 localCom; public Quaternion anchorLocalRot; public float takenAt; }

    // A piece just handed over can't be handed straight back: two intakes whose mouths overlap would
    // otherwise trade the same piece every physics step for as long as both buttons are held.
    private const float HandoffCooldown = 0.35f;

    // How far outside the mouth a carried piece can be and still be worth mentioning in the log — far
    // enough to catch "I aimed at it and nothing happened", near enough not to report the whole field.
    private const float HandoffLogRange = 12f;

    // Every piece ANY intake is currently carrying, and which intake is carrying it. A carried piece has
    // its colliders off, so no trigger and no overlap query can see it — this registry is the only thing
    // that still knows the piece exists, and it is what lets one intake hand a piece to another (and what
    // stops two of them carrying the same piece at once).
    private static readonly Dictionary<Rigidbody, IntakePull> carriers = new Dictionary<Rigidbody, IntakePull>();

    // A piece ejected and flying out as a ghosted projectile, re-solidified once it has travelled clear of
    // the mouth. Kept OUT of `held` so the intake can grab again immediately.
    private class Ejected { public Rigidbody rb; public Vector3 launchPos; }

    // Pieces overlapping the mouth, counted (a cup/pin has several child colliders → several triggers).
    private readonly Dictionary<Rigidbody, int> inMouth = new Dictionary<Rigidbody, int>();
    private readonly List<Held> held = new List<Held>();
    private readonly List<Ejected> ejected = new List<Ejected>();
    private readonly List<Rigidbody> scratch = new List<Rigidbody>();
    private readonly List<Held> heldScratch = new List<Held>();
    private readonly List<Rigidbody> handoffScratch = new List<Rigidbody>();
    private float lastEjectTime;   // when the last piece was launched, for the eject-one-at-a-time spacing
    private float lastReachLog = float.NegativeInfinity;   // rate limit for the "outside the mouth" hint
    private Collider mouthCol;

    private Transform HoldTf => holdPoint != null ? holdPoint : transform;
    private Vector3 StackDir => stackAxis.sqrMagnitude > 1e-6f ? stackAxis.normalized : Vector3.up;

    // A per-slot anchor if one is assigned (drag them to lay out the stack), else null → use the line below.
    private Transform SlotAnchor(int slot) =>
        (slotAnchors != null && slot >= 0 && slot < slotAnchors.Length) ? slotAnchors[slot] : null;

    // Slot world position: the slot's anchor if set, else hold point + a rotation-only offset along the
    // stack axis (spacing is real WORLD units, NOT multiplied by the robot's ~10x scale). Live, rides the bot.
    private Vector3 SlotWorldPos(int slot)
    {
        Transform a = SlotAnchor(slot);
        if (a != null) return a.position;
        return HoldTf.position + HoldTf.rotation * (StackDir * (slot * slotSpacing));
    }

    // The frame a piece in this slot is seated in: the slot anchor's rotation if set (so rotating
    // an anchor rotates its piece), else the hold point's.
    private Quaternion SlotAnchorRot(int slot)
    {
        Transform a = SlotAnchor(slot);
        return a != null ? a.rotation : HoldTf.rotation;
    }

    // The piece's "standing" axis expressed in its RIGIDBODY-local frame, for Auto Upright.
    // Returns zero if there's no mesh to measure (Auto Upright then leaves that piece's rotation alone).
    private Vector3 ComputeUpAxis(Rigidbody rb) => PieceGeometry.MeasureUpAxis(rb, uprightMeshAxis);

    // Mouth (grab-zone) center in world — the trigger box's center, i.e. where the yellow mouth marker is.
    // Reverse-eject glides pieces back out through this point before shoving them clear.
    private Vector3 MouthWorldPos()
    {
        if (MouthCol is BoxCollider box) return transform.TransformPoint(box.center);
        return transform.position;
    }

    // The trigger collider that IS the mouth. Cached, but re-resolved if it goes away, so a validator
    // (or a user) adding the collider after the component still gets a working mouth.
    private Collider MouthCol => mouthCol != null ? mouthCol : (mouthCol = GetComponent<Collider>());

    void Reset()
    {
        Collider col = GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
    }

    void Awake()
    {
        if (intakeMotor == null) intakeMotor = GetComponentInParent<MotorActuator>();
        if (GetComponent<Collider>() == null)
            Debug.LogWarning("IntakePull: no Collider on this object — add a trigger collider to define the intake mouth zone.", this);
        if (intakeMotor == null)
            Debug.LogWarning("IntakePull: no MotorActuator assigned or found in parents — the intake will never activate.", this);

        // Resolve the motor BEFORE re-anchoring (re-anchoring moves us off the roller, out of the
        // motor's parent chain). Then stabilize so the hold point can never whirl with the roller.
        if (stabilizeHoldPoint) StabilizeAnchors();
        if (logEvents) LogStartupDiagnostics();
    }

    void OnEnable()
    {
        if (scoreAction != null && scoreAction.action != null)
        {
            scoreAction.action.performed += OnScorePerformed;
            scoreAction.action.Enable();
        }
    }

    void OnDisable()
    {
        if (scoreAction != null && scoreAction.action != null)
            scoreAction.action.performed -= OnScorePerformed;

        // Never leave a piece kinematic/ghosted if this component switches off or unloads — solidify held
        // pieces and un-ghost any still-flying ejected ones.
        heldScratch.Clear();
        heldScratch.AddRange(held);
        foreach (Held h in heldScratch) Solidify(h);
        held.Clear();
        foreach (Ejected e in ejected) if (e.rb != null) SetPieceColliders(e.rb, true);
        ejected.Clear();
    }

    void OnTriggerEnter(Collider other)
    {
        // attachedRigidbody is null for the robot's ArticulationBodies, so the robot never matches.
        Rigidbody rb = other.attachedRigidbody;
        if (rb == null || !IsPiece(rb.gameObject)) return;
        inMouth.TryGetValue(rb, out int c);
        inMouth[rb] = c + 1;
    }

    void OnTriggerExit(Collider other)
    {
        Rigidbody rb = other.attachedRigidbody;
        if (rb == null || !inMouth.TryGetValue(rb, out int c)) return;
        if (c <= 1) inMouth.Remove(rb);
        else inMouth[rb] = c - 1;
    }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;

        // Re-solidify ejected pieces once they've flown clear (runs every step, independent of input).
        UpdateEjected();

        if (intakeMotor == null) return;

        float input = intakeMotor.CurrentInput;
        if (reverseDirection) input = -input;
        bool intaking = input > inputThreshold;
        bool ejecting = input < -inputThreshold;

        // Lift interlock: while the DR4B is RAISED, disable BOTH intake and outtake — a grabbed piece would
        // just float up, and the stack is meant to leave via the Score button (drops it onto the goal), not
        // get spat back out the mouth. (Score is gated the other way: it only fires while raised.)
        if (lift != null && lift.Progress > liftRaisedThreshold) { intaking = false; ejecting = false; }

        if (ejecting)
        {
            Vector3 mouth = MouthWorldPos();
            Vector3 outward = mouth - HoldTf.position;
            outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : -transform.forward;

            if (ejectInterval <= 0f)
            {
                // Dump the whole stack at once.
                heldScratch.Clear();
                heldScratch.AddRange(held);
                foreach (Held h in heldScratch) EjectOne(h, outward);
            }
            else if (held.Count > 0 && Time.time - lastEjectTime >= ejectInterval)
            {
                // One at a time, bottom of the stack first, spaced by ejectInterval so they don't clump.
                EjectOne(LowestSlotHeld(), outward);
                lastEjectTime = Time.time;
            }

            // Keep the not-yet-ejected pieces riding on their slots so they don't detach while they wait.
            heldScratch.Clear();
            heldScratch.AddRange(held);
            foreach (Held h in heldScratch)
                if (h.rb != null) CarryTo(h, SlotWorldPos(h.slot), false, dt);

            // Shove any loose (uncaptured) pieces sitting in the mouth out too — but never when reverse
            // only DROPS: a piece released a moment ago is solid again and usually still inside the mouth
            // trigger, so the shove would kick away the piece gravity is supposed to be taking.
            if (!reverseDropsInPlace)
            {
                scratch.Clear();
                scratch.AddRange(inMouth.Keys);
                foreach (Rigidbody rb in scratch) if (rb != null) PushOut(rb, HoldTf.position);
            }
            return;   // don't capture while reversing
        }

        // Grab pieces at the mouth, up to capacity.
        if (intaking)
        {
            scratch.Clear();
            scratch.AddRange(inMouth.Keys);
            foreach (Rigidbody rb in scratch)
            {
                if (held.Count >= maxHeld) break;
                Capture(rb);
            }

            // Loose pieces come in through the trigger above; a piece another intake is already carrying
            // can only come in this way, because it has no colliders left to trip a trigger with.
            TakeHandoffs();
        }

        bool holding = intaking || !dropWhenIdle;
        if (!holding) { ReleaseAll(); return; }   // momentary: idle drops everything (committed ejects still finish)
        if (held.Count == 0) return;

        // Glide each held piece straight to its slot and hold it there. Kinematic → no gravity, no
        // overshoot, no orbit. Once arrived it snaps to the (bot-relative) slot every step, so it rides
        // rigidly even when the bot drives faster than glideSpeed.
        heldScratch.Clear();
        heldScratch.AddRange(held);
        foreach (Held h in heldScratch)
        {
            if (h.rb == null) { held.Remove(h); Forget(h.rb); continue; }

            Vector3 slot = SlotWorldPos(h.slot);
            Vector3 nextCom = CarryTo(h, slot, !h.arrived, dt);   // glides until it arrives, then snaps to the slot

            if (!h.arrived && (nextCom - slot).sqrMagnitude < 1e-4f)
            {
                h.arrived = true;
                if (logEvents)
                {
                    // curCom uses the stored localCom (rb.worldCenterOfMass is unreliable while colliders
                    // are ghosted). pivotΔ shows why aiming the pivot looked wrong; comΔ shows it's fixed.
                    Rigidbody rb = h.rb;
                    Vector3 curCom = rb.position + rb.rotation * h.localCom;
                    float pivotDelta = (rb.position - slot).magnitude;
                    float comDelta = (curCom - slot).magnitude;
                    Debug.Log($"IntakePull: '{rb.name}' arrived at slot {h.slot} — center locked to the bot. " +
                              $"comΔ={comDelta:0.###}u (on the marker), pivotΔ={pivotDelta:0.#}u (the piece's off-center pivot).", this);
                }
            }
        }
    }

    // Carry one held piece toward a target CENTER-OF-MASS position, easing it to its seated
    // orientation — the slot anchor's frame times the attitude solved at capture, so tilting or
    // twisting a slot marker carries the piece with it. Works in center-of-mass space so the
    // visible mesh — not the off-center pivot — lands on the target: the pivot is placed from the
    // SAME rotation we apply, so rotating the piece can't swing the mesh off, even though the
    // pivot is 9-15u away. Returns the piece's center after this step. Shared by the intake hold
    // loop and the parked pieces mid-eject.
    private Vector3 CarryTo(Held h, Vector3 targetCom, bool glide, float dt)
    {
        Rigidbody rb = h.rb;
        Quaternion target = SlotAnchorRot(h.slot) * h.anchorLocalRot;
        Quaternion desiredRot = rotateToHold
            ? Quaternion.RotateTowards(rb.rotation, target, rotateSpeed * dt)
            : rb.rotation;
        Vector3 curCom = rb.position + rb.rotation * h.localCom;
        Vector3 nextCom = glide ? Vector3.MoveTowards(curCom, targetCom, glideSpeed * dt) : targetCom;
        rb.MovePosition(nextCom - desiredRot * h.localCom);   // pivot placed so the center hits nextCom
        if (rotateToHold) rb.MoveRotation(desiredRot);
        return nextCom;
    }

    // Begin holding a piece: make it kinematic (so it glides cleanly, immune to gravity/knocks) and ghost
    // it so it passes through the CAD. Drops it into the bottom slot, pushing the stack up (bottom-fed).
    //
    // `from` is the record another intake was carrying this piece under, and is set only on the handoff
    // path. It matters more than it looks: a piece that is already ghosted can no longer be MEASURED
    // (see localCom/wasKinematic below), so those two facts travel with it instead of being re-read.
    //
    // Public so the headless validator can drive the real capture instead of a copy of it.
    public bool TryCapture(Rigidbody rb) { int before = held.Count; Capture(rb); return held.Count > before; }

    private void Capture(Rigidbody rb, Held from = null)
    {
        if (rb == null || held.Count >= maxHeld || IsHeld(rb)) return;

        // Never quietly take a piece out of another intake's stack: that has to go through the handoff,
        // which is what makes the other one let go. (A carried piece can't reach the trigger path at all,
        // so this only guards a hand-written call.)
        if (from == null && carriers.TryGetValue(rb, out IntakePull owner) && owner != null && owner != this) return;

        // Bottom-fed magazine: a piece enters at the MOUTH (slot 0, the bottom). If slot 0 is occupied,
        // shove the current stack UP one slot to make room underneath, so the FIRST piece intaked ends up
        // on TOP — like a real intake, instead of the first piece sitting on the bottom. When slot 0 is
        // already free (e.g. right after an eject), nothing shifts: the new piece just drops into the
        // bottom, leaving the eject-then-refill behavior unchanged (eject the bottom, next piece to bottom).
        int free = NextFreeSlot();
        if (free < 0) return;
        ShiftUpBelow(free);
        int slot = 0;
        inMouth.Remove(rb);

        // Descoring: a piece seated on a goal by GoalStackMagnet must leave the goal's stack the
        // moment the intake takes it, or the magnet would keep counting (and re-slotting) it.
        GoalStackMagnet.ReleaseIfSeated(rb);

        // Read the center of mass BEFORE ghosting — disabling colliders makes PhysX recompute the COM to
        // the pivot, which for these off-pivot field pieces would throw the offset away. A piece taken off
        // another intake is ALREADY ghosted, so re-reading it here would give exactly that thrown-away
        // value (the pivot, 9-15u from the mesh) and the piece would jump; inherit it instead.
        Vector3 localCom = from != null ? from.localCom : rb.centerOfMass;

        // Measure this piece's standing axis in its own local frame, so Auto Upright can stand it up no
        // matter how its mesh happens to be tilted (every field pin is baked at a different child rotation).
        Vector3 localUpAxis = autoUpright ? ComputeUpAxis(rb) : Vector3.zero;

        // Seat attitude, solved ONCE and stored relative to the slot anchor: in any slot the piece
        // then rides at SlotAnchorRot(slot) * anchorLocalRot, so tilting/twisting a slot marker
        // carries the piece with it, and a stack shift re-expresses the same attitude in the new
        // slot's frame. autoUpright composes the SMALLEST arc that stands the measured axis along
        // the slot's up without discarding the caught pose — the ClawGrab.StandUpRotation recipe,
        // end-flip guard included, so a pin grabbed nose-down doesn't seat upside-down.
        Quaternion anchorRot = SlotAnchorRot(0);   // bottom-fed: a capture always enters slot 0
        Quaternion seatedWorld = rb.rotation;
        if (autoUpright && localUpAxis.sqrMagnitude > 1e-6f)
        {
            Vector3 up = anchorRot * Vector3.up;
            Vector3 worldAxis = rb.rotation * localUpAxis;
            if (Vector3.Dot(worldAxis, up) < 0f) worldAxis = -worldAxis;   // stand on the end already uppermost
            seatedWorld = Quaternion.FromToRotation(worldAxis, up) * rb.rotation;
        }
        Quaternion anchorLocalRot = Quaternion.Inverse(anchorRot) * seatedWorld;

        // Same story as the centre of mass: the piece is kinematic RIGHT NOW because the other intake made
        // it so, so reading it here would record "was kinematic" and the piece would never fall again.
        bool wasKinematic = from != null ? from.wasKinematic : rb.isKinematic;
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;   // smooth the glide/carry between physics steps
        if (passThroughWhileHeld) SetPieceColliders(rb, false);

        held.Add(new Held { rb = rb, slot = slot, arrived = false, wasKinematic = wasKinematic, localCom = localCom,
                            anchorLocalRot = anchorLocalRot, takenAt = from != null ? Time.time : float.NegativeInfinity });
        Track(rb);
        if (logEvents) Debug.Log($"IntakePull: {(from != null ? "TOOK" : "captured")} '{rb.name}' → slot {slot} (holding {held.Count}/{maxHeld}); gliding its center (pivot→center offset {localCom.magnitude:0.#}u) to {SlotWorldPos(slot)}." +
                                 (autoUpright ? (localUpAxis.sqrMagnitude > 1e-6f ? " Auto-upright ON." : " Auto-upright ON but NO MESH found to measure — piece keeps its caught attitude.") : ""), this);
    }

    // ---------------------------------------------------------------------------------------------
    // Handoff: one intake gathers, another lifts and scores.
    // ---------------------------------------------------------------------------------------------

    // Take a piece another intake is carrying, if this mouth covers where it is being carried. A carried
    // piece is kinematic with its colliders OFF: it trips no trigger, and Physics.Overlap* cannot find it
    // either, so this is the one capture path that does not go through PhysX at all — the mouth box is
    // measured directly against the piece's centre. This is the floor-intake → scoring-mechanism handoff.
    //
    // Public so the headless validator can drive the real thing rather than a copy of its logic.
    public void TakeHandoffs()
    {
        if (!takeFromOtherIntakes || carriers.Count == 0 || held.Count >= maxHeld) return;

        // Snapshot the keys: relinquishing mutates the registry, and one of the carriers might be us.
        handoffScratch.Clear();
        foreach (KeyValuePair<Rigidbody, IntakePull> kv in carriers)
            if (kv.Value != this) handoffScratch.Add(kv.Key);

        foreach (Rigidbody rb in handoffScratch)
        {
            if (held.Count >= maxHeld) return;
            if (rb == null) { Forget(rb); continue; }                        // destroyed piece — prune it
            if (!carriers.TryGetValue(rb, out IntakePull from) || from == null || from == this) continue;
            if (!MouthCovers(from.CarriedCenter(rb), out float miss)) { NoteOutOfReach(rb, miss); continue; }

            Held taken = from.Relinquish(rb);
            if (taken != null) Capture(rb, taken);
        }
    }

    // Hand a piece over to another intake. It stays kinematic and ghosted the whole way across — there is
    // no step in which it is a loose physical object, so it cannot drop, bounce or clip through anything
    // mid-handoff. Refused for a moment after WE took it (see HandoffCooldown). Returns the record the
    // taker needs, or null if we aren't carrying this piece or won't give it up yet.
    private Held Relinquish(Rigidbody rb)
    {
        for (int i = 0; i < held.Count; i++)
        {
            Held h = held[i];
            if (h.rb != rb) continue;
            if (Time.time - h.takenAt < HandoffCooldown) return null;
            held.RemoveAt(i);
            Forget(rb);
            if (logEvents) Debug.Log($"IntakePull[{name}]: handed '{rb.name}' over to another intake.", this);
            return h;
        }
        return null;
    }

    // Where a piece we are carrying actually IS: its centre of mass, rebuilt from the offset captured
    // before it was ghosted. rb.worldCenterOfMass is no use here — with the colliders off PhysX has
    // recomputed the centre back to the pivot, which for these field pieces is 9-15u from the mesh.
    // Public alongside IsCarrying so the validator can ask both questions of the real component.
    public Vector3 CarriedCenter(Rigidbody rb)
    {
        foreach (Held h in held) if (h.rb == rb) return rb.position + rb.rotation * h.localCom;
        return rb.position;
    }

    // Is a world point inside the mouth zone? Both sides of this test are invisible to PhysX (the mouth is
    // a trigger, the piece has no colliders), so the box is measured by hand. `miss` is how far OUTSIDE
    // the box the point is, in world units — 0 when it's inside — which is what the hint below reports.
    private bool MouthCovers(Vector3 worldPoint, out float miss)
    {
        miss = float.PositiveInfinity;
        Collider col = MouthCol;
        if (col == null) return false;

        if (col is BoxCollider box)
        {
            Vector3 d = transform.InverseTransformPoint(worldPoint) - box.center;
            Vector3 half = box.size * 0.5f;
            Vector3 over = Vector3.Max(Vector3.zero, new Vector3(Mathf.Abs(d.x) - half.x,
                Mathf.Abs(d.y) - half.y, Mathf.Abs(d.z) - half.z));
            // Local → world: the mouth lives inside the robot's ~10x scale like everything else, so the
            // overshoot has to be scaled before it means anything in world units.
            miss = Vector3.Scale(over, transform.lossyScale).magnitude;
            return over == Vector3.zero;
        }

        miss = (col.ClosestPoint(worldPoint) - worldPoint).magnitude;   // 0 inside, for any convex shape
        return miss <= 1e-4f;
    }

    // "I pressed the button and nothing happened": say so, once a second, when the piece the player is
    // plainly aiming at is being carried just outside this mouth. Cheap to leave on — it only fires while
    // the button is held, and only for a piece already in another intake's hands.
    private void NoteOutOfReach(Rigidbody rb, float miss)
    {
        if (!logEvents || miss > HandoffLogRange || Time.time - lastReachLog < 1f) return;
        lastReachLog = Time.time;
        Debug.Log($"IntakePull[{name}]: '{rb.name}' is held by another intake {miss:0.#}u OUTSIDE this " +
                  "mouth box, so there is nothing here to take. Move this intake's mouth over where the " +
                  "other one carries its stack (or make the mouth bigger) — the yellow box is the zone.", this);
    }

    // Registry bookkeeping. Forget() deliberately compares with (object) rather than ==: a DESTROYED
    // Rigidbody reads as null through Unity's operator, and skipping it there would leave the dead key in
    // the dictionary forever.
    private void Track(Rigidbody rb) { if (rb != null) carriers[rb] = this; }

    private void Forget(Rigidbody rb)
    {
        if ((object)rb == null) return;
        if (!carriers.TryGetValue(rb, out IntakePull c)) return;
        if (c == this || c == null || rb == null) carriers.Remove(rb);   // ours, or orphaned, or destroyed
    }

    // Which intake is carrying this piece, if any — the answer that used to be unobtainable once a piece
    // went kinematic. Null means it is loose on the field (or gone).
    public static IntakePull CarrierOf(Rigidbody rb) =>
        rb != null && carriers.TryGetValue(rb, out IntakePull c) ? c : null;

    // Restore one piece's dynamics and colliders (no list change). Shared by Release and OnDisable.
    private void Solidify(Held h)
    {
        Rigidbody rb = h.rb;
        Forget(rb);                      // physical again — no longer a piece anyone is carrying
        if (rb == null) return;
        rb.isKinematic = h.wasKinematic;
        if (passThroughWhileHeld) SetPieceColliders(rb, true);
    }

    // Stop holding one piece: restore it and free its slot.
    private void Release(Held h)
    {
        Solidify(h);
        if (h.rb != null && logEvents) Debug.Log($"IntakePull: released '{h.rb.name}'.", this);
        held.Remove(h);
    }

    // Momentary idle: drop everything held (ejected pieces are already out of `held` and finish on their own).
    private void ReleaseAll()
    {
        if (held.Count == 0) return;
        heldScratch.Clear();
        heldScratch.AddRange(held);
        foreach (Held h in heldScratch) Release(h);
    }

    // SCORE: drop the held stack straight down (release to gravity) so it falls onto the goal from up top.
    // Unlike eject, there's no outward launch back at the mouth. Only works while the lift is RAISED — with
    // the lift down there's nothing to score onto (and it would just dump at the intake). Bound to the score button.
    public void ScoreDrop()
    {
        if (lift != null && lift.Progress <= liftRaisedThreshold) return;
        if (held.Count == 0) return;
        ReleaseAll();
        if (logEvents) Debug.Log("IntakePull: scored — dropped the held stack.", this);
    }

    private void OnScorePerformed(InputAction.CallbackContext ctx) => ScoreDrop();

    // One piece leaves the stack. Two ways out, chosen by reverseDropsInPlace: thrown clear of the mouth
    // (a roller intake), or simply handed back to physics where it sits (a scoring mechanism reversing and
    // letting gravity take the piece). Both are committed the moment they're called.
    private void EjectOne(Held h, Vector3 outward)
    {
        if (reverseDropsInPlace) DropInPlace(h);
        else LaunchOut(h, outward);
    }

    // Reverse as a real scoring mechanism does it: stop holding the piece, hand it back to physics exactly
    // where it is, and let gravity do the work — no launch velocity, nothing pushed. It turns solid
    // IMMEDIATELY, unlike a launched piece: a piece that is only falling out would sit inside the
    // ejectClearance radius for a while, and staying ghosted that long would drop it straight through the
    // stake or goal it is aimed at. This is the same release path as ScoreDrop, one piece at a time.
    private void DropInPlace(Held h)
    {
        held.Remove(h);
        Solidify(h);
        if (h.rb != null && logEvents)
            Debug.Log($"IntakePull: dropped '{h.rb.name}' in place — physical again, gravity takes it from here.", this);
    }

    // Eject one piece: pull it from `held` NOW (freeing its slot), make it a free dynamic body flying
    // outward in WORLD space (so it separates from the bot instead of clinging), and — if it was ghosted
    // while held — hand it to `ejected` to re-solidify once it's clear of the rollers. Committed the moment
    // it's called: it finishes on its own regardless of the button, and can never get stuck back on a slot.
    private void LaunchOut(Held h, Vector3 outward)
    {
        held.Remove(h);
        Rigidbody rb = h.rb;
        Forget(rb);
        if (rb == null) return;
        rb.isKinematic = h.wasKinematic;                              // free body again (was kinematic while held)
        if (!rb.isKinematic)
        {
            rb.AddForce(outward * ejectSpeed, ForceMode.VelocityChange);   // fly straight out
            rb.angularVelocity = Vector3.zero;
        }
        if (passThroughWhileHeld) ejected.Add(new Ejected { rb = rb, launchPos = rb.position });  // stay ghosted till clear
        if (logEvents) Debug.Log($"IntakePull: ejected '{rb.name}' — launched out (re-solidifies after {ejectClearance:0.#}u).", this);
    }

    // Re-solidify ejected pieces once they've travelled ejectClearance from where they launched — they're
    // ghosted projectiles until then, so they pass cleanly through the rollers, then turn solid in the air.
    // Distance-based on a freely-moving body, so it ALWAYS completes (there's no arrival point to miss).
    private void UpdateEjected()
    {
        for (int i = ejected.Count - 1; i >= 0; i--)
        {
            Ejected e = ejected[i];
            if (e.rb == null) { ejected.RemoveAt(i); continue; }
            if ((e.rb.position - e.launchPos).sqrMagnitude >= ejectClearance * ejectClearance)
            {
                SetPieceColliders(e.rb, true);       // solid again — no more phase-through
                ejected.RemoveAt(i);
                if (logEvents) Debug.Log($"IntakePull: '{e.rb.name}' cleared the intake — solid now.", this);
            }
        }
    }

    private void PushOut(Rigidbody rb, Vector3 from)
    {
        if (rb.isKinematic) return;
        Vector3 outDir = rb.worldCenterOfMass - from;
        if (outDir.sqrMagnitude < 1e-6f) outDir = -transform.forward;
        rb.AddForce(outDir.normalized * ejectAcceleration, ForceMode.Acceleration);
    }

    private bool IsHeld(Rigidbody rb)
    {
        foreach (Held h in held) if (h.rb == rb) return true;
        return false;
    }

    // Is this intake carrying that piece? (The registry answers "who holds it"; this answers it for one
    // intake, which is how a handoff can be checked from both ends.)
    public bool IsCarrying(Rigidbody rb) => IsHeld(rb);

    // The held piece lowest in the stack (smallest slot index = bottom, nearest the mouth) — the one
    // ejected first. Includes null-rb entries so stale ones get cleaned up rather than blocking the queue.
    private Held LowestSlotHeld()
    {
        Held best = null;
        foreach (Held h in held) if (best == null || h.slot < best.slot) best = h;
        return best;
    }

    private int NextFreeSlot()
    {
        for (int i = 0; i < maxHeld; i++)
        {
            bool used = false;
            foreach (Held h in held) if (h.slot == i) { used = true; break; }
            if (!used) return i;
        }
        return -1;
    }

    // Push every held piece in slots [0, top-1] up one slot (into [1, top]) to vacate slot 0 for a piece
    // just captured at the mouth — the bottom-fed stacking that leaves the first-intaked piece on top.
    // `top` is the lowest free slot, so [0, top-1] is a contiguous occupied run and this keeps the stack
    // gapless. Cleared 'arrived' so each shifted piece GLIDES up to its raised slot instead of snapping.
    private void ShiftUpBelow(int top)
    {
        foreach (Held h in held)
            if (h.slot < top) { h.slot += 1; h.arrived = false; }
    }

    private static void SetPieceColliders(Rigidbody rb, bool enabled)
    {
        if (rb == null) return;
        foreach (Collider c in rb.GetComponentsInChildren<Collider>())
            c.enabled = enabled;
    }

    private static bool IsPiece(GameObject go) => GamePiece.IsPiece(go);

    // ---------------------------------------------------------------------------------------------
    // Stability: keep the hold point (and mouth) off any spinning/moving link.
    // ---------------------------------------------------------------------------------------------

    // Re-anchor EVERY anchor that a free-spinning link would whirl — the mouth, the hold point and the
    // stack slots — to the rigid chassis. Preserves world pose, so an anchor sitting at the right spot
    // stays there; it just stops being dragged in circles.
    //
    // The slots used to be left out of this sweep while the hold point was in it, and that asymmetry
    // read as a bug in the intake: mount the whole intake on a pivoting arm and IntakeSlot1 followed
    // the arm while the hold point (slot 0) was snapped back to the chassis, so the stack tore itself
    // apart. Both halves are fixed — slots are swept too, and a LIMITED link like that arm is no longer
    // re-anchored at all (see NeedsReanchor).
    //
    // Public so the headless validator can drive the real thing instead of a copy of its logic; Awake
    // is the only caller in the game.
    public void StabilizeAnchors()
    {
        Transform chassis = ResolveStableChassis();
        if (chassis == null) return;

        // Parents before children: the markers can be children of the mouth, and one whose chain is
        // already clean after its parent moved needs no rescue of its own.
        TryReanchor(transform, chassis, "the intake mouth");
        if (holdPoint != null) TryReanchor(holdPoint, chassis, $"hold point '{holdPoint.name}'");
        if (slotAnchors == null) return;
        foreach (Transform a in slotAnchors)
            if (a != null && a != holdPoint && a != transform)
                TryReanchor(a, chassis, $"stack slot '{a.name}'");
    }

    private void TryReanchor(Transform t, Transform chassis, string what)
    {
        if (!NeedsReanchor(t, chassis, out string reason)) return;
        Debug.LogWarning(
            $"IntakePull: {what} is {reason} — it would whirl around at Play and drag pieces to random " +
            $"points. Re-anchoring it to the chassis '{chassis.name}'. Mount the markers beside the " +
            "roller (Build Intake does that) instead of inside it, and APPLY TO THE PREFAB to fix it " +
            "permanently. Hanging them off a limited arm/wrist/lift link is fine — only a free-spinning " +
            "link is moved.", this);
        t.SetParent(chassis, true);
    }

    // The robot's rigid base: the topmost ArticulationBody ancestor (the articulation root — it drives
    // WITH the bot but never spins a joint), else the RobotMechanisms holder, else the hierarchy root.
    private Transform ResolveStableChassis()
    {
        ArticulationBody top = null;
        foreach (ArticulationBody ab in GetComponentsInParent<ArticulationBody>(true))
            top = ab;                       // ordered nearest-first, so the last is the topmost
        if (top != null) return top.transform;

        RobotMechanisms rm = GetComponentInParent<RobotMechanisms>();
        if (rm != null) return rm.transform;
        return transform.root;
    }

    // True if t would be whirled around by a FREE-SPINNING link between it and the chassis, or isn't
    // under the chassis at all.
    //
    // "Any ArticulationBody above it" was the old test, and it was too broad by a long way: it caught
    // every limited joint too, so an intake bolted to a pivoting arm had its anchors torn off the arm
    // and pinned to the chassis at Play. A LIMITED joint cannot whirl — it only goes where the driver
    // drives it, and an anchor riding it is the whole point of mounting an intake on an arm (the same
    // reason the LiftCarriage bypass below exists, generalized). What actually breaks pieces is an
    // unbounded spin: the roller/flywheel the intake is built around. So that, and only that, is what
    // gets rescued. A fixed link is bounded to the point of not moving at all, so it rides too.
    //
    // Public so the validator can exercise the rule directly.
    public static bool NeedsReanchor(Transform t, Transform chassis, out string reason)
    {
        reason = null;
        if (t == null || chassis == null || t == chassis) return false;

        // The lift's end-effector (tray) is a moving link the anchors are DELIBERATELY parented to, so
        // the held stack rides up as the lift raises. If ANY ancestor up to the chassis is a
        // LiftCarriage-marked link, the anchor is meant to ride the lift subtree — never reanchor it,
        // even across the intermediate moving links (the DR4B driver/follower bars) between the anchor
        // and that carriage.
        for (Transform p = t.parent; p != null && p != chassis; p = p.parent)
            if (p.GetComponent<LiftCarriage>() != null) return false;

        for (Transform p = t.parent; p != null; p = p.parent)
        {
            if (p == chassis) return false;                                  // reached the rigid base cleanly
            if (SpinsFreely(p.GetComponent<ArticulationBody>()))
            {
                reason = $"parented under the free-spinning link '{p.name}'";
                return true;
            }
        }
        reason = "not parented under the chassis";
        return true;
    }

    // A link with no travel limit on its own DOF — one that can turn (or slide) forever. A revolute
    // whose twist is FreeMotion is exactly what the "Spinning motor" mechanism kind writes for a
    // roller, flywheel or intake shaft, and it is the one link an anchor must never hang off. Every
    // other joint is bounded, so whatever it carries keeps a fixed relationship to it.
    // (The chassis's own joint settings are never consulted: the walk above stops the moment it
    // reaches the chassis, which is by definition the frame everything else is measured against.)
    private static bool SpinsFreely(ArticulationBody body)
    {
        if (body == null) return false;
        switch (body.jointType)
        {
            case ArticulationJointType.RevoluteJoint:
                return body.twistLock == ArticulationDofLock.FreeMotion;
            case ArticulationJointType.SphericalJoint:
                return body.twistLock == ArticulationDofLock.FreeMotion ||
                       body.swingYLock == ArticulationDofLock.FreeMotion ||
                       body.swingZLock == ArticulationDofLock.FreeMotion;
            case ArticulationJointType.PrismaticJoint:
                return body.linearLockX == ArticulationDofLock.FreeMotion ||
                       body.linearLockY == ArticulationDofLock.FreeMotion ||
                       body.linearLockZ == ArticulationDofLock.FreeMotion;
            default:
                return false;            // fixed joint — welded, so riding it is riding the chassis
        }
    }

    private void LogStartupDiagnostics()
    {
        Transform h = HoldTf;
        int intakeCount = 0;
        Transform chassis = ResolveStableChassis();
        if (chassis != null)
            intakeCount = chassis.GetComponentsInChildren<IntakePull>(true).Length;

        Debug.Log(
            $"IntakePull[{name}] ready. Hold point = '{HierarchyPath(h)}' at world {h.position}. " +
            $"maxHeld={maxHeld}, glideSpeed={glideSpeed}, slotSpacing={slotSpacing}, dropWhenIdle={dropWhenIdle}, " +
            $"reverse={(reverseDropsInPlace ? "DROPS pieces in place (gravity)" : $"LAUNCHES pieces out at {ejectSpeed}")}. " +
            (intakeCount > 1
                ? $"NOTE: {intakeCount} IntakePull components on this robot — this one " +
                  (takeFromOtherIntakes
                      ? "CAN take pieces off the others (hold its button with its mouth over their stack). "
                      : "will NOT take pieces off the others (Take From Other Intakes is off). ")
                : "") +
            "If this world position isn't where you dragged the hold point, your edit didn't reach the spawned PREFAB " +
            "(RobotSpawner instantiates the prefab, not the scene object).", this);
    }

    private static string HierarchyPath(Transform t)
    {
        if (t == null) return "<none>";
        string path = t.name;
        for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }

#if UNITY_EDITOR
    // Editor-only preview — gizmos never render in the Game view (unless its Gizmos toggle is
    // forced on) or in a build, so nothing shows during Play. Drawn when the mouth is selected:
    // each slot at piece scale (world is 10x; a cup is ~1.6u), the direction the piece in that
    // slot will STAND (rotates with the slot anchor — that is the editable seating orientation),
    // the mouth trigger box, and the pull line. The Build Intake window adds drag/rotate handles
    // on top of these.
    void OnDrawGizmosSelected()
    {
        for (int i = 0; i < Mathf.Max(1, maxHeld); i++)
        {
            Gizmos.color = new Color(0.2f, 0.9f, 1f, i == 0 ? 0.9f : 0.5f);
            Vector3 pos = SlotWorldPos(i);
            Gizmos.DrawWireSphere(pos, i == 0 ? 0.4f : 0.25f);
            Gizmos.DrawRay(pos, SlotAnchorRot(i) * Vector3.up * 1.2f);
        }
        Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.6f);
        Gizmos.DrawLine(transform.position, HoldTf.position);

        if (GetComponent<Collider>() is BoxCollider box)
        {
            Gizmos.color = new Color(1f, 0.85f, 0.15f, 0.9f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(box.center, box.size);
            Gizmos.matrix = Matrix4x4.identity;
        }
    }
#endif
}
