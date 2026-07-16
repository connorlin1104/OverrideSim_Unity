using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Builds a LIFT that visibly expands the robot AND carries the held intake stack up with it — the
// next mechanism after the intake. A real Double-Reverse-Four-Bar (DR4B) is a CLOSED kinematic loop;
// ArticulationBody is a strict TREE and can't represent one, so we fake the linkage the way the rest
// of this project does: ONE powered revolute DRIVER bar + JointCoupler Position followers that force
// the child joint angles to track the driver (see JointCoupler / Link Coupled Joints). The tray/
// end-effector (the highest link) is marked LiftCarriage and the intake's hold point + stack slots are
// re-parented onto it, so the kinematic-glide stack rides up for free (see IntakePull) while the intake
// MOUTH stays at the base (you scoop low; only the stored stack lifts).
//
// Two flavors, same tool:
//   • Full coupled DR4B (default): real coupled revolute joints. The bars are jointed bodies; the tray
//     tracks the driver with ratio -1 (single stage) so it stays level while rising. Highest fidelity,
//     but ratios/axes/anchors are geometry-dependent and need a tuning pass.
//   • Articulated cheat (checkbox): only the driver is a real joint; the tray + bars are transform-only
//     LinkageBarFollowers. The tray is translated straight up (dead-level), parented under the chassis
//     so the stack rides it with NO LiftCarriage exemption needed. Same scissor look, ~1 number to tune.
//
// Either way the extra scissor bars are cosmetic LinkageBarFollowers (no real joints to fight/jitter).
//
// Usage: set up the robot + intake first, then Tools > RoboSim > Robot > Mechanisms > Build DR4B Lift.
public class AddLiftWindow : EditorWindow
{
    private const string Title = "Build DR4B Lift";

    private enum AxisPreset { X, Y, Z, Custom, Auto }

    [SerializeField] private GameObject driverNode;
    [SerializeField] private GameObject trayNode;
    [SerializeField] private bool doubleStage = false;
    [SerializeField] private GameObject midBarNode;
    [SerializeField] private List<GameObject> cosmeticBars = new List<GameObject>();

    [SerializeField] private AxisPreset driverAxisPreset = AxisPreset.Auto;
    [SerializeField] private Vector3 driverCustomAxis = Vector3.right;
    [SerializeField] private float lowerDeg = 0f;
    [SerializeField] private float upperDeg = 70f;

    [SerializeField] private float trayRatio = -1f;
    [SerializeField] private float trayOffsetDeg = 0f;
    [SerializeField] private float cosmeticRatio = 1f;

    [SerializeField] private bool articulatedCheat = true; // safe default: no stiff coupled drives to destabilize
    [SerializeField] private float liftHeightUnits = 20f; // world units the tray rises at full sweep (Flavor B)

    [SerializeField] private bool reverseDriver = false;
    [SerializeField] private float holdFriction = 8f;
    [SerializeField] private bool autoAssignButtons = true;

    [SerializeField] private Vector2 scroll;

    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Build DR4B Lift", false, 21)]
    private static void ShowWindow()
    {
        AddLiftWindow window = GetWindow<AddLiftWindow>(Title);
        window.minSize = new Vector2(460f, 520f);
        window.Show();
    }

