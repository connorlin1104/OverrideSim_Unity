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
// Usage: select the Robot object in the Hierarchy, then Tools > VEX > Rig Drivetrain
// Articulation. Batch: -executeMethod RigDrivetrainArticulation.RunBatchOnRobot
// (opens SampleScene, rigs the Robot, saves).
public class RigDrivetrainArticulation
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string UndoName = "Rig Drivetrain Articulation";

    private const int ExpectedWheelClusters = 6;
    private const float RootMass = 24f;          // chassis; each of the 6 wheel links adds 1 => ~30 total, matching the old rig
    private const float WheelMass = 1f;
    private const float WheelStallTorque = 700f; // matches RobotMotorController.wheelStallTorque
    private const float WheelDriveDamping = 1000f;
    private const float WheelMaxJointVelocity = 44f; // rad/s — 360 RPM (37.7 rad/s) plus headroom
    private const float AxleAngleToleranceDeg = 10f;

    [MenuItem("Tools/VEX/Rig Drivetrain Articulation")]
    private static void RigSelected()
    {
        GameObject robot = Selection.activeGameObject;
        if (robot == null)
        {
            EditorUtility.DisplayDialog(UndoName,
                "Select your Robot GameObject in the Hierarchy first.", "OK");
            return;
        }

        try
        {
            Rig(robot);
        }
        catch (System.InvalidOperationException e)
        {
            EditorUtility.DisplayDialog(UndoName, e.Message, "OK");
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
    public static void Rig(GameObject robot)
    {
        if (robot == null) throw new System.ArgumentNullException(nameof(robot));
        Transform wrapper = robot.transform;

        // --- Preconditions (validate everything before touching the scene) ---------------

        // WS-B's per-part colliders (wheel SphereColliders in particular) must already exist:
        // the links inherit them by reparenting, and inertia tensors are derived from them.
        if (robot.GetComponentsInChildren<Collider>(true).Length == 0)
            throw new System.InvalidOperationException(
                $"No colliders found under '{robot.name}'. Run Tools > VEX > Generate Part Colliders first — " +
                "wheel links need their sphere colliders before rigging.");

        // Idempotency guard: never double-rig.
        if (robot.GetComponentInChildren<ArticulationBody>(true) != null)
            throw new System.InvalidOperationException(
                $"'{robot.name}' already contains an ArticulationBody — it appears to be rigged already.");

        RobotDriveController drive = robot.GetComponent<RobotDriveController>();
        if (drive == null)
            throw new System.InvalidOperationException(
                $"'{robot.name}' has no RobotDriveController. Select the drive robot wrapper.");

        List<RobotPartClassifier.WheelCluster> clusters = RobotPartClassifier.FindWheelClusters(robot);
        if (clusters == null || clusters.Count == 0)
            throw new System.InvalidOperationException(
                $"RobotPartClassifier found no wheel clusters under '{robot.name}'; cannot rig.");
        if (clusters.Count != ExpectedWheelClusters)
            Debug.LogWarning($"{UndoName}: expected {ExpectedWheelClusters} wheel clusters, found {clusters.Count}. " +
                             "Rigging anyway — check the drivetrain naming/geometry.", robot);

        // --- Mutations (one collapsed undo group) -----------------------------------------

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName(UndoName);
        int undoGroup = Undo.GetCurrentGroup();

        // 1) Capture the input action references off the old controller, then remove the old
        //    stack. The controller must go FIRST: its [RequireComponent(typeof(Rigidbody))]
        //    blocks destroying the Rigidbody while the controller still exists.
        SerializedObject driveSo = new SerializedObject(drive);
        Object leftActionRef = driveSo.FindProperty("leftJoystickAction").objectReferenceValue;
        Object rightActionRef = driveSo.FindProperty("rightJoystickAction").objectReferenceValue;

        Rigidbody oldBody = robot.GetComponent<Rigidbody>();
        Undo.DestroyObjectImmediate(drive);
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

            // Side: geometric (sign of the cluster center's wrapper-local X; +X = robot right),
            // cross-checked against the FBX group names ("Drivetrain LS"/"Drivetrain RS").
            bool isLeft = wrapper.InverseTransformPoint(cluster.Center).x < 0f;
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
            ab.mass = WheelMass;
            ab.jointFriction = 0.05f;
            ab.angularDamping = 0.05f;
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
