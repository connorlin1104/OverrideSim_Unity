using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Headless check of the Build Cascade Lift core (CascadeSetup.Build/Strip) against a synthetic robot
// in a scratch scene — no real robot prefab is touched.
//
// What this exists to catch, in order of how expensive it is to discover in Play:
//
//  1. THE BARS DON'T NEST. A cascade's whole point is that bar 2 rides bar 1, so the top of a 3-bar
//     lift reaches the SUM of the travels. Build it as three siblings of the chassis and each bar
//     still slides its own distance — it looks like it works until you notice the lift is a third of
//     the height it should be. So the test measures cumulative rise, not "did it move".
//  2. THE CARRIED ARM SNAPS. Reparenting a jointed link changes its parent body, and PhysX keeps the
//     joint frame on BOTH sides — leave the parent anchor stale and the arm teleports on the first
//     simulation step, which reads as "the claw exploded" rather than as a missing anchor.
//  3. A STAGE GETS A BUTTON. A stage that is registered or carries an actuator would have
//     ButtonRouter fighting CascadeLift for its drive every frame.
//  4. THE TRAVEL IGNORES THE BAR BELOW. A long bar on a short one can only run out as far as the
//     short one allows; using its own length looks right until the top bar floats off its channel.
//
// Usage: Tools > RoboSim > Testing > Validate Build Cascade, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod CascadeBuilderValidation.RunBatchValidate
public static class CascadeBuilderValidation
{
    private const string TestRobotId = "__cascade_validation__";
    private const string CatalogPath = "Assets/Settings/RobotModelCatalog.asset";
    private const float ChannelLength = 4f;
    private const float OverlapHoles = 2f;
    private const float RaiseSeconds = 0.5f;   // short, so a test run is a second of simulation
    private const float Step = 0.02f;

    // travel = channel - overlap x pitch, the rule the window shows before you build.
    private static float ExpectedTravel =>
        ChannelLength - OverlapHoles * CascadeSetup.DefaultHolePitch;

    [MenuItem("Tools/RoboSim/Testing/Validate Build Cascade", false, 13)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        try
        {
            EditorUtility.DisplayDialog("Validate Build Cascade", Run(), "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Validate Build Cascade", "FAILED\n\n" + e.Message, "OK");
            Debug.LogException(e);
        }
    }

    public static void RunBatchValidate()
    {
        try
        {
            Debug.Log(Run());
        }
        catch (Exception e)
        {
            Debug.LogError("Validate Build Cascade FAILED: " + e.Message);
            EditorApplication.Exit(1);
            return;
        }
        EditorApplication.Exit(0);
    }