    private void OnEnable()
    {
        if (driverNode == null) driverNode = Selection.activeGameObject;
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.HelpBox(
            "Builds a lift that expands the robot and carries the held stack up. Pick the powered DRIVER " +
            "bar (pivots at the tower base) and the TRAY (the top link the intake scores from). A DR4B is " +
            "a closed loop that ArticulationBody can't model, so the linkage is faked: the driver is a real " +
            "motor, the tray tracks it (ratio -1 keeps it level), and extra scissor bars are cosmetic. The " +
            "intake mouth stays at the base; only the stored stack rides the tray.\n\n" +
            "Set up the robot (Set Up Imported Robot) and its intake (Add Intake) first.",
            MessageType.Info);

        driverNode = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Driver bar (powered)",
            "The lower DR4B bar that pivots at the tower base. Becomes a hold-to-run arm motor."),
            driverNode, typeof(GameObject), true);
        trayNode = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Tray / end-effector",
            "The top link that reaches highest — the plate/basket the intake scores from. The stack rides here."),
            trayNode, typeof(GameObject), true);

        if (driverNode == null || trayNode == null)
        {
            EditorGUILayout.HelpBox("Pick both the driver bar and the tray link.", MessageType.Warning);
            EditorGUILayout.EndScrollView();
            return;
        }
        RobotMechanisms registry = driverNode.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
        {
            EditorGUILayout.HelpBox(
                "The driver is not under a set-up robot (no RobotMechanisms on the root). Run " +
                "Set Up Imported Robot first.", MessageType.Error);
            EditorGUILayout.EndScrollView();
            return;
        }
        if (registry.GetComponentInChildren<IntakePull>(true) == null)
            EditorGUILayout.HelpBox(
                "No intake (IntakePull) found on this robot. The lift will still expand, but it won't carry " +
                "a stack until you run Add Intake (Pull-Force).", MessageType.Warning);

        EditorGUILayout.Space();
        doubleStage = EditorGUILayout.Toggle(new GUIContent("Double stage",
            "Two reverse stages for more height. Adds a middle bar (ratio +1) with the tray at ratio -2."),
            doubleStage);
        if (doubleStage)
        {
            midBarNode = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Middle bar",
                "The intermediate bar between the driver and the tray."), midBarNode, typeof(GameObject), true);
            if (Mathf.Approximately(trayRatio, -1f)) trayRatio = -2f; // sensible default for double stage
        }

        EditorGUILayout.Space();
        driverAxisPreset = (AxisPreset)EditorGUILayout.EnumPopup(new GUIContent("Driver axis (link-local)",
            "Auto guesses the hinge axis from geometry. Verify it in Add or Fix Mechanism Joint if the bar " +
            "swings in the wrong plane."), driverAxisPreset);
        if (driverAxisPreset == AxisPreset.Custom)
            driverCustomAxis = EditorGUILayout.Vector3Field("Custom Axis", driverCustomAxis);

        EditorGUILayout.LabelField("Driver sweep (degrees)", EditorStyles.miniBoldLabel);
        lowerDeg = EditorGUILayout.FloatField("Down (rest)", lowerDeg);
        upperDeg = EditorGUILayout.FloatField("Up (raised)", upperDeg);

        using (new EditorGUI.DisabledScope(articulatedCheat))
        {
            EditorGUILayout.LabelField("Tray coupling (full DR4B)", EditorStyles.miniBoldLabel);
            trayRatio = EditorGUILayout.FloatField(new GUIContent("Tray ratio",
                "follower : driver. -1 keeps a single-stage tray level; -2 for a double stage."), trayRatio);
            trayOffsetDeg = EditorGUILayout.FloatField(new GUIContent("Tray offset (deg)",
                "The tray's resting tilt. Adjust if it's level but sits at the wrong angle."), trayOffsetDeg);
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(new GUIContent("Cosmetic scissor bars (optional)",
            "The extra visible bars that complete the DR4B look. Driven as transform-only followers — no " +
            "real joints, so they can't jitter or fight physics. Their colliders are disabled."),
            EditorStyles.miniBoldLabel);
        for (int i = 0; i < cosmeticBars.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            cosmeticBars[i] = (GameObject)EditorGUILayout.ObjectField(cosmeticBars[i], typeof(GameObject), true);
            if (GUILayout.Button("X", GUILayout.Width(24))) { cosmeticBars.RemoveAt(i); i--; }
            EditorGUILayout.EndHorizontal();
        }
        if (GUILayout.Button("Add Bar", GUILayout.Width(100))) cosmeticBars.Add(null);
        cosmeticRatio = EditorGUILayout.FloatField(new GUIContent("Cosmetic bar ratio",
            "Default follow ratio for the bars above (+1 swings with the base, -1 mirrors). Tune per bar " +
            "on each LinkageBarFollower afterward."), cosmeticRatio);

        EditorGUILayout.Space();
        articulatedCheat = EditorGUILayout.Toggle(new GUIContent("Articulated cheat (recommended)",
            "ON (default): no coupled joints — the tray + bars are transform-only followers, the tray rises " +
            "dead-straight and stays level, the stack rides it, and there are NO stiff drives to destabilize. " +
            "OFF: real coupled DR4B joints (higher fidelity, but needs mass/ratio tuning — a stiff drive on a " +
            "light bar can go unstable)."), articulatedCheat);
        if (articulatedCheat)
            liftHeightUnits = EditorGUILayout.FloatField(new GUIContent("Tray rise (world units)",
                "How far the tray translates up at full driver sweep (1 unit = 0.1 m)."), liftHeightUnits);
        else
            EditorGUILayout.HelpBox(
                "Full coupled DR4B uses stiff position springs. The tool now forces a minimum bar mass and a " +
                "softer spring so it stays stable, but if the bars still shake, switch the cheat back on.",
                MessageType.Warning);

        EditorGUILayout.Space();
        reverseDriver = EditorGUILayout.Toggle(new GUIContent("Reverse driver",
            "Flip if the UP button lowers the lift."), reverseDriver);
        holdFriction = EditorGUILayout.FloatField(new GUIContent("Hold friction",
            "Joint friction so the lift self-holds when released. Raise if it sags; if it then won't rise, " +
            "raise the motor's stall torque."), holdFriction);
        autoAssignButtons = EditorGUILayout.Toggle(new GUIContent("Auto-assign buttons",
            "Map the lift to the next free up/down button pair (the intake holds R1/R2, so the lift lands on L1/L2)."),
            autoAssignButtons);

        EditorGUILayout.Space();
        if (GUILayout.Button(articulatedCheat ? "Build Lift (articulated cheat)" : "Build Lift (full DR4B)",
            GUILayout.Height(32)))
        {
            try
            {
                var opts = new LiftSetup.Options
                {
                    driverNode = driverNode,
                    trayNode = trayNode,
                    doubleStage = doubleStage,
                    midBarNode = midBarNode,
                    cosmeticBars = cosmeticBars,
                    cosmeticRatio = cosmeticRatio,
                    lowerDeg = lowerDeg,
                    upperDeg = upperDeg,
                    driverAxisAuto = driverAxisPreset == AxisPreset.Auto,
                    driverAxis = driverAxisPreset switch
                    {
                        AxisPreset.X => Vector3.right,
                        AxisPreset.Y => Vector3.up,
                        AxisPreset.Z => Vector3.forward,
                        AxisPreset.Custom => driverCustomAxis,
                        _ => Vector3.right,
                    },
                    trayRatio = trayRatio,
                    trayOffsetDeg = trayOffsetDeg,
                    articulatedCheat = articulatedCheat,
                    liftHeightUnits = liftHeightUnits,
                    reverseDriver = reverseDriver,
                    holdFriction = holdFriction,
                    autoAssignButtons = autoAssignButtons,
                };
                string report = LiftSetup.Build(opts, useUndo: true);
                EditorUtility.DisplayDialog(Title, report, "OK");
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog(Title, e.Message, "OK");
                Debug.LogException(e, driverNode);
            }
        }

        EditorGUILayout.EndScrollView();
    }
}

