using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using Scene = UnityEngine.SceneManagement.Scene;

// One-click builder for the app's home screen.
//
// Creates Assets/Scenes/HomeScene.unity from scratch each run: a dark URP camera, an overlay
// Canvas (same scaler settings as the field scene), an EventSystem on the Input System UI
// module, and a TMP-based UI (title, Drive/Settings main panel, and a settings panel where
// the player picks a robot model from the RobotModelCatalog and sizes the on-screen joysticks).
// It also:
//   - imports the TMP Essential Resources on first run (asset-only package: no scripts, so
//     no domain reload — safe to keep building in the same run),
//   - creates Assets/Settings/RobotModelCatalog.asset if missing,
//   - registers HomeScene + SampleScene in Build Settings (home first, so it boots the app),
//   - adds a "Home" button to the field scene's Canvas that loads back into HomeScene,
//   - adds a JoystickScaler to the field Canvas so the joystick-size setting takes effect there.
//
// Usage: Tools > RoboSim > Scenes > Build Home Screen (safe to re-run; every step skips if already done,
// except the HomeScene itself which is rebuilt). Batch: -executeMethod BuildHomeScene.RunBatch.
public class BuildHomeScene
{
    private const string HomeScenePath = "Assets/Scenes/HomeScene.unity";
    private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";
    private const string CatalogPath = "Assets/Settings/RobotModelCatalog.asset";
    private const string TmpSettingsPath = "Assets/TextMesh Pro/Resources/TMP Settings.asset";

    // The Input System package's built-in DefaultInputActions asset (same one the field
    // scene's EventSystem uses), resolved via GUID so a package move doesn't break us.
    private const string DefaultInputActionsGuid = "ca9f5fa95ffab41fb9a615ab714db018";

    // Dark theme. Accent/neutral must match HomeScreenController's selection tints so the
    // list highlight looks consistent with the rest of the buttons.
    private static readonly Color BackgroundColor = new Color32(0x1A, 0x1D, 0x23, 0xFF);
    private static readonly Color PanelColor = new Color32(0x26, 0x2A, 0x33, 0xF5);
    private static readonly Color ListColor = new Color32(0x1F, 0x23, 0x2B, 0xFF);
    private static readonly Color AccentColor = new Color(0.24f, 0.49f, 0.92f);
    private static readonly Color NeutralColor = new Color(0.23f, 0.25f, 0.30f);
    private static readonly Color TextColor = new Color32(0xE8, 0xEA, 0xF0, 0xFF);

    [MenuItem("Tools/RoboSim/Scenes/Build Home Screen", false, 1)]
    private static void BuildInteractive()
    {
        Build(true);
    }

    // Batch entry point for -executeMethod: no dialogs, throws on failure (nonzero exit).
    public static void RunBatch()
    {
        Build(false);
    }

