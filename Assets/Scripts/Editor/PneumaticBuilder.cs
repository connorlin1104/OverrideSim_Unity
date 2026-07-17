using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Role-based pneumatics builder (the DR4B builder's sibling). Two modes:
//   • Linear — a cylinder: the ROD (+ anything welded to it) slides out of a stationary BARREL.
//     The rod becomes a prismatic ArticulationBody link driven by a PneumaticActuator between
//     0 and the stroke — a real physical joint, so the rod visibly extends AND can push things.
//     The drive force is uncapped, so the rod always reaches its endpoint (no air-pressure stall).
//   • Rotary — a piston-driven pivot (doinker/flipper/wing). The MOVING METAL becomes a revolute
//     link snapped between two angles. Optionally name the CYLINDER body too: it stays on the
//     chassis and cosmetically swivels about its own mount to stay aimed at the metal (two pivots —
//     one for the metal's hinge, one for the cylinder's mount) so it reads as the piston driving it.
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

    // The three real VEX cylinder bore/stroke classes, plus a free-form escape hatch.
    private enum CylinderSize { Large90, Regular50, Small20, Custom }
    private static readonly string[] CylinderSizeLabels =
    {
        "Large (90 mm)", "Regular (50 mm)", "Small (20 mm)", "Custom (mm)",
    };

    [SerializeField] private PneumaticRig.RigMode mode = PneumaticRig.RigMode.Linear;
    [SerializeField] private GameObject movingPart;   // Linear: the rod. Rotary: the rotating part.
    [SerializeField] private GameObject barrel;       // Linear only
    [SerializeField] private List<GameObject> alsoMove = new List<GameObject>();
    [SerializeField] private CylinderSize cylinderSize = CylinderSize.Large90;
    [SerializeField] private float strokeMm = 90f;
    [SerializeField] private float retractedDeg = 0f;
    [SerializeField] private float extendedDeg = 90f;
    [SerializeField] private PneumaticRig.RigAxis axisPreset = PneumaticRig.RigAxis.Auto;
    [SerializeField] private Vector3 customAxis = Vector3.right;
    [SerializeField] private Vector3 anchor = Vector3.zero;
    [SerializeField] private Transform pivotMarker;
    [SerializeField] private GameObject pneumaticBody;   // Rotary: optional cosmetic cylinder body
    [SerializeField] private GameObject cylinderRod;     // Rotary: optional cosmetic cylinder rod
    [SerializeField] private Transform pneumaticPivot;   // Rotary: optional cylinder mount
    [SerializeField] private bool flipRod;               // Rotary: flip the cosmetic rod's slide direction
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
            "Build a pneumatic on a set-up robot.\n\n" +
            "LINEAR: drag the ROD (extends) and its stationary BARREL in; the rod becomes a real sliding " +
            "joint that pushes things. It always reaches its endpoint (uncapped force).\n\n" +
            "ROTARY (doinker/flipper): the MOVING METAL is the real joint — it swings about its Metal pivot " +
            "between the two angles you set. Optionally add the CYLINDER BODY + ROD and a Pneumatic pivot: " +
            "they cosmetically follow, so the piston looks like it's driving the metal. The two pivots define " +
            "the motion; the angles are your manual knob to make it look right.\n\n" +
            "Rebuilding UPDATES the same pneumatic in place — use the list below to edit or delete existing " +
            "ones. No clean step needed. Apply to the PREFAB afterward.", MessageType.Info);

        RobotMechanisms registry = ResolveRegistry();
        DrawExistingRigs(registry);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Pneumatic", EditorStyles.boldLabel);
        mode = (PneumaticRig.RigMode)EditorGUILayout.Popup(new GUIContent("Mode",
            "Linear = a cylinder whose rod slides out. Rotary = a piston-driven pivot that snaps " +
            "between two angles."), (int)mode, ModeLabels);

        bool linear = mode == PneumaticRig.RigMode.Linear;
        movingPart = (GameObject)EditorGUILayout.ObjectField(new GUIContent(
            linear ? "1. Rod (extends)" : "1. Moving metal part (swings)",
            linear ? "The ONE part that slides out (plus anything in 'Moves with it'). Not the barrel."
                   : "Your MetalPart — the metal that swings about the Metal pivot. This is the ONE real " +
                     "moving joint; the cylinder body/rod just follow it. Add extra co-moving parts under 'Moves with it'."),
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
            DrawCylinderSize("Cylinder size (stroke)",
                "How far the rod slides out. Pick the real cylinder class on this model — VEX cylinders come " +
                "in Large (90 mm), Regular (50 mm) and Small (20 mm); Custom for anything else.");
        }
        else
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Swing angles (your manual knob)", EditorStyles.boldLabel);
            retractedDeg = EditorGUILayout.FloatField(new GUIContent("Retracted angle (deg)",
                "The metal's angle at REST (piston in). Usually 0."), retractedDeg);
            extendedDeg = EditorGUILayout.FloatField(new GUIContent("Extended angle (deg)",
                "The metal's angle when FIRED (piston out). Tune this so the swing matches the real doinker — " +
                "the cylinder body/rod follow automatically, so this one number sets the whole look."), extendedDeg);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Pivot points (define the motion)", EditorStyles.boldLabel);
            pivotMarker = (Transform)EditorGUILayout.ObjectField(new GUIContent("2. Metal pivot point",
                "Your MetalPivotPoint — a child empty/spacer sitting exactly on the hinge the metal swings " +
                "about (same idea as the DR4B pivot spacer). This is the joint's pivot."),
                pivotMarker, typeof(Transform), true);
            pneumaticPivot = (Transform)EditorGUILayout.ObjectField(new GUIContent("3. Pneumatic pivot",
                "Your Pneumatic Pivot — the empty where the cylinder is PINNED to the chassis. The cylinder " +
                "body swivels about this point and the rod slides through it as the metal moves."),
                pneumaticPivot, typeof(Transform), true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(new GUIContent("Cylinder parts (cosmetic — they follow the metal)",
                "The body swivels about the Pneumatic pivot to stay aimed at the metal; the rod swivels with it " +
                "AND slides out to reach the metal (the visible stroke). Neither gets a joint — they're posed " +
                "each frame off the two pivots. Leave both empty to just swing the metal with no cylinder motion."),
                EditorStyles.boldLabel);
            pneumaticBody = (GameObject)EditorGUILayout.ObjectField(new GUIContent("4. Cylinder body",
                "Your CylinderBody — the barrel. Stays pinned at the Pneumatic pivot and only swivels."),
                pneumaticBody, typeof(GameObject), true);
            cylinderRod = (GameObject)EditorGUILayout.ObjectField(new GUIContent("5. Cylinder rod",
                "Your CylinderRod — the part that extends. Swivels with the body and slides out along the " +
                "cylinder axis as the metal swings."),
                cylinderRod, typeof(GameObject), true);
            if (cylinderRod != null)
            {
                DrawCylinderSize("Cylinder size (rod travel)",
                    "How far the rod slides out at full swing. Pick the real cylinder class on this model — " +
                    "VEX cylinders are Large (90 mm), Regular (50 mm) or Small (20 mm); Custom for anything else. " +
                    "The rod extends 0 → this over the retracted→extended swing.");
                flipRod = EditorGUILayout.Toggle(new GUIContent("Flip rod direction",
                    "If the rod slides IN when the metal fires (extended looks retracted) and OUT when it rests, " +
                    "tick this to invert the rod's slide. Depends on how the cylinder CAD is modeled — cosmetic only."),
                    flipRod);
            }
        }

        axisPreset = (PneumaticRig.RigAxis)EditorGUILayout.EnumPopup(new GUIContent(
            linear ? "Slide axis (link-local)" : "Hinge axis (link-local)",
            linear
                ? "Auto prefers the barrel→rod direction when a barrel is set. X/Y/Z/Custom set it by hand."
                : "Auto uses the drivetrain's left↔right (lateral) axis — the same hinge axis the DR4B arms " +
                  "use, so a doinker swings up/over instead of sideways. X/Y/Z/Custom set it by hand."),
            axisPreset);
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
        cylinderSize = SizeFromStroke(rig.strokeMm);
        retractedDeg = rig.retractedDeg;
        extendedDeg = rig.extendedDeg;
        axisPreset = rig.axisPreset;
        customAxis = rig.customAxis;
        anchor = rig.anchor;
        pivotMarker = rig.pivotMarker;
        pneumaticBody = rig.pneumaticBody;
        cylinderRod = rig.cylinderRod;
        pneumaticPivot = rig.pneumaticPivot;
        flipRod = rig.flipRod;
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
        pneumaticBody = null; cylinderRod = null; pneumaticPivot = null; flipRod = false;
        alsoMove.Clear();
        strokeMm = 90f; cylinderSize = CylinderSize.Large90; retractedDeg = 0f; extendedDeg = 90f;
        axisPreset = PneumaticRig.RigAxis.Auto; customAxis = Vector3.right; anchor = Vector3.zero;
        reverse = false; startExtended = false; autoAssignButton = true;
        displayName = "Pneumatic";
        Repaint();
    }

    // Cylinder-size picker: the preset classes lock the stroke; Custom exposes a free-form mm field.
    // Shared by both modes — Linear uses it as the joint stroke, Rotary as the cosmetic rod travel.
    private void DrawCylinderSize(string label, string tip)
    {
        cylinderSize = (CylinderSize)EditorGUILayout.Popup(new GUIContent(label, tip), (int)cylinderSize, CylinderSizeLabels);
        switch (cylinderSize)
        {
            case CylinderSize.Large90:   strokeMm = 90f; break;
            case CylinderSize.Regular50: strokeMm = 50f; break;
            case CylinderSize.Small20:   strokeMm = 20f; break;
            case CylinderSize.Custom:
                strokeMm = EditorGUILayout.FloatField(new GUIContent("Custom stroke (mm)",
                    "How far the rod extends, in millimeters."), strokeMm);
                break;
        }
        EditorGUILayout.LabelField(" ", $"= {strokeMm / PneumaticSetup.MmPerUnit:0.###} world units ({strokeMm / 1000f:0.###} m)");
        if (strokeMm > 1000f)
            EditorGUILayout.HelpBox("Over 1 m — a real cylinder is a few cm. A big number here launches the " +
                "part across the field (the old 654V bug). Millimeters, not units.", MessageType.Warning);
    }

    private static CylinderSize SizeFromStroke(float mm)
        => Mathf.Approximately(mm, 90f) ? CylinderSize.Large90
         : Mathf.Approximately(mm, 50f) ? CylinderSize.Regular50
         : Mathf.Approximately(mm, 20f) ? CylinderSize.Small20
         : CylinderSize.Custom;

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
        pneumaticBody = pneumaticBody,
        cylinderRod = cylinderRod,
        pneumaticPivot = pneumaticPivot,
        flipRod = flipRod,
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
        public Transform pivotMarker;        // Rotary: the moving metal's hinge (anchor override)
        public GameObject pneumaticBody;     // Rotary: optional cosmetic cylinder body (swivels)
        public GameObject cylinderRod;       // Rotary: optional cosmetic cylinder rod (swivels + extends)
        public Transform pneumaticPivot;     // Rotary: optional cylinder mount (its swivel pivot)
        public bool flipRod;                 // Rotary: flip the cosmetic rod's slide direction
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

        // Rotary cosmetic cylinder parts (optional): each must be a separate chassis mesh, not the metal.
        if (!linear)
        {
            ValidateCosmetic(o.pneumaticBody, o.link, "cylinder body");
            ValidateCosmetic(o.cylinderRod, o.link, "cylinder rod");
            if (o.pneumaticBody != null && o.cylinderRod == o.pneumaticBody)
                throw new InvalidOperationException("The cylinder body and cylinder rod are the same object — " +
                    "pick the barrel as the body and the extending part as the rod.");
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

        // Renaming the link between edits changes its id — sweep any OTHER mechanism registered on
        // this same link (its actuator is about to be replaced anyway), or the old id's registry
        // row and button bindings would dangle forever.
        foreach (RobotMechanisms.Mechanism stale in registry.mechanisms.ToArray())
        {
            if (stale == null || stale.id == id) continue;
            GameObject holder = stale.motor != null ? stale.motor.gameObject
                : stale.pneumatic != null ? stale.pneumatic.gameObject : null;
            if (holder != o.link) continue;
            UrdfPostProcessor.RemoveMechanism(registry, stale.id, useUndo);
            MechanismBuildUtil.ClearMechanismBindings(registry.robotId, stale.id);
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

        // Keep the moving link from colliding with the rest of its own robot. Sibling ArticulationBody
        // links collide, so a rough-CAD rod/doinker that clips a drivetrain wheel or the chassis would
        // otherwise push the bot around (the "creeps backward while retracted" bug). It still hits pieces.
        MechanismBuildUtil.AddOrGet<IgnoreRobotSelfCollision>(o.link, useUndo);

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

        // Clear cosmetic cylinder parts from a PRIOR build (rig fields still hold the old ones here)
        // — covers editing the cylinder, or switching a rotary build to linear.
        RemoveCylinderFollower(rig.pneumaticBody, useUndo);
        RemoveCylinderFollower(rig.cylinderRod, useUndo);

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
        rig.pneumaticBody = linear ? null : o.pneumaticBody;
        rig.cylinderRod = linear ? null : o.cylinderRod;
        rig.pneumaticPivot = linear ? null : o.pneumaticPivot;
        rig.flipRod = o.flipRod;
        rig.reverse = o.reverse;
        rig.startExtended = o.startExtended;
        rig.autoAssignButton = o.autoAssignButton;

        // Rotary: make the cylinder body/rod follow the moving metal off the two pivots (cosmetic).
        if (!linear && (o.pneumaticBody != null || o.cylinderRod != null))
            WireRotaryCylinder(o, registry, useUndo);

        if (useUndo) Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(registry);
        EditorUtility.SetDirty(rig);
        if (registry.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(registry.gameObject.scene);

        return
            $"Built the {(linear ? "linear" : "rotary")} pneumatic '{rig.displayName}' on '{o.link.name}'.\n\n" +
            (linear
                ? $"• Rod slides 0 → {o.strokeMm} mm ({o.strokeMm / MmPerUnit:0.###} units){(o.barrel != null ? $"; barrel '{o.barrel.name}' stays put" : "")}.\n"
                : $"• Metal swings {o.retractedDeg}° → {o.extendedDeg}° about its pivot" +
                  ((o.pneumaticBody != null || o.cylinderRod != null)
                      ? $"; cylinder {(o.pneumaticBody != null ? "body" : "")}{(o.pneumaticBody != null && o.cylinderRod != null ? "+rod" : o.cylinderRod != null ? "rod" : "")} follows it"
                      : "") + ".\n") +
            $"• Button: {buttonNote}. Press toggles extend/retract; the piston always reaches its endpoint (force uncapped).\n" +
            "• The moving link no longer collides with its own wheels/chassis, so a rough-CAD part can't push the bot.\n" +
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
        RemoveCylinderFollower(rig.pneumaticBody, useUndo); // rotary cosmetic cylinder body, if any
        RemoveCylinderFollower(rig.cylinderRod, useUndo);   // rotary cosmetic cylinder rod, if any
        MechanismBuildUtil.RemoveComponents<IgnoreRobotSelfCollision>(link, useUndo);

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
                else if (!linear && TryDrivetrainLateralLocal(o.link, out Vector3 lateralLocal))
                {
                    // A doinker/flipper hinges about the drivetrain's LATERAL axis (left↔right), exactly like
                    // the DR4B arms — deriving it from the wheels is right regardless of how the CAD is
                    // oriented, where the generic geometry guess (TryInferAxisAnchor) picked the wrong plane.
                    axis = lateralLocal;
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

    // The robot's lateral (left↔right) axis in the LINK's local frame — the doinker/DR4B hinge axis,
    // read from the drivetrain wheels so it's correct whatever the CAD orientation. False if no drivetrain.
    private static bool TryDrivetrainLateralLocal(GameObject link, out Vector3 axisLocal)
    {
        axisLocal = Vector3.right;
        RobotMechanisms reg = link.GetComponentInParent<RobotMechanisms>();
        RobotMotorController mc = reg != null ? reg.GetComponentInChildren<RobotMotorController>(true) : null;
        if (mc == null) return false;
        Vector3 lat = Centroid(mc.rightWheels) - Centroid(mc.leftWheels);
        if (lat.sqrMagnitude < 1e-6f) return false;
        axisLocal = link.transform.InverseTransformDirection(lat.normalized).normalized;
        return axisLocal.sqrMagnitude > 1e-8f;
    }

    private static Vector3 Centroid(ArticulationBody[] arr)
    {
        if (arr == null) return Vector3.zero;
        Vector3 s = Vector3.zero; int n = 0;
        foreach (ArticulationBody a in arr) if (a != null) { s += a.transform.position; n++; }
        return n > 0 ? s / n : Vector3.zero;
    }

    private static bool TryBoundsCenter(GameObject go, out Vector3 center)
    {
        center = Vector3.zero;
        if (!TryBounds(go, out Bounds b)) return false;
        center = b.center;
        return true;
    }

    private static bool TryBounds(GameObject go, out Bounds b)
    {
        b = default;
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return false;
        b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return true;
    }

    // The point on go's mesh bounds nearest `point` (falls back to its transform origin). Used to place
    // the cosmetic cylinder's mount + arm-connection helper empties on the actual geometry.
    private static Vector3 ClosestOnBounds(GameObject go, Vector3 point)
        => TryBounds(go, out Bounds b) ? b.ClosestPoint(point) : go.transform.position;

    // A cosmetic cylinder part must be a separate chassis mesh, never the moving metal (or its child).
    private static void ValidateCosmetic(GameObject part, GameObject metal, string role)
    {
        if (part == null) return;
        if (part == metal || part.transform.IsChildOf(metal.transform) || metal.transform.IsChildOf(part.transform))
            throw new InvalidOperationException(
                $"The {role} '{part.name}' overlaps the moving metal '{metal.name}' in the hierarchy. Pick the " +
                "cylinder parts (which stay on the chassis) separately from the METAL they push.");
        if (part.GetComponentInChildren<Renderer>(true) == null)
            throw new InvalidOperationException($"The {role} '{part.name}' has no mesh to move.");
    }

    // Rotary cosmetic: the moving metal (arm) is the real revolute joint; the cylinder BODY + ROD just
    // follow it, posed off the two pivots. Two shared helper empties are baked: a MOUNT in the chassis
    // frame (the parts swivel about it = the Pneumatic pivot) and a CONNECTION point parented under the
    // arm (so it rides the metal as it swings). PneumaticCylinderFollower re-aims each part from mount ->
    // connection every frame; the ROD also slides out to reach the connection (the visible stroke). The
    // parts are neutralized (no body; colliders off) so they never fight physics.
    private static void WireRotaryCylinder(Options o, RobotMechanisms registry, bool useUndo)
    {
        GameObject arm = o.link, body = o.pneumaticBody, rod = o.cylinderRod;
        Transform pivot = o.pneumaticPivot;

        RemoveCylinderFollower(body, useUndo); // idempotent: clear any leftover on these same parts
        RemoveCylinderFollower(rod, useUndo);

        GameObject anyCyl = body != null ? body : rod;

        // Mount = the Pneumatic pivot (where the cylinder is pinned to the chassis). Baked into a stable
        // empty under the robot root so the swivel frame can't drift with a moving marker.
        Vector3 mountPos = pivot != null ? pivot.position : ClosestOnBounds(anyCyl, BoundsCenterOrOrigin(arm));
        GameObject mount = new GameObject("Pneumatic Mount");
        if (useUndo) Undo.RegisterCreatedObjectUndo(mount, UndoName);
        MechanismBuildUtil.EnsureChildOf(mount.transform, registry.transform, useUndo);
        mount.transform.position = mountPos;

        // Connection = the clevis on the metal the rod reaches: the rod's far tip ALONG THE CYLINDER AXIS
        // (mount -> rod center, extended to the rod's far surface) if a rod is given, else the arm point
        // nearest the body. Using the centerline point — not an axis-aligned bounding-box corner — keeps the
        // rod's aim and slide collinear with the body, so it stays coming out of the body's end instead of
        // sliding off to one side. Parented under the arm so PhysX carries it as the metal swings.
        Vector3 connPos = rod != null ? AxisFarPoint(rod, mountPos)
                                      : ClosestOnBounds(arm, BoundsCenterOrOrigin(body));
        GameObject conn = new GameObject("Pneumatic Link");
        if (useUndo) Undo.RegisterCreatedObjectUndo(conn, UndoName);
        MechanismBuildUtil.EnsureChildOf(conn.transform, arm.transform, useUndo);
        conn.transform.position = connPos;

        // Body swivels about the mount only.
        if (body != null)
            AttachCosmetic(body, mount.transform, conn.transform, false, false, null, 0f, 0f, 0f, useUndo);

        // Rod swivels AND slides out by the chosen cylinder stroke, scaled by the metal's swing progress.
        // Keep the rod from being dragged by the body's transform (if nested) by lifting it to the chassis.
        if (rod != null)
        {
            if (body != null && (rod.transform.IsChildOf(body.transform) || body.transform.IsChildOf(rod.transform)))
                MechanismBuildUtil.EnsureChildOf(rod.transform, registry.transform, useUndo);

            // Read the resting/fired joint angles off the actuator (accounts for reverse + limit ordering)
            // so progress is 0 at rest and 1 when fired regardless of how the two angles were entered.
            PneumaticActuator act = arm.GetComponent<PneumaticActuator>();
            float lowRad = (act != null ? act.retractedTarget : o.retractedDeg) * Mathf.Deg2Rad;
            float highRad = (act != null ? act.extendedTarget : o.extendedDeg) * Mathf.Deg2Rad;
            AttachCosmetic(rod, mount.transform, conn.transform, true, o.flipRod, arm.GetComponent<ArticulationBody>(),
                o.strokeMm / MmPerUnit, lowRad, highRad, useUndo);
        }
    }

    // Neutralize a cosmetic part (strip any body, disable its colliders so a moving mesh can't wobble the
    // chassis) and wire the follower to swivel it about the mount, aimed at the connection. The rod also
    // gets its stroke + the metal joint so it slides out by the real cylinder travel as the metal swings.
    private static void AttachCosmetic(GameObject part, Transform mount, Transform connection, bool extend,
        bool flipExtend, ArticulationBody progressBody, float strokeUnits, float lowRad, float highRad, bool useUndo)
    {
        ArticulationBody stray = part.GetComponent<ArticulationBody>();
        if (stray != null)
        {
            if (useUndo) Undo.DestroyObjectImmediate(stray);
            else UnityEngine.Object.DestroyImmediate(stray);
        }
        MechanismBuildUtil.DisableColliders(part, useUndo);

        PneumaticCylinderFollower follower = MechanismBuildUtil.AddOrGet<PneumaticCylinderFollower>(part, useUndo);
        if (useUndo) Undo.RecordObject(follower, UndoName);
        follower.mount = mount;
        follower.connection = connection;
        follower.extend = extend;
        follower.flipExtend = flipExtend;
        follower.progressBody = progressBody;
        follower.maxExtendUnits = strokeUnits;
        follower.progressLowRad = lowRad;
        follower.progressHighRad = highRad;
        EditorUtility.SetDirty(follower);
    }

    // Remove a cosmetic cylinder follower + its helper empties and re-enable the colliders the build
    // disabled. Safe when the part is null or has none. Body + rod share the empties, so the first call
    // destroys them and the second sees them gone (Unity null) and no-ops.
    private static void RemoveCylinderFollower(GameObject part, bool useUndo)
    {
        if (part == null) return;
        PneumaticCylinderFollower follower = part.GetComponent<PneumaticCylinderFollower>();
        if (follower == null) return;

        DestroyGo(follower.mount, useUndo);
        DestroyGo(follower.connection, useUndo);
        if (useUndo) Undo.DestroyObjectImmediate(follower);
        else UnityEngine.Object.DestroyImmediate(follower);

        foreach (Collider c in part.GetComponentsInChildren<Collider>(true))
        {
            if (c == null || c.enabled) continue;
            if (useUndo) Undo.RecordObject(c, UndoName);
            c.enabled = true;
        }
    }

    private static void DestroyGo(Transform t, bool useUndo)
    {
        if (t == null) return;
        if (useUndo) Undo.DestroyObjectImmediate(t.gameObject);
        else UnityEngine.Object.DestroyImmediate(t.gameObject);
    }

    private static Vector3 BoundsCenterOrOrigin(GameObject go)
    {
        if (go == null) return Vector3.zero;
        return TryBoundsCenter(go, out Vector3 c) ? c : go.transform.position;
    }

    // The far end of `go` ALONG THE CYLINDER AXIS (the ray from `from` through the mesh's visual center),
    // i.e. a point on the rod's centerline, not an axis-aligned bounding-box corner. Keeping the connection
    // on the centerline makes the rod slide straight out of the body instead of skewing toward a corner.
    // Bounds are renderer-based (the visual), so a mesh whose transform origin isn't at its visual center
    // still resolves correctly — the reason the rod looked detached from the body.
    private static Vector3 AxisFarPoint(GameObject go, Vector3 from)
    {
        if (!TryBounds(go, out Bounds b)) return go.transform.position;
        Vector3 dir = b.center - from;
        if (dir.sqrMagnitude < 1e-8f) return b.center;
        dir.Normalize();
        // Distance from the center to the AABB surface in the direction `dir` (support width of the box).
        float ext = Mathf.Abs(dir.x) * b.extents.x + Mathf.Abs(dir.y) * b.extents.y + Mathf.Abs(dir.z) * b.extents.z;
        return b.center + dir * ext;
    }
}
