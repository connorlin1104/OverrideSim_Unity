using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Drive the robot at a goal, hard, and check it does not end up INSIDE it.
//
// THE BUG THIS EXISTS FOR, and the reason it is a simulation rather than a geometry check: "the bot
// gets stuck inside the goal" has now been diagnosed twice from static geometry and fixed twice, and
// it came back both times. First the ring corners were open — eight full-height slots per goal, all
// closed by SealGoalShell. Then the shell was found standing 45 mm proud of the goal's visual
// surface. GoalShellValidation proves both of those about the SHAPE of the shell, and passes.
//
// What neither can see is whether the SOLVER actually keeps the robot out of it. That depends on the
// robot: which of its parts arrives first, what that part is jointed to, and whether the mass ratio
// across that joint lets a contact transmit any force at all. A shell can be geometrically perfect
// and still be driven through by a link the solver cannot push back.
//
// So this drives the real robot into a real goal in the real field and asks the only question that
// matters — is any of the robot inside the ring afterwards. It is deliberately the harshest case:
// full throttle, straight at the goal, from close enough that it arrives at speed.
//
// THE RETRACTED/EXTENDED SPLIT IS THE POINT. A goal aligner is a pneumatic arm that holds the frame
// off the goal, and the bug only ever showed up with it RETRACTED — extended, it stops the robot
// before the frame gets close enough for any of this to matter, which is why the goal looked fixed.
// Edit-mode simulation never runs MonoBehaviours, so PneumaticActuator never poses anything: the
// joints sit at whatever the prefab serialized, which is the retracted pose. That is the bad case,
// for free, and it is what this runs.
//
// Usage: Tools > RoboSim > Validation > Validate Goal Entrapment, or headless
//   Unity -batchmode -nographics -quit -projectPath . \
//         -executeMethod GoalEntrapmentValidation.RunBatchValidate
public static class GoalEntrapmentValidation
{
    private const int ApproachSteps = 250;      // 2.5 s of full throttle at the goal
    private const int SettleSteps = 100;        // ...then let the contact resolve

    // How far INSIDE the ring a robot collider has to be before it counts as trapped. The ring's own
    // panels are 0.1 thick and the robot is allowed to touch them, so this has to clear the wall
    // itself plus the contact offset — it is "past the wall", not "against it".
    private const float InsideTolerance = 0.15f;

    // How close a robot collider has to get before the run counts as having reached the goal at all.
    // Generous, because the robot stops on its OUTERMOST part and that part is often a bumper or an
    // aligner standing well proud of the frame.
    // Measured on the bare-floor rig, all four robots, aligner in and out: they come to rest with
    // their nearest collider between 11 and 76 mm of the ring's panel PLANES. None of that is a gap
    // in the ordinary sense — the plane is the flat face of an octagon and a robot stopped against a
    // corner is legitimately that far from both faces that form it. 100 mm accepts every run that
    // actually made contact and still refuses a robot that never arrived. At 50 mm this quietly threw
    // away six runs of eight, including the one that reproduces the bug.
    private const float ArrivalTolerance = 1.0f;

    // Ramp for the pneumatic, in steps. Driving a 34 g actuator straight to its limit slams it there
    // in one frame, which is a harness artefact rather than what the button does.
    private const int AlignerRampSteps = 60;

    // The lift's own tuned raise time (CascadeLift/Dr4bLift default 2 s), same as TipOverValidation.
    private const int LiftRampSteps = 200;

    // Recoil is only watched inside this radius of the ring centre. Outside it the robot is still
    // lining up and any backwards component is steering, not a bounce.
    private const float ReboundWatchDistance = 4f;

    // How fast a robot may be travelling AWAY from a goal it just drove into. Every physics material
    // in the project is bounciness 0 and the goals carry no material at all, so the only thing that
    // can throw a robot back off a wall is depenetration — and the project's
    // m_DefaultMaxDepenetrationVelocity is Unity's stock 10, which in a world scaled at 1 unit = 0.1 m
    // is roughly the robots' own top speed. 1 u/s is "it stopped, and settled".
    private const float MaxReboundSpeed = 1f;

    // The bare-floor rig, matching TipOverValidation's.
    private const float FloorSize = 400f;
    private const float DropHeight = 2f;
    private const float ApproachRunUp = 8f;     // enough to reach full speed, with only the goal ahead
    private const string FloorMaterialPath = "Assets/ZeroBounce.physicMaterial";

