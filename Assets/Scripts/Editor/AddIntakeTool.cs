using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Adds a pull-force intake to an existing intake mechanism. Creates two objects ON THE CHASSIS (the
// robot's non-spinning root) — NOT on the roller link, which spins and would whirl them (and the hold
// point) around:
//   • IntakeMouth     — a trigger box (the grab zone; keep it small, at the opening),
//   • IntakeHoldPoint — where captured pieces are pulled to (drag it up inside the bot),
// plus an IntakePull that reads the intake's MotorActuator. The intake joint/motor must already exist
// (Add or Fix Mechanism Joint / Auto-Detect); this only adds the grabbing behavior, riding the button
// the roller already uses.
//
// Usage: select the intake mechanism (the roller link with a MotorActuator), then
// Tools > RoboSim > Robot > Mechanisms > Add Intake (Pull-Force). Then, in the Scene view: shrink the
// IntakeMouth box onto the opening and drag IntakeHoldPoint to where pieces should end up. Re-runnable:
// it finds an existing mouth/hold-point anywhere on the robot and re-homes it to the chassis (so an
// old setup that was stuck on the spinning roller is migrated in place).
public static class AddIntakeTool
{
    private const string UndoName = "Add Pull-Force Intake";
    private const string MouthName = "IntakeMouth";
    private const string HoldName = "IntakeHoldPoint";

    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Add Intake (Pull-Force)", false, 20)]
    private static void AddIntake()
    {
        GameObject sel = Selection.activeGameObject;
        MotorActuator motor = sel != null ? sel.GetComponentInParent<MotorActuator>() : null;
        if (motor == null && sel != null) motor = sel.GetComponentInChildren<MotorActuator>();
        if (motor == null)
        {
            EditorUtility.DisplayDialog(UndoName,
                "Select the intake mechanism first — the roller link that has a MotorActuator (the " +
                "thing that already spins when you hold its button).\n\nIf it has no motor yet, rig the " +
                "joint with Add or Fix Mechanism Joint or Auto-Detect Mechanisms, then run this.",
                "OK");
            return;
        }

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

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName(UndoName);
        int group = Undo.GetCurrentGroup();

        // --- Mouth (grab zone), parented to the chassis so it doesn't spin with the roller ---------
        Transform mouthT = FindDescendant(chassis, MouthName);
        GameObject mouth;
        bool newMouth = mouthT == null;
        if (newMouth)
        {
            mouth = new GameObject(MouthName);
            Undo.RegisterCreatedObjectUndo(mouth, UndoName);
        }
        else
        {
            mouth = mouthT.gameObject;
            Undo.RegisterFullObjectHierarchyUndo(mouth, UndoName);
        }
        if (mouth.transform.parent != chassis)
            Undo.SetTransformParent(mouth.transform, chassis, UndoName); // keeps world pose
        if (newMouth)
            mouth.transform.SetPositionAndRotation(bounds.center, Quaternion.identity);

        BoxCollider box = mouth.GetComponent<BoxCollider>();
        bool newBox = box == null;
        if (newBox) box = Undo.AddComponent<BoxCollider>(mouth);
        box.isTrigger = true;
        // Only seed default size/center on first creation — a re-run must not wipe a box the user has
        // already sized onto the opening.
        if (newBox)
        {
            box.center = Vector3.zero;
            Vector3 lossy = mouth.transform.lossyScale;
            Vector3 world = Vector3.Max(bounds.size, Vector3.one * 0.2f);
            box.size = new Vector3(world.x / Nz(lossy.x), world.y / Nz(lossy.y), world.z / Nz(lossy.z));
        }

        // --- Hold point (destination), also on the chassis ----------------------------------------
        Transform holdT = FindDescendant(chassis, HoldName);
        GameObject holdGo;
        bool newHold = holdT == null;
        if (newHold)
        {
            holdGo = new GameObject(HoldName);
            Undo.RegisterCreatedObjectUndo(holdGo, UndoName);
        }
        else
        {
            holdGo = holdT.gameObject;
            Undo.RegisterFullObjectHierarchyUndo(holdGo, UndoName);
        }
        if (holdGo.transform.parent != chassis)
            Undo.SetTransformParent(holdGo.transform, chassis, UndoName);
        if (newHold)
            holdGo.transform.SetPositionAndRotation(
                bounds.center + Vector3.up * (bounds.size.y * 0.5f + 0.5f), Quaternion.identity);

        // --- Behavior -----------------------------------------------------------------------------
        IntakePull pull = mouth.GetComponent<IntakePull>();
        if (pull == null) pull = Undo.AddComponent<IntakePull>(mouth);
        Undo.RecordObject(pull, UndoName);
        pull.intakeMotor = motor;
        pull.holdPoint = holdGo.transform;

        Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(mouth);
        EditorSceneManager.MarkSceneDirty(link.scene);
        Selection.activeGameObject = mouth;

        EditorUtility.DisplayDialog(UndoName,
            $"Added a pull-force intake, anchored to the chassis '{chassis.name}' so it no longer spins " +
            "with the roller. Two objects (both on the chassis now):\n\n" +
            "• IntakeMouth — the grab zone. Shrink it onto the intake opening.\n" +
            "• IntakeHoldPoint — where pieces end up. Drag it up inside the bot.\n\n" +
            "IMPORTANT: the field scene spawns the robot PREFAB at Play, not this scene object. After you " +
            "position the mouth and hold point, APPLY THE CHANGES TO THE PREFAB (Overrides > Apply All, or " +
            "edit in Prefab Mode) — otherwise the hold point you dragged won't be the one that spawns.\n\n" +
            "Play, drive into a cup/pin, HOLD intake: a bright marker shows the hold point (and stack " +
            "slots), the piece is grabbed, glides through the frame to the hold point, and stays there " +
            "(momentary — frees on release). Reverse spits it out.\n\n" +
            "Wrong button grabs → Reverse Direction. Stay held while driving → Keep Held When Idle. " +
            "Comes in too fast → lower Glide Speed. Hide the markers → Show Runtime Markers off.",
            "OK");
    }

    // The robot's non-spinning frame: the RobotMechanisms holder (lives on the root), else the root
    // ArticulationBody, else the top of the hierarchy.
    private static Transform ResolveChassis(MotorActuator motor)
    {
        RobotMechanisms rm = motor.GetComponentInParent<RobotMechanisms>();
        if (rm != null) return rm.transform;
        foreach (ArticulationBody ab in motor.GetComponentsInParent<ArticulationBody>(true))
            if (ab.isRoot) return ab.transform;
        return motor.transform.root;
    }

    // First descendant of root (inclusive) whose name matches — finds an existing mouth/hold-point
    // wherever it currently lives (e.g. still stuck under the roller from an older run).
    private static Transform FindDescendant(Transform root, string name)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    private static float Nz(float v) => Mathf.Abs(v) < 1e-4f ? 1f : v;
}