    private static void Build(bool interactive)
    {
        // 1) TMP essential resources (fonts/shaders/settings) must exist before we create
        //    any TextMeshProUGUI, or the labels have no default font.
        bool tmpImported;
        if (!EnsureTmpEssentials(interactive, out tmpImported)) return;

        // 2) The model catalog the home screen lists.
        bool catalogCreated;
        RobotModelCatalog catalog = EnsureCatalog(out catalogCreated);

        // 3) Rebuild the home scene from scratch (idempotent by construction).
        string previousScenePath = SceneManager.GetActiveScene().path;
        if (interactive && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.Log("Build Home Scene: cancelled at the save prompt; nothing changed.");
            return;
        }
        Scene homeScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        BuildHomeSceneContents(catalog);
        EditorSceneManager.SaveScene(homeScene, HomeScenePath);

        // Cross-asset references can fail to persist when the referenced asset was created in
        // the same batch as the scene save (the shipped scene once carried catalog: {fileID: 0}
        // and the model list was silently dead on device). Reload from disk and prove the
        // catalog reference survived.
        Scene reloaded = EditorSceneManager.OpenScene(HomeScenePath, OpenSceneMode.Single);
        HomeScreenController saved = null;
        foreach (GameObject rootGo in reloaded.GetRootGameObjects())
        {
            saved = rootGo.GetComponentInChildren<HomeScreenController>(true);
            if (saved != null) break;
        }
        SerializedProperty savedCatalog = saved != null
            ? new SerializedObject(saved).FindProperty("catalog") : null;
        if (savedCatalog == null || savedCatalog.objectReferenceValue == null)
        {
            const string msg = "Build Home Scene: the saved HomeScene lost its RobotModelCatalog " +
                               "reference — the model list would never build at runtime.";
            Debug.LogError(msg);
            if (interactive) { EditorUtility.DisplayDialog("Build Home Scene", msg, "OK"); return; }
            throw new InvalidOperationException(msg);
        }

        // 4) Build settings: home screen boots the app at index 0, the field scene follows.
        //    Scenes this tool doesn't know about are preserved (re-running must not clobber
        //    scenes added later).
        var buildScenes = new List<EditorBuildSettingsScene>
        {
            new EditorBuildSettingsScene(HomeScenePath, true),
            new EditorBuildSettingsScene(SampleScenePath, true),
        };
        foreach (EditorBuildSettingsScene existing in EditorBuildSettings.scenes)
        {
            if (existing.path != HomeScenePath && existing.path != SampleScenePath)
                buildScenes.Add(existing);
        }
        EditorBuildSettings.scenes = buildScenes.ToArray();

        // 5) Field scene edits: a way back to the home screen, and the JoystickScaler that
        //    applies the home screen's joystick-size setting to the on-screen sticks.
        Scene sampleScene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
        string homeButtonStatus = EnsureFieldHomeButton(sampleScene, interactive, out bool homeButtonAdded);
        string joystickScalerStatus = EnsureJoystickScaler(sampleScene, out bool joystickScalerChanged);
        if (homeButtonAdded || joystickScalerChanged) EditorSceneManager.SaveScene(sampleScene);

        // Interactive runs put the user back where they were; batch leaves SampleScene open.
        if (interactive && !string.IsNullOrEmpty(previousScenePath) && previousScenePath != SampleScenePath)
            EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);

