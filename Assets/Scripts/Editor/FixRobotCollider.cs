using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// LEGACY — superseded by Tools > RoboSim > Robot > Set Up Imported Robot.
//
// This built the physics for the OLD robot: a single Rigidbody driven by setting velocities
// directly (RobotDriveController), wrapped in three hand-approximated boxes (Left drivetrain,
// Right drivetrain, Frame) and pinned to the floor. The robot is now an ArticulationBody with
// per-part colliders and torque-driven wheels, so running this on it would strip the rig's
// colliders and leave a robot that cannot drive. It is kept only to rebuild the old setup if
// the articulation experiment is ever reverted; the menu item asks for confirmation first.
//
// What it does: strips every collider under the robot, adds one convex box per sub-group,
// records the drivetrain box centers as RobotDriveController's inner-wheel turn pivots, sets
// mass 30, and tags the robot "Player".
//
// Usage: select the Robot object, then
// Tools > RoboSim > Legacy — Old Velocity Drive > Rebuild Velocity-Drive Colliders.
// The menu item is greyed out whenever the selected robot has an ArticulationBody.
public class FixRobotCollider
{
    // Major sub-assemblies of the drivetrain, matched by name substring.
    private const string LeftGroup = "Drivetrain LS";
    private const string RightGroup = "Drivetrain RS";
    private const string FrameGroup = "Frame";

    private const float RobotMass = 30f;

    // Greys the item out unless the selection is a robot this tool can safely act on: it is
    // disabled for the motor-driven robot (whose rig it would destroy) and when nothing that
    // looks like a robot is selected. Belt and braces with the confirmation dialog below.
    [MenuItem("Tools/RoboSim/Legacy — Old Velocity Drive/Rebuild Velocity-Drive Colliders", true)]
    private static bool FixColliderEnabled()
    {
        GameObject robot = Selection.activeGameObject;
        return robot != null
               && robot.GetComponentInChildren<MeshFilter>(true) != null
               && robot.GetComponentInChildren<ArticulationBody>(true) == null;
    }

    [MenuItem("Tools/RoboSim/Legacy — Old Velocity Drive/Rebuild Velocity-Drive Colliders", false, 1000)]
    private static void FixCollider()
    {
        GameObject robot = Selection.activeGameObject;
        if (robot == null)
        {
            EditorUtility.DisplayDialog("Rebuild Velocity-Drive Colliders",
                "Select your Robot GameObject in the Hierarchy first.", "OK");
            return;
        }

        // The current robot is motor-driven; this tool would gut it. Make that impossible to
        // do by accident.
        if (robot.GetComponentInChildren<ArticulationBody>() != null &&
            !EditorUtility.DisplayDialog("Rebuild Velocity-Drive Colliders",
                $"'{robot.name}' is a motor-driven ArticulationBody robot.\n\n" +
                "This legacy tool rebuilds the OLD velocity-drive setup: it will delete the " +
                "per-part colliders and wheel spheres the rig depends on, and the robot will " +
                "not drive until you re-rig it.\n\nContinue anyway?",
                "Yes, revert to the old setup", "Cancel"))
        {
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
