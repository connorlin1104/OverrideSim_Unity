using System;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using Unity.Robotics.UrdfImporter;

// The one tool to run on a newly imported robot. Everything else under Tools > RoboSim > Robot
// is a step this calls, exposed separately under Advanced/ for when a single stage needs redoing.
//
// It figures out which kind of robot was dropped in the scene and runs the matching pipeline:
//
//   URDF robot (has UrdfLink children, from the URDF importer)
//     -> Post-Process: bake the 10x world scale, rebuild inertia, swap the importer's convex
//        hulls for per-part boxes + wheel spheres, wire the motors, register in the catalog.
//
//   Mesh robot (a plain FBX drag-and-drop, like the 360 RPM Drivetrain)
//     -> Rebuild Part Colliders: tight box per structural part, sphere per wheel cluster.
//     -> Rig Motors and Wheel Joints: ArticulationBody root + one revolute wheel link per
//        wheel, driven by RobotMotorController.
//
// Both paths end with the robot drivable by the on-screen joysticks. Nothing here touches the
// old velocity-drive tools under Legacy/.
//
// Usage: drop the robot in the scene, select its root, then
// Tools > RoboSim > Robot > Set Up Imported Robot.
public class SetUpImportedRobot : EditorWindow
{
    private const string Title = "Set Up Imported Robot";
    private const float UrdfScaleFactor = 10f; // this project's world: 1 unit = 0.1 m

    [SerializeField] private GameObject robotRoot;
    [SerializeField] private string meshWheelNamePrefix = RobotPartClassifier.WheelNamePrefix;
    [SerializeField] private string urdfWheelNameSubstring = "wheel";
    [SerializeField] private bool validateAfterSetup = true;

    [MenuItem("Tools/RoboSim/Robot/Set Up Imported Robot", false, 1)]
    private static void ShowWindow()
    {
        SetUpImportedRobot window = GetWindow<SetUpImportedRobot>(Title);
        window.minSize = new Vector2(420f, 260f);
        window.Show();
    }

    private void OnEnable()
    {
        if (robotRoot == null) robotRoot = Selection.activeGameObject;
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Run this once on a newly imported robot. It detects whether the robot came from a " +
            "URDF export or a plain mesh/FBX import and does the whole setup: colliders, motors, " +
            "wheel joints, input wiring.", MessageType.Info);

        robotRoot = (GameObject)EditorGUILayout.ObjectField("Robot Root", robotRoot, typeof(GameObject), true);

        if (robotRoot == null)
        {
            EditorGUILayout.HelpBox("Select the robot's root GameObject in the Hierarchy.", MessageType.Warning);
            return;
        }

        RobotKind kind = Detect(robotRoot);
        switch (kind)
        {
            case RobotKind.Urdf:
                EditorGUILayout.HelpBox($"Detected: URDF robot. Will post-process at {UrdfScaleFactor}x scale.",
                    MessageType.None);
                urdfWheelNameSubstring = EditorGUILayout.TextField("Wheel Name Contains", urdfWheelNameSubstring);
                break;

            case RobotKind.Mesh:
                EditorGUILayout.HelpBox("Detected: mesh/FBX robot. Will build part colliders, then rig motors.",
                    MessageType.None);
                meshWheelNamePrefix = EditorGUILayout.TextField("Wheel Nodes Start With", meshWheelNamePrefix);
                break;

            case RobotKind.AlreadySetUp:
                EditorGUILayout.HelpBox(
                    $"'{robotRoot.name}' already has an ArticulationBody — it has been set up.\n\n" +
                    "Re-importing is the clean way to start over. To redo one stage, use " +
                    "Tools > RoboSim > Robot > Advanced.", MessageType.Warning);
                return;

            case RobotKind.Unknown:
                EditorGUILayout.HelpBox(
                    $"'{robotRoot.name}' has no meshes and no URDF links — this does not look like " +
                    "a robot. Select the root object of the imported model.", MessageType.Error);
                return;
        }

        validateAfterSetup = EditorGUILayout.Toggle("Validate Physics After", validateAfterSetup);

        EditorGUILayout.Space();
        if (!GUILayout.Button("Set Up Robot", GUILayout.Height(32))) return;

