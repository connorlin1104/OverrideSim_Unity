using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Robotics.UrdfImporter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;

// Post-processes a robot imported by the Unity URDF-Importer package so it fits this
// project's 10x-scale world (1 unit = 0.1 m, gravity -98.1) and drives with the same
// input wiring as the rest of the sim.
//
// Why bake the scale instead of scaling the imported root transform: ArticulationBody
// joint anchors live in link-local space and PhysX does not reliably compose a scaled
// ancestor transform into them — a scaled root stretches the meshes while the joints
// keep solving at 1x offsets and the rig tears itself apart on the first step. So we
// multiply the scale INTO the data instead: link origins (the baked joint offsets) x10,
// each link's Visuals/Collisions group x10, prismatic drive limits x10.
//
// It also rebuilds inertia for the new geometry, force-enables the root body, replaces
// the importer's legacy-input Controller with this project's RobotMotorController wired
// to the RobotControls input actions, tags everything "Player" for the match loaders,
// and registers the robot in the home-screen model catalog.
//
// Usage: import a URDF (GameObject > 3D Object > URDF Model (import)), select the
// imported root, then Tools > RoboSim > Robot > Advanced > Post-Process Imported URDF Robot and press Run.
// RunBatchValidateTestbot is the headless end-to-end check for -executeMethod runs.
public class UrdfPostProcessor : EditorWindow
{
    private const string UndoName = "Post-Process URDF Robot";
    private const string InputActionsPath = "Assets/RobotControls.inputactions";
    private const string CatalogPath = "Assets/Settings/RobotModelCatalog.asset";

    // Wheel velocity-drive tuning — matches what RobotMotorController.Awake() bakes at play
    // time, so edit-mode simulation (batch validation) behaves like play mode.
    private const float WheelDriveForceLimit = 700f;
    private const float WheelDriveDamping = 1000f;

    [SerializeField] private GameObject targetRoot;
    [SerializeField] private float scaleFactor = 10f;
    [SerializeField] private bool replaceCollidersWithPartBoxes = true;
    [SerializeField] private string wheelNameSubstring = "wheel";
    [SerializeField] private bool keepUrdfInertials;

    [MenuItem("Tools/RoboSim/Robot/Advanced/Post-Process Imported URDF Robot", false, 3)]
    private static void ShowWindow()
    {
        UrdfPostProcessor window = GetWindow<UrdfPostProcessor>("URDF Post-Process");
        window.minSize = new Vector2(380f, 190f);
        window.Show();
    }

    private void OnEnable()
    {
        if (targetRoot == null) targetRoot = Selection.activeGameObject;
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Bakes the 10x world scale into an imported URDF robot (link origins, visuals, " +
            "collisions, prismatic limits, anchors), rebuilds inertia, wires wheel joints to " +
            "a RobotMotorController, and tags the robot 'Player'.", MessageType.Info);

        targetRoot = (GameObject)EditorGUILayout.ObjectField("Imported Robot Root", targetRoot, typeof(GameObject), true);
        scaleFactor = EditorGUILayout.FloatField("Scale Factor", scaleFactor);
        replaceCollidersWithPartBoxes = EditorGUILayout.Toggle("Replace Colliders With Part Boxes", replaceCollidersWithPartBoxes);
        wheelNameSubstring = EditorGUILayout.TextField("Wheel Name Substring", wheelNameSubstring);
        keepUrdfInertials = EditorGUILayout.Toggle(new GUIContent("Keep URDF Inertials",
            "Keep the URDF-authored center of mass and inertia tensors (scaled to the 10x world) " +
            "instead of recomputing them from the colliders. Use when the CAD export carries " +
            "accurate mass properties — makes tipping behave like the real robot."),
            keepUrdfInertials);

        EditorGUILayout.Space();
        if (!GUILayout.Button("Run")) return;

        if (targetRoot == null)
        {
            EditorUtility.DisplayDialog("Post-Process Imported URDF Robot",
                "Assign (or select in the Hierarchy) the imported URDF robot root first.", "OK");
            return;
        }
        if (targetRoot.GetComponentsInChildren<UrdfLink>(true).Length == 0)
        {
            EditorUtility.DisplayDialog("Post-Process Imported URDF Robot",
                $"'{targetRoot.name}' has no UrdfLink children — select the root GameObject " +
                "created by the URDF importer.", "OK");
            return;
        }

        try
        {
            PostProcess(targetRoot, scaleFactor, replaceCollidersWithPartBoxes, wheelNameSubstring, keepUrdfInertials);
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Post-Process Imported URDF Robot", e.Message, "OK");
        }
    }

