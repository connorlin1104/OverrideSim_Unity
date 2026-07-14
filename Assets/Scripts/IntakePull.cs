using System.Collections.Generic;
using UnityEngine;
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
// Reverse ejects everything (back to normal dynamic physics, shoved out). Hold is MOMENTARY by default
// (keepHeldWhenIdle off): releasing the button drops what's held. Turn it on to keep the stack while
// you drive.
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
    [Tooltip("The intake's motor. Its CurrentInput drives the intake: forward = grab/pull in, reverse = eject. Auto-found on this object's parents if empty.")]
    public MotorActuator intakeMotor;

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

    [Header("Hold behavior")]
    [Tooltip("OFF (default): momentary — release the button and held pieces drop. ON: keep the stack while you drive; reverse to eject.")]
    public bool keepHeldWhenIdle = false;
    [Tooltip("While a piece is held, switch OFF its colliders so it passes through the wheels/frame and can't shove the bot. Restored on release.")]
    public bool passThroughWhileHeld = true;

    [Header("Glide (all in WORLD units — world is 10x scale)")]
    [Tooltip("How fast a captured piece glides to its slot (world units/sec). It's kinematic, so this can't overshoot; higher = snappier.")]
    public float glideSpeed = 24f;
    [Tooltip("Also rotate a captured piece to match the hold point's orientation as it comes in, so it stops tumbling.")]
    public bool rotateToHold = true;
    [Tooltip("How fast a captured piece rotates to the hold orientation (degrees/sec).")]
    public float rotateSpeed = 720f;

    [Header("Eject")]
    [Tooltip("How hard pieces are shoved out on reverse (acceleration; must beat gravity ~98 to arc out).")]
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

    // One held piece: its stack slot, whether it has finished gliding in, and its pre-capture kinematic
    // state (so a piece that was somehow kinematic before is restored correctly on release).
    private class Held { public Rigidbody rb; public int slot; public bool arrived; public bool wasKinematic; public Vector3 localCom; }

    // Pieces overlapping the mouth, counted (a cup/pin has several child colliders → several triggers).
    private readonly Dictionary<Rigidbody, int> inMouth = new Dictionary<Rigidbody, int>();
    private readonly List<Held> held = new List<Held>();
    private readonly List<Rigidbody> scratch = new List<Rigidbody>();
    private readonly List<Held> heldScratch = new List<Held>();

    private readonly List<Transform> slotMarkers = new List<Transform>();  // [0] = hold point, [i] = slot i
    private LineRenderer markerLine;

    private Transform HoldTf => holdPoint != null ? holdPoint : transform;
    private Vector3 StackDir => stackAxis.sqrMagnitude > 1e-6f ? stackAxis.normalized : Vector3.up;

    // Slot world position: hold point + a rotation-only offset, so spacing is real WORLD units and is NOT
    // multiplied by the robot's (~10x) transform scale. Recomputed live so it rides with the bot.
    private Vector3 SlotWorldPos(int slot) => HoldTf.position + HoldTf.rotation * (StackDir * (slot * slotSpacing));

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

    void OnDisable()
    {
        // Never leave a piece kinematic/ghosted if this component switches off or unloads.
        ReleaseAll();
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
        if (intakeMotor == null) return;

        float input = intakeMotor.CurrentInput;
        if (reverseDirection) input = -input;
        bool intaking = input > inputThreshold;
        bool ejecting = input < -inputThreshold;

        if (ejecting) { EjectAll(HoldTf.position); return; }

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
        if (!holding) { ReleaseAll(); return; }   // momentary: idle drops everything
        if (held.Count == 0) return;

        // Glide each held piece straight to its slot and hold it there. Kinematic → no gravity, no
        // overshoot, no orbit. Once arrived it snaps to the (bot-relative) slot every step, so it rides
        // rigidly even when the bot drives faster than glideSpeed.
        float dt = Time.fixedDeltaTime;
        heldScratch.Clear();
        heldScratch.AddRange(held);
        foreach (Held h in heldScratch)
        {
            Rigidbody rb = h.rb;
            if (rb == null) { held.Remove(h); continue; }

            // Glide in CENTER-OF-MASS space, not pivot space: move the piece's center (what the eye
            // tracks) toward the slot, then place the pivot so that center lands there under the target
            // rotation. Computing the pivot from the same rotation we apply means rotating the piece
            // can't swing the mesh off the slot, even though the pivot is 9-15u away.
            Vector3 slot = SlotWorldPos(h.slot);
            Quaternion desiredRot = rotateToHold
                ? Quaternion.RotateTowards(rb.rotation, HoldTf.rotation, rotateSpeed * dt)
                : rb.rotation;
            Vector3 curCom = rb.position + rb.rotation * h.localCom;
            Vector3 nextCom = h.arrived ? slot : Vector3.MoveTowards(curCom, slot, glideSpeed * dt);
            rb.MovePosition(nextCom - desiredRot * h.localCom);      // pivot placed so the center hits nextCom
            if (rotateToHold) rb.MoveRotation(desiredRot);

            if (!h.arrived && (nextCom - slot).sqrMagnitude < 1e-4f)
            {
                h.arrived = true;
                if (logEvents)
                {
                    // curCom uses the stored localCom (rb.worldCenterOfMass is unreliable while colliders
                    // are ghosted). pivotΔ shows why aiming the pivot looked wrong; comΔ shows it's fixed.
                    float pivotDelta = (rb.position - slot).magnitude;
                    float comDelta = (curCom - slot).magnitude;
                    Debug.Log($"IntakePull: '{rb.name}' arrived at slot {h.slot} — center locked to the bot. " +
                              $"comΔ={comDelta:0.###}u (on the marker), pivotΔ={pivotDelta:0.#}u (the piece's off-center pivot).", this);
                }
            }
        }
    }

    void LateUpdate()
    {
        // Keep the markers pinned to the live slot/hold positions (the same math the pieces use), so
        // what you see is exactly where a piece will go — independent of any parent scale.
        for (int i = 0; i < slotMarkers.Count; i++)
            if (slotMarkers[i] != null) slotMarkers[i].position = SlotWorldPos(i);

        if (markerLine != null)
        {
            markerLine.SetPosition(0, transform.position);
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

        bool wasKinematic = rb.isKinematic;
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;   // smooth the glide/carry between physics steps
        if (passThroughWhileHeld) SetPieceColliders(rb, false);

        held.Add(new Held { rb = rb, slot = slot, arrived = false, wasKinematic = wasKinematic, localCom = localCom });
        if (logEvents) Debug.Log($"IntakePull: captured '{rb.name}' → slot {slot} (holding {held.Count}/{maxHeld}); gliding its center (pivot→center offset {localCom.magnitude:0.#}u) to {SlotWorldPos(slot)}.", this);
    }

    // Stop holding one piece: restore its dynamics and colliders, and free its slot.
    private void Release(Held h)
    {
        Rigidbody rb = h.rb;
        if (rb != null)
        {
            rb.isKinematic = h.wasKinematic;
            if (passThroughWhileHeld) SetPieceColliders(rb, true);
            if (logEvents) Debug.Log($"IntakePull: released '{rb.name}'.", this);
        }
        held.Remove(h);
    }

    private void ReleaseAll()
    {
        if (held.Count == 0) return;
        heldScratch.Clear();
        heldScratch.AddRange(held);
        foreach (Held h in heldScratch) Release(h);
    }

    // Reverse: release everything and shove it (plus anything sitting in the mouth) back out.
    private void EjectAll(Vector3 hold)
    {
        heldScratch.Clear();
        heldScratch.AddRange(held);
        foreach (Held h in heldScratch)
        {
            Rigidbody rb = h.rb;
            Release(h);                       // solidify + restore dynamics first
            if (rb != null) PushOut(rb, hold);
        }
        scratch.Clear();
        scratch.AddRange(inMouth.Keys);
        foreach (Rigidbody rb in scratch)
            if (rb != null) PushOut(rb, hold);
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
        markerLine.SetPosition(0, transform.position);
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