        try
        {
            string summary = Run(robotRoot, kind, meshWheelNamePrefix, urdfWheelNameSubstring);
            EditorUtility.DisplayDialog(Title, summary + Validate(robotRoot), "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog(Title, $"Setup failed:\n\n{e.Message}", "OK");
            Debug.LogException(e, robotRoot);
        }
    }

    // Simulates the freshly built robot. The smoke test reloads the scene from disk to throw away
    // the simulated poses, so the setup MUST be saved first or it would be reloaded away — and
    // afterwards every scene object we were holding (robotRoot) is a destroyed reference.
    private string Validate(GameObject root)
    {
        if (!validateAfterSetup) return "\n\nSave the scene, then run Robot > Validate Robot Physics to test it.";

        UnityEngine.SceneManagement.Scene scene = root.scene;
        if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
            return "\n\nSkipped physics validation: save the scene first, then run " +
                   "Robot > Validate Robot Physics.";

        if (!EditorSceneManager.SaveScene(scene))
            return "\n\nSkipped physics validation: the scene could not be saved.";

        try
        {
            PhysicsSmokeTest.ValidateRobot(root);
            return "\n\nPhysics validation: PASSED (settles, drives, turns).";
        }
        catch (Exception e)
        {
            return $"\n\nPhysics validation FAILED:\n{e.Message}\n\n" +
                   "The robot is set up but does not drive correctly yet — see the Console.";
        }
        finally
        {
            // The scene was reloaded from disk; our reference points at a destroyed object.
            robotRoot = null;
        }
    }

    // --- Detection ---------------------------------------------------------------------------

    public enum RobotKind { Urdf, Mesh, AlreadySetUp, Unknown }

    public static RobotKind Detect(GameObject root)
    {
        // Order matters: a post-processed URDF robot has BOTH UrdfLinks and ArticulationBodies,
        // and must read as already set up rather than as a fresh URDF import.
        if (root.GetComponentInChildren<ArticulationBody>(true) != null) return RobotKind.AlreadySetUp;
        if (root.GetComponentsInChildren<UrdfLink>(true).Length > 0) return RobotKind.Urdf;
        if (root.GetComponentInChildren<MeshFilter>(true) != null) return RobotKind.Mesh;
        return RobotKind.Unknown;
    }

    // --- Pipeline ----------------------------------------------------------------------------

    // Runs the full setup for the detected robot kind. Returns a human-readable summary.
    // Throws InvalidOperationException with a readable message on any precondition failure.
    public static string Run(GameObject root, RobotKind kind, string meshWheelPrefix, string urdfWheelSubstring)
    {
        switch (kind)
        {
            case RobotKind.Urdf:
                // Post-Process already does colliders + motors + catalog registration in one pass.
                UrdfPostProcessor.PostProcess(root, UrdfScaleFactor, true, urdfWheelSubstring);
                MarkDirty(root);
                return $"'{root.name}' (URDF) is set up:\n" +
                       $"  - baked to {UrdfScaleFactor}x world scale\n" +
                       "  - per-part box colliders + wheel spheres\n" +
                       "  - wheel motors wired to the joysticks\n" +
                       "  - added to the home-screen robot list";

            case RobotKind.Mesh:
                GeneratePartColliders.Report report = GeneratePartColliders.Generate(root, meshWheelPrefix);
                if (report.sphereCount == 0)
                    throw new InvalidOperationException(
                        $"No wheel nodes under '{root.name}' start with '{meshWheelPrefix}', so there is " +
                        "nothing to turn into motors. Set 'Wheel Nodes Start With' to match this robot's " +
                        "wheel objects in the Hierarchy, then run again.");

                RigDrivetrainArticulation.Rig(root, meshWheelPrefix);
                MarkDirty(root);
                return $"'{root.name}' (mesh) is set up:\n" +
                       $"  - {report.boxCount + report.obbChildCount} part box colliders " +
                       $"({report.skippedFasteners} fasteners skipped)\n" +
                       $"  - {report.sphereCount} wheel spheres, now {report.sphereCount} motor-driven wheel links\n" +
                       "  - motors wired to the joysticks";

            default:
                throw new InvalidOperationException(
                    $"'{root.name}' is {(kind == RobotKind.AlreadySetUp ? "already set up" : "not a recognizable robot")}.");
        }
    }

    private static void MarkDirty(GameObject root)
    {
        EditorUtility.SetDirty(root);
        if (root.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.scene);
    }
}
