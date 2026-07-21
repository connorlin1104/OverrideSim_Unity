using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Author or fix ONE mechanism joint on an already-imported, already-set-up robot — entirely in
// Unity, no Fusion round-trip. The URDF export carries the accurate joint axes/limits, but if a
// part came in as a plain fixed attachment (or with the wrong type/limits), this converts it to a
// working revolute/continuous/prismatic mechanism (or back to fixed), wiring it identically to the
// joints the post-processor wires — so it shows up in the home-screen controller-config UI and is
// mappable to a button. Re-applying to the same link replaces its mechanism, so it doubles as the
// "fix the wrong joint" path.
//
// Scope: both robot kinds. A URDF link already IS an ArticulationBody (including parts that
// imported as fixed) — this retypes it. A mesh/FBX part has no body yet — this SPLITS a new moving
// link off the chassis: it adds the body, so the part's colliders/meshes leave the chassis body and
// become their own link, then joints it. Either way the result is wired identically. It can't build
// a moving body with no geometry — model the part in CAD first.
//
// Usage: Tools > RoboSim > Robot > Mechanisms > Add or Fix Mechanism Joint.
public class AddMechanismJointWindow : EditorWindow
{
    private const string Title = "Add or Fix Mechanism Joint";
    private const float MetersPerUnit = 0.1f; // this project's world: 1 scaled unit = 0.1 m

    // Auto WAS last so the stored integers for X/Y/Z/Custom stayed stable across an earlier upgrade;
    // the robot/scene options are APPENDED after it for the same reason (an open window's serialized
    // selection must not shift). Display order is decoupled from this via AxisOrder below.
    private enum AxisPreset { X, Y, Z, Custom, Auto, RobotUp, RobotSide, RobotFwd, WorldX, WorldY, WorldZ, FromAxle }

    // Display order + labels for the dropdown, kept separate from the enum's serialization order. Same
    // idea as the claw builder's picker: a joint axis lives in the link's local frame, and an imported
    // CAD frame is arbitrary — its "Y" is only up by luck. So the friendly options resolve against a
    // dropped-in SHAFT, the ROBOT (mean the same on every model), or the SCENE gizmo arrows (checkable
    // by eye); part-local X/Y/Z sinks to the bottom as the URDF-fix escape hatch.
    private static readonly AxisPreset[] AxisOrder =
    {
        AxisPreset.Auto, AxisPreset.FromAxle, AxisPreset.RobotSide, AxisPreset.RobotUp, AxisPreset.RobotFwd,
        AxisPreset.WorldX, AxisPreset.WorldY, AxisPreset.WorldZ,
        AxisPreset.X, AxisPreset.Y, AxisPreset.Z, AxisPreset.Custom,
    };
    private static readonly string[] AxisLabels =
    {
        "Auto — guess from the part's shape",
        "Axle / shaft — read the axis from a part",
        "Robot left/right — an arm swinging front↔back",
        "Robot up/down — a turret spinning flat",
        "Robot front/back — a wrist rolling over",
        "Scene X — the RED gizmo arrow",
        "Scene Y — the GREEN gizmo arrow",
        "Scene Z — the BLUE gizmo arrow",
        "Part-local X (URDF axis fix)",
        "Part-local Y (URDF axis fix)",
        "Part-local Z (URDF axis fix)",
        "Custom vector (link-local)",
    };

    // User-facing mechanism intent — pick what the part DOES, and the tool maps it to a joint DOF +
    // actuation. Replaces the raw "Joint Type" + "Piston Toggle" jargon.
    private enum MechanismKind
    {
        SpinningMotor,  // Continuous + motor   — roller / flywheel / intake shaft (free-spins both ways)
        ArmMotor,       // Revolute   + motor   — limited arm / lift hinge (hold-to-run within its range)
        RotatingPiston, // Revolute   + toggle  — doinker / flipper (piston snaps a hinge between 2 angles)
        LinearPiston,   // Prismatic  + toggle  — cylinder that slides a part in/out
        Fixed,          // welded — removes any mechanism
    }

    private static readonly string[] KindLabels =
    {
        "Spinning motor (roller / flywheel / intake)",
        "Arm / lift motor (limited hinge)",
        "Rotating piston (doinker / flipper)",
        "Linear piston (slides in / out)",
        "Fixed (weld — no mechanism)",
    };

    private static AddMechanismJoint.JointType JointTypeOf(MechanismKind k) => k switch
    {
        MechanismKind.SpinningMotor => AddMechanismJoint.JointType.Continuous,
        MechanismKind.ArmMotor => AddMechanismJoint.JointType.Revolute,
        MechanismKind.RotatingPiston => AddMechanismJoint.JointType.Revolute,
        MechanismKind.LinearPiston => AddMechanismJoint.JointType.Prismatic,
        _ => AddMechanismJoint.JointType.Fixed,
    };

