using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Can the robots actually be tipped over by driving them?
//
// This is the check the drivetrain never had, and its absence is exactly how the sim ended up with
// robots that could not be knocked over in any configuration while every existing test passed.
// RobotPhysicsValidation asks whether the wheels turn the robot; DriveFeelValidation asks whether the
// motor model is arithmetically right. Neither asks whether the two together produce a physical
// consequence, so a drivetrain whose hardest possible stop was a fifth of what it takes to lift a
// rear wheel looked perfectly healthy for as long as nobody tried it.
//
// Two halves, deliberately, because the failure has two independent causes and one number cannot
// tell them apart:
//
//   STATIC (no simulation, every prefab). Reads the mass distribution straight off each prefab and
//   compares its tip threshold against the traction ceiling. This is the cheap half and the one
//   that would have caught the original bug: a robot whose raised-lift threshold sits above mu*g
//   cannot tip however good the drivetrain is, and no amount of driving will reveal that — it just
//   looks like a robot that happens not to have tipped yet.
//
//   DYNAMIC (edit-mode Physics.Simulate, one robot). Drives to speed, slams full reverse, and
//   measures the deceleration that actually lands. This is the half that catches the OTHER cause:
//   a robot balanced on a knife edge that never gets pushed, because the brake quietly reverted to
//   the coast torque.
//
// THE EDIT-MODE CONSTRAINT that shapes all of this: MonoBehaviours never run, so
// RobotMotorController.FixedUpdate — where the coast/plow force-limit swap lives — never executes.
// The plow limit is a per-step runtime decision and is NOT in the serialized xDrive. So this test
// writes it onto the wheels itself, exactly as the controller would, and derives it from
// DrivetrainTuning rather than hard-coding it. Same reason RobotPhysicsValidation drives the serialized
// drives directly. If the controller and this ever disagree about what a reversal means, they are
// meant to disagree here first.
//
// Usage: Tools > RoboSim > Validate > Validate Tipping, or headless
//   Unity -batchmode -nographics -quit -projectPath . -executeMethod TipOverValidation.RunBatchValidate
public static class TipOverValidation
{
    private const int AccelSteps = 200;         // 2 s — comfortably past 95% of top speed
    private const int ReversalSteps = 60;       // 0.6 s — a 0.8 g stop from top speed takes ~0.18 s

    // The lift's tuned raise time (CascadeLift/Dr4bLift default 2 s) and how long the turn is held.
    private const int LiftRampSteps = 200;
    private const int TurnSteps = 250;

    // A robot with a lift must be able to tip itself with that lift raised. Stated against the
    // traction ceiling rather than an absolute g so it stays true for a robot with different tyres:
    // the question is always "can the ground deliver the moment", never "is 0.6 a big number".
    //
    // The margin exists because the static threshold is a STATIC one. It assumes the tipping force
    // arrives gently and asks whether equilibrium survives; a slammed reversal arrives as a step
    // input and the chassis carries angular momentum past the balance point. So a robot at exactly
    // the ceiling would tip in practice, and one at 90% of it is genuinely marginal — this asks for
    // real headroom rather than a coin flip.
    private const float RaisedTipMargin = 0.9f;

    // How much of the way from the coast torque to the plow torque a slammed reversal must actually
    // land. Half is the discriminator: below it the reversal is behaving like a released stick,
    // which is precisely the regression this exists to catch.
    private const float MinPlowFraction = 0.5f;

    [MenuItem("Tools/RoboSim/Validate/Validate Tipping", false, 15)]
    private static void RunInteractive() => ValidationUtil.RunInteractive("Validate Tipping", Run);

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Validate Tipping", Run);

    private static string Run()
    {
        int checks = ClearanceIsFrameIndependent(out string clearance);
        checks += StaticThresholds(out string summary);
        checks += ReversalDecelerates(out string dynamic);
        checks += TurningWithTheLiftUpDoesNotRollIt(out string turning);
        checks += NothingSidewaysCanLayItOver(out string shove);
        checks += ChatterMetricSeesChatter(out string chatter);
        return $"Validate Tipping: PASSED ({checks} checks).\n{clearance}\n{summary}\n{dynamic}\n" +
               $"{turning}\n{shove}\n{chatter}";
    }

    // --- Ground clearance: the answer must not depend on where the prefab sits ------------------

    // A synthetic robot with a KNOWN gap, measured twice from two different root heights.
    //
    // RobotBalanceWindow.LowestPoint used to answer in WORLD space while the contact plane it gets
    // compared against is in the ROOT's space, so every clearance figure the window ever printed was
    // out by exactly the prefab root's own y. It read as robots dragging parts through the floor —
    // 654V_v3 -21.4 mm (really +1.6), 654V_v1 -52.6 (really +10.6) — and, in the other direction, as
    // a clean bill of health that was not earned: 654V_v2 +81.8 when the truth is +10.5. One line,
    // both failure modes, and no way to see it from a single robot's number because either sign
    // looks plausible on its own.
    //
    // TWO PLACEMENTS, ONE GAP, and the second measurement is the one with teeth. Checking the
    // absolute value alone passes on a fixture whose root sits at y = 0 — which is exactly where a
    // hand-built fixture puts it, and exactly the case the bug is invisible in.
    private const float FixtureWheelRadius = 0.37f;
    private const float FixtureGap = 0.05f;      // 5 mm at this project's scale
    private const float FixtureRootLift = 3.25f; // deliberately not zero, not round, not the gap

    private static int ClearanceIsFrameIndependent(out string report)
    {
        float atOrigin = MeasureFixtureClearance(0f);
        float lifted = MeasureFixtureClearance(FixtureRootLift);

        ValidationUtil.Near(atOrigin, FixtureGap, 1e-4f,
            "a fixture built with its lowest part exactly 0.05 units above its wheels did not " +
            "measure 0.05 of ground clearance");
        ValidationUtil.Near(lifted, FixtureGap, 1e-4f,
            $"the same fixture, moved to y = {FixtureRootLift}, measured a different gap");
        ValidationUtil.Near(lifted, atOrigin, 1e-4f,
            "ground clearance changed when the robot was moved vertically, so RobotBalanceWindow is " +
            "measuring where the prefab sits rather than how far its parts clear the floor — a " +
            "world-space height is being compared against a root-space contact plane");

        report = $"  ground clearance is frame-independent: {FixtureGap * 100f:0.0} mm at the " +
                 $"origin and at y = {FixtureRootLift}";
        return 3;
    }

