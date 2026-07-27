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
//   • RigDrivetrainArticulation puts a hard-coded 24 kg on the chassis link, which is 66-68% of
//     every shipped robot, sitting low and never moving.
//   • Each wheel link is 1 kg — 6 kg of ballast at axle height, when a real VEX 2.75" omni is
//     about 0.11 kg.
//   • Every lift link lands on MechanismBuildUtil.MinLiftMass (1.5 kg), which has won on 100% of
//     the shipped links: the mass-from-geometry pass computes near-zero volume for thin plates, so
//     the floor is the value, not a floor.
//
// Net effect: raising a full cascade lift moves the composite COM by ~44 mm, and every shipped
// robot's tip threshold (1.16-1.95 g, or 0.92 g for a fully raised cascade) sits ABOVE the 0.8 g
// its tyres can actually deliver. The robots physically cannot tip themselves by driving, in any
// configuration, no matter how hard a reversal is slammed. For scale, a real VEX V5 robot is at
// most 11.3 kg with its COM 150-200 mm up on a ~300 mm track, and tips well under 0.5 g with a
// loaded lift raised.
//
// Applying the VEX-realistic masses below leaves acceleration essentially unchanged — DrivetrainTuning
// derives drive force from mu*m*g, so force and inertia scale together — and changes only what mass
// distribution was ever supposed to change: stability.
//
// TWO THINGS THIS DOES NOT FIX, both worth knowing before reading the report:
//   • The DR4B contributes exactly 0 mm of COM rise on 654V_v1, and no mass change here will alter
//     that. Its stages are not ArticulationBody links at all — they are transform-posed visuals
//     (Dr4bMoveFollower / PivotRotateFollower) owned by the chassis link with their colliders
//     disabled. The only DR4B body is a colliderless 1.5 kg motor hub whose COM sits on its own
//     rotation axis, so rotating it cannot move it. Making the DR4B affect balance means giving it
//     real links, which is a builder change, not a mass change.
//   • A carried game piece is weightless: ClawGrab and IntakePull set isKinematic on the piece, and
//     a kinematic body contributes no mass to the solver. A loaded lift is indistinguishable from
//     an empty one.
//
// Usage: Tools > RoboSim > Robot > Mass & Balance. Measuring is always safe; applying rewrites
// prefabs and then re-bakes the drives (mass feeds the traction budget).
public class RobotBalanceWindow : EditorWindow
{
    private const string RobotsFolder = "Assets/Robots";
    private const string UndoName = "Apply Robot Masses";

    // The rig tool is the authority on these — it is what writes them on a fresh rig, and this
    // window's whole job is bringing already-rigged robots into line with it.
    private const float ChassisMass = RigDrivetrainArticulation.RootMass;
    private const float WheelMass = RigDrivetrainArticulation.WheelMass;

    private readonly List<Report> reports = new List<Report>();
    private Vector2 scroll;
    private string status;

