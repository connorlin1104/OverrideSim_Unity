using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Headless checks of the Build Intake core (IntakeSetup.Build / Remove) against a synthetic robot in a
// scratch scene — no real robot prefab is touched.
//
// WHAT THIS EXISTS TO CATCH: a robot with TWO intakes. A bot that grabs pieces at the floor and SCORES
// from an arm or a chain wants two IntakePulls — one that pulls in, one that carries and drops. The tool
// used to be keyed on the ROBOT and its markers found by NAME from the robot root, so the second build
// (a) was handed the FIRST intake as "the existing one" and merely re-pointed it at the new motor, and
// (b) if it had created markers, a later Remove would have swept "IntakeSlot1" by name and deleted the
// other intake's stack. Both are silent — you find out in Play when half a robot stops working. Name
// collisions are a known hazard on these imports (654V_v2 has 1260 objects called Body1), so markers are
// resolved through the component's own references now, and each intake owns a numbered name set.
//
// The four cases run in sequence on ONE robot, because that IS the scenario: build intake 1, rebuild it,
// build intake 2 beside it, then remove them one at a time.
//
// Usage: Tools > RoboSim > Validation > Validate Build Intake, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod IntakeBuilderValidation.RunBatchValidate
public static class IntakeBuilderValidation
{
    [MenuItem("Tools/RoboSim/Validation/Validate Build Intake", false, 15)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive("Validate Build Intake", Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Validate Build Intake", Run);

    private static string Run()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        Transform robot = MakeRobot();
        Transform intakeMount = Folder(robot, "IntakeAssembly");
        MotorActuator roller = MakeMechanism(intakeMount, "Roller", new Vector3(0f, 2f, 6f));
        Transform scoreMount = Folder(robot, "ScoringAssembly");
        MotorActuator arm = MakeMechanism(scoreMount, "ScoringArm", new Vector3(0f, 8f, -4f));

        int checks = BuildsTheSetBesideTheMechanism(robot, intakeMount, roller);
        checks += RebuildKeepsWhatYouPositioned(robot, roller);
        checks += TwoIntakesDoNotFightOverMarkers(robot, scoreMount, roller, arm);
        checks += RemovingOneLeavesTheOtherAlone(robot, roller, arm);

        return $"Validate Build Intake: PASSED ({checks} checks) — one intake built beside its roller, " +
               "rebuilt in place, a second intake added on the scoring arm with its own numbered " +
               "markers, and each removed without touching the other.";
    }

    // --- 1. A first build ---------------------------------------------------------------------------

    private static int BuildsTheSetBesideTheMechanism(Transform robot, Transform mount, MotorActuator roller)
    {
        IntakePull pull = IntakeSetup.Build(roller, 3, 1.5f, reverseDropsInPlace: false,
            takeFromOtherIntakes: false, useUndo: false);

        ValidationUtil.Assert(pull != null, "Build should have returned the intake it created");
        ValidationUtil.Assert(pull.intakeMotor == roller, "the intake must be wired to the motor it was built on");
        ValidationUtil.Assert(pull.name == "IntakeMouth",
            $"a robot's FIRST intake keeps the historical marker names, or every existing robot gets " +
            $"renamed on the next rebuild (got '{pull.name}')");
        ValidationUtil.Assert(pull.transform.parent == mount,
            "the mouth belongs beside the roller (one step up), never inside it — a marker on the " +
            "spinning link whirls at Play and drags pieces across the field");
        ValidationUtil.Assert(pull.transform.parent != roller.transform, "...specifically not the roller itself");

        BoxCollider box = pull.GetComponent<BoxCollider>();
        ValidationUtil.Assert(box != null && box.isTrigger,
            "the mouth needs a TRIGGER box — that is the grab zone, and a solid one would shove pieces away");

        ValidationUtil.Assert(pull.holdPoint != null && pull.holdPoint.name == "IntakeHoldPoint",
            "the build must create the hold point");
        ValidationUtil.Assert(pull.holdPoint.parent == mount, "the hold point goes on the same mount as the mouth");
        ValidationUtil.Assert(pull.slotAnchors != null && pull.slotAnchors.Length == 3,
            "Max Held 3 means three stack slots");
        ValidationUtil.Assert(pull.slotAnchors[0] == pull.holdPoint,
            "slot 0 IS the hold point — every seating calculation reads slotAnchors[0]");
        ValidationUtil.Assert(pull.slotAnchors[1].name == "IntakeSlot1" && pull.slotAnchors[2].name == "IntakeSlot2",
            "slots 1..n are the numbered markers");
        ValidationUtil.Assert(pull.maxHeld == 3, "Max Held must reach the component");
        ValidationUtil.Near(pull.slotSpacing, 1.5f, 1e-4f, "Slot Spacing must reach the component");
        ValidationUtil.Assert(!pull.reverseDropsInPlace,
            "an unticked checkbox must leave reverse LAUNCHING pieces, the old behaviour");
        ValidationUtil.Assert(!pull.takeFromOtherIntakes,
            "an unticked Take From Other Intakes must reach the component too — it defaults to ON in " +
            "C#, so a write that silently doesn't happen would look correct in every other test");
        ValidationUtil.Assert(CountNamed(robot, "IntakeMouth") == 1 && CountNamed(robot, "IntakeSlot1") == 1,
            "one build, one set of markers");
        return 16;
    }

    // --- 2. Rebuild is an edit, not a second build --------------------------------------------------

    private static int RebuildKeepsWhatYouPositioned(Transform robot, MotorActuator roller)
    {
        IntakePull before = IntakeSetup.FindExisting(roller);
        ValidationUtil.Assert(before != null, "the fixture must already have an intake on the roller");

        // Position and angle a marker by hand, as the Scene handles do, then shrink the stack.
        Transform hold = before.holdPoint;
        hold.SetPositionAndRotation(new Vector3(1f, 5f, 7f), Quaternion.Euler(20f, 0f, 35f));
        Transform droppedSlot = before.slotAnchors[2];
        Vector3 holdAt = hold.position;
        Quaternion holdRot = hold.rotation;

        IntakePull after = IntakeSetup.Build(roller, 2, 1.5f, reverseDropsInPlace: true,
            takeFromOtherIntakes: true, useUndo: false);

        ValidationUtil.Assert(after == before,
            "a rebuild must UPDATE the intake in place, not add a second IntakePull to the robot");
        ValidationUtil.Assert(after.holdPoint == hold, "the hold point must be the same object, not a fresh one");
        ValidationUtil.Near((hold.position - holdAt).magnitude, 0f, 1e-4f,
            "a rebuild must not move a marker you positioned");
        ValidationUtil.Near(Quaternion.Angle(hold.rotation, holdRot), 0f, 1e-3f,
            "...nor re-level one you angled: the anchor's rotation is how the piece sits");
        ValidationUtil.Assert(after.slotAnchors.Length == 2, "Max Held 2 leaves two slots");
        ValidationUtil.Assert(droppedSlot == null,
            "the surplus slot marker must be deleted, not left orphaned on the robot");
        ValidationUtil.Assert(after.reverseDropsInPlace,
            "the Reverse Drops In Place checkbox has to reach the component — that is the whole feature");
        ValidationUtil.Assert(after.takeFromOtherIntakes,
            "...and a rebuild must be able to turn the handoff back ON, not just off");
        ValidationUtil.Assert(CountNamed(robot, "IntakeMouth") == 1,
            "a rebuild that duplicates the mouth leaves two intakes fighting over the same pieces");
        ValidationUtil.Assert(CountNamed(robot, "IntakeHoldPoint") == 1, "...same for the hold point");
        return 11;
    }

    // --- 3. Two intakes, one robot -----------------------------------------------------------------

    private static int TwoIntakesDoNotFightOverMarkers(Transform robot, Transform scoreMount,
        MotorActuator roller, MotorActuator arm)
    {
        IntakePull first = IntakeSetup.FindExisting(roller);
        // Tautology guards: two DIFFERENT motors, and the first intake really does own the bare-named
        // markers the second build would otherwise grab.
        ValidationUtil.Assert(first != null && roller != arm && first.intakeMotor == roller,
            "this fixture needs one built intake and a second, different motor to build on");
        ValidationUtil.Assert(first.name == "IntakeMouth" && first.slotAnchors[1].name == "IntakeSlot1",
            "the first intake must hold the bare marker names, or nothing below is being tested");

        ValidationUtil.Assert(IntakeSetup.FindExisting(arm) == null,
            "a motor with no intake of its own must report NOTHING. Handing back the robot's other " +
            "intake is what made Build silently re-point intake 1 at this motor instead of creating one");

        Transform firstHold = first.holdPoint, firstSlot = first.slotAnchors[1];
        Vector3 firstHoldAt = firstHold.position, firstSlotAt = firstSlot.position;

        IntakePull second = IntakeSetup.Build(arm, 2, 2f, reverseDropsInPlace: true,
            takeFromOtherIntakes: true, useUndo: false);

        ValidationUtil.Assert(second != null && second != first,
            "building on a second motor must create a SECOND intake");
        ValidationUtil.Assert(second.intakeMotor == arm && first.intakeMotor == roller,
            "each intake rides its own motor — that is what gives them separate buttons");
        ValidationUtil.Assert(second.name == "Intake2Mouth",
            $"the second set must be numbered so the two can never collide by name (got '{second.name}')");
        ValidationUtil.Assert(second.holdPoint.name == "Intake2HoldPoint" &&
                              second.slotAnchors[1].name == "Intake2Slot1",
            "the whole set is numbered, not just the mouth");
        ValidationUtil.Assert(second.holdPoint != firstHold && second.slotAnchors[1] != firstSlot,
            "the second intake must not ADOPT the first one's markers — they would then move together");
        ValidationUtil.Assert(second.transform.parent == scoreMount,
            "the second set mounts beside its own mechanism, not beside the first intake's");
        ValidationUtil.Assert(firstHold != null && firstSlot != null,
            "building the second intake must not destroy the first one's markers");
        ValidationUtil.Near((firstHold.position - firstHoldAt).magnitude, 0f, 1e-4f,
            "...nor move them");
        ValidationUtil.Near((firstSlot.position - firstSlotAt).magnitude, 0f, 1e-4f, "...any of them");
        ValidationUtil.Assert(first.slotAnchors[0] == firstHold && first.slotAnchors[1] == firstSlot,
            "...nor rewrite its slot list");
        ValidationUtil.Assert(IntakeSetup.FindExisting(roller) == first && IntakeSetup.FindExisting(arm) == second,
            "with two intakes on the robot, each motor must still resolve to its OWN");
        ValidationUtil.Assert(IntakeSetup.AllOnRobot(arm).Length == 2,
            "the window counts the robot's intakes to decide what the Build button says");
        ValidationUtil.Assert(CountNamed(robot, "IntakeMouth") == 1 && CountNamed(robot, "IntakeSlot1") == 1,
            "the second build must not create a SECOND object with a bare name either — two markers " +
            "sharing a name is how a name-keyed rebuild picks the wrong one later");
        return 16;
    }

    // --- 4. Removing one --------------------------------------------------------------------------

    private static int RemovingOneLeavesTheOtherAlone(Transform robot, MotorActuator roller, MotorActuator arm)
    {
        IntakePull first = IntakeSetup.FindExisting(roller);
        IntakePull second = IntakeSetup.FindExisting(arm);
        ValidationUtil.Assert(first != null && second != null, "this fixture needs both intakes present");

        Transform firstHold = first.holdPoint, firstSlot = first.slotAnchors[1];
        Transform secondHold = second.holdPoint, secondSlot = second.slotAnchors[1];
        // The guard that makes the next assert mean something: the two stacks use the same slot NUMBER,
        // and it is that number the old sweep matched on.
        ValidationUtil.Assert(firstSlot.name == "IntakeSlot1" && secondSlot.name == "Intake2Slot1",
            "both intakes must have a slot 1 for the name-sweep hazard to be under test");

        IntakeSetup.Remove(second, useUndo: false);

        ValidationUtil.Assert(secondHold == null && secondSlot == null,
            "Remove must take the intake's own hold point and slots with it");
        ValidationUtil.Assert(first != null && firstHold != null && firstSlot != null,
            "removing intake 2 must leave intake 1 whole — a name sweep for 'IntakeSlot1' from the " +
            "robot root deleted the OTHER intake's stack, and nothing said so");
        ValidationUtil.Assert(first.slotAnchors[1] == firstSlot, "...with its slot list intact");
        ValidationUtil.Assert(CountNamed(robot, "Intake2Mouth") == 0, "the second mouth is gone");

        IntakeSetup.Remove(first, useUndo: false);
        ValidationUtil.Assert(CountNamed(robot, "IntakeMouth") == 0 &&
                              CountNamed(robot, "IntakeHoldPoint") == 0 &&
                              CountNamed(robot, "IntakeSlot1") == 0,
            "removing the last intake must leave no markers behind");
        ValidationUtil.Assert(robot.GetComponentsInChildren<IntakePull>(true).Length == 0,
            "and no IntakePull");
        ValidationUtil.Assert(roller != null && arm != null,
            "Remove must never touch the mechanisms themselves — only what Build added");
        return 9;
    }

    // --- Fixtures ---------------------------------------------------------------------------------

    // A robot the builder recognizes: a RobotMechanisms registry (which is what ResolveChassis keys on)
    // and an immovable root body, as every real robot has.
    private static Transform MakeRobot()
    {
        GameObject root = new GameObject("IntakeTestBot");
        root.AddComponent<RobotMechanisms>();
        ArticulationBody chassis = root.AddComponent<ArticulationBody>();
        chassis.immovable = true;
        ValidationUtil.MakeBox(root.transform, "ChassisMesh", Vector3.zero, Quaternion.identity,
            new Vector3(6f, 1f, 6f));
        return root.transform;
    }

    private static Transform Folder(Transform parent, string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    // A driven link with a mesh, exactly what Build reads: the motor to key on, and renderer bounds to
    // size the mouth box and seed the hold point from.
    private static MotorActuator MakeMechanism(Transform mount, string name, Vector3 at)
    {
        GameObject link = new GameObject(name);
        link.transform.SetParent(mount, false);
        link.transform.position = at;
        ValidationUtil.MakeBox(link.transform, name + "Mesh", at, Quaternion.identity, new Vector3(2f, 2f, 4f));
        link.AddComponent<ArticulationBody>();
        return link.AddComponent<MotorActuator>();
    }

    private static int CountNamed(Transform root, string name)
    {
        int n = 0;
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) n++;
        return n;
    }
}