// The lift-building core, split out so a headless caller (or a validator) can drive it without the UI.
// Orchestrates the existing authoring cores: AddMechanismJoint.Apply (driver arm), JointCouplingSetup
// .Apply (tray follower), MechanismAutoDetect (axis inference + button assignment).
public static class LiftSetup
{
    private const string UndoName = "Build DR4B Lift";

    // Stability: mass-from-geometry on thin DR4B bars comes out near-zero (~0.01 kg). A stiff position
    // spring on a near-massless body has a natural frequency the physics step can't integrate, so it
    // explodes numerically (violent shake + solver thrash that pins the CPU). Force a sane minimum bar
    // mass and use a much softer, well-damped coupler spring than JointCoupler's default 20000/500.
    private const float MinLiftMass = 1.5f;      // kg in this scaled world (a game piece is ~1)
    private const float SafeStiffness = 4000f;   // vs JointCoupler default 20000 — stable at MinLiftMass
    private const float SafeDamping = 600f;      // overdamped at MinLiftMass, so it can't oscillate

    public struct Options
    {
        public GameObject driverNode;
        public GameObject trayNode;
        public bool doubleStage;
        public GameObject midBarNode;
        public List<GameObject> cosmeticBars;
        public float cosmeticRatio;
        public float lowerDeg, upperDeg;
        public Vector3 driverAxis;    // link-local; used when !driverAxisAuto
        public bool driverAxisAuto;
        public float trayRatio, trayOffsetDeg;
        public bool articulatedCheat; // Flavor B
        public float liftHeightUnits; // Flavor B tray rise at full sweep
        public bool reverseDriver;
        public float holdFriction;
        public bool autoAssignButtons;
    }

