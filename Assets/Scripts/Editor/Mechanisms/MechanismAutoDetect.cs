using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Auto-detects and wires a mesh/FBX robot's mechanisms (arms, intakes, flywheels, pneumatics) in
// one pass, so you don't hand-mark every DOF. An FBX bakes only geometry — no joints — but VEX
// robots keep meaningful names on their moving groups even when custom-cut leaves are generic
// "BodyN" (e.g. "50mm Cylinder", "Intake", "claw mech"). This tool:
//   1. finds the named mechanism groups (RobotPartClassifier.TryClassifyMechanism), skipping wheels,
//      fasteners, and support/passive parts,
//   2. guesses each one's joint axis + anchor from its geometry (MechanismAutoDetect.TryInferAxisAnchor),
//   3. splits it off the chassis into a working mechanism (AddMechanismJoint.Apply),
//   4. assigns it the next free controller button(s), and
//   5. reports exactly what it did so you can verify/fix axes in Add or Fix Mechanism Joint.
//
// It is a best-guess FIRST PASS: axes, pneumatic kinematics, and grouping often need a look. It is
// idempotent — a part that already became a mechanism (has an ArticulationBody) is left alone, so
// re-running only picks up newly-named parts.
//
// Usage: select the set-up robot root, then Tools > RoboSim > Robot > Mechanisms > Auto-Detect Mechanisms.
public class AutoDetectMechanismsWindow : EditorWindow
{
    private const string Title = "Auto-Detect Mechanisms";

    [SerializeField] private GameObject robotRoot;
    [SerializeField] private bool assignButtons = true;

    // Preview is cached and only recomputed when the target changes (scanning a 2000+ node robot
    // every repaint would lag the window). Cleared after wiring so the list refreshes.
    private GameObject previewedRoot;
    private List<MechanismAutoDetect.Candidate> preview;

    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Auto-Detect Mechanisms", false, 2)]
    private static void ShowWindow()
    {
        AutoDetectMechanismsWindow window = GetWindow<AutoDetectMechanismsWindow>(Title);
        window.minSize = new Vector2(460f, 300f);
        window.Show();
    }

    private void OnEnable()
    {
        if (robotRoot == null) robotRoot = Selection.activeGameObject;
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Finds this robot's arms / intakes / flywheels / pneumatics by name, splits each off the " +
            "chassis into a mechanism, guesses its axis from geometry, and maps it to a free button. " +
            "It's a best-guess first pass — verify each axis afterward in Add or Fix Mechanism Joint. " +
            "Set up the robot (Set Up Imported Robot) first; re-running only adds newly-named parts.",
            MessageType.Info);

        robotRoot = (GameObject)EditorGUILayout.ObjectField("Robot Root", robotRoot, typeof(GameObject), true);
        if (robotRoot == null)
        {
            EditorGUILayout.HelpBox("Select the robot's root GameObject.", MessageType.Warning);
            return;
        }
        if (robotRoot.GetComponentInParent<RobotMechanisms>() == null)
        {
            EditorGUILayout.HelpBox(
                $"'{robotRoot.name}' is not a set-up robot (no RobotMechanisms). Run " +
                "Tools > RoboSim > Robot > Set Up Imported Robot first.", MessageType.Error);
            return;
        }

        assignButtons = EditorGUILayout.Toggle(new GUIContent("Assign Buttons",
            "Map each detected mechanism to the next free controller button (motors get a forward/reverse " +
            "pair, pneumatics get a toggle). Writes the robot's button map."), assignButtons);