    private struct Report
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
        public float tipG;          // lateral acceleration that tips it, in g
        public float tipGRaised;    // ...with the lifts up
        public float liftTravel;    // total vertical travel available, world units
        public float tractionG;     // what the tyres can actually deliver
        public int wheelCount;
        public int colliderlessLinks;
        public float colliderlessMass;
        public string note;
    }

    [MenuItem("Tools/RoboSim/Robot/Mass & Balance", false, 5)]
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
            "Tip threshold is the lateral acceleration that would put the robot over, from its " +
            "measured centre of mass and half-track. Compare it against the traction ceiling: the " +
            "tyres cannot push harder than mu*g, so a robot whose tip threshold is ABOVE its " +
            "traction ceiling can never tip itself by driving, however hard you slam a reversal.\n\n" +
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
            EditorGUILayout.LabelField(
                $"COM {r.comHeight * 100f:0.} mm above the contact patch, half-track " +
                $"{r.halfTrack * 100f:0.} mm");

            bool canTip = r.tipG < r.tractionG;
            EditorGUILayout.LabelField(
                $"lift down: tips at {r.tipG:0.00} g   ·   tyres deliver {r.tractionG:0.00} g   ·   " +
                (canTip ? "CAN tip by driving" : "cannot tip by driving"),
                canTip ? EditorStyles.boldLabel : EditorStyles.label);

            if (r.liftTravel > 1e-3f)
            {
                bool canTipRaised = r.tipGRaised < r.tractionG;
                EditorGUILayout.LabelField(
                    $"lift up ({r.liftTravel * 100f:0.} mm): COM {r.comHeightRaised * 100f:0.} mm, " +
                    $"tips at {r.tipGRaised:0.00} g   ·   " +
                    (canTipRaised ? "CAN tip by driving" : "cannot tip by driving"),
                    canTipRaised ? EditorStyles.boldLabel : EditorStyles.label);
            }

            // Worth surfacing: a link with no enabled collider gets automaticCenterOfMass, which
            // puts its whole mass at the link origin. The mechanism builders place a motor hub's
            // origin on its joint axis, which can be below the floor — so this is mass actively
            // pulling the COM the wrong way, not just an unmeasured link.
            if (r.colliderlessLinks > 0)
                EditorGUILayout.LabelField(
                    $"{r.colliderlessLinks} link(s) with no enabled collider carry " +
                    $"{r.colliderlessMass:0.00} kg at their own origin", EditorStyles.miniLabel);
        }
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

    private void Measure()
    {
        reports.Clear();
        status = null;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { RobotsFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;
            reports.Add(Measure(prefab, path));
        }
        if (reports.Count == 0) status = $"No robot prefabs with a RobotMotorController under {RobotsFolder}.";
    }

    private static Report Measure(GameObject prefab, string path)
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
        float radius = DrivetrainTuning.MeasureWheelRadius(wheels);
        float lowest = float.PositiveInfinity;
        var centres = new List<Vector3>();
        foreach (ArticulationBody wheel in wheels)
        {
            Vector3 c = LinkCentre(wheel, rootTransform, out _);
            centres.Add(c);
            lowest = Mathf.Min(lowest, c.y - radius);
        }
        r.halfTrack = HalfTrack(centres);
        r.comHeight = com.y - lowest;

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

        r.tipG = r.halfTrack / r.comHeight;

        // The case the whole question is really about: a robot with a lift DOWN is not the robot
        // that tips. Drive every prismatic joint to its upper limit on paper and see where the
        // centre of mass ends up.
        float raisedMoment = LiftedMoment(root, rootTransform, out r.liftTravel);
        r.comHeightRaised = r.comHeight + raisedMoment / r.totalMass;
        r.tipGRaised = r.comHeightRaised > 0.01f ? r.halfTrack / r.comHeightRaised : r.tipG;

        r.tractionG = DrivetrainTuning.MeasureFriction(wheels);
        return r;
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

    // Widest separation of the wheel centres perpendicular to the wheelbase — i.e. the track. Found
    // as the smaller spread of the two horizontal extents, since the longer one is the wheelbase.
    private static float HalfTrack(List<Vector3> centres)
    {
        float minX = float.PositiveInfinity, maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity, maxZ = float.NegativeInfinity;
        foreach (Vector3 c in centres)
        {
            minX = Mathf.Min(minX, c.x); maxX = Mathf.Max(maxX, c.x);
            minZ = Mathf.Min(minZ, c.z); maxZ = Mathf.Max(maxZ, c.z);
        }
        return Mathf.Max(Mathf.Min(maxX - minX, maxZ - minZ) * 0.5f, 1e-4f);
    }

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

            bool dirty = !Mathf.Approximately(rootBody.mass, ChassisMass);
            rootBody.mass = ChassisMass;
            foreach (ArticulationBody wheel in wheels)
            {
                if (!Mathf.Approximately(wheel.mass, WheelMass)) dirty = true;
                wheel.mass = WheelMass;
            }
            if (!dirty) { log.AppendLine($"  {root.name}: unchanged"); return false; }

            // The traction budget is mu*m*g, so every drive constant depends on the mass that just
            // changed. Re-bake in the same pass or edit-mode simulation (PhysicsSmokeTest) measures
            // a drivetrain tuned for the old weight.
            DrivetrainTuning.Result tuning = RigDrivetrainArticulation.ApplyDriveTuning(root, useUndo: false);
            log.AppendLine($"  {root.name}: chassis {ChassisMass} kg, {wheels.Count} wheels at " +
                           $"{WheelMass} kg — {RigDrivetrainArticulation.DescribeTuning(tuning)}");

            PrefabUtility.SaveAsPrefabAsset(root, path);
            return true;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }
}
