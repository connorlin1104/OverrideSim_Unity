using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Rigs a chain — a run of sprockets that turn together because a chain links them in real life.
// Chain isn't in CAD (it's impractical to model), so this is where you state which parts spin
// together and on what shaft.
//
// You give it a list of STATIONS. A station is one spinning assembly: the group holding everything
// that turns at that spot (sprocket, flex wheels, collars, spacers) plus the axle it turns on. The
// first station is powered by a motor; the rest are chained to it and match its speed. Every station
// spins the SAME direction, which is what a chain does.
//
// The axle is what makes this accurate: its longest dimension is the spin axis, taken as a real
// oriented direction rather than snapped to the nearest world X/Y/Z, so a diagonally-mounted shaft
// works. Leave it empty and the axis is guessed from the spinning group's own shape instead.
//
// Usage: Tools > RoboSim > Robot > Mechanisms > Build Chain, IN PREFAB MODE (this reparents parts
// into their station's link, which prefab overrides can't carry). Build the chain FIRST; if it's an
// intake, run Add Intake (Pull-Force) on the powered station afterwards to add the mouth and hold
// point. Existing chains are listed at the bottom and can be removed there.
public class BuildChainWindow : EditorWindow
{
    private const string Title = "Build Chain";

    [SerializeField] private List<ChainBuilder.Station> stations = new List<ChainBuilder.Station>();
    [SerializeField] private string mechanismName = string.Empty;
    [SerializeField] private bool reverseDirection;
    [SerializeField] private bool autoAssignButton = true;
    [SerializeField] private bool showExtras;
    [SerializeField] private Vector2 scroll;

    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Build Chain", false, 19)]
    private static void ShowWindow()
    {
        BuildChainWindow window = GetWindow<BuildChainWindow>(Title);
        window.minSize = new Vector2(500f, 480f);
        window.Show();
    }

    private void OnEnable()
    {
        if (stations.Count == 0)
        {
            stations.Add(new ChainBuilder.Station { spins = Selection.activeGameObject });
            stations.Add(new ChainBuilder.Station());
        }
        SceneView.duringSceneGui += OnSceneGui;
    }

    private void OnDisable() => SceneView.duringSceneGui -= OnSceneGui;

    // Draws each station's derived spin axis through its axle, so a wrong axle pick is visible in
    // the Scene view before anything is built.
    private void OnSceneGui(SceneView view)
    {
        if (!ChainBuilder.TryResolveAxes(stations, out List<ChainBuilder.Resolved> resolved, out _)) return;
        for (int i = 0; i < resolved.Count; i++)
        {
            ChainBuilder.Resolved r = resolved[i];
            float len = Mathf.Max(HandleUtility.GetHandleSize(r.worldCenter) * 1.5f, 1f);
            Handles.color = i == 0 ? Color.green : Color.cyan;
            Handles.DrawLine(r.worldCenter - r.worldAxis * len, r.worldCenter + r.worldAxis * len, 3f);
            Handles.Label(r.worldCenter + r.worldAxis * len, i == 0 ? "powered" : $"chained {i}");
        }
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.HelpBox(
            "Chain isn't in CAD, so state here which parts spin together.\n" +
            "• Spins — the group holding everything that turns at one spot (sprocket, flex wheels, " +
            "collars). Everything under it becomes one spinning link.\n" +
            "• Axle — the shaft it turns on. Its long direction is the spin axis. Optional: left " +
            "empty, the axis is guessed from the group's shape.\n" +
            "Station 1 is driven by the motor; the rest are chained to it and spin the same way.",
            MessageType.Info);

        EditorGUILayout.HelpBox(
            "Run this in PREFAB MODE. It reparents parts into their station's link, and Unity can't " +
            "carry a reparent as a prefab override — edits made on a scene instance won't reach the " +
            "prefab the field actually spawns.",
            PrefabStageUtility.GetCurrentPrefabStage() != null ? MessageType.None : MessageType.Warning);

        mechanismName = EditorGUILayout.TextField(new GUIContent("Mechanism name",
            "What this shows as in Configure Controller. Also RENAMES station 1's object, because " +
            "the mechanism's id comes from that name and CAD parts are full of duplicate 'Body1' " +
            "names that would collide. Leave empty to keep the current name."), mechanismName);
        reverseDirection = EditorGUILayout.Toggle(new GUIContent("Reverse direction",
            "Flip which way the chain runs for a 'forward' press. Applies to the whole chain — the " +
            "followers copy the powered station's measured speed, sign included."), reverseDirection);
        autoAssignButton = EditorGUILayout.Toggle(new GUIContent("Assign buttons",
            "Claim free buttons for this mechanism, following its control style (motors default to " +
            "a hold forward/reverse pair)."), autoAssignButton);

        EditorGUILayout.Space();
        DrawStations();

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Add Station", GUILayout.Width(120)))
                stations.Add(new ChainBuilder.Station());
            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField($"{Selection.gameObjects.Length} selected", EditorStyles.miniLabel,
                GUILayout.Width(80));
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Build Chain", GUILayout.Height(30))) BuildNow();

        DrawExistingChains();
        EditorGUILayout.EndScrollView();
    }

