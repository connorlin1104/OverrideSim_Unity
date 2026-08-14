using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

// Reports — and optionally fixes — where each robot's mass actually is, and therefore whether it
// can tip.
//
// WHY THIS EXISTS. PhysX has always computed a centre of mass: every link in every shipped robot
// serializes m_ImplicitCom: 1 (automaticCenterOfMass), so each link's COM is the centroid of its
// own colliders and the composite COM is a genuine mass-weighted aggregate that the solver uses for
// everything. The problem was never that the COM wasn't calculated. It was that the MASSES make it
// almost impossible to move:
//
//   • RigDrivetrainArticulation put a hard-coded 24 kg on the chassis link, which was 66-68% of
//     every shipped robot, sitting low and never moving. Then 7 kg, which was still enough floor
//     to outvote any lift. It is 4 kg now — see RootMass, which has the measurements.
//   • Each wheel link was 1 kg — up to 8 kg at axle height. Halved to 0.5, which is still 5x a real
//     omni's 0.11 kg: a wheel's centre sits one radius above the contact patch so its mass is weak
//     ballast either way, and what stops it going lower is the mass RATIO across the drive joint,
//     not the mass. See RigDrivetrainArticulation.WheelMass.
//   • Every lift link lands on MechanismBuildUtil.MinLiftMass (1.5 kg), which has won on 100% of
//     the shipped links. This one is NOT a measurement failure, which is worth knowing before
//     trying to fix it: every mesh in 654V_v1's DR4B is closed and measurable, and the assembly
//     genuinely comes to ~1.2 kg of aluminium. The CAD models the structure, not the robot.
//
// Net effect BEFORE any of that: raising a full cascade lift moved the composite COM by ~44 mm, and
// every shipped robot's tip threshold sat ABOVE the 0.8 g its tyres can deliver. The robots
// physically could not tip themselves by driving, in any configuration, no matter how hard a
// reversal was slammed. For scale, a real VEX V5 robot is at most 11.3 kg with its COM 150-200 mm
// up on a ~300 mm track, and tips well under 0.5 g with a loaded lift raised.
//
// Applying the VEX-realistic masses below leaves acceleration essentially unchanged — DrivetrainTuning
// derives drive force from mu*m*g, so force and inertia scale together — and changes only what mass
// distribution was ever supposed to change: stability.
//
// TWO THINGS TO KNOW BEFORE READING THE REPORT:
//   • The DR4B's mass now moves, but only its own. Its stages are still transform-posed visuals
//     (Dr4bMoveFollower / PivotRotateFollower) with their colliders disabled and their bodies
//     destroyed — a four-bar is a closed loop and an ArticulationBody tree is a tree — so the mass
//     they represent rides a Dr4bBallast link instead: one real prismatic joint, no collider,
//     carrying the assembly's measured mass along its measured travel. What that buys is honest
//     rather than dramatic. 654V_v1's linkage is 1.5 kg moving 320 mm, so it lifts the composite
//     COM by ~43 mm and the robot still cannot tip itself with the lift empty. That is the right
//     answer for a chain-driven DR4B whose motors stay on the chassis; what tips one is the load.
//   • Which is still weightless. ClawGrab and IntakePull set isKinematic on a carried piece, and a
//     kinematic body contributes no mass to the solver, so a loaded lift remains indistinguishable
//     from an empty one. On a stacker like 654V_v1 that is the difference between a lift that
//     cannot tip the robot and one that easily can — four 1 kg cups at 630 mm would take it from
//     1.11 g to well under 0.5.
//
// Usage: Tools > RoboSim > Robot > Mass & Balance. Measuring is always safe; applying rewrites
// prefabs and then re-bakes the drives (mass feeds the traction budget).
public class RobotBalanceWindow : EditorWindow
{
    private const string UndoName = "Apply Robot Masses";

    // The rig tool is the authority on these — it is what writes them on a fresh rig, and this
    // window's whole job is bringing already-rigged robots into line with it.
    private const float ChassisMass = RigDrivetrainArticulation.RootMass;
    private const float WheelMass = RigDrivetrainArticulation.WheelMass;
    private const float WorldScaleFactor = 10f;   // 1 scaled unit = 0.1 m, as everywhere else

    private readonly List<Report> reports = new List<Report>();
    private Vector2 scroll;
    private string status;

    internal struct Report
    {
        public string path;
        public string name;
        public float totalMass;
        public float chassisMass;
        public float wheelMass;
        public float otherMass;
        public float comHeight;     // above the wheel contact plane, world units
        public float comHeightRaised; // ...with every prismatic lift at its upper limit
        public float halfTrack;     // world units
        public float tipG;          // LATERAL acceleration that tips it, in g — a hard turn
        public float tipGRaised;    // ...with the lifts up

        // The lengthwise pair, which is what a slammed reversal tips it over. Two numbers rather
        // than one because the centre of mass is rarely centred fore-and-aft: a lift hung off the
        // front shortens the nose margin and lengthens the tail one, so the robot goes over its
        // nose long before it would go over its tail.
        public float noseMargin;    // COM to the front contact line, world units
        public float tailMargin;    // COM to the rear contact line
        public float leftMargin;    // ...and the same pair across the track, for the ROLL threshold
        public float rightMargin;
        public bool baseIsZ;        // wheelbase runs along root +Z ("nose") rather than +X
        public float liftTravel;    // total vertical travel available, world units
        public float tractionG;     // what the tyres can actually deliver
        public int wheelCount;
        public int colliderlessLinks;
        public float colliderlessMass;
        public float groundClearance;   // lowest non-wheel collider above the contact plane
        public string lowestPart;
        public string note;
    }