    [SerializeField] private GameObject childLink;
    // Default to a free-spinning axle (roller/shaft) — the common case, and the one that used to jam
    // when it defaulted to a limited hinge.
    [SerializeField] private MechanismKind mechKind = MechanismKind.SpinningMotor;
    [SerializeField] private AxisPreset axisPreset = AxisPreset.Auto;
    [SerializeField] private Vector3 customAxis = Vector3.up;
    [SerializeField] private Vector3 anchor = Vector3.zero;
    [SerializeField] private float lowerLimit = -90f;
    [SerializeField] private float upperLimit = 90f;
    [SerializeField] private bool autoAssignButton = true;
    [SerializeField] private List<GameObject> alsoMove = new List<GameObject>();
    [SerializeField] private bool reverseDirection;
    // Put the hinge/slide origin at the part's own centre (inferred) rather than making the user type
    // link-local coordinates — on for the friendly axis presets by default.
    [SerializeField] private bool autoPivot = true;
    [SerializeField] private bool showAxisPreview = true;
    // The shaft/rod the part turns on, when the axis is defined by pointing at a part (FromAxle).
    [SerializeField] private GameObject axlePart;

    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Add or Fix Mechanism Joint", false, 1)]
    private static void ShowWindow()
    {
        AddMechanismJointWindow window = GetWindow<AddMechanismJointWindow>(Title);
        window.minSize = new Vector2(420f, 320f);
        window.Show();
    }

    private void OnEnable()
    {
        if (childLink == null) childLink = Selection.activeGameObject;
        SceneView.duringSceneGui += OnSceneGUI;
    }

