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
// (the intake can grab again at once) and it can never get stuck back on the stack. Hold is MOMENTARY by
// default (keepHeldWhenIdle off): releasing the button drops what's held; turn it on to keep the stack
// while you drive (then reverse to eject).
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
// THE HOLD POINT MUST NOT SPIN. It (and the mouth) belong on the rigid chassis, not the spinning roller
// link — otherwise the target whirls around at Play and pieces are dragged to random points. The Build
// Intake tool anchors them to the chassis, and this component also SELF-HEALS at play start: if the
// mouth/hold point hang off a moving articulation link it re-anchors them to the chassis and warns
// (stabilizeHoldPoint). Select the mouth to see the editor-only gizmos; the Build Intake window adds
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

    [Header("Hold behavior")]
    [Tooltip("OFF (default): momentary — release the button and held pieces drop. ON: keep the stack while you drive; reverse to eject.")]
    public bool keepHeldWhenIdle = false;
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
    [Tooltip("Seconds between ejecting one piece and the next while reverse is held — pieces come out ONE AT A TIME (bottom of the stack first) so they don't clump together, overlap and jam. Tap reverse to eject just one. Set 0 to dump the whole stack at once.")]
    public float ejectInterval = 0.2f;
    [Tooltip("The world velocity each piece is launched with on eject (world units/sec). Kept separate from Glide Speed so eject stays snappy even if intake glide is slow. World is 10x scale.")]
    public float ejectSpeed = 40f;
    [Tooltip("Keep an ejected piece ghosted (phasing through the frame) until it has flown this many WORLD units from where it launched, THEN it turns solid. Raise it if pieces re-solidify too soon and clip/jam on the bot; lower it if they phase through things too long. World is 10x scale.")]
    public float ejectClearance = 6f;
    [Tooltip("Extra outward shove given to loose (uncaptured) pieces sitting in the mouth on reverse (acceleration; must beat gravity ~98 to arc out).")]
    public float ejectAcceleration = 300f;

    [Header("Stability & debug")]
    [Tooltip("At play start, if the mouth/hold point hang off a spinning or moving articulation link, re-anchor them to the rigid chassis so the hold point can't whirl around. Also logs a warning telling you to fix the prefab.")]
    public bool stabilizeHoldPoint = true;
    [Tooltip("Log a startup diagnostic (where the hold point actually is + its hierarchy path) and one line per capture/arrive/release/eject. Turn off once it's working.")]
    public bool logEvents = true;

    // One held piece: its stack slot, whether it has finished gliding in, its pre-capture kinematic state
    // (so a piece that was somehow kinematic before is restored correctly on release), and its seat
    // attitude relative to the slot anchor — solved once at capture, replayed every step.
    private class Held { public Rigidbody rb; public int slot; public bool arrived; public bool wasKinematic; public Vector3 localCom; public Quaternion anchorLocalRot; }

    // A piece ejected and flying out as a ghosted projectile, re-solidified once it has travelled clear of
    // the mouth. Kept OUT of `held` so the intake can grab again immediately.
    private class Ejected { public Rigidbody rb; public Vector3 launchPos; }

    // Pieces overlapping the mouth, counted (a cup/pin has several child colliders → several triggers).
    private readonly Dictionary<Rigidbody, int> inMouth = new Dictionary<Rigidbody, int>();
    private readonly List<Held> held = new List<Held>();
    private readonly List<Ejected> ejected = new List<Ejected>();
    private readonly List<Rigidbody> scratch = new List<Rigidbody>();
    private readonly List<Held> heldScratch = new List<Held>();
    private float lastEjectTime;   // when the last piece was launched, for the eject-one-at-a-time spacing

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
        if (GetComponent<Collider>() is BoxCollider box) return transform.TransformPoint(box.center);
        return transform.position;
    }

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
                foreach (Held h in heldScratch) LaunchOut(h, outward);
            }
            else if (held.Count > 0 && Time.time - lastEjectTime >= ejectInterval)
            {
                // One at a time, bottom of the stack first, spaced by ejectInterval so they don't clump.
                LaunchOut(LowestSlotHeld(), outward);
                lastEjectTime = Time.time;
            }

            // Keep the not-yet-ejected pieces riding on their slots so they don't detach while they wait.
            heldScratch.Clear();
            heldScratch.AddRange(held);
            foreach (Held h in heldScratch)
                if (h.rb != null) CarryTo(h, SlotWorldPos(h.slot), false, dt);

            // Shove any loose (uncaptured) pieces sitting in the mouth out too.
            scratch.Clear();
            scratch.AddRange(inMouth.Keys);
            foreach (Rigidbody rb in scratch) if (rb != null) PushOut(rb, HoldTf.position);
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
        }

        bool holding = intaking || keepHeldWhenIdle;
        if (!holding) { ReleaseAll(); return; }   // momentary: idle drops everything (committed ejects still finish)
        if (held.Count == 0) return;

        // Glide each held piece straight to its slot and hold it there. Kinematic → no gravity, no
        // overshoot, no orbit. Once arrived it snaps to the (bot-relative) slot every step, so it rides
        // rigidly even when the bot drives faster than glideSpeed.
        heldScratch.Clear();
        heldScratch.AddRange(held);
        foreach (Held h in heldScratch)
        {
            if (h.rb == null) { held.Remove(h); continue; }

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
    private void Capture(Rigidbody rb)
    {
        if (rb == null || held.Count >= maxHeld || IsHeld(rb)) return;

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
        // the pivot, which for these off-pivot field pieces would throw the offset away.
        Vector3 localCom = rb.centerOfMass;

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

        bool wasKinematic = rb.isKinematic;
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;   // smooth the glide/carry between physics steps
        if (passThroughWhileHeld) SetPieceColliders(rb, false);

        held.Add(new Held { rb = rb, slot = slot, arrived = false, wasKinematic = wasKinematic, localCom = localCom, anchorLocalRot = anchorLocalRot });
        if (logEvents) Debug.Log($"IntakePull: captured '{rb.name}' → slot {slot} (holding {held.Count}/{maxHeld}); gliding its center (pivot→center offset {localCom.magnitude:0.#}u) to {SlotWorldPos(slot)}." +
                                 (autoUpright ? (localUpAxis.sqrMagnitude > 1e-6f ? " Auto-upright ON." : " Auto-upright ON but NO MESH found to measure — piece keeps its caught attitude.") : ""), this);
    }

    // Restore one piece's dynamics and colliders (no list change). Shared by Release and OnDisable.
    private void Solidify(Held h)
    {
        Rigidbody rb = h.rb;
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

    // Reverse-eject, one physics step: carry every committed-leaving piece OUT through the mouth (staggered
    // by slot so they don't pile), staying ghosted so they pass through the rollers; once a piece is clear
    // (ejectClearance past the mouth) it turns solid and gets shoved out. Called every FixedUpdate, so an
    // eject finishes even if reverse was only tapped — the piece can't get re-glued to its slot.
    // Eject one piece: pull it from `held` NOW (freeing its slot), make it a free dynamic body flying
    // outward in WORLD space (so it separates from the bot instead of clinging), and — if it was ghosted
    // while held — hand it to `ejected` to re-solidify once it's clear of the rollers. Committed the moment
    // it's called: it finishes on its own regardless of the button, and can never get stuck back on a slot.
    private void LaunchOut(Held h, Vector3 outward)
    {
        held.Remove(h);
        Rigidbody rb = h.rb;
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

    // Re-anchor the mouth and hold point to the rigid chassis if either currently hangs off a moving
    // articulation link (e.g. left on the spinning roller by an old setup or a stale prefab). Preserves
    // world pose, so a hold point sitting at the right spot stays there — it just stops whirling.
    private void StabilizeAnchors()
    {
        Transform chassis = ResolveStableChassis();
        if (chassis == null) return;

        if (holdPoint != null && NeedsReanchor(holdPoint, chassis, out string holdReason))
        {
            Debug.LogWarning(
                $"IntakePull: hold point '{holdPoint.name}' is {holdReason} — it would whirl around at Play " +
                $"and drag pieces to random points. Re-anchoring it to the chassis '{chassis.name}'. " +
                "Re-run Tools > RoboSim > Robot > Mechanisms > Add Intake and APPLY TO THE PREFAB to fix it permanently.", this);
            holdPoint.SetParent(chassis, true);
        }

        if (NeedsReanchor(transform, chassis, out string mouthReason))
        {
            Debug.LogWarning(
                $"IntakePull: intake mouth is {mouthReason} — re-anchoring it to the chassis '{chassis.name}' so the grab zone doesn't spin.", this);
            transform.SetParent(chassis, true);
        }
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

    // True if t is not cleanly parented under the chassis via rigid (non-articulated) transforms — i.e.
    // some ancestor between t and the chassis is its own ArticulationBody (a joint link that moves), or
    // t isn't under the chassis at all.
    private static bool NeedsReanchor(Transform t, Transform chassis, out string reason)
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
            if (p.GetComponent<ArticulationBody>() != null)
            {
                reason = $"parented under the moving link '{p.name}'";
                return true;
            }
        }
        reason = "not parented under the chassis";
        return true;
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
            $"maxHeld={maxHeld}, glideSpeed={glideSpeed}, slotSpacing={slotSpacing}, keepHeldWhenIdle={keepHeldWhenIdle}. " +
            (intakeCount > 1 ? $"NOTE: {intakeCount} IntakePull components on this robot. " : "") +
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