    [MenuItem("Tools/RoboSim/Robot/Mass & Balance", false, 4)]
    private static void Open()
    {
        RobotBalanceWindow window = GetWindow<RobotBalanceWindow>(false, "Mass & Balance", true);
        window.minSize = new Vector2(620f, 420f);
        window.Measure();
        window.Show();
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Tip threshold is the acceleration that would put the robot over, from its measured " +
            "centre of mass and how far that sits inside the wheels. SIDEWAYS is a hard turn, over " +
            "the track. LENGTHWISE is a slammed reversal, over the wheelbase — the longer axis, so " +
            "it is the harder one to reach, and it is the one that answers 'why won't it nose " +
            "over'. Compare either against the traction ceiling: the tyres cannot push harder than " +
            "mu*g, so a robot whose threshold is ABOVE its traction ceiling can never tip itself " +
            "by driving, however hard you slam a reversal.\n\n" +
            "Link COMs here are computed from collider volumes, which is what PhysX does for boxes " +
            "and spheres; convex meshes are approximated by their bounds, so absolute heights carry " +
            "a few mm of uncertainty. Comparisons between robots and before/after are exact.",
            MessageType.None);

        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);

        // Deferred, never called inline. IMGUI runs OnGUI twice per frame — a Layout pass that
        // counts the controls and a Repaint pass that draws them — and both passes must produce
        // the SAME sequence of layout groups. Measuring changes how many report boxes exist and
        // DisplayDialog pumps events mid-pass, so doing either from inside a button branch makes
        // the two passes disagree and Unity logs "EndLayoutGroup: BeginLayoutGroup must be called
        // first" / "pushing more GUIClips than you are popping". delayCall runs it after OnGUI has
        // finished entirely, where there is no IMGUI state left to corrupt.
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Measure")) Defer(Measure);
            if (GUILayout.Button($"Apply VEX Masses to All  (chassis {ChassisMass} kg, wheel {WheelMass} kg)"))
                Defer(ApplyAll);
        }

        scroll = EditorGUILayout.BeginScrollView(scroll);
        foreach (Report r in reports) DrawReport(r);
        EditorGUILayout.EndScrollView();
    }

    private void DrawReport(Report r)
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField(r.name, EditorStyles.boldLabel);
            if (!string.IsNullOrEmpty(r.note))
            {
                EditorGUILayout.HelpBox(r.note, MessageType.Warning);
                return;
            }

            EditorGUILayout.LabelField(
                $"{r.totalMass:0.00} kg  —  chassis {r.chassisMass:0.00} " +
                $"({(r.totalMass > 0f ? r.chassisMass / r.totalMass : 0f):P0}), " +
                $"{r.wheelCount} wheels {r.wheelMass:0.00}, everything else {r.otherMass:0.00}");
            string front = r.baseIsZ ? "+Z" : "+X";
            EditorGUILayout.LabelField(
                $"COM {r.comHeight * 100f:0.} mm above the contact patch, half-track " +
                $"{r.halfTrack * 100f:0.} mm, nose/tail margin {r.noseMargin * 100f:0.}/" +
                $"{r.tailMargin * 100f:0.} mm (nose = root {front})");

            DrawThresholds("lift down", r, r.comHeight);
            if (r.liftTravel > 1e-3f)
                DrawThresholds($"lift up ({r.liftTravel * 100f:0.} mm)", r, r.comHeightRaised);

            // Worth surfacing: a link with no enabled collider gets automaticCenterOfMass, which
            // puts its whole mass at the link origin. The mechanism builders place a motor hub's
            // origin on its joint axis, which can be below the floor — so this is mass actively
            // pulling the COM the wrong way, not just an unmeasured link.
            bool tight = r.groundClearance < 0.06f; // under 6 mm — a real VEX bot runs 6-10
            EditorGUILayout.LabelField(
                $"ground clearance {r.groundClearance * 100f:0.0} mm" +
                (string.IsNullOrEmpty(r.lowestPart) ? "" : $"  (lowest: {r.lowestPart})") +
                (tight ? "  — TIGHT: this robot may rest on that part instead of its wheels" : ""),
                tight ? EditorStyles.boldLabel : EditorStyles.miniLabel);

            if (r.colliderlessLinks > 0)
                EditorGUILayout.LabelField(
                    $"{r.colliderlessLinks} link(s) with no enabled collider carry " +
                    $"{r.colliderlessMass:0.00} kg at their own origin", EditorStyles.miniLabel);
        }
    }

    // One pose's worth of thresholds: sideways (a hard turn) and lengthwise (a slammed reversal),
    // each against what the tyres can actually deliver.
    //
    // LENGTHWISE IS THE ONE TO READ for the reversal question, and it is reported as the WORSE of
    // the nose and tail margins rather than an average. The player can flip which end is the front
    // (Reverse Drive Direction), and a robot only has to go over once.
    private static string ThresholdLine(string pose, Report r, float comHeight, out bool canTip)
    {
        float left = TipG(r.leftMargin, comHeight);
        float right = TipG(r.rightMargin, comHeight);
        float roll = Mathf.Min(left, right);
        float nose = TipG(r.noseMargin, comHeight);
        float tail = TipG(r.tailMargin, comHeight);
        float pitch = Mathf.Min(nose, tail);

        canTip = Mathf.Min(roll, pitch) < r.tractionG;
        return $"{pose}: COM {comHeight * 100f:0.} mm  ·  " +
               $"sideways {roll:0.00} g (L {left:0.00} / R {right:0.00})  ·  " +
               $"lengthwise {pitch:0.00} g (nose {nose:0.00} / tail {tail:0.00})  ·  " +
               $"tyres deliver {r.tractionG:0.00} g  ·  " +
               (canTip ? "CAN tip by driving" : "cannot tip by driving");
    }

    private static void DrawThresholds(string pose, Report r, float comHeight)
    {
        string line = ThresholdLine(pose, r, comHeight, out bool canTip);
        EditorGUILayout.LabelField(line, canTip ? EditorStyles.boldLabel : EditorStyles.label);
    }

    // Run `action` once OnGUI has returned. See the note at the call sites.
    private void Defer(System.Action action)
    {
        EditorApplication.delayCall += () =>
        {
            if (this == null) return; // window closed between the click and the callback
            action();
            Repaint();
        };
    }

    // --- Measuring ------------------------------------------------------------------------------

    // Batch entry, following the RunBatch* convention the other RoboSim tools use:
    //   Unity -batchmode -nographics -quit -projectPath . \
    //         -executeMethod RobotBalanceWindow.RunBatchMeasure
    //
    // Measuring only — it writes nothing. This exists because the numbers it prints are the ONLY
    // way to tell whether a mass change moved the thing it was supposed to move, and reading them
    // off an EditorWindow means a human has to be sitting there to iterate.
    public static void RunBatchMeasure()
    {
        var log = new StringBuilder("Robot Mass & Balance\n");
        int found = 0;
        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;
            log.AppendLine(Describe(Measure(prefab, path)));
            found++;
        }
        if (found == 0)
            throw new System.InvalidOperationException(
                $"No robot prefabs with a RobotMotorController under {RoboSimPaths.RobotsFolder}.");
        Debug.Log(log.ToString());
    }

    // The write half, headless. Same sweep the Apply button runs, minus the confirmation dialog —
    // which is the whole reason it needs its own entry: calibrating the chassis and wheel constants
    // against a tip target is a measure/change/measure loop, and a modal dialog in the middle of it
    // means a human has to sit there clicking through every iteration.
    //   Unity -batchmode -nographics -quit -projectPath . \
    //         -executeMethod RobotBalanceWindow.RunBatchApply
    public static void RunBatchApply()
    {
        var log = new StringBuilder();
        int changed = 0, total = 0;
        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;
            total++;
            if (Apply(path, log)) changed++;
        }
        if (total == 0)
            throw new System.InvalidOperationException(
                $"No robot prefabs with a RobotMotorController under {RoboSimPaths.RobotsFolder}.");
        AssetDatabase.SaveAssets();
        Debug.Log($"Robot Mass & Balance: {changed} of {total} prefab(s) updated " +
                  $"(chassis {ChassisMass} kg, wheel {WheelMass} kg).\n{log}");
    }

    private static string Describe(Report r)
    {
        if (!string.IsNullOrEmpty(r.note)) return $"{r.name}\n  {r.note}\n";

        var s = new StringBuilder();
        s.AppendLine(r.name);
        s.AppendLine($"  {r.totalMass:0.00} kg — chassis {r.chassisMass:0.00} " +
                     $"({(r.totalMass > 0f ? r.chassisMass / r.totalMass : 0f):P0}), " +
                     $"{r.wheelCount} wheels {r.wheelMass:0.00}, everything else {r.otherMass:0.00}");
        s.AppendLine($"  half-track {r.halfTrack * 100f:0.} mm, nose/tail margin " +
                     $"{r.noseMargin * 100f:0.}/{r.tailMargin * 100f:0.} mm " +
                     $"(nose = root {(r.baseIsZ ? "+Z" : "+X")})");
        s.AppendLine("  " + ThresholdLine("lift down", r, r.comHeight, out _));
        if (r.liftTravel > 1e-3f)
            s.AppendLine("  " + ThresholdLine($"lift up ({r.liftTravel * 100f:0.} mm)",
                r, r.comHeightRaised, out _));
        s.AppendLine($"  ground clearance {r.groundClearance * 100f:0.0} mm" +
                     (string.IsNullOrEmpty(r.lowestPart) ? "" : $" (lowest: {r.lowestPart})"));
        if (r.colliderlessLinks > 0)
            s.AppendLine($"  {r.colliderlessLinks} colliderless link(s) carry " +
                         $"{r.colliderlessMass:0.00} kg at their own origin");
        return s.ToString();
    }

    private void Measure()
    {
        reports.Clear();
        status = null;
        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;
            reports.Add(Measure(prefab, path));
        }
        if (reports.Count == 0) status = $"No robot prefabs with a RobotMotorController under {RoboSimPaths.RobotsFolder}.";
    }

    internal static Report Measure(GameObject prefab, string path)
    {
        var r = new Report { path = path, name = prefab.name };
        RobotMotorController motor = prefab.GetComponent<RobotMotorController>();
        ArticulationBody root = prefab.GetComponent<ArticulationBody>();
        if (root == null) { r.note = "no root ArticulationBody — not rigged yet"; return r; }

        var wheels = new List<ArticulationBody>();
        if (motor.leftWheels != null) foreach (ArticulationBody w in motor.leftWheels) if (w != null) wheels.Add(w);
        if (motor.rightWheels != null) foreach (ArticulationBody w in motor.rightWheels) if (w != null) wheels.Add(w);
        if (wheels.Count == 0) { r.note = "no wheels wired to the RobotMotorController"; return r; }
        r.wheelCount = wheels.Count;

        var wheelSet = new HashSet<ArticulationBody>(wheels);
        Transform rootTransform = root.transform;
        Vector3 moment = Vector3.zero;
        foreach (ArticulationBody body in root.GetComponentsInChildren<ArticulationBody>(true))
        {
            if (body == null || body.mass <= 0f) continue;
            r.totalMass += body.mass;
            if (body == root) r.chassisMass += body.mass;
            else if (wheelSet.Contains(body)) r.wheelMass += body.mass;
            else r.otherMass += body.mass;

            moment += LinkCentre(body, rootTransform, out bool hadCollider) * body.mass;
            if (!hadCollider) { r.colliderlessLinks++; r.colliderlessMass += body.mass; }
        }
        if (r.totalMass <= 0f) { r.note = "every link has zero mass"; return r; }
        Vector3 com = moment / r.totalMass;

        // The contact plane is the bottom of the lowest wheel sphere, and the half-track is the
        // widest lateral spread of the wheel centres. Both are measured off the wheels rather than
        // the chassis bounds, because it is the wheels the robot actually pivots over.
        //
        // EVERYTHING FROM HERE IS IN THE ROOT'S SPACE, and that is load-bearing: `com` above came
        // out of LinkCentre, which ends in InverseTransformPoint. Mixing a world height into this
        // arithmetic is off by exactly the prefab root's own y — which on these four robots is
        // -0.632 to +0.974 units, i.e. 63 to 97 mm of pure fiction. See GroundClearance.
        var centres = new List<Vector3>();
        foreach (ArticulationBody wheel in wheels)
            centres.Add(LinkCentre(wheel, rootTransform, out _));

        float lowest = WheelContactPlane(wheels, rootTransform);
        if (float.IsPositiveInfinity(lowest))
        {
            // No sphere on any wheel link. Fall back to the old estimate — the link centroid minus
            // the fitted radius — but say so, because that is only the tyre's bottom on a wheel link
            // that carries nothing except its own sphere, which is the shape the rig builds today.
            float radius = DrivetrainTuning.MeasureWheelRadius(wheels);
            foreach (Vector3 c in centres) lowest = Mathf.Min(lowest, c.y - radius);
            r.note = "no SphereCollider on any wheel link — contact plane estimated from the wheel " +
                     "links' collider centroids minus the fitted radius, so ground clearance is " +
                     "approximate.";
        }
        Footprint footprint = MeasureFootprint(centres, WheelbaseRunsAlongZ(wheels, rootTransform));
        r.halfTrack = footprint.halfTrack;
        r.baseIsZ = footprint.baseIsZ;
        r.comHeight = com.y - lowest;

        // Margins from the composite COM to the outermost contact lines, on BOTH axes. Signed
        // subtraction, not a half-track or half-wheelbase: where the COM actually sits inside the
        // footprint is the whole point, and every one of these robots is asymmetric.
        //
        // 654V_v3 is why the lateral pair is measured rather than assumed symmetric. Its centre of
        // mass sits well to one side of its own wheel track — TipOverValidation, measuring the same
        // robot on a bare floor, puts it 21.9 mm right of a 93.1 mm half-track, which leaves 17.7
        // degrees of roll margin one way against 27.3 the other. Reporting the average of a strong
        // side and a weak one describes a robot that does not exist; it goes over on the weak side.
        //
        // The two tools do NOT yet agree on the half-track itself (this one reports 130 mm for the
        // same robot) because they measure different things: half the SPREAD of LinkCentre — the
        // wheel LINK's collider centroid, gears and hubs included — against the MEAN lateral offset
        // of wheel.transform.position. Same trap WheelContactPlane was fixed for. Until that is
        // unified, quote TipOverValidation's numbers for absolute margins and this tool's for
        // which-side-is-weak; the asymmetry is robust, the scale is not.
        float comAlongBase = footprint.baseIsZ ? com.z : com.x;
        r.noseMargin = footprint.baseMax - comAlongBase;
        r.tailMargin = comAlongBase - footprint.baseMin;

        float comAcrossTrack = footprint.baseIsZ ? com.x : com.z;
        r.leftMargin = footprint.trackMax - comAcrossTrack;
        r.rightMargin = comAcrossTrack - footprint.trackMin;

        // A COM at or below the contact patch is not a stable robot with a huge tip threshold, it
        // is a measurement that has gone wrong (or a robot with most of its mass on colliderless
        // links pinned at the origin). Say so instead of dividing by a floored epsilon and printing
        // a confident five-figure number.
        if (r.comHeight <= 0.01f)
        {
            r.note = $"centre of mass computed {r.comHeight * 100f:0.} mm above the contact patch, " +
                     "which is at or below the wheels — the tip threshold would be meaningless. " +
                     (r.colliderlessLinks > 0
                         ? $"{r.colliderlessLinks} link(s) carrying {r.colliderlessMass:0.00} kg have no " +
                           "enabled collider, so their mass sits at the link origin."
                         : "Check that this robot's colliders were generated.");
            return r;
        }

        r.tipG = TipG(r.halfTrack, r.comHeight);

        // The case the whole question is really about: a robot with a lift DOWN is not the robot
        // that tips. Drive every prismatic joint to its upper limit on paper and see where the
        // centre of mass ends up.
        float raisedMoment = LiftedMoment(root, rootTransform, out r.liftTravel);
        r.comHeightRaised = r.comHeight + raisedMoment / r.totalMass;
        r.tipGRaised = r.comHeightRaised > 0.01f ? TipG(r.halfTrack, r.comHeightRaised) : r.tipG;

        r.tractionG = DrivetrainTuning.MeasureFriction(wheels);
        r.groundClearance = GroundClearance(root, wheelSet, lowest, out r.lowestPart);
        return r;
    }

    // The plane the tyres actually touch, in the ROOT's space. Positive infinity if no wheel link
    // carries a sphere, which the caller treats as "fall back and warn" rather than as a height.
    //
    // Read off the wheels' own SphereColliders rather than LinkCentre minus a fitted radius. Those
    // two agree today only because the rig gives a wheel link exactly one collider and nothing else;
    // add a hub or a gear to a wheel link and the volume-weighted centroid walks off the axle,
    // taking the ground plane — and therefore every clearance number — with it. Averaging radii
    // across wheels has the same shape of problem on a robot with two wheel sizes.
    private static float WheelContactPlane(List<ArticulationBody> wheels, Transform root)
    {
        float lowest = float.PositiveInfinity;
        foreach (ArticulationBody wheel in wheels)
        {
            if (wheel == null) continue;
            foreach (SphereCollider sphere in wheel.GetComponentsInChildren<SphereCollider>(true))
            {
                if (sphere == null || sphere.isTrigger || !sphere.enabled) continue;
                if (!sphere.gameObject.activeSelf) continue;
                // Stop at a child link: its colliders belong to it, not to this wheel.
                if (sphere.GetComponentInParent<ArticulationBody>(true) != wheel) continue;
                lowest = Mathf.Min(lowest, LowestPoint(sphere, root));
            }
        }
        return lowest;
    }

    // How far the lowest NON-WHEEL collider sits above the plane the wheels touch.
    //
    // This is the number that decides whether a robot can drive at all, and nothing surfaced it
    // before. A drivetrain resting on a bracket instead of its tyres will spin its wheels and go
    // nowhere — which is what PhysicsSmokeTest reports as "the wheels spun but the robot didn't
    // turn", and it names the offending part. Worth watching after any mass change, since a robot
    // with only a few mm to spare has no margin for the chassis settling into its contacts.
    private static float GroundClearance(ArticulationBody root, HashSet<ArticulationBody> wheels,
        float contactPlane, out string lowestPart)
    {
        lowestPart = null;
        float lowest = float.PositiveInfinity;

        foreach (Collider col in root.GetComponentsInChildren<Collider>(true))
        {
            if (col == null || col.isTrigger || !col.enabled || !col.gameObject.activeSelf) continue;
            ArticulationBody owner = col.GetComponentInParent<ArticulationBody>(true);
            if (owner == null || wheels.Contains(owner)) continue;

            float bottom = LowestPoint(col, root.transform);
            if (bottom >= lowest) continue;
            lowest = bottom;
            lowestPart = col.transform.parent != null
                ? $"{col.transform.parent.name}/{col.name}" : col.name;
        }
        return float.IsPositiveInfinity(lowest) ? 0f : lowest - contactPlane;
    }

    // Lowest point of a collider IN THE ROOT'S SPACE, projecting its oriented box onto the root's up
    // axis rather than reading Collider.bounds — which, on a prefab asset with no PhysX shapes, is a
    // degenerate box at the origin and would report every part as buried under the floor.
    //
    // THE `root` ARGUMENT IS THE WHOLE POINT AND IS NOT OPTIONAL. This used to answer in WORLD space
    // while its only caller compared the result against a contact plane in ROOT space, so every
    // ground-clearance figure this window has ever printed was off by exactly the prefab root's own
    // y position. Measured against the prefabs' real geometry: 654V_v3 reported -21.4 mm and is
    // +1.6; 654V_v2 reported +81.8 and is +10.5; 654V_v1 reported -52.6 and is +10.6; the residual
    // is the root y (-0.230, +0.713, -0.632 units) to within 0.1 mm on all four. Nothing was ever
    // dragging on the floor. `comHeight` was never affected — LinkCentre already returns root space,
    // so that subtraction was between two numbers in the same frame.
    private static float LowestPoint(Collider col, Transform root)
    {
        Transform t = col.transform;
        Vector3 lossy = t.lossyScale;
        float uniform = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Max(Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));

        // A radius is a length, so it converts by the root's own scale, not by InverseTransformVector
        // (which would shear it on a non-uniformly scaled root). Exact for the unit-scale roots the
        // rig produces; on anything else it is the best a single scalar can be.
        Vector3 rootLossy = root.lossyScale;
        float rootUniform = Mathf.Max(Mathf.Abs(rootLossy.x),
            Mathf.Max(Mathf.Abs(rootLossy.y), Mathf.Abs(rootLossy.z)));
        if (rootUniform <= 1e-6f) rootUniform = 1f;

        Vector3 centre;
        Vector3 halfExtents;
        switch (col)
        {
            case SphereCollider sphere:
                return root.InverseTransformPoint(t.TransformPoint(sphere.center)).y
                       - sphere.radius * uniform / rootUniform;
            case CapsuleCollider capsule:
                return root.InverseTransformPoint(t.TransformPoint(capsule.center)).y
                       - capsule.height * uniform * 0.5f / rootUniform;
            case BoxCollider box:
                centre = t.TransformPoint(box.center);
                halfExtents = box.size * 0.5f;
                break;
            case MeshCollider mesh when mesh.sharedMesh != null:
                centre = t.TransformPoint(mesh.sharedMesh.bounds.center);
                halfExtents = mesh.sharedMesh.bounds.extents;
                break;
            default:
                return float.PositiveInfinity;
        }

        // Vertical half-extent of the oriented box: each local axis contributes its own half-size
        // times how much of that axis points down IN ROOT SPACE. Using the local Y size alone would
        // be wrong for a rotated part, which every C-channel on these robots is; using world down
        // would be wrong for a rotated root.
        float drop = 0f;
        drop += Mathf.Abs(root.InverseTransformVector(t.rotation * new Vector3(lossy.x, 0f, 0f)).y) * halfExtents.x;
        drop += Mathf.Abs(root.InverseTransformVector(t.rotation * new Vector3(0f, lossy.y, 0f)).y) * halfExtents.y;
        drop += Mathf.Abs(root.InverseTransformVector(t.rotation * new Vector3(0f, 0f, lossy.z)).y) * halfExtents.z;
        return root.InverseTransformPoint(centre).y - drop;
    }

    // How much upward moment (kg * world units) the robot gains with every prismatic joint driven
    // to its upper limit.
    //
    // A prismatic link carries everything below it in the tree, so the contribution of each joint
    // is its own travel times the mass of its whole subtree — which is also what makes nesting come
    // out right without special-casing it: on a three-stage cascade, stage 1's travel lifts stages
    // 2 and 3 as well, and stage 2's travel lifts stage 3 again.
    //
    // Prismatic only. A revolute mechanism sweeps an arc whose vertical extent depends on where its
    // centre of mass sits relative to the axis, and guessing at that would be worse than declaring
    // it out of scope — see the DR4B note in the header, which is the mechanism this omits.
    private static float LiftedMoment(ArticulationBody root, Transform rootTransform, out float travelSum)
    {
        float moment = 0f;
        travelSum = 0f;

        foreach (ArticulationBody body in root.GetComponentsInChildren<ArticulationBody>(true))
        {
            if (body == null || body == root) continue;
            if (body.jointType != ArticulationJointType.PrismaticJoint) continue;
            if (body.linearLockX == ArticulationDofLock.LockedMotion) continue; // some other axis slides
            if (body.transform.parent == null) continue;

            float travel = body.xDrive.upperLimit - body.xDrive.lowerLimit;
            if (travel <= 0f) continue;

            // The joint frame is anchored on the PARENT and rotated by parentAnchorRotation, so the
            // slide direction is that frame's +X in world space. Converted into the ROOT's space
            // rather than dotted against world up, because comHeight is measured there too — a
            // robot whose prefab root carries a rotation or a scale would otherwise have its lift
            // travel and its centre-of-mass height expressed in different units.
            Vector3 axis = body.transform.parent.rotation * body.parentAnchorRotation * Vector3.right;
            float rise = rootTransform.InverseTransformVector(axis * travel).y;
            if (Mathf.Abs(rise) < 1e-4f) continue; // slides sideways; contributes no height

            float subtree = 0f;
            foreach (ArticulationBody below in body.GetComponentsInChildren<ArticulationBody>(true))
                if (below != null) subtree += below.mass;

            moment += rise * subtree;
            travelSum += rise;
        }
        return moment;
    }

    // A link's centre of mass: the volume-weighted centroid of its OWN colliders, expressed in the
    // robot root's local space. Not its children's — those are separate links carrying their own
    // mass — so the walk stops at any descendant that has an ArticulationBody of its own.
    //
    // EVERYTHING HERE IS TRANSFORM MATH, DELIBERATELY. The obvious implementation reads
    // Collider.bounds, and it is wrong in this context twice over: bounds is a query against the
    // PhysX scene, and a prefab ASSET has no PhysX shapes at all, so it returns a degenerate box at
    // the origin. Disabled colliders do the same even in a live scene, and robots carry hundreds of
    // them (the DR4B's visual-only parts are all disabled). Either way the centroid gets dragged
    // toward the origin, the composite COM sinks below the wheels, and the tip threshold explodes
    // to whatever halfTrack/1e-4 happens to be.
    //
    // TransformPoint and lossyScale are pure hierarchy arithmetic, valid on an asset, so this
    // measures the same numbers whether the prefab is open in a scene or not.
    private static Vector3 LinkCentre(ArticulationBody body, Transform root, out bool hadCollider)
    {
        Vector3 weighted = Vector3.zero;
        float volume = 0f;
        Accumulate(body.transform, body, ref weighted, ref volume);

        hadCollider = volume > 0f;
        // No collider means automaticCenterOfMass puts this link's mass at its own origin — which
        // for a builder-created motor hub is on the joint axis, sometimes below the floor. That is
        // real, so it is reported rather than hidden.
        Vector3 world = hadCollider ? weighted / volume : body.transform.position;
        return root.InverseTransformPoint(world);
    }

    private static void Accumulate(Transform node, ArticulationBody owner, ref Vector3 weighted, ref float volume)
    {
        if (node != owner.transform && node.GetComponent<ArticulationBody>() != null) return; // child link

        foreach (Collider col in node.GetComponents<Collider>())
        {
            if (col == null || col.isTrigger || !col.enabled || !node.gameObject.activeSelf) continue;
            float v = ColliderVolume(col, out Vector3 centre);
            if (v <= 0f) continue;
            weighted += centre * v;
            volume += v;
        }
        foreach (Transform child in node) Accumulate(child, owner, ref weighted, ref volume);
    }

    // Volume in world units cubed, and the collider's centre in world space. Volumes are only ever
    // used as relative weights within one link, so a consistent scale is all that is required.
    private static float ColliderVolume(Collider col, out Vector3 centre)
    {
        Vector3 lossy = col.transform.lossyScale;
        float uniform = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Max(Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));
        switch (col)
        {
            case SphereCollider sphere:
            {
                centre = col.transform.TransformPoint(sphere.center);
                float rad = sphere.radius * uniform;
                return 4f / 3f * Mathf.PI * rad * rad * rad;
            }
            case BoxCollider box:
            {
                centre = col.transform.TransformPoint(box.center);
                Vector3 size = Vector3.Scale(box.size, lossy);
                return Mathf.Abs(size.x * size.y * size.z);
            }
            case MeshCollider mesh when mesh.sharedMesh != null:
            {
                // Convex hulls: sized by the mesh's own local bounds, which over-states a thin
                // plate. Every robot's plates are over-stated equally, so comparisons still hold.
                Bounds local = mesh.sharedMesh.bounds;
                centre = col.transform.TransformPoint(local.center);
                Vector3 size = Vector3.Scale(local.size, lossy);
                return Mathf.Abs(size.x * size.y * size.z);
            }
            case CapsuleCollider capsule:
            {
                centre = col.transform.TransformPoint(capsule.center);
                float rad = capsule.radius * uniform;
                float height = Mathf.Max(capsule.height * uniform - 2f * rad, 0f);
                return Mathf.PI * rad * rad * (height + 4f / 3f * rad);
            }
            default:
                centre = col.transform.position;
                return 0f;
        }
    }

    // The wheels' footprint: which horizontal axis is the TRACK (the one a turn tips the robot over)
    // and which is the WHEELBASE (the one a reversal tips it over), plus where the outermost contact
    // lines sit along each.
    //
    // The two axes were not worth separating while only the roll threshold was reported — the old
    // HalfTrack took the smaller spread and threw the larger one away with a one-line comment. They
    // matter now because the question that started this was "why doesn't slamming reverse tip it",
    // and that tips over the wheelbase.
    //
    // WHICH IS WHICH IS NOT A GEOMETRY QUESTION, and answering it as one was wrong on every robot in
    // this project. This used to decide with `baseIsZ = spreadZ >= spreadX` — "the wheelbase is the
    // longer spread" — and every one of these four robots is WIDER THAN IT IS LONG, so it labelled
    // the track as the wheelbase and swapped its own two margins, on all four, silently. 654V_v3
    // came out with a 127 mm half-track against TipOverValidation's 93.1 mm for the same robot, and
    // the disagreement was never chased because both numbers looked plausible.
    //
    // The wheels know. The rig aligns every wheel link's local +X with robot RIGHT, so the mean
    // wheel axle IS the track axis and the wheelbase is perpendicular to it — measured, not guessed,
    // and the same derivation RobotMotorController.MeasureDriveAxes uses so the tool and the
    // drivetrain cannot disagree about which way the robot faces.
    private struct Footprint
    {
        public float halfTrack;      // half the shorter horizontal spread
        public float trackMin;       // outermost wheel centre across the track, root-local
        public float trackMax;
        public float baseMin;        // ...and along the wheelbase
        public float baseMax;
        public bool baseIsZ;         // wheelbase runs along root Z (the usual case) rather than X
    }

    private static Footprint MeasureFootprint(List<Vector3> centres, bool baseIsZ)
    {
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        foreach (Vector3 c in centres)
        {
            minX = Mathf.Min(minX, c.x); maxX = Mathf.Max(maxX, c.x);
            minZ = Mathf.Min(minZ, c.z); maxZ = Mathf.Max(maxZ, c.z);
        }

        var f = new Footprint { baseIsZ = baseIsZ };
        f.baseMin = baseIsZ ? minZ : minX;
        f.baseMax = baseIsZ ? maxZ : maxX;
        f.trackMin = baseIsZ ? minX : minZ;
        f.trackMax = baseIsZ ? maxX : maxZ;
        f.halfTrack = Mathf.Max((f.trackMax - f.trackMin) * 0.5f, 1e-4f);
        return f;
    }

    // The track axis, from the wheels' own axles. Root-local, and only ever answering "X or Z"
    // because everything downstream indexes root-local components; a robot whose axles sit at 45
    // degrees to its own root would need more than this, and would also need more than this
    // everywhere else.
    private static bool WheelbaseRunsAlongZ(IEnumerable<ArticulationBody> wheels, Transform root)
    {
        Vector3 sum = Vector3.zero;
        foreach (ArticulationBody wheel in wheels)
        {
            if (wheel == null) continue;
            Vector3 axle = root.InverseTransformDirection(wheel.transform.right);
            if (sum != Vector3.zero && Vector3.Dot(axle, sum) < 0f) axle = -axle;
            sum += axle;
        }
        // The axle runs across the TRACK, so the wheelbase is the other axis. No wheels, or axles
        // pointing straight up, falls back to the old assumption rather than inventing one.
        if (sum.sqrMagnitude < 1e-6f) return true;
        return Mathf.Abs(sum.x) >= Mathf.Abs(sum.z);
    }

    // Acceleration, in g, that puts the robot over an edge `margin` from its centre of mass when
    // that centre sits `height` above the contact plane. Both are root-local units, so the ratio is
    // dimensionless and the world scale cancels.
    internal static float TipG(float margin, float height)
        => height > 0.01f ? Mathf.Max(margin, 0f) / height : 0f;

    // --- Applying -------------------------------------------------------------------------------

    private void ApplyAll()
    {
        if (!EditorUtility.DisplayDialog("Apply VEX Masses",
                $"Set every robot's chassis link to {ChassisMass} kg and every wheel link to " +
                $"{WheelMass} kg, then re-bake the drives?\n\n" +
                "Mechanism links are left alone. Straight-line acceleration barely changes " +
                "(drive force is derived from mu*m*g, so force and inertia scale together); what " +
                "changes is that a raised lift finally moves the centre of mass enough to tip.\n\n" +
                "This rewrites the robot prefabs. Close any scene that has a robot open first.",
                "Apply", "Cancel"))
            return;

        var log = new StringBuilder();
        int changed = 0;
        foreach (Report r in reports)
        {
            if (!string.IsNullOrEmpty(r.note)) continue;
            if (Apply(r.path, log)) changed++;
        }
        AssetDatabase.SaveAssets();
        Measure();
        status = $"{changed} prefab(s) updated.\n{log}";
        Debug.Log("Robot Mass & Balance:\n" + log);
    }

    private static bool Apply(string path, StringBuilder log)
    {
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            RobotMotorController motor = root.GetComponent<RobotMotorController>();
            ArticulationBody rootBody = root.GetComponent<ArticulationBody>();
            if (motor == null || rootBody == null) return false;

            var wheels = new List<ArticulationBody>();
            if (motor.leftWheels != null) foreach (ArticulationBody w in motor.leftWheels) if (w != null) wheels.Add(w);
            if (motor.rightWheels != null) foreach (ArticulationBody w in motor.rightWheels) if (w != null) wheels.Add(w);
            if (wheels.Count == 0) return false;

            // Write first, decide afterwards. The mechanism floor is RELATIVE to the chassis mass,
            // so "has anything changed" cannot be answered before the chassis mass is written — and
            // the old check, which compared only the chassis and wheels, would have declared a robot
            // "unchanged" and skipped the save on exactly the prefabs whose chassis was already
            // right and whose aligner was still 0.034 kg.
            bool dirty = !Mathf.Approximately(rootBody.mass, ChassisMass);
            foreach (ArticulationBody wheel in wheels)
                if (!Mathf.Approximately(wheel.mass, WheelMass)) dirty = true;

            rootBody.mass = ChassisMass;
            foreach (ArticulationBody wheel in wheels) wheel.mass = WheelMass;

            // ...and re-run the LIFT links' own rule, because MechanismBuildUtil.MinLiftMass is a
            // build-time constant and the robots were built before it moved. It is the dominant
            // balance lever now — a cascade stage is the only mass on these robots that travels
            // 600 mm upward — so leaving it to "rebuild the robot" would mean the one number that
            // decides whether a raised lift rolls the robot over could never be corrected in place.
            //
            // Re-DERIVED, not scaled: each stage is re-measured from its own geometry and the floor
            // re-applied, so a stage that genuinely masses more than the floor keeps its own mass and
            // this stays correct if the floor moves again.
            //
            // Identified from CascadeLift's OWN stage list rather than by joint shape. "Any prismatic
            // link with travel" looks equivalent and is not: it also catches pneumatic slides, and
            // PneumaticBuilder never applies this floor. Sweeping by shape put 0.9 kg on 654V_v3's
            // goal aligner — a 34 g polycarbonate plate — which is not re-running the builder's rule,
            // it is inventing a new one. The DR4B's ballast is likewise excluded: it is a mass PROXY
            // for geometry that lives elsewhere in the hierarchy, so measuring its own empty node
            // would zero it, and ApplyDr4bBallastTool owns that number.
            var liftLinks = new HashSet<ArticulationBody>();
            foreach (CascadeLift lift in root.GetComponentsInChildren<CascadeLift>(true))
            {
                if (lift.stages == null) continue;
                foreach (CascadeLift.Stage stage in lift.stages)
                    if (stage != null && stage.body != null) liftLinks.Add(stage.body);
            }

            var relifted = new List<string>();
            foreach (ArticulationBody body in liftLinks)
            {
                float geometry = RobotMassFromGeometry.MassAndCentre(
                    new[] { body.gameObject }, root.transform, WorldScaleFactor, out _, out _);
                float wanted = Mathf.Max(geometry, MechanismBuildUtil.MinLiftMass);
                if (Mathf.Abs(wanted - body.mass) <= 1e-3f) continue;

                relifted.Add($"{body.name} {body.mass:0.##}->{wanted:0.##}");
                body.mass = wanted;
                body.ResetInertiaTensor();
                dirty = true;
            }

            if (!dirty) { log.AppendLine($"  {root.name}: unchanged"); return false; }

            // The traction budget is mu*m*g, so every drive constant depends on the mass that just
            // changed. Re-bake in the same pass or edit-mode simulation (PhysicsSmokeTest) measures
            // a drivetrain tuned for the old weight.
            DrivetrainTuning.Result tuning = RigDrivetrainArticulation.ApplyDriveTuning(root, useUndo: false);
            log.AppendLine($"  {root.name}: chassis {ChassisMass} kg, {wheels.Count} wheels at " +
                           $"{WheelMass} kg — {RigDrivetrainArticulation.DescribeTuning(tuning)}");
            if (relifted.Count > 0)
                log.AppendLine($"    lift links re-massed (floor {MechanismBuildUtil.MinLiftMass} kg): " +
                               string.Join(", ", relifted));

            PrefabUtility.SaveAsPrefabAsset(root, path);
            return true;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
