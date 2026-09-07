using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

// THE TYRE. One physical fact the sim was missing: an omni wheel grips along its rolling direction
// and rolls freely across it. Every wheel here is a sphere collider with ONE isotropic friction
// coefficient, so a skid-steer turn had to scrub all six tyres sideways at full grip — which is why
// a moving turn locked its inner wheel and chattered, a released spin died on the spot, forward+turn
// went straight, and a sideways push was resisted at 0.8 g. Three drivetrain compensations (the
// turn exemption, the back-drive cap, the roll relief) existed to hide that, and go with it.
//
// HOW: PhysX contact modification. Physics.ContactModifyEvent hands over every contact pair that
// involves a collider flagged hasModifiableContacts, BEFORE the solver runs, with both bodies'
// velocities, the contact points and normals, and lets the friction coefficient be set per contact.
// The wheel keeps its material (0.8/0.9, Maximum combine — what DrivetrainTuning measures the motor
// against) and the tyre only ever LOWERS it, per contact, when that contact is moving sideways.
//
// THE RULE IS KEYED ON LATERAL MOTION ONLY, and that is the whole design:
//
//     mu = muLat + (muLong - muLat) / (1 + (lat / lateralScale)^2 + (Fext / externalScale)^2)
//
// `lat` is the contact's relative velocity along the wheel's axle; `Fext` is the sideways force the
// rest of the robot is being given by things that are not this robot (below).
//
//   - NOT keyed on the slip direction. A wheel that grips has ZERO longitudinal slip — PhysX friction
//     is a velocity constraint, not a spring — so a rule that reads "which way is it slipping" is
//     undefined at exactly the operating point of a driving wheel, and dithers 0.05 <-> 0.8 on
//     alternate steps: low mu, the wheel over-spins, the slip reads longitudinal, high mu, the wheel
//     is yanked back to zero slip, low mu again. With mu a function of lateral velocity alone, a
//     wheel rolling straight — driving, braking, parked — has muLong at every slip, so every
//     straight-line number the drivetrain was tuned against is untouched by construction.
//   - The lateral direction has no such loop: lateral friction only ever REDUCES lateral velocity,
//     and less velocity means more friction, so it stops. In a pivot the wheels far from the yaw
//     centre (5-9 u/s sideways) drop to ~0.06 and the ones near it keep most of their grip, which is
//     where the yaw centre then settles — on a real robot too. What DOES happen there (measured on
//     654V_v3, WheelTyreValidation's trace): the yaw centre hunts by a few hundred millimetres
//     around the gripping wheel at ~9 Hz, so that wheel's friction alternates 0.8 <-> 0.15 while
//     the yaw rate holds within 1 % and every wheel's spin within 1-4 %. Invisible in anything the
//     driver or the physics can see; a slow grip recovery was tried against it and changed nothing,
//     so it is not there. The validator asserts on the wheels and the chassis, not on mu.
//   - Fext is what lets a SUSTAINED shove move a robot that is not yet moving sideways. At rest, or
//     driving straight, a contact has no lateral velocity, so its friction is muLong and a gentle
//     push is cancelled inside every step and never starts it sliding (a hit does get through: it
//     makes velocity). Physics.ContactEvent reports every contact's normal impulse, so the handler
//     sums, over this robot's colliders, the impulses from bodies that are NOT this robot, and the
//     controller projects that onto its own right axis. Something pushing the robot sideways lowers
//     the friction under it; nothing else does.
//
// A TRACTION wheel is registered as such and skipped: its contacts keep the material friction, so it
// resists sideways motion at full grip — a pair of them is what makes a sideways hit cost something
// and a turn hold its line. See RobotMotorController.tractionPair.
//
// WHAT ELSE THE CONTACT EVENT PROVIDES: each wheel's normal impulse, i.e. the load it is carrying,
// one step old. Nothing in the drivetrain uses it — splitting a rail's torque by it was built and
// removed on 2026-09-06, for reasons worth reading before rebuilding it (RobotMotorController, above
// the force limits). It stays because the release PROBE reports per-wheel load against the even
// share, and that census is how the 220% / 56% / 1% split of a three-wheel rail was found at all.
// ContactPairPoint.impulse is the NORMAL impulse (PhysX does not report friction per point) and
// nothing here relies on a friction impulse.
//
// LIFETIME AND THREADS. One static registry for every robot in the scene: entries are keyed by
// collider, so a second robot registers beside the first. The modify callback may run off the main
// thread, so it reads a NativeArray that is rebuilt only on the main thread between steps, and
// touches no Unity API. The contact-event handler runs on the main thread at the end of the step
// and is the only writer of per-step state; the controller consumes that state in the next
// ApplyStep, before the next Simulate. Subscribed lazily on the first Register, unsubscribed on the
// last Unregister; a domain reload empties the statics and Awake re-registers. Edit-mode validators
// spawn robots into fresh scenes where nothing calls OnDisable, so ValidationUtil.SpawnOnBareFloor
// calls Clear().
public static class WheelTyreModel
{
    // The validators flip this to A/B the tyre against the isotropic wheel on one robot, and the
    // trace probe reads friction through it. Not a player setting and never will be. Flipping it
    // while robots are registered takes effect on the next step.
    private static bool frictionEnabled = true;
    public static bool FrictionEnabled
    {
        get => frictionEnabled;
        set
        {
            frictionEnabled = value;
            if (wheels.Count == 0) return;
            if (value && !modifySubscribed) { Physics.ContactModifyEvent += OnContactModify; modifySubscribed = true; }
            if (!value && modifySubscribed) { Physics.ContactModifyEvent -= OnContactModify; modifySubscribed = false; }
        }
    }

