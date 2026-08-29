using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Headless checks for the passive arm — the unpowered hinge that only turns when something hits it
// and rubber-bands back to its drawn pose.
//
// Four of the five are simulated, because the four things that can go wrong are invisible to
// arithmetic:
//
//   THE UNIT. ArticulationDrive.stiffness is torque per SOMETHING. A revolute drive speaks degrees
//   for its target and radians for its position read-back, so "per radian" is a guess until a
//   torque is applied and the deflection read. SizeBand assumes per radian; that guess is MEASURED
//   here, and if it is wrong SizeBand is what changes, not this file.
//
//   THE RETURN. A spring at target 0 only proves the arm comes back if gravity is pulling the other
//   way while it does — a fixture where gravity helps passes with the band off. So the arm rests
//   at the TOP of its travel with its weight outboard, and the in-test guard switches the band off
//   and watches the same arm sag.
//
//   THE PUSH. The arm exists to be hit by its own robot's toggle, and every other mechanism link
//   carries a blanket IgnoreRobotSelfCollision that stops exactly that. The rules re-deciding
//   those pairs run in Start, which edit-mode physics never calls — so the fixture lays the
//   blanket by hand, proves the toggle passes straight through, then applies the rules and proves
//   it no longer does.
//
//   THE WINDOW. A passive arm is a limited revolute with no pneumatic, which is what an ARM MOTOR
//   looks like to a joint-type switch. If the window's kind detection ever consults the joint
//   before it looks for the PassiveArm, opening the arm to nudge a limit re-applies it as a motor.
//
// Usage: Tools > RoboSim > Validate > Validate Passive Arm, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod PassiveArmValidation.RunBatchValidate
public static class PassiveArmValidation
{
    private const string TestRobotId = "passivearmtestbot";

    // The project's gravity, set explicitly so a run never depends on what the previous validator
    // left in Physics.gravity (StartingPoseValidation runs with it at zero).
    private const float Gravity = 98f;

    // The hinge axis every arm here is built about. ConfigureJointLink points the anchor frame's X
    // down MINUS the axis it is handed, and a positive joint angle is right-handed about that X —
    // so with this axis a positive angle swings the bar UP (+X toward +Y) and gravity pulls it
    // NEGATIVE. StartingPoseValidation pins that convention against simulation; the sag guard in
    // the return case and the swing in the push case both fail loudly if it ever flips.
    private static readonly Vector3 ArmAxis = Vector3.back;

    // The arm's bar, hung off its origin: 4 u long along +X, so the hinge sits 2 u from the bar's
    // centre, and thin, so it weighs about what a flap weighs (~0.2 kg from geometry) rather than
    // what a lift arm does — the push case pits the stock toggle motor against the band plus the
    // arm's weight plus friction at the sliding contact, and a heavy bar turns that into a
    // wrestling match the motor can lose. The skewed offset also slides the bar 1 u ALONG the
    // hinge line, which changes the straight-line distance from hinge to centre (2.24 u) but not
    // the perpendicular one (2 u) — the builder has to size the band from the latter.
    private static readonly Vector3 ArmMeshSize = new Vector3(4f, 0.2f, 0.2f);
    private static readonly Vector3 ArmMeshOffset = new Vector3(2f, 0f, 0f);
    private static readonly Vector3 SkewedMeshOffset = new Vector3(2f, 0f, 1f);
    private const float ExpectedLeverArm = 2f;

    // How fast the toggle sweeps, in the DEGREES per second a revolute velocity drive speaks. At
    // this rate the bar's contact point moves well under the arm's thickness per step, so the hit
    // is a contact and not a tunnelling question.
    private const float ToggleSweepDegPerSec = 60f;   // a deliberate press, 1.5 s to the top of its 90 deg
    private const float PushArmHeight = 1f;   // case 4's arm hinge height above the chassis origin

    // A velocity reversal slower than this is the arm settling onto its rest, not ringing.
    private const float ReversalDeadbandRadPerSec = 0.25f;

