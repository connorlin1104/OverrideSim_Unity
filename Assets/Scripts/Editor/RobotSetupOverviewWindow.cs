using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// One place to see and edit a robot's whole control setup: the drivetrain wheels, every mechanism
// (rename, reverse, and its button bindings), and every chain/linkage. It reads the same data the
// other tools write — RobotMechanisms (mechanisms + robotId), RobotMotorController (wheels),
// JointCoupler (chains), and the per-robot ButtonMap (PlayerPrefs, keyed by robotId) — so nothing
// new is stored; this window just surfaces it for review and quick edits without hunting through
// the Hierarchy or entering play mode.
//
// Usage: Tools > RoboSim > Robot > Robot Setup Overview. Select the robot in the Hierarchy (or
// assign it in the Robot field). Button-map edits here match what the in-app Configure Controller
// screen shows, since both use ControllerMapSettings.
public class RobotSetupOverviewWindow : EditorWindow
{
    private const string Title = "Robot Setup Overview";

    [SerializeField] private GameObject robotRoot;
    [SerializeField] private Vector2 scroll;

    // Per-mechanism state for the "add a binding" row (keyed by mechanism id).
    private readonly Dictionary<string, ControllerButton> pendingButton = new Dictionary<string, ControllerButton>();
    private readonly Dictionary<string, string> pendingMode = new Dictionary<string, string>();

    [MenuItem("Tools/RoboSim/Robot/Robot Setup Overview", false, 0)]
    private static void ShowWindow()
    {
        RobotSetupOverviewWindow window = GetWindow<RobotSetupOverviewWindow>(Title);
        window.minSize = new Vector2(520f, 480f);
        window.Show();
    }

    private void OnEnable() => TryResolveRoot();

    private void OnSelectionChange()
    {
        TryResolveRoot();
        Repaint();
    }

    // Prefer the robot of the current selection; otherwise keep what we have, else find one in scene.
    private void TryResolveRoot()
    {
        if (Selection.activeGameObject != null)
        {
            RobotMechanisms sel = Selection.activeGameObject.GetComponentInParent<RobotMechanisms>();
            if (sel != null) { robotRoot = sel.gameObject; return; }
        }
        if (robotRoot == null)
        {
            RobotMechanisms[] all = UnityEngine.Object.FindObjectsByType<RobotMechanisms>(FindObjectsInactive.Include);
            if (all != null && all.Length > 0) robotRoot = all[0].gameObject;
        }
    }

    private void OnGUI()
    {
        robotRoot = (GameObject)EditorGUILayout.ObjectField("Robot", robotRoot, typeof(GameObject), true);
        RobotMechanisms registry = robotRoot != null ? robotRoot.GetComponentInParent<RobotMechanisms>() : null;
        if (registry == null)
        {
            EditorGUILayout.HelpBox("Pick a set-up robot (a GameObject with RobotMechanisms on its root), " +
                "or select one in the Hierarchy.", MessageType.Info);
            if (GUILayout.Button("Find robot in scene")) TryResolveRoot();
            return;
        }
        robotRoot = registry.gameObject;
        if (string.IsNullOrEmpty(registry.robotId))
            EditorGUILayout.HelpBox("This robot has no robotId, so button bindings can't be saved. Re-run " +
                "Set Up Imported Robot / Save As Robot Prefab to assign one.", MessageType.Warning);

        scroll = EditorGUILayout.BeginScrollView(scroll);
        DrawDrivetrainSection(registry);
        DrawMechanismsSection(registry);
        DrawChainsSection(registry);
        EditorGUILayout.EndScrollView();
    }

    // --- Drivetrain ---

    private void DrawDrivetrainSection(RobotMechanisms registry)
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Drivetrain", EditorStyles.boldLabel);

        RobotMotorController motor = registry.GetComponent<RobotMotorController>();
        if (motor == null)
        {
            EditorGUILayout.LabelField("Not rigged (no RobotMotorController). Use Rig Drivetrain.",
                EditorStyles.miniLabel);
            return;
        }

        int leftN = CountNonNull(motor.leftWheels);
        int rightN = CountNonNull(motor.rightWheels);
        EditorGUILayout.LabelField($"Driven wheels — left: {leftN}    right: {rightN}");
        if (leftN < 2 || rightN < 2)
            EditorGUILayout.HelpBox("A side has fewer than 2 driven wheels, so some wheels may not spin. Select " +
                "the missing wheel part(s) in the Hierarchy and press Add Selected Wheels.", MessageType.Warning);

