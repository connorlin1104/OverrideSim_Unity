using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Role-assignment builder for a CASCADE (telescoping) lift — a stack of C-channel bars that slide out
// of each other. You drop each bar's parts into its own block and it rigs the whole lift.
//
// WHY REAL JOINTS, unlike the DR4B. A double-reverse-four-bar is a closed kinematic loop, which an
// ArticulationBody tree cannot express, so that builder fakes the motion with transform followers on
// collider-disabled parts. A cascade is the opposite: a plain serial chain, exactly what an
// articulation IS. So every bar becomes a real prismatic link nested inside the one below it, with
// live colliders — and the claw arm that rides the top rides for free, because it becomes a child in
// the physics tree. That last part is not a preference: a claw is made of real articulation links,
// and PhysX owns their transforms, so no transform follower could ever carry one.
//
//   chassis
//   +-- CascadeMotor          hidden revolute + MotorActuator — the ONE thing on the buttons
//   +-- CascadeStage1         prismatic, slides along ITS OWN C-channel
//       +-- CascadeStage2     prismatic
//           +-- CascadeStage3 prismatic
//               +-- ClawArm -> Claw   (reparented here; their own joints keep working)
//
// The hidden driver exists so one button pair drives the lift whatever the stage count or order is:
// a motor on bar 1 could never start bar 2, and with the bars sequenced top-first, bar 1 moves LAST.
// CascadeLift turns the driver's 0->1 progress into a position target for every bar.
//
// Usage: rig the drivetrain and the claw/arm FIRST, then open the robot PREFAB (this tool reparents)
// and run Tools > RoboSim > Robot > Mechanisms > Build Cascade Lift (roles).
public class CascadeBuilderWindow : EditorWindow
{
    private const string Title = "Build Cascade Lift";

    [SerializeField] private string displayName = "Cascade";
    [SerializeField] private List<CascadeRig.Bar> bars = new List<CascadeRig.Bar>();
    [SerializeField] private List<GameObject> ridesAlong = new List<GameObject>();
    [SerializeField] private int attachToBarIndex = -1;
    [SerializeField] private bool oneAtATime;
    [SerializeField] private bool topFirst;
    [SerializeField] private float overlapHoles = 2f;
    [SerializeField] private float holePitch = CascadeSetup.DefaultHolePitch;
    [SerializeField] private bool reverseDirection;
    [SerializeField] private float raiseSeconds = 2f;
    [SerializeField] private float stageStiffness = 20000f;
    [SerializeField] private float stageDamping = 500f;
    [SerializeField] private float stageForceLimit = 5000f;
    [SerializeField] private bool autoAssignButtons = true;

    [SerializeField] private bool showAdvanced;
    [SerializeField] private bool showPreview = true;
    private Vector2 scroll;

    // The preview draws into the Scene view while the window is open. Which way a bar slides is the
    // one thing this tool has to GUESS (it reads the channel's long axis and points it up the robot),
    // so the guess is drawn before you commit to it rather than discovered in Play.
    void OnEnable() { SceneView.duringSceneGui += OnSceneGUI; }
    void OnDisable() { SceneView.duringSceneGui -= OnSceneGUI; }

    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Build Cascade Lift (roles)", false, 25)]
    private static void Open()
    {
        CascadeBuilderWindow window = GetWindow<CascadeBuilderWindow>(true, Title, true);
        window.minSize = new Vector2(500f, 640f);
        window.Show();
    }

    void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.HelpBox(
            "Rigs a telescoping cascade and puts it on one button pair. Add a block per bar, bottom " +
            "first — bar 1 slides on the robot, bar 2 slides on bar 1, and so on.\n\n" +
            "   Parts on this bar  =  everything bolted to it: motors, sprockets, pulleys, string mounts\n" +
            "   C-channel(s)       =  that bar's channels (group both under one empty)\n" +
            "   Rides with the lift  =  the claw arm and its claw\n\n" +
            "The channel bucket is separate because the build reads the slide DIRECTION and the travel " +
            "off it — that's what lets a slightly angled cascade lean the way it really leans.\n\n" +
            "Leave the channel that's BOLTED TO THE ROBOT out entirely: every bar you list slides.\n" +
            "Build the drivetrain, the arm and the claw FIRST — this tool moves them onto the top bar.",
            MessageType.Info);

        showPreview = EditorGUILayout.ToggleLeft(new GUIContent(
            "Show slide directions in the Scene view",
            "Draws each bar's slide direction and how far it goes. If an arrow points the wrong way " +
            "here, the bar will slide the wrong way after you build."), showPreview);

        RobotMechanisms registry = ResolveRegistry();
        DrawExistingRig(registry);

        // Reparenting into an articulation tree is not something a prefab instance can record as an
        // override — the same requirement (and wording) as Build Claw and Build Chain.
        if (PrefabStageUtility.GetCurrentPrefabStage() == null)
            EditorGUILayout.HelpBox(
                "You're not in Prefab Mode. This tool reparents parts, which a prefab instance can't " +
                "store as an override — open the robot prefab first (double-click it) or your work " +
                "won't survive.", MessageType.Warning);

        EditorGUILayout.Space();
        displayName = EditorGUILayout.TextField(new GUIContent("Lift name",
            "Shown on the Configure Controller screen."), displayName);