    private void OnDisable() => SceneView.duringSceneGui -= OnSceneGUI;

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Turn one part into a controllable mechanism (or fix/remove one). The robot must already be " +
            "set up (Set Up Imported Robot).\n\n" +
            "Child Link = the ONE part that physically moves (the arm, the roller shaft, the flap). Its " +
            "parent is found automatically (the nearest body above it, usually the chassis). Then pick what " +
            "it does.", MessageType.Info);

        childLink = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Moving Part (Child Link)",
            "The single part that rotates or slides — the arm, roller shaft, or flap. Not the motor " +
            "housing, not the whole subassembly."), childLink, typeof(GameObject), true);
        if (childLink == null)
        {
            EditorGUILayout.HelpBox("Select the link (URDF) or part (mesh/FBX) that should move.", MessageType.Warning);
            return;
        }
        RobotMechanisms registry = childLink.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
        {
            EditorGUILayout.HelpBox(
                "This is not under a set-up robot (no RobotMechanisms on the root). Run " +
                "Tools > RoboSim > Robot > Set Up Imported Robot first.", MessageType.Error);
            return;
        }
        // A mesh/FBX part has no ArticulationBody yet — Apply splits it off the chassis into a new
        // moving link. That needs a rigged chassis (ArticulationBody) above it.
        bool willSplitNewLink = childLink.GetComponent<ArticulationBody>() == null;
        if (willSplitNewLink)
        {
            if (childLink.GetComponentInParent<ArticulationBody>() == null)
            {
                EditorGUILayout.HelpBox(
                    $"'{childLink.name}' has no ArticulationBody and no rigged chassis above it. Run " +
                    "Set Up Imported Robot first, then pick the part that should move.", MessageType.Error);
                return;
            }
            EditorGUILayout.HelpBox(
                $"'{childLink.name}' isn't a moving link yet — Apply will split it off the chassis as a new " +
                "mechanism (its meshes and colliders leave the chassis body). Pick the node that moves as a " +
                "unit, and set the Anchor to the hinge/slide axis location.", MessageType.Info);
        }

        mechKind = (MechanismKind)EditorGUILayout.Popup(new GUIContent("Mechanism Type",
            "What this part does. Spinning / Arm = motor (hold a button to run). Rotating / Linear piston = " +
            "pneumatic (press to snap between two ends). Fixed = weld it still."),
            (int)mechKind, KindLabels);

        AddMechanismJoint.JointType jointType = JointTypeOf(mechKind);
        bool asToggle = mechKind == MechanismKind.RotatingPiston;
        bool isFixed = mechKind == MechanismKind.Fixed;

        bool showAxis = !isFixed;
        bool showLimits = jointType == AddMechanismJoint.JointType.Revolute ||
                          jointType == AddMechanismJoint.JointType.Prismatic;

        if (showAxis)
        {
            int cur = Mathf.Max(0, Array.IndexOf(AxisOrder, axisPreset));
            int picked = EditorGUILayout.Popup(new GUIContent("Which way it turns",
                "The line the part hinges (or slides) about. 'Auto' reads it from the part's shape — good " +
                "for a roller/axle. The ROBOT options mean the same thing on every model however the CAD " +
                "is oriented (an arm swings about the robot's left/right line). The SCENE options are the " +
                "coloured move-gizmo arrows you can check by eye. Part-local X/Y/Z is the URDF-fix hatch."),
                cur, AxisLabels);
            axisPreset = AxisOrder[Mathf.Clamp(picked, 0, AxisOrder.Length - 1)];

            if (axisPreset == AxisPreset.FromAxle)
            {
                axlePart = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Axle / shaft part",
                    "Drop the shaft, rod or tube the part turns on. Its LONG direction becomes the hinge " +
                    "axis and its centre becomes the pivot — nothing to type. This only READS the axis; " +
                    "if the shaft should also move with the part, add it under 'Parts That Move Together'."),
                    axlePart, typeof(GameObject), true);
                if (axlePart == null)
                    EditorGUILayout.HelpBox("Drop the shaft/rod the part rotates on — its long direction " +
                        "is the axis, its centre is the pivot.", MessageType.Warning);
                else if (!ChainBuilder.TryAxleWorldAxis(axlePart, out _, out _))
                    EditorGUILayout.HelpBox($"'{axlePart.name}' has no mesh to read a direction from. Pick " +
                        "the actual shaft geometry (the long thin part), not an empty group.", MessageType.Warning);
                else
                    EditorGUILayout.HelpBox($"Axis + pivot read from '{axlePart.name}'. Check the blue " +
                        "line in the Scene view runs down the shaft.", MessageType.None);
            }
            else if (axisPreset == AxisPreset.Auto)
            {
                EditorGUILayout.HelpBox("Axis + pivot inferred from the part's geometry — a best guess. " +
                    "Watch the Scene view; if it hinges the wrong way, pick a robot or scene axis.",
                    MessageType.None);
            }
            else
            {
                if (axisPreset == AxisPreset.Custom)
                    customAxis = EditorGUILayout.Vector3Field("Custom Axis (link-local)", customAxis);
                autoPivot = EditorGUILayout.Toggle(new GUIContent("Pivot from geometry",
                    "Put the hinge/slide origin at the part's own centre (inferred). Turn off to type the " +
                    "pivot in the part's local space — only needed if the hinge isn't at the part's middle."),
                    autoPivot);
                if (!autoPivot)
                    anchor = EditorGUILayout.Vector3Field(new GUIContent("Anchor (link-local)",
                        "Pivot/slide origin in the link's local space. 0 = the link origin."), anchor);
            }

            showAxisPreview = EditorGUILayout.ToggleLeft(new GUIContent("Show axis in the Scene view",
                "Draw the line the part turns about and the arc it sweeps, live, before you Apply — so a " +
                "wrong axis is visible now rather than after."), showAxisPreview);
        }

        if (!isFixed)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(new GUIContent("Parts That Move Together",
                "Extra parts on the SAME shaft to weld into this one moving link so the whole axle moves as a " +
                "unit (the shaft, co-rotating plates). Leave the MOTOR out. For SEPARATE shafts linked by " +
                "chain (chained rollers/sprockets), don't list them here — use Build Chain."),
                EditorStyles.miniBoldLabel);
            for (int i = 0; i < alsoMove.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                alsoMove[i] = (GameObject)EditorGUILayout.ObjectField(alsoMove[i], typeof(GameObject), true);
                if (GUILayout.Button("X", GUILayout.Width(24))) { alsoMove.RemoveAt(i); i--; }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("Add Part", GUILayout.Width(100))) alsoMove.Add(null);

            reverseDirection = EditorGUILayout.Toggle(new GUIContent("Reverse Direction",
                "Flip the drive sense if the mechanism runs backward for 'forward' input (motor) or starts " +
                "at the wrong end (piston)."), reverseDirection);
        }

        if (!isFixed)
            autoAssignButton = EditorGUILayout.Toggle(new GUIContent("Auto-Assign Button",
                "After applying, map this mechanism to the next free controller button (motor = " +
                "forward/reverse pair, piston = toggle) so it's drivable without opening Configure Controller."),
                autoAssignButton);

        if (showLimits)
        {
            if (jointType == AddMechanismJoint.JointType.Revolute)
            {
                EditorGUILayout.LabelField(asToggle ? "Snap Angles (degrees)" : "Limits (degrees)",
                    EditorStyles.miniBoldLabel);
                lowerLimit = EditorGUILayout.FloatField(asToggle ? "Down (retracted)" : "Lower", lowerLimit);
                upperLimit = EditorGUILayout.FloatField(asToggle ? "Up (extended)" : "Upper", upperLimit);
            }
            else // Prismatic (linear piston)
            {
                EditorGUILayout.LabelField("Stroke (scaled units, 1 unit = 0.1 m)", EditorStyles.miniBoldLabel);
                lowerLimit = EditorGUILayout.FloatField("Retracted", lowerLimit);
                upperLimit = EditorGUILayout.FloatField("Extended", upperLimit);
                EditorGUILayout.LabelField(" ",
                    $"= {lowerLimit * MetersPerUnit:0.###} .. {upperLimit * MetersPerUnit:0.###} m");
                if (Mathf.Abs(upperLimit - lowerLimit) * MetersPerUnit > 1.0f)
                    EditorGUILayout.HelpBox("That stroke is over 1 m — a real VEX cylinder is a few cm. A big " +
                        "number here launches the part across the field (this was the 654V bug). Check the units " +
                        "(1 unit = 0.1 m), or did you mean a Rotating piston in degrees?", MessageType.Warning);
            }
        }
        else if (mechKind == MechanismKind.SpinningMotor)
        {
            EditorGUILayout.HelpBox("Spins freely both ways — no limits.", MessageType.None);
        }
        else if (isFixed)
        {
            EditorGUILayout.HelpBox(
                "Fixed welds the link to its parent and REMOVES any mechanism it had.", MessageType.None);
        }

        // Any edit above can move the previewed axis/arc, so keep the Scene view in step.
        if (GUI.changed && showAxisPreview) SceneView.RepaintAll();

        EditorGUILayout.Space();
        if (!GUILayout.Button(isFixed ? "Apply (make fixed)" : "Apply Mechanism", GUILayout.Height(30))) return;

        try
        {
            Vector3 axis = Vector3.up;
            Vector3 effectiveAnchor = anchor;
            if (!isFixed)
                ResolveAxisAnchor(childLink, registry, jointType, out axis, out effectiveAnchor);

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Title);
            int group = Undo.GetCurrentGroup();

            var options = new AddMechanismJoint.Options
            {
                alsoMove = alsoMove.Count > 0 ? alsoMove.ToArray() : null,
                reverseDirection = reverseDirection,
                actuation = asToggle ? AddMechanismJoint.Actuation.Toggle : AddMechanismJoint.Actuation.Auto,
            };
            AddMechanismJoint.Apply(childLink, jointType, axis, effectiveAnchor, lowerLimit, upperLimit, options, useUndo: true);

            // Map it to a free button so it's drivable immediately (skipped for Fixed, which removed
            // the mechanism). A rotating piston maps like a pneumatic (one toggle button), so pass
            // Prismatic for the button style. Non-fatal: a full map just means the user maps it later.
            string buttonNote = "";
            if (autoAssignButton && !isFixed)
            {
                RobotMechanisms reg = childLink.GetComponentInParent<RobotMechanisms>();
                AddMechanismJoint.JointType buttonType = asToggle ? AddMechanismJoint.JointType.Prismatic : jointType;
                if (reg != null)
                    buttonNote = "\nButton: " + MechanismAutoDetect.AssignButtons(
                        reg.robotId, UrdfPostProcessor.Slugify(childLink.name), buttonType);
            }
            Undo.CollapseUndoOperations(group);

            EditorUtility.DisplayDialog(Title,
                $"'{childLink.name}' is now set up as: {KindLabels[(int)mechKind]}" +
                (isFixed ? " (mechanism removed)." : ".") +
                buttonNote +
                "\n\nSave the scene, then Robot > Validate Robot Physics to test it.", "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog(Title, e.Message, "OK");
            Debug.LogException(e, childLink);
        }
    }

    // The link-local axis + anchor the current form describes — the exact pair Apply feeds the joint
    // core, and what the Scene preview draws, so the two can never disagree. Auto (and 'Pivot from
    // geometry') infer the anchor from the part's shape; the friendly robot/scene presets are resolved
    // into the link's own frame, which is the frame a joint axis is measured in.
    private void ResolveAxisAnchor(GameObject link, RobotMechanisms reg,
        AddMechanismJoint.JointType jointType, out Vector3 axis, out Vector3 anchorLocal)
    {
        // An axle part wins: the shaft's own long axis + centre, read exactly the way Build Chain reads a
        // shaft, then converted into the link's frame. This is what lets you point at the pin instead of
        // typing where the axis sits.
        if (axisPreset == AxisPreset.FromAxle && axlePart != null &&
            ChainBuilder.TryAxleWorldAxis(axlePart, out Vector3 wAxis, out Vector3 wCenter))
        {
            Vector3 a = link.transform.InverseTransformDirection(wAxis);
            axis = a.sqrMagnitude > 1e-8f ? a.normalized : Vector3.up;
            anchorLocal = link.transform.InverseTransformPoint(wCenter);
            return;
        }

        // Auto — and FromAxle with no usable shaft yet — infer axis+anchor from the part's own geometry.
        bool inferMode = axisPreset == AxisPreset.Auto || axisPreset == AxisPreset.FromAxle;
        bool needInfer = inferMode || autoPivot;
        bool inferredOk = false;
        Vector3 autoAxis = Vector3.up, autoAnchor = Vector3.zero;
        if (needInfer)
            inferredOk = MechanismAutoDetect.TryInferAxisAnchor(link, jointType, out autoAxis, out autoAnchor);

        if (inferMode)
        {
            axis = inferredOk ? autoAxis : Vector3.up;
            anchorLocal = inferredOk ? autoAnchor : anchor;
            return;
        }
        axis = ResolveAxisLocal(link, reg, axisPreset, customAxis);
        anchorLocal = autoPivot ? (inferredOk ? autoAnchor : Vector3.zero) : anchor;
    }

    // One friendly preset -> a unit axis in `link`'s local frame. Robot options convert the robot's own
    // up/right/forward through the link; scene options convert the world gizmo arrows; part-local is the
    // raw axis. RobotSide prefers the drivetrain's measured left/right line over the root's X, which
    // stays right even when the CAD root is rotated relative to the chassis.
    private static Vector3 ResolveAxisLocal(GameObject link, RobotMechanisms reg, AxisPreset p, Vector3 custom)
    {
        switch (p)
        {
            case AxisPreset.X: return Vector3.right;
            case AxisPreset.Y: return Vector3.up;
            case AxisPreset.Z: return Vector3.forward;
            case AxisPreset.Custom: return custom.sqrMagnitude > 1e-8f ? custom.normalized : Vector3.up;
            case AxisPreset.RobotUp: return RobotDirLocal(link, reg, Vector3.up);
            case AxisPreset.RobotFwd: return RobotDirLocal(link, reg, Vector3.forward);
            case AxisPreset.RobotSide:
                return MechanismBuildUtil.TryDrivetrainLateralLocal(link, out Vector3 lat)
                    ? lat : RobotDirLocal(link, reg, Vector3.right);
            case AxisPreset.WorldX: return link.transform.InverseTransformDirection(Vector3.right).normalized;
            case AxisPreset.WorldY: return link.transform.InverseTransformDirection(Vector3.up).normalized;
            case AxisPreset.WorldZ: return link.transform.InverseTransformDirection(Vector3.forward).normalized;
            default: return Vector3.up;
        }
    }

    private static Vector3 RobotDirLocal(GameObject link, RobotMechanisms reg, Vector3 rootLocalDir)
    {
        Vector3 world = reg != null ? reg.transform.TransformDirection(rootLocalDir) : rootLocalDir;
        Vector3 local = link.transform.InverseTransformDirection(world);
        return local.sqrMagnitude > 1e-8f ? local.normalized : Vector3.up;
    }

    // Draws, live in the Scene view, the line the part will turn (or slide) about and the arc it sweeps
    // between its limits — resolved through the SAME ResolveAxisAnchor the Apply uses, so what you see is
    // what you'll get. "Diagnose by looking": a hinge about the wrong line is obvious here in a way a
    // link-local vector in a field never is.
    private void OnSceneGUI(SceneView view)
    {
        if (!showAxisPreview || childLink == null) return;
        RobotMechanisms reg = childLink.GetComponentInParent<RobotMechanisms>();
        if (reg == null || mechKind == MechanismKind.Fixed) return;

        AddMechanismJoint.JointType jointType = JointTypeOf(mechKind);
        ResolveAxisAnchor(childLink, reg, jointType, out Vector3 axisLocal, out Vector3 anchorLocal);
        Vector3 axisW = childLink.transform.TransformDirection(axisLocal);
        if (axisW.sqrMagnitude < 1e-6f) return;
        axisW.Normalize();
        Vector3 pivotW = childLink.transform.TransformPoint(anchorLocal);

        float h = HandleUtility.GetHandleSize(pivotW);
        Vector3 center = MechanismBuildUtil.BoundsCenterOrOrigin(childLink);
        Vector3 arm = Vector3.ProjectOnPlane(center - pivotW, axisW);
        float reach = Mathf.Max(arm.magnitude, h * 2.5f);
        var color = new Color(0.35f, 0.7f, 1f);

        Handles.color = color;
        Handles.DrawAAPolyLine(4f, pivotW - axisW * reach, pivotW + axisW * reach);
        Handles.SphereHandleCap(0, pivotW, Quaternion.identity, h * 0.16f, EventType.Repaint);

        if (jointType == AddMechanismJoint.JointType.Prismatic)
        {
            Handles.ArrowHandleCap(0, pivotW, Quaternion.LookRotation(axisW), h, EventType.Repaint);
            Handles.ArrowHandleCap(0, pivotW, Quaternion.LookRotation(-axisW), h, EventType.Repaint);
            Handles.Label(pivotW + axisW * reach, $"{childLink.name} slides along this line");
            return;
        }

        if (arm.sqrMagnitude < 1e-8f)
        {
            Handles.DrawWireDisc(pivotW, axisW, reach);
            Handles.Label(pivotW + axisW * reach, $"{childLink.name} turns about this line (pivot at its centre)");
            return;
        }

        if (jointType == AddMechanismJoint.JointType.Continuous)
        {
            Handles.DrawWireDisc(pivotW, axisW, arm.magnitude);
            Handles.DrawAAPolyLine(2f, pivotW, pivotW + arm);
            Handles.Label(pivotW + arm * 1.1f, $"{childLink.name} free-spins about this line");
            return;
        }

        // Revolute: shade the swept range low..high about the current (rest) pose, mark both ends.
        Handles.color = new Color(color.r, color.g, color.b, 0.18f);
        Handles.DrawSolidArc(pivotW, axisW, Quaternion.AngleAxis(lowerLimit, axisW) * arm,
            upperLimit - lowerLimit, arm.magnitude);
        Handles.color = color;
        Handles.DrawAAPolyLine(2f, pivotW, pivotW + arm);                                   // rest pose
        Handles.DrawDottedLine(pivotW, pivotW + Quaternion.AngleAxis(lowerLimit, axisW) * arm, 3f);
        Handles.DrawDottedLine(pivotW, pivotW + Quaternion.AngleAxis(upperLimit, axisW) * arm, 3f);
        Handles.Label(pivotW + arm * 1.12f, $"{childLink.name}: swings {lowerLimit:0}°..{upperLimit:0}°");
    }
}

