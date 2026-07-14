using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Link parts that are chained/geared/sprocketed together in real life so they move together in
// sim — chained rollers/intakes, a drivetrain's non-motor wheels, and a double-reverse-four-bar
// (DR4B). You pick the parts, mark which one is POWERED, and the rest become passive followers
// that track it (see JointCoupler). Chain isn't in CAD, so this is where you state those links.
//
// Two link types:
//   • Sprockets / chain (spin together): followers spin at the driver's rate x ratio.
//   • Linkage / 4-bar (track angle): followers track the driver's angle x ratio (use -1 to mirror
//     the opposite side). A DR4B follower must sit BELOW the driver in the hierarchy.
//
// Usage: Tools > RoboSim > Robot > Mechanisms > Link Coupled Joints. Select the chained parts in the
// Hierarchy and press "Use Selection" (it auto-picks the one that's already a motor as the driver),
// or set the driver + followers by hand. The existing-chains list at the bottom shows and removes
// what's already coupled.
public class LinkCoupledJointsWindow : EditorWindow
{
    private const string Title = "Link Coupled Joints";

    private enum AxisPreset { X, Y, Z, Custom, Auto }

    // index 0 -> Velocity, 1 -> Position (kept in sync with ModeFromIndex/IndexFromMode below).
    private static readonly string[] ModeLabels =
    {
        "Sprockets / chain — spin together",
        "Linkage / 4-bar — track angle",
    };

    [SerializeField] private GameObject driverLink;
    [SerializeField] private List<GameObject> followers = new List<GameObject>();
    [SerializeField] private JointCoupler.CoupleMode mode = JointCoupler.CoupleMode.Velocity;
    [SerializeField] private float ratio = 1f;
    [SerializeField] private float offsetDeg = 0f;
    [SerializeField] private bool showAdvancedAxis = false;
    // Only used for followers that are still plain parts (no ArticulationBody) and must be split off.
    [SerializeField] private AxisPreset followerAxisPreset = AxisPreset.Auto;
    [SerializeField] private Vector3 followerCustomAxis = Vector3.up;
    [SerializeField] private Vector3 followerAnchor = Vector3.zero;
    [SerializeField] private Vector2 scroll;

    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Link Coupled Joints", false, 3)]
    private static void ShowWindow()
    {
        LinkCoupledJointsWindow window = GetWindow<LinkCoupledJointsWindow>(Title);
        window.minSize = new Vector2(460f, 420f);
        window.Show();
    }