    private sealed class WheelSlot
    {
        public RobotMotorController owner;
        public ArticulationBody wheel;
        public SphereCollider collider;
        public EntityId id;
        public Vector3 localAxle;      // the axle, in the collider transform's own frame
        public bool traction;
        public float normalImpulse;    // this step's, summed over its contacts (force x dt)
    }

    private sealed class RobotSlot
    {
        public RobotMotorController owner;
        public readonly List<EntityId> colliderIds = new List<EntityId>();
        public readonly List<WheelSlot> wheels = new List<WheelSlot>();
        public Vector3 externalImpulse;  // this step's, on this robot, from bodies that are not it
        public float weight;             // m * g
    }

    // The callback's view of a wheel: numbers and an id, nothing that is a Unity object.
    private struct TyreEntry
    {
        public EntityId id;
        public Vector3 localAxle;
        public float muLong, muLat;
        public float lateralScale;      // u/s
        public float externalScale;     // force
        public float externalLateral;   // force, published for the coming step
        public bool traction;
        public float lastMu;            // what the callback last set on this wheel (-1: no contact yet)
        public float lastLateral;       // the lateral velocity it keyed that on, u/s
    }

    private static readonly List<WheelSlot> wheels = new List<WheelSlot>();
    private static readonly Dictionary<EntityId, WheelSlot> wheelById = new Dictionary<EntityId, WheelSlot>();
    private static readonly Dictionary<EntityId, RobotSlot> robotByCollider = new Dictionary<EntityId, RobotSlot>();
    private static readonly Dictionary<RobotMotorController, RobotSlot> robots =
        new Dictionary<RobotMotorController, RobotSlot>();
    private static NativeArray<TyreEntry> native;
    private static bool contactsSubscribed, modifySubscribed, lifetimeHooked, envChecked;

    public static int RegisteredWheelCount => wheels.Count;

    // Diagnostics: how many times each callback has run since the domain loaded. A validator that
    // sees zero modify callbacks with wheels registered knows the event is not firing at all.
    public static int ContactEvents { get; private set; }
    public static int ModifyCallbacks => modifyCallbacks;
    private static int modifyCallbacks;

    // --- Registration (main thread) ------------------------------------------------------------

