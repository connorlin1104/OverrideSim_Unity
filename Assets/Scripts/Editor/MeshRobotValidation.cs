using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Headless end-to-end validation of the MESH (FBX) robot path for -executeMethod. It needs no FBX:
// it builds a synthetic mesh robot in a scratch scene — a chassis box, two "wheel"-named boxes, and
// an "arm" box — runs Set Up Imported Robot's mesh path (colliders + wheel motors + registry +
// button router + catalog), then splits the arm into a revolute mechanism with
// AddMechanismJoint.Apply and proves in edit-mode physics that it wired and actually sweeps under
// its motor. Throws (nonzero editor exit) on any failure.
//
// This is the FBX-side analogue of UrdfPostProcessor.RunBatchValidateJointTool (which proves the
// same split-a-fixed-link path for URDF robots). Run:
//   Unity -batchmode -executeMethod MeshRobotValidation.RunBatchValidateMeshRobot -quit
public static class MeshRobotValidation
{
    private const string CatalogPath = "Assets/Settings/RobotModelCatalog.asset";
    private const string SamplePath = "Assets/Scenes/SampleScene.unity";

    public static void RunBatchValidateMeshRobot()
    {
        const string robotName = "MeshBot";
        string catalogEntryId = UrdfPostProcessor.Slugify(robotName);
        bool hadCatalogEntry = HasCatalogEntry(catalogEntryId);
        SimulationMode previousSimulationMode = Physics.simulationMode;

        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateGroundPlane();
            GameObject robot = BuildSyntheticMeshRobot(robotName);
            robot.transform.position = new Vector3(0f, 1f, 0f); // ~1 unit above the ground

            if (SetUpImportedRobot.Detect(robot) != SetUpImportedRobot.RobotKind.Mesh)
                throw new InvalidOperationException(
                    "Mesh validation FAILED: synthetic robot was not detected as a mesh robot.");

            // The path the docs tell users to run: colliders + wheel motors + registry/router/catalog.
            SetUpImportedRobot.Run(robot, SetUpImportedRobot.RobotKind.Mesh, "wheel", "wheel");

            // 1) Structural: drivetrain wired 1 left + 1 right, and it's a first-class set-up robot.
            RobotMotorController motor = robot.GetComponent<RobotMotorController>();
            if (motor == null || motor.leftWheels == null || motor.rightWheels == null ||
                motor.leftWheels.Length != 1 || motor.rightWheels.Length != 1)
                throw new InvalidOperationException(
                    "Mesh validation FAILED: expected 1 left + 1 right wheel wired, got " +
                    $"{motor?.leftWheels?.Length ?? 0} left / {motor?.rightWheels?.Length ?? 0} right.");

            RobotMechanisms registry = robot.GetComponent<RobotMechanisms>();
            if (registry == null || registry.robotId != catalogEntryId)
                throw new InvalidOperationException(
                    $"Mesh validation FAILED: RobotMechanisms missing or robotId '{registry?.robotId}' != '{catalogEntryId}'.");
            if (robot.GetComponent<ButtonRouter>() == null)
                throw new InvalidOperationException("Mesh validation FAILED: no ButtonRouter on the root.");
            if (registry.mechanisms.Count != 0)
                throw new InvalidOperationException(
                    $"Mesh validation FAILED: expected 0 button mechanisms before the split, got {registry.mechanisms.Count}.");

            // 2) Split the "arm" mesh part into a revolute mechanism. Axis = Y so the sweep is
            //    horizontal (gravity-neutral) and can't be masked by the arm sagging.
            GameObject arm = FindDescendant(robot.transform, "arm");
            if (arm == null)
                throw new InvalidOperationException("Mesh validation FAILED: no 'arm' node found.");
            if (arm.GetComponent<ArticulationBody>() != null)
                throw new InvalidOperationException(
                    "Mesh validation FAILED: 'arm' already had an ArticulationBody before the split.");

            AddMechanismJoint.Apply(arm, AddMechanismJoint.JointType.Revolute, Vector3.up, Vector3.zero,
                -90f, 90f, useUndo: false);

            ArticulationBody armBody = arm.GetComponent<ArticulationBody>();
            if (armBody == null || armBody.jointType != ArticulationJointType.RevoluteJoint)
                throw new InvalidOperationException(
                    "Mesh validation FAILED: 'arm' did not become a revolute ArticulationBody after the split.");
            if (armBody.mass <= 0f)
                throw new InvalidOperationException(
                    $"Mesh validation FAILED: split arm has non-positive mass {armBody.mass}.");

            RobotMechanisms.Mechanism armMech = registry.Find("arm");
            if (armMech == null || armMech.type != RobotMechanisms.TypeMotor || armMech.motor == null ||
                armMech.motor.body == null)
                throw new InvalidOperationException(
                    "Mesh validation FAILED: 'arm' is not a wired motor mechanism after the split.");

            RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
            RobotModelCatalog.Entry entry = catalog != null
                ? catalog.models.Find(e => e != null && e.id == catalogEntryId) : null;
            if (catalog != null && (entry == null || entry.mechanisms == null ||
                                    entry.mechanisms.Find(m => m != null && m.id == "arm") == null))
                throw new InvalidOperationException(
                    "Mesh validation FAILED: catalog entry is missing the 'arm' mechanism after the split.");

            // 3) Edit-mode physics: the split arm must actually sweep under its motor.
            Physics.simulationMode = SimulationMode.Script;
            for (int i = 0; i < 100; i++) Physics.Simulate(0.01f); // settle onto the ground

            if (armBody.dofCount < 1)
                throw new InvalidOperationException(
                    "EDITMODE_SIM_UNSUPPORTED: arm reports zero DOFs after Physics.Simulate — the " +
                    "articulation was never rebuilt after the split; validate in play mode instead.");

            float armStart = armBody.jointPosition[0];
            armMech.motor.SetInput(1f);
            for (int i = 0; i < 200; i++) Physics.Simulate(0.01f);
            float armDelta = armBody.jointPosition[0] - armStart;
            armMech.motor.SetInput(0f);

            if (float.IsNaN(armDelta))
                throw new InvalidOperationException("Mesh validation FAILED: arm joint position became NaN.");
            if (Mathf.Abs(armDelta) < 0.3f)
                throw new InvalidOperationException(
                    $"Mesh validation FAILED: the split arm swept only {armDelta:F3} rad over 2 s of full " +
                    "input — the MotorActuator drive on the new link is not moving it.");

            Debug.Log($"Mesh robot validation PASSED: a synthetic FBX-style robot rigged (1 left / 1 right " +
                      $"wheel), registered as a robot, and a mesh 'arm' split off the chassis into a revolute " +
                      $"mechanism that swept {armDelta:F2} rad under its motor.");
        }
        finally
        {
            Physics.simulationMode = previousSimulationMode;
            if (!hadCatalogEntry) RemoveCatalogEntry(catalogEntryId);
            PlayerPrefs.DeleteKey(ControllerMapSettings.PrefKey(catalogEntryId));
            if (File.Exists(SamplePath)) EditorSceneManager.OpenScene(SamplePath, OpenSceneMode.Single);
            else EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }

    // A chassis, two coincidence-separated "wheel" boxes (become left/right motor links), and an
    // "arm" box above the chassis (the split target). Boxes carry MeshRenderers so the wheel finder
    // and collider generator can size from their bounds; their auto BoxColliders are stripped because
    // GeneratePartColliders rebuilds colliders itself.
    private static GameObject BuildSyntheticMeshRobot(string name)
    {
        GameObject root = new GameObject(name);
        MakeBox("chassis", root.transform, new Vector3(0f, 0f, 0f), new Vector3(4f, 1f, 3f));
        MakeBox("wheel_L", root.transform, new Vector3(-2f, -0.5f, 0f), new Vector3(0.6f, 0.6f, 0.4f));
        MakeBox("wheel_R", root.transform, new Vector3(2f, -0.5f, 0f), new Vector3(0.6f, 0.6f, 0.4f));
        MakeBox("arm", root.transform, new Vector3(0f, 1f, 0f), new Vector3(2f, 0.3f, 0.3f));
        return root;
    }

    private static void MakeBox(string name, Transform parent, Vector3 localPos, Vector3 scale)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        Collider auto = go.GetComponent<Collider>();
        if (auto != null) UnityEngine.Object.DestroyImmediate(auto);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = scale;
    }

    private static GameObject FindDescendant(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t.gameObject;
        return null;
    }

    private static void CreateGroundPlane()
    {
        GameObject ground = new GameObject("Ground");
        BoxCollider box = ground.AddComponent<BoxCollider>();
        box.size = new Vector3(200f, 1f, 200f);
        ground.transform.position = new Vector3(0f, -0.5f, 0f);
    }

    private static bool HasCatalogEntry(string id)
    {
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
        return catalog != null && catalog.models != null && catalog.models.Exists(e => e != null && e.id == id);
    }

    private static void RemoveCatalogEntry(string id)
    {
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
        if (catalog != null && catalog.models != null &&
            catalog.models.RemoveAll(e => e != null && e.id == id) > 0)
        {
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
        }
    }
}