// The joint-authoring core, split out so the headless validator can drive it without the window.
public static class AddMechanismJoint
{
    public enum JointType { Revolute, Continuous, Prismatic, Fixed }

    // How a mechanism is actuated from its button(s), independent of the joint's DOF type. Auto
    // classifies by joint type (revolute/continuous -> hold-to-run motor, prismatic -> pneumatic
    // toggle); Toggle forces a binary snap between the limits (a piston-driven pivot/flipper);
    // HoldToRun forces a velocity motor.
    public enum Actuation { Auto, HoldToRun, Toggle }

    // Optional authoring extras for Apply. All default to "single picked part, forward direction,
    // auto actuation", so the legacy 7-arg overload keeps working unchanged.
    public struct Options
    {
        public GameObject[] alsoMove;   // plain parts to fold into the one driven link
        public bool reverseDirection;   // flip motor sense / swap pneumatic endpoints
        public Actuation actuation;
    }

    private const float WorldScaleFactor = 10f;  // this project's world: 1 scaled unit = 0.1 m
    private const float DefaultLinkMass = 1f;     // fallback mass for a split link with no closed mesh
    private const float MinSplitMass = 1e-3f;     // below this the geometry mass is treated as absent

    // Configures the link's ArticulationBody as the requested joint (type -> DOF locks -> anchors
    // -> limits, matching the URDF importer's AdjustMovement and the post-processor's anchor
    // re-derivation), then wires (or removes) the mechanism and refreshes the catalog. When the link
    // is a plain mesh part with no body, first splits a new link off the chassis (adds the body + a
    // geometry-derived mass). Throws on any precondition failure. useUndo=false for batch/headless callers.
    public static void Apply(GameObject link, JointType type, Vector3 axis, Vector3 anchor,
        float lowerLimit, float upperLimit, bool useUndo)
        => Apply(link, type, axis, anchor, lowerLimit, upperLimit, default, useUndo);

