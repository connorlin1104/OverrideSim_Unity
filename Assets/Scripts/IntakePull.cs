using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

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
// Reverse plays the intake BACKWARDS: held pieces are LAUNCHED out one at a time (bottom of the stack
// first, spaced by ejectInterval so they don't come out as a clump that overlaps and jams) — each sent
// flying outward in WORLD space as a free dynamic body (so it separates from the bot instead of clinging
// as you drive). A launched piece stays ghosted just long enough to travel ejectClearance clear of the
// rollers, then turns solid in the air. Because it leaves `held` immediately, its slot frees up right away
// (the intake can grab again at once) and it can never get stuck back on the stack. Hold is MOMENTARY by
// default (keepHeldWhenIdle off): releasing the button drops what's held; turn it on to keep the stack
// while you drive (then reverse to eject).
//
// As pieces come in they're rotated so they stop tumbling and stack cleanly. By default (autoUpright) each
// piece is stood UPRIGHT by geometry — its longest mesh axis is aligned to the slot's up, measured per
// instance, so pins that are each baked at a different child tilt all end up vertical (the field's pins
// share ONE mesh but sit at ~a dozen different rotations, so no single per-type angle could fix them).
// Stack slots default to a straight line (stackAxis × slotSpacing) but can be overridden per slot by
// slotAnchors — draggable/rotatable Transforms that place AND angle each slot. (With autoUpright off,
// orientation comes from the slot rotation + holdEulerOffset + per-type pieceOrientations instead — only
// usable when every piece of a type shares one baked tilt, which these pins do not.)
//
// THE HOLD POINT MUST NOT SPIN. It (and the mouth) belong on the rigid chassis, not the spinning roller
// link — otherwise the target whirls around at Play and pieces are dragged to random points. The Add
// Intake tool anchors them to the chassis, and this component also SELF-HEALS at play start: if the
// mouth/hold point hang off a moving articulation link it re-anchors them to the chassis and warns
// (stabilizeHoldPoint). Turn on showRuntimeMarkers to see the hold point + slots at runtime. World is
// 10x scale, gravity ~-98, pieces mass 1 — but slot spacing and glide speed are all in WORLD units.
//
// Pieces are aimed by their CENTER OF MASS, not their transform pivot. The field's Cup*/Pin* pieces were
// split from one field FBX without re-centering, so each keeps the CAD origin as its pivot — ~9-15 world
// units from the actual mesh. Aiming the pivot at the hold point would leave the visible mesh that far
// off. `Held.localCom` (captured at grab, BEFORE colliders are ghosted, since disabling colliders makes
// PhysX recompute the COM back to the pivot) is the pivot→center offset we use to place the mesh exactly.
public class IntakePull : MonoBehaviour
{
    // Per-piece-type orientation fix: the field pieces (Cup*/Pin*) were split from one FBX on different
    // local axes, so a single rotation can't stand them all up. Each entry adds a rotation for pieces whose
    // name starts with namePrefix, applied on top of the slot orientation.
    [System.Serializable]
    public class PieceOrientation
    {
        [Tooltip("Piece name prefix this applies to, e.g. 'Cup' or 'Pin' (matched at the START of the piece's name).")]
        public string namePrefix;
        [Tooltip("Extra rotation (Euler degrees) for this type, on top of the slot orientation — dial it until this type stands up right.")]
        public Vector3 euler;
    }

    [Tooltip("The intake's motor. Its CurrentInput drives the intake: forward = grab/pull in, reverse = eject. Auto-found on this object's parents if empty.")]
    public MotorActuator intakeMotor;

    [Header("Lift interlock & scoring")]
    [Tooltip("The DR4B lift (optional). While it's RAISED, intaking is disabled (a grabbed piece would just float up); the Score button only drops while it's raised. Wired by Build DR4B Lift.")]
    public Dr4bLift lift;
    [Tooltip("Lift progress (0..1) above which the lift counts as 'raised' for the interlock.")]
    [Range(0f, 1f)] public float liftRaisedThreshold = 0.15f;
    [Tooltip("Button that DROPS the held stack to SCORE (only while the lift is raised). Set by Build DR4B Lift.")]
    public InputActionReference scoreAction;

