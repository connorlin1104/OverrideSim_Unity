using System;
using System.Collections.Generic;
using UnityEngine;

// Controller for a CASCADE (telescoping) lift: a stack of C-channel bars where each one slides out of
// the one below it. Unlike the DR4B — a closed kinematic loop that has to be faked with transform
// followers (see Dr4bLift) — a cascade is a pure SERIAL chain, which is exactly what an
// ArticulationBody tree expresses. So every bar here is a REAL prismatic link nested inside the one
// below it, with live colliders, and whatever rides the top (a claw arm and its claw) rides for free
// as a child in the physics tree.
//
// ONE hidden revolute joint (the driver, a MotorActuator mechanism on the buttons) supplies a 0->1
// Progress; this component turns that into a position target for every stage. The stages are NOT
// actuators and NOT registered mechanisms — if they were, ButtonRouter would fight this controller
// for their drives, the same rule a JointCoupler-driven follower obeys.
//
// Two motions, because teams rig cascades both ways:
//   All at once   — every bar extends together, each over its own travel.
//   One at a time — the bars run out in order (bottom-first or top-first), never at random.
//
// Wired by Tools > RoboSim > Robot > Mechanisms > Build Cascade Lift (roles).
[DisallowMultipleComponent]
public class CascadeLift : MonoBehaviour
{
    // One sliding bar. `travel` is how far it can come out of the bar below it, in world units
    // (1 unit = 0.1 m) — measured from the C-channel at build time.
    [Serializable]
    public class Stage
    {
        public ArticulationBody body;
        public float travel;
        [Tooltip("For the Inspector only — which bar this is.")]
        public string label;
    }

    [Tooltip("The hidden revolute joint the buttons drive. Its angle, over the sweep, is the lift's 0->1 progress.")]
    public ArticulationBody driver;
    [Tooltip("The rigid robot base. Auto-resolved to the topmost ArticulationBody ancestor.")]
    public Transform chassis;

    [Header("Lift speed")]
    [Tooltip("Seconds to raise the lift fully while holding the UP button. Lower = faster. EDITABLE LIVE — " +
             "changing it here (even at Play) re-derives the hidden motor's speed and takes effect " +
             "immediately, no rebuild.")]
    public float raiseSeconds = 2f;

    // Internal: the driver's travel, used only to normalize progress. The user tunes per-stage travel
    // and the raise time instead, so this never needs to be touched.
    [HideInInspector] public float sweepDeg = 60f;

    [Header("Motion")]
    [Tooltip("OFF = every bar extends together (each over its own travel). ON = the bars run out one " +
             "at a time, in order.")]
    public bool oneAtATime;
    [Tooltip("One-at-a-time only: ON = the TOP bar goes first, OFF = the bottom bar goes first.")]
    public bool topFirst;

    [Header("Stages (bottom to top, wired by the cascade builder)")]
    public List<Stage> stages = new List<Stage>();

    [Header("Stage drive")]
    [Tooltip("Position spring gain holding each bar at its commanded height.")]
    public float stageStiffness = 20000f;
    [Tooltip("Velocity damping on each bar — enough to stop it ringing at the end of its travel.")]
    public float stageDamping = 500f;
    [Tooltip("Force limit per bar — the lift's stall force, so a lift jammed under the field stalls " +
             "like real hardware instead of shoving the whole robot. Gravity here is 10x, so this is " +
             "much higher than a small mechanism's: too low and the lift sags under the claw.")]
    public float stageForceLimit = 5000f;

    // 0 at rest -> 1 fully extended. Read by the followers/UI; also handy in the Inspector while tuning.
    public float Progress { get; private set; }

    private float restRad;
    private MotorActuator driverMotor;
    private float lastAppliedRaiseSeconds = float.NaN;   // so the first step always syncs the speed

    void Awake()
    {
        if (chassis == null) chassis = ResolveChassis();
        restRad = DriverRad();
        BakeDrives();
        ApplyLiftSpeed();
    }

