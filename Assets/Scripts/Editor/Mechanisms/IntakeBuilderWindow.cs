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
// What it builds (all parented to the chassis, the robot's non-spinning root — NOT the roller
// link, which spins and would whirl them around):
//   • IntakeMouth     — a trigger box (the grab zone) carrying the IntakePull behavior,
//   • IntakeHoldPoint — slot 0, where a captured piece seats,
//   • IntakeSlot1..n  — the rest of the stack.
// Rotating a hold/slot marker rotates how the piece in that slot SITS — tilt the marker and the
// piece tilts, twist it and the piece twists (IntakePull seats pieces in the anchor's frame).
//
// The intake joint/motor must already exist (Add or Fix Mechanism Joint / Auto-Detect); this only
// adds the grabbing behavior, riding the button the roller already uses.
public class IntakeBuilderWindow : EditorWindow
{
    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Build Intake", false, 20)]
    private static void Open() => GetWindow<IntakeBuilderWindow>("Build Intake");

    private MotorActuator motor;
    private int maxHeld = 3;
    private float slotSpacing = 1.5f;
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
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.HelpBox(
            "Adds a pull-force intake to an existing intake mechanism (the roller link with a " +
            "MotorActuator). Hold the mechanism's button to grab pieces at the mouth; they glide " +
            "to the hold point and stack; reverse spits them back out.\n\n" +
            "After building, position everything in the Scene view: shrink the yellow IntakeMouth " +
            "box onto the opening, drag the hold point and slots where the stack should sit — and " +
            "ROTATE them to set how each piece sits (tilt the marker, the piece tilts with it).",
            MessageType.Info);

        motor = (MotorActuator)EditorGUILayout.ObjectField(
            new GUIContent("Intake Motor", "The roller link's MotorActuator — the thing that " +
                "already spins when you hold its button. If it has no motor yet, rig the joint " +
                "with Add or Fix Mechanism Joint or Auto-Detect Mechanisms first."),
            motor, typeof(MotorActuator), true);

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
        if (existing != null)
            EditorGUILayout.HelpBox(
                $"This robot already has an intake ('{existing.name}'). Build updates it in place — " +
                "markers you have already positioned stay where you put them.", MessageType.None);

