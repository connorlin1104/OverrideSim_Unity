using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.Rendering;

// Role-assignment builder for the pull-force intake, shaped like the other mechanism builders
// (Claw / Cascade / DR4B / Pneumatic): a window with the fields and persistent help, a live
// Scene-view preview with drag AND rotate handles on the hold point and slots, prefab-mode
// support, re-open-to-edit, and a Remove path. It replaces a bare menu item whose entire
// documentation was one 17-line modal dialog.
//
// What it builds:
//   • IntakeMouth     — a trigger box (the grab zone) carrying the IntakePull behavior,
//   • IntakeHoldPoint — slot 0, where a captured piece seats,
//   • IntakeSlot1..n  — the rest of the stack.
//
// WHERE they go: alongside the roller link, under whatever the roller is mounted on. They must not
// be children of the roller ITSELF — that spins, and would whirl them around — but they used to be
// dumped on the robot ROOT instead, which put five loose empties at the top of a 1200-node hierarchy
// next to the chassis. One step up from the roller is as close as they can get while still being
// still, it keeps the CAD's own structure readable, and it means an intake mounted on a lift stage
// has its hold points ride the lift for free.
// Rotating a hold/slot marker rotates how the piece in that slot SITS — tilt the marker and the
// piece tilts, twist it and the piece twists (IntakePull seats pieces in the anchor's frame).
//
// The intake joint/motor must already exist (Add or Fix Mechanism Joint / Auto-Detect); this only
// adds the grabbing behavior, riding the button the roller already uses.
//
// ONE ROBOT, SEVERAL INTAKES. A bot that intakes at the floor and SCORES from an arm or a chain wants
// two of these: one grabbing intake, one carrier that reverses to drop what it holds (Reverse Drops In
// Place). So the tool is keyed on the MOTOR, not on the robot: each motor gets its own IntakePull, and
// intake 2 numbers its whole marker set (Intake2Mouth, Intake2HoldPoint, Intake2Slot1, ...) so the two
// can never fight over the same five names. Markers are resolved through the component's own
// references, never by name alone — a name search from the robot root is exactly how a second build
// would have stolen (and a second Remove deleted) the first intake's markers. The two are then linked by
// the HANDOFF (Take From Other Intakes): the scoring intake's mouth goes over where the floor intake
// holds its stack, and holding its button takes the piece across. It has to be explicit, because a
// carried piece has its colliders off and trips no trigger — see IntakePull's header.
public class IntakeBuilderWindow : EditorWindow
{
    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Build Intake", false, 25)]
    private static void Open() => GetWindow<IntakeBuilderWindow>("Build Intake");

    private MotorActuator motor;
    private int maxHeld = 3;
    private float slotSpacing = 1.5f;
    private bool reverseDropsInPlace;
    private bool takeFromOtherIntakes = true;
    private bool rotateHandles;
    private Vector2 scroll;