    // Full overload: adds part-grouping (the driven link co-rotates a whole axle while the unlisted
    // motor housing stays welded), a reverse-direction flip, and an actuation override (drive a
    // revolute as a pneumatic toggle — a piston-driven pivot). See Options.
    public static void Apply(GameObject link, JointType type, Vector3 axis, Vector3 anchor,
        float lowerLimit, float upperLimit, Options options, bool useUndo)
    {
        if (link == null) throw new ArgumentNullException(nameof(link));

        RobotMechanisms registry = link.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
            throw new InvalidOperationException(
                $"'{link.name}' is not under a set-up robot (no RobotMechanisms). Run " +
                "Set Up Imported Robot first.");
        GameObject root = registry.gameObject;

        ArticulationBody body = ConfigureJointLink(link, type, axis, anchor, lowerLimit, upperLimit, options, registry, useUndo);

        string id = UrdfPostProcessor.Slugify(link.name);
        if (type == JointType.Fixed)
        {
            UrdfPostProcessor.RemoveMechanism(registry, id, useUndo);
        }
        else
        {
            UrdfPostProcessor.MechKind kind = ResolveKind(body, options.actuation);
            RobotMechanisms.Mechanism mech = UrdfPostProcessor.WireMechanism(body, link, kind, useUndo);
            ApplyDirection(mech, body, options.reverseDirection, useUndo);
            UrdfPostProcessor.RegisterMechanism(registry, mech, useUndo);
        }
        UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, root.name, registry);

