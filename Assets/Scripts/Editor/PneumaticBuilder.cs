using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Role-based pneumatics builder (the DR4B builder's sibling). Two modes:
//   • Linear — a cylinder: the ROD (+ anything welded to it) slides out of a stationary BARREL.
//     The rod becomes a prismatic ArticulationBody link driven by a PneumaticActuator between
//     0 and the stroke — a real physical joint, so the rod visibly extends AND can push things
//     (force-capped by the actuator's cylinderForce, like real air pressure).
//   • Rotary — a piston-driven pivot (doinker/flipper/wing): the part becomes a revolute link
//     snapped between two angles by the same actuator.
//
// Rebuilds are IN PLACE: the window lists every pneumatic already on the robot (via the
// PneumaticRig record each build leaves on its link); Edit re-opens one into the form, Build
// replaces its joint/actuator/registration/binding for the same link, and Delete strips it —
// no manual clean step between edits, and multiple pneumatics coexist.
//
// Usage: set up the robot first (Set Up Imported Robot), then
// Tools > RoboSim > Robot > Mechanisms > Build Pneumatic (roles). Save to the PREFAB afterward.
public class PneumaticBuilderWindow : EditorWindow
{
    private const string Title = "Build Pneumatic (roles)";

    private static readonly string[] ModeLabels =
    {
        "Linear piston (rod slides out of a barrel)",
        "Rotary piston (part swings between two angles)",
    };

    [SerializeField] private PneumaticRig.RigMode mode = PneumaticRig.RigMode.Linear;
    [SerializeField] private GameObject movingPart;   // Linear: the rod. Rotary: the rotating part.
    [SerializeField] private GameObject barrel;       // Linear only
    [SerializeField] private List<GameObject> alsoMove = new List<GameObject>();
    [SerializeField] private float strokeMm = 50f;
    [SerializeField] private float retractedDeg = 0f;
    [SerializeField] private float extendedDeg = 90f;
    [SerializeField] private PneumaticRig.RigAxis axisPreset = PneumaticRig.RigAxis.Auto;
    [SerializeField] private Vector3 customAxis = Vector3.right;
    [SerializeField] private Vector3 anchor = Vector3.zero;
    [SerializeField] private Transform pivotMarker;
    [SerializeField] private bool reverse;
    [SerializeField] private bool startExtended;
    [SerializeField] private bool autoAssignButton = true;
    [SerializeField] private string displayName = "Pneumatic";
    [SerializeField] private Vector2 scroll;

    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Build Pneumatic (roles)", false, 23)]
    private static void ShowWindow()
    {
        PneumaticBuilderWindow w = GetWindow<PneumaticBuilderWindow>(Title);
        w.minSize = new Vector2(460f, 560f);
        w.Show();
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.HelpBox(
            "Build a pneumatic on a set-up robot. Linear: drag the ROD (the part that extends) and its " +
            "stationary BARREL in; the rod becomes a real sliding joint that pushes with air-limited " +
            "force. Rotary: drag the swinging part in and set its two angles (doinker/flipper).\n\n" +
            "Rebuilding UPDATES the same pneumatic in place — use the list below to edit or delete " +
            "existing ones. No clean step needed.", MessageType.Info);

        RobotMechanisms registry = ResolveRegistry();
        DrawExistingRigs(registry);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Pneumatic", EditorStyles.boldLabel);
        mode = (PneumaticRig.RigMode)EditorGUILayout.Popup(new GUIContent("Mode",
            "Linear = a cylinder whose rod slides out. Rotary = a piston-driven pivot that snaps " +
            "between two angles."), (int)mode, ModeLabels);

        bool linear = mode == PneumaticRig.RigMode.Linear;
        movingPart = (GameObject)EditorGUILayout.ObjectField(new GUIContent(
            linear ? "Rod (extends)" : "Rotating part",
            linear ? "The ONE part that slides out (plus anything in 'Moves with it'). Not the barrel."
                   : "The ONE part that swings (plus anything in 'Moves with it')."),
            movingPart, typeof(GameObject), true);
        if (linear)
            barrel = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Barrel (stationary)",
                "The cylinder body the rod slides out of. Stays welded to the chassis; also gives the " +
                "Auto axis its direction (barrel center → rod center)."), barrel, typeof(GameObject), true);

        if (GUILayout.Button("Auto-fill from names (best guess)", GUILayout.Width(220))) AutoFillByName(registry);

        EditorGUILayout.LabelField(new GUIContent("Moves with it",
            "Extra parts welded into the moving link (a clamp bolted to the rod, the doinker's hook). " +
            "Leave the barrel/motor housing out."), EditorStyles.miniBoldLabel);
        for (int i = 0; i < alsoMove.Count; i++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                alsoMove[i] = (GameObject)EditorGUILayout.ObjectField(alsoMove[i], typeof(GameObject), true);
                if (GUILayout.Button("X", GUILayout.Width(24))) { alsoMove.RemoveAt(i); i--; }
            }
        }
        if (GUILayout.Button("Add Part", GUILayout.Width(100))) alsoMove.Add(null);

        EditorGUILayout.Space();
        if (linear)
        {
            strokeMm = EditorGUILayout.FloatField(new GUIContent("Stroke (mm)",
                "How far the rod extends. A real VEX cylinder is ~50 mm."), strokeMm);
            EditorGUILayout.LabelField(" ", $"= {strokeMm / PneumaticSetup.MmPerUnit:0.###} world units ({strokeMm / 1000f:0.###} m)");
            if (strokeMm > 1000f)
                EditorGUILayout.HelpBox("That stroke is over 1 m — a real cylinder is a few cm. A big number " +
                    "here launches the part across the field (the old 654V bug). Millimeters, not units.", MessageType.Warning);
        }
        else
        {
            retractedDeg = EditorGUILayout.FloatField(new GUIContent("Retracted angle (deg)",
                "Joint angle at rest (piston retracted)."), retractedDeg);
            extendedDeg = EditorGUILayout.FloatField(new GUIContent("Extended angle (deg)",
                "Joint angle when fired (piston extended)."), extendedDeg);
            pivotMarker = (Transform)EditorGUILayout.ObjectField(new GUIContent("Pivot marker (optional)",
                "Drag a child empty sitting exactly on the hinge point — it overrides the anchor below."),
                pivotMarker, typeof(Transform), true);
        }

        axisPreset = (PneumaticRig.RigAxis)EditorGUILayout.EnumPopup(new GUIContent(
            linear ? "Slide axis (link-local)" : "Hinge axis (link-local)",
            "Auto guesses from geometry (Linear prefers barrel→rod direction when a barrel is set). " +
            "X/Y/Z/Custom set it by hand."), axisPreset);
        if (axisPreset == PneumaticRig.RigAxis.Custom)
            customAxis = EditorGUILayout.Vector3Field("Custom axis", customAxis);
        if (axisPreset != PneumaticRig.RigAxis.Auto && (linear || pivotMarker == null))
            anchor = EditorGUILayout.Vector3Field(new GUIContent("Anchor (link-local)",
                "Pivot/slide origin in the link's local space. 0 = the link origin (the usual case)."), anchor);

        reverse = EditorGUILayout.Toggle(new GUIContent("Reverse direction",
            "Flip if it starts at the wrong end (swaps the two endpoints)."), reverse);
        startExtended = EditorGUILayout.Toggle(new GUIContent("Start extended",
            "Begin the match with the piston extended instead of retracted."), startExtended);
        autoAssignButton = EditorGUILayout.Toggle(new GUIContent("Auto-assign button",
            "Map this pneumatic to the next free controller button as a toggle."), autoAssignButton);
        displayName = EditorGUILayout.TextField(new GUIContent("Name (config label)",
            "What the Configure Controller screen shows for this pneumatic."), displayName);

        EditorGUILayout.Space();
        if (GUILayout.Button("Build / Update Pneumatic", GUILayout.Height(32)))
        {
            try
            {
                string report = PneumaticSetup.Build(BuildOptions(), useUndo: true);
                EditorUtility.DisplayDialog(Title, report, "OK");
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog(Title, e.Message, "OK");
                Debug.LogException(e);
            }
        }

        EditorGUILayout.EndScrollView();
    }

    // The already-built pneumatics on this robot, each editable/deletable in place.
    private void DrawExistingRigs(RobotMechanisms registry)
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Existing pneumatics on this robot", EditorStyles.boldLabel);
        if (registry == null)
        {
            EditorGUILayout.HelpBox("Select the robot (or drag one of its parts into a slot) to list " +
                                    "and edit its pneumatics.", MessageType.None);
            return;
        }

        PneumaticRig[] rigs = registry.GetComponentsInChildren<PneumaticRig>(true);
        if (rigs.Length == 0)
        {
            EditorGUILayout.LabelField("(none yet)", EditorStyles.miniLabel);
            return;
        }

        foreach (PneumaticRig rig in rigs)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"• {rig.displayName}  ({rig.mode}, on '{rig.name}')");
                if (GUILayout.Button("Edit", GUILayout.Width(50))) LoadRig(rig);
                if (GUILayout.Button("Delete", GUILayout.Width(60)))
                {
                    if (EditorUtility.DisplayDialog(Title,
                        $"Delete the pneumatic '{rig.displayName}' on '{rig.name}'? The part becomes a " +
                        "plain welded mesh again (its geometry is kept).", "Delete", "Cancel"))
                    {
                        string report = PneumaticSetup.Strip(registry, rig, useUndo: true);
                        EditorUtility.DisplayDialog(Title, report, "OK");
                        GUIUtility.ExitGUI(); // the rigs array just changed under this loop
                    }
                }
            }
        }
        if (GUILayout.Button("New Pneumatic (clear form)", GUILayout.Width(200))) ClearForm();
    }

    private void LoadRig(PneumaticRig rig)
    {
        mode = rig.mode;
        movingPart = rig.gameObject;
        barrel = rig.barrel;
        alsoMove = new List<GameObject>(rig.alsoMove ?? new List<GameObject>());
        strokeMm = rig.strokeMm;
        retractedDeg = rig.retractedDeg;
        extendedDeg = rig.extendedDeg;
        axisPreset = rig.axisPreset;
        customAxis = rig.customAxis;
        anchor = rig.anchor;
        pivotMarker = rig.pivotMarker;
        reverse = rig.reverse;
        startExtended = rig.startExtended;
        autoAssignButton = rig.autoAssignButton;
        displayName = rig.displayName;
        Repaint();
    }

    private void ClearForm()
    {
        mode = PneumaticRig.RigMode.Linear;
        movingPart = null; barrel = null; pivotMarker = null;
        alsoMove.Clear();
        strokeMm = 50f; retractedDeg = 0f; extendedDeg = 90f;
        axisPreset = PneumaticRig.RigAxis.Auto; customAxis = Vector3.right; anchor = Vector3.zero;
        reverse = false; startExtended = false; autoAssignButton = true;
        displayName = "Pneumatic";
        Repaint();
    }

    // Fill empty rod/barrel slots from name tokens under the robot — a best guess to verify.
    private void AutoFillByName(RobotMechanisms registry)
    {
        if (registry == null)
        {
            EditorUtility.DisplayDialog(Title, "Select the robot (or a part of it) in the Hierarchy first.", "OK");
            return;
        }
        string[] rodTokens = { "rod", "piston", "ram", "plunger" };
        string[] barrelTokens = { "cylinder", "barrel", "solenoid", "pneumatic", "tube" };
        foreach (Transform t in registry.GetComponentsInChildren<Transform>(true))
        {
            if (t.GetComponent<ArticulationBody>() != null) continue;                 // already rigged
            if (t.GetComponentsInChildren<Renderer>(true).Length == 0) continue;      // nothing to move
            string n = Normalize(t.name);
            if (movingPart == null && Matches(n, rodTokens)) { movingPart = t.gameObject; continue; }
            if (barrel == null && Matches(n, barrelTokens)) barrel = t.gameObject;
        }
        Repaint();
    }

    private static bool Matches(string normalized, string[] tokens)
    {
        foreach (string token in tokens) if (normalized.Contains(token)) return true;
        return false;
    }

    private static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private RobotMechanisms ResolveRegistry()
    {
        foreach (GameObject g in new[] { movingPart, barrel })
            if (g != null)
            {
                RobotMechanisms r = g.GetComponentInParent<RobotMechanisms>();
                if (r != null) return r;
            }
        if (Selection.activeGameObject != null)
            return Selection.activeGameObject.GetComponentInParent<RobotMechanisms>();
        return null;
    }

    private PneumaticSetup.Options BuildOptions() => new PneumaticSetup.Options
    {
        link = movingPart,
        mode = mode,
        barrel = barrel,
        alsoMove = alsoMove,
        strokeMm = strokeMm,
        retractedDeg = retractedDeg,
        extendedDeg = extendedDeg,
        axisPreset = axisPreset,
        customAxis = customAxis,
        anchor = anchor,
        pivotMarker = pivotMarker,
        reverse = reverse,
        startExtended = startExtended,
        autoAssignButton = autoAssignButton,
        displayName = displayName,
    };
}