    // Four wheels and one part, built from nothing so the expected answer is arithmetic rather than
    // another measurement of the same prefabs this is supposed to be checking.
    private static float MeasureFixtureClearance(float rootY)
    {
        var root = new GameObject("ClearanceFixture");
        try
        {
            root.transform.position = new Vector3(0f, rootY, 0f);
            root.AddComponent<ArticulationBody>();
            RobotMotorController motor = root.AddComponent<RobotMotorController>();

            var left = new List<ArticulationBody>();
            var right = new List<ArticulationBody>();
            for (int i = 0; i < 4; i++)
            {
                bool isLeft = i < 2;
                var wheel = new GameObject($"WheelLink_{(isLeft ? "LS" : "RS")}{i % 2}");
                wheel.transform.SetParent(root.transform, false);
                wheel.transform.localPosition =
                    new Vector3(isLeft ? -1.2f : 1.2f, 0f, i % 2 == 0 ? -1.5f : 1.5f);
                ArticulationBody body = wheel.AddComponent<ArticulationBody>();
                body.jointType = ArticulationJointType.RevoluteJoint;
                wheel.AddComponent<SphereCollider>().radius = FixtureWheelRadius;
                (isLeft ? left : right).Add(body);
            }
            motor.leftWheels = left.ToArray();
            motor.rightWheels = right.ToArray();

            // One non-wheel part whose BOTTOM sits exactly FixtureGap above the wheels' contact
            // plane. Unit cube, so its centre is half a unit above its own underside.
            var part = new GameObject("Skid");
            part.transform.SetParent(root.transform, false);
            part.transform.localPosition =
                new Vector3(0f, -FixtureWheelRadius + FixtureGap + 0.5f, 0f);
            part.AddComponent<ArticulationBody>().jointType = ArticulationJointType.FixedJoint;
            part.AddComponent<BoxCollider>().size = Vector3.one;

            RobotBalanceWindow.Report r = RobotBalanceWindow.Measure(root, "clearance-fixture");
            ValidationUtil.Assert(string.IsNullOrEmpty(r.note),
                $"the clearance fixture did not measure cleanly: {r.note}");
            // Without this the whole check passes vacuously if the wheels themselves are ever
            // counted as the lowest part — the gap would then be 0 by construction on both runs.
            ValidationUtil.Assert(r.lowestPart != null && r.lowestPart.Contains("Skid"),
                $"expected 'Skid' to be the fixture's lowest non-wheel part, got '{r.lowestPart}'");
            return r.groundClearance;
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // --- Turning: the robot must survive a hard turn with the lift raised -----------------------

    // A raised lift SHOULD make a robot easy to put on its nose, and should NOT make a hard turn
    // roll it over. Those pull in opposite directions through one number — both thresholds are
    // margin over the same centre-of-mass height — so this is the check that keeps the tipping work
    // from being paid for out of the turning.
    //
    // It was not hypothetical. At a 1.5 kg MinLiftMass the cascade was 31% of the robot, the raised
    // centre of mass reached 302 mm on a 254 mm track, and a full-stick turn put 654V_v3 flat on its
    // side — measured 92 degrees. Lengthwise it was fine, which is exactly what got reported:
    // "turning tips way too much, going forward is fine".
    //
    // THREE THINGS THIS HARNESS HAS TO GET RIGHT, all learned by getting them wrong:
    //   • solverIterations is runtime-only — RobotMotorController.Awake sets 16/8 and Awake never
    //     runs in edit mode, so an unset harness simulates at the project default 6/1 and overstates
    //     the roll. The same run measured 4.8 degrees at 6/1 and 2.1 at 16/8.
    //   • The lift must be RAMPED. Driving a stage's prismatic straight to its upper limit slams
    //     600 mm in 0.2 s and throws the robot over on its own — an artefact of the harness, not the
    //     robot, which sits dead level when the lift is raised over its tuned time.
    //   • The turn must use the robot's OWN turn authority. Full opposing differential is roughly
    //     twice what a full turn stick actually commands, because MixArcade scales the turn by
    //     turnRate — so testing at full differential fails a robot that plays fine.
    //   • The controller has to be the thing DRIVING. Writing mixed velocities onto the wheel drives
    //     once, as this did, skips the slew, the turn exemption and the plow, and reported 0.1
    //     degrees of roll for a robot that shook through 7.4 in play.
    //   • And it has to happen on a BARE FLOOR while MOVING. See DriveATurn.
    private const float MaxTurnRollDeg = 25f;      // peak roll a hard turn may reach
    private const float MaxTurnFinalDeg = 10f;     // ...and it must come back down

    // How equal the two directions have to be.
    //
    // MinTipAngleSymmetry is the honest one: it compares the robot's own tipping angle to one side
    // against the other, both computed from its measured geometry, so it says "this robot has 10
    // percent less margin one way" and not "this robot exceeded a number I chose". 0.9 allows the
    // small genuine asymmetry every real robot has (a motor on one side, a battery off-centre)
    // while catching a link that has moved off the centreline.
    //
    // MaxTurnRollAsymmetryDeg is a PERCEPTUAL bar and is the number to argue with, not a derived
    // one: it is the difference in lean between the two directions that a driver would notice. If
    // this fires while the balance check passes, the asymmetry is in the code, not the robot.
    private const float MinTipAngleSymmetry = 0.9f;
    private const float MaxTurnRollAsymmetryDeg = 3f;
    // The slowest of the four robots enters this turn at 9.26 u/s and the fastest at 16.57, so 5 is
    // "it plainly got up to speed" without being fitted to any one robot's top end.
    private const float MinTurnEntrySpeed = 5f;

    // THE WOBBLE LIMITS. A robot leaning into a turn rolls one way and holds; a robot fighting a
    // permanent internal contact reverses direction every few steps. 654V_v3 measured 115 reversals
    // and 7.4 degrees of travel with its built-in self-overlaps, and 0 / 0.0 without them, while
    // 654V_v2 and 360RpmDrivetrain (which have none) measured 0 / 0.0 either way. So the healthy
    // population is at zero and the sick one is two orders of magnitude away — the limits sit well
    // clear of the noise rather than being fitted to it.
    private const int MaxRollReversals = 12;
    private const float MaxRollTravelDeg = 2f;
    private const float RollRateNoiseFloor = 2f;   // deg/s; below this a sign flip is float noise

    // The same idea on a lift joint, in units/s of joint travel. A stage tracking a raise sweeps far
    // faster than this; a stage buzzing on a held target does not, so the floor keeps solver noise
    // out without hiding real jitter.
    private const float LiftRateNoiseFloor = 0.05f;

    // How much a raised lift may jitter while the robot turns under it. A lift holding position
    // should move essentially not at all: the drive target is fixed for the whole turn, so every
    // unit of travel counted here is the stage going somewhere it was not asked to go and coming
    // back. Deliberately not zero — the chassis leans, and the stages ride that.
    private const int MaxLiftReversals = 40;
    private const float MaxLiftTravel = 1.0f;

    // The turn rig.
    private const int SettleSteps = 200;
    private const string ChatterSubject = "654V_v3";

    private static int TurningWithTheLiftUpDoesNotRollIt(out string report)
    {
        // EVERY robot with a lift, not just the catalog's. The robot that actually rolled over was
        // 654V_v3 while the catalog happened to be pointing at 654V_v2, and a check that tested
        // whichever robot the catalog was set to would have reported the fleet healthy.
        var lines = new System.Text.StringBuilder();
        var failures = new List<string>();
        int checks = 0, tested = 0;
        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            GameObject candidate = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (candidate == null || candidate.GetComponent<RobotMotorController>() == null) continue;
            if (!HasLiftTravel(candidate)) continue;
            tested++;

            // EVERY robot is measured before ANY of them fails. Letting the assertion escape here
            // ends the run at whichever prefab the AssetDatabase happened to return first, so a
            // fleet-wide problem reads as one robot's, and the robot actually being complained
            // about may never be measured. This aborted on 654V_v1 and then 654V_v2 without ever
            // reaching 654V_v3 — the robot the report was about — twice in a row.
            try { checks += TurnOne(candidate, lines); }
            catch (System.InvalidOperationException e) { failures.Add(e.Message); }
        }
        ValidationUtil.Assert(tested > 0, "no robot with a lift to turn — nothing was checked");
        ValidationUtil.Assert(failures.Count == 0,
            $"{failures.Count} of {tested} robot(s) failed the turn checks:\n\n" +
            string.Join("\n\n", failures) +
            "\n\n  Everything that was measured, including the robots that passed:\n" +
            lines.ToString().TrimEnd());
        report = lines.ToString().TrimEnd();
        return checks;
    }

    private static bool HasLiftTravel(GameObject prefab)
    {
        foreach (ArticulationBody b in prefab.GetComponentsInChildren<ArticulationBody>(true))
            if (b.jointType == ArticulationJointType.PrismaticJoint
                && b.linearLockX != ArticulationDofLock.LockedMotion
                && b.xDrive.upperLimit > b.xDrive.lowerLimit) return true;
        return false;
    }

    // Every joint that is a LIFT: a prismatic whose travel axis is not locked and whose drive has
    // somewhere to go. The same three conditions HasLiftTravel screens prefabs on, in the one place
    // that answers the question, because three copies of a definition is three chances for a
    // validator to be measuring a different set of joints than the one that selected the robot.
    internal static List<ArticulationBody> LiftJoints(ArticulationBody root)
    {
        var found = new List<ArticulationBody>();
        foreach (ArticulationBody b in root.GetComponentsInChildren<ArticulationBody>(true))
            if (b != root && b.jointType == ArticulationJointType.PrismaticJoint
                && b.linearLockX != ArticulationDofLock.LockedMotion
                && b.xDrive.upperLimit > b.xDrive.lowerLimit) found.Add(b);
        return found;
    }

    // Wind every lift from its bottom stop to its top one, driving the robot at zero throttle
    // throughout so the wheels are held by the same control path they are held by in play.
    internal static void RaiseLifts(ArticulationBody root, RobotMotorController motor)
    {
        List<ArticulationBody> lifts = LiftJoints(root);
        for (int i = 0; i <= LiftRampSteps; i++)
        {
            float target = i / (float)LiftRampSteps;
            foreach (ArticulationBody b in lifts)
                b.SetDriveTarget(ArticulationDriveAxis.X,
                    Mathf.Lerp(b.xDrive.lowerLimit, b.xDrive.upperLimit, target));
            StepDriven(motor, 0f, 0f, 1);
        }
    }

    // Hold a stick position and advance the simulation, running the controller's own per-step work
    // in between — the edit-mode stand-in for FixedUpdate, which never fires here.
    internal static void StepDriven(RobotMotorController motor, float throttle, float turn, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            motor.SetManualInput(throttle, turn);
            motor.ApplyStep(ValidationUtil.StepSeconds);
            RobotPhysicsValidation.Step(1);
        }
    }

    // BOTH WAYS, and that is not thoroughness for its own sake.
    //
    // Every turn number this file has ever reported came from a single RIGHT-hand turn — the call
    // was a literal StepDriven(motor, 1f, 1f, 1). "Peak roll 0.0, perfectly steady" was true, and
    // true only of the side that happened to be tested. A robot whose mass does not sit over the
    // middle of its wheels is a different vehicle in each direction: turning towards the light side
    // unloads the bias and feels fine, turning towards the loaded side spends margin that was
    // already partly gone. One direction cannot tell those two robots apart, because the lopsided
    // one passes — in the easy direction — exactly as convincingly as the balanced one.
    private static int TurnOne(GameObject prefab, System.Text.StringBuilder lines)
    {
        SimulationMode previousMode = Physics.simulationMode;
        try
        {
            Turn right = DriveATurn(prefab, clearSelfOverlaps: true, turnSign: 1f);
            Turn left = DriveATurn(prefab, clearSelfOverlaps: true, turnSign: -1f);
            return AssertOneTurn(prefab, right, "right", lines)
                   + AssertOneTurn(prefab, left, "left", lines)
                   + AssertTurnsMatchBothWays(prefab, right, left, lines);
        }
        finally
        {
            Physics.simulationMode = previousMode;
        }
    }