    // `worldAxles[i]` is wheel i's axle in world space right now — the joint's twist axis, the same
    // vector MeasureDriveAxes votes with. `weight` is m*g, the scale the external-shove term is
    // measured against.
    public static void Register(RobotMotorController owner, ArticulationBody root,
        ArticulationBody[] robotWheels, Vector3[] worldAxles, bool[] traction, float weight)
    {
        if (owner == null || root == null || robotWheels == null || worldAxles == null) return;
        // ROBOSIM_TYRE_OFF=1 runs any batch validator on the isotropic wheel, for an A/B.
        if (!envChecked)
        {
            envChecked = true;
            if (System.Environment.GetEnvironmentVariable("ROBOSIM_TYRE_OFF") == "1") frictionEnabled = false;
        }
        Unregister(owner);
        Prune();

        var robot = new RobotSlot { owner = owner, weight = Mathf.Max(weight, 1e-3f) };
        robots[owner] = robot;

        var wheelColliders = new HashSet<Collider>();
        for (int i = 0; i < robotWheels.Length && i < worldAxles.Length; i++)
        {
            ArticulationBody wheel = robotWheels[i];
            if (wheel == null) continue;
            SphereCollider sphere = FindSphere(wheel);
            if (sphere == null) continue;

            sphere.hasModifiableContacts = true;
            sphere.providesContacts = true;
            wheelColliders.Add(sphere);

            var slot = new WheelSlot
            {
                owner = owner,
                wheel = wheel,
                collider = sphere,
                id = sphere.GetEntityId(),
                localAxle = (Quaternion.Inverse(sphere.transform.rotation) * worldAxles[i]).normalized,
                traction = traction != null && i < traction.Length && traction[i],
            };
            wheels.Add(slot);
            wheelById[slot.id] = slot;
            robotByCollider[slot.id] = robot;
            robot.colliderIds.Add(slot.id);
            robot.wheels.Add(slot);
        }

        // Every other collider on the robot reports its contacts too, for the external-force sum.
        foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
        {
            if (c == null || c.isTrigger || wheelColliders.Contains(c)) continue;
            c.providesContacts = true;
            EntityId id = c.GetEntityId();
            robotByCollider[id] = robot;
            robot.colliderIds.Add(id);
        }

        RebuildNative();
        EnsureSubscribed();
    }

    public static void Unregister(RobotMotorController owner)
    {
        if (owner == null || !robots.TryGetValue(owner, out RobotSlot robot)) return;
        foreach (EntityId id in robot.colliderIds)
        {
            robotByCollider.Remove(id);
            if (wheelById.TryGetValue(id, out WheelSlot slot))
            {
                wheelById.Remove(id);
                wheels.Remove(slot);
            }
        }
        robots.Remove(owner);
        if (wheels.Count == 0) Unsubscribe(); else RebuildNative();
    }

    public static void Clear()
    {
        foreach (RobotMotorController owner in new List<RobotMotorController>(robots.Keys))
            Unregister(owner);
        wheels.Clear();
        wheelById.Clear();
        robotByCollider.Clear();
        robots.Clear();
        Unsubscribe();
    }

    // Destroyed robots leave slots with null owners behind (edit mode has no OnDisable); drop them
    // the next time anyone registers.
    private static void Prune()
    {
        foreach (RobotMotorController owner in new List<RobotMotorController>(robots.Keys))
            if (owner == null) UnregisterDead(robots[owner]);
    }

    private static void UnregisterDead(RobotSlot robot)
    {
        foreach (EntityId id in robot.colliderIds)
        {
            robotByCollider.Remove(id);
            if (wheelById.TryGetValue(id, out WheelSlot slot))
            {
                wheelById.Remove(id);
                wheels.Remove(slot);
            }
        }
        foreach (var kv in new List<KeyValuePair<RobotMotorController, RobotSlot>>(robots))
            if (kv.Value == robot) robots.Remove(kv.Key);
    }

    private static SphereCollider FindSphere(ArticulationBody wheel)
    {
        foreach (SphereCollider s in wheel.GetComponentsInChildren<SphereCollider>(true))
            if (s != null && s.enabled && !s.isTrigger) return s;
        return null;
    }