    private void OnEnable()
    {
        if (driverLink == null) driverLink = Selection.activeGameObject;
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.HelpBox(
            "State which parts are chained/geared together so they move as one — chain and sprockets " +
            "aren't in CAD, so tell the sim here. Mark the POWERED part; the rest follow it.\n" +
            "• Sprockets / chain: followers spin at the driver's rate x ratio (rollers, extra wheels).\n" +
            "• Linkage / 4-bar: followers track the driver's angle x ratio (use -1 to mirror a DR4B side).",
            MessageType.Info);

        // Fast path: fill driver + followers straight from the Hierarchy selection.
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("Use Selection",
                "Fill from the selected Hierarchy objects. The one that's already a motor becomes the " +
                "driver (powered); the rest become followers."), GUILayout.Height(22)))
            {
                UseSelection();
            }
            EditorGUILayout.LabelField($"{Selection.gameObjects.Length} selected", EditorStyles.miniLabel);
        }

        driverLink = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Powered (Driver)",
            "The powered joint the followers copy. Set it up as a spinning/arm motor first " +
            "(Add or Fix Mechanism Joint)."), driverLink, typeof(GameObject), true);
        if (driverLink == null)
        {
            EditorGUILayout.HelpBox("Pick the powered joint the followers should track (or Use Selection).",
                MessageType.Warning);
            EditorGUILayout.EndScrollView();
            return;
        }

        RobotMechanisms registry = driverLink.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
        {
            EditorGUILayout.HelpBox(
                "The driver is not under a set-up robot (no RobotMechanisms on the root). Run " +
                "Set Up Imported Robot first.", MessageType.Error);
            DrawExistingChains(null);
            EditorGUILayout.EndScrollView();
            return;
        }
        ArticulationBody driverBody = driverLink.GetComponent<ArticulationBody>();
        bool driverIsWheel = IsDrivetrainWheel(driverBody, registry.gameObject);
        if (driverBody == null || driverBody.jointType != ArticulationJointType.RevoluteJoint)
        {
            EditorGUILayout.HelpBox(
                $"'{driverLink.name}' isn't a revolute/continuous joint yet. Set it up as a spinning " +
                "or arm motor in Add or Fix Mechanism Joint (or a drive wheel), then couple followers to it.",
                MessageType.Error);
            DrawExistingChains(registry.gameObject);
            EditorGUILayout.EndScrollView();
            return;
        }
        if (driverIsWheel)
            EditorGUILayout.HelpBox($"'{driverLink.name}' is a drive wheel — followers coupled to it will be " +
                "removed from the drivetrain and chained to it instead (so they don't fight the motor).",
                MessageType.None);

        int modeIndex = EditorGUILayout.Popup(new GUIContent("Link type",
            "Sprockets/chain = match spin rate (rollers, extra wheels). Linkage/4-bar = track angle (DR4B)."),
            IndexFromMode(mode), ModeLabels);
        mode = ModeFromIndex(modeIndex);

        ratio = EditorGUILayout.FloatField(new GUIContent("Ratio (follower : driver)",
            "1 = same as driver; 0.5 = half; a negative value reverses (mirror). Set by sprocket/gear teeth."),
            ratio);
        if (Mathf.Approximately(ratio, 0f))
            EditorGUILayout.HelpBox("Ratio 0 means the follower won't move.", MessageType.Warning);

        if (mode == JointCoupler.CoupleMode.Position)
            offsetDeg = EditorGUILayout.FloatField(new GUIContent("Offset (degrees)",
                "Added to the tracked angle — the linkage's neutral offset when the driver is at 0."), offsetDeg);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(new GUIContent("Followers (chained to the driver)",
            "Joints (or plain parts) driven by the driver. A plain part is split into a new joint; an " +
            "existing joint keeps its axis and loses its own button (it's now coupled)."),
            EditorStyles.miniBoldLabel);
        for (int i = 0; i < followers.Count; i++)
        {
            EditorGUILayout.BeginHorizontal();
            followers[i] = (GameObject)EditorGUILayout.ObjectField(followers[i], typeof(GameObject), true);
            if (GUILayout.Button("X", GUILayout.Width(24))) { followers.RemoveAt(i); i--; EditorGUILayout.EndHorizontal(); continue; }
            EditorGUILayout.EndHorizontal();

            GameObject f = i >= 0 && i < followers.Count ? followers[i] : null;
            if (f == null) continue;
            if (f == driverLink)
                EditorGUILayout.HelpBox("A follower can't be the driver itself.", MessageType.Error);
            else if (f.GetComponentInParent<RobotMechanisms>() != registry)
                EditorGUILayout.HelpBox($"'{f.name}' is not part of the same robot as the driver.", MessageType.Error);
            else if (IsDrivetrainWheel(f.GetComponent<ArticulationBody>(), registry.gameObject))
                EditorGUILayout.HelpBox($"'{f.name}' is a drive wheel — it will be removed from the drivetrain " +
                    "and chained to the driver instead.", MessageType.None);
            else if (mode == JointCoupler.CoupleMode.Position &&
                     f.GetComponent<ArticulationBody>() != null &&
                     !JointCouplingSetup.IsArticulationDescendant(f.GetComponent<ArticulationBody>(), driverBody))
                EditorGUILayout.HelpBox($"'{f.name}' isn't below the driver in the tree. That's fine for a " +
                    "MIRROR pair (opposite motion in the same frame — e.g. claw halves). For a DR4B, where the " +
                    "follower should ADD to the driver's lift, make it a child of the driver link.",
                    MessageType.Info);
        }
        if (GUILayout.Button("Add Follower", GUILayout.Width(120))) followers.Add(null);

        EditorGUILayout.Space();
        showAdvancedAxis = EditorGUILayout.Foldout(showAdvancedAxis, "Advanced: split axis (plain parts only)");
        if (showAdvancedAxis)
        {
            followerAxisPreset = (AxisPreset)EditorGUILayout.EnumPopup(new GUIContent("Split Axis",
                "Only used for followers that are still plain parts and must be split into a new joint. Auto " +
                "guesses from geometry; already-rigged followers keep their own axis."), followerAxisPreset);
            if (followerAxisPreset == AxisPreset.Custom)
                followerCustomAxis = EditorGUILayout.Vector3Field("Custom Axis", followerCustomAxis);
            if (followerAxisPreset != AxisPreset.Auto)
                followerAnchor = EditorGUILayout.Vector3Field(new GUIContent("Split Anchor (link-local)",
                    "Pivot/spin origin for a split-off plain follower. 0 = the link origin."), followerAnchor);
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Chain Followers to Driver", GUILayout.Height(30)))
            CoupleNow(registry);

        DrawExistingChains(registry.gameObject);
        EditorGUILayout.EndScrollView();
    }

    private void CoupleNow(RobotMechanisms registry)
    {
        try
        {
            bool axisAuto = !showAdvancedAxis || followerAxisPreset == AxisPreset.Auto;
            Vector3 axis = followerAxisPreset switch
            {
                AxisPreset.X => Vector3.right,
                AxisPreset.Y => Vector3.up,
                AxisPreset.Z => Vector3.forward,
                AxisPreset.Custom => followerCustomAxis,
                _ => Vector3.up, // Auto: inferred per-follower inside Apply
            };

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Title);
            int group = Undo.GetCurrentGroup();

            // A follower must be EITHER a directly-driven drivetrain wheel OR a coupler follower,
            // never both, or the motor controller and the coupler fight over its drive target.
            int stripped = StripFollowersFromDrivetrain(registry.gameObject, followers);

            int coupled = JointCouplingSetup.Apply(driverLink, followers, mode, ratio, offsetDeg,
                axis, followerAnchor, axisAuto, useUndo: true);

            Undo.CollapseUndoOperations(group);

            string wheelNote = stripped > 0
                ? $"\n\nMoved {stripped} wheel(s) off the drivetrain — they're now chained to '{driverLink.name}'."
                : string.Empty;
            EditorUtility.DisplayDialog(Title,
                $"Coupled {coupled} follower(s) to '{driverLink.name}' ({ModeLabels[IndexFromMode(mode)]}, " +
                $"ratio {ratio}).{wheelNote}\n\nSave the scene, then Robot > Validate Robot Physics to test it.",
                "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog(Title, e.Message, "OK");
            Debug.LogException(e, driverLink);
        }
    }

    // Fills driver + followers from the Hierarchy selection. The selected object that's already a
    // motor mechanism becomes the driver; if none is, the current driver (if selected) or the first
    // selected object is used. Everything else becomes a follower.
    private void UseSelection()
    {
        GameObject[] sel = Selection.gameObjects;
        if (sel == null || sel.Length == 0) return;

        GameObject powered = null;
        foreach (GameObject go in sel)
            if (go != null && go.GetComponent<MotorActuator>() != null) { powered = go; break; }
        if (powered == null)
            powered = Array.IndexOf(sel, driverLink) >= 0 ? driverLink : sel[0];

        driverLink = powered;
        followers.Clear();
        foreach (GameObject go in sel)
            if (go != null && go != powered) followers.Add(go);
    }

    // Lists every JointCoupler under the robot so chains can be seen and removed — the first way to
    // review/undo a chain after it's been authored.
    private void DrawExistingChains(GameObject robotRoot)
    {
        EditorGUILayout.Space();
        JointCoupler[] couplers = robotRoot != null
            ? robotRoot.GetComponentsInChildren<JointCoupler>(true)
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
            EditorGUILayout.BeginHorizontal();
            string driverName = c.driver != null ? c.driver.name : "(none)";
            string followerName = c.follower != null ? c.follower.name : c.name;
            string kind = c.mode == JointCoupler.CoupleMode.Velocity ? "spin" : "linkage";
            EditorGUILayout.LabelField($"{driverName} → {followerName}  ({kind}, x{c.ratio:0.##})");
            bool remove = GUILayout.Button("Remove", GUILayout.Width(70));
            EditorGUILayout.EndHorizontal();
            if (remove)
            {
                GameObject host = c.gameObject;
                Undo.DestroyObjectImmediate(c); // follower stays a joint, just no longer tracks
                if (host != null && host.scene.IsValid()) EditorSceneManager.MarkSceneDirty(host.scene);
                break; // list changed; stop iterating the now-stale array this pass
            }
        }
    }

    // Removes any follower ArticulationBodies from the robot's RobotMotorController wheel arrays so a
    // coupled wheel isn't also directly driven. Returns how many were removed.
    private int StripFollowersFromDrivetrain(GameObject robotRoot, IList<GameObject> targets)
    {
        RobotMotorController motor = robotRoot.GetComponent<RobotMotorController>();
        if (motor == null || targets == null) return 0;

        HashSet<ArticulationBody> bodies = new HashSet<ArticulationBody>();
        foreach (GameObject go in targets)
        {
            if (go == null) continue;
            ArticulationBody b = go.GetComponent<ArticulationBody>();
            if (b != null) bodies.Add(b);
        }
        if (bodies.Count == 0) return 0;

        ArticulationBody[] newLeft = WithoutBodies(motor.leftWheels, bodies, out int leftRemoved);
        ArticulationBody[] newRight = WithoutBodies(motor.rightWheels, bodies, out int rightRemoved);
        if (leftRemoved + rightRemoved == 0) return 0;

        Undo.RecordObject(motor, Title);
        motor.leftWheels = newLeft;
        motor.rightWheels = newRight;
        EditorUtility.SetDirty(motor);
        return leftRemoved + rightRemoved;
    }

    private static ArticulationBody[] WithoutBodies(ArticulationBody[] arr, HashSet<ArticulationBody> remove, out int removed)
    {
        removed = 0;
        if (arr == null) return null;
        List<ArticulationBody> kept = new List<ArticulationBody>(arr.Length);
        foreach (ArticulationBody b in arr)
        {
            if (b != null && remove.Contains(b)) { removed++; continue; }
            kept.Add(b);
        }
        return kept.ToArray();
    }

    private static bool IsDrivetrainWheel(ArticulationBody body, GameObject robotRoot)
    {
        if (body == null || robotRoot == null) return false;
        RobotMotorController motor = robotRoot.GetComponent<RobotMotorController>();
        if (motor == null) return false;
        return Array.IndexOf(motor.leftWheels ?? Array.Empty<ArticulationBody>(), body) >= 0
            || Array.IndexOf(motor.rightWheels ?? Array.Empty<ArticulationBody>(), body) >= 0;
    }

    private static JointCoupler.CoupleMode ModeFromIndex(int i) =>
        i == 0 ? JointCoupler.CoupleMode.Velocity : JointCoupler.CoupleMode.Position;

    private static int IndexFromMode(JointCoupler.CoupleMode m) =>
        m == JointCoupler.CoupleMode.Velocity ? 0 : 1;
}