        Debug.Log($"Build Home Scene: TMP essentials {(tmpImported ? "imported" : "already present")}, " +
                  $"catalog {(catalogCreated ? "created at " + CatalogPath : "found")}, " +
                  $"rebuilt {HomeScenePath}, build settings = [HomeScene, SampleScene], " +
                  $"field Home button {homeButtonStatus}, joystick scaler {joystickScalerStatus}.");
    }

    // --- Step 1: TMP essentials ---

    private static bool EnsureTmpEssentials(bool interactive, out bool imported)
    {
        imported = false;
        if (File.Exists(TmpSettingsPath)) return true;

        // Asset-only package (fonts/shaders/materials — no .cs), so importing it triggers no
        // domain reload. NOTE: AssetDatabase.ImportPackage queues asynchronously even in batch
        // mode, so a headless first run can still come up empty — in that case bootstrap by
        // extracting the unitypackage (a tar.gz of guid/asset+meta entries) into Assets first.
        TMP_PackageResourceImporter.ImportResources(true, false, false);
        AssetDatabase.Refresh();

        if (File.Exists(TmpSettingsPath))
        {
            imported = true;
            return true;
        }

        // In the interactive editor the package import can complete asynchronously, after
        // this method returns — tell the user to simply run the tool again.
        if (interactive)
        {
            EditorUtility.DisplayDialog("Build Home Scene",
                "TMP Essential Resources are still importing. Wait for the import to finish, " +
                "then run Tools > RoboSim > Scenes > Build Home Screen again.", "OK");
            return false;
        }
        Debug.LogError("Build Home Scene: TMP Essential Resources missing after import.");
        throw new InvalidOperationException("TMP Essential Resources missing after import.");
    }

    // --- Step 2: model catalog asset ---

    private static RobotModelCatalog EnsureCatalog(out bool created)
    {
        created = false;
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
        if (catalog != null) return catalog;

        if (!AssetDatabase.IsValidFolder("Assets/Settings"))
            AssetDatabase.CreateFolder("Assets", "Settings");

        catalog = ScriptableObject.CreateInstance<RobotModelCatalog>();
        catalog.models.Add(new RobotModelCatalog.Entry
        {
            id = "360rpm-drivetrain",
            displayName = "360 RPM Drivetrain",
        });
        AssetDatabase.CreateAsset(catalog, CatalogPath);
        AssetDatabase.SaveAssets();
        created = true;
        return catalog;
    }

    // --- Step 3: home scene contents ---

    private static void BuildHomeSceneContents(RobotModelCatalog catalog)
    {
        // Camera: pure UI backdrop, solid dark clear. GetUniversalAdditionalCameraData()
        // adds the URP camera data component if it doesn't exist yet.
        GameObject cameraGo = new GameObject("Main Camera");
        cameraGo.tag = "MainCamera";
        cameraGo.transform.position = new Vector3(0f, 1f, -10f);
        Camera camera = cameraGo.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = BackgroundColor;
        cameraGo.AddComponent<AudioListener>();
        camera.GetUniversalAdditionalCameraData();

        // Canvas: mirror the field scene's setup (overlay, scale-with-screen 1920x1080).
        GameObject canvasGo = CreateUIObject("Canvas", null);
        Canvas canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();

        // EventSystem on the Input System UI module (the project uses the new Input System).
        GameObject eventSystemGo = new GameObject("EventSystem");
        eventSystemGo.AddComponent<EventSystem>();
        InputSystemUIInputModule uiModule = eventSystemGo.AddComponent<InputSystemUIInputModule>();
        AssignDefaultUiActions(uiModule);

        // Title.
        TextMeshProUGUI title = CreateText("Title", canvasGo.transform, "RoboSim", 96f);
        title.fontStyle = FontStyles.Bold;
        RectTransform titleRect = title.rectTransform;
        titleRect.anchorMin = titleRect.anchorMax = new Vector2(0.5f, 1f);
        titleRect.pivot = new Vector2(0.5f, 1f);
        titleRect.anchoredPosition = new Vector2(0f, -60f);
        titleRect.sizeDelta = new Vector2(800f, 120f);

        // Main panel: Drive / Settings.
        GameObject mainPanel = CreatePanel("MainPanel", canvasGo.transform, new Vector2(520f, 340f));
        AddVerticalLayout(mainPanel, 40, 28f);
        Button driveButton = CreateButton("DriveButton", mainPanel.transform, "Drive", 52f, AccentColor);
        SetLayoutHeight(driveButton.gameObject, 110f);
        Button settingsButton = CreateButton("SettingsButton", mainPanel.transform, "Settings", 52f, AccentColor);
        SetLayoutHeight(settingsButton.gameObject, 110f);

        // Settings panel: model picker built at runtime from the catalog by cloning an
        // inactive template button (so catalog edits need no scene change), plus a joystick
        // size slider. Taller than the main panel to fit the size control under the list.
        GameObject settingsPanel = CreatePanel("SettingsPanel", canvasGo.transform, new Vector2(680f, 900f));
        AddVerticalLayout(settingsPanel, 40, 24f);

        TextMeshProUGUI header = CreateText("HeaderLabel", settingsPanel.transform, "Select Robot Model", 48f);
        header.fontStyle = FontStyles.Bold;
        SetLayoutHeight(header.gameObject, 70f);

        GameObject modelList = CreateUIObject("ModelList", settingsPanel.transform);
        Image listImage = modelList.AddComponent<Image>();
        listImage.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd");
        listImage.type = Image.Type.Sliced;
        listImage.color = ListColor;
        VerticalLayoutGroup listLayout = AddVerticalLayout(modelList, 20, 14f);
        listLayout.childAlignment = TextAnchor.UpperCenter;
        LayoutElement listElement = modelList.AddComponent<LayoutElement>();
        listElement.flexibleHeight = 1f; // list absorbs the leftover panel height

        Button template = CreateButton("ModelButtonTemplate", modelList.transform, "Model", 40f, NeutralColor);
        SetLayoutHeight(template.gameObject, 84f);
        template.gameObject.SetActive(false); // template stays inactive; controller clones it

        // Joystick size control: label (also the live percentage readout) + slider. The
        // controller reads/writes JoystickSettings; the field scene's JoystickScaler applies it.
        TextMeshProUGUI joystickLabel = CreateText("JoystickSizeLabel", settingsPanel.transform, "Joystick Size", 40f);
        joystickLabel.fontStyle = FontStyles.Bold;
        SetLayoutHeight(joystickLabel.gameObject, 56f);

        Slider joystickSlider = CreateSlider("JoystickSizeSlider", settingsPanel.transform,
            JoystickSettings.MinScale, JoystickSettings.MaxScale, JoystickSettings.DefaultScale);
        SetLayoutHeight(joystickSlider.gameObject, 56f);

        Button backButton = CreateButton("BackButton", settingsPanel.transform, "Back", 44f, AccentColor);
        SetLayoutHeight(backButton.gameObject, 96f);

        settingsPanel.SetActive(false); // controller shows it via OnSettingsPressed

        // Controller root: wire the private serialized refs (same SerializedObject pattern
        // as the Fix Robot Drive Collider tool) and persistent onClicks so everything
        // serializes into the scene.
        GameObject homeRoot = new GameObject("HomeScreen");
        HomeScreenController controller = homeRoot.AddComponent<HomeScreenController>();
        SerializedObject so = new SerializedObject(controller);
        // Re-load rather than trusting the instance loaded before NewScene: the scene swap can
        // destroy the native object behind an already-loaded asset reference, and a destroyed
        // object silently serializes as {fileID: 0} — the shipped-dead-model-list bug.
        RobotModelCatalog freshCatalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
        so.FindProperty("catalog").objectReferenceValue = freshCatalog != null ? freshCatalog : catalog;
        so.FindProperty("mainPanel").objectReferenceValue = mainPanel;
        so.FindProperty("settingsPanel").objectReferenceValue = settingsPanel;
        so.FindProperty("modelListParent").objectReferenceValue = modelList.transform;
        so.FindProperty("modelButtonTemplate").objectReferenceValue = template;
        so.FindProperty("joystickSizeSlider").objectReferenceValue = joystickSlider;
        so.FindProperty("joystickSizeLabel").objectReferenceValue = joystickLabel;
        so.ApplyModifiedPropertiesWithoutUndo();

        UnityEventTools.AddPersistentListener(driveButton.onClick, controller.OnDrivePressed);
        UnityEventTools.AddPersistentListener(settingsButton.onClick, controller.OnSettingsPressed);
        UnityEventTools.AddPersistentListener(backButton.onClick, controller.OnBackPressed);
    }

    // Point the UI module at the package's DefaultInputActions asset (what the field scene's
    // EventSystem uses) and at its imported action sub-assets, so the scene serializes
    // asset-backed references. Written via SerializedObject — the C# property setters hook
    // action callbacks as a side effect, which we don't want in edit mode. If the asset can't
    // be found we leave everything null; the module assigns runtime defaults in OnEnable.
    private static void AssignDefaultUiActions(InputSystemUIInputModule uiModule)
    {
        string path = AssetDatabase.GUIDToAssetPath(DefaultInputActionsGuid);
        InputActionAsset actions = string.IsNullOrEmpty(path)
            ? null
            : AssetDatabase.LoadAssetAtPath<InputActionAsset>(path);
        if (actions == null)
        {
            Debug.LogWarning("Build Home Scene: DefaultInputActions asset not found; the UI input " +
                             "module will self-initialize at runtime.");
            return;
        }

        SerializedObject so = new SerializedObject(uiModule);
        so.FindProperty("m_ActionsAsset").objectReferenceValue = actions;
        foreach (UnityEngine.Object sub in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
        {
            InputActionReference reference = sub as InputActionReference;
            if (reference == null || reference.action == null) continue;
            string field = null;
            switch (reference.action.name)
            {
                case "Point": field = "m_PointAction"; break;
                case "Click": field = "m_LeftClickAction"; break;
                case "RightClick": field = "m_RightClickAction"; break;
                case "MiddleClick": field = "m_MiddleClickAction"; break;
                case "ScrollWheel": field = "m_ScrollWheelAction"; break;
                case "Navigate": field = "m_MoveAction"; break;
                case "Submit": field = "m_SubmitAction"; break;
                case "Cancel": field = "m_CancelAction"; break;
                case "TrackedDevicePosition": field = "m_TrackedDevicePositionAction"; break;
                case "TrackedDeviceOrientation": field = "m_TrackedDeviceOrientationAction"; break;
            }
            if (field != null) so.FindProperty(field).objectReferenceValue = reference;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    // --- Step 5: field scene Home button ---

    private static string EnsureFieldHomeButton(Scene sampleScene, bool interactive, out bool added)
    {
        added = false;
        GameObject canvasGo = null;
        foreach (GameObject root in sampleScene.GetRootGameObjects())
        {
            if (root.name == "Canvas" && root.GetComponent<Canvas>() != null)
            {
                canvasGo = root;
                break;
            }
        }
        if (canvasGo == null)
        {
            if (interactive)
            {
                EditorUtility.DisplayDialog("Build Home Scene",
                    "SampleScene has no root 'Canvas' object; the Home button was not added.", "OK");
                return "skipped (no root Canvas found)";
            }
            Debug.LogError("Build Home Scene: SampleScene has no root 'Canvas' object.");
            throw new InvalidOperationException("SampleScene has no root 'Canvas' object.");
        }

        // Idempotency: never add a second button (and never touch the joystick objects).
        if (canvasGo.transform.Find("HomeButton") != null) return "already present";

        Button homeButton = CreateButton("HomeButton", canvasGo.transform, "Home", 32f, AccentColor);
        RectTransform rect = (RectTransform)homeButton.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f); // top-left, clear of the joysticks
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(40f, -40f);
        rect.sizeDelta = new Vector2(160f, 64f);

        SceneNavButton nav = homeButton.gameObject.AddComponent<SceneNavButton>();
        nav.sceneName = "HomeScene";
        UnityEventTools.AddPersistentListener(homeButton.onClick, nav.Load);

        Undo.RegisterCreatedObjectUndo(homeButton.gameObject, "Add Home Button");
        EditorSceneManager.MarkSceneDirty(sampleScene);
        added = true;
        return "added";
    }

    // --- Step 5b: field scene JoystickScaler ---

    // Ensure a JoystickScaler on the field Canvas, wired to the two joystick backgrounds, so the
    // sticks pick up the home screen's size setting at scene load. Idempotent: re-running finds
    // the existing component and just re-asserts the references.
    private static string EnsureJoystickScaler(Scene sampleScene, out bool changed)
    {
        changed = false;

        RectTransform left = FindDescendantRect(sampleScene, "LeftJoystick_BG");
        RectTransform right = FindDescendantRect(sampleScene, "RightJoystick_BG");
        if (left == null || right == null)
            return "skipped (joystick objects not found)";

        GameObject canvasGo = null;
        foreach (GameObject root in sampleScene.GetRootGameObjects())
        {
            if (root.name == "Canvas" && root.GetComponent<Canvas>() != null) { canvasGo = root; break; }
        }
        if (canvasGo == null) return "skipped (no root Canvas found)";

        JoystickScaler scaler = canvasGo.GetComponent<JoystickScaler>();
        if (scaler == null)
        {
            scaler = Undo.AddComponent<JoystickScaler>(canvasGo);
            changed = true;
        }

        SerializedObject so = new SerializedObject(scaler);
        SerializedProperty joysticks = so.FindProperty("joysticks");
        joysticks.arraySize = 2;
        joysticks.GetArrayElementAtIndex(0).objectReferenceValue = left;
        joysticks.GetArrayElementAtIndex(1).objectReferenceValue = right;
        if (so.ApplyModifiedPropertiesWithoutUndo()) changed = true;

        if (changed) EditorSceneManager.MarkSceneDirty(sampleScene);
        return changed ? "wired" : "already present";
    }

    private static RectTransform FindDescendantRect(Scene scene, string name)
    {
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (RectTransform rect in root.GetComponentsInChildren<RectTransform>(true))
            {
                if (rect.name == name) return rect;
            }
        }
        return null;
    }

    // --- UI building helpers ---

    private static GameObject CreateUIObject(string name, Transform parent)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.layer = LayerMask.NameToLayer("UI");
        if (parent != null) go.transform.SetParent(parent, false);
        return go;
    }

    // Centered panel with a dark sliced background.
    private static GameObject CreatePanel(string name, Transform parent, Vector2 size)
    {
        GameObject go = CreateUIObject(name, parent);
        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = size;
        Image image = go.AddComponent<Image>();
        image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd");
        image.type = Image.Type.Sliced;
        image.color = PanelColor;
        return go;
    }

    // Children get their width from the layout; heights come from each child's LayoutElement.
    private static VerticalLayoutGroup AddVerticalLayout(GameObject go, int padding, float spacing)
    {
        VerticalLayoutGroup layout = go.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(padding, padding, padding, padding);
        layout.spacing = spacing;
        layout.childAlignment = TextAnchor.MiddleCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        return layout;
    }

    private static void SetLayoutHeight(GameObject go, float height)
    {
        LayoutElement element = go.AddComponent<LayoutElement>();
        element.minHeight = height;
        element.preferredHeight = height;
        element.flexibleHeight = 0f;
    }

    private static TextMeshProUGUI CreateText(string name, Transform parent, string text, float fontSize)
    {
        GameObject go = CreateUIObject(name, parent);
        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = TextColor;
        tmp.alignment = TextAlignmentOptions.Center;
        return tmp;
    }

    private static Button CreateButton(string name, Transform parent, string label, float fontSize, Color color)
    {
        GameObject go = CreateUIObject(name, parent);
        Image image = go.AddComponent<Image>();
        image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        image.type = Image.Type.Sliced;
        image.color = color;
        Button button = go.AddComponent<Button>();
        button.targetGraphic = image;

        TextMeshProUGUI text = CreateText("Label", go.transform, label, fontSize);
        text.raycastTarget = false; // clicks belong to the button, not the label
        RectTransform textRect = text.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        return button;
    }

    // Horizontal slider built from Unity's DefaultControls (same structure as GameObject > UI >
    // Slider) so the Background/Fill/Handle wiring is correct, then themed to match the panel.
    private static Slider CreateSlider(string name, Transform parent, float min, float max, float value)
    {
        var resources = new DefaultControls.Resources
        {
            standard = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd"),
            background = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd"),
            knob = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd"),
        };

        GameObject go = DefaultControls.CreateSlider(resources);
        go.name = name;
        go.transform.SetParent(parent, false);
        foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = LayerMask.NameToLayer("UI");

        // Theme the parts to match the dark panel: dark track, accent fill, light knob.
        TintChildImage(go, "Background", ListColor);
        TintChildImage(go, "Fill Area/Fill", AccentColor);
        TintChildImage(go, "Handle Slide Area/Handle", TextColor);

        Slider slider = go.GetComponent<Slider>();
        slider.wholeNumbers = false;
        slider.minValue = min;
        slider.maxValue = max;
        slider.value = value;
        return slider;
    }

    private static void TintChildImage(GameObject root, string path, Color color)
    {
        Transform child = root.transform.Find(path);
        Image image = child != null ? child.GetComponent<Image>() : null;
        if (image != null) image.color = color;
    }
}
