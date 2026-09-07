using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

// Stops a marked mechanism link from HOLDING THE ROBOT UP on anything, while leaving it free to
// push things around.
//
// WHY THIS EXISTS. IgnoreFieldFloor already solved this once, for the floor: a driven roller hung
// low enough to pick pieces off the tiles is, to PhysX, a rigid box with friction, and a robot that
// pitches onto it ends up standing on it. That fix mutes the link against the ground slab by name,
// and it works — for the ground slab. The field has other things to stand on.
//
// Measured on 654V_v3, six laps of a scripted match on the field (WheelGroundContactProbe,
// 2026-09-06). For 35 s the robot dips to two loaded wheels and recovers every time. Then it climbs
// and never gets off, right through the settle phases where it is stationary with no input:
//     34.5s  loaded 5/6  tilt 0.0 deg  height -0.19
//     35.0s  loaded 2/6  tilt 3.0 deg  height -0.09   <- climbs
//     45.0s  loaded 2/6  tilt 3.0 deg  height -0.09   <- still up, sitting still
// Four of six wheels off the ground for up to 4.6 s, gaps to 224 mm, and the census names what is
// holding it up: IntakeMech, 51% of an even wheel share, resting on GoalWall_Outer_Octagon_0 (76%)
// and CupWall_Top_Inner_3 (23%). Not the floor, so IgnoreFieldFloor could never have caught it.
// Connor: "the drivetrain just breaks after a while. Half the wheels cease touching the ground."
// 654V_v2 has no intake mechanism and never lifts a wheel in the same run — the control that names
// the mechanism rather than the drivetrain.
//
// WHY NOT Physics.IgnoreCollision, the way IgnoreFieldFloor does it. That is all or nothing. An
// intake MUST collide with cups — moving them is its entire job — so muting it against pieces
// breaks the mechanism, and muting it against the goal lets it pass through a wall on camera.
// What has to go is not the contact, only the contact's ability to CARRY WEIGHT.
//
// HOW. Contact modification, the same PhysX hook the tyre uses, on its own subscription so the
// tyre is untouched. A contact is killed only when both of these hold:
//   1. its normal is within VerticalDegrees of straight up, i.e. it is a resting contact, not a
//      pushing one — a shove against a cup or a wall is horizontal and survives untouched; and
//   2. the contact point is BELOW the marked body's own centre, i.e. the link is on top of the
//      other thing rather than under it — so the intake can still lift a piece from beneath.
// Both tests are sign-free ON PURPOSE. PhysX's normal points from one shape in the pair to the
// other and which one you get depends on the pair's ordering, so a rule written on the sign of
// n.y is a coin flip that silently does nothing (or the exact opposite) half the time. |n.y|
// answers "is this a resting contact" without caring, and the point-versus-centre test answers
// "which of us is on top" from geometry that has no convention at all.
public static class NonSupportingLinkModel
{
    // How far from vertical a contact normal may lean and still count as the link resting on
    // something. Wide enough to catch a link perched on the sloped shoulder of a goal wall,
    // narrow enough that pushing a cup across the floor is untouched.
    public const float VerticalDegrees = 40f;

    private static readonly float VerticalCos = Mathf.Cos(VerticalDegrees * Mathf.Deg2Rad);

    private struct Entry
    {
        public EntityId id;
    }

    private static readonly Dictionary<NonSupportingLink, List<EntityId>> owners =
        new Dictionary<NonSupportingLink, List<EntityId>>();
    private static NativeArray<Entry> native;
    private static bool subscribed;

    // Diagnostics. A component whose whole effect is a contact that never happens is invisible
    // until something climbs, so the counts are the only way to tell "working" from "not wired up".
    public static int RegisteredColliderCount { get; private set; }
    private static int suppressed;
    public static int SuppressedContacts => suppressed;

    // Contacts on a marked link bucketed by how far their normal leans from vertical, split by
    // whether the link was on top. The cone threshold is a guess until this says what is actually
    // there, and a threshold that misses the real geometry looks exactly like a fix that half works.
    //
    // OFF unless a probe asks for it. This is two interlocked increments per contact per step
    // inside the solver callback, on a link that can carry dozens of contacts; a diagnostic has no
    // business costing that in a shipped match.
    public static bool CollectDiagnostics { get; set; }
    private static readonly int[] coneAbove = new int[9];
    private static readonly int[] coneBelow = new int[9];