    [Tooltip("Where captured pieces glide to and stack. The Add Intake tool creates an IntakeHoldPoint you can drag; if empty it falls back to this object's position.")]
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
    [Tooltip("Optional per-slot anchors — drag one Transform per stack position to lay out THIS model's stack exactly (angled or flat). Slot 0 is the hold point; ROTATING an anchor also sets how the piece in that slot sits. Empty/missing entries fall back to the stackAxis line. The Add Intake tool creates these as draggable IntakeSlot points.")]
    public Transform[] slotAnchors;

    [Header("Hold behavior")]
    [Tooltip("OFF (default): momentary — release the button and held pieces drop. ON: keep the stack while you drive; reverse to eject.")]
    public bool keepHeldWhenIdle = false;
    [Tooltip("While a piece is held, switch OFF its colliders so it passes through the wheels/frame and can't shove the bot. Restored on release.")]
    public bool passThroughWhileHeld = true;

    [Header("Glide (all in WORLD units — world is 10x scale)")]
    [Tooltip("How fast a captured piece glides to its slot (world units/sec). It's kinematic, so this can't overshoot; higher = snappier. Reverse-eject reuses this speed to glide pieces back out the mouth.")]
    public float glideSpeed = 24f;
    [Tooltip("Also rotate a captured piece to match the hold point's orientation as it comes in, so it stops tumbling.")]
    public bool rotateToHold = true;
    [Tooltip("How fast a captured piece rotates to the hold orientation (degrees/sec).")]
    public float rotateSpeed = 720f;
    [Tooltip("RECOMMENDED. Stand each captured piece upright automatically by aligning its longest mesh axis to the slot's up — computed PER PIECE from geometry, so it handles pieces whose tilt is baked differently into every instance (e.g. the field's pins: they share one mesh but each sits at a different child rotation). Overrides the manual Hold Euler Offset / Piece Orientations below.")]
    public bool autoUpright = true;
    [Tooltip("Advanced: which axis of the MESH is the piece's 'up' (its long/standing axis). Leave (0,0,0) to auto-pick the longest mesh-bounds axis. Set e.g. (0,1,0) if auto-pick stands a piece up the wrong way.")]
    public Vector3 uprightMeshAxis = Vector3.zero;
    [Tooltip("Used only when Auto Upright is OFF. Extra rotation (Euler degrees, LOCAL to the hold point) applied to EVERY held piece — a global stacking tweak. Needs 'Rotate To Hold' on.")]
    public Vector3 holdEulerOffset = Vector3.zero;
    [Tooltip("Used only when Auto Upright is OFF. Per-piece-TYPE orientation fix by name prefix — only works when each type shares a single baked orientation. (The pins DON'T: each instance is tilted differently, which is why Auto Upright exists.)")]
    public List<PieceOrientation> pieceOrientations = new List<PieceOrientation>();

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
    [Tooltip("Spawn visible in-world markers at the hold point, each stack slot, and the mouth zone (with a connecting line) so you can watch where pieces are being pulled at runtime.")]
    public bool showRuntimeMarkers = true;
    [Tooltip("World-space diameter of the hold-point marker sphere (slot markers are smaller). World is 10x scale.")]
    public float markerSize = 0.5f;
    [Tooltip("Log a startup diagnostic (where the hold point actually is + its hierarchy path) and one line per capture/arrive/release/eject. Turn off once it's working.")]
    public bool logEvents = true;

    // One held piece: its stack slot, whether it has finished gliding in, its pre-capture kinematic state
    // (so a piece that was somehow kinematic before is restored correctly on release), and its measured
    // standing axis (for Auto Upright).
    private class Held { public Rigidbody rb; public int slot; public bool arrived; public bool wasKinematic; public Vector3 localCom; public Vector3 localUpAxis; }

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