        DrawBars(registry);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Rides with the lift", EditorStyles.boldLabel);
        DrawGoList("Carried parts",
            "The claw arm and its claw — anything that moves WITH the lift without being part of it. " +
            "They're reparented onto the top bar, so their own joints keep working while the lift " +
            "carries them. Don't list the bars themselves here.", ridesAlong);
        if (CountNonNull(ridesAlong) > 0 && bars.Count > 0)
        {
            var choices = new string[bars.Count + 1];
            choices[0] = "Top bar (normal)";
            for (int i = 0; i < bars.Count; i++) choices[i + 1] = $"Bar {i + 1}";
            int current = attachToBarIndex < 0 ? 0 : Mathf.Min(attachToBarIndex + 1, bars.Count);
            int picked = EditorGUILayout.Popup(new GUIContent("Mounted on",
                "Which bar the carried parts bolt to. The top one unless your arm mounts lower down."),
                current, choices);
            attachToBarIndex = picked == 0 ? -1 : picked - 1;
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Motion", EditorStyles.boldLabel);
        oneAtATime = EditorGUILayout.Popup(new GUIContent("Bars move",
            "How the stages share one press. Both are real builds — pick what your cascade does."),
            oneAtATime ? 1 : 0, new[] { "All at the same time", "One at a time" }) == 1;
        if (oneAtATime)
            topFirst = EditorGUILayout.Popup(new GUIContent("  Order",
                "Which end runs out first. Either is fine — it just must not be random."),
                topFirst ? 1 : 0, new[] { "Ascending (bottom bar first)", "Descending (top bar first)" }) == 1;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Travel + feel", EditorStyles.boldLabel);
        overlapHoles = EditorGUILayout.FloatField(new GUIContent("Overlap (holes)",
            "How many holes of channel stay overlapped at full extension — a bar can't come all the " +
            "way out or it falls off. 2 is the usual build; 1 reaches a little higher."), overlapHoles);
        reverseDirection = EditorGUILayout.Toggle(new GUIContent("Slide the other way",
            "For CAD drawn already extended, or a cascade that reaches downward."), reverseDirection);
        raiseSeconds = EditorGUILayout.FloatField(new GUIContent("Raise time (s)",
            "Seconds to extend fully while holding the button. Editable live afterward on the " +
            "CascadeLift component — no rebuild."), raiseSeconds);
        autoAssignButtons = EditorGUILayout.Toggle(new GUIContent("Auto-assign buttons",
            "Map the lift to the next free button(s). Off keeps whatever it's mapped to now."),
            autoAssignButtons);

        showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced", true);
        if (showAdvanced)
        {
            holePitch = EditorGUILayout.FloatField(new GUIContent("Hole pitch (units)",
                "Distance between two holes. VEX C-channel is 0.5 in = 0.0127 m, and this project's " +
                "world is 10x (1 unit = 0.1 m), so 0.127."), holePitch);
            stageStiffness = EditorGUILayout.FloatField(new GUIContent("Stage stiffness",
                "Position spring holding each bar at its commanded height."), stageStiffness);
            stageDamping = EditorGUILayout.FloatField(new GUIContent("Stage damping",
                "Raise it if a bar rings at the end of its travel."), stageDamping);
            stageForceLimit = EditorGUILayout.FloatField(new GUIContent("Stage force limit",
                "The lift's stall force. Gravity here is 10x, so a claw on a 3-stage lift sags on a " +
                "small limit — raise this if the lift droops, lower it if it shoves the field around."),
                stageForceLimit);
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Build Cascade Lift", GUILayout.Height(32)))
        {
            try
            {
                string report = CascadeSetup.Build(BuildOptions(), useUndo: true);
                EditorUtility.DisplayDialog(Title, report, "OK");
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog(Title, "Couldn't build the cascade.\n\n" + e.Message, "OK");
                Debug.LogException(e);
            }
        }

        EditorGUILayout.EndScrollView();
    }

    private CascadeSetup.Options BuildOptions() => new CascadeSetup.Options
    {
        displayName = displayName,
        bars = bars,
        ridesAlong = ridesAlong,
        attachToBarIndex = attachToBarIndex,
        oneAtATime = oneAtATime,
        topFirst = topFirst,
        overlapHoles = overlapHoles,
        holePitch = holePitch,
        reverseDirection = reverseDirection,
        raiseSeconds = raiseSeconds,
        stageStiffness = stageStiffness,
        stageDamping = stageDamping,
        stageForceLimit = stageForceLimit,
        autoAssignButtons = autoAssignButtons,
    };

    private void LoadRig(CascadeRig rig)
    {
        displayName = rig.displayName;
        bars = CloneBars(rig.bars);
        ridesAlong = new List<GameObject>(rig.ridesAlong ?? new List<GameObject>());
        attachToBarIndex = rig.attachToBarIndex;
        oneAtATime = rig.oneAtATime;
        topFirst = rig.topFirst;
        overlapHoles = rig.overlapHoles;
        holePitch = rig.holePitch > 1e-5f ? rig.holePitch : CascadeSetup.DefaultHolePitch;
        reverseDirection = rig.reverseDirection;
        raiseSeconds = rig.raiseSeconds;
        stageStiffness = rig.stageStiffness;
        stageDamping = rig.stageDamping;
        stageForceLimit = rig.stageForceLimit;
        autoAssignButtons = rig.autoAssignButtons;
    }

    // Deep copy, so editing the form doesn't quietly rewrite the built rig before Build is pressed.
    private static List<CascadeRig.Bar> CloneBars(List<CascadeRig.Bar> source)
    {
        var copy = new List<CascadeRig.Bar>();
        if (source == null) return copy;
        foreach (CascadeRig.Bar b in source)
        {
            if (b == null) continue;
            copy.Add(new CascadeRig.Bar
            {
                parts = new List<GameObject>(b.parts ?? new List<GameObject>()),
                channels = new List<GameObject>(b.channels ?? new List<GameObject>()),
                travelOverride = b.travelOverride,
                builtLink = b.builtLink,
                builtTravel = b.builtTravel,
            });
        }
        return copy;
    }

    // --- Sub-editors ------------------------------------------------------------------------------

    private void DrawBars(RobotMechanisms registry)
    {
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Bars (bottom first)", EditorStyles.boldLabel);

        int removeAt = -1;
        for (int i = 0; i < bars.Count; i++)
        {
            CascadeRig.Bar bar = bars[i];
            if (bar == null) bars[i] = bar = new CascadeRig.Bar();

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(i == 0
                        ? "Bar 1 — slides on the robot"
                        : $"Bar {i + 1} — slides on bar {i}", EditorStyles.boldLabel);
                    if (GUILayout.Button("X", GUILayout.Width(24))) removeAt = i;
                }

                DrawGoList("Parts on this bar",
                    "Everything bolted to this bar EXCEPT its channels — motors, sprockets, pulleys, " +
                    "string mounts, brackets. It all welds into one moving link.", bar.parts);
                DrawGoList("C-channel(s)",
                    "This bar's channel(s) — group both under one empty and drop it here. The build " +
                    "reads the slide direction and the maximum travel off the longest one.", bar.channels);

                DrawMeasuredTravel(bar, i, registry);
                bar.travelOverride = EditorGUILayout.FloatField(new GUIContent("Travel override",
                    "Force how far this bar slides, in world units (1 unit = 0.1 m). 0 = work it out " +
                    "from the channel."), bar.travelOverride);
            }
        }
        if (removeAt >= 0) bars.RemoveAt(removeAt);
        if (GUILayout.Button("Add bar", GUILayout.Width(120))) bars.Add(new CascadeRig.Bar());
    }

    // What the build WILL do with this bar, shown before committing: how long its channel measured,
    // roughly how many holes that is, and the travel that falls out after the overlap is taken off.
    private void DrawMeasuredTravel(CascadeRig.Bar bar, int index, RobotMechanisms registry)
    {
        Transform frame = registry != null ? registry.transform : null;
        bool measured = CascadeSetup.TryBarAxis(bar, frame, out _, out float length);
        float previous = index > 0 ? CascadeSetup.ChannelLength(bars[index - 1]) : 0f;
        float travel = CascadeSetup.ResolveTravel(bar, length, previous, overlapHoles, holePitch);

        if (!measured && bar.travelOverride <= 1e-4f)
        {
            EditorGUILayout.HelpBox("No C-channel to measure — add one, or set a travel override.",
                MessageType.Warning);
            return;
        }
        string holes = length > 1e-4f && holePitch > 1e-5f
            ? $" (~{Mathf.RoundToInt(length / holePitch)} holes)" : "";
        string source = bar.travelOverride > 1e-4f ? "override" : "measured";
        EditorGUILayout.LabelField(measured
            ? $"channel {length:F2} u{holes}  ->  travel {travel:F2} u ({source})"
            : $"travel {travel:F2} u ({source}) — direction defaults to straight up",
            EditorStyles.miniLabel);
    }

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

    // The cascade already on this robot, so it can be re-opened or deleted without hunting the
    // hierarchy — same in-place management as the claw, pneumatic and chain builders.
    private void DrawExistingRig(RobotMechanisms registry)
    {
        if (registry == null) return;
        CascadeRig rig = registry.GetComponentInChildren<CascadeRig>(true);
        if (rig == null) return;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Existing cascade", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField($"{rig.displayName}  ({rig.bars.Count} bars)");
            if (GUILayout.Button("Edit", GUILayout.Width(50))) { LoadRig(rig); GUIUtility.ExitGUI(); }
            if (GUILayout.Button("Delete", GUILayout.Width(60)))
            {
                try
                {
                    string report = CascadeSetup.Strip(registry, rig, useUndo: true);
                    EditorUtility.DisplayDialog(Title, report, "OK");
                }
                catch (Exception e)
                {
                    EditorUtility.DisplayDialog(Title, "Couldn't delete the cascade.\n\n" + e.Message, "OK");
                    Debug.LogException(e);
                }
                GUIUtility.ExitGUI();   // the rig just went away under this layout
            }
        }
    }

    // --- Scene preview ----------------------------------------------------------------------------

    private void OnSceneGUI(SceneView view)
    {
        if (!showPreview) return;
        RobotMechanisms registry = ResolveRegistry();
        Transform frame = registry != null ? registry.transform : null;

        for (int i = 0; i < bars.Count; i++)
        {
            CascadeRig.Bar bar = bars[i];
            if (bar == null) continue;
            if (!CascadeSetup.TryBarCenter(bar, out Vector3 center)) continue;

            bool measured = CascadeSetup.TryBarAxis(bar, frame, out Vector3 axis, out float length);
            if (!measured) axis = frame != null ? frame.up : Vector3.up;
            if (reverseDirection) axis = -axis;
            float previous = i > 0 ? CascadeSetup.ChannelLength(bars[i - 1]) : 0f;
            float travel = CascadeSetup.ResolveTravel(bar, length, previous, overlapHoles, holePitch);
            if (travel <= 1e-4f) continue;

            Vector3 tip = center + axis * travel;
            Handles.color = measured ? new Color(0.3f, 0.9f, 1f) : new Color(1f, 0.7f, 0.2f);
            Handles.DrawLine(center, tip, 3f);
            Handles.SphereHandleCap(0, center, Quaternion.identity,
                HandleUtility.GetHandleSize(center) * 0.08f, EventType.Repaint);
            Handles.ConeHandleCap(0, tip, Quaternion.LookRotation(axis),
                HandleUtility.GetHandleSize(tip) * 0.18f, EventType.Repaint);
            Handles.Label(tip, $"Bar {i + 1}: {travel:F2} u" + (measured ? "" : " (no channel — guessing up)"));
        }
    }

    // --- Small helpers ----------------------------------------------------------------------------

    private RobotMechanisms ResolveRegistry()
    {
        foreach (CascadeRig.Bar bar in bars)
        {
            if (bar == null) continue;
            RobotMechanisms r = FirstRegistry(bar.parts) ?? FirstRegistry(bar.channels);
            if (r != null) return r;
        }
        RobotMechanisms fromRiders = FirstRegistry(ridesAlong);
        if (fromRiders != null) return fromRiders;
        return Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponentInParent<RobotMechanisms>() : null;
    }

    private static RobotMechanisms FirstRegistry(List<GameObject> list)
    {
        if (list == null) return null;
        foreach (GameObject go in list)
        {
            if (go == null) continue;
            RobotMechanisms r = go.GetComponentInParent<RobotMechanisms>();
            if (r != null) return r;
        }
        return null;
    }

    private static int CountNonNull(List<GameObject> list)
    {
        if (list == null) return 0;
        int n = 0;
        foreach (GameObject go in list) if (go != null) n++;
        return n;
    }
}

