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
// imported root, then Tools > VEX > Post-Process Imported URDF Robot and press Run.
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

    [MenuItem("Tools/VEX/Post-Process Imported URDF Robot")]
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
            PostProcess(targetRoot, scaleFactor, replaceCollidersWithPartBoxes, wheelNameSubstring);
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Post-Process Imported URDF Robot", e.Message, "OK");
        }
    }

    // Core pipeline. Everything is collapsed into a single Undo group so one Ctrl+Z restores
    // the freshly imported robot.
    public static void PostProcess(GameObject root, float scaleFactor, bool replaceColliders, string wheelNameSubstring)
    {
        if (root == null) throw new ArgumentNullException(nameof(root));
        UrdfLink[] links = root.GetComponentsInChildren<UrdfLink>(true);
        if (links.Length == 0)
            throw new InvalidOperationException($"'{root.name}' has no UrdfLink children — not an imported URDF robot.");

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
            // joints tear apart when the origins spread 10x. The importer actually leaves
            // anchorPosition at (0,0,0) and only sets anchorRotation (UrdfJoint*.AdjustMovement),
            // but scale it anyway in case the robot was hand-tweaked after import. matchAnchors
            // stays at Unity's default (true), so the parent-side anchor is recomputed from this
            // one automatically; only when it was turned off is parentAnchorPosition authored
            // data (in the PARENT link's local frame) that needs the same scaling.
            body.anchorPosition *= scaleFactor;
            if (!body.matchAnchors)
                body.parentAnchorPosition *= scaleFactor;
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

        // -- 4) Rebuild inertia from the 10x geometry. URDF masses are kept (mass untouched);
        //       only the tensors/centers must match the new size or the robot wobbles like 1x.
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
        List<ArticulationBody> leftWheels = new List<ArticulationBody>();
        List<ArticulationBody> rightWheels = new List<ArticulationBody>();
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

            // Root-local X sign splits the sides (URDF x-forward imports facing Unity +Z, so
            // +X is the robot's right). A dead-center wheel counts as right.
            float x = root.transform.InverseTransformPoint(wheel.transform.position).x;
            (x < 0f ? leftWheels : rightWheels).Add(wheel);
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

        // -- 7) Register the robot in the home-screen catalog (if the catalog asset exists).
        bool catalogAdded = AddToCatalog(root);

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
                  $"reset inertia on {bodies.Length} articulation body(ies), wired {leftWheels.Count} left / " +
                  $"{rightWheels.Count} right wheel(s), tagged '{root.name}' as Player" +
                  (catalogAdded ? ", added a catalog entry." : "."), root);
    }

    // Headless end-to-end validation for -executeMethod: imports Assets/TestRobots/testbot.urdf,
    // post-processes it in a scratch scene, and drives it with edit-mode physics. Throws (nonzero
    // exit) on failure; throws "EDITMODE_SIM_UNSUPPORTED ..." specifically when the articulation
    // never moves at all so the orchestrator can pivot to a play-mode test.
    public static void RunBatchValidateTestbot()
    {
        const string urdfAssetPath = "Assets/TestRobots/testbot.urdf";

        // 1) Make sure a freshly copied testbot.urdf is known to the asset database.
        AssetDatabase.Refresh();
        string urdfFullPath = Path.GetFullPath(urdfAssetPath);
        if (!File.Exists(urdfFullPath))
            throw new FileNotFoundException($"URDF batch validation: '{urdfAssetPath}' not found.");

        // PostProcess appends a catalog entry; remember whether it existed so cleanup can
        // remove only what this validation run added.
        RobotModelCatalog catalogBefore = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
        bool hadCatalogEntry = catalogBefore != null && catalogBefore.models != null &&
                               catalogBefore.models.Exists(e => e != null && e.id == "testbot");

        SimulationMode previousSimulationMode = Physics.simulationMode;
        try
        {
            // 2) Scratch scene FIRST so the importer instantiates the robot into it. Clear the
            // selection: ImportPipelinePostCreate parents the new robot under Selection.activeObject.
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

            // 3) Ground plane (top face at y = 0) and the standard post-process at 10x, keeping
            // the imported primitive colliders (GeneratePartColliders targets mesh-based robots).
            GameObject ground = new GameObject("Ground");
            BoxCollider groundBox = ground.AddComponent<BoxCollider>();
            groundBox.size = new Vector3(200f, 1f, 200f);
            ground.transform.position = new Vector3(0f, -0.5f, 0f);

            PostProcess(robot, 10f, false, "wheel");
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
            Physics.simulationMode = previousSimulationMode;

            // 5) Discard the scratch scene — and the robot instance in it — without saving.
            const string samplePath = "Assets/Scenes/SampleScene.unity";
            if (File.Exists(samplePath)) EditorSceneManager.OpenScene(samplePath, OpenSceneMode.Single);
            else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // The importer writes assets next to the URDF: Materials/Default.mat (always) and
            // meshes/Cylinder.asset (wheel collision cylinders). Delete both so repeat runs and
            // version control stay clean; testbot.urdf itself is kept.
            AssetDatabase.DeleteAsset("Assets/TestRobots/Materials");
            AssetDatabase.DeleteAsset("Assets/TestRobots/meshes");

            if (!hadCatalogEntry)
            {
                RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
                if (catalog != null && catalog.models != null &&
                    catalog.models.RemoveAll(e => e != null && e.id == "testbot") > 0)
                {
                    EditorUtility.SetDirty(catalog);
                }
            }
            AssetDatabase.SaveAssets();
        }
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
    private static InputActionReference LoadActionReference(string actionName)
    {
        foreach (UnityEngine.Object obj in AssetDatabase.LoadAllAssetRepresentationsAtPath(InputActionsPath))
        {
            InputActionReference reference = obj as InputActionReference;
            if (reference != null && reference.action != null && reference.action.name == actionName)
                return reference;
        }
        Debug.LogWarning($"Post-Process URDF Robot: action '{actionName}' not found in {InputActionsPath}; " +
                         "assign it on the RobotMotorController manually.");
        return null;
    }

    // Appends {id: slug(root.name), displayName: root.name} to the model catalog if missing.
    // Returns true when an entry was added. Quietly does nothing when the catalog asset
    // doesn't exist yet (it's created by the Build Home Scene tool).
    private static bool AddToCatalog(GameObject root)
    {
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
        if (catalog == null) return false;

        string id = Slugify(root.name);
        if (catalog.models == null) catalog.models = new List<RobotModelCatalog.Entry>();
        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry != null && entry.id == id) return false;
        }

        Undo.RecordObject(catalog, UndoName);
        catalog.models.Add(new RobotModelCatalog.Entry { id = id, displayName = root.name });
        EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        return true;
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
