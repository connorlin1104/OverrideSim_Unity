using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem.OnScreen;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using Scene = UnityEngine.SceneManagement.Scene;

// One-click builder for the field scene's on-screen controller: the full VEX V5 button set
// laid out around the two existing joysticks, plus the Home/Reset pair at the top center.
//
//   - L1/L2 stacked at the top-left, R1/R2 at the top-right (shoulder/trigger positions)
//   - two four-button diamonds between the joysticks along the bottom center:
//     arrows (Up/Down/Left/Right) on the left, X/B/A/Y on the right (VEX arrangement:
//     X top, B right, A bottom, Y left)
//   - Home (relocated from the top-left corner, which the shoulders now occupy) and a new
//     Reset button that reloads the scene — identical to exiting to the home screen and
//     pressing Drive again, since nothing persists across scene loads except PlayerPrefs.
//
// Every button is an OnScreenButton writing a synthetic <Gamepad> control (leftShoulder,
// buttonNorth, dpad/up, ...), the same mechanism the OnScreenSticks already use — all
// on-screen controls share one virtual gamepad, and a real Bluetooth controller drives the
// same actions with zero extra wiring. The button actions live in
// Assets/RobotControls.inputactions; ButtonRouter routes them to robot mechanisms.
//
// It also swaps the field Canvas's legacy JoystickScaler for ControlsAppearance (size AND
// opacity for sticks + buttons, via CanvasGroups) and normalizes the sticks' authored image
// alphas to 1 so the CanvasGroup is the single opacity authority.
//
// Usage: Tools > RoboSim > Scenes > Build Drive Controls (idempotent: clusters are deleted
// and rebuilt, Home/Reset are re-asserted in place). Batch: -executeMethod BuildDriveControls.RunBatch.
public static class BuildDriveControls
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string UndoName = "Build Drive Controls";

    // Cluster + control-path layout, all at the 1920x1080 canvas reference resolution.
    // The four cluster roots carry the CanvasGroups (opacity) and are what ControlsAppearance
    // scales; keep these names in sync with EnsureControlsAppearance below.
    private const string ShoulderLeftName = "ShoulderButtonsLeft";
    private const string ShoulderRightName = "ShoulderButtonsRight";
    private const string PadLeftName = "ButtonPadLeft";
    private const string PadRightName = "ButtonPadRight";

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
        BuildShoulderClusters(canvasGo);
        BuildDiamondPads(canvasGo);
        string appearanceStatus = EnsureControlsAppearance(scene, out _);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene))
            throw new System.InvalidOperationException($"Build Drive Controls: failed to save {ScenePath}.");

        Debug.Log("Build Drive Controls: Home/Reset at top center, L1/L2 + R1/R2 shoulders, " +
                  $"arrow + XBAY diamonds rebuilt in {ScenePath}; controls appearance {appearanceStatus}. Scene saved.");
    }

    // --- Home + Reset -------------------------------------------------------------------------

    // The Home button used to sit in the top-left corner, which now belongs to L1/L2 — both nav
    // buttons live as a pair at the top center. Home is re-positioned (BuildHomeScene creates it),
    // Reset is deleted and rebuilt so its wiring never duplicates.
    private static void EnsureTopBarButtons(GameObject canvasGo)
    {
        Transform home = canvasGo.transform.Find("HomeButton");
        if (home != null)
        {
            RectTransform rect = (RectTransform)home;
            Undo.RecordObject(rect, UndoName);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-12f, -24f);
            rect.sizeDelta = new Vector2(160f, 64f);
        }
        else
        {
            Debug.LogWarning("Build Drive Controls: no HomeButton on the Canvas — run " +
                             "Tools > RoboSim > Scenes > Build Home Screen first to create it.");
        }

        Transform oldReset = canvasGo.transform.Find("ResetButton");
        if (oldReset != null) Undo.DestroyObjectImmediate(oldReset.gameObject);

        Button resetButton = BuildHomeScene.CreateButton("ResetButton", canvasGo.transform, "Reset", 32f,
            BuildHomeScene.AccentColor);
        RectTransform resetRect = (RectTransform)resetButton.transform;
        resetRect.anchorMin = resetRect.anchorMax = new Vector2(0.5f, 1f);
        resetRect.pivot = new Vector2(0f, 1f);
        resetRect.anchoredPosition = new Vector2(12f, -24f);
        resetRect.sizeDelta = new Vector2(160f, 64f);

        // Reloading the active scene IS the reset: robot pose, every game piece, and the match
        // loaders' re-arm latches all come back from the scene asset, exactly as if the player
        // went Home and pressed Drive — the only cross-scene state is PlayerPrefs, which is the
        // part that should survive (settings, button maps).
        SceneNavButton nav = resetButton.gameObject.AddComponent<SceneNavButton>();
        nav.sceneName = "SampleScene";
        UnityEventTools.AddPersistentListener(resetButton.onClick, nav.Load);
        Undo.RegisterCreatedObjectUndo(resetButton.gameObject, UndoName);
    }

    // --- Shoulder buttons ---------------------------------------------------------------------

    private static void BuildShoulderClusters(GameObject canvasGo)
    {
        // Left: anchored to the top-left corner, L1 above L2 (VEX shoulder stack).
        GameObject left = BuildClusterRoot(canvasGo.transform, ShoulderLeftName,
            new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -40f),
            new Vector2(ShoulderButtonSize.x, ShoulderButtonSize.y * 2f + 20f));
        CreateShoulderButton(left.transform, "ButtonL1", "L1", new Vector2(0f, 1f), Vector2.zero,
            "<Gamepad>/leftShoulder");
        CreateShoulderButton(left.transform, "ButtonL2", "L2", new Vector2(0f, 1f),
            new Vector2(0f, -(ShoulderButtonSize.y + 20f)), "<Gamepad>/leftTrigger");

        GameObject right = BuildClusterRoot(canvasGo.transform, ShoulderRightName,
            new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-40f, -40f),
            new Vector2(ShoulderButtonSize.x, ShoulderButtonSize.y * 2f + 20f));
        CreateShoulderButton(right.transform, "ButtonR1", "R1", new Vector2(1f, 1f), Vector2.zero,
            "<Gamepad>/rightShoulder");
        CreateShoulderButton(right.transform, "ButtonR2", "R2", new Vector2(1f, 1f),
            new Vector2(0f, -(ShoulderButtonSize.y + 20f)), "<Gamepad>/rightTrigger");
    }

    private static void CreateShoulderButton(Transform parent, string name, string label,
        Vector2 anchorAndPivot, Vector2 position, string controlPath)
    {
        Button button = BuildHomeScene.CreateButton(name, parent, label, 36f, BuildHomeScene.NeutralColor);
        RectTransform rect = (RectTransform)button.transform;
        rect.anchorMin = rect.anchorMax = anchorAndPivot;
        rect.pivot = anchorAndPivot;
        rect.anchoredPosition = position;
        rect.sizeDelta = ShoulderButtonSize;
        AddOnScreenButton(button.gameObject, controlPath);
    }

    // --- Diamond pads ---------------------------------------------------------------------

    private static void BuildDiamondPads(GameObject canvasGo)
    {
        // Left diamond: the four arrows.
        GameObject padLeft = BuildClusterRoot(canvasGo.transform, PadLeftName,
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            new Vector2(-PadClusterCenterOffsetX, PadClusterBottomY), PadClusterSize);
        AddArrowGlyph(CreatePadButton(padLeft.transform, "PadUp", new Vector2(0f, PadButtonSpread),
            null, "<Gamepad>/dpad/up"), 180f);
        AddArrowGlyph(CreatePadButton(padLeft.transform, "PadDown", new Vector2(0f, -PadButtonSpread),
            null, "<Gamepad>/dpad/down"), 0f);
        AddArrowGlyph(CreatePadButton(padLeft.transform, "PadLeft", new Vector2(-PadButtonSpread, 0f),
            null, "<Gamepad>/dpad/left"), -90f);
        AddArrowGlyph(CreatePadButton(padLeft.transform, "PadRight", new Vector2(PadButtonSpread, 0f),
            null, "<Gamepad>/dpad/right"), 90f);

        // Right diamond: X top, B right, A bottom, Y left — the VEX V5 arrangement.
        GameObject padRight = BuildClusterRoot(canvasGo.transform, PadRightName,
            new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
            new Vector2(PadClusterCenterOffsetX, PadClusterBottomY), PadClusterSize);
        CreatePadButton(padRight.transform, "ButtonX", new Vector2(0f, PadButtonSpread), "X",
            "<Gamepad>/buttonNorth");
        CreatePadButton(padRight.transform, "ButtonB", new Vector2(PadButtonSpread, 0f), "B",
            "<Gamepad>/buttonEast");
        CreatePadButton(padRight.transform, "ButtonA", new Vector2(0f, -PadButtonSpread), "A",
            "<Gamepad>/buttonSouth");
        CreatePadButton(padRight.transform, "ButtonY", new Vector2(-PadButtonSpread, 0f), "Y",
            "<Gamepad>/buttonWest");
    }

    // Round pad button (Knob sprite). label may be null (the arrows use a glyph child instead).
    private static GameObject CreatePadButton(Transform parent, string name, Vector2 position,
        string label, string controlPath)
    {
        GameObject go = BuildHomeScene.CreateUIObject(name, parent);
        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = PadButtonSize;

        Image image = go.AddComponent<Image>();
        image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");
        image.color = BuildHomeScene.NeutralColor;

        // The uGUI Button adds the pressed-tint feedback; the OnScreenButton is what actually
        // feeds the input system (both receive the same pointer events).
        Button button = go.AddComponent<Button>();
        button.targetGraphic = image;

        if (!string.IsNullOrEmpty(label))
        {
            TextMeshProUGUI text = BuildHomeScene.CreateText("Label", go.transform, label, 40f);
            text.fontStyle = FontStyles.Bold;
            text.raycastTarget = false;
            RectTransform textRect = text.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
        }

        AddOnScreenButton(go, controlPath);
        return go;
    }

    // Arrow glyph for the d-pad: the built-in dropdown arrow points DOWN, so Up is a 180-degree
    // roll and Left/Right are -90/+90 (positive z rotation is counter-clockwise).
    private static GameObject AddArrowGlyph(GameObject padButton, float zRotationDegrees)
    {
        GameObject glyph = BuildHomeScene.CreateUIObject("Arrow", padButton.transform);
        RectTransform rect = (RectTransform)glyph.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(44f, 44f);
        rect.localRotation = Quaternion.Euler(0f, 0f, zRotationDegrees);

        Image image = glyph.AddComponent<Image>();
        image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/DropdownArrow.psd");
        image.color = BuildHomeScene.TextColor;
        image.raycastTarget = false; // touches belong to the pad button
        return padButton;
    }

    // Empty container carrying the cluster's CanvasGroup (opacity) — deleted and rebuilt each
    // run so the tool stays idempotent without diffing children.
    private static GameObject BuildClusterRoot(Transform canvas, string name, Vector2 anchor,
        Vector2 pivot, Vector2 position, Vector2 size)
    {
        Transform old = canvas.Find(name);
        if (old != null) Undo.DestroyObjectImmediate(old.gameObject);

        GameObject root = BuildHomeScene.CreateUIObject(name, canvas);
        RectTransform rect = (RectTransform)root.transform;
        rect.anchorMin = rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
        root.AddComponent<CanvasGroup>();
        Undo.RegisterCreatedObjectUndo(root, UndoName);
        return root;
    }

    private static void AddOnScreenButton(GameObject go, string controlPath)
    {
        OnScreenButton onScreen = go.AddComponent<OnScreenButton>();
        // Same serialized-path wiring the scene's OnScreenSticks use.
        SerializedObject so = new SerializedObject(onScreen);
        so.FindProperty("m_ControlPath").stringValue = controlPath;
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // --- ControlsAppearance wiring --------------------------------------------------------------

    // (Re)wires the ControlsAppearance component on the field Canvas: joystick + cluster scale
    // targets and the CanvasGroups that carry the opacity setting, replacing the legacy
    // size-only JoystickScaler. Shared with Build Home Screen, which calls this so the joystick
    // settings keep working even if Build Drive Controls hasn't run yet (clusters are optional).
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
            Undo.DestroyObjectImmediate(legacy);
            changed = true;
        }

        // CanvasGroups carry the opacity; the authored image alphas must be normalized to 1 so
        // the group value is the single opacity authority (they multiply otherwise).
        var groups = new List<CanvasGroup>();
        foreach (RectTransform stick in new[] { left, right })
        {
            CanvasGroup group = stick.GetComponent<CanvasGroup>();
            if (group == null)
            {
                group = Undo.AddComponent<CanvasGroup>(stick.gameObject);
                changed = true;
            }
            groups.Add(group);
            foreach (Image image in stick.GetComponentsInChildren<Image>(true))
            {
                if (Mathf.Approximately(image.color.a, 1f)) continue;
                Undo.RecordObject(image, UndoName);
                Color color = image.color;
                color.a = 1f;
                image.color = color;
                changed = true;
            }
        }

        var clusters = new List<RectTransform>();
        foreach (string name in new[] { ShoulderLeftName, ShoulderRightName, PadLeftName, PadRightName })
        {
            Transform cluster = canvasGo.transform.Find(name);
            if (cluster == null) continue; // Build Drive Controls hasn't run yet
            clusters.Add((RectTransform)cluster);
            CanvasGroup group = cluster.GetComponent<CanvasGroup>();
            if (group == null)
            {
                group = Undo.AddComponent<CanvasGroup>(cluster.gameObject);
                changed = true;
            }
            groups.Add(group);
        }

        ControlsAppearance appearance = canvasGo.GetComponent<ControlsAppearance>();
        if (appearance == null)
        {
            appearance = Undo.AddComponent<ControlsAppearance>(canvasGo);
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