    // The intake being edited, resolved fresh each layout pass — the window never caches scene
    // objects across builds, so undo/redo and external edits can't leave it stale.
    private IntakePull Existing => IntakeSetup.FindExisting(motor);

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        if (motor == null && Selection.activeGameObject != null)
            motor = IntakeSetup.ResolveMotor(Selection.activeGameObject);
        AdoptExisting();
    }

    private void OnDisable() => SceneView.duringSceneGui -= OnSceneGUI;

    // Re-opening the window on a robot that already has an intake shows that intake's numbers
    // instead of defaults, so "edit" and "create" are the same flow.
    private void AdoptExisting()
    {
        IntakePull pull = Existing;
        if (pull == null) return;
        maxHeld = Mathf.Max(1, pull.maxHeld);
        slotSpacing = pull.slotSpacing;
        reverseDropsInPlace = pull.reverseDropsInPlace;
        takeFromOtherIntakes = pull.takeFromOtherIntakes;
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.HelpBox(
            "Adds a pull-force intake to an existing mechanism (a link with a MotorActuator). Hold " +
            "the mechanism's button to grab pieces at the mouth; they glide to the hold point and " +
            "stack; reverse gets rid of them — thrown out of the mouth, or simply DROPPED where they " +
            "sit if you tick Reverse Drops In Place.\n\n" +
            "After building, position everything in the Scene view: shrink the yellow IntakeMouth " +
            "box onto the opening, drag the hold point and slots where the stack should sit — and " +
            "ROTATE them to set how each piece sits (tilt the marker, the piece tilts with it).\n\n" +
            "One robot can have several: build one on the intake roller and another on the scoring " +
            "mechanism. Each is keyed to its own motor, so each rides its own button — and the second " +
            "can take pieces straight out of the first, as long as its mouth covers where the first one " +
            "holds its stack.",
            MessageType.Info);

        // Switching motors re-reads that motor's intake, so the fields below can never describe one
        // intake while Build writes them onto another.
        EditorGUI.BeginChangeCheck();
        motor = (MotorActuator)EditorGUILayout.ObjectField(
            new GUIContent("Intake Motor", "The roller link's MotorActuator — the thing that " +
                "already spins when you hold its button. If it has no motor yet, rig the joint " +
                "with Add or Fix Mechanism Joint or Auto-Detect Mechanisms first."),
            motor, typeof(MotorActuator), true);
        if (EditorGUI.EndChangeCheck()) AdoptExisting();

        if (motor == null && Selection.activeGameObject != null)
        {
            MotorActuator fromSelection = IntakeSetup.ResolveMotor(Selection.activeGameObject);
            if (fromSelection != null && GUILayout.Button($"Use '{fromSelection.name}' from the selection"))
            {
                motor = fromSelection;
                AdoptExisting();
            }
        }

        IntakePull existing = Existing;
        int others = IntakeSetup.AllOnRobot(motor).Length - (existing != null ? 1 : 0);
        if (existing != null)
            EditorGUILayout.HelpBox(
                $"This motor already has an intake ('{existing.name}'). Build updates it in place — " +
                "markers you have already positioned stay where you put them." +
                (others > 0 ? $" The robot's {others} other intake(s) are left alone." : ""),
                MessageType.None);
        else if (motor != null && others > 0)
            EditorGUILayout.HelpBox(
                $"This robot already has {others} intake(s), on other mechanisms. Build adds ANOTHER " +
                "one for this motor, with its own numbered markers (Intake2Mouth, Intake2HoldPoint, " +
                "...) — that is how a bot gets a grabbing intake AND a separate scoring mechanism. " +
                "The existing intakes are not touched.", MessageType.Info);

        maxHeld = EditorGUILayout.IntSlider(
            new GUIContent("Max Held", "How many pieces the intake holds before it's full. One " +
                "hold/slot marker per piece."),
            maxHeld, 1, 6);
        slotSpacing = EditorGUILayout.FloatField(
            new GUIContent("Slot Spacing", "Default gap between stacked pieces in WORLD units " +
                "(world is 10x scale; a cup is ~1.6). Only seeds NEW slot markers — dragged ones " +
                "keep their place."),
            slotSpacing);
        reverseDropsInPlace = EditorGUILayout.Toggle(
            new GUIContent("Reverse Drops In Place",
                "ON: reverse just LETS GO — each held piece turns back into a physical object exactly " +
                "where it sits and gravity does the rest, the way a real scoring mechanism dumps " +
                "(reverse, and the cup/pin falls out). Nothing is launched, nothing loose is shoved. " +
                "This is what you want for a basket or claw carried on an arm or a chain.\n\n" +
                "OFF: pieces are thrown out through the mouth — right for a roller intake that has to " +
                "spit them clear of itself."),
            reverseDropsInPlace);
        takeFromOtherIntakes = EditorGUILayout.Toggle(
            new GUIContent("Take From Other Intakes",
                "ON (default): this intake can take a piece straight out of ANOTHER intake on the " +
                "robot. Hold this one's button with its mouth over where the other one carries its " +
                "stack and the piece is handed across — that is how the floor intake loads the " +
                "scoring mechanism. It has to be explicit: a carried piece is kinematic with its " +
                "colliders off, so it trips no trigger and this intake is otherwise blind to it.\n\n" +
                "OFF: this intake only picks up loose pieces off the field."),
            takeFromOtherIntakes);

        rotateHandles = GUILayout.Toggle(rotateHandles,
            new GUIContent(rotateHandles ? "Scene handles: ROTATE" : "Scene handles: MOVE",
                "What the Scene-view handles on the hold point and slots do. Rotating a marker " +
                "sets how the piece in that slot sits."),
            "Button");

        // The one mistake this tool used to let happen silently: dragging markers on the SCENE
        // instance and wondering why Play looked the same — the field scene spawns the PREFAB.
        if (existing != null && PrefabStageUtility.GetCurrentPrefabStage() == null &&
            PrefabUtility.IsPartOfPrefabInstance(existing.gameObject))
            EditorGUILayout.HelpBox(
                "You are editing a scene INSTANCE. The field scene spawns the robot PREFAB, so " +
                "after positioning the mouth/hold/slots, apply the changes to the prefab " +
                "(Overrides > Apply All) — or edit in Prefab Mode instead.", MessageType.Warning);

        using (new EditorGUI.DisabledScope(motor == null))
        {
            if (GUILayout.Button(existing != null ? "Rebuild Intake"
                    : others > 0 ? "Build a Second Intake" : "Build Intake", GUILayout.Height(28)))
            {
                IntakePull pull = IntakeSetup.Build(motor, maxHeld, slotSpacing, reverseDropsInPlace,
                    takeFromOtherIntakes, useUndo: true);
                Selection.activeGameObject = pull.gameObject;
                SceneView.RepaintAll();
            }
        }

        using (new EditorGUI.DisabledScope(existing == null))
        {
            if (GUILayout.Button("Remove Intake") && EditorUtility.DisplayDialog("Remove Intake",
                    $"Delete '{(existing != null ? existing.name : "IntakeMouth")}' (with its " +
                    "IntakePull), its hold point and its slot markers? The mechanism itself is " +
                    "untouched, and so is any other intake on this robot.",
                    "Remove", "Cancel"))
            {
                IntakeSetup.Remove(Existing, useUndo: true);
                SceneView.RepaintAll();
            }
        }

        EditorGUILayout.EndScrollView();
    }

    // --- Scene-view preview ----------------------------------------------------------------------

    // Same deal as the claw builder's hold point: bare empties can't be clicked in the Scene view
    // and spend their lives buried inside the CAD, so the window draws them through the plastic
    // (zTest Always) and hands over handles. The arrow on each marker is the direction the piece
    // in that slot STANDS — rotating the marker moves the arrow AND the piece.
    private void OnSceneGUI(SceneView view)
    {
        IntakePull pull = Existing;
        if (pull == null) return;

        CompareFunction wasZTest = Handles.zTest;
        Handles.zTest = CompareFunction.Always;

        // The mouth trigger, in its own (scaled) local space.
        if (pull.GetComponent<Collider>() is BoxCollider box)
        {
            using (new Handles.DrawingScope(new Color(1f, 0.85f, 0.15f, 0.9f),
                       box.transform.localToWorldMatrix))
                Handles.DrawWireCube(box.center, box.size);
            Handles.color = Color.white;
            Handles.Label(box.transform.TransformPoint(box.center), "IntakeMouth (grab zone)");
        }

        Transform hold = pull.holdPoint;
        if (hold != null)
        {
            Handles.color = new Color(0.15f, 0.95f, 1f);
            Handles.DrawDottedLine(pull.transform.position, hold.position, 4f);
        }

        Transform[] anchors = pull.slotAnchors;
        int slots = Mathf.Max(1, pull.maxHeld);
        for (int i = 0; i < slots; i++)
        {
            Transform anchor = anchors != null && i < anchors.Length ? anchors[i] : null;
            if (anchor == null) continue;

            float handle = HandleUtility.GetHandleSize(anchor.position);
            Handles.color = i == 0 ? new Color(0.15f, 0.95f, 1f) : new Color(0.15f, 0.95f, 1f, 0.6f);
            Handles.SphereHandleCap(0, anchor.position, Quaternion.identity,
                handle * (i == 0 ? 0.25f : 0.16f), EventType.Repaint);

            // Which way a piece in this slot stands — the anchor's up, which the seating math
            // (IntakePull.CarryTo) actually uses, so what the arrow promises is what Play does.
            Vector3 up = anchor.rotation * Vector3.up;
            Handles.ArrowHandleCap(0, anchor.position, Quaternion.LookRotation(up), handle * 0.9f,
                EventType.Repaint);
            Handles.Label(anchor.position + up * handle,
                i == 0 ? "Hold point (pieces stand along this)" : $"Slot {i}");

            EditorGUI.BeginChangeCheck();
            if (rotateHandles)
            {
                Quaternion turned = Handles.RotationHandle(anchor.rotation, anchor.position);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(anchor, "Rotate intake slot");
                    anchor.rotation = turned;
                }
            }
            else
            {
                Vector3 moved = Handles.PositionHandle(anchor.position, anchor.rotation);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(anchor, "Move intake slot");
                    anchor.position = moved;
                }
            }
        }

        Handles.zTest = wasZTest;
    }
}

