using System;
using System.Text;
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
//     -> Rig Drivetrain: ArticulationBody root + one revolute wheel link per
//        wheel, driven by RobotMotorController.
//
// Both paths end with the robot drivable by the on-screen joysticks. It then validates the physics and
// saves the robot as a prefab linked to the home-screen model picker, so one press yields a ready,
// spawnable robot; add arms/lifts/pistons afterward and re-run Save As Robot Prefab.
//
// Usage: drop the robot in the scene, select its root, then
// Tools > RoboSim > Robot > Set Up Imported Robot.
public class SetUpImportedRobot : EditorWindow
{
    private const string Title = "Set Up Imported Robot";
    private const float UrdfScaleFactor = 10f; // this project's world: 1 unit = 0.1 m

    [SerializeField] private GameObject robotRoot;
    [SerializeField] private string meshWheelNamePrefix = RobotPartClassifier.DefaultWheelTokens;
    [SerializeField] private string urdfWheelNameSubstring = "wheel";
    [SerializeField] private bool keepUrdfInertials;
    [SerializeField] private bool computeMassFromGeometry = true;
    [SerializeField] private bool accurateColliders = true;
    [SerializeField] private bool validateAfterSetup = true;
    [SerializeField] private bool saveAsPrefabAfterSetup = true;

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
            "URDF export or a plain mesh/FBX import and does the whole setup in one press: simplified " +
            "box/sphere colliders (its own mesh colliders are replaced), motors, wheel joints, input " +
            "wiring — then validates the physics and saves it as a prefab in the model picker. Add " +
            "arms/lifts/pistons afterward, then re-run Save As Robot Prefab.", MessageType.Info);

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
                keepUrdfInertials = EditorGUILayout.Toggle(new GUIContent("Keep URDF Inertials",
                    "Keep the URDF-authored masses/COM/inertia (scaled to the 10x world) instead of " +
                    "recomputing from colliders — use when the CAD export carries accurate mass " +
                    "properties, for realistic tipping."), keepUrdfInertials);
                using (new EditorGUI.DisabledScope(keepUrdfInertials))
                    computeMassFromGeometry = EditorGUILayout.Toggle(new GUIContent("Compute Mass From Geometry",
                        "Compute each link's mass from its mesh volume x a density looked up from the " +
                        "part name, so parts exported without a Fusion physical material aren't stuck at " +
                        "the importer's 0.1 kg clamp. Lets you skip assigning materials in Fusion. " +
                        "Disabled when Keep URDF Inertials is on."), computeMassFromGeometry);
                break;

            case RobotKind.Mesh:
                EditorGUILayout.HelpBox("Detected: mesh/FBX robot. Will build part colliders, rig the drive " +
                    "wheels, and register it as a robot. Add arms/pistons afterward with " +
                    "Advanced ▸ Add or Fix Mechanism Joint.",
                    MessageType.None);
                meshWheelNamePrefix = EditorGUILayout.TextField(new GUIContent("Wheel Name Contains",
                    "Comma-separated name tokens that identify the drive wheels (matched anywhere in the " +
                    "node name), e.g. \"3.25 AS Omni\" or \"Omni, Traction\". These nodes get rolling sphere " +
                    "colliders and become motor-driven wheel links."), meshWheelNamePrefix);
                accurateColliders = EditorGUILayout.Toggle(new GUIContent("Accurate Colliders",
                    "Give concave PLASTIC (funnels / web panels a game piece rests in or deflects off) " +
                    "convex-hull mesh colliders that follow their shape. Wheels get spheres; every other " +
                    "component (metal, standoffs, sensors, motors, misc) gets one box sized/angled to the " +
                    "part; screws/spacers are skipped. Off = boxes only, no hulls."),
                    accurateColliders);
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
        saveAsPrefabAfterSetup = EditorGUILayout.Toggle(new GUIContent("Save As Prefab After",
            "Save the finished robot as a prefab under Assets/Robots, link it to the home-screen model " +
            "picker, and remove the inline scene copy (so it doesn't double-spawn). To add mechanisms, " +
            "drag the saved prefab back in, add them, then re-run Save As Robot Prefab."), saveAsPrefabAfterSetup);

        EditorGUILayout.Space();
        if (!GUILayout.Button("Set Up Robot", GUILayout.Height(32))) return;

        try
        {
            string robotName = robotRoot.name;
            string summary = Run(robotRoot, kind, meshWheelNamePrefix, urdfWheelNameSubstring, keepUrdfInertials,
                computeMassFromGeometry, accurateColliders);
            EditorUtility.DisplayDialog(Title, summary + Finish(robotName), "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog(Title, $"Setup failed:\n\n{e.Message}", "OK");
            Debug.LogException(e, robotRoot);
        }
    }

    // Finishes a one-press setup: save the robot as a prefab linked to the model picker, then validate
    // its physics. The prefab is saved FIRST so validation can test the actual spawnable prefab in the
    // field scene (with a floor to settle on) regardless of which scene setup was done in. Saving with
    // removeInlineCopy destroys our in-scene reference, so the robot is re-found by name up front.
    private string Finish(string robotName)
    {
        StringBuilder sb = new StringBuilder();
        GameObject root = FindRobotByName(robotName); // robotRoot may already be stale
        GameObject prefab = null;

        // 1) Save as a prefab and link it to the model picker (removing the inline copy so it doesn't
        //    double-spawn). Runs even if validation later fails — the prefab is still worth iterating on.
        if (saveAsPrefabAfterSetup)
        {
            if (root == null)
                sb.Append("\n\nSkipped prefab save: couldn't find the robot. Run Robot > Save As Robot Prefab manually.");
            else
            {
                UnityEngine.SceneManagement.Scene scene = root.scene;
                try
                {
                    string note = SaveRobotPrefab.Run(root, removeInlineCopy: true);
                    if (scene.IsValid() && !string.IsNullOrEmpty(scene.path)) EditorSceneManager.SaveScene(scene);
                    prefab = AssetDatabase.LoadAssetAtPath<GameObject>(SaveRobotPrefab.PrefabPathFor(robotName));
                    sb.Append("\n\n").Append(note);
                    sb.Append("\n\nTo add arms/lifts/pistons: drag ").Append(SaveRobotPrefab.PrefabPathFor(robotName))
                      .Append(" back into the scene, add them with the Mechanisms tools, then re-run Save As Robot Prefab.");
                }
                catch (Exception e)
                {
                    sb.Append($"\n\nPrefab save FAILED:\n{e.Message}");
                }
            }
        }

        // 2) Validate the physics. Prefer the saved prefab (spawned into the field scene); otherwise
        //    test the in-scene robot, which needs the scene on disk (the smoke test reloads it).
        if (validateAfterSetup)
        {
            try
            {
                if (prefab != null)
                {
                    PhysicsSmokeTest.ValidateSpawnedPrefab(prefab);
                    sb.Append("\n\nPhysics validation: PASSED (settles, drives, turns).");
                }
                else if (root != null && root.scene.IsValid() && !string.IsNullOrEmpty(root.scene.path)
                         && EditorSceneManager.SaveScene(root.scene))
                {
                    PhysicsSmokeTest.ValidateRobot(root);
                    sb.Append("\n\nPhysics validation: PASSED (settles, drives, turns).");
                }
                else
                {
                    sb.Append("\n\nSkipped validation: save the scene first, then run Robot > Validate Robot Physics.");
                }
            }
            catch (Exception e)
            {
                sb.Append($"\n\nPhysics validation FAILED:\n{e.Message}\n" +
                          "The robot is set up but needs tuning — see the Console.");
            }
        }

        if (!validateAfterSetup && !saveAsPrefabAfterSetup)
            sb.Append("\n\nSave the scene, then run Validate Robot Physics and Save As Robot Prefab when ready.");

        robotRoot = null; // references may be stale after the scene reload / inline-copy removal
        return sb.ToString();
    }

    // Re-find the just-set-up robot after saving its prefab / a validation scene-reload invalidates our
    // reference. Set-up robots carry a RobotMotorController; match the one whose name we saved.
    private static GameObject FindRobotByName(string robotName)
    {
        foreach (RobotMotorController c in
                 UnityEngine.Object.FindObjectsByType<RobotMotorController>(FindObjectsInactive.Include))
            if (c != null && c.gameObject.name == robotName) return c.gameObject;
        return null;
    }

    // --- Detection ---------------------------------------------------------------------------

    public enum RobotKind { Urdf, Mesh, AlreadySetUp, Unknown }

    public static RobotKind Detect(GameObject root)
    {
        // "Already set up" is marked by RobotMotorController — both pipelines end by wiring
        // one, and UrdfPostProcessor's own idempotency guard keys on it. A bare
        // ArticulationBody is NOT a valid marker for URDF robots: the importer itself creates
        // ArticulationBodies on every link at import time (UrdfInertial and UrdfJoint both
        // [RequireComponent] one), so a fresh URDF import always carries them and the old
        // body-first check made the URDF setup path unreachable.
        if (root.GetComponentInChildren<RobotMotorController>(true) != null) return RobotKind.AlreadySetUp;
        if (root.GetComponentsInChildren<UrdfLink>(true).Length > 0) return RobotKind.Urdf;
        // Mesh robots rigged by hand (no controller) still read as set up via their bodies.
        if (root.GetComponentInChildren<ArticulationBody>(true) != null) return RobotKind.AlreadySetUp;
        if (root.GetComponentInChildren<MeshFilter>(true) != null) return RobotKind.Mesh;
        return RobotKind.Unknown;
    }

    // --- Pipeline ----------------------------------------------------------------------------

    // Runs the full setup for the detected robot kind. Returns a human-readable summary.
    // Throws InvalidOperationException with a readable message on any precondition failure.
    public static string Run(GameObject root, RobotKind kind, string meshWheelPrefix, string urdfWheelSubstring,
        bool keepUrdfInertials = false, bool computeMassFromGeometry = true, bool hullConcaveStructural = true)
    {
        switch (kind)
        {
            case RobotKind.Urdf:
                // Post-Process already does colliders + motors + mechanisms + catalog in one pass.
                UrdfPostProcessor.PostProcess(root, UrdfScaleFactor, true, urdfWheelSubstring, keepUrdfInertials,
                    computeMassFromGeometry);
                MarkDirty(root);
                return $"'{root.name}' (URDF) is set up:\n" +
                       $"  - baked to {UrdfScaleFactor}x world scale\n" +
                       "  - per-part colliders + wheel spheres\n" +
                       (computeMassFromGeometry && !keepUrdfInertials
                           ? "  - link masses computed from part geometry (see Console for the breakdown)\n"
                           : keepUrdfInertials ? "  - kept the URDF mass properties\n" : "") +
                       "  - wheel motors wired to the joysticks\n" +
                       "  - arm/piston mechanisms wired for the controller buttons\n" +
                       "  - added to the home-screen robot list";

            case RobotKind.Mesh:
                GeneratePartColliders.Report report = GeneratePartColliders.Generate(root, meshWheelPrefix, hullConcaveStructural);
                if (report.sphereCount == 0)
                    throw new InvalidOperationException(
                        $"No wheel nodes under '{root.name}' matched '{meshWheelPrefix}', so there is " +
                        "nothing to turn into motors. Set 'Wheel Name Contains' to a token in this robot's " +
                        "wheel node names (comma-separate several), then run again.");

                RigDrivetrainArticulation.Rig(root, meshWheelPrefix);

                // Register it as a first-class set-up robot: a mechanisms registry + button router
                // (so Add/Fix Mechanism Joint can split arms/pistons off afterward) and a home-screen
                // catalog entry. It starts with no button mechanisms — the drivetrain is on the sticks.
                RobotMechanisms registry = UrdfPostProcessor.EnsureRegistry(root, useUndo: true);
                UrdfPostProcessor.EnsureButtonRouter(root, registry, useUndo: true);
                UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, root.name, registry);

                MarkDirty(root);
                return $"'{root.name}' (mesh) is set up:\n" +
                       $"  - {report.boxCount + report.obbChildCount} part box colliders " +
                       $"({report.skippedFasteners} fasteners skipped)\n" +
                       $"  - {report.sphereCount} wheel spheres, now {report.sphereCount} motor-driven wheel links\n" +
                       "  - motors wired to the joysticks\n" +
                       "  - registered as a robot (add arms/pistons with Advanced ▸ Add or Fix Mechanism Joint)";

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
