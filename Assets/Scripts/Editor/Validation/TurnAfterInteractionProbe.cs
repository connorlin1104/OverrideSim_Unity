using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

// PROBE. Reproduces, on the bare floor, the 2026-08-30 report: "at the beginning of the simulation it
// seems to be working fine, but once it interacts with any game elements (toggles, intaking
// pins/cups) it seems to disable turning ... going from right to left, it starts right and once the
// joystick moves left it goes straight. Sometimes both just move backwards and forwards."
//
// Nothing asserts here. For every robot it measures the SAME from-rest turn — full turn stick, both
// directions — first on a robot that has done nothing, then again after each thing a driver does in
// the first ten seconds of a match: a toggled mechanism (each MotorActuator pressed and released, and
// held), the lift raised, a cup taken by each intake, a wall rammed, and a turn reversed mid-turn
// from rest and while moving. Beside the yaw it prints what a driver cannot see: the path the robot
// travelled while it was meant to be spinning on the spot, each side's mean wheel speed (a locked
// side reads ~0, an airborne side reads the free-spin target), which wheels are off the floor, which
// NON-wheel collider is touching the floor, where the composite centre of mass sits over the wheels,
// and how far every mechanism joint has moved from where it rested. Whichever of those changes
// together with the yaw is the mechanism.
//
// The rig pumps every per-step hook a play-mode robot gets — RobotMotorController.ApplyStep,
// CascadeLift/JointCoupler/Dr4bBallast.ApplyStep and IntakePull.FixedUpdate — and runs the same
// one-time setup Awake/Start would (Initialise, Configure, BakeDrive(s), StabilizeAnchors,
// IgnoreAgainstRobot). A harness that skips any of those is measuring a robot that never ships.
public static class TurnAfterInteractionProbe
{
    // 1 s by default; ROBOSIM_PROBE_SETTLE_STEPS overrides it, because a spin started the moment the
    // spawn-drop stops settling measures differently from one started a second later.
    private static readonly int SettleSteps =
        int.TryParse(Environment.GetEnvironmentVariable("ROBOSIM_PROBE_SETTLE_STEPS"), out int n) && n > 0 ? n : 100;
    private const int SpinSteps = 150;     // 1.5 s of held turn — the same window the 08-29 ram probe used
    private const int StopSteps = 100;     // 1 s of centred sticks between turns
    private const int PressSteps = 100;    // 1 s on a mechanism button
    private const float BareFloorTopY = 0f;   // ValidationUtil's floor: a box whose top face is y=0
    private const int FullRaiseSteps = 250;    // CascadeLift.raiseSeconds is 2 s; give it the whole sweep
    private const float AirborneGap = 0.02f;
    private const float WheelRateNoiseFloor = 30f;   // deg/s, as MovingTurnValidation

    [MenuItem("Tools/RoboSim/Validate/Probes/Turn After Interaction", false, 73)]
    public static void Probe() => ValidationUtil.RunInteractive("Turn After Interaction", Run);

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Turn After Interaction", Run);

    private class Rig
    {
        public GameObject prefab;
        public ArticulationBody root;
        public RobotMotorController motor;
        public ArticulationBody[] wheels, left, right;
        public HashSet<ArticulationBody> wheelSet;
        public float radius;
        public RobotMechanisms mechs;
        public CascadeLift[] lifts;
        public JointCoupler[] couplers;
        public Dr4bBallast[] ballasts;
        public IntakePull[] intakes;
        public ArticulationBody[] links;          // every non-root, non-wheel link with a DOF
        public Dictionary<ArticulationBody, float> restJoint = new Dictionary<ArticulationBody, float>();
        public string intakeError;
        public float floorY;                       // top of whatever the robot is standing on
        public string where;                       // "bare floor" or "field"
        public MinHeightClamp[] clamps = new MinHeightClamp[0];   // the field's pieces, ticked like play
        public Vector3 spawnPos;
    }

    private struct Turn
    {
        public string label;
        public float yawDeg, pathU, meanSpeed, leftSpin, rightSpin;
        public int reversals, airborne;
        public string gaps, contacts;
    }

    private static readonly MethodInfo IntakeFixedUpdate =
        typeof(IntakePull).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo ClampAwake =
        typeof(MinHeightClamp).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo ClampFixedUpdate =
        typeof(MinHeightClamp).GetMethod("FixedUpdate", BindingFlags.Instance | BindingFlags.NonPublic);

