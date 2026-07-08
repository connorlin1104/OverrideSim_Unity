using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// One-click fixer for the driving robot's physics collider.
//
// The imported drivetrain gives the robot hundreds of concave MeshColliders, which Unity
// cannot solve on a moving (non-kinematic) Rigidbody: the robot passes through walls and
// then gets flung. This tool strips those child colliders and replaces them with a single
// convex BoxCollider sized to the whole chassis and centered on it, so the robot is blocked
// cleanly by walls and rotates around its own center. It also tags the robot "Player" so the
// match loaders only react to the robot (not to spawned game pieces).
//
// Usage: select the Robot object in the Hierarchy, then Tools > VEX > Fix Robot Drive Collider.
public class FixRobotCollider
{
    [MenuItem("Tools/VEX/Fix Robot Drive Collider")]
    private static void FixCollider()
    {
        GameObject robot = Selection.activeGameObject;
        if (robot == null)
        {
            EditorUtility.DisplayDialog("Fix Robot Drive Collider",
                "Select your Robot GameObject in the Hierarchy first.", "OK");
            return;
        }

        // 1) Remove every collider under the robot (concave mesh colliders can't drive a moving body).
        Collider[] existing = robot.GetComponentsInChildren<Collider>(true);
        int removed = 0;
        foreach (Collider col in existing)
        {
            Undo.DestroyObjectImmediate(col);
            removed++;
        }

        // 2) Measure the whole chassis from its renderers (world-space AABB).
        Renderer[] renderers = robot.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            EditorUtility.DisplayDialog("Fix Robot Drive Collider",
                "That object has no renderers to size a collider from. Select the Robot (the parent of the drivetrain model).", "OK");
            return;
        }

        Bounds worldBounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            worldBounds.Encapsulate(renderers[i].bounds);
        }

        // 3) Add one convex BoxCollider on the robot root, converted into its local space.
        BoxCollider box = Undo.AddComponent<BoxCollider>(robot);
        Transform t = robot.transform;
        box.center = t.InverseTransformPoint(worldBounds.center);
        Vector3 localSize = t.InverseTransformVector(worldBounds.size);
        box.size = new Vector3(Mathf.Abs(localSize.x), Mathf.Abs(localSize.y), Mathf.Abs(localSize.z));

        // 4) Tag the robot so the match loaders can filter for it.
        Undo.RecordObject(robot, "Fix Robot Drive Collider");
        robot.tag = "Player";

        EditorUtility.SetDirty(robot);
        EditorSceneManager.MarkSceneDirty(robot.scene);

        Debug.Log($"Fix Robot Drive Collider: removed {removed} collider(s), added 1 chassis BoxCollider " +
                  $"(size {box.size}, center {box.center}), and tagged '{robot.name}' as Player.", robot);
    }
}