    [MenuItem("Tools/RoboSim/Validate/Validate Passive Arm", false, 27)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive("Validate Passive Arm", Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Validate Passive Arm", Run);

    private static string Run()
    {
        // ApplyPassiveArm refreshes the robot CATALOG, a real project asset, and the fixture's
        // button map lands in PlayerPrefs — both swept back out exactly as StartingPoseValidation
        // sweeps its own.
        bool hadEntry = RoboSimPaths.HasCatalogEntry(TestRobotId);
        SimulationMode previousSimulation = Physics.simulationMode;
        Vector3 previousGravity = Physics.gravity;
        try
        {
            Physics.simulationMode = SimulationMode.Script;

            // A fresh scene per case: Physics.Simulate moves real transforms, and every case here
            // builds from the authored pose.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string built = BuiltThroughTheRealPath();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string unit = TheStiffnessUnitIsMeasured();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string returns = TheBandReturnsTheArm();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string pushed = TheToggleCanPushIt();

            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            string window = TheWindowRecognisesIt();

            return "Validate Passive Arm: PASSED\n\n" + built + "\n" + unit + "\n" + returns + "\n" +
                   pushed + "\n" + window;
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

    // --- 1. Built through the real path ----------------------------------------------------------

    // The flap is first built as a POWERED arm with a button, a style, a stray piston and the
    // blanket — the state a link is in when someone picked the wrong kind and came back to fix it —
    // so that "nothing powers it afterwards" is a sweep being tested rather than an empty link.
    private static string BuiltThroughTheRealPath()
    {
        Physics.gravity = new Vector3(0f, -Gravity, 0f);
        GameObject root = MakeChassis(out RobotMechanisms registry);

        // A second mechanism, so "the sweep removed the arm's record" and "the sweep removed
        // everything" are different outcomes.
        GameObject roller = MakeLink(root.transform, "Roller", new Vector3(0f, -2f, 0f),
            new Vector3(2f, 0f, 0f), new Vector3(2f, 0.4f, 0.4f));
        AddMechanismJoint.Apply(roller, AddMechanismJoint.JointType.Continuous, Vector3.forward,
            Vector3.zero, 0f, 0f, useUndo: false);
        string rollerId = UrdfPostProcessor.Slugify(roller.name);

        GameObject flap = MakeLink(root.transform, "Flap", new Vector3(0f, 2f, 0f), SkewedMeshOffset, ArmMeshSize);
        AddMechanismJoint.Apply(flap, AddMechanismJoint.JointType.Revolute, ArmAxis, Vector3.zero, 0f, 90f, useUndo: false);
        string id = UrdfPostProcessor.Slugify(flap.name);
        MechanismAutoDetect.AssignButtons(TestRobotId, id, AddMechanismJoint.JointType.Revolute);
        ButtonMap map = ControllerMapSettings.Load(TestRobotId);
        ControllerMapSettings.SetStyle(map, id, RobotMechanisms.TypeMotor, ControllerMapSettings.StyleOneButton);
        ControllerMapSettings.Save(TestRobotId, map);
        flap.AddComponent<PneumaticActuator>();
        flap.AddComponent<IgnoreRobotSelfCollision>();

        // The state to be swept has to exist, or every "gone" below is vacuous.
        map = ControllerMapSettings.Load(TestRobotId);
        ValidationUtil.Assert(registry.mechanisms.Count == 2 && registry.Find(id) != null &&
                              flap.GetComponent<MotorActuator>() != null,
            "fixture: the flap must start life as a registered arm motor");
        ValidationUtil.Assert(map.assignments.Exists(a => a != null && a.mechanismId == id),
            "fixture: the motor must hold a button before the sweep is asked to drop it");
        ValidationUtil.Assert(map.styles.Exists(s => s != null && s.mechanismId == id),
            "fixture: the motor must hold a control style before the sweep is asked to drop it");

        PassiveArm arm = AddMechanismJoint.ApplyPassiveArm(flap, ArmAxis, Vector3.zero, 0f, 90f,
            new AddMechanismJoint.PassiveArmOptions { returnToRest = true, bandStrength = 3f }, useUndo: false);
        ArticulationBody body = flap.GetComponent<ArticulationBody>();

        ValidationUtil.Assert(arm != null && body != null && arm.body == body,
            "ApplyPassiveArm must return the arm with its body wired — Awake never runs in edit " +
            "mode, so the builder has to do it, or edit-mode physics sizes and bakes against nothing");
        ValidationUtil.Assert(flap.GetComponent<MotorActuator>() == null,
            "the motor must be gone — a passive arm with a motor still on it is an arm motor with extra steps");
        ValidationUtil.Assert(flap.GetComponent<PneumaticActuator>() == null,
            "a stray piston with no registry record must be stripped too, not just the recorded actuator");
        ValidationUtil.Assert(flap.GetComponent<IgnoreRobotSelfCollision>() == null,
            "the blanket must go: an arm that ignores its whole robot can never be pushed by the toggle");
        ValidationUtil.Assert(registry.mechanisms.Count == 1 && registry.Find(id) == null,
            "the sweep must remove the arm's own registry record — a stale one keeps offering the " +
            "config screen a button for an actuator that no longer exists");
        RobotMechanisms.Mechanism rollerRecord = registry.Find(rollerId);
        ValidationUtil.Assert(rollerRecord != null && rollerRecord.motor != null &&
                              rollerRecord.motor.gameObject == roller,
            "...and ONLY the arm's record: the roller's motor must survive the sweep untouched");
        map = ControllerMapSettings.Load(TestRobotId);
        ValidationUtil.Assert(!map.assignments.Exists(a => a != null && a.mechanismId == id),
            "the old motor's button binding must be dropped — a passive arm has no button, so a " +
            "binding left behind points at nothing and blocks that button for everything else");
        ValidationUtil.Assert(!map.styles.Exists(s => s != null && s.mechanismId == id),
            "the old motor's control style must be dropped along with its bindings");
        ValidationUtil.Assert(RoboSimPaths.HasCatalogEntry(TestRobotId),
            "the catalog entry must be refreshed (it is what the config screen reads)");

        ArticulationDrive d = body.xDrive;
        ValidationUtil.Assert(body.jointType == ArticulationJointType.RevoluteJoint &&
                              body.twistLock == ArticulationDofLock.LimitedMotion,
            "a passive arm is a LIMITED hinge — its limits are its hard stops");
        ValidationUtil.Near(d.lowerLimit, 0f, 1e-3f, "the lower limit must be the one asked for");
        ValidationUtil.Near(d.upperLimit, 90f, 1e-3f, "the upper limit must be the one asked for");
        ValidationUtil.Assert(d.driveType == ArticulationDriveType.Force,
            "the band is a POSITION-target drive — a velocity drive cannot pull toward a pose");
        ValidationUtil.Near(d.target, 0f, 1e-6f,
            "the band's target is joint zero, which IS the drawn pose (Set Starting Pose keeps it so)");
        ValidationUtil.Near(d.stiffness, arm.bandStiffness, 1e-3f, "the drive must carry the baked stiffness");
        ValidationUtil.Near(d.damping, arm.bandDamping, 1e-3f, "the drive must carry the baked damping");
        ValidationUtil.Near(d.forceLimit, arm.bandForceLimit, 1e-3f, "the drive must carry the baked cap");

        // The sizing itself: bandStrength x weight torque, over the PERPENDICULAR distance to the
        // hinge line. The fixture's bar is slid 1 u along the hinge, so a builder that measured
        // straight from the pivot point would size 12% high and miss this.
        float expectedCap = arm.bandStrength * body.mass * Gravity * ExpectedLeverArm;
        ValidationUtil.Assert(body.mass > 0.05f && expectedCap > 2f * PassiveArm.MinBandForceLimit,
            $"fixture: the bar must weigh enough (mass {body.mass:0.###} kg) for the sizing to be " +
            "above the band's floor, or this measures the floor");
        ValidationUtil.Near(arm.bandForceLimit, expectedCap, 0.01f * expectedCap,
            "the cap must be bandStrength x (mass x g x lever arm), with the lever arm measured " +
            "perpendicular to the hinge LINE — not from the pivot point, and not from the link origin");
        ValidationUtil.Near(arm.bandStiffness, arm.bandForceLimit / (PassiveArm.BandSaturationDegrees * Mathf.Deg2Rad),
            1e-3f * arm.bandStiffness,
            "the spring must reach the cap BandSaturationDegrees from rest — that slope is the whole " +
            "'pre-tensioned band' model");
        ValidationUtil.Finite(arm.bandDamping, "the baked damping");
        ValidationUtil.Assert(arm.bandDamping > 0f, "an undamped band rings forever");

        // Band off is a free hinge with a little friction — target drive, no spring, uncapped.
        arm.returnToRest = false;
        arm.BakeDrive();
        ArticulationDrive free = body.xDrive;
        ValidationUtil.Assert(free.driveType == ArticulationDriveType.Force && free.stiffness == 0f,
            "with the band off there must be no spring at all — a flap that 'stays where it was left' cannot pull");
        ValidationUtil.Near(free.damping, arm.hingeFriction, 1e-6f,
            "with the band off the only drive term is the hinge's own friction");
        ValidationUtil.Assert(free.forceLimit >= 1e6f,
            "friction must be uncapped — a cap on damping alone would let a hard hit spin the arm through it");
        arm.returnToRest = true;
        arm.BakeDrive();

        return $"built: motor, piston, blanket, record, button and style all swept; band cap " +
               $"{arm.bandForceLimit:0.#} = 3 x {body.mass:0.###} kg x {Gravity} x {ExpectedLeverArm} u, " +
               $"stiffness {arm.bandStiffness:0.#}, damping {arm.bandDamping:0.#}; Target drive at 0.";
    }

    // --- 2. The stiffness unit -------------------------------------------------------------------

    // Gravity off, a constant torque on the hinge, read the deflection it settles at: the ratio IS
    // the drive's stiffness in whatever unit PhysX runs it in. Torque is kept inside the band's
    // linear zone so the cap never enters into it.
    private static string TheStiffnessUnitIsMeasured()
    {
        // The known torque is GRAVITY on the arm's own mass — the one torque whose value is not in
        // dispute (mass x g x the COM's perpendicular distance from the hinge line, all read back
        // off the built body), and the one the band is sized against. ArticulationBody.AddTorque was
        // tried first and moved the joint 0.000 deg in edit-mode simulation, which says nothing about
        // the band; a sag under gravity is a deflection PhysX cannot fail to produce.
        Physics.gravity = new Vector3(0f, -Gravity, 0f);
        GameObject root = MakeChassis(out _);
        GameObject flap = MakeLink(root.transform, "Flap", new Vector3(0f, 2f, 0f), ArmMeshOffset, ArmMeshSize);
        PassiveArm arm = BuildArm(flap, -90f, 90f, returnToRest: true, strength: 3f);
        ArticulationBody body = flap.GetComponent<ArticulationBody>();
        Physics.Simulate(0.02f);

        // Control first: park the joint 10 deg out with a drive no band can argue with. A joint that
        // will not go there is not being simulated at all, and a zero sag below would say nothing
        // about the band — the first version of this case read 0.000 deg and could not tell why.
        ArticulationDrive band = body.xDrive;
        DriveTo(body, 10f);
        Simulate(100);
        float parked = JointDeg(body);
        Debug.Log($"PassiveArmValidation unit control: mass {body.mass:0.###} kg, COM {body.worldCenterOfMass}, " +
                  $"useGravity {body.useGravity}, joint {body.jointType}/{body.twistLock}, limits " +
                  $"{band.lowerLimit}..{band.upperLimit}, band k {band.stiffness:0.#} c {band.damping:0.#} cap " +
                  $"{band.forceLimit:0.#} target {band.target}; driven to 10 deg -> {parked:0.###} deg.");
        ValidationUtil.Near(parked, 10f, 1f,
            "fixture: a stiff drive must be able to park the arm at 10 deg — if it cannot, the joint is not " +
            "being simulated and nothing below measures the band");
        body.xDrive = band;   // the band back, exactly as baked
        Simulate(250);
        float before = JointDeg(body);
        Simulate(50);    // 3 s in all: the sag is an equilibrium, so it has to be measured at rest
        float drift = Mathf.Abs(JointDeg(body) - before);

        ValidationUtil.Assert(StartingPose.TryJointFrame(body, out Vector3 twistW, out Vector3 pivotW),
            "fixture: the built arm must expose a hinge frame to measure against");
        Vector3 lever = Vector3.ProjectOnPlane(body.worldCenterOfMass - pivotW, twistW);
        float torque = Vector3.Dot(Vector3.Cross(lever, body.mass * Physics.gravity), twistW);   // about the hinge
        float deflectionRad = body.jointPosition[0];
        float deflectionDeg = Mathf.Abs(deflectionRad) * Mathf.Rad2Deg;

        // Settled means the ANGLE stopped changing. jointVelocity on a spring-held link is solver
        // residual — this project's goal magnets measure 1.2 rad/s on a piece that travels 0.000 deg
        // — and asserting on it here read 0.15 rad/s at a sag that had not moved in half a second.
        ValidationUtil.Assert(drift < 0.05f,
            $"fixture: the arm must have settled (moved {drift:0.###} deg in the last 0.5 s, reporting " +
            $"{JointRadPerSec(body):0.###} rad/s) or the sag is not an equilibrium");
        ValidationUtil.Assert(Mathf.Abs(torque) > 1f,
            $"fixture: gravity must put a real torque on the hinge (got {torque:0.###}) — the bar has to hang " +
            "off to the side of the hinge line, not on it");
        ValidationUtil.Assert(deflectionDeg > 0.2f,
            $"fixture: the arm's own weight must sag it measurably against the band (got {deflectionDeg:0.###} deg) " +
            "— if it does not, the band is not what is holding the joint");
        ValidationUtil.Assert(deflectionDeg < PassiveArm.BandSaturationDegrees,
            $"fixture: the sag ({deflectionDeg:0.##} deg) must stay inside the band's linear zone " +
            $"({PassiveArm.BandSaturationDegrees} deg) or the force cap corrupts the measurement");
        // The band pulls the arm back toward zero, so the sag has the SIGN of the gravity torque in
        // the hinge's own frame — a mismatch means the axis convention is not what SizeBand assumes.
        ValidationUtil.Assert(Mathf.Sign(deflectionRad) == Mathf.Sign(torque),
            $"the arm sagged {deflectionRad * Mathf.Rad2Deg:0.##} deg under a {torque:0.#} hinge torque — opposite " +
            "signs, so the joint's positive direction is not the anchor-X right-hand rule the builder relies on");

        float perRadian = Mathf.Abs(torque) / Mathf.Abs(deflectionRad);
        float perDegree = Mathf.Abs(torque) / deflectionDeg;
        // A quarter either way: the question is per radian or per degree, a factor of 57, and the
        // measured figure has run 12% stiff (2523 against a baked 2246 — joint friction and the
        // solver's own hold both take a share of the load before the spring does).
        ValidationUtil.Near(perRadian, arm.bandStiffness, 0.25f * arm.bandStiffness,
            $"MEASURED drive stiffness is {perRadian:0.#} per radian ({perDegree:0.##} per degree) " +
            $"against a baked {arm.bandStiffness:0.#}. SizeBand assumes ArticulationDrive.stiffness is " +
            "torque per RADIAN; if the per-degree figure is the one that matches, fix SizeBand, not this");
        return $"unit: the arm's weight ({Mathf.Abs(torque):0.#} about the hinge) sags it {deflectionDeg:0.##} deg -> " +
               $"tau/delta = {perRadian:0.#} per radian ({perDegree:0.##} per degree) against a baked " +
               $"{arm.bandStiffness:0.#} — ArticulationDrive.stiffness is torque per radian.";
    }

    // --- 3. The return ---------------------------------------------------------------------------

    // Rest at the UPPER limit with the bar outboard: gravity pulls the arm away from rest the whole
    // time, so getting back is the band beating the arm's own weight, not gravity helping.
    private static string TheBandReturnsTheArm()
    {
        Physics.gravity = new Vector3(0f, -Gravity, 0f);
        GameObject root = MakeChassis(out _);
        GameObject flap = MakeLink(root.transform, "Flap", new Vector3(0f, 2f, 0f), ArmMeshOffset, ArmMeshSize);
        PassiveArm arm = BuildArm(flap, -90f, 0f, returnToRest: true, strength: 3f);
        ArticulationBody body = flap.GetComponent<ArticulationBody>();
        Physics.Simulate(0.02f);

        // Knock it 45 degrees off rest with a drive far stiffer than the band, then hand it back.
        DriveTo(body, -45f);
        Simulate(200);
        ValidationUtil.Near(JointDeg(body), -45f, 2f,
            "fixture: the temporary drive must park the arm 45 deg off rest, or the return measures nothing");
        arm.BakeDrive();
        ValidationUtil.Assert(body.xDrive.driveType == ArticulationDriveType.Force && body.xDrive.target == 0f &&
                              body.xDrive.stiffness == arm.bandStiffness,
            "fixture: re-baking must hand the joint back to the band (target 0, the baked stiffness)");

        int reversals = 0;
        float lastSign = 0f;
        for (int i = 0; i < 100; i++)   // 1 s
        {
            Physics.Simulate(ValidationUtil.StepSeconds);
            float w = JointRadPerSec(body);
            if (Mathf.Abs(w) <= ReversalDeadbandRadPerSec) continue;
            float sign = Mathf.Sign(w);
            if (lastSign != 0f && sign != lastSign) reversals++;
            lastSign = sign;
        }
        float returned = JointDeg(body);
        ValidationUtil.Near(returned, 0f, 2f,
            "with the band on, the knocked arm must be back within 2 deg of its drawn pose after 1 s — " +
            "gravity is pulling it the other way the whole time, so this is the band overcoming the " +
            "arm's own weight (cap too low, spring too soft, or the target not at zero all land here)");
        ValidationUtil.Assert(reversals <= 2,
            $"the return reversed direction {reversals} times above {ReversalDeadbandRadPerSec} rad/s — " +
            "a band that rings is under-damped, and a flap that wobbles after every hit looks broken");

        // The guard: the same arm with the band off must sag under gravity. If it stays put, gravity
        // was never pulling away from rest and the return above proved nothing.
        arm.returnToRest = false;
        arm.BakeDrive();
        Simulate(200);
        float sagged = JointDeg(body);
        ValidationUtil.Assert(sagged < -60f,
            $"with the band OFF the arm must sag toward its lower limit (-90 deg) under its own weight, " +
            $"but it sits at {sagged:0.#} deg — the fixture is not arranged so gravity pulls away from " +
            "rest, so the return above is not the band's doing");

        return $"returns: knocked 45 deg off rest against gravity, back to {returned:0.##} deg within 1 s " +
               $"with {reversals} reversal(s); band off, the same arm sags to {sagged:0.#} deg.";
    }

    // --- 4. The push -----------------------------------------------------------------------------

    // A sibling toggle link, driven by the standard motor drive, sweeps a bar up through where the
    // arm lies. The blanket IgnoreRobotSelfCollision lays on the toggle's pairs at play time is laid
    // by hand here (Start never runs), first WITHOUT the rules to prove it stops the push, then with
    // them. The second fixture also carries a bracket drawn through the arm, which the rules must
    // leave muted — the other half of "mute what overlaps at rest, collide with everything else".
    private static string TheToggleCanPushIt()
    {
        float untouched = SwingToggleIntoArm(applyRules: false, withBracket: false,
            out _, out _, out float toggleAloneDeg);
        ValidationUtil.Assert(toggleAloneDeg > 45f,
            $"fixture: the toggle must swing up on its own (it reached {toggleAloneDeg:0.#} deg)");
        ValidationUtil.Assert(untouched < 5f,
            $"fixture guard: with every arm/toggle pair ignored and the rules NOT applied, the toggle must " +
            $"pass straight through the arm, but the arm turned {untouched:0.#} deg — so the >= 30 deg " +
            "below would pass on a robot where the blanket never ran, and prove nothing about the rules");

        // CONTROL: no blanket ever laid, no rules — do two links of one articulation collide at all
        // here? If this reads ~0 too, the fixture (not the rules) is what is broken.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        float plain = SwingToggleIntoArm(applyRules: false, withBracket: false, out _, out _, out _, layBlanket: false);
        Debug.Log($"PassiveArmValidation push CONTROL (never ignored, no rules): arm peak {plain:0.#} deg.");

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        float pushed = SwingToggleIntoArm(applyRules: true, withBracket: true,
            out int muted, out List<string> report, out float toggleDeg);
        ValidationUtil.Assert(muted == 1 && report.Count == 1 && report[0].StartsWith("Bracket"),
            $"the bracket drawn through the arm is the ONE pair that must stay muted (got {muted}: " +
            $"{string.Join(", ", report)}) — parts bolted through each other in CAD do not push each other apart");
        ValidationUtil.Assert(toggleDeg > 30f,
            $"fixture: the toggle must still swing up into the arm (it reached {toggleDeg:0.#} deg)");
        ValidationUtil.Assert(pushed >= 30f,
            $"the toggle must push the arm up at least 30 deg, but it turned {pushed:0.#} deg — " +
            "ApplyCollisionRules has to switch the arm/toggle pairs back ON after the blanket " +
            "switched them off (order 50 vs 60), or nothing on the robot can ever hit the arm");

        return $"pushed: rules off, the toggle passes through ({untouched:0.##} deg); rules on, the bracket " +
               $"through the arm stays muted and the toggle turns it {pushed:0.#} deg.";
    }

    // Builds arm + toggle (+ bracket), lays the blanket over the arm/toggle pairs, optionally
    // applies the arm's rules, sweeps the toggle up for 2 s and returns the arm's peak angle.
    private static float SwingToggleIntoArm(bool applyRules, bool withBracket,
        out int muted, out List<string> report, out float toggleDeg, bool layBlanket = true)
    {
        Physics.gravity = new Vector3(0f, -Gravity, 0f);
        GameObject root = MakeChassis(out _);
        // The arm sits LOW over the toggle (hinge 1 u up, bar 0.9..1.1) so the rising toggle meets the
        // bar while still shallow — about 40 deg, where its top face pushes mostly UP, 0.9 u in from
        // the bar's tip. With the bar at 2 u the first version of this fixture met it at 63 deg and
        // at the very tip, and a 63-deg bar pushes a tip mostly ALONG the arm: no torque about the
        // hinge, the arm read 0 deg while the toggle jammed on it for half a second and slid past.
        GameObject flap = MakeLink(root.transform, "Flap", new Vector3(0f, PushArmHeight, 0f), ArmMeshOffset, ArmMeshSize);
        // The weakest band: this case is about whether the toggle can REACH the arm at all, and a
        // band the toggle cannot overpower would read as the same failure.
        PassiveArm arm = BuildArm(flap, 0f, 90f, returnToRest: true, strength: 1f);
        ArticulationBody armBody = flap.GetComponent<ArticulationBody>();

        // The toggle: hinged 1 u out from the arm's hinge, a 5 u bar that lies flat pointing out and
        // rises through the MIDDLE of the arm's bar (first touch near x = 1.8 of a bar that ends at 4).
        // Hinged at x = 3 it met the bar 0.24 u from the tip, and box-against-box PhysX took the bar's
        // END face as the separating axis: an axial shove with no torque about the hinge, then a
        // tunnel straight through (measured 0.4 deg with the pair never ignored at all).
        GameObject toggle = MakeLink(root.transform, "Toggle", new Vector3(1f, 0f, 0f),
            new Vector3(2.5f, 0f, 0f), new Vector3(5f, 0.4f, 0.4f));
        AddMechanismJoint.Apply(toggle, AddMechanismJoint.JointType.Revolute, ArmAxis, Vector3.zero, 0f, 90f, useUndo: false);
        ArticulationBody toggleBody = toggle.GetComponent<ArticulationBody>();
        MotorActuator motor = toggle.GetComponent<MotorActuator>();
        ValidationUtil.Assert(motor != null, "fixture: the toggle must be wired as the standard motor");
        ArticulationDrive td = toggleBody.xDrive;
        ValidationUtil.Assert(td.driveType == ArticulationDriveType.Velocity,
            "fixture: the toggle runs on the motor's velocity drive");
        ValidationUtil.Near(td.forceLimit, motor.stallTorque, 1e-3f, "fixture: the toggle's cap is the motor's stall torque");
        ValidationUtil.Near(td.damping, motor.velocityDriveDamping, 1e-3f, "fixture: the toggle's damping is the motor's");

        // A third link, bolted to the chassis and drawn THROUGH the arm's bar. Siblings collide, so
        // left alone PhysX would eject the arm on the first step.
        if (withBracket)
        {
            // Placed by the arm's hinge, where the toggle's near end (radius 0.2 about x = 1) never reaches.
            GameObject bracket = ValidationUtil.MakeBox(root.transform, "Bracket", new Vector3(0.4f, PushArmHeight, 0f),
                new Vector3(0.6f, 0.6f, 0.6f));
            bracket.AddComponent<ArticulationBody>();   // a fixed link: a sibling of the arm, not its parent
        }

        // The blanket IgnoreRobotSelfCollision on the toggle would lay at execution order 50.
        if (layBlanket)
            foreach (Collider a in armBody.GetComponentsInChildren<Collider>())
                foreach (Collider t in toggleBody.GetComponentsInChildren<Collider>())
                    Physics.IgnoreCollision(a, t, true);

        report = new List<string>();
        muted = applyRules ? arm.ApplyCollisionRules(report) : 0;

        Physics.Simulate(0.02f);
        ValidationUtil.Near(JointDeg(armBody), 0f, 2f,
            "the arm must sit at rest before the toggle moves — with the rules on, a bracket drawn " +
            "through it was NOT muted and threw it (a pair that overlaps at rest has to be ignored)");

        // What the rules actually left: every arm/toggle pair's ignore flag, read back from PhysX.
        var pairs = new List<string>();
        foreach (Collider a in armBody.GetComponentsInChildren<Collider>())
            foreach (Collider t in toggleBody.GetComponentsInChildren<Collider>())
                pairs.Add($"{a.name}/{t.name}:{(Physics.GetIgnoreCollision(a, t) ? "IGNORED" : "collide")}");

        td.targetVelocity = ToggleSweepDegPerSec;
        toggleBody.xDrive = td;
        float peak = 0f;
        var trace = new System.Text.StringBuilder();
        for (int i = 0; i < 200; i++)   // 2 s
        {
            Physics.Simulate(ValidationUtil.StepSeconds);
            peak = Mathf.Max(peak, JointDeg(armBody));
            if (i % 10 == 9) trace.Append($" t{(i + 1) * 0.01f:0.00}: toggle {JointDeg(toggleBody):0.#} arm {JointDeg(armBody):0.#};");
        }
        toggleDeg = JointDeg(toggleBody);
        Debug.Log($"PassiveArmValidation push (blanket {layBlanket}, rules {(applyRules ? "on" : "off")}, bracket {withBracket}): muted {muted} " +
                  $"[{string.Join(", ", report)}]; pairs [{string.Join(", ", pairs)}]; toggle mass {toggleBody.mass:0.##}, " +
                  $"arm mass {armBody.mass:0.##}, arm cap {arm.bandForceLimit:0.#};{trace} peak arm {peak:0.#}.");
        return peak;
    }

    // --- 5. The window ---------------------------------------------------------------------------

    private static string TheWindowRecognisesIt()
    {
        Physics.gravity = new Vector3(0f, -Gravity, 0f);
        GameObject root = MakeChassis(out RobotMechanisms registry);
        GameObject flap = MakeLink(root.transform, "Flap", new Vector3(0f, 2f, 0f), ArmMeshOffset, ArmMeshSize);
        BuildArm(flap, 0f, 90f, returnToRest: true, strength: 3f);
        string id = UrdfPostProcessor.Slugify(flap.name);

        ValidationUtil.Assert(AddMechanismJointWindow.KindOf(flap) == AddMechanismJointWindow.MechanismKind.PassiveArm,
            $"the window must read the built link back as a passive arm (it read {AddMechanismJointWindow.KindOf(flap)}) " +
            "— read as an arm motor, the next Apply from the window wires a motor onto it");

        // The discriminator: a motor arm built the same way reads as the MOTOR, so 'PassiveArm'
        // is not simply what the detection says of every limited hinge.
        GameObject powered = MakeLink(root.transform, "Powered", new Vector3(0f, -2f, 0f), ArmMeshOffset, ArmMeshSize);
        AddMechanismJoint.Apply(powered, AddMechanismJoint.JointType.Revolute, ArmAxis, Vector3.zero, 0f, 90f, useUndo: false);
        ValidationUtil.Assert(AddMechanismJointWindow.KindOf(powered) == AddMechanismJointWindow.MechanismKind.ArmMotor,
            "a limited hinge with a motor on it is still an arm MOTOR to the window");

        // Re-kinding strips the band both ways, so a link never keeps a spring under a motor or a weld.
        AddMechanismJoint.Apply(flap, AddMechanismJoint.JointType.Revolute, ArmAxis, Vector3.zero, 0f, 90f, useUndo: false);
        ValidationUtil.Assert(flap.GetComponent<PassiveArm>() == null && flap.GetComponent<MotorActuator>() != null &&
                              registry.Find(id) != null,
            "re-applied as an arm motor, the link must lose its PassiveArm and gain a motor and a record");
        ValidationUtil.Assert(AddMechanismJointWindow.KindOf(flap) == AddMechanismJointWindow.MechanismKind.ArmMotor,
            "...and the window must now read it as the motor it is");

        BuildArm(flap, 0f, 90f, returnToRest: true, strength: 3f);
        ValidationUtil.Assert(flap.GetComponent<PassiveArm>() != null && flap.GetComponent<MotorActuator>() == null &&
                              registry.Find(id) == null,
            "back to a passive arm, the motor and its record must go again");

        AddMechanismJoint.Apply(flap, AddMechanismJoint.JointType.Fixed, Vector3.zero, Vector3.zero, 0f, 0f, useUndo: false);
        ValidationUtil.Assert(flap.GetComponent<PassiveArm>() == null,
            "welded Fixed, the link must lose its band — the Fixed branch of Apply is the one path " +
            "WireMechanism's strip never reaches");
        ValidationUtil.Assert(AddMechanismJointWindow.KindOf(flap) == AddMechanismJointWindow.MechanismKind.Fixed,
            "...and read as Fixed");

        return "window: the built arm reads back as a passive arm, a motor arm as a motor, and " +
               "re-kinding to motor or Fixed strips the band.";
    }

    // --- Fixture ---------------------------------------------------------------------------------

    private static GameObject MakeChassis(out RobotMechanisms registry)
    {
        GameObject root = new GameObject("PassiveBot");
        registry = root.AddComponent<RobotMechanisms>();
        registry.robotId = TestRobotId;
        ArticulationBody chassis = root.AddComponent<ArticulationBody>();
        chassis.immovable = true;   // the world end of the chain, so only the links can move
        ValidationUtil.MakeBox(root.transform, "ChassisMesh", Vector3.zero, new Vector3(6f, 1f, 6f));
        return root;
    }

    // An empty link with its mesh OFF to one side, so the link's own scale stays 1 (anchorPosition
    // is link-local) and the hinge has a real lever arm to the bar.
    private static GameObject MakeLink(Transform parent, string name, Vector3 localPosition,
        Vector3 meshOffset, Vector3 meshSize)
    {
        GameObject link = new GameObject(name);
        link.transform.SetParent(parent, false);
        link.transform.localPosition = localPosition;
        ValidationUtil.MakeBox(link.transform, name + "Mesh", link.transform.position + meshOffset, meshSize);
        return link;
    }

    // Through the real builder, hinged at the link origin about ArmAxis.
    private static PassiveArm BuildArm(GameObject link, float lower, float upper, bool returnToRest, float strength)
        => AddMechanismJoint.ApplyPassiveArm(link, ArmAxis, Vector3.zero, lower, upper,
            new AddMechanismJoint.PassiveArmOptions { returnToRest = returnToRest, bandStrength = strength },
            useUndo: false);

    // A drive far stiffer than any band, so the arm parks where it is told inside a step budget —
    // gravity stays ON here, unlike StartingPoseValidation's copy, because the cases that use this
    // are about what gravity does next.
    private static void DriveTo(ArticulationBody body, float degrees)
    {
        ArticulationDrive d = body.xDrive;
        d.driveType = ArticulationDriveType.Target;   // a constraint: parks exactly, whatever the band says
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

    // jointPosition is radians and NaN before the first step; Near() treats NaN as a failure.
    private static float JointDeg(ArticulationBody body)
        => body.jointPosition.dofCount > 0 ? body.jointPosition[0] * Mathf.Rad2Deg : float.NaN;

    private static float JointRadPerSec(ArticulationBody body)
        => body.jointVelocity.dofCount > 0 ? body.jointVelocity[0] : float.NaN;
}