// Headless-runnable core (window/core split like Dr4bLiftSetup and ClawSetup), so the validation
// harness can build and tear down a cascade in batch mode.
public static class CascadeSetup
{
    public const string DriverName = "CascadeMotor";
    public const string StagePrefix = "CascadeStage";
    // VEX C-channel holes are 0.5 in apart = 0.0127 m; this project's world is 10x (1 unit = 0.1 m).
    public const float DefaultHolePitch = 0.127f;

    private const string UndoName = "Build Cascade Lift";
    // The hidden driver's internal travel. Only the 0->1 progress matters — per-bar travel is set
    // directly in world units — so this number never changes how the lift looks.
    private const float DriverSweepDeg = 60f;
    private const float WorldScaleFactor = 10f;   // 1 scaled unit = 0.1 m, as everywhere else here

    public struct Options
    {
        public string displayName;
        public List<CascadeRig.Bar> bars;
        public List<GameObject> ridesAlong;
        public int attachToBarIndex;        // -1 = the top bar
        public bool oneAtATime, topFirst;
        public float overlapHoles, holePitch;
        public bool reverseDirection;
        public float raiseSeconds;
        public float stageStiffness, stageDamping, stageForceLimit;
        public bool autoAssignButtons;
    }

    public static string Build(Options o, bool useUndo)
    {
        RobotMechanisms registry = ResolveRegistry(o);
        if (registry == null)
            throw new InvalidOperationException(
                "No set-up robot found from the assigned parts (no RobotMechanisms). Assign the bars " +
                "and run Set Up Imported Robot first.");
        Transform chassis = registry.transform;

        // Everything is checked BEFORE anything moves — a half-reparented cascade is far worse than a
        // refused build.
        List<CascadeRig.Bar> bars = UsableBars(o.bars);
        Validate(o, bars);

        int group = 0;
        if (useUndo)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            group = Undo.GetCurrentGroup();
        }

