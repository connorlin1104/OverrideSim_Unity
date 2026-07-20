using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Role-assignment builder for a CLAW — the mechanism most designs in this game share. You drop your
// CAD parts into named buckets and it rigs the whole thing; nothing here needs hand-jointing.
//
// A claw has two independent pneumatic functions, each landing on its own button:
//
//   FLIP  — the whole assembly rotates (typically 180°) so a stack picked up upside down ends up the
//           right way up: pick a cup sitting on a pin, flip, and the pin is on the bottom.
//   CLAMP — the jaws close. Usually two mirrored halves; only the first gets a button, the rest track
//           it through a JointCoupler so one press closes the whole claw.
//
// WHY REAL JOINTS. The DR4B builder fakes its motion with transform followers because a four-bar is a
// closed kinematic loop and an ArticulationBody tree can't express one. A claw is not a loop — it's
// exactly a tree, so it gets real links with live colliders:
//
//   chassis
//   +-- FlipLink              revolute 0 -> flipAngle, PneumaticActuator (Toggle)
//       +-- ClampLink 1       revolute 0 -> closeAngle, PneumaticActuator (Toggle)
//       +-- ClampLink 2..n    revolute, JointCoupler(Position, ratio -1) -> mirrors half 1
//       +-- ClawMouth         trigger + ClawGrab, ClawHoldPoint
//       +-- clamp cylinder    cosmetic, PneumaticSlideFollower
//   flip cylinder             cosmetic, PneumaticSlideFollower (stays on the chassis)
//
// Nothing passes through the jaws, and because the clamp links are CHILDREN of the flip link, "the
// whole claw flips together" falls out of the articulation tree for free.
//
// THE CYLINDERS are cosmetic — they carry no joint. Both halves slide along their own centerline:
// the rod extends and the body recoils the other way, because most claws don't bolt the barrel down
// and letting it float keeps the assembly's center of mass put. The Recoil slider splits the travel
// (0 = barrel bolted, 0.5 = balanced, 1 = the barrel does all the moving).
//
// Usage: open the robot PREFAB (this tool reparents), fill the buckets, Build. If you leave a pivot
// slot empty the build creates a marker for you — drag it onto the edge of the plastic where the part
// should hinge, then Build again to bake it in.
public class ClawBuilderWindow : EditorWindow
{
    private const string Title = "Build Claw";

    private static readonly string[] CylinderSizeLabels =
        { "Large (90 mm)", "Regular (50 mm)", "Small (20 mm)", "Custom" };
    private static readonly float[] CylinderSizeStrokes = { 90f, 50f, 20f };

    [SerializeField] private string displayName = "Claw";

    [SerializeField] private List<GameObject> flippingParts = new List<GameObject>();
    [SerializeField] private float flipAngleDeg = 180f;
    [SerializeField] private Transform flipPivot;
    [SerializeField] private ClawRig.HingeAxis flipAxisPreset = ClawRig.HingeAxis.Auto;
    [SerializeField] private Vector3 flipCustomAxis = Vector3.right;
    [SerializeField] private bool flipStartExtended;
    [SerializeField] private float flipStiffness = 20000f;
    [SerializeField] private float flipDamping = 500f;
    [SerializeField] private GameObject flipCylinderBody;
    [SerializeField] private GameObject flipCylinderRod;
    [SerializeField] private float flipStrokeMm = 90f;
    [SerializeField] private float flipRecoil = 0.5f;
    [SerializeField] private bool flipCylinderReverse;

    [SerializeField] private List<ClawRig.ClampSection> clampSections = new List<ClawRig.ClampSection>();
    [SerializeField] private ClawRig.JawRest clampModelled = ClawRig.JawRest.ModelledOpen;
    [SerializeField] private bool clampStartClosed;
    [SerializeField] private float clampTrimDeg;
    [SerializeField] private float clampStiffness = 20000f;
    [SerializeField] private float clampDamping = 500f;
    [SerializeField] private GameObject clampCylinderBody;
    [SerializeField] private GameObject clampCylinderRod;
    [SerializeField] private float clampStrokeMm = 50f;
    [SerializeField] private float clampRecoil = 0.5f;
    [SerializeField] private bool clampCylinderReverse;

    [SerializeField] private bool enableGrab = true;
    [SerializeField] private bool grabPassThrough = true;
    [SerializeField] private bool grabAutoUpright = true;
    [SerializeField] private Transform holdPoint;
    [SerializeField] private bool autoAssignButtons = true;

    [SerializeField] private bool showAdvanced;
    [SerializeField] private bool showPreview = true;
    private Vector2 scroll;

    // The preview draws into the Scene view for as long as this window is open. Which hinge axis a claw
    // needs isn't derivable from its CAD, so the tool's job is to SHOW you the one it's about to use
    // rather than to keep guessing better.
    void OnEnable() { SceneView.duringSceneGui += OnSceneGUI; }
    void OnDisable() { SceneView.duringSceneGui -= OnSceneGUI; }

    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Build Claw (roles)", false, 24)]
    private static void Open()
    {
        ClawBuilderWindow window = GetWindow<ClawBuilderWindow>(true, Title, true);
        window.minSize = new Vector2(480f, 620f);
        window.Show();
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.HelpBox(
            "Rigs a claw from your CAD parts and gives you two buttons: FLIP turns the whole claw over " +
            "(180° puts a stack the right way up), CLAMP closes the jaws.\n\n" +
            "Every part goes in exactly ONE bucket — a part listed twice is an error, because it can " +
            "only hinge once. A typical claw:\n" +
            "   Flipping parts  =  the mount the jaws hang off (or the group the claw sits in)\n" +
            "   Clamp half 1 / 2  =  the left jaw / the right jaw (tick Mirror on the second)\n" +
            "   Each cylinder  =  its own barrel and rod\n" +
            "The jaws end up as children of the flip link automatically, so they flip with it — you " +
            "don't list them in the flip bucket to make that happen.\n\n" +
            "Both halves are optional: leave the flip buckets empty for a claw that only clamps. Leave a " +
            "pivot empty and the build drops a marker to drag onto the plastic's edge; Build again to " +
            "bake it in.", MessageType.Info);

        showPreview = EditorGUILayout.ToggleLeft(new GUIContent(
            "Show hinges in the Scene view",
            "Draws the axis and the swing arc for every hinge below, live, before you build — and puts " +
            "a drag handle on each pivot. If a jaw sweeps the wrong way here, it will sweep the wrong " +
            "way after you build, so fix it now."), showPreview);
        if (showPreview)
            EditorGUILayout.HelpBox(
                "Look at the Scene view: each hinge draws a coloured line (the axis it turns about) and " +
                "a shaded wedge (where it swings to). Drag the handle to move a pivot. Change \"Which " +
                "way it turns\" until the wedge matches what the real claw does.", MessageType.None);

        RobotMechanisms registry = ResolveRegistry();
        DrawExistingClaws(registry);

        // This tool reparents parts into an articulation tree, which prefab instances can't record as
        // overrides — same requirement (and same wording) as Build Chain.
        if (PrefabStageUtility.GetCurrentPrefabStage() == null)
            EditorGUILayout.HelpBox(
                "You're not in Prefab Mode. This tool reparents parts, which a prefab instance can't " +
                "store as an override — open the robot prefab first (double-click it) or your work " +
                "won't survive.", MessageType.Warning);

        EditorGUILayout.Space();
        displayName = EditorGUILayout.TextField(new GUIContent("Claw name",
            "Shown on the Configure Controller screen, suffixed with Flip / Clamp."), displayName);

        // --- Flip -------------------------------------------------------------------------------
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Flip (optional)", EditorStyles.boldLabel);
        DrawGoList("1. Flipping parts",
            "What the claw hangs off — its mount plate, or the group the whole claw sits in. Anything " +
            "inside comes along for free, so do NOT also list the jaws here: they go in the clamp " +
            "halves below and get reparented under this link. Leave empty for a claw that only clamps.",
            flippingParts);

        if (CountNonNull(flippingParts) > 0)
        {
            flipAngleDeg = EditorGUILayout.FloatField(new GUIContent("Flip angle (deg)",
                "How far the claw turns when the cylinder fires. 180 puts a stack upside down."), flipAngleDeg);
            flipPivot = (Transform)EditorGUILayout.ObjectField(new GUIContent("Flip pivot",
                "The POINT the claw turns about — usually the shaft it hangs from. Empty = the build " +
                "creates a ClawFlipPivot marker to drag. Which WAY it turns is the setting below."),
                flipPivot, typeof(Transform), true);
            DrawAxis(ref flipAxisPreset, ref flipCustomAxis, isFlip: true);

            EditorGUILayout.LabelField(new GUIContent("2. Flip cylinder (cosmetic — no joint)",
                "The rod slides out and the body recoils the other way. Neither gets a joint; they're " +
                "posed off the flip joint's progress."), EditorStyles.miniBoldLabel);
            DrawCylinder(ref flipCylinderBody, ref flipCylinderRod, ref flipStrokeMm,
                ref flipRecoil, ref flipCylinderReverse);
        }

        // --- Clamp ------------------------------------------------------------------------------
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Clamp (optional)", EditorStyles.boldLabel);
        DrawClampSections();

        WarnIfPivotsCoincide();

        if (clampSections.Count > 0)
        {
            clampModelled = (ClawRig.JawRest)EditorGUILayout.EnumPopup(new GUIContent(
                "CAD draws the jaws",
                "How the claw sits in your model, BEFORE anything moves. A joint rests where the " +
                "modeller left it and travels away from that pose, so this decides which end of the " +
                "swing counts as shut — and therefore when the claw grabs."), clampModelled);
            EditorGUILayout.LabelField(" ", clampModelled == ClawRig.JawRest.ModelledClosed
                ? "firing the piston OPENS the jaws; it grabs as they shut again"
                : "firing the piston CLOSES the jaws; it grabs as they shut",
                EditorStyles.miniLabel);

            bool shut = clampModelled == ClawRig.JawRest.ModelledClosed;
            clampTrimDeg = EditorGUILayout.FloatField(new GUIContent(
                shut ? "Closed trim (deg)" : "Resting trim (deg)",
                "Nudges where the jaws SIT before the piston fires, on the same axis and in the same " +
                "sense as the swing angle. Leave at 0 to rest exactly where the CAD drew them; " +
                "-10 carries them 10° further round, which is how you tighten a model whose closed " +
                "pose is a little too wide."), clampTrimDeg);
            if (Mathf.Abs(clampTrimDeg) > 1e-3f)
            {
                // "Tighter" means the trim runs OPPOSITE to the swing. Which of open/shut that lands
                // on depends on the modelled pose: the swing opens a claw drawn shut, and closes one
                // drawn open, so the same opposing trim reads the other way round.
                float swing = clampSections.Count > 0 && clampSections[0] != null
                    ? clampSections[0].closeAngleDeg : 1f;
                bool opposesSwing = clampTrimDeg * swing < 0f;
                bool tighter = shut ? opposesSwing : !opposesSwing;
                EditorGUILayout.LabelField(" ",
                    $"jaws rest {Mathf.Abs(clampTrimDeg):0.#}° {(tighter ? "TIGHTER" : "wider")} than the CAD",
                    EditorStyles.miniLabel);
            }

            EditorGUILayout.LabelField(new GUIContent("Clamp cylinder (cosmetic — no joint)",
                "One cylinder drives every half."), EditorStyles.miniBoldLabel);
            DrawCylinder(ref clampCylinderBody, ref clampCylinderRod, ref clampStrokeMm,
                ref clampRecoil, ref clampCylinderReverse);

            EditorGUILayout.Space();
            enableGrab = EditorGUILayout.Toggle(new GUIContent("Grab pieces when closed",
                "Hold whatever is between the jaws while the claw is shut, so it survives a drive and a " +
                "flip. Off = the jaws are solid but can't retain anything (pieces are minimum-friction, " +
                "so squeezing alone never grips)."), enableGrab);
            if (enableGrab)
            {
                holdPoint = (Transform)EditorGUILayout.ObjectField(new GUIContent("Hold point",
                    "WHERE a grabbed piece is carried. Empty = the build creates a ClawHoldPoint in " +
                    "the middle of the jaws; drag it (the Scene view gives it a handle) to move where " +
                    "pieces sit, and its UP axis is which way they stand."),
                    holdPoint, typeof(Transform), true);
                grabAutoUpright = EditorGUILayout.Toggle(new GUIContent("Stand pieces up",
                    "Align each grabbed piece's long axis with the hold point's up, measured per " +
                    "piece. Without this a pin lying on its side is carried lying on its side, so the " +
                    "same grab looks right on an upright piece and sideways on a match-loaded one."),
                    grabAutoUpright);
                grabPassThrough = EditorGUILayout.Toggle(new GUIContent("Held piece passes through",
                    "A grabbed piece stops colliding with anything until it's dropped, and eases into " +
                    "the hold point rather than freezing where it was caught. Keeps a piece snatched " +
                    "at a bad angle — half through a jaw, or mid-topple on the floor — from wedging " +
                    "itself and throwing the robot. Off = it only ignores the claw and stays solid to " +
                    "the field."), grabPassThrough);
            }
        }

        // --- Options ----------------------------------------------------------------------------
        EditorGUILayout.Space();
        autoAssignButtons = EditorGUILayout.Toggle(new GUIContent("Assign buttons automatically",
            "Give Flip and Clamp a free button each. Off = leave the current mapping alone and set it " +
            "yourself in Configure Controller."), autoAssignButtons);

        showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced", true);
        if (showAdvanced)
        {
            using (new EditorGUI.IndentLevelScope())
            {
                flipStartExtended = EditorGUILayout.Toggle(new GUIContent("Start flipped",
                    "Begin the match with the claw already turned over."), flipStartExtended);
                clampStartClosed = EditorGUILayout.Toggle(new GUIContent("Start clamped",
                    "Begin the match with the jaws shut — whichever end of the travel that is for the " +
                    "way your CAD is drawn."), clampStartClosed);
                EditorGUILayout.LabelField(new GUIContent("Joint feel",
                    "Pneumatics snap hard. Lower the stiffness or raise the damping if a 180° flip slams."),
                    EditorStyles.miniBoldLabel);
                flipStiffness = EditorGUILayout.FloatField("Flip stiffness", flipStiffness);
                flipDamping = EditorGUILayout.FloatField("Flip damping", flipDamping);
                clampStiffness = EditorGUILayout.FloatField("Clamp stiffness", clampStiffness);
                clampDamping = EditorGUILayout.FloatField("Clamp damping", clampDamping);
            }
        }

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Auto-fill roles by name (best guess)")) AutoFill(registry);
            if (GUILayout.Button(new GUIContent("Re-seed markers",
                    "Throw away the pivots, the mouth trigger and the hold point, and let the next " +
                    "build place them again. A marker you've dragged is deliberately kept across " +
                    "rebuilds — which also means a claw built earlier keeps wherever its markers " +
                    "were first put."),
                    GUILayout.Width(120)))
            {
                int removed = ClawSetup.ResetPivotMarkers(registry, useUndo: true);
                flipPivot = null;
                foreach (ClawRig.ClampSection s in clampSections)
                    if (s != null) s.pivot = null;
                SceneView.RepaintAll();
                EditorUtility.DisplayDialog(Title, removed == 0
                    ? "No generated markers to clear."
                    : $"Cleared {removed} marker(s), including the mouth and hold point. Build the " +
                      "claw to place them again.", "OK");
            }
            if (GUILayout.Button("Clear form", GUILayout.Width(90))) ClearForm();
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Build / Update Claw", GUILayout.Height(32)))
        {
            try
            {
                string report = ClawSetup.Build(BuildOptions(), true);
                EditorUtility.DisplayDialog(Title, report, "OK");
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog(Title, "Couldn't build the claw.\n\n" + e.Message, "OK");
                Debug.LogException(e);
            }
        }

        EditorGUILayout.EndScrollView();

        // Any edit above can change what the hinges look like, so keep the Scene view in step.
        if (GUI.changed && showPreview) SceneView.RepaintAll();
    }