        if (GUILayout.Button("Add Selected Wheels to Drivetrain"))
            AddSelectedWheels(registry.gameObject);
    }

    private void AddSelectedWheels(GameObject robot)
    {
        GameObject[] selection = Selection.gameObjects;
        if (selection == null || selection.Length == 0)
        {
            EditorUtility.DisplayDialog(Title, "Select the wheel part(s) to add in the Hierarchy first.", "OK");
            return;
        }
        try
        {
            int added = RigDrivetrainArticulation.AddWheelsToDrivetrain(robot, selection);
            EditorUtility.DisplayDialog(Title,
                added == 0 ? "No new wheels added (already wired, or no renderers found)."
                    : $"Added {added} wheel(s) to the drivetrain.", "OK");
            Repaint();
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog(Title, e.Message, "OK");
        }
    }

    // --- Mechanisms ---

    private void DrawMechanismsSection(RobotMechanisms registry)
    {
        EditorGUILayout.Space();
        List<RobotMechanisms.Mechanism> mechs = registry.mechanisms ?? new List<RobotMechanisms.Mechanism>();
        EditorGUILayout.LabelField($"Mechanisms ({mechs.Count})", EditorStyles.boldLabel);
        if (mechs.Count == 0)
        {
            EditorGUILayout.LabelField("None. Use Add or Fix Mechanism Joint / Auto-Detect Mechanisms.",
                EditorStyles.miniLabel);
            return;
        }

        // Loaded fresh for display; each edit re-loads/saves immediately (see AddBinding/RemoveBinding)
        // so we never hold a stale map across the auto-assign path, which saves its own map.
        ButtonMap map = ControllerMapSettings.Load(registry.robotId);

        foreach (RobotMechanisms.Mechanism m in mechs)
        {
            if (m == null || string.IsNullOrEmpty(m.id)) continue;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string newName = EditorGUILayout.DelayedTextField(m.displayName ?? m.id);
                    if (!string.IsNullOrEmpty(newName) && newName != m.displayName)
                    {
                        // Rename display only — the id is the join key for the button map/catalog.
                        Undo.RecordObject(registry, "Rename Mechanism");
                        m.displayName = newName;
                        EditorUtility.SetDirty(registry);
                        UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, registry.gameObject.name, registry);
                    }
                    GUILayout.Label(m.type == RobotMechanisms.TypePneumatic ? "pneumatic" : "motor",
                        EditorStyles.miniLabel, GUILayout.Width(72));
                    GameObject host = m.motor != null ? m.motor.gameObject
                        : m.pneumatic != null ? m.pneumatic.gameObject : null;
                    using (new EditorGUI.DisabledScope(host == null))
                        if (GUILayout.Button("Select", GUILayout.Width(56)) && host != null)
                            Selection.activeGameObject = host;
                }

                // Reverse: motors flip with MotorActuator.invert. Pneumatics reverse by swapping the
                // two piston targets, which the Add or Fix Mechanism Joint tool handles — omitted here.
                if (m.type == RobotMechanisms.TypeMotor && m.motor != null)
                {
                    bool inv = EditorGUILayout.Toggle(new GUIContent("Reverse direction",
                        "Flip which way this mechanism runs for 'forward' input."), m.motor.invert);
                    if (inv != m.motor.invert)
                    {
                        Undo.RecordObject(m.motor, "Reverse Mechanism");
                        m.motor.invert = inv;
                        EditorUtility.SetDirty(m.motor);
                    }
                }

                DrawMechanismBindings(registry, m, map);
            }
        }
    }

    private void DrawMechanismBindings(RobotMechanisms registry, RobotMechanisms.Mechanism m, ButtonMap map)
    {
        EditorGUILayout.LabelField("Buttons", EditorStyles.miniBoldLabel);

        List<ButtonAssignment> mine = new List<ButtonAssignment>();
        foreach (ButtonAssignment a in map.assignments)
            if (a != null && a.mechanismId == m.id) mine.Add(a);
        if (mine.Count == 0) EditorGUILayout.LabelField("  (none)", EditorStyles.miniLabel);

        foreach (ButtonAssignment a in mine)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"  {a.button} — {ControllerMapSettings.ModeLabel(a.mode)}",
                    GUILayout.Width(220));
                if (GUILayout.Button("x", GUILayout.Width(24)) && Enum.TryParse(a.button, out ControllerButton btn))
                {
                    // Saves to PlayerPrefs directly; the in-pass `mine` list is unaffected, so the
                    // change shows on the next repaint (no ExitGUI needed inside the scroll view).
                    RemoveBinding(registry.robotId, btn, m.id, a.mode);
                    Repaint();
                }
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            ControllerButton pb = pendingButton.TryGetValue(m.id, out ControllerButton vb) ? vb : ControllerButton.R1;
            pb = (ControllerButton)EditorGUILayout.EnumPopup(pb, GUILayout.Width(72));
            pendingButton[m.id] = pb;

            string pm = pendingMode.TryGetValue(m.id, out string vm) ? vm : DefaultMode(map, m);
            pm = DrawModePopup(map, m, pm);
            pendingMode[m.id] = pm;

            if (GUILayout.Button("Add", GUILayout.Width(56)))
            {
                AddBinding(registry.robotId, pb, m.id, pm);
                Repaint();
            }
            if (GUILayout.Button(new GUIContent("Auto", "Assign the next free button(s)"), GUILayout.Width(56)))
            {
                AddMechanismJoint.JointType jt = m.type == RobotMechanisms.TypePneumatic
                    ? AddMechanismJoint.JointType.Prismatic : AddMechanismJoint.JointType.Continuous;
                MechanismAutoDetect.AssignButtons(registry.robotId, m.id, jt);
                Repaint();
            }
        }
    }

    // Offers exactly the functions this mechanism's CONTROL STYLE exposes, so a binding added here
    // can't contradict the style the player picked on the home screen (a one-button motor gets
    // toggle rows, not forward/reverse). Style itself is edited there, not here.
    private static string DrawModePopup(ButtonMap map, RobotMechanisms.Mechanism m, string current)
    {
        string[] modes = ModesFor(map, m);
        if (modes.Length == 1)
        {
            EditorGUILayout.LabelField(ControllerMapSettings.ModeLabel(modes[0]),
                EditorStyles.miniLabel, GUILayout.Width(90));
            return modes[0];
        }
        var labels = new string[modes.Length];
        int idx = 0;
        for (int i = 0; i < modes.Length; i++)
        {
            labels[i] = ControllerMapSettings.ModeLabel(modes[i]);
            if (modes[i] == current) idx = i;
        }
        idx = EditorGUILayout.Popup(idx, labels, GUILayout.Width(90));
        return modes[idx];
    }

    private static string[] ModesFor(ButtonMap map, RobotMechanisms.Mechanism m) =>
        ControllerMapSettings.ModesFor(m.type, ControllerMapSettings.GetStyle(map, m.id, m.type));

    private static string DefaultMode(ButtonMap map, RobotMechanisms.Mechanism m) => ModesFor(map, m)[0];

    // Each mutation loads/saves the map on its own so the auto-assign path (which loads+saves
    // internally) never races a stale in-memory copy held across the frame.
    private static void AddBinding(string robotId, ControllerButton button, string mechanismId, string mode)
    {
        ButtonMap map = ControllerMapSettings.Load(robotId);
        ControllerMapSettings.AddAssignment(map, button, mechanismId, mode);
        ControllerMapSettings.Save(robotId, map);
    }

    private static void RemoveBinding(string robotId, ControllerButton button, string mechanismId, string mode)
    {
        ButtonMap map = ControllerMapSettings.Load(robotId);
        ControllerMapSettings.RemoveAssignment(map, button, mechanismId, mode);
        ControllerMapSettings.Save(robotId, map);
    }

    // --- Chains ---

    private void DrawChainsSection(RobotMechanisms registry)
    {
        EditorGUILayout.Space();
        JointCoupler[] couplers = registry.GetComponentsInChildren<JointCoupler>(true);
        EditorGUILayout.LabelField($"Chains ({couplers.Length})", EditorStyles.boldLabel);
        if (couplers.Length == 0)
        {
            EditorGUILayout.LabelField("None. Use Build Chain to chain sprockets together.",
                EditorStyles.miniLabel);
            return;
        }

        foreach (JointCoupler c in couplers)
        {
            if (c == null) continue;
            using (new EditorGUILayout.HorizontalScope())
            {
                string driverName = c.driver != null ? c.driver.name : "(none)";
                string followerName = c.follower != null ? c.follower.name : c.name;
                string kind = c.mode == JointCoupler.CoupleMode.Velocity ? "spin" : "linkage";
                EditorGUILayout.LabelField($"{driverName} → {followerName}  ({kind}, x{c.ratio:0.##})");
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    GameObject host = c.gameObject;
                    Undo.DestroyObjectImmediate(c); // follower stays a joint, just no longer tracks
                    if (host != null && host.scene.IsValid()) EditorSceneManager.MarkSceneDirty(host.scene);
                    break; // couplers array now stale; stop iterating and repaint fresh next pass
                }
            }
        }
    }

    private static int CountNonNull(ArticulationBody[] arr)
    {
        if (arr == null) return 0;
        int n = 0;
        foreach (ArticulationBody b in arr) if (b != null) n++;
        return n;
    }
}