        // Preview what WOULD be wired, so the button click isn't a leap of faith. Cached per target.
        if (preview == null || previewedRoot != robotRoot)
        {
            preview = MechanismAutoDetect.FindCandidates(robotRoot);
            previewedRoot = robotRoot;
        }
        EditorGUILayout.LabelField(preview.Count == 0
            ? "No new named mechanisms found."
            : $"Found {preview.Count} mechanism(s) to wire:", EditorStyles.boldLabel);
        foreach (MechanismAutoDetect.Candidate c in preview)
            EditorGUILayout.LabelField("   • " + c.node.name, $"{c.type}" + (c.instances > 1 ? $"  (×{c.instances})" : ""));
        if (GUILayout.Button("Refresh", GUILayout.Width(90))) previewedRoot = null;

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(preview.Count == 0))
        {
            if (GUILayout.Button("Detect & Wire Mechanisms", GUILayout.Height(30)))
            {
                try
                {
                    string summary = MechanismAutoDetect.DetectAndWire(robotRoot, assignButtons, useUndo: true);
                    previewedRoot = null; // wired parts now have bodies; refresh the list
                    EditorUtility.DisplayDialog(Title, summary, "OK");
                }
                catch (System.Exception e)
                {
                    EditorUtility.DisplayDialog(Title, e.Message, "OK");
                    Debug.LogException(e, robotRoot);
                }
            }
        }
    }
}

// Detection + geometry inference + button assignment, split out so the window (and a headless
// validator) can drive it without UI.
public static class MechanismAutoDetect
{
    private const float RevoluteLowerDeg = -90f;
    private const float RevoluteUpperDeg = 90f;
    private const float PrismaticLowerUnits = 0f;   // scaled units (1 = 0.1 m)
    private const float PrismaticUpperUnits = 3f;    // a guess for pneumatic stroke; user tunes
    private const float DiscThinRatio = 0.5f;        // min-extent < this * mid-extent => disc-like (spin about thin axis)

    // A part that should become a mechanism: the node to split, its guessed type, and how many
    // same-named siblings it stands for (only one is wired).
    public class Candidate
    {
        public Transform node;
        public AddMechanismJoint.JointType type;
        public int instances;
    }

    // The top-most named mechanism groups under root that aren't already wired. Groups same-named
    // duplicates (the 4 "50mm Cylinder"s) into one candidate. Excludes drive wheels, fasteners,
    // support/passive parts, empty nodes, and anything already carrying an ArticulationBody.
    public static List<Candidate> FindCandidates(GameObject root)
    {
        RobotMechanisms registry = root.GetComponentInParent<RobotMechanisms>();
        GameObject robot = registry != null ? registry.gameObject : root;
        RobotMotorController motor = robot.GetComponent<RobotMotorController>();

        // Pass 1: every node whose own name classifies as a mechanism and that could be split.
        var classified = new List<(Transform node, AddMechanismJoint.JointType type)>();
        var classifiedSet = new HashSet<Transform>();
        foreach (Transform node in robot.GetComponentsInChildren<Transform>(true))
        {
            if (node == robot.transform) continue;
            if (!RobotPartClassifier.TryClassifyMechanism(node.name, out AddMechanismJoint.JointType type)) continue;
            if (node.GetComponent<ArticulationBody>() != null) continue;          // already a link/mechanism
            if (node.GetComponentsInChildren<Renderer>(true).Length == 0) continue; // no geometry to move
            if (IsDriveWheel(node, motor)) continue;
            classified.Add((node, type));
            classifiedSet.Add(node);
        }

        // Pass 2: keep only the top-most of each mechanism subtree (so "Intake" wins over any inner
        // "roller" leaves), then collapse same-named siblings into one candidate.
        var byName = new Dictionary<string, Candidate>();
        var order = new List<Candidate>();
        foreach ((Transform node, AddMechanismJoint.JointType type) in classified)
        {
            if (HasAncestorIn(node, classifiedSet)) continue; // a deeper part of another candidate
            string key = RobotPartClassifier.NormalizeName(node.name);
            if (byName.TryGetValue(key, out Candidate existing)) { existing.instances++; continue; }
            var candidate = new Candidate { node = node, type = type, instances = 1 };
            byName[key] = candidate;
            order.Add(candidate);
        }
        return order;
    }

