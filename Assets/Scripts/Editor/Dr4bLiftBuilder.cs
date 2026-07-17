using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Role-based DR4B lift builder with an ADDITIVE two-stage movement model. The user drops model groups
// into three PURELY TRANSLATIONAL movement buckets — First Stage (up + forward), Second Stage (adds the
// opposing/crane reach on top), Scoring (rides the top) — plus two pivot slots and two arm slots. Only
// the ARMS rotate: first arms pivot at their fixed connection to the stationary channel; second arms
// pivot at the (rising) second sprocket. A hidden motor is the ONE real joint; everything else is a
// transform follower (stable). The imported model is never reparented; only the stack carriage moves.
//
// Usage: set up the robot + intake first, then Tools > RoboSim > Robot > Mechanisms > Build DR4B Lift (roles).
public class Dr4bLiftBuilderWindow : EditorWindow
{
    private const string Title = "Build DR4B Lift (roles)";

    // Movement buckets (purely translational).
    [SerializeField] private List<GameObject> firstStageMove = new List<GameObject>();
    [SerializeField] private List<GameObject> secondStageMove = new List<GameObject>();
    [SerializeField] private List<GameObject> scoring = new List<GameObject>();
    // Pivots (2 each, left+right; share height = the pivot axis).
    [SerializeField] private List<GameObject> firstArmPivots = new List<GameObject>();   // fixed, on the stationary channel
    [SerializeField] private List<GameObject> secondArmPivots = new List<GameObject>();  // the second sprockets (rise with stage 1)
    // Arms (rotate; may also hold parts that follow the arms).
    [SerializeField] private List<GameObject> firstArms = new List<GameObject>();
    [SerializeField] private List<GameObject> secondArms = new List<GameObject>();

    // Tuning. Defaults are 654V's current TUNED, working values, so a rebuild reproduces the dialed-in lift.
    [SerializeField] private float stage1Rise = 3f;
    [SerializeField] private float stage1Forward = -0.1f;
    [SerializeField] private float stage2Rise = 3.3f;
    [SerializeField] private float stage2Forward = -1.5f;
    [SerializeField] private float firstStageAngle = -90f;
    [SerializeField] private float secondStageAngle = 90f;
    [SerializeField] private bool reverseDriver = false;
    [SerializeField] private bool autoLateralAxis = true;
    [SerializeField] private bool arms4Bar = true;
    [SerializeField] private float holdFriction = 8f;
    [SerializeField] private float liftRaiseSeconds = 2.0f;
    [SerializeField] private bool autoAssignButtons = true;
    [SerializeField] private string liftDisplayName = "DR4B Lift";

