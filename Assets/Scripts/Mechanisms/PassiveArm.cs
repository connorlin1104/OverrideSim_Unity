using UnityEngine;

// An UNPOWERED hinge: a flap, a deflector, a wiper that only turns because something OUTSIDE the
// robot hits it — a game piece, a wall, the field — and comes back on its own. Nothing here reads
// a button.
//
// WHY A COMPONENT AT ALL. A revolute with no actuator is already unpowered, but the one thing a
// bare joint cannot do is the whole feature:
//
//   THE RUBBER BAND. The real part is held against its drawn pose by an elastic, so it returns
//   after being knocked. That is a position-target drive at target 0 — joint zero IS the drawn
//   pose, and Set Starting Pose re-zeroes the joint whenever the drawn pose changes, so the target
//   never has to move: the band always pulls the arm back to exactly where the prefab draws it, and
//   nowhere else. The band is PRE-TENSIONED: it pulls with everything it has within a few degrees
//   of rest (BandSaturationDegrees) and then saturates, which is exactly what a stiff spring under a
//   force cap gives. The cap is sized in multiples of the arm's own weight (bandStrength) by the
//   builder, and the three baked numbers are serialized so edit-mode physics and play mode run one
//   drive.
//
//   IT MOVES ONLY ITS OWN PART. The band is a drive on THIS link's own joint — it moves this one
//   arm and nothing else. The arm is also kept from colliding with the rest of its OWN robot: the
//   builder puts an IgnoreRobotSelfCollision on it, exactly like every other mechanism link, so a
//   returning arm can never shove the chassis, a wheel, or another mechanism around (that cross-talk
//   is what makes a robot lurch or lose its drive after a hit). The arm still collides with
//   everything OUTSIDE the robot — game pieces, walls, the floor — which is the whole point: a
//   spring-loaded flap that deflects pieces and springs straight back to its drawn pose, entirely
//   self-contained. An earlier version re-enabled collision with the robot's own toggle so the
//   toggle could bat it; that also let the arm bang into its own frame, so it is gone. Drive the arm
//   with an arm motor if you want the robot itself to move it.
//
// Awake bakes the drive; it is public because edit-mode Physics.Simulate never runs Awake (the
// Dr4bBallast.BakeDrive convention), and the builder calls BakeDrive so the serialized drive is the
// play-mode drive.
//
// Built by Tools > RoboSim > Robot > Mechanisms > Add or Fix Mechanism Joint (Passive arm).
[DisallowMultipleComponent]
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
    // live on this link (~10 rad/s at 100 RPM) would make a fast piece hit look like a drag.
    private const float PushedJointVelocityCap = 100f;

    void Awake()
    {
        if (body == null) body = GetComponent<ArticulationBody>();
        BakeDrive();
    }

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

    public string DescribeBand() => returnToRest ? $"rubber band ×{bandStrength:0.#}" : "free hinge";
}