// The build/remove logic, separated from the window so the validators can drive it headlessly
// (useUndo: false), the same contract as ClawSetup / CascadeSetup / Dr4bLiftSetup.
internal static class IntakeSetup
{
    private const string UndoName = "Build Intake";
    private const string MouthRole = "Mouth";
    private const string HoldRole = "HoldPoint";
    private const string SlotRole = "Slot";
    private const int MaxSlotSweep = 12;   // slots are capped at 6 in the window; sweep well past it

    // A marker's name. The robot's FIRST intake keeps the historical names ("IntakeMouth",
    // "IntakeHoldPoint", "IntakeSlot1"), so no robot that already has one is renamed; a second intake
    // numbers its whole set ("Intake2Mouth", "Intake2Slot1"). Names are only used to CREATE markers and
    // to sweep leftovers — anything that already exists is found through the component's references.
    public static string MarkerName(int index, string role) =>
        index <= 1 ? "Intake" + role : $"Intake{index}{role}";

    // Which numbered set an intake belongs to, read back off its mouth's name (1 when the name says
    // nothing, including a mouth the user renamed).
    public static int IndexOf(IntakePull pull)
    {
        string name = pull != null ? pull.name : null;
        if (string.IsNullOrEmpty(name) || !name.StartsWith("Intake")) return 1;
        int digits = 0, i = "Intake".Length;
        while (i < name.Length && char.IsDigit(name[i])) { digits = digits * 10 + (name[i] - '0'); i++; }
        return digits >= 2 ? digits : 1;
    }