        EditorUtility.SetDirty(body);
        EditorUtility.SetDirty(registry);
        if (root.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.scene);
    }

    // Turns `link` into a configured ArticulationBody joint of `type` (DOF locks, axis anchorRotation,
    // parent-anchor re-derivation, travel limits) WITHOUT wiring an actuator or touching the
    // registry/catalog — the reusable core shared by Apply (which then wires/registers a button
    // mechanism) and the Build Chain tool (which then attaches a JointCoupler instead). When
    // the link is a plain mesh part with no body, first splits a new link off the chassis (adds the
    // body + a geometry-derived mass). `registry` supplies the robot root (mass frame, tag,
    // drivetrain-wheel guard). Throws on any precondition failure. Returns the configured body.
    internal static ArticulationBody ConfigureJointLink(GameObject link, JointType type, Vector3 axis,
        Vector3 anchor, float lowerLimit, float upperLimit, Options options, RobotMechanisms registry, bool useUndo)
    {
        GameObject root = registry.gameObject;

        // Fold any "parts that move together" into this link BEFORE it gets a body, so the driven
        // link is the whole axle (its mass covers them) while the unlisted motor housing stays welded
        // to the chassis. Only meaningful for a moving joint.
        bool mergedExtras = type != JointType.Fixed && MergeIntoLink(link, options.alsoMove, useUndo);

        // A URDF link already carries an ArticulationBody; a plain FBX part does not. When it
        // doesn't, split a new moving link off its rigid parent: adding the body moves this part's
        // colliders/meshes out of the chassis body into their own link, jointed to the nearest
        // ancestor body below. Needs a rigged chassis above it, and there's no mechanism to remove
        // yet, so Fixed is meaningless here.
        ArticulationBody body = link.GetComponent<ArticulationBody>();
        if (body == null)
        {
            if (FindParentBodyOf(link.transform) == null)
                throw new InvalidOperationException(
                    $"'{link.name}' has no ArticulationBody and no rigged chassis above it. Run Set Up " +
                    "Imported Robot first, then pick the part that should move.");
            if (type == JointType.Fixed)
                throw new InvalidOperationException(
                    $"'{link.name}' isn't a moving link yet, so there's nothing to make Fixed. Pick " +
                    "Revolute, Continuous, or Prismatic to split it off as a mechanism.");

            // Size the new link's mass from its geometry (part name -> density) before it gets a body.
            float density = RobotPartClassifier.TryGetDensity(link.name, out float d)
                ? d : RobotPartClassifier.DefaultDensity;
            float massKg = RobotMassFromGeometry.MassForLinkNode(link, root.transform, WorldScaleFactor, density);

            body = useUndo ? Undo.AddComponent<ArticulationBody>(link) : link.AddComponent<ArticulationBody>();
            body.mass = massKg > MinSplitMass ? massKg : DefaultLinkMass;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.angularDamping = 0.05f;
            body.ResetCenterOfMass();
            body.ResetInertiaTensor(); // from the colliders that just became this link's

            // The split-off geometry must keep the robot's tag so the match loaders still see it.
            if (useUndo) Undo.RecordObject(link, "Add or Fix Mechanism Joint");
            link.tag = root.tag;
        }
        else if (mergedExtras)
        {
            // Extra geometry just joined an existing link — refresh its mass distribution.
            if (useUndo) Undo.RecordObject(body, "Add or Fix Mechanism Joint");
            body.ResetCenterOfMass();
            body.ResetInertiaTensor();
        }

        // Drivetrain wheels belong to the joysticks via RobotMotorController, not the buttons.
        RobotMotorController motor = root.GetComponent<RobotMotorController>();
        if (motor != null && ((motor.leftWheels != null && Array.IndexOf(motor.leftWheels, body) >= 0) ||
                              (motor.rightWheels != null && Array.IndexOf(motor.rightWheels, body) >= 0)))
            throw new InvalidOperationException(
                $"'{link.name}' is a drivetrain wheel wired to the joysticks — it can't be a button mechanism.");

        if (type != JointType.Fixed)
            axis = axis.sqrMagnitude < 1e-8f ? Vector3.right : axis.normalized;

        if (useUndo) Undo.RecordObject(body, "Add or Fix Mechanism Joint");

        // Type first (changing it resets the drives), then the DOF locks + anchor rotation exactly
        // as the importer's AdjustMovement would set them for a joint of this type.
        switch (type)
        {
            case JointType.Revolute:
            case JointType.Continuous:
                body.jointType = ArticulationJointType.RevoluteJoint;
                body.linearLockX = ArticulationDofLock.LockedMotion;
                body.linearLockY = ArticulationDofLock.LockedMotion;
                body.linearLockZ = ArticulationDofLock.LockedMotion;
                body.twistLock = type == JointType.Continuous
                    ? ArticulationDofLock.FreeMotion : ArticulationDofLock.LimitedMotion;
                body.anchorRotation = Quaternion.FromToRotation(Vector3.right, -axis);
                break;
            case JointType.Prismatic:
                body.jointType = ArticulationJointType.PrismaticJoint;
                body.linearLockX = ArticulationDofLock.LimitedMotion;
                body.linearLockY = ArticulationDofLock.LockedMotion;
                body.linearLockZ = ArticulationDofLock.LockedMotion;
                body.anchorRotation = Quaternion.FromToRotation(Vector3.right, axis);
                break;
            case JointType.Fixed:
                body.jointType = ArticulationJointType.FixedJoint;
                break;
        }

        body.anchorPosition = anchor;

        // Re-derive the parent-side anchor from the actual (already-scaled) transforms with
        // matchAnchors off — the same fix the post-processor's scale bake uses, or PhysX snaps the
        // link back on the first Simulate().
        body.matchAnchors = false;
        ArticulationBody parentBody = FindParentBody(body);
        if (parentBody != null)
        {
            Transform p = parentBody.transform, c = body.transform;
            body.parentAnchorPosition = p.InverseTransformPoint(c.TransformPoint(body.anchorPosition));
            body.parentAnchorRotation = Quaternion.Inverse(p.rotation) * (c.rotation * body.anchorRotation);
        }

        // Travel limits (degrees for revolute, scaled units for prismatic; set BEFORE WireMechanism
        // because the pneumatic reads these as its endpoints).
        if (type == JointType.Revolute || type == JointType.Prismatic)
        {
            ArticulationDrive drive = body.xDrive;
            drive.lowerLimit = Mathf.Min(lowerLimit, upperLimit);
            drive.upperLimit = Mathf.Max(lowerLimit, upperLimit);
            body.xDrive = drive;
        }

        return body;
    }

    // Nearest ancestor ArticulationBody — the parent link this joint connects to.
    private static ArticulationBody FindParentBody(ArticulationBody body) => FindParentBodyOf(body.transform);

    // Nearest ArticulationBody strictly above a transform. Used before the split link has its own
    // body, to confirm there's a rigged chassis to joint it to.
    private static ArticulationBody FindParentBodyOf(Transform t)
    {
        for (Transform p = t.parent; p != null; p = p.parent)
        {
            ArticulationBody ancestor = p.GetComponent<ArticulationBody>();
            if (ancestor != null) return ancestor;
        }
        return null;
    }

    // Reparents each extra part under the driven link so the whole axle co-rotates as one body.
    // Only plain (un-rigged) parts can join — a part that's already its own link can't be absorbed.
    // Reparenting keeps world position, so the geometry doesn't jump. Returns true if anything moved.
    private static bool MergeIntoLink(GameObject link, GameObject[] alsoMove, bool useUndo)
    {
        if (alsoMove == null) return false;

        // Validate EVERY entry before moving anything, so a bad one can't leave the hierarchy
        // half-reparented. Skip entries already inside the link (idempotent re-apply) before the
        // rigged-link check, or a nested link already part of this driven subtree would false-trip it.
        var toMove = new List<Transform>();
        foreach (GameObject part in alsoMove)
        {
            if (part == null || part == link) continue;
            if (part.transform.IsChildOf(link.transform)) continue; // already part of the link
            if (part.GetComponentInChildren<ArticulationBody>(true) != null)
                throw new InvalidOperationException(
                    $"'{part.name}' already contains a rigged link, so it can't be merged into " +
                    $"'{link.name}'. Add only plain, un-rigged parts to a driven link.");
            if (link.transform.IsChildOf(part.transform))
                throw new InvalidOperationException(
                    $"'{part.name}' is above the driven link '{link.name}' in the hierarchy. Pick the " +
                    "axle/output as the moving link, then add the parts attached to it — not the reverse.");
            toMove.Add(part.transform);
        }

        foreach (Transform t in toMove)
        {
            if (useUndo) Undo.SetTransformParent(t, link.transform, "Add or Fix Mechanism Joint");
            else t.SetParent(link.transform, worldPositionStays: true);
        }
        return toMove.Count > 0;
    }

    // The actuator kind for a driven joint: Toggle drives it as a binary pneumatic (snap between the
    // limits — a piston-driven pivot), HoldToRun as a velocity motor, Auto by the joint's DOF type.
    private static UrdfPostProcessor.MechKind ResolveKind(ArticulationBody body, Actuation actuation)
    {
        switch (actuation)
        {
            case Actuation.Toggle:    return UrdfPostProcessor.MechKind.Pneumatic;
            case Actuation.HoldToRun: return UrdfPostProcessor.MechKind.Motor;
            default:                  return UrdfPostProcessor.ClassifyMechanism(body);
        }
    }

    // Applies the reverse-direction flip to the freshly-wired actuator: motors invert their input
    // sense; pistons swap their two endpoints (and re-seat the rest target) so they start at the
    // other end.
    private static void ApplyDirection(RobotMechanisms.Mechanism mech, ArticulationBody body, bool reverse, bool useUndo)
    {
        if (mech == null) return;
        if (mech.motor != null)
        {
            if (useUndo) Undo.RecordObject(mech.motor, "Add or Fix Mechanism Joint");
            mech.motor.invert = reverse;
        }
        else if (mech.pneumatic != null && reverse)
        {
            PneumaticActuator p = mech.pneumatic;
            if (useUndo) Undo.RecordObject(p, "Add or Fix Mechanism Joint");
            (p.retractedTarget, p.extendedTarget) = (p.extendedTarget, p.retractedTarget);
            ArticulationDrive d = body.xDrive;
            d.target = p.startExtended ? p.extendedTarget : p.retractedTarget;
            body.xDrive = d;
        }
    }
}