    private static void RebuildNative()
    {
        DisposeNative();
        native = new NativeArray<TyreEntry>(wheels.Count, Allocator.Persistent);
        for (int i = 0; i < wheels.Count; i++)
        {
            WheelSlot w = wheels[i];
            float muLong = DrivetrainTuning.FallbackFriction;
            PhysicsMaterial mat = w.collider != null ? w.collider.sharedMaterial : null;
            if (mat != null) muLong = mat.dynamicFriction;
            RobotSlot robot = robots.TryGetValue(w.owner, out RobotSlot r) ? r : null;
            native[i] = new TyreEntry
            {
                id = w.id,
                localAxle = w.localAxle,
                muLong = muLong,
                muLat = Mathf.Min(DrivetrainTuning.OmniLateralFriction, muLong),
                lateralScale = Mathf.Max(DrivetrainTuning.LateralSlipScale, 1e-3f),
                externalScale = Mathf.Max(DrivetrainTuning.ExternalLateralForceFraction
                                          * (robot != null ? robot.weight : 1f), 1e-3f),
                externalLateral = 0f,
                traction = w.traction,
                lastMu = -1f,
                lastLateral = 0f,
            };
        }
    }

    private static void DisposeNative()
    {
        if (native.IsCreated) native.Dispose();
    }

    private static void EnsureSubscribed()
    {
        if (!lifetimeHooked)
        {
            lifetimeHooked = true;
            Application.quitting += DisposeNative;
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += DisposeNative;
#endif
        }
        if (!contactsSubscribed)
        {
            Physics.ContactEvent += OnContact;
            contactsSubscribed = true;
        }
        if (FrictionEnabled && !modifySubscribed)
        {
            Physics.ContactModifyEvent += OnContactModify;
            modifySubscribed = true;
        }
    }

    private static void Unsubscribe()
    {
        if (contactsSubscribed) { Physics.ContactEvent -= OnContact; contactsSubscribed = false; }
        if (modifySubscribed) { Physics.ContactModifyEvent -= OnContactModify; modifySubscribed = false; }
        DisposeNative();
    }

    // --- Per-step state (main thread) ----------------------------------------------------------

    // The normal impulse this wheel's contacts carried in the last completed step (force x dt), read
    // without clearing — for probes that print beside the controller.
    public static float PeekNormalImpulse(ArticulationBody wheel)
    {
        for (int i = 0; i < wheels.Count; i++)
            if (wheels[i].wheel == wheel) return wheels[i].normalImpulse;
        return 0f;
    }

    // The same, cleared on read, so a step in which no contact was reported reads as no load rather
    // than as last step's — an airborne wheel must not keep the load it had on the floor. Written for
    // a per-step consumer in the controller that was measured and removed; kept because that is the
    // right shape for any future one, and Peek is not (it would hand an airborne wheel a stale load).
    // Unused today — see RobotMotorController's note above the force limits before wiring it up.
    public static float ConsumeNormalImpulse(ArticulationBody wheel)
    {
        for (int i = 0; i < wheels.Count; i++)
        {
            if (wheels[i].wheel != wheel) continue;
            float v = wheels[i].normalImpulse;
            wheels[i].normalImpulse = 0f;
            return v;
        }
        return 0f;
    }

    // The friction the callback last set on this wheel's contact, or -1 if it has not been touched
    // since registration. Trace-only: it is what the solver was given, one step ago.
    public static float PeekLastMu(ArticulationBody wheel)
    {
        if (!native.IsCreated) return -1f;
        for (int i = 0; i < wheels.Count && i < native.Length; i++)
            if (wheels[i].wheel == wheel) return native[i].lastMu;
        return -1f;
    }

    public static float PeekLastLateral(ArticulationBody wheel)
    {
        if (!native.IsCreated) return 0f;
        for (int i = 0; i < wheels.Count && i < native.Length; i++)
            if (wheels[i].wheel == wheel) return native[i].lastLateral;
        return 0f;
    }

    // The external lateral force this robot's tyres were last given, in force units.
    public static float PeekExternalLateral(RobotMotorController owner)
    {
        if (!native.IsCreated) return 0f;
        for (int i = 0; i < wheels.Count && i < native.Length; i++)
            if (wheels[i].owner == owner) return native[i].externalLateral;
        return 0f;
    }