    private void DrawStations()
    {
        ChainBuilder.TryResolveAxes(stations, out List<ChainBuilder.Resolved> resolved, out string axisProblem);

        int filled = 0;
        for (int i = 0; i < stations.Count; i++)
        {
            ChainBuilder.Station s = stations[i];
            if (s == null) { stations[i] = s = new ChainBuilder.Station(); }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(i == 0 ? $"{i + 1} — Powered (the motor turns this)"
                        : $"{i + 1} — Chained to station 1", EditorStyles.miniBoldLabel);
                    if (GUILayout.Button(new GUIContent("Use Selection",
                        "Fill from the Hierarchy: the active object spins, and anything selected with " +
                        "'axle' or 'shaft' in its name becomes the axle."), GUILayout.Width(100)))
                    {
                        FillFromSelection(s);
                    }
                    using (new EditorGUI.DisabledScope(stations.Count <= 1))
                    {
                        if (GUILayout.Button("X", GUILayout.Width(24)))
                        {
                            stations.RemoveAt(i);
                            return; // list changed; redraw next pass
                        }
                    }
                }

                s.spins = (GameObject)EditorGUILayout.ObjectField("Spins", s.spins, typeof(GameObject), true);
                s.axle = (GameObject)EditorGUILayout.ObjectField("Axle", s.axle, typeof(GameObject), true);
                if (i > 0)
                    s.ratio = EditorGUILayout.FloatField(new GUIContent("Ratio",
                        "This station's speed relative to the powered one. 1 = same. Set it from the " +
                        "sprocket teeth (a 12T driven by a 24T runs at 2). Negative reverses it."), s.ratio);

                if (s.spins == null) continue;
                filled++;

                if (resolved != null && i < resolved.Count)
                {
                    ChainBuilder.Resolved r = resolved[i];
                    EditorGUILayout.LabelField(
                        $"Axis {r.worldAxis.x:0.00}, {r.worldAxis.y:0.00}, {r.worldAxis.z:0.00}" +
                        (r.fromAxle ? "  (from the axle)" : "  (guessed from shape — no axle set)"),
                        EditorStyles.miniLabel);
                    if (i > 0 && r.alignment < ChainBuilder.MinAlignment)
                        EditorGUILayout.HelpBox(
                            "This station's axis is nearly perpendicular to the powered one's. Chained " +
                            "sprockets turn on parallel shafts, so this is probably the wrong axle.",
                            MessageType.Warning);
                }

                if (ChainBuilder.LooksLikeMotorHousing(s.spins))
                    EditorGUILayout.HelpBox(
                        "This group looks like it contains a motor. The motor housing is bolted to the " +
                        "frame and must NOT spin — only the axle coming out of it does. Point 'Spins' at " +
                        "the axle group instead.", MessageType.Warning);

                showExtras = EditorGUILayout.Foldout(showExtras, "Also spins with it (loose parts)");
                if (showExtras) DrawExtras(s);
            }
        }

        if (filled < 2)
            EditorGUILayout.HelpBox("A chain needs at least two stations — the powered one and " +
                "something chained to it.", MessageType.Warning);
        if (!string.IsNullOrEmpty(axisProblem))
            EditorGUILayout.HelpBox(axisProblem, MessageType.Error);
    }

    private static void DrawExtras(ChainBuilder.Station s)
    {
        if (s.alsoSpins == null) s.alsoSpins = new List<GameObject>();
        EditorGUI.indentLevel++;
        for (int j = 0; j < s.alsoSpins.Count; j++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                s.alsoSpins[j] = (GameObject)EditorGUILayout.ObjectField(s.alsoSpins[j], typeof(GameObject), true);
                if (GUILayout.Button("X", GUILayout.Width(24))) { s.alsoSpins.RemoveAt(j); j--; }
            }
        }
        if (GUILayout.Button("Add Part", GUILayout.Width(100))) s.alsoSpins.Add(null);
        EditorGUI.indentLevel--;
    }

    // Active object spins; a selected object whose name reads like a shaft becomes the axle, and
    // anything else selected joins the spinning group.
    private static void FillFromSelection(ChainBuilder.Station s)
    {
        GameObject[] sel = Selection.gameObjects;
        if (sel == null || sel.Length == 0) return;

        GameObject active = Selection.activeGameObject != null ? Selection.activeGameObject : sel[0];
        s.spins = active;
        s.axle = null;
        if (s.alsoSpins == null) s.alsoSpins = new List<GameObject>();
        s.alsoSpins.Clear();

        foreach (GameObject go in sel)
        {
            if (go == null || go == active) continue;
            if (s.axle == null && ChainBuilder.LooksLikeAxle(go)) s.axle = go;
            else s.alsoSpins.Add(go);
        }
    }

    private void BuildNow()
    {
        try
        {
            string report = ChainBuilder.Apply(stations, new ChainBuilder.Options
            {
                mechanismName = mechanismName,
                reverseDirection = reverseDirection,
                autoAssignButton = autoAssignButton,
            }, useUndo: true);
            EditorUtility.DisplayDialog(Title, report, "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog(Title, e.Message, "OK");
            Debug.LogException(e);
        }
    }

    // Every chain on the robot, so what's already coupled can be reviewed and undone.
    private void DrawExistingChains()
    {
        EditorGUILayout.Space();
        GameObject root = null;
        foreach (ChainBuilder.Station s in stations)
        {
            if (s == null || s.spins == null) continue;
            RobotMechanisms reg = s.spins.GetComponentInParent<RobotMechanisms>();
            if (reg != null) { root = reg.gameObject; break; }
        }

        JointCoupler[] couplers = root != null
            ? root.GetComponentsInChildren<JointCoupler>(true)
            : Array.Empty<JointCoupler>();
        EditorGUILayout.LabelField($"Existing Chains ({couplers.Length})", EditorStyles.boldLabel);
        if (couplers.Length == 0)
        {
            EditorGUILayout.LabelField("None on this robot yet.", EditorStyles.miniLabel);
            return;
        }
        foreach (JointCoupler c in couplers)
        {
            if (c == null) continue;
            using (new EditorGUILayout.HorizontalScope())
            {
                string driverName = c.driver != null ? c.driver.name : "(none)";
                string followerName = c.follower != null ? c.follower.name : c.name;
                EditorGUILayout.LabelField($"{driverName} → {followerName}  (x{c.ratio:0.##})");
                if (GUILayout.Button("Remove", GUILayout.Width(70)))
                {
                    GameObject host = c.gameObject;
                    Undo.DestroyObjectImmediate(c); // follower stays a joint, just no longer chained
                    if (host != null && host.scene.IsValid()) EditorSceneManager.MarkSceneDirty(host.scene);
                    return; // list changed; stop iterating the now-stale array
                }
            }
        }
    }
}