    private readonly List<Transform> slotMarkers = new List<Transform>();  // [0] = hold point, [i] = slot i
    private LineRenderer markerLine;

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

    // Orientation a piece in this slot is eased to: the slot anchor's rotation if set (so you can angle or
    // flatten each slot per model), else the hold point's rotation — both times the holdEulerOffset.
    private Quaternion SlotWorldRot(int slot)
    {
        Transform a = SlotAnchor(slot);
        Quaternion baseRot = a != null ? a.rotation : HoldTf.rotation;
        return baseRot * Quaternion.Euler(holdEulerOffset);
    }

    // The direction a piece should STAND in this slot (world), used by Auto Upright — the slot anchor's up,
    // else the hold point's up. Deliberately excludes holdEulerOffset (that's for the manual mode); tilt a
    // slot anchor if you want the stack to lean.
    private Vector3 SlotUpDir(int slot)
    {
        Transform a = SlotAnchor(slot);
        return (a != null ? a.rotation : HoldTf.rotation) * Vector3.up;
    }

    // Extra per-type rotation for a held piece, matched by name prefix (longest match wins, so a more
    // specific prefix overrides a shorter one). Identity if nothing matches.
    private Quaternion PieceTypeRot(Rigidbody rb)
    {
        if (rb == null || pieceOrientations == null) return Quaternion.identity;
        PieceOrientation best = null;
        foreach (PieceOrientation po in pieceOrientations)
        {
            if (po == null || string.IsNullOrEmpty(po.namePrefix) || !rb.name.StartsWith(po.namePrefix)) continue;
            if (best == null || po.namePrefix.Length > best.namePrefix.Length) best = po;
        }
        return best != null ? Quaternion.Euler(best.euler) : Quaternion.identity;
    }