    public static string Build(Options o, bool useUndo)
    {
        if (o.driverNode == null) throw new ArgumentNullException(nameof(o.driverNode));
        if (o.trayNode == null) throw new ArgumentNullException(nameof(o.trayNode));
        if (o.driverNode == o.trayNode)
            throw new InvalidOperationException("The driver bar and the tray must be different parts.");
        if (Mathf.Approximately(o.lowerDeg, o.upperDeg))
            throw new InvalidOperationException("Driver sweep is zero — set different Down/Up angles.");

        RobotMechanisms registry = o.driverNode.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
            throw new InvalidOperationException(
                $"'{o.driverNode.name}' is not under a set-up robot (no RobotMechanisms). Run Set Up Imported Robot first.");
        if (o.trayNode.GetComponentInParent<RobotMechanisms>() != registry)
            throw new InvalidOperationException("The tray is not part of the same robot as the driver.");
        if (o.doubleStage && o.midBarNode == null)
            throw new InvalidOperationException("Double stage needs a Middle bar.");

        int group = 0;
        if (useUndo)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            group = Undo.GetCurrentGroup();
        }

        // 1) Driver bar -> hold-to-run arm motor.
        Vector3 dAxis = o.driverAxis;
        Vector3 dAnchor = Vector3.zero;
        if (o.driverAxisAuto)
            MechanismAutoDetect.TryInferAxisAnchor(o.driverNode, AddMechanismJoint.JointType.Revolute, out dAxis, out dAnchor);

        var driverOpts = new AddMechanismJoint.Options
        {
            actuation = AddMechanismJoint.Actuation.HoldToRun,
            reverseDirection = o.reverseDriver,
        };
        AddMechanismJoint.Apply(o.driverNode, AddMechanismJoint.JointType.Revolute,
            dAxis, dAnchor, o.lowerDeg, o.upperDeg, driverOpts, useUndo);

        ArticulationBody driverBody = o.driverNode.GetComponent<ArticulationBody>();
        if (driverBody != null)
        {
            if (useUndo) Undo.RecordObject(driverBody, UndoName);
            if (o.holdFriction > 0f) driverBody.jointFriction = o.holdFriction;
            // Thin bars weigh ~grams from geometry — too light for a stable driven joint. Floor it.
            if (driverBody.mass < MinLiftMass) driverBody.mass = MinLiftMass;
        }

        string driverId = UrdfPostProcessor.Slugify(o.driverNode.name);
        string flavor;

        if (!o.articulatedCheat)
        {
            flavor = BuildCoupledTray(o, registry, useUndo);
        }
        else
        {
            flavor = BuildCheatTray(o, registry, driverBody, useUndo);
        }

        // Cosmetic scissor bars: transform-only followers of the driver.
        int cosmetic = AddCosmeticBars(o, driverBody, useUndo);