    // Two jaws that hinge about the same point don't close on a piece, they scissor through each other.
    // It's the failure the auto-seeded pivots actually produce — both markers land on the point of their
    // jaw nearest the claw's centre, which for jaws that meet at the front is very nearly one point — so
    // it's worth saying out loud rather than leaving to be discovered in Play.
    private void WarnIfPivotsCoincide()
    {
        RobotMechanisms registry = ResolveRegistry();
        if (registry == null || clampSections.Count < 2) return;

        GameObject flipRoot = ClawSetup.FirstNonNull(flippingParts);
        GameObject towardRef = flipRoot != null ? flipRoot : registry.gameObject;

        var spots = new List<(Vector3 pos, float size, int index)>();
        for (int i = 0; i < clampSections.Count; i++)
        {
            ClawRig.ClampSection s = clampSections[i];
            GameObject link = s == null ? null : ClawSetup.FirstNonNull(s.parts);
            if (link == null) continue;
            if (!ClawSetup.TryPreviewHinge(link, s.pivot, ClawSetup.PreviewClampPivotPrefix + (i + 1),
                    ClawSetup.ClampPivotSeed(link, towardRef), s.axisPreset, s.customAxis, false,
                    out Vector3 p, out _, out _))
                continue;
            float size = MechanismBuildUtil.TryBounds(link, out Bounds b) ? b.size.magnitude : 1f;
            spots.Add((p, size, i + 1));
        }

        for (int a = 0; a < spots.Count; a++)
            for (int b = a + 1; b < spots.Count; b++)
            {
                float gap = Vector3.Distance(spots[a].pos, spots[b].pos);
                float tooClose = 0.1f * Mathf.Max(spots[a].size, spots[b].size);
                if (gap > tooClose) continue;
                EditorGUILayout.HelpBox(
                    $"Half {spots[a].index} and half {spots[b].index} hinge about almost the same point " +
                    $"({gap:0.##} units apart). Two jaws need their own pins, one on each side, or they'll " +
                    "sweep through each other instead of closing on a piece. Drag each pivot onto its " +
                    "own hinge — the Scene view shows both.", MessageType.Warning);
                return;
            }
    }

    // --- Scene-view preview ------------------------------------------------------------------------

    // Draws, for every hinge the form describes: the axis it will turn about, the arc it will sweep,
    // and a ghost of where the parts end up — plus a drag handle on each pivot. Nothing here mutates
    // anything except a pivot you deliberately drag.
    private void OnSceneGUI(SceneView view)
    {
        if (!showPreview) return;
        RobotMechanisms registry = ResolveRegistry();
        if (registry == null) return;

        GameObject flipRoot = ClawSetup.FirstNonNull(flippingParts);
        if (flipRoot != null)
            DrawHinge(flipRoot, flipPivot, ClawSetup.PreviewFlipPivotName,
                ClawSetup.FlipPivotSeed(flippingParts, clampSections, clampCylinderBody, clampCylinderRod),
                flipAxisPreset, flipCustomAxis, true, flipAngleDeg, "Flip",
                new Color(0.35f, 0.7f, 1f));

        DrawHoldPoint(registry);

        GameObject towardRef = flipRoot != null ? flipRoot : registry.gameObject;
        for (int i = 0; i < clampSections.Count; i++)
        {
            ClawRig.ClampSection s = clampSections[i];
            GameObject link = s == null ? null : ClawSetup.FirstNonNull(s.parts);
            if (link == null) continue;
            float signed = (s.mirror ? -1f : 1f) * s.closeAngleDeg;
            DrawHinge(link, s.pivot, ClawSetup.PreviewClampPivotPrefix + (i + 1),
                ClawSetup.ClampPivotSeed(link, towardRef),
                s.axisPreset, s.customAxis, false, signed, $"Half {i + 1}",
                i == 0 ? new Color(0.4f, 1f, 0.5f) : new Color(1f, 0.8f, 0.3f));
        }
    }

