using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Headless checks for Set Starting Pose — re-zeroing a mechanism's rest pose without breaking its
// joint.
//
// This one is simulated rather than arithmetic, because the two things that can go wrong are both
// invisible to arithmetic:
//
//   THE SIGN. A joint angle's positive direction is a property of PhysX, not of the code that wrote
//   the joint. Get it backwards and "start at the bottom of its range" swings the part the wrong way
//   and lands it outside its own limits — where PhysX quietly drags it back and the tool looks like
//   it did nothing. So the convention is MEASURED here (drive the joint, read the transform) and
//   compared against what StartingPose.JointRotation predicts, instead of being asserted in a
//   comment.
//
//   THE PARENT ANCHOR. A joint frame is stored on both sides. Move the link and re-measure only the
//   limits, and the parent still says the OLD pose is angle zero: the part snaps back on the first
//   physics step. Nothing in edit mode shows this — the Scene view looks perfect right up until you
//   press Play. Hence the "simulate and check it stayed put" assertions.
//
// Usage: Tools > RoboSim > Validation > Validate Starting Pose, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod StartingPoseValidation.RunBatchValidate
public static class StartingPoseValidation
{
    private const string TestRobotId = "startingposetestbot";
    private const float Lower = 0f;
    private const float Upper = 90f;