    // Runs the full detect -> infer -> split -> assign pass and returns a human-readable summary.
    // Throws on a precondition failure (not set up, no chassis).
    public static string DetectAndWire(GameObject root, bool assignButtons, bool useUndo)
    {
        RobotMechanisms registry = root.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
            throw new System.InvalidOperationException(
                $"'{root.name}' is not a set-up robot (no RobotMechanisms). Run Set Up Imported Robot first.");
        GameObject robot = registry.gameObject;
        if (robot.GetComponentInChildren<ArticulationBody>(true) == null)
            throw new System.InvalidOperationException(
                $"'{robot.name}' has no ArticulationBody chassis. Run Set Up Imported Robot first.");

        List<Candidate> candidates = FindCandidates(robot);
        if (candidates.Count == 0)
            return "No new named mechanisms found. Name the moving parts (arm, intake, claw, cylinder, " +
                   "flywheel…) in your CAD/hierarchy, or mark them with Add or Fix Mechanism Joint.";

        int group = 0;
        if (useUndo)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Auto-Detect Mechanisms");
            group = Undo.GetCurrentGroup();
        }

        var report = new StringBuilder();
        int wired = 0;
        foreach (Candidate c in candidates)
        {
            try
            {
                TryInferAxisAnchor(c.node.gameObject, c.type, out Vector3 axis, out Vector3 anchor);
                float lower = c.type == AddMechanismJoint.JointType.Prismatic ? PrismaticLowerUnits : RevoluteLowerDeg;
                float upper = c.type == AddMechanismJoint.JointType.Prismatic ? PrismaticUpperUnits : RevoluteUpperDeg;

                AddMechanismJoint.Apply(c.node.gameObject, c.type, axis, anchor, lower, upper, useUndo);
                wired++;

                string line = $"  • {c.node.name} → {c.type}";
                if (assignButtons)
                {
                    string id = UrdfPostProcessor.Slugify(c.node.name);
                    line += "  [" + AssignButtons(registry.robotId, id, c.type) + "]";
                }
                if (c.instances > 1) line += $"  (×{c.instances} found — wired 1; group the rest manually if they move together)";
                report.AppendLine(line);
            }
            catch (System.Exception e)
            {
                report.AppendLine($"  • {c.node.name} → SKIPPED: {e.Message}");
            }
        }

        if (useUndo) Undo.CollapseUndoOperations(group);
        if (robot.scene.IsValid()) EditorSceneManager.MarkSceneDirty(robot.scene);

