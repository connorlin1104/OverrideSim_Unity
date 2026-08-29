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
    // actuation. Replaces the raw "Joint Type" + "Piston Toggle" jargon. PassiveArm is APPENDED
    // (the field is serialized, so an open window's stored integer must keep meaning what it did);
    // KindOrder below puts it where it reads right in the dropdown.
    internal enum MechanismKind
    {
        SpinningMotor,  // Continuous + motor   — roller / flywheel / intake shaft (free-spins both ways)
        ArmMotor,       // Revolute   + motor   — limited arm / lift hinge (hold-to-run within its range)
        RotatingPiston, // Revolute   + toggle  — doinker / flipper (piston snaps a hinge between 2 angles)
        LinearPiston,   // Prismatic  + toggle  — cylinder that slides a part in/out
        Fixed,          // welded — removes any mechanism
        PassiveArm,     // Revolute   + nothing — turns only when hit, rubber-bands back (no button)
    }

    // Dropdown order, decoupled from the enum's serialization order the way AxisOrder is: the
    // passive arm sits under the arm motor because that is the choice it is the alternative to.
    private static readonly MechanismKind[] KindOrder =
    {
        MechanismKind.SpinningMotor, MechanismKind.ArmMotor, MechanismKind.PassiveArm,
        MechanismKind.RotatingPiston, MechanismKind.LinearPiston, MechanismKind.Fixed,
    };
    private static readonly string[] KindLabels =
    {
        "Spinning motor (roller / flywheel / intake)",
        "Arm / lift motor (limited hinge)",
        "Passive arm (pushed, not powered — rubber band)",
        "Rotating piston (doinker / flipper)",
        "Linear piston (slides in / out)",
        "Fixed (weld — no mechanism)",
    };
    private static string KindLabel(MechanismKind k) => KindLabels[Mathf.Max(0, Array.IndexOf(KindOrder, k))];

    // PassiveArm is listed explicitly: the `_ => Fixed` default would hide the limits, resolve a
    // Fixed axis and draw no preview for it.
    private static AddMechanismJoint.JointType JointTypeOf(MechanismKind k) => k switch
    {
        MechanismKind.SpinningMotor => AddMechanismJoint.JointType.Continuous,
        MechanismKind.ArmMotor => AddMechanismJoint.JointType.Revolute,
        MechanismKind.PassiveArm => AddMechanismJoint.JointType.Revolute,
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
    // Passive arm only: the rubber band, and how hard it pulls in multiples of the arm's weight.
    [SerializeField] private bool returnToRest = true;
    [SerializeField] private float bandStrength = 3f;
    [SerializeField] private Vector2 scroll;

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
        // Scrolled because the roster plus the form is now taller than the window on a robot with a
        // handful of mechanisms, and IMGUI silently clips instead of scrolling on its own.
        scroll = EditorGUILayout.BeginScrollView(scroll);
        DrawBody();
        EditorGUILayout.EndScrollView();
    }

    private void DrawBody()
    {
        // Everything already on this robot, first — "which of these am I fixing?" is the question
        // this window opens with, and until now the only way to answer it was to know the part's
        // name and find it yourself in the hierarchy.
        RobotMechanisms robot = MechanismBuildUtil.ResolveRobot(childLink);
        GameObject pickedMechanism = MechanismBuildUtil.DrawMechanismRoster(robot, childLink, "Edit");
        if (pickedMechanism != null)
        {
            LoadFrom(pickedMechanism);
            SceneView.RepaintAll();
        }
        EditorGUILayout.Space();

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

        int kindIndex = Mathf.Max(0, Array.IndexOf(KindOrder, mechKind));
        int pickedKind = EditorGUILayout.Popup(new GUIContent("Mechanism Type",
            "What this part does. Spinning / Arm = motor (hold a button to run). Passive arm = no button; " +
            "it turns when something hits it and a rubber band returns it. Rotating / Linear piston = " +
            "pneumatic (press to snap between two ends). Fixed = weld it still."),
            kindIndex, KindLabels);
        MechanismKind newKind = KindOrder[Mathf.Clamp(pickedKind, 0, KindOrder.Length - 1)];
        if (newKind != mechKind)
        {
            // A passive arm is nearly always a flap on a spacer or shaft with a stop at one end, so
            // the first switch to it seeds the form for that: read the axis off the shaft you drop
            // in, and sweep 0..90 away from the drawn pose.
            if (newKind == MechanismKind.PassiveArm)
            {
                axisPreset = AxisPreset.FromAxle;
                lowerLimit = 0f;
                upperLimit = 90f;
            }
            mechKind = newKind;
        }

        AddMechanismJoint.JointType jointType = JointTypeOf(mechKind);
        bool asToggle = mechKind == MechanismKind.RotatingPiston;
        bool isFixed = mechKind == MechanismKind.Fixed;
        bool isPassive = mechKind == MechanismKind.PassiveArm;

        if (isPassive)
            EditorGUILayout.HelpBox(
                "A passive arm has no motor and no button: it turns only when something hits it — usually " +
                "the toggle — and the rubber band pulls it back to the pose it is drawn in. It collides " +
                "with everything it touches, on the robot too; parts drawn bolted through it stay muted " +
                "(the Console lists them on Apply). Drop the spacer or shaft it turns on below, and check " +
                "the blue line.", MessageType.Info);

        // "I move the part but it doesn't correspond when I press play." A built joint is placed by its
        // ANCHORS, not its transform: drag the link in the Scene after building and PhysX snaps it back
        // the instant Play starts. Detect that the selected link's transform has drifted from its anchor
        // frame and offer the one-click re-anchor (the same thing Set Starting Pose does).
        if (childLink != null)
        {
            ArticulationBody selBody = childLink.GetComponent<ArticulationBody>();
            if (AnchorDriftedFromTransform(selBody, out float driftMm))
            {
                EditorGUILayout.HelpBox(
                    $"'{childLink.name}' has been MOVED since its joint was set (its pivot is {driftMm:0.##} u off " +
                    "where the transform now is). Play places a link by its joint anchors, not its transform, so it " +
                    "will snap back the instant you press Play. Re-anchor it here, or re-Apply below, or use Set " +
                    "Starting Pose — all three make where it sits now the joint's rest pose.", MessageType.Warning);
                if (GUILayout.Button("Re-anchor to where it is now"))
                {
                    Undo.RecordObject(selBody, "Re-anchor Joint");
                    MechanismBuildUtil.RederiveParentAnchors(selBody);
                    PassiveArm reArm = childLink.GetComponent<PassiveArm>();
                    if (reArm != null) { Undo.RecordObject(reArm, "Re-anchor Joint"); reArm.BakeDrive(); }
                    EditorUtility.SetDirty(selBody);
                    if (childLink.scene.IsValid()) EditorSceneManager.MarkSceneDirty(childLink.scene);
                    Debug.Log($"Re-anchored '{childLink.name}': where it sits now is its joint rest pose, so it " +
                              "stays put in Play.", childLink);
                }
            }
        }

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

            // Nothing drives a passive arm, so there is no sense to reverse.
            if (!isPassive)
                reverseDirection = EditorGUILayout.Toggle(new GUIContent("Reverse Direction",
                    "Flip the drive sense if the mechanism runs backward for 'forward' input (motor) or starts " +
                    "at the wrong end (piston)."), reverseDirection);
        }

        if (!isFixed && !isPassive)
            autoAssignButton = EditorGUILayout.Toggle(new GUIContent("Auto-Assign Button",
                "After applying, map this mechanism to the next free controller button (motor = " +
                "forward/reverse pair, piston = toggle) so it's drivable without opening Configure Controller."),
                autoAssignButton);

        if (isPassive)
        {
            EditorGUILayout.Space();
            returnToRest = EditorGUILayout.Toggle(new GUIContent("Return to rest (rubber band)",
                "Pull the arm back to its drawn pose after it has been knocked. Off = a free hinge with a " +
                "little friction that stays wherever it was left."), returnToRest);
            if (returnToRest)
                bandStrength = EditorGUILayout.Slider(new GUIContent("Band strength (× arm weight)",
                    "How hard the band pulls, in multiples of the arm's own weight held out at its centre. " +
                    "1 = just enough to lift it, 3 = returns briskly, 10 = nearly rigid."),
                    bandStrength, 1f, 10f);
        }

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

            GameObject[] extras = alsoMove.Count > 0 ? alsoMove.ToArray() : null;
            if (isPassive)
            {
                AddMechanismJoint.ApplyPassiveArm(childLink, axis, effectiveAnchor, lowerLimit, upperLimit,
                    new AddMechanismJoint.PassiveArmOptions
                    {
                        alsoMove = extras,
                        returnToRest = returnToRest,
                        bandStrength = bandStrength,
                    }, useUndo: true);
            }
            else
            {
                var options = new AddMechanismJoint.Options
                {
                    alsoMove = extras,
                    reverseDirection = reverseDirection,
                    actuation = asToggle ? AddMechanismJoint.Actuation.Toggle : AddMechanismJoint.Actuation.Auto,
                };
                AddMechanismJoint.Apply(childLink, jointType, axis, effectiveAnchor, lowerLimit, upperLimit, options, useUndo: true);
            }

            // Map it to a free button so it's drivable immediately (skipped for Fixed, which removed
            // the mechanism, and for a passive arm, which nothing drives). A rotating piston maps like
            // a pneumatic (one toggle button), so pass Prismatic for the button style. Non-fatal: a
            // full map just means the user maps it later.
            string buttonNote = "";
            if (autoAssignButton && !isFixed && !isPassive)
            {
                RobotMechanisms reg = childLink.GetComponentInParent<RobotMechanisms>();
                AddMechanismJoint.JointType buttonType = asToggle ? AddMechanismJoint.JointType.Prismatic : jointType;
                if (reg != null)
                    buttonNote = "\nButton: " + MechanismAutoDetect.AssignButtons(
                        reg.robotId, UrdfPostProcessor.Slugify(childLink.name), buttonType);
            }
            Undo.CollapseUndoOperations(group);

            // Re-read the form from the joint that now exists, so the fields describe reality and
            // pressing Apply twice is a no-op instead of re-inferring the axis from scratch.
            if (!isFixed) LoadFrom(childLink);

            EditorUtility.DisplayDialog(Title,
                $"'{childLink.name}' is now set up as: {KindLabel(mechKind)}" +
                (isFixed ? " (mechanism removed)." : isPassive ? " (no button — it turns when hit)." : ".") +
                buttonNote +
                "\n\nSave the scene, then Validation > Validate Robot Physics to test it.", "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog(Title, e.Message, "OK");
            Debug.LogException(e, childLink);
        }
    }

    // Fills the form from a joint that already exists, so an existing mechanism can be ADJUSTED
    // rather than re-described from scratch.
    //
    // The axis and pivot come back as Custom/typed values read straight off the ArticulationBody
    // rather than as the preset that originally produced them. That is deliberate and it is the
    // whole safety property of the edit path: a preset would be RE-RESOLVED on Apply against
    // today's geometry — "Auto" in particular re-guesses from the part's shape — and a driver who
    // opened this to change a limit by 5 degrees would have the hinge silently move. Reading the
    // joint back means Apply reproduces the joint it found unless the user changed a field.
    private void LoadFrom(GameObject link)
    {
        childLink = link;
        if (link == null) return;

        ArticulationBody body = link.GetComponent<ArticulationBody>();
        if (body == null) return;

        ArticulationDrive d = body.xDrive;
        mechKind = KindOf(link);
        PassiveArm passive = link.GetComponent<PassiveArm>();
        if (passive != null)
        {
            returnToRest = passive.returnToRest;
            bandStrength = passive.bandStrength;
        }

        lowerLimit = d.lowerLimit;
        upperLimit = d.upperLimit;
        axisPreset = AxisPreset.Custom;
        customAxis = JointAxisLocal(body);
        autoPivot = false;
        anchor = body.anchorPosition;
        axlePart = null;
        alsoMove.Clear();   // already folded into the link; re-listing them would be a no-op at best

        MotorActuator motor = link.GetComponent<MotorActuator>();
        reverseDirection = motor != null && motor.invert;
    }

    // Which kind an existing link IS, read off what is on it. Static and internal so the validator
    // can ask the real question the window asks. A link with no body is nothing that moves.
    //
    // The PassiveArm check comes BEFORE the joint-type switch, and the order is load-bearing: a
    // passive arm is a limited revolute with no pneumatic, which the switch would call an arm MOTOR
    // — and Apply would then wire a motor onto it, silently promoting the flap to a powered arm the
    // first time anyone opened it to nudge a limit.
    internal static MechanismKind KindOf(GameObject link)
    {
        ArticulationBody body = link != null ? link.GetComponent<ArticulationBody>() : null;
        if (body == null) return MechanismKind.Fixed;
        if (link.GetComponent<PassiveArm>() != null) return MechanismKind.PassiveArm;

        bool toggle = link.GetComponent<PneumaticActuator>() != null;
        switch (body.jointType)
        {
            case ArticulationJointType.PrismaticJoint:
                return MechanismKind.LinearPiston;
            case ArticulationJointType.RevoluteJoint:
                return body.twistLock == ArticulationDofLock.FreeMotion ? MechanismKind.SpinningMotor
                    : toggle ? MechanismKind.RotatingPiston : MechanismKind.ArmMotor;
            default:
                return MechanismKind.Fixed;
        }
    }

    // The user-facing axis a configured joint was built from — the exact inverse of the
    // anchorRotation ConfigureJointLink writes. A revolute's twist runs along the anchor frame's X,
    // which that code deliberately points down MINUS the chosen axis (matching the URDF importer),
    // so recovering the axis means negating it back.
    private static Vector3 JointAxisLocal(ArticulationBody body)
    {
        Vector3 anchorX = body.anchorRotation * Vector3.right;
        Vector3 axis = body.jointType == ArticulationJointType.PrismaticJoint ? anchorX : -anchorX;
        return axis.sqrMagnitude > 1e-8f ? axis.normalized : Vector3.up;
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
    // Has the selected link been dragged away from where its joint anchors put it? PhysX places a link
    // so its anchor point meets its parent's anchor point in the world and IGNORES the transform, so a
    // link moved after its joint was set snaps back the instant Play runs. `driftWorld` is how far the
    // two anchor points now sit apart, in world units. Fixed joints and the root have no such frame.
    private static bool AnchorDriftedFromTransform(ArticulationBody body, out float driftWorld)
    {
        driftWorld = 0f;
        if (body == null || body.jointType == ArticulationJointType.FixedJoint) return false;
        ArticulationBody parent = null;
        for (Transform p = body.transform.parent; p != null && parent == null; p = p.parent)
            parent = p.GetComponent<ArticulationBody>();
        if (parent == null) return false;

        Vector3 childWorld = body.transform.TransformPoint(body.anchorPosition);
        Vector3 parentWorld = parent.transform.TransformPoint(body.parentAnchorPosition);
        driftWorld = (childWorld - parentWorld).magnitude;

        Quaternion childRot = body.transform.rotation * body.anchorRotation;
        Quaternion parentRot = parent.transform.rotation * body.parentAnchorRotation;
        float angle = Quaternion.Angle(childRot, parentRot);
        return driftWorld > 0.005f || angle > 0.5f;
    }

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

        // Drag the pivot straight from the Scene view — drop a spacer in to get the line, then
        // slide the hinge along it (or off it) by eye. The drag pins the axis it started from as
        // Custom: a preset would be RE-RESOLVED on Apply, and an Auto/FromAxle pivot would snap
        // back to the inferred one, undoing the drag the moment it mattered.
        EditorGUI.BeginChangeCheck();
        Vector3 movedPivot = Handles.PositionHandle(pivotW, Quaternion.identity);
        if (EditorGUI.EndChangeCheck())
        {
            customAxis = axisLocal;
            axisPreset = AxisPreset.Custom;
            autoPivot = false;
            anchor = childLink.transform.InverseTransformPoint(movedPivot);
            pivotW = movedPivot;
            Repaint();
        }

        float h = HandleUtility.GetHandleSize(pivotW);
        Vector3 center = MechanismBuildUtil.BoundsCenterOrOrigin(childLink);
        Vector3 arm = Vector3.ProjectOnPlane(center - pivotW, axisW);
        float reach = Mathf.Max(arm.magnitude, h * 2.5f);
        var color = new Color(0.35f, 0.7f, 1f);
        string kindTag = mechKind == MechanismKind.PassiveArm ? " (passive)" : "";

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
        //
        // About the TWIST axis, which is minus the chosen axis — ConfigureJointLink points the
        // anchor frame's X down -axis to match the URDF importer, and the joint's positive angle is
        // a right-handed rotation about that frame's X. Drawing these arcs about +axisW instead (as
        // this did until 2026-07-29) mirrored them: a 0..90 arm was previewed sweeping the opposite
        // way from where it would actually go, which is exactly the thing this preview exists to
        // stop being a surprise. Symmetric limits hid it.
        Vector3 twistW = -axisW;
        Handles.color = new Color(color.r, color.g, color.b, 0.18f);
        Handles.DrawSolidArc(pivotW, twistW, Quaternion.AngleAxis(lowerLimit, twistW) * arm,
            upperLimit - lowerLimit, arm.magnitude);
        Handles.color = color;
        Handles.DrawAAPolyLine(2f, pivotW, pivotW + arm);                                   // rest pose
        Vector3 lowArm = Quaternion.AngleAxis(lowerLimit, twistW) * arm;
        Vector3 highArm = Quaternion.AngleAxis(upperLimit, twistW) * arm;
        Handles.DrawDottedLine(pivotW, pivotW + lowArm, 3f);
        Handles.DrawDottedLine(pivotW, pivotW + highArm, 3f);
        // Naming the ends in the view is what makes "should it start down or up?" answerable by
        // looking, which is the question Set Starting Pose then acts on.
        Handles.Label(pivotW + lowArm * 1.06f, $"{lowerLimit:0}° (lower)");
        Handles.Label(pivotW + highArm * 1.06f, $"{upperLimit:0}° (upper)");
        Handles.Label(pivotW + arm * 1.12f, $"{childLink.name}{kindTag}: swings {lowerLimit:0}°..{upperLimit:0}°");
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

    // Authoring extras for ApplyPassiveArm. No direction to reverse and no actuation to choose:
    // nothing drives a passive arm.
    public struct PassiveArmOptions
    {
        public GameObject[] alsoMove;   // plain parts to fold into the one hinged link
        public bool returnToRest;       // the rubber band
        public float bandStrength;      // x the arm's own weight; <= 0 takes the component default
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
            // A weld has nothing to spring back to. WireMechanism strips a band on the powered
            // paths; this is the one path that never reaches it.
            MechanismBuildUtil.RemoveComponents<PassiveArm>(link, useUndo);
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

    // The passive-arm counterpart of Apply: the same joint core, and then — instead of the actuator
    // and registry record that make a link a button mechanism — a PassiveArm with its band sized
    // and baked. Public, and useUndo=false capable, so PassiveArmValidation drives the path the
    // window drives. Returns the arm. Throws on any precondition failure.
    public static PassiveArm ApplyPassiveArm(GameObject link, Vector3 axis, Vector3 anchor,
        float lowerLimit, float upperLimit, PassiveArmOptions options, bool useUndo)
    {
        if (link == null) throw new ArgumentNullException(nameof(link));

        RobotMechanisms registry = link.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
            throw new InvalidOperationException(
                $"'{link.name}' is not under a set-up robot (no RobotMechanisms). Run " +
                "Set Up Imported Robot first.");
        GameObject root = registry.gameObject;

        ArticulationBody body = ConfigureJointLink(link, JointType.Revolute, axis, anchor, lowerLimit, upperLimit,
            new Options { alsoMove = options.alsoMove }, registry, useUndo);

        // Whatever powered this link before goes, record first: a passive arm is not on the button
        // router's list, and a stale record would keep offering the config screen a button whose
        // actuator is about to be destroyed. Swept by WHERE the actuator lives, not by slugged
        // name, for the reason Build Chain gives — duplicate CAD names would let a name-keyed
        // removal destroy some other mechanism's motor.
        if (registry.mechanisms != null)
        {
            foreach (RobotMechanisms.Mechanism stale in registry.mechanisms.ToArray())
            {
                if (stale == null || MechanismBuildUtil.MechanismLink(stale) != link) continue;
                UrdfPostProcessor.RemoveMechanism(registry, stale.id, useUndo);
                MechanismBuildUtil.ClearMechanismBindings(registry.robotId, stale.id);
                // The player's style choice for a mechanism that no longer exists — the cleanup
                // Delete Mechanism does, so entries cannot accumulate under a dead id. Saved only
                // when something was actually dropped, so a build never writes an empty map.
                ButtonMap map = ControllerMapSettings.Load(registry.robotId);
                int stylesBefore = map.styles != null ? map.styles.Count : 0;
                ControllerMapSettings.RemoveStyle(map, stale.id);
                if ((map.styles != null ? map.styles.Count : 0) != stylesBefore)
                    ControllerMapSettings.Save(registry.robotId, map);
            }
        }
        // An actuator with no record (hand-added), and the blanket: an arm that ignores its whole
        // robot cannot be pushed by the toggle, and PassiveArm re-decides those pairs itself.
        MechanismBuildUtil.RemoveComponents<MotorActuator>(link, useUndo);
        MechanismBuildUtil.RemoveComponents<PneumaticActuator>(link, useUndo);
        MechanismBuildUtil.RemoveComponents<IgnoreRobotSelfCollision>(link, useUndo);

        PassiveArm arm = MechanismBuildUtil.AddOrGet<PassiveArm>(link, useUndo);
        if (useUndo) Undo.RecordObject(arm, "Add or Fix Mechanism Joint");
        arm.body = body;
        arm.returnToRest = options.returnToRest;
        arm.bandStrength = options.bandStrength > 0f ? options.bandStrength : PassiveArm.DefaultBandStrength;

        // The band is sized against gravity's torque on the arm held out: mass x g x the
        // PERPENDICULAR distance from the hinge line to the centre of what it moves. The hinge is
        // read back off the body's anchor frame — the frame Set Starting Pose reads, and the joint
        // itself — rather than off the axis this was handed, which the core normalises and
        // re-anchors on the way in.
        if (!StartingPose.TryJointFrame(body, out Vector3 axisW, out Vector3 pivotW))
            throw new InvalidOperationException($"'{link.name}' did not come out of the joint core as a hinge.");
        Vector3 centreW = MechanismBuildUtil.BoundsCenterOrOrigin(link);
        float leverArm = Vector3.ProjectOnPlane(centreW - pivotW, axisW).magnitude;
        arm.SizeBand(leverArm, Mathf.Abs(Physics.gravity.y));
        arm.BakeDrive();

        // Say now which robot parts are drawn through the arm: those pairs stay muted in play, and
        // "why doesn't the flap hit the bracket" is a question best answered at build time.
        List<string> bolted = arm.RestOverlaps();
        Debug.Log($"Passive arm '{link.name}': {arm.DescribeBand()}, cap {arm.bandForceLimit:0.#} " +
                  $"(mass {body.mass:0.###} kg, {leverArm:0.##} u lever arm)" +
                  (bolted.Count == 0
                      ? "; nothing on the robot overlaps it at rest, so it collides with every other link."
                      : $"; bolted through (these stay muted): {string.Join(", ", bolted)}"), link);

        UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, root.name, registry);

        EditorUtility.SetDirty(body);
        EditorUtility.SetDirty(registry);
        EditorUtility.SetDirty(arm);
        if (root.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.scene);
        return arm;
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
        MechanismBuildUtil.RederiveParentAnchors(body);

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

    // Nearest ArticulationBody strictly above a transform — the parent link a joint connects to.
    // Used before the split link has its own body, to confirm there's a rigged chassis to joint it to.
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
