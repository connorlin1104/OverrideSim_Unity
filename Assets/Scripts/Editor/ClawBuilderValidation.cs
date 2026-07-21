using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Headless check of the Build Claw core (ClawSetup.Build/Strip) against a synthetic robot built in a
// scratch scene — no real robot prefab is touched.
//
// The thing this exists to catch is the MIRRORED JAW. A claw's second half is the first tool in this
// project to author JointCoupler's Position mode, and whether it closes AGAINST the driven half or
// sweeps the same way is a sign buried in a ratio — invisible until you're in Play watching the
// robot. So the halves aren't just inspected, they're actually fired under physics and their travel
// compared. The same reasoning as Build Chain's spin-direction test.
//
// It also pins the two structural rules a coupled follower must obey (no actuator, no registry entry,
// or ButtonRouter and the coupler fight over the drive) and proves a rebuild that drops a half leaves
// no orphaned joint behind.
//
// Usage: Tools > RoboSim > Testing > Validate Build Claw, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod ClawBuilderValidation.RunBatchValidate
public static class ClawBuilderValidation
{
    private const string TestRobotId = "__claw_validation__";
    private const string CatalogPath = "Assets/Settings/RobotModelCatalog.asset";
    private const float CloseAngle = 35f;
    private const float FlipAngle = 180f;
    private const float FlipSeconds = 0.35f;
    private const float ClampStrokeMm = 50f;

