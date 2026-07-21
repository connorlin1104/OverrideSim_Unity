using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;   // CompareFunction, for drawing the hold point through the CAD

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

    [SerializeField] private bool showLevel;
    [SerializeField] private List<GameObject> levelParts = new List<GameObject>();
    [SerializeField] private GameObject armDriver;
    [SerializeField] private GameObject levelAxle;
    [SerializeField] private ClawRig.HingeAxis levelAxisPreset = ClawRig.HingeAxis.MatchArm;
    [SerializeField] private Vector3 levelCustomAxis = Vector3.right;
    [SerializeField] private float levelRatio = -1f;
    [SerializeField] private float levelSweepDeg = 180f;
    [SerializeField] private float levelStiffness = 20000f;
    [SerializeField] private float levelDamping = 500f;
    [SerializeField] private bool levelFlipPastMidpoint;
    [SerializeField] private float levelFlipDegrees = 180f;
    [SerializeField] private float levelFlipFraction = 0.5f;
    [SerializeField] private float levelFlipSeconds = 0.3f;
    [SerializeField] private List<GameObject> yawWristParts = new List<GameObject>();
    [SerializeField] private Transform yawWristPivot;

    [SerializeField] private List<GameObject> flippingParts = new List<GameObject>();
    [SerializeField] private float flipAngleDeg = 180f;
    [SerializeField] private Transform flipPivot;
    [SerializeField] private ClawRig.HingeAxis flipAxisPreset = ClawRig.HingeAxis.Auto;
    [SerializeField] private Vector3 flipCustomAxis = Vector3.right;
    [SerializeField] private float flipTravelSeconds = 0.35f;
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

        // --- Rides a rotating arm (stays level) -------------------------------------------------
        DrawLevelSection();

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
            flipTravelSeconds = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent("Flip time (s)",
                "How long the turn takes. A pneumatic snaps, and half a turn that snaps is over before " +
                "the eye catches it — on a claw that's roughly symmetric about its pivot that reads as " +
                "nothing having happened. 0 restores the instant snap."), flipTravelSeconds));
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
                // The hold point is an empty with no mesh, so there is nothing in the Scene view to
                // click and nothing to pick it out in a hierarchy of hundreds of CAD parts. Selecting
                // it from here hands it to Unity's own move tool, which beats competing with the pivot
                // markers' drag handles for the same few pixels.
                using (new EditorGUILayout.HorizontalScope())
                {
                    holdPoint = (Transform)EditorGUILayout.ObjectField(new GUIContent("Hold point",
                        "WHERE a grabbed piece is carried. Empty = the build creates a ClawHoldPoint " +
                        "in the middle of the jaws. Only its POSITION matters — which way pieces " +
                        "stand is measured off the robot, not off this marker's own rotation."),
                        holdPoint, typeof(Transform), true);

                    RobotMechanisms reg = ResolveRegistry();
                    Transform existingHold = holdPoint != null ? holdPoint
                        : (reg != null ? FindHoldPoint(reg) : null);
                    using (new EditorGUI.DisabledScope(existingHold == null))
                    {
                        if (GUILayout.Button(new GUIContent("Select",
                                "Select the hold point so the normal move tool (W) can drag it, and " +
                                "frame the Scene view on it."), GUILayout.Width(56f)))
                        {
                            Selection.activeTransform = existingHold;
                            EditorGUIUtility.PingObject(existingHold);
                            if (SceneView.lastActiveSceneView != null)
                                SceneView.lastActiveSceneView.FrameSelected();
                        }
                    }
                }
                grabAutoUpright = EditorGUILayout.Toggle(new GUIContent("Stand pieces up",
                    "Stand each grabbed piece along the ROBOT's up, measuring the piece's long axis " +
                    "per piece. Without this a pin lying on its side is carried lying on its side, so " +
                    "the same grab looks right on an upright piece and sideways on a match-loaded one. " +
                    "The claw's flip still turns a held stack over."),
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
                GUIUtility.ExitGUI();   // destroyed markers + a modal dialog mid-OnGUI — bail cleanly
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
            // Build reparents/creates/destroys parts and pops a modal dialog mid-OnGUI, which leaves IMGUI's
            // layout groups unbalanced ("EndLayoutGroup must be called first" / GUIClip spam). Bail out of
            // this GUI pass cleanly — same as the Delete path in Build Pneumatic. Outside the try so the
            // ExitGUIException it throws isn't swallowed.
            GUIUtility.ExitGUI();
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

        // Drawn FIRST so its drag handle wins ties: the hold point is seeded at the middle of the jaws
        // and the flip pivot at the middle of the assembly, which on a compact claw is nearly the same
        // few pixels — and a handle you can't reliably grab reads as a handle that isn't there.
        DrawHoldPoint(registry);
        DrawLevelHinge(registry);

        GameObject flipRoot = ClawSetup.FirstNonNull(flippingParts);
        if (flipRoot != null)
            DrawHinge(flipRoot, flipPivot, ClawSetup.PreviewFlipPivotName,
                ClawSetup.FlipPivotSeed(flippingParts, clampSections, clampCylinderBody, clampCylinderRod),
                flipAxisPreset, flipCustomAxis, true, flipAngleDeg, "Flip",
                new Color(0.35f, 0.7f, 1f));

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
    // where a grabbed piece ends up. So the tool draws it and hands over a drag handle, the same deal
    // as the pivots.
    //
    // The arrow is which way pieces STAND, and it deliberately does not read the marker's own up: that
    // is inherited from the claw's CAD and points wherever the modeller left it, which is what carried
    // every grabbed pin lying on its side. ClawGrab is asked for the answer rather than the formula
    // being written out twice.
    private void DrawHoldPoint(RobotMechanisms registry)
    {
        if (!enableGrab) return;
        Transform hold = holdPoint != null ? holdPoint : FindHoldPoint(registry);
        if (hold == null) return;

        float handle = HandleUtility.GetHandleSize(hold.position);
        Handles.color = new Color(0.2f, 1f, 0.5f);
        // Drawn through the CAD: the hold point sits between the jaws, so depth-tested it spends most
        // of its life buried inside the plastic it's meant to be positioned against.
        CompareFunction wasZTest = Handles.zTest;
        Handles.zTest = CompareFunction.Always;
        Handles.SphereHandleCap(0, hold.position, Quaternion.identity, handle * 0.22f, EventType.Repaint);

        // Before the first build there is no ClawGrab to ask, so fall back to the same robot up it
        // would measure — what the arrow promises is what the next build will do.
        ClawGrab grab = registry.GetComponentInChildren<ClawGrab>(true);
        Vector3 up = grab != null ? grab.UprightWorldDir() : registry.transform.up;
        if (grabAutoUpright && up.sqrMagnitude > 1e-6f)
        {
            Handles.ArrowHandleCap(0, hold.position, Quaternion.LookRotation(up), handle, EventType.Repaint);
            Handles.Label(hold.position + up * handle * 1.1f, "Hold point (pieces stand along this)");
        }
        else
        {
            Handles.Label(hold.position + Vector3.up * handle * 0.4f,
                "Hold point (pieces keep the angle they were caught at)");
        }

        EditorGUI.BeginChangeCheck();
        Vector3 moved = Handles.PositionHandle(hold.position, hold.rotation);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(hold, "Move claw hold point");
            hold.position = moved;
        }
        Handles.zTest = wasZTest;
    }

    private static Transform FindHoldPoint(RobotMechanisms registry)
    {
        ClawGrab grab = registry.GetComponentInChildren<ClawGrab>(true);
        if (grab != null && grab.holdPoint != null) return grab.holdPoint;
        foreach (Transform t in registry.GetComponentsInChildren<Transform>(true))
            if (t.name == ClawSetup.PreviewHoldName) return t;
        return null;
    }

    // Draws the level-keeper the way the flip/clamp hinges are drawn, but as a PAIR of opposed arcs about
    // one shared line: the arm carries the claw one way, the mount counter-rotates the other, and the two
    // cancel so the claw stays level. The whole point of this preview is that you can look at it and see
    // whether the mount is turning about the arm's line and the right way round before committing.
    private void DrawLevelHinge(RobotMechanisms registry)
    {
        GameObject levelLink = ClawSetup.FirstNonNull(levelParts);
        if (levelLink == null) return;
        ArticulationBody arm = ClawSetup.ResolveArmDriver(armDriver);

        if (!ClawSetup.TryPreviewLevelHinge(levelLink, null, levelAxisPreset, levelCustomAxis, arm,
                levelAxle, ClawSetup.FirstNonNull(flippingParts), clampSections,
                out Vector3 pivot, out Vector3 axis, out Transform pivotTf))
            return;

        float handle = HandleUtility.GetHandleSize(pivot);
        var armColor = new Color(1f, 0.55f, 0.15f);    // the arm's carry (the tumble to correct)
        var keepColor = new Color(0.6f, 0.5f, 1f);     // the mount's counter-rotation
        float reach = handle * 3f;

        // The shared spin line the mount turns about.
        Handles.color = keepColor;
        Handles.DrawAAPolyLine(4f, pivot - axis * reach, pivot + axis * reach);
        Handles.SphereHandleCap(0, pivot, Quaternion.identity, handle * 0.16f, EventType.Repaint);

        // A reference lever perpendicular to the axis, so a representative wedge of each rotation reads.
        Vector3 lever = Vector3.ProjectOnPlane(registry.transform.up, axis);
        if (lever.sqrMagnitude < 1e-5f) lever = Vector3.ProjectOnPlane(registry.transform.forward, axis);
        if (lever.sqrMagnitude > 1e-5f)
        {
            lever = lever.normalized * reach * 0.85f;
            float show = Mathf.Clamp(Mathf.Abs(levelSweepDeg), 5f, 90f);  // a wedge, not a full turn
            float ratio = levelRatio < -1e-3f ? levelRatio : -1f;

            // The arm would carry the claw THIS way — the tumble the mount exists to undo.
            Handles.color = new Color(armColor.r, armColor.g, armColor.b, 0.13f);
            Handles.DrawSolidArc(pivot, axis, lever, show, lever.magnitude);
            Handles.color = armColor;
            Handles.DrawAAPolyLine(2f, pivot + lever, pivot + Quaternion.AngleAxis(show, axis) * lever);
            Handles.Label(pivot + Quaternion.AngleAxis(show, axis) * lever, "arm carries the claw →");

            // ...and the mount counter-rotates the OTHER way, so the claw's net orientation holds.
            Handles.color = new Color(keepColor.r, keepColor.g, keepColor.b, 0.16f);
            Handles.DrawSolidArc(pivot, axis, lever, show * ratio, lever.magnitude);
            Handles.color = keepColor;
            Handles.DrawDottedLine(pivot, pivot + Quaternion.AngleAxis(show * ratio, axis) * lever, 4f);
            Handles.Label(pivot + lever * 1.12f, "mount counter-rotates ← (claw stays level)");
        }

        // The arm's own axis through its pivot, dotted, so it's plain the two share one line.
        if (arm != null)
        {
            Vector3 armPivot = arm.transform.TransformPoint(arm.anchorPosition);
            Handles.color = armColor;
            Handles.DrawDottedLine(armPivot - axis * reach, armPivot + axis * reach, 3f);
            Handles.Label(armPivot + axis * reach, $"arm '{arm.name}' spins about this");
        }

        // Drag the mount pivot straight from the Scene view; Build bakes wherever you leave it.
        if (pivotTf != null)
        {
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.PositionHandle(pivotTf.position, Quaternion.identity);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(pivotTf, "Move claw mount pivot");
                pivotTf.position = moved;
            }
        }
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

    // Optional: a claw hung off a rotating arm needs a link that counter-rotates the arm, or it tumbles
    // with it (a 180 swing lands it upside down). Folded away by default — most claws bolt straight to
    // the chassis and never see an arm.
    private void DrawLevelSection()
    {
        EditorGUILayout.Space();
        showLevel = EditorGUILayout.Foldout(showLevel,
            "Rides a rotating arm (stays level) — optional", true);
        if (!showLevel) return;

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.HelpBox(
                "For a claw on a turret/arm that swings it front-to-back. A rotating joint turns its " +
                "whole subtree, so without help the claw arrives upside down and facing the wrong way. " +
                "This inserts a MOUNT between the arm and the claw that counter-rotates the arm 1:1, so " +
                "the claw rides the arc but keeps one orientation.\n\n" +
                "Build the arm FIRST (Build Chain — spin it about its axis), then point 'Rotating arm' " +
                "at it here. The flip and jaws get reparented under the mount for you.", MessageType.Info);

            armDriver = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Rotating arm",
                "The part you spun up with Build Chain — the swinging arm. The mount reads its axis and " +
                "slaves to its motion, so build the arm before this."), armDriver, typeof(GameObject), true);

            ArticulationBody arm = ClawSetup.ResolveArmDriver(armDriver);
            if (armDriver != null)
            {
                if (arm == null)
                    EditorGUILayout.HelpBox("That part isn't a joint yet — spin it up with Build Chain " +
                        "first, then come back.", MessageType.Warning);
                else if (arm.jointType != ArticulationJointType.RevoluteJoint)
                    EditorGUILayout.HelpBox($"'{arm.name}' is a {arm.jointType}, not a rotating joint. " +
                        "The mount can only counter-rotate a joint that turns.", MessageType.Warning);
                else if (arm.twistLock == ArticulationDofLock.FreeMotion)
                    EditorGUILayout.HelpBox($"'{arm.name}' is a FREE-SPINNING joint, so the claw only " +
                        "stays level within the sweep range below. For a bounded front-to-back swing, a " +
                        "bounded revolute arm keeps it level everywhere.", MessageType.None);
            }

            DrawGoList("Mount (stays level)",
                "The bracket the claw bolts to at the end of the arm. It travels with the arm but " +
                "counter-rotates so the claw doesn't turn. Don't list the flip/jaws — they get " +
                "reparented under this for you.", levelParts);

            if (CountNonNull(levelParts) > 0 || armDriver != null)
            {
                levelAxle = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Pivot axle",
                    "The SHAFT the claw pivots on at the end of the arm. Its centre becomes the rotation " +
                    "point (so the claw turns THERE and stays at the arm's end, not on a huge arc about the " +
                    "robot's middle), AND — welded to the arm — it rides the arm's end through the swing. " +
                    "Drop the axle/standoff part here; empty falls back to the claw's own centre."),
                    levelAxle, typeof(GameObject), true);
                if (levelAxle != null && !ChainBuilder.TryAxleWorldAxis(levelAxle, out _, out _))
                    EditorGUILayout.HelpBox("That part has no mesh to read a shaft from — drop in the " +
                        "actual axle/standoff part.", MessageType.Warning);

                DrawLevelAxis();
                levelSweepDeg = EditorGUILayout.FloatField(new GUIContent("Sweep range (deg)",
                    "How far each way the mount may counter-rotate. Must cover the arm's whole swing, or " +
                    "the claw stops staying level past that point. 180 suits a front-to-back arm."),
                    levelSweepDeg);
                levelRatio = EditorGUILayout.Slider(new GUIContent("Counter ratio",
                    "Mount turn : arm turn. -1 keeps the claw dead level (equal and opposite). Between " +
                    "-1 and 0 lets it lean part way with the arm. 0 = no leveling (use with the flip " +
                    "below for a claw that just flips at the top and otherwise rides the arm)."),
                    levelRatio, -1f, 0f);

                levelFlipPastMidpoint = EditorGUILayout.Toggle(new GUIContent("Flip past the midpoint",
                    "For a claw that must turn to the other side on the back half of the swing: once the " +
                    "arm passes the midpoint, it rolls a quick 180 about the AXLE (the same shaft it levels " +
                    "on) on top of the leveling. Off = the claw keeps one orientation the whole arc."),
                    levelFlipPastMidpoint);
                if (levelFlipPastMidpoint)
                    using (new EditorGUI.IndentLevelScope())
                    {
                        levelFlipFraction = EditorGUILayout.Slider(new GUIContent("Fires at",
                            "How far through the swing the flip triggers, from the arm's rest to its far " +
                            "end. 0.5 = the exact midpoint (the top of a front-to-back arc)."),
                            levelFlipFraction, 0.1f, 0.9f);
                        levelFlipDegrees = EditorGUILayout.FloatField(new GUIContent("Flip angle (deg)",
                            "How far it turns the claw. 180 faces it the opposite way."), levelFlipDegrees);
                        levelFlipSeconds = Mathf.Max(0f, EditorGUILayout.FloatField(new GUIContent(
                            "Flip time (s)", "How long the flip takes once it fires. 0 = an instant snap."),
                            levelFlipSeconds));

                        EditorGUILayout.Space(2);
                        DrawGoList("Turn on a separate link (optional)",
                            "OPTIONAL. By default the 180 rolls the whole claw about the axle. Drop a part " +
                            "here to carry that turn on its OWN link instead (a wrist/bracket between the " +
                            "mount and the claw) — same roll about the axle, just on a distinct body. Leave " +
                            "EMPTY and the mount does the turn. One part is enough.", yawWristParts);
                        if (CountNonNull(yawWristParts) > 0)
                            yawWristPivot = (Transform)EditorGUILayout.ObjectField(new GUIContent(
                                "Turn pivot",
                                "The point the turn pivots about. Empty = it uses the Pivot axle (or the " +
                                "claw's centre if none)."),
                                yawWristPivot, typeof(Transform), true);
                    }
            }
        }
    }

    // The mount's axis picker: defaults to matching the arm, which is what makes the counter-rotation
    // cancel exactly. The scene axes are the escape hatch, same as the flip/clamp pickers.
    private void DrawLevelAxis()
    {
        levelAxisPreset = (ClawRig.HingeAxis)EditorGUILayout.EnumPopup(new GUIContent(
            "Counter-rotation axis",
            "The line the mount turns about. 'Match the arm' reads it straight off the arm so the two " +
            "cancel — leave it there unless the preview draws the wrong line."), levelAxisPreset);
        if (levelAxisPreset == ClawRig.HingeAxis.Auto || levelAxisPreset == ClawRig.HingeAxis.MatchArm)
            EditorGUILayout.LabelField(" ", "= turns about the SAME line as the arm (recommended)",
                EditorStyles.miniLabel);
        else if (levelAxisPreset == ClawRig.HingeAxis.FromAxle)
            EditorGUILayout.LabelField(" ", "= turns about the Pivot axle's shaft — cancels the arm only " +
                "if that axle is mounted parallel to it", EditorStyles.miniLabel);
        else
            EditorGUILayout.LabelField(" ", "overriding the arm's axis — the counter-rotation only " +
                "cancels if this is parallel to it", EditorStyles.miniLabel);
        if (levelAxisPreset == ClawRig.HingeAxis.Custom)
            levelCustomAxis = EditorGUILayout.Vector3Field("Custom axis", levelCustomAxis);
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
        flipTravelSeconds = flipTravelSeconds,
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
        levelParts = levelParts,
        armDriver = armDriver,
        levelAxle = levelAxle,
        levelPivot = null,   // the manual Mount-pivot bucket was removed; the axle seeds the pivot now
        levelAxisPreset = levelAxisPreset,
        levelCustomAxis = levelCustomAxis,
        levelRatio = levelRatio,
        levelSweepDeg = levelSweepDeg,
        levelStiffness = levelStiffness,
        levelDamping = levelDamping,
        levelFlipPastMidpoint = levelFlipPastMidpoint,
        levelFlipDegrees = levelFlipDegrees,
        levelFlipFraction = levelFlipFraction,
        levelFlipSeconds = levelFlipSeconds,
        yawWristParts = yawWristParts,
        yawWristPivot = yawWristPivot,
    };

    private void LoadRig(ClawRig rig)
    {
        displayName = rig.displayName;
        flippingParts = new List<GameObject>(rig.flippingParts);
        flipAngleDeg = rig.flipAngleDeg;
        flipPivot = rig.flipPivot;
        flipAxisPreset = rig.flipAxisPreset;
        flipCustomAxis = rig.flipCustomAxis;
        flipTravelSeconds = rig.flipTravelSeconds;
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
        levelParts = new List<GameObject>(rig.levelParts ?? new List<GameObject>());
        armDriver = rig.armDriver;
        levelAxle = rig.levelAxle;
        levelAxisPreset = rig.levelAxisPreset;
        levelCustomAxis = rig.levelCustomAxis;
        levelRatio = rig.levelRatio;
        levelSweepDeg = rig.levelSweepDeg;
        levelStiffness = rig.levelStiffness;
        levelDamping = rig.levelDamping;
        levelFlipPastMidpoint = rig.levelFlipPastMidpoint;
        levelFlipDegrees = rig.levelFlipDegrees;
        levelFlipFraction = rig.levelFlipFraction;
        levelFlipSeconds = rig.levelFlipSeconds;
        yawWristParts = new List<GameObject>(rig.yawWristParts ?? new List<GameObject>());
        yawWristPivot = rig.yawWristPivot;
        showLevel = armDriver != null || CountNonNull(levelParts) > 0;   // reveal it if this claw uses it
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
        flipTravelSeconds = 0.35f;
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
        showLevel = false;
        levelParts = new List<GameObject>(); armDriver = null; levelAxle = null;
        levelAxisPreset = ClawRig.HingeAxis.MatchArm; levelCustomAxis = Vector3.right;
        levelRatio = -1f; levelSweepDeg = 180f; levelStiffness = 20000f; levelDamping = 500f;
        levelFlipPastMidpoint = false; levelFlipDegrees = 180f; levelFlipFraction = 0.5f; levelFlipSeconds = 0.3f;
        yawWristParts = new List<GameObject>(); yawWristPivot = null;
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
        foreach (GameObject go in levelParts)
            if (go != null)
            {
                RobotMechanisms r = go.GetComponentInParent<RobotMechanisms>();
                if (r != null) return r;
            }
        if (armDriver != null)
        {
            RobotMechanisms r = armDriver.GetComponentInParent<RobotMechanisms>();
            if (r != null) return r;
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
    private const string LevelPivotName = "ClawLevelPivot";
    private const string YawWristPivotName = "ClawYawWristPivot";
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
        public float flipTravelSeconds;
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

        // Level-keeper (optional): the claw rides a rotating arm but must not turn with it. The mount
        // parts become a revolute link slaved to the arm through a Position coupler at ratio -1, so the
        // whole claw hangs off a link that counter-rotates the arm and keeps one orientation.
        public List<GameObject> levelParts;
        public GameObject armDriver;
        public GameObject levelAxle;
        public Transform levelPivot;
        public ClawRig.HingeAxis levelAxisPreset;
        public Vector3 levelCustomAxis;
        public float levelRatio;
        public float levelSweepDeg;
        public float levelStiffness, levelDamping;
        public bool levelFlipPastMidpoint;
        public float levelFlipDegrees;
        public float levelFlipFraction;
        public float levelFlipSeconds;
        public List<GameObject> yawWristParts;
        public Transform yawWristPivot;
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

        // --- Level-keeper: a claw on a rotating arm counter-rotates so it stays level -------------
        GameObject levelLink = FirstNonNull(o.levelParts);
        ArticulationBody armBody = ResolveArmDriver(o.armDriver);
        ValidateLevelKeeper(o, levelLink, armBody, flipRoot, sections);

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
        if (levelLink != null) keep.Add(levelLink);
        GameObject wristKeep = FirstNonNull(o.yawWristParts);
        if (wristKeep != null) keep.Add(wristKeep);
        foreach (ClawRig.ClampSection s in sections) keep.Add(s.parts[0]);
        if (previous != null) StripStaleLinks(registry, previous, keep, useUndo);

        // --- Level-keeper -------------------------------------------------------------------------
        // Built FIRST, and the claw reparented under it, so the flip link's (or the jaws') articulation
        // parent is the counter-rotating mount rather than the arm — that's what makes the whole claw
        // ride one link that stays level. A no-op when no arm was named.
        Transform levelPivot = null, wristPivot = null;
        ArticulationBody levelBody = BuildLevelKeeper(registry, o, levelLink, armBody, flipRoot, sections,
            ref levelPivot, ref wristPivot, out GameObject clawTopLink, out GameObject builtWristLink, useUndo);
        // The claw hangs off the wrist if there is one, else the mount — so leveling (mount) and yawing
        // (wrist) are separate joints and the whole claw rides both.
        if (levelBody != null && flipRoot != null && !flipRoot.transform.IsChildOf(clawTopLink.transform))
            MechanismBuildUtil.EnsureChildOf(flipRoot.transform, clawTopLink.transform, useUndo);

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
                o.flipStiffness, o.flipDamping, o.flipTravelSeconds, useUndo);
            MechanismBuildUtil.AddOrGet<IgnoreRobotSelfCollision>(flipLink, useUndo);
            EnsurePivotChildOf(pivot, flipLink, useUndo);
        }

        // Everything below the flip rides it; a flipless claw hangs off the claw-top link (the wrist, or
        // the mount) if it has a level-keeper, else the chassis. Either way the jaws end up under whichever
        // link is the top of the claw, so they turn over with a flip and stay level with a level-keeper.
        Transform clawParent = flipLink != null ? flipLink.transform
            : levelBody != null ? clawTopLink.transform
            : registry.transform;

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
                    o.clampStiffness, o.clampDamping, 0f, useUndo);
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
        // Same story one level up: the flip link and the jaws became their own child bodies AFTER the
        // level-keeper's (and the wrist's) mass was measured, so those bodies still count the claw's
        // colliders. Recompute so each drives against its own link's mass, not the whole claw's.
        if (levelBody != null)
        {
            if (useUndo) Undo.RecordObject(levelBody, UndoName);
            levelBody.ResetCenterOfMass();
            levelBody.ResetInertiaTensor();
        }
        ArticulationBody wristBodyForMass = builtWristLink != null ? builtWristLink.GetComponent<ArticulationBody>() : null;
        if (wristBodyForMass != null)
        {
            if (useUndo) Undo.RecordObject(wristBodyForMass, UndoName);
            wristBodyForMass.ResetCenterOfMass();
            wristBodyForMass.ResetInertiaTensor();
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
            if (levelLink != null) clawParts.Add(levelLink);
            if (builtWristLink != null) clawParts.Add(builtWristLink);
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
        rig.flipTravelSeconds = o.flipTravelSeconds;
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
        rig.levelParts = new List<GameObject>(o.levelParts ?? new List<GameObject>());
        rig.armDriver = o.armDriver;
        rig.levelAxle = o.levelAxle;
        rig.levelPivot = levelPivot;
        rig.levelAxisPreset = o.levelAxisPreset;
        rig.levelCustomAxis = o.levelCustomAxis;
        rig.levelRatio = o.levelRatio;
        rig.levelSweepDeg = o.levelSweepDeg;
        rig.levelStiffness = o.levelStiffness;
        rig.levelDamping = o.levelDamping;
        rig.levelFlipPastMidpoint = o.levelFlipPastMidpoint;
        rig.levelFlipDegrees = o.levelFlipDegrees;
        rig.levelFlipFraction = o.levelFlipFraction;
        rig.levelFlipSeconds = o.levelFlipSeconds;
        rig.yawWristParts = new List<GameObject>(o.yawWristParts ?? new List<GameObject>());
        rig.yawWristPivot = wristPivot;
        rig.builtLevelLink = levelBody != null ? levelLink : null;
        rig.builtYawWristLink = builtWristLink;
        rig.autoAssignButtons = o.autoAssignButtons;

        if (useUndo) Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(registry);
        EditorUtility.SetDirty(rig);
        if (registry.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(registry.gameObject.scene);

        return BuildReport(o, flipLink, sections, grabWired, buttonNote, levelBody != null, armBody);
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
        // The wrist and the mount are the claw's PARENT links, so they come last (deepest child first):
        // stripping a parent's body while a child below it still had a joint would re-parent that joint.
        StripLink(registry, rig.builtYawWristLink, useUndo);
        StripLink(registry, FirstNonNull(rig.yawWristParts), useUndo);
        StripLink(registry, rig.builtLevelLink, useUndo);
        StripLink(registry, FirstNonNull(rig.levelParts), useUndo);

        RemoveSlideFollower(rig.flipCylinderBody, useUndo);
        RemoveSlideFollower(rig.flipCylinderRod, useUndo);
        RemoveSlideFollower(rig.clampCylinderBody, useUndo);
        RemoveSlideFollower(rig.clampCylinderRod, useUndo);

        RemoveGrab(registry, useUndo);
        foreach (Transform t in registry.GetComponentsInChildren<Transform>(true))
            if (t != null && (t.name == FlipPivotName || t.name == LevelPivotName ||
                              t.name == YawWristPivotName || t.name.StartsWith(ClampPivotPrefix)))
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
        // A mount or wrist dropped from the form (or moved to a different part) loses its joint + coupler.
        if (previous.builtLevelLink != null && !keep.Contains(previous.builtLevelLink))
            StripLink(registry, previous.builtLevelLink, useUndo);
        if (previous.builtYawWristLink != null && !keep.Contains(previous.builtYawWristLink))
            StripLink(registry, previous.builtYawWristLink, useUndo);

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
        bool startExtended, float stiffness, float damping, float travelSeconds, bool useUndo)
    {
        RobotMechanisms.Mechanism mech = registry.Find(id);
        if (mech == null || mech.pneumatic == null) return;

        PneumaticActuator act = mech.pneumatic;
        if (useUndo) Undo.RecordObject(act, UndoName);
        act.startExtended = startExtended;
        act.stiffness = stiffness;
        act.damping = damping;
        // The jaws keep the honest pneumatic snap; only the flip is paced, and only because half a
        // turn is otherwise invisible.
        act.travelSeconds = travelSeconds;
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
        // MatchArm and FromAxle only mean something to the level-keeper (they read the arm / the axle
        // part); anywhere else they're just the default guess, so they can't misbehave if picked on a
        // flip or a clamp.
        if (preset == ClawRig.HingeAxis.Auto || preset == ClawRig.HingeAxis.MatchArm ||
            preset == ClawRig.HingeAxis.FromAxle)
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

    // --- Level-keeper: a claw on a rotating arm counter-rotates so it stays level ------------------

    // The arm's ArticulationBody: the part the user spun up with Build Chain, or the nearest jointed
    // thing above whatever they pointed at. Null if there's no body to find.
    internal static ArticulationBody ResolveArmDriver(GameObject armDriver)
    {
        if (armDriver == null) return null;
        ArticulationBody body = armDriver.GetComponent<ArticulationBody>();
        return body != null ? body : armDriver.GetComponentInParent<ArticulationBody>();
    }

    // Refuses a level-keeper that couldn't work rather than half-building it. A no-op when neither an arm
    // nor a mount was named — the level-keeper is optional.
    private static void ValidateLevelKeeper(Options o, GameObject levelLink, ArticulationBody armBody,
        GameObject flipRoot, List<ClawRig.ClampSection> sections)
    {
        if (levelLink == null && o.armDriver == null) return;

        if (levelLink == null)
            throw new InvalidOperationException(
                "You named a rotating arm but no mount to keep level. Add the mount parts, or clear the " +
                "arm if the claw is meant to turn with it.");
        if (o.armDriver == null)
            throw new InvalidOperationException(
                $"The mount '{levelLink.name}' has no rotating arm to ride. Point 'Rotating arm' at the " +
                "part you spun up with Build Chain, and build that first.");
        if (flipRoot == null && sections.Count == 0)
            throw new InvalidOperationException(
                "A level-keeper needs a claw to keep level. Add the flip or the clamp parts as well.");
        if (armBody == null)
            throw new InvalidOperationException(
                $"The rotating arm '{o.armDriver.name}' isn't a joint yet. Turn it into a spinning joint " +
                "with Build Chain first, then build the claw.");
        if (armBody.jointType != ArticulationJointType.RevoluteJoint)
            throw new InvalidOperationException(
                $"The rotating arm '{armBody.name}' is a {armBody.jointType}, not a rotating joint. A " +
                "mount can only counter-rotate an arm that itself turns (build it Continuous or Revolute).");
        if (armBody.isRoot)
            throw new InvalidOperationException(
                $"The rotating arm '{armBody.name}' is the robot's root, which never moves. Point it at " +
                "the swinging arm link, not the chassis.");
        if (armBody.gameObject == levelLink)
            throw new InvalidOperationException(
                $"'{levelLink.name}' is named as both the arm and the mount. The arm turns; the mount " +
                "hangs off it and counter-turns — they have to be two different links.");
        if (armBody.transform.IsChildOf(levelLink.transform))
            throw new InvalidOperationException(
                $"The arm '{armBody.name}' sits INSIDE the mount '{levelLink.name}'. The mount hangs off " +
                "the arm, not the other way round — point the arm at the swinging link and the mount at " +
                "the bracket the claw bolts to.");

        // A part takes one role only. The mount can't also be a flip/clamp link — those become their
        // own joints hanging off it.
        foreach (GameObject part in o.levelParts)
        {
            if (part == null) continue;
            if (part == flipRoot)
                throw new InvalidOperationException(
                    $"'{part.name}' is listed as both the mount and the flipping assembly. The mount is " +
                    "the bracket that stays level; the flip is the claw that hangs off it.");
            foreach (ClawRig.ClampSection s in sections)
                if (s.parts.Contains(part))
                    throw new InvalidOperationException(
                        $"'{part.name}' is listed as both the mount and a clamp half.");
        }

        // The yaw wrist, if named, is its own link between the mount and the claw — it can't double as
        // the mount, the flip, a jaw, or the arm, and it only does anything with the midpoint flip on.
        GameObject wrist = FirstNonNull(o.yawWristParts);
        if (wrist != null)
        {
            if (!o.levelFlipPastMidpoint)
                throw new InvalidOperationException(
                    "A yaw wrist was named but 'Flip past the midpoint' is off, so it would never turn. " +
                    "Tick the flip, or clear the wrist.");
            if (wrist == levelLink)
                throw new InvalidOperationException(
                    $"'{wrist.name}' is named as both the mount and the yaw wrist. The mount levels; the " +
                    "wrist yaws — they're two different links.");
            if (wrist == flipRoot)
                throw new InvalidOperationException(
                    $"'{wrist.name}' is named as both the yaw wrist and the flipping assembly.");
            if (armBody != null && wrist == armBody.gameObject)
                throw new InvalidOperationException($"'{wrist.name}' is the arm, not a wrist link.");
            foreach (ClawRig.ClampSection s in sections)
                if (s.parts.Contains(wrist))
                    throw new InvalidOperationException(
                        $"'{wrist.name}' is named as both the yaw wrist and a clamp half.");
        }
    }

    // Turns the mount into a revolute link slaved to the arm at ratio -1 (a Position coupler), so the arm
    // rotates +θ, the mount -θ, and the claw hanging off the mount keeps one orientation. Returns the
    // mount's body (null when no level-keeper was asked for). PASSIVE — no actuator, no registry entry,
    // exactly like a chained station or a mirrored jaw — so nothing on a button fights the coupler.
    private static ArticulationBody BuildLevelKeeper(RobotMechanisms registry, Options o,
        GameObject levelLink, ArticulationBody armBody, GameObject flipRoot,
        List<ClawRig.ClampSection> sections, ref Transform levelPivot, ref Transform wristPivot,
        out GameObject clawTopLink, out GameObject builtWristLink, bool useUndo)
    {
        // The claw hangs off whatever this returns as its "top" — the wrist if there is one, else the
        // mount. Assigned before the early-out so the out contract holds even with no level-keeper.
        clawTopLink = levelLink;
        builtWristLink = null;
        if (levelLink == null || armBody == null) return null;

        GameObject wristLink = FirstNonNull(o.yawWristParts);

        // The mount has to sit DIRECTLY under the arm — not merely somewhere beneath it. CAD often draws
        // the mount INSIDE the flip assembly, and leaving it nested there is the bug that makes it a leaf
        // that flips alone while the claw stays rigid to the arm: the flip link then can't be reparented
        // under the mount without forming a cycle (parenting a node under its own descendant), so that
        // step silently no-ops. Pulling the mount up to be the arm's own child breaks the nesting so the
        // flip → mount → arm chain can form. EnsureChildOf self-guards when it's already a direct child
        // and keeps world pose, so this is idempotent and never moves anything in the scene.
        MechanismBuildUtil.EnsureChildOf(levelLink.transform, armBody.transform, useUndo);

        // The rotation CENTRE. An explicit axle part pins it onto the shaft the claw actually pivots on —
        // the fix for the "huge arch": without one the seed falls back to the claw's own middle, and on a
        // claw whose jaws can't be measured that fell all the way to the mount's CAD origin (often out at
        // the robot's centre), so the counter-rotation swung the whole claw on a robot-radius arc. Point
        // the mount at its axle and it turns THERE, staying at the end of the arm. Pre-declared so the &&
        // short-circuit still leaves them definitely assigned when there's no axle.
        Vector3 axleAxisWorld = Vector3.right, axleCenterWorld = Vector3.zero;
        bool haveAxle = o.levelAxle != null &&
            ChainBuilder.TryAxleWorldAxis(o.levelAxle, out axleAxisWorld, out axleCenterWorld);
        Vector3 seedWorld = haveAxle ? axleCenterWorld : LevelPivotSeed(levelLink, flipRoot, sections);

        // Weld the axle to the ARM so it rides the arm's end through the whole swing — staying visually
        // bolted to the tip — instead of drifting off (it was going "above the arm" because it hung on the
        // counter-rotating mount). EnsureChildOf keeps world pose, so the pivot just seeded from its centre
        // is unaffected, and doing it here (before the mount's mass is computed) keeps the axle out of the
        // mount body.
        if (haveAxle) MechanismBuildUtil.EnsureChildOf(o.levelAxle.transform, armBody.transform, useUndo);

        // reseed: haveAxle — with an axle the marker must follow it on every rebuild, or a stale marker
        // from the last build pins the pivot and moving the axle changes nothing.
        Transform pivot = EnsurePivot(o.levelPivot, levelLink, LevelPivotName, seedWorld, useUndo,
            reseed: haveAxle);
        levelPivot = pivot;

        Vector3 axis, anchor;
        if (o.levelAxisPreset == ClawRig.HingeAxis.FromAxle && haveAxle)
        {
            // The axle's own shaft, pre-negated because ConfigureJointLink negates the axis it's handed.
            axis = levelLink.transform.InverseTransformDirection(-axleAxisWorld);
            if (axis.sqrMagnitude < 1e-8f) axis = Vector3.right;
            axis.Normalize();
            anchor = levelLink.transform.InverseTransformPoint(pivot.position);
        }
        else
        {
            ResolveLevelAxisAnchor(levelLink, pivot, o.levelAxisPreset, o.levelCustomAxis, armBody,
                out axis, out anchor);
        }

        // Symmetric travel: with ratio -1 and the arm resting at joint 0, the mount tracks -armAngle, so
        // it must reach as far each way as the arm swings. It reads the arm's CURRENT bounded span, so a
        // rebuild after retuning the arm's range picks up the new travel — a level-keeper built against
        // an old [0,180] arm has stale ±limits and clamps once the arm is widened to (say) [-270,0],
        // which strands the claw short of level on the far half. The midpoint flip needs its own degrees
        // on top, and a margin so the target never sits exactly ON the limit (a drive pinned to its stop
        // fights and buzzes).
        // The midpoint flip only rides the MOUNT when there's no wrist (matches the coupler dispatch below);
        // with a wrist the flip is on the wrist, so the mount doesn't need room for it.
        float flipRoom = (o.levelFlipPastMidpoint && wristLink == null) ? Mathf.Abs(o.levelFlipDegrees) : 0f;
        float levelNeed = Mathf.Max(1f, Mathf.Abs(o.levelSweepDeg), ArmTravelDeg(armBody));
        // A Unity revolute joint is HARD-CAPPED at ±360° — set the limits past that and PhysX clamps them
        // with its own cryptic "out of range" warning. Cap here so that warning never fires. The limit
        // follows the Sweep field (which may be set generously); behaviour is unchanged — PhysX was already
        // clamping to 360.
        const float RevoluteMax = 360f;
        float sweep = Mathf.Min(levelNeed + flipRoom + 45f, RevoluteMax);
        // ...but only WARN when the ACTUAL motion at the arm's own extreme (its travel + the flip) can't fit
        // in 360°, so a generously-set Sweep field doesn't cry wolf. A wide arm AND a flip on one joint is
        // the case that genuinely can't fit.
        float actualNeed = ArmTravelDeg(armBody) + flipRoom;
        if (actualNeed > RevoluteMax + 0.5f)
            Debug.LogWarning(
                $"Claw level-keeper: leveling this {ArmTravelDeg(armBody):F0}° arm swing" +
                (flipRoom > 0f ? $" plus a {flipRoom:F0}° midpoint flip" : "") +
                $" needs {actualNeed:F0}° on one joint, but a revolute maxes at 360°, so the claw won't " +
                "fully " + (flipRoom > 0f ? "level-and-flip" : "level") + " at the far end of the swing. " +
                (flipRoom > 0f
                    ? "Fix: turn OFF 'Flip past the midpoint' (use the claw's own flip button instead), " +
                      "narrow the arm's swing, or drop a part into 'Turn on a separate link' so the flip " +
                      "rides its own joint."
                    : "Fix: narrow the arm's swing so the mount stays within 360°."), levelLink);

        ArticulationBody body = AddMechanismJoint.ConfigureJointLink(levelLink,
            AddMechanismJoint.JointType.Revolute, axis, anchor, -sweep, sweep,
            new AddMechanismJoint.Options
            {
                alsoMove = LevelExtraParts(o.levelParts, levelLink, flipRoot, sections),
            }, registry, useUndo);

        // Passive linkage: strip any registration/actuator the link might carry from a prior life, then
        // couple it. A registered mount would let ButtonRouter fight the coupler for the drive.
        string levelId = UrdfPostProcessor.Slugify(levelLink.name);
        UrdfPostProcessor.RemoveMechanism(registry, levelId, useUndo);
        MechanismBuildUtil.ClearMechanismBindings(registry.robotId, levelId);
        MechanismBuildUtil.RemoveComponents<MotorActuator>(levelLink, useUndo);
        MechanismBuildUtil.RemoveComponents<PneumaticActuator>(levelLink, useUndo);
        MechanismBuildUtil.RemoveComponents<JointCoupler>(levelLink, useUndo);   // DisallowMultiple

        JointCoupler coupler = MechanismBuildUtil.AddOrGet<JointCoupler>(levelLink, useUndo);
        if (useUndo) Undo.RecordObject(coupler, UndoName);
        coupler.follower = body;
        coupler.driver = armBody;
        coupler.mode = JointCoupler.CoupleMode.Position;
        // -1 keeps the claw dead level; between -1 and 0 it leans part way with the arm. 0 or positive
        // could never cancel the tumble, so it's clamped away from there.
        coupler.ratio = o.levelRatio < -1e-3f ? o.levelRatio : -1f;
        coupler.offsetDeg = 0f;   // the claw is modelled level at the arm's rest pose (joint 0)
        coupler.positionStiffness = o.levelStiffness;
        coupler.positionDamping = o.levelDamping;
        // The midpoint 180 rides the MOUNT itself when there's no separate wrist link — the mount both
        // levels AND adds the turn, about the same axle shaft, pivoting on the axle. A wrist link (below)
        // takes the turn instead so it sits on its own body; either way it's a roll about the axle.
        coupler.flipPastMidpoint = o.levelFlipPastMidpoint && wristLink == null;
        coupler.flipDegrees = o.levelFlipDegrees;
        coupler.flipFraction = o.levelFlipFraction;
        coupler.flipTravelSeconds = o.levelFlipSeconds;
        // Uncapped, like the mirrored jaw: the mount mirrors the arm and must always reach its target,
        // or the claw it carries sags off level under its own weight.
        coupler.forceLimit = float.MaxValue;
        coupler.BakeDrive();     // so edit-mode Physics.Simulate matches Play
        EditorUtility.SetDirty(coupler);

        MechanismBuildUtil.AddOrGet<IgnoreRobotSelfCollision>(levelLink, useUndo);
        EnsurePivotChildOf(pivot, levelLink, useUndo);

        // --- Optional flip wrist -----------------------------------------------------------------------
        // A second link between the mount and the claw that carries the 180 turn on its OWN body rather
        // than doubling it onto the mount — for a claw whose turn happens on a distinct physical part. It
        // pivots on the AXLE and rolls about the axle's shaft (same as the mount turn), tracks NOTHING
        // (ratio 0 → holds still) and turns 180 past the midpoint. The claw reparents under it, so leveling
        // (mount) and the turn (wrist) stay separate joints. Not needed for a plain axle roll.
        if (wristLink != null)
        {
            MechanismBuildUtil.EnsureChildOf(wristLink.transform, levelLink.transform, useUndo);
            // The 180 turn pivots on the AXLE too (same shaft the mount levels about), so it turns at the
            // end of the arm rather than about the claw's own middle.
            Vector3 wristSeed = haveAxle ? axleCenterWorld : LevelPivotSeed(wristLink, flipRoot, sections);
            Transform wPivot = EnsurePivot(o.yawWristPivot, wristLink, YawWristPivotName, wristSeed, useUndo,
                reseed: haveAxle);
            wristPivot = wPivot;

            // The 180 turns about the AXLE's own shaft — a roll over the mount shaft, the way a claw on a
            // single axle really reorients as the arm carries it over — NOT a yaw about vertical (this
            // mechanism has no vertical pin, so that read as impossible). Whichever way the user aimed the
            // axle IS the turn axis, so reorienting the axle changes the motion. Falls back to the robot's
            // up only when there's no axle to read. A 180 is sign-agnostic, so no negate bookkeeping.
            Vector3 flipAxisWorld = haveAxle ? axleAxisWorld : registry.transform.up;
            Vector3 wristAxis = wristLink.transform.InverseTransformDirection(flipAxisWorld);
            if (wristAxis.sqrMagnitude < 1e-8f) wristAxis = Vector3.up;
            wristAxis.Normalize();
            Vector3 wristAnchor = wristLink.transform.InverseTransformPoint(wPivot.position);

            float yaw = Mathf.Max(1f, Mathf.Abs(o.levelFlipDegrees));
            float wristSweep = yaw + 45f;
            ArticulationBody wristBody = AddMechanismJoint.ConfigureJointLink(wristLink,
                AddMechanismJoint.JointType.Revolute, wristAxis, wristAnchor, -wristSweep, wristSweep,
                new AddMechanismJoint.Options(), registry, useUndo);

            string wristId = UrdfPostProcessor.Slugify(wristLink.name);
            UrdfPostProcessor.RemoveMechanism(registry, wristId, useUndo);
            MechanismBuildUtil.ClearMechanismBindings(registry.robotId, wristId);
            MechanismBuildUtil.RemoveComponents<MotorActuator>(wristLink, useUndo);
            MechanismBuildUtil.RemoveComponents<PneumaticActuator>(wristLink, useUndo);
            MechanismBuildUtil.RemoveComponents<JointCoupler>(wristLink, useUndo);

            JointCoupler wc = MechanismBuildUtil.AddOrGet<JointCoupler>(wristLink, useUndo);
            if (useUndo) Undo.RecordObject(wc, UndoName);
            wc.follower = wristBody;
            wc.driver = armBody;
            wc.mode = JointCoupler.CoupleMode.Position;
            wc.ratio = 0f;                       // doesn't track the arm — holds level, then yaws at the top
            wc.offsetDeg = 0f;
            wc.positionStiffness = o.levelStiffness;
            wc.positionDamping = o.levelDamping;
            wc.flipPastMidpoint = o.levelFlipPastMidpoint;
            wc.flipDegrees = o.levelFlipDegrees;
            wc.flipFraction = o.levelFlipFraction;
            wc.flipTravelSeconds = o.levelFlipSeconds;
            wc.forceLimit = float.MaxValue;
            wc.BakeDrive();
            EditorUtility.SetDirty(wc);

            MechanismBuildUtil.AddOrGet<IgnoreRobotSelfCollision>(wristLink, useUndo);
            EnsurePivotChildOf(wPivot, wristLink, useUndo);
            clawTopLink = wristLink;
            builtWristLink = wristLink;
        }

        return body;
    }

    // The world line the arm's joint actually spins about (right-hand positive), read straight off the
    // built joint's anchor frame. Matching the mount to THIS — same line, same sign — is what makes a
    // ratio of -1 cancel the arm's turn rather than double it.
    internal static Vector3 DriverWorldTwist(ArticulationBody arm)
    {
        if (arm == null) return Vector3.right;
        Vector3 world = arm.transform.rotation * (arm.anchorRotation * Vector3.right);
        return world.sqrMagnitude > 1e-8f ? world.normalized : arm.transform.right;
    }

    // The arm's bounded travel in degrees, or 0 for a free-spinning (Continuous) arm whose limits mean
    // nothing.
    private static float ArmTravelDeg(ArticulationBody arm)
    {
        if (arm == null || arm.twistLock == ArticulationDofLock.FreeMotion) return 0f;
        return Mathf.Abs(arm.xDrive.upperLimit - arm.xDrive.lowerLimit);
    }

    // The mount's hinge axis (link-local) + anchor. "Match the arm" reads the arm's own spin line so the
    // counter-rotation cancels exactly; the other presets fall through to the shared resolver (the flip's
    // pins), for the rare mount that must turn about something else. ConfigureJointLink negates the axis
    // it's handed, so the arm-matched case is pre-negated to land back on the arm's true line.
    internal static void ResolveLevelAxisAnchor(GameObject link, Transform pivot, ClawRig.HingeAxis preset,
        Vector3 customAxis, ArticulationBody armBody, out Vector3 axis, out Vector3 anchor)
    {
        // FromAxle with no axle to read (the caller resolves the axle case itself before getting here)
        // falls back to matching the arm — the safe exact-cancel default — rather than a stray guess.
        if ((preset == ClawRig.HingeAxis.Auto || preset == ClawRig.HingeAxis.MatchArm ||
             preset == ClawRig.HingeAxis.FromAxle) && armBody != null)
        {
            axis = link.transform.InverseTransformDirection(-DriverWorldTwist(armBody));
            if (axis.sqrMagnitude < 1e-8f) axis = Vector3.right;
            axis.Normalize();
            anchor = pivot != null ? link.transform.InverseTransformPoint(pivot.position) : Vector3.zero;
            return;
        }
        // No arm to match (a preview before the driver is set) or a deliberate override: reuse the flip's
        // axis resolution, whose "Auto" is the robot's front/back line.
        ResolveAxisAnchor(link, pivot, preset, customAxis, isFlip: true, out axis, out anchor);
    }

    // Where the mount hinges by default: the combined centre of the CLAW it carries (flip + jaws), NOT
    // the mount's own origin. The counter-rotation only cancels the arm's ORIENTATION (any parallel axis
    // does that) — but its POSITION is decided by the pivot, and this is the fix for a claw that "swings
    // off into the scene": hinging about a point OFFSET from the claw (the mount's origin, which on a
    // bracket with no mesh of its own is wherever the CAD dropped it — metres from the jaws) makes the
    // leveling sweep the claw through a huge arc, because the claw's offset from the pivot rotates
    // opposite the arm. Centre the pivot on the claw's own middle and that arc collapses: the claw spins
    // in place to stay level while its centre rides the arm's arc, staying put at the end of the arm.
    // Falls back to the mount's centre only when there's no claw to measure.
    internal static Vector3 LevelPivotSeed(GameObject levelLink, GameObject flipRoot,
        List<ClawRig.ClampSection> sections)
    {
        Bounds all = default;
        bool has = false;
        void Add(GameObject go)
        {
            if (go == null || !MechanismBuildUtil.TryBounds(go, out Bounds b)) return;
            if (!has) { all = b; has = true; } else all.Encapsulate(b);
        }
        // The JAWS first — the claw's business end, closest to its centre of mass, and a tight bound.
        // The flip link is often a big enclosing GROUP whose bounds centre drifts metres off the actual
        // claw, which is what left the pivot far from the jaws and swung the claw off the arm. Fall back
        // to the flip only if there are no jaws to measure, and to the mount only if neither exists.
        if (sections != null)
            foreach (ClawRig.ClampSection s in sections)
                if (s?.parts != null)
                    foreach (GameObject p in s.parts) Add(p);
        if (!has) Add(flipRoot);
        return has ? all.center : MechanismBuildUtil.BoundsCenterOrOrigin(levelLink);
    }

    // The mount's extra parts to weld in: everything listed except the driven mount link, and except
    // anything tangled up with the claw that hangs off it (the flip link or a jaw), which has to stay
    // free to become its OWN joint rather than be swallowed into the mount.
    private static GameObject[] LevelExtraParts(List<GameObject> list, GameObject levelLink,
        GameObject flipRoot, List<ClawRig.ClampSection> sections)
    {
        var extras = new List<GameObject>();
        if (list == null) return extras.ToArray();
        foreach (GameObject go in list)
        {
            if (go == null || go == levelLink) continue;
            if (flipRoot != null && (go == flipRoot || go.transform.IsChildOf(flipRoot.transform) ||
                                     flipRoot.transform.IsChildOf(go.transform)))
                continue;
            bool tangled = false;
            if (sections != null)
                foreach (ClawRig.ClampSection s in sections)
                {
                    Transform jaw = s.parts[0].transform;
                    if (go == s.parts[0] || go.transform.IsChildOf(jaw) || jaw.IsChildOf(go.transform))
                    { tangled = true; break; }
                }
            if (!tangled) extras.Add(go);
        }
        return extras.ToArray();
    }

    internal const string PreviewLevelPivotName = LevelPivotName;

    // Read-only twin of the level-keeper's pivot+axis resolution, for the Scene preview: same answer, but
    // it creates nothing. The drawn line is the arm's real spin line when the mount is matched to it — and
    // the drawn POINT is the axle's centre when one is assigned, so you see the pivot land on the shaft
    // before committing.
    internal static bool TryPreviewLevelHinge(GameObject levelLink, Transform assignedPivot,
        ClawRig.HingeAxis preset, Vector3 customAxis, ArticulationBody armBody, GameObject levelAxle,
        GameObject flipRoot, List<ClawRig.ClampSection> sections,
        out Vector3 pivotWorld, out Vector3 axisWorld, out Transform existingPivot)
    {
        pivotWorld = Vector3.zero;
        axisWorld = Vector3.right;
        existingPivot = null;
        if (levelLink == null) return false;

        Vector3 axleAxis = Vector3.right, axleCenter = Vector3.zero;
        bool haveAxle = levelAxle != null &&
            ChainBuilder.TryAxleWorldAxis(levelAxle, out axleAxis, out axleCenter);

        // An explicit marker wins (the user dragged it); otherwise the axle centre, else the claw's middle.
        existingPivot = assignedPivot != null ? assignedPivot : FindChild(levelLink.transform, LevelPivotName);
        pivotWorld = existingPivot != null ? existingPivot.position
            : haveAxle ? axleCenter
            : LevelPivotSeed(levelLink, flipRoot, sections);

        if (preset == ClawRig.HingeAxis.FromAxle && haveAxle)
        {
            axisWorld = axleAxis;
        }
        else if (preset == ClawRig.HingeAxis.Auto || preset == ClawRig.HingeAxis.MatchArm ||
                 preset == ClawRig.HingeAxis.FromAxle)
        {
            if (armBody != null) axisWorld = DriverWorldTwist(armBody);
        }
        else
        {
            ResolveLevelAxisAnchor(levelLink, existingPivot, preset, customAxis, armBody,
                out Vector3 axisLocal, out _);
            axisWorld = levelLink.transform.TransformDirection(axisLocal).normalized;
        }
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
            if (t.name == FlipPivotName || t.name == LevelPivotName || t.name == YawWristPivotName ||
                t.name == MouthName || t.name == HoldName ||
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
            rig.levelPivot = null;
            rig.yawWristPivot = null;
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
        Vector3 seedPoint, bool useUndo, bool reseed = false)
    {
        if (assigned != null) return assigned;

        Transform existing = FindChild(link.transform, markerName);
        if (existing != null)
        {
            // On a REBUILD the marker from the last build is still here. For a fine-tune marker (flip/clamp)
            // that's the point — keep where the user dragged it. But an axle-DRIVEN marker (level/wrist) must
            // track the seed, or the stale marker overrides the axle and moving the axle does NOTHING on
            // rebuild (the reported bug). Re-seed it to the current point when the caller says so.
            if (reseed)
            {
                if (useUndo) Undo.RecordObject(existing, UndoName);
                existing.position = seedPoint;
                EditorUtility.SetDirty(existing);
            }
            return existing;
        }

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
        List<ClawRig.ClampSection> sections, bool grabWired, string buttonNote,
        bool levelBuilt, ArticulationBody armBody)
    {
        string levelLine = levelBuilt
            ? $"• STAYS LEVEL: the mount rides '{(armBody != null ? armBody.name : "the arm")}' but " +
              $"counter-rotates it ×{(o.levelRatio < -1e-3f ? o.levelRatio : -1f):0.##}, so the whole " +
              "claw keeps its orientation as the arm swings.\n" +
              (armBody != null && armBody.twistLock == ArticulationDofLock.FreeMotion
                  ? "  ⚠ The arm is a FREE-SPINNING (Continuous) joint. The claw only stays level within " +
                    $"±{Mathf.Max(1f, Mathf.Abs(o.levelSweepDeg)):0}° of the arm's rest — for a front-to-" +
                    "back swing, build the arm as a bounded revolute, or widen the sweep range.\n"
                  : "")
            : "";
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
              "opening in the Scene view.\n" +
              (o.grabAutoUpright
                  ? "• Each piece is stood up along the ROBOT's up as it comes in, so a pin lying " +
                    "flat is carried the same way round as an upright one — and the flip still turns " +
                    "a held stack over.\n"
                  : "")
            : "• GRAB: off — the jaws are solid but won't retain a piece.\n";

        return $"Built the claw '{o.displayName}'.\n\n" + levelLine + flipLine + clampLine + grabLine +
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
