using TMPro;
using UnityEngine;
using UnityEngine.InputSystem.OnScreen;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using Scene = UnityEngine.SceneManagement.Scene;

// One-click builder for the field scene's on-screen controller: the full VEX V5 button set
// laid out around the two existing joysticks, plus the Reset / Home / Camera row at the top center.
//
//   - L1/L2 stacked at the top-left, R1/R2 at the top-right (shoulder/trigger positions)
//   - two four-button diamonds between the joysticks along the bottom center:
//     arrows (Up/Down/Left/Right) on the left, X/B/A/Y on the right (VEX arrangement:
//     X top, B right, A bottom, Y left)
//   - Home (relocated from the top-left corner, which the shoulders now occupy) and a new
//     Reset button that reloads the scene.
//   - a camera button under them that switches between the two views, plus the ChaseCamera
//     object (the robot follow view) those views switch between.
//
// Every button is an OnScreenButton writing a synthetic <Gamepad> control (leftShoulder,
// buttonNorth, dpad/up, ...), the same mechanism the OnScreenSticks already use. The button
// actions live in Assets/RobotControls.inputactions; ButtonRouter routes them to robot mechanisms.
//
// Incremental / idempotent: objects are found-or-created and updated IN PLACE (never destroyed
// and recreated), so re-running only touches what changed, preserves object identities that
// ControlsAppearance references, and produces a byte-identical scene when nothing changed.
//
// Usage: Tools > RoboSim > Scenes > Build Drive Controls. Batch: -executeMethod BuildDriveControls.RunBatch.
public static class BuildDriveControls
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";

    // Cluster + control-path layout, all at the 1920x1080 canvas reference resolution.
    private const string ShoulderLeftName = "ShoulderButtonsLeft";
    private const string ShoulderRightName = "ShoulderButtonsRight";
    private const string PadLeftName = "ButtonPadLeft";
    private const string PadRightName = "ButtonPadRight";

    private const string ChaseCameraName = "ChaseCamera";

    // Authored framing for the chase camera, WRITTEN ON EVERY RUN.
    //
    // These live as serialized values on the camera object, so editing the field defaults in
    // RobotChaseCamera.cs reaches a fresh camera and nothing else — a scene that already has one keeps
    // whatever was serialized the day it was created. That is why two rounds of framing changes
    // appeared to do nothing. Setting them here makes re-running this tool the way to apply a change,
    // which is what every other number in this file already does.
    //
    // The trade: hand-tuning distance or start yaw in the Inspector is overwritten by a re-run. Tune
    // them here instead. The feel knobs the tool doesn't author (pitch, zoom range, smoothing) are
    // left alone.
    private const float ChaseCameraDistance = 10f;   // ~1 m back, about three robot-widths
    private const float ChaseCameraStartYaw = -90f;  // quarter turn round, looking at the claw end

    // The three top-of-screen buttons — Reset | Home | Camera View — laid out as one evenly spaced
    // row centred on the top edge. Each is anchored AND pivoted at top-centre and offset by a whole
    // pitch, so the row stays centred and evenly gapped at any canvas width; the earlier layout
    // pivoted the pair against the centre line and hung the camera button off the end, which read as
    // two buttons plus an afterthought.
    private const float TopButtonY = -24f;
    private const float TopButtonGap = 12f;
    private static readonly Vector2 TopButtonSize = new Vector2(180f, 64f);
    private static readonly float TopButtonPitch = TopButtonSize.x + TopButtonGap;

    private static readonly Vector2 ShoulderButtonSize = new Vector2(180f, 72f);
    private static readonly Vector2 PadButtonSize = new Vector2(90f, 90f);
    private static readonly Vector2 PadClusterSize = new Vector2(300f, 300f);
    private const float PadClusterCenterOffsetX = 230f; // pad centers sit this far from screen center
    private const float PadClusterBottomY = 30f;
    private const float PadButtonSpread = 105f;         // diamond arm length from the cluster center

    [MenuItem("Tools/RoboSim/Scenes/Build Drive Controls", false, 2)]
    private static void BuildInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.Log("Build Drive Controls: cancelled at the save prompt; nothing changed.");
            return;
        }
        Build();
    }

    // Batch entry point for -executeMethod: throws on failure (nonzero exit).
    public static void RunBatch()
    {
        Build();
    }

    private static void Build()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        GameObject canvasGo = FindRootCanvas(scene);
        if (canvasGo == null)
            throw new System.InvalidOperationException(
                $"Build Drive Controls: no root 'Canvas' object in {ScenePath}.");

        EnsureTopBarButtons(canvasGo);
        EnsureMatchLoadButton(canvasGo);
        Camera chaseCamera = EnsureChaseCamera(scene, out Camera freeCamera);
        EnsureCameraViewButton(canvasGo, freeCamera, chaseCamera);
        BuildShoulderClusters(canvasGo);
        BuildDiamondPads(canvasGo);
        string appearanceStatus = EnsureControlsAppearance(scene, out _);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new System.InvalidOperationException($"Build Drive Controls: failed to save {ScenePath}.");

        Debug.Log("Build Drive Controls: Reset | Home | Camera row at top center, L1/L2 + R1/R2 shoulders, " +
                  $"arrow + XBAY diamonds updated in place in {ScenePath}; controls appearance {appearanceStatus}; " +
                  $"{ChaseCameraName} + camera view button wired. Scene saved.");
    }

    // --- Home + Reset -------------------------------------------------------------------------

    // Home sits in the MIDDLE of the top row with Reset to its left and the camera toggle to its
    // right. Home is created by Build Home Screen; Reset is created here. Both are reused in place —
    // never destroyed — so re-running keeps their wiring and identities.
    private static void EnsureTopBarButtons(GameObject canvasGo)
    {
        Transform home = canvasGo.transform.Find("HomeButton");
        if (home != null)
        {
            PlaceInTopRow((RectTransform)home, 0f);
            EnsurePressFeedback(home.gameObject);
        }
        else
        {
            Debug.LogWarning("Build Drive Controls: no HomeButton on the Canvas — run " +
                             "Tools > RoboSim > Scenes > Build Home Screen first to create it.");
        }

        bool created = canvasGo.transform.Find("ResetButton") == null;
        Button resetButton = EnsureButton(canvasGo.transform, "ResetButton", "Reset", 32f, BuildHomeScene.AccentColor);
        PlaceInTopRow((RectTransform)resetButton.transform, -TopButtonPitch);

        // Reloading the active scene IS the reset. Wire the nav listener only when the button is
        // first created, so re-runs don't stack duplicate persistent listeners.
        SceneNavButton nav = resetButton.GetComponent<SceneNavButton>();
        if (nav == null) nav = resetButton.gameObject.AddComponent<SceneNavButton>();
        nav.sceneName = "SampleScene";
        if (created)
            UnityEventTools.AddPersistentListener(resetButton.onClick, nav.Load);
    }

    // Slot one button into the top row, `slot` pitches either side of the centre line.
    private static void PlaceInTopRow(RectTransform rect, float slotOffsetX)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(slotOffsetX, TopButtonY);
        rect.sizeDelta = TopButtonSize;
    }

    // Manual match-load trigger, centered directly under the top row. MatchLoadButton
    // hides it at runtime while Automatic Matchloading (Settings) is on, wires its own onClick,
    // and enables it only while a loader can actually spawn — nothing to wire here.
    private static void EnsureMatchLoadButton(GameObject canvasGo)
    {
        Button button = EnsureButton(canvasGo.transform, "MatchLoadButton", "Match Load", 28f, BuildHomeScene.AccentColor);
        RectTransform rect = (RectTransform)button.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, -96f);
        rect.sizeDelta = new Vector2(200f, 64f);
        EnsureComponent<MatchLoadButton>(button.gameObject);
    }

    // --- Camera views -----------------------------------------------------------------------

    // The robot follow view is a SECOND camera rather than another mode bolted onto the main one,
    // so each view keeps its own pose and lens: switching back to the free camera returns you
    // exactly where you left it, with no state to reconstruct.
    //
    // It deliberately gets NO AudioListener — a scene may only have one, and the main camera keeps
    // it — and no UniversalAdditionalCameraData, which URP adds itself, with base-camera defaults,
    // the first time it renders a camera that lacks one.
    //
    // Reused in place like everything else here, so re-running never duplicates the camera and
    // never resets settings tuned by hand in the Inspector.
    private static Camera EnsureChaseCamera(Scene scene, out Camera freeCamera)
    {
        freeCamera = FindFreeCamera(scene);
        if (freeCamera == null)
            Debug.LogWarning($"Build Drive Controls: no camera with a TouchCameraController in {ScenePath} — " +
                             "the camera view button will be created but left unwired.");

        GameObject go = FindRootObject(scene, ChaseCameraName);
        bool created = go == null;
        if (created) go = new GameObject(ChaseCameraName); // OpenScene(Single) made this the active scene

        Camera cam = EnsureComponent<Camera>(go);
        RobotChaseCamera chase = EnsureComponent<RobotChaseCamera>(go);

        // Re-apply the authored framing every run — see ChaseCameraDistance for why the field
        // defaults alone can't reach a camera that already exists.
        SerializedObject chaseSo = new SerializedObject(chase);
        chaseSo.FindProperty("distance").floatValue = ChaseCameraDistance;
        chaseSo.FindProperty("startYawOffset").floatValue = ChaseCameraStartYaw;
        chaseSo.ApplyModifiedPropertiesWithoutUndo();

        // Tagged MainCamera so Camera.main keeps resolving while the free camera is switched off.
        // Only ever one of the two is enabled, so the shared tag is never ambiguous.
        go.tag = "MainCamera";

        // Only on creation: match the main camera's lens and start from its pose, so the very first
        // frame — before the follow camera anchors onto the robot — looks like the scene the user
        // already knows. Re-runs leave both alone.
        if (created && freeCamera != null)
        {
            cam.fieldOfView = freeCamera.fieldOfView;
            cam.nearClipPlane = freeCamera.nearClipPlane;
            cam.farClipPlane = freeCamera.farClipPlane;
            cam.clearFlags = freeCamera.clearFlags;
            cam.backgroundColor = freeCamera.backgroundColor;
            cam.cullingMask = freeCamera.cullingMask;
            go.transform.SetPositionAndRotation(freeCamera.transform.position, freeCamera.transform.rotation);
        }

        // The scene is always SAVED in the free view; CameraViewToggle applies the player's saved
        // choice in Awake, before the first frame renders.
        cam.enabled = false;
        return cam;
    }

    // Camera view switch — the right-hand slot of the top row, opposite Reset.
    private static void EnsureCameraViewButton(GameObject canvasGo, Camera freeCamera, Camera chaseCamera)
    {
        Button button = EnsureButton(canvasGo.transform, "CameraViewButton", "Free Cam", 28f,
            BuildHomeScene.AccentColor);
        PlaceInTopRow((RectTransform)button.transform, TopButtonPitch);

        CameraViewToggle toggle = EnsureComponent<CameraViewToggle>(button.gameObject);
        SerializedObject so = new SerializedObject(toggle);
        so.FindProperty("freeCamera").objectReferenceValue = freeCamera;
        so.FindProperty("chaseCamera").objectReferenceValue = chaseCamera;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // The free-look camera is whichever camera carries the TouchCameraController — that component
    // defines the view, so this keeps working if the object is ever renamed.
    private static Camera FindFreeCamera(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            TouchCameraController controller = root.GetComponentInChildren<TouchCameraController>(true);
            if (controller != null) return controller.GetComponent<Camera>();
        }
        return null;
    }

    private static GameObject FindRootObject(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == name) return root;
        }
        return null;
    }

    // --- Shoulder buttons ---------------------------------------------------------------------

    private static void BuildShoulderClusters(GameObject canvasGo)
    {
        // Left: anchored to the top-left corner, L1 above L2 (VEX shoulder stack).
        GameObject left = EnsureClusterRoot(canvasGo.transform, ShoulderLeftName,
            new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -40f),
            new Vector2(ShoulderButtonSize.x, ShoulderButtonSize.y * 2f + 20f));
        EnsureShoulderButton(left.transform, "ButtonL1", "L1", new Vector2(0f, 1f), Vector2.zero,
            "<Gamepad>/leftShoulder");
        EnsureShoulderButton(left.transform, "ButtonL2", "L2", new Vector2(0f, 1f),
            new Vector2(0f, -(ShoulderButtonSize.y + 20f)), "<Gamepad>/leftTrigger");

        GameObject right = EnsureClusterRoot(canvasGo.transform, ShoulderRightName,
            new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-40f, -40f),
            new Vector2(ShoulderButtonSize.x, ShoulderButtonSize.y * 2f + 20f));
        EnsureShoulderButton(right.transform, "ButtonR1", "R1", new Vector2(1f, 1f), Vector2.zero,
            "<Gamepad>/rightShoulder");
        EnsureShoulderButton(right.transform, "ButtonR2", "R2", new Vector2(1f, 1f),
            new Vector2(0f, -(ShoulderButtonSize.y + 20f)), "<Gamepad>/rightTrigger");
    }

    private static void EnsureShoulderButton(Transform parent, string name, string label,
        Vector2 anchorAndPivot, Vector2 position, string controlPath)
    {
        Button button = EnsureButton(parent, name, label, 36f, BuildHomeScene.NeutralColor);
        RectTransform rect = (RectTransform)button.transform;
        rect.anchorMin = rect.anchorMax = anchorAndPivot;
        rect.pivot = anchorAndPivot;
        rect.anchoredPosition = position;
        rect.sizeDelta = ShoulderButtonSize;
        EnsureOnScreenButton(button.gameObject, controlPath);
    }

    // --- Diamond pads ---------------------------------------------------------------------

    private static void BuildDiamondPads(GameObject canvasGo)
    {
        // Left diamond: the four arrows.
        GameObject padLeft = EnsureClusterRoot(canvasGo.transform, PadLeftName,
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            new Vector2(-PadClusterCenterOffsetX, PadClusterBottomY), PadClusterSize);
        EnsureArrowGlyph(EnsurePadButton(padLeft.transform, "PadUp", new Vector2(0f, PadButtonSpread),
            null, "<Gamepad>/dpad/up"), 180f);
        EnsureArrowGlyph(EnsurePadButton(padLeft.transform, "PadDown", new Vector2(0f, -PadButtonSpread),
            null, "<Gamepad>/dpad/down"), 0f);
        EnsureArrowGlyph(EnsurePadButton(padLeft.transform, "PadLeft", new Vector2(-PadButtonSpread, 0f),
            null, "<Gamepad>/dpad/left"), -90f);
        EnsureArrowGlyph(EnsurePadButton(padLeft.transform, "PadRight", new Vector2(PadButtonSpread, 0f),
            null, "<Gamepad>/dpad/right"), 90f);

        // Right diamond: X top, B right, A bottom, Y left — the VEX V5 arrangement.
        GameObject padRight = EnsureClusterRoot(canvasGo.transform, PadRightName,
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            new Vector2(PadClusterCenterOffsetX, PadClusterBottomY), PadClusterSize);
        EnsurePadButton(padRight.transform, "ButtonX", new Vector2(0f, PadButtonSpread), "X",
            "<Gamepad>/buttonNorth");
        EnsurePadButton(padRight.transform, "ButtonB", new Vector2(PadButtonSpread, 0f), "B",
            "<Gamepad>/buttonEast");
        EnsurePadButton(padRight.transform, "ButtonA", new Vector2(0f, -PadButtonSpread), "A",
            "<Gamepad>/buttonSouth");
        EnsurePadButton(padRight.transform, "ButtonY", new Vector2(-PadButtonSpread, 0f), "Y",
            "<Gamepad>/buttonWest");
    }

    // Round pad button (Knob sprite), reused in place. label may be null (arrows use a glyph child).
    private static GameObject EnsurePadButton(Transform parent, string name, Vector2 position,
        string label, string controlPath)
    {
        GameObject go = EnsureChild(parent, name);
        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = PadButtonSize;

        Image image = EnsureComponent<Image>(go);
        image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");
        image.type = Image.Type.Simple;
        image.color = BuildHomeScene.NeutralColor;

        Button button = EnsureComponent<Button>(go);
        button.targetGraphic = image;

        if (!string.IsNullOrEmpty(label))
        {
            TextMeshProUGUI text = EnsureLabel(go.transform, label, 40f);
            text.fontStyle = FontStyles.Bold;
        }

        EnsureOnScreenButton(go, controlPath);
        EnsurePressFeedback(go);
        return go;
    }

    // Arrow glyph for the d-pad, reused in place. The built-in dropdown arrow points DOWN, so Up
    // is a 180-degree roll and Left/Right are -90/+90 (positive z rotation is counter-clockwise).
    private static GameObject EnsureArrowGlyph(GameObject padButton, float zRotationDegrees)
    {
        GameObject glyph = EnsureChild(padButton.transform, "Arrow");
        RectTransform rect = (RectTransform)glyph.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(44f, 44f);
        rect.localRotation = Quaternion.Euler(0f, 0f, zRotationDegrees);

        Image image = EnsureComponent<Image>(glyph);
        image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd");
        image.color = BuildHomeScene.TextColor;
        image.raycastTarget = false; // touches belong to the pad button
        return padButton;
    }

    // Empty container carrying the cluster's CanvasGroup (opacity), reused in place.
    private static GameObject EnsureClusterRoot(Transform canvas, string name, Vector2 anchor,
        Vector2 pivot, Vector2 position, Vector2 size)
    {
        GameObject root = EnsureChild(canvas, name);
        RectTransform rect = (RectTransform)root.transform;
        rect.anchorMin = rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        EnsureComponent<CanvasGroup>(root);
        return root;
    }

    private static void EnsureOnScreenButton(GameObject go, string controlPath)
    {
        OnScreenButton onScreen = EnsureComponent<OnScreenButton>(go);
        SerializedObject so = new SerializedObject(onScreen);
        so.FindProperty("m_ControlPath").stringValue = controlPath;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // --- Idempotent UI helpers ----------------------------------------------------------------

    private static GameObject EnsureChild(Transform parent, string name)
    {
        Transform existing = parent.Find(name);
        if (existing != null) return existing.gameObject;
        return BuildHomeScene.CreateUIObject(name, parent);
    }

    private static T EnsureComponent<T>(GameObject go) where T : Component
    {
        T component = go.GetComponent<T>();
        return component != null ? component : go.AddComponent<T>();
    }

    // Adds obvious press feedback (sink-in indent + shrink + brighten) to a control button and
    // turns OFF the Button's own ColorTint transition so it doesn't fight the color the feedback
    // writes. Idempotent — find-or-add, safe on re-runs.
    private static void EnsurePressFeedback(GameObject go)
    {
        Button button = go.GetComponent<Button>();
        if (button != null) button.transition = Selectable.Transition.None;
        EnsureComponent<PressFeedback>(go);
    }

    // Find-or-create a labelled Button under parent (Image + Button + "Label" TMP child), reusing
    // and re-theming an existing one rather than duplicating components.
    private static Button EnsureButton(Transform parent, string name, string label, float fontSize, Color color)
    {
        GameObject go = EnsureChild(parent, name);
        Image image = EnsureComponent<Image>(go);
        image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        image.type = Image.Type.Sliced;
        image.color = color;
        Button button = EnsureComponent<Button>(go);
        button.targetGraphic = image;
        EnsureLabel(go.transform, label, fontSize);
        EnsurePressFeedback(go);
        return button;
    }

    // Find-or-create the "Label" TMP child that fills its parent button.
    private static TextMeshProUGUI EnsureLabel(Transform parent, string label, float fontSize)
    {
        GameObject go = EnsureChild(parent, "Label");
        TextMeshProUGUI text = EnsureComponent<TextMeshProUGUI>(go);
        text.text = label;
        text.fontSize = fontSize;
        text.color = BuildHomeScene.TextColor;
        text.alignment = TextAlignmentOptions.Center;
        text.raycastTarget = false; // clicks belong to the button, not the label
        RectTransform rect = text.rectTransform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        return text;
    }

    // --- ControlsAppearance wiring --------------------------------------------------------------

    // (Re)wires the ControlsAppearance component on the field Canvas: joystick + cluster scale
    // targets and the CanvasGroups that carry the opacity setting, replacing the legacy
    // size-only JoystickScaler. Shared with Build Home Screen.
    internal static string EnsureControlsAppearance(Scene sampleScene, out bool changed)
    {
        changed = false;

        GameObject canvasGo = FindRootCanvas(sampleScene);
        if (canvasGo == null) return "skipped (no root Canvas found)";
        RectTransform left = BuildHomeScene.FindDescendantRect(sampleScene, "LeftJoystick_BG");
        RectTransform right = BuildHomeScene.FindDescendantRect(sampleScene, "RightJoystick_BG");
        if (left == null || right == null) return "skipped (joystick objects not found)";

        // The legacy size-only scaler is superseded — remove it so the two components never
        // fight over the joysticks' localScale.
        JoystickScaler legacy = canvasGo.GetComponent<JoystickScaler>();
        if (legacy != null)
        {
            Object.DestroyImmediate(legacy);
            changed = true;
        }

        // CanvasGroups carry the opacity; the authored image alphas must be normalized to 1 so
        // the group value is the single opacity authority (they multiply otherwise).
        var groups = new System.Collections.Generic.List<CanvasGroup>();
        foreach (RectTransform stick in new[] { left, right })
        {
            CanvasGroup group = stick.GetComponent<CanvasGroup>();
            if (group == null)
            {
                group = stick.gameObject.AddComponent<CanvasGroup>();
                changed = true;
            }
            groups.Add(group);
            foreach (Image image in stick.GetComponentsInChildren<Image>(true))
            {
                if (Mathf.Approximately(image.color.a, 1f)) continue;
                Color color = image.color;
                color.a = 1f;
                image.color = color;
                changed = true;
            }
        }

        var clusters = new System.Collections.Generic.List<RectTransform>();
        foreach (string name in new[] { ShoulderLeftName, ShoulderRightName, PadLeftName, PadRightName })
        {
            Transform cluster = canvasGo.transform.Find(name);
            if (cluster == null) continue; // Build Drive Controls hasn't run yet
            clusters.Add((RectTransform)cluster);
            CanvasGroup group = cluster.GetComponent<CanvasGroup>();
            if (group == null)
            {
                group = cluster.gameObject.AddComponent<CanvasGroup>();
                changed = true;
            }
            groups.Add(group);
        }

        ControlsAppearance appearance = canvasGo.GetComponent<ControlsAppearance>();
        if (appearance == null)
        {
            appearance = canvasGo.AddComponent<ControlsAppearance>();
            changed = true;
        }

        SerializedObject so = new SerializedObject(appearance);
        SerializedProperty joysticksProp = so.FindProperty("joysticks");
        joysticksProp.arraySize = 2;
        joysticksProp.GetArrayElementAtIndex(0).objectReferenceValue = left;
        joysticksProp.GetArrayElementAtIndex(1).objectReferenceValue = right;
        SerializedProperty clustersProp = so.FindProperty("buttonClusters");
        clustersProp.arraySize = clusters.Count;
        for (int i = 0; i < clusters.Count; i++)
            clustersProp.GetArrayElementAtIndex(i).objectReferenceValue = clusters[i];
        SerializedProperty groupsProp = so.FindProperty("opacityGroups");
        groupsProp.arraySize = groups.Count;
        for (int i = 0; i < groups.Count; i++)
            groupsProp.GetArrayElementAtIndex(i).objectReferenceValue = groups[i];
        if (so.ApplyModifiedPropertiesWithoutUndo()) changed = true;

        if (changed) EditorSceneManager.MarkSceneDirty(sampleScene);
        return changed ? "wired" : "already present";
    }

    private static GameObject FindRootCanvas(Scene scene)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == "Canvas" && root.GetComponent<Canvas>() != null) return root;
        }
        return null;
    }
}