    // The hold point is a bare empty, so it can't be clicked in the Scene view and is easy to miss in
    // the hierarchy — yet it's the one marker whose PLACEMENT the player feels directly, since it's
    // where a grabbed piece ends up and its up axis is which way that piece stands. So the tool draws
    // it and hands over a drag handle, the same deal as the pivots.
    private void DrawHoldPoint(RobotMechanisms registry)
    {
        if (!enableGrab) return;
        Transform hold = holdPoint != null ? holdPoint : FindHoldPoint(registry);
        if (hold == null) return;

        float handle = HandleUtility.GetHandleSize(hold.position);
        Handles.color = new Color(0.2f, 1f, 0.5f);
        Handles.SphereHandleCap(0, hold.position, Quaternion.identity, handle * 0.22f, EventType.Repaint);
        // Its UP is not decoration: Stand pieces up aligns each piece's long axis to this arrow.
        Handles.ArrowHandleCap(0, hold.position, Quaternion.LookRotation(hold.up), handle, EventType.Repaint);
        Handles.Label(hold.position + hold.up * handle * 1.1f, "Hold point (pieces stand along this)");

        EditorGUI.BeginChangeCheck();
        Vector3 moved = Handles.PositionHandle(hold.position, hold.rotation);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(hold, "Move claw hold point");
            hold.position = moved;
        }
    }

    private static Transform FindHoldPoint(RobotMechanisms registry)
    {
        ClawGrab grab = registry.GetComponentInChildren<ClawGrab>(true);
        if (grab != null && grab.holdPoint != null) return grab.holdPoint;
        foreach (Transform t in registry.GetComponentsInChildren<Transform>(true))
            if (t.name == ClawSetup.PreviewHoldName) return t;
        return null;
    }

    private void DrawHinge(GameObject link, Transform assigned, string markerName, Vector3 seedPoint,
        ClawRig.HingeAxis preset, Vector3 custom, bool isFlip, float angleDeg, string label, Color color)
    {
        if (!ClawSetup.TryPreviewHinge(link, assigned, markerName, seedPoint, preset, custom, isFlip,
                out Vector3 pivot, out Vector3 axis, out Transform pivotTf))
            return;

        float handle = HandleUtility.GetHandleSize(pivot);
        Vector3 center = MechanismBuildUtil.BoundsCenterOrOrigin(link);

        // The lever arm is what actually swings: the part's centre measured perpendicular to the axis.
        // Drawing the arc on that radius is what makes "closes inward" vs "pitches up" obvious at a
        // glance, which is the whole point of this preview.
        Vector3 arm = Vector3.ProjectOnPlane(center - pivot, axis);
        float reach = Mathf.Max(arm.magnitude, handle * 2f);

        Handles.color = color;
        Handles.DrawAAPolyLine(4f, pivot - axis * reach, pivot + axis * reach);
        Handles.SphereHandleCap(0, pivot, Quaternion.identity, handle * 0.18f, EventType.Repaint);

        if (arm.sqrMagnitude > 1e-8f)
        {
            Handles.color = new Color(color.r, color.g, color.b, 0.18f);
            Handles.DrawSolidArc(pivot, axis, arm.normalized, angleDeg, arm.magnitude);

            Handles.color = color;
            Vector3 swung = pivot + Quaternion.AngleAxis(angleDeg, axis) * arm;
            Handles.DrawAAPolyLine(2f, pivot, pivot + arm);
            Handles.DrawDottedLine(pivot, swung, 4f);
            Handles.SphereHandleCap(0, swung, Quaternion.identity, handle * 0.14f, EventType.Repaint);
            Handles.Label(swung + Vector3.up * handle * 0.3f, $"{label} → {angleDeg:0}°");
        }
        else if (isFlip)
        {
            // A flip hinges through the middle of what it turns, so there IS no lever arm — that's
            // correct, not a mistake to warn about. Show the plane it sweeps instead.
            Handles.DrawWireDisc(pivot, axis, reach);
            Handles.Label(pivot + axis * reach, $"{label} → {angleDeg:0}° about this line, turning in place");
        }
        else
        {
            Handles.Label(pivot + axis * reach, $"{label} (pivot is at the part's centre — drag it out)");
        }

        // Drag the real marker straight from the Scene view; Build bakes wherever you leave it.
        if (pivotTf != null)
        {
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(pivotTf.position, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(pivotTf, "Move claw pivot");
                pivotTf.position = moved;
            }
        }
    }

    // --- Sub-editors -----------------------------------------------------------------------------

    private void DrawGoList(string label, string tip, List<GameObject> list)
    {
        EditorGUILayout.LabelField(new GUIContent(label, tip), EditorStyles.miniBoldLabel);
        for (int i = 0; i < list.Count; i++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                list[i] = (GameObject)EditorGUILayout.ObjectField(list[i], typeof(GameObject), true);
                if (GUILayout.Button("X", GUILayout.Width(24))) { list.RemoveAt(i); i--; }
            }
        }
        if (GUILayout.Button("Add", GUILayout.Width(70))) list.Add(null);
    }

    // The axis picker never names the PART's own X/Y/Z, because a joint axis lives in the link's local
    // frame and imported CAD frames are arbitrary — "Y" is only "up" by luck. It offers two frames you
    // can actually reason about: the robot's own directions, and the scene's coloured gizmo arrows.
    private void DrawAxis(ref ClawRig.HingeAxis preset, ref Vector3 custom, bool isFlip)
    {
        preset = (ClawRig.HingeAxis)EditorGUILayout.EnumPopup(new GUIContent("Which way it turns",
            isFlip
                ? "The line the claw turns about when it flips over."
                : "The line this jaw swings about."), preset);

        if (preset == ClawRig.HingeAxis.Auto)
            EditorGUILayout.LabelField(" ", isFlip
                ? "= rolls over (robot front/back pins) — a guess; check the preview"
                : "= closes inward (robot up/down pins)", EditorStyles.miniLabel);
        if (preset == ClawRig.HingeAxis.Custom)
            custom = EditorGUILayout.Vector3Field("Custom axis", custom);

        // The escape hatch, spelled out: watch the preview turn the wrong way, note which coloured
        // arrow it SHOULD have used, pick that. No reasoning about local frames required.
        EditorGUILayout.LabelField(" ", "wrong way? pick the Scene X/Y/Z that matches the gizmo arrow",
            EditorStyles.miniLabel);
    }

    // Cylinder role slots + the three real VEX bore/stroke classes, matching Build Pneumatic's picker.
    private void DrawCylinder(ref GameObject body, ref GameObject rod, ref float strokeMm,
        ref float recoil, ref bool reverse)
    {
        body = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Cylinder body",
            "The barrel. It recoils backwards as the rod extends."), body, typeof(GameObject), true);
        rod = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Cylinder rod",
            "The part that extends."), rod, typeof(GameObject), true);
        if (body == null && rod == null) return;

        int index = SizeIndexFromStroke(strokeMm);
        int picked = EditorGUILayout.Popup(new GUIContent("Cylinder size (stroke)",
            "How far the rod travels — the real VEX classes, or a free-form value."), index, CylinderSizeLabels);
        if (picked < CylinderSizeStrokes.Length) strokeMm = CylinderSizeStrokes[picked];
        else strokeMm = EditorGUILayout.FloatField(new GUIContent("Custom stroke (mm)",
            "How far the rod extends, in millimetres."), strokeMm);
        EditorGUILayout.LabelField(" ", $"= {strokeMm / PneumaticSetup.MmPerUnit:0.###} world units");

        recoil = EditorGUILayout.Slider(new GUIContent("Body recoil",
            "How much of the travel the BODY takes instead of the rod. 0 = barrel bolted down, " +
            "0.5 = balanced (the cylinder's midpoint holds still), 1 = the barrel does all the moving."),
            recoil, 0f, 1f);
        reverse = EditorGUILayout.Toggle(new GUIContent("Reverse slide",
            "Tick if the cylinder retracts when it should extend."), reverse);
    }

    private void DrawClampSections()
    {
        int removeAt = -1;
        for (int i = 0; i < clampSections.Count; i++)
        {
            ClawRig.ClampSection section = clampSections[i];
            if (section == null) { clampSections[i] = section = new ClawRig.ClampSection(); }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(i == 0
                        ? "Half 1 — driven (the piston turns this one)"
                        : $"Half {i + 1} — follows half 1", EditorStyles.boldLabel);
                    if (GUILayout.Button("X", GUILayout.Width(24))) removeAt = i;
                }

                DrawGoList("Clamping parts",
                    "The parts of this jaw half. The first is the driven link; the rest are welded into it.",
                    section.parts);
                bool fromShut = clampModelled == ClawRig.JawRest.ModelledClosed;
                section.closeAngleDeg = EditorGUILayout.FloatField(new GUIContent(
                    fromShut ? "Open angle (deg)" : "Close angle (deg)",
                    fromShut
                        ? "How far this half swings OPEN when the cylinder fires — your CAD already has " +
                          "it shut, so the travel is the opening stroke."
                        : "How far this half swings shut when the cylinder fires."), section.closeAngleDeg);
                section.pivot = (Transform)EditorGUILayout.ObjectField(new GUIContent("Pivot point",
                    "The POINT this half hinges about — drag it onto the edge of the plastic. Empty = " +
                    "the build creates a marker for you. Which WAY it turns is the setting below."),
                    section.pivot, typeof(Transform), true);
                DrawAxis(ref section.axisPreset, ref section.customAxis, isFlip: false);
                section.mirror = EditorGUILayout.Toggle(new GUIContent(
                    i == 0 ? "Swing the other way" : "Mirror half 1",
                    i == 0 ? "Flip which way this half swings."
                           : "Close opposite to half 1 — what makes two jaws meet instead of both " +
                             "sweeping the same way."), section.mirror);
            }
        }
        if (removeAt >= 0) clampSections.RemoveAt(removeAt);
        if (GUILayout.Button("Add clamp half", GUILayout.Width(140)))
            clampSections.Add(new ClawRig.ClampSection());
    }

    // Every claw already built on this robot, so one can be re-opened or deleted without hunting the
    // hierarchy — same in-place management as the pneumatic and chain builders.
    private void DrawExistingClaws(RobotMechanisms registry)
    {
        if (registry == null) return;
        ClawRig[] rigs = registry.GetComponentsInChildren<ClawRig>(true);
        if (rigs.Length == 0) return;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField($"Existing Claws ({rigs.Length})", EditorStyles.boldLabel);
        foreach (ClawRig rig in rigs)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{rig.displayName}  ({rig.name})");
                if (GUILayout.Button("Edit", GUILayout.Width(50))) { LoadRig(rig); GUIUtility.ExitGUI(); }
                if (GUILayout.Button("Delete", GUILayout.Width(60)))
                {
                    try
                    {
                        string report = ClawSetup.Strip(registry, rig, true);
                        EditorUtility.DisplayDialog(Title, report, "OK");
                    }
                    catch (Exception e)
                    {
                        EditorUtility.DisplayDialog(Title, "Couldn't delete the claw.\n\n" + e.Message, "OK");
                        Debug.LogException(e);
                    }
                    GUIUtility.ExitGUI(); // the rig array just mutated under this loop
                }
            }
        }
    }

    // --- Form <-> data ---------------------------------------------------------------------------

    private ClawSetup.Options BuildOptions() => new ClawSetup.Options
    {
        displayName = displayName,
        flippingParts = flippingParts,
        flipAngleDeg = flipAngleDeg,
        flipPivot = flipPivot,
        flipAxisPreset = flipAxisPreset,
        flipCustomAxis = flipCustomAxis,
        flipStartExtended = flipStartExtended,
        flipStiffness = flipStiffness,
        flipDamping = flipDamping,
        flipCylinderBody = flipCylinderBody,
        flipCylinderRod = flipCylinderRod,
        flipStrokeMm = flipStrokeMm,
        flipRecoil = flipRecoil,
        flipCylinderReverse = flipCylinderReverse,
        clampSections = clampSections,
        clampModelled = clampModelled,
        clampStartClosed = clampStartClosed,
        clampTrimDeg = clampTrimDeg,
        clampStiffness = clampStiffness,
        clampDamping = clampDamping,
        clampCylinderBody = clampCylinderBody,
        clampCylinderRod = clampCylinderRod,
        clampStrokeMm = clampStrokeMm,
        clampRecoil = clampRecoil,
        clampCylinderReverse = clampCylinderReverse,
        enableGrab = enableGrab,
        grabPassThrough = grabPassThrough,
        grabAutoUpright = grabAutoUpright,
        holdPoint = holdPoint,
        autoAssignButtons = autoAssignButtons,
    };

    private void LoadRig(ClawRig rig)
    {
        displayName = rig.displayName;
        flippingParts = new List<GameObject>(rig.flippingParts);
        flipAngleDeg = rig.flipAngleDeg;
        flipPivot = rig.flipPivot;
        flipAxisPreset = rig.flipAxisPreset;
        flipCustomAxis = rig.flipCustomAxis;
        flipStartExtended = rig.flipStartExtended;
        flipStiffness = rig.flipStiffness;
        flipDamping = rig.flipDamping;
        flipCylinderBody = rig.flipCylinderBody;
        flipCylinderRod = rig.flipCylinderRod;
        flipStrokeMm = rig.flipStrokeMm;
        flipRecoil = rig.flipRecoil;
        flipCylinderReverse = rig.flipCylinderReverse;
        clampSections = CloneSections(rig.clampSections);
        clampModelled = rig.clampModelled;
        clampStartClosed = rig.clampStartClosed;
        clampTrimDeg = rig.clampTrimDeg;
        clampStiffness = rig.clampStiffness;
        clampDamping = rig.clampDamping;
        clampCylinderBody = rig.clampCylinderBody;
        clampCylinderRod = rig.clampCylinderRod;
        clampStrokeMm = rig.clampStrokeMm;
        clampRecoil = rig.clampRecoil;
        clampCylinderReverse = rig.clampCylinderReverse;
        enableGrab = rig.enableGrab;
        grabPassThrough = rig.grabPassThrough;
        grabAutoUpright = rig.grabAutoUpright;
        holdPoint = rig.holdPoint;
        autoAssignButtons = rig.autoAssignButtons;
    }

    // Deep copy, so editing the form doesn't mutate the built rig before the user hits Build.
    private static List<ClawRig.ClampSection> CloneSections(List<ClawRig.ClampSection> source)
    {
        var copy = new List<ClawRig.ClampSection>();
        if (source == null) return copy;
        foreach (ClawRig.ClampSection s in source)
        {
            if (s == null) continue;
            copy.Add(new ClawRig.ClampSection
            {
                parts = new List<GameObject>(s.parts),
                closeAngleDeg = s.closeAngleDeg,
                pivot = s.pivot,
                axisPreset = s.axisPreset,
                customAxis = s.customAxis,
                mirror = s.mirror,
                builtLink = s.builtLink,
            });
        }
        return copy;
    }

    private void ClearForm()
    {
        displayName = "Claw";
        flippingParts = new List<GameObject>();
        flipAngleDeg = 180f; flipPivot = null;
        flipAxisPreset = ClawRig.HingeAxis.Auto; flipCustomAxis = Vector3.right;
        flipStartExtended = false; flipStiffness = 20000f; flipDamping = 500f;
        flipCylinderBody = null; flipCylinderRod = null;
        flipStrokeMm = 90f; flipRecoil = 0.5f; flipCylinderReverse = false;
        clampSections = new List<ClawRig.ClampSection>();
        clampModelled = ClawRig.JawRest.ModelledOpen; clampStartClosed = false; clampTrimDeg = 0f;
        clampStiffness = 20000f; clampDamping = 500f;
        clampCylinderBody = null; clampCylinderRod = null;
        clampStrokeMm = 50f; clampRecoil = 0.5f; clampCylinderReverse = false;
        enableGrab = true; grabPassThrough = true; grabAutoUpright = true;
        holdPoint = null; autoAssignButtons = true;
    }

    // Best-guess role assignment from part names, so a well-named CAD import is one click from built.
    // Deliberately conservative: it only fills EMPTY buckets, never overwrites a choice already made.
    private void AutoFill(RobotMechanisms registry)
    {
        if (registry == null)
        {
            EditorUtility.DisplayDialog(Title, "Select the robot (or any part of it) first.", "OK");
            return;
        }

        var flip = new List<GameObject>();
        var clamp = new List<GameObject>();
        GameObject flipBody = null, flipRod = null, clampBody = null, clampRod = null;

        foreach (Transform t in registry.GetComponentsInChildren<Transform>(true))
        {
            string n = t.name.ToLowerInvariant();
            bool isFlip = n.Contains("flip");
            bool isClamp = n.Contains("clamp") || n.Contains("claw") || n.Contains("jaw");
            bool isCylinder = n.Contains("cylinder") || n.Contains("piston") || n.Contains("pneumatic");
            bool isRod = n.Contains("rod");
            bool isBody = n.Contains("body") || n.Contains("barrel");

            if (isCylinder && isFlip && isRod) flipRod = flipRod ?? t.gameObject;
            else if (isCylinder && isFlip && isBody) flipBody = flipBody ?? t.gameObject;
            else if (isCylinder && isClamp && isRod) clampRod = clampRod ?? t.gameObject;
            else if (isCylinder && isClamp && isBody) clampBody = clampBody ?? t.gameObject;
            else if (isCylinder) continue;             // an unlabelled cylinder — too ambiguous to place
            else if (isFlip) flip.Add(t.gameObject);
            else if (isClamp) clamp.Add(t.gameObject);
        }

        if (CountNonNull(flippingParts) == 0 && flip.Count > 0) flippingParts = TopMostOnly(flip);
        if (clampSections.Count == 0 && clamp.Count > 0)
            foreach (GameObject go in TopMostOnly(clamp))
                clampSections.Add(new ClawRig.ClampSection
                {
                    parts = new List<GameObject> { go },
                    mirror = clampSections.Count > 0,   // the second half closes against the first
                });
        flipCylinderBody = flipCylinderBody != null ? flipCylinderBody : flipBody;
        flipCylinderRod = flipCylinderRod != null ? flipCylinderRod : flipRod;
        clampCylinderBody = clampCylinderBody != null ? clampCylinderBody : clampBody;
        clampCylinderRod = clampCylinderRod != null ? clampCylinderRod : clampRod;

        EditorUtility.DisplayDialog(Title,
            $"Guessed from part names:\n\n" +
            $"• Flipping parts: {CountNonNull(flippingParts)}\n" +
            $"• Clamp halves: {clampSections.Count}\n" +
            $"• Flip cylinder: {(flipCylinderRod != null ? "found" : "not found")}\n" +
            $"• Clamp cylinder: {(clampCylinderRod != null ? "found" : "not found")}\n\n" +
            "Check every slot before building — names are only a hint.", "OK");
    }

    // Drop any entry that's already inside another entry, so a group and its children don't both get
    // listed (which would try to rig the same geometry twice).
    private static List<GameObject> TopMostOnly(List<GameObject> parts)
    {
        var result = new List<GameObject>();
        foreach (GameObject go in parts)
        {
            bool nested = false;
            foreach (GameObject other in parts)
                if (other != go && go.transform.IsChildOf(other.transform)) { nested = true; break; }
            if (!nested) result.Add(go);
        }
        return result;
    }

    private RobotMechanisms ResolveRegistry()
    {
        foreach (GameObject go in flippingParts)
            if (go != null)
            {
                RobotMechanisms r = go.GetComponentInParent<RobotMechanisms>();
                if (r != null) return r;
            }
        foreach (ClawRig.ClampSection s in clampSections)
        {
            if (s?.parts == null) continue;
            foreach (GameObject go in s.parts)
                if (go != null)
                {
                    RobotMechanisms r = go.GetComponentInParent<RobotMechanisms>();
                    if (r != null) return r;
                }
        }
        if (Selection.activeGameObject != null)
            return Selection.activeGameObject.GetComponentInParent<RobotMechanisms>();
        return null;
    }

    private static int CountNonNull(List<GameObject> list)
    {
        int n = 0;
        if (list != null) foreach (GameObject go in list) if (go != null) n++;
        return n;
    }

    private static int SizeIndexFromStroke(float mm)
    {
        for (int i = 0; i < CylinderSizeStrokes.Length; i++)
            if (Mathf.Approximately(mm, CylinderSizeStrokes[i])) return i;
        return CylinderSizeLabels.Length - 1; // Custom
    }
}