    [SerializeField] private Vector2 scroll;

    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Build DR4B Lift (roles)", false, 22)]
    private static void ShowWindow()
    {
        Dr4bLiftBuilderWindow w = GetWindow<Dr4bLiftBuilderWindow>(Title);
        w.minSize = new Vector2(480f, 700f);
        w.Show();
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.HelpBox(
            "Assign your DR4B, then Build. A hidden motor drives it; ONLY the arms rotate — everything in " +
            "the movement buckets purely translates.\n" +
            "• First Stage movement = everything that moves (up + forward): the moving C-channel, the driving " +
            "sprockets, brackets, etc.\n" +
            "• Second Stage movement = the subset that ALSO does the opposing/crane motion (added on top).\n" +
            "• Scoring = the scoring parts (ride the top; the stack rides here, orientation kept).\n" +
            "• First-arm pivots = the 2 fixed points where the first arms meet the stationary channel.\n" +
            "• Second-arm pivots = the 2 second sprockets (they rise; second arms pivot at their height).\n" +
            "Leave the stationary channel / screws / unrelated parts unassigned.\n\n" +
            "Set up the robot (Set Up Imported Robot) and its intake (Add Intake) first.",
            MessageType.Info);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Clean / reset", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Select the robot (or any DR4B part) and click Clean to strip ALL old lift " +
            "wiring off it (old + current followers, couplers, the hidden motor + carriage, lift bodies/" +
            "mechanisms) and restore the parts to plain meshes. Wheels, intake, and your meshes are kept.", MessageType.None);
        if (GUILayout.Button("Clean DR4B lift off the robot"))
            DoClean();

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Auto-fill roles by name (best guess)")) AutoFillByName();
            if (GUILayout.Button("Clear all slots", GUILayout.Width(110))) ClearAllSlots();
        }
        if (GUILayout.Button("Import existing cheat lift (migrate sweep + rise)")) ImportExisting();

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Movement (purely translational)", EditorStyles.boldLabel);
        DrawGoList("First Stage movement", "Everything that moves — translates up + forward. Includes the driving sprockets (they just ride).", firstStageMove);
        DrawGoList("Second Stage movement", "The subset that ALSO does the opposing/crane motion (added on top of the first).", secondStageMove);
        DrawGoList("Scoring mechanism", "The scoring parts — ride the top (first + second stage), orientation kept. The held stack rides here.", scoring);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Pivots (2 each — left + right, same height)", EditorStyles.boldLabel);
        DrawGoList("First-arm pivots (fixed)", "The 2 points where the first arms connect to the stationary channel. Fixed — the first arms rotate about the nearest one.", firstArmPivots);
        DrawGoList("Second-arm pivots (rise)", "The 2 second sprockets. They rise with the first stage; the second arms rotate about the nearest one's height.", secondArmPivots);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Arms (rotate; may include parts that follow them)", EditorStyles.boldLabel);
        DrawGoList("First stage arms", "Scissor about the nearest first-arm pivot. Add anything that rotates with them.", firstArms);
        DrawGoList("Second stage arms", "Counter-scissor about the nearest second sprocket. Add anything that rotates with them.", secondArms);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Tuning", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("First stage (everything moves)", EditorStyles.miniBoldLabel);
        firstStageAngle = EditorGUILayout.FloatField(new GUIContent("  Arm rotation (deg)", "How many DEGREES the first-stage arms rotate at full lift. Signed — flip the sign if they swing the wrong way."), firstStageAngle);
        stage1Rise = EditorGUILayout.FloatField(new GUIContent("  Rise up", "How far the first stage lifts at full lift (1 unit = 0.1 m)."), stage1Rise);
        stage1Forward = EditorGUILayout.FloatField(new GUIContent("  Forward", "Forward drift at full lift (toward the stationary channel). Signed."), stage1Forward);
        EditorGUILayout.LabelField("Second stage (subset + scoring, ADDED on top)", EditorStyles.miniBoldLabel);
        secondStageAngle = EditorGUILayout.FloatField(new GUIContent("  Arm rotation (deg)", "How many DEGREES the second-stage arms rotate at full lift. Signed — usually opposite the first (the 'reverse')."), secondStageAngle);
        stage2Rise = EditorGUILayout.FloatField(new GUIContent("  Rise up", "Extra up for the second stage at full lift."), stage2Rise);
        stage2Forward = EditorGUILayout.FloatField(new GUIContent("  Forward", "Extra forward reach for the second stage. Signed — negative pulls it back."), stage2Forward);
        reverseDriver = EditorGUILayout.Toggle(new GUIContent("Reverse driver", "Flip if the UP button lowers the lift."), reverseDriver);
        autoLateralAxis = EditorGUILayout.Toggle(new GUIContent("Auto hinge axis (from drivetrain)", "Derive the arm hinge axis from the left/right wheels."), autoLateralAxis);
        arms4Bar = EditorGUILayout.Toggle(new GUIContent("Arms are 4-bar (per-bar pivots)", "Each arm group is a parallelogram of parallel bars (an upper + a lower/support bar with different pins): rotate EACH bar about its own base so the support bars don't swing off their connection. Big head assemblies auto-stay rigid."), arms4Bar);
        holdFriction = EditorGUILayout.FloatField(new GUIContent("Hold friction", "Driver joint friction so the lift self-holds when released."), holdFriction);
        liftRaiseSeconds = EditorGUILayout.FloatField(new GUIContent("Lift raise time (s)", "Seconds to raise fully while holding the UP button. Lower = faster. Hold to lift; it stops at the top. Editable live afterward on the Dr4bLift component (no rebuild)."), liftRaiseSeconds);
        autoAssignButtons = EditorGUILayout.Toggle(new GUIContent("Auto-assign buttons", "Map the lift to the next free up/down pair (intake holds R1/R2, so lift -> L1/L2). Score is on the A button."), autoAssignButtons);
        liftDisplayName = EditorGUILayout.TextField(new GUIContent("Lift name (config label)", "What the Configure Controller screen shows for this lift. Set it to anything — it's independent of the hidden motor's object name, and persists on Build."), liftDisplayName);

        EditorGUILayout.Space();
        if (GUILayout.Button("Build DR4B Lift", GUILayout.Height(32)))
        {
            try
            {
                string report = Dr4bLiftSetup.Build(BuildOptions(), useUndo: true);
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

    private Dr4bLiftSetup.Options BuildOptions() => new Dr4bLiftSetup.Options
    {
        firstStageMove = firstStageMove,
        secondStageMove = secondStageMove,
        scoring = scoring,
        firstArmPivots = firstArmPivots,
        secondArmPivots = secondArmPivots,
        firstArms = firstArms,
        secondArms = secondArms,
        stage1Rise = stage1Rise,
        stage1Forward = stage1Forward,
        stage2Rise = stage2Rise,
        stage2Forward = stage2Forward,
        firstStageAngle = firstStageAngle,
        secondStageAngle = secondStageAngle,
        reverseDriver = reverseDriver,
        autoLateralAxis = autoLateralAxis,
        arms4Bar = arms4Bar,
        holdFriction = holdFriction,
        liftRaiseSeconds = liftRaiseSeconds,
        autoAssignButtons = autoAssignButtons,
        liftDisplayName = liftDisplayName,
    };

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

    private void ImportExisting()
    {
        RobotMechanisms reg = ResolveRegistry();
        if (reg == null) { EditorUtility.DisplayDialog(Title, "Select the robot (or a part of it) first.", "OK"); return; }
        foreach (LinkageBarFollower f in reg.GetComponentsInChildren<LinkageBarFollower>(true))
            if (f.mode == LinkageBarFollower.FollowMode.TranslateAlongWorldAxis && Mathf.Abs(f.unitsPerRadian) > 1e-4f)
            {
                float total = Mathf.Abs(f.unitsPerRadian) * (60f * Mathf.Deg2Rad);   // old total rise
                stage1Rise = total * 0.5f; stage2Rise = total * 0.5f;
                break;
            }
        EditorUtility.DisplayDialog(Title, "Migrated the old cheat lift's rise. Now drag your folders into the " +
            "movement buckets + pivot/arm slots (both sides) and Build.", "OK");
    }

    private RobotMechanisms ResolveRegistry()
    {
        foreach (var list in new[] { firstStageMove, scoring, secondArmPivots, firstArms, secondArms, firstArmPivots })
            foreach (GameObject g in list)
                if (g != null) { RobotMechanisms r = g.GetComponentInParent<RobotMechanisms>(); if (r != null) return r; }
        if (Selection.activeGameObject != null)
        {
            RobotMechanisms r = Selection.activeGameObject.GetComponentInParent<RobotMechanisms>();
            if (r != null) return r;
        }
        return null;
    }

    // ---- Auto-fill roles by node name ------------------------------------------------------------
    private enum Slot { None, Skip, FirstStageMove, SecondStageMove, Scoring, FirstArmPivots, SecondArmPivots, FirstArms, SecondArms }

    private void ClearAllSlots()
    {
        firstStageMove.Clear(); secondStageMove.Clear(); scoring.Clear();
        firstArmPivots.Clear(); secondArmPivots.Clear(); firstArms.Clear(); secondArms.Clear();
        Repaint();
    }

    private void DoClean()
    {
        RobotMechanisms reg = ResolveRegistry();
        if (reg == null) { EditorUtility.DisplayDialog(Title, "Select the robot (or a DR4B part) in the Hierarchy first.", "OK"); return; }
        try
        {
            string r = Dr4bLiftSetup.Clean(reg, useUndo: true);
            ClearAllSlots();
            EditorUtility.DisplayDialog(Title, r, "OK");
        }
        catch (Exception e) { EditorUtility.DisplayDialog(Title, e.Message, "OK"); Debug.LogException(e); }
    }

    private void AutoFillByName()
    {
        RobotMechanisms reg = ResolveRegistry();
        if (reg == null) { EditorUtility.DisplayDialog(Title, "Select the robot (or a DR4B part) in the Hierarchy first.", "OK"); return; }
        ClearAllSlots();
        AutoFillRec(reg.transform, Slot.None);
        string r = "Auto-filled by name (VERIFY each slot, then Build):\n\n" +
            ReportLine("First Stage movement", firstStageMove) +
            ReportLine("Second Stage movement", secondStageMove) +
            ReportLine("Scoring", scoring) +
            ReportLine("First-arm pivots", firstArmPivots) +
            ReportLine("Second-arm pivots", secondArmPivots) +
            ReportLine("First stage arms", firstArms) +
            ReportLine("Second stage arms", secondArms) +
            "\nHeads up: the fixed 'First-arm pivots' (stationary connection points) usually can't be auto-detected " +
            "from names — if that slot is empty, add those 2 manually.";
        EditorUtility.DisplayDialog(Title, r, "OK");
        Repaint();
    }

    // Descend the hierarchy; assign the first named node in each branch to its slot (so a whole folder
    // is one entry). A folder that would be an arm/mover but CONTAINS a sprocket/pivot is split (descend
    // into its children instead). parentHint biases generic 'arm'/'sprocket' children inside a 2nd-stage folder.
    private void AutoFillRec(Transform t, Slot parentHint)
    {
        if (t == null) return;
        if (t.name == "LiftMotor" || t.name == "Dr4bCarriage") return;

        Slot slot = Classify(t.name);
        if (parentHint == Slot.SecondArms)
        {
            if (slot == Slot.FirstArms) slot = Slot.SecondArms;
            if (slot == Slot.FirstStageMove && Norm(t.name).Contains("sprocket")) slot = Slot.SecondArmPivots;
        }
        if (slot == Slot.Skip) return;

        if (slot != Slot.None)
        {
            bool bucketOrArm = slot == Slot.FirstStageMove || slot == Slot.SecondStageMove || slot == Slot.Scoring
                               || slot == Slot.FirstArms || slot == Slot.SecondArms;
            if (bucketOrArm && HasDescendantToken(t, "sprocket", "pivot"))
            {
                Slot hint = (slot == Slot.SecondArms || slot == Slot.SecondStageMove) ? Slot.SecondArms : parentHint;
                foreach (Transform c in t) AutoFillRec(c, hint);
                return;
            }
            AddToSlot(slot, t.gameObject);
            return;   // whole node assigned; don't descend
        }
        foreach (Transform c in t) AutoFillRec(c, parentHint);
    }

    private static Slot Classify(string rawName)
    {
        string n = Norm(rawName);
        if (n.Contains("scoring") || n.Contains("tray")) return Slot.Scoring;
        if (n.Contains("sprocket"))
        {
            if (n.Contains("driv")) return Slot.FirstStageMove;
            if (n.Contains("second") || n.Contains("2")) return Slot.SecondArmPivots;
            return Slot.FirstStageMove;
        }
        if (n.Contains("pivot")) return Slot.FirstArmPivots;
        if (n.Contains("arm")) return n.Contains("second") ? Slot.SecondArms : Slot.FirstArms;
        if (n.Contains("secondstage")) return Slot.SecondArms;
        if (n.Contains("nonstationary") || n.Contains("movingc") || n.Contains("moveswith")) return Slot.FirstStageMove;
        if (n.Contains("firststage")) return Slot.FirstStageMove;
        if (n.Contains("stationarychannel") || n.Contains("screw") || n.Contains("bearing")
            || n.Contains("spacer") || n.Contains("washer") || n.Contains("elsestuff")) return Slot.Skip;
        return Slot.None;
    }

    private static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private bool HasDescendantToken(Transform t, params string[] tokens)
    {
        foreach (Transform d in t.GetComponentsInChildren<Transform>(true))
        {
            if (d == t) continue;
            string n = Norm(d.name);
            foreach (string tok in tokens) if (n.Contains(tok)) return true;
        }
        return false;
    }

    private void AddToSlot(Slot s, GameObject go)
    {
        switch (s)
        {
            case Slot.FirstStageMove: if (!firstStageMove.Contains(go)) firstStageMove.Add(go); break;
            case Slot.SecondStageMove: if (!secondStageMove.Contains(go)) secondStageMove.Add(go); break;
            case Slot.Scoring: if (!scoring.Contains(go)) scoring.Add(go); break;
            case Slot.FirstArmPivots: if (!firstArmPivots.Contains(go)) firstArmPivots.Add(go); break;
            case Slot.SecondArmPivots: if (!secondArmPivots.Contains(go)) secondArmPivots.Add(go); break;
            case Slot.FirstArms: if (!firstArms.Contains(go)) firstArms.Add(go); break;
            case Slot.SecondArms: if (!secondArms.Contains(go)) secondArms.Add(go); break;
        }
    }

    private static string ReportLine(string label, List<GameObject> list)
    {
        if (list.Count == 0) return label + ": (none)\n";
        return label + ": " + string.Join(", ", list.ConvertAll(g => g != null ? g.name : "?")) + "\n";
    }
}

// Headless-runnable core (window/core split like PneumaticBuilderWindow/PneumaticSetup).
public static class Dr4bLiftSetup
{
    private const string UndoName = "Build DR4B Lift";
    private const string HubName = "LiftMotor";
    // The hidden driver joint's travel. Internal — only the 0->1 lift progress matters, and the arm
    // angles + rise are set directly, so this value doesn't change the final look.
    private const float DriverSweepDeg = 60f;

    public struct Options
    {
        public List<GameObject> firstStageMove;
        public List<GameObject> secondStageMove;
        public List<GameObject> scoring;
        public List<GameObject> firstArmPivots;    // fixed (referenced only)
        public List<GameObject> secondArmPivots;   // rise with stage 1 (referenced + wired)
        public List<GameObject> firstArms;
        public List<GameObject> secondArms;

        public float stage1Rise, stage1Forward;
        public float stage2Rise, stage2Forward;
        public float firstStageAngle, secondStageAngle;   // degrees each stage's arms rotate at full lift
        public bool reverseDriver;
        public bool autoLateralAxis;
        public bool arms4Bar;                              // rotate each sub-bar of an arm group about its own base
        public float holdFriction;
        public float liftRaiseSeconds;
        public bool autoAssignButtons;
        public string liftDisplayName;
    }

    public static string Build(Options o, bool useUndo)
    {
        RobotMechanisms registry = ResolveRegistry(o);
        if (registry == null)
            throw new InvalidOperationException(
                "No set-up robot found from the assigned parts (no RobotMechanisms). Assign parts and run Set Up Imported Robot first.");
        Transform chassis = registry.transform;
        Vector3 lateralWorld = DrivetrainLateralWorld(chassis, registry);

        int group = 0;
        if (useUndo)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            group = Undo.GetCurrentGroup();
        }

        // FULL clean first — not just the lift mechanism ids. Re-running with a CHANGED role
        // assignment used to orphan parts: anything removed from a slot between runs kept its
        // follower and stayed neutralized (colliders off, body gone) until a manual Clean. Clean
        // restores every previously-wired part to a plain mesh; the wiring below then rebuilds
        // exactly what's in the slots now, so Build is safely re-runnable on its own.
        // Clean also wipes the lift's button bindings — when auto-assign is OFF ("kept" behavior),
        // snapshot them and put them back.
        List<ButtonAssignment> keptBindings = o.autoAssignButtons
            ? null : SnapshotBindings(registry.robotId, UrdfPostProcessor.Slugify(HubName));
        Clean(registry, useUndo, refreshCatalog: false);
        if (keptBindings != null) RestoreBindings(registry.robotId, keptBindings);

        // 1) Hidden drive hub = the ONE real revolute motor. Hold-to-run to the joint limit (= full lift),
        //    at a speed set by the raise time (sweep degrees / (6 * seconds) = RPM).
        GameObject hub = EnsureHub(chassis, lateralWorld, DriverSweepDeg, o.reverseDriver, o.holdFriction, useUndo, out string hubId, out ArticulationBody hubBody);
        MotorActuator hubMotor = hub.GetComponent<MotorActuator>();
        if (hubMotor != null)
        {
            if (useUndo) Undo.RecordObject(hubMotor, UndoName);
            hubMotor.maxRpm = DriverSweepDeg / (6f * Mathf.Max(0.05f, o.liftRaiseSeconds));
        }

        // 2) Controller.
        Dr4bLift lift = MechanismBuildUtil.AddOrGet<Dr4bLift>(chassis.gameObject, useUndo);
        if (useUndo) Undo.RecordObject(lift, UndoName);
        lift.driver = hubBody;
        lift.chassis = chassis;
        lift.sweepDeg = DriverSweepDeg;
        lift.liftRaiseSeconds = o.liftRaiseSeconds;   // runtime source of truth; editable live on the component
        lift.stage1Rise = o.stage1Rise;
        lift.stage1Forward = o.stage1Forward;
        lift.stage2Rise = o.stage2Rise;
        lift.stage2Forward = o.stage2Forward;
        lift.autoLateralAxis = o.autoLateralAxis;
        lift.lateralAxisChassisLocal = chassis.InverseTransformDirection(lateralWorld).normalized;
        lift.translators = new List<Dr4bMoveFollower>();
        lift.rotators = new List<PivotRotateFollower>();

        var skip = new HashSet<GameObject> { hub };

        // 3) Purely-translational movers. Second-arm pivots (second sprockets) also translate on stage 1
        //    so they RISE — the second arms then pivot about their live (risen) position.
        WireTranslators(o.firstStageMove, true, false, lift, skip, useUndo);
        WireTranslators(o.secondStageMove, true, true, lift, skip, useUndo);
        WireTranslators(o.scoring, true, true, lift, skip, useUndo);
        WireTranslators(o.secondArmPivots, true, false, lift, skip, useUndo);
        // First-arm pivots are FIXED (on the stationary channel) — referenced only, never wired/neutralized.

        // 4) Only the arms rotate. First arms pivot at their fixed stationary connection; second arms at
        //    the (rising) second sprocket. Each arm binds to the nearest pivot (both sides handled). With
        //    arms4Bar on, a group of parallel bars rotates each bar about its own base (see PivotRotateFollower).
        WireRotators(o.firstArms, o.firstArmPivots, o.firstStageAngle, o.arms4Bar, lift, skip, useUndo);
        WireRotators(o.secondArms, o.secondArmPivots, o.secondStageAngle, o.arms4Bar, lift, skip, useUndo);

        // 5) Stack carriage rides the scoring (stage 1 + 2).
        string carriageNote = WireStackCarriage(registry, chassis, lift, useUndo);

        // 6) Buttons + display name on the hub.
        string buttonNote = "kept";
        if (o.autoAssignButtons)
        {
            MechanismBuildUtil.ClearMechanismBindings(registry.robotId, hubId);
            buttonNote = MechanismAutoDetect.AssignButtons(registry.robotId, hubId, AddMechanismJoint.JointType.Revolute);
        }
        RobotMechanisms.Mechanism mech = registry.Find(hubId);
        if (mech != null)
        {
            mech.displayName = string.IsNullOrEmpty(o.liftDisplayName) ? "Lift" : o.liftDisplayName;
            UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, registry.gameObject.name, registry);
            AssetDatabase.SaveAssets();   // flush the catalog so Configure Controller shows the new name
        }

        if (useUndo) Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(registry);
        EditorUtility.SetDirty(lift);
        if (registry.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(registry.gameObject.scene);

        return
            $"Built a role-based DR4B lift on '{registry.name}'.\n\n" +
            $"• Hidden motor '{HubName}' drives it; arms rotate {o.firstStageAngle} / {o.secondStageAngle} deg. Buttons: {buttonNote}.\n" +
            $"• Translational movers: {lift.translators.Count}  Scissor arms: {lift.rotators.Count}" +
            (o.arms4Bar ? " (4-bar: each parallel bar rotates about its OWN base; big head assemblies stay rigid).\n" : ".\n") +
            $"• Stack: {carriageNote}. Intake mouth stays at the base.\n" +
            $"• Hold-to-lift, stops at top ({o.liftRaiseSeconds}s to full — editable live on the Dr4bLift component). Score = A button (drops the stack, only while raised).\n" +
            $"• Intake AND outtake auto-disable while the lift is raised; the intake markers/mouth overlay are hidden.\n\n" +
            "IMPORTANT: the field spawns the robot PREFAB at Play, not this scene object. APPLY THESE " +
            "CHANGES TO THE PREFAB (Prefab Mode, or Overrides > Apply All) or they won't spawn.\n\n" +
            "Play, then hold the UP button (key 1). Tuning:\n" +
            "• Up lowers -> Reverse driver.\n" +
            "• A stage's arms swing the wrong way -> flip the sign of that stage's Arm rotation (deg).\n" +
            "• Adjust each stage's Arm rotation + Rise + Forward so the linkage lines up. Scoring reaches the highest.";
    }

    // Strip ALL lift wiring off the robot so it's a blank slate to re-add: every follower/marker/coupler
    // (old + current), the hidden motor + carriage, the controller, and the lift mechanisms/bindings.
    // On each lift part it re-enables colliders and removes the lift ArticulationBody/MotorActuator so
    // the part is a plain mesh again. Wheels, the intake, other mechanisms, and the chassis root are kept.
    // refreshCatalog=false is for the Build-internal call, which refreshes + saves at the end anyway.
    public static string Clean(RobotMechanisms registry, bool useUndo, bool refreshCatalog = true)
    {
        if (registry == null) throw new InvalidOperationException("No robot (RobotMechanisms) found from the selection.");
        GameObject robot = registry.gameObject;
        Transform chassis = registry.transform;
        RobotMotorController mc = robot.GetComponent<RobotMotorController>();

        int group = 0;
        if (useUndo) { Undo.IncrementCurrentGroup(); Undo.SetCurrentGroupName("Clean DR4B Lift"); group = Undo.GetCurrentGroup(); }

        int followers = 0, bodies = 0, helpers = 0;
        var removedIds = new List<string>();

        // 1) Restore the intake anchors to the chassis before the carriage is destroyed.
        IntakePull pull = registry.GetComponentInChildren<IntakePull>(true);
        if (pull != null)
        {
            if (pull.holdPoint != null) MechanismBuildUtil.EnsureChildOf(pull.holdPoint, chassis, useUndo);
            if (pull.slotAnchors != null)
                foreach (Transform a in pull.slotAnchors)
                    if (a != null && a != pull.holdPoint) MechanismBuildUtil.EnsureChildOf(a, chassis, useUndo);
        }

        // 2) Strip lift followers/markers/couplers; restore each host to a plain mesh.
        followers += StripFollowers<Dr4bMoveFollower>(robot, registry, mc, ref bodies, useUndo);
        followers += StripFollowers<PivotRotateFollower>(robot, registry, mc, ref bodies, useUndo);
        followers += StripFollowers<LinkageBarFollower>(robot, registry, mc, ref bodies, useUndo);
        foreach (LiftCarriage lc in robot.GetComponentsInChildren<LiftCarriage>(true)) DestroyComp(lc, useUndo);
        foreach (JointCoupler jc in robot.GetComponentsInChildren<JointCoupler>(true)) DestroyComp(jc, useUndo);

        // 3) Remove lift mechanisms + any now-dangling mechanism, with their bindings.
        foreach (RobotMechanisms.Mechanism m in registry.mechanisms.ToArray())
        {
            if (m == null) continue;
            bool isLift = m.id == "lift" || m.id == "liftmotor" || m.displayName == "Lift";
            bool dangling = m.motor == null && m.pneumatic == null;
            if (!isLift && !dangling) continue;
            if (m.motor != null)
            {
                GameObject go = m.motor.gameObject;
                if (go != null && !IsProtected(go, registry, mc))
                { ReEnableColliders(go, useUndo); RemoveComp<MotorActuator>(go, useUndo); if (RemoveBody(go, useUndo)) bodies++; }
            }
            removedIds.Add(m.id);
        }
        foreach (string id in removedIds)
        {
            UrdfPostProcessor.RemoveMechanism(registry, id, useUndo);
            MechanismBuildUtil.ClearMechanismBindings(registry.robotId, id);
        }

        // 4) Destroy the controller + the created helper objects.
        foreach (Dr4bLift dl in robot.GetComponentsInChildren<Dr4bLift>(true)) DestroyComp(dl, useUndo);
        helpers += DestroyNamedChild(chassis, HubName, useUndo);
        helpers += DestroyNamedChild(chassis, "Dr4bCarriage", useUndo);

        if (refreshCatalog)
        {
            UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, robot.name, registry);
            AssetDatabase.SaveAssets();   // flush the catalog so the removed lift disappears from Configure Controller
        }
        if (useUndo) Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(registry);
        if (robot.scene.IsValid()) EditorSceneManager.MarkSceneDirty(robot.scene);

        return
            $"Cleaned the DR4B lift on '{registry.name}':\n" +
            $"• followers removed: {followers}, lift bodies/motors removed: {bodies}, helper objects removed: {helpers}.\n" +
            $"• mechanisms removed: {(removedIds.Count > 0 ? string.Join(", ", removedIds) : "none")}.\n" +
            $"• intake anchors restored to the chassis; colliders re-enabled.\n\n" +
            "The DR4B parts are plain meshes again. Re-add with Auto-fill + Build, then save the prefab.";
    }

    private static int StripFollowers<T>(GameObject robot, RobotMechanisms registry, RobotMotorController mc, ref int bodies, bool useUndo) where T : Component
    {
        int n = 0;
        foreach (T comp in robot.GetComponentsInChildren<T>(true))
        {
            if (comp == null) continue;
            GameObject go = comp.gameObject;
            if (!IsProtected(go, registry, mc))
            {
                ReEnableColliders(go, useUndo);
                RemoveComp<MotorActuator>(go, useUndo);
                if (RemoveBody(go, useUndo)) bodies++;
            }
            DestroyComp(comp, useUndo);
            n++;
        }
        return n;
    }

    // Root / drive wheels / any NON-lift registered mechanism (e.g. the intake roller) must never lose
    // their ArticulationBody.
    private static bool IsProtected(GameObject go, RobotMechanisms registry, RobotMotorController mc)
    {
        ArticulationBody ab = go.GetComponent<ArticulationBody>();
        if (ab != null && ab.isRoot) return true;
        if (ab != null && mc != null)
        {
            if (mc.leftWheels != null && System.Array.IndexOf(mc.leftWheels, ab) >= 0) return true;
            if (mc.rightWheels != null && System.Array.IndexOf(mc.rightWheels, ab) >= 0) return true;
        }
        foreach (RobotMechanisms.Mechanism m in registry.mechanisms)
        {
            if (m == null) continue;
            if (m.id == "lift" || m.id == "liftmotor" || m.displayName == "Lift") continue;
            if (m.motor != null && m.motor.gameObject == go) return true;
            if (m.pneumatic != null && m.pneumatic.gameObject == go) return true;
        }
        return false;
    }

    private static void ReEnableColliders(GameObject go, bool useUndo)
    {
        foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
        {
            if (c == null || c.enabled) continue;
            if (useUndo) Undo.RecordObject(c, "Clean DR4B Lift");
            c.enabled = true;
        }
    }

    private static void RemoveComp<T>(GameObject go, bool useUndo) where T : Component
    {
        foreach (T c in go.GetComponents<T>()) if (c != null) DestroyComp(c, useUndo);
    }

    private static bool RemoveBody(GameObject go, bool useUndo)
    {
        ArticulationBody ab = go.GetComponent<ArticulationBody>();
        if (ab == null || ab.isRoot) return false;
        DestroyComp(ab, useUndo);
        return true;
    }

    private static void DestroyComp(Component c, bool useUndo)
    {
        if (c == null) return;
        if (useUndo) Undo.DestroyObjectImmediate(c);
        else UnityEngine.Object.DestroyImmediate(c);
    }

    private static int DestroyNamedChild(Transform chassis, string name, bool useUndo)
    {
        Transform t = FindChild(chassis, name);
        if (t == null) return 0;
        if (useUndo) Undo.DestroyObjectImmediate(t.gameObject);
        else UnityEngine.Object.DestroyImmediate(t.gameObject);
        return 1;
    }

    private static GameObject EnsureHub(Transform chassis, Vector3 lateralWorld, float sweepDeg, bool reverse,
        float holdFriction, bool useUndo, out string hubId, out ArticulationBody hubBody)
    {
        Transform existing = FindChild(chassis, HubName);
        GameObject hub;
        if (existing != null) hub = existing.gameObject;
        else
        {
            hub = new GameObject(HubName);
            if (useUndo) Undo.RegisterCreatedObjectUndo(hub, UndoName);
            hub.transform.SetParent(chassis, false);
        }
        Vector3 axisLocal = hub.transform.InverseTransformDirection(lateralWorld);
        if (axisLocal.sqrMagnitude < 1e-8f) axisLocal = Vector3.right;
        AddMechanismJoint.Apply(hub, AddMechanismJoint.JointType.Revolute, axisLocal.normalized, Vector3.zero,
            0f, sweepDeg,
            new AddMechanismJoint.Options { actuation = AddMechanismJoint.Actuation.HoldToRun, reverseDirection = reverse },
            useUndo);
        hubId = UrdfPostProcessor.Slugify(hub.name);
        hubBody = hub.GetComponent<ArticulationBody>();
        if (hubBody != null)
        {
            if (useUndo) Undo.RecordObject(hubBody, UndoName);
            if (hubBody.mass < MechanismBuildUtil.MinLiftMass) hubBody.mass = MechanismBuildUtil.MinLiftMass;
            hubBody.jointFriction = holdFriction;
        }
        return hub;
    }

    private static void WireTranslators(List<GameObject> group, bool stage1, bool stage2,
        Dr4bLift lift, HashSet<GameObject> skip, bool useUndo)
    {
        if (group == null) return;
        foreach (GameObject go in group)
        {
            if (go == null || skip.Contains(go)) continue;
            MechanismBuildUtil.NeutralizeToPlainTransform(go, useUndo);
            MechanismBuildUtil.DisableColliders(go, useUndo);
            Dr4bMoveFollower f = MechanismBuildUtil.AddOrGet<Dr4bMoveFollower>(go, useUndo);
            if (useUndo) Undo.RecordObject(f, UndoName);
            f.followsStage1 = stage1;
            f.followsStage2 = stage2;
            f.spinRatio = 0f;   // sprockets don't spin — only the arms rotate
            if (!lift.translators.Contains(f)) lift.translators.Add(f);
        }
    }

    private static void WireRotators(List<GameObject> arms, List<GameObject> pivots, float angleDeg,
        bool arms4Bar, Dr4bLift lift, HashSet<GameObject> skip, bool useUndo)
    {
        if (arms == null) return;
        foreach (GameObject go in arms)
        {
            if (go == null || skip.Contains(go)) continue;
            Transform pivot = NearestTransform(go, pivots);
            MechanismBuildUtil.NeutralizeToPlainTransform(go, useUndo);
            MechanismBuildUtil.DisableColliders(go, useUndo);
            PivotRotateFollower f = MechanismBuildUtil.AddOrGet<PivotRotateFollower>(go, useUndo);
            if (useUndo) Undo.RecordObject(f, UndoName);
            f.pivot = pivot;
            f.armAngleDeg = angleDeg;
            f.useControllerAxis = true;
            f.rotateBarsIndividually = arms4Bar;   // parallelogram: each bar about its own base (self-guards on big assemblies)
            if (!lift.rotators.Contains(f)) lift.rotators.Add(f);
        }
    }

    private static string WireStackCarriage(RobotMechanisms registry, Transform chassis, Dr4bLift lift, bool useUndo)
    {
        IntakePull pull = registry.GetComponentInChildren<IntakePull>(true);
        if (pull == null) return "no intake found (lift carries nothing until Add Intake is run)";

        // Interlock: intake off while raised, Score (A button) drops the stack only while raised; hide markers.
        if (useUndo) Undo.RecordObject(pull, UndoName);
        pull.lift = lift;
        pull.scoreAction = UrdfPostProcessor.LoadActionReference("A");
        pull.showRuntimeMarkers = false;

        Transform existing = FindChild(chassis, "Dr4bCarriage");
        GameObject carriage;
        if (existing != null) carriage = existing.gameObject;
        else
        {
            carriage = new GameObject("Dr4bCarriage");
            if (useUndo) Undo.RegisterCreatedObjectUndo(carriage, UndoName);
            carriage.transform.SetParent(chassis, false);
        }
        if (pull.holdPoint != null) carriage.transform.position = pull.holdPoint.position;

        Dr4bMoveFollower f = MechanismBuildUtil.AddOrGet<Dr4bMoveFollower>(carriage, useUndo);
        if (useUndo) Undo.RecordObject(f, UndoName);
        f.followsStage1 = true; f.followsStage2 = true; f.spinRatio = 0f;   // rides the scoring (top)
        if (!lift.translators.Contains(f)) lift.translators.Add(f);

        if (pull.holdPoint != null) MechanismBuildUtil.EnsureChildOf(pull.holdPoint, carriage.transform, useUndo);
        if (pull.slotAnchors != null)
            foreach (Transform a in pull.slotAnchors)
                if (a != null && a != pull.holdPoint) MechanismBuildUtil.EnsureChildOf(a, carriage.transform, useUndo);
        return "hold point + slots ride the Dr4bCarriage";
    }

    // The hub's button bindings, so a Build with auto-assign OFF can carry them across the
    // internal Clean (which wipes every lift binding). PlayerPrefs-backed, so order vs. the
    // scene edits doesn't matter.
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

    private static RobotMechanisms ResolveRegistry(Options o)
    {
        foreach (var list in new[] { o.firstStageMove, o.scoring, o.secondArmPivots, o.firstArms, o.secondArms, o.firstArmPivots })
        {
            if (list == null) continue;
            foreach (GameObject g in list)
                if (g != null) { RobotMechanisms r = g.GetComponentInParent<RobotMechanisms>(); if (r != null) return r; }
        }
        return null;
    }

    private static Transform NearestTransform(GameObject go, List<GameObject> candidates)
    {
        if (candidates == null) return null;
        Transform best = null; float bestSq = float.MaxValue;
        Vector3 p = go.transform.position;
        foreach (GameObject c in candidates)
        {
            if (c == null || c == go) continue;
            float d = (c.transform.position - p).sqrMagnitude;
            if (d < bestSq) { bestSq = d; best = c.transform; }
        }
        return best;
    }

    private static Transform FindChild(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    private static Vector3 DrivetrainLateralWorld(Transform chassis, RobotMechanisms registry)
    {
        RobotMotorController mc = registry.GetComponentInChildren<RobotMotorController>(true);
        if (mc == null) return chassis.right;
        Vector3 lat = Centroid(mc.rightWheels) - Centroid(mc.leftWheels);
        return lat.sqrMagnitude > 1e-6f ? lat.normalized : chassis.right;
    }

    private static Vector3 Centroid(ArticulationBody[] arr)
    {
        if (arr == null) return Vector3.zero;
        Vector3 s = Vector3.zero; int n = 0;
        foreach (ArticulationBody a in arr) if (a != null) { s += a.transform.position; n++; }
        return n > 0 ? s / n : Vector3.zero;
    }
}