// The chain-authoring core, split out so a headless caller can drive it without the window.
public static class ChainBuilder
{
    private const string UndoName = "Build Chain";

    // Below this |dot| against the powered station's axis, a station's shaft is treated as
    // non-parallel — chained sprockets run on parallel shafts, so it's flagged as a likely mistake.
    public const float MinAlignment = 0.3f;

    // Above this |dot| against the drivetrain's lateral axis, a chain is treated as running on
    // lateral shafts and is aligned to the robot rather than to its own powered station. Below it
    // (a vertical or fore-aft chain) there's no robot-wide reference to agree on, so the powered
    // station stands in.
    private const float LateralAlignment = 0.5f;

    [Serializable]
    public class Station
    {
        public GameObject spins;              // the group whose whole subtree rotates
        public GameObject axle;               // optional: the shaft it turns on, defines the axis
        public List<GameObject> alsoSpins = new List<GameObject>(); // loose parts folded into the link
        public float ratio = 1f;              // this station : powered station (followers only)
    }

    public struct Options
    {
        public string mechanismName;   // renames the powered station; empty keeps its name
        public bool reverseDirection;
        public bool autoAssignButton;
    }

    // A station's spin axis, in world space for display/sign-matching and in the link's own frame
    // for the joint core (which takes link-local axis and anchor).
    public struct Resolved
    {
        public Vector3 worldAxis;
        public Vector3 worldCenter;
        public Vector3 linkAxis;    // worldAxis in the spinning group's local frame
        public Vector3 linkAnchor;  // worldCenter in the spinning group's local frame
        public bool fromAxle;       // false = guessed from the spinning group's own shape
        public float alignment;     // |dot| against the powered station's axis
    }

