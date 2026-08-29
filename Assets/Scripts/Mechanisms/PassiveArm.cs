using System.Collections.Generic;
using UnityEngine;

// An UNPOWERED hinge: a flap, a deflector, a wiper that only turns because something hits it —
// usually the robot's own toggle — and comes back on its own. Nothing here reads a button.
//
// WHY A COMPONENT AT ALL. A revolute with no actuator is already unpowered, but the two things a
// bare joint cannot do are the whole feature:
//
//   THE RUBBER BAND. The real part is held against its drawn pose by an elastic, so it returns
//   after being knocked. That is a position-target drive at target 0 — joint zero IS the drawn
//   pose, and Set Starting Pose re-zeroes the joint whenever the drawn pose changes, so the target
//   never has to move. The band is PRE-TENSIONED: it pulls with everything it has within a few
//   degrees of rest (BandSaturationDegrees) and then saturates, which is exactly what a stiff
//   spring under a force cap gives. The cap is sized in multiples of the arm's own weight
//   (bandStrength) by the builder, and the three baked numbers are serialized so edit-mode
//   physics and play mode run one drive.
//
//   COLLIDING WITH ITS OWN ROBOT. IgnoreRobotSelfCollision blanks EVERY pair between a mechanism
//   link and the rest of its robot, and that blanket is the reason nothing on a robot could push
//   anything else. A passive arm exists to be pushed, so it re-decides every pair itself, after
//   the blanket has run (execution order 60; IgnoreRobotSelfCollision is 50, and the drivetrain's
//   Awake pass is earlier still): a pair that already overlaps in the rest pose is a part drawn
//   bolted through another and stays muted; every other pair collides. That is the same rule
//   RobotMotorController.IgnoreBuiltInSelfOverlaps applies to the whole robot at Awake, decided by
//   the same OverlapsAtRest test. The only pairs left alone are direct joint parent<->child,
//   which PhysX never collides anyway — so an arm split off the chassis never touches the chassis
//   (its joint limit is the hard stop), and an arm mounted on the toggle does hit the chassis,
//   which is what "it collides with everything it hits" means.
//
// Awake bakes the drive and Start applies the collision rules; both are public because edit-mode
// Physics.Simulate runs neither (the Dr4bBallast.BakeDrive convention), and the builder calls
// BakeDrive so the serialized drive is the play-mode drive.
//
// Built by Tools > RoboSim > Robot > Mechanisms > Add or Fix Mechanism Joint (Passive arm).
[DisallowMultipleComponent]
[DefaultExecutionOrder(60)]
public class PassiveArm : MonoBehaviour
{
    [Tooltip("The hinge this arm turns on. Defaults to the ArticulationBody on this GameObject.")]
    public ArticulationBody body;

    [Header("Rubber band")]
    [Tooltip("Pull the arm back to its drawn pose after it has been knocked. Off = a free hinge with " +
             "a little friction, which flops wherever it was left.")]
    public bool returnToRest = true;
    [Tooltip("How hard the band pulls, in multiples of the arm's own weight held out at its centre. " +
             "1 = just enough to lift it; 3 = returns briskly; 10 = nearly rigid. The builder sizes " +
             "the three baked numbers below from this.")]
    public float bandStrength = DefaultBandStrength;
    [Tooltip("Baked: the band's torque cap. bandStrength x (mass x gravity x lever arm).")]
    public float bandForceLimit;
    [Tooltip("Baked: the band's spring, torque per radian. Reaches the cap a few degrees off rest.")]
    public float bandStiffness;
    [Tooltip("Baked: near-critical damping for the arm's inertia about the hinge.")]
    public float bandDamping;

    [Header("Band off")]
    [Tooltip("Drive damping when the band is off — the hinge's own friction, so a knocked arm coasts " +
             "to a stop instead of swinging forever.")]
    public float hingeFriction = 2f;

    public const float DefaultBandStrength = 3f;

    // A pre-tensioned band is already pulling at full strength a hair off rest; the spring only
    // exists to give the solver a slope to converge on. That slope is also the arm's DROOP: a
    // linear spring puts out nothing at rest, so an arm held out against gravity settles where
    // spring = weight, which is this angle divided by bandStrength. 3 degrees keeps a x3 arm
    // within a degree of its drawn pose and a x1 arm ("just enough to lift it") within 3; a wider
    // slope reads as a flap hanging visibly below where it was drawn. PassiveArmValidation holds
    // the return to 2 degrees at the default strength, which this has to leave room for.
    public const float BandSaturationDegrees = 3f;

    // Below this the band is not a band. A weightless arm (no closed mesh, so no mass from
    // geometry) would otherwise size a cap of zero and never return.
    public const float MinBandForceLimit = 5f;

    // Damping is sized against mass x r^2, and an arm whose centre sits on its own hinge line would
    // size to zero and ring forever. A quarter unit (25 mm) is smaller than any flap worth rigging.
    public const float MinLeverArm = 0.25f;

    // A pushed arm turns as fast as the thing hitting it. A cap left behind by a motor that used to
    // live on this link (~10 rad/s at 100 RPM) would make a fast toggle hit look like a drag.
    private const float PushedJointVelocityCap = 100f;

    void Awake()
    {
        if (body == null) body = GetComponent<ArticulationBody>();
        BakeDrive();
    }

    void Start() => ApplyCollisionRules();

