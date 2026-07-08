using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// One-click setup for the driving robot's physics.
//
// The imported drivetrain gives the robot hundreds of concave MeshColliders, which Unity
// cannot solve on a moving (non-kinematic) Rigidbody: the robot passes through walls and
// then gets flung. This tool strips those colliders and rebuilds a small compound collider
// that follows the chassis shape (one convex box per major sub-group: Left drivetrain, Right
// drivetrain, Frame). It also:
//   - records the left/right drivetrain box centers as the turn pivots on RobotDriveController
//     (so turning pivots on the inner wheel),
//   - bumps the robot mass so it shoves light game pieces instead of climbing over them,
//   - tags the robot "Player" so the match loaders only react to the robot.
//
// Usage: select the Robot object in the Hierarchy, then Tools > VEX > Fix Robot Drive Collider.
public class FixRobotCollider
{
    // Major sub-assemblies of the drivetrain, matched by name substring.
    private const string LeftGroup = "Drivetrain LS";
    private const string RightGroup = "Drivetrain RS";
    private const string FrameGroup = "Frame";

    private const float RobotMass = 30f;

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

        // 2) Build one convex BoxCollider per major sub-group, in the robot's local space.
        int boxes = 0;
        bool haveLeft = false, haveRight = false;
        Vector3 leftCenter = Vector3.zero, rightCenter = Vector3.zero;
        BoxCollider frameBox = null;
        float minPodBottom = float.PositiveInfinity; // lowest bottom of the two drivetrain pods

        foreach (string groupName in new[] { LeftGroup, RightGroup, FrameGroup })
        {
            if (!TryGetGroupLocalBounds(robot, groupName, out Vector3 center, out Vector3 size))
            {
                Debug.LogWarning($"Fix Robot Drive Collider: no group matching '{groupName}' found; skipping.", robot);
                continue;
            }

            BoxCollider box = Undo.AddComponent<BoxCollider>(robot);
            box.center = center;
            box.size = size;
            boxes++;

            if (groupName == LeftGroup) { leftCenter = center; haveLeft = true; minPodBottom = Mathf.Min(minPodBottom, center.y - size.y * 0.5f); }
            else if (groupName == RightGroup) { rightCenter = center; haveRight = true; minPodBottom = Mathf.Min(minPodBottom, center.y - size.y * 0.5f); }
            else if (groupName == FrameGroup) { frameBox = box; }
        }

        // The frame model sits above the wheels, leaving an open channel under the robot that
        // short cups/pins slip into (so it looks like the robot drives over them). Drop the
        // frame collider's bottom down to the drivetrain pods' bottom to seal that channel.
        if (frameBox != null && !float.IsPositiveInfinity(minPodBottom))
        {
            float top = frameBox.center.y + frameBox.size.y * 0.5f;
            if (minPodBottom < frameBox.center.y - frameBox.size.y * 0.5f)
            {
                Vector3 s = frameBox.size; s.y = top - minPodBottom; frameBox.size = s;
                Vector3 c = frameBox.center; c.y = (top + minPodBottom) * 0.5f; frameBox.center = c;
            }
        }

        // Fallback: if the named groups weren't found, wrap the whole robot in one box so it
        // at least drives solidly (pivot will be centered rather than per-wheel).
        Vector3 overallCenter = Vector3.zero;
        if (TryGetGroupLocalBounds(robot, null, out Vector3 allCenter, out Vector3 allSize))
        {
            overallCenter = allCenter;
            if (boxes == 0)
            {
                BoxCollider box = Undo.AddComponent<BoxCollider>(robot);
                box.center = allCenter;
                box.size = allSize;
                boxes++;
            }
        }

        // 3) Feed the turn pivots (drivetrain rail centers) to the drive controller.
        RobotDriveController drive = robot.GetComponent<RobotDriveController>();
        if (drive != null)
        {
            SerializedObject so = new SerializedObject(drive);
            so.FindProperty("leftPivotOffset").vector3Value = haveLeft ? leftCenter : overallCenter;
            so.FindProperty("rightPivotOffset").vector3Value = haveRight ? rightCenter : overallCenter;
            so.FindProperty("centerOffset").vector3Value = overallCenter;
            so.ApplyModifiedProperties();
        }
        else
        {
            Debug.LogWarning("Fix Robot Drive Collider: no RobotDriveController on the selected object; " +
                             "turn pivots were not set.", robot);
        }

        // 4) Heavier robot pushes light pieces (mass 1) instead of riding up over them.
        Rigidbody rb = robot.GetComponent<Rigidbody>();
        if (rb != null)
        {
            Undo.RecordObject(rb, "Fix Robot Drive Collider");
            rb.mass = RobotMass;
        }

        // 5) Tag the robot so the match loaders can filter for it.
        Undo.RecordObject(robot, "Fix Robot Drive Collider");
        robot.tag = "Player";

        EditorUtility.SetDirty(robot);
        EditorSceneManager.MarkSceneDirty(robot.scene);

        Debug.Log($"Fix Robot Drive Collider: removed {removed} collider(s), added {boxes} chassis box(es), " +
                  $"set mass {RobotMass}, wrote turn pivots, and tagged '{robot.name}' as Player.", robot);
    }

    // Combined renderer bounds of a named sub-group (or the whole robot when groupName is null),
    // expressed in the robot root's local space as a BoxCollider center + size.
    private static bool TryGetGroupLocalBounds(GameObject robot, string groupName, out Vector3 center, out Vector3 size)
    {
        center = Vector3.zero;
        size = Vector3.zero;

        // Collect the renderers that belong to the group (or all of them).
        List<Renderer> renderers = new List<Renderer>();
        if (groupName == null)
        {
            renderers.AddRange(robot.GetComponentsInChildren<Renderer>());
        }
        else
        {
            foreach (Transform child in robot.GetComponentsInChildren<Transform>())
            {
                if (child.name.Contains(groupName))
                {
                    renderers.AddRange(child.GetComponentsInChildren<Renderer>());
                }
            }
        }

        if (renderers.Count == 0) return false;

        // World-space AABB over the group's renderers.
        Bounds world = renderers[0].bounds;
        for (int i = 1; i < renderers.Count; i++) world.Encapsulate(renderers[i].bounds);

        // Convert into the robot root's local space (root is unrotated/unscaled in practice,
        // but Abs keeps the size sane if that ever changes).
        Transform t = robot.transform;
        center = t.InverseTransformPoint(world.center);
        Vector3 local = t.InverseTransformVector(world.size);
        size = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
        return true;
    }
}