        // Buttons: an up/down pair on the driver (Revolute -> forward/reverse). Drop any bindings the
        // driver already has FIRST — AssignButtons always grabs the next FREE pair, so without this a
        // re-run piles on a second/third pair (the 654V lift ended up on BOTH L1/L2 and the arrow keys).
        string buttonNote = "not assigned";
        if (o.autoAssignButtons)
        {
            ClearMechanismBindings(registry.robotId, driverId);
            buttonNote = MechanismAutoDetect.AssignButtons(registry.robotId, driverId,
                AddMechanismJoint.JointType.Revolute);
        }

        if (useUndo) Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(registry);
        if (registry.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(registry.gameObject.scene);

        return
            $"Built a {flavor} lift on '{registry.name}'.\n\n" +
            $"• Driver '{o.driverNode.name}' → hold-to-run arm motor, sweep {o.lowerDeg}..{o.upperDeg}°.\n" +
            $"• Buttons: {buttonNote}.\n" +
            $"• Tray '{o.trayNode.name}' carries the stack; the intake mouth stays at the base.\n" +
            $"• Cosmetic scissor bars wired: {cosmetic}.\n\n" +
            "IMPORTANT: the field spawns the robot PREFAB at Play, not this scene object. APPLY THESE " +
            "CHANGES TO THE PREFAB (Overrides > Apply All, or edit in Prefab Mode) or they won't spawn.\n\n" +
            "Play, intake pieces, then hold the UP button: the bars scissor, the tray + stack rise. Tuning:\n" +
            "• Nothing moves at all → the UP button isn't driving the joint (check the binding), or the\n" +
            "  driver axis is wrong (fix it in Add or Fix Mechanism Joint). The one driver bar should visibly turn.\n" +
            "• Up lowers → Reverse driver.\n" +
            (o.articulatedCheat
                ? "• Tray rises too far / too little → adjust Tray rise (world units).\n" +
                  "• A bar scissors the wrong way → flip that bar's LinkageBarFollower ratio sign.\n" +
                  "• For the highest fidelity you can later uncheck 'Articulated cheat' — it's now mass/spring-hardened."
                : "• Tray tilts as it rises → tray ratio isn't exactly -1 (single) / -2 (double).\n" +
                  "• Sags when released → raise Hold friction (then stall torque if it won't rise).\n" +
                  "• Bars shake / CPU spikes → the coupled path is unstable on these masses; switch 'Articulated cheat' ON.");
    }

    // Flavor A: real coupled DR4B. Nest the tray (and mid bar) under the driver so rotations compound,
    // then couple them Position-mode to the driver via the existing Link Coupled Joints core.
    private static string BuildCoupledTray(Options o, RobotMechanisms registry, bool useUndo)
    {
        // If we're re-running over a prior cheat build, drop the transform-only followers first.
        RemoveComponents<LinkageBarFollower>(o.trayNode, useUndo);
        if (o.doubleStage) RemoveComponents<LinkageBarFollower>(o.midBarNode, useUndo);

        if (o.doubleStage)
        {
            // mid bar under driver (ratio +1), tray under mid bar (ratio trayRatio, default -2 vs driver).
            EnsureChildOf(o.midBarNode.transform, o.driverNode.transform, useUndo);
            EnsureChildOf(o.trayNode.transform, o.midBarNode.transform, useUndo);
            JointCouplingSetup.Apply(o.driverNode, new List<GameObject> { o.midBarNode },
                JointCoupler.CoupleMode.Position, 1f, 0f, Vector3.up, Vector3.zero, axisAuto: true, useUndo: useUndo);
            JointCouplingSetup.Apply(o.driverNode, new List<GameObject> { o.trayNode },
                JointCoupler.CoupleMode.Position, o.trayRatio, o.trayOffsetDeg, Vector3.up, Vector3.zero, axisAuto: true, useUndo: useUndo);
            StabilizeCoupledLink(o.midBarNode, useUndo);
        }
        else
        {
            EnsureChildOf(o.trayNode.transform, o.driverNode.transform, useUndo);
            JointCouplingSetup.Apply(o.driverNode, new List<GameObject> { o.trayNode },
                JointCoupler.CoupleMode.Position, o.trayRatio, o.trayOffsetDeg, Vector3.up, Vector3.zero, axisAuto: true, useUndo: useUndo);
        }
        StabilizeCoupledLink(o.trayNode, useUndo);

        // Mark the tray as the carriage the stack rides, and re-parent the stack anchors onto it.
        MarkCarriage(o.trayNode, useUndo);
        ReparentStackAnchors(registry, o.trayNode.transform, useUndo);
        return "full coupled DR4B";
    }