    // Builds the chain. Returns a report for the caller to show. Throws on any precondition failure,
    // having changed nothing (everything is validated up front). useUndo=false for headless callers.
    public static string Apply(IList<Station> stations, Options o, bool useUndo)
    {
        // --- Validate everything BEFORE mutating anything -------------------------------------
        var list = new List<Station>();
        foreach (Station s in stations)
            if (s != null && s.spins != null) list.Add(s);
        if (list.Count < 2)
            throw new InvalidOperationException(
                "A chain needs at least two stations: the powered one and something chained to it.");

        RobotMechanisms registry = list[0].spins.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
            throw new InvalidOperationException(
                $"'{list[0].spins.name}' is not under a set-up robot (no RobotMechanisms). Run " +
                "Set Up Imported Robot first.");
        GameObject root = registry.gameObject;

        foreach (Station s in list)
        {
            if (s.spins.GetComponentInParent<RobotMechanisms>() != registry)
                throw new InvalidOperationException($"'{s.spins.name}' is not part of the same robot.");
            ArticulationBody existingBody = s.spins.GetComponent<ArticulationBody>();
            if (existingBody != null && existingBody.jointType == ArticulationJointType.PrismaticJoint)
                throw new InvalidOperationException(
                    $"'{s.spins.name}' is already a piston (prismatic joint). A chain station has to " +
                    "spin — re-author it, or pick the part that actually turns.");
        }
        ValidateDisjoint(list);
        ValidateSharedParent(list);

        if (!TryResolveAxes(list, out List<Resolved> resolved, out string axisProblem))
            throw new InvalidOperationException(axisProblem);

        // The powered station's name becomes the mechanism id. CAD exports are full of duplicate
        // names ("Body1" x1260 on a real robot), and a duplicate id would silently replace another
        // mechanism's registration — so the rename is how the user gets a unique, meaningful id.
        Station driver = list[0];
        string driverName = string.IsNullOrEmpty(o.mechanismName) ? driver.spins.name : o.mechanismName.Trim();
        string id = UrdfPostProcessor.Slugify(driverName);
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException("The mechanism name has no usable characters in it.");
        RobotMechanisms.Mechanism clash = registry.Find(id);
        GameObject clashHolder = HolderOf(clash);
        if (clashHolder != null && clashHolder != driver.spins)
            throw new InvalidOperationException(
                $"Another mechanism already uses the id '{id}' (on '{clashHolder.name}'). Give this " +
                "chain a different Mechanism name so the ids stay unique.");

        // --- Build ------------------------------------------------------------------------------
        int group = 0;
        if (useUndo)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            group = Undo.GetCurrentGroup();
        }

        // A station is EITHER a joystick-driven drivetrain wheel OR part of this chain, never both.
        // This must happen before any station is configured: the joint core refuses to touch a body
        // that's still wired to the joysticks.
        int stripped = StripFromDrivetrain(root, list, useUndo);

        if (driver.spins.name != driverName)
        {
            if (useUndo) Undo.RecordObject(driver.spins, UndoName);
            driver.spins.name = driverName;
        }

        // Powered station: the shared joint core splits/retypes the link, folds the axle in, wires a
        // MotorActuator and registers the mechanism. Continuous so it free-spins both ways.
        //
        // Actuation stays Auto (a velocity motor). One-button "toggle" control is a router-level
        // latch on top of this same motor — NOT Actuation.Toggle, which would swap in a pneumatic
        // that snaps between two angles.
        AddMechanismJoint.Apply(driver.spins, AddMechanismJoint.JointType.Continuous,
            resolved[0].linkAxis, resolved[0].linkAnchor, 0f, 0f,
            new AddMechanismJoint.Options
            {
                alsoMove = MergeList(driver),
                reverseDirection = o.reverseDirection,
                actuation = AddMechanismJoint.Actuation.Auto,
            },
            useUndo);
        MechanismBuildUtil.AddOrGet<IgnoreRobotSelfCollision>(driver.spins, useUndo);