    // Called by the controller once per step, before Simulate: turn the external impulse the robot
    // collected last step into a sideways force along its own right axis, hand it to every one of
    // its tyres for the coming step, and clear the sum.
    public static float PublishExternalLateral(RobotMotorController owner, Vector3 rightAxis, float dt)
    {
        if (owner == null || !robots.TryGetValue(owner, out RobotSlot robot)) return 0f;
        float force = dt > 1e-6f ? Mathf.Abs(Vector3.Dot(robot.externalImpulse, rightAxis)) / dt : 0f;
        robot.externalImpulse = Vector3.zero;
        if (native.IsCreated)
        {
            for (int i = 0; i < wheels.Count && i < native.Length; i++)
            {
                if (wheels[i].owner != owner) continue;
                TyreEntry e = native[i];
                e.externalLateral = force;
                native[i] = e;
            }
        }
        return force;
    }

    // Set ROBOSIM_TYRE_DUMP_CONTACTS=1 to log every reported pair on the first few steps — the
    // check that a wheel reading zero load has no pair at all rather than one that is being missed.
    private static int dumpStepsLeft = -1;

    private static void OnContact(PhysicsScene scene, NativeArray<ContactPairHeader>.ReadOnly headers)
    {
        ContactEvents++;
        if (dumpStepsLeft < 0)
            dumpStepsLeft = System.Environment.GetEnvironmentVariable("ROBOSIM_TYRE_DUMP_CONTACTS") == "1" ? 3 : 0;
        // Dump only steps in which a registered wheel is touching something — the first few events
        // after a spawn are the robot's own parts settling against each other in mid-air.
        bool dump = dumpStepsLeft > 0 && AnyWheelPair(headers);
        if (dump) dumpStepsLeft--;

        // One event per step, with every pair in it: start the step's sums from zero.
        for (int i = 0; i < wheels.Count; i++) wheels[i].normalImpulse = 0f;
        foreach (RobotSlot r in robots.Values) r.externalImpulse = Vector3.zero;

        for (int h = 0; h < headers.Length; h++)
        {
            ContactPairHeader header = headers[h];
            int pairCount = header.pairCount;
            for (int p = 0; p < pairCount; p++)
            {
                ContactPair pair = header.GetContactPair(p);
                EntityId a = pair.colliderEntityId;
                EntityId b = pair.otherColliderEntityId;
                wheelById.TryGetValue(a, out WheelSlot wa);
                wheelById.TryGetValue(b, out WheelSlot wb);
                robotByCollider.TryGetValue(a, out RobotSlot ra);
                robotByCollider.TryGetValue(b, out RobotSlot rb);
                if (wa == null && wb == null && ra == null && rb == null) continue;

                Vector3 impulse = Vector3.zero;
                float normal = 0f;
                int points = pair.contactCount;
                for (int i = 0; i < points; i++)
                {
                    ContactPairPoint pt = pair.GetContactPoint(i);
                    Vector3 j = pt.impulse;
                    impulse += j;
                    normal += j.magnitude;
                }

                if (wa != null) wa.normalImpulse += normal;
                if (wb != null) wb.normalImpulse += normal;
                if (dump)
                    Debug.Log($"[tyre contacts] {(pair.collider != null ? pair.collider.name : "?")} " +
                              $"(wheel {(wa != null ? wa.wheel.name : "-")}) vs " +
                              $"{(pair.otherCollider != null ? pair.otherCollider.name : "?")} " +
                              $"(wheel {(wb != null ? wb.wheel.name : "-")}): {points} point(s), " +
                              $"normal impulse {normal:0.000}, sum {impulse}");

                // The reported impulse is the one applied to the FIRST body; the second receives its
                // negative. A robot's own parts touching each other are not an external force.
                if (ra != null && ra != rb) ra.externalImpulse += impulse;
                if (rb != null && rb != ra) rb.externalImpulse -= impulse;
            }
        }
    }

    private static bool AnyWheelPair(NativeArray<ContactPairHeader>.ReadOnly headers)
    {
        for (int h = 0; h < headers.Length; h++)
        {
            ContactPairHeader header = headers[h];
            for (int p = 0; p < header.pairCount; p++)
            {
                ContactPair pair = header.GetContactPair(p);
                if (wheelById.ContainsKey(pair.colliderEntityId) || wheelById.ContainsKey(pair.otherColliderEntityId))
                    return true;
            }
        }
        return false;
    }