    [MenuItem("Tools/RoboSim/Validation/Validate Starting Pose", false, 13)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive("Validate Starting Pose", Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Validate Starting Pose", Run);

    private static string Run()
    {
        // AddMechanismJoint.Apply refreshes the robot CATALOG, which is a real project asset — so the
        // synthetic robot has to be swept back out of it, exactly as the builder validators do.
        bool hadEntry = RoboSimPaths.HasCatalogEntry(TestRobotId);
        SimulationMode previousSimulation = Physics.simulationMode;
        Vector3 previousGravity = Physics.gravity;
        try
        {
            // Gravity off for the whole run: every assertion here is about where a joint HOLDS a
            // link, and a sagging arm fighting its drive only adds noise to that.
            Physics.simulationMode = SimulationMode.Script;
            Physics.gravity = Vector3.zero;

            // A fresh scene per case — Physics.Simulate moves real transforms, and a re-pose reads
            // the transform, so a simulated fixture is no longer the authored one.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string sign = PositiveAngleMatchesTheRotationWeApply();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string landed = RePoseLandsWhereTheLimitDid();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string held = RePoseSurvivesSimulation();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string range = TravelStillReachesTheSamePlaces();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string edges = EdgeCasesHold();

            return "Validate Starting Pose: PASSED\n\n" + sign + "\n" + landed + "\n" + held + "\n" +
                   range + "\n" + edges;
        }
        finally
        {
            Physics.simulationMode = previousSimulation;
            Physics.gravity = previousGravity;
            PlayerPrefs.DeleteKey(ControllerMapSettings.PrefKey(TestRobotId));
            PlayerPrefs.Save();
            if (!hadEntry) RoboSimPaths.RemoveCatalogEntry(TestRobotId);
            // The scratch scenes are never saved, so the synthetic robots die with them.
        }
    }

    // --- The sign convention ----------------------------------------------------------------------

    // Drive a real joint to a known angle, then check that the rotation StartingPose WOULD apply for
    // that same angle puts the link in the same place. This is the assertion the whole tool rests on.
    private static string PositiveAngleMatchesTheRotationWeApply()
    {
        GameObject arm = MakeArm(Lower, Upper);
        ArticulationBody body = arm.GetComponent<ArticulationBody>();
        ValidationUtil.Assert(StartingPose.TryJointFrame(body, out Vector3 axisWorld, out Vector3 pivotWorld),
            "a revolute mechanism must have a readable joint frame");

        Quaternion restRot = arm.transform.rotation;
        const float driven = 45f;

        DriveTo(body, driven);
        Simulate(300);

        float measured = body.jointPosition.dofCount > 0 ? body.jointPosition[0] * Mathf.Rad2Deg : float.NaN;
        ValidationUtil.Near(measured, driven, 2f,
            "the fixture's drive must actually reach its target, or this measures nothing");

        Quaternion predicted = StartingPose.JointRotation(driven, axisWorld) * restRot;
        float error = Quaternion.Angle(arm.transform.rotation, predicted);
        ValidationUtil.Assert(error < 1.5f,
            $"a joint angle of +{driven}° must be a right-handed rotation of +{driven}° about the " +
            $"anchor frame's X axis — the simulated link is {error:0.0}° away from that. If this " +
            "fails, StartingPose.JointRotation needs its sign flipped and every re-pose has been " +
            "moving parts the wrong way.");

        // The tautology guard from the testing rules: prove this check can tell the two signs apart.
        // Without it a formula that ignored the sign entirely would pass the assertion above.
        float mirroredError = Quaternion.Angle(arm.transform.rotation,
            StartingPose.JointRotation(-driven, axisWorld) * restRot);
        ValidationUtil.Assert(mirroredError > 45f,
            $"the mirrored prediction is only {mirroredError:0.0}° away, so this check cannot tell a " +
            "correct sign from a flipped one — pick a fixture angle where the two differ");

        // And the axis is the ANCHOR's X, not the axis the tool was handed: ConfigureJointLink points
        // the anchor down MINUS the requested axis (matching the URDF importer), so a caller reading
        // the requested axis instead would be 180° out. Pinned because it is the exact confusion that
        // had the joint window previewing its swept arc mirrored.
        Vector3 requested = Vector3.forward;
        ValidationUtil.Assert(Vector3.Dot(axisWorld, requested) < -0.9f,
            $"the joint frame's X should oppose the requested axis {requested} (got {axisWorld}) — if " +
            "this changes, AddMechanismJointWindow's arc preview has to change with it");

        ValidationUtil.Assert(Vector3.Distance(pivotWorld, arm.transform.position) < 1e-3f,
            "the fixture's pivot should sit on the link origin");
        return $"sign: a +{driven}° joint angle is +{driven}° right-handed about the anchor X ({error:0.00}° error).";
    }

    // --- The re-pose ------------------------------------------------------------------------------

    // Re-posing to the top of the range must put the part exactly where driving it to the top would,
    // and re-measure the limits so the new rest pose is zero.
    private static string RePoseLandsWhereTheLimitDid()
    {
        GameObject arm = MakeArm(Lower, Upper);
        ArticulationBody body = arm.GetComponent<ArticulationBody>();
        StartingPose.TryJointFrame(body, out Vector3 axisWorld, out Vector3 pivotWorld);

        Quaternion restRot = arm.transform.rotation;
        Vector3 restPos = arm.transform.position;
        Quaternion expectedRot = StartingPose.JointRotation(Upper, axisWorld) * restRot;
        Vector3 expectedPos = pivotWorld + StartingPose.JointRotation(Upper, axisWorld) * (restPos - pivotWorld);

        StartingPose.Apply(arm, Upper, useUndo: false);

        ValidationUtil.Assert(Quaternion.Angle(arm.transform.rotation, expectedRot) < 0.01f,
            "re-posing to the top of the range must leave the part where driving it to the top would");
        ValidationUtil.Assert(Vector3.Distance(arm.transform.position, expectedPos) < 1e-3f,
            "the part must rotate ABOUT ITS PIVOT — a rotation about the wrong point moves it off " +
            "the hinge and the joint drags it back");

        ArticulationDrive d = body.xDrive;
        ValidationUtil.Near(d.lowerLimit, Lower - Upper, 1e-3f, "the lower limit must shift by the re-pose");
        ValidationUtil.Near(d.upperLimit, Upper - Upper, 1e-3f, "the new rest pose must be the top of the range");

        // The hinge itself must not have moved: it is the axis of the rotation, so both the axis and
        // the pivot are invariant. A drifting pivot is how "the arm slowly walks across the robot
        // every time you re-pose it" would start.
        StartingPose.TryJointFrame(body, out Vector3 axisAfter, out Vector3 pivotAfter);
        ValidationUtil.Assert(Vector3.Distance(pivotAfter, pivotWorld) < 1e-3f,
            $"the pivot moved from {pivotWorld} to {pivotAfter} — a re-pose must turn the part about " +
            "the hinge, not carry the hinge with it");
        ValidationUtil.Assert(Vector3.Dot(axisAfter, axisWorld) > 0.9999f,
            "the hinge axis must come out of a re-pose unchanged");
        return $"re-pose: the part lands on the old {Upper}° pose and its travel re-measures to " +
               $"{d.lowerLimit:0.#}°…{d.upperLimit:0.#}°.";
    }

    // The one that only simulation can see: PhysX has to AGREE that the new pose is zero.
    private static string RePoseSurvivesSimulation()
    {
        GameObject arm = MakeArm(Lower, Upper);
        ArticulationBody body = arm.GetComponent<ArticulationBody>();

        StartingPose.Apply(arm, Upper, useUndo: false);
        Quaternion posed = arm.transform.rotation;

        // Target zero — i.e. "stay at the new rest pose". If the parent-side anchor still described
        // the OLD pose, the joint would now be sitting Upper degrees off its own zero and would haul
        // the part back through the whole range.
        DriveTo(body, 0f);
        Simulate(300);

        float drift = Quaternion.Angle(arm.transform.rotation, posed);
        ValidationUtil.Assert(drift < 1.5f,
            $"the part swung {drift:0.0}° away from its new starting pose once physics ran. The " +
            "parent-side joint anchor still describes the old rest pose — RederiveParentAnchors is " +
            "missing or ran before the transform moved.");

        float angleNow = body.jointPosition.dofCount > 0 ? body.jointPosition[0] * Mathf.Rad2Deg : float.NaN;
        ValidationUtil.Near(angleNow, 0f, 1.5f,
            "the joint must read ZERO at the new starting pose — that is what 'this is now its rest " +
            "pose' means");
        return $"holds: the new pose survives 3 s of simulation ({drift:0.00}° drift) and reads as joint zero.";
    }

    // Range preservation, stated the way the tool promises it: everywhere the part could reach
    // before, it can still reach.
    private static string TravelStillReachesTheSamePlaces()
    {
        GameObject arm = MakeArm(Lower, Upper);
        ArticulationBody body = arm.GetComponent<ArticulationBody>();
        Quaternion oldRest = arm.transform.rotation;   // the old LOWER end, since Lower == 0

        StartingPose.Apply(arm, Upper, useUndo: false);

        // Drive to the new lower limit, which is the old rest pose expressed in the new frame.
        DriveTo(body, body.xDrive.lowerLimit);
        Simulate(400);

        float error = Quaternion.Angle(arm.transform.rotation, oldRest);
        ValidationUtil.Assert(error < 2f,
            $"driven to its new lower limit the part should be back at the pose it used to start in, " +
            $"but it is {error:0.0}° away — the limits were shifted by the wrong amount, so the part " +
            "has lost (or gained) travel it should not have");
        return $"travel: driving to the new lower limit returns the part to its old start ({error:0.0}° error).";
    }

    // --- Guards -----------------------------------------------------------------------------------

    private static string EdgeCasesHold()
    {
        GameObject root = MakeChassis(out RobotMechanisms _);

        // Nothing to re-pose: no joint at all, and the articulation root.
        GameObject plain = ValidationUtil.MakeBox(root.transform, "PlainPart", new Vector3(3f, 1f, 0f),
            new Vector3(1f, 1f, 1f));
        ValidationUtil.AssertThrows(() => StartingPose.Apply(plain, 30f, useUndo: false),
            "re-posing a part with no joint");
        ValidationUtil.AssertThrows(() => StartingPose.Apply(root, 30f, useUndo: false),
            "re-posing the articulation root");

        // A free-spinning joint has no travel window, so there is nothing to shift — and inventing
        // one would turn a roller that spins forever into a roller with a 30 degree range.
        GameObject roller = MakeLink(root.transform, "Roller", new Vector3(0f, 2f, 0f));
        AddMechanismJoint.Apply(roller, AddMechanismJoint.JointType.Continuous, Vector3.forward,
            Vector3.zero, 0f, 0f, useUndo: false);
        ArticulationBody rollerBody = roller.GetComponent<ArticulationBody>();
        Quaternion before = roller.transform.rotation;
        StartingPose.Apply(roller, 30f, useUndo: false);
        ValidationUtil.Assert(Quaternion.Angle(roller.transform.rotation, before) > 25f,
            "a free-spinning joint must still be re-posable — it just has no limits to keep in step");
        ValidationUtil.Near(rollerBody.xDrive.lowerLimit, 0f, 1e-4f,
            "a free-spinning joint must not be given a lower limit by a re-pose");
        ValidationUtil.Near(rollerBody.xDrive.upperLimit, 0f, 1e-4f,
            "a free-spinning joint must not be given an upper limit by a re-pose");

        // Past the end of the travel: clamped to the end, not obeyed. Parking a part outside its own
        // limits is never what the caller wanted, and PhysX would drag it back anyway.
        GameObject arm = MakeLink(root.transform, "ClampArm", new Vector3(0f, 4f, 0f));
        AddMechanismJoint.Apply(arm, AddMechanismJoint.JointType.Revolute, Vector3.forward,
            Vector3.zero, Lower, Upper, useUndo: false);
        StartingPose.Apply(arm, 500f, useUndo: false);
        ArticulationDrive clamped = arm.GetComponent<ArticulationBody>().xDrive;
        ValidationUtil.Near(clamped.lowerLimit, Lower - Upper, 1e-3f,
            "an offset past the end of the travel must clamp to the end");
        ValidationUtil.Near(clamped.upperLimit, 0f, 1e-3f,
            "...leaving the part parked exactly at that end");

        // A piston's endpoints ARE its joint limits, so a re-pose has to carry them along or the
        // cylinder starts driving to a target outside its own range.
        GameObject rod = MakeLink(root.transform, "Rod", new Vector3(0f, 6f, 0f));
        AddMechanismJoint.Apply(rod, AddMechanismJoint.JointType.Prismatic, Vector3.right,
            Vector3.zero, 0f, 5f, useUndo: false);
        PneumaticActuator piston = rod.GetComponent<PneumaticActuator>();
        ValidationUtil.Assert(piston != null, "a prismatic mechanism should be wired as a pneumatic");
        Vector3 rodBefore = rod.transform.position;
        StartingPose.Apply(rod, 5f, useUndo: false);
        ValidationUtil.Near(Vector3.Distance(rod.transform.position, rodBefore), 5f, 1e-3f,
            "a slider must be re-posed by MOVING it along its axis");
        ValidationUtil.Near(piston.retractedTarget, -5f, 1e-3f,
            "the piston's retracted endpoint must follow the shifted lower limit");
        ValidationUtil.Near(piston.extendedTarget, 0f, 1e-3f,
            "the piston's extended endpoint must follow the shifted upper limit");

        // Zero is a no-op rather than an error, so a caller can apply unconditionally.
        Quaternion untouched = arm.transform.rotation;
        StartingPose.Apply(arm, 0f, useUndo: false);
        ValidationUtil.Assert(Quaternion.Angle(arm.transform.rotation, untouched) < 1e-3f,
            "a zero offset must change nothing");
        return "guards: no-joint and root rejected, free spin keeps no limits, overshoot clamps, " +
               "piston endpoints follow, zero is a no-op.";
    }

    // --- Fixture ----------------------------------------------------------------------------------

    // A chassis with an arm hinged at its own origin, out along +X, about the world +Z axis — built
    // through the real AddMechanismJoint path so the joint under test is the one the tool ships,
    // anchorRotation convention included.
    private static GameObject MakeArm(float lower, float upper)
    {
        GameObject root = MakeChassis(out RobotMechanisms _);
        GameObject arm = MakeLink(root.transform, "Outtake", new Vector3(0f, 2f, 0f));
        AddMechanismJoint.Apply(arm, AddMechanismJoint.JointType.Revolute, Vector3.forward,
            Vector3.zero, lower, upper, useUndo: false);
        return arm;
    }

    private static GameObject MakeChassis(out RobotMechanisms registry)
    {
        GameObject root = new GameObject("PoseBot");
        registry = root.AddComponent<RobotMechanisms>();
        registry.robotId = TestRobotId;
        ArticulationBody chassis = root.AddComponent<ArticulationBody>();
        chassis.immovable = true;   // the world end of the chain, so only the arm can move
        ValidationUtil.MakeBox(root.transform, "ChassisMesh", Vector3.zero, new Vector3(6f, 1f, 6f));
        return root;
    }

    // An empty link with its mesh OFF to one side, so the link's own scale stays 1 (anchorPosition is
    // link-local, and a scaled link would make every anchor coordinate a puzzle) and the part has a
    // real arm for a rotation to be visible on.
    private static GameObject MakeLink(Transform parent, string name, Vector3 localPosition)
    {
        GameObject link = new GameObject(name);
        link.transform.SetParent(parent, false);
        link.transform.localPosition = localPosition;
        ValidationUtil.MakeBox(link.transform, name + "Mesh",
            link.transform.position + new Vector3(2f, 0f, 0f), new Vector3(4f, 0.4f, 0.4f));
        return link;
    }

    // A stiff position drive, so the joint parks where it is told inside a test's step budget rather
    // than creeping there. Gravity is off for the whole run, so nothing is fighting it.
    private static void DriveTo(ArticulationBody body, float degrees)
    {
        body.useGravity = false;
        ArticulationDrive d = body.xDrive;
        d.driveType = ArticulationDriveType.Target;
        d.stiffness = 200000f;
        d.damping = 5000f;
        d.forceLimit = float.MaxValue;
        d.target = degrees;
        body.xDrive = d;
    }

    private static void Simulate(int steps)
    {
        for (int i = 0; i < steps; i++) Physics.Simulate(ValidationUtil.StepSeconds);
    }
}
