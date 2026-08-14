using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Move a mechanism's REST POSE — where the part sits before anyone touches a button — and keep it
// there permanently.
//
// THE PROBLEM. A joint's rest pose is whatever pose the part was modelled in: PhysX takes the link's
// transform at build time as joint angle 0, and the travel limits are measured from there. So a part
// that was drawn in CAD sticking up starts the match sticking up, and there is nothing in the joint
// tool that can change that — its limits are relative to the same pose. That bites twice:
//
//   * SIZE. A robot has to start inside its size box. An outtake modelled extended is out of size
//     before the match begins, and the only fix was to go back to CAD.
//   * AUTHORING. Hold points, intake mouths and pivots are all placed by eye against the part.
//     Placing them while the part is folded 95 degrees out of the way is guesswork.
//
// WHAT THIS DOES. Rotating the part in the Scene view alone is not enough — the joint would still
// call the OLD pose zero, so PhysX snaps the part back on the first simulation step and the limits
// now describe a range the part can't reach. This does the whole re-zero: it rotates (or slides) the
// link about ITS OWN joint axis through ITS OWN pivot, shifts the limits by the same amount so the
// range the part sweeps in the world is untouched, re-derives the parent-side anchor so PhysX agrees
// the new pose is zero, and re-seats a piston's endpoints. Permanent, undoable, and it survives a
// prefab save because it is nothing but serialized transform + joint data afterwards.
//
// Usage: Tools > RoboSim > Robot > Mechanisms > Set Starting Pose. Build in Prefab Mode and apply,
// like every other mechanism tool — RobotSpawner instantiates the prefab, so a scene instance's new
// pose never reaches the game.
public class StartingPoseWindow : EditorWindow
{
    private const string Title = "Set Starting Pose";

    [SerializeField] private GameObject link;
    // Where the part should end up, in the joint's CURRENT frame: degrees for a hinge, scaled units
    // for a slider. Held separately from the joint so the slider can be dragged around without every
    // intermediate value being committed.
    [SerializeField] private float target;
    [SerializeField] private bool showPreview = true;
    [SerializeField] private Vector2 scroll;

    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Set Starting Pose", false, 5)]
    private static void ShowWindow()
    {
        StartingPoseWindow window = GetWindow<StartingPoseWindow>(Title);
        window.minSize = new Vector2(430f, 300f);
        window.Show();
    }

    private void OnEnable()
    {
        if (link == null) link = Selection.activeGameObject;
        SceneView.duringSceneGui += OnSceneGUI;
    }