    [MenuItem("Tools/RoboSim/Validation/Validate Goal Entrapment", false, 18)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive("Validate Goal Entrapment", Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Validate Goal Entrapment", Run);

    // Public so a diagnostic run can read the numbers without the assertion stopping at the first
    // failure — the report is what tells you WHICH part went in, which is the whole diagnosis.
    // What gets driven, per robot.
    //
    // The original two runs — aligner in and out, square onto a face, lift down — passed while the
    // bug was on screen, so the matrix is where it was too narrow rather than the tolerances.
    //   • LIFT UP is how the robot is actually driven at a goal, and it is not a cosmetic difference:
    //     the mass moves up, so the same impact pitches the frame much further and the nose goes
    //     somewhere quite different.
    //   • 22.5 DEGREES is a corner. A face-on run never loads the seam between two panels, which is
    //     the one piece of this shell whose seal has been rebuilt twice.
    private static readonly (bool extended, bool raised, float bearing)[] Cases =
    {
        (false, false, 0f),      // the original: square onto a face, everything stowed
        (true,  false, 0f),      // the bounce case
        (false, true,  0f),      // square on, driven the way a driver drives
        (false, true,  22.5f),   // onto a corner, driven the way a driver drives
    };

    public static string Run()
    {
        string previous = SceneManager.GetActiveScene().path;
        var log = new System.Text.StringBuilder();
        var failures = new List<string>();
        var inconclusive = new List<string>();
        int checks = 0, conclusive = 0;

        try
        {
            foreach (string robotPath in RoboSimPaths.RobotPrefabPaths())
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(robotPath);
                if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;

            foreach ((bool extended, bool raised, float bearing) in Cases)
            {
                Result r = DriveIntoGoal(prefab, extended, raised, bearing);
                checks++;
                string state = $"aligner {(extended ? "OUT" : "IN ")}, lift {(raised ? "UP  " : "DOWN")}, {bearing,4:0.#} deg";

                // The two failure modes read completely differently here, so both numbers are always
                // printed: peak climb says whether the robot got up onto the rim, and the bottom of
                // the deepest part against the rim top says where it ended up relative to it.
                string route = r.deepest <= InsideTolerance ? "stayed out"
                    : r.deepestBottom > r.rimTop - InsideTolerance ? "OVER THE RIM"
                    : "THROUGH THE WALL";
                log.AppendLine($"  {prefab.name} [{state}] -> '{r.goal}': hit at {r.impactSpeed:0.0} u/s, " +
                               $"bounced back at {r.reboundSpeed:0.0}, {r.actuators} actuator(s), " +
                               $"nearest {r.nearest * 100f:0.} mm, {r.deepest * 100f:0.} mm past the wall, " +
                               $"{r.intruders} part(s) inside, climbed {r.peakClimb * 100f:0.} mm — {route}" +
                               (r.deepestPart != null ? $" ('{r.deepestPart}'" +
                                   (r.deepestBody != null ? $" on link '{r.deepestBody}', {r.deepestMass:0.###} kg" +
                                       $" vs {r.chassisMass:0.##} kg chassis = {r.chassisMass / Mathf.Max(r.deepestMass, 1e-4f):0.} : 1" : " on the chassis") + ")"
                                   : ""));
                // A robot that never reached the goal is INCONCLUSIVE, not a pass — "nothing got
                // inside" is trivially true of a robot parked two metres away, and the first version
                // of this check passed exactly that way on all four robots. It is not a failure
                // either: the nearest goal to the spawn is a central stake with field structure
                // around it, and a low bare drivetrain legitimately cannot get its bumper to the
                // ring. Judged on CONTACT rather than distance travelled, because these prefabs'
                // origins sit well off their own geometry — a robot pressed hard against a goal can
                // still show its root several units away.
                if (r.nearest >= ArrivalTolerance)
                {
                    inconclusive.Add($"{prefab.name} [{state.Trim()}] (stopped {r.nearest * 100f:0.} mm short)");
                    continue;
                }
                conclusive++;

                // THE BOUNCE. A robot that hits a goal should stop against it, not be fired back off
                // it, and with every physics material at bounciness 0 there is no restitution in this
                // scene that could do that legitimately — a rebound here is the solver ejecting a
                // penetrating collider, not the goal being springy.
                if (r.reboundSpeed > MaxReboundSpeed)
                    failures.Add($"'{prefab.name}' hit '{r.goal}' at {r.impactSpeed:0.0} u/s with its " +
                                 $"aligner {(extended ? "EXTENDED" : "RETRACTED")} and was thrown back at " +
                                 $"{r.reboundSpeed:0.0} u/s (limit {MaxReboundSpeed}). Nothing in the scene has " +
                                 "any bounciness, so this is depenetration, not restitution: something got far " +
                                 "enough inside the wall that PhysX had to push it out hard. Check " +
                                 "Physics.defaultMaxDepenetrationVelocity against this world's scale, and the " +
                                 "mass of whichever link is leading the contact.");

                if (r.deepest > InsideTolerance && r.deepestBottom > r.rimTop - InsideTolerance)
                    failures.Add($"'{prefab.name}' ended up ON TOP OF '{r.goal}' — it climbed " +
                                 $"{r.peakClimb * 100f:0.} mm and finished with '{r.deepestPart}' " +
                                 $"{(r.deepestBottom - r.rimTop) * 100f:0.} mm above the rim, " +
                                 $"{r.deepest * 100f:0.} mm inside the ring's footprint, driving at " +
                                 $"{r.impactSpeed:0.0} u/s onto a ring only " +
                                 $"{(r.rimTop - r.restLowest) * 100f:0.} mm tall. This is not a hole in " +
                                 "the shell and sealing the corners will not touch it: the goal is short " +
                                 "enough to be driven over, so the fix is the rim's height or its shape, " +
                                 "not its thickness.");
                else if (r.deepest > InsideTolerance)
                    failures.Add($"'{prefab.name}' drove {r.deepest * 100f:0.} mm inside '{r.goal}' — " +
                                 (r.deepestBody != null
                                     ? $"through '{r.deepestPart}', which is on the {r.deepestMass:0.###} kg link " +
                                       $"'{r.deepestBody}'. That is {r.chassisMass / Mathf.Max(r.deepestMass, 1e-4f):0.}:1 against the " +
                                       $"{r.chassisMass:0.##} kg chassis. RigDrivetrainArticulation.WheelMass has the " +
                                       "measurement for what that means: at 47:1 across a joint the articulation solver " +
                                       "stops converging and contacts stop transmitting force, so a wall the link touches " +
                                       "cannot push it back. Raising that link's mass is the lever — but measure the turn " +
                                       "afterwards, because mass added out at a radius costs yaw authority"
                                     : $"through '{r.deepestPart}', which is on the chassis itself, so this is a " +
                                       "geometry hole rather than a mass-ratio problem — check GoalShellValidation"));
            }
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(previous)) EditorSceneManager.OpenScene(previous, OpenSceneMode.Single);
        }

        ValidationUtil.Assert(checks > 0, "no robot prefab was driven at a goal — nothing was checked");

        // THE tautology guard, at fleet level: if NOTHING reached a goal, this check proved nothing
        // at all and must not report success.
        ValidationUtil.Assert(conclusive > 0,
            $"none of the {checks} robot(s) got within {ArrivalTolerance * 100f:0.} mm of a goal, so nothing " +
            "here was actually tested. Either the robots cannot drive (run Validate Robot Physics) or the " +
            "goal this picks is unreachable — see NearestGoalRing.");

        ValidationUtil.Assert(failures.Count == 0,
            "a robot did not meet a goal cleanly (went inside it, or was thrown back off it):\n  - "
            + string.Join("\n  - ", failures) + "\n" + log);

        return $"Validate Goal Entrapment: PASSED ({conclusive} of {checks} robot(s) reached a goal, none got inside" +
               (inconclusive.Count > 0 ? $"; inconclusive: {string.Join(", ", inconclusive)}" : "") + $").\n{log}";
    }

    private struct Result
    {
        public string goal;
        public float approach;      // how far the robot actually closed on the goal
        public float probe;         // how far it moved during the direction probe
        public int actuators;
        public float impactSpeed;   // peak speed closing on the goal
        public float reboundSpeed;  // ...and peak speed thrown back off it
        public float nearest;       // signed distance of the closest collider to the ring: -ve = inside
        public float deepest;       // deepest penetration past a ring wall, world units
        public string deepestPart;
        public string deepestBody;  // the link that part belongs to, null when it is the chassis
        public float deepestMass;
        public float chassisMass;

        public float bearingDeg;    // approach heading: 0 = square onto a face, 22.5 = onto a corner
        public int intruders;       // how many robot colliders ended up past the ring's inner face
        public float rimTop;        // world y of the top of the ring
        public float deepestBottom; // world y of the bottom of the deepest-intruding part
        public float restLowest;    // the robot's lowest point once settled, before the run
        public float peakClimb;     // ...and how far above that it got while driving at the goal
    }

    private static Result DriveIntoGoal(GameObject prefab, bool extendAligner,
        bool raiseLift, float bearingDeg)
    {
        // COPY ONE GOAL RING ONTO A BARE FLOOR, and drive at that.
        //
        // Run in the populated field this measured whatever the robot hit on the way. Same eight
        // robot-runs, back to back: 654V_v1 arrived at 13.8 u/s and rebounded 5.9 one time and
        // arrived at 6.7 from 631 mm short the next, and 360RpmDrivetrain "bounced back at 8.5 u/s"
        // having stopped 1238 mm from the goal — off a wall, not the goal. Six of eight runs came
        // back INCONCLUSIVE. A collision test whose result depends on what else is in the room is not
        // measuring the collision.
        //
        // The ring IS its box colliders — there is no mesh in the physics of a goal — so copying the
        // panels' world transforms and sizes reproduces it exactly, and nothing else exists to hit.
        Scene field = EditorSceneManager.OpenScene(RoboSimPaths.MainScene, OpenSceneMode.Single);
        List<BoxCollider> source = NearestGoalRing(out string goalName, out Vector3 sourceCentre);
        if (source.Count == 0)
            throw new System.InvalidOperationException("no goal ring found in the field scene to drive at");

        var panels = new List<(Vector3 pos, Quaternion rot, Vector3 scale, Vector3 size, Vector3 centre, string name)>();
        foreach (BoxCollider p in source)
            panels.Add((p.transform.position - sourceCentre, p.transform.rotation, p.transform.lossyScale,
                        p.size, p.center, p.name));

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var floor = new GameObject("Floor");
        floor.transform.position = new Vector3(0f, -0.5f, 0f);
        BoxCollider fc = floor.AddComponent<BoxCollider>();
        fc.size = new Vector3(FloorSize, 1f, FloorSize);
        fc.sharedMaterial = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(FloorMaterialPath);

        var goalRoot = new GameObject("GoalUnderTest");
        var ring = new List<BoxCollider>();
        Vector3 ringCentre = Vector3.zero;
        foreach (var p in panels)
        {
            var wall = new GameObject(p.name);
            wall.transform.SetParent(goalRoot.transform);
            wall.transform.SetPositionAndRotation(p.pos, p.rot);
            wall.transform.localScale = p.scale;
            BoxCollider box = wall.AddComponent<BoxCollider>();
            box.size = p.size;
            box.center = p.centre;
            ring.Add(box);
        }
        Physics.SyncTransforms();

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene());
        instance.transform.position = new Vector3(0f, DropHeight, ApproachRunUp);
        Physics.SyncTransforms();
        ArticulationBody root = instance.GetComponent<ArticulationBody>()
            ?? throw new System.InvalidOperationException($"'{prefab.name}' has no ArticulationBody");
        ArticulationBody[] wheels = PhysicsSmokeTest.FindWheels(root, out _, out _);

        // Awake's work, and specifically IgnoreBuiltInSelfOverlaps. Without it this drives a robot
        // whose own parts are permanently jammed inside each other — 654V_v3's goal aligner sits
        // 6.5 mm inside two of its drive wheels — which is not the robot that meets the goal in play.
        root.GetComponent<RobotMotorController>().Initialise();

        var result = new Result
        {
            goal = goalName, chassisMass = root.mass, nearest = float.PositiveInfinity,
            bearingDeg = bearingDeg,
        };
        SimulationMode previousMode = Physics.simulationMode;
        try
        {
            // Point the robot at the goal and put it a couple of body-lengths short, so it arrives
            // at speed rather than crawling into contact.
            //
            // TeleportRoot, NOT transform.position. PlaceForValidation has already stepped the
            // simulation, so the articulation is built and PhysX owns every link transform — writing
            // transform.position here is silently discarded on the next step. That is not a
            // hypothetical: it is what made the first version of this check pass on all four robots
            // while none of them ever reached the goal.
            Physics.simulationMode = SimulationMode.Script;

            // Let it fall and settle BEFORE the probe. PlaceForValidation used to do this, and
            // dropping it when this moved to the bare-floor rig cost two robots their whole run:
            // 654V_v2 and 654V_v3 probed while still airborne, measured no travel, defaulted their
            // heading to +forward and reported "hit at 0.0 u/s, stopped 2.6 m short".
            PhysicsSmokeTest.Step(SettleSteps * 2);

            float full = root.GetComponent<RobotMotorController>().maxWheelRpm * 6f;

            // WHICH WAY IS FORWARD, measured rather than assumed. Driving every wheel at +full moves
            // some robots along wrapper.forward and others against it — that is what
            // RobotMotorController's invertLeft/invertRight exist for, and 654V_v1 is one of the
            // inverted ones. Aiming it at the goal and driving the wrong sign reverses away from
            // the goal, which the tautology guard correctly refuses to accept as a pass.
            Vector3 probeStart = root.transform.position;
            Quaternion probeRotation = root.transform.rotation;
            foreach (ArticulationBody w in wheels) w.SetDriveTargetVelocity(ArticulationDriveAxis.X, full);
            PhysicsSmokeTest.Step(60);
            foreach (ArticulationBody w in wheels) w.SetDriveTargetVelocity(ArticulationDriveAxis.X, 0f);
            PhysicsSmokeTest.Step(40);

            Vector3 travel = root.transform.position - probeStart; travel.y = 0f;
            result.probe = travel.magnitude;
            // The travel direction expressed in the robot's OWN frame, so the aim below works for a
            // robot that veers as well as one that simply drives backwards. A sign test on
            // transform.forward handles the inverted robots but not the crooked ones.
            Vector3 travelLocal = result.probe > 1e-3f
                ? Quaternion.Inverse(probeRotation) * travel.normalized : Vector3.forward;

            // Aim that direction at the goal and stand a couple of body-lengths off.
            //
            // TeleportRoot, NOT transform.position. The simulation has already stepped, so the
            // articulation is built and PhysX owns every link transform — writing transform.position
            // here is silently discarded on the next step. That is not hypothetical: it is what made
            // the first version of this check pass on all four robots while none reached the goal.
            // WHICH WAY IN. Taken off panel 0's own outward normal rather than from wherever the
            // robot happened to settle, so a bearing means the same thing on every robot: 0 drives
            // square onto the middle of a flat face, 22.5 drives at the corner where two panels meet
            // — the seam whose seal is the thing under test — and 45 onto the next face along.
            Vector3 faceNormal = ring[0].transform.up; faceNormal.y = 0f;
            Vector3 approach = faceNormal.sqrMagnitude > 1e-6f
                ? Quaternion.AngleAxis(bearingDeg, Vector3.up) * -faceNormal.normalized
                : (ringCentre - root.transform.position).normalized;
            approach.y = 0f; approach.Normalize();
            root.TeleportRoot(
                new Vector3(ringCentre.x, root.transform.position.y, ringCentre.z) - approach * ApproachRunUp,
                Quaternion.FromToRotation(travelLocal, approach));
            Physics.SyncTransforms();
            PhysicsSmokeTest.Step(50);   // settle onto the floor at the new pose

            // THE ALIGNER. Connor's report separates cleanly on this: retracted it gets stuck inside,
            // extended it bounces off — so the two states are different failures and have to be
            // driven separately. The pneumatic is the non-lift prismatic: a cascade stage is named
            // for its lift, everything else that slides is an actuator (654V_v3's is 'Component2:1',
            // 34 g, and it is the leading collider when it is out).
            // Awake never runs here, so the piston's drive is whatever is serialized — and on every
            // shipped prefab that is still the uncapped force limit. Bake it, or this measures the
            // old cylinder no matter what PneumaticActuator says.
            foreach (PneumaticActuator piston in root.GetComponentsInChildren<PneumaticActuator>(true))
                piston.BakeDrive();

            var actuators = new List<ArticulationBody>();
            foreach (ArticulationBody b in root.GetComponentsInChildren<ArticulationBody>(true))
                if (b != root && b.jointType == ArticulationJointType.PrismaticJoint
                    && b.linearLockX != ArticulationDofLock.LockedMotion
                    && b.xDrive.upperLimit > b.xDrive.lowerLimit
                    && !b.name.Contains("Cascade") && !b.name.Contains("Stage") && !b.name.Contains("Ballast"))
                    actuators.Add(b);
            result.actuators = actuators.Count;

            for (int i = 0; i <= AlignerRampSteps; i++)
            {
                float t = i / (float)AlignerRampSteps;
                foreach (ArticulationBody b in actuators)
                    b.SetDriveTarget(ArticulationDriveAxis.X, extendAligner
                        ? Mathf.Lerp(b.xDrive.lowerLimit, b.xDrive.upperLimit, t)
                        : b.xDrive.lowerLimit);
                PhysicsSmokeTest.Step(1);
            }
            PhysicsSmokeTest.Step(50);

            // THE LIFT, RAMPED. A raised lift is the state Connor actually drives in and it changes
            // this collision, not just the tipping: it moves mass up and back, so the same impulse
            // pitches the robot further, and the front of the frame dips onto the rim rather than
            // into the wall. Slamming a stage to its limit in one step is a harness artefact — see
            // TipOverValidation, which learned the same thing — so it goes over the lift's own tuned
            // two seconds.
            if (raiseLift)
            {
                var lifts = new List<ArticulationBody>();
                foreach (ArticulationBody b in root.GetComponentsInChildren<ArticulationBody>(true))
                    if (b != root && b.jointType == ArticulationJointType.PrismaticJoint
                        && b.linearLockX != ArticulationDofLock.LockedMotion
                        && b.xDrive.upperLimit > b.xDrive.lowerLimit
                        && !actuators.Contains(b)) lifts.Add(b);

                for (int i = 0; i <= LiftRampSteps; i++)
                {
                    float t = i / (float)LiftRampSteps;
                    foreach (ArticulationBody b in lifts)
                        b.SetDriveTarget(ArticulationDriveAxis.X,
                            Mathf.Lerp(b.xDrive.lowerLimit, b.xDrive.upperLimit, t));
                    PhysicsSmokeTest.Step(1);
                }
                PhysicsSmokeTest.Step(SettleSteps);
            }

            float startDistance = Planar(root.transform.position - ringCentre);
            result.restLowest = LowestPoint(root);

            // STEERED, not aimed once. A robot pointed at a goal and driven open-loop for six units
            // does not necessarily arrive: these drivetrains veer (unequal wheel drag, an off-centre
            // centre of mass, a part scraping), and 360RpmDrivetrain veered far enough to end up
            // FURTHER from the goal than it started. Correcting the heading every step is both more
            // robust and closer to what a driver does, and it means the tautology guard below fails
            // only when the robot genuinely cannot get there.
            ArticulationBody[] left = PhysicsSmokeTest.FindWheels(root, out ArticulationBody[] ls, out ArticulationBody[] rs);
            ArticulationBody[] bodies = root.GetComponentsInChildren<ArticulationBody>(true);
            DrivetrainTuning.TryMeasureCompositeCom(bodies, out Vector3 comPrev);
            for (int i = 0; i < ApproachSteps; i++)
            {
                Vector3 heading = travelLocal.magnitude > 1e-3f
                    ? root.transform.rotation * travelLocal : root.transform.forward;
                heading.y = 0f;
                Vector3 want = ringCentre - root.transform.position; want.y = 0f;
                // Signed heading error about world up, as a -1..1 differential.
                float error = Vector3.SignedAngle(heading.normalized, want.normalized, Vector3.up) / 45f;
                float turn = Mathf.Clamp(error, -0.5f, 0.5f);
                foreach (ArticulationBody w in ls) w.SetDriveTargetVelocity(ArticulationDriveAxis.X, full * (1f + turn));
                foreach (ArticulationBody w in rs) w.SetDriveTargetVelocity(ArticulationDriveAxis.X, full * (1f - turn));
                PhysicsSmokeTest.Step(1);

                // Speed along the approach line, signed: positive is closing on the goal, negative is
                // being thrown back off it. The peak of each is the whole point of this pass — "it
                // bounces off" is a report about the second number, and nothing measured it before.
                //
                // MEASURED ON THE CENTRE OF MASS, differentiated, not on root.linearVelocity. These
                // prefabs' roots sit well off their own geometry, so a robot that merely SLEWS ROUND
                // after clipping a goal gives its root a large backwards velocity while the machine
                // itself is going nowhere. Reading the root reported 2.4-3.3 u/s of "bounce" on
                // 654V_v2, which has no pneumatic at all and never leaves the wall.
                Vector3 toCentre = ringCentre - root.transform.position; toCentre.y = 0f;
                DrivetrainTuning.TryMeasureCompositeCom(bodies, out Vector3 comNow);
                Vector3 comVelocity = (comNow - comPrev) / ValidationUtil.StepSeconds;
                comPrev = comNow;
                float closing = Vector3.Dot(comVelocity, toCentre.normalized);
                result.impactSpeed = Mathf.Max(result.impactSpeed, closing);
                // Only count recoil once it is actually near the goal, or the slew-up at the start of
                // the run (where the robot is still shuffling into line) reads as a rebound.
                if (Planar(toCentre) < ReboundWatchDistance)
                    result.reboundSpeed = Mathf.Max(result.reboundSpeed, -closing);

                // How far the robot has picked itself up off the floor. A robot that drives THROUGH
                // a wall never leaves the ground; one that rides UP a rim it cannot climb shows the
                // whole climb here, and shows it while it is happening rather than only if it
                // happens to still be up there when the run ends.
                result.peakClimb = Mathf.Max(result.peakClimb, LowestPoint(root) - result.restLowest);
            }
            foreach (ArticulationBody w in wheels)
                w.SetDriveTargetVelocity(ArticulationDriveAxis.X, 0f);
            PhysicsSmokeTest.Step(SettleSteps);

            result.approach = startDistance - Planar(root.transform.position - ringCentre);
            MeasureIntrusion(root, ring, ringCentre, ref result);
        }
        finally
        {
            Physics.simulationMode = previousMode;
        }
        return result;
    }

