using UnityEngine;

// Hold-to-run motor driver for a single non-drivetrain ArticulationBody joint (arm, lift,
// intake...). Same torque-limited velocity-drive model as the drivetrain wheels in
// RobotMotorController: stiffness 0 (no position spring), damping as the velocity-tracking
// gain, forceLimit as the stall torque — so a loaded arm slows and stalls instead of
// teleporting through obstacles.
//
// SetInput applies the drive target immediately rather than in FixedUpdate: drive target
// velocities persist across physics steps, and this is what lets edit-mode batch validation
// (Physics.Simulate, where Awake/Update never run) drive a joint whose drive parameters were
// baked at edit time by the URDF post-processor.
//
// A joint that has TRAVEL LIMITS holds its angle when the input is 0 (see ShouldHoldWhenIdle). That is
// the difference between an arm and a roller: a velocity drive with a target of 0 resists how FAST a
// back-drive turns the joint, never where it ends up, so gravity walks a raised arm down and it never
// stays up. A free-spinning link is left to coast, which is what a roller or flywheel is for.
//
// Works for both revolute joints (maxRpm, drive speaks degrees/s) and prismatic joints
// (maxLinearSpeed in world units/s). Never touches the joint anchors — for URDF imports the
// importer's anchorRotation encodes the joint axis, and re-seeding it is the known way to
// break a joint.
//
// Usage: added and wired by the URDF post-processor for every non-wheel revolute/continuous
// joint; ButtonRouter calls SetInput from the mapped controller buttons.
public class MotorActuator : MonoBehaviour
{
    [Tooltip("The joint link to drive. Defaults to the ArticulationBody on this GameObject.")]
    public ArticulationBody body;

    [Header("Motor Settings")]
    [Tooltip("Free-spin speed at full input for revolute joints, in RPM (100 RPM = a typical geared V5 arm — tune per mechanism).")]
    public float maxRpm = 100f;
    [Tooltip("Full-input speed for prismatic joints, in world units/s (1 unit = 0.1 m).")]
    public float maxLinearSpeed = 2f;
    [Tooltip("Drive force limit — the motor's stall torque (revolute) or stall force (prismatic) in scaled units.")]
    public float stallTorque = 700f;
    [Tooltip("Velocity drives use damping as the velocity-tracking gain. MUST be > 0 or the drive produces no torque at all.")]
    public float velocityDriveDamping = 1000f;
    [Tooltip("Flip if the mechanism empirically runs backward for 'forward' input.")]
    public bool invert;

    [Header("Idle hold")]
    [Tooltip("Hold the joint's current angle when input is 0 — for a lift/arm that must not sag or be shoved off its height by contact. A plain velocity drive only fights the SPEED of a back-drive, so a sustained push (e.g. ramming a round field roller, whose reaction is angled UP, unlike a flat wall) slowly creeps the joint. This pins the angle instead. Leave OFF for free-spinning rollers that should coast. Revolute joints only.")]
    public bool holdPositionWhenIdle;
    [Tooltip("Position spring stiffness while holding idle (only if Hold Position When Idle). Higher = firmer hold against external pushes; the hold torque is still capped by the stall torque, so a shove past stall still gives — but it springs back when released instead of staying where it was pushed.")]
    public float holdStiffness = 20000f;
    [Tooltip("Let this joint go LIMP when idle instead of holding its angle. Only matters for a joint with travel limits (an arm, a wrist, a rotating outtake): those hold their angle by default, because a plain velocity drive resists only the SPEED of a back-drive, so gravity walks a raised arm down a fraction of a degree at a time and it never stays up. Free-spinning rollers and flywheels always coast — they are never held either way. Tick this for a limited joint that SHOULD fall or swing freely when you let go.")]
    public bool coastWhenIdle;

    // Last commanded input in [-1, 1]; ButtonRouter uses it to skip redundant drive writes.
    public float CurrentInput { get; private set; }

    // True while the idle position-hold drive is engaged (so the hold angle is captured only ONCE,
    // on the moving->idle transition — a push while idle must not ratchet the hold angle upward).
    private bool holding;

