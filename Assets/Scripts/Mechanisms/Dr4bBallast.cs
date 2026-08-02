using UnityEngine;

// The DR4B's weight, as a link that actually moves.
//
// WHY THIS EXISTS. Dr4bLift poses its entire linkage with transform followers, and it has to: a
// double-reverse-four-bar is a closed kinematic loop, an ArticulationBody tree is a tree, and the
// one time this project tried to express the loop as coupled physics joints the stiff drives on
// light bars exploded numerically and pinned the CPU. So the visuals are right and the physics
// knows nothing about them — MechanismBuildUtil.NeutralizeToPlainTransform DESTROYS each part's
// body, and DisableColliders switches off its shapes.
//
// The cost was invisible until someone asked why the robot wouldn't tip. Raising a DR4B moved
// exactly zero kilograms: the only body in the whole mechanism was a colliderless motor hub whose
// centre of mass sits on its own rotation axis, so rotating it cannot move it. Measured on
// 654V_v1, the composite centre of mass was identical lift up and lift down, and the balance report
// printed no "lift up" line at all because the robot had no prismatic joint to report travel for.
// A robot whose lift cannot move its centre of mass cannot be tipped by raising it, which is most
// of what makes a lift interesting to drive with.
//
// So: one real, serial, prismatic link, no collider, carrying the assembly's real mass, sliding
// along the assembly's real travel. It is a MASS PROXY and nothing else — nothing renders, nothing
// collides. What it buys is that the solver's composite centre of mass, its rotational inertia, and
// therefore its tip threshold all move the way the visible lift says they should.
//
// WHY A SLANTED AXIS. A DR4B does not go straight up. On 654V_v1 the two stages rise 300 and 330 mm
// while drifting 160 mm BACKWARD, and that horizontal component is not a rounding error against a
// nose margin of ~125 mm. A prismatic joint's axis is just a direction, so the builder points it
// along the mass-weighted total travel and the single DOF covers both — no second joint, no
// approximation to apologise for.
//
// Wired by Tools > RoboSim > Robot > Mechanisms > Build DR4B Lift. Nothing to set up by hand.
[DisallowMultipleComponent]
public class Dr4bBallast : MonoBehaviour
{
    [Tooltip("The lift whose Progress (0 at rest, 1 fully raised) this ballast tracks.")]
    public Dr4bLift lift;
    [Tooltip("The prismatic link this drives. Defaults to the ArticulationBody on this GameObject.")]
    public ArticulationBody body;

    [Tooltip("How far the ballast slides along its joint axis at full lift, in world units " +
             "(1 unit = 0.1 m). The builder sets this from the mass-weighted travel of the parts " +
             "the lift actually moves; it is the joint's upper limit too.")]
    public float travel;

    [Header("Drive Settings")]
    [Tooltip("Position spring gain — high, because this link must TRACK the visual lift rather " +
             "than sag behind it. Same value JointCoupler uses in Position mode.")]
    public float positionStiffness = 20000f;
    [Tooltip("Velocity damping — enough to kill ringing without making the lift feel laggy.")]
    public float positionDamping = 500f;
    [Tooltip("Drive force limit. Generous: this link carries no load of its own, and a ballast " +
             "that stalls behind the visuals is a centre of mass in the wrong place.")]
    public float forceLimit = 4000f;

    void Awake()
    {
        if (body == null) body = GetComponent<ArticulationBody>();
        if (lift == null) lift = GetComponentInParent<Dr4bLift>();
        BakeDrive();
    }

    // Public, and called by the authoring tool at edit time as well as from Awake, for the reason
    // JointCoupler.BakeDrive and MotorActuator's bake are public: edit-mode Physics.Simulate
    // validation never runs Awake, so anything only Awake sets is invisible to it. The serialized
    // drive and the play-mode drive have to be the same drive.
    public void BakeDrive()
    {
        if (body == null) return;

        ArticulationDrive d = body.xDrive;
        d.driveType = ArticulationDriveType.Target;
        d.stiffness = positionStiffness;
        d.damping = positionDamping;
        d.forceLimit = forceLimit;
        d.lowerLimit = Mathf.Min(0f, travel);
        d.upperLimit = Mathf.Max(0f, travel);
        body.xDrive = d;

        // A prismatic joint's maxJointVelocity is in DISTANCE per second, not radians — the one
        // unit trap in this file. Sized off the lift's own raise time with headroom, so a lift
        // retimed to half a second doesn't leave the ballast crawling behind the visuals with the
        // centre of mass somewhere between the two poses.
        float seconds = lift != null ? Mathf.Max(0.05f, lift.liftRaiseSeconds) : 1f;
        body.maxJointVelocity = Mathf.Max(Mathf.Abs(travel) / seconds * 3f, 10f);
    }

    void FixedUpdate() => ApplyStep();

    // One tracking step. Public for the same edit-mode reason as BakeDrive — a headless harness
    // stepping Physics.Simulate gets no FixedUpdate and has to pump this itself.
    public void ApplyStep()
    {
        if (body == null || lift == null) return;
        body.SetDriveTarget(ArticulationDriveAxis.X, lift.Progress * travel);
    }
}