    // The lowest point of the whole robot, in world y. Bounds are world-axis-aligned and so slightly
    // over-report a tilted part, which is the safe direction here: it can only make a climb look
    // smaller than it was.
    private static float LowestPoint(ArticulationBody root)
    {
        float lowest = float.PositiveInfinity;
        foreach (Collider col in root.GetComponentsInChildren<Collider>(true))
        {
            if (col == null || col.isTrigger || !col.enabled || !col.gameObject.activeInHierarchy) continue;
            lowest = Mathf.Min(lowest, col.bounds.min.y);
        }
        return lowest;
    }

    // The ring's vertical extent, in world units. The panels' local +Z is their height axis (see
    // FieldSetupTools: box.size is width, thickness, height) and local +Y is the outward normal.
    private static void RingSpan(List<BoxCollider> ring, out float top, out float bottom)
    {
        top = float.NegativeInfinity;
        bottom = float.PositiveInfinity;
        foreach (BoxCollider panel in ring)
        {
            float halfHeight = Mathf.Abs(panel.size.z * panel.transform.lossyScale.z) * 0.5f;
            float y = panel.transform.TransformPoint(panel.center).y;
            top = Mathf.Max(top, y + halfHeight);
            bottom = Mathf.Min(bottom, y - halfHeight);
        }
    }