    private void OnDisable() => SceneView.duringSceneGui -= OnSceneGUI;

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);
        DrawBody();
        EditorGUILayout.EndScrollView();
    }

    private void DrawBody()
    {
        EditorGUILayout.HelpBox(
            "Changes where a mechanism STARTS, for good — the pose it was modelled in.\n\n" +
            "Pick a mechanism, drag it to where it should sit at the start of a match, and Apply. " +
            "The travel it has doesn't change: the part keeps reaching everywhere it reached before, " +
            "the limits just get re-measured from the new start.", MessageType.Info);

        RobotMechanisms robot = MechanismBuildUtil.ResolveRobot(link);
        GameObject picked = MechanismBuildUtil.DrawMechanismRoster(robot, link, "Pose");
        if (picked != null) Select(picked);
        EditorGUILayout.Space();

        GameObject newLink = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Mechanism part",
            "The moving link whose starting pose should change. Pick it from the list above, or drop " +
            "the part in."), link, typeof(GameObject), true);
        if (newLink != link) Select(newLink);

        if (link == null)
        {
            EditorGUILayout.HelpBox("Pick a mechanism above.", MessageType.Warning);
            return;
        }

        ArticulationBody body = link.GetComponent<ArticulationBody>();
        if (body == null)
        {
            EditorGUILayout.HelpBox(
                $"'{link.name}' isn't a moving link — it has no joint, so it has no starting pose to " +
                "change. Use Add or Fix Mechanism Joint to make it a mechanism first (or just move it " +
                "with the normal transform tools, since nothing is holding it anywhere).",
                MessageType.Error);
            return;
        }
        if (body.isRoot)
        {
            EditorGUILayout.HelpBox(
                $"'{link.name}' is the robot's root body, not a mechanism. Moving it would move the " +
                "whole robot.", MessageType.Error);
            return;
        }
        if (!StartingPose.TryJointFrame(body, out _, out _))
        {
            EditorGUILayout.HelpBox(
                $"'{link.name}' is a {body.jointType} — it has no axis to move along, so there is " +
                "nothing to re-pose. Only hinges and sliders have a starting pose.", MessageType.Error);
            return;
        }

        ArticulationDrive drive = body.xDrive;
        bool prismatic = body.jointType == ArticulationJointType.PrismaticJoint;
        bool limited = StartingPose.IsLimited(body);
        string unit = prismatic ? "u" : "°";

        EditorGUILayout.LabelField("Where it is now",
            MechanismBuildUtil.DescribeJointTravel(body), EditorStyles.wordWrappedMiniLabel);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField(new GUIContent(prismatic ? "Move it to" : "Turn it to",
            "Measured from where the part sits right now: 0 leaves it alone. The Scene view shows " +
            "where it will land in orange."), EditorStyles.miniBoldLabel);

        if (limited)
        {
            // Bounded by the joint's own travel, because that is the only range the part can be
            // parked in and still reach the rest of its sweep. Typing past an end is not a useful
            // wish — it would leave the part outside its own limits and PhysX would drag it back.
            target = EditorGUILayout.Slider(target, drive.lowerLimit, drive.upperLimit);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(prismatic ? "Fully retracted" : "Bottom of its range")) target = drive.lowerLimit;
                if (GUILayout.Button("Middle")) target = (drive.lowerLimit + drive.upperLimit) * 0.5f;
                if (GUILayout.Button(prismatic ? "Fully extended" : "Top of its range")) target = drive.upperLimit;
            }
        }
        else
        {
            // A free-spinning joint has no limits to shift, so any angle is fair game — this is just
            // "turn the roller to a nicer angle and keep it".
            target = EditorGUILayout.FloatField($"Offset ({unit})", target);
            EditorGUILayout.HelpBox("This joint spins freely, so there are no limits to keep in step — " +
                "any angle is allowed.", MessageType.None);
        }

        if (Mathf.Abs(target) > 1e-4f)
        {
            string after = limited
                ? $"limits become {drive.lowerLimit - target:0.#}{unit} … {drive.upperLimit - target:0.#}{unit}"
                : "limits unchanged (it spins freely)";
            EditorGUILayout.LabelField(" ",
                $"Applying moves the part {target:0.#}{unit} and re-measures from there — {after}. " +
                "It reaches exactly the same places it does now.", EditorStyles.wordWrappedMiniLabel);
        }

        showPreview = EditorGUILayout.ToggleLeft(new GUIContent("Show it in the Scene view",
            "Draw the part's current arm in blue and where Apply will put it in orange."), showPreview);
        if (GUI.changed && showPreview) SceneView.RepaintAll();

        EditorGUILayout.Space();
        if (link.scene.IsValid() && PrefabStageUtility.GetCurrentPrefabStage() == null &&
            PrefabUtility.IsPartOfPrefabInstance(link))
            EditorGUILayout.HelpBox(
                "This is a prefab INSTANCE in a scene. RobotSpawner instantiates the prefab at Play, " +
                "so open the robot in Prefab Mode (or apply the override) or the new starting pose " +
                "won't reach the game.", MessageType.Warning);

        using (new EditorGUI.DisabledScope(Mathf.Abs(target) <= 1e-4f))
        {
            if (!GUILayout.Button("Make This the Starting Pose", GUILayout.Height(30))) return;
        }

        try
        {
            string what = StartingPose.Apply(link, target, useUndo: true);
            target = 0f;   // the part is there now; leaving the old value would re-apply it
            SceneView.RepaintAll();
            EditorUtility.DisplayDialog(Title, what, "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog(Title, e.Message, "OK");
            Debug.LogException(e, link);
        }
    }

    private void Select(GameObject go)
    {
        link = go;
        target = 0f;   // a target measured against the last part means nothing on this one
        SceneView.RepaintAll();
    }

    // Blue: where the part's arm points now (its rest pose, which is what a match starts with).
    // Orange: where Apply would put it. Both drawn from the joint's real pivot and axis, so a pivot
    // that isn't where you thought it was shows up as an arm pointing somewhere surprising.
    private void OnSceneGUI(SceneView view)
    {
        if (!showPreview || link == null) return;
        ArticulationBody body = link.GetComponent<ArticulationBody>();
        if (body == null || !StartingPose.TryJointFrame(body, out Vector3 axisW, out Vector3 pivotW)) return;

        float h = HandleUtility.GetHandleSize(pivotW);
        bool prismatic = body.jointType == ArticulationJointType.PrismaticJoint;
        Vector3 center = MechanismBuildUtil.BoundsCenterOrOrigin(link);
        Vector3 arm = prismatic ? Vector3.zero : Vector3.ProjectOnPlane(center - pivotW, axisW);
        if (!prismatic && arm.sqrMagnitude < 1e-8f) arm = Vector3.Cross(axisW, Vector3.up) * h * 2.5f;

        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
        Handles.color = new Color(0.35f, 0.7f, 1f);
        Handles.DrawAAPolyLine(4f, pivotW - axisW * h * 2f, pivotW + axisW * h * 2f);
        Handles.SphereHandleCap(0, pivotW, Quaternion.identity, h * 0.16f, EventType.Repaint);

        if (prismatic)
        {
            Handles.DrawAAPolyLine(3f, center, center + axisW * target);
            Handles.color = new Color(1f, 0.6f, 0.1f);
            Handles.SphereHandleCap(0, center + axisW * target, Quaternion.identity, h * 0.2f, EventType.Repaint);
            Handles.Label(center + axisW * target, $"{link.name} starts here");
            Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
            return;
        }

        Handles.DrawAAPolyLine(3f, pivotW, pivotW + arm);
        Handles.Label(pivotW + arm * 1.08f, $"{link.name} starts here now");

        Vector3 moved = StartingPose.JointRotation(target, axisW) * arm;
        Handles.color = new Color(1f, 0.6f, 0.1f);
        Handles.DrawAAPolyLine(3f, pivotW, pivotW + moved);
        Handles.DrawSolidArc(pivotW, axisW, arm, target, arm.magnitude * 0.9f);
        if (Mathf.Abs(target) > 1e-4f)
            Handles.Label(pivotW + moved * 1.08f, $"…and would start here ({target:0.#}°)");
        Handles.zTest = UnityEngine.Rendering.CompareFunction.LessEqual;
    }
}