    private bool IsPrismatic => body != null && body.jointType == ArticulationJointType.PrismaticJoint;

    void Awake() => Configure();

    // Everything the motor needs before a button can drive it: resolve the joint, bake the motor model
    // into its X drive, cap the joint velocity, and decide whether this joint holds its angle when idle.
    //
    // Public and idempotent so the headless validator can set a motor up exactly the way the game does —
    // Awake never runs in edit mode, and a validator that re-implemented this would be testing its own
    // copy of the rule instead of the real one.
    public void Configure()
    {
        if (body == null) body = GetComponent<ArticulationBody>();
        if (body == null)
        {
            Debug.LogWarning("MotorActuator: no ArticulationBody assigned or found on this GameObject.", this);
            return;
        }

        // Bake the motor model into the joint's X drive (struct: copy, modify, assign back).
        // The URDF post-processor bakes the same values at edit time; re-baking here keeps the
        // component correct when hand-added to a joint in the editor.
        ArticulationDrive d = body.xDrive;
        d.driveType = ArticulationDriveType.Velocity;
        d.stiffness = 0f;
        d.damping = velocityDriveDamping;
        d.forceLimit = stallTorque;
        body.xDrive = d;

        // maxJointVelocity is rad/s for revolute joints, units/s for prismatic. Cap slightly
        // above the full-input target so the drive can actually reach it.
        body.maxJointVelocity = IsPrismatic
            ? maxLinearSpeed * 1.1f
            : maxRpm * Mathf.PI * 2f / 60f * 1.1f;

        // Decide whether this joint holds its angle when idle, then engage the hold now so it is pinned
        // from frame 0 (ButtonRouter may never send an explicit SetInput(0), so we can't wait for one).
        // Runs regardless of Awake order vs. whoever set the flag — see SetHoldPositionWhenIdle.
        holdPositionWhenIdle = ShouldHoldWhenIdle(body, holdPositionWhenIdle, coastWhenIdle);
        if (holdPositionWhenIdle) EnterHold();
    }

    // A joint with a TRAVEL RANGE — an arm, a wrist, a rotating outtake — is meant to stay where the
    // driver left it, and a velocity drive cannot do that: with a target of 0 it fights the SPEED of a
    // back-drive, not the position, so gravity creeps a raised arm down and nothing ever brings it back.
    // So a limited revolute holds its angle when idle by DEFAULT — exactly what Dr4bLift and CascadeLift
    // already switch on by hand for their lift drivers — and a free-spinning roller/flywheel never does,
    // because coasting is its whole job. coastWhenIdle opts a limited joint back out.
    //
    // The opt-out is a NEW field on purpose: every shipped robot serializes `holdPositionWhenIdle: 0`,
    // and a saved value beats a changed C# default, so flipping that default would have reached no
    // existing prefab. A field absent from the YAML deserializes to its C# default, which does.
    //
    // Pure and public so the headless validator can test the rule directly.
    public static bool ShouldHoldWhenIdle(ArticulationBody body, bool holdPositionWhenIdle, bool coastWhenIdle)
    {
        if (coastWhenIdle) return false;
        return holdPositionWhenIdle || HoldsAngleByDefault(body);
    }

    // Revolute + LimitedMotion is the "positions to an angle and stays there" joint. FreeMotion is a
    // roller (must coast), LockedMotion has no travel to hold, and prismatic joints are excluded because
    // EnterHold speaks degrees — a linear lift is held by its own builder instead (CascadeLift bakes stiff
    // position drives on the stages).
    private static bool HoldsAngleByDefault(ArticulationBody body) =>
        body != null &&
        body.jointType == ArticulationJointType.RevoluteJoint &&
        body.twistLock == ArticulationDofLock.LimitedMotion;