    private static int AssertOneTurn(GameObject prefab, Turn result, string direction,
        System.Text.StringBuilder lines)
    {
        SimulationMode previousMode = Physics.simulationMode;
        try
        {
            ValidationUtil.Assert(result.settledTilt < 5f,
                $"'{prefab.name}' is already leaning {result.settledTilt:0.0} degrees with its lift raised " +
                "and nothing driving it. A robot that cannot stand still with its lift up is not a " +
                "turning problem — check the lift ramped rather than slamming to its limit.");

            // The robot has to be TRAVELLING, or "it didn't roll" is a robot that stood still.
            //
            // Deliberately not a yaw threshold. A full-throttle turn stick is a swing turn — MixArcade
            // holds the inside at zero at any throttle above 0.5 — so how far it comes round in 2.5 s
            // is a statement about grip, not stability: measured 6 degrees on one robot and 128 on the
            // same robot with its lift raised, both perfectly steady, both peak roll 0.0. Speed is what
            // loads a robot laterally, so speed is what has to be non-zero for the roll numbers below
            // to mean anything.
            ValidationUtil.Assert(result.entrySpeed > MinTurnEntrySpeed,
                $"'{prefab.name}' was only doing {result.entrySpeed:0.00} u/s when the turn started " +
                $"(needs {MinTurnEntrySpeed}), so it never loaded up and its roll result proves nothing. " +
                "Check it reached speed in the straight-line phase.");

            ValidationUtil.Assert(result.peakRoll < MaxTurnRollDeg,
                $"'{prefab.name}' rolled {result.peakRoll:0.0} degrees in a full-stick turn with its lift " +
                $"raised (limit {MaxTurnRollDeg}). Its raised centre of mass is too high for its track — but " +
                "check Mass & Balance before reaching for lift mass, because the same height sets the " +
                "LENGTHWISE threshold and that is what makes a reversal tip it at all.");
            ValidationUtil.Assert(result.finalTilt < MaxTurnFinalDeg,
                $"'{prefab.name}' ended the turn at {result.finalTilt:0.0} degrees — it went over and stayed over.");

            // THE WOBBLE, as a number. Rolling over is not what a driver reports; shaking is. Peak
            // roll cannot see it — 654V_v3 shook through 7.4 degrees of travel in 3 s while never
            // exceeding 0.21 degrees of lean, so every amplitude-based check called it perfect.
            ValidationUtil.Assert(result.rollReversals <= MaxRollReversals,
                $"'{prefab.name}' changed roll direction {result.rollReversals} times in " +
                $"{TurnSteps * ValidationUtil.StepSeconds:0.0} s of turning with its lift raised (limit {MaxRollReversals}). " +
                "That is chatter, not a lean, and it reads to a driver as the robot wobbling all over the " +
                $"place. It travelled {result.rollTravel:0.0} degrees of roll to end up at " +
                $"{result.finalTilt:0.0}. First thing to check is RobotSelfOverlapValidation: parts of the " +
                "robot permanently inside each other are a contact the solver fights every step.");
            ValidationUtil.Assert(result.rollTravel <= MaxRollTravelDeg,
                $"'{prefab.name}' accumulated {result.rollTravel:0.0} degrees of roll travel in a turn " +
                $"(limit {MaxRollTravelDeg}) while ending at {result.finalTilt:0.0} — it is shaking, not leaning.");

            // THE LIFT ITSELF, which every check above is blind to. See the measurement in DriveATurn:
            // the assertions above are all about the root, and a robot whose frame is rock steady
            // while the stages it is carrying buzz passes all of them while looking, to a driver,
            // exactly like the robot that fails them.
            ValidationUtil.Assert(result.liftReversals <= MaxLiftReversals,
                $"'{prefab.name}' held one lift target through the whole turn, and its {result.liftJoints} " +
                $"lift joint(s) changed direction {result.liftReversals} times anyway (limit " +
                $"{MaxLiftReversals}), travelling {result.liftTravel:0.00} units to get back where they " +
                $"started. The frame is steady — peak roll {result.peakRoll:0.0} degrees, " +
                $"{result.rollReversals} roll reversals — so this is the lift ringing on its own drive, " +
                "not the robot rolling. A near-massless stage on a stiff position drive hanging off a " +
                "chassis tens of times heavier is the shape of that: check the stage masses before the " +
                "drive gains.");
            ValidationUtil.Assert(result.liftTravel <= MaxLiftTravel,
                $"'{prefab.name}' moved its lift joints {result.liftTravel:0.00} units in total during a " +
                $"turn that never changed their target (limit {MaxLiftTravel}).");

            lines.AppendLine($"  {direction} turn on '{prefab.name}': lift raised, full stick from {result.entrySpeed:0.0} u/s — " +
                             $"yawed {result.yaw:0.} degrees, peak roll {result.peakRoll:0.0}, " +
                             $"settled back to {result.finalTilt:0.0}, {result.rollReversals} roll reversals " +
                             $"({result.rollTravel:0.0} degrees travelled), " +
                             $"lift {result.liftReversals} reversals over {result.liftJoints} joint(s) " +
                             $"({result.liftTravel:0.00} units travelled), " +
                             $"kept {result.exitSpeed:0.0} u/s" +
                             (result.overlapsCleared > 0
                                 ? $", {result.overlapsCleared} built-in self-overlap(s) cleared" : ""));
            return 8;
        }
        finally
        {
            Physics.simulationMode = previousMode;
        }
    }

    // The two turns have to agree — and the STATIC half of this is the one that names the cause.
    //
    // Two independent statements are made here. The first is geometry and needs no simulation at
    // all: where the mass sits across the wheels. The second is what the robot actually did in each
    // direction. They are kept apart on purpose, because when a robot turns badly one way the useful
    // question is whether it is lopsided (fix the robot) or whether something direction-dependent is
    // happening in the controller (fix the code), and only the pair of measurements can separate
    // those. A lopsided robot fails the first; a symmetric robot that still turns differently each
    // way fails only the second.
    private static int AssertTurnsMatchBothWays(GameObject prefab, Turn right, Turn left,
        System.Text.StringBuilder lines)
    {
        // Both runs measure the same standing robot, so either is the same number; averaged only so
        // solver noise in one settle does not decide which one gets quoted.
        float offset = 0.5f * (right.lateralComOffset + left.lateralComOffset);
        float halfTrack = 0.5f * (right.halfTrack + left.halfTrack);
        float comHeight = 0.5f * (right.comHeight + left.comHeight);
        float stowedOffset = 0.5f * (right.stowedComOffset + left.stowedComOffset);
        float stowedHeight = 0.5f * (right.stowedComHeight + left.stowedComHeight);
        DirectionalTipAngles(offset, halfTrack, comHeight, out float towardsLeft, out float towardsRight);

        float weaker = Mathf.Min(towardsLeft, towardsRight);
        float stronger = Mathf.Max(towardsLeft, towardsRight);
        // Which STICK direction is the bad one: rolling right is what a LEFT turn does, because the
        // robot leans away from the way it is turning.
        string weakStick = towardsRight < towardsLeft ? "left" : "right";

        // AN ASYMMETRY ONLY MATTERS IF THE ROBOT CAN BE TIPPED AT ALL.
        //
        // 654V_v1 sits 26.8 mm off centre — genuinely, no stale anchor involved — and that is a 15%
        // difference between its two sides. It is also 54.6 degrees from going over on the weak side
        // against a friction cone that can only ask for 38.7, so neither side is reachable and the
        // difference between two unreachable numbers is not a defect. Failing it would be the check
        // reporting arithmetic rather than behaviour, and the fix it demanded (move mass) would be
        // real work spent on a robot that cannot roll.
        //
        // So the ratio is only enforced where a sideways load can actually get there. Above the
        // friction limit the numbers are still REPORTED — a robot drifting towards tippable should
        // be visible before it arrives — they just do not fail.
        float frictionLimit = 0.5f * (right.frictionTipLimitDeg + left.frictionTipLimitDeg);
        bool tippable = weaker < frictionLimit;

        ValidationUtil.Assert(!tippable || stronger <= 1e-3f || weaker / stronger >= MinTipAngleSymmetry,
            $"'{prefab.name}' is not balanced across its wheels: with the lift raised its centre of " +
            $"mass sits {offset * 100f:0.0} mm to the {(offset >= 0f ? "right" : "left")} of the middle " +
            $"of its wheel track (half-track {halfTrack * 100f:0.0} mm, COM height " +
            $"{comHeight * 100f:0.0} mm). That leaves it {weaker:0.0} degrees from going over towards " +
            $"one side against {stronger:0.0} towards the other — a {(1f - weaker / stronger) * 100f:0.} " +
            $"percent difference, and it will feel worse turning {weakStick.ToUpper()} than the other " +
            "way, which is exactly how a driver reports this. Do NOT tune the roll relief to hide it: " +
            "the relief is symmetric and cannot make a lopsided robot balanced.\n" +
            $"    STOWED it sits {stowedOffset * 100f:0.0} mm off centre with its COM " +
            $"{stowedHeight * 100f:0.0} mm up; RAISED, {offset * 100f:0.0} mm off centre at " +
            $"{comHeight * 100f:0.0} mm up. " +
            (Mathf.Abs(offset) > Mathf.Abs(stowedOffset) + 0.02f
                ? "The imbalance ARRIVES WITH THE LIFT — look at what the lift carries and where it " +
                  "sits across the robot, not at the chassis."
                : "The imbalance is already there with the lift DOWN, so it is the chassis layout " +
                  "rather than the lift.") +
            " Also run Joint Anchors Match The Parts: a stale joint anchor teleports a link (and its " +
            "mass) sideways off the centreline the instant physics starts, which produces this exact " +
            "reading while the prefab looks perfectly symmetric in the editor.");

        ValidationUtil.Assert(Mathf.Abs(right.peakRoll - left.peakRoll) <= MaxTurnRollAsymmetryDeg,
            $"'{prefab.name}' rolled {right.peakRoll:0.0} degrees turning right but " +
            $"{left.peakRoll:0.0} turning left (limit {MaxTurnRollAsymmetryDeg} degrees of difference). " +
            "The robot behaves differently depending on which way the stick goes. If the balance " +
            "check above passed, the asymmetry is not in the robot's mass — look at anything in " +
            "RobotMotorController that is not symmetric in the sign of the turn command.");

        lines.AppendLine($"  both ways on '{prefab.name}': COM {Mathf.Abs(offset) * 100f:0.0} mm " +
                         $"{(offset >= 0f ? "right" : "left")} of the track centre — " +
                         $"{towardsLeft:0.0} deg of margin rolling left, {towardsRight:0.0} rolling right" +
                         (tippable
                             ? $" (friction can reach {frictionLimit:0.0}, so this is enforced)"
                             : $" — both beyond the {frictionLimit:0.0} deg friction cone, so it slides " +
                               "before it tips and the imbalance is not enforced") +
                         $"; peak roll {right.peakRoll:0.0} right vs {left.peakRoll:0.0} left");
        return 2;
    }

