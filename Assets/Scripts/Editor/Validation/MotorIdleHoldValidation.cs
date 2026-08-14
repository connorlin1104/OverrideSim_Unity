using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Headless checks for what a motor does when you let go of the button.
//
// THE BUG THIS EXISTS FOR. A MotorActuator drives its joint with a torque-limited VELOCITY drive, and an
// idle velocity drive commands a target of ZERO SPEED — not a position. That fights how FAST something
// back-drives the joint and nothing else, so gravity turns a raised arm a fraction of a degree per step
// and there is no term that ever brings it back: "every time it's up, it continuously goes down". The
// rotating outtake on 654V_v3 sagged for exactly this reason, and the two lift builders had already been
// working around it for their own drivers (SetHoldPositionWhenIdle at startup).
//
// THE RULE, and it is the whole point of this file: a joint with TRAVEL LIMITS (an arm, a wrist, a
// rotating outtake) holds its angle when idle; a FREE-SPINNING one (a roller, a flywheel, an intake shaft)
// never does, because coasting is its entire job. That is the same limited-vs-free distinction the intake's
// anchor rescue turns on. coastWhenIdle is the opt-out for a limited joint that should flop.
//
// Pure decision logic plus the drive it writes — no simulation, so this stays fast.
//
// Usage: Tools > RoboSim > Validate > Validate Motor Idle Hold, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod MotorIdleHoldValidation.RunBatchValidate
public static class MotorIdleHoldValidation
{
    [MenuItem("Tools/RoboSim/Validate/Validate Motor Idle Hold", false, 14)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive("Validate Motor Idle Hold", Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Validate Motor Idle Hold", Run);

    private static string Run()
    {
        SimulationMode previousSimulation = Physics.simulationMode;
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            int checks = OnlyAJointWithTravelLimitsHolds();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            checks += TheHoldIsAPositionDriveAndItComesBack();

            return $"Validate Motor Idle Hold: PASSED ({checks} checks) — an arm holds its angle when idle, " +
                   "a roller still coasts, and the hold is a position drive that releases on input and " +
                   "re-engages when the button is let go.";
        }
        finally
        {
            Physics.simulationMode = previousSimulation;
        }
    }

    // --- The rule -----------------------------------------------------------------------------------

    private static int OnlyAJointWithTravelLimitsHolds()
    {
        ArticulationBody arm = Body("Arm", ArticulationJointType.RevoluteJoint, ArticulationDofLock.LimitedMotion);
        ArticulationBody roller = Body("Roller", ArticulationJointType.RevoluteJoint, ArticulationDofLock.FreeMotion);
        ArticulationBody locked = Body("Locked", ArticulationJointType.RevoluteJoint, ArticulationDofLock.LockedMotion);
        ArticulationBody slide = Body("LiftStage", ArticulationJointType.PrismaticJoint, ArticulationDofLock.LimitedMotion);
        ArticulationBody weld = Body("Bracket", ArticulationJointType.FixedJoint);

        ValidationUtil.Assert(MotorActuator.ShouldHoldWhenIdle(arm, false, false),
            "THE FIX: a revolute with travel limits is an arm/wrist/outtake and must hold its angle when " +
            "idle, with no inspector tick required — a velocity drive at zero only resists the SPEED of a " +
            "back-drive, so gravity walks it down and it never comes back up");
        ValidationUtil.Assert(!MotorActuator.ShouldHoldWhenIdle(roller, false, false),
            "a FREE-SPINNING link must never be held — it is a roller or a flywheel, and coasting is what " +
            "it is for");
        ValidationUtil.Assert(!MotorActuator.ShouldHoldWhenIdle(locked, false, false),
            "a locked DOF has no travel to hold");
        ValidationUtil.Assert(!MotorActuator.ShouldHoldWhenIdle(slide, false, false),
            "a PRISMATIC joint is excluded: the hold captures an angle in degrees, and a linear lift is " +
            "held by its own builder's stage drives instead");
        ValidationUtil.Assert(!MotorActuator.ShouldHoldWhenIdle(weld, false, false),
            "a fixed link cannot move, so there is nothing to hold");

        ValidationUtil.Assert(!MotorActuator.ShouldHoldWhenIdle(arm, false, true),
            "coastWhenIdle is the opt-out — a limited joint that SHOULD flop when you let go must be able to");
        ValidationUtil.Assert(MotorActuator.ShouldHoldWhenIdle(roller, true, false),
            "an explicit Hold Position When Idle must still be honoured on any joint, whatever the rule " +
            "would have decided (this is the flag both lift builders set by hand)");
        ValidationUtil.Assert(!MotorActuator.ShouldHoldWhenIdle(roller, true, true),
            "asked to hold AND to coast, coast wins — one rule, no contradictory pair to reason about");
        ValidationUtil.Assert(!MotorActuator.ShouldHoldWhenIdle(null, false, false),
            "a motor with no body resolved yet must decide 'no' instead of throwing");
        return 9;
    }

    // --- The drive it writes ------------------------------------------------------------------------

    private static int TheHoldIsAPositionDriveAndItComesBack()
    {
        ArticulationBody arm = Body("HeldArm", ArticulationJointType.RevoluteJoint, ArticulationDofLock.LimitedMotion);
        MotorActuator motor = arm.gameObject.AddComponent<MotorActuator>();
        motor.body = arm;                       // Awake never runs in edit mode, so wire it as Awake would

        // One step first: the hold angle is read from jointPosition, which only means anything once the
        // articulation has been built and stepped.
        Physics.simulationMode = SimulationMode.Script;
        Physics.Simulate(0.02f);

        // Configure() IS what Awake runs — calling the real thing rather than a copy of the decision.
        motor.Configure();
        ValidationUtil.Assert(motor.holdPositionWhenIdle,
            "an untouched MotorActuator on a limited revolute must come out of Awake HOLDING — that is " +
            "what reaches every existing robot, since a brand-new C# default lands on prefabs that never " +
            "serialized the field, and a saved value would not have");

        ArticulationDrive held = arm.xDrive;
        ValidationUtil.Assert(held.driveType == ArticulationDriveType.Target,
            "holding means a POSITION-target drive — a velocity drive cannot hold an angle at all");
        ValidationUtil.Near(held.stiffness, motor.holdStiffness, 1e-3f,
            "the hold spring has to be the configured stiffness, or the arm sags at a different rate " +
            "instead of not sagging");
        ValidationUtil.Near(held.forceLimit, motor.stallTorque, 1e-3f,
            "the hold is still capped by the motor's stall torque — a shove past stall must give, the " +
            "same as driving into a wall does");
        ValidationUtil.Near(held.damping, motor.velocityDriveDamping, 1e-3f,
            "damping stays, or the held joint rings");

        // Pressing the button has to release the hold, or the arm fights its own driver.
        motor.SetInput(1f);
        ArticulationDrive driving = arm.xDrive;
        ValidationUtil.Assert(driving.driveType == ArticulationDriveType.Velocity,
            "any input must drop back to the velocity drive — a position spring left engaged would pull " +
            "against the motor for the whole travel");
        ValidationUtil.Near(driving.stiffness, 0f, 1e-6f, "...with no position spring left over");
        ValidationUtil.Near(driving.targetVelocity, motor.maxRpm * 6f, 1e-2f,
            "full input is maxRpm in DEGREES per second (rpm x 6), the unit a revolute drive speaks");

        // And letting go has to hold again — the actual moment the arm used to start sagging.
        motor.SetInput(0f);
        ValidationUtil.Assert(arm.xDrive.driveType == ArticulationDriveType.Target,
            "letting go of the button is the moment the sag used to start: the drive has to become a " +
            "position hold again, not a velocity target of zero");

        // A roller, wired identically, must still coast — the check that stops this fix from freezing
        // every intake and flywheel on the robot.
        ArticulationBody spin = Body("HeldRoller", ArticulationJointType.RevoluteJoint, ArticulationDofLock.FreeMotion);
        MotorActuator rollerMotor = spin.gameObject.AddComponent<MotorActuator>();
        rollerMotor.body = spin;
        rollerMotor.Configure();
        ValidationUtil.Assert(!rollerMotor.holdPositionWhenIdle,
            "...and it must not have decided to hold, either");
        rollerMotor.SetInput(1f);
        rollerMotor.SetInput(0f);
        ValidationUtil.Assert(spin.xDrive.driveType == ArticulationDriveType.Velocity,
            "a free-spinning roller that has been run and released must be left on a VELOCITY drive: " +
            "pinning its angle would stop the intake dead the instant the button came up");
        return 11;
    }

    // --- Fixtures -----------------------------------------------------------------------------------

    private static ArticulationBody Body(string name, ArticulationJointType type,
        ArticulationDofLock twist = ArticulationDofLock.LockedMotion)
    {
        // A child of a root body, so the joint settings mean something (a root ArticulationBody has no
        // joint of its own).
        GameObject root = new GameObject(name + "Root");
        root.AddComponent<ArticulationBody>().immovable = true;
        GameObject go = new GameObject(name);
        go.transform.SetParent(root.transform, false);

        ArticulationBody body = go.AddComponent<ArticulationBody>();
        body.jointType = type;
        if (type == ArticulationJointType.RevoluteJoint) body.twistLock = twist;
        else if (type == ArticulationJointType.PrismaticJoint) body.linearLockX = twist;
        return body;
    }
}
