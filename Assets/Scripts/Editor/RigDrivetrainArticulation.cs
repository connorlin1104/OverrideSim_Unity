using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

// One-click conversion of the driving robot from the old "teleport-velocity" Rigidbody stack
// to a proper ArticulationBody rig with torque-driven wheels.
//
// The old RobotDriveController force-sets linear/angular velocity every step, which can never
// stall, slip, or push with honest contact forces. This tool rebuilds the robot as an
// articulation: the wrapper becomes the root ArticulationBody, each wheel cluster (found by
// RobotPartClassifier) becomes a revolute-jointed link child carrying its WS-B SphereColliders,
// and a RobotMotorController drives the joints with velocity drives limited by motor torque.
// It also flips the physics solver to TGS (Temporal Gauss-Seidel), which PhysX recommends for
// articulations and drives.
//
// Axis note (verified against the Unity PhysicsModule docs): a RevoluteJoint rotates about the
// X axis of the joint anchor. Every wheel link is created with world rotation == wrapper
// rotation, so link-local +X is already the axle (the wrapper's right axis) and no
// anchorRotation gymnastics are needed.
//
// Usage: select the Robot object in the Hierarchy, then
// Tools > RoboSim > Robot > Mechanisms > Rig Drivetrain.
// Batch: -executeMethod RigDrivetrainArticulation.RunBatchOnRobot
// (opens SampleScene, rigs the Robot, saves).
public class RigDrivetrainArticulation
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string UndoName = "Rig Drivetrain Articulation";
    private const string AddWheelsUndo = "Add Wheels to Drivetrain";

    private const int ExpectedWheelClusters = 6;
    private const float RootMass = 24f;          // chassis; each of the 6 wheel links adds 1 => ~30 total, matching the old rig
    private const float WheelMass = 1f;
    private const float WheelStallTorque = 700f; // matches RobotMotorController.wheelStallTorque
    private const float WheelDriveDamping = 1000f;
    private const float WheelMaxJointVelocity = 44f; // rad/s — 360 RPM (37.7 rad/s) plus headroom
    // Drivetrain-loss knobs baked into the wheel links (a real dt is never frictionless). These
    // match RobotMotorController.wheelRollingResistance / wheelSpinDamping, which re-applies them
    // to every wheel at play — that's the runtime authority; these keep edit-mode simulation
    // (PhysicsSmokeTest) and freshly-rigged serialized values consistent with it.
    private const float WheelRollingResistance = 0.3f; // Coulomb axle friction (coast-to-stop feel)
    private const float WheelSpinDamping = 0.5f;        // velocity-proportional spin loss (bleeds top speed)
    private const float AxleAngleToleranceDeg = 10f;

    // Rig (or re-rig) the drivetrain. The selection decides how the wheels are found:
    //   • Select the ROBOT root  → wheels are auto-detected by name (RobotPartClassifier).
    //   • Select the DRIVE WHEELS (2+ parts) → each selected part becomes one wheel — the escape
    //     hatch for when auto-detect grabs the wrong parts (e.g. colliderless cosmetic wheels).
    // Clean Robot Rig (Reset) first if the robot is already rigged.
    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Rig Drivetrain", false, 4)]
    private static void RigDrivetrainMenu()
    {
        GameObject[] selection = Selection.gameObjects;
        if (selection == null || selection.Length == 0)
        {
            EditorUtility.DisplayDialog(UndoName,
                "Select the Robot root to auto-detect its wheels, or select the drive wheel parts " +
                "(2 or more) to rig from exactly those.", "OK");
            return;
        }

        try
        {
            if (selection.Length == 1)
            {
                // One object selected = the robot root; auto-detect the wheels by name.
                Rig(selection[0]);
            }
            else
            {
                // Several objects selected = the drive wheels; rig from exactly those.
                GameObject robot = ResolveRobotRootFromSelection(selection);
                if (robot == null)
                {
                    EditorUtility.DisplayDialog(UndoName,
                        "Couldn't find the robot root from the selection. Select wheels under a set-up " +
                        "robot (one with RobotMechanisms), or select the robot's top object to auto-detect.", "OK");
                    return;
                }
                int n = RigFromWheelParts(robot, selection);
                RobotMotorController motor = robot.GetComponent<RobotMotorController>();
                EditorUtility.DisplayDialog(UndoName,
                    $"Rigged the drivetrain from {n} selected wheel(s): {motor.leftWheels.Length} left / " +
                    $"{motor.rightWheels.Length} right.\n\nSave, then Validate Robot Physics.", "OK");
            }
        }
        catch (System.InvalidOperationException e)
        {
            EditorUtility.DisplayDialog(UndoName, e.Message, "OK");
        }
    }

    // Robot root = the ancestor carrying RobotMechanisms (kept even after Clean Robot Rig), else the
    // topmost transform of the first selected object.
    private static GameObject ResolveRobotRootFromSelection(GameObject[] selection)
    {
        foreach (GameObject go in selection)
        {
            if (go == null) continue;
            RobotMechanisms reg = go.GetComponentInParent<RobotMechanisms>();
            if (reg != null) return reg.gameObject;
        }
        foreach (GameObject go in selection)
            if (go != null) return go.transform.root.gameObject;
        return null;
    }

    // Adds the selected wheel part(s) to an already-rigged drivetrain, for when auto-detect missed
    // some (only 2 wheels spin per side). Each selected part becomes a revolute wheel link on the
    // near side and is appended to the RobotMotorController arrays, so it spins with the rest.
    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Add Selected Wheels to Drivetrain", false, 5)]
    private static void AddSelectedWheels()
    {
        GameObject[] selection = Selection.gameObjects;
        if (selection == null || selection.Length == 0)
        {
            EditorUtility.DisplayDialog(AddWheelsUndo,
                "Select the wheel part(s) to add in the Hierarchy first.", "OK");
            return;
        }

        // The rigged robot is the RobotMotorController root above any selected wheel part.
        GameObject robot = null;
        foreach (GameObject go in selection)
        {
            RobotMotorController m = go != null ? go.GetComponentInParent<RobotMotorController>() : null;
            if (m != null) { robot = m.gameObject; break; }
        }
        if (robot == null)
        {
            EditorUtility.DisplayDialog(AddWheelsUndo,
                "None of the selected objects are under a rigged robot (no RobotMotorController on the " +
                "root). Run Rig Drivetrain first.", "OK");
            return;
        }

        try
        {
            int added = AddWheelsToDrivetrain(robot, selection);
            RobotMotorController motor = robot.GetComponent<RobotMotorController>();
            EditorUtility.DisplayDialog(AddWheelsUndo,
                added == 0
                    ? "No new wheels added (already wired, or the selection had no renderers)."
                    : $"Added {added} wheel(s). Drivetrain now has {motor.leftWheels.Length} left / " +
                      $"{motor.rightWheels.Length} right. Save the scene, then Validate Robot Physics.",
                "OK");
        }
        catch (System.InvalidOperationException e)
        {
            EditorUtility.DisplayDialog(AddWheelsUndo, e.Message, "OK");
        }
    }

    // Batch entry for -executeMethod: opens the main scene, rigs the Robot, saves. Throws
    // (nonzero editor exit) on any failure instead of showing dialogs.
    public static void RunBatchOnRobot()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        GameObject robot = null;
        // Unity 6000.5 deprecated the FindObjectsSortMode overload; FindObjectsInactive.Exclude
        // matches the old SortMode.None behavior (active objects only).
        foreach (RobotDriveController drive in Object.FindObjectsByType<RobotDriveController>(FindObjectsInactive.Exclude))
        {
            if (robot == null || drive.gameObject.name == "Robot") robot = drive.gameObject;
        }
        if (robot == null)
            throw new System.InvalidOperationException(
                $"{UndoName}: no GameObject with a RobotDriveController found in {ScenePath}.");

        Rig(robot);

        if (!EditorSceneManager.SaveScene(scene))
            throw new System.InvalidOperationException($"{UndoName}: failed to save {ScenePath}.");
        AssetDatabase.SaveAssets();
        Debug.Log($"{UndoName}: rigged '{robot.name}' and saved {ScenePath}.");
    }

    // Converts the given robot wrapper into an ArticulationBody rig. Throws
    // InvalidOperationException with a user-readable message on any precondition failure
    // (all validation happens BEFORE the first mutation, so a throw leaves the scene intact).
    // wheelNamePrefix: null uses this project's drivetrain wheel name; pass a new robot's wheel
    // node prefix when rigging a freshly imported mesh robot.
    public static void Rig(GameObject robot, string wheelNamePrefix = null)
    {
        if (robot == null) throw new System.ArgumentNullException(nameof(robot));

        // --- Preconditions (validate everything before touching the scene) ---------------

        // WS-B's per-part colliders (wheel SphereColliders in particular) must already exist:
        // the links inherit them by reparenting, and inertia tensors are derived from them.
        if (robot.GetComponentsInChildren<Collider>(true).Length == 0)
            throw new System.InvalidOperationException(
                $"No colliders found under '{robot.name}'. Run Tools > RoboSim > Robot > Advanced > Rebuild Part Colliders first — " +
                "wheel links need their sphere colliders before rigging.");

        // Idempotency guard: never double-rig.
        if (robot.GetComponentInChildren<ArticulationBody>(true) != null)
            throw new System.InvalidOperationException(
                $"'{robot.name}' already contains an ArticulationBody — it appears to be rigged already.");

        List<RobotPartClassifier.WheelCluster> clusters = RobotPartClassifier.FindWheelClusters(robot, wheelNamePrefix);
        if (clusters == null || clusters.Count == 0)
            throw new System.InvalidOperationException(
                $"RobotPartClassifier found no wheel clusters under '{robot.name}'; cannot rig.");
        if (clusters.Count != ExpectedWheelClusters)
            Debug.LogWarning($"{UndoName}: expected {ExpectedWheelClusters} wheel clusters, found {clusters.Count}. " +
                             "Rigging anyway — check the drivetrain naming/geometry.", robot);

        RigWithClusters(robot, clusters);
    }

    // Rigs a drivetrain from an explicit list of wheel parts (each part = one wheel) instead of
    // name-based auto-detection — use it when the wheels are named unpredictably or auto-detect grabs
    // the wrong parts (e.g. it drove wheels with no ground colliders). Flow: Clean Robot Rig (Reset),
    // select the actual drive wheels (the ones that touch the ground / carry the sphere colliders),
    // then run this. Throws if the robot is already rigged (Clean first) or nothing selectable was
    // found. Returns the number of wheels rigged.
    public static int RigFromWheelParts(GameObject robot, IList<GameObject> wheelParts)
    {
        if (robot == null) throw new System.ArgumentNullException(nameof(robot));
        if (wheelParts == null || wheelParts.Count == 0)
            throw new System.InvalidOperationException("No wheel parts selected.");
        if (robot.GetComponentInChildren<ArticulationBody>(true) != null)
            throw new System.InvalidOperationException(
                $"'{robot.name}' is already rigged (has an ArticulationBody). Run Clean Robot Rig (Reset) " +
                "first, then rig from the selected wheels.");

        List<RobotPartClassifier.WheelCluster> clusters = new List<RobotPartClassifier.WheelCluster>();
        int skippedNoCollider = 0;
        foreach (GameObject part in wheelParts)
        {
            if (part == null || part.transform == robot.transform) continue;
            if (!part.transform.IsChildOf(robot.transform)) continue;
            Renderer[] renderers = part.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) continue;
            // A drive wheel needs a ground-contact collider. Skipping parts without one keeps the tool
            // from re-driving colliderless cosmetic wheels (the exact bug that broke the last rig).
            if (part.GetComponentInChildren<Collider>(true) == null)
            {
                skippedNoCollider++;
                Debug.LogWarning($"{UndoName}: skipped '{part.name}' — it has no collider, so it can't be a " +
                                 "ground-contact drive wheel. (If it should be, run Rebuild Part Colliders first.)", part);
                continue;
            }
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            clusters.Add(new RobotPartClassifier.WheelCluster
            {
                topmost = part.transform,
                nodes = new List<Transform> { part.transform },
                worldBounds = bounds,
            });
        }
        if (clusters.Count == 0)
            throw new System.InvalidOperationException(
                skippedNoCollider > 0
                    ? "None of the selected parts have a collider, so none can be a ground-contact wheel. " +
                      "Run Rebuild Part Colliders first, then select the wheels and try again."
                    : "None of the selected parts have renderers to rig as wheels — select the wheel meshes.");

        RigWithClusters(robot, clusters);
        return clusters.Count;
    }

    // The shared rigging core: builds the root ArticulationBody + one revolute wheel link per cluster
    // (split into sides by the clusters' mean X), wires the RobotMotorController, and switches to TGS.
    // Shared by Rig (name-detected clusters) and RigFromWheelParts (hand-picked wheels) so both use
    // identical, validated logic.
    private static void RigWithClusters(GameObject robot, List<RobotPartClassifier.WheelCluster> clusters)
    {
        Transform wrapper = robot.transform;

        // A RobotDriveController is only present on the ORIGINAL velocity-driven robot; we lift its
        // joystick action references before deleting it. A freshly imported/cleaned robot has none,
        // which is fine — the actions are then loaded straight from the input asset.
        RobotDriveController drive = robot.GetComponent<RobotDriveController>();

        // --- Mutations (one collapsed undo group) -----------------------------------------

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName(UndoName);
        int undoGroup = Undo.GetCurrentGroup();

        // 1) Capture the input action references off the old controller, then remove the old
        //    stack. The controller must go FIRST: its [RequireComponent(typeof(Rigidbody))]
        //    blocks destroying the Rigidbody while the controller still exists. When there is no
        //    old controller (a fresh import), take the actions straight from the input asset.
        Object leftActionRef, rightActionRef;
        if (drive != null)
        {
            SerializedObject driveSo = new SerializedObject(drive);
            leftActionRef = driveSo.FindProperty("leftJoystickAction").objectReferenceValue;
            rightActionRef = driveSo.FindProperty("rightJoystickAction").objectReferenceValue;
        }
        else
        {
            leftActionRef = UrdfPostProcessor.LoadActionReference("LeftStick");
            rightActionRef = UrdfPostProcessor.LoadActionReference("RightStick");
        }

        Rigidbody oldBody = robot.GetComponent<Rigidbody>();
        if (drive != null) Undo.DestroyObjectImmediate(drive);
        if (oldBody != null) Undo.DestroyObjectImmediate(oldBody);

        // 2) Root ArticulationBody on the wrapper — the free-floating chassis base. Same
        //    solver-iteration bump the old rig used, for firm contacts against light pieces.
        ArticulationBody rootAb = Undo.AddComponent<ArticulationBody>(robot);
        rootAb.immovable = false;
        rootAb.useGravity = true;
        rootAb.mass = RootMass;
        rootAb.solverIterations = 16;
        rootAb.solverVelocityIterations = 8;
        rootAb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        rootAb.linearDamping = 0.05f;
        rootAb.angularDamping = 0.05f;

        // 3) One revolute-jointed link per wheel cluster.
        List<ArticulationBody> leftLinks = new List<ArticulationBody>();
        List<ArticulationBody> rightLinks = new List<ArticulationBody>();
        StringBuilder linkSummary = new StringBuilder();

        // The wrapper's origin sits well off the chassis center (the FBX pivot is ~1.2 units to
        // one side), so the sign of a cluster's local X says nothing about its side — every wheel
        // lands negative. Split against the MEAN X of all clusters instead: the two rails
        // straddle their own average.
        float meanClusterX = 0f;
        foreach (RobotPartClassifier.WheelCluster cluster in clusters)
            meanClusterX += robot.transform.InverseTransformPoint(cluster.Center).x;
        meanClusterX /= clusters.Count;

        foreach (RobotPartClassifier.WheelCluster cluster in clusters)
        {
            // Axle axis = the world axis of the cluster's smallest AABB extent (wheels are thin
            // along their axle). Pure sanity gate: the link is oriented to the wrapper below, so
            // a bad axis here means the classifier grabbed something that isn't wheel-shaped.
            Vector3 ext = cluster.worldBounds.extents;
            Vector3 axleWorld;
            if (ext.x <= ext.y && ext.x <= ext.z) axleWorld = Vector3.right;
            else if (ext.y <= ext.z) axleWorld = Vector3.up;
            else axleWorld = Vector3.forward;

            float angle = Vector3.Angle(axleWorld, wrapper.right);
            float foldedAngle = Mathf.Min(angle, 180f - angle); // axles have no preferred sign
            if (foldedAngle > AxleAngleToleranceDeg)
            {
                Debug.LogWarning($"{UndoName}: cluster '{cluster.topmost.name}' smallest-extent axis is " +
                                 $"{foldedAngle:F1}° off the wrapper's right axis; using wrapper.right as the axle.", cluster.topmost);
                axleWorld = wrapper.right;
            }
            Vector3 axleWrapperSpace = wrapper.InverseTransformDirection(axleWorld);

            // Side: geometric (wrapper-local X relative to the clusters' mean X; +X = robot
            // right), cross-checked against the FBX group names ("Drivetrain LS"/"Drivetrain RS").
            bool isLeft = wrapper.InverseTransformPoint(cluster.Center).x < meanClusterX;
            string nameSide = AncestorSideTag(cluster.topmost, wrapper);
            if (nameSide != null && (nameSide == "LS") != isLeft)
                Debug.LogWarning($"{UndoName}: cluster '{cluster.topmost.name}' sits on the " +
                                 $"{(isLeft ? "left" : "right")} geometrically but its ancestors say '{nameSide}'. " +
                                 "Trusting the geometry.", cluster.topmost);

            int sideIndex = isLeft ? leftLinks.Count : rightLinks.Count;
            string linkName = $"WheelLink_{(isLeft ? "LS" : "RS")}{sideIndex}";

            // Direct child of the wrapper, world rotation == wrapper rotation: the revolute
            // joint spins about the ANCHOR's X axis (Unity PhysicsModule: "Revolute joint
            // allows rotational movement around the X axis of the parent's anchor"), so with
            // this orientation link-local +X IS the axle and matchAnchors needs no help.
            GameObject link = new GameObject(linkName);
            Undo.RegisterCreatedObjectUndo(link, UndoName);
            link.transform.SetParent(wrapper, false);
            link.transform.SetPositionAndRotation(cluster.Center, wrapper.rotation);
            link.transform.localScale = Vector3.one;

            // Move the cluster's nodes (and their WS-B SphereColliders) under the link. Only
            // reparent nodes whose ancestors aren't also cluster members, so any internal
            // hierarchy inside the cluster survives intact.
            HashSet<Transform> members = new HashSet<Transform>(cluster.nodes);
            foreach (Transform node in cluster.nodes)
            {
                if (node == null || HasAncestorInSet(node, members, wrapper)) continue;
                Undo.SetTransformParent(node, link.transform, UndoName); // keeps world placement
            }

            ArticulationBody ab = Undo.AddComponent<ArticulationBody>(link);
            ab.jointType = ArticulationJointType.RevoluteJoint; // set BEFORE drive config (type changes reset drives)
            ab.matchAnchors = true;
            // Unity seeds a NEW ArticulationBody's anchor with a +90°-about-Z rotation (anchor X
            // → link Y), which makes the revolute twist axis VERTICAL — wheels spin like tops at
            // full speed while the robot goes nowhere. Force the anchor to identity so the twist
            // axis is the link's +X (the axle); matchAnchors recomputes the parent side.
            ab.anchorPosition = Vector3.zero;
            ab.anchorRotation = Quaternion.identity;
            ab.mass = WheelMass;
            ab.jointFriction = WheelRollingResistance;
            ab.angularDamping = WheelSpinDamping;
            ab.maxJointVelocity = WheelMaxJointVelocity;
            ab.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            // Velocity drive: stiffness 0 (no position spring), damping as the velocity gain,
            // forceLimit as the motor stall torque. RobotMotorController re-bakes these same
            // values in Awake at runtime; baking them here too lets edit-mode simulation
            // (PhysicsSmokeTest) drive the wheels without any MonoBehaviour running.
            ArticulationDrive d = ab.xDrive;
            d.driveType = ArticulationDriveType.Velocity;
            d.forceLimit = WheelStallTorque;
            d.damping = WheelDriveDamping;
            d.stiffness = 0f;
            ab.xDrive = d;

            (isLeft ? leftLinks : rightLinks).Add(ab);
            linkSummary.AppendLine($"  {linkName}: axle(wrapper-space) {axleWrapperSpace:F2}, nodes {cluster.nodes.Count}");
        }

        // 4) Tag the wrapper AND every link "Player" — the match loaders identify the robot by
        //    the tag on a collider's owning body, and wheel colliders now belong to the links.
        Undo.RecordObject(robot, UndoName);
        robot.tag = "Player";
        foreach (ArticulationBody ab in leftLinks) ab.gameObject.tag = "Player";
        foreach (ArticulationBody ab in rightLinks) ab.gameObject.tag = "Player";

        // 5) Derive mass properties from the actual colliders: the root's inertia from the
        //    WS-B chassis boxes, each wheel's from its sphere.
        foreach (ArticulationBody ab in robot.GetComponentsInChildren<ArticulationBody>())
        {
            ab.ResetCenterOfMass();
            ab.ResetInertiaTensor();
        }

        // 6) Motor controller, wired to the links and the old controller's input actions.
        RobotMotorController motor = Undo.AddComponent<RobotMotorController>(robot);
        motor.leftWheels = leftLinks.ToArray();
        motor.rightWheels = rightLinks.ToArray();
        SerializedObject motorSo = new SerializedObject(motor);
        motorSo.FindProperty("leftJoystickAction").objectReferenceValue = leftActionRef;
        motorSo.FindProperty("rightJoystickAction").objectReferenceValue = rightActionRef;
        motorSo.ApplyModifiedProperties();

        // 7) TGS solver — PhysX's recommendation for articulations + drives (PGS lets driven
        //    joints sag and jitter under load).
        string tgsState = EnableTgsSolver();

        Undo.CollapseUndoOperations(undoGroup);
        EditorUtility.SetDirty(robot);
        EditorSceneManager.MarkSceneDirty(robot.scene);

        Debug.Log($"{UndoName}: rigged '{robot.name}' — root ArticulationBody (mass {RootMass}) + " +
                  $"{leftLinks.Count} left / {rightLinks.Count} right wheel links, tagged Player, " +
                  $"motor controller wired ({(leftActionRef != null && rightActionRef != null ? "actions restored" : "ACTIONS MISSING")}), " +
                  $"solver: {tgsState}.\n{linkSummary}", robot);
    }

    // Wires already-present wheel parts into an ALREADY-rigged drivetrain: each part becomes a
    // revolute wheel link (same torque-limited motor model as Rig) assigned to the near rail, then
    // appended to the RobotMotorController arrays so it spins with the rest. This is the reliable
    // escape hatch when auto-detect missed wheels. Throws if the robot isn't rigged yet. Skips parts
    // already wired or with no renderers. Returns the number of wheels added.
    public static int AddWheelsToDrivetrain(GameObject robot, IEnumerable<GameObject> wheelParts)
    {
        if (robot == null) throw new System.ArgumentNullException(nameof(robot));
        RobotMotorController motor = robot.GetComponent<RobotMotorController>();
        ArticulationBody rootAb = robot.GetComponent<ArticulationBody>();
        if (motor == null || rootAb == null)
            throw new System.InvalidOperationException(
                $"'{robot.name}' isn't rigged yet (no RobotMotorController / root ArticulationBody). Run " +
                "Rig Drivetrain first, then add extra wheels.");

        Transform wrapper = robot.transform;
        List<ArticulationBody> left = new List<ArticulationBody>(motor.leftWheels ?? System.Array.Empty<ArticulationBody>());
        List<ArticulationBody> right = new List<ArticulationBody>(motor.rightWheels ?? System.Array.Empty<ArticulationBody>());
        HashSet<ArticulationBody> alreadyWired = new HashSet<ArticulationBody>(left);
        alreadyWired.UnionWith(right);

        // Existing wheels define the two rails; a new wheel joins whichever rail's mean X it's nearer.
        float leftMeanX = MeanWrapperLocalX(left, wrapper);
        float rightMeanX = MeanWrapperLocalX(right, wrapper);

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName(AddWheelsUndo);
        int undoGroup = Undo.GetCurrentGroup();

        int added = 0;
        foreach (GameObject part in wheelParts)
        {
            if (part == null || part.transform == wrapper) continue;
            // Must be under this robot, and not already a wired wheel link.
            if (part.GetComponentInParent<RobotMotorController>() != motor) continue;
            ArticulationBody existing = part.GetComponent<ArticulationBody>();
            if (existing != null && alreadyWired.Contains(existing)) continue;

            // World bounds of the part's meshes -> link placement + side.
            Renderer[] renderers = part.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) continue;
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            float localX = wrapper.InverseTransformPoint(bounds.center).x;
            bool isLeft = DecideSide(localX, leftMeanX, rightMeanX);
            int sideIndex = isLeft ? left.Count : right.Count;
            string linkName = $"WheelLink_{(isLeft ? "LS" : "RS")}{sideIndex}_added";

            // Direct child of the wrapper, world rotation == wrapper rotation, anchor forced to
            // identity so the revolute twist axis is the link's +X (the axle) — the same setup Rig
            // uses (see the axis note in this file's header).
            GameObject link = new GameObject(linkName);
            Undo.RegisterCreatedObjectUndo(link, AddWheelsUndo);
            link.transform.SetParent(wrapper, false);
            link.transform.SetPositionAndRotation(bounds.center, wrapper.rotation);
            link.transform.localScale = Vector3.one;
            Undo.SetTransformParent(part.transform, link.transform, AddWheelsUndo); // keeps world placement

            ArticulationBody ab = Undo.AddComponent<ArticulationBody>(link);
            ab.jointType = ArticulationJointType.RevoluteJoint; // BEFORE drive config (type change resets drives)
            ab.matchAnchors = true;
            ab.anchorPosition = Vector3.zero;
            ab.anchorRotation = Quaternion.identity;
            ab.mass = WheelMass;
            ab.jointFriction = WheelRollingResistance;
            ab.angularDamping = WheelSpinDamping;
            ab.maxJointVelocity = WheelMaxJointVelocity;
            ab.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            ab.ResetCenterOfMass();
            ab.ResetInertiaTensor();

            ArticulationDrive d = ab.xDrive;
            d.driveType = ArticulationDriveType.Velocity;
            d.forceLimit = WheelStallTorque;
            d.damping = WheelDriveDamping;
            d.stiffness = 0f;
            ab.xDrive = d;

            link.tag = "Player"; // match loaders identify the robot by the tag on a collider's body

            (isLeft ? left : right).Add(ab);
            // Fold the new wheel into the running mean so several adds distribute across sides sanely.
            if (isLeft) leftMeanX = float.IsNaN(leftMeanX) ? localX : (leftMeanX + localX) * 0.5f;
            else rightMeanX = float.IsNaN(rightMeanX) ? localX : (rightMeanX + localX) * 0.5f;
            added++;
        }

        if (added > 0)
        {
            Undo.RecordObject(motor, AddWheelsUndo);
            motor.leftWheels = left.ToArray();
            motor.rightWheels = right.ToArray();
            EditorUtility.SetDirty(motor);
            EditorSceneManager.MarkSceneDirty(robot.scene);
        }
        Undo.CollapseUndoOperations(undoGroup);
        return added;
    }

    // Mean wrapper-local X of a wheel rail, or NaN when the rail is empty.
    private static float MeanWrapperLocalX(List<ArticulationBody> wheels, Transform wrapper)
    {
        if (wheels == null || wheels.Count == 0) return float.NaN;
        float sum = 0f;
        int n = 0;
        foreach (ArticulationBody w in wheels)
        {
            if (w == null) continue;
            sum += wrapper.InverseTransformPoint(w.transform.position).x;
            n++;
        }
        return n == 0 ? float.NaN : sum / n;
    }

    // Assigns a wheel to the nearer rail. The left rail sits at the smaller X (see Rig's mean-X
    // split); when a rail is still empty, decide by the other rail, or by sign as a last resort.
    private static bool DecideSide(float localX, float leftMeanX, float rightMeanX)
    {
        bool haveLeft = !float.IsNaN(leftMeanX);
        bool haveRight = !float.IsNaN(rightMeanX);
        if (haveLeft && haveRight)
            return Mathf.Abs(localX - leftMeanX) <= Mathf.Abs(localX - rightMeanX);
        if (haveLeft) return localX <= leftMeanX;   // larger X than the left rail => right
        if (haveRight) return localX < rightMeanX;   // smaller X than the right rail => left
        return localX < 0f;                          // bare drivetrain fallback
    }

    // Flips ProjectSettings/DynamicsManager.asset m_SolverType to 1 (TGS). Idempotent.
    private static string EnableTgsSolver()
    {
        Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/DynamicsManager.asset");
        if (assets == null || assets.Length == 0)
            throw new System.InvalidOperationException($"{UndoName}: could not load ProjectSettings/DynamicsManager.asset.");

        SerializedObject so = new SerializedObject(assets[0]);
        SerializedProperty solverType = so.FindProperty("m_SolverType");
        if (solverType == null)
            throw new System.InvalidOperationException($"{UndoName}: DynamicsManager.asset has no m_SolverType property.");

        int old = solverType.intValue;
        if (old == 1)
        {
            Debug.Log($"{UndoName}: physics solver already TGS (m_SolverType 1); leaving it alone.");
            return "already TGS";
        }

        solverType.intValue = 1;
        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        Debug.Log($"{UndoName}: physics solver m_SolverType {old} (PGS) -> 1 (TGS).");
        return $"PGS({old}) -> TGS(1)";
    }

    // First "LS"/"RS" substring found walking up from node to the wrapper (inclusive), or null.
    private static string AncestorSideTag(Transform node, Transform stopAt)
    {
        for (Transform t = node; t != null; t = t.parent)
        {
            if (t.name.Contains("LS")) return "LS";
            if (t.name.Contains("RS")) return "RS";
            if (t == stopAt) break;
        }
        return null;
    }

    // True if any ancestor of node (up to, not including, the wrapper) is in the set.
    private static bool HasAncestorInSet(Transform node, HashSet<Transform> set, Transform stopAt)
    {
        for (Transform t = node.parent; t != null && t != stopAt; t = t.parent)
        {
            if (set.Contains(t)) return true;
        }
        return false;
    }
}