        maxHeld = EditorGUILayout.IntSlider(
            new GUIContent("Max Held", "How many pieces the intake holds before it's full. One " +
                "hold/slot marker per piece."),
            maxHeld, 1, 6);
        slotSpacing = EditorGUILayout.FloatField(
            new GUIContent("Slot Spacing", "Default gap between stacked pieces in WORLD units " +
                "(world is 10x scale; a cup is ~1.6). Only seeds NEW slot markers — dragged ones " +
                "keep their place."),
            slotSpacing);

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
            if (GUILayout.Button(existing != null ? "Rebuild Intake" : "Build Intake", GUILayout.Height(28)))
            {
                IntakePull pull = IntakeSetup.Build(motor, maxHeld, slotSpacing, useUndo: true);
                Selection.activeGameObject = pull.gameObject;
                SceneView.RepaintAll();
            }
        }

        using (new EditorGUI.DisabledScope(existing == null))
        {
            if (GUILayout.Button("Remove Intake") && EditorUtility.DisplayDialog("Remove Intake",
                    "Delete the IntakeMouth (with its IntakePull), IntakeHoldPoint and IntakeSlot " +
                    "markers from this robot? The roller mechanism itself is untouched.",
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
    private const string MouthName = "IntakeMouth";
    private const string HoldName = "IntakeHoldPoint";
    private const string SlotPrefix = "IntakeSlot";

    // The motor on or around a picked object — parents first (the usual click on the roller
    // mesh), then children (a click on the robot root).
    public static MotorActuator ResolveMotor(GameObject picked)
    {
        if (picked == null) return null;
        MotorActuator m = picked.GetComponentInParent<MotorActuator>();
        return m != null ? m : picked.GetComponentInChildren<MotorActuator>();
    }

    // The existing intake this window would edit: prefer the motor's own robot, else whatever is
    // open — the Prefab Mode stage root when one is open (FindObjectsByType does not see stage
    // contents), else the loaded scenes.
    public static IntakePull FindExisting(MotorActuator motor)
    {
        if (motor != null)
        {
            Transform chassis = ResolveChassis(motor);
            IntakePull onRobot = chassis != null ? chassis.GetComponentInChildren<IntakePull>(true) : null;
            if (onRobot != null) return onRobot;
        }
        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && stage.prefabContentsRoot != null)
            return stage.prefabContentsRoot.GetComponentInChildren<IntakePull>(true);
        return Object.FindFirstObjectByType<IntakePull>(FindObjectsInactive.Include);
    }

    // Create or update the intake. Re-runnable: existing mouth/hold/slot objects are kept where
    // the user dragged them (only re-parented to the chassis if they would spin), the trigger box
    // is only seeded on first creation, and slots beyond the new Max Held are pruned.
    public static IntakePull Build(MotorActuator motor, int maxHeld, float slotSpacing, bool useUndo)
    {
        if (motor == null) throw new System.ArgumentNullException(nameof(motor));

        GameObject link = motor.gameObject;
        Transform chassis = ResolveChassis(motor);

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

        // --- Mouth (grab zone), parented to the chassis so it doesn't spin with the roller ------
        GameObject mouth = FindOrCreate(chassis, MouthName, useUndo, out bool newMouth);
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

        // --- Hold point (slot 0), also on the chassis --------------------------------------------
        GameObject holdGo = FindOrCreate(chassis, HoldName, useUndo, out bool newHold);
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

        // --- Stack slot anchors -------------------------------------------------------------------
        // Slot 0 IS the hold point; slots 1..n-1 are chassis children seeded along the stack axis
        // FROM THE HOLD POINT'S OWN FRAME, so a rotated hold point seeds a rotated stack. Existing
        // slots keep their pose; surplus ones (Max Held reduced) are deleted.
        int slots = pull.maxHeld;
        Vector3 dir = pull.stackAxis.sqrMagnitude > 1e-6f ? pull.stackAxis.normalized : Vector3.up;
        Transform[] anchors = new Transform[slots];
        anchors[0] = holdGo.transform;
        for (int i = 1; i < slots; i++)
        {
            GameObject sgo = FindOrCreate(chassis, SlotPrefix + i, useUndo, out bool newSlot);
            if (newSlot)
                sgo.transform.SetPositionAndRotation(
                    holdGo.transform.position + holdGo.transform.rotation * (dir * (i * slotSpacing)),
                    holdGo.transform.rotation);
            anchors[i] = sgo.transform;
        }
        for (int i = slots; ; i++)
        {
            Transform surplus = MechanismBuildUtil.FindChild(chassis, SlotPrefix + i);
            if (surplus == null) break;
            MechanismBuildUtil.DestroyGo(surplus, useUndo);
        }
        pull.slotAnchors = anchors;

        if (useUndo) Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(mouth);
        if (link.scene.IsValid()) EditorSceneManager.MarkSceneDirty(link.scene);
        return pull;
    }

    // Delete everything Build created; the roller mechanism itself is untouched.
    public static void Remove(IntakePull pull, bool useUndo)
    {
        if (pull == null) return;
        Transform chassis = pull.transform.parent;
        UnityEngine.SceneManagement.Scene scene = pull.gameObject.scene;

        if (pull.holdPoint != null && pull.holdPoint.gameObject != pull.gameObject)
            MechanismBuildUtil.DestroyGo(pull.holdPoint, useUndo);
        if (pull.slotAnchors != null)
            foreach (Transform a in pull.slotAnchors)
                if (a != null) MechanismBuildUtil.DestroyGo(a, useUndo);
        if (chassis != null)
            for (int i = 1; ; i++)
            {
                Transform stray = MechanismBuildUtil.FindChild(chassis, SlotPrefix + i);
                if (stray == null) break;
                MechanismBuildUtil.DestroyGo(stray, useUndo);
            }
        MechanismBuildUtil.DestroyGo(pull.transform, useUndo); // the mouth, taking IntakePull with it

        if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
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

    // Find an existing marker anywhere on the robot (it may be stuck under the roller from an
    // older run) and re-home it to the chassis, else create it there. World pose is preserved.
    private static GameObject FindOrCreate(Transform chassis, string name, bool useUndo, out bool created)
    {
        Transform t = MechanismBuildUtil.FindChild(chassis, name);
        created = t == null;
        GameObject go;
        if (created)
        {
            go = new GameObject(name);
            if (useUndo) Undo.RegisterCreatedObjectUndo(go, UndoName);
            go.transform.SetParent(chassis, true);
        }
        else
        {
            go = t.gameObject;
            if (useUndo) Undo.RegisterFullObjectHierarchyUndo(go, UndoName);
            if (go.transform.parent != chassis) MechanismBuildUtil.EnsureChildOf(go.transform, chassis, useUndo);
        }
        return go;
    }
}