    private static string Run()
    {
        var sb = new StringBuilder();
        string only = Environment.GetEnvironmentVariable("ROBOSIM_PROBE_ROBOT");
        int robots = 0;
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;
            if (!string.IsNullOrEmpty(only) && prefab.name != only) continue;
            robots++;
            OneRobot(prefab, sb);
        }
        return $"Turn after interaction — {robots} robot(s):\n{sb.ToString().TrimEnd()}";
    }

    private static void OneRobot(GameObject prefab, StringBuilder sb)
    {
        sb.AppendLine($"=== {prefab.name} ===");

        Turn baseR = default, baseL = default;
        if (MinLinkMass > 0f) sb.AppendLine($"  (every mechanism link raised to at least {MinLinkMass} mass)");
        if (!FieldOnly)
        Scenario(prefab, sb, "baseline", null, (r, lines) =>
        {
            baseR = Spin(r, 0f, +1f, "baseline R");
            Stop(r);
            baseL = Spin(r, 0f, -1f, "baseline L");
            lines.Add(baseR); lines.Add(baseL);
        });
        if (BaselineOnly) { sb.AppendLine(); return; }

        // The lift, the way TipOverValidation/MovingTurnValidation raise it (drive targets).
        if (!FieldOnly)
        Scenario(prefab, sb, "lift raised (drive targets)", r =>
        {
            TipOverValidation.RaiseLifts(r.root, r.motor);
            Hold(r, SettleSteps);
        }, StandardSpins);

        // Every mechanism a button can drive: pressed for a second and released (hold engages on a
        // limited joint), and separately held through the turn (a roller intake is held while
        // driving over a cup).
        // Read everything needed from this rig NOW: every Scenario below opens a fresh scene, which
        // destroys these objects, and a destroyed ArticulationBody throws on touch.
        var mechIds = new List<string>();
        var probeRig = Build(prefab);
        if (probeRig.mechs != null)
            foreach (RobotMechanisms.Mechanism m in probeRig.mechs.mechanisms)
                if (m != null && m.motor != null) mechIds.Add(m.id);
        int intakeCount = probeRig.intakes.Length;
        bool hasPassiveArms = probeRig.root.GetComponentsInChildren<PassiveArm>(true).Length > 0;
        foreach (string id in FieldOnly ? new List<string>() : mechIds)
        {
            Scenario(prefab, sb, $"toggle '{id}' (press 1 s, release)", r =>
            {
                MotorActuator m = Motor(r, id);
                for (int i = 0; i < PressSteps; i++) { m.SetInput(1f); Tick(r, 0f, 0f); }
                m.SetInput(0f);
                Hold(r, SettleSteps);
            }, StandardSpins);

            Scenario(prefab, sb, $"hold '{id}' (button held through the turns)", r =>
            {
                MotorActuator m = Motor(r, id);
                for (int i = 0; i < PressSteps; i++) { m.SetInput(1f); Tick(r, 0f, 0f); }
            }, (r, lines) =>
            {
                MotorActuator m = Motor(r, id);
                lines.Add(Spin(r, 0f, +1f, "R", () => m.SetInput(1f)));
                Stop(r, () => m.SetInput(1f));
                lines.Add(Spin(r, 0f, -1f, "L", () => m.SetInput(1f)));
            });
        }

        // Each intake takes a cup (the real capture path), then carries it through the turns.
        for (int k = 0; k < (FieldOnly ? 0 : intakeCount); k++)
        {
            int index = k;
            Scenario(prefab, sb, $"intake #{index} captured a cup", r =>
            {
                IntakePull intake = r.intakes[index];
                MotorActuator m = intake.GetComponentInParent<MotorActuator>();
                Rigidbody cup = MakeCup(intake.transform.position + Vector3.up * 0.5f);
                bool took = intake.TryCapture(cup);
                if (!took) throw new InvalidOperationException("TryCapture refused the cup");
                // Hold the intake button while the piece glides in, then let go (dropWhenIdle is off).
                for (int i = 0; i < PressSteps; i++) { if (m != null) m.SetInput(1f); Tick(r, 0f, 0f); }
                if (m != null) m.SetInput(0f);
                Hold(r, SettleSteps);
                if (cup == null || !cup.isKinematic || !intake.IsCarrying(cup))
                    throw new InvalidOperationException("the cup is not being carried after the glide");
            }, StandardSpins);
        }

        // The 08-29 ram, for the record: full throttle into a wall, then the same two turns.
        if (!FieldOnly)
        Scenario(prefab, sb, "rammed a wall at full throttle", r =>
        {
            Vector3 fwd = r.motor.DriveForwardWorld;
            ValidationUtil.MakeBox(null, "Wall", r.root.transform.position + fwd * 10f + Vector3.up * 3f,
                Quaternion.LookRotation(fwd, Vector3.up), new Vector3(30f, 6f, 1f));
            for (int i = 0; i < 150; i++) Tick(r, 1f, 0f);
            Stop(r);
        }, StandardSpins);

        // ROUND 2 — what round 1 pointed at.

        // The cascade raised all the way by its own motor (round 1 pressed for 1 s of a 2 s sweep).
        if (mechIds.Contains("cascademotor") && !FieldOnly)
        {
            Scenario(prefab, sb, "cascade FULL raise (2.5 s press, release)", r => Press(r, "cascademotor", FullRaiseSteps),
                StandardSpins);
            Scenario(prefab, sb, "cascade FULL raise, then scoring intake held", r => Press(r, "cascademotor", FullRaiseSteps),
                (r, lines) =>
                {
                    MotorActuator m = Motor(r, "scoringmech");
                    lines.Add(Spin(r, 0f, +1f, "R", () => m.SetInput(1f)));
                    Stop(r, () => m.SetInput(1f));
                    lines.Add(Spin(r, 0f, -1f, "L", () => m.SetInput(1f)));
                });
        }

        // The ram again, but backed a metre off the wall first — so a corner clipping the wall is out.
        if (!FieldOnly)
        Scenario(prefab, sb, "rammed a wall, then backed off 1 s", r =>
        {
            Vector3 fwd = r.motor.DriveForwardWorld;
            ValidationUtil.MakeBox(null, "Wall", r.root.transform.position + fwd * 10f + Vector3.up * 3f,
                Quaternion.LookRotation(fwd, Vector3.up), new Vector3(30f, 6f, 1f));
            for (int i = 0; i < 150; i++) Tick(r, 1f, 0f);
            for (int i = 0; i < 100; i++) Tick(r, -1f, 0f);
            Stop(r);
        }, StandardSpins);

        // The knocked arm on its own: every PassiveArm displaced by the 0.4 rad the ram left, no wall.
        if (hasPassiveArms && !FieldOnly)
            Scenario(prefab, sb, "passive arms kicked 0.4 rad (no wall)", r =>
            {
                foreach (PassiveArm a in r.root.GetComponentsInChildren<PassiveArm>(true))
                {
                    ArticulationBody b = a.body != null ? a.body : a.GetComponent<ArticulationBody>();
                    if (b == null || b.dofCount == 0) continue;
                    ArticulationReducedSpace p = b.jointPosition;
                    p[0] = Joint(b) + 0.4f;
                    b.jointPosition = p;
                }
                Tick(r, 0f, 0f);
            }, StandardSpins);

        // THE FIELD. Same turns on the shipped scene: tiles, seams, tape, walls.
        Scenario(prefab, sb, "baseline", null, (r, lines) =>
        {
            lines.Add(Spin(r, 0f, +1f, "R"));
            Stop(r);
            lines.Add(Spin(r, 0f, -1f, "L"));
        }, onField: true);
        if (mechIds.Contains("scoringmech"))
            Scenario(prefab, sb, "hold 'scoringmech' (button held through the turns)", r => Press(r, "scoringmech", PressSteps),
                (r, lines) =>
                {
                    MotorActuator m = Motor(r, "scoringmech");
                    lines.Add(Spin(r, 0f, +1f, "R", () => m.SetInput(1f)));
                    Stop(r, () => m.SetInput(1f));
                    lines.Add(Spin(r, 0f, -1f, "L", () => m.SetInput(1f)));
                }, onField: true);
        if (mechIds.Contains("cascademotor"))
            Scenario(prefab, sb, "cascade FULL raise (2.5 s press, release)", r => Press(r, "cascademotor", FullRaiseSteps),
                StandardSpins, onField: true);
        // A cup lying 4 u ahead on open floor, driven over at full throttle.
        Scenario(prefab, sb, "drove over a cup on the floor", r =>
        {
            Vector3 fwd = Vector3.ProjectOnPlane(r.motor.DriveForwardWorld, Vector3.up).normalized;
            Rigidbody cup = BorrowCup(r.root.transform.position + fwd * 4f + Vector3.up * 0.3f)
                            ?? throw new InvalidOperationException("no loose cup in the scene");
            Hold(r, 50);
            for (int i = 0; i < 100; i++) Tick(r, 1f, 0f);
            Stop(r);
        }, StandardSpins, onField: true);

        // The nearest roller, bumped the way a driver clicks it over.
        Scenario(prefab, sb, "bumped the nearest roller at full throttle", r =>
        {
            Transform roller = FindNearest("Roller", r.root.transform.position);
            if (roller == null) throw new InvalidOperationException("no Roller* in the scene");
            FaceToward(r, roller.position);
            Hold(r, SettleSteps);
            for (int i = 0; i < 300; i++) Tick(r, 1f, 0f);
            Stop(r);
        }, StandardSpins, onField: true);

        // The nearest match-load button, likewise.
        Scenario(prefab, sb, "bumped the nearest match-load button at full throttle", r =>
        {
            Transform button = FindNearest("Button", r.root.transform.position);
            if (button == null) throw new InvalidOperationException("no Button* in the scene");
            FaceToward(r, button.position);
            Hold(r, SettleSteps);
            for (int i = 0; i < 300; i++) Tick(r, 1f, 0f);
            Stop(r);
        }, StandardSpins, onField: true);

        // The wander that beached in round 2, kept as it was, now with the census.
        Scenario(prefab, sb, "drive 1 s forward, then the turns (moving)", null, (r, lines) =>
        {
            for (int i = 0; i < 100; i++) Tick(r, 1f, 0f);
            lines.Add(Spin(r, 1f, +1f, "R while moving"));
            Stop(r);
            for (int i = 0; i < 100; i++) Tick(r, 1f, 0f);
            lines.Add(Spin(r, 1f, -1f, "L while moving"));
        }, onField: true);

        // Reversals: the report's "starts right and once the joystick moves left it goes straight".
        if (!FieldOnly)
        Scenario(prefab, sb, "reversal from rest (R 1 s then L)", null, (r, lines) =>
        {
            lines.Add(Spin(r, 0f, +1f, "R (1 s)", null, 100));
            lines.Add(Spin(r, 0f, -1f, "then L, no pause"));
        });
        if (!FieldOnly)
        Scenario(prefab, sb, "moving reversal (half throttle, R 1 s then L)", null, (r, lines) =>
        {
            lines.Add(Spin(r, 0.5f, +1f, "R (1 s) @ half throttle", null, 100));
            lines.Add(Spin(r, 0.5f, -1f, "then L @ half throttle"));
        });

        // Ratios against the untouched robot, so the eye lands on the scenario that changed something.
        sb.AppendLine($"  baseline turned R {baseR.yawDeg:+0;-0} / L {baseL.yawDeg:+0;-0} deg in {SpinSteps / 100f:0.0} s; " +
                      "every line above is that same turn after one interaction.");
        sb.AppendLine();
    }

    private static void StandardSpins(Rig r, List<Turn> lines)
    {
        lines.Add(Spin(r, 0f, +1f, "R"));
        Stop(r);
        lines.Add(Spin(r, 0f, -1f, "L"));
    }

    // One fresh spawn: settle, do the interaction, describe the robot's state, then run the turns and
    // print them. A failing scenario prints its exception and the next one still runs.
    private static void Scenario(GameObject prefab, StringBuilder sb, string title,
        Action<Rig> interaction, Action<Rig, List<Turn>> turns, bool onField = false)
    {
        SimulationMode previous = Physics.simulationMode;
        try
        {
            Rig r = onField ? BuildOnField(prefab) : Build(prefab);
            Physics.simulationMode = SimulationMode.Script;
            Hold(r, SettleSteps);
            foreach (ArticulationBody b in r.links) r.restJoint[b] = Joint(b);

            interaction?.Invoke(r);
            sb.AppendLine($"  -- {title} [{r.where}]");
            sb.AppendLine($"     state after: {State(r)}");
            if (r.intakeError != null) sb.AppendLine($"     (intake tick threw: {r.intakeError})");

            var lines = new List<Turn>();
            turns(r, lines);
            foreach (Turn t in lines) sb.AppendLine("     " + Format(t));
            sb.AppendLine($"     state at end: {State(r)}");
        }
        catch (Exception e)
        {
            sb.AppendLine($"  -- {title}: FAILED — {e.GetType().Name}: {e.Message}");
        }
        finally { Physics.simulationMode = previous; }
    }

    // --- The play-mode robot, in edit mode --------------------------------------------------------

    private static Rig Build(GameObject prefab)
    {
        var r = new Rig { prefab = prefab, floorY = BareFloorTopY, where = "bare floor" };
        r.root = ValidationUtil.SpawnOnBareFloor(prefab, out r.motor);
        Prepare(r);
        return r;
    }

    // The real field: SampleScene as it ships (tiles, seams, tape, walls, settled pieces), the robot
    // dropped where RobotSpawner would put it. Nothing here is saved.
    private static Rig BuildOnField(GameObject prefab)
    {
        var r = new Rig { prefab = prefab, where = "field" };
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene(RoboSimPaths.MainScene,
            UnityEditor.SceneManagement.OpenSceneMode.Single);

        RobotSpawner spawner = UnityEngine.Object.FindFirstObjectByType<RobotSpawner>(FindObjectsInactive.Include);
        Vector3 spawnAt = new Vector3(15.99f, 0.974f, 7.91f);   // RobotSpawner's serialized default
        if (spawner != null)
        {
            var so = new SerializedObject(spawner);
            SerializedProperty sp = so.FindProperty("spawnPosition");
            if (sp != null) spawnAt = sp.vector3Value;
        }

        // The floor under the spawn point, measured rather than assumed.
        Physics.SyncTransforms();
        r.floorY = Physics.Raycast(spawnAt + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 20f)
            ? hit.point.y : 0.72f;

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab,
            UnityEngine.SceneManagement.SceneManager.GetActiveScene());
        instance.transform.position = spawnAt + Vector3.up * 2f;
        Physics.SyncTransforms();

        // RobotSpawner.RecenterFootprint, in miniature: footprint centre on the spawn point, lowest
        // collider just above the floor plus the drop.
        Bounds b = default; bool has = false;
        foreach (Collider c in instance.GetComponentsInChildren<Collider>())
        {
            if (c.isTrigger) continue;
            if (!has) { b = c.bounds; has = true; } else b.Encapsulate(c.bounds);
        }
        if (has)
        {
            Vector3 delta = new Vector3(spawnAt.x - b.center.x, (r.floorY + 0.05f + ValidationUtil.RigDropHeight) - b.min.y,
                spawnAt.z - b.center.z);
            instance.transform.position += delta;
            Physics.SyncTransforms();
        }

        r.root = instance.GetComponent<ArticulationBody>()
                 ?? throw new InvalidOperationException($"'{prefab.name}' has no root ArticulationBody");
        r.motor = r.root.GetComponent<RobotMotorController>()
                  ?? throw new InvalidOperationException($"'{prefab.name}' has no RobotMotorController");
        r.spawnPos = spawnAt;

        // The pieces' floor clamp runs in play and is what keeps a cup from being crushed away under
        // a 12-mass robot; without it a beaching that happens in the game cannot happen here.
        r.clamps = UnityEngine.Object.FindObjectsByType<MinHeightClamp>(FindObjectsInactive.Exclude);
        if (ClampAwake != null) foreach (MinHeightClamp c in r.clamps) ClampAwake.Invoke(c, null);

        Prepare(r);
        return r;
    }

    // Turn the freshly placed robot to face a world point — before the first physics step, when a
    // transform write on the root is still honoured (SpawnOnBareFloor positions the same way).
    private static void FaceToward(Rig r, Vector3 target)
    {
        Vector3 fwd = Vector3.ProjectOnPlane(r.motor.DriveForwardWorld, Vector3.up).normalized;
        Vector3 want = Vector3.ProjectOnPlane(target - r.root.transform.position, Vector3.up).normalized;
        if (fwd.sqrMagnitude < 1e-6f || want.sqrMagnitude < 1e-6f) return;
        r.root.transform.rotation = Quaternion.FromToRotation(fwd, want) * r.root.transform.rotation;
        Physics.SyncTransforms();
    }

    private static Transform FindNearest(string nameStartsWith, Vector3 to)
    {
        Transform best = null; float bestD = float.PositiveInfinity;
        foreach (Transform t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude))
        {
            if (!t.name.StartsWith(nameStartsWith)) continue;
            float d = (t.position - to).sqrMagnitude;
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    // A loose field cup, moved to a world point (both the body and the transform: edit mode).
    private static Rigidbody BorrowCup(Vector3 to)
    {
        foreach (Rigidbody rb in UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude))
        {
            if (!rb.name.StartsWith("Cup") || rb.isKinematic) continue;
            Vector3 delta = to - rb.position;
            rb.transform.position += delta;
            rb.position = rb.transform.position;
            rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
            return rb;
        }
        return null;
    }

    // Every non-robot collider the robot is touching right now, deepest first: what it is resting on.
    private static string RestingOn(Rig r)
    {
        var hits = new Dictionary<string, float>();
        var robotCols = new HashSet<Collider>(r.root.GetComponentsInChildren<Collider>(false));
        Bounds b = default; bool has = false;
        foreach (Collider c in robotCols)
        {
            if (!c.enabled || c.isTrigger) continue;
            if (!has) { b = c.bounds; has = true; } else b.Encapsulate(c.bounds);
        }
        if (!has) return "?";
        foreach (Collider other in Physics.OverlapBox(b.center, b.extents + Vector3.one * 0.05f))
        {
            if (other == null || other.isTrigger || robotCols.Contains(other)) continue;
            if (other.name == "Floor" || other.name == "Wall") continue;
            foreach (Collider mine in robotCols)
            {
                if (!mine.enabled || mine.isTrigger || !mine.bounds.Intersects(other.bounds)) continue;
                if (!Physics.ComputePenetration(mine, mine.transform.position, mine.transform.rotation,
                        other, other.transform.position, other.transform.rotation, out _, out float depth)) continue;
                ArticulationBody owner = mine.GetComponentInParent<ArticulationBody>();
                string key = $"{other.transform.root.name}/{other.name} (top {(other.bounds.max.y - r.floorY) * 1000f:0}mm) under {(owner != null ? owner.name : "?")}";
                if (!hits.TryGetValue(key, out float d) || depth > d) hits[key] = depth;
            }
        }
        if (hits.Count == 0) return "nothing but the floor";
        var list = new List<KeyValuePair<string, float>>(hits);
        list.Sort((x, y) => y.Value.CompareTo(x.Value));
        var parts = new List<string>();
        for (int i = 0; i < Mathf.Min(4, list.Count); i++) parts.Add($"{list[i].Key} ({list[i].Value * 1000f:0}mm)");
        return string.Join("; ", parts);
    }

    // ROBOSIM_PROBE_MIN_LINK_MASS: an A/B lever. Every non-root, non-wheel link lighter than this is
    // raised to it BEFORE Initialise (so the drive tune sees the same robot play would). The shipped
    // v3 carries rollers and flaps of 0.01-0.05 mass on a 4-mass chassis — 400:1 ratios the solver
    // resolves by letting the light link sink into whatever it hits.
    private static float MinLinkMass
        => float.TryParse(Environment.GetEnvironmentVariable("ROBOSIM_PROBE_MIN_LINK_MASS"), out float m) ? m : 0f;
    private static bool FieldOnly => Environment.GetEnvironmentVariable("ROBOSIM_PROBE_FIELD_ONLY") == "1";
    private static bool BaselineOnly => Environment.GetEnvironmentVariable("ROBOSIM_PROBE_BASELINE_ONLY") == "1";

    private static void Prepare(Rig r)
    {
        float floor = MinLinkMass;
        if (floor > 0f)
        {
            var wheelSet = new HashSet<ArticulationBody>();
            if (r.motor.leftWheels != null) foreach (ArticulationBody w in r.motor.leftWheels) if (w != null) wheelSet.Add(w);
            if (r.motor.rightWheels != null) foreach (ArticulationBody w in r.motor.rightWheels) if (w != null) wheelSet.Add(w);
            foreach (ArticulationBody b in r.root.GetComponentsInChildren<ArticulationBody>(true))
                if (b != null && b != r.root && !wheelSet.Contains(b) && b.mass < floor) b.mass = floor;
        }

        r.motor.Initialise();   // includes IgnoreBuiltInSelfOverlaps, as Awake does

        foreach (MotorActuator m in r.root.GetComponentsInChildren<MotorActuator>(true)) m.Configure();
        foreach (PneumaticActuator p in r.root.GetComponentsInChildren<PneumaticActuator>(true)) p.BakeDrive();
        foreach (PassiveArm a in r.root.GetComponentsInChildren<PassiveArm>(true)) a.BakeDrive();
        r.lifts = r.root.GetComponentsInChildren<CascadeLift>(true);
        foreach (CascadeLift l in r.lifts) l.BakeDrives();
        r.couplers = r.root.GetComponentsInChildren<JointCoupler>(true);
        foreach (JointCoupler c in r.couplers) c.BakeDrive();
        r.ballasts = r.root.GetComponentsInChildren<Dr4bBallast>(true);
        foreach (Dr4bBallast b in r.ballasts) b.BakeDrive();
        r.intakes = r.root.GetComponentsInChildren<IntakePull>(true);
        foreach (IntakePull i in r.intakes) { i.logEvents = false; if (i.stabilizeHoldPoint) i.StabilizeAnchors(); }
        foreach (IgnoreRobotSelfCollision s in r.root.GetComponentsInChildren<IgnoreRobotSelfCollision>(true))
            s.IgnoreAgainstRobot();
        foreach (IgnoreFieldFloor f in r.root.GetComponentsInChildren<IgnoreFieldFloor>(true))
            f.IgnoreAgainstFloor();
        r.mechs = r.root.GetComponent<RobotMechanisms>();

        r.wheels = RobotPhysicsValidation.FindWheels(r.root, out r.left, out r.right);
        r.wheelSet = new HashSet<ArticulationBody>(r.wheels);
        r.radius = DrivetrainTuning.MeasureWheelRadius(r.wheels);

        var links = new List<ArticulationBody>();
        foreach (ArticulationBody b in r.root.GetComponentsInChildren<ArticulationBody>(true))
            if (b != null && b != r.root && !r.wheelSet.Contains(b) && b.dofCount > 0) links.Add(b);
        r.links = links.ToArray();
    }

    private static MotorActuator Motor(Rig r, string id)
    {
        RobotMechanisms.Mechanism m = r.mechs != null ? r.mechs.Find(id) : null;
        if (m == null || m.motor == null) throw new InvalidOperationException($"no motor mechanism '{id}'");
        return m.motor;
    }

    private static Rigidbody MakeCup(Vector3 at)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "Cup_Probe";
        go.transform.localScale = Vector3.one * 1.2f;
        go.transform.position = at;
        Rigidbody rb = go.AddComponent<Rigidbody>();
        rb.mass = 0.068f;      // a field cup
        rb.position = at;      // edit mode: the body does not follow the transform on its own
        return rb;
    }

    // One physics step with everything a play-mode step runs.
    private static void Tick(Rig r, float throttle, float turn)
    {
        r.motor.SetManualInput(throttle, turn);
        r.motor.ApplyStep(ValidationUtil.StepSeconds);
        foreach (CascadeLift l in r.lifts) l.ApplyStep();
        foreach (JointCoupler c in r.couplers) c.ApplyStep();
        foreach (Dr4bBallast b in r.ballasts) b.ApplyStep();
        if (ClampFixedUpdate != null)
            foreach (MinHeightClamp c in r.clamps) if (c != null) ClampFixedUpdate.Invoke(c, null);
        if (IntakeFixedUpdate != null)
            foreach (IntakePull i in r.intakes)
            {
                try { IntakeFixedUpdate.Invoke(i, null); }
                catch (Exception e) { r.intakeError ??= (e.InnerException ?? e).Message; }
            }
        Physics.Simulate(ValidationUtil.StepSeconds);
    }

    private static void Hold(Rig r, int steps) { for (int i = 0; i < steps; i++) Tick(r, 0f, 0f); }

    // A button held for `steps`, then released, then a second to settle — a toggle.
    private static void Press(Rig r, string id, int steps)
    {
        MotorActuator m = Motor(r, id);
        for (int i = 0; i < steps; i++) { m.SetInput(1f); Tick(r, 0f, 0f); }
        m.SetInput(0f);
        Hold(r, SettleSteps);
    }

    private static void Stop(Rig r, Action each = null)
    {
        for (int i = 0; i < StopSteps; i++) { each?.Invoke(); Tick(r, 0f, 0f); }
    }

    // --- Measurement ------------------------------------------------------------------------------

    private static Turn Spin(Rig r, float throttle, float turn, string label, Action each = null,
        int steps = SpinSteps)
    {
        var t = new Turn { label = $"{label} (throttle {throttle:0.0#}, turn {turn:+0;-0})" };
        Transform root = r.root.transform;
        float lastYaw = root.eulerAngles.y;
        Vector3 start = root.position;
        var lastSpin = new float[r.wheels.Length];
        for (int w = 0; w < r.wheels.Length; w++) lastSpin[w] = WheelSpin(r.wheels[w]);
        float leftSum = 0f, rightSum = 0f, speedSum = 0f;

        for (int i = 0; i < steps; i++)
        {
            each?.Invoke();
            Tick(r, throttle, turn);

            float yawNow = root.eulerAngles.y;
            t.yawDeg += Mathf.DeltaAngle(lastYaw, yawNow);
            lastYaw = yawNow;
            speedSum += new Vector2(r.root.linearVelocity.x, r.root.linearVelocity.z).magnitude;

            for (int w = 0; w < r.wheels.Length; w++)
            {
                float spin = WheelSpin(r.wheels[w]);
                float rate = (spin - lastSpin[w]) / ValidationUtil.StepSeconds;
                if (i > 0 && Mathf.Sign(spin) != Mathf.Sign(lastSpin[w]) && Mathf.Abs(rate) > WheelRateNoiseFloor)
                    t.reversals++;
                lastSpin[w] = spin;
                if (Array.IndexOf(r.left, r.wheels[w]) >= 0) leftSum += spin; else rightSum += spin;
            }
        }

        Vector3 d = root.position - start;
        t.pathU = new Vector2(d.x, d.z).magnitude;
        t.meanSpeed = speedSum / steps;
        t.leftSpin = r.left.Length > 0 ? leftSum / (steps * r.left.Length) : 0f;
        t.rightSpin = r.right.Length > 0 ? rightSum / (steps * r.right.Length) : 0f;
        t.airborne = Airborne(r, out t.gaps);
        t.contacts = FloorContacts(r);
        return t;
    }

    private static string Format(Turn t)
        => $"{t.label,-44} yaw {t.yawDeg,+5:0;-0} deg  path {t.pathU,5:0.00} u  v {t.meanSpeed,4:0.0}  " +
           $"wheel L {t.leftSpin,+6:0;-0} R {t.rightSpin,+6:0;-0} deg/s  rev {t.reversals,3}  " +
           $"airborne {t.airborne} [{t.gaps}]  floor-contacts [{t.contacts}]";

    private static float WheelSpin(ArticulationBody w)
        => w != null && w.jointVelocity.dofCount > 0 ? w.jointVelocity[0] * Mathf.Rad2Deg : 0f;

    private static float Joint(ArticulationBody b)
    {
        ArticulationReducedSpace p = b.jointPosition;
        float v = p.dofCount > 0 ? p[0] : 0f;
        return float.IsNaN(v) ? 0f : v;
    }

    private static int Airborne(Rig r, out string gaps)
    {
        int n = 0;
        var parts = new List<string>();
        foreach (ArticulationBody w in r.wheels)
        {
            float gap = w.transform.position.y - r.radius - r.floorY;
            if (gap > AirborneGap) n++;
            parts.Add($"{Short(w.name)} {gap * 1000f:+0;-0}mm");
        }
        gaps = string.Join(" ", parts);
        return n;
    }

    // Every enabled, solid, NON-wheel collider whose lowest point is at the floor: a skid.
    private static string FloorContacts(Rig r)
    {
        var hits = new List<string>();
        foreach (Collider c in r.root.GetComponentsInChildren<Collider>(false))
        {
            if (c == null || !c.enabled || c.isTrigger) continue;
            ArticulationBody owner = c.GetComponentInParent<ArticulationBody>();
            if (owner == null || r.wheelSet.Contains(owner)) continue;
            float bottom = c.bounds.min.y - r.floorY;
            if (bottom <= 0.01f) hits.Add($"{owner.name}/{c.name} {bottom * 1000f:+0;-0}mm");
        }
        return hits.Count == 0 ? "none" : string.Join(", ", hits);
    }

    private static string State(Rig r)
    {
        // Composite centre of mass over the wheel centroid, in the robot's own measured frame.
        Vector3 weighted = Vector3.zero; float total = 0f;
        foreach (ArticulationBody b in r.root.GetComponentsInChildren<ArticulationBody>(true))
        {
            if (b == null || b.mass <= 0f) continue;
            weighted += b.transform.position * b.mass; total += b.mass;
        }
        Vector3 com = total > 0f ? weighted / total : r.root.transform.position;
        Vector3 centroid = Vector3.zero;
        foreach (ArticulationBody w in r.wheels) centroid += w.transform.position;
        centroid /= Mathf.Max(1, r.wheels.Length);
        Vector3 fwd = r.motor.DriveForwardWorld;
        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
        Vector3 off = com - centroid;
        float lat = Vector3.Dot(off, right), lon = Vector3.Dot(off, fwd), height = com.y - r.floorY;

        // Mechanism joints: how far from where they rested, and which moved most.
        float sum = 0f, worst = 0f; string worstName = "-";
        foreach (ArticulationBody b in r.links)
        {
            if (!r.restJoint.TryGetValue(b, out float rest)) continue;
            float dlt = Mathf.Abs(Joint(b) - rest);
            sum += dlt;
            if (dlt > worst) { worst = dlt; worstName = b.name; }
        }

        Vector3 up = r.root.transform.up;
        float tilt = Vector3.Angle(up, Vector3.up);
        int air = Airborne(r, out string gaps);
        Vector3 pos = r.root.transform.position;
        return $"at ({pos.x:0.0}, {pos.z:0.0}) · resting on: {RestingOn(r)} · " +
               $"COM lat {lat:+0.00;-0.00} lon {lon:+0.00;-0.00} h {height:0.00} u · tilt {tilt:0.0} deg · " +
               $"joints moved Σ{sum:0.00} rad (most: {worstName} {worst:0.00}) · airborne {air} [{gaps}] · " +
               $"floor-contacts [{FloorContacts(r)}]";
    }

    private static string Short(string name)
        => name.StartsWith("WheelLink_") ? name.Substring("WheelLink_".Length) : name;
}