    // Sizes the three baked band numbers from the arm's mass, its lever arm (pivot line to the
    // centre of what it moves, in world units) and gravity. Called by the builder with the
    // renderer-bounds centre, so the numbers ship on the prefab and never depend on what runs at
    // Awake. torque = mass x g x r is the torque gravity puts on the arm held out horizontally;
    // the band is bandStrength of those.
    public void SizeBand(float leverArm, float gravity)
    {
        float mass = body != null ? body.mass : 1f;
        float weightTorque = mass * Mathf.Abs(gravity) * Mathf.Max(0f, leverArm);
        bandForceLimit = Mathf.Max(MinBandForceLimit, bandStrength * weightTorque);
        bandStiffness = bandForceLimit / (BandSaturationDegrees * Mathf.Deg2Rad);
        float r = Mathf.Max(leverArm, MinLeverArm);
        bandDamping = 2f * Mathf.Sqrt(bandStiffness * mass * r * r);
    }

    // Writes the drive the band (or the free hinge) is. Target 0 in both cases: joint zero is the
    // drawn pose, and with the band off the target is moot because the spring is zero.
    //
    // FORCE, not Target. A Target drive is not a spring: PhysX solves it as a position CONSTRAINT
    // bounded by forceLimit, the way a Velocity drive is a velocity constraint (the drivetrain
    // learned that one first). Measured in PassiveArmValidation with this drive as Target: an arm
    // whose weight put 38.6 of torque on a 2246-per-radian "spring" sagged 0.000 degrees — it sat
    // exactly at zero until the load beat the cap. A Force drive is the spring-damper the numbers
    // describe: torque = stiffness x (target - angle) + damping x (0 - rate), capped at forceLimit,
    // and that sag is what a rubber band looks like.
    public void BakeDrive()
    {
        if (body == null) body = GetComponent<ArticulationBody>();
        if (body == null) return;

        ArticulationDrive d = body.xDrive;
        d.driveType = ArticulationDriveType.Force;
        d.target = 0f;
        d.targetVelocity = 0f;
        if (returnToRest)
        {
            d.stiffness = bandStiffness;
            d.damping = bandDamping;
            d.forceLimit = bandForceLimit;
        }
        else
        {
            d.stiffness = 0f;
            d.damping = hingeFriction;
            d.forceLimit = float.MaxValue;
        }
        body.xDrive = d;
        body.maxJointVelocity = PushedJointVelocityCap;
    }

    // Decides every collider pair between this arm and the rest of its robot: muted if the two
    // already interpenetrate where the robot stands now (drawn bolted through), colliding
    // otherwise — including pairs a blanket IgnoreRobotSelfCollision on the OTHER link switched
    // off a moment earlier, which is why this runs at execution order 60. Returns how many pairs
    // were muted; `report` (optional) gets one line per muted pair so the builder can say which.
    public int ApplyCollisionRules(List<string> report = null)
    {
        int muted = 0;
        ForEachRobotPair((mine, other, owner) =>
        {
            bool bolted = RobotMotorController.OverlapsAtRest(mine, other, out float depth);
            Physics.IgnoreCollision(mine, other, bolted);
            if (!bolted) return;
            muted++;
            report?.Add($"{owner.name}/{other.name} ({depth * 100f:0.0} mm)");
        });
        return muted;
    }

    // The pairs ApplyCollisionRules WOULD mute, without touching anything — the builder logs these
    // at edit time so a part drawn through the arm is a known fact rather than a surprise in play.
    public List<string> RestOverlaps()
    {
        var overlaps = new List<string>();
        ForEachRobotPair((mine, other, owner) =>
        {
            if (RobotMotorController.OverlapsAtRest(mine, other, out float depth))
                overlaps.Add($"{owner.name}/{other.name} ({depth * 100f:0.0} mm)");
        });
        return overlaps;
    }

    public string DescribeBand() => returnToRest ? $"rubber band ×{bandStrength:0.#}" : "free hinge";

    // Every (arm collider, other robot collider) pair the rules apply to. Colliders on this link's
    // own subtree stop at a nested body (that geometry belongs to a link hinged on this arm), and
    // the other side skips this link's direct joint parent and direct joint children — PhysX
    // filters those itself, so deciding them here would be noise. Nothing outside the robot root
    // is ever touched: pieces, walls and the floor are the whole point of the arm.
    private void ForEachRobotPair(System.Action<Collider, Collider, ArticulationBody> visit)
    {
        if (body == null) body = GetComponent<ArticulationBody>();
        if (body == null) return;
        RobotMechanisms robot = GetComponentInParent<RobotMechanisms>();
        if (robot == null) return;
        Physics.SyncTransforms();

        ArticulationBody parentLink = NearestBodyAbove(body.transform);

        var mine = new List<Collider>();
        foreach (Collider c in body.GetComponentsInChildren<Collider>(true))
            if (Usable(c) && c.GetComponentInParent<ArticulationBody>(true) == body) mine.Add(c);
        if (mine.Count == 0) return;

        foreach (Collider other in robot.GetComponentsInChildren<Collider>(true))
        {
            if (!Usable(other)) continue;
            ArticulationBody owner = other.GetComponentInParent<ArticulationBody>(true);
            if (owner == null || owner == body || owner == parentLink) continue;
            if (NearestBodyAbove(owner.transform) == body) continue;   // hinged ON this arm
            foreach (Collider m in mine) visit(m, other, owner);
        }
    }

    // Active, enabled, solid. IgnoreCollision on a disabled collider warns and does not stick, and
    // a trigger never pushes anything.
    private static bool Usable(Collider c) =>
        c != null && c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy;

    // The joint parent, found by walking the hierarchy — never ArticulationBody.isRoot, which reads
    // true for every body on a prefab that has not been instantiated.
    private static ArticulationBody NearestBodyAbove(Transform t)
    {
        for (Transform p = t.parent; p != null; p = p.parent)
        {
            ArticulationBody b = p.GetComponent<ArticulationBody>();
            if (b != null) return b;
        }
        return null;
    }
}