// The coupling-authoring core, split out so a headless caller can drive it without the window.
public static class JointCouplingSetup
{
    private const string UndoName = "Link Coupled Joints";

    // Couples each follower to the driver joint. A plain-part follower is split into a bare revolute
    // joint (reusing AddMechanismJoint.ConfigureJointLink); an existing revolute follower keeps its
    // axis/anchors and only has its DOF/limits adjusted for the mode. Every follower then loses its
    // own actuator + registry entry (it's a passive linkage, not a button mechanism) and gains a
    // JointCoupler pointing at the driver. Returns the number of followers coupled. Throws on any
    // precondition failure. useUndo=false for headless/batch callers.
    public static int Apply(GameObject driverLink, IList<GameObject> followers,
        JointCoupler.CoupleMode mode, float ratio, float offsetDeg,
        Vector3 followerAxis, Vector3 followerAnchor, bool axisAuto, bool useUndo)
    {
        if (driverLink == null) throw new ArgumentNullException(nameof(driverLink));

        RobotMechanisms registry = driverLink.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
            throw new InvalidOperationException(
                $"'{driverLink.name}' is not under a set-up robot (no RobotMechanisms). Run " +
                "Set Up Imported Robot first.");
        GameObject root = registry.gameObject;

        ArticulationBody driver = driverLink.GetComponent<ArticulationBody>();
        if (driver == null)
            throw new InvalidOperationException(
                $"'{driverLink.name}' isn't a joint yet. Set it up as a revolute/continuous motor " +
                "(Add or Fix Mechanism Joint) before coupling followers to it.");
        if (driver.jointType != ArticulationJointType.RevoluteJoint)
            throw new InvalidOperationException(
                $"Driver '{driverLink.name}' must be a revolute/continuous joint, not {driver.jointType}.");
        if (Mathf.Approximately(ratio, 0f))
            Debug.LogWarning("JointCouplingSetup: ratio is 0 — the follower won't move.", driver);
        if (mode == JointCoupler.CoupleMode.Position && driver.twistLock == ArticulationDofLock.FreeMotion)
            Debug.LogWarning($"JointCouplingSetup: driver '{driverLink.name}' spins freely (Continuous), so a " +
                "Linkage follower has no fixed angle to track. Use Spin Together, or make the driver a limited " +
                "arm joint.", driver);

        // Follower joint kind by mode: velocity followers spin freely (Continuous, no travel limit),
        // position followers are limited revolutes spanning the commanded arc.
        AddMechanismJoint.JointType jt = mode == JointCoupler.CoupleMode.Velocity
            ? AddMechanismJoint.JointType.Continuous : AddMechanismJoint.JointType.Revolute;
        ComputeFollowerLimits(driver, mode, ratio, offsetDeg, out float lo, out float hi);

        int coupled = 0;
        if (followers != null)
        {
            foreach (GameObject follower in followers)
            {
                if (follower == null || follower == driverLink) continue;
                if (follower.GetComponentInParent<RobotMechanisms>() != registry)
                    throw new InvalidOperationException(
                        $"'{follower.name}' is not part of the same robot as the driver.");
                if (driverLink.transform.IsChildOf(follower.transform))
                    throw new InvalidOperationException(
                        $"'{follower.name}' is ABOVE the driver in the hierarchy — pick the driven part, " +
                        "not the thing that drives it.");

                ArticulationBody fb = follower.GetComponent<ArticulationBody>();
                if (fb == null)
                {
                    // Plain part: split a new bare joint off the chassis (mass, anchors, DOF locks).
                    Vector3 axis = followerAxis, anchor = followerAnchor;
                    if (axisAuto) MechanismAutoDetect.TryInferAxisAnchor(follower, jt, out axis, out anchor);
                    fb = AddMechanismJoint.ConfigureJointLink(follower, jt, axis, anchor, lo, hi, default, registry, useUndo);
                }
                else
                {
                    // Existing joint: keep its axis/anchors, just set the DOF + limits for the mode.
                    if (fb.jointType != ArticulationJointType.RevoluteJoint)
                        throw new InvalidOperationException(
                            $"Follower '{follower.name}' is already a {fb.jointType} joint — coupling needs a " +
                            "revolute. Re-author it as a spinning/arm motor first, or add the plain part instead.");
                    if (useUndo) Undo.RecordObject(fb, UndoName);
                    if (mode == JointCoupler.CoupleMode.Velocity)
                    {
                        fb.twistLock = ArticulationDofLock.FreeMotion; // spin freely
                    }
                    else
                    {
                        fb.twistLock = ArticulationDofLock.LimitedMotion;
                        ArticulationDrive d = fb.xDrive;
                        d.lowerLimit = lo;
                        d.upperLimit = hi;
                        fb.xDrive = d;
                    }
                }

                // It's coupled, not player-driven: strip any actuator + registry/catalog entry so it
                // never shows in Configure Controller and ButtonRouter can't fight the coupler. Strip
                // any old coupler too, so re-applying doesn't stack (JointCoupler disallows duplicates).
                foreach (MotorActuator m in follower.GetComponents<MotorActuator>()) DestroyComponent(m, useUndo);
                foreach (PneumaticActuator p in follower.GetComponents<PneumaticActuator>()) DestroyComponent(p, useUndo);
                foreach (JointCoupler old in follower.GetComponents<JointCoupler>()) DestroyComponent(old, useUndo);
                UrdfPostProcessor.RemoveMechanism(registry, UrdfPostProcessor.Slugify(follower.name), useUndo);

                if (mode == JointCoupler.CoupleMode.Position && !IsArticulationDescendant(fb, driver))
                    Debug.LogWarning($"'{follower.name}' is not below driver '{driverLink.name}' in the tree. " +
                        "Fine for a MIRROR pair (opposite motion, same frame); but a DR4B follower must be a " +
                        "child of the driver link to ADD to its lift.", follower);

                JointCoupler coupler = AddComponent<JointCoupler>(follower, useUndo);
                coupler.follower = fb;
                coupler.driver = driver;
                coupler.mode = mode;
                coupler.ratio = ratio;
                coupler.offsetDeg = offsetDeg;
                coupler.BakeDrive();

                // Seat the initial position target at the current linkage pose so it doesn't snap on
                // the first physics step. (jointPosition is empty at edit time -> falls back to offset.)
                if (mode == JointCoupler.CoupleMode.Position)
                {
                    ArticulationReducedSpace dp = driver.jointPosition;
                    float seat = dp.dofCount > 0 ? dp[0] * Mathf.Rad2Deg * ratio + offsetDeg : offsetDeg;
                    fb.SetDriveTarget(ArticulationDriveAxis.X, seat);
                }

                EditorUtility.SetDirty(fb);
                EditorUtility.SetDirty(coupler);
                coupled++;
            }
        }

        // Followers were removed from the registry — refresh the home-screen catalog to match.
        UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, root.name, registry);
        EditorUtility.SetDirty(registry);
        if (root.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.scene);
        return coupled;
    }

    // Follower travel limits: Continuous (velocity) ignores them; a Position follower must span the
    // driver's arc x ratio (+ offset), padded, so the target drive doesn't clamp the linkage.
    private static void ComputeFollowerLimits(ArticulationBody driver, JointCoupler.CoupleMode mode,
        float ratio, float offsetDeg, out float lo, out float hi)
    {
        if (mode == JointCoupler.CoupleMode.Velocity) { lo = 0f; hi = 0f; return; }
        ArticulationDrive dd = driver.xDrive;
        float a = dd.lowerLimit * ratio + offsetDeg;
        float b = dd.upperLimit * ratio + offsetDeg;
        lo = Mathf.Min(a, b) - 5f;
        hi = Mathf.Max(a, b) + 5f;
    }

    // True if `driver` is an ancestor ArticulationBody of `fb` — required for a Position/Linkage
    // follower so its rotation compounds on the driver's. Walks the full ancestor chain (a robot is a
    // shallow tree) rather than trusting ArticulationBody.isRoot, which is unreliable at edit time.
    internal static bool IsArticulationDescendant(ArticulationBody fb, ArticulationBody driver)
    {
        if (fb == null || driver == null) return false;
        for (Transform p = fb.transform.parent; p != null; p = p.parent)
        {
            if (p.GetComponent<ArticulationBody>() == driver) return true;
        }
        return false;
    }

    private static T AddComponent<T>(GameObject go, bool useUndo) where T : Component
        => useUndo ? Undo.AddComponent<T>(go) : go.AddComponent<T>();

    private static void DestroyComponent(Component c, bool useUndo)
    {
        if (c == null) return;
        if (useUndo) Undo.DestroyObjectImmediate(c);
        else UnityEngine.Object.DestroyImmediate(c);
    }
}