    private static string Run()
    {
        bool hadEntry = HasCatalogEntry(TestRobotId);
        SimulationMode previousSimulation = Physics.simulationMode;
        Vector3 previousGravity = Physics.gravity;
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string structure = Structure();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string together = BarsStackUpTogether();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string sequence = BarsRunOutInOrder();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string travel = TravelComesFromTheChannel();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string teardown = RebuildAndDelete();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string rejects = RejectionsHold();

            return structure + "\n\n" + together + "\n\n" + sequence + "\n\n" + travel + "\n\n" +
                   teardown + "\n\n" + rejects;
        }
        finally
        {
            Physics.simulationMode = previousSimulation;
            Physics.gravity = previousGravity;
            PlayerPrefs.DeleteKey(ControllerMapSettings.PrefKey(TestRobotId));
            PlayerPrefs.Save();
            if (!hadEntry) RemoveCatalogEntry(TestRobotId);
            // The scratch scenes are never saved, so the synthetic robots die with them.
        }
    }

    // --- Structure: the tree, the drives, and who is allowed a button -------------------------------
    private static string Structure()
    {
        Fixture f = MakeFixture("StructureBot", 3);
        CascadeSetup.Build(f.Options(), useUndo: false);

        CascadeRig rig = f.registry.GetComponent<CascadeRig>();
        Assert(rig != null, "the build should leave a CascadeRig so the lift can be re-edited");
        Assert(rig.bars.Count == 3, $"all three bars should have been built (got {rig.bars.Count})");

        // --- Nesting: bar N is a CHILD of bar N-1, which is what makes the reaches add up ----------
        Transform expectedParent = f.registry.transform;
        for (int i = 0; i < rig.bars.Count; i++)
        {
            GameObject link = rig.bars[i].builtLink;
            Assert(link != null, $"bar {i + 1} should have produced a link");
            Assert(link.transform.parent == expectedParent,
                $"bar {i + 1}'s link should hang off {(i == 0 ? "the chassis" : $"bar {i}'s link")} — " +
                "siblings would each slide their own distance instead of stacking up");

            ArticulationBody body = link.GetComponent<ArticulationBody>();
            Assert(body != null && body.jointType == ArticulationJointType.PrismaticJoint,
                $"bar {i + 1} must be a prismatic (sliding) joint");
            Assert(body.linearLockX == ArticulationDofLock.LimitedMotion,
                $"bar {i + 1} must be limited-motion, or it slides off the end of its channel");
            AssertApprox(body.xDrive.lowerLimit, 0f, 1e-3f, $"bar {i + 1} should rest fully retracted");
            AssertApprox(body.xDrive.upperLimit, ExpectedTravel, 1e-3f,
                $"bar {i + 1}'s travel should come from its channel");
            Assert(body.xDrive.driveType == ArticulationDriveType.Target,
                $"bar {i + 1} needs a position drive baked at build time — edit-mode validation and " +
                "the pre-settle never run Awake, so an unbaked stage behaves differently there");

            Assert(link.GetComponent<IgnoreRobotSelfCollision>() != null,
                $"bar {i + 1} needs IgnoreRobotSelfCollision — telescoping bars overlap by construction");

            // The rule a controller-driven link lives by: no actuator, no registry entry.
            Assert(link.GetComponent<MotorActuator>() == null &&
                   link.GetComponent<PneumaticActuator>() == null,
                $"bar {i + 1} is driven by CascadeLift, so it must not also carry an actuator");
            Assert(f.registry.Find(UrdfPostProcessor.Slugify(link.name)) == null,
                $"bar {i + 1} must not be registered — ButtonRouter would fight CascadeLift for its drive");

            Assert(f.channels[i].transform.IsChildOf(link.transform) &&
                   f.parts[i].transform.IsChildOf(link.transform),
                $"bar {i + 1}'s parts and channel should have welded into its link");
            expectedParent = link.transform;
        }

        // --- The one thing on the buttons ----------------------------------------------------------
        string driverId = UrdfPostProcessor.Slugify(CascadeSetup.DriverName);
        RobotMechanisms.Mechanism mech = f.registry.Find(driverId);
        Assert(mech != null, "the hidden driver should be the lift's registered mechanism");
        Assert(mech.type == RobotMechanisms.TypeMotor,
            "the lift must register as a MOTOR — that's what gives the player the hold-forward/reverse " +
            "pair (and the 1-vs-2-button choice) for free");
        Assert(mech.displayName == "Test Cascade",
            $"the config screen should show the name the form asked for (got '{mech.displayName}')");

        ButtonMap map = ControllerMapSettings.Load(TestRobotId);
        Assert(FindAssignment(map, driverId) != null, "the lift should have been given a button");
        foreach (CascadeRig.Bar bar in rig.bars)
            Assert(FindAssignment(map, UrdfPostProcessor.Slugify(bar.builtLink.name)) == null,
                "a bar must never get a button of its own");

        // --- The carried arm ------------------------------------------------------------------------
        GameObject top = rig.bars[rig.bars.Count - 1].builtLink;
        Assert(f.arm.transform.parent == top.transform,
            "the carried arm should hang off the TOP bar, or it wouldn't ride the lift");
        Assert(f.arm.GetComponent<ArticulationBody>() != null,
            "the carried arm keeps its own joint — the lift moves it, it isn't absorbed");
        Assert(f.registry.Find(UrdfPostProcessor.Slugify(f.arm.name)) != null,
            "the carried arm's own mechanism must survive being picked up by the lift");

        CascadeLift lift = f.registry.GetComponent<CascadeLift>();
        Assert(lift != null && lift.stages.Count == 3, "the controller should hold all three stages");
        Assert(lift.driver != null && lift.driver.gameObject.name == CascadeSetup.DriverName,
            "the controller should read its progress off the hidden driver");
        for (int i = 0; i < 3; i++)
            Assert(lift.stages[i].body == rig.bars[i].builtLink.GetComponent<ArticulationBody>(),
                "the controller's stages must be in bottom-to-top order — the sequence depends on it");

        return "Structure: PASSED — bars nested bottom-to-top as real prismatic joints, travel from " +
               "the channel, drives baked, only the hidden driver registered or bound to a button, and " +
               "the carried arm reparented onto the top bar with its own mechanism intact.";
    }

    // --- All at once: the reaches ADD UP, and the arm rides the whole way ---------------------------
    private static string BarsStackUpTogether()
    {
        Fixture f = MakeFixture("TogetherBot", 3);
        CascadeSetup.Build(f.Options(), useUndo: false);
        CascadeRig rig = f.registry.GetComponent<CascadeRig>();
        CascadeLift lift = f.registry.GetComponent<CascadeLift>();

        Physics.simulationMode = SimulationMode.Script;
        Physics.gravity = Vector3.zero;

        // Where everything sits before anything is pressed.
        var restY = new float[rig.bars.Count];
        for (int i = 0; i < rig.bars.Count; i++) restY[i] = rig.bars[i].builtLink.transform.position.y;
        float armRestY = f.arm.transform.position.y;
        Vector3 armRest = f.arm.transform.position;

        // Settle with NO input first. A jointed link whose parent changed and whose parent-side anchor
        // was never re-derived jumps on the very first step — this is that check, and it's why the
        // reparent path re-derives anchors rather than trusting the transforms.
        lift.ApplyStep();
        for (int i = 0; i < 10; i++) { Physics.Simulate(Step); lift.ApplyStep(); }
        float drift = Vector3.Distance(f.arm.transform.position, armRest);
        Assert(drift < 0.05f,
            $"the carried arm moved {drift:F2} units just by being simulated — its joint is still " +
            "anchored to where the chassis used to hold it, so it snaps the moment physics runs");

        RunLift(lift, f.DriverMotor(), 1f, 150);

        AssertApprox(lift.Progress, 1f, 0.02f, "holding the button should drive the lift fully out");

        // Bar N rides bars 1..N-1, so its rise is the RUNNING TOTAL. Three siblings would each rise
        // one travel and this is what would catch it.
        for (int i = 0; i < rig.bars.Count; i++)
        {
            float expected = ExpectedTravel * (i + 1);
            float actual = rig.bars[i].builtLink.transform.position.y - restY[i];
            AssertApprox(actual, expected, 0.15f,
                $"bar {i + 1} should have risen the total of the {i + 1} bars below and including it " +
                "— if every bar rises the same amount, they aren't nested");
        }

        float armRise = f.arm.transform.position.y - armRestY;
        AssertApprox(armRise, ExpectedTravel * rig.bars.Count, 0.15f,
            "the carried arm rides the top bar, so it should reach the lift's full height");

        // And it comes back down.
        RunLift(lift, f.DriverMotor(), -1f, 200);
        AssertApprox(lift.Progress, 0f, 0.02f, "the lift should come back down when reversed");
        AssertApprox(f.arm.transform.position.y - armRestY, 0f, 0.15f,
            "a fully retracted lift should put the arm back where it started");

        return "All at once: PASSED — every bar rose by the running total of the ones below it, the " +
               "carried arm reached the lift's full height without snapping, and reversing brought it " +
               "all back down.";
    }

    // --- One at a time: the bars run out in ORDER, never all at once --------------------------------
    private static string BarsRunOutInOrder()
    {
        Fixture f = MakeFixture("SequenceBot", 3);
        CascadeSetup.Options ascending = f.Options();
        ascending.oneAtATime = true;
        CascadeSetup.Build(ascending, useUndo: false);
        CascadeRig rig = f.registry.GetComponent<CascadeRig>();
        CascadeLift lift = f.registry.GetComponent<CascadeLift>();

        Physics.simulationMode = SimulationMode.Script;
        Physics.gravity = Vector3.zero;

        // A third of the way through the press, with three bars: the first one is out, the other two
        // haven't started. (Held at a fixed driver angle rather than timed, so the reading is about
        // the sequencing and not about how fast the test happens to run.)
        float[] extension = ExtensionsAtProgress(lift, f, rig, 1f / 3f);
        AssertApprox(extension[0], ExpectedTravel, 0.2f,
            "bottom-first: a third of the way in, bar 1 should be fully out");
        AssertApprox(extension[1], 0f, 0.2f,
            "bottom-first: bar 2 must not have started until bar 1 finished — that's the whole " +
            "difference from the all-at-once mode");
        AssertApprox(extension[2], 0f, 0.2f, "bottom-first: bar 3 must not have started either");

        // ...and by the end they're all out, whichever order they went in.
        float[] full = ExtensionsAtProgress(lift, f, rig, 1f);
        for (int i = 0; i < 3; i++)
            AssertApprox(full[i], ExpectedTravel, 0.2f,
                $"bar {i + 1} should be fully out at the end of the press");

        // Descending is the mirror: the TOP bar goes first.
        Fixture g = MakeFixture("SequenceTopBot", 3);
        CascadeSetup.Options descending = g.Options();
        descending.oneAtATime = true;
        descending.topFirst = true;
        CascadeSetup.Build(descending, useUndo: false);
        CascadeRig topRig = g.registry.GetComponent<CascadeRig>();
        CascadeLift topLift = g.registry.GetComponent<CascadeLift>();

        float[] topFirst = ExtensionsAtProgress(topLift, g, topRig, 1f / 3f);
        AssertApprox(topFirst[2], ExpectedTravel, 0.2f,
            "top-first: a third of the way in, the TOP bar should be the one that's out");
        AssertApprox(topFirst[0], 0f, 0.2f,
            "top-first: the bottom bar must go LAST — otherwise the order setting does nothing");

        return "One at a time: PASSED — bottom-first ran bar 1 out before bar 2 started, top-first " +
               "reversed that, and both had every bar out by the end of the press.";
    }

    // --- Travel: measured off the channel, and limited by the bar below -----------------------------
    private static string TravelComesFromTheChannel()
    {
        // Bar 2's channel is half as long again as bar 1's. It can still only run out along the
        // channel it SITS IN, so bar 1 is what limits it.
        Fixture f = MakeFixture("TravelBot", 2, longSecondChannel: true);
        CascadeSetup.Build(f.Options(), useUndo: false);
        CascadeRig rig = f.registry.GetComponent<CascadeRig>();

        AssertApprox(rig.bars[0].builtTravel, ExpectedTravel, 1e-3f,
            "bar 1's travel should be its channel less the overlap that has to stay engaged");
        AssertApprox(rig.bars[1].builtTravel, ExpectedTravel, 1e-3f,
            "bar 2 is longer, but it can only run out as far as the bar it slides in allows — using " +
            "its own length floats it off the end of bar 1's channel");

        // A hand-set override beats the measurement outright.
        CascadeSetup.Options overridden = f.Options();
        overridden.bars[1].travelOverride = 1.25f;
        CascadeSetup.Build(overridden, useUndo: false);
        rig = f.registry.GetComponent<CascadeRig>();
        AssertApprox(rig.bars[1].builtTravel, 1.25f, 1e-3f, "a travel override should win outright");
        AssertApprox(rig.bars[1].builtLink.GetComponent<ArticulationBody>().xDrive.upperLimit, 1.25f,
            1e-3f, "the override has to reach the JOINT, not just the record");
        overridden.bars[1].travelOverride = 0f;   // the fixture's bars are shared with later builds

        // More overlap than there is channel is a build that can't move; it's refused rather than
        // silently producing a lift with nowhere to go.
        CascadeSetup.Options greedy = f.Options();
        greedy.overlapHoles = 999f;
        AssertThrows(() => CascadeSetup.Build(greedy, useUndo: false),
            "an overlap that eats the whole channel");

        return "Travel: PASSED — measured off the channel less the overlap, capped by the bar below, " +
               "overridable by hand, and refused when the overlap leaves nothing to slide.";
    }

    // --- Rebuild and delete -------------------------------------------------------------------------
    private static string RebuildAndDelete()
    {
        Fixture f = MakeFixture("TeardownBot", 3);
        Transform[] homes = new Transform[f.channels.Length];
        for (int i = 0; i < f.channels.Length; i++) homes[i] = f.channels[i].transform.parent;
        Transform armHome = f.arm.transform.parent;

        CascadeSetup.Build(f.Options(), useUndo: false);

        // Rebuild with one fewer bar: the dropped bar's parts must come home, not stay riding a link
        // nobody lists any more.
        GameObject droppedChannel = f.channels[2];
        CascadeSetup.Options fewer = f.Options();
        fewer.bars = new List<CascadeRig.Bar> { fewer.bars[0], fewer.bars[1] };
        CascadeSetup.Build(fewer, useUndo: false);

        CascadeRig rig = f.registry.GetComponent<CascadeRig>();
        Assert(rig.bars.Count == 2, "the rebuild should have kept only the two listed bars");
        Assert(droppedChannel.transform.parent == homes[2],
            "a bar dropped from the form must be put back where it came from, or it keeps riding a " +
            "stage that no longer exists in the record");
        int stages = 0;
        foreach (Transform t in f.registry.GetComponentsInChildren<Transform>(true))
            if (t.name.StartsWith(CascadeSetup.StagePrefix, StringComparison.Ordinal)) stages++;
        Assert(stages == 2, $"a rebuild must leave exactly one link per listed bar (found {stages})");

        // Delete: everything home, nothing left behind.
        CascadeSetup.Strip(f.registry, rig, useUndo: false);

        for (int i = 0; i < 2; i++)
        {
            // Checked before the parent, because the way this goes wrong is fatal rather than untidy:
            // a part still sitting inside a stage link when that link is destroyed is destroyed WITH
            // it, so "Delete didn't restore it" and "Delete ate half the robot" are the same bug.
            Assert(f.channels[i] != null,
                $"Delete destroyed bar {i + 1}'s channel — its parts have to come out of the stage " +
                "link before the link is removed");
            Assert(f.channels[i].transform.parent == homes[i],
                $"Delete must put bar {i + 1}'s channel back in the group it came from");
        }
        Assert(f.arm.transform.parent == armHome,
            "Delete must put the carried arm back on the chassis");
        Assert(f.arm.GetComponent<ArticulationBody>() != null &&
               f.registry.Find(UrdfPostProcessor.Slugify(f.arm.name)) != null,
            "Delete must leave the carried arm's own joint and mechanism alone — the lift borrowed it, " +
            "it didn't own it");
        Assert(f.registry.GetComponent<CascadeLift>() == null &&
               f.registry.GetComponent<CascadeRig>() == null,
            "Delete must remove the controller and its record");
        Assert(f.registry.Find(UrdfPostProcessor.Slugify(CascadeSetup.DriverName)) == null,
            "Delete must remove the lift's mechanism");
        foreach (Transform t in f.registry.GetComponentsInChildren<Transform>(true))
        {
            Assert(!t.name.StartsWith(CascadeSetup.StagePrefix, StringComparison.Ordinal),
                $"Delete left the stage link '{t.name}' behind");
            Assert(t.name != CascadeSetup.DriverName, "Delete left the hidden driver behind");
        }
        ButtonMap map = ControllerMapSettings.Load(TestRobotId);
        Assert(FindAssignment(map, UrdfPostProcessor.Slugify(CascadeSetup.DriverName)) == null,
            "Delete must release the button the lift held");

        // And the arm, back on the chassis, still doesn't snap when physics runs.
        Physics.simulationMode = SimulationMode.Script;
        Physics.gravity = Vector3.zero;
        Vector3 armAt = f.arm.transform.position;
        for (int i = 0; i < 10; i++) Physics.Simulate(Step);
        float drift = Vector3.Distance(f.arm.transform.position, armAt);
        Assert(drift < 0.05f,
            $"the arm moved {drift:F2} units after Delete — putting it back on the chassis has to " +
            "re-derive its parent anchor too, or removing the lift throws the claw across the field");

        return "Rebuild + delete: PASSED — a dropped bar came home and left no orphan link, and Delete " +
               "restored every part, the arm (joint intact, no snap), the registry and the buttons.";
    }

    // --- What the form must refuse -------------------------------------------------------------------
    private static string RejectionsHold()
    {
        Fixture f = MakeFixture("RejectBot", 2);

        CascadeSetup.Options empty = f.Options();
        empty.bars = new List<CascadeRig.Bar>();
        AssertThrows(() => CascadeSetup.Build(empty, useUndo: false), "a cascade with no bars");

        // The same part on two bars can only hinge once, and which bar wins would be silent.
        CascadeSetup.Options twice = f.Options();
        twice.bars[1].parts.Add(f.parts[0]);
        AssertThrows(() => CascadeSetup.Build(twice, useUndo: false), "one part listed on two bars");
        twice.bars[1].parts.Remove(f.parts[0]);

        // A group that already contains another bar's parts would be welded into one link and then
        // have that part yanked back out — a hierarchy nobody can reason about afterwards.
        // (Each mutation is undone before the next: the Options share one set of Bar objects, so a
        // leftover would make the NEXT case throw for the previous reason and pass by accident.)
        CascadeSetup.Options nested = f.Options();
        GameObject otherGroup = f.channels[1].transform.parent.gameObject;
        nested.bars[0].parts.Add(otherGroup);
        AssertThrows(() => CascadeSetup.Build(nested, useUndo: false),
            "a bar listing a group that contains another bar's parts");
        nested.bars[0].parts.Remove(otherGroup);

        // A bar with nothing to measure and no override can't be built.
        CascadeSetup.Options unmeasurable = f.Options();
        List<GameObject> channels = unmeasurable.bars[1].channels;
        unmeasurable.bars[1].channels = new List<GameObject>();
        AssertThrows(() => CascadeSetup.Build(unmeasurable, useUndo: false),
            "a bar with no channel and no travel override");
        unmeasurable.bars[1].channels = channels;

        // Nothing above should have left a half-built lift behind.
        Assert(f.registry.GetComponent<CascadeRig>() == null &&
               f.registry.GetComponent<CascadeLift>() == null,
            "a refused build must not leave any wiring on the robot — everything is validated before " +
            "anything moves");

        return "Rejections: PASSED — no bars, a part on two bars, a bar nested inside another, and a " +
               "bar with nothing to measure are all refused before anything is touched.";
    }

    // --- Driving the lift ----------------------------------------------------------------------------

    // Hold the button for `steps` physics steps. Note the ORDER: one ApplyStep first, which is where
    // CascadeLift does its one-time speed setup (and pins the driver's idle hold), then the input.
    private static void RunLift(CascadeLift lift, MotorActuator motor, float input, int steps)
    {
        lift.ApplyStep();
        motor.SetInput(input);
        for (int i = 0; i < steps; i++)
        {
            Physics.Simulate(Step);
            lift.ApplyStep();   // stands in for FixedUpdate, which edit-mode simulation never calls
        }
    }

    // How far each bar has slid out of the one below it, with the driver PARKED at a chosen fraction
    // of its sweep. Parking it (a position drive on the hidden joint) rather than timing a button
    // press keeps the reading about the sequencing rather than about how fast the test runs.
    private static float[] ExtensionsAtProgress(CascadeLift lift, Fixture f, CascadeRig rig, float progress)
    {
        lift.ApplyStep();   // flush the one-time setup before overriding the driver's drive
        ArticulationBody driver = lift.driver;
        ArticulationDrive d = driver.xDrive;
        d.driveType = ArticulationDriveType.Target;
        d.stiffness = 20000f;
        d.damping = 500f;
        d.forceLimit = 1e6f;
        driver.xDrive = d;
        driver.SetDriveTarget(ArticulationDriveAxis.X, lift.sweepDeg * progress);

        for (int i = 0; i < 200; i++) { Physics.Simulate(Step); lift.ApplyStep(); }

        var extensions = new float[rig.bars.Count];
        for (int i = 0; i < rig.bars.Count; i++)
        {
            ArticulationReducedSpace p = rig.bars[i].builtLink.GetComponent<ArticulationBody>().jointPosition;
            extensions[i] = p.dofCount > 0 ? Mathf.Abs(p[0]) : 0f;
        }
        return extensions;
    }

    // --- Fixture --------------------------------------------------------------------------------------

    private class Fixture
    {
        public RobotMechanisms registry;
        public GameObject[] channels, parts;
        public GameObject arm;
        public List<CascadeRig.Bar> bars;

        public MotorActuator DriverMotor()
        {
            CascadeLift lift = registry.GetComponent<CascadeLift>();
            return lift.driver.GetComponent<MotorActuator>();
        }

        public CascadeSetup.Options Options() => new CascadeSetup.Options
        {
            displayName = "Test Cascade",
            bars = bars,
            ridesAlong = new List<GameObject> { arm },
            attachToBarIndex = -1,
            overlapHoles = OverlapHoles,
            holePitch = CascadeSetup.DefaultHolePitch,
            raiseSeconds = RaiseSeconds,
            stageStiffness = 20000f,
            stageDamping = 500f,
            stageForceLimit = 5000f,
            autoAssignButtons = true,
        };
    }

    // A fixed chassis with a drivetrain (so there's a lateral axis to read), `barCount` bars — each a
    // vertical channel plus a part, in its own group so the teardown has a real home to restore to —
    // and a jointed arm standing in for the claw arm.
    //
    // The bars are deliberately spread out in Z instead of telescoped inside one another: edit-mode
    // Physics.Simulate never runs Start, so IgnoreRobotSelfCollision hasn't taken effect and an
    // overlapping fixture would measure contact resolution rather than the lift's drives.
    private static Fixture MakeFixture(string name, int barCount, bool longSecondChannel = false)
    {
        GameObject root = new GameObject(name);
        var f = new Fixture { channels = new GameObject[barCount], parts = new GameObject[barCount] };
        f.registry = root.AddComponent<RobotMechanisms>();
        f.registry.robotId = TestRobotId;
        ArticulationBody chassis = root.AddComponent<ArticulationBody>();
        chassis.immovable = true;
        MakeBox(root.transform, "ChassisMesh", Vector3.zero, new Vector3(6f, 1f, 6f));

        RobotMotorController mc = root.AddComponent<RobotMotorController>();
        mc.leftWheels = new[] { MakeWheel(root.transform, "WheelL", new Vector3(0f, 0f, -3f)) };
        mc.rightWheels = new[] { MakeWheel(root.transform, "WheelR", new Vector3(0f, 0f, 3f)) };

        f.bars = new List<CascadeRig.Bar>();
        for (int i = 0; i < barCount; i++)
        {
            GameObject group = new GameObject($"Bar{i + 1}Group");
            group.transform.SetParent(root.transform, false);

            float length = longSecondChannel && i == 1 ? ChannelLength * 1.5f : ChannelLength;
            float z = i * 2f;   // side by side, not nested — see the note above
            f.channels[i] = MakeBox(group.transform, $"{i + 1}1 - 2x C-Chan", new Vector3(0f, 3f, z),
                new Vector3(0.4f, length, 0.4f));
            f.parts[i] = MakeBox(group.transform, $"Bar{i + 1} Motor", new Vector3(0.6f, 3f, z),
                new Vector3(0.5f, 0.5f, 0.5f));

            f.bars.Add(new CascadeRig.Bar
            {
                parts = new List<GameObject> { f.parts[i] },
                channels = new List<GameObject> { f.channels[i] },
            });
        }

        // The carried arm: a real jointed link, the way Build Chain leaves one, so the reparent path
        // is exercised against an actual articulation rather than a plain mesh.
        f.arm = MakeBox(root.transform, "ClawArm", new Vector3(2f, 6f, 0f), new Vector3(2f, 0.4f, 0.4f));
        AddMechanismJoint.Apply(f.arm, AddMechanismJoint.JointType.Revolute, Vector3.forward,
            Vector3.zero, -90f, 90f, useUndo: false);
        return f;
    }

    private static ArticulationBody MakeWheel(Transform parent, string name, Vector3 position)
        => MakeBox(parent, name, position, new Vector3(1f, 1f, 0.4f)).AddComponent<ArticulationBody>();

    private static GameObject MakeBox(Transform parent, string name, Vector3 position, Vector3 size)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = position;
        go.transform.localScale = size;
        return go;
    }

    // --- Small helpers ---------------------------------------------------------------------------

    private static ButtonAssignment FindAssignment(ButtonMap map, string mechanismId)
    {
        if (map?.assignments == null) return null;
        foreach (ButtonAssignment a in map.assignments)
            if (a != null && a.mechanismId == mechanismId) return a;
        return null;
    }

    private static bool HasCatalogEntry(string id)
    {
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
        return catalog != null && catalog.models != null &&
               catalog.models.Exists(e => e != null && e.id == id);
    }

    // The build registers the synthetic robot in the shared catalog asset; drop it again so a
    // validation run leaves no trace in a committed asset.
    private static void RemoveCatalogEntry(string id)
    {
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
        if (catalog == null || catalog.models == null) return;
        if (catalog.models.RemoveAll(e => e != null && e.id == id) == 0) return;
        EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
    }

    private static void Assert(bool condition, string why)
    {
        if (!condition) throw new InvalidOperationException(why);
    }

    private static void AssertApprox(float actual, float expected, float tolerance, string why)
    {
        if (Mathf.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException($"{why} (expected {expected}, got {actual})");
    }

    private static void AssertThrows(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            return; // rejected, as it should be
        }
        throw new InvalidOperationException($"'{what}' was accepted, but it should have been rejected");
    }
}