    // Floor a coupled link's mass and soften its JointCoupler position spring, so a near-massless bar
    // + a stiff spring can't blow up the solver (the CPU-melting shake). Re-bakes the drive.
    private static void StabilizeCoupledLink(GameObject link, bool useUndo)
    {
        ArticulationBody body = link.GetComponent<ArticulationBody>();
        if (body != null && body.mass < MinLiftMass)
        {
            if (useUndo) Undo.RecordObject(body, UndoName);
            body.mass = MinLiftMass;
        }
        JointCoupler coupler = link.GetComponent<JointCoupler>();
        if (coupler != null)
        {
            if (useUndo) Undo.RecordObject(coupler, UndoName);
            coupler.positionStiffness = SafeStiffness;
            coupler.positionDamping = SafeDamping;
            coupler.BakeDrive();
        }
    }

    // Flavor B: the articulated cheat. Only the driver is a real joint; the tray becomes a transform-
    // only follower parented under the CHASSIS, translated straight up. The stack rides it with no
    // LiftCarriage exemption needed (no moving ArticulationBody sits between the anchors and the chassis).
    private static string BuildCheatTray(Options o, RobotMechanisms registry, ArticulationBody driverBody, bool useUndo)
    {
        // Turn the tray back into a plain, script-movable transform. If a prior FULL-DR4B run left a
        // coupled ArticulationBody + stiff JointCoupler here, it must be REMOVED — otherwise the position
        // spring keeps fighting (the CPU-melting shake) and a LinkageBarFollower can't move an AB link's
        // transform (PhysX owns it). This is what makes switching a broken coupled build over to the cheat safe.
        NeutralizeToPlainTransform(o.trayNode, useUndo);

        Transform chassis = registry.transform;
        EnsureChildOf(o.trayNode.transform, chassis, useUndo);
        DisableColliders(o.trayNode, useUndo); // cosmetic: script-moved, must not act on physics

        float sweepRad = Mathf.Abs(o.upperDeg - o.lowerDeg) * Mathf.Deg2Rad;
        float unitsPerRad = sweepRad > 1e-4f ? o.liftHeightUnits / sweepRad : 0f;

        LinkageBarFollower f = AddOrGetFollower(o.trayNode, useUndo);
        if (useUndo) Undo.RecordObject(f, UndoName);
        f.driver = driverBody;
        f.mode = LinkageBarFollower.FollowMode.TranslateAlongWorldAxis;
        f.worldAxis = Vector3.up;
        f.ratio = 1f;
        f.unitsPerRadian = unitsPerRad;

        ReparentStackAnchors(registry, o.trayNode.transform, useUndo);
        return "articulated-cheat";
    }

    // Cosmetic scissor bars: each gets a transform-only rotate follower of the driver, its pivot axis
    // inferred from geometry, and its colliders disabled. Skips the driver/tray/mid bar and nulls.
    private static int AddCosmeticBars(Options o, ArticulationBody driverBody, bool useUndo)
    {
        if (o.cosmeticBars == null) return 0;
        int n = 0;
        foreach (GameObject bar in o.cosmeticBars)
        {
            if (bar == null || bar == o.driverNode || bar == o.trayNode || bar == o.midBarNode) continue;
            MechanismAutoDetect.TryInferAxisAnchor(bar, AddMechanismJoint.JointType.Revolute,
                out Vector3 axis, out Vector3 _);

            LinkageBarFollower f = AddOrGetFollower(bar, useUndo);
            if (useUndo) Undo.RecordObject(f, UndoName);
            f.driver = driverBody;
            f.mode = LinkageBarFollower.FollowMode.RotateAboutAxis;
            f.ratio = o.cosmeticRatio;
            f.localPivotAxis = axis.sqrMagnitude > 1e-8f ? axis : Vector3.right;

            DisableColliders(bar, useUndo);
            n++;
        }
        return n;
    }