    // How far past the ring's inner face the deepest robot collider has got.
    //
    // Measured against each panel's own PLANE rather than as a radius from the ring centre, because
    // the ring is an octagon and a radius would call the corners intrusions. A point is inside the
    // ring only when it is behind EVERY panel; the depth reported is how far behind the nearest one.
    //
    // WHY THIS NO LONGER TESTS col.bounds.center, WHICH IS WHAT IT DID AND WHY IT FOUND NOTHING.
    // A collider's bounds centre is one point in the middle of the part. A chassis rail spanning the
    // whole robot has its centre halfway down the robot, meters from the goal, however far its END is
    // driven into the ring — so the part that goes in is never the part that gets measured. Worse,
    // the old version skipped any collider whose bounds centre was outside the ring's own vertical
    // band, and this ring is 0.70 units tall against a robot the better part of 3 units tall: nearly
    // every collider on the robot was discarded before it was even tested. That is a check that
    // reports "nothing got inside" for a robot sitting in the goal, and it is why this rig called
    // four robots clean while the same four visibly wedge themselves in play.
    //
    // ClosestPoint instead: the point of the collider's own geometry nearest the goal's axis. For a
    // collider that has actually entered the ring, that point IS inside the ring, and its height is
    // what says whether the part came in over the rim or through the wall.
    private static void MeasureIntrusion(ArticulationBody root, List<BoxCollider> ring,
        Vector3 ringCentre, ref Result result)
    {
        RingSpan(ring, out float ringTop, out float ringBottom);
        result.rimTop = ringTop;

        foreach (Collider col in root.GetComponentsInChildren<Collider>(true))
        {
            if (col == null || col.isTrigger || !col.enabled || !col.gameObject.activeInHierarchy) continue;

            // Probe the goal's axis at the height this collider could reach, then ask the collider
            // for its own nearest point to it. ClosestPoint returns the probe itself when the probe
            // is already inside the collider, which is exactly the "it is in the goal" case.
            float probeY = Mathf.Clamp(col.bounds.center.y, ringBottom, ringTop);
            Vector3 probe = new Vector3(ringCentre.x, probeY, ringCentre.z);
            Vector3 p = col.ClosestPoint(probe);
            if (p.y > ringTop || p.y < ringBottom) continue;    // clear above or below the ring

            // Signed distance to the ring: for a convex octagon that is the LARGEST of the per-panel
            // plane distances, not the smallest. Taking the smallest picks the panel on the far side
            // of the goal, whose outward normal points away from the robot, so a robot standing well
            // clear of the goal measures as metres deep inside it — which is exactly what this
            // reported before the Max. Negative means behind every plane, i.e. genuinely enclosed.
            float outsideBy = float.NegativeInfinity;
            foreach (BoxCollider panel in ring)
            {
                Vector3 outward = panel.transform.up;          // local +Y is the panel's outward normal
                Vector3 face = panel.transform.TransformPoint(
                    panel.center + Vector3.up * (panel.size.y * 0.5f));
                outsideBy = Mathf.Max(outsideBy, Vector3.Dot(p - face, outward));
            }
            if (outsideBy < -InsideTolerance) result.intruders++;
            if (outsideBy >= result.nearest) continue;          // want the closest / deepest collider
            result.nearest = outsideBy;
            result.deepest = Mathf.Max(-outsideBy, 0f);         // behind every plane -> positive
            result.deepestPart = col.transform.parent != null
                ? $"{col.transform.parent.name}/{col.name}" : col.name;
            ArticulationBody owner = col.GetComponentInParent<ArticulationBody>(true);
            result.deepestBody = owner != null && owner != root ? owner.name : null;
            result.deepestMass = owner != null ? owner.mass : 0f;

            // OVER THE RIM, OR THROUGH THE WALL. These need opposite fixes — a rim a robot can ride
            // over is a goal that is too short to be rammed, a part that appears behind a sealed wall
            // is the solver failing to keep it out — and telling them apart afterwards from a depth
            // alone is guesswork. The bottom of the offending part against the top of the ring says
            // it outright: resting at rim height means it climbed.
            result.deepestBottom = col.bounds.min.y;
        }
    }