    [MenuItem("Tools/RoboSim/Testing/Validate Build Claw", false, 12)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        try
        {
            EditorUtility.DisplayDialog("Validate Build Claw", Run(), "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Validate Build Claw", "FAILED\n\n" + e.Message, "OK");
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
            Debug.LogError("Validate Build Claw FAILED: " + e.Message);
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
            string structure = StructureAndTeardown();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string motion = JawsCloseAgainstEachOther();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string trim = TrimMovesTheRestPose();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string flip = FlipTurnsInPlace();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string carry = CarriedPiecesLandRight();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string axes = ExplicitAxesMeanWhatTheySay();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string level = LevelKeeperStaysLevel();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string levelFlip = LevelKeeperFlipsPastMidpoint();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string yaw = LevelKeeperYawsWithWrist();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string axle = LevelKeeperRidesTheAxle();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string roll = FlipRollsAboutTheAxle();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string rebuild = RebuildFollowsMovedAxle();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string rejects = RejectionsHold();
            return structure + "\n\n" + motion + "\n\n" + trim + "\n\n" + flip + "\n\n" + carry
                   + "\n\n" + axes + "\n\n" + level + "\n\n" + levelFlip + "\n\n" + yaw + "\n\n" + axle
                   + "\n\n" + roll + "\n\n" + rebuild + "\n\n" + rejects;
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

    // --- Structure: what the build must produce, what a rebuild must clean, what Strip must undo ---
    private static string StructureAndTeardown()
    {
        Fixture f = MakeFixture("StructureBot");
        ClawSetup.Build(f.Options(), useUndo: false);

        string flipId = UrdfPostProcessor.Slugify(f.flip.name);
        string clampId = UrdfPostProcessor.Slugify(f.jawA.name);
        string followerId = UrdfPostProcessor.Slugify(f.jawB.name);

        // --- The two button mechanisms ------------------------------------------------------------
        foreach ((string id, string what) in new[] { (flipId, "flip"), (clampId, "clamp") })
        {
            RobotMechanisms.Mechanism mech = f.registry.Find(id);
            Assert(mech != null, $"the {what} should be registered as a mechanism (id '{id}')");
            Assert(mech.type == RobotMechanisms.TypePneumatic,
                $"the {what} must register as a PNEUMATIC — that's what gives the player the " +
                "1-button/2-button choice for free");
            Assert(mech.pneumatic != null, $"the {what} needs a PneumaticActuator");
        }

        // --- The flip is PACED, the jaws are not ---------------------------------------------------
        // A pneumatic snaps, and a snapped half-turn is over inside one frame — on a claw that is
        // roughly symmetric about its pivot the end pose looks like the start pose, so the flip reads
        // as having done nothing. A jaw keeps the honest snap; there is nothing subtle to see there.
        PneumaticActuator flipAct = f.registry.Find(flipId).pneumatic;
        PneumaticActuator clampAct = f.registry.Find(clampId).pneumatic;
        AssertApprox(flipAct.travelSeconds, FlipSeconds, 0.001f,
            "the flip should travel over the time the form asked for");
        Assert(clampAct.travelSeconds <= 0f,
            "the jaws must keep the instant pneumatic snap — only the flip is paced");

        ArticulationBody flipBody = f.flip.GetComponent<ArticulationBody>();
        Assert(flipBody != null && flipBody.jointType == ArticulationJointType.RevoluteJoint,
            "the flip must be a revolute joint");
        Assert(flipBody.twistLock == ArticulationDofLock.LimitedMotion,
            "the flip must be limited-motion — a free-spinning flip would never stop at 180");
        AssertApprox(Mathf.Abs(flipBody.xDrive.upperLimit - flipBody.xDrive.lowerLimit), FlipAngle, 0.5f,
            "the flip's travel should span the requested flip angle");

        // --- Which end of the travel counts as "shut" ---------------------------------------------
        // A claw drawn shut opens when its piston fires, so the grab has to happen on the RETRACT
        // stroke. Getting this backwards seizes the piece exactly as the claw lets go of it, which is
        // indistinguishable from a broken grab when you're looking at it in Play.
        ClawGrab openGrab = f.registry.GetComponentInChildren<ClawGrab>(true);
        Assert(openGrab != null, "a claw with grab enabled should have produced a ClawGrab");
        Assert(!openGrab.grabWhenRetracted,
            "a claw modelled OPEN closes when fired, so it must grab on the extend stroke");

        Fixture shut = MakeFixture("ShutBot");
        ClawSetup.Options shutOptions = shut.Options();
        shutOptions.clampModelled = ClawRig.JawRest.ModelledClosed;
        ClawSetup.Build(shutOptions, useUndo: false);
        ClawGrab shutGrab = shut.registry.GetComponentInChildren<ClawGrab>(true);
        Assert(shutGrab != null && shutGrab.grabWhenRetracted,
            "a claw modelled SHUT opens when fired, so it must grab on the retract stroke — otherwise " +
            "it grabs the piece on the way open and drops it on the way closed");

        // --- The mirrored half is a passive linkage, not a second button ---------------------------
        ArticulationBody followerBody = f.jawB.GetComponent<ArticulationBody>();
        Assert(followerBody != null, "the second clamp half should have become a joint");
        Assert(f.jawB.GetComponent<PneumaticActuator>() == null,
            "the second clamp half is coupled, so it must NOT also carry an actuator");
        Assert(f.registry.Find(followerId) == null,
            "the second clamp half must not be registered — ButtonRouter would fight the coupler for " +
            "the drive");
        JointCoupler coupler = f.jawB.GetComponent<JointCoupler>();
        Assert(coupler != null, "the second clamp half should be coupled to the first");
        Assert(coupler.mode == JointCoupler.CoupleMode.Position,
            "clamp halves track each other's ANGLE (a linkage), not speed");
        Assert(coupler.driver == f.jawA.GetComponent<ArticulationBody>(),
            "the second half should follow the driven half");
        Assert(coupler.follower == followerBody, "the coupler should drive its own body");
        Assert(coupler.ratio < 0f,
            $"a mirrored half needs a NEGATIVE ratio so it closes against the other one (got {coupler.ratio})");

        // --- The tree: everything hangs off the flip, so it all turns over together ---------------
        Assert(f.jawA.transform.IsChildOf(f.flip.transform) && f.jawB.transform.IsChildOf(f.flip.transform),
            "both clamp halves must sit under the flip link, or they wouldn't flip with the claw");
        Assert(f.clampRod.transform.IsChildOf(f.flip.transform),
            "the clamp cylinder rides the claw, so it belongs under the flip link");
        Assert(!f.flipRod.transform.IsChildOf(f.flip.transform),
            "the flip cylinder DRIVES the flip, so it must stay on the chassis rather than ride it");

        // --- Cosmetic cylinders: rod out, body back, and always a full stroke apart ---------------
        PneumaticSlideFollower rod = f.clampRod.GetComponent<PneumaticSlideFollower>();
        PneumaticSlideFollower body = f.clampBody.GetComponent<PneumaticSlideFollower>();
        Assert(rod != null && body != null, "both halves of the clamp cylinder need a slide follower");
        Assert(rod.slideUnits > 0f && body.slideUnits < 0f,
            $"the rod must extend and the body recoil the other way (got rod {rod.slideUnits}, " +
            $"body {body.slideUnits})");
        AssertApprox(rod.slideUnits - body.slideUnits, ClampStrokeMm / PneumaticSetup.MmPerUnit, 1e-3f,
            "however the recoil is split, the rod and body must stay one full stroke apart");
        AssertApprox(rod.slideUnits, -body.slideUnits, 1e-3f,
            "at recoil 0.5 the two halves should travel equal and opposite (the balanced case)");
        Assert(rod.progressBody == f.jawA.GetComponent<ArticulationBody>(),
            "the clamp cylinder should read its progress off the driven clamp joint");
        Assert(f.clampRod.GetComponent<ArticulationBody>() == null,
            "a cosmetic cylinder part must not keep a joint — PhysX would fight the follower for it");
        foreach (Collider c in f.clampRod.GetComponentsInChildren<Collider>(true))
            Assert(!c.enabled, "a cosmetic cylinder's colliders must be off — a teleporting collider " +
                               "would fling game pieces");

        // --- Grab ----------------------------------------------------------------------------------
        ClawGrab grab = f.registry.GetComponentInChildren<ClawGrab>(true);
        Assert(grab != null, "the grab should have been wired");
        Assert(grab.clampPneumatic == f.registry.Find(clampId).pneumatic,
            "the grab must ride the CLAMP's piston, so closing the claw is grabbing");
        Assert(grab.holdPoint != null && grab.holdPoint.IsChildOf(f.flip.transform),
            "the hold point must hang off the flip link, or held pieces wouldn't survive a flip");
        Collider mouth = grab.GetComponent<Collider>();
        Assert(mouth != null && mouth.isTrigger, "the claw mouth must be a trigger to catch pieces");
        Assert(grab.passThroughWhileHeld,
            "the built claw should carry the pass-through the form asked for — a grab that catches a " +
            "piece half inside a jaw leaves an overlap the solver can't resolve, which throws the robot");
        Assert(grab.snapSpeed > 0f,
            "a grabbed piece has to EASE to the hold point; pinning it where it was caught is what " +
            "wedges it through the CAD");
        // Only the default is checkable here — the grace itself is a FixedUpdate behaviour, and
        // MonoBehaviours don't run at edit time. Pinned so a future edit can't quietly zero it.
        Assert(grab.releaseGrace > 0f,
            "a dropped piece needs a moment still ignoring the claw, or the jaws it was sitting " +
            "inside kick it away as it goes solid");

        ClawSetup.Options solidOptions = f.Options();
        solidOptions.grabPassThrough = false;
        ClawSetup.Build(solidOptions, useUndo: false);
        Assert(!f.registry.GetComponentInChildren<ClawGrab>(true).passThroughWhileHeld,
            "turning pass-through off must reach the built claw, or a carried piece can't be made " +
            "solid to the field");
        ClawSetup.Build(f.Options(), useUndo: false); // back to the shipped default for the checks below

        // --- Self-collision + buttons ---------------------------------------------------------------
        foreach (GameObject link in new[] { f.flip, f.jawA, f.jawB })
            Assert(link.GetComponent<IgnoreRobotSelfCollision>() != null,
                $"'{link.name}' needs IgnoreRobotSelfCollision — sibling links collide and a rough-CAD " +
                "claw would shove the robot around");

        ButtonMap map = ControllerMapSettings.Load(TestRobotId);
        ButtonAssignment flipAssign = FindAssignment(map, flipId);
        ButtonAssignment clampAssign = FindAssignment(map, clampId);
        Assert(flipAssign != null && clampAssign != null, "both claw functions should have got a button");
        Assert(flipAssign.mode == ControllerMapSettings.ModeToggle &&
               clampAssign.mode == ControllerMapSettings.ModeToggle,
            "a piston defaults to one toggle button, so that's what auto-assign should hand out");
        Assert(flipAssign.button != clampAssign.button,
            "flip and clamp must land on DIFFERENT buttons or one is unreachable");
        Assert(FindAssignment(map, followerId) == null,
            "the coupled half must never be given a button of its own");

        // --- Rebuild without the second half: no orphan left behind ---------------------------------
        ClawSetup.Options fewer = f.Options();
        fewer.clampSections = new List<ClawRig.ClampSection> { fewer.clampSections[0] };
        ClawSetup.Build(fewer, useUndo: false);

        Assert(f.jawB.GetComponent<ArticulationBody>() == null,
            "dropping a clamp half must remove its joint, or it stays a live orphan that still moves");
        Assert(f.jawB.GetComponent<JointCoupler>() == null,
            "dropping a clamp half must remove its coupler");
        Assert(f.registry.Find(clampId) != null,
            "the surviving clamp half should still be registered after a rebuild");

        // --- Strip: back to plain geometry -----------------------------------------------------------
        ClawRig rig = f.registry.GetComponentInChildren<ClawRig>(true);
        Assert(rig != null, "the build should leave a ClawRig record so the claw can be re-edited");
        ClawSetup.Strip(f.registry, rig, useUndo: false);

        Assert(f.registry.Find(flipId) == null && f.registry.Find(clampId) == null,
            "Strip must remove both claw mechanisms from the registry");
        Assert(f.flip.GetComponent<ArticulationBody>() == null &&
               f.jawA.GetComponent<ArticulationBody>() == null,
            "Strip must return the claw's links to plain welded geometry");
        Assert(f.registry.GetComponentInChildren<ClawGrab>(true) == null, "Strip must remove the grab");
        Assert(f.registry.GetComponentInChildren<ClawRig>(true) == null, "Strip must remove its own record");
        foreach (Collider c in f.clampRod.GetComponentsInChildren<Collider>(true))
            Assert(c.enabled, "Strip must give a cosmetic cylinder its colliders back");
        ButtonMap after = ControllerMapSettings.Load(TestRobotId);
        Assert(FindAssignment(after, flipId) == null && FindAssignment(after, clampId) == null,
            "Strip must release the buttons the claw held");

        return "Structure + teardown: PASSED — flip and clamp registered as pistons, the mirrored half " +
               "coupled but unregistered, the tree nested so the claw flips as one, cylinders split " +
               "rod/body travel, a dropped half left no orphan, and Strip undid all of it.";
    }

    // --- Trim: where the jaws SIT before the piston fires -----------------------------------------
    // A CAD model drawn with the jaws already shut is usually shut a little too wide, and there is no
    // way to correct that from the swing angle — that only changes how far they travel, not where they
    // start. The trim shifts the whole travel instead, and the mirrored half has to come with it.
    private static string TrimMovesTheRestPose()
    {
        const float Trim = -10f;
        Fixture f = MakeFixture("TrimBot");
        ClawSetup.Options o = f.Options();
        o.flippingParts = new List<GameObject>();   // clamp-only, so nothing else moves in the read
        o.clampTrimDeg = Trim;
        ClawSetup.Build(o, useUndo: false);

        ArticulationBody driver = f.jawA.GetComponent<ArticulationBody>();
        ArticulationBody follower = f.jawB.GetComponent<ArticulationBody>();
        JointCoupler coupler = f.jawB.GetComponent<JointCoupler>();
        PneumaticActuator piston = f.jawA.GetComponent<PneumaticActuator>();
        Assert(driver != null && follower != null && coupler != null && piston != null,
            "the trimmed fixture should still produce two jaw joints, a coupler and a piston");

        // SHIFTED, not stretched: the same 35 degrees of swing, starting 10 further round.
        AssertApprox(driver.xDrive.lowerLimit, Trim, 0.5f,
            $"a {Trim} trim should carry the jaws' resting pose {-Trim} degrees past the CAD pose");
        AssertApprox(driver.xDrive.upperLimit, CloseAngle + Trim, 0.5f,
            "trimming the rest pose must move the whole travel, not lengthen it");
        AssertApprox(driver.xDrive.upperLimit - driver.xDrive.lowerLimit, CloseAngle, 0.5f,
            "the jaw should still swing exactly its close angle after a trim");
        AssertApprox(piston.retractedTarget, Trim, 0.5f,
            "the trimmed end of the travel is the one the jaws rest at");

        // The mirrored half's trim is mirrored too, and at ratio -1 that cancels to a zero offset —
        // the coupler tracks a driver that is itself already offset.
        AssertApprox(coupler.offsetDeg, 0f, 0.01f,
            $"equal and opposite trims about a ratio of {coupler.ratio:F2} should need no coupler " +
            $"offset, but it came out {coupler.offsetDeg:F2}");

        // Behaviour, not just numbers: let it settle where it rests and read both jaws.
        Physics.gravity = Vector3.zero;
        Physics.simulationMode = SimulationMode.Script;
        piston.Retract();
        for (int step = 0; step < 200; step++) { coupler.ApplyStep(); Physics.Simulate(0.02f); }

        float restDriver = JointAngleDeg(driver);
        float restFollower = JointAngleDeg(follower);
        AssertApprox(restDriver, Trim, 2f,
            $"the driven jaw should rest at the trimmed pose ({Trim}) but sat at {restDriver:F1}");
        AssertApprox(restFollower, -Trim, 2f,
            $"the mirrored jaw should rest at the mirrored trim ({-Trim}) but sat at {restFollower:F1} " +
            "— a trim that only moves one jaw closes the claw crooked");

        // ...and the far end of the travel must have moved by the same amount.
        piston.Extend();
        for (int step = 0; step < 200; step++) { coupler.ApplyStep(); Physics.Simulate(0.02f); }
        float firedDriver = JointAngleDeg(driver);
        AssertApprox(firedDriver, CloseAngle + Trim, 2f,
            $"the fired end should shift with the trim to {CloseAngle + Trim}, but the jaw reached " +
            $"{firedDriver:F1}");

        return $"Clamp trim: PASSED — travel {driver.xDrive.lowerLimit:F0}..{driver.xDrive.upperLimit:F0} " +
               $"degrees, jaws resting at {restDriver:F1} / {restFollower:F1} and firing to {firedDriver:F1}.";
    }

    // --- The flip has to turn the claw over IN PLACE ------------------------------------------------
    // A flip pivot seeded on the first listed part alone put the hinge at one plate's centre while the
    // rest of the assembly hung off to one side, so 180 degrees threw everything to the diametrically
    // opposite place — "it just inverts everything" rather than turning the claw over. The pivot has to
    // sit at the middle of EVERYTHING that flips, jaws included, since those are reparented under it.
    private static string FlipTurnsInPlace()
    {
        Fixture f = MakeFixture("FlipBot");
        // Measured BEFORE the build, while the flip plate is still its own object — afterwards the
        // jaws and the clamp cylinder are its children and its bounds cover them too.
        Vector3 plateCenterBefore = MechanismBuildUtil.BoundsCenterOrOrigin(f.flip);
        ClawSetup.Build(f.Options(), useUndo: false);

        Transform pivot = FindDescendant(f.registry.transform, "ClawFlipPivot");
        Assert(pivot != null, "the build should have created a flip pivot marker");

        // What actually flips is everything under the flip link: the plate, both jaws and the clamp
        // cylinder, all reparented there by the build.
        Bounds assembly = WorldBounds(f.flip);
        assembly.Encapsulate(WorldBounds(f.jawA));
        assembly.Encapsulate(WorldBounds(f.jawB));

        float offset = Vector3.Distance(pivot.position, assembly.center);
        float span = assembly.size.magnitude;
        Assert(offset < span * 0.05f,
            $"the flip pivot sits {offset:F2} units off the middle of what it flips (assembly {span:F2} " +
            "across) — half a turn about a pivot that far out swings the whole claw to the other side " +
            "instead of turning it over where it stands");

        // The rest of the assembly is the reason: seeding on the flip plate alone lands somewhere else
        // entirely, so prove the two really differ here rather than passing by coincidence.
        float plateOnly = Vector3.Distance(plateCenterBefore, assembly.center);
        Assert(plateOnly > span * 0.05f,
            "this fixture can't detect the bug: its flip plate's centre already coincides with the " +
            "whole assembly's, so seeding on either would pass");

        // And the hold point belongs in the JAWS' opening, not at the centroid of the claw plus mount.
        ClawGrab grab = f.registry.GetComponentInChildren<ClawGrab>(true);
        Bounds jawsOnly = WorldBounds(f.jawA);
        jawsOnly.Encapsulate(WorldBounds(f.jawB));
        float holdOffset = Vector3.Distance(grab.holdPoint.position, jawsOnly.center);
        Assert(holdOffset < jawsOnly.size.magnitude * 0.25f,
            $"the hold point sits {holdOffset:F2} units from the middle of the jaws — a piece would be " +
            "carried inside the claw body rather than in its mouth");

        return $"Flip geometry: PASSED — pivot {offset:F2} units off the flipping assembly's middle " +
               $"(vs {plateOnly:F2} for the flip plate alone), hold point {holdOffset:F2} from the jaws.";
    }

    private static Bounds WorldBounds(GameObject go)
        => MechanismBuildUtil.TryBounds(go, out Bounds b) ? b : new Bounds(go.transform.position, Vector3.zero);

    private static Transform FindDescendant(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    // --- Carrying a piece: it has to land where you're looking, standing the right way up ----------
    // Two failures the claw shipped with, both invisible to a structural check:
    //   * the field's pieces keep the CAD origin as their pivot, 9-15 units off their mesh, so aiming
    //     the PIVOT at the claw teleported the visible piece across the field;
    //   * the pins share one mesh at a different child rotation per instance, so a pin lying where the
    //     match loader dropped it was carried lying down while an upright one was carried upright.
    // Both are pure geometry, so they can be exercised without Play — which is just as well, since
    // ClawGrab's FixedUpdate never runs at edit time.
    private static string CarriedPiecesLandRight()
    {
        Fixture f = MakeFixture("CarryBot");
        // The claw's CAD is rotated, like every real one: this is what put the hold point's own +Y
        // sideways and carried every grabbed pin lying down. An axis-aligned fixture cannot see it —
        // there, the marker's up IS up and any formula looks right. (Scale made uniform first, so
        // rotating it doesn't shear everything reparented underneath.)
        f.flip.transform.localScale = Vector3.one * 2f;
        f.flip.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
        ClawSetup.Build(f.Options(), useUndo: false);
        ClawGrab grab = f.registry.GetComponentInChildren<ClawGrab>(true);
        Assert(grab != null && grab.holdPoint != null, "the build should have wired a grab and hold point");
        Assert(grab.autoUpright,
            "the built claw should carry the Stand-pieces-up the form asked for — without it a pin " +
            "from the match loader is carried lying down while an upright one looks fine");

        // A pin whose mesh hangs well off its pivot, lying on its side like a match-loaded one.
        GameObject pin = MakeBox(null, "PinPiece", new Vector3(40f, 5f, 0f), Vector3.one);
        pin.transform.localScale = new Vector3(4f, 0.5f, 0.5f);   // long axis is local X
        GameObject pivotRoot = new GameObject("PinPivot");
        pivotRoot.transform.position = new Vector3(28f, 5f, 0f);  // pivot 12 units off the mesh
        pin.transform.SetParent(pivotRoot.transform, worldPositionStays: true);
        Rigidbody rb = pivotRoot.AddComponent<Rigidbody>();
        rb.useGravity = false;

        // Physics has to have stepped once for the auto-computed center of mass to be readable.
        Physics.simulationMode = SimulationMode.Script;
        Physics.gravity = Vector3.zero;
        Physics.Simulate(0.02f);

        float comOffset = rb.centerOfMass.magnitude;
        Assert(comOffset > 1f,
            $"this fixture can't detect the pivot bug: its piece's center sits only {comOffset:F2} " +
            "units off the pivot, so aiming either would look the same");

        // ClawGrab is ASKED which way up is rather than the formula being written out a second time.
        // The old version of this test re-derived it as holdPoint.up and then asserted the piece stood
        // along holdPoint.up — true by construction, and blind to that being sideways.
        Vector3 up = grab.UprightWorldDir();
        float offRobotUp = Vector3.Angle(up, f.registry.transform.up);
        Assert(offRobotUp < 1f,
            $"the claw stands pieces {offRobotUp:F0} degrees off the robot's up — the hold point " +
            "inherits the claw's CAD rotation, so reading its own +Y carries every piece lying down");

        // Where the carry math puts it — asked of ClawGrab, not restated: stand the long axis up, then
        // place the PIVOT so the piece's CENTER lands on the hold point.
        Vector3 localCom = rb.centerOfMass;
        Vector3 longAxisWorld = pin.transform.right;            // the mesh's longest side, right now
        Vector3 localUpAxis = (Quaternion.Inverse(rb.rotation) * longAxisWorld).normalized;
        Quaternion carried = grab.StandUpRotation(rb.rotation, localUpAxis);
        Vector3 placed = grab.holdPoint.position - carried * localCom;

        // 1. The MESH lands on the hold point, not the pivot.
        Vector3 centerAfter = placed + carried * localCom;
        float miss = Vector3.Distance(centerAfter, grab.holdPoint.position);
        Assert(miss < 0.01f,
            $"the carried piece's centre missed the hold point by {miss:F2} units — the claw is aiming " +
            "the piece's pivot, so an off-pivot piece appears to teleport away when grabbed");

        // 2. And it ends up STANDING, however it was lying when grabbed.
        float tilt = Vector3.Angle(carried * localUpAxis, f.registry.transform.up);
        Assert(tilt < 1f,
            $"a piece grabbed lying down stayed {tilt:F0} degrees off the robot's up — pins from the " +
            "match loader would be carried sideways while upright ones look fine");

        // 3. Every piece is stood up the SHORT way round, from wherever it was caught.
        //
        // Which END of the mesh the measured axis points at is whatever the modeller drew, so it can't
        // be taken on trust — doing so turned anything drawn the other way end-for-end, and a stack
        // grabbed pin-on-top, cup-below came into the claw cup-on-top, pin-below. Nor may the caught
        // attitude simply be discarded and re-picked: a piece already close to vertical should barely
        // move. The 180 and 150 rows are the ones that fail if the sign is trusted.
        Quaternion upright = Quaternion.FromToRotation(localUpAxis, f.registry.transform.up);
        foreach ((float caughtLean, float expectedSwing, string what) in new[]
                 {
                     (0f,   0f,  "already standing"),
                     (180f, 0f,  "standing, but with its mesh axis drawn the other way up"),
                     (30f,  30f, "leaning 30 degrees"),
                     (150f, 30f, "caught 150 degrees over — 30 from vertical the other way"),
                 })
        {
            Quaternion caught = Quaternion.AngleAxis(caughtLean, f.registry.transform.forward) * upright;
            float swung = Quaternion.Angle(caught, grab.StandUpRotation(caught, localUpAxis));
            AssertApprox(swung, expectedSwing, 1f,
                $"a piece {what} was turned {swung:F0} degrees to stand it up rather than " +
                $"{expectedSwing:F0} — either the long axis's sign is being trusted, which lands a " +
                "grabbed stack upside down, or the attitude it was caught in is being thrown away");
        }

        // 4. The carried attitude is stored in the HOLD POINT's frame, which is what makes a held piece
        // rigid to the claw: turn the claw and the piece turns WITH it, same axis, same amount. Solved
        // live instead, the shortest-arc answer moves as the claw does and pieces spin about whatever
        // axis it lands on — while the jaws they are supposedly clamped in go somewhere else.
        Quaternion holdLocal = grab.CarriedHoldLocalRotation(rb.rotation, localUpAxis);
        Transform flipTf = grab.holdPoint.parent != null ? grab.holdPoint.parent : grab.holdPoint;
        Quaternion beforeFlip = flipTf.rotation;
        Quaternion halfTurn = Quaternion.AngleAxis(180f, f.registry.transform.forward);
        flipTf.rotation = halfTurn * beforeFlip;
        Quaternion afterFlip = grab.holdPoint.rotation * holdLocal;    // what the carry replays
        flipTf.rotation = beforeFlip;
        float rides = Quaternion.Angle(afterFlip, halfTurn * carried);
        Assert(rides < 1f,
            $"turning the claw over moved the held piece {rides:F0} degrees away from turning over " +
            "with it — a clamped piece has to ride the jaws rigidly, not be re-solved each step");

        // 5. A STACK straddles the hold point instead of hanging off one side of it.
        //
        // Which piece the claw catches first is arbitrary, and the rest are arranged around it. Put
        // THAT piece on the hold point and the stack is lopsided about the point the flip turns it
        // around, so the far piece swings through the stack's whole height — grab the bottom of a
        // cup-on-pin and the top went into the floor. The flip sweeps a piece through twice its
        // distance from the hold point, so balancing the pair halves the excursion.
        const float StackHeight = 6f;
        var evenOffsets = new List<Vector3> { Vector3.zero, new Vector3(0f, StackHeight, 0f) };
        Vector3 middle = ClawGrab.StackCenterLocal(new List<float> { 1f, 1f }, evenOffsets);
        float worst = Mathf.Max((evenOffsets[0] - middle).magnitude, (evenOffsets[1] - middle).magnitude);
        AssertApprox(worst, StackHeight * 0.5f, 0.01f,
            $"the far piece of a {StackHeight}-unit stack sits {worst:F1} units from the hold point, so " +
            $"a flip swings it {worst * 2f:F0} — the stack is being hung off one piece rather than " +
            "balanced about its combined centre of mass");

        // And it is genuinely mass-weighted, so a heavy piece pulls the balance toward itself rather
        // than the pair simply being split down the middle.
        Vector3 heavy = ClawGrab.StackCenterLocal(new List<float> { 3f, 1f }, evenOffsets);
        AssertApprox(heavy.y, StackHeight * 0.25f, 0.01f,
            "a piece three times the mass of its neighbour should pull the balance point three " +
            "quarters of the way toward itself");

        // A lone piece must still land exactly on the hold point — the single-piece case is the one
        // that was already right, and balancing must not have shifted it.
        Vector3 alone = ClawGrab.StackCenterLocal(new List<float> { 1f }, new List<Vector3> { Vector3.zero });
        Assert(alone.magnitude < 0.001f, "one piece on its own belongs on the hold point, unshifted");

        return $"Carried pieces: PASSED — on a claw rotated 90 degrees off the robot, a piece whose " +
               $"centre is {comOffset:F1} units off its pivot lands within {miss:F3} of the hold " +
               $"point and stands within {tilt:F1} degrees of the robot's up; pieces are stood up the " +
               "short way round whichever end their mesh axis points at, a held one turns over with " +
               $"the jaws, and a {StackHeight}-unit stack balances {worst:F1} either side of the hold " +
               "point instead of hanging off one end.";
    }

    // --- Axes: the explicit pickers must override the guess -----------------------------------------
    // Auto has now been wrong twice on real robots, in both directions, because how a claw is mounted
    // is not inferable from its geometry. What makes that survivable is being able to point at a
    // coloured gizmo arrow and say "that one" — so the scene axes must resolve to the SCENE's axes
    // even on a robot that isn't facing down them, which is precisely when the robot presets differ.
    private static string ExplicitAxesMeanWhatTheySay()
    {
        Fixture f = MakeFixture("AxisBot");
        // Turn the robot off the world axes, and the part off both. Nothing is simulated here — this
        // is the axis MATH, which is where the bug lived.
        f.registry.transform.rotation = Quaternion.Euler(0f, 37f, 0f);
        f.jawA.transform.rotation = Quaternion.Euler(23f, 47f, 11f);

        Vector3 sceneX = AxisWorld(f.jawA, ClawRig.HingeAxis.WorldX);
        Vector3 sceneY = AxisWorld(f.jawA, ClawRig.HingeAxis.WorldY);
        Vector3 sceneZ = AxisWorld(f.jawA, ClawRig.HingeAxis.WorldZ);
        AssertApprox(Mathf.Abs(Vector3.Dot(sceneX, Vector3.right)), 1f, 0.01f,
            "'Scene X (red)' must hinge about the scene's red arrow, whatever frame the part is in");
        AssertApprox(Mathf.Abs(Vector3.Dot(sceneY, Vector3.up)), 1f, 0.01f,
            "'Scene Y (green)' must hinge about the scene's green arrow");
        AssertApprox(Mathf.Abs(Vector3.Dot(sceneZ, Vector3.forward)), 1f, 0.01f,
            "'Scene Z (blue)' must hinge about the scene's blue arrow");

        // The whole point of offering both frames: on a robot at an angle they are NOT the same, so
        // picking the scene axis is a real override rather than a differently-worded guess.
        Vector3 robotForward = AxisWorld(f.jawA, ClawRig.HingeAxis.RollsSideways);
        float apart = Vector3.Angle(robotForward, sceneZ);
        Assert(apart > 30f,
            $"on a robot yawed 37 degrees the robot's front/back line and the scene's blue axis should " +
            $"differ by about that much, but they came out {apart:F1} apart — the scene presets are " +
            "resolving against the robot, so there is no way to override a wrong guess");

        // And the flip's guess is the robot's front/back line, which is what a claw hanging off the
        // side of a bot actually needs. Pinned because it has been changed under fire before.
        Vector3 flipAuto = AxisWorld(f.jawA, ClawRig.HingeAxis.Auto, isFlip: true);
        AssertApprox(Mathf.Abs(Vector3.Dot(flipAuto, robotForward)), 1f, 0.01f,
            "the flip's Auto should be the robot's front/back line");
        Vector3 clampAuto = AxisWorld(f.jawA, ClawRig.HingeAxis.Auto, isFlip: false);
        AssertApprox(Mathf.Abs(Vector3.Dot(clampAuto, f.registry.transform.up)), 1f, 0.01f,
            "the clamp's Auto should be the robot's own vertical, so the jaws close inward");

        return $"Axis presets: PASSED — scene X/Y/Z resolve to the gizmo arrows on a part at " +
               $"(23,47,11) under a robot yawed 37, and sit {apart:F0} degrees off the robot presets.";
    }

    // The world-space hinge line a preset resolves to for `link`, which is what the player sees it
    // turn about.
    private static Vector3 AxisWorld(GameObject link, ClawRig.HingeAxis preset, bool isFlip = false)
    {
        ClawSetup.ResolveAxisAnchor(link, null, preset, Vector3.right, isFlip,
            out Vector3 axisLocal, out _);
        return link.transform.TransformDirection(axisLocal).normalized;
    }

    // --- Level-keeper: a claw on a rotating arm has to keep its orientation as the arm sweeps ---------
    // The whole feature is a sign problem two ways over — the counter axis has to be the arm's OWN line
    // (not its negative, or ratio -1 doubles the tumble instead of cancelling it), and the ratio has to
    // be negative. Neither is visible in a number; both fail the same way (the claw tumbles). And an
    // axis-aligned fixture hides both, the same way it hid the carry bugs. So the robot is tilted off
    // every world axis, the arm is actually swept under physics, and the shipped coupler — not a restated
    // formula — is asked to hold the claw level. The in-test mutation at the end proves the check bites.
    private static string LevelKeeperStaysLevel()
    {
        ArmFixture f = MakeSwingArmClaw("ArmBot", flipPastMidpoint: false);
        RobotMechanisms registry = f.registry;
        GameObject arm = f.arm, mount = f.mount, flip = f.flip;
        ArticulationBody armBody = f.armBody, mountBody = f.mountBody;
        JointCoupler coupler = f.coupler;

        // --- Structure: the mount is a passive counter-rotating link between the arm and the claw ----
        Assert(mountBody != null && mountBody.jointType == ArticulationJointType.RevoluteJoint,
            "the mount should have become a revolute joint");
        Assert(mount.transform.parent == arm.transform,
            "the mount must be pulled up to be a DIRECT child of the arm even when the CAD nested it " +
            "inside the flip assembly — otherwise it's a leaf that flips alone while the claw stays rigid");
        Assert(flip.transform.IsChildOf(mount.transform),
            "the claw (flip link) must ride the mount, or it wouldn't be kept level");
        Assert(f.jawA.transform.IsChildOf(mount.transform) && f.jawB.transform.IsChildOf(mount.transform),
            "the JAWS must end up under the mount too, or they stay rigid to the arm (the reported bug)");
        Assert(mount.GetComponent<PneumaticActuator>() == null && mount.GetComponent<MotorActuator>() == null,
            "the mount is a passive linkage — an actuator would let ButtonRouter fight the coupler");
        Assert(registry.Find(UrdfPostProcessor.Slugify(mount.name)) == null,
            "the mount must not be registered as a mechanism (it rides no button)");
        Assert(coupler != null && coupler.mode == JointCoupler.CoupleMode.Position,
            "the mount should track the arm's ANGLE through a Position coupler");
        Assert(coupler.driver == armBody && coupler.follower == mountBody,
            "the coupler should read the arm and drive the mount");
        Assert(coupler.ratio < 0f,
            $"the counter-rotation needs a NEGATIVE ratio to cancel the arm (got {coupler.ratio})");
        Assert(!coupler.flipPastMidpoint, "the plain level-keeper must not flip unless it's asked to");

        // The pivot must sit on the CLAW's centre, not the mount's own origin — else leveling swings the
        // claw off into the scene, because the claw's offset from an off-claw pivot rotates opposite the
        // arm. Here the mount sits ~1.5u from the claw's middle, so the two are distinguishable.
        Vector3 pivotWorld = mountBody.transform.TransformPoint(mountBody.anchorPosition);
        Bounds clawB = WorldBounds(flip);
        clawB.Encapsulate(WorldBounds(f.jawA));
        clawB.Encapsulate(WorldBounds(f.jawB));
        float pivToClaw = Vector3.Distance(pivotWorld, clawB.center);
        float pivToMountOrigin = Vector3.Distance(pivotWorld, mount.transform.position);
        Assert(pivToClaw < pivToMountOrigin,
            $"the level pivot is nearer the mount's origin ({pivToMountOrigin:F1}u) than the claw's centre " +
            $"({pivToClaw:F1}u) — it must hinge about the claw so the claw spins in place, not swing off " +
            "into the scene as it levels");

        // --- Behaviour: sweep the arm and watch the claw hold its orientation ------------------------
        // The arm is RAMPED to each checkpoint, the way a motor drives it — a stiff position target
        // slammed on in one step rings the two coupled drives into instability (and isn't how the real
        // thing moves). Small dt for the same reason.
        Physics.gravity = Vector3.zero;
        Physics.simulationMode = SimulationMode.Script;

        SettleArm(armBody, coupler, 0f, 150);
        Quaternion mountAtRest = mountBody.transform.rotation;
        Vector3 clawAtRest = flip.transform.position;

        float worstTilt = 0f, farthest = 0f, current = 0f;
        foreach (float checkpoint in new[] { 45f, 90f, 135f, 180f })
        {
            for (; current < checkpoint; current += 1.5f)   // ramp smoothly, like the motor would
            {
                DriveArmTo(armBody, current);
                for (int s = 0; s < 4; s++) { coupler.ApplyStep(); Physics.Simulate(0.01f); }
            }
            SettleArm(armBody, coupler, checkpoint, 400);    // then let it settle firmly at the checkpoint

            float armReached = JointAngleDeg(armBody);
            Assert(Mathf.Abs(armReached - checkpoint) < 8f,
                $"the arm never settled at {checkpoint} (sat at {armReached:F0}) — the sweep isn't " +
                "happening, so the test can't say anything about staying level");
            worstTilt = Mathf.Max(worstTilt, Quaternion.Angle(mountBody.transform.rotation, mountAtRest));
            farthest = Mathf.Max(farthest, Vector3.Distance(flip.transform.position, clawAtRest));
        }

        // The claw must have RIDDEN the arc — otherwise "stays level" is vacuous (a frozen arm keeps its
        // orientation for free).
        Assert(farthest > 1f,
            $"the claw moved only {farthest:F2} units as the arm swept, so it isn't riding the arm — a " +
            "stationary claw stays level trivially and proves nothing");
        // ...and at every checkpoint through the 180 sweep it held the orientation it started at.
        Assert(worstTilt < 5f,
            $"the claw tilted {worstTilt:F1} degrees as the arm swept 180 — the mount isn't cancelling " +
            "the arm's rotation, so a claw picked up at the front arrives crooked at the back");

        // --- Mutation guard: prove the -1 is load-bearing --------------------------------------------
        // If tracking the arm 1:1 the SAME way (ratio +1) left the claw just as level, this test would be
        // blind to a broken coupler. It must instead tumble — hard.
        coupler.ratio = 1f;
        coupler.BakeDrive();
        for (current = JointAngleDeg(armBody); current > 45f; current -= 1.5f)   // ramp back down smoothly
        {
            DriveArmTo(armBody, current);
            for (int s = 0; s < 4; s++) { coupler.ApplyStep(); Physics.Simulate(0.01f); }
        }
        SettleArm(armBody, coupler, 45f, 200);
        float mutantTilt = Quaternion.Angle(mountBody.transform.rotation, mountAtRest);
        Assert(mutantTilt > 30f,
            $"tracking the arm 1:1 the wrong way only tilted the claw {mutantTilt:F0} degrees — the " +
            "stays-level check can't tell a working level-keeper from a broken one");

        return $"Level-keeper: PASSED — on a robot tilted (17,40,13), the claw rode the arm {farthest:F0} " +
               $"units through a 180 sweep while holding its orientation to within {worstTilt:F1} degrees; " +
               $"reversing the coupler tumbled it {mutantTilt:F0} degrees, so the check has teeth.";
    }

    // --- Midpoint flip: past the top of the swing the claw must snap to face the other way -----------
    // The counter-rotation keeps the claw level; this option adds a 180 once the arm is past the midpoint,
    // for a claw that grabs at the front and must present the other face at the back. The proof is a
    // before/after read across the midpoint; the mutation is the SAME sweep with the flip OFF, which must
    // NOT flip — otherwise the test would pass on a claw that never had the feature.
    private static string LevelKeeperFlipsPastMidpoint()
    {
        ArmFixture f = MakeSwingArmClaw("FlipBot", flipPastMidpoint: true);
        JointCoupler coupler = f.coupler;
        ArticulationBody armBody = f.armBody, mountBody = f.mountBody;
        Assert(coupler != null && coupler.flipPastMidpoint,
            "the flip option should have reached the built coupler");
        AssertApprox(coupler.flipDegrees, 180f, 1e-3f, "the flip should be the 180 that was asked for");

        Physics.gravity = Vector3.zero;
        Physics.simulationMode = SimulationMode.Script;

        // Just before the midpoint (arm 80 of 0..180, fraction ~0.44): the flip hasn't fired, still level.
        RampArm(armBody, coupler, 0f, 80f);
        SettleArm(armBody, coupler, 80f, 300);
        Quaternion beforeMid = mountBody.transform.rotation;

        // Well past the midpoint (arm 160, fraction ~0.89): the claw has snapped ~180 from that pose.
        RampArm(armBody, coupler, 80f, 160f);
        SettleArm(armBody, coupler, 160f, 400);
        float flipped = Quaternion.Angle(mountBody.transform.rotation, beforeMid);
        Assert(flipped > 150f,
            $"past the midpoint the claw turned only {flipped:F0} degrees from its pre-midpoint pose — the " +
            "180 flip isn't firing, so a claw grabbed at the front faces the wrong way at the back");

        // Mutation: the SAME sweep with the flip OFF must leave the claw level across the midpoint (the
        // level-keeper alone holds one orientation), so the jump above is genuinely the flip.
        ArmFixture nf = MakeSwingArmClaw("NoFlipBot", flipPastMidpoint: false);
        RampArm(nf.armBody, nf.coupler, 0f, 80f);
        SettleArm(nf.armBody, nf.coupler, 80f, 300);
        Quaternion nfBefore = nf.mountBody.transform.rotation;
        RampArm(nf.armBody, nf.coupler, 80f, 160f);
        SettleArm(nf.armBody, nf.coupler, 160f, 400);
        float nfMoved = Quaternion.Angle(nf.mountBody.transform.rotation, nfBefore);
        Assert(nfMoved < 15f,
            $"with the flip OFF the claw still swung {nfMoved:F0} degrees across the midpoint — the flip " +
            "test would then pass on a claw that never had the feature");

        return $"Midpoint flip: PASSED — past the midpoint the claw snapped {flipped:F0} degrees to face " +
               $"the other way, while the same sweep with the flip off moved only {nfMoved:F0}.";
    }

    // --- Yaw wrist: keep level, face the OTHER way (option B) --------------------------------------
    // With a wrist part the midpoint flip must ride a SEPARATE joint about VERTICAL, so the claw stays
    // level (up preserved) and yaws 180 to face the opposite way — instead of tipping over on the
    // leveling joint (a pitch, which flips the up). The proof: the claw's UP holds across the midpoint
    // while its facing reverses. The mutation is the no-wrist build, whose up FLIPS (a pitch).
    private static string LevelKeeperYawsWithWrist()
    {
        ArmFixture f = MakeSwingArmClaw("YawBot", flipPastMidpoint: true, withWrist: true);
        Assert(f.wristBody != null && f.wristCoupler != null,
            "the wrist part should have become a joint with a coupler");
        Assert(f.flip.transform.IsChildOf(f.wrist.transform),
            "the claw must ride the WRIST, so the yaw carries it");
        Assert(f.wrist.transform.IsChildOf(f.mount.transform),
            "the wrist must ride the MOUNT, so the leveling carries both");
        Assert(!f.coupler.flipPastMidpoint,
            "with a wrist present the MOUNT must not flip — it only levels; the wrist yaws");
        Assert(f.wristCoupler.flipPastMidpoint && Mathf.Abs(f.wristCoupler.ratio) < 1e-3f,
            "the wrist must yaw (flip on) without tracking the arm (ratio 0)");

        Physics.gravity = Vector3.zero;
        Physics.simulationMode = SimulationMode.Script;

        for (int s = 0; s < 150; s++) { f.coupler.ApplyStep(); f.wristCoupler.ApplyStep(); Physics.Simulate(0.01f); }
        Quaternion clawAtRest = f.flip.transform.rotation;
        Vector3 upAtRest = f.flip.transform.up;

        // Before the midpoint (arm 60 of 0..180, fraction 0.33): level, not yet yawed.
        RampBoth(f, 0f, 60f); SettleBoth(f, 60f, 300);
        float unyawed = Quaternion.Angle(f.flip.transform.rotation, clawAtRest);
        Assert(unyawed < 12f,
            $"before the midpoint the claw turned {unyawed:F0} degrees — it should still face its start way");

        // After the midpoint (arm 150): the claw must have turned ~180 to face the other way, AND kept its
        // UP (a yaw preserves the claw's up; a pitch would flip it). Those two ARE option B — measured off
        // the claw's own up, not the robot's, so a tilted mount doesn't muddy the read.
        RampBoth(f, 60f, 150f); SettleBoth(f, 150f, 400);
        float fromStart = Quaternion.Angle(f.flip.transform.rotation, clawAtRest);
        float upTilt = Vector3.Angle(f.flip.transform.up, upAtRest);
        Assert(fromStart > 150f, $"the claw only turned {fromStart:F0} degrees — it didn't face the other way");
        Assert(upTilt < 15f,
            $"the claw's up tilted {upTilt:F0} degrees across the midpoint — a yaw keeps it level; this is " +
            "tipping over (a pitch) instead");

        // Mutation: the SAME flip with NO wrist is a pitch on the mount, which FLIPS the up (not a yaw) —
        // so the up-preserved check above genuinely distinguishes a yaw from a tip-over.
        ArmFixture pf = MakeSwingArmClaw("PitchBot", flipPastMidpoint: true, withWrist: false);
        for (int s = 0; s < 150; s++) { pf.coupler.ApplyStep(); Physics.Simulate(0.01f); }
        Vector3 pfUpRest = pf.flip.transform.up;
        RampArm(pf.armBody, pf.coupler, 0f, 150f); SettleArm(pf.armBody, pf.coupler, 150f, 400);
        float pitchUpTilt = Vector3.Angle(pf.flip.transform.up, pfUpRest);
        Assert(pitchUpTilt > 90f,
            $"without a wrist the flip only tilted the up {pitchUpTilt:F0} degrees — the up-preserved check " +
            "would then pass on a claw that tips over, so it wouldn't prove the wrist yaws");

        return $"Yaw wrist: PASSED — past the midpoint the claw turned {fromStart:F0} degrees to face the " +
               $"other way while its up held to {upTilt:F0} degrees (level, a yaw); the no-wrist pitch " +
               $"tipped the up {pitchUpTilt:F0} degrees, so the check bites.";
    }

    // --- Explicit axle: the mount must pivot about the AXLE the claw hangs on, not its own far origin ----
    // The reported "huge arch" was the counter-rotation hinging about a point out near the robot's centre
    // (the mount's CAD origin), so the claw swept a robot-radius arc instead of turning in place at the end
    // of the arm. Pointing the level-keeper at the axle part pins the rotation centre onto the shaft the
    // claw really pivots on. Proof: the built pivot lands ON the axle, and through a whole 180 sweep the
    // claw holds a fixed distance from it — it rides WITH the arm's end while it levels. Mutation: force the
    // pivot onto the robot's centre (the exact bug) and that distance must blow up.
    private static string LevelKeeperRidesTheAxle()
    {
        ArmFixture f = MakeSwingArmClaw("AxleBot", flipPastMidpoint: false, withAxle: true);
        ArticulationBody armBody = f.armBody, mountBody = f.mountBody;
        JointCoupler coupler = f.coupler;
        Assert(f.axle != null, "the axle fixture should have created an axle part");

        // Static: the built rotation centre sits ON the axle, and the axle is well clear of the mount's own
        // origin (or 'pivots on the axle' rather than 'on its origin' would prove nothing).
        Vector3 pivotWorld = mountBody.transform.TransformPoint(mountBody.anchorPosition);
        ChainBuilder.TryAxleWorldAxis(f.axle, out _, out Vector3 axleCentre);
        float pivToAxle = Vector3.Distance(pivotWorld, axleCentre);
        float axleToMountOrigin = Vector3.Distance(axleCentre, mountBody.transform.position);
        Assert(pivToAxle < 0.2f,
            $"the mount's rotation centre is {pivToAxle:F2}u off the axle it was told to pivot on — the " +
            "axle input isn't pinning the pivot, so the claw would still arc about the wrong point");
        Assert(axleToMountOrigin > 0.5f,
            $"the fixture's axle sits only {axleToMountOrigin:F1}u from the mount's origin — they must " +
            "differ, or landing the pivot on the axle proves nothing over the old behaviour");

        // Behaviour: sweep the arm; the claw must hold a fixed distance from the axle (stay at the end of
        // the arm) AND stay level, while actually riding the arc (or 'fixed distance' is vacuous on a frozen
        // claw).
        Physics.gravity = Vector3.zero;
        Physics.simulationMode = SimulationMode.Script;
        SettleArm(armBody, coupler, 0f, 150);
        Quaternion clawAtRest = f.flip.transform.rotation;
        Vector3 clawStart = f.flip.transform.position;
        float restGap = Vector3.Distance(f.flip.transform.position, f.axle.transform.position);
        float minGap = restGap, maxGap = restGap, worstTilt = 0f, farthest = 0f, current = 0f;
        foreach (float checkpoint in new[] { 45f, 90f, 135f, 180f })
        {
            RampArm(armBody, coupler, current, checkpoint); current = checkpoint;
            SettleArm(armBody, coupler, checkpoint, 400);
            Assert(Mathf.Abs(JointAngleDeg(armBody) - checkpoint) < 8f,
                $"the arm never settled at {checkpoint} (sat at {JointAngleDeg(armBody):F0}) — the sweep " +
                "isn't happening, so the test can't say anything");
            float gap = Vector3.Distance(f.flip.transform.position, f.axle.transform.position);
            minGap = Mathf.Min(minGap, gap); maxGap = Mathf.Max(maxGap, gap);
            worstTilt = Mathf.Max(worstTilt, Quaternion.Angle(f.flip.transform.rotation, clawAtRest));
            farthest = Mathf.Max(farthest, Vector3.Distance(f.flip.transform.position, clawStart));
        }
        Assert(farthest > 1f,
            $"the claw moved only {farthest:F2}u as the arm swept — it isn't riding the arm, so holding a " +
            "fixed distance to the axle proves nothing");
        float wobble = maxGap - minGap;
        Assert(wobble < 1f,
            $"the claw's distance to the axle swung by {wobble:F1}u over the 180 sweep — it isn't staying " +
            "at the end of the arm, it's arcing away on its own");
        Assert(worstTilt < 5f,
            $"the claw tilted {worstTilt:F1} degrees — the axle-hinged mount isn't keeping it level");

        // Mutation: same claw, but hinge about the robot's centre (the reported bug). The claw must arc
        // away — its distance to the axle at the arm's end must vary wildly where the good one held steady.
        ArmFixture m = MakeSwingArmClaw("AxleMutant", flipPastMidpoint: false, withAxle: true,
            forceLevelPivotAtRootCentre: true);
        SettleArm(m.armBody, m.coupler, 0f, 150);
        float mMin = Vector3.Distance(m.flip.transform.position, m.axle.transform.position);
        float mMax = mMin, mc = 0f;
        foreach (float checkpoint in new[] { 90f, 180f })
        {
            RampArm(m.armBody, m.coupler, mc, checkpoint); mc = checkpoint;
            SettleArm(m.armBody, m.coupler, checkpoint, 300);
            float gap = Vector3.Distance(m.flip.transform.position, m.axle.transform.position);
            mMin = Mathf.Min(mMin, gap); mMax = Mathf.Max(mMax, gap);
        }
        float mutantWobble = mMax - mMin;
        Assert(mutantWobble > 2f,
            $"hinging about the robot centre only wobbled the claw {mutantWobble:F1}u — the stay-attached " +
            "check can't tell the axle pivot from the broken one it exists to catch");

        return $"Axle pivot: PASSED — the mount hinged on the axle ({pivToAxle:F2}u off it, the axle itself " +
               $"{axleToMountOrigin:F1}u from the mount origin); the claw rode the arm {farthest:F0}u yet " +
               $"held its distance to the arm's end to within {wobble:F2}u while staying level to " +
               $"{worstTilt:F1} degrees, and hinging about the robot centre instead blew that out to " +
               $"{mutantWobble:F1}u.";
    }

    // --- The 180 turn rides the axle: welded to the arm's end, pivoting and rolling about the shaft ------
    // Follow-ups the user asked for after the axle landed: (a) the axle metal must stay bolted to the end
    // of the arm through the swing — it was drifting "above the arm" because it hung on the counter-rotating
    // mount; (b) the 180 must pivot ON that axle; (c) and roll about the axle's own shaft, not the old yaw
    // about vertical (a single-axle arm has no vertical pin to yaw on). Built with the axle nested ON THE
    // MOUNT and off vertical, so each fix has an adversarial starting point: skip the weld and the axle
    // stays under the mount; seed the old pivot and it lands on the claw's middle; keep the old axis and it
    // reads vertical — every assertion below then flips.
    private static string FlipRollsAboutTheAxle()
    {
        ArmFixture f = MakeSwingArmClaw("RollBot", flipPastMidpoint: true, withWrist: true, withAxle: true,
            axleUnderMount: true);
        Assert(f.axle != null && f.wristBody != null, "the fixture should have an axle and a wrist joint");

        // (a) Welded to the ARM — a DIRECT child of the arm, pulled out of the mount it was nested on.
        Assert(f.axle.transform.parent == f.arm.transform,
            $"the axle stayed under '{f.axle.transform.parent?.name}', not the arm — it must weld to the arm " +
            "so it rides the arm's end instead of drifting off with the counter-rotating mount");

        // (b) The 180 pivots ON the axle.
        Vector3 turnPivot = f.wristBody.transform.TransformPoint(f.wristBody.anchorPosition);
        ChainBuilder.TryAxleWorldAxis(f.axle, out Vector3 axleAxis, out Vector3 axleCentre);
        float pivToAxle = Vector3.Distance(turnPivot, axleCentre);
        Assert(pivToAxle < 0.2f,
            $"the 180 pivots {pivToAxle:F2}u off the axle — it must turn on the axle, not the claw's middle");

        // (c) ...and rolls about the axle's OWN shaft, not the vertical (the old yaw the arm can't do).
        Vector3 turnAxis = ClawSetup.DriverWorldTwist(f.wristBody);
        float toAxle = Mathf.Min(Vector3.Angle(turnAxis, axleAxis), Vector3.Angle(turnAxis, -axleAxis));
        float toVertical = Mathf.Min(Vector3.Angle(turnAxis, f.registry.transform.up),
                                     Vector3.Angle(turnAxis, -f.registry.transform.up));
        Assert(toAxle < 5f,
            $"the turn axis is {toAxle:F0} degrees off the axle's shaft — it isn't rolling about the axle");
        Assert(toVertical > 20f,
            $"the turn axis is only {toVertical:F0} degrees off vertical — still the old yaw, not an axle roll " +
            "(the fixture's axle is well off vertical, so a real roll must read far from it)");

        // (a), behaviour: sweep the arm; the axle must hold a fixed distance to the arm's origin (riding it
        // rigidly), where hanging on the counter-rotating mount would swing that distance around.
        Physics.gravity = Vector3.zero;
        Physics.simulationMode = SimulationMode.Script;
        SettleBoth(f, 0f, 120);
        float minD = float.MaxValue, maxD = 0f, current = 0f;
        foreach (float checkpoint in new[] { 60f, 120f, 180f })
        {
            RampBoth(f, current, checkpoint); current = checkpoint;
            SettleBoth(f, checkpoint, 250);
            float d = Vector3.Distance(f.axle.transform.position, f.arm.transform.position);
            minD = Mathf.Min(minD, d); maxD = Mathf.Max(maxD, d);
        }
        Assert(maxD - minD < 0.5f,
            $"the axle's distance to the arm swung by {(maxD - minD):F1}u over the sweep — it isn't riding " +
            "the arm rigidly, so it won't stay bolted to the end");

        return $"Flip about the axle: PASSED — the axle welded to the arm (rode it to within " +
               $"{(maxD - minD):F2}u of rigid), and the 180 pivots on it ({pivToAxle:F2}u off) and rolls " +
               $"about its shaft ({toAxle:F0} degrees off the shaft, {toVertical:F0} off vertical).";
    }

    // --- Rebuild must follow a MOVED axle, not a stale marker (2026-07-21 regression) -------------------
    // Removing the manual Mount-pivot bucket exposed this: EnsurePivot reused the ClawLevelPivot marker
    // from the previous build at its OLD spot, so the axle only seeded the pivot on the FIRST build — move
    // the axle and rebuild and nothing changed (the user's exact report). The fix re-seeds the axle-driven
    // marker every rebuild. This drives it end to end: build, move the axle, rebuild, prove the pivot went
    // with it.
    private static string RebuildFollowsMovedAxle()
    {
        ArmFixture f = MakeSwingArmClaw("RebuildBot", flipPastMidpoint: false, withAxle: true);
        ArticulationBody mount = f.mount.GetComponent<ArticulationBody>();
        Vector3 anchor0 = mount.transform.TransformPoint(mount.anchorPosition);
        ChainBuilder.TryAxleWorldAxis(f.axle, out _, out Vector3 axle0);
        Assert(Vector3.Distance(anchor0, axle0) < 0.2f,
            $"the first build should already pivot on the axle ({Vector3.Distance(anchor0, axle0):F2}u off)");

        // Move the axle well away, then rebuild exactly as the window's Build button does (same Options).
        f.axle.transform.position += new Vector3(0f, 0f, 3f);
        ChainBuilder.TryAxleWorldAxis(f.axle, out _, out Vector3 axle1);
        Assert(Vector3.Distance(axle0, axle1) > 1f,
            "the axle must have actually moved for the test to mean anything");

        ClawSetup.Build(f.options, useUndo: false);

        mount = f.mount.GetComponent<ArticulationBody>();
        Vector3 anchor1 = mount.transform.TransformPoint(mount.anchorPosition);
        Assert(Vector3.Distance(anchor1, axle1) < 0.2f,
            $"after rebuild the pivot must follow the moved axle — it sits {Vector3.Distance(anchor1, axle1):F2}u " +
            "off it, so a stale marker pinned it and moving the axle did nothing (the reported bug)");
        Assert(Vector3.Distance(anchor1, axle0) > 1f,
            "the pivot must have LEFT the old axle spot — it's still pinned there");

        return $"Rebuild follows the axle: PASSED — moving the axle {Vector3.Distance(axle0, axle1):F0}u and " +
               $"rebuilding carried the pivot with it (now {Vector3.Distance(anchor1, axle1):F2}u off the axle, " +
               $"{Vector3.Distance(anchor1, axle0):F0}u from where it started).";
    }

    // Ramp/settle the arm while stepping BOTH the mount and wrist couplers (the level test's single-coupler
    // helpers don't drive the wrist).
    private static void RampBoth(ArmFixture f, float fromDeg, float toDeg)
    {
        float step = toDeg >= fromDeg ? 1.5f : -1.5f;
        for (float a = fromDeg; step > 0 ? a < toDeg : a > toDeg; a += step)
        {
            DriveArmTo(f.armBody, a);
            for (int s = 0; s < 4; s++) { f.coupler.ApplyStep(); f.wristCoupler.ApplyStep(); Physics.Simulate(0.01f); }
        }
    }

    private static void SettleBoth(ArmFixture f, float targetDeg, int steps)
    {
        DriveArmTo(f.armBody, targetDeg);
        for (int s = 0; s < steps; s++) { f.coupler.ApplyStep(); f.wristCoupler.ApplyStep(); Physics.Simulate(0.01f); }
    }

    private class ArmFixture
    {
        public RobotMechanisms registry;
        public GameObject arm, mount, flip, jawA, jawB, wrist, axle;
        public ArticulationBody armBody, mountBody, wristBody;
        public JointCoupler coupler, wristCoupler;
        public ClawSetup.Options options;   // the exact options used, so a test can rebuild like the window
    }

    // Builds the swing-arm + claw fixture the level-keeper tests share: a robot tilted off every world
    // axis (so a frame/sign slip can't hide), a bounded-revolute arm on the wheels' lateral line, and a
    // claw whose mount counter-rotates the arm. `flipPastMidpoint` turns on the extra 180.
    private static ArmFixture MakeSwingArmClaw(string name, bool flipPastMidpoint, bool withWrist = false,
        bool withAxle = false, bool forceLevelPivotAtRootCentre = false, bool axleUnderMount = false)
    {
        GameObject root = new GameObject(name);
        RobotMechanisms registry = root.AddComponent<RobotMechanisms>();
        registry.robotId = TestRobotId;
        ArticulationBody chassis = root.AddComponent<ArticulationBody>();
        chassis.immovable = true;
        MakeBox(root.transform, "ChassisMesh", Vector3.zero, new Vector3(6f, 1f, 6f));
        RobotMotorController mc = root.AddComponent<RobotMotorController>();
        mc.leftWheels = new[] { MakeWheel(root.transform, "WheelL", new Vector3(0f, 0f, -3f)) };
        mc.rightWheels = new[] { MakeWheel(root.transform, "WheelR", new Vector3(0f, 0f, 3f)) };

        // The arm hinges about the lateral line (front-to-back swing); the claw hangs off it, every part
        // OFFSET from the hinge so it rides an arc rather than spinning in place.
        GameObject arm = MakeBox(root.transform, "SwingArm", new Vector3(6f, 3f, 0f), new Vector3(1f, 1f, 5f));
        GameObject mount = MakeBox(root.transform, "ClawMount", new Vector3(8f, 3f, 0f), new Vector3(1.5f, 1.5f, 1.5f));
        GameObject flip = MakeBox(root.transform, "ClawFlipAssembly", new Vector3(9f, 3f, 0f), new Vector3(2f, 1f, 3f));
        GameObject jawA = MakeBox(root.transform, "ClawJawLeft", new Vector3(10f, 3f, -1.2f), new Vector3(2f, 0.6f, 0.4f));
        GameObject jawB = MakeBox(root.transform, "ClawJawRight", new Vector3(10f, 3f, 1.2f), new Vector3(2f, 0.6f, 0.4f));
        // A wrist between the mount and the claw, for the yaw test — the flip rides this (a yaw about
        // vertical) instead of the mount (a pitch).
        GameObject wrist = withWrist
            ? MakeBox(root.transform, "ClawWrist", new Vector3(8.6f, 3f, 0f), new Vector3(1f, 1f, 1f)) : null;
        // An axle/standoff at the end of the arm that the claw pivots on, for the explicit-axle path. A
        // thin shaft along the lateral (Z) line — parallel to the arm's hinge — so 'From the axle' and
        // 'Match the arm' agree. It sits ~1u off the claw's centre AND far from the robot's origin, so a
        // pivot that lands on it is distinguishable from both the claw middle and the robot-centre bug.
        GameObject axle = withAxle
            ? MakeBox(root.transform, "ClawAxle", new Vector3(8.5f, 3f, 0f), new Vector3(0.3f, 0.3f, 2.2f))
            : null;

        // Draw the CAD the way a real one comes in: the mount NESTED INSIDE the flip assembly. The build
        // has to pull the mount back out to a direct child of the arm — leaving it nested makes the
        // level-keeper a leaf that flips alone while the claw stays rigid to the arm (the reported bug
        // this fixture reproduces). (Only the mount is nested; the jaws stay siblings so their mass isn't
        // folded into the flip body before they split off.)
        mount.transform.SetParent(flip.transform, worldPositionStays: true);

        root.transform.rotation = Quaternion.Euler(17f, 40f, 13f);

        Vector3 lateralWorld = (Centroid(mc.rightWheels) - Centroid(mc.leftWheels)).normalized;
        Vector3 armAxisLocal = arm.transform.InverseTransformDirection(lateralWorld);
        AddMechanismJoint.Apply(arm, AddMechanismJoint.JointType.Revolute, armAxisLocal, Vector3.zero,
            0f, 180f, new AddMechanismJoint.Options { actuation = AddMechanismJoint.Actuation.HoldToRun }, false);
        ArticulationBody armBody = arm.GetComponent<ArticulationBody>();
        Assert(armBody != null && armBody.jointType == ArticulationJointType.RevoluteJoint,
            "the fixture's swing arm should have become a revolute joint");

        // Hang the flip assembly (mount nested inside it) off the ARM, reproducing the reported setup
        // EXACTLY: the mount is now a DESCENDANT of the arm, so a guard that only asks "is it under the
        // arm?" thinks it's already placed and skips pulling it out — which is the bug. Done AFTER the
        // arm joint is built, so the flip's mass isn't welded into the arm body.
        flip.transform.SetParent(arm.transform, worldPositionStays: true);

        // Where the axle STARTS. Normally on the arm's end (rides the arm). `axleUnderMount` instead nests
        // it on the counter-rotating mount — the reported "drifts above the arm" setup — so the build's
        // weld-to-arm has something to pull it out of. Parented AFTER the arm joint either way so its mesh
        // isn't welded into the arm body's mass.
        if (axle != null)
            axle.transform.SetParent(axleUnderMount ? mount.transform : arm.transform, worldPositionStays: true);

        // For the mutation: a pivot forced onto the robot's centre (the reported bug), overriding the axle.
        Transform forcedPivot = null;
        if (forceLevelPivotAtRootCentre)
        {
            GameObject piv = new GameObject("ForcedRootCentrePivot");
            piv.transform.SetParent(root.transform, worldPositionStays: true);
            piv.transform.position = root.transform.position;
            forcedPivot = piv.transform;
        }

        ClawSetup.Options o = new ClawSetup.Options
        {
            displayName = "Arm Claw",
            flippingParts = new List<GameObject> { flip },
            flipAngleDeg = FlipAngle,
            flipAxisPreset = ClawRig.HingeAxis.Auto,
            flipCustomAxis = Vector3.right,
            flipStiffness = 20000f, flipDamping = 500f, flipTravelSeconds = FlipSeconds,
            flipStrokeMm = 90f, flipRecoil = 0.5f,
            clampSections = new List<ClawRig.ClampSection>
            {
                Section(jawA, CloseAngle, false),
                Section(jawB, CloseAngle, true),
            },
            clampStiffness = 20000f, clampDamping = 500f,
            clampStrokeMm = ClampStrokeMm, clampRecoil = 0.5f,
            clampModelled = ClawRig.JawRest.ModelledOpen,
            enableGrab = false,
            autoAssignButtons = true,
            levelParts = new List<GameObject> { mount },
            armDriver = arm,
            levelAxle = axle,
            levelPivot = forcedPivot,
            levelAxisPreset = ClawRig.HingeAxis.MatchArm,
            levelCustomAxis = Vector3.right,
            levelRatio = -1f,
            levelSweepDeg = 200f,
            levelStiffness = 20000f, levelDamping = 500f,
            levelFlipPastMidpoint = flipPastMidpoint || withWrist,   // a wrist needs the flip on to yaw
            levelFlipDegrees = 180f,
            levelFlipFraction = 0.5f,
            levelFlipSeconds = 0.15f,
            yawWristParts = wrist != null ? new List<GameObject> { wrist } : null,
        };
        ClawSetup.Build(o, useUndo: false);

        // Edit-mode Physics.Simulate never runs IgnoreRobotSelfCollision.Awake, so the claw's boxes would
        // collide with the chassis at some sweep angles and stall the leveling drive — a test artifact,
        // not real behaviour (in Play the claw ignores the robot). Disable colliders so these tests
        // measure the DRIVE alone.
        foreach (Collider c in registry.GetComponentsInChildren<Collider>(true)) c.enabled = false;

        return new ArmFixture
        {
            registry = registry, arm = arm, mount = mount, flip = flip, jawA = jawA, jawB = jawB,
            wrist = wrist, axle = axle, options = o,
            armBody = armBody, mountBody = mount.GetComponent<ArticulationBody>(),
            coupler = mount.GetComponent<JointCoupler>(),
            wristBody = wrist != null ? wrist.GetComponent<ArticulationBody>() : null,
            wristCoupler = wrist != null ? wrist.GetComponent<JointCoupler>() : null,
        };
    }

    // Ramp the arm smoothly from one angle to another, stepping the coupler each step — a slam rings the
    // coupled drives, so the motion is walked in as a real motor would drive it.
    private static void RampArm(ArticulationBody arm, JointCoupler coupler, float fromDeg, float toDeg)
    {
        float step = toDeg >= fromDeg ? 1.5f : -1.5f;
        for (float a = fromDeg; step > 0 ? a < toDeg : a > toDeg; a += step)
        {
            DriveArmTo(arm, a);
            for (int s = 0; s < 4; s++) { coupler.ApplyStep(); Physics.Simulate(0.01f); }
        }
    }

    // Ramp the arm to `targetDeg` and hold it there for `steps`, stepping the coupler each step so the
    // mount tracks — the edit-mode stand-in for a motor driving the arm while FixedUpdate is asleep.
    private static void SettleArm(ArticulationBody arm, JointCoupler coupler, float targetDeg, int steps)
    {
        DriveArmTo(arm, targetDeg);
        for (int s = 0; s < steps; s++) { coupler.ApplyStep(); Physics.Simulate(0.01f); }
    }

    // Drive the arm joint to a fixed angle with a position target, overriding its velocity motor for a
    // clean edit-mode sweep (the motor's FixedUpdate never runs here anyway). Stiffness is kept in the
    // jaws' proven range, not cranked — two coupled stiff drives ring at this timestep. targetVelocity
    // is cleared deliberately: the HoldToRun motor leaves one behind, and a position drive's
    // damping*(targetVelocity - v) term would otherwise shove the arm clean past its target.
    private static void DriveArmTo(ArticulationBody arm, float targetDeg)
    {
        ArticulationDrive d = arm.xDrive;
        d.driveType = ArticulationDriveType.Target;
        d.stiffness = 30000f;
        d.damping = 3000f;
        d.forceLimit = float.MaxValue;
        d.target = targetDeg;
        d.targetVelocity = 0f;
        arm.xDrive = d;
    }

    private static Vector3 Centroid(ArticulationBody[] bodies)
    {
        Vector3 sum = Vector3.zero;
        int n = 0;
        foreach (ArticulationBody b in bodies) if (b != null) { sum += b.transform.position; n++; }
        return n > 0 ? sum / n : Vector3.zero;
    }

    // --- Motion: the halves must actually close ON each other, not sweep the same way --------------
    private static string JawsCloseAgainstEachOther()
    {
        Fixture f = MakeFixture("MotionBot");
        ClawSetup.Options o = f.Options();
        o.flippingParts = new List<GameObject>();   // clamp-only, so nothing else moves in the read
        ClawSetup.Build(o, useUndo: false);

        ArticulationBody driver = f.jawA.GetComponent<ArticulationBody>();
        ArticulationBody follower = f.jawB.GetComponent<ArticulationBody>();
        JointCoupler coupler = f.jawB.GetComponent<JointCoupler>();
        PneumaticActuator piston = f.jawA.GetComponent<PneumaticActuator>();
        Assert(driver != null && follower != null && coupler != null && piston != null,
            "the fixture should have produced two jaw joints, a coupler and a piston");

        // Isolate the drive: gravity would swing these links about their own hinges and muddy the read.
        Physics.gravity = Vector3.zero;
        Physics.simulationMode = SimulationMode.Script;

        Vector3 jawBefore = MechanismBuildUtil.BoundsCenterOrOrigin(f.jawA);

        piston.Extend();   // writes the drive target; Awake never runs at edit time, but this doesn't need it
        for (int step = 0; step < 200; step++)
        {
            coupler.ApplyStep();   // stands in for FixedUpdate, which doesn't run at edit time
            Physics.Simulate(0.02f);
        }

        // jointPosition is RADIANS (drive targets and limits are degrees — the standing Unity quirk).
        float driverDeg = JointAngleDeg(driver);
        float followerDeg = JointAngleDeg(follower);

        Assert(Mathf.Abs(driverDeg) > CloseAngle * 0.5f,
            $"the driven jaw barely moved ({driverDeg:F1} degrees of {CloseAngle}) — the piston never fired");
        Assert(Mathf.Abs(followerDeg) > CloseAngle * 0.5f,
            $"the mirrored jaw barely moved ({followerDeg:F1} degrees) while the driven one went " +
            $"{driverDeg:F1} — the coupling isn't driving it");
        Assert(Mathf.Sign(driverDeg) != Mathf.Sign(followerDeg),
            $"the two halves must close TOWARD each other: the driven jaw went {driverDeg:F0} degrees " +
            $"and the mirrored one went {followerDeg:F0} — same sign means the claw opens on one side " +
            "as it shuts on the other.");

        // Symmetry, not just direction. At ratio -1 the halves are meant to be mirror images, and a
        // lopsided claw is exactly what a force-capped follower produces: it stalls short while its
        // uncapped twin closes fully. That's a bug you'd only ever notice by looking at the robot.
        AssertApprox(Mathf.Abs(followerDeg), Mathf.Abs(driverDeg), 2f,
            $"at ratio -1 both halves must travel the same distance, but the driven jaw went " +
            $"{driverDeg:F1} degrees and the mirrored one {followerDeg:F1} — the claw closes lopsided");
        AssertApprox(Mathf.Abs(driverDeg), CloseAngle, 2f,
            "the driven jaw should reach the close angle it was given");

        // WHICH WAY it swings, not just how far. Degrees alone can't tell "closes inward on a cup" from
        // "pitches up in the air" — and the first cut of this tool did the latter on every real claw,
        // because the axis came off the pivot marker's rotation (seeded to the robot's left-right axis)
        // while this suite pinned an explicit vertical axis and sailed past it. Assert the behaviour the
        // player actually sees: the jaw travels sideways, not upward.
        Vector3 jawTravel = MechanismBuildUtil.BoundsCenterOrOrigin(f.jawA) - jawBefore;
        float upward = Mathf.Abs(Vector3.Dot(jawTravel, Vector3.up));
        float sideways = Vector3.ProjectOnPlane(jawTravel, Vector3.up).magnitude;
        Assert(jawTravel.magnitude > 1e-3f,
            "the jaw's geometry never moved, so which way it hinges can't be read");
        Assert(sideways > upward * 2f,
            $"the jaws must close INWARD, but this one moved {upward:F2} units vertically against " +
            $"{sideways:F2} horizontally — it's pitching up instead of closing on the piece. Check the " +
            "hinge axis default (a clamp should hinge about the robot's vertical).");

        // The cosmetic cylinder must be reading this motion, or the piston would look frozen while the
        // jaws move.
        PneumaticSlideFollower rod = f.clampRod.GetComponent<PneumaticSlideFollower>();
        Assert(rod.Progress01() > 0.5f,
            $"the clamp cylinder should read as fired once the jaws are shut (progress " +
            $"{rod.Progress01():F2}) — its progress joint or endpoints are wired wrong");

        return $"Jaw motion under physics: PASSED — driven jaw {driverDeg:F0} degrees, mirrored jaw " +
               $"{followerDeg:F0} degrees, closing against each other, swinging sideways " +
               $"({sideways:F2} units) rather than upward ({upward:F2}), cylinder reading " +
               $"{rod.Progress01():P0} fired.";
    }

    // --- Inputs that must be refused rather than half-built ----------------------------------------
    private static string RejectionsHold()
    {
        Fixture f = MakeFixture("RejectBot");

        AssertThrows(() =>
        {
            ClawSetup.Options o = f.Options();
            o.flippingParts = new List<GameObject>();
            o.clampSections = new List<ClawRig.ClampSection>();
            ClawSetup.Build(o, false);
        }, "a claw with nothing in either bucket");

        AssertThrows(() =>
        {
            ClawSetup.Options o = f.Options();
            o.flipAngleDeg = 0f;
            ClawSetup.Build(o, false);
        }, "a flip angle of 0 degrees");

        AssertThrows(() =>
        {
            ClawSetup.Options o = f.Options();
            o.clampCylinderRod = null;   // body without a rod: no direction to extend along
            ClawSetup.Build(o, false);
        }, "a cylinder with a body but no rod");

        AssertThrows(() =>
        {
            ClawSetup.Options o = f.Options();
            o.clampCylinderRod = f.jawA;   // the cylinder IS the metal it drives
            ClawSetup.Build(o, false);
        }, "a cylinder part that is also the clamping metal");

        AssertThrows(() =>
        {
            ClawSetup.Options o = f.Options();
            o.clampSections = new List<ClawRig.ClampSection>
            {
                Section(f.jawA, CloseAngle, false),
                Section(f.jawA, CloseAngle, true),   // same part in two halves
            };
            ClawSetup.Build(o, false);
        }, "the same part listed as two clamp halves");

        AssertThrows(() =>
        {
            ClawSetup.Options o = f.Options();
            o.clampSections = new List<ClawRig.ClampSection> { Section(f.flip, CloseAngle, false) };
            ClawSetup.Build(o, false);
        }, "the flipping assembly listed as a clamp half");

        Assert(f.registry.mechanisms.Count == 0,
            "a rejected build must leave the robot untouched — no half-registered mechanisms");
        Assert(f.jawA.GetComponent<ArticulationBody>() == null,
            "a rejected build must not have jointed anything before it threw");

        return "Rejections: PASSED — empty buckets, a zero flip angle, a half-assigned cylinder, a " +
               "cylinder that is its own metal, a duplicated jaw and a jaw that is the flip assembly " +
               "are all refused with the robot left untouched.";
    }

    // --- Fixture ---------------------------------------------------------------------------------

    private class Fixture
    {
        public RobotMechanisms registry;
        public GameObject flip, jawA, jawB, flipBody, flipRod, clampBody, clampRod;

        public ClawSetup.Options Options() => new ClawSetup.Options
        {
            displayName = "Test Claw",
            flippingParts = new List<GameObject> { flip },
            flipAngleDeg = FlipAngle,
            flipAxisPreset = ClawRig.HingeAxis.Auto,
            flipCustomAxis = Vector3.right,
            flipStiffness = 20000f,
            flipDamping = 500f,
            flipTravelSeconds = FlipSeconds,   // ditto: unset, a plain struct reads 0 = instant snap
            flipCylinderBody = flipBody,
            flipCylinderRod = flipRod,
            flipStrokeMm = 90f,
            flipRecoil = 0.5f,
            clampSections = new List<ClawRig.ClampSection>
            {
                Section(jawA, CloseAngle, false),
                Section(jawB, CloseAngle, true),
            },
            clampStiffness = 20000f,
            clampDamping = 500f,
            clampCylinderBody = clampBody,
            clampCylinderRod = clampRod,
            clampStrokeMm = ClampStrokeMm,
            clampRecoil = 0.5f,
            enableGrab = true,
            grabPassThrough = true,        // these two mirror the window's defaults; Options is a
            grabAutoUpright = true,        // plain struct, so anything unset here silently reads false
            clampModelled = ClawRig.JawRest.ModelledOpen,
            clampStartClosed = false,
            autoAssignButtons = true,
        };
    }

    private static ClawRig.ClampSection Section(GameObject part, float angle, bool mirror)
        => new ClawRig.ClampSection
        {
            parts = new List<GameObject> { part },
            closeAngleDeg = angle,
            // Deliberately Auto — the DEFAULT is what ships and what everyone hits first. Pinning this
            // to an explicit vertical axis is how the original suite passed while every real claw built
            // with the tool hinged about the robot's left-right axis and pitched its jaws upward.
            axisPreset = ClawRig.HingeAxis.Auto,
            customAxis = Vector3.up,
            mirror = mirror,
        };

    // A fixed chassis with a drivetrain (so there's a lateral axis to derive), a flip assembly, two
    // jaws alongside it, and two cylinders. The jaws start as SIBLINGS of the flip assembly, so the
    // build has to reparent them — the nesting is part of what's under test.
    private static Fixture MakeFixture(string name)
    {
        GameObject root = new GameObject(name);
        var f = new Fixture();
        f.registry = root.AddComponent<RobotMechanisms>();
        f.registry.robotId = TestRobotId;
        ArticulationBody chassis = root.AddComponent<ArticulationBody>();
        chassis.immovable = true;
        MakeBox(root.transform, "ChassisMesh", Vector3.zero, new Vector3(6f, 1f, 6f));

        // Left/right wheels at -Z/+Z make the robot's lateral axis +Z.
        RobotMotorController mc = root.AddComponent<RobotMotorController>();
        mc.leftWheels = new[] { MakeWheel(root.transform, "WheelL", new Vector3(0f, 0f, -3f)) };
        mc.rightWheels = new[] { MakeWheel(root.transform, "WheelR", new Vector3(0f, 0f, 3f)) };

        f.flip = MakeBox(root.transform, "ClawFlipAssembly", new Vector3(5f, 2f, 0f), new Vector3(2f, 1f, 3f));
        f.jawA = MakeBox(root.transform, "ClawJawLeft", new Vector3(7f, 2f, -1.2f), new Vector3(2f, 0.6f, 0.4f));
        f.jawB = MakeBox(root.transform, "ClawJawRight", new Vector3(7f, 2f, 1.2f), new Vector3(2f, 0.6f, 0.4f));

        f.flipBody = MakeBox(root.transform, "FlipCylinderBody", new Vector3(2f, 3f, 0f), new Vector3(1.5f, 0.4f, 0.4f));
        f.flipRod = MakeBox(root.transform, "FlipCylinderRod", new Vector3(3.5f, 3f, 0f), new Vector3(1f, 0.2f, 0.2f));
        f.clampBody = MakeBox(root.transform, "ClampCylinderBody", new Vector3(5f, 3f, 0f), new Vector3(1.2f, 0.3f, 0.3f));
        f.clampRod = MakeBox(root.transform, "ClampCylinderRod", new Vector3(6.2f, 3f, 0f), new Vector3(0.8f, 0.15f, 0.15f));
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

    private static float JointAngleDeg(ArticulationBody body)
    {
        ArticulationReducedSpace jp = body.jointPosition;
        return jp.dofCount == 0 ? 0f : jp[0] * Mathf.Rad2Deg;
    }

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