// Headless-runnable core (window/core split like PneumaticSetup and Dr4bLiftSetup), so the validator
// can drive a full build without opening the window.
public static class ClawSetup
{
    private const string UndoName = "Build Claw";
    private const string FlipPivotName = "ClawFlipPivot";
    private const string ClampPivotPrefix = "ClawClampPivot";
    private const string MouthName = "ClawMouth";
    private const string HoldName = "ClawHoldPoint";

    public struct Options
    {
        public string displayName;

        public List<GameObject> flippingParts;
        public float flipAngleDeg;
        public Transform flipPivot;
        public ClawRig.HingeAxis flipAxisPreset;
        public Vector3 flipCustomAxis;
        public bool flipStartExtended;
        public float flipStiffness, flipDamping;
        public GameObject flipCylinderBody, flipCylinderRod;
        public float flipStrokeMm, flipRecoil;
        public bool flipCylinderReverse;

        public List<ClawRig.ClampSection> clampSections;
        public ClawRig.JawRest clampModelled;
        public bool clampStartClosed;
        public float clampTrimDeg;
        public float clampStiffness, clampDamping;
        public GameObject clampCylinderBody, clampCylinderRod;
        public float clampStrokeMm, clampRecoil;
        public bool clampCylinderReverse;

        // NOTE: a plain struct, so every bool here reads FALSE unless the caller sets it — which is
        // the opposite of several shipped defaults. The window fills all of them; anything else that
        // builds an Options must too, or it quietly turns features off.
        public bool enableGrab;
        public bool grabPassThrough;
        public bool grabAutoUpright;
        public Transform holdPoint;
        public bool autoAssignButtons;
    }

    // Build or UPDATE a claw. Re-runnable: links that survive are re-typed in place (AddMechanismJoint
    // is idempotent) and links the form no longer mentions are stripped, so removing a clamp half
    // can't leave an orphaned ArticulationBody behind. Throws on any precondition failure, having
    // changed nothing.
    public static string Build(Options o, bool useUndo)
    {
        // --- Validate everything BEFORE mutating anything ---------------------------------------
        GameObject flipRoot = FirstNonNull(o.flippingParts);
        List<ClawRig.ClampSection> sections = UsableSections(o.clampSections);

        if (flipRoot == null && sections.Count == 0)
            throw new InvalidOperationException(
                "Nothing to build. Assign the flipping parts, the clamping parts, or both.");

        GameObject seed = flipRoot != null ? flipRoot : sections[0].parts[0];
        RobotMechanisms registry = seed.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
            throw new InvalidOperationException(
                $"'{seed.name}' is not under a set-up robot (no RobotMechanisms). Run Set Up Imported " +
                "Robot first.");

        if (flipRoot != null && Mathf.Abs(o.flipAngleDeg) < 1e-3f)
            throw new InvalidOperationException("The flip angle is 0° — the claw wouldn't move. Try 180.");
        foreach (ClawRig.ClampSection s in sections)
            if (Mathf.Abs(s.closeAngleDeg) < 1e-3f)
                throw new InvalidOperationException(
                    $"A clamp half has a close angle of 0° — it wouldn't move. Give it an angle " +
                    "(35 is a reasonable starting point).");

        ValidateCylinderPair(o.flipCylinderBody, o.flipCylinderRod, o.flipStrokeMm, "flip");
        ValidateCylinderPair(o.clampCylinderBody, o.clampCylinderRod, o.clampStrokeMm, "clamp");

        // A cylinder is geometry posed by a follower, so it must never BE (or contain) the metal it
        // drives — the follower and PhysX would fight over the same transform.
        //
        // Which metal that is differs per cylinder, and the distinction matters: the FLIP cylinder
        // drives the flip, so it has to stay off it (it can't push what it's riding), while the CLAMP
        // cylinder drives only the jaws and is REQUIRED to sit inside the flip assembly so it turns
        // over with the claw. Checking every cylinder against everything would reject that.
        foreach (GameObject cyl in new[] { o.flipCylinderBody, o.flipCylinderRod })
        {
            if (cyl == null) continue;
            if (flipRoot != null) ValidateNotSamePart(cyl, flipRoot, "flipping parts");
            foreach (ClawRig.ClampSection s in sections) ValidateNotSamePart(cyl, s.parts[0], "clamping parts");
        }
        foreach (GameObject cyl in new[] { o.clampCylinderBody, o.clampCylinderRod })
        {
            if (cyl == null) continue;
            foreach (ClawRig.ClampSection s in sections) ValidateNotSamePart(cyl, s.parts[0], "clamping parts");
            // May live INSIDE the flip assembly (that's the point) — it just can't be it or swallow it.
            if (flipRoot != null && (cyl == flipRoot || flipRoot.transform.IsChildOf(cyl.transform)))
                throw new InvalidOperationException(
                    $"The clamp cylinder part '{cyl.name}' is (or contains) the flipping assembly " +
                    $"'{flipRoot.name}'. Pick the cylinder's own barrel and rod, not the group they sit in.");
        }

        // A part can only belong to one joint. Catch the common slip of listing a jaw in two halves,
        // or naming the same group as both the flipping assembly and a jaw.
        var claimed = new HashSet<GameObject>();
        for (int i = 0; i < sections.Count; i++)
            foreach (GameObject part in sections[i].parts)
            {
                if (part == null) continue;
                if (!claimed.Add(part))
                    throw new InvalidOperationException(
                        $"'{part.name}' is listed in more than one clamp half. Each part can only hinge once.");
                if (part == flipRoot)
                    throw new InvalidOperationException(
                        $"'{part.name}' is both the flipping assembly and a clamp half. The flip bucket " +
                        "takes the whole claw; the clamp halves take the jaws INSIDE it.");
            }

        int group = 0;
        if (useUndo)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            group = Undo.GetCurrentGroup();
        }