        return $"Wired {wired} of {candidates.Count} detected mechanism(s) on '{robot.name}':\n\n" + report +
               "\nVerify each axis/anchor in Add or Fix Mechanism Joint, then Validate Robot Physics. " +
               "Save the scene to keep the changes.";
    }

    // Assigns the next free button(s) to a mechanism and saves the robot's map, following the
    // mechanism's CONTROL STYLE (default: a motor gets a hold forward/reverse pair, a pneumatic gets
    // one toggle — but a mechanism the player already switched to the other style keeps it, so
    // re-running a builder doesn't silently undo their choice). Returns a short note of what it did.
    public static string AssignButtons(string robotId, string mechanismId, AddMechanismJoint.JointType type)
    {
        ButtonMap map = ControllerMapSettings.Load(robotId);
        string mechType = type == AddMechanismJoint.JointType.Prismatic
            ? RobotMechanisms.TypePneumatic : RobotMechanisms.TypeMotor;
        string[] modes = ControllerMapSettings.ModesFor(
            mechType, ControllerMapSettings.GetStyle(map, mechanismId, mechType));

        var assigned = new List<string>();
        string shortfall = null;
        foreach (string mode in modes)
        {
            if (!ControllerMapSettings.TryNextFree(map, out ControllerButton button))
            {
                shortfall = ControllerMapSettings.ModeLabel(mode);
                break;
            }
            ControllerMapSettings.SetAssignment(map, button, mechanismId, mode);
            assigned.Add($"{button} = {ControllerMapSettings.ModeLabel(mode)}");
        }

        if (assigned.Count == 0) return "no free button";
        ControllerMapSettings.Save(robotId, map);
        string note = string.Join(", ", assigned);
        if (shortfall != null) note += $" (no free button left for {shortfall})";
        return note;
    }

    // Best-guess joint axis (in the part/link's local frame) and anchor (link-local) from the part's
    // world renderer bounds. Assumes the robot is roughly axis-aligned in the scene — the same
    // assumption RigDrivetrainArticulation uses to find axle axes. Heuristic; the caller reports it
    // for the user to verify. Falls back to a Y axis through the link origin when there's no geometry.
    public static bool TryInferAxisAnchor(GameObject part, AddMechanismJoint.JointType type,
        out Vector3 linkLocalAxis, out Vector3 linkLocalAnchor)
    {
        linkLocalAxis = Vector3.up;
        linkLocalAnchor = Vector3.zero;

        Renderer[] renderers = part.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return false;
        Bounds world = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) world.Encapsulate(renderers[i].bounds);

        Vector3[] cardinals = { Vector3.right, Vector3.up, Vector3.forward };
        float[] ext = { world.extents.x, world.extents.y, world.extents.z };
        int iMax = 0, iMin = 0;
        for (int i = 1; i < 3; i++)
        {
            if (ext[i] > ext[iMax]) iMax = i;
            if (ext[i] < ext[iMin]) iMin = i;
        }
        int iMid = 3 - iMax - iMin;
        if (iMax == iMin) { iMax = 1; iMid = 0; iMin = 2; } // near-cube: no strong axis, just pick one

        Vector3 worldAxis;
        switch (type)
        {
            case AddMechanismJoint.JointType.Prismatic:
                worldAxis = cardinals[iMax]; // slide along the long axis
                break;
            case AddMechanismJoint.JointType.Continuous:
                // Disc (flywheel/wheel) spins about its thin axis; a rod (roller/shaft) about its long axis.
                worldAxis = ext[iMin] < DiscThinRatio * ext[iMid] ? cardinals[iMin] : cardinals[iMax];
                break;
            default: // Revolute — hinge about the more-horizontal of the two shorter axes
                worldAxis = Mathf.Abs(cardinals[iMid].y) <= Mathf.Abs(cardinals[iMin].y)
                    ? cardinals[iMid] : cardinals[iMin];
                break;
        }

        // Anchor: for a revolute arm the pivot sits at the base — the end along the long (reach)
        // axis nearest the chassis. For a slide/spin, the geometric center is the neutral choice.
        Vector3 worldAnchor = world.center;
        if (type != AddMechanismJoint.JointType.Prismatic && type != AddMechanismJoint.JointType.Continuous)
        {
            Vector3 reach = cardinals[iMax];
            Vector3 parentRef = NearestAncestorBodyPos(part.transform, world.center);
            float sign = Vector3.Dot(parentRef - world.center, reach) >= 0f ? 1f : -1f;
            worldAnchor = world.center + reach * ext[iMax] * sign;
        }

        linkLocalAxis = part.transform.InverseTransformDirection(worldAxis).normalized;
        if (linkLocalAxis.sqrMagnitude < 1e-8f) linkLocalAxis = Vector3.up;
        linkLocalAnchor = part.transform.InverseTransformPoint(worldAnchor);
        return true;
    }

    private static Vector3 NearestAncestorBodyPos(Transform t, Vector3 fallback)
    {
        for (Transform p = t.parent; p != null; p = p.parent)
        {
            if (p.GetComponent<ArticulationBody>() != null) return p.position;
        }
        return fallback;
    }

    private static bool IsDriveWheel(Transform node, RobotMotorController motor)
    {
        if (motor == null) return false;
        ArticulationBody body = node.GetComponentInParent<ArticulationBody>();
        if (body == null) return false;
        if (motor.leftWheels != null && System.Array.IndexOf(motor.leftWheels, body) >= 0) return true;
        if (motor.rightWheels != null && System.Array.IndexOf(motor.rightWheels, body) >= 0) return true;
        return false;
    }

    private static bool HasAncestorIn(Transform node, HashSet<Transform> set)
    {
        for (Transform p = node.parent; p != null; p = p.parent)
        {
            if (set.Contains(p)) return true;
        }
        return false;
    }
}