    // The goal ring nearest the validation spawn, so the robot has a short, clear run at it.
    private static List<BoxCollider> NearestGoalRing(out string goalName, out Vector3 centre)
    {
        var best = new List<BoxCollider>();
        goalName = null;
        centre = Vector3.zero;
        float bestDistance = float.PositiveInfinity;

        var rings = new Dictionary<string, List<BoxCollider>>();
        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (!t.name.StartsWith("GoalWall_Outer_Octagon")) continue;
            BoxCollider box = t.GetComponent<BoxCollider>();
            if (box == null) continue;
            string key = t.parent != null ? t.parent.name : "<no parent>";
            if (!rings.TryGetValue(key, out List<BoxCollider> ring)) rings[key] = ring = new List<BoxCollider>();
            ring.Add(box);
        }

        foreach (KeyValuePair<string, List<BoxCollider>> ring in rings)
        {
            if (ring.Value.Count < 3) continue;
            Vector3 c = Vector3.zero;
            foreach (BoxCollider b in ring.Value) c += b.transform.position;
            c /= ring.Value.Count;
            float d = Planar(c - PhysicsSmokeTest.ValidationSpawn);
            if (d >= bestDistance) continue;
            bestDistance = d; best = ring.Value; goalName = ring.Key; centre = c;
        }
        return best;
    }

    private static float Planar(Vector3 v) => new Vector2(v.x, v.z).magnitude;
}