// Headless-runnable core (window/core split like Dr4bLiftSetup).
public static class PneumaticSetup
{
    private const string UndoName = "Build Pneumatic";
    public const float MmPerUnit = 100f; // 1 world unit = 0.1 m = 100 mm

    public struct Options
    {
        public GameObject link;              // the rod (Linear) or the rotating part (Rotary)
        public PneumaticRig.RigMode mode;
        public GameObject barrel;            // Linear only; validation + Auto axis
        public List<GameObject> alsoMove;
        public float strokeMm;               // Linear
        public float retractedDeg, extendedDeg; // Rotary
        public PneumaticRig.RigAxis axisPreset;
        public Vector3 customAxis;
        public Vector3 anchor;
        public Transform pivotMarker;        // Rotary anchor override
        public bool reverse;
        public bool startExtended;
        public bool autoAssignButton;
        public string displayName;
    }

    // Build or UPDATE (same link → replaced in place; AddMechanismJoint.Apply retypes the existing
    // joint, WireMechanism strips the stale actuator, RegisterMechanism replaces by id — so no
    // manual clean step exists in this workflow). Throws on any precondition failure.
    public static string Build(Options o, bool useUndo)
    {
        if (o.link == null)
            throw new InvalidOperationException("Assign the moving part (the rod / the rotating part) first.");
        RobotMechanisms registry = o.link.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
            throw new InvalidOperationException(
                $"'{o.link.name}' is not under a set-up robot (no RobotMechanisms). Run Set Up Imported Robot first.");

        bool linear = o.mode == PneumaticRig.RigMode.Linear;
        if (linear)
        {
            if (o.strokeMm <= 0f)
                throw new InvalidOperationException("Stroke must be positive (millimeters — a VEX cylinder is ~50).");
            if (o.barrel != null && (o.barrel == o.link || o.barrel.transform.IsChildOf(o.link.transform)))
                throw new InvalidOperationException(
                    $"The barrel '{o.barrel.name}' is (inside) the rod '{o.link.name}' — it would slide out with it. " +
                    "The barrel is the STATIONARY body; pick the rod as the moving part.");
        }
        else if (Mathf.Abs(o.extendedDeg - o.retractedDeg) < 1e-3f)
        {
            throw new InvalidOperationException("Retracted and extended angles are the same — nothing would move.");
        }

        // Mechanism ids come from the link's name; a second link slugging to the same id would
        // silently replace the first mechanism's registration.
        string id = UrdfPostProcessor.Slugify(o.link.name);
        RobotMechanisms.Mechanism clash = registry.Find(id);
        GameObject clashHolder = clash == null ? null
            : clash.motor != null ? clash.motor.gameObject
            : clash.pneumatic != null ? clash.pneumatic.gameObject : null;
        if (clashHolder != null && clashHolder != o.link)
            throw new InvalidOperationException(
                $"Another mechanism already uses the id '{id}' (on '{clashHolder.name}'). Rename " +
                $"'{o.link.name}' so the ids stay unique, then Build again.");

        int group = 0;
        if (useUndo)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            group = Undo.GetCurrentGroup();
        }