    // Core pipeline. Everything is collapsed into a single Undo group so one Ctrl+Z restores
    // the freshly imported robot.
    public static void PostProcess(GameObject root, float scaleFactor, bool replaceColliders, string wheelNameSubstring,
        bool keepUrdfInertials = false)
    {
        if (root == null) throw new ArgumentNullException(nameof(root));
        UrdfLink[] links = root.GetComponentsInChildren<UrdfLink>(true);
        if (links.Length == 0)
            throw new InvalidOperationException($"'{root.name}' has no UrdfLink children — not an imported URDF robot.");

        // Idempotency guard: the scale bake is destructive (a second run turns a 10x robot into
        // a 100x one). A processed robot always carries RobotMotorController; a scaled
        // Visuals/Collisions group is the fallback signal for robots with no matched wheels.
        if (root.GetComponent<RobotMotorController>() != null)
            throw new InvalidOperationException(
                $"'{root.name}' already has a RobotMotorController — it has been post-processed. " +
                "Re-import the URDF to start over; running the scale bake twice would double it.");
        foreach (UrdfLink link in links)
        {
            foreach (Transform child in link.transform)
            {
                bool isGroup = child.GetComponent<UrdfVisuals>() != null || child.GetComponent<UrdfCollisions>() != null;
                if (isGroup && child.localScale.x >= scaleFactor * 0.9f)
                    throw new InvalidOperationException(
                        $"'{root.name}' looks already scaled ('{link.name}/{child.name}' localScale " +
                        $"{child.localScale.x:F1}) — running the scale bake twice would compound it.");
            }
        }

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName(UndoName);
        int undoGroup = Undo.GetCurrentGroup();

        // -- 1) Scale bake. Never scale the articulation root transform; multiply the scale
        //       into link origins and the per-link Visuals/Collisions groups instead.
        int scaledGroups = 0;
        foreach (UrdfLink link in links)
        {
            Undo.RecordObject(link.transform, UndoName);
            link.transform.localPosition *= scaleFactor;

            foreach (Transform child in link.transform)
            {
                // The group's localScale carries both mesh size and the URDF origin offsets of
                // everything under it, so one multiply scales the whole part coherently. Child
                // links are siblings of these groups and are handled by the loop above.
                if (child.GetComponent<UrdfVisuals>() != null || child.GetComponent<UrdfCollisions>() != null)
                {
                    Undo.RecordObject(child, UndoName);
                    child.localScale *= scaleFactor;
                    scaledGroups++;
                }
            }
        }

        ArticulationBody[] bodies = root.GetComponentsInChildren<ArticulationBody>(true);
        foreach (ArticulationBody body in bodies)
        {
            Undo.RecordObject(body, UndoName);
            // Anchors are link-local offsets, so they must scale with the link origins or the
            // joints snap back when the origins spread 10x. The importer leaves anchorPosition
            // at (0,0,0) and only sets anchorRotation (UrdfJoint*.AdjustMovement), but scale it
            // anyway in case the robot was hand-tweaked after import.
            body.anchorPosition *= scaleFactor;

            // The parent-side anchor is what PhysX actually enforces when it builds the
            // articulation — matchAnchors does NOT recompute it from the transforms, and the
            // importer serializes it inconsistently (stale 1x link offsets on revolute/
            // continuous joints, plain (0,0,0) on prismatic ones). Trusting or merely scaling
            // it let the first Simulate() snap every link back to those poses: the whole robot
            // collapsed to 1x link spacing under 10x geometry, and pistons teleported into the
            // chassis (both caught by RunBatchValidateMechanisms). Re-derive it from the joint
            // frame's actual scaled pose instead, and pin matchAnchors off so nothing recomputes
            // it behind our back — the two sides are exactly consistent by construction.
            ArticulationBody parentBody = FindParentBody(body);
            if (parentBody != null)
            {
                Transform parentTransform = parentBody.transform;
                Transform childTransform = body.transform;
                body.matchAnchors = false;
                body.parentAnchorPosition =
                    parentTransform.InverseTransformPoint(childTransform.TransformPoint(body.anchorPosition));
                body.parentAnchorRotation =
                    Quaternion.Inverse(parentTransform.rotation) * (childTransform.rotation * body.anchorRotation);
            }
        }

        // -- 2) Prismatic joints: linear limits and max linear velocity are lengths -> scale.
        //       (Revolute limits are degrees and angular velocities are rad/s: scale-free.)
        int prismaticCount = 0;
        foreach (UrdfJointPrismatic prismatic in root.GetComponentsInChildren<UrdfJointPrismatic>(true))
        {
            ArticulationBody body = prismatic.GetComponent<ArticulationBody>();
            if (body == null) continue;
            ArticulationDrive drive = body.xDrive;
            drive.lowerLimit *= scaleFactor;
            drive.upperLimit *= scaleFactor;
            body.xDrive = drive;
            body.maxLinearVelocity *= scaleFactor;
            prismaticCount++;
        }

        // -- 3) Optionally replace the imported colliders (convex hulls / VHACD output) with
        //       the project's tight per-part boxes + wheel spheres.
        int removedColliders = 0;
        if (replaceColliders)
        {
            foreach (UrdfCollisions collisions in root.GetComponentsInChildren<UrdfCollisions>(true))
            {
                foreach (Collider collider in collisions.GetComponentsInChildren<Collider>(true))
                {
                    Undo.DestroyObjectImmediate(collider);
                    removedColliders++;
                }
            }
            GeneratePartColliders.Generate(root);
        }

        // -- 4) Inertia for the 10x geometry. URDF masses are kept in both modes; the
        //       tensors/centers must match the new size or the robot wobbles like 1x.
        if (keepUrdfInertials)
        {
            // Trust the URDF-authored mass properties (accurate CAD exports): scale the stored
            // values into the 10x world — COM is a length (x10), the tensor is mass·length²
            // (x100), rotations are scale-free — and push them onto the bodies now so edit-mode
            // simulation matches what UrdfInertial.Start() re-applies at play time.
            foreach (UrdfInertial inertial in root.GetComponentsInChildren<UrdfInertial>(true))
            {
                if (!inertial.useUrdfData) continue; // link had no <inertial> authored
                Undo.RecordObject(inertial, UndoName);
                inertial.centerOfMass *= scaleFactor;
                inertial.inertiaTensor *= scaleFactor * scaleFactor;
                inertial.UpdateLinkData();
            }
        }
        else
        {
            foreach (ArticulationBody body in bodies)
            {
                body.ResetCenterOfMass();
                body.ResetInertiaTensor();
            }
            foreach (UrdfInertial inertial in root.GetComponentsInChildren<UrdfInertial>(true))
            {
                // UrdfInertial.Start() re-applies the serialized 1x URDF tensors at play time while
                // useUrdfData is set, silently undoing the reset above — switch it to the
                // "recompute from colliders" mode instead.
                Undo.RecordObject(inertial, UndoName);
                inertial.useUrdfData = false;
            }
        }

        // -- 5) Root body must be movable (the importer/issue-#210 family of bugs can leave
        //       robots frozen), and everything gets the Player tag for the match loaders.
        ArticulationBody rootBody = FindArticulationRoot(root, bodies);
        if (rootBody != null) rootBody.immovable = false;
        else Debug.LogWarning("Post-Process URDF Robot: no ArticulationBody found under the root.", root);

        Undo.RecordObject(root, UndoName);
        root.tag = "Player";
        foreach (UrdfLink link in links)
        {
            Undo.RecordObject(link.gameObject, UndoName);
            link.gameObject.tag = "Player";
        }

        // The importer's own Controller drives joints from the legacy Input Manager (throws in
        // this New-Input-System-only project) and its Start() stomps every drive's forceLimit.
        // Remove it and its FKRobot helper; RobotMotorController takes over below.
        var importController = root.GetComponent<Unity.Robotics.UrdfImporter.Control.Controller>();
        if (importController != null) Undo.DestroyObjectImmediate(importController);
        var importFkRobot = root.GetComponent<Unity.Robotics.UrdfImporter.Control.FKRobot>();
        if (importFkRobot != null) Undo.DestroyObjectImmediate(importFkRobot);

        // -- 6) Wheel wiring: revolute/continuous links named like wheels become velocity-driven
        //       motors, split left/right by which side of the chassis they sit on.
        List<ArticulationBody> wheels = new List<ArticulationBody>();
        foreach (UrdfJoint joint in root.GetComponentsInChildren<UrdfJoint>(true))
        {
            if (!(joint is UrdfJointContinuous) && !(joint is UrdfJointRevolute)) continue;
            if (string.IsNullOrEmpty(wheelNameSubstring) ||
                joint.gameObject.name.IndexOf(wheelNameSubstring, StringComparison.OrdinalIgnoreCase) < 0) continue;
            ArticulationBody wheel = joint.GetComponent<ArticulationBody>();
            if (wheel == null) continue;

            ArticulationDrive drive = wheel.xDrive;
            drive.driveType = ArticulationDriveType.Velocity;
            drive.forceLimit = WheelDriveForceLimit;
            drive.damping = WheelDriveDamping;
            drive.stiffness = 0f; // pure velocity control — no position spring
            wheel.xDrive = drive;
            wheels.Add(wheel);
        }

        // If the part-box pass ran, it has just boxed the wheels too (its sphere path only
        // recognizes the FBX drivetrain's wheel names) — square low-friction wheels can't roll.
        // Give every wheel link a rolling SphereCollider sized from its renderers instead.
        if (replaceColliders)
        {
            PhysicsMaterial wheelMaterial = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>("Assets/WheelPhysics.physicMaterial");
            foreach (ArticulationBody wheel in wheels)
            {
                foreach (Collider stale in wheel.GetComponentsInChildren<Collider>(true))
                    Undo.DestroyObjectImmediate(stale);

                Renderer[] renderers = wheel.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0)
                {
                    Debug.LogWarning($"Post-Process URDF Robot: wheel '{wheel.name}' has no renderers to size a sphere from.", wheel);
                    continue;
                }
                Bounds worldBounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) worldBounds.Encapsulate(renderers[i].bounds);

                SphereCollider sphere = Undo.AddComponent<SphereCollider>(wheel.gameObject);
                sphere.center = wheel.transform.InverseTransformPoint(worldBounds.center);
                float maxLossy = Mathf.Max(Mathf.Abs(wheel.transform.lossyScale.x),
                                           Mathf.Abs(wheel.transform.lossyScale.y),
                                           Mathf.Abs(wheel.transform.lossyScale.z));
                sphere.radius = Mathf.Max(worldBounds.extents.x, worldBounds.extents.y, worldBounds.extents.z)
                                / Mathf.Max(maxLossy, 1e-6f);
                if (wheelMaterial != null) sphere.sharedMaterial = wheelMaterial;

                // Step 4 rebuilt inertia while this wheel still wore the part boxes. In
                // keep-inertials mode the URDF values are authoritative — don't overwrite them.
                if (!keepUrdfInertials)
                {
                    wheel.ResetCenterOfMass();
                    wheel.ResetInertiaTensor();
                }
            }
        }

        // Side split: relative to the MEAN wheel X, not the raw local-X sign — an off-center
        // root origin (the FBX drivetrain's pivot sits ~1.2 units to one side; URDF base_link
        // origins can do the same) puts every wheel on one side of x=0, but the two rails
        // always straddle their own average. (+X is the robot's right.)
        List<ArticulationBody> leftWheels = new List<ArticulationBody>();
        List<ArticulationBody> rightWheels = new List<ArticulationBody>();
        float meanWheelX = 0f;
        foreach (ArticulationBody wheel in wheels)
            meanWheelX += root.transform.InverseTransformPoint(wheel.transform.position).x;
        if (wheels.Count > 0) meanWheelX /= wheels.Count;
        foreach (ArticulationBody wheel in wheels)
        {
            float x = root.transform.InverseTransformPoint(wheel.transform.position).x;
            (x < meanWheelX ? leftWheels : rightWheels).Add(wheel);
        }

        RobotMotorController motor = root.GetComponent<RobotMotorController>();
        if (motor == null) motor = Undo.AddComponent<RobotMotorController>(root);
        Undo.RecordObject(motor, UndoName);
        motor.leftWheels = leftWheels.ToArray();
        motor.rightWheels = rightWheels.ToArray();
        motor.leftJoystickAction = LoadActionReference("LeftStick");
        motor.rightJoystickAction = LoadActionReference("RightStick");
        EditorUtility.SetDirty(motor);
        if (leftWheels.Count == 0 && rightWheels.Count == 0)
            Debug.LogWarning($"Post-Process URDF Robot: no revolute/continuous links matching " +
                             $"'{wheelNameSubstring}' found — RobotMotorController has no wheels.", root);

        // -- 6.5) Mechanisms: every remaining powered joint becomes a controllable mechanism —
        //         non-wheel revolute/continuous joints get a hold-to-run MotorActuator, prismatic
        //         joints get a binary PneumaticActuator (sliders are pneumatic pistons per the
        //         Fusion export conventions). Drives are baked HERE, at edit time, because batch
        //         validation steps physics without Awake ever running; the components re-bake the
        //         same values at play time. The registry + ButtonRouter make the mechanisms
        //         reachable from the player's saved button map, and the id list is mirrored onto
        //         the catalog entry so the home screen can offer them without loading the scene.
        string robotId = Slugify(root.name);
        var mechanismInfos = new List<RobotModelCatalog.MechanismInfo>();
        RobotMechanisms registry = root.GetComponent<RobotMechanisms>();
        if (registry == null) registry = Undo.AddComponent<RobotMechanisms>(root);
        Undo.RecordObject(registry, UndoName);
        registry.robotId = robotId;
        registry.mechanisms.Clear();

        foreach (UrdfJoint joint in root.GetComponentsInChildren<UrdfJoint>(true))
        {
            ArticulationBody body = joint.GetComponent<ArticulationBody>();
            if (body == null || wheels.Contains(body)) continue;

            RobotMechanisms.Mechanism mechanism = null;
            if (joint is UrdfJointContinuous || joint is UrdfJointRevolute)
            {
                MotorActuator motorActuator = Undo.AddComponent<MotorActuator>(joint.gameObject);
                motorActuator.body = body;

                // Same bake MotorActuator.Awake does at play time. Only the drive and the
                // velocity cap — never the anchors; the importer's anchorRotation IS the axis.
                ArticulationDrive mechDrive = body.xDrive;
                mechDrive.driveType = ArticulationDriveType.Velocity;
                mechDrive.stiffness = 0f;
                mechDrive.damping = motorActuator.velocityDriveDamping;
                mechDrive.forceLimit = motorActuator.stallTorque;
                body.xDrive = mechDrive;
                body.maxJointVelocity = motorActuator.maxRpm * Mathf.PI * 2f / 60f * 1.1f;

                mechanism = new RobotMechanisms.Mechanism
                {
                    id = Slugify(joint.gameObject.name),
                    displayName = PrettifyMechanismName(joint.gameObject.name),
                    type = RobotMechanisms.TypeMotor,
                    motor = motorActuator,
                };
            }
            else if (joint is UrdfJointPrismatic)
            {
                PneumaticActuator piston = Undo.AddComponent<PneumaticActuator>(joint.gameObject);
                piston.body = body;

                // Joint limits were scaled x10 in step 2; they ARE the piston's two endpoints.
                // Bake the Target drive the same way PneumaticActuator.Awake does at play time.
                // The legacy per-piston toggleAction stays null — ButtonRouter owns input now.
                ArticulationDrive mechDrive = body.xDrive;
                piston.retractedTarget = mechDrive.lowerLimit;
                piston.extendedTarget = mechDrive.upperLimit;
                mechDrive.driveType = ArticulationDriveType.Target;
                mechDrive.stiffness = piston.stiffness;
                mechDrive.damping = piston.damping;
                mechDrive.forceLimit = piston.cylinderForce;
                mechDrive.target = piston.retractedTarget;
                body.xDrive = mechDrive;

                mechanism = new RobotMechanisms.Mechanism
                {
                    id = Slugify(joint.gameObject.name),
                    displayName = PrettifyMechanismName(joint.gameObject.name),
                    type = RobotMechanisms.TypePneumatic,
                    pneumatic = piston,
                };
            }
            if (mechanism == null) continue;

            registry.mechanisms.Add(mechanism);
            mechanismInfos.Add(new RobotModelCatalog.MechanismInfo
            {
                id = mechanism.id,
                displayName = mechanism.displayName,
                type = mechanism.type,
            });
        }
        EditorUtility.SetDirty(registry);

        ButtonRouter router = root.GetComponent<ButtonRouter>();
        if (router == null) router = Undo.AddComponent<ButtonRouter>(root);
        Undo.RecordObject(router, UndoName);
        router.mechanisms = registry;
        string[] buttonNames = Enum.GetNames(typeof(ControllerButton));
        router.buttonActions = new InputActionReference[buttonNames.Length];
        for (int i = 0; i < buttonNames.Length; i++)
            router.buttonActions[i] = LoadActionReference(buttonNames[i]);
        EditorUtility.SetDirty(router);

        // -- 7) Register the robot in the home-screen catalog (if the catalog asset exists).
        bool catalogAdded = AddToCatalog(root, robotId, mechanismInfos);

        EditorUtility.SetDirty(root);
        if (root.scene.IsValid())
            EditorSceneManager.MarkSceneDirty(root.scene);

        Undo.CollapseUndoOperations(undoGroup);

        // -- 8) Summary.
        Debug.Log($"Post-Process URDF Robot: scaled {links.Length} link origin(s) and {scaledGroups} " +
                  $"visual/collision group(s) x{scaleFactor}, scaled {prismaticCount} prismatic joint(s), " +
                  (replaceColliders
                      ? $"replaced {removedColliders} imported collider(s) with part boxes, "
                      : "kept the imported colliders, ") +
                  (keepUrdfInertials
                      ? $"kept URDF inertials (scaled) on {bodies.Length} body(ies), "
                      : $"reset inertia on {bodies.Length} articulation body(ies), ") +
                  $"wired {leftWheels.Count} left / {rightWheels.Count} right wheel(s) and " +
                  $"{registry.mechanisms.Count} mechanism(s), tagged '{root.name}' as Player" +
                  (catalogAdded ? ", added a catalog entry." : ", refreshed the catalog entry."), root);
    }

    // Headless end-to-end validation for -executeMethod: imports Assets/TestRobots/testbot.urdf,
    // post-processes it in a scratch scene, and drives it with edit-mode physics. Throws (nonzero
    // exit) on failure; throws "EDITMODE_SIM_UNSUPPORTED ..." specifically when the articulation
    // never moves at all so the orchestrator can pivot to a play-mode test.
    public static void RunBatchValidateTestbot()
    {
        const string urdfAssetPath = "Assets/TestRobots/testbot.urdf";
        const string catalogEntryId = "testbot";

        // PostProcess upserts a catalog entry; remember whether it existed so cleanup can
        // remove only what this validation run added.
        bool hadCatalogEntry = HasCatalogEntry(catalogEntryId);
        SimulationMode previousSimulationMode = Physics.simulationMode;
        try
        {
            GameObject robot = ImportUrdfIntoScratchScene(urdfAssetPath);

            // Ground plane (top face at y = 0) and the standard post-process at 10x with the
            // DEFAULT collider replacement on — this is the path the docs tell users to run, so
            // it is the one worth validating (part boxes on the chassis, spheres on wheel links).
            CreateGroundPlane();

            PostProcess(robot, 10f, true, "wheel");
            robot.transform.position = new Vector3(0f, 1f, 0f); // ~1 unit above the ground

            RobotMotorController motor = robot.GetComponent<RobotMotorController>();
            if (motor == null || motor.leftWheels == null || motor.rightWheels == null ||
                motor.leftWheels.Length != 1 || motor.rightWheels.Length != 1)
                throw new InvalidOperationException(
                    "URDF batch validation FAILED: expected exactly 1 left + 1 right wheel wired on " +
                    $"RobotMotorController, got {motor?.leftWheels?.Length ?? 0} left / {motor?.rightWheels?.Length ?? 0} right.");

            ArticulationBody leftWheel = motor.leftWheels[0];
            ArticulationBody rightWheel = motor.rightWheels[0];
            ArticulationBody rootBody = FindArticulationRoot(robot, robot.GetComponentsInChildren<ArticulationBody>(true));
            if (rootBody == null)
                throw new InvalidOperationException("URDF batch validation FAILED: no root ArticulationBody found.");

            // 4) Edit-mode physics: manual stepping only works in Script simulation mode.
            Physics.simulationMode = SimulationMode.Script;

            for (int i = 0; i < 100; i++) Physics.Simulate(0.01f); // settle onto the ground

            if (leftWheel.dofCount < 1 || rightWheel.dofCount < 1)
                throw new InvalidOperationException(
                    "EDITMODE_SIM_UNSUPPORTED: wheel joints report zero DOFs after Physics.Simulate — " +
                    "the articulation was never built in edit mode.");

            Vector3 startPosition = rootBody.transform.position;
            float leftStartAngle = leftWheel.jointPosition[0];
            float rightStartAngle = rightWheel.jointPosition[0];

            // 2160 deg/s = 360 RPM, the sim's standard drivetrain speed. Same sign on both
            // sides drives the testbot straight (its wheel axes are authored for that).
            leftWheel.SetDriveTargetVelocity(ArticulationDriveAxis.X, 2160f);
            rightWheel.SetDriveTargetVelocity(ArticulationDriveAxis.X, 2160f);
            for (int i = 0; i < 300; i++) Physics.Simulate(0.01f);

            Vector3 endPosition = rootBody.transform.position;
            Vector3 planar = endPosition - startPosition;
            planar.y = 0f;
            float wheelTravel = Mathf.Abs(leftWheel.jointPosition[0] - leftStartAngle) +
                                Mathf.Abs(rightWheel.jointPosition[0] - rightStartAngle);
            bool hasNaN = float.IsNaN(endPosition.x) || float.IsNaN(endPosition.y) || float.IsNaN(endPosition.z);

            // NaN wheel readings fail the NaN check below, not this one (NaN comparisons are false).
            if (wheelTravel < 1e-4f)
                throw new InvalidOperationException(
                    "EDITMODE_SIM_UNSUPPORTED: wheel jointPositions did not change over 300 driven steps — " +
                    "edit-mode articulation simulation appears inert; validate in play mode instead.");
            if (hasNaN)
                throw new InvalidOperationException(
                    $"URDF batch validation FAILED: robot position became NaN ({endPosition}) — physics blew up.");
            if (planar.magnitude <= 1f)
                throw new InvalidOperationException(
                    $"URDF batch validation FAILED: planar displacement {planar.magnitude:F3} units <= 1 " +
                    $"(wheels turned {wheelTravel:F2} rad) — robot spun without driving.");

            Debug.Log($"URDF batch validation PASSED: testbot drove {planar.magnitude:F2} units in 3 s " +
                      $"(wheels turned {wheelTravel:F1} rad), no NaN.");
        }
        finally
        {
            CleanupBatchImport(previousSimulationMode, catalogEntryId, hadCatalogEntry);
        }
    }

    // Headless end-to-end validation of the MECHANISM pipeline for -executeMethod: imports
    // Assets/TestRobots/testbot_mech.urdf (testbot + a revolute arm + a prismatic piston),
    // post-processes it, and proves in edit-mode physics that the non-wheel joints were found,
    // wired, and actually move: the arm under its MotorActuator, the piston under its
    // PneumaticActuator toggle. Throws (nonzero exit) on failure.
    public static void RunBatchValidateMechanisms()
    {
        const string urdfAssetPath = "Assets/TestRobots/testbot_mech.urdf";
        const string catalogEntryId = "testbot-mech";

        bool hadCatalogEntry = HasCatalogEntry(catalogEntryId);
        SimulationMode previousSimulationMode = Physics.simulationMode;
        try
        {
            GameObject robot = ImportUrdfIntoScratchScene(urdfAssetPath);
            CreateGroundPlane();

            PostProcess(robot, 10f, true, "wheel");
            robot.transform.position = new Vector3(0f, 1f, 0f); // ~1 unit above the ground

            // 1) Structural asserts: registry, actuators, router, catalog metadata.
            RobotMechanisms registry = robot.GetComponent<RobotMechanisms>();
            if (registry == null)
                throw new InvalidOperationException("Mechanism validation FAILED: no RobotMechanisms on the root.");
            if (registry.robotId != catalogEntryId)
                throw new InvalidOperationException(
                    $"Mechanism validation FAILED: robotId '{registry.robotId}' != '{catalogEntryId}'.");
            if (registry.mechanisms.Count != 2)
                throw new InvalidOperationException(
                    $"Mechanism validation FAILED: expected 2 mechanisms (arm motor + piston), got {registry.mechanisms.Count}.");

            RobotMechanisms.Mechanism armMech = registry.Find("arm");
            RobotMechanisms.Mechanism pistonMech = registry.Find("piston");
            if (armMech == null || armMech.type != RobotMechanisms.TypeMotor || armMech.motor == null ||
                armMech.motor.body == null)
                throw new InvalidOperationException(
                    "Mechanism validation FAILED: 'arm' is not a wired motor mechanism.");
            if (pistonMech == null || pistonMech.type != RobotMechanisms.TypePneumatic ||
                pistonMech.pneumatic == null || pistonMech.pneumatic.body == null)
                throw new InvalidOperationException(
                    "Mechanism validation FAILED: 'piston' is not a wired pneumatic mechanism.");

            PneumaticActuator piston = pistonMech.pneumatic;
            float pistonRange = piston.extendedTarget - piston.retractedTarget;
            // URDF limits 0..0.05 m must arrive x10: retracted 0, extended 0.5 world units.
            if (Mathf.Abs(piston.retractedTarget) > 0.01f || Mathf.Abs(piston.extendedTarget - 0.5f) > 0.05f)
                throw new InvalidOperationException(
                    $"Mechanism validation FAILED: piston targets [{piston.retractedTarget:F3}, " +
                    $"{piston.extendedTarget:F3}] != expected [0, 0.5] — prismatic limits not scaled.");

            ButtonRouter router = robot.GetComponent<ButtonRouter>();
            if (router == null || router.buttonActions == null ||
                router.buttonActions.Length != ControllerMapSettings.ButtonCount)
                throw new InvalidOperationException(
                    "Mechanism validation FAILED: ButtonRouter missing or button action array is the wrong size.");
            for (int i = 0; i < router.buttonActions.Length; i++)
            {
                if (router.buttonActions[i] == null)
                    throw new InvalidOperationException(
                        $"Mechanism validation FAILED: button action {(ControllerButton)i} not wired — " +
                        "is Assets/RobotControls.inputactions missing the button actions?");
            }

            RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
            RobotModelCatalog.Entry entry = catalog != null
                ? catalog.models.Find(e => e != null && e.id == catalogEntryId) : null;
            if (catalog != null && (entry == null || entry.mechanisms == null || entry.mechanisms.Count != 2))
                throw new InvalidOperationException(
                    "Mechanism validation FAILED: catalog entry is missing the 2 mechanism metadata records.");

            // 2) Edit-mode physics: the arm must sweep under its motor, the piston must toggle
            // between its endpoints — all WITHOUT Awake/Update ever running (drives are baked
            // at edit time by the post-processor; SetInput/Toggle work from serialized state).
            Physics.simulationMode = SimulationMode.Script;

            for (int i = 0; i < 100; i++) Physics.Simulate(0.01f); // settle onto the ground

            ArticulationBody armBody = armMech.motor.body;
            if (armBody.dofCount < 1)
                throw new InvalidOperationException(
                    "EDITMODE_SIM_UNSUPPORTED: arm joint reports zero DOFs after Physics.Simulate — " +
                    "the articulation was never built in edit mode.");

            float armStart = armBody.jointPosition[0];
            armMech.motor.SetInput(1f);
            for (int i = 0; i < 200; i++) Physics.Simulate(0.01f);
            float armDelta = armBody.jointPosition[0] - armStart;
            armMech.motor.SetInput(0f);
            if (float.IsNaN(armDelta))
                throw new InvalidOperationException("Mechanism validation FAILED: arm joint position became NaN.");
            if (Mathf.Abs(armDelta) < 0.3f)
                throw new InvalidOperationException(
                    $"Mechanism validation FAILED: arm swept only {armDelta:F3} rad over 2 s of full " +
                    "input — the MotorActuator drive is not moving the joint.");

            ArticulationBody pistonBody = piston.body;
            float pistonStart = pistonBody.jointPosition[0];
            piston.Toggle(); // retracted (baked start) -> extend
            for (int i = 0; i < 200; i++) Physics.Simulate(0.01f);
            float pistonExtended = pistonBody.jointPosition[0];
            if (Mathf.Abs(pistonExtended - piston.extendedTarget) > 0.2f * Mathf.Abs(pistonRange))
                throw new InvalidOperationException(
                    $"Mechanism validation FAILED: piston reached {pistonExtended:F3} after Toggle(), " +
                    $"expected ~{piston.extendedTarget:F3} — the pneumatic drive is not extending.");
            piston.Toggle(); // -> retract
            for (int i = 0; i < 200; i++) Physics.Simulate(0.01f);
            float pistonRetracted = pistonBody.jointPosition[0];
            if (Mathf.Abs(pistonRetracted - piston.retractedTarget) > 0.2f * Mathf.Abs(pistonRange))
                throw new InvalidOperationException(
                    $"Mechanism validation FAILED: piston sat at {pistonRetracted:F3} after the second " +
                    $"Toggle(), expected ~{piston.retractedTarget:F3} — the pneumatic drive is not retracting.");

            // 3) Keep-URDF-inertials mode spot check on a fresh import: the authored COM
            // (base_link <inertial> origin z=0.03 m, Unity y after frame conversion) must land
            // x10, the tensor x100, and useUrdfData must survive so play mode re-applies it.
            Physics.simulationMode = previousSimulationMode;
            GameObject robot2 = ImportUrdfIntoScratchScene(urdfAssetPath);
            PostProcess(robot2, 10f, true, "wheel", keepUrdfInertials: true);
            UrdfInertial baseInertial = null;
            foreach (UrdfInertial inertial in robot2.GetComponentsInChildren<UrdfInertial>(true))
            {
                if (inertial.gameObject.name == "base_link") { baseInertial = inertial; break; }
            }
            if (baseInertial == null || !baseInertial.useUrdfData)
                throw new InvalidOperationException(
                    "Mechanism validation FAILED: keep-inertials mode lost useUrdfData on base_link.");
            if (Mathf.Abs(baseInertial.centerOfMass.magnitude - 0.3f) > 0.02f)
                throw new InvalidOperationException(
                    $"Mechanism validation FAILED: keep-inertials COM magnitude {baseInertial.centerOfMass.magnitude:F3} " +
                    "!= expected 0.3 (0.03 m x10).");
            if (baseInertial.inertiaTensor.x < 1f || baseInertial.inertiaTensor.y < 1f || baseInertial.inertiaTensor.z < 1f)
                throw new InvalidOperationException(
                    $"Mechanism validation FAILED: keep-inertials tensor {baseInertial.inertiaTensor} looks " +
                    "unscaled (authored 1x values are all < 0.07; x100 they must all exceed 1).");

            Debug.Log($"Mechanism batch validation PASSED: arm swept {armDelta:F2} rad, piston toggled " +
                      $"{pistonStart:F2} -> {pistonExtended:F2} -> {pistonRetracted:F2} " +
                      "(targets 0 / 0.5 / 0), 12 button actions wired, catalog metadata present, " +
                      "keep-inertials mode scales COM/tensor correctly.");
        }
        finally
        {
            PlayerPrefs.DeleteKey(ControllerMapSettings.PrefKey(catalogEntryId));
            CleanupBatchImport(previousSimulationMode, catalogEntryId, hadCatalogEntry);
        }
    }

    // Shared batch-import boilerplate: scratch scene + synchronous URDF import. The scratch
    // scene must exist FIRST so the importer instantiates the robot into it; the selection is
    // cleared because ImportPipelinePostCreate parents the new robot under Selection.activeObject.
    private static GameObject ImportUrdfIntoScratchScene(string urdfAssetPath)
    {
        AssetDatabase.Refresh(); // a freshly copied .urdf must be known to the asset database
        string urdfFullPath = Path.GetFullPath(urdfAssetPath);
        if (!File.Exists(urdfFullPath))
            throw new FileNotFoundException($"URDF batch validation: '{urdfAssetPath}' not found.");

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        Selection.activeObject = null;

        // 'unity' convex method: primitive geometry keeps its primitive colliders and no
        // VHACD decomposition runs — fully headless-safe and writes no per-hull mesh assets.
        ImportSettings settings = new ImportSettings
        {
            chosenAxis = ImportSettings.axisType.yAxis,
            convexMethod = ImportSettings.convexDecomposer.unity,
        };

        // With loadStatus=false the import coroutine never yields mid-pipeline: the first
        // MoveNext() runs init -> hierarchy build -> post-create and then yields the finished
        // robot, so this loop drives it synchronously. The null-refresh branch only matters
        // if a future package version yields while waiting on assets.
        IEnumerator<GameObject> import = UrdfRobotExtensions.Create(urdfFullPath, settings);
        GameObject robot = null;
        while (import.MoveNext())
        {
            if (import.Current != null) robot = import.Current;
            else AssetDatabase.Refresh();
        }
        if (robot == null)
            throw new InvalidOperationException("URDF batch validation FAILED: import produced no robot GameObject.");
        return robot;
    }

    // Ground plane with its top face at y = 0.
    private static void CreateGroundPlane()
    {
        GameObject ground = new GameObject("Ground");
        BoxCollider groundBox = ground.AddComponent<BoxCollider>();
        groundBox.size = new Vector3(200f, 1f, 200f);
        ground.transform.position = new Vector3(0f, -0.5f, 0f);
    }

    private static bool HasCatalogEntry(string id)
    {
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
        return catalog != null && catalog.models != null &&
               catalog.models.Exists(e => e != null && e.id == id);
    }

    // Restores sim mode, discards the scratch scene (and the robot in it) without saving,
    // deletes the assets the importer wrote next to the URDF (Materials/, meshes/ — the .urdf
    // files themselves are kept), and removes the catalog entry if this run created it.
    private static void CleanupBatchImport(SimulationMode previousSimulationMode, string catalogEntryId,
        bool hadCatalogEntry)
    {
        Physics.simulationMode = previousSimulationMode;

        const string samplePath = "Assets/Scenes/SampleScene.unity";
        if (File.Exists(samplePath)) EditorSceneManager.OpenScene(samplePath, OpenSceneMode.Single);
        else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        AssetDatabase.DeleteAsset("Assets/TestRobots/Materials");
        AssetDatabase.DeleteAsset("Assets/TestRobots/meshes");

        if (!hadCatalogEntry)
        {
            RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
            if (catalog != null && catalog.models != null &&
                catalog.models.RemoveAll(e => e != null && e.id == catalogEntryId) > 0)
            {
                EditorUtility.SetDirty(catalog);
            }
        }
        AssetDatabase.SaveAssets();
    }

    // Nearest ancestor ArticulationBody — the parent link this body's joint connects to.
    private static ArticulationBody FindParentBody(ArticulationBody body)
    {
        for (Transform t = body.transform.parent; t != null; t = t.parent)
        {
            ArticulationBody ancestor = t.GetComponent<ArticulationBody>();
            if (ancestor != null) return ancestor;
        }
        return null;
    }

    // The root body is the one with no ArticulationBody anywhere above it. Don't trust
    // ArticulationBody.isRoot in edit mode — it reflects native state that may not exist
    // before the articulation has ever been built.
    private static ArticulationBody FindArticulationRoot(GameObject root, ArticulationBody[] bodies)
    {
        foreach (ArticulationBody body in bodies)
        {
            bool hasBodyAbove = false;
            for (Transform parent = body.transform.parent; parent != null; parent = parent.parent)
            {
                if (parent.GetComponent<ArticulationBody>() != null) { hasBodyAbove = true; break; }
                if (parent == root.transform) break;
            }
            if (!hasBodyAbove) return body;
        }
        return null;
    }

    // Returns the persistent InputActionReference sub-asset for an action in RobotControls.
    // The InputActionImporter creates one visible InputActionReference sub-asset per action
    // (plus a hidden legacy duplicate that LoadAllAssetRepresentationsAtPath skips), and
    // referencing that existing sub-asset serializes as a stable {fileID, guid} pair — the
    // same wiring the scene's RobotDriveController uses. InputActionReference.Create would
    // instead make a NEW in-memory ScriptableObject that persists nowhere and goes null on
    // the next domain reload.
    // Public because the drivetrain rig tool needs the same wiring when it rigs a freshly
    // imported robot that has no old controller to lift the references from.
    public static InputActionReference LoadActionReference(string actionName)
    {
        foreach (UnityEngine.Object obj in AssetDatabase.LoadAllAssetRepresentationsAtPath(InputActionsPath))
        {
            InputActionReference reference = obj as InputActionReference;
            if (reference != null && reference.action != null && reference.action.name == actionName)
                return reference;
        }
        Debug.LogWarning($"Action '{actionName}' not found in {InputActionsPath}; " +
                         "assign it on the RobotMotorController manually.");
        return null;
    }

    // Upserts {id, displayName, mechanisms} into the model catalog. Returns true when a new
    // entry was added; an existing entry gets its mechanism metadata refreshed either way, so
    // re-importing a robot with new mechanisms updates the home screen's config UI. Quietly
    // does nothing when the catalog asset doesn't exist yet (it's created by the Build Home
    // Scene tool).
    private static bool AddToCatalog(GameObject root, string id, List<RobotModelCatalog.MechanismInfo> mechanisms)
    {
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
        if (catalog == null) return false;

        if (catalog.models == null) catalog.models = new List<RobotModelCatalog.Entry>();
        RobotModelCatalog.Entry existing = null;
        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry != null && entry.id == id) { existing = entry; break; }
        }

        Undo.RecordObject(catalog, UndoName);
        bool added = existing == null;
        if (added)
        {
            existing = new RobotModelCatalog.Entry { id = id, displayName = root.name };
            catalog.models.Add(existing);
        }
        existing.mechanisms = mechanisms != null
            ? new List<RobotModelCatalog.MechanismInfo>(mechanisms)
            : new List<RobotModelCatalog.MechanismInfo>();
        EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        return added;
    }

    // "lift_arm" -> "Lift Arm"; a trailing "joint"/"link" token is dropped ("arm_joint" -> "Arm").
    // Used for the mechanism names the config UI shows, sourced from URDF link names.
    private static string PrettifyMechanismName(string name)
    {
        string[] words = name.Replace('_', ' ').Replace('-', ' ')
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(words.Length);
        foreach (string word in words)
            result.Add(char.ToUpperInvariant(word[0]) + word.Substring(1));
        if (result.Count > 1)
        {
            string last = result[result.Count - 1].ToLowerInvariant();
            if (last == "joint" || last == "link") result.RemoveAt(result.Count - 1);
        }
        return result.Count > 0 ? string.Join(" ", result) : name;
    }

    // Stable id from a display name: lowercase, runs of non-alphanumerics collapse to single
    // dashes ("My Robot v2!" -> "my-robot-v2"), so renaming for display never orphans saves.
    private static string Slugify(string name)
    {
        StringBuilder slug = new StringBuilder(name.Length);
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) slug.Append(c);
            else if (slug.Length > 0 && slug[slug.Length - 1] != '-') slug.Append('-');
        }
        return slug.ToString().TrimEnd('-');
    }
}