    // The motor on or around a picked object — parents first (the usual click on the roller
    // mesh), then children (a click on the robot root).
    public static MotorActuator ResolveMotor(GameObject picked)
    {
        if (picked == null) return null;
        MotorActuator m = picked.GetComponentInParent<MotorActuator>();
        return m != null ? m : picked.GetComponentInChildren<MotorActuator>();
    }

    // Every intake on this motor's robot. The window shows the count so "Build" can say whether it
    // updates an intake or adds another one.
    public static IntakePull[] AllOnRobot(MotorActuator motor)
    {
        if (motor == null) return new IntakePull[0];
        Transform chassis = ResolveChassis(motor);
        return chassis != null ? chassis.GetComponentsInChildren<IntakePull>(true) : new IntakePull[0];
    }

    // The intake this window edits — THIS MOTOR'S, not "the robot's". A bot can carry an intake and a
    // separate scoring mechanism, and returning the wrong one meant Build re-pointed the first intake
    // at the second motor instead of creating anything.
    public static IntakePull FindExisting(MotorActuator motor)
    {
        if (motor != null)
        {
            IntakePull[] onRobot = AllOnRobot(motor);
            foreach (IntakePull p in onRobot)
                if (p != null && p.intakeMotor == motor) return p;
            // Nobody's: hand-added, or built before this window set the field. Adopt it rather than
            // building a duplicate on top of it.
            foreach (IntakePull p in onRobot)
                if (p != null && p.intakeMotor == null) return p;
            // Everything on this robot belongs to another motor → this motor gets its own intake.
            return null;
        }
        // No motor picked yet: show whatever is open, so the Scene preview works before the first click.
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && stage.prefabContentsRoot != null)
            return stage.prefabContentsRoot.GetComponentInChildren<IntakePull>(true);
        return Object.FindAnyObjectByType<IntakePull>(FindObjectsInactive.Include);
    }

    // Every marker the robot's OTHER intakes own. Build must never adopt one of these by name and
    // Remove must never delete one: with two intakes on a robot, a name sweep from the root would
    // happily hand intake 2 the markers belonging to intake 1 — and then delete them.
    public static HashSet<Transform> ClaimedByOthers(Transform chassis, IntakePull mine)
    {
        var claimed = new HashSet<Transform>();
        if (chassis == null) return claimed;
        foreach (IntakePull other in chassis.GetComponentsInChildren<IntakePull>(true))
        {
            if (other == null || other == mine) continue;
            claimed.Add(other.transform);
            if (other.holdPoint != null) claimed.Add(other.holdPoint);
            if (other.slotAnchors == null) continue;
            foreach (Transform a in other.slotAnchors) if (a != null) claimed.Add(a);
        }
        return claimed;
    }

    // Create or update THIS MOTOR'S intake. Re-runnable: existing mouth/hold/slot objects are kept
    // where the user dragged them (only re-parented if they would spin with the roller), the trigger
    // box is only seeded on first creation, and slots beyond the new Max Held are pruned. Another
    // intake on the same robot is never read, moved, adopted or deleted.
    public static IntakePull Build(MotorActuator motor, int maxHeld, float slotSpacing,
        bool reverseDropsInPlace, bool takeFromOtherIntakes, bool useUndo)
    {
        if (motor == null) throw new System.ArgumentNullException(nameof(motor));

        GameObject link = motor.gameObject;
        Transform chassis = ResolveChassis(motor);
        // Where new markers are created, and where a spinning one is rescued to. Searching still
        // starts at the robot root, so markers an older build left up there are found and re-homed.
        Transform mount = MarkerMount(link.transform, chassis);

        // What this build is allowed to touch: its own intake (by motor), its own numbered marker
        // names, and nothing another IntakePull already holds a reference to.
        IntakePull existing = FindExisting(motor);
        HashSet<Transform> claimed = ClaimedByOthers(chassis, existing);
        int index = existing != null ? IndexOf(existing) : FirstFreeIndex(chassis);
        Transform[] previousSlots = existing != null ? existing.slotAnchors : null;

        // World bounds of the intake link's meshes → default mouth size + hold-point guess.
        Bounds bounds;
        Renderer[] rends = link.GetComponentsInChildren<Renderer>();
        if (rends.Length > 0)
        {
            bounds = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) bounds.Encapsulate(rends[i].bounds);
        }
        else bounds = new Bounds(link.transform.position, Vector3.one);

        int group = 0;
        if (useUndo)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);
            group = Undo.GetCurrentGroup();
        }

        // --- Mouth (grab zone), mounted beside the roller so it does not spin with it ------------
        GameObject mouth = ResolveMarker(existing != null ? existing.transform : null, chassis, mount,
            link.transform, MarkerName(index, MouthRole), claimed, useUndo, out bool newMouth);
        if (newMouth)
            mouth.transform.SetPositionAndRotation(bounds.center, Quaternion.identity);

        BoxCollider box = mouth.GetComponent<BoxCollider>();
        bool newBox = box == null;
        if (newBox) box = useUndo ? Undo.AddComponent<BoxCollider>(mouth) : mouth.AddComponent<BoxCollider>();
        box.isTrigger = true;
        // Only seed default size/center on first creation — a re-run must not wipe a box the user
        // has already sized onto the opening.
        if (newBox)
        {
            box.center = Vector3.zero;
            Vector3 lossy = mouth.transform.lossyScale;
            Vector3 world = Vector3.Max(bounds.size, Vector3.one * 0.2f);
            box.size = new Vector3(world.x / MechanismBuildUtil.Nz(lossy.x),
                world.y / MechanismBuildUtil.Nz(lossy.y), world.z / MechanismBuildUtil.Nz(lossy.z));
        }

        // --- Hold point (slot 0), on the same mount ----------------------------------------------
        GameObject holdGo = ResolveMarker(existing != null ? existing.holdPoint : null, chassis, mount,
            link.transform, MarkerName(index, HoldRole), claimed, useUndo, out bool newHold);
        if (newHold)
            holdGo.transform.SetPositionAndRotation(
                bounds.center + Vector3.up * (bounds.size.y * 0.5f + 0.5f), Quaternion.identity);

        // --- Behavior -----------------------------------------------------------------------------
        IntakePull pull = mouth.GetComponent<IntakePull>();
        if (pull == null) pull = useUndo ? Undo.AddComponent<IntakePull>(mouth) : mouth.AddComponent<IntakePull>();
        if (useUndo) Undo.RecordObject(pull, UndoName);
        pull.intakeMotor = motor;
        pull.holdPoint = holdGo.transform;
        pull.maxHeld = Mathf.Max(1, maxHeld);
        pull.slotSpacing = slotSpacing;
        pull.reverseDropsInPlace = reverseDropsInPlace;
        pull.takeFromOtherIntakes = takeFromOtherIntakes;

        // --- Stack slot anchors -------------------------------------------------------------------
        // Slot 0 IS the hold point; slots 1..n-1 sit on the same mount, seeded along the stack axis
        // FROM THE HOLD POINT'S OWN FRAME, so a rotated hold point seeds a rotated stack. Existing
        // slots keep their pose; surplus ones (Max Held reduced) are deleted.
        int slots = pull.maxHeld;
        Vector3 dir = pull.stackAxis.sqrMagnitude > 1e-6f ? pull.stackAxis.normalized : Vector3.up;
        Transform[] anchors = new Transform[slots];
        anchors[0] = holdGo.transform;
        for (int i = 1; i < slots; i++)
        {
            Transform known = previousSlots != null && i < previousSlots.Length ? previousSlots[i] : null;
            // A hand-edited list that names the hold point (or the mouth) twice must not turn slot 0
            // into slot i — that would later prune the hold point as surplus.
            if (known == holdGo.transform || known == mouth.transform) known = null;
            GameObject sgo = ResolveMarker(known, chassis, mount, link.transform,
                MarkerName(index, SlotRole + i), claimed, useUndo, out bool newSlot);
            if (newSlot)
                sgo.transform.SetPositionAndRotation(
                    holdGo.transform.position + holdGo.transform.rotation * (dir * (i * slotSpacing)),
                    holdGo.transform.rotation);
            anchors[i] = sgo.transform;
        }

        // Slots beyond the new Max Held: the intake's OWN surplus anchors first (they are authoritative
        // even if renamed), then any leftover carrying one of this set's names. Nothing another intake
        // holds is ever deleted, and the name sweep does not stop at the first gap.
        if (previousSlots != null)
            for (int i = slots; i < previousSlots.Length; i++)
            {
                Transform surplus = previousSlots[i];
                if (surplus != null && surplus != holdGo.transform && surplus != mouth.transform &&
                    !claimed.Contains(surplus))
                    MechanismBuildUtil.DestroyGo(surplus, useUndo);
            }
        for (int i = slots; i <= MaxSlotSweep; i++)
        {
            Transform surplus = FindUnclaimed(chassis, MarkerName(index, SlotRole + i), claimed);
            if (surplus != null) MechanismBuildUtil.DestroyGo(surplus, useUndo);
        }
        pull.slotAnchors = anchors;

        if (useUndo) Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(mouth);
        if (link.scene.IsValid()) EditorSceneManager.MarkSceneDirty(link.scene);
        return pull;
    }

    // Delete everything Build created FOR THIS INTAKE; the mechanism itself, and any other intake on
    // the robot, are untouched.
    public static void Remove(IntakePull pull, bool useUndo)
    {
        if (pull == null) return;
        // Strays are swept from the ROBOT ROOT, not from the mouth's parent: markers built before they
        // moved next to the roller are still sitting up there, and a Remove that misses them leaves
        // IntakeSlot2 orphaned on the robot forever. But only this set's names, and only objects no
        // other IntakePull claims — the old sweep hit "IntakeSlot1" by name and so would have deleted
        // the first intake's stack while removing the second.
        RobotMechanisms registry = pull.GetComponentInParent<RobotMechanisms>();
        Transform chassis = registry != null ? registry.transform : pull.transform.root;
        HashSet<Transform> claimed = ClaimedByOthers(chassis, pull);
        int index = IndexOf(pull);
        UnityEngine.SceneManagement.Scene scene = pull.gameObject.scene;

        if (pull.holdPoint != null && pull.holdPoint.gameObject != pull.gameObject &&
            !claimed.Contains(pull.holdPoint))
            MechanismBuildUtil.DestroyGo(pull.holdPoint, useUndo);
        if (pull.slotAnchors != null)
            foreach (Transform a in pull.slotAnchors)
                if (a != null && a != pull.transform && !claimed.Contains(a))
                    MechanismBuildUtil.DestroyGo(a, useUndo);
        if (chassis != null)
        {
            Transform strayHold = FindUnclaimed(chassis, MarkerName(index, HoldRole), claimed);
            if (strayHold != null && strayHold != pull.transform)
                MechanismBuildUtil.DestroyGo(strayHold, useUndo);
            for (int i = 1; i <= MaxSlotSweep; i++)
            {
                Transform stray = FindUnclaimed(chassis, MarkerName(index, SlotRole + i), claimed);
                if (stray != null && stray != pull.transform) MechanismBuildUtil.DestroyGo(stray, useUndo);
            }
        }
        MechanismBuildUtil.DestroyGo(pull.transform, useUndo); // the mouth, taking IntakePull with it

        if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
    }

    // Where the mouth/hold/slot markers live: one step up from the roller, so they sit with the
    // intake in the CAD's own structure instead of loose on the robot root, and ride whatever the
    // intake is bolted to. Falls back to the chassis for a roller that has no parent (or one parked
    // outside the robot).
    public static Transform MarkerMount(Transform rollerLink, Transform chassis)
    {
        Transform parent = rollerLink != null ? rollerLink.parent : null;
        if (parent == null) return chassis;
        // Only inside the robot: a roller dragged out from under the chassis would otherwise scatter
        // markers into whatever else is in the scene. (IsChildOf is true for the chassis itself.)
        return chassis != null && !parent.IsChildOf(chassis) ? chassis : parent;
    }

    // The robot's non-spinning frame: the RobotMechanisms holder (lives on the root), else the
    // root ArticulationBody, else the top of the hierarchy.
    public static Transform ResolveChassis(MotorActuator motor)
    {
        RobotMechanisms rm = motor.GetComponentInParent<RobotMechanisms>();
        if (rm != null) return rm.transform;
        foreach (ArticulationBody ab in motor.GetComponentsInParent<ArticulationBody>(true))
            if (ab.isRoot) return ab.transform;
        return motor.transform.root;
    }

    // Resolve one marker, in priority order:
    //   1. the reference the intake already holds — authoritative, because names are cosmetic and the
    //      user may have renamed anything (this is also what keeps two intakes from swapping markers),
    //   2. an object of that name anywhere on the robot that no OTHER intake claims — how markers an
    //      older build left on the robot root get re-homed instead of duplicated,
    //   3. a new empty on `mount`.
    // World pose is preserved in every case.
    //
    // An existing marker is re-homed ONLY if it sits inside the roller link, where it would spin.
    // Anywhere else is somebody's decision — the DR4B builder deliberately re-parents these onto its
    // carriage so the stack rides the lift, and a rebuild that dragged them back would silently undo
    // that. (The old code re-homed anything whose parent wasn't the chassis, which did exactly that.)
    private static GameObject ResolveMarker(Transform known, Transform searchRoot, Transform mount,
        Transform rollerLink, string name, HashSet<Transform> claimed, bool useUndo, out bool created)
    {
        Transform t = known != null ? known : FindUnclaimed(searchRoot, name, claimed);
        created = t == null;
        GameObject go;
        if (created)
        {
            go = new GameObject(name);
            if (useUndo) Undo.RegisterCreatedObjectUndo(go, UndoName);
            go.transform.SetParent(mount, true);
        }
        else
        {
            go = t.gameObject;
            if (useUndo) Undo.RegisterFullObjectHierarchyUndo(go, UndoName);
            if (rollerLink != null && go.transform.IsChildOf(rollerLink))
                MechanismBuildUtil.EnsureChildOf(go.transform, mount, useUndo);
        }
        return go;
    }

    // MechanismBuildUtil.FindChild, minus anything another intake owns.
    private static Transform FindUnclaimed(Transform root, string name, HashSet<Transform> claimed)
    {
        if (root == null) return null;
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name && (claimed == null || !claimed.Contains(t))) return t;
        return null;
    }

    // The lowest marker-set number free on this robot: 1 for a robot's first intake, so it keeps the
    // historical names; 2 for the scoring mechanism built next to it.
    private static int FirstFreeIndex(Transform chassis)
    {
        for (int n = 1; n <= 99; n++)
            if (MechanismBuildUtil.FindChild(chassis, MarkerName(n, MouthRole)) == null) return n;
        return 1;
    }
}