        // --- Clear out anything a PREVIOUS build left that this one no longer wants ---------------
        ClawRig previous = FindRigFor(registry, flipRoot, sections);
        var keep = new HashSet<GameObject>();
        if (flipRoot != null) keep.Add(flipRoot);
        foreach (ClawRig.ClampSection s in sections) keep.Add(s.parts[0]);
        if (previous != null) StripStaleLinks(registry, previous, keep, useUndo);

        // --- Flip link ---------------------------------------------------------------------------
        GameObject flipLink = null;
        ArticulationBody flipBody = null;
        Transform flipPivot = null;
        string flipId = null;
        if (flipRoot != null)
        {
            Transform pivot = EnsurePivot(o.flipPivot, flipRoot, FlipPivotName,
                FlipPivotSeed(o.flippingParts, sections, o.clampCylinderBody, o.clampCylinderRod),
                useUndo);
            flipPivot = pivot;
            ResolveAxisAnchor(flipRoot, pivot, o.flipAxisPreset, o.flipCustomAxis, isFlip: true,
                out Vector3 axis, out Vector3 anchor);

            // Signed limits, so a claw authored to swing "negative" still STARTS open: the pneumatic
            // takes lower as retracted and upper as extended, and reverseDirection swaps them.
            float signed = o.flipAngleDeg;
            AddMechanismJoint.Apply(flipRoot, AddMechanismJoint.JointType.Revolute, axis, anchor,
                Mathf.Min(0f, signed), Mathf.Max(0f, signed),
                new AddMechanismJoint.Options
                {
                    alsoMove = ExtraParts(o.flippingParts, flipRoot, sections),
                    reverseDirection = signed < 0f,
                    actuation = AddMechanismJoint.Actuation.Toggle,
                }, useUndo);

            flipLink = flipRoot;
            flipBody = flipLink.GetComponent<ArticulationBody>();
            flipId = UrdfPostProcessor.Slugify(flipLink.name);
            TunePneumatic(registry, flipId, $"{o.displayName} Flip", o.flipStartExtended,
                o.flipStiffness, o.flipDamping, useUndo);
            MechanismBuildUtil.AddOrGet<IgnoreRobotSelfCollision>(flipLink, useUndo);
            EnsurePivotChildOf(pivot, flipLink, useUndo);
        }

        // Everything below the flip rides it; with no flip, it all hangs off the chassis.
        Transform clawParent = flipLink != null ? flipLink.transform : registry.transform;

        // --- Clamp halves -------------------------------------------------------------------------
        ArticulationBody clampDriver = null;
        string clampId = null;
        float driverSigned = 0f;
        float driverStart = 0f;
        for (int i = 0; i < sections.Count; i++)
        {
            ClawRig.ClampSection s = sections[i];
            GameObject link = s.parts[0];

            // Only reparent when it isn't already somewhere below the flip link — a jaw modelled as a
            // grandchild should stay where the CAD put it.
            if (!link.transform.IsChildOf(clawParent))
                MechanismBuildUtil.EnsureChildOf(link.transform, clawParent, useUndo);

            Transform pivot = EnsurePivot(s.pivot, link, ClampPivotPrefix + (i + 1),
                ClampPivotSeed(link, clawParent.gameObject), useUndo);
            ResolveAxisAnchor(link, pivot, s.axisPreset, s.customAxis, isFlip: false,
                out Vector3 axis, out Vector3 anchor);

            // The jaw rests at joint 0 — where the CAD drew it — unless a trim shifts it. Trimming
            // moves the RESTING end of the travel, in the same sense as the swing, so a model drawn a
            // touch too open closes past its CAD pose at -10. Mirrored halves take the mirrored trim,
            // so both jaws move in together rather than the pair sliding sideways.
            float sign = s.mirror ? -1f : 1f;
            float signed = sign * s.closeAngleDeg;
            float start = sign * o.clampTrimDeg;
            float lower = Mathf.Min(start, start + signed), upper = Mathf.Max(start, start + signed);
            var jointOptions = new AddMechanismJoint.Options
            {
                alsoMove = ExtraParts(s.parts, link, null),
                // Which limit the piston treats as retracted follows the direction of travel, which
                // the trim shifts but never reverses.
                reverseDirection = signed < 0f,
                actuation = AddMechanismJoint.Actuation.Toggle,
            };

            if (i == 0)
            {
                // The driven half: a real button mechanism.
                AddMechanismJoint.Apply(link, AddMechanismJoint.JointType.Revolute, axis, anchor,
                    lower, upper, jointOptions, useUndo);
                clampDriver = link.GetComponent<ArticulationBody>();
                clampId = UrdfPostProcessor.Slugify(link.name);
                driverSigned = signed;
                driverStart = start;
                TunePneumatic(registry, clampId, $"{o.displayName} Clamp", StartsExtended(o),
                    o.clampStiffness, o.clampDamping, useUndo);
            }
            else
            {
                // A following half: joint only, NO actuator and NO registry entry — a coupled follower
                // is a passive linkage, and registering it would let ButtonRouter fight the coupler
                // for the drive (the rule Build Chain follows for its chained stations).
                ArticulationBody follower = AddMechanismJoint.ConfigureJointLink(link,
                    AddMechanismJoint.JointType.Revolute, axis, anchor, lower, upper, jointOptions,
                    registry, useUndo);
                string followerId = UrdfPostProcessor.Slugify(link.name);
                UrdfPostProcessor.RemoveMechanism(registry, followerId, useUndo);
                MechanismBuildUtil.ClearMechanismBindings(registry.robotId, followerId);

                JointCoupler coupler = MechanismBuildUtil.AddOrGet<JointCoupler>(link, useUndo);
                if (useUndo) Undo.RecordObject(coupler, UndoName);
                coupler.follower = follower;
                coupler.driver = clampDriver;
                coupler.mode = JointCoupler.CoupleMode.Position;
                // The driver sweeps driverSigned; this half must sweep its own signed angle over the
                // same press, so the ratio is simply the two angles' quotient (negative = mirrored).
                coupler.ratio = Mathf.Abs(driverSigned) > 1e-6f ? signed / driverSigned : 1f;
                // The coupler solves target = driver * ratio + offset. Ratio alone lines the two ENDS
                // of the travel up only when both halves rest at the same angle; the offset is what
                // keeps them together once a trim moves the resting pose. Falls out as 0 for the
                // usual mirrored pair (equal and opposite trims about a ratio of -1).
                coupler.offsetDeg = start - driverStart * coupler.ratio;
                coupler.positionStiffness = o.clampStiffness;
                coupler.positionDamping = o.clampDamping;
                // Uncapped, like the piston it mirrors. JointCoupler's default cap models a motor
                // that stalls under load, but this half is half of a PNEUMATIC — and a pneumatic's
                // drive is deliberately force-uncapped so it always reaches its endpoint. Leaving the
                // cap on made the mirrored jaw stall several degrees short of its twin, so the claw
                // closed visibly lopsided.
                coupler.forceLimit = float.MaxValue;
                coupler.BakeDrive(); // so edit-mode Physics.Simulate matches Play
                EditorUtility.SetDirty(coupler);
            }

            MechanismBuildUtil.AddOrGet<IgnoreRobotSelfCollision>(link, useUndo);
            EnsurePivotChildOf(pivot, link, useUndo);
            s.pivot = pivot;
            s.builtLink = link;
        }

        // Each jaw that split off took its colliders out of the flip link's body with it, so the flip
        // link's mass distribution is now stale — recompute it or the flip swings about a phantom
        // center of mass that still includes the jaws.
        if (flipBody != null && sections.Count > 0)
        {
            if (useUndo) Undo.RecordObject(flipBody, UndoName);
            flipBody.ResetCenterOfMass();
            flipBody.ResetInertiaTensor();
        }

        // --- Cosmetic cylinders --------------------------------------------------------------------
        // The flip cylinder stays on the chassis (it drives the flip, so it can't ride it); the clamp
        // cylinder rides the flip with the rest of the claw.
        WireCylinder(o.flipCylinderBody, o.flipCylinderRod, flipBody, registry.transform,
            o.flipStrokeMm, o.flipRecoil, o.flipCylinderReverse, useUndo);
        WireCylinder(o.clampCylinderBody, o.clampCylinderRod, clampDriver, clawParent,
            o.clampStrokeMm, o.clampRecoil, o.clampCylinderReverse, useUndo);

        // --- Grab ------------------------------------------------------------------------------------
        GameObject mouth = null;
        Transform hold = null;
        bool grabWired = o.enableGrab && clampDriver != null;
        if (grabWired)
        {
            PneumaticActuator clampActuator = clampDriver.GetComponent<PneumaticActuator>();
            var clawParts = new List<GameObject>();
            if (flipLink != null) clawParts.Add(flipLink);
            var jawParts = new List<GameObject>();
            foreach (ClawRig.ClampSection s in sections) jawParts.Add(s.parts[0]);
            clawParts.AddRange(jawParts);
            // clawParts is what a held piece stops colliding with (the mount included); jawParts is
            // what the MOUTH is measured from, so the trigger and the hold point land in the opening
            // between the jaws rather than at the centroid of the whole claw, mount and all.
            WireGrab(registry, clawParent, clawParts, jawParts, clampActuator, GrabsOnRetract(o),
                o.grabPassThrough, o.grabAutoUpright, o.holdPoint, useUndo, out mouth, out hold);
        }
        else
        {
            // Grab turned off (or nothing to clamp): clear a previous build's leftovers.
            RemoveGrab(registry, useUndo);
        }

        // --- Buttons ----------------------------------------------------------------------------------
        string buttonNote;
        if (o.autoAssignButtons)
        {
            // Clear first: AssignButtons claims the next FREE button, so without a clear an edit would
            // strand the mechanism on new buttons while the old ones lingered.
            var notes = new List<string>();
            if (flipId != null)
            {
                MechanismBuildUtil.ClearMechanismBindings(registry.robotId, flipId);
                // Prismatic is passed purely to pick the ONE-BUTTON toggle style — the same idiom the
                // pneumatic builder uses for its revolute rotating piston.
                notes.Add("Flip → " + MechanismAutoDetect.AssignButtons(registry.robotId, flipId,
                    AddMechanismJoint.JointType.Prismatic));
            }
            if (clampId != null)
            {
                MechanismBuildUtil.ClearMechanismBindings(registry.robotId, clampId);
                notes.Add("Clamp → " + MechanismAutoDetect.AssignButtons(registry.robotId, clampId,
                    AddMechanismJoint.JointType.Prismatic));
            }
            buttonNote = string.Join("; ", notes);
        }
        else
        {
            buttonNote = "left as they were (set them in Configure Controller)";
        }

        UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, registry.gameObject.name, registry);
        AssetDatabase.SaveAssets(); // flush the catalog so Configure Controller shows the new names

        // --- Record ------------------------------------------------------------------------------------
        GameObject rigHost = flipLink != null ? flipLink : sections[0].parts[0];
        if (previous != null && previous.gameObject != rigHost)
        {
            if (useUndo) Undo.DestroyObjectImmediate(previous);
            else UnityEngine.Object.DestroyImmediate(previous);
            previous = null;
        }
        ClawRig rig = MechanismBuildUtil.AddOrGet<ClawRig>(rigHost, useUndo);
        if (useUndo) Undo.RecordObject(rig, UndoName);
        rig.displayName = o.displayName;
        rig.flippingParts = new List<GameObject>(o.flippingParts ?? new List<GameObject>());
        rig.flipAngleDeg = o.flipAngleDeg;
        rig.flipPivot = flipPivot; // whatever was actually used — a marker we made, or the user's own
        rig.flipAxisPreset = o.flipAxisPreset;
        rig.flipCustomAxis = o.flipCustomAxis;
        rig.flipStartExtended = o.flipStartExtended;
        rig.flipStiffness = o.flipStiffness;
        rig.flipDamping = o.flipDamping;
        rig.flipCylinderBody = o.flipCylinderBody;
        rig.flipCylinderRod = o.flipCylinderRod;
        rig.flipStrokeMm = o.flipStrokeMm;
        rig.flipRecoil = o.flipRecoil;
        rig.flipCylinderReverse = o.flipCylinderReverse;
        // Copy, don't alias: the window keeps editing its own section objects after Build, and the
        // record must stay a snapshot of what was actually built.
        rig.clampSections = CopySections(sections);
        rig.clampModelled = o.clampModelled;
        rig.clampStartClosed = o.clampStartClosed;
        rig.clampTrimDeg = o.clampTrimDeg;
        rig.clampStiffness = o.clampStiffness;
        rig.clampDamping = o.clampDamping;
        rig.clampCylinderBody = o.clampCylinderBody;
        rig.clampCylinderRod = o.clampCylinderRod;
        rig.clampStrokeMm = o.clampStrokeMm;
        rig.clampRecoil = o.clampRecoil;
        rig.clampCylinderReverse = o.clampCylinderReverse;
        rig.enableGrab = o.enableGrab;
        rig.grabPassThrough = o.grabPassThrough;
        rig.grabAutoUpright = o.grabAutoUpright;
        rig.grabWhenRetracted = GrabsOnRetract(o);
        rig.clawMouth = mouth;
        rig.holdPoint = hold;
        rig.flipLink = flipLink;
        rig.autoAssignButtons = o.autoAssignButtons;

        if (useUndo) Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(registry);
        EditorUtility.SetDirty(rig);
        if (registry.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(registry.gameObject.scene);

        return BuildReport(o, flipLink, sections, grabWired, buttonNote);
    }

    // Delete one built claw: both mechanisms + their bindings, every joint, the couplers, the cosmetic
    // followers (colliders re-enabled), the grab and every marker the build created. Geometry stays
    // exactly where it is — the parts just become plain welded meshes again.
    public static string Strip(RobotMechanisms registry, ClawRig rig, bool useUndo)
    {
        if (registry == null || rig == null)
            throw new InvalidOperationException("No robot/claw to delete.");

        int group = 0;
        if (useUndo)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Delete Claw");
            group = Undo.GetCurrentGroup();
        }

        string label = rig.displayName;

        // Clamp halves first: destroying the flip link's body while its children still had joints
        // would silently re-parent them onto the chassis.
        if (rig.clampSections != null)
            foreach (ClawRig.ClampSection s in rig.clampSections)
            {
                if (s == null) continue;
                StripLink(registry, s.builtLink, useUndo);
                StripLink(registry, FirstNonNull(s.parts), useUndo);
            }
        StripLink(registry, rig.flipLink, useUndo);
        StripLink(registry, FirstNonNull(rig.flippingParts), useUndo);

        RemoveSlideFollower(rig.flipCylinderBody, useUndo);
        RemoveSlideFollower(rig.flipCylinderRod, useUndo);
        RemoveSlideFollower(rig.clampCylinderBody, useUndo);
        RemoveSlideFollower(rig.clampCylinderRod, useUndo);

        RemoveGrab(registry, useUndo);
        foreach (Transform t in registry.GetComponentsInChildren<Transform>(true))
            if (t != null && (t.name == FlipPivotName || t.name.StartsWith(ClampPivotPrefix)))
                MechanismBuildUtil.DestroyGo(t, useUndo);

        if (useUndo) Undo.DestroyObjectImmediate(rig);
        else UnityEngine.Object.DestroyImmediate(rig);

        UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, registry.gameObject.name, registry);
        AssetDatabase.SaveAssets();
        if (useUndo) Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(registry);
        if (registry.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(registry.gameObject.scene);

        return $"Deleted the claw '{label}' — its parts are plain welded meshes again. Remember to " +
               "apply the change to the PREFAB.";
    }

    // --- Build helpers -------------------------------------------------------------------------------

    // Turn one moving link back into plain geometry: mechanism + bindings gone, coupler gone, body gone.
    private static void StripLink(RobotMechanisms registry, GameObject link, bool useUndo)
    {
        if (link == null) return;
        string id = UrdfPostProcessor.Slugify(link.name);
        UrdfPostProcessor.RemoveMechanism(registry, id, useUndo); // destroys the actuator too
        MechanismBuildUtil.ClearMechanismBindings(registry.robotId, id);
        MechanismBuildUtil.RemoveComponents<JointCoupler>(link, useUndo);
        MechanismBuildUtil.RemoveComponents<IgnoreRobotSelfCollision>(link, useUndo);

        ArticulationBody body = link.GetComponent<ArticulationBody>();
        if (body != null && !body.isRoot && !MechanismBuildUtil.IsProtected(body, registry))
        {
            if (useUndo) Undo.DestroyObjectImmediate(body);
            else UnityEngine.Object.DestroyImmediate(body);
        }
    }

    // Links the PREVIOUS build rigged that this one no longer mentions — e.g. a clamp half removed
    // from the form. Without this they'd linger as orphaned joints that still move.
    private static void StripStaleLinks(RobotMechanisms registry, ClawRig previous,
        HashSet<GameObject> keep, bool useUndo)
    {
        if (previous.clampSections != null)
            foreach (ClawRig.ClampSection s in previous.clampSections)
                if (s?.builtLink != null && !keep.Contains(s.builtLink))
                    StripLink(registry, s.builtLink, useUndo);
        if (previous.flipLink != null && !keep.Contains(previous.flipLink))
            StripLink(registry, previous.flipLink, useUndo);

        // Cylinder parts dropped from the form must give their colliders back.
        foreach (GameObject part in new[] { previous.flipCylinderBody, previous.flipCylinderRod,
                                            previous.clampCylinderBody, previous.clampCylinderRod })
            if (part != null) RemoveSlideFollower(part, useUndo);
    }

    // Applies the display name, start pose and joint feel to a freshly-wired pneumatic. WireMechanism
    // bakes the drive from the actuator's DEFAULT stiffness/damping, so overriding them means writing
    // both the component fields (what Awake re-bakes from at play time) and the live drive (what
    // edit-mode Physics.Simulate reads).
    private static void TunePneumatic(RobotMechanisms registry, string id, string displayName,
        bool startExtended, float stiffness, float damping, bool useUndo)
    {
        RobotMechanisms.Mechanism mech = registry.Find(id);
        if (mech == null || mech.pneumatic == null) return;

        PneumaticActuator act = mech.pneumatic;
        if (useUndo) Undo.RecordObject(act, UndoName);
        act.startExtended = startExtended;
        act.stiffness = stiffness;
        act.damping = damping;
        mech.displayName = displayName;

        ArticulationBody body = act.body != null ? act.body : act.GetComponent<ArticulationBody>();
        if (body != null)
        {
            if (useUndo) Undo.RecordObject(body, UndoName);
            ArticulationDrive d = body.xDrive;
            d.stiffness = stiffness;
            d.damping = damping;
            d.target = startExtended ? act.extendedTarget : act.retractedTarget;
            body.xDrive = d;
        }
        EditorUtility.SetDirty(act);
    }

    // The hinge axis (in the link's local frame) and the anchor point it turns about.
    //
    // Auto reads the PIVOT MARKER's own X axis: the marker is created aligned to the robot's
    // left-right axis, which is right for nearly every claw, and rotating it in the Scene view is how
    // you re-aim a hinge that isn't. That makes one draggable object define both the point and the
    // axis — the same "drag AND rotate" idea the intake's stack slots use.
    // The pivot marker gives the POINT; this gives the DIRECTION. They're deliberately separate: a
    // marker is easy to drag onto the edge of the plastic but fiddly to rotate accurately, and reading
    // the axis off its rotation (the first cut of this tool) made every clamp hinge about the robot's
    // left-right axis — so the jaws pitched UP instead of closing inward.
    internal static void ResolveAxisAnchor(GameObject link, Transform pivot, ClawRig.HingeAxis preset,
        Vector3 customAxis, bool isFlip, out Vector3 axis, out Vector3 anchor)
    {
        // What "Auto" means depends on the job. The flip's guess is the robot's front-back line
        // because that is what turned out to be right on the claws built with this tool — a claw
        // hanging off the side of a bot rolls over rather than pitching. It IS only a guess: which
        // way a claw is mounted isn't inferable from the geometry, which is why the picker offers the
        // scene's own coloured axes and the Scene view previews whatever is chosen.
        if (preset == ClawRig.HingeAxis.Auto)
            preset = isFlip ? ClawRig.HingeAxis.RollsSideways : ClawRig.HingeAxis.JawsCloseInward;

        switch (preset)
        {
            case ClawRig.HingeAxis.JawsCloseInward:
                axis = RobotDirLocal(link, Vector3.up);
                break;
            case ClawRig.HingeAxis.TurnsOverForward:
                // Prefer the wheels' own left-right line over the root's X: it stays right even when
                // the CAD root is rotated relative to the chassis.
                axis = MechanismBuildUtil.TryDrivetrainLateralLocal(link, out Vector3 lateral)
                    ? lateral
                    : RobotDirLocal(link, Vector3.right);
                break;
            case ClawRig.HingeAxis.RollsSideways:
                axis = RobotDirLocal(link, Vector3.forward);
                break;
            // The scene's own axes — the red/green/blue arrows on the move gizmo. Unlike the robot
            // presets these ignore how the bot is oriented, which is exactly the point: they're the
            // ones you can verify by eye.
            case ClawRig.HingeAxis.WorldX:
                axis = WorldDirLocal(link, Vector3.right);
                break;
            case ClawRig.HingeAxis.WorldY:
                axis = WorldDirLocal(link, Vector3.up);
                break;
            case ClawRig.HingeAxis.WorldZ:
                axis = WorldDirLocal(link, Vector3.forward);
                break;
            case ClawRig.HingeAxis.PivotMarkerX:
                axis = pivot != null
                    ? link.transform.InverseTransformDirection(pivot.right).normalized
                    : RobotDirLocal(link, Vector3.right);
                break;
            default: // Custom
                axis = customAxis.sqrMagnitude > 1e-8f ? customAxis.normalized : Vector3.right;
                break;
        }
        if (axis.sqrMagnitude < 1e-8f) axis = Vector3.right;

        anchor = pivot != null
            ? link.transform.InverseTransformPoint(pivot.position)
            : Vector3.zero;
    }

    // Read-only twin of the build's pivot+axis resolution, for the Scene-view preview: same answer, but
    // it creates nothing. Existing so the window can show the hinge BEFORE anything is committed —
    // which of these two a given claw needs turns out not to be inferable from geometry (the auto-seeded
    // pivot lands at the claw's centre, and "which way does this jaw swing" depends on how the CAD was
    // drawn), so the only honest tool is one you can look at.
    internal static bool TryPreviewHinge(GameObject link, Transform assignedPivot, string markerName,
        Vector3 seedPoint, ClawRig.HingeAxis preset, Vector3 customAxis, bool isFlip,
        out Vector3 pivotWorld, out Vector3 axisWorld, out Transform existingPivot)
    {
        pivotWorld = Vector3.zero;
        axisWorld = Vector3.right;
        existingPivot = null;
        if (link == null) return false;

        // The caller passes the seed through the SAME helper the build uses, so a preview can't show a
        // pivot the build wouldn't create — which would be worse than having no preview at all.
        existingPivot = assignedPivot != null ? assignedPivot : FindChild(link.transform, markerName);
        pivotWorld = existingPivot != null ? existingPivot.position : seedPoint;

        ResolveAxisAnchor(link, existingPivot, preset, customAxis, isFlip, out Vector3 axisLocal, out _);
        axisWorld = link.transform.TransformDirection(axisLocal).normalized;
        return axisWorld.sqrMagnitude > 1e-8f;
    }

    // A joint rests where the modeller left it and travels away from that pose, so which END of the
    // travel means "shut" depends entirely on how the CAD was drawn. Everything that cares about the
    // jaws being shut derives from this one answer rather than from separate toggles that could
    // disagree — the bug that put the grab on the OPENING stroke, so a piece was seized as the claw
    // let go of it.
    private static bool GrabsOnRetract(Options o)
        => o.clampModelled == ClawRig.JawRest.ModelledClosed;

    private static bool StartsExtended(Options o)
        => o.clampModelled == ClawRig.JawRest.ModelledClosed ? !o.clampStartClosed : o.clampStartClosed;

    internal const string PreviewFlipPivotName = FlipPivotName;
    internal const string PreviewClampPivotPrefix = ClampPivotPrefix;
    internal const string PreviewHoldName = HoldName;

    // A direction named against the ROBOT (its own up / right / forward), converted into `link`'s local
    // frame — which is the frame an ArticulationBody joint axis is measured in. This is what lets the
    // picker say "closes inward" and have it mean the same thing on every model, however the CAD is
    // oriented. Falls back to world axes when the part isn't under a set-up robot.
    private static Vector3 RobotDirLocal(GameObject link, Vector3 rootLocalDir)
    {
        RobotMechanisms reg = link.GetComponentInParent<RobotMechanisms>();
        Vector3 world = reg != null ? reg.transform.TransformDirection(rootLocalDir) : rootLocalDir;
        return link.transform.InverseTransformDirection(world).normalized;
    }

    // A SCENE direction — one of the move gizmo's coloured arrows — in `link`'s local frame.
    private static Vector3 WorldDirLocal(GameObject link, Vector3 worldDir)
        => link.transform.InverseTransformDirection(worldDir).normalized;

    // Where a flip hinges by default: the centre of EVERYTHING that flips — every listed flipping part
    // AND every jaw, because the jaws are reparented under the flip link and turn over with it.
    //
    // Seeding this on just the first listed part is what made a 180 look like it "inverted everything"
    // rather than turning the claw over: the pivot sat at one plate's centre while the rest of the
    // assembly hung off to one side, so half a turn threw all of it to the diametrically opposite
    // place. A claw turns over WHERE IT STANDS, which means hinging about the assembly's own middle.
    internal static Vector3 FlipPivotSeed(List<GameObject> flippingParts,
        List<ClawRig.ClampSection> sections, GameObject clampCylinderBody, GameObject clampCylinderRod)
    {
        Bounds all = default;
        bool has = false;
        void Add(GameObject go)
        {
            if (go == null || !MechanismBuildUtil.TryBounds(go, out Bounds b)) return;
            if (!has) { all = b; has = true; } else all.Encapsulate(b);
        }

        if (flippingParts != null) foreach (GameObject go in flippingParts) Add(go);
        if (sections != null)
            foreach (ClawRig.ClampSection s in sections)
            {
                if (s?.parts == null) continue;
                foreach (GameObject go in s.parts) Add(go);
            }
        // The clamp cylinder is reparented under the flip link and turns over with everything else,
        // so it counts toward the middle. The FLIP cylinder deliberately doesn't — it stays on the
        // chassis, because it's what drives the flip.
        Add(clampCylinderBody);
        Add(clampCylinderRod);

        if (has) return all.center;
        GameObject first = FirstNonNull(flippingParts);
        return first != null ? MechanismBuildUtil.BoundsCenterOrOrigin(first) : Vector3.zero;
    }

    // Throw away the markers this tool generated — the pivots, the mouth trigger and the hold point —
    // so the next build seeds them again.
    //
    // The build deliberately REUSES a marker it finds: that's what makes a pivot you dragged onto the
    // right pin, or a mouth box you shrank onto the opening, survive a rebuild. The cost is that a
    // claw built before a change to the default seeding keeps the old placement forever, with no hint
    // that the tool would now choose better. This is the way out that doesn't involve hunting the
    // hierarchy.
    public static int ResetPivotMarkers(RobotMechanisms registry, bool useUndo)
    {
        if (registry == null) return 0;

        var doomed = new List<Transform>();
        foreach (Transform t in registry.GetComponentsInChildren<Transform>(true))
            if (t.name == FlipPivotName || t.name == MouthName || t.name == HoldName ||
                t.name.StartsWith(ClampPivotPrefix, StringComparison.Ordinal))
                doomed.Add(t);

        foreach (Transform t in doomed)
        {
            if (t == null) continue;
            if (useUndo) Undo.DestroyObjectImmediate(t.gameObject);
            else UnityEngine.Object.DestroyImmediate(t.gameObject);
        }

        // The rig's stored references are now destroyed objects, which Unity reports as null — exactly
        // what "create a fresh marker" looks like to the next build. Clear them anyway so the record
        // doesn't serialize dangling ids.
        foreach (ClawRig rig in registry.GetComponentsInChildren<ClawRig>(true))
        {
            if (useUndo) Undo.RecordObject(rig, UndoName);
            rig.flipPivot = null;
            if (rig.clampSections != null)
                foreach (ClawRig.ClampSection s in rig.clampSections)
                    if (s != null) s.pivot = null;
            EditorUtility.SetDirty(rig);
        }
        return doomed.Count;
    }

    // Where a jaw hinges by default: the point of its own plastic nearest the middle of the claw —
    // roughly where a pin through the assembly would pass. Unlike the flip, a jaw really does swing
    // about its EDGE, so this deliberately isn't the part's centre.
    internal static Vector3 ClampPivotSeed(GameObject link, GameObject clawParent)
        => MechanismBuildUtil.ClosestOnBounds(link, MechanismBuildUtil.BoundsCenterOrOrigin(clawParent));

    // Returns the pivot to hinge about, creating a draggable marker at `seedPoint` when the slot is
    // empty, aligned so its X axis is the robot's left-right axis.
    private static Transform EnsurePivot(Transform assigned, GameObject link, string markerName,
        Vector3 seedPoint, bool useUndo)
    {
        if (assigned != null) return assigned;

        Transform existing = FindChild(link.transform, markerName);
        if (existing != null) return existing;

        GameObject marker = new GameObject(markerName);
        if (useUndo) Undo.RegisterCreatedObjectUndo(marker, UndoName);
        marker.transform.SetParent(link.transform, worldPositionStays: true);
        marker.transform.position = seedPoint;
        marker.transform.rotation = MechanismBuildUtil.TryDrivetrainLateralLocal(link, out Vector3 lateralLocal)
            ? Quaternion.FromToRotation(Vector3.right, link.transform.TransformDirection(lateralLocal))
            : Quaternion.identity;
        return marker.transform;
    }

    // A marker made before the link was jointed can end up outside it after a reparent; re-home it so
    // its local position stays the joint anchor across rebuilds.
    private static void EnsurePivotChildOf(Transform pivot, GameObject link, bool useUndo)
    {
        if (pivot == null || link == null) return;
        if (!pivot.IsChildOf(link.transform))
            MechanismBuildUtil.EnsureChildOf(pivot, link.transform, useUndo);
    }

    // Pose a cylinder's two halves off the driven joint's progress. Neither gets a joint: they're
    // neutralized to plain transforms with their colliders off (a teleporting collider would fling
    // pieces) and slid along the body->rod centerline, signed so the rod extends while the body
    // recoils. The two are always `stroke` apart however the recoil splits it.
    private static void WireCylinder(GameObject body, GameObject rod, ArticulationBody progressBody,
        Transform parent, float strokeMm, float recoil, bool reverse, bool useUndo)
    {
        if (body == null || rod == null || progressBody == null) return;

        // Reparent BEFORE measuring: slideAxisLocal lives in the part's parent frame, which is what
        // lets a clamp cylinder parented under the flip link ride the flip for free.
        MechanismBuildUtil.EnsureChildOf(body.transform, parent, useUndo);
        MechanismBuildUtil.EnsureChildOf(rod.transform, parent, useUndo);

        Vector3 worldAxis = MechanismBuildUtil.BoundsCenterOrOrigin(rod) -
                            MechanismBuildUtil.BoundsCenterOrOrigin(body);
        if (worldAxis.sqrMagnitude < 1e-8f)
            throw new InvalidOperationException(
                "The cylinder body and rod sit at the same place, so there's no direction for the rod " +
                "to extend along. Pick the barrel as the body and the sliding part as the rod.");
        worldAxis.Normalize();

        float stroke = strokeMm / PneumaticSetup.MmPerUnit;
        if (reverse) stroke = -stroke;

        // Read the endpoints off the actuator, so progress is 0 at rest and 1 when fired however the
        // two angles were entered (it accounts for the reverse-direction swap).
        PneumaticActuator act = progressBody.GetComponent<PneumaticActuator>();
        float lowRad = (act != null ? act.retractedTarget : 0f) * Mathf.Deg2Rad;
        float highRad = (act != null ? act.extendedTarget : 1f) * Mathf.Deg2Rad;

        AttachSlide(rod, worldAxis, +(1f - recoil) * stroke, progressBody, lowRad, highRad, useUndo);
        AttachSlide(body, worldAxis, -recoil * stroke, progressBody, lowRad, highRad, useUndo);
    }

    private static void AttachSlide(GameObject part, Vector3 worldAxis, float slideUnits,
        ArticulationBody progressBody, float lowRad, float highRad, bool useUndo)
    {
        MechanismBuildUtil.NeutralizeToPlainTransform(part, useUndo);
        MechanismBuildUtil.DisableColliders(part, useUndo);

        PneumaticSlideFollower follower = MechanismBuildUtil.AddOrGet<PneumaticSlideFollower>(part, useUndo);
        if (useUndo) Undo.RecordObject(follower, UndoName);
        Transform parent = part.transform.parent;
        follower.slideAxisLocal = parent != null
            ? parent.InverseTransformDirection(worldAxis).normalized
            : worldAxis;
        follower.slideUnits = slideUnits;
        follower.progressBody = progressBody;
        follower.progressLowRad = lowRad;
        follower.progressHighRad = highRad;
        EditorUtility.SetDirty(follower);
    }

    private static void RemoveSlideFollower(GameObject part, bool useUndo)
    {
        if (part == null) return;
        if (part.GetComponent<PneumaticSlideFollower>() == null) return;
        MechanismBuildUtil.RemoveComponents<PneumaticSlideFollower>(part, useUndo);
        MechanismBuildUtil.EnableColliders(part, useUndo);
    }

    // The grab zone and the frame captured pieces ride in. Both hang off the claw's moving parent, so
    // they follow the flip; the mouth is seeded over the jaws and is meant to be shrunk onto the
    // opening by hand. Re-runnable: an existing mouth/hold point is re-homed rather than duplicated.
    private static void WireGrab(RobotMechanisms registry, Transform parent, List<GameObject> clawParts,
        List<GameObject> jawParts, PneumaticActuator clampActuator, bool grabWhenRetracted,
        bool passThrough, bool autoUpright, Transform assignedHold, bool useUndo,
        out GameObject mouth, out Transform hold)
    {
        Bounds jaws = default;
        bool haveBounds = false;
        foreach (GameObject part in (jawParts != null && jawParts.Count > 0 ? jawParts : clawParts))
        {
            if (!MechanismBuildUtil.TryBounds(part, out Bounds b)) continue;
            if (!haveBounds) { jaws = b; haveBounds = true; }
            else jaws.Encapsulate(b);
        }
        if (!haveBounds) jaws = new Bounds(parent.position, Vector3.one);

        mouth = EnsureHelper(registry, parent, MouthName, jaws.center, useUndo);
        BoxCollider box = mouth.GetComponent<BoxCollider>();
        bool newBox = box == null;
        if (newBox) box = MechanismBuildUtil.AddOrGet<BoxCollider>(mouth, useUndo);
        if (useUndo) Undo.RecordObject(box, UndoName);
        box.isTrigger = true;
        // Only seed the size on FIRST creation — a rebuild must not undo a box the user already
        // shrank onto the opening.
        if (newBox)
        {
            Vector3 lossy = mouth.transform.lossyScale;
            Vector3 size = Vector3.Max(jaws.size * 0.6f, Vector3.one * 0.2f);
            box.center = Vector3.zero;
            box.size = new Vector3(size.x / Nz(lossy.x), size.y / Nz(lossy.y), size.z / Nz(lossy.z));
        }

        // An explicitly assigned hold point wins — that's how you put the carry position on a part
        // of your own CAD rather than on a generated empty.
        hold = assignedHold != null
            ? assignedHold
            : EnsureHelper(registry, parent, HoldName, jaws.center, useUndo).transform;

        ClawGrab grab = MechanismBuildUtil.AddOrGet<ClawGrab>(mouth, useUndo);
        if (useUndo) Undo.RecordObject(grab, UndoName);
        grab.clampPneumatic = clampActuator;
        grab.grabWhenRetracted = grabWhenRetracted;
        grab.passThroughWhileHeld = passThrough;
        grab.autoUpright = autoUpright;
        grab.holdPoint = hold;
        grab.clawParts = clawParts.ToArray();
        EditorUtility.SetDirty(grab);
    }

    private static void RemoveGrab(RobotMechanisms registry, bool useUndo)
    {
        foreach (ClawGrab grab in registry.GetComponentsInChildren<ClawGrab>(true))
            if (grab != null) MechanismBuildUtil.DestroyGo(grab.transform, useUndo);
        foreach (Transform t in registry.GetComponentsInChildren<Transform>(true))
            if (t != null && t.name == HoldName) MechanismBuildUtil.DestroyGo(t, useUndo);
    }

    // Find-or-create a named helper anywhere under the robot, re-homing it to `parent` (keeping its
    // world pose) so an old setup left on the wrong link is migrated instead of duplicated.
    private static GameObject EnsureHelper(RobotMechanisms registry, Transform parent, string name,
        Vector3 seedPos, bool useUndo)
    {
        Transform existing = FindChild(registry.transform, name);
        GameObject go;
        if (existing == null)
        {
            go = new GameObject(name);
            if (useUndo) Undo.RegisterCreatedObjectUndo(go, UndoName);
            go.transform.SetParent(parent, worldPositionStays: true);
            go.transform.SetPositionAndRotation(seedPos, parent.rotation);
        }
        else
        {
            go = existing.gameObject;
            if (useUndo) Undo.RegisterFullObjectHierarchyUndo(go, UndoName);
            if (go.transform.parent != parent)
                MechanismBuildUtil.EnsureChildOf(go.transform, parent, useUndo);
        }
        return go;
    }

    // --- Validation + small helpers -------------------------------------------------------------------

    private static void ValidateCylinderPair(GameObject body, GameObject rod, float strokeMm, string role)
    {
        if (body == null && rod == null) return;
        if (body == null || rod == null)
            throw new InvalidOperationException(
                $"The {role} cylinder needs BOTH a body and a rod (or neither) — the direction the rod " +
                "extends is measured between them.");
        if (body == rod)
            throw new InvalidOperationException(
                $"The {role} cylinder's body and rod are the same object. Pick the barrel as the body " +
                "and the extending part as the rod.");
        if (strokeMm <= 0f)
            throw new InvalidOperationException(
                $"The {role} cylinder's stroke must be positive (a real VEX cylinder is 20-90 mm).");
        if (body.GetComponentInChildren<Renderer>(true) == null ||
            rod.GetComponentInChildren<Renderer>(true) == null)
            throw new InvalidOperationException($"The {role} cylinder's parts need meshes to move.");
    }

    // Each case gets its own wording: "overlaps in the hierarchy" is accurate but leaves you guessing
    // which object to move, and these are the errors people hit on their first claw.
    private static void ValidateNotSamePart(GameObject cylinder, GameObject metal, string role)
    {
        if (metal == null) return;
        if (cylinder == metal)
            throw new InvalidOperationException(
                $"'{cylinder.name}' is listed both as a cylinder part and as the {role}. A cylinder is " +
                "posed by a follower rather than jointed, so it can't also be the metal it drives — put " +
                "the barrel and the rod in the cylinder slots, and the plastic they push in the parts list.");
        if (cylinder.transform.IsChildOf(metal.transform))
            throw new InvalidOperationException(
                $"The cylinder part '{cylinder.name}' sits INSIDE the {role} ('{metal.name}'), so it " +
                $"would be welded into the very link it's meant to push. Drag '{cylinder.name}' out of " +
                $"'{metal.name}' in the Hierarchy — a flip cylinder mounts to the chassis — or name a " +
                $"smaller group as the {role} that doesn't contain the cylinder.");
        if (metal.transform.IsChildOf(cylinder.transform))
            throw new InvalidOperationException(
                $"The {role} ('{metal.name}') sits inside the cylinder part '{cylinder.name}'. The " +
                "cylinder slots want the barrel and rod themselves, not a group that contains the claw.");
    }

    // Sections with at least one real part; the first part is that half's driven link.
    private static List<ClawRig.ClampSection> UsableSections(List<ClawRig.ClampSection> sections)
    {
        var result = new List<ClawRig.ClampSection>();
        if (sections == null) return result;
        foreach (ClawRig.ClampSection s in sections)
        {
            if (s == null) continue;
            GameObject first = FirstNonNull(s.parts);
            if (first == null) continue;
            // Normalize so parts[0] is always the driven link.
            s.parts.RemoveAll(p => p == null);
            s.parts.Remove(first);
            s.parts.Insert(0, first);
            result.Add(s);
        }
        return result;
    }

    // Everything in `list` except the driven link itself — and, when given, except anything entangled
    // with a clamp half's own driven link. A jaw becomes its OWN child joint, so it must not be welded
    // into the flip link; and a group CONTAINING a jaw can't be welded either, because merging a part
    // that already holds an ArticulationBody is rejected outright (it would swallow a live joint).
    private static GameObject[] ExtraParts(List<GameObject> list, GameObject link,
        List<ClawRig.ClampSection> excludeSectionLinks)
    {
        var extras = new List<GameObject>();
        if (list == null) return extras.ToArray();
        foreach (GameObject go in list)
        {
            if (go == null || go == link) continue;
            bool entangled = false;
            if (excludeSectionLinks != null)
                foreach (ClawRig.ClampSection s in excludeSectionLinks)
                {
                    Transform jaw = s.parts[0].transform;
                    if (s.parts[0] == go || go.transform.IsChildOf(jaw) || jaw.IsChildOf(go.transform))
                    { entangled = true; break; }
                }
            if (!entangled) extras.Add(go);
        }
        return extras.ToArray();
    }

    // The claw record already on one of these links, if this is a rebuild.
    private static ClawRig FindRigFor(RobotMechanisms registry, GameObject flipRoot,
        List<ClawRig.ClampSection> sections)
    {
        if (flipRoot != null)
        {
            ClawRig r = flipRoot.GetComponent<ClawRig>();
            if (r != null) return r;
        }
        foreach (ClawRig.ClampSection s in sections)
        {
            ClawRig r = s.parts[0].GetComponent<ClawRig>();
            if (r != null) return r;
        }
        // A claw whose flip link changed between builds: fall back to the only rig on the robot.
        ClawRig[] all = registry.GetComponentsInChildren<ClawRig>(true);
        return all.Length == 1 ? all[0] : null;
    }

    private static List<ClawRig.ClampSection> CopySections(List<ClawRig.ClampSection> source)
    {
        var copy = new List<ClawRig.ClampSection>();
        foreach (ClawRig.ClampSection s in source)
            copy.Add(new ClawRig.ClampSection
            {
                parts = new List<GameObject>(s.parts),
                closeAngleDeg = s.closeAngleDeg,
                pivot = s.pivot,
                axisPreset = s.axisPreset,
                customAxis = s.customAxis,
                mirror = s.mirror,
                builtLink = s.builtLink,
            });
        return copy;
    }

    internal static GameObject FirstNonNull(List<GameObject> list)
    {
        if (list == null) return null;
        foreach (GameObject go in list) if (go != null) return go;
        return null;
    }

    private static Transform FindChild(Transform root, string name)
    {
        if (root == null) return null;
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    private static float Nz(float v) => Mathf.Abs(v) < 1e-4f ? 1f : v;

    private static string BuildReport(Options o, GameObject flipLink,
        List<ClawRig.ClampSection> sections, bool grabWired, string buttonNote)
    {
        string flipLine = flipLink != null
            ? $"• FLIP: '{flipLink.name}' turns {o.flipAngleDeg}° about its pivot, carrying the whole claw.\n"
            : "• FLIP: none (clamp-only claw).\n";
        string clampLine = sections.Count > 0
            ? $"• CLAMP: {sections.Count} half/halves; '{sections[0].parts[0].name}' is driven and the " +
              "rest mirror it off one button.\n"
            : "• CLAMP: none.\n";
        string grabLine = grabWired
            ? "• GRAB: closing the claw locks whatever is in the ClawMouth trigger to the claw — it " +
              "survives driving and the flip, and drops when you open. Shrink ClawMouth onto the " +
              "opening in the Scene view.\n"
            : "• GRAB: off — the jaws are solid but won't retain a piece.\n";

        return $"Built the claw '{o.displayName}'.\n\n" + flipLine + clampLine + grabLine +
            $"• Buttons: {buttonNote}. Each function is a piston, so Configure Controller lets you " +
            "switch it between one toggle button and two (extend / retract).\n" +
            "• The cylinders are cosmetic: the rod slides out while the body recoils, keeping the " +
            "assembly's center of mass put.\n\n" +
            "PIVOTS: any slot you left empty now has a marker (ClawFlipPivot / ClawClampPivot1…). Drag " +
            "each onto the edge of the plastic where that part should hinge — rotate it to re-aim the " +
            "hinge axis — then Build again to bake it in.\n\n" +
            "IMPORTANT: the field spawns the robot PREFAB at Play, not this scene object. APPLY THESE " +
            "CHANGES TO THE PREFAB (Prefab Mode, or Overrides > Apply All) or they won't spawn.";
    }
}