// The re-pose itself, split out so the headless validator can drive it with no window.
internal static class StartingPose
{
    private const string UndoName = "Set Starting Pose";

    // A joint's axis in WORLD space, and the point it acts about.
    //
    // Read from the anchor frame rather than from whatever axis a builder was once given, because
    // the anchor frame IS the joint: PhysX twists (or slides) along the anchor frame's X, so this is
    // right for a joint the URDF importer made, one the joint tool made, and one someone hand-set.
    public static bool TryJointFrame(ArticulationBody body, out Vector3 axisWorld, out Vector3 pivotWorld)
    {
        axisWorld = Vector3.right;
        pivotWorld = Vector3.zero;
        if (body == null) return false;
        if (body.jointType != ArticulationJointType.RevoluteJoint &&
            body.jointType != ArticulationJointType.PrismaticJoint) return false;

        Transform t = body.transform;
        Vector3 axis = t.TransformDirection(body.anchorRotation * Vector3.right);
        if (axis.sqrMagnitude < 1e-8f) return false;
        axisWorld = axis.normalized;
        pivotWorld = t.TransformPoint(body.anchorPosition);
        return true;
    }

    // Whether the joint has a travel window that has to be kept in step. A continuous revolute has
    // lowerLimit == upperLimit == 0 and shifting those would invent a window it never had.
    public static bool IsLimited(ArticulationBody body)
    {
        if (body == null) return false;
        return body.jointType == ArticulationJointType.PrismaticJoint
            ? body.linearLockX == ArticulationDofLock.LimitedMotion
            : body.twistLock == ArticulationDofLock.LimitedMotion;
    }

    // The world rotation for a joint angle of `degrees`.
    //
    // SIGN. A positive joint angle is a right-handed rotation about the anchor frame's X axis, which
    // is what TryJointFrame returns — the same rule the drivetrain relies on and RobotPhysicsValidation
    // measures (a positive wheel target spins the tyre's contact patch backwards, driving the robot
    // forwards). Quaternion.AngleAxis is right-handed about its axis in Unity's numeric basis, so
    // the two line up with no correction. StartingPoseValidation pins this against real simulation
    // rather than against this comment.
    public static Quaternion JointRotation(float degrees, Vector3 axisWorld)
        => Quaternion.AngleAxis(degrees, axisWorld);