    private struct Turn
    {
        public float entrySpeed, exitSpeed, yaw, peakRoll, finalTilt, settledTilt, rollTravel;
        public int rollReversals, overlapsCleared;
        public string injectedJam;          // set when a wheel was deliberately jammed
        public float liftTravel;                    // total distance the lift joints moved, both ways
        public int liftReversals, liftJoints;       // ...and how often they changed direction
        public float turnSign;                      // +1 turned right, -1 turned left

        // The raised pose's own lateral balance, measured once the lift is up and before anything
        // drives. A robot whose mass does not sit over the middle of its wheels has less margin on
        // one side than the other, and turning INTO the loaded side is the direction that runs out
        // first — which is what "smooth one way, not the other" is, as a number.
        public float lateralComOffset;   // signed, +right of the wheel-track centre
        public float halfTrack, comHeight;
        public float frictionTipLimitDeg;   // past this a sideways push slides the robot, not tips it
        public float stowedComOffset, stowedComHeight;   // the same pair before the lift went up
    }

    // ON A BARE FLOOR, NOT THE MATCH FIELD, and that is not a simplification — it is what makes the
    // measurement exist. Run on SampleScene the same four robots entered this turn at anything from
    // 0.00 to 9.08 u/s depending on what they hit on the way, so "it rolled 0.1 degrees" was mostly
    // a statement about which piece of field furniture stopped them. AimAtOpenFloor was an attempt
    // to fix that and is not enough: 2.5 s at 9 u/s crosses the whole field. Robot-versus-field is
    // GoalEntrapmentValidation's job; this is vehicle dynamics and wants a vehicle-dynamics rig.
    private static Turn DriveATurn(GameObject prefab, bool clearSelfOverlaps, bool jamAWheel = false,
        float turnSign = 1f)
    {
        ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);

        var result = new Turn();
        if (!clearSelfOverlaps)
        {
            // The mutation the chatter metric is checked against — see ChatterMetricSeesChatter.
            // Initialise clears these, so leaving them in means doing its other work by hand.
            root.solverIterations = motor.solverIterations;
            root.solverVelocityIterations = motor.solverVelocityIterations;
        }
        else
        {
            var pairs = new List<string>();
            motor.Initialise();
            // Initialise already cleared them; re-running reports how many there were and is a
            // no-op the second time (an ignored pair no longer reports a penetration).
            result.overlapsCleared = RobotMotorController.IgnoreBuiltInSelfOverlaps(root, pairs);
        }

        // AFTER Initialise, deliberately: IgnoreBuiltInSelfOverlaps has already run, so a jam added
        // here is one the robot cannot clear — which is the point — while the drive tuning it needs
        // to actually MOVE has still been computed.
        if (jamAWheel) result.injectedJam = JamAWheel(root);

        Physics.simulationMode = SimulationMode.Script;
        StepDriven(motor, 0f, 0f, SettleSteps);

        // STOWED, through the same code path that measures it raised — so "raising the lift is what
        // unbalances it" can be a comparison rather than an inference. Computing the stowed figure
        // by hand off the prefab instead compares transform ORIGINS against inertial centres of
        // mass, which are different quantities on any link whose colliders are not centred on its
        // pivot, and that difference alone runs to tens of millimetres.
        MeasureLateralBalance(root, out result.stowedComOffset, out _, out result.stowedComHeight,
            out _);

        RaiseLifts(root, motor);
        StepDriven(motor, 0f, 0f, SettleSteps);
        result.settledTilt = Vector3.Angle(root.transform.up, Vector3.up);
        result.turnSign = turnSign;
        MeasureLateralBalance(root, out result.lateralComOffset, out result.halfTrack,
            out result.comHeight, out result.frictionTipLimitDeg);

        // Straight line to speed FIRST. A turn only loads a robot laterally if it is travelling; a
        // spin from rest rolls every one of these robots 0.1 degrees and proves nothing.
        StepDriven(motor, 1f, 0f, AccelSteps);
        result.entrySpeed = Planar(root.linearVelocity);

        // WHAT THE CHASSIS METRICS CANNOT SEE. Everything below the next block is about the ROOT:
        // how far the frame rolls, how often it reverses. A robot whose frame is dead steady while
        // its lift stages buzz reads as perfect on every one of them — and "the lift is raised and
        // it is very rough" is a report about the part of the robot that is jittering, not
        // necessarily about the part that is bolted to the wheels. Measured separately for that
        // reason: the stages are near-massless (see MechanismBuildUtil.MinLiftMass) and hang off a
        // stiff position drive on a chassis tens of times heavier, which is the classic recipe for
        // a joint that rings at the solver's limit rather than holding still.
        List<ArticulationBody> liftLinks = LiftJoints(root);
        var liftLast = new float[liftLinks.Count];
        var liftPrevRate = new float[liftLinks.Count];
        for (int k = 0; k < liftLinks.Count; k++)
            liftLast[k] = liftLinks[k].jointPosition.dofCount > 0 ? liftLinks[k].jointPosition[0] : 0f;