        ArticulationBody driverBody = driver.spins.GetComponent<ArticulationBody>();
        if (driverBody == null)
            throw new InvalidOperationException(
                $"'{driver.spins.name}' did not end up as a joint — nothing to chain to.");

        // Chained stations: same joint core (so a station that was ALREADY rigged gets its axis
        // corrected rather than keeping a bad one), then a JointCoupler that copies the powered
        // station's speed.
        int chained = 0;
        for (int i = 1; i < list.Count; i++)
        {
            Station s = list[i];
            ArticulationBody body = AddMechanismJoint.ConfigureJointLink(s.spins,
                AddMechanismJoint.JointType.Continuous, resolved[i].linkAxis, resolved[i].linkAnchor,
                0f, 0f, new AddMechanismJoint.Options { alsoMove = MergeList(s) }, registry, useUndo);

            // A chained station is passive, so it must not also be a button mechanism or the router
            // and the coupler fight over its drive target. Sweep by WHERE the actuator lives, not by
            // slugged name: duplicate CAD names mean a name-keyed removal could destroy a different
            // mechanism's motor — including the powered station's, just created above.
            foreach (RobotMechanisms.Mechanism stale in registry.mechanisms.ToArray())
            {
                if (stale == null || HolderOf(stale) != s.spins) continue;
                UrdfPostProcessor.RemoveMechanism(registry, stale.id, useUndo);
                MechanismBuildUtil.ClearMechanismBindings(registry.robotId, stale.id);
            }
            MechanismBuildUtil.RemoveComponents<MotorActuator>(s.spins, useUndo);
            MechanismBuildUtil.RemoveComponents<PneumaticActuator>(s.spins, useUndo);
            // JointCoupler is DisallowMultipleComponent — an old one must go before a new one lands.
            MechanismBuildUtil.RemoveComponents<JointCoupler>(s.spins, useUndo);

            JointCoupler coupler = MechanismBuildUtil.AddOrGet<JointCoupler>(s.spins, useUndo);
            coupler.follower = body;
            coupler.driver = driverBody;
            coupler.mode = JointCoupler.CoupleMode.Velocity;
            coupler.ratio = Mathf.Approximately(s.ratio, 0f) ? 1f : s.ratio;
            coupler.offsetDeg = 0f;
            coupler.BakeDrive(); // reads the driver's speed cap, so it must run after Apply above

            MechanismBuildUtil.AddOrGet<IgnoreRobotSelfCollision>(s.spins, useUndo);
            EditorUtility.SetDirty(body);
            EditorUtility.SetDirty(coupler);
            chained++;
        }