    // The hold angle can only be read once PhysX has stepped the joint — jointPosition is NaN before that
    // — and ButtonRouter never sends SetInput(0) for a button nobody has pressed yet, so a joint that
    // should be holding from the start would otherwise sit there limp forever waiting for an input that
    // never comes. Retry until it takes; once holding, this costs a bool test per step.
    void FixedUpdate()
    {
        if (holdPositionWhenIdle && !holding && !IsPrismatic && Mathf.Abs(CurrentInput) < 1e-4f)
            EnterHold();
    }

    // Live-set the free-spin speed (RPM, revolute) and re-apply the joint's velocity cap so a FASTER
    // speed can actually be reached (maxJointVelocity is otherwise only baked in Awake). Safe to call at
    // runtime — e.g. Dr4bLift's editable raise-time knob re-derives RPM from seconds and calls this so the
    // lift speed can be tuned live without a rebuild. No-op for prismatic joints (they use maxLinearSpeed).
    public void SetMaxRpm(float rpm)
    {
        maxRpm = Mathf.Max(0f, rpm);
        if (body == null) body = GetComponent<ArticulationBody>();
        if (body != null && !IsPrismatic)
            body.maxJointVelocity = maxRpm * Mathf.PI * 2f / 60f * 1.1f;
    }

    // input in [-1, 1]: +1 full forward, -1 full reverse, 0 brake. With holdPositionWhenIdle a zero
    // input pins the joint's current angle (a position drive) so contact can't back-drive it;
    // otherwise 0 is a velocity target of 0 (damping resists motion but slow sustained force creeps).
    public void SetInput(float input)
    {
        if (body == null) return;
        CurrentInput = Mathf.Clamp(input, -1f, 1f);

        if (holdPositionWhenIdle && !IsPrismatic && Mathf.Abs(CurrentInput) < 1e-4f)
        {
            if (!holding) EnterHold();   // capture the angle once; a later idle push must not re-capture
            return;
        }
        if (holding) ExitHold();

        float sign = invert ? -1f : 1f;
        // Revolute drive targets are in DEGREES per second: rpm x 360/60 = rpm x 6.
        float target = IsPrismatic
            ? CurrentInput * maxLinearSpeed
            : CurrentInput * maxRpm * 6f;
        body.SetDriveTargetVelocity(ArticulationDriveAxis.X, target * sign);
    }

    // Enable/disable idle position-hold at runtime, applying it immediately if the joint is already
    // idle. Dr4bLift turns this on for its lift driver at startup, so an EXISTING lift prefab is
    // fixed with no rebuild. Resolves body itself so it works whatever the Awake order is.
    public void SetHoldPositionWhenIdle(bool enabled)
    {
        holdPositionWhenIdle = enabled;
        if (body == null) body = GetComponent<ArticulationBody>();
        if (enabled)
        {
            if (!holding && !IsPrismatic && Mathf.Abs(CurrentInput) < 1e-4f) EnterHold();
        }
        else if (holding) ExitHold();
    }

    // Pin the joint at its current angle with a stiff position-target drive (force-capped by the
    // stall torque, so a shove past stall still gives — but it springs back on release).
    private void EnterHold()
    {
        if (body == null) return;
        ArticulationReducedSpace p = body.jointPosition;
        float angleDeg = (p.dofCount > 0 ? p[0] : 0f) * Mathf.Rad2Deg;
        if (float.IsNaN(angleDeg)) return;   // joint not stepped yet — a later idle call retries

        ArticulationDrive d = body.xDrive;
        d.driveType = ArticulationDriveType.Target;
        d.stiffness = holdStiffness;
        d.damping = velocityDriveDamping;
        d.forceLimit = stallTorque;
        body.xDrive = d;
        body.SetDriveTarget(ArticulationDriveAxis.X, angleDeg);
        holding = true;
    }

    // Restore the velocity drive so the motor runs on command again.
    private void ExitHold()
    {
        if (body == null) return;
        ArticulationDrive d = body.xDrive;
        d.driveType = ArticulationDriveType.Velocity;
        d.stiffness = 0f;
        d.damping = velocityDriveDamping;
        d.forceLimit = stallTorque;
        body.xDrive = d;
        holding = false;
    }

    void OnDisable()
    {
        SetInput(0f);
    }
}