        // Axis / anchor / endpoints per mode.
        ResolveAxisAnchor(o, linear, out Vector3 axis, out Vector3 jointAnchor);
        float lower = linear ? 0f : o.retractedDeg;
        float upper = linear ? o.strokeMm / MmPerUnit : o.extendedDeg;

        // The one shared joint core: splits/retypes the link, folds alsoMove in, wires the
        // PneumaticActuator (Toggle) with the limits as its endpoints, registers it in the
        // registry + catalog. Idempotent per link.
        AddMechanismJoint.Apply(o.link, linear ? AddMechanismJoint.JointType.Prismatic : AddMechanismJoint.JointType.Revolute,
            axis, jointAnchor, lower, upper,
            new AddMechanismJoint.Options
            {
                alsoMove = (o.alsoMove != null && o.alsoMove.Count > 0) ? o.alsoMove.ToArray() : null,
                reverseDirection = o.reverse,
                actuation = AddMechanismJoint.Actuation.Toggle,
            },
            useUndo);

        // Display name + start pose on the freshly-wired actuator.
        RobotMechanisms.Mechanism mech = registry.Find(id);
        if (mech != null && mech.pneumatic != null)
        {
            if (useUndo) Undo.RecordObject(mech.pneumatic, UndoName);
            mech.pneumatic.startExtended = o.startExtended;
            if (o.startExtended)
            {
                ArticulationBody body = o.link.GetComponent<ArticulationBody>();
                ArticulationDrive d = body.xDrive;
                d.target = mech.pneumatic.extendedTarget;
                body.xDrive = d;
            }
            mech.displayName = string.IsNullOrEmpty(o.displayName) ? "Pneumatic" : o.displayName;
            UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, registry.gameObject.name, registry);
            AssetDatabase.SaveAssets(); // flush the catalog so Configure Controller shows the new name
        }

        // Buttons: always re-cleared (so an edit never stacks), re-assigned as a single toggle —
        // Prismatic forces the toggle button style for BOTH modes, same trick the Add/Fix tool
        // uses for its rotating piston. ButtonRouter then fires it with zero changes.
        MechanismBuildUtil.ClearMechanismBindings(registry.robotId, id);
        string buttonNote = "kept clear (assign in Configure Controller)";
        if (o.autoAssignButton)
            buttonNote = MechanismAutoDetect.AssignButtons(registry.robotId, id, AddMechanismJoint.JointType.Prismatic);

        // The rig record makes this pneumatic listable/editable next time the window opens.
        PneumaticRig rig = MechanismBuildUtil.AddOrGet<PneumaticRig>(o.link, useUndo);
        if (useUndo) Undo.RecordObject(rig, UndoName);
        rig.mode = o.mode;
        rig.displayName = mech != null ? mech.displayName : o.displayName;
        rig.barrel = o.barrel;
        rig.alsoMove = o.alsoMove != null ? new List<GameObject>(o.alsoMove) : new List<GameObject>();
        rig.strokeMm = o.strokeMm;
        rig.retractedDeg = o.retractedDeg;
        rig.extendedDeg = o.extendedDeg;
        rig.axisPreset = o.axisPreset;
        rig.customAxis = o.customAxis;
        rig.anchor = o.anchor;
        rig.pivotMarker = o.pivotMarker;
        rig.reverse = o.reverse;
        rig.startExtended = o.startExtended;
        rig.autoAssignButton = o.autoAssignButton;

        if (useUndo) Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(registry);
        EditorUtility.SetDirty(rig);
        if (registry.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(registry.gameObject.scene);

        return
            $"Built the {(linear ? "linear" : "rotary")} pneumatic '{rig.displayName}' on '{o.link.name}'.\n\n" +
            (linear
                ? $"• Rod slides 0 → {o.strokeMm} mm ({o.strokeMm / MmPerUnit:0.###} units){(o.barrel != null ? $"; barrel '{o.barrel.name}' stays put" : "")}.\n"
                : $"• Swings {o.retractedDeg}° → {o.extendedDeg}°.\n") +
            $"• Button: {buttonNote}. Press toggles extend/retract; force is air-capped (cylinderForce).\n" +
            "• Rebuild any time — this window's Edit updates it in place.\n\n" +
            "IMPORTANT: the field spawns the robot PREFAB at Play, not this scene object. APPLY THESE " +
            "CHANGES TO THE PREFAB (Prefab Mode, or Overrides > Apply All) or they won't spawn.";
    }

    // Delete one built pneumatic: mechanism + bindings + actuator gone, the link's ArticulationBody
    // removed (its colliders re-weld to the chassis), the rig record removed. Geometry is untouched;
    // alsoMove parts stay where the build reparented them (they're plain meshes on the same weld).
    public static string Strip(RobotMechanisms registry, PneumaticRig rig, bool useUndo)
    {
        if (registry == null || rig == null)
            throw new InvalidOperationException("No robot/pneumatic to delete.");
        GameObject link = rig.gameObject;
        string id = UrdfPostProcessor.Slugify(link.name);

        int group = 0;
        if (useUndo)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Delete Pneumatic");
            group = Undo.GetCurrentGroup();
        }

        UrdfPostProcessor.RemoveMechanism(registry, id, useUndo); // destroys the actuator too
        MechanismBuildUtil.ClearMechanismBindings(registry.robotId, id);

        ArticulationBody body = link.GetComponent<ArticulationBody>();
        if (body != null && !body.isRoot && !IsProtected(body, registry))
        {
            if (useUndo) Undo.DestroyObjectImmediate(body);
            else UnityEngine.Object.DestroyImmediate(body);
        }

        string label = rig.displayName;
        if (useUndo) Undo.DestroyObjectImmediate(rig);
        else UnityEngine.Object.DestroyImmediate(rig);

        UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, registry.gameObject.name, registry);
        AssetDatabase.SaveAssets();
        if (useUndo) Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(registry);
        if (registry.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(registry.gameObject.scene);

        return $"Deleted the pneumatic '{label}' on '{link.name}' — the part is a plain welded mesh " +
               "again. Remember to apply the change to the PREFAB.";
    }

    // Never strip the drivetrain or another mechanism's body out from under it.
    private static bool IsProtected(ArticulationBody body, RobotMechanisms registry)
    {
        RobotMotorController mc = registry.GetComponent<RobotMotorController>();
        if (mc != null)
        {
            if (mc.leftWheels != null && Array.IndexOf(mc.leftWheels, body) >= 0) return true;
            if (mc.rightWheels != null && Array.IndexOf(mc.rightWheels, body) >= 0) return true;
        }
        foreach (RobotMechanisms.Mechanism m in registry.mechanisms)
        {
            if (m == null) continue;
            if (m.motor != null && m.motor.gameObject == body.gameObject) return true;
            if (m.pneumatic != null && m.pneumatic.gameObject == body.gameObject) return true;
        }
        return false;
    }

    private static void ResolveAxisAnchor(Options o, bool linear, out Vector3 axis, out Vector3 anchor)
    {
        axis = Vector3.right;
        anchor = o.anchor;

        switch (o.axisPreset)
        {
            case PneumaticRig.RigAxis.X: axis = Vector3.right; break;
            case PneumaticRig.RigAxis.Y: axis = Vector3.up; break;
            case PneumaticRig.RigAxis.Z: axis = Vector3.forward; break;
            case PneumaticRig.RigAxis.Custom:
                axis = o.customAxis.sqrMagnitude > 1e-8f ? o.customAxis.normalized : Vector3.right;
                break;
            case PneumaticRig.RigAxis.Auto:
                if (linear && o.barrel != null &&
                    TryBoundsCenter(o.link, out Vector3 rodCenter) && TryBoundsCenter(o.barrel, out Vector3 barrelCenter) &&
                    (rodCenter - barrelCenter).sqrMagnitude > 1e-8f)
                {
                    // The rod extends AWAY from the barrel — much better than the generic
                    // longest-axis guess when a barrel is assigned.
                    Vector3 worldAxis = (rodCenter - barrelCenter).normalized;
                    axis = o.link.transform.InverseTransformDirection(worldAxis).normalized;
                    anchor = Vector3.zero;
                }
                else
                {
                    MechanismAutoDetect.TryInferAxisAnchor(o.link,
                        linear ? AddMechanismJoint.JointType.Prismatic : AddMechanismJoint.JointType.Revolute,
                        out axis, out anchor);
                }
                break;
        }

        // A pivot marker pinpoints the rotary hinge regardless of how the axis was chosen.
        if (!linear && o.pivotMarker != null)
            anchor = o.link.transform.InverseTransformPoint(o.pivotMarker.position);
    }

    private static bool TryBoundsCenter(GameObject go, out Vector3 center)
    {
        center = Vector3.zero;
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return false;
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        center = b.center;
        return true;
    }
}
