using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Strips a robot back to bare meshes + colliders so it can be re-rigged from scratch. Removes every
// ArticulationBody (the root, the wheel links, and every mechanism joint), every motor/piston/coupler,
// the drivetrain and button-router controllers, clears the mechanism registry, un-nests the
// WheelLink_* wrappers the rig created, and clears the saved controller map.
//
// Use it when a rig has gotten into a bad state — joints sagging under gravity, couplers fighting — and
// you want a clean slate. Recommended flow: Clean, then rebuild ONE system at a time and test each:
//   1) Clean Robot Rig (Reset)
//   2) Rig Drivetrain  -> Validate Robot Physics   (get the drivetrain solid first)
//   3) add mechanisms one at a time, validating after each.
//
// Meshes, colliders, and the robotId are kept, so re-rigging just works. Undoable, and it asks first.
public static class CleanRobotRig
{
    private const string Title = "Clean Robot Rig";

    [MenuItem("Tools/RoboSim/Robot/Advanced/Clean Robot Rig (Reset)", false, 3)]
    private static void CleanSelected()
    {
        GameObject robot = ResolveRobotRoot(Selection.activeGameObject);
        if (robot == null)
        {
            EditorUtility.DisplayDialog(Title, "Select your robot (the root object) in the Hierarchy first.", "OK");
            return;
        }

        if (!EditorUtility.DisplayDialog(Title,
            $"Strip ALL rig data from '{robot.name}'?\n\n" +
            "Removes every joint (ArticulationBody), motor/piston/coupler, the drivetrain and button " +
            "controllers, and clears the mechanism list + saved button map. Meshes and colliders are kept.\n\n" +
            "Use this to start the rig over — you can Undo it.", "Clean it", "Cancel"))
            return;

        string report = Clean(robot, useUndo: true);
        EditorUtility.DisplayDialog(Title,
            report + "\nNext: Rig Drivetrain, then Validate Robot Physics.", "OK");
        Debug.Log($"{Title}: {report}", robot);
    }

    // Does the stripping. useUndo=false for headless/batch callers. Returns a short report.
    public static string Clean(GameObject robot, bool useUndo)
    {
        if (robot == null) throw new System.ArgumentNullException(nameof(robot));

        int group = 0;
        if (useUndo)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Title);
            group = Undo.GetCurrentGroup();
        }

        // 1) Actuators + couplers first (they reference bodies we're about to remove).
        int couplers = DestroyAll<JointCoupler>(robot, useUndo);
        int motors = DestroyAll<MotorActuator>(robot, useUndo);
        int pistons = DestroyAll<PneumaticActuator>(robot, useUndo);

        // 2) Controllers.
        int controllers = DestroyAll<RobotMotorController>(robot, useUndo) + DestroyAll<ButtonRouter>(robot, useUndo);

        // 3) Clear the mechanism registry, keeping the component + robotId so the robot keeps its
        //    identity and the catalog/button map still line up.
        string robotId = null;
        RobotMechanisms registry = robot.GetComponent<RobotMechanisms>();
        if (registry != null)
        {
            robotId = registry.robotId;
            if (registry.mechanisms != null && registry.mechanisms.Count > 0)
            {
                if (useUndo) Undo.RecordObject(registry, Title);
                registry.mechanisms.Clear();
                EditorUtility.SetDirty(registry);
            }
        }

        // 4) Every ArticulationBody (mechanism joints, wheel links, the root).
        int joints = DestroyAll<ArticulationBody>(robot, useUndo);

        // 5) Un-nest the WheelLink_* wrappers the rig created: lift their children back up and delete
        //    the empties, so a fresh Rig re-clusters cleanly instead of nesting new links inside old.
        int flattened = FlattenWheelLinks(robot, useUndo);

        // 6) Clear the saved controller map so bindings start fresh.
        bool mapCleared = false;
        if (!string.IsNullOrEmpty(robotId) && PlayerPrefs.HasKey(ControllerMapSettings.PrefKey(robotId)))
        {
            PlayerPrefs.DeleteKey(ControllerMapSettings.PrefKey(robotId));
            PlayerPrefs.Save();
            mapCleared = true;
        }

        // Mirror the now-empty mechanism list into the home-screen catalog.
        if (registry != null && !string.IsNullOrEmpty(robotId))
            UrdfPostProcessor.RefreshCatalogMechanisms(robotId, robot.name, registry);

        if (useUndo)
        {
            Undo.CollapseUndoOperations(group);
            if (robot.scene.IsValid()) EditorSceneManager.MarkSceneDirty(robot.scene);
        }
        EditorUtility.SetDirty(robot);

        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"Cleaned '{robot.name}':");
        sb.AppendLine($"  couplers removed: {couplers}");
        sb.AppendLine($"  motors removed: {motors}");
        sb.AppendLine($"  pistons removed: {pistons}");
        sb.AppendLine($"  controllers removed: {controllers}");
        sb.AppendLine($"  joints (ArticulationBody) removed: {joints}");
        sb.AppendLine($"  wheel-link wrappers flattened: {flattened}");
        sb.AppendLine($"  button map cleared: {(mapCleared ? "yes" : "none")}");
        return sb.ToString();
    }

    // Destroys deepest-first (reverse of the root-first GetComponentsInChildren order) so removing an
    // ArticulationBody chain doesn't repeatedly re-root the survivors.
    private static int DestroyAll<T>(GameObject root, bool useUndo) where T : Component
    {
        T[] items = root.GetComponentsInChildren<T>(true);
        int removed = 0;
        for (int i = items.Length - 1; i >= 0; i--)
        {
            if (items[i] == null) continue;
            if (useUndo) Undo.DestroyObjectImmediate(items[i]);
            else Object.DestroyImmediate(items[i]);
            removed++;
        }
        return removed;
    }

    // Moves each WheelLink_* wrapper's children up to the wrapper's parent (keeping world placement),
    // then deletes the now-empty wrapper. Returns how many wrappers were removed.
    private static int FlattenWheelLinks(GameObject root, bool useUndo)
    {
        List<Transform> wrappers = new List<Transform>();
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t != null && t.name.StartsWith("WheelLink_")) wrappers.Add(t);

        int removed = 0;
        foreach (Transform wrapper in wrappers)
        {
            if (wrapper == null) continue;
            Transform parent = wrapper.parent;
            List<Transform> children = new List<Transform>();
            foreach (Transform child in wrapper) children.Add(child);
            foreach (Transform child in children)
            {
                if (useUndo) Undo.SetTransformParent(child, parent, Title);
                else child.SetParent(parent, true); // keep world placement
            }
            if (useUndo) Undo.DestroyObjectImmediate(wrapper.gameObject);
            else Object.DestroyImmediate(wrapper.gameObject);
            removed++;
        }
        return removed;
    }

    // The robot root: the highest ancestor carrying rig data, else the selection itself.
    private static GameObject ResolveRobotRoot(GameObject sel)
    {
        if (sel == null) return null;
        RobotMechanisms reg = sel.GetComponentInParent<RobotMechanisms>();
        if (reg != null) return reg.gameObject;
        RobotMotorController motor = sel.GetComponentInParent<RobotMotorController>();
        if (motor != null) return motor.gameObject;
        ArticulationBody ab = sel.GetComponentInParent<ArticulationBody>();
        if (ab != null)
        {
            Transform top = ab.transform;
            for (Transform t = top.parent; t != null; t = t.parent)
                if (t.GetComponent<ArticulationBody>() != null) top = t;
            return top.gameObject;
        }
        return sel; // nothing rigged yet — treat the selection as the robot
    }
}