    // Moves `link` to joint position `offset` and makes that its new rest pose, permanently.
    //
    // The four things that have to happen together — miss any one and the joint is broken in a way
    // that only shows up on the first physics step:
    //   1. the link (and everything under it, including any nested links) moves;
    //   2. the limits shift by the same amount, so the world range the part sweeps is unchanged;
    //   3. the PARENT-side anchor is re-derived, or PhysX still thinks the old pose is zero and
    //      snaps the part back the moment the scene simulates;
    //   4. a piston's endpoints are re-seated, since for a pneumatic the limits ARE its two ends.
    // Returns a description of what changed. Throws if the link has no re-poseable joint.
    public static string Apply(GameObject link, float offset, bool useUndo)
    {
        if (link == null) throw new ArgumentNullException(nameof(link));
        ArticulationBody body = link.GetComponent<ArticulationBody>();
        if (body == null)
            throw new InvalidOperationException(
                $"'{link.name}' has no ArticulationBody, so it has no joint and no starting pose.");
        if (body.isRoot)
            throw new InvalidOperationException(
                $"'{link.name}' is the articulation root — re-posing it would move the whole robot.");
        if (!TryJointFrame(body, out Vector3 axisWorld, out Vector3 pivotWorld))
            throw new InvalidOperationException(
                $"'{link.name}' is a {body.jointType} — only hinges and sliders have a starting pose.");

        bool prismatic = body.jointType == ArticulationJointType.PrismaticJoint;
        bool limited = IsLimited(body);
        ArticulationDrive drive = body.xDrive;

        if (limited)
        {
            // Clamped, not rejected: the caller's slider is already bounded, and a value that came
            // from somewhere else must not leave the part parked outside its own travel.
            float clamped = Mathf.Clamp(offset, drive.lowerLimit, drive.upperLimit);
            if (Mathf.Abs(clamped - offset) > 1e-4f) offset = clamped;
        }
        if (Mathf.Abs(offset) < 1e-5f) return $"'{link.name}' is already there — nothing changed.";

        if (useUndo)
        {
            Undo.RecordObject(link.transform, UndoName);
            Undo.RecordObject(body, UndoName);
        }

        // 1. Move the link. Children — meshes, colliders, nested links, hold points — ride along,
        // which is the point: the whole mechanism re-poses as one rigid piece.
        if (prismatic) link.transform.position += axisWorld * offset;
        else link.transform.RotateAround(pivotWorld, axisWorld, offset);

        // 2. Re-measure the travel from the new rest pose. The part reaches exactly where it did.
        if (limited)
        {
            drive.lowerLimit -= offset;
            drive.upperLimit -= offset;
            drive.target = Mathf.Clamp(drive.target - offset, drive.lowerLimit, drive.upperLimit);
            body.xDrive = drive;
        }

        // 3. The parent side of the joint frame, which is what makes the NEW pose zero.
        MechanismBuildUtil.RederiveParentAnchors(body);

        // 4. A piston's two ends are its joint limits, so they moved too.
        PneumaticActuator piston = link.GetComponent<PneumaticActuator>();
        if (piston != null && limited)
        {
            if (useUndo) Undo.RecordObject(piston, UndoName);
            bool wasSwapped = piston.retractedTarget > piston.extendedTarget;
            piston.retractedTarget = wasSwapped ? drive.upperLimit : drive.lowerLimit;
            piston.extendedTarget = wasSwapped ? drive.lowerLimit : drive.upperLimit;
            EditorUtility.SetDirty(piston);
        }

        EditorUtility.SetDirty(body);
        EditorUtility.SetDirty(link.transform);
        if (link.scene.IsValid()) EditorSceneManager.MarkSceneDirty(link.scene);

        string unit = prismatic ? "u" : "°";
        string range = limited
            ? $" Its travel is now {drive.lowerLimit:0.#}{unit} … {drive.upperLimit:0.#}{unit}, " +
              "measured from where it stands — it reaches all the same places."
            : " It spins freely, so it had no limits to re-measure.";
        return $"'{link.name}' now starts {offset:0.#}{unit} from where it did." + range +
               "\n\nSave the prefab, then Validation > Validate Robot Physics to check it holds.";
    }
}