        // Display name for Configure Controller. The catalog is what the home screen reads, so it
        // has to be refreshed and flushed for the name to show up there.
        RobotMechanisms.Mechanism mech = registry.Find(id);
        if (mech != null)
        {
            mech.displayName = driverName;
            UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, root.name, registry);
            AssetDatabase.SaveAssets();
        }

        // Buttons: cleared first so a rebuild never stacks another pair. The style the player chose
        // for this mechanism (if any) is deliberately kept — AssignButtons follows it.
        MechanismBuildUtil.ClearMechanismBindings(registry.robotId, id);
        string buttonNote = "kept clear (assign in Configure Controller)";
        if (o.autoAssignButton)
            buttonNote = MechanismAutoDetect.AssignButtons(registry.robotId, id,
                AddMechanismJoint.JointType.Continuous);

        if (useUndo) Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(registry);
        if (root.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.scene);

        string wheelNote = stripped > 0
            ? $"\n\nMoved {stripped} wheel(s) off the drivetrain — they're part of this chain now."
            : string.Empty;
        bool anyGuessed = false;
        foreach (Resolved r in resolved) if (!r.fromAxle) anyGuessed = true;
        string guessNote = anyGuessed
            ? "\n\nSome stations had no axle set, so their axis was guessed from the group's shape. " +
              "If one spins about the wrong line, set its Axle and build again."
            : string.Empty;

        return $"Built the chain '{driverName}': 1 powered station + {chained} chained.\n\n" +
               $"Buttons: {buttonNote}.{wheelNote}{guessNote}\n\n" +
               "Next: if this is an intake, run Add Intake (Pull-Force) with the powered station " +
               "selected — it adds the mouth and hold point and rides the button this chain already " +
               "uses.\n\n" +
               "Then PLAY to check it: hold the forward button and every station should turn the " +
               "same way, in place. Validate Robot Physics won't catch a bad chain — it drives the " +
               "wheels directly and never touches these motors.";
    }

    // Where a registered mechanism's actuator actually lives, or null.
    private static GameObject HolderOf(RobotMechanisms.Mechanism m)
    {
        if (m == null) return null;
        if (m.motor != null) return m.motor.gameObject;
        if (m.pneumatic != null) return m.pneumatic.gameObject;
        return null;
    }

    private static GameObject[] MergeList(Station s)
    {
        var extras = new List<GameObject>();
        if (s.axle != null) extras.Add(s.axle);
        if (s.alsoSpins != null)
            foreach (GameObject go in s.alsoSpins)
                if (go != null) extras.Add(go);
        return extras.Count > 0 ? extras.ToArray() : null;
    }

    // Stations must be independent links hanging off the chassis. If one sat inside another, the
    // joint core would parent it to that station instead of the chassis, and a coupler matching the
    // parent's joint-space rate would spin it at double speed in world.
    private static void ValidateDisjoint(List<Station> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            for (int j = i + 1; j < list.Count; j++)
            {
                Transform a = list[i].spins.transform, b = list[j].spins.transform;
                if (a == b)
                    throw new InvalidOperationException(
                        $"'{a.name}' is listed as two stations. Each station is a separate spinning group.");
                if (a.IsChildOf(b) || b.IsChildOf(a))
                    throw new InvalidOperationException(
                        $"'{a.name}' and '{b.name}' are nested inside each other. Each station must be " +
                        "its own group hanging off the frame, not one inside another.");
            }
        }

        var owner = new Dictionary<GameObject, int>();
        for (int i = 0; i < list.Count; i++)
        {
            foreach (GameObject go in AllParts(list[i]))
            {
                if (go == null) continue;
                if (owner.TryGetValue(go, out int first) && first != i)
                    throw new InvalidOperationException(
                        $"'{go.name}' is used by station {first + 1} and station {i + 1}. A part can " +
                        "only spin with one station.");
                owner[go] = i;
            }
        }
    }

    // Every station has to hang off the SAME link. One nested inside another link — a different
    // station, or an already-rigged mechanism like a lift arm — gets jointed to THAT link instead of
    // the frame, and a coupler matching the powered station's joint-space rate then spins it at
    // double speed in world. Mounting the whole chain on a lift carriage is fine: they still share
    // a parent. This catches what the pairwise nesting check above can't, because the link a station
    // is buried inside need not be a station itself.
    private static void ValidateSharedParent(List<Station> list)
    {
        ArticulationBody reference = NearestAncestorBody(list[0].spins.transform);
        for (int i = 1; i < list.Count; i++)
        {
            ArticulationBody parent = NearestAncestorBody(list[i].spins.transform);
            if (parent == reference) continue;
            throw new InvalidOperationException(
                $"'{list[i].spins.name}' is mounted on {Describe(parent)}, but '{list[0].spins.name}' " +
                $"is mounted on {Describe(reference)}. Every station in a chain has to hang off the " +
                "same frame — one buried inside another moving link would be driven by that link as " +
                "well as by the chain.");
        }
    }

    private static string Describe(ArticulationBody body) => body != null ? $"'{body.name}'" : "nothing";

    private static ArticulationBody NearestAncestorBody(Transform t)
    {
        for (Transform p = t.parent; p != null; p = p.parent)
        {
            ArticulationBody body = p.GetComponent<ArticulationBody>();
            if (body != null) return body;
        }
        return null;
    }

    private static IEnumerable<GameObject> AllParts(Station s)
    {
        yield return s.spins;
        if (s.axle != null) yield return s.axle;
        if (s.alsoSpins == null) yield break;
        foreach (GameObject go in s.alsoSpins)
            if (go != null) yield return go;
    }

    // Works out every station's spin axis. Two passes, because the SIGN has to be settled across
    // stations before the axes are converted into each link's own frame: which way a shaft's long
    // axis happens to point in world depends on how that instance was placed in CAD, and the joint
    // core bakes that sign into the joint's positive rotation direction — so without agreeing on a
    // sign first, a subset of the chain would run backwards.
    public static bool TryResolveAxes(IList<Station> stations, out List<Resolved> resolved, out string problem)
    {
        resolved = null;
        problem = null;

        var list = new List<Station>();
        foreach (Station s in stations)
            if (s != null && s.spins != null) list.Add(s);
        if (list.Count == 0) return false;

        var result = new List<Resolved>(list.Count);
        foreach (Station s in list)
        {
            Vector3 worldAxis, worldCenter;
            bool fromAxle = TryAxleWorldAxis(s.axle, out worldAxis, out worldCenter);
            if (!fromAxle)
            {
                if (!MechanismAutoDetect.TryInferAxisAnchor(s.spins,
                        AddMechanismJoint.JointType.Continuous, out Vector3 la, out Vector3 lc))
                {
                    problem = $"'{s.spins.name}' has no meshes to work an axis out from. Set its Axle, " +
                              "or pick the group that actually holds the geometry.";
                    return false;
                }
                worldAxis = s.spins.transform.TransformDirection(la).normalized;
                worldCenter = s.spins.transform.TransformPoint(lc);
            }
            result.Add(new Resolved { worldAxis = worldAxis, worldCenter = worldCenter, fromAxle = fromAxle });
        }

        // Agree on a direction. Aligning to the powered station alone makes each chain internally
        // consistent but says nothing ACROSS chains: two mirrored copies of a mechanism (a left and
        // right intake side) have mirrored axles, so each would align to its own and the two
        // assemblies would counter-rotate in world for the same button press.
        //
        // So the reference is the ROBOT's lateral axis when the shafts run that way — which is the
        // normal case, chains sit on lateral shafts like the wheels do. Every chain on the robot
        // then resolves to the same reference, and "forward" means one world direction across all of
        // them. Reverse Direction is there when a pair genuinely should counter-rotate.
        Vector3 reference = result[0].worldAxis;
        Vector3 lateral = DrivetrainLateralWorld(list[0].spins);
        if (lateral.sqrMagnitude > 1e-6f && Mathf.Abs(Vector3.Dot(reference, lateral)) > LateralAlignment)
            reference = Vector3.Dot(reference, lateral) < 0f ? -reference : reference;

        for (int i = 0; i < result.Count; i++)
        {
            Resolved r = result[i];
            float dot = Vector3.Dot(r.worldAxis, reference);
            if (dot < 0f) r.worldAxis = -r.worldAxis;
            r.alignment = Mathf.Abs(dot);
            r.linkAxis = list[i].spins.transform.InverseTransformDirection(r.worldAxis).normalized;
            if (r.linkAxis.sqrMagnitude < 1e-8f) r.linkAxis = Vector3.up;
            r.linkAnchor = list[i].spins.transform.InverseTransformPoint(r.worldCenter);
            result[i] = r;
        }

        resolved = result;
        return true;
    }

    // The axle's spin axis: the longest dimension of its biggest mesh, kept as a real oriented
    // direction (rotated by the mesh's own transform) rather than snapped to a world X/Y/Z the way
    // the generic shape guess does. The anchor is that mesh's center, which sits on the shaft's
    // centerline by construction — and the anchor matters even for a free-spinning joint, since it's
    // the point the axis runs through (get it wrong and the part orbits instead of spinning in place).
    public static bool TryAxleWorldAxis(GameObject axle, out Vector3 worldAxis, out Vector3 worldCenter)
    {
        worldAxis = Vector3.right;
        worldCenter = Vector3.zero;
        if (axle == null) return false;

        MeshFilter best = null;
        float bestLength = 0f;
        foreach (MeshFilter mf in axle.GetComponentsInChildren<MeshFilter>(true))
        {
            // sharedMesh, not mesh: reading `mesh` at edit time instantiates and leaks a copy.
            if (mf == null || mf.sharedMesh == null) continue;
            Vector3 size = WorldSize(mf);
            float longest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            // An axle group can hold collars and spacers as well as the shaft; the shaft is the
            // longest thing in it.
            if (longest <= bestLength) continue;
            bestLength = longest;
            best = mf;
        }
        if (best == null || bestLength <= 1e-6f) return false;

        // The mesh's own transform, not the axle root's — mesh leaves sit nested under part groups
        // and can carry their own rotation.
        Vector3 worldSize = WorldSize(best);
        Vector3 localAxis = worldSize.x >= worldSize.y && worldSize.x >= worldSize.z ? Vector3.right
            : worldSize.y >= worldSize.z ? Vector3.up : Vector3.forward;
        worldAxis = (best.transform.rotation * localAxis).normalized;

        Renderer renderer = best.GetComponent<Renderer>();
        worldCenter = renderer != null
            ? renderer.bounds.center
            : best.transform.TransformPoint(best.sharedMesh.bounds.center);
        return true;
    }

    // Mesh-local bounds scaled into world units, so the longest side is compared fairly under a
    // non-uniform scale.
    private static Vector3 WorldSize(MeshFilter mf)
    {
        Vector3 size = mf.sharedMesh.bounds.size;
        Vector3 scale = mf.transform.lossyScale;
        return new Vector3(size.x * Mathf.Abs(scale.x), size.y * Mathf.Abs(scale.y), size.z * Mathf.Abs(scale.z));
    }

    // The robot's left-to-right axis, taken from the drivetrain wheel centroids — the same reading
    // Dr4bLiftBuilder and PneumaticBuilder use, and more reliable than the chassis transform's own
    // right, which reflects however the CAD happened to be oriented on import.
    private static Vector3 DrivetrainLateralWorld(GameObject part)
    {
        RobotMechanisms registry = part.GetComponentInParent<RobotMechanisms>();
        if (registry == null) return Vector3.zero;
        RobotMotorController mc = registry.GetComponentInChildren<RobotMotorController>(true);
        if (mc == null) return registry.transform.right;
        Vector3 lateral = Centroid(mc.rightWheels) - Centroid(mc.leftWheels);
        return lateral.sqrMagnitude > 1e-6f ? lateral.normalized : registry.transform.right;
    }

    private static Vector3 Centroid(ArticulationBody[] bodies)
    {
        if (bodies == null) return Vector3.zero;
        Vector3 sum = Vector3.zero;
        int n = 0;
        foreach (ArticulationBody b in bodies)
            if (b != null) { sum += b.transform.position; n++; }
        return n > 0 ? sum / n : Vector3.zero;
    }

    public static bool LooksLikeAxle(GameObject go)
    {
        if (go == null) return false;
        string name = RobotPartClassifier.NormalizeForTokens(go.name);
        return name.Contains("axle") || name.Contains("shaft");
    }

    // A V5 motor is bolted to the frame — only the axle out of it turns. Catching it in the spinning
    // group is worth a warning, because a spinning motor housing drags its wiring geometry around.
    public static bool LooksLikeMotorHousing(GameObject go)
    {
        if (go == null) return false;
        foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            if (RobotPartClassifier.NormalizeForTokens(t.name).Contains("motor")) return true;
        return false;
    }

    // Drops every station from the robot's drivetrain wheel arrays, so a chained wheel isn't also
    // driven directly by the joysticks. Returns how many were removed.
    private static int StripFromDrivetrain(GameObject root, List<Station> list, bool useUndo)
    {
        RobotMotorController motor = root.GetComponent<RobotMotorController>();
        if (motor == null) return 0;

        var bodies = new HashSet<ArticulationBody>();
        foreach (Station s in list)
        {
            ArticulationBody b = s.spins.GetComponent<ArticulationBody>();
            if (b != null) bodies.Add(b);
        }
        if (bodies.Count == 0) return 0;

        ArticulationBody[] left = WithoutBodies(motor.leftWheels, bodies, out int leftRemoved);
        ArticulationBody[] right = WithoutBodies(motor.rightWheels, bodies, out int rightRemoved);
        if (leftRemoved + rightRemoved == 0) return 0;

        if (useUndo) Undo.RecordObject(motor, UndoName);
        motor.leftWheels = left;
        motor.rightWheels = right;
        EditorUtility.SetDirty(motor);
        return leftRemoved + rightRemoved;
    }

    private static ArticulationBody[] WithoutBodies(ArticulationBody[] arr, HashSet<ArticulationBody> remove,
        out int removed)
    {
        removed = 0;
        if (arr == null) return null;
        var kept = new List<ArticulationBody>(arr.Length);
        foreach (ArticulationBody b in arr)
        {
            if (b != null && remove.Contains(b)) { removed++; continue; }
            kept.Add(b);
        }
        return kept.ToArray();
    }
}