    public static string ConeHistogram()
    {
        var sb = new System.Text.StringBuilder("normal angle from vertical (deg): ");
        for (int i = 0; i < 9; i++)
            sb.Append($"{i * 10}-{i * 10 + 10}: on-top {coneAbove[i]} / under {coneBelow[i]}  ");
        return sb.ToString();
    }

    public static void ResetCounters()
    {
        suppressed = 0;
        System.Array.Clear(coneAbove, 0, 9);
        System.Array.Clear(coneBelow, 0, 9);
    }

    // ROBOSIM_NONSUPPORT_OFF=1 runs any harness with this model disabled, for a single-variable
    // A/B. Same shape as ROBOSIM_TYRE_OFF.
    private static bool envChecked, disabled;

    public static bool Disabled
    {
        get
        {
            if (!envChecked)
            {
                envChecked = true;
                disabled = System.Environment.GetEnvironmentVariable("ROBOSIM_NONSUPPORT_OFF") == "1";
            }
            return disabled;
        }
    }

    public static void Register(NonSupportingLink link)
    {
        if (link == null || Disabled) return;
        var ids = new List<EntityId>();
        foreach (Collider c in link.GetComponentsInChildren<Collider>(true))
        {
            if (c == null || c.isTrigger) continue;
            c.hasModifiableContacts = true;
            ids.Add(c.GetEntityId());
        }
        if (ids.Count == 0) return;
        owners[link] = ids;
        Rebuild();
    }

    public static void Unregister(NonSupportingLink link)
    {
        if (link == null || !owners.Remove(link)) return;
        Rebuild();
    }

    public static void Clear()
    {
        owners.Clear();
        Rebuild();
    }

    private static void Rebuild()
    {
        if (native.IsCreated) native.Dispose();
        var all = new List<EntityId>();
        foreach (KeyValuePair<NonSupportingLink, List<EntityId>> kv in owners) all.AddRange(kv.Value);
        RegisteredColliderCount = all.Count;

        if (all.Count == 0)
        {
            if (subscribed) { Physics.ContactModifyEvent -= OnContactModify; subscribed = false; }
            return;
        }

        native = new NativeArray<Entry>(all.Count, Allocator.Persistent);
        for (int i = 0; i < all.Count; i++) native[i] = new Entry { id = all[i] };
        if (!subscribed) { Physics.ContactModifyEvent += OnContactModify; subscribed = true; }
    }

    // Off the main thread: NativeArray and plain arithmetic only, no Unity API.
    private static void OnContactModify(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
    {
        if (!native.IsCreated) return;
        NativeArray<Entry> entries = native;
        int count = entries.Length;

        for (int p = 0; p < pairs.Length; p++)
        {
            ModifiableContactPair pair = pairs[p];
            bool other = false;
            int idx = Find(entries, count, pair.colliderEntityId);
            if (idx < 0) { idx = Find(entries, count, pair.otherColliderEntityId); other = true; }
            if (idx < 0) continue;

            // The marked body's own centre. Anything the link touches BELOW this is something it
            // is standing on; anything above it is something it is holding up, which is allowed.
            Vector3 mine = other ? pair.otherPosition : pair.position;

            for (int i = 0; i < pair.contactCount; i++)
            {
                Vector3 n = pair.GetNormal(i);
                bool onTop = pair.GetPoint(i).y < mine.y;

                if (CollectDiagnostics)
                {
                    int bucket = Mathf.Clamp((int)(Mathf.Acos(Mathf.Clamp(Mathf.Abs(n.y), 0f, 1f))
                                                   * Mathf.Rad2Deg / 10f), 0, 8);
                    if (onTop) System.Threading.Interlocked.Increment(ref coneAbove[bucket]);
                    else System.Threading.Interlocked.Increment(ref coneBelow[bucket]);
                }

                if (Mathf.Abs(n.y) < VerticalCos) continue;   // a push, not a rest
                if (!onTop) continue;                         // the link is UNDER this, so it is
                                                              // holding the thing up, which is fine

                pair.SetMaxImpulse(i, 0f);
                System.Threading.Interlocked.Increment(ref suppressed);
            }
        }
    }

    private static int Find(NativeArray<Entry> entries, int count, EntityId id)
    {
        for (int i = 0; i < count; i++)
            if (entries[i].id == id) return i;
        return -1;
    }
}