        // Full strip-then-rebuild, the same way the DR4B builder cleans first: parts move between bars
        // between runs, and anything left in an old stage would keep riding a joint nobody lists any
        // more. Strip also drops the lift's button bindings, so snapshot them when auto-assign is off
        // ("kept" behaviour) and put them back.
        string driverId = UrdfPostProcessor.Slugify(DriverName);
        List<ButtonAssignment> keptBindings = o.autoAssignButtons
            ? null : SnapshotBindings(registry.robotId, driverId);
        CascadeRig previous = registry.GetComponentInChildren<CascadeRig>(true);
        if (previous != null) Strip(registry, previous, useUndo, refreshCatalog: false);
        if (keptBindings != null) RestoreBindings(registry.robotId, keptBindings);

        CascadeRig rig = MechanismBuildUtil.AddOrGet<CascadeRig>(registry.gameObject, useUndo);
        if (useUndo) Undo.RecordObject(rig, UndoName);
        rig.moved = new List<CascadeRig.Moved>();

        // 1) The hidden driver: one revolute joint, hold-to-run, whose angle is the lift's progress.
        GameObject driver = BuildDriver(chassis, o, useUndo, out ArticulationBody driverBody);

        // 2) The bars, bottom up — each one a real prismatic link nested inside the one below it.
        var stages = new List<CascadeLift.Stage>();
        Transform parentLink = chassis;
        float previousChannel = 0f;
        var warnings = new List<string>();

        for (int i = 0; i < bars.Count; i++)
        {
            CascadeRig.Bar bar = bars[i];
            bool measured = TryBarAxis(bar, chassis, out Vector3 axisWorld, out float channelLength);
            if (!measured)
            {
                axisWorld = chassis.up;
                warnings.Add($"bar {i + 1} has no C-channel to measure — it slides straight up the robot");
            }
            else if (Mathf.Abs(Vector3.Dot(axisWorld, chassis.up)) < 0.5f)
            {
                warnings.Add($"bar {i + 1}'s channel lies more across the robot than up it — check the " +
                             "arrow in the Scene view before you trust the direction");
            }
            if (o.reverseDirection) axisWorld = -axisWorld;

            float travel = ResolveTravel(bar, channelLength, previousChannel, o.overlapHoles, o.holePitch);

            // A fresh empty is the link, rather than one of the CAD groups: the bar's parts weld into
            // it, so the joint sits at the bar's real centre instead of wherever the modeller left a
            // group's origin, and Strip has one unambiguous object to remove.
            GameObject stage = new GameObject(StagePrefix + (i + 1));
            if (useUndo) Undo.RegisterCreatedObjectUndo(stage, UndoName);
            stage.transform.SetParent(parentLink, false);
            stage.transform.rotation = chassis.rotation;
            stage.transform.position = TryBarCenter(bar, out Vector3 center) ? center : parentLink.position;

            // Record where every part came from BEFORE it moves, so Delete can put the CAD back.
            List<GameObject> roots = BarRoots(bar);
            foreach (GameObject part in roots)
                rig.moved.Add(new CascadeRig.Moved { part = part, originalParent = part.transform.parent });

            Vector3 axisLocal = stage.transform.InverseTransformDirection(axisWorld).normalized;
            ArticulationBody body = AddMechanismJoint.ConfigureJointLink(stage,
                AddMechanismJoint.JointType.Prismatic, axisLocal, Vector3.zero, 0f, travel,
                new AddMechanismJoint.Options { alsoMove = roots.ToArray() }, registry, useUndo);

            if (useUndo) Undo.RecordObject(body, UndoName);
            // ConfigureJointLink sizes the mass from the LINK's name, and a "CascadeStage2" empty
            // matches no material — so re-do it per part, where the names (C-Chan, Motor, ...) are.
            float mass = BarMass(roots, chassis);
            body.mass = Mathf.Max(mass, MechanismBuildUtil.MinLiftMass);
            body.ResetCenterOfMass();
            body.ResetInertiaTensor();
            // Telescoping bars overlap by construction — without this they shove each other, and the
            // robot, around.
            MechanismBuildUtil.AddOrGet<IgnoreRobotSelfCollision>(stage, useUndo);

            stages.Add(new CascadeLift.Stage { body = body, travel = travel, label = $"Bar {i + 1}" });
            bar.builtLink = stage;
            bar.builtTravel = travel;
            parentLink = stage.transform;
            previousChannel = channelLength;
        }