    // The piece's "standing" axis expressed in its RIGIDBODY-local frame, for Auto Upright. It's the
    // explicit uprightMeshAxis, or the longest axis of the mesh's local bounds, mapped through the mesh
    // child's rotation into the root's frame — so it's fixed no matter how we later rotate the piece.
    // Returns zero if there's no mesh to measure (Auto Upright then leaves that piece's rotation alone).
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
            Vector3 s = mesh.bounds.size;                                   // longest local bounds axis
            axisMeshLocal = (s.x >= s.y && s.x >= s.z) ? Vector3.right
                          : (s.y >= s.z) ? Vector3.up : Vector3.forward;
        }
        else return Vector3.zero;

        Vector3 world = meshTf.rotation * axisMeshLocal;                    // the standing axis in world now
        return (Quaternion.Inverse(rb.rotation) * world).normalized;       // → rigidbody-local (rotation-stable)
    }

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

    void Start()
    {
        // Markers are built in Start, after RobotSpawner.RecenterFootprint has run in Awake, so their
        // (already collider-free) geometry can never perturb the spawn footprint scan.
        if (showRuntimeMarkers) BuildMarkers();
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

        // Lift interlock: no sucking while the DR4B is raised (a grabbed piece would just float up to it).
        if (lift != null && lift.Progress > liftRaisedThreshold) intaking = false;

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

    // Carry one held piece toward a target CENTER-OF-MASS position, easing it to the held orientation
    // (hold rotation + holdEulerOffset). Works in center-of-mass space so the visible mesh — not the
    // off-center pivot — lands on the target: the pivot is placed from the SAME rotation we apply, so
    // rotating the piece can't swing the mesh off, even though the pivot is 9-15u away. Returns the
    // piece's center after this step. Shared by the intake hold loop and the parked pieces mid-eject.
    private Vector3 CarryTo(Held h, Vector3 targetCom, bool glide, float dt)
    {
        Rigidbody rb = h.rb;
        // Auto Upright: rotate the ROOT so the piece's measured standing axis points along the slot's up —
        // per instance, so every differently-tilted pin ends up vertical. Fall back to the manual per-slot /
        // per-type rotation when it's off (or when there was no mesh to measure).
        Quaternion target = (autoUpright && h.localUpAxis.sqrMagnitude > 1e-6f)
            ? Quaternion.FromToRotation(h.localUpAxis, SlotUpDir(h.slot))
            : SlotWorldRot(h.slot) * PieceTypeRot(rb);
        Quaternion desiredRot = rotateToHold
            ? Quaternion.RotateTowards(rb.rotation, target, rotateSpeed * dt)
            : rb.rotation;
        Vector3 curCom = rb.position + rb.rotation * h.localCom;
        Vector3 nextCom = glide ? Vector3.MoveTowards(curCom, targetCom, glideSpeed * dt) : targetCom;
        rb.MovePosition(nextCom - desiredRot * h.localCom);   // pivot placed so the center hits nextCom
        if (rotateToHold) rb.MoveRotation(desiredRot);
        return nextCom;
    }

    void LateUpdate()
    {
        // Keep the markers pinned to the live slot/hold positions (the same math the pieces use), so
        // what you see is exactly where a piece will go — independent of any parent scale.
        for (int i = 0; i < slotMarkers.Count; i++)
            if (slotMarkers[i] != null) slotMarkers[i].position = SlotWorldPos(i);

        if (markerLine != null)
        {
            markerLine.SetPosition(0, MouthWorldPos());
            markerLine.SetPosition(1, HoldTf.position);
        }
    }

    // Begin holding a piece: make it kinematic (so it glides cleanly, immune to gravity/knocks) and ghost
    // it so it passes through the CAD. Assigns a free slot.
    private void Capture(Rigidbody rb)
    {
        if (rb == null || held.Count >= maxHeld || IsHeld(rb)) return;
        int slot = NextFreeSlot();
        if (slot < 0) return;
        inMouth.Remove(rb);

        // Read the center of mass BEFORE ghosting — disabling colliders makes PhysX recompute the COM to
        // the pivot, which for these off-pivot field pieces would throw the offset away.
        Vector3 localCom = rb.centerOfMass;

        // Measure this piece's standing axis in its own local frame, so Auto Upright can stand it up no
        // matter how its mesh happens to be tilted (every field pin is baked at a different child rotation).
        Vector3 localUpAxis = autoUpright ? ComputeUpAxis(rb) : Vector3.zero;

        bool wasKinematic = rb.isKinematic;
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;   // smooth the glide/carry between physics steps
        if (passThroughWhileHeld) SetPieceColliders(rb, false);

        held.Add(new Held { rb = rb, slot = slot, arrived = false, wasKinematic = wasKinematic, localCom = localCom, localUpAxis = localUpAxis });
        if (logEvents) Debug.Log($"IntakePull: captured '{rb.name}' → slot {slot} (holding {held.Count}/{maxHeld}); gliding its center (pivot→center offset {localCom.magnitude:0.#}u) to {SlotWorldPos(slot)}." +
                                 (autoUpright ? (localUpAxis.sqrMagnitude > 1e-6f ? " Auto-upright ON." : " Auto-upright ON but NO MESH found to measure — piece won't be re-oriented.") : ""), this);
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

    private static void SetPieceColliders(Rigidbody rb, bool enabled)
    {
        if (rb == null) return;
        foreach (Collider c in rb.GetComponentsInChildren<Collider>())
            c.enabled = enabled;
    }

    // Project convention for identifying pieces (cups/pins). A dedicated GamePiece marker/layer is the
    // clean upgrade (claws/scoring will want one); this one method is the swap point.
    private static bool IsPiece(GameObject go)
    {
        string n = go.name;
        return n.StartsWith("Cup") || n.StartsWith("Pin");
    }

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

    // ---------------------------------------------------------------------------------------------
    // Runtime markers: real (collider-free) renderers so you can SEE the hold point/slots/mouth in the
    // Game view at play, not just Scene-view gizmos. Positions are refreshed each LateUpdate.
    // ---------------------------------------------------------------------------------------------

    private void BuildMarkers()
    {
        Color holdColor = new Color(0.15f, 0.95f, 1f, 1f);   // bright cyan = the hold point (slot 0)
        Color slotColor = new Color(0.15f, 0.95f, 1f, 0.7f); // fainter = the other stack slots

        slotMarkers.Clear();
        for (int i = 0; i < Mathf.Max(1, maxHeld); i++)
        {
            float dia = i == 0 ? markerSize : markerSize * 0.6f;
            GameObject s = MakeSphere(dia, i == 0 ? holdColor : slotColor,
                i == 0 ? "IntakeHoldMarker" : $"IntakeSlotMarker{i}");
            slotMarkers.Add(s.transform);
        }

        // Mouth (grab zone) as a translucent box matching the trigger.
        BoxCollider box = GetComponent<Collider>() as BoxCollider;
        if (box != null)
        {
            GameObject m = GameObject.CreatePrimitive(PrimitiveType.Cube);
            m.name = "IntakeMouthMarker";
            StripCollider(m);
            m.transform.SetParent(transform, false);
            m.transform.localPosition = box.center;
            m.transform.localRotation = Quaternion.identity;
            m.transform.localScale = box.size;               // rides the trigger's own (scaled) space
            Paint(m, new Color(1f, 0.85f, 0.15f, 0.15f));
        }

        // A line from the mouth to the hold point, updated each LateUpdate.
        GameObject lineGo = new GameObject("IntakeMarkerLine");
        lineGo.transform.SetParent(transform, false);
        markerLine = lineGo.AddComponent<LineRenderer>();
        markerLine.useWorldSpace = true;
        markerLine.positionCount = 2;
        markerLine.numCornerVertices = 0;
        markerLine.startWidth = markerLine.endWidth = markerSize * 0.15f;
        markerLine.material = UnlitMaterial(new Color(0.15f, 0.95f, 1f, 0.8f), true);
        markerLine.SetPosition(0, MouthWorldPos());
        markerLine.SetPosition(1, HoldTf.position);
    }

    // A sphere marker of a given WORLD diameter, parented to the mouth (so it's cleaned up with the
    // robot) but with its localScale un-scaled by the parent's (~10x) lossyScale. Its world position is
    // (re)set each LateUpdate to the live slot position.
    private GameObject MakeSphere(float worldDiameter, Color color, string goName)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = goName;
        StripCollider(go);
        go.transform.SetParent(transform, false);
        Vector3 ls = transform.lossyScale;
        go.transform.localScale = new Vector3(worldDiameter / Nz(ls.x), worldDiameter / Nz(ls.y), worldDiameter / Nz(ls.z));
        Paint(go, color);
        return go;
    }

    private static float Nz(float v) => Mathf.Abs(v) < 1e-4f ? 1f : v;

    private static void StripCollider(GameObject go)
    {
        Collider col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);   // markers must never touch physics or the spawn footprint
    }

    private static void Paint(GameObject go, Color color)
    {
        MeshRenderer r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = UnlitMaterial(color, color.a < 0.99f);
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
    }

    // A URP-unlit material (falls back to built-in unlit), transparent when the color has alpha < 1.
    private static Material UnlitMaterial(Color color, bool transparent)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        Material m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Color")) m.SetColor("_Color", color);
        if (transparent)
        {
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);   // URP: 0=opaque, 1=transparent
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
        }
        return m;
    }

#if UNITY_EDITOR
    // Always-on Scene-view gizmos (in addition to the runtime markers), so the hold point and slots are
    // visible while debugging even when the object isn't selected.
    void OnDrawGizmos()
    {
        for (int i = 0; i < Mathf.Max(1, maxHeld); i++)
        {
            Gizmos.color = new Color(0.2f, 0.9f, 1f, i == 0 ? 0.9f : 0.5f);
            Gizmos.DrawWireSphere(SlotWorldPos(i), i == 0 ? 0.2f : 0.12f);
        }
        Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.6f);
        Gizmos.DrawLine(transform.position, HoldTf.position);
    }
#endif
}
