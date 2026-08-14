using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Headless checks for WHICH parent an intake's anchors are allowed to keep.
//
// IntakePull self-heals at play start: an anchor (the mouth, the hold point, a stack slot) that hangs
// off a spinning link is re-parented to the rigid chassis, because a whirling target drags held pieces
// to random points across the field. That rescue is load-bearing — and until 2026-07-29 it was wrong in
// two ways at once, which showed up as one baffling symptom: mount a whole intake on a pivoting arm,
// and IntakeSlot1 swung with the arm while the hold point (slot 0) was yanked back to the chassis, so
// the stack tore itself in half.
//
//   TOO BROAD. The test tripped on ANY ArticulationBody above the anchor. A limited arm/wrist/lift
//   link cannot whirl — it only goes where the driver drives it — so an intake bolted to one is
//   SUPPOSED to carry its anchors through the arc. Only an unbounded spin (the roller itself) breaks
//   pieces, and only that is rescued now.
//
//   ASYMMETRIC. The sweep covered the mouth and the hold point but not slotAnchors, so the two halves
//   of the same stack ended up on different parents. Every anchor goes through the same rule now.
//
// Pure hierarchy arithmetic driving the REAL IntakePull.StabilizeAnchors — no physics, no robot, no
// simulation, so it stays fast and green while covering the exact thing that was broken.
//
// Usage: Tools > RoboSim > Validate > Validate Intake Anchors, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod IntakeAnchorValidation.RunBatchValidate
public static class IntakeAnchorValidation
{
    [MenuItem("Tools/RoboSim/Validate/Validate Intake Anchors", false, 23)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive("Validate Intake Anchors", Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Validate Intake Anchors", Run);

    private static string Run()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        int checks = OnlyAFreeSpinIsRescued();
        checks += TheLiftCarriageBypassIsWhatSavesTheCarriage();
        checks += EveryAnchorGoesThroughTheSameSweep();
        checks += AnchorsOnAnArmKeepRidingTheArm();
        return $"Validate Intake Anchors: PASSED ({checks} checks).";
    }

    // --- The rule -----------------------------------------------------------------------------------

    private static int OnlyAFreeSpinIsRescued()
    {
        Transform chassis = Body(null, "Robot", ArticulationJointType.FixedJoint).transform;

        // THE CASE THAT WAS BROKEN. An intake on a pivoting arm: the arm is a limited revolute, and its
        // anchors have to ride it or the intake cannot work at all.
        Transform arm = Body(chassis, "OuttakeArm", ArticulationJointType.RevoluteJoint, ArticulationDofLock.LimitedMotion).transform;
        ValidationUtil.Assert(!IntakePull.NeedsReanchor(Child(arm, "IntakeHoldPoint"), chassis, out _),
            "an anchor on a LIMITED arm must be left alone — a limited joint can't whirl, and riding " +
            "the arm is the only way an intake mounted on one can work");
        ValidationUtil.Assert(!IntakePull.NeedsReanchor(Child(Child(arm, "CAD Folder"), "IntakeSlot1"), chassis, out _),
            "the same holds however many plain folders sit between the anchor and the arm");

        // THE CASE THAT MUST STAY BROKEN-PROOF. A roller is a free-spinning revolute — this is the
        // whirl the whole rescue exists for.
        Transform roller = Body(chassis, "IntakeRoller", ArticulationJointType.RevoluteJoint, ArticulationDofLock.FreeMotion).transform;
        ValidationUtil.Assert(IntakePull.NeedsReanchor(Child(roller, "IntakeHoldPoint"), chassis, out string why),
            "an anchor inside a FREE-SPINNING roller must be rescued — that is the bug the rescue exists for");
        ValidationUtil.Assert(!string.IsNullOrEmpty(why) && why.Contains("IntakeRoller"),
            $"the warning has to name the link that is spinning so it can be found in the hierarchy (got '{why}')");
        ValidationUtil.Assert(IntakePull.NeedsReanchor(Child(Child(roller, "Folder"), "IntakeSlot2"), chassis, out _),
            "the walk must reach the spinning link through folders, not just check the anchor's parent");

        // A spin ANYWHERE in the chain disqualifies it, even under an otherwise-legitimate arm.
        Transform armOnRoller = Body(roller, "ArmOnRoller", ArticulationJointType.RevoluteJoint, ArticulationDofLock.LimitedMotion).transform;
        ValidationUtil.Assert(IntakePull.NeedsReanchor(Child(armOnRoller, "IntakeMouth"), chassis, out _),
            "a limited link that itself hangs off a spinning one still spins — the walk must not stop " +
            "at the first joint it likes");

        // The other bounded joints. A cascade stage slides to a commanded position and a fixed link is
        // welded, so both carry their anchors exactly as the chassis would.
        //
        // Both prismatic shapes on purpose. A fresh ArticulationBody defaults EVERY lock to FreeMotion,
        // so a stage configured by hand carries Limited on its travel axis and the default Free on the
        // two axes the joint does not use, while the builders write Locked on both. Only the first
        // unlocked axis is the joint's degree of freedom; reading the other two turned a lift stage into
        // a "free-spinning link" and tore the intake off it.
        Transform stage = Body(chassis, "LiftStage", ArticulationJointType.PrismaticJoint, ArticulationDofLock.LimitedMotion).transform;
        ValidationUtil.Assert(!IntakePull.NeedsReanchor(Child(stage, "IntakeSlot1"), chassis, out _),
            "an anchor on a limited PRISMATIC stage must ride it — that's an intake on a cascade lift");
        ArticulationBody built = Body(chassis, "BuiltLiftStage", ArticulationJointType.PrismaticJoint, ArticulationDofLock.LimitedMotion);
        built.linearLockY = ArticulationDofLock.LockedMotion;
        built.linearLockZ = ArticulationDofLock.LockedMotion;
        ValidationUtil.Assert(!IntakePull.NeedsReanchor(Child(built.transform, "IntakeSlot1"), chassis, out _),
            "...and the same stage as the joint tool writes it, with the two unused axes locked");
        Transform weld = Body(chassis, "WeldedBracket", ArticulationJointType.FixedJoint).transform;
        ValidationUtil.Assert(!IntakePull.NeedsReanchor(Child(weld, "IntakeMouth"), chassis, out _),
            "a FIXED link never moves, so riding it is identical to riding the chassis");

        // A free-sliding prismatic has no end either, so it gets the same treatment as a roller.
        Transform freeSlide = Body(chassis, "FreeSlide", ArticulationJointType.PrismaticJoint, ArticulationDofLock.FreeMotion).transform;
        ValidationUtil.Assert(IntakePull.NeedsReanchor(Child(freeSlide, "IntakeMouth"), chassis, out _),
            "an unbounded PRISMATIC can run away as surely as a roller can whirl");

        // Degenerate inputs: nothing to rescue, nothing to crash on.
        ValidationUtil.Assert(!IntakePull.NeedsReanchor(Child(chassis, "IntakeMouth"), chassis, out _),
            "an anchor already directly under the chassis needs nothing done to it");
        ValidationUtil.Assert(!IntakePull.NeedsReanchor(chassis, chassis, out _),
            "the chassis is its own stable frame");
        ValidationUtil.Assert(!IntakePull.NeedsReanchor(null, chassis, out _), "a null anchor is not a failure");
        ValidationUtil.Assert(!IntakePull.NeedsReanchor(Child(chassis, "X"), null, out _),
            "with no chassis resolved there is nowhere to rescue anything to");
        ValidationUtil.Assert(IntakePull.NeedsReanchor(new GameObject("Loose").transform, chassis, out _),
            "an anchor that isn't on the robot at all must be pulled back onto the chassis");
        return 15;
    }

    // The DR4B builder deliberately parents the stack onto its carriage so a held stack rides the lift.
    // The carriage here is FREE-SPINNING on purpose: that makes the bypass the only thing that can
    // return false, so this can't pass by accident through the limited-joint rule above.
    private static int TheLiftCarriageBypassIsWhatSavesTheCarriage()
    {
        Transform chassis = Body(null, "LiftRobot", ArticulationJointType.FixedJoint).transform;
        ArticulationBody carriage = Body(chassis, "Carriage", ArticulationJointType.RevoluteJoint, ArticulationDofLock.FreeMotion);
        LiftCarriage marker = carriage.gameObject.AddComponent<LiftCarriage>();
        Transform anchor = Child(Child(carriage.transform, "TrayFolder"), "IntakeHoldPoint");

        ValidationUtil.Assert(!IntakePull.NeedsReanchor(anchor, chassis, out _),
            "a LiftCarriage-marked link keeps its anchors — that is how a held stack rides the lift");
        Object.DestroyImmediate(marker);
        ValidationUtil.Assert(IntakePull.NeedsReanchor(anchor, chassis, out _),
            "removing the LiftCarriage must flip the answer, or the check above proves nothing about " +
            "the bypass (this fixture spins freely so only the bypass can spare it)");
        return 2;
    }

    // --- The sweep ----------------------------------------------------------------------------------

    // Slots were the half of the stack the sweep never touched. Every anchor has to be treated the
    // same, or one stack ends up on two different parents.
    private static int EveryAnchorGoesThroughTheSameSweep()
    {
        Transform chassis = Body(null, "SweepRobot", ArticulationJointType.FixedJoint).transform;
        Transform roller = Body(chassis, "Roller", ArticulationJointType.RevoluteJoint, ArticulationDofLock.FreeMotion).transform;

        // Everything left inside the roller — a stale prefab, or a hand-parented setup.
        IntakePull pull = MakeIntake(roller, roller, roller);
        Transform hold = pull.holdPoint;
        Transform slot1 = pull.slotAnchors[1];
        Transform slot2 = pull.slotAnchors[2];
        Vector3 mouthAt = pull.transform.position, holdAt = hold.position;
        Vector3 slot1At = slot1.position, slot2At = slot2.position;

        // The tautology guard, taken BEFORE the sweep: prove all four anchors really start inside the
        // spinning roller, so the parent checks below can't be reading a hierarchy that was already flat.
        ValidationUtil.Assert(pull.transform.parent == roller && hold.parent == roller &&
                              slot1.parent == roller && slot2.parent == roller,
            "this fixture must start with every anchor genuinely inside the spinning roller");

        pull.StabilizeAnchors();

        ValidationUtil.Assert(pull.transform.parent == chassis, "the mouth must be rescued off the roller");
        ValidationUtil.Assert(hold.parent == chassis, "the hold point must be rescued off the roller");
        ValidationUtil.Assert(slot1.parent == chassis,
            "a STACK SLOT must be rescued too — leaving slots behind is what split the stack across " +
            "two parents while the hold point moved");
        ValidationUtil.Assert(slot2.parent == chassis, "every slot, not just the first one");

        // World pose is the whole contract of the rescue: the anchor stops spinning, it does not move.
        ValidationUtil.Near((pull.transform.position - mouthAt).magnitude, 0f, 1e-4f,
            "rescuing the mouth must not move it");
        ValidationUtil.Near((hold.position - holdAt).magnitude, 0f, 1e-4f,
            "rescuing the hold point must not move it");
        ValidationUtil.Near((slot1.position - slot1At).magnitude, 0f, 1e-4f,
            "rescuing a slot must not move it — the stack layout is hand-placed");
        ValidationUtil.Near((slot2.position - slot2At).magnitude, 0f, 1e-4f, "same for the last slot");
        return 9;
    }

    // The user-facing payoff, stated as a check: an intake bolted to an arm keeps every anchor on the
    // arm, so the mouth, the hold point and the whole stack swing through the arc together.
    private static int AnchorsOnAnArmKeepRidingTheArm()
    {
        Transform chassis = Body(null, "ArmRobot", ArticulationJointType.FixedJoint).transform;
        Transform arm = Body(chassis, "OuttakeArm", ArticulationJointType.RevoluteJoint, ArticulationDofLock.LimitedMotion).transform;
        // The roller lives on the arm; the anchors sit beside it, which is where Build Intake puts them.
        Body(arm, "Roller", ArticulationJointType.RevoluteJoint, ArticulationDofLock.FreeMotion);

        IntakePull pull = MakeIntake(arm, arm, arm);
        pull.StabilizeAnchors();

        ValidationUtil.Assert(pull.transform.parent == arm && pull.holdPoint.parent == arm,
            "an intake mounted on a limited arm must keep its mouth and hold point on the arm");
        ValidationUtil.Assert(pull.slotAnchors[1].parent == arm && pull.slotAnchors[2].parent == arm,
            "...and its slots, or the stack rides two frames at once");

        // And they must actually be carried: swing the arm and the anchors go with it.
        Vector3 before = pull.holdPoint.position;
        Vector3 slotBefore = pull.slotAnchors[2].position;
        arm.localRotation = Quaternion.AngleAxis(30f, Vector3.right);
        ValidationUtil.Assert((pull.holdPoint.position - before).magnitude > 0.1f,
            "the hold point has to move when the arm swings — that is what 'rides the arm' means");
        ValidationUtil.Assert((pull.slotAnchors[2].position - slotBefore).magnitude > 0.1f,
            "and so does the top of the stack");
        return 4;
    }

    // --- Fixtures -----------------------------------------------------------------------------------

    // A mouth carrying IntakePull, a hold point and two slots, each on the parent asked for and at a
    // distinct offset so a move shows up.
    private static IntakePull MakeIntake(Transform mouthParent, Transform holdParent, Transform slotParent)
    {
        Transform mouth = Child(mouthParent, "IntakeMouth");
        mouth.position = new Vector3(0f, 2f, 3f);
        IntakePull pull = mouth.gameObject.AddComponent<IntakePull>();
        pull.logEvents = false;

        Transform hold = Child(holdParent, "IntakeHoldPoint");
        hold.position = new Vector3(0f, 4f, 3f);
        pull.holdPoint = hold;

        Transform slot1 = Child(slotParent, "IntakeSlot1");
        slot1.position = new Vector3(0f, 5.5f, 3f);
        Transform slot2 = Child(slotParent, "IntakeSlot2");
        slot2.position = new Vector3(0f, 7f, 3f);
        pull.slotAnchors = new[] { hold, slot1, slot2 };   // slot 0 IS the hold point
        pull.maxHeld = 3;
        return pull;
    }

    private static ArticulationBody Body(Transform parent, string name, ArticulationJointType type,
        ArticulationDofLock twist = ArticulationDofLock.LockedMotion)
    {
        Transform t = parent == null ? new GameObject(name).transform : Child(parent, name);
        // Parent first, then the body, so the child links resolve as non-root against the chassis above.
        ArticulationBody body = t.gameObject.AddComponent<ArticulationBody>();
        body.jointType = type;
        if (type == ArticulationJointType.RevoluteJoint) body.twistLock = twist;
        else if (type == ArticulationJointType.PrismaticJoint) body.linearLockX = twist;
        return body;
    }

    private static Transform Child(Transform parent, string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }
}