    // Bake every stage's xDrive as a stiff position drive. Public so the authoring tool can bake the
    // SAME drive at edit time — edit-mode Physics.Simulate never runs Awake, and the validation
    // harness would otherwise be testing a different lift from the one that ships (the reason
    // JointCoupler.BakeDrive and PneumaticActuator's bake are public too).
    public void BakeDrives()
    {
        float seconds = Mathf.Max(0.05f, raiseSeconds);
        foreach (Stage s in stages)
        {
            if (s == null || s.body == null) continue;
            ArticulationDrive d = s.body.xDrive;   // struct: copy, modify, assign back
            d.driveType = ArticulationDriveType.Target;
            d.stiffness = stageStiffness;
            d.damping = stageDamping;
            d.forceLimit = stageForceLimit;
            s.body.xDrive = d;
            // maxJointVelocity is units/s for a prismatic joint. The default caps a stage well below
            // the speed a short raise time asks for, which silently makes every lift take the same
            // (slow) time however the raise seconds are set. One-at-a-time crams each stage's travel
            // into a fraction of the raise, so size for the worst case: the whole travel in one slot.
            s.body.maxJointVelocity = Mathf.Max(1f, Mathf.Abs(s.travel) / seconds * 3f);
        }
    }

    // Re-derive the hidden motor's speed from raiseSeconds + the sweep, exactly as Dr4bLift does: the
    // driver runs at maxRpm*6 deg/s, so a full sweep takes sweepDeg/(maxRpm*6) s. Also pins the
    // driver's angle whenever the button is released, so contact can't back-drive the lift up or down.
    private void ApplyLiftSpeed()
    {
        if (driver == null) return;
        if (driverMotor == null) driverMotor = driver.GetComponent<MotorActuator>();
        if (driverMotor == null) return;
        float seconds = Mathf.Max(0.05f, raiseSeconds);
        driverMotor.SetMaxRpm(Mathf.Abs(sweepDeg) / (6f * seconds));
        driverMotor.SetHoldPositionWhenIdle(true);
        lastAppliedRaiseSeconds = raiseSeconds;
    }

    void FixedUpdate() => ApplyStep();

    // One step: read the driver's progress and command every stage's height. Public so a headless
    // edit-mode harness (Physics.Simulate, which never calls FixedUpdate) can step the lift the same
    // way it steps a JointCoupler.
    public void ApplyStep()
    {
        if (driver == null) return;
        if (raiseSeconds != lastAppliedRaiseSeconds) { BakeDrives(); ApplyLiftSpeed(); }

        float sweepRad = Mathf.Max(1e-4f, Mathf.Abs(sweepDeg) * Mathf.Deg2Rad);
        Progress = Mathf.Clamp01((DriverRad() - restRad) / sweepRad);

        int n = stages.Count;
        for (int i = 0; i < n; i++)
        {
            Stage s = stages[i];
            if (s == null || s.body == null) continue;
            s.body.SetDriveTarget(ArticulationDriveAxis.X, StageFraction(i, n) * s.travel);
        }
    }

    // How far through ITS OWN travel stage i should be at the current progress.
    //  - all at once:   every stage tracks the whole press.
    //  - one at a time: the press is split into n equal slots; the stage whose turn it is runs 0->1
    //    across its slot while the ones before it stay out and the ones after stay in. Which slot a
    //    stage gets is its position in the order, so the sequence is always ascending or descending —
    //    never random.
    public float StageFraction(int index, int count)
    {
        if (!oneAtATime || count <= 1) return Progress;
        int slot = topFirst ? count - 1 - index : index;
        return Mathf.Clamp01(Progress * count - slot);
    }

    private float DriverRad()
    {
        if (driver == null) return 0f;
        ArticulationReducedSpace p = driver.jointPosition;
        // A joint's position reads NaN until it has been stepped once; treat that as "at rest" rather
        // than poisoning every stage target with NaN on the first frame.
        float rad = p.dofCount > 0 ? p[0] : 0f;
        return float.IsNaN(rad) ? restRad : rad;
    }

    // The robot's rigid base — same rule as Dr4bLift/IntakePull (topmost AB, else the RobotMechanisms
    // holder, else the hierarchy root).
    private Transform ResolveChassis()
    {
        ArticulationBody top = null;
        foreach (ArticulationBody ab in GetComponentsInParent<ArticulationBody>(true)) top = ab;
        if (top != null) return top.transform;
        RobotMechanisms rm = GetComponentInParent<RobotMechanisms>();
        if (rm != null) return rm.transform;
        return transform.root;
    }
}