    private static void MarkCarriage(GameObject tray, bool useUndo)
    {
        LiftCarriage lc = tray.GetComponent<LiftCarriage>();
        if (lc == null) lc = useUndo ? Undo.AddComponent<LiftCarriage>(tray) : tray.AddComponent<LiftCarriage>();
        if (useUndo) Undo.RecordObject(lc, UndoName);
        lc.body = tray.GetComponent<ArticulationBody>();
    }

    // Re-parent the intake's hold point + stack slot anchors onto the tray so the held stack rides up.
    // The MOUTH (the IntakePull object) is left where it is — the intake stays at the base.
    private static void ReparentStackAnchors(RobotMechanisms registry, Transform tray, bool useUndo)
    {
        IntakePull pull = registry.GetComponentInChildren<IntakePull>(true);
        if (pull == null) return;

        if (pull.holdPoint != null) EnsureChildOf(pull.holdPoint, tray, useUndo);
        if (pull.slotAnchors != null)
            foreach (Transform a in pull.slotAnchors)
                if (a != null && a != pull.holdPoint) EnsureChildOf(a, tray, useUndo);
    }

    private static LinkageBarFollower AddOrGetFollower(GameObject go, bool useUndo)
    {
        LinkageBarFollower f = go.GetComponent<LinkageBarFollower>();
        if (f == null) f = useUndo ? Undo.AddComponent<LinkageBarFollower>(go) : go.AddComponent<LinkageBarFollower>();
        return f;
    }

    private static void EnsureChildOf(Transform t, Transform parent, bool useUndo)
    {
        if (t == null || parent == null || t.parent == parent || t == parent) return;
        if (useUndo) Undo.SetTransformParent(t, parent, UndoName); // keeps world pose
        else t.SetParent(parent, true);
    }

    private static void DisableColliders(GameObject go, bool useUndo)
    {
        foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
        {
            if (c == null || !c.enabled) continue;
            if (useUndo) Undo.RecordObject(c, UndoName);
            c.enabled = false;
        }
    }

    // Strip a prior coupled-lift build off a node so it becomes an inert transform the cheat can move:
    // the JointCoupler (its stiff position spring is the instability), the LiftCarriage marker, and the
    // ArticulationBody itself (a follower's transform can't be script-driven while PhysX owns it). Order
    // matters — coupler/marker before the body. Safe when none are present.
    private static void NeutralizeToPlainTransform(GameObject go, bool useUndo)
    {
        RemoveComponents<JointCoupler>(go, useUndo);
        RemoveComponents<LiftCarriage>(go, useUndo);
        ArticulationBody body = go.GetComponent<ArticulationBody>();
        if (body != null)
        {
            if (useUndo) Undo.DestroyObjectImmediate(body);
            else UnityEngine.Object.DestroyImmediate(body);
        }
    }

    private static void RemoveComponents<T>(GameObject go, bool useUndo) where T : Component
    {
        if (go == null) return;
        foreach (T c in go.GetComponents<T>())
        {
            if (c == null) continue;
            if (useUndo) Undo.DestroyObjectImmediate(c);
            else UnityEngine.Object.DestroyImmediate(c);
        }
    }

    // Drop every button binding a mechanism currently holds, so re-assigning gives a single clean pair
    // instead of stacking another one each run (map.assignments is a public flat list keyed by id).
    private static void ClearMechanismBindings(string robotId, string mechanismId)
    {
        ButtonMap map = ControllerMapSettings.Load(robotId);
        if (map == null || map.assignments == null) return;
        int removed = map.assignments.RemoveAll(a => a != null && a.mechanismId == mechanismId);
        if (removed > 0) ControllerMapSettings.Save(robotId, map);
    }
}