        // 3) Whatever rides the lift — the claw arm and its claw — moves onto the chosen bar. Their
        //    own joints keep working; they just hang off a link that moves now.
        Transform attach = AttachPoint(stages, o.attachToBarIndex, chassis);
        int riders = WireRiders(o.ridesAlong, attach, rig, useUndo);

        // 4) The controller. It owns every stage's drive; the stages carry no actuator and are not
        //    registered, so ButtonRouter can never fight it for them.
        CascadeLift lift = MechanismBuildUtil.AddOrGet<CascadeLift>(registry.gameObject, useUndo);
        if (useUndo) Undo.RecordObject(lift, UndoName);
        lift.driver = driverBody;
        lift.chassis = chassis;
        lift.sweepDeg = DriverSweepDeg;
        lift.raiseSeconds = o.raiseSeconds;
        lift.oneAtATime = o.oneAtATime;
        lift.topFirst = o.topFirst;
        lift.stageStiffness = o.stageStiffness;
        lift.stageDamping = o.stageDamping;
        lift.stageForceLimit = o.stageForceLimit;
        lift.stages = stages;
        lift.BakeDrives();   // so edit-mode Physics.Simulate behaves like Play, which never ran Awake

        // 5) Buttons + the name the config screen shows.
        string buttonNote = "kept";
        if (o.autoAssignButtons)
        {
            MechanismBuildUtil.ClearMechanismBindings(registry.robotId, driverId);
            buttonNote = MechanismAutoDetect.AssignButtons(registry.robotId, driverId,
                AddMechanismJoint.JointType.Revolute);
        }
        RobotMechanisms.Mechanism mech = registry.Find(driverId);
        if (mech != null)
            mech.displayName = string.IsNullOrEmpty(o.displayName) ? "Cascade" : o.displayName;
        UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, registry.gameObject.name, registry);
        AssetDatabase.SaveAssets();   // flush the catalog so Configure Controller sees the lift

        // 6) The authoring record, so the window can re-open this build.
        rig.displayName = o.displayName;
        rig.bars = bars;
        rig.ridesAlong = o.ridesAlong ?? new List<GameObject>();
        rig.attachToBarIndex = o.attachToBarIndex;
        rig.oneAtATime = o.oneAtATime;
        rig.topFirst = o.topFirst;
        rig.overlapHoles = o.overlapHoles;
        rig.holePitch = o.holePitch;
        rig.reverseDirection = o.reverseDirection;
        rig.raiseSeconds = o.raiseSeconds;
        rig.stageStiffness = o.stageStiffness;
        rig.stageDamping = o.stageDamping;
        rig.stageForceLimit = o.stageForceLimit;
        rig.autoAssignButtons = o.autoAssignButtons;
        rig.builtDriver = driver;

        if (useUndo) Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(registry);
        EditorUtility.SetDirty(rig);
        EditorUtility.SetDirty(lift);
        if (registry.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(registry.gameObject.scene);

        float total = 0f;
        foreach (CascadeLift.Stage s in stages) total += s.travel;
        return
            $"Built a {stages.Count}-bar cascade on '{registry.name}'.\n\n" +
            $"• Bars: {BarReport(stages)}  (total reach {total:F2} u)\n" +
            $"• Motion: {(o.oneAtATime ? (o.topFirst ? "one at a time, top bar first" : "one at a time, bottom bar first") : "all at the same time")}, " +
            $"{o.raiseSeconds}s to full — editable live on the CascadeLift component.\n" +
            $"• Carried onto {(o.attachToBarIndex < 0 ? "the top bar" : $"bar {o.attachToBarIndex + 1}")}: {riders} part(s).\n" +
            $"• Buttons: {buttonNote}.\n" +
            (warnings.Count > 0 ? "\nCHECK: " + string.Join("; ", warnings) + ".\n" : "") +
            "\nIMPORTANT: the field spawns the robot PREFAB at Play, not this scene object. APPLY THESE " +
            "CHANGES TO THE PREFAB (Prefab Mode, or Overrides > Apply All) or they won't spawn.\n\n" +
            "Play, then hold the lift button. Tuning:\n" +
            "• A bar slides the wrong way -> 'Slide the other way' (or check its channel bucket).\n" +
            "• A bar runs out of channel -> raise Overlap (holes), or set a travel override.\n" +
            "• The lift sags under the claw -> raise the stage force limit (Advanced).";
    }

    // Take the cascade back off the robot: every part returns to the group it came from, the stage
    // links and the hidden driver go away, and the bars are plain welded geometry again.
    public static string Strip(RobotMechanisms registry, CascadeRig rig, bool useUndo,
        bool refreshCatalog = true)
    {
        if (registry == null || rig == null)
            throw new InvalidOperationException("No robot/cascade to delete.");

        int group = 0;
        if (useUndo)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Delete Cascade Lift");
            group = Undo.GetCurrentGroup();
        }

        string label = rig.displayName;
        int restored = 0;

        // 1) Everything the build moved goes home FIRST, while the stage links still exist — pulling a
        //    part out of a link is safe, but destroying a link that still holds one would drag the
        //    part (and any joint under it) somewhere unintended.
        if (rig.moved != null)
            foreach (CascadeRig.Moved m in rig.moved)
            {
                if (m == null || m.part == null || m.originalParent == null) continue;
                MechanismBuildUtil.EnsureChildOf(m.part.transform, m.originalParent, useUndo);
                restored++;
                // A carried ARM keeps its own joint; its parent link just changed back to the chassis,
                // and a stale parent anchor snaps it on the first Simulate().
                foreach (ArticulationBody body in TopBodiesIn(m.part))
                {
                    if (useUndo) Undo.RecordObject(body, "Delete Cascade Lift");
                    MechanismBuildUtil.RederiveParentAnchors(body);
                }
            }

        // 2) The stage links, deepest first — destroying an outer one first would take the inner ones
        //    with it and leave the loop deleting objects that are already gone.
        int links = 0;
        if (rig.bars != null)
            for (int i = rig.bars.Count - 1; i >= 0; i--)
            {
                CascadeRig.Bar bar = rig.bars[i];
                if (bar?.builtLink == null) continue;
                string id = UrdfPostProcessor.Slugify(bar.builtLink.name);
                UrdfPostProcessor.RemoveMechanism(registry, id, useUndo);
                MechanismBuildUtil.ClearMechanismBindings(registry.robotId, id);
                MechanismBuildUtil.DestroyGo(bar.builtLink.transform, useUndo);
                bar.builtLink = null;
                links++;
            }
        // Belt and braces: a stage empty whose bar record was lost (a hand-edited rig) still goes.
        foreach (Transform t in registry.GetComponentsInChildren<Transform>(true))
            if (t != null && t.name.StartsWith(StagePrefix, StringComparison.Ordinal))
            {
                MechanismBuildUtil.DestroyGo(t, useUndo);
                links++;
            }

        // 3) The hidden driver, its mechanism and its buttons.
        string driverId = UrdfPostProcessor.Slugify(DriverName);
        UrdfPostProcessor.RemoveMechanism(registry, driverId, useUndo);
        MechanismBuildUtil.ClearMechanismBindings(registry.robotId, driverId);
        Transform driver = FindChild(registry.transform, DriverName);
        if (driver != null) MechanismBuildUtil.DestroyGo(driver, useUndo);

        // 4) The controller and the record itself.
        foreach (CascadeLift lift in registry.GetComponentsInChildren<CascadeLift>(true))
            if (lift != null)
            {
                if (useUndo) Undo.DestroyObjectImmediate(lift);
                else UnityEngine.Object.DestroyImmediate(lift);
            }
        if (useUndo) Undo.DestroyObjectImmediate(rig);
        else UnityEngine.Object.DestroyImmediate(rig);

        if (refreshCatalog)
        {
            UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, registry.gameObject.name, registry);
            AssetDatabase.SaveAssets();   // so the lift disappears from Configure Controller
        }
        if (useUndo) Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(registry);
        if (registry.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(registry.gameObject.scene);

        return $"Deleted the cascade '{label}': {links} stage link(s) removed, {restored} part(s) put " +
               "back where they came from, buttons released.\n\nThe bars are plain welded meshes again. " +
               "Remember to apply the change to the PREFAB.";
    }

    // --- Measuring a bar ---------------------------------------------------------------------------

    // The bar's slide direction (pointed UP the robot) and the length of the channel it was read from.
    // The channel is what carries this information: a bar's other parts say nothing about which way it
    // travels, and on a slightly angled cascade each bar leans its own way. `frame` supplies the
    // robot's up; null falls back to world up (the Scene preview can run before a robot is resolved).
    public static bool TryBarAxis(CascadeRig.Bar bar, Transform frame, out Vector3 axisWorld, out float length)
    {
        axisWorld = frame != null ? frame.up : Vector3.up;
        length = 0f;
        if (bar?.channels == null) return false;

        Vector3 up = frame != null ? frame.up : Vector3.up;
        bool found = false;
        foreach (GameObject channel in bar.channels)
        {
            if (channel == null) continue;
            if (!ChainBuilder.TryAxleWorldAxis(channel, out Vector3 axis, out _, out float len)) continue;
            if (len <= length) continue;
            // The long axis has no inherent sign (it's whichever way the mesh was drawn), so aim it up
            // the robot — a cascade extends upward, and the reverse toggle covers the exceptions.
            axisWorld = Vector3.Dot(axis, up) < 0f ? -axis : axis;
            length = len;
            found = true;
        }
        return found;
    }

    // Just the measured channel length (0 when there's nothing to measure) — the bar BELOW is what
    // limits how far this one can slide, so the builder needs to ask about a neighbour.
    public static float ChannelLength(CascadeRig.Bar bar)
        => TryBarAxis(bar, null, out _, out float length) ? length : 0f;

    // How far a bar slides: its channel, minus the overlap that has to stay engaged, and never more
    // than the channel BELOW it can offer (a bar can only run out along the one it sits in). An
    // explicit override wins outright.
    public static float ResolveTravel(CascadeRig.Bar bar, float ownLength, float belowLength,
        float overlapHoles, float holePitch)
    {
        if (bar != null && bar.travelOverride > 1e-4f) return bar.travelOverride;
        float limit = ownLength;
        if (belowLength > 1e-4f) limit = Mathf.Min(limit, belowLength);
        return Mathf.Max(0f, limit - Mathf.Max(0f, overlapHoles) * Mathf.Max(0f, holePitch));
    }

    // The visual centre of everything on the bar — where the stage link is seated.
    public static bool TryBarCenter(CascadeRig.Bar bar, out Vector3 center)
    {
        center = Vector3.zero;
        if (bar == null) return false;
        Bounds all = default;
        bool has = false;
        foreach (GameObject go in BarRoots(bar))
        {
            if (!MechanismBuildUtil.TryBounds(go, out Bounds b)) continue;
            if (!has) { all = b; has = true; } else all.Encapsulate(b);
        }
        if (!has)
        {
            // No renderers at all (empties): fall back to the first assigned transform.
            foreach (GameObject go in BarRoots(bar)) { center = go.transform.position; return true; }
            return false;
        }
        center = all.center;
        return true;
    }

    // --- Build helpers -----------------------------------------------------------------------------

    // The hidden revolute joint the buttons drive. Same shape as the DR4B's hidden hub: hold-to-run
    // over a fixed internal sweep, with the speed derived from the raise time, and enough mass that a
    // near-massless helper link doesn't upset the solver.
    private static GameObject BuildDriver(Transform chassis, Options o, bool useUndo,
        out ArticulationBody body)
    {
        Transform existing = FindChild(chassis, DriverName);
        GameObject hub;
        if (existing != null) hub = existing.gameObject;
        else
        {
            hub = new GameObject(DriverName);
            if (useUndo) Undo.RegisterCreatedObjectUndo(hub, UndoName);
            hub.transform.SetParent(chassis, false);
        }

        Vector3 axis = MechanismBuildUtil.TryDrivetrainLateralLocal(hub, out Vector3 lateral)
            ? lateral : Vector3.right;
        AddMechanismJoint.Apply(hub, AddMechanismJoint.JointType.Revolute, axis, Vector3.zero,
            0f, DriverSweepDeg,
            new AddMechanismJoint.Options { actuation = AddMechanismJoint.Actuation.HoldToRun }, useUndo);

        body = hub.GetComponent<ArticulationBody>();
        if (body != null)
        {
            if (useUndo) Undo.RecordObject(body, UndoName);
            if (body.mass < MechanismBuildUtil.MinLiftMass) body.mass = MechanismBuildUtil.MinLiftMass;
        }
        MotorActuator motor = hub.GetComponent<MotorActuator>();
        if (motor != null)
        {
            // The same speed CascadeLift re-derives at play time, baked now so edit-mode stepping runs
            // at the authored rate too. SetMaxRpm rather than the field, because it also re-caps the
            // joint's velocity — set the field alone and a fast raise time is silently throttled.
            if (useUndo) Undo.RecordObject(motor, UndoName);
            motor.SetMaxRpm(DriverSweepDeg / (6f * Mathf.Max(0.05f, o.raiseSeconds)));
        }
        return hub;
    }

    // Move the carried parts onto the lift. A rider that is itself a joint (the claw arm) keeps its
    // own body — only its PARENT changed, so its parent-side anchor has to be re-derived or PhysX
    // snaps it back to where the chassis used to hold it. A plain part just welds into the bar.
    private static int WireRiders(List<GameObject> riders, Transform attach, CascadeRig rig, bool useUndo)
    {
        if (riders == null || attach == null) return 0;
        int moved = 0;
        bool welded = false;
        foreach (GameObject rider in riders)
        {
            if (rider == null || rider.transform == attach || attach.IsChildOf(rider.transform)) continue;
            rig.moved.Add(new CascadeRig.Moved { part = rider, originalParent = rider.transform.parent });
            MechanismBuildUtil.EnsureChildOf(rider.transform, attach, useUndo);

            var bodies = new List<ArticulationBody>(TopBodiesIn(rider));
            foreach (ArticulationBody body in bodies)
            {
                if (useUndo) Undo.RecordObject(body, UndoName);
                MechanismBuildUtil.RederiveParentAnchors(body);
            }
            if (bodies.Count == 0) welded = true;
            moved++;
        }

        ArticulationBody stageBody = attach.GetComponent<ArticulationBody>();
        if (welded && stageBody != null)
        {
            // Plain geometry that joined the bar is part of its body now.
            if (useUndo) Undo.RecordObject(stageBody, UndoName);
            stageBody.ResetCenterOfMass();
            stageBody.ResetInertiaTensor();
        }
        return moved;
    }

    // The ArticulationBodies at the TOP of a moved subtree — the ones whose parent link changed. A
    // deeper link (a claw jaw under its flip link) moved together with its own parent, so its anchors
    // are still correct and re-deriving them would be wrong.
    private static IEnumerable<ArticulationBody> TopBodiesIn(GameObject root)
    {
        if (root == null) yield break;
        Transform stop = root.transform.parent;
        foreach (ArticulationBody body in root.GetComponentsInChildren<ArticulationBody>(true))
        {
            bool nested = false;
            for (Transform p = body.transform.parent; p != null && p != stop && !nested; p = p.parent)
                nested = p.GetComponent<ArticulationBody>() != null;
            if (!nested) yield return body;
        }
    }

    private static Transform AttachPoint(List<CascadeLift.Stage> stages, int index, Transform fallback)
    {
        if (stages.Count == 0) return fallback;
        int i = index < 0 ? stages.Count - 1 : Mathf.Clamp(index, 0, stages.Count - 1);
        return stages[i].body != null ? stages[i].body.transform : fallback;
    }

    // Mass of one bar, summed part by part with each part's OWN material density — a bar is aluminium
    // channel plus motors and plastic, and one blanket density over the lot is how a lift ends up
    // weighing what a bag of screws does.
    private static float BarMass(List<GameObject> roots, Transform robotRoot)
    {
        float kg = 0f;
        foreach (GameObject go in roots)
        {
            float density = RobotPartClassifier.TryGetDensity(go.name, out float d)
                ? d : RobotPartClassifier.DefaultDensity;
            kg += RobotMassFromGeometry.MassForLinkNode(go, robotRoot, WorldScaleFactor, density);
        }
        return kg;
    }

    private static List<GameObject> BarRoots(CascadeRig.Bar bar)
    {
        var roots = new List<GameObject>();
        if (bar == null) return roots;
        foreach (List<GameObject> list in new[] { bar.parts, bar.channels })
        {
            if (list == null) continue;
            foreach (GameObject go in list)
                if (go != null && !roots.Contains(go)) roots.Add(go);
        }
        return roots;
    }

    private static List<CascadeRig.Bar> UsableBars(List<CascadeRig.Bar> bars)
    {
        var usable = new List<CascadeRig.Bar>();
        if (bars == null) return usable;
        foreach (CascadeRig.Bar bar in bars)
            if (bar != null && BarRoots(bar).Count > 0) usable.Add(bar);
        return usable;
    }

    private static string BarReport(List<CascadeLift.Stage> stages)
    {
        var parts = new List<string>();
        foreach (CascadeLift.Stage s in stages) parts.Add($"{s.travel:F2}");
        return parts.Count == 0 ? "none" : string.Join(" + ", parts) + " u";
    }

    // --- Validation ---------------------------------------------------------------------------------

    private static void Validate(Options o, List<CascadeRig.Bar> bars)
    {
        if (bars.Count == 0)
            throw new InvalidOperationException(
                "No bars assigned. Add a block per sliding bar and put its parts and its C-channel(s) " +
                "in — the channel bolted to the robot doesn't get listed.");

        // Every listed object, once, and nothing tangled up with anything else: a part inside another
        // bar's group would be pulled into one link and then yanked out into the next, which is a
        // hierarchy you can't reason about afterward.
        var seen = new List<GameObject>();
        var owner = new List<string>();
        void Claim(GameObject go, string where)
        {
            if (go == null) return;
            for (int i = 0; i < seen.Count; i++)
            {
                if (seen[i] == go)
                    throw new InvalidOperationException(
                        $"'{go.name}' is listed twice ({owner[i]} and {where}). Each part can only " +
                        "ride one bar.");
                if (go.transform.IsChildOf(seen[i].transform) || seen[i].transform.IsChildOf(go.transform))
                    throw new InvalidOperationException(
                        $"'{go.name}' ({where}) sits inside '{seen[i].name}' ({owner[i]}) in the " +
                        "hierarchy, so they can't move independently. List the individual parts, not a " +
                        "group that already contains another bar's parts.");
            }
            seen.Add(go);
            owner.Add(where);
        }

        for (int i = 0; i < bars.Count; i++)
        {
            CascadeRig.Bar bar = bars[i];
            foreach (GameObject go in bar.parts ?? new List<GameObject>()) Claim(go, $"bar {i + 1}");
            foreach (GameObject go in bar.channels ?? new List<GameObject>()) Claim(go, $"bar {i + 1}'s channels");

            float travel = ResolveTravel(bar, ChannelLength(bar),
                i > 0 ? ChannelLength(bars[i - 1]) : 0f, o.overlapHoles, o.holePitch);
            if (travel <= 1e-4f)
                throw new InvalidOperationException(
                    $"Bar {i + 1} would slide {travel:F2} units — it has no C-channel to measure (or " +
                    "the overlap eats the whole channel). Drop its channel(s) in, or set a travel " +
                    "override.");
        }
        foreach (GameObject go in o.ridesAlong ?? new List<GameObject>()) Claim(go, "the carried parts");
    }

    // --- Small helpers ------------------------------------------------------------------------------

    private static RobotMechanisms ResolveRegistry(Options o)
    {
        if (o.bars != null)
            foreach (CascadeRig.Bar bar in o.bars)
            {
                if (bar == null) continue;
                foreach (GameObject go in BarRoots(bar))
                {
                    RobotMechanisms r = go.GetComponentInParent<RobotMechanisms>();
                    if (r != null) return r;
                }
            }
        if (o.ridesAlong != null)
            foreach (GameObject go in o.ridesAlong)
            {
                if (go == null) continue;
                RobotMechanisms r = go.GetComponentInParent<RobotMechanisms>();
                if (r != null) return r;
            }
        return null;
    }

    // The lift's button bindings, so a Build with auto-assign OFF carries them across the internal
    // Strip (which releases them). PlayerPrefs-backed, so ordering against the scene edits is moot.
    private static List<ButtonAssignment> SnapshotBindings(string robotId, string mechanismId)
    {
        ButtonMap map = ControllerMapSettings.Load(robotId);
        return map.assignments.FindAll(a => a != null && a.mechanismId == mechanismId);
    }

    private static void RestoreBindings(string robotId, List<ButtonAssignment> kept)
    {
        if (kept == null || kept.Count == 0) return;
        ButtonMap map = ControllerMapSettings.Load(robotId);
        foreach (ButtonAssignment a in kept)
            if (Enum.TryParse(a.button, out ControllerButton button))
                ControllerMapSettings.AddAssignment(map, button, a.mechanismId, a.mode);
        ControllerMapSettings.Save(robotId, map);
    }

    private static Transform FindChild(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }
}