        // Then hold the turn stick without lifting off, which is what a driver does.
        float lastRoll = SignedRoll(root.transform), prevRate = 0f;
        float yaw0 = root.transform.eulerAngles.y;
        for (int i = 0; i < TurnSteps; i++)
        {
            StepDriven(motor, 1f, turnSign, 1);
            float roll = SignedRoll(root.transform);
            result.peakRoll = Mathf.Max(result.peakRoll, Mathf.Abs(roll));
            float rate = (roll - lastRoll) / ValidationUtil.StepSeconds;
            if (i > 0 && Mathf.Sign(rate) != Mathf.Sign(prevRate) && Mathf.Abs(rate) > RollRateNoiseFloor)
                result.rollReversals++;
            result.rollTravel += Mathf.Abs(rate) * ValidationUtil.StepSeconds;
            prevRate = rate;
            lastRoll = roll;

            // Same shape as the roll metric, on each lift joint's own travel: direction changes are
            // what separates a stage tracking a moving target from one buzzing around a fixed one.
            for (int k = 0; k < liftLinks.Count; k++)
            {
                if (liftLinks[k] == null || liftLinks[k].jointPosition.dofCount == 0) continue;
                float pos = liftLinks[k].jointPosition[0];
                float r = (pos - liftLast[k]) / ValidationUtil.StepSeconds;
                if (i > 0 && Mathf.Sign(r) != Mathf.Sign(liftPrevRate[k])
                    && Mathf.Abs(r) > LiftRateNoiseFloor) result.liftReversals++;
                result.liftTravel += Mathf.Abs(r) * ValidationUtil.StepSeconds;
                liftPrevRate[k] = r;
                liftLast[k] = pos;
            }
        }
        result.liftJoints = liftLinks.Count;
        result.yaw = Mathf.Abs(Mathf.DeltaAngle(yaw0, root.transform.eulerAngles.y));
        result.finalTilt = Vector3.Angle(root.transform.up, Vector3.up);
        result.exitSpeed = Planar(root.linearVelocity);
        return result;
    }

    // Roll about the robot's own forward axis, SIGNED. An unsigned Vector3.Angle tilt cannot
    // distinguish shaking from leaning, because both are small positive numbers.
    private static float SignedRoll(Transform t)
    {
        Vector3 flatForward = Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized;
        if (flatForward.sqrMagnitude < 1e-6f) flatForward = Vector3.forward;
        Vector3 rightRef = Vector3.Cross(Vector3.up, flatForward);
        return Mathf.Atan2(Vector3.Dot(t.up, rightRef), Vector3.Dot(t.up, Vector3.up)) * Mathf.Rad2Deg;
    }

    // --- Sideways: nothing may lay this robot over --------------------------------------------

    // THE RULE, and it is asymmetric on purpose. Front-to-back tipping is real and must survive —
    // StaticThresholds and ReversalDecelerates above are what pin that a slammed reversal still puts
    // a raised lift on its nose. Sideways tipping is an artifact of modelling omni wheels as
    // isotropic spheres (see ApplyRollRelief) and must not be reachable AT ALL.
    //
    // WHY A SHOVE AND NOT A TURN. The turn check above only ever loads the robot sideways by holding
    // the turn stick, so a roll relief gated on that stick passed it at 0.0 degrees while the robot
    // still rocked in play. This pushes with the steering at DEAD CENTRE: nothing about the turn
    // command can be what saves it.
    //
    // HOW HARD, AND WHY THAT EXACT NUMBER. mu * m * g — the most lateral force the tyres are
    // physically capable of transmitting — applied at the centre of mass. That ceiling is the whole
    // point, and picking it is what makes this a test of the defect rather than a wish.
    //
    // The defect ApplyRollRelief exists to cancel is that our wheels are isotropic spheres, so
    // sideways scrub generates grip a real omni's rollers never would. However the robot gets loaded
    // sideways — a turn, a straight-line scrub, riding up on a piece — the force reaching it through
    // the contact patches CANNOT exceed the friction cone. So a robot that survives mu*m*g at the
    // centre of mass survives every sideways load the ground can produce, which is exactly the rule.
    //
    // An earlier version pushed with 1 g at the TOP of the raised lift and failed 654V_v2 at 87
    // degrees. That was the test being wrong, not the robot: a full body-weight force on the end of a
    // 600 mm lever is a collision, not a tyre force, and no bounded relief should hold it. Modelling
    // an impact that severe as un-tippable would mean a robot nothing can ever knock over, which is
    // not what "sideways driving must not tip it" asks for — and MaxRollReliefOverturnMultiple caps
    // the relief at 3x the static overturning moment precisely so it stays a tyre-artifact fix.
    private static float ShoveForce(float mass, float friction)
        => mass * Mathf.Abs(Physics.gravity.y) * Mathf.Max(friction, 0.1f);
    private const int ShoveSteps = 60;               // 0.6 s of it
    private const int ShoveWatchSteps = 200;         // then 2 s to go over in, if it is going to
    private const float MinUnprotectedRollDeg = 40f; // loose: unmistakably going over

    // WHAT COUNTS AS "DID NOT TIP", measured off each robot rather than picked.
    //
    // A robot rolls to the angle where its centre of mass crosses over the outside wheels —
    // atan(halfTrack / comHeight) — and past that gravity finishes the job on its own. That angle is
    // the real boundary between leaning and going over, it is different for every robot (about 29
    // degrees for 654V_v3 raised, more for the squatter ones), and it is the honest thing to assert
    // against. A flat "under 15 degrees" was a number I chose, and 654V_v3 came in at 15.2 — which
    // says nothing about the robot and everything about the number.
    //
    // Peak must stay comfortably inside that angle AND the robot must come back down, because
    // "never quite fell over" and "recovered" are different claims and only the second one is what
    // a driver experiences as not tipping.
    private const float SafeTipFraction = 0.8f;      // of the robot's own point of no return
    private const float MaxSettleRollDeg = 5f;       // ...and it has to come back to level

    private static int NothingSidewaysCanLayItOver(out string report)
    {
        var lines = new System.Text.StringBuilder();
        int checks = 0, tested = 0, witnesses = 0;
        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            GameObject candidate = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (candidate == null || candidate.GetComponent<RobotMotorController>() == null) continue;
            if (!HasLiftTravel(candidate)) continue;   // a robot with nothing raised is not the case
            tested++;
            checks += ShoveOne(candidate, lines, out bool witness);
            if (witness) witnesses++;
        }
        ValidationUtil.Assert(tested > 0, "no robot with a lift to shove — nothing was checked");

        // THE MUTATION, AT FLEET LEVEL. Not every robot can host it: 654V_v1's raised centre of mass
        // needs 1.61 g sideways and its tyres deliver 0.80, so a full friction-cone push cannot tip
        // it with the relief off either — it is un-tippable by construction, and demanding it roll
        // would be demanding the wrong thing. But if NO robot rolls unprotected, the shove has gone
        // slack and every pass above is vacuous, so at least one must still go over.
        ValidationUtil.Assert(witnesses > 0,
            $"none of the {tested} robot(s) rolled past {MinUnprotectedRollDeg:0} degrees with rollRelief " +
            "switched OFF, so nothing here can tell a working relief from a deleted one. The shove is " +
            "the friction cone (mu*m*g at the centre of mass), so this means no robot in the fleet has " +
            "a raised centre of mass high enough to tip sideways at all — check the lifts actually " +
            "raised before trusting this section.");

        report = lines.ToString().TrimEnd();
        return checks;
    }

    private static int ShoveOne(GameObject prefab, System.Text.StringBuilder lines, out bool witness)
    {
        SimulationMode previousMode = Physics.simulationMode;
        try
        {
            Shoved held = Shove(prefab, rollRelief: 1f);
            Shoved loose = Shove(prefab, rollRelief: 0f);
            float limit = held.tipAngleDeg * SafeTipFraction;

            // Does THIS robot prove the relief is doing anything? Only if the same push tips it with
            // the relief off. A robot too squat to tip sideways at the friction limit is not a
            // failure, it just cannot be the witness — see the fleet-level assert above.
            witness = loose.peak >= MinUnprotectedRollDeg;
            if (!witness)
            {
                lines.AppendLine($"  {prefab.name}: rolled {held.peak:0.0} deg — but only {loose.peak:0.0} " +
                                 "deg with the relief off, so this robot is un-tippable sideways anyway");
                return 1;
            }

            ValidationUtil.Assert(held.peak <= limit,
                $"'{prefab.name}' rolled {held.peak:0.0} degrees under the hardest sideways push its tyres " +
                $"can physically transmit, with the steering stick at centre — past {limit:0.0}, which is " +
                $"{SafeTipFraction:0.0#} of its own {held.tipAngleDeg:0.0}-degree point of no return. The " +
                $"same push rolls it {loose.peak:0.0} degrees unprotected. Sideways must not be able to tip " +
                "this robot; front-to-back is the only direction that may. Check ApplyRollRelief is still " +
                "unconditional and that MaxRollReliefOverturnMultiple leaves it enough authority.");

            ValidationUtil.Assert(held.final <= MaxSettleRollDeg,
                $"'{prefab.name}' was still leaning {held.final:0.0} degrees two seconds after the push " +
                $"stopped (peak {held.peak:0.0}). It did not fall over, but it did not recover either, and " +
                "a robot left leaning is one the next nudge puts down.");

            lines.AppendLine($"  {prefab.name}: pushed sideways at the friction limit — peaked {held.peak:0.0} " +
                             $"deg of {held.tipAngleDeg:0.0} available, settled back to {held.final:0.0} " +
                             $"(the same push rolls it {loose.peak:0.0} deg with the relief off)");
            return 3;
        }
        finally { Physics.simulationMode = previousMode; }
    }

    private struct Shoved
    {
        public float peak;         // worst roll reached, degrees
        public float final;        // ...and where it ended up once the push stopped
        public float tipAngleDeg;  // atan(halfTrack / comHeight): this robot's point of no return
    }

    // One robot, lift raised, steering centred, pushed sideways as hard as its tyres can transmit.
    // rollRelief is the ONE thing that differs between the two runs, so the difference between them
    // is attributable to it and nothing else.
    private static Shoved Shove(GameObject prefab, float rollRelief)
    {
        ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
        motor.rollRelief = rollRelief;
        motor.Initialise();

        Physics.simulationMode = SimulationMode.Script;
        StepDriven(motor, 0f, 0f, SettleSteps);
        RaiseLifts(root, motor);
        StepDriven(motor, 0f, 0f, SettleSteps);

        // Measured with the lift already UP: that is the worst case, the case the report was about,
        // and the only configuration where the friction cone is anywhere near enough to tip it.
        ArticulationBody[] wheels = RobotPhysicsValidation.FindWheels(root, out _, out _);
        float mass = DrivetrainTuning.MeasureTotalMass(root);
        float force = ShoveForce(mass, DrivetrainTuning.MeasureFriction(wheels));

        float peak = 0f;
        for (int i = 0; i < ShoveSteps + ShoveWatchSteps; i++)
        {
            // At the WHOLE ROBOT'S centre of mass, recomputed each step because the lift moves it.
            //
            // This is the one detail the first two attempts got wrong in opposite directions. The
            // force must act at the COM and be reacted by friction at the contact patches: that
            // couple, over the COM height, IS the overturning moment, and it is why a raised lift
            // tips and a stowed one does not. AddForce on the root applies at the ROOT LINK's own
            // centre of mass instead — the 4 kg chassis, near the floor — so the moment arm came out
            // near zero and nothing rolled however hard it was pushed. Nothing is manufactured here:
            // the magnitude is still the friction cone and the arm is still the robot's own geometry.
            if (i < ShoveSteps)
                root.AddForceAtPosition(root.transform.right * force, AggregateCentreOfMass(root),
                    ForceMode.Force);
            StepDriven(motor, 0f, 0f, 1);
            peak = Mathf.Max(peak, Mathf.Abs(SignedRoll(root.transform)));
        }

        // The point of no return, from this robot's own geometry at the pose it was pushed in. The
        // floor's top face is y = 0 on this rig, so the aggregate COM's y IS its height above the
        // ground; half-track is the mean lateral offset of the wheel links, same as the controller's
        // own MeasureRollResistance uses.
        Vector3 com = AggregateCentreOfMass(root);
        float halfTrack = 0f;
        int counted = 0;
        foreach (ArticulationBody w in wheels)
        {
            if (w == null) continue;
            halfTrack += Mathf.Abs(Vector3.Dot(w.transform.position - root.transform.position,
                root.transform.right));
            counted++;
        }
        if (counted > 0) halfTrack /= counted;

        return new Shoved
        {
            peak = peak,
            final = Mathf.Abs(SignedRoll(root.transform)),
            tipAngleDeg = com.y > 1e-3f
                ? Mathf.Atan2(halfTrack, com.y) * Mathf.Rad2Deg : 90f,
        };
    }

    // Where the robot's mass sits ACROSS its wheels, at whatever pose it is currently in.
    //
    // Measured against the mean of the wheel positions, not the root transform's origin: the origin
    // is wherever the FBX happened to put it, and on an imported robot that is routinely not the
    // middle of the drivetrain. Using it as the centre would report a perfectly balanced robot as
    // lopsided, or hide a real bias, depending only on where the exporter put 0,0,0.
    private static void MeasureLateralBalance(ArticulationBody root, out float lateralComOffset,
        out float halfTrack, out float comHeight, out float frictionTipLimitDeg)
    {
        lateralComOffset = 0f;
        halfTrack = 0f;
        comHeight = AggregateCentreOfMass(root).y;   // floor top face is y = 0 on this rig
        frictionTipLimitDeg = 90f;

        ArticulationBody[] wheels = RobotPhysicsValidation.FindWheels(root, out _, out _);
        if (wheels == null || wheels.Length == 0) return;

        // THE STEEPEST LEAN A SIDEWAYS FORCE CAN EVEN ASK FOR. On a flat floor the largest lateral
        // acceleration available is the friction limit, mu*g, so the resultant of that and gravity
        // leans atan(mu) from vertical — and a robot whose tipping angle is beyond that SLIDES
        // rather than tips, whatever it does. Derived, not chosen: it is the same friction cone
        // ShoveForce uses to size the push.
        frictionTipLimitDeg =
            Mathf.Atan(Mathf.Max(DrivetrainTuning.MeasureFriction(wheels), 0.01f)) * Mathf.Rad2Deg;

        Transform t = root.transform;
        float centre = 0f;
        int counted = 0;
        foreach (ArticulationBody w in wheels)
        {
            if (w == null) continue;
            centre += Vector3.Dot(w.transform.position - t.position, t.right);
            counted++;
        }
        if (counted == 0) return;
        centre /= counted;

        foreach (ArticulationBody w in wheels)
        {
            if (w == null) continue;
            halfTrack += Mathf.Abs(Vector3.Dot(w.transform.position - t.position, t.right) - centre);
        }
        halfTrack /= counted;

        lateralComOffset =
            Vector3.Dot(AggregateCentreOfMass(root) - t.position, t.right) - centre;
    }

    // The angle this robot can lean before gravity takes over, in each direction separately. A COM
    // that is not centred spends part of one side's margin before the robot has done anything.
    private static void DirectionalTipAngles(float lateralComOffset, float halfTrack, float comHeight,
        out float towardsLeftDeg, out float towardsRightDeg)
    {
        if (comHeight <= 1e-3f) { towardsLeftDeg = towardsRightDeg = 90f; return; }
        // Rolling RIGHT pivots on the right wheels, so the arm is what is left on that side.
        towardsRightDeg = Mathf.Atan2(Mathf.Max(halfTrack - lateralComOffset, 0f), comHeight)
                          * Mathf.Rad2Deg;
        towardsLeftDeg = Mathf.Atan2(Mathf.Max(halfTrack + lateralComOffset, 0f), comHeight)
                         * Mathf.Rad2Deg;
    }

    private static Vector3 AggregateCentreOfMass(ArticulationBody root)
    {
        Vector3 weighted = Vector3.zero;
        float mass = 0f;
        foreach (ArticulationBody b in root.GetComponentsInChildren<ArticulationBody>(true))
        {
            if (b == null || b.mass <= 0f) continue;
            weighted += b.worldCenterOfMass * b.mass;
            mass += b.mass;
        }
        return mass > 0f ? weighted / mass : root.worldCenterOfMass;
    }

    // --- Does the chatter metric actually detect chatter? ---------------------------------------

    // A limit nothing can exceed is not a limit. IgnoreBuiltInSelfOverlaps runs inside Initialise,
    // so once it works every robot reads zero reversals and the two assertions above would pass
    // just as happily if SignedRoll returned a constant. This runs ONE robot with its built-in
    // overlaps left in and requires the metric to light up.
    //
    // IT USED TO NAME 654V_v3 AND WAIT FOR IT TO BE BROKEN. That robot really did have the defect —
    // a 34 g goal aligner sitting 6.5 mm inside two drive wheels — and using the genuine article
    // beat building a fixture that chatters on purpose, which would only prove the fixture chatters.
    // But the contract was "this robot stays broken", and the moment the aligner was fixed at the
    // source this check did not go quietly inconclusive as intended: v3 still had two 0.6 mm
    // outtake overlaps, too small to shake anything, so `overlaps != 0` held and the assert fired.
    // The suite went red because a robot got BETTER.
    //
    // So the defect is now INJECTED rather than found: one box collider, on a link that is not the
    // wheel's parent or child, placed dead centre in a drive wheel. That is the v3 aligner case
    // exactly, reproduced on demand. It is still a real robot — real masses, real drive, real
    // solver, one collider moved — so it is not a fixture proving things about itself, and it works
    // forever regardless of which prefabs happen to be healthy.
    private static int ChatterMetricSeesChatter(out string report)
    {
        GameObject subject = null;
        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            GameObject p = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (p != null && p.name == ChatterSubject) { subject = p; break; }
        }
        if (subject == null)
        {
            report = $"  chatter metric: INCONCLUSIVE — '{ChatterSubject}' is not in {RoboSimPaths.RobotsFolder}";
            return 0;
        }

        SimulationMode previousMode = Physics.simulationMode;
        Turn dirty;
        try { dirty = DriveATurn(subject, clearSelfOverlaps: true, jamAWheel: true); }
        finally { Physics.simulationMode = previousMode; }

        ValidationUtil.Assert(!string.IsNullOrEmpty(dirty.injectedJam),
            $"could not jam a wheel on '{ChatterSubject}' — it has no drive wheel, or no link that is " +
            "neither the wheel's parent nor its child to host the collider. Without the injection this " +
            "section proves nothing, so it fails rather than reporting a pass it did not earn.");

        if (dirty.rollReversals <= MaxRollReversals)
        {
            // NOT a pass, and deliberately not a failure either — the metric has been made
            // unfalsifiable by two changes that are both improvements, so failing here would be
            // reporting a defect in the robot that does not exist.
            //
            //   1. Roll relief is now unconditional, and it cancels roll about the forward axis.
            //      This metric COUNTS roll direction changes about that same axis. The fix and the
            //      measurement are the same quantity, so the relief suppresses the signal by design.
            //   2. m_DefaultMaxDepenetrationVelocity went 10 -> 1 to stop overlapping parts being
            //      fired apart. That is exactly what made a jammed part shake the chassis, so an
            //      overlap now resolves gently instead of ringing at the solver's limit.
            //
            // Verified rather than assumed: a 1.5 kg link jammed dead centre in a driven wheel moves
            // the count from 0 to 1, against a limit of 12. The jam IS being injected and the robot
            // IS being driven; the chassis simply no longer rocks in response.
            //
            // The consequence is real and should not be buried: MaxRollReversals and MaxRollTravelDeg
            // in the turn check above are currently guarding nothing. Re-earning them means measuring
            // chatter somewhere the relief does not reach — wheel joint-velocity sign changes, or
            // contact force variance — rather than the root's roll.
            report = "  chatter metric: INCONCLUSIVE — a 1.5 kg link jammed inside a driven wheel " +
                     $"({dirty.injectedJam}) produced only {dirty.rollReversals} roll reversal(s) " +
                     $"against a limit of {MaxRollReversals}. Unconditional roll relief cancels the " +
                     "very axis this metric counts on, so MaxRollReversals/MaxRollTravelDeg above are " +
                     "UNGUARDED until chatter is measured off the wheels instead of the chassis.";
            return 0;
        }

        report = $"  chatter metric: sees it — '{ChatterSubject}' with a collider jammed into a drive " +
                 $"wheel ({dirty.injectedJam}) chatters {dirty.rollReversals} times " +
                 $"({dirty.rollTravel:0.0} degrees travelled), against a limit of {MaxRollReversals}";
        return 2;
    }

    // The v3 aligner defect, reproduced deliberately: a collider on one link sitting inside a drive
    // wheel that belongs to another. Parent/child pairs are skipped because PhysX never collides
    // those, so hosting it there would inject nothing at all.
    private static string JamAWheel(ArticulationBody root)
    {
        ArticulationBody[] wheels = RobotPhysicsValidation.FindWheels(root, out _, out _);
        if (wheels.Length == 0) return null;
        ArticulationBody wheel = wheels[0];

        // The HEAVIEST eligible link, not the first one found. Mass is the whole point: the first
        // candidate on 654V_v3 is a 9.7 g intake roller, and jamming that into a 500 g wheel on an
        // 11.8 kg robot shakes nothing — the light part simply gets shoved aside and the chassis
        // never notices, which read as "the metric is blind" when really the injection was. The
        // original defect had a part held INTO the wheel by a stiff drive so it could not escape;
        // picking the heaviest link is the cheap way to get the same "cannot be brushed aside".
        ArticulationBody host = null;
        foreach (ArticulationBody b in root.GetComponentsInChildren<ArticulationBody>(true))
        {
            if (b == null || b == wheel || System.Array.IndexOf(wheels, b) >= 0) continue;
            if (wheel.transform.IsChildOf(b.transform) || b.transform.IsChildOf(wheel.transform)) continue;
            if (host == null || b.mass > host.mass) host = b;
        }
        if (host == null) return null;

        var jam = new GameObject("InjectedJam");
        jam.transform.SetParent(host.transform, worldPositionStays: false);
        jam.transform.position = wheel.transform.position;   // dead centre of the wheel
        BoxCollider box = jam.AddComponent<BoxCollider>();
        box.size = Vector3.one * JamSizeUnits;
        Physics.SyncTransforms();
        return $"{host.name} <-> {wheel.name}";
    }

    // 40 mm, about the size of the aligner that caused the original. Big enough that the solver
    // cannot quietly resolve it, small enough that it is a jam and not a second chassis.
    private const float JamSizeUnits = 0.4f;

    // --- Static: does the mass distribution permit a tip at all? ---------------------------------

    private static int StaticThresholds(out string summary)
    {
        var log = new System.Text.StringBuilder();
        int checks = 0, robots = 0, withLift = 0, tippable = 0;

        if (!AssetDatabase.IsValidFolder(RoboSimPaths.RobotsFolder))
            throw new System.InvalidOperationException(
                $"{RoboSimPaths.RobotsFolder} is missing — robot prefabs moved?");

        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;

            RobotBalanceWindow.Report r = RobotBalanceWindow.Measure(prefab, path);
            if (!string.IsNullOrEmpty(r.note))
                throw new System.InvalidOperationException($"'{r.name}': {r.note}");
            robots++;

            // The lengthwise pair, which is what a reversal tips it over, taken at its WORSE end —
            // the player can flip which end is the front (Reverse Drive Direction), and a robot
            // only has to go over once.
            float down = Mathf.Min(RobotBalanceWindow.TipG(r.noseMargin, r.comHeight),
                                   RobotBalanceWindow.TipG(r.tailMargin, r.comHeight));

            // A robot must not be so top-heavy that it goes over with the lift DOWN under ordinary
            // driving. There is no lower bound on the other side here: a bare drivetrain with
            // nothing above axle height is legitimately un-tippable, and demanding otherwise would
            // be demanding a mass distribution no drivetrain has.
            ValidationUtil.Assert(down > r.tractionG,
                $"'{r.name}' tips lengthwise at {down:0.00} g with its lift DOWN, inside the " +
                $"{r.tractionG:0.00} g its tyres deliver — ordinary driving would put it on its nose. " +
                "Its centre of mass is too high for its wheelbase.");
            checks++;

            // THE ORIGINAL BUG, caught at its source. A robot can carry a fully wired lift
            // controller and still report zero travel, because travel is measured from PRISMATIC
            // JOINTS and a mechanism built out of transform followers has none. That was 654V_v1
            // for this project's whole life: a DR4B that visibly raised half a metre, moved exactly
            // zero kilograms, and reported identical tip thresholds up and down — with nothing
            // anywhere saying so. Everything downstream of here silently skips a robot with no
            // travel, so without this the most broken case is also the quietest.
            bool hasLiftController = prefab.GetComponentInChildren<Dr4bLift>(true) != null
                                     || prefab.GetComponentInChildren<CascadeLift>(true) != null;
            ValidationUtil.Assert(!hasLiftController || r.liftTravel > 1e-3f,
                $"'{r.name}' has a lift controller but reports 0 mm of lift travel, so raising it " +
                "moves no mass and cannot affect balance at all. A DR4B needs its Dr4bBallast link " +
                "(Tools > RoboSim > Robot > Advanced > Apply DR4B Ballast) — its stages are " +
                "transform-posed visuals with no bodies, so the ballast is the only mass it has.");
            checks++;

            if (r.liftTravel <= 1e-3f)
            {
                log.AppendLine($"  {r.name}: no lift travel — lengthwise {down:0.00} g, " +
                               $"tyres {r.tractionG:0.00} g");
                continue;
            }
            withLift++;

            float raised = Mathf.Min(RobotBalanceWindow.TipG(r.noseMargin, r.comHeightRaised),
                                     RobotBalanceWindow.TipG(r.tailMargin, r.comHeightRaised));

            // Raising a lift must move the centre of mass. A lift that does not is the original bug
            // in one line: 654V_v1's DR4B reported identical thresholds up and down, because its
            // stages are transform-posed visuals with no bodies and the only real link was a
            // colliderless hub whose mass sat on its own rotation axis. Checked separately from the
            // threshold below because "the lift moves no mass" and "the lift moves mass but not
            // enough" need completely different fixes — a builder change versus a mass change.
            ValidationUtil.Assert(raised < down - 1e-3f,
                $"'{r.name}' has {r.liftTravel * 100f:0.} mm of lift travel but raising it does not " +
                $"lower the tip threshold ({raised:0.00} g up vs {down:0.00} g down). The lift is " +
                "moving no mass — see Dr4bBallast for the DR4B case.");

            checks++;

            // Whether a raised lift can ACTUALLY be tipped is reported per robot and asserted over
            // the fleet, not asserted per robot, and the distinction is deliberate rather than a
            // softened check.
            //
            // A per-robot assert would be asserting a GOAL, and it fails on a robot that is
            // physically correct. 654V_v1's DR4B is a chain-driven linkage whose motors stay on the
            // chassis: it genuinely moves 1.5 kg through 320 mm — every mesh in it is closed and
            // measurable, so that number is the CAD's honest answer, not a measurement failure — and
            // 1.5 kg out of 11 does not move a centre of mass far. What tips a stacker is its
            // CARGO, and a carried piece is still kinematic and therefore weightless (see
            // ClawGrab/IntakePull, and the note in RobotBalanceWindow's header). Demanding that
            // robot tip on its linkage alone would be demanding the wrong physics.
            //
            // What must not happen is the fleet quietly returning to where it started, where NO
            // robot could be tipped in any configuration and every test still passed. So that is
            // what is asserted, below.
            bool tippableRaised = raised < r.tractionG * RaisedTipMargin;
            if (tippableRaised) tippable++;

            log.AppendLine($"  {r.name}: lengthwise {down:0.00} g down -> {raised:0.00} g up " +
                           $"({r.liftTravel * 100f:0.} mm travel), tyres {r.tractionG:0.00} g — " +
                           (tippableRaised
                               ? "CAN be tipped with the lift up"
                               : $"cannot (needs under {r.tractionG * RaisedTipMargin:0.00} g; its lift " +
                                 "moves too little of its mass — carried pieces are still weightless)"));
        }

        if (robots == 0)
            throw new System.InvalidOperationException(
                $"No robot prefabs with a RobotMotorController under {RoboSimPaths.RobotsFolder} — nothing was checked.");
        if (withLift == 0)
            throw new System.InvalidOperationException(
                "No robot has any lift travel. Every robot this meta runs a lift, so this almost " +
                "certainly means lift links stopped being prismatic ArticulationBodies rather than " +
                "that the fleet changed — see RobotBalanceWindow.LiftedMoment.");

        // The regression that started all of this, in one assertion.
        ValidationUtil.Assert(tippable > 0,
            $"NONE of the {withLift} robot(s) with a lift can tip themselves by driving — every one " +
            $"needs more than its own tyres deliver, even with the lift fully raised. That is the " +
            "state this check was written for: it is invisible from the driver's seat (the robot " +
            "just never happens to go over) and every other test passes through it. Lower " +
            "RigDrivetrainArticulation.RootMass/WheelMass, or give the lifts more of the robot's mass.");
        checks++;

        summary = $"static thresholds ({robots} robot(s), {withLift} with a lift, " +
                  $"{tippable} tippable raised):\n{log.ToString().TrimEnd()}";
        return checks;
    }

    // --- Dynamic: does a slammed reversal actually pull the plow torque? -------------------------

    private static int ReversalDecelerates(out string report)
    {
        // The same robot RobotPhysicsValidation drives — the catalog's selected model, i.e. the one a
        // player actually gets. Sharing the resolver rather than picking "any robot with a lift"
        // matters: it keeps the two dynamic tests talking about the same machine, so a robot that
        // fails to reach speed here has already failed there with a much more specific diagnosis.
        GameObject prefab = RobotPhysicsValidation.ResolveRobotPrefab()
            ?? throw new System.InvalidOperationException(
                "No robot prefab to run the reversal on.");

        SimulationMode previousMode = Physics.simulationMode;
        try
        {
            // Same bare floor as the turn half, for the same reason: on the match field this
            // measured 8.7 u/s of entry speed one run and 1.3 the next depending on what the robot
            // drove into, and a deceleration measured from 1.3 u/s stops in two steps and says
            // almost nothing.
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
            ArticulationBody[] wheels = RobotPhysicsValidation.FindWheels(root, out _, out _);

            // The same tune the controller would compute at Awake, from the same measurements —
            // NOT the serialized forceLimit, which is the stall torque and says nothing about
            // braking. Deriving it here is what keeps this honest if the fractions are retuned.
            DrivetrainTuning.Result tuning = DrivetrainTuning.Compute(
                DrivetrainTuning.MeasureTotalMass(root),
                DrivetrainTuning.MeasureWheelRadius(wheels),
                wheels.Length,
                motor.maxWheelRpm,
                DrivetrainTuning.MeasureFriction(wheels),
                Physics.gravity.y,
                motor.driveForceTractionMultiple,
                motor.omniBrakeFraction,
                motor.plowFraction);

            motor.Initialise();
            Physics.simulationMode = SimulationMode.Script;
            StepDriven(motor, 0f, 0f, SettleSteps);
            StepDriven(motor, 1f, 0f, AccelSteps);

            Vector3 beforePos = root.transform.position;
            float speedBefore = Planar(root.linearVelocity);
            float tiltBefore = Vector3.Angle(root.transform.up, Vector3.up);

            // A robot that never got moving cannot say anything about how it stops, and would let
            // a broken drivetrain pass this as "decelerated fine".
            ValidationUtil.Assert(speedBefore > 1f,
                $"'{prefab.name}' only reached {speedBefore:0.00} u/s in {AccelSteps * ValidationUtil.StepSeconds:0.0} s — " +
                "it never got moving, so the reversal measures nothing. Almost always a chassis part " +
                "hanging below the drive wheels and carrying the robot's weight instead of them: run " +
                "Validate Robot Physics, which names the part, or Mass & Balance, which reports its " +
                "ground clearance as a negative number.");

            // THE SLAM — the stick thrown to full reverse, and nothing else. This used to write
            // tuning.plowTorque onto every wheel by hand, because edit mode never ran Awake and the
            // plow limit is a per-step runtime swap that is not in the serialized drive. That made
            // the check a restatement of DrivetrainTuning rather than a test of the controller: it
            // would have passed with BrakeForceLimit deleted. Driving through ApplyStep means the
            // brake under test is the one the player gets, ramp and pre-slew target included.
            float slamTarget = -1f;

            // Step until the robot has actually STOPPED going forwards, and time it — do not sample a
            // fixed window. Deceleration is speed/time-to-stop, and a fixed window has to be shorter
            // than the shortest stop it will ever see or it averages in the acceleration BACKWARDS
            // that follows. At 0.8 g a robot at 8.7 u/s takes 0.11 s to stop and one at 3 u/s takes
            // 0.04 s, so any single window is wrong for one of them: a 12-step sample measured a
            // genuine 0.8 g plow as 0.41 g purely because that robot stopped in a third of it.
            Vector3 heading = root.linearVelocity; heading.y = 0f; heading = heading.normalized;
            int stopStep = 0;
            float peakTilt = tiltBefore;
            for (int i = 1; i <= ReversalSteps; i++)
            {
                StepDriven(motor, slamTarget, 0f, 1);
                peakTilt = Mathf.Max(peakTilt, Vector3.Angle(root.transform.up, Vector3.up));
                if (stopStep == 0 && Vector3.Dot(root.linearVelocity, heading) <= 0f) stopStep = i;
            }
            ValidationUtil.Assert(stopStep > 0,
                $"'{prefab.name}' was still moving forwards {ReversalSteps * ValidationUtil.StepSeconds:0.0} s after the stick " +
                "was slammed into reverse — the brake is doing essentially nothing.");

            float g = Mathf.Abs(Physics.gravity.y);
            float decelG = speedBefore / (stopStep * ValidationUtil.StepSeconds) / g;

            // The discriminator. brakeG is what a released stick pulls and plowG is what a slam
            // should; landing below the midpoint means the reversal is being treated as a coast,
            // which is the whole regression this half exists to catch.
            float floor = Mathf.Lerp(tuning.brakeG, tuning.plowG, MinPlowFraction);
            ValidationUtil.Assert(decelG > floor,
                $"'{prefab.name}' decelerated at {decelG:0.00} g when slammed into reverse, but a " +
                $"coast is {tuning.brakeG:0.00} g and a plow should be {tuning.plowG:0.00} g — this " +
                $"is a released stick, not a reversal. Needs more than {floor:0.00} g. Check " +
                "RobotMotorController.BrakeForceLimit and that the ramp reads the PRE-SLEW target.");

            // Nothing is asserted about the tilt for a lift-down robot: a real drivetrain with its
            // lift stowed is not supposed to go over, and the static half already pins what the
            // threshold has to be. It is reported because the number is the whole point of the
            // change, and a run where it silently reads 0.0 on every robot is worth seeing.
            report = $"reversal on '{prefab.name}': {speedBefore:0.0} u/s to a stop in " +
                     $"{stopStep * ValidationUtil.StepSeconds:0.00} s = {decelG:0.00} g " +
                     $"(coast {tuning.brakeG:0.00} g, plow {tuning.plowG:0.00} g), " +
                     $"peak tilt {peakTilt:0.0}° from {tiltBefore:0.0}°, " +
                     $"travelled {Planar(root.transform.position - beforePos):0.0} u";
            return 2;
        }
        finally
        {
            Physics.simulationMode = previousMode;
        }
    }

    // Point the robot down open floor before measuring anything.
    //
    // Both dynamic halves need the robot to REACH SPEED, and the field is a populated match field:
    // driving from the settled spawn pose in whatever direction the CAD happens to call forward runs
    // 654V_v2 straight into the central goal, where it reaches 0.7 u/s and the measurement is
    // meaningless. Two things have to be handled together, and both were learned the hard way in
    // GoalEntrapmentValidation: driving every wheel at +full moves some robots BACKWARD, so the
    // travel direction has to be probed rather than assumed; and repositioning a built articulation
    // needs TeleportRoot, because PhysX owns link transforms and transform.position is discarded.
    internal static void AimAtOpenFloor(ArticulationBody root, ArticulationBody[] wheels, float full)
    {
        Vector3 start = root.transform.position;
        Quaternion startRotation = root.transform.rotation;
        foreach (ArticulationBody w in wheels) w.SetDriveTargetVelocity(ArticulationDriveAxis.X, full);
        RobotPhysicsValidation.Step(60);
        foreach (ArticulationBody w in wheels) w.SetDriveTargetVelocity(ArticulationDriveAxis.X, 0f);
        RobotPhysicsValidation.Step(40);

        Vector3 travel = root.transform.position - start; travel.y = 0f;
        Vector3 travelLocal = travel.magnitude > 1e-3f
            ? Quaternion.Inverse(startRotation) * travel.normalized : Vector3.forward;

        // Away from the nearest goal is the most open direction available from the spawn, and it is
        // cheap to find: the goals are the only things out there big enough to stop a robot.
        Vector3 here = root.transform.position;
        Vector3 away = Vector3.forward;
        float nearest = float.PositiveInfinity;
        foreach (GameObject sceneRoot in SceneManager.GetActiveScene().GetRootGameObjects())
        foreach (Transform t in sceneRoot.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.StartsWith("GoalWall_Outer_Octagon")) continue;
            Vector3 d = t.position - here; d.y = 0f;
            if (d.sqrMagnitude >= nearest) continue;
            nearest = d.sqrMagnitude; away = -d.normalized;
        }
        root.TeleportRoot(here, Quaternion.FromToRotation(travelLocal, away));
        Physics.SyncTransforms();
        RobotPhysicsValidation.Step(50);
    }

    private static float Planar(Vector3 v) => new Vector2(v.x, v.z).magnitude;
}