    // --- The tyre itself (may run off the main thread) ------------------------------------------

    private static void OnContactModify(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
    {
        System.Threading.Interlocked.Increment(ref modifyCallbacks);
        if (!native.IsCreated) return;
        NativeArray<TyreEntry> entries = native;
        int count = entries.Length;

        for (int p = 0; p < pairs.Length; p++)
        {
            ModifiableContactPair pair = pairs[p];
            bool other = false;
            int idx = Find(entries, count, pair.colliderEntityId);
            if (idx < 0) { idx = Find(entries, count, pair.otherColliderEntityId); other = true; }
            if (idx < 0) continue;

            TyreEntry t = entries[idx];
            if (t.traction) continue;

            Quaternion rot = other ? pair.otherRotation : pair.rotation;
            Vector3 pos = other ? pair.otherPosition : pair.position;
            Vector3 v = other ? pair.otherBodyVelocity : pair.bodyVelocity;
            Vector3 w = other ? pair.otherBodyAngularVelocity : pair.bodyAngularVelocity;
            Vector3 posO = other ? pair.position : pair.otherPosition;
            Vector3 vO = other ? pair.bodyVelocity : pair.otherBodyVelocity;
            Vector3 wO = other ? pair.bodyAngularVelocity : pair.otherBodyAngularVelocity;
            Vector3 axle = rot * t.localAxle;

            float ext = t.externalLateral / t.externalScale;
            float extTerm = ext * ext;

            float lowest = t.muLong;

            int points = pair.contactCount;
            for (int i = 0; i < points; i++)
            {
                Vector3 pt = pair.GetPoint(i);
                Vector3 n = pair.GetNormal(i);

                // The wheel SURFACE against the other surface, at this point — the sphere's centre
                // is its link's centre of mass (it is the link's only collider), so the body
                // velocity plus the spin about it is the surface velocity.
                Vector3 rel = (v + Vector3.Cross(w, pt - pos)) - (vO + Vector3.Cross(wO, pt - posO));
                rel -= n * Vector3.Dot(rel, n);

                Vector3 lateral = axle - n * Vector3.Dot(axle, n);
                float latMag = lateral.magnitude;
                if (latMag < 1e-4f) continue;         // axle along the normal: no rolling direction here
                float lat = Vector3.Dot(rel, lateral) / latMag;

                float x = lat / t.lateralScale;
                float mu = t.muLat + (t.muLong - t.muLat) / (1f + x * x + extTerm);

                // Only ever LOWER what the materials gave this contact. The wheel's own 0.8 wins the
                // combine against the floor, so on the floor this is the blend; against a
                // frictionless or low-friction body (a game piece at 0.2, a validator's pusher at 0)
                // it must not push the contact UP to 0.8 — measured: a box pushing a robot from
                // behind touched a rear wheel and dragged on its rolling surface, 30% more resistance
                // than with the tyre off.
                float materialDynamic = pair.GetDynamicFriction(i);
                if (mu > materialDynamic) mu = materialDynamic;

                // Static = dynamic. The material's 0.9/0.8 split gives a LOCKED inner wheel dragged
                // across the floor something to catch on every time its contact velocity passes
                // through zero — stick, slip, stick — which is the snatch a moving turn makes on a
                // held wheel (654V_v1 69 reversals, v3 38; both 0-1 with the split removed). An omni
                // tread has no such catch: the rollers are already turning.
                pair.SetDynamicFriction(i, mu);
                pair.SetStaticFriction(i, Mathf.Min(pair.GetStaticFriction(i), mu));
                if (mu < lowest) lowest = mu;
                t.lastLateral = lat;
            }
            if (points > 0) t.lastMu = lowest;
            entries[idx] = t;
        }
    }

    private static int Find(NativeArray<TyreEntry> entries, int count, EntityId id)
    {
        for (int i = 0; i < count; i++)
            if (entries[i].id == id) return i;
        return -1;
    }
}
