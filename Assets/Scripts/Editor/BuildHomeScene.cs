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
//   - wires ControlsAppearance on the field Canvas (via Build Drive Controls) so the control
//     size/opacity settings take effect there.
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
    // list highlight looks consistent with the rest of the buttons. Internal (with the UI
    // helpers below) so Build Drive Controls builds the field-scene buttons in the same style.
    private static readonly Color BackgroundColor = new Color32(0x1A, 0x1D, 0x23, 0xFF);
    internal static readonly Color PanelColor = new Color32(0x26, 0x2A, 0x33, 0xF5);
    internal static readonly Color ListColor = new Color32(0x1F, 0x23, 0x2B, 0xFF);
    internal static readonly Color AccentColor = new Color(0.24f, 0.49f, 0.92f);
    internal static readonly Color NeutralColor = new Color(0.23f, 0.25f, 0.30f);
    internal static readonly Color TextColor = new Color32(0xE8, 0xEA, 0xF0, 0xFF);

    [MenuItem("Tools/RoboSim/Scenes/Build Home Screen", false, 1)]
    private static void BuildInteractive()
    {
        Build(true, false);
    }

    // Force a full rebuild even when a valid HomeScene already exists — use after changing the
    // home-screen UI code. The default menu item above skips the rebuild when it isn't needed.
    [MenuItem("Tools/RoboSim/Scenes/Rebuild Home Screen (Force)", false, 3)]
    private static void RebuildInteractive()
    {
        Build(true, true);
    }

    // Batch entry point for -executeMethod: no dialogs, throws on failure (nonzero exit). Always
    // forces a clean rebuild so CI is deterministic from any checkout.
    public static void RunBatch()
    {
        Build(false, true);
    }

    private static void Build(bool interactive, bool force)
    {
        // 1) TMP essential resources (fonts/shaders/settings) must exist before we create
        //    any TextMeshProUGUI, or the labels have no default font.
        bool tmpImported;
        if (!EnsureTmpEssentials(interactive, out tmpImported)) return;

        // 2) The model catalog the home screen lists.
        bool catalogCreated;
        RobotModelCatalog catalog = EnsureCatalog(out catalogCreated);

        string previousScenePath = SceneManager.GetActiveScene().path;
        if (interactive && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.Log("Build Home Scene: cancelled at the save prompt; nothing changed.");
            return;
        }

        // 3) Rebuild the home scene ONLY when needed: a full teardown+rebuild is skipped when a
        //    valid HomeScene already exists (fast, no scene churn). Force (or a missing/broken
        //    scene) does the from-scratch rebuild. The field-scene edits below run either way and
        //    are idempotent.
        string rebuildStatus;
        if (!force && HomeSceneIsValid())
        {
            rebuildStatus = "skipped (already built; use Rebuild Home Screen (Force) to regenerate)";
        }
        else
        {
            Scene homeScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            BuildHomeSceneContents(catalog);
            EditorSceneManager.SaveScene(homeScene, HomeScenePath);

            // Cross-asset references can fail to persist when the referenced asset was created in
            // the same batch as the scene save (the shipped scene once carried catalog: {fileID: 0}
            // and the model list was silently dead on device). Reload from disk and prove the
            // catalog reference survived.
            if (!VerifySavedWiring(interactive)) return;
            rebuildStatus = force ? "rebuilt (forced)" : "rebuilt (was missing or invalid)";
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

        // 5) Field scene edits: a way back to the home screen, and the ControlsAppearance that
        //    applies the home screen's control size/opacity settings to the on-screen controls.
        Scene sampleScene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
        string homeButtonStatus = EnsureFieldHomeButton(sampleScene, interactive, out bool homeButtonAdded);
        string appearanceStatus = BuildDriveControls.EnsureControlsAppearance(sampleScene, out bool appearanceChanged);
        if (homeButtonAdded || appearanceChanged) EditorSceneManager.SaveScene(sampleScene);

        // Interactive runs put the user back where they were; batch leaves SampleScene open.
        if (interactive && !string.IsNullOrEmpty(previousScenePath) && previousScenePath != SampleScenePath)
            EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);

        Debug.Log($"Build Home Scene: TMP essentials {(tmpImported ? "imported" : "already present")}, " +
                  $"catalog {(catalogCreated ? "created at " + CatalogPath : "found")}, " +
                  $"HomeScene {rebuildStatus}, build settings = [HomeScene, SampleScene], " +
                  $"field Home button {homeButtonStatus}, controls appearance {appearanceStatus}.");
    }

    // Is there already a valid HomeScene so a full rebuild can be skipped? True when the scene
    // exists and its HomeScreenController's catalog + controller-config + controls-layout
    // references all survived (an older scene missing the layout screen counts as invalid, so the
    // first run after adding it rebuilds once, then subsequent runs skip). Opens the scene to
    // inspect it — the caller has already offered to save the current one.
    private static bool HomeSceneIsValid()
    {
        if (!File.Exists(HomeScenePath)) return false;
        Scene scene = EditorSceneManager.OpenScene(HomeScenePath, OpenSceneMode.Single);
        HomeScreenController controller = null;
        foreach (GameObject rootGo in scene.GetRootGameObjects())
        {
            controller = rootGo.GetComponentInChildren<HomeScreenController>(true);
            if (controller != null) break;
        }
        if (controller == null) return false;
        SerializedObject so = new SerializedObject(controller);
        return IsRefSet(so, "catalog") && IsRefSet(so, "controllerConfig") && IsRefSet(so, "controlsLayout");
    }

    private static bool IsRefSet(SerializedObject so, string propertyName)
    {
        SerializedProperty property = so.FindProperty(propertyName);
        return property != null && property.objectReferenceValue != null;
    }

    // Reloads the just-saved HomeScene from disk and proves the critical cross-asset references
    // survived the save. Returns false (after logging + a dialog in interactive mode) when a
    // reference was lost; throws in batch mode so -executeMethod exits nonzero.
    private static bool VerifySavedWiring(bool interactive)
    {
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
            if (!interactive) throw new InvalidOperationException(msg);
            EditorUtility.DisplayDialog("Build Home Scene", msg, "OK");
            return false;
        }

        ControllerConfigScreen savedConfig = null;
        foreach (GameObject rootGo in reloaded.GetRootGameObjects())
        {
            savedConfig = rootGo.GetComponentInChildren<ControllerConfigScreen>(true);
            if (savedConfig != null) break;
        }
        SerializedProperty savedConfigCatalog = savedConfig != null
            ? new SerializedObject(savedConfig).FindProperty("catalog") : null;
        if (savedConfigCatalog == null || savedConfigCatalog.objectReferenceValue == null)
        {
            const string msg = "Build Home Scene: the saved HomeScene lost the ControllerConfigScreen's " +
                               "RobotModelCatalog reference — the mapping screen would show no mechanisms.";
            Debug.LogError(msg);
            if (!interactive) throw new InvalidOperationException(msg);
            EditorUtility.DisplayDialog("Build Home Scene", msg, "OK");
            return false;
        }
        return true;
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
        // inactive template button (so catalog edits need no scene change), plus the control
        // size/opacity sliders and the controller-config entry point. Taller than the main
        // panel to fit everything under the list (which absorbs the leftover height).
        GameObject settingsPanel = CreatePanel("SettingsPanel", canvasGo.transform, new Vector2(680f, 980f));
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

        // Controls opacity: label (live percentage readout) + slider. Applies to joysticks AND
        // the on-screen controller buttons (ControlsAppearance reads it in the field scene).
        TextMeshProUGUI opacityLabel = CreateText("ControlsOpacityLabel", settingsPanel.transform,
            "Controls Opacity", 40f);
        opacityLabel.fontStyle = FontStyles.Bold;
        SetLayoutHeight(opacityLabel.gameObject, 56f);

        Slider opacitySlider = CreateSlider("ControlsOpacitySlider", settingsPanel.transform,
            ControlsOpacitySettings.MinOpacity, ControlsOpacitySettings.MaxOpacity,
            ControlsOpacitySettings.DefaultOpacity);
        SetLayoutHeight(opacitySlider.gameObject, 56f);

        // Entry point to the button -> mechanism mapping screen.
        Button configureButton = CreateButton("ConfigureControllerButton", settingsPanel.transform,
            "Configure Controller", 40f, AccentColor);
        SetLayoutHeight(configureButton.gameObject, 84f);

        // Entry point to the drag-to-reposition control layout screen.
        Button editLayoutButton = CreateButton("EditLayoutButton", settingsPanel.transform,
            "Edit Control Layout", 40f, AccentColor);
        SetLayoutHeight(editLayoutButton.gameObject, 84f);

        Button backButton = CreateButton("BackButton", settingsPanel.transform, "Back", 44f, AccentColor);
        SetLayoutHeight(backButton.gameObject, 96f);

        settingsPanel.SetActive(false); // controller shows it via OnSettingsPressed

        // Controller config panel (inactive; opened from Settings > Configure Controller).
        ControllerConfigParts configParts = BuildControllerConfigPanel(canvasGo.transform);

        // Control layout panel (inactive; opened from Settings > Edit Control Layout).
        ControlsLayoutParts layoutParts = BuildControlsLayoutPanel(canvasGo.transform);

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
        so.FindProperty("controlsOpacitySlider").objectReferenceValue = opacitySlider;
        so.FindProperty("controlsOpacityLabel").objectReferenceValue = opacityLabel;

        // Controller config screen: same root object, wired to the diagram it opens.
        ControllerConfigScreen configScreen = homeRoot.AddComponent<ControllerConfigScreen>();
        SerializedObject configSo = new SerializedObject(configScreen);
        configSo.FindProperty("catalog").objectReferenceValue = freshCatalog != null ? freshCatalog : catalog;
        configSo.FindProperty("panel").objectReferenceValue = configParts.panel;
        configSo.FindProperty("headerLabel").objectReferenceValue = configParts.header;
        configSo.FindProperty("emptyStateLabel").objectReferenceValue = configParts.emptyState;
        SerializedProperty buttonsProp = configSo.FindProperty("buttons");
        SerializedProperty labelsProp = configSo.FindProperty("assignmentLabels");
        buttonsProp.arraySize = configParts.buttons.Length;
        labelsProp.arraySize = configParts.captions.Length;
        for (int i = 0; i < configParts.buttons.Length; i++)
        {
            buttonsProp.GetArrayElementAtIndex(i).objectReferenceValue = configParts.buttons[i];
            labelsProp.GetArrayElementAtIndex(i).objectReferenceValue = configParts.captions[i];
        }
        configSo.FindProperty("assignmentPanel").objectReferenceValue = configParts.assignmentPanel;
        configSo.FindProperty("assignmentHeader").objectReferenceValue = configParts.assignmentHeader;
        configSo.FindProperty("assignmentListParent").objectReferenceValue = configParts.assignmentList;
        configSo.FindProperty("assignmentRowTemplate").objectReferenceValue = configParts.rowTemplate;
        configSo.FindProperty("clearButton").objectReferenceValue = configParts.clearButton;
        configSo.FindProperty("cancelButton").objectReferenceValue = configParts.cancelButton;
        configSo.ApplyModifiedPropertiesWithoutUndo();

        so.FindProperty("controllerConfig").objectReferenceValue = configScreen;

        // Controls layout screen: same root object, wired to the preview it drives.
        ControlsLayoutScreen layoutScreen = homeRoot.AddComponent<ControlsLayoutScreen>();
        SerializedObject layoutSo = new SerializedObject(layoutScreen);
        layoutSo.FindProperty("panel").objectReferenceValue = layoutParts.panel;
        SerializedProperty proxiesProp = layoutSo.FindProperty("proxies");
        proxiesProp.arraySize = layoutParts.proxies.Length;
        for (int i = 0; i < layoutParts.proxies.Length; i++)
            proxiesProp.GetArrayElementAtIndex(i).objectReferenceValue = layoutParts.proxies[i];
        layoutSo.ApplyModifiedPropertiesWithoutUndo();

        so.FindProperty("controlsLayout").objectReferenceValue = layoutScreen;
        so.ApplyModifiedPropertiesWithoutUndo();

        UnityEventTools.AddPersistentListener(driveButton.onClick, controller.OnDrivePressed);
        UnityEventTools.AddPersistentListener(settingsButton.onClick, controller.OnSettingsPressed);
        UnityEventTools.AddPersistentListener(backButton.onClick, controller.OnBackPressed);
        UnityEventTools.AddPersistentListener(configureButton.onClick, controller.OnConfigureControllerPressed);
        UnityEventTools.AddPersistentListener(configParts.backButton.onClick, controller.OnConfigBackPressed);
        UnityEventTools.AddPersistentListener(editLayoutButton.onClick, controller.OnEditLayoutPressed);
        UnityEventTools.AddPersistentListener(layoutParts.backButton.onClick, controller.OnLayoutBackPressed);
        UnityEventTools.AddPersistentListener(layoutParts.resetButton.onClick, layoutScreen.OnResetPressed);
    }

    // --- Controller config panel ---

    private class ControllerConfigParts
    {
        public GameObject panel;
        public TextMeshProUGUI header;
        public GameObject emptyState;
        public Button[] buttons = new Button[ControllerMapSettings.ButtonCount];
        public TextMeshProUGUI[] captions = new TextMeshProUGUI[ControllerMapSettings.ButtonCount];
        public GameObject assignmentPanel;
        public TextMeshProUGUI assignmentHeader;
        public Transform assignmentList;
        public Button rowTemplate;
        public Button clearButton;
        public Button cancelButton;
        public Button backButton;
    }

    // Near-fullscreen panel showing a stylized controller: 12 tappable buttons laid out like
    // the drive scene's on-screen controller (shoulders top corners, arrow + XBAY diamonds in
    // the middle, decorative stick circles below), each with an assignment caption beneath it.
    // Tapping a button opens the assignment popup; ControllerConfigScreen drives the logic.
    private static ControllerConfigParts BuildControllerConfigPanel(Transform canvas)
    {
        var parts = new ControllerConfigParts();

        GameObject panel = CreatePanel("ControllerConfigPanel", canvas, new Vector2(1700f, 980f));
        parts.panel = panel;

        parts.header = CreateText("ConfigHeader", panel.transform, "Controller", 48f);
        parts.header.fontStyle = FontStyles.Bold;
        RectTransform headerRect = parts.header.rectTransform;
        headerRect.anchorMin = headerRect.anchorMax = new Vector2(0.5f, 1f);
        headerRect.pivot = new Vector2(0.5f, 1f);
        headerRect.anchoredPosition = new Vector2(0f, -20f);
        headerRect.sizeDelta = new Vector2(1200f, 60f);

        TextMeshProUGUI emptyState = CreateText("EmptyStateLabel", panel.transform,
            "This robot has no mechanisms to control. Import a URDF robot with arm or piston " +
            "joints, then map its buttons here.", 28f);
        RectTransform emptyRect = emptyState.rectTransform;
        emptyRect.anchorMin = emptyRect.anchorMax = new Vector2(0.5f, 1f);
        emptyRect.pivot = new Vector2(0.5f, 1f);
        emptyRect.anchoredPosition = new Vector2(0f, -84f);
        emptyRect.sizeDelta = new Vector2(1500f, 76f);
        parts.emptyState = emptyState.gameObject;

        GameObject diagram = CreateUIObject("ControllerDiagram", panel.transform);
        RectTransform diagramRect = (RectTransform)diagram.transform;
        diagramRect.anchorMin = diagramRect.anchorMax = new Vector2(0.5f, 0.5f);
        diagramRect.pivot = new Vector2(0.5f, 0.5f);
        diagramRect.anchoredPosition = new Vector2(0f, -30f);
        diagramRect.sizeDelta = new Vector2(1560f, 700f);
        Image diagramImage = diagram.AddComponent<Image>();
        diagramImage.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd");
        diagramImage.type = Image.Type.Sliced;
        diagramImage.color = ListColor;

        AddDecorativeStick(diagram.transform, "LeftStickMarker", new Vector2(-450f, -180f));
        AddDecorativeStick(diagram.transform, "RightStickMarker", new Vector2(450f, -180f));

        // ControllerButton order: L1 L2 R1 R2 | Up Down Left Right | X B A Y.
        Vector2 pillSize = new Vector2(170f, 70f);
        parts.buttons[0] = CreateConfigButton(diagram.transform, "CfgL1", "L1",
            new Vector2(-600f, 250f), pillSize, false, out parts.captions[0]);
        parts.buttons[1] = CreateConfigButton(diagram.transform, "CfgL2", "L2",
            new Vector2(-600f, 130f), pillSize, false, out parts.captions[1]);
        parts.buttons[2] = CreateConfigButton(diagram.transform, "CfgR1", "R1",
            new Vector2(600f, 250f), pillSize, false, out parts.captions[2]);
        parts.buttons[3] = CreateConfigButton(diagram.transform, "CfgR2", "R2",
            new Vector2(600f, 130f), pillSize, false, out parts.captions[3]);

        // Diamond centers sit +-260 from the panel center: far enough apart that the two
        // inner buttons' 220px-wide assignment captions (CfgRight at x-140, CfgY at x+140)
        // never overlap each other.
        Vector2 roundSize = new Vector2(76f, 76f);
        parts.buttons[4] = CreateConfigButton(diagram.transform, "CfgUp", "Up",
            new Vector2(-260f, 180f), roundSize, true, out parts.captions[4]);
        parts.buttons[5] = CreateConfigButton(diagram.transform, "CfgDown", "Down",
            new Vector2(-260f, -60f), roundSize, true, out parts.captions[5]);
        parts.buttons[6] = CreateConfigButton(diagram.transform, "CfgLeft", "Left",
            new Vector2(-380f, 60f), roundSize, true, out parts.captions[6]);
        parts.buttons[7] = CreateConfigButton(diagram.transform, "CfgRight", "Right",
            new Vector2(-140f, 60f), roundSize, true, out parts.captions[7]);

        parts.buttons[8] = CreateConfigButton(diagram.transform, "CfgX", "X",
            new Vector2(260f, 180f), roundSize, true, out parts.captions[8]);
        parts.buttons[9] = CreateConfigButton(diagram.transform, "CfgB", "B",
            new Vector2(380f, 60f), roundSize, true, out parts.captions[9]);
        parts.buttons[10] = CreateConfigButton(diagram.transform, "CfgA", "A",
            new Vector2(260f, -60f), roundSize, true, out parts.captions[10]);
        parts.buttons[11] = CreateConfigButton(diagram.transform, "CfgY", "Y",
            new Vector2(140f, 60f), roundSize, true, out parts.captions[11]);

        parts.backButton = CreateButton("ConfigBackButton", panel.transform, "Back", 36f, AccentColor);
        RectTransform backRect = (RectTransform)parts.backButton.transform;
        backRect.anchorMin = backRect.anchorMax = new Vector2(0.5f, 0f);
        backRect.pivot = new Vector2(0.5f, 0f);
        backRect.anchoredPosition = new Vector2(0f, 18f);
        backRect.sizeDelta = new Vector2(240f, 64f);

        // Assignment popup: header + scrollable option list + Clear/Cancel. Scrolls because a
        // many-motor robot yields two rows per motor.
        GameObject assignmentPanel = CreatePanel("AssignmentPanel", panel.transform, new Vector2(720f, 780f));
        AddVerticalLayout(assignmentPanel, 32, 16f);
        parts.assignmentPanel = assignmentPanel;

        parts.assignmentHeader = CreateText("AssignmentHeader", assignmentPanel.transform, "Assign", 40f);
        parts.assignmentHeader.fontStyle = FontStyles.Bold;
        SetLayoutHeight(parts.assignmentHeader.gameObject, 56f);

        GameObject scroll = CreateUIObject("AssignmentScroll", assignmentPanel.transform);
        Image scrollImage = scroll.AddComponent<Image>(); // list backdrop + drag-catcher
        scrollImage.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd");
        scrollImage.type = Image.Type.Sliced;
        scrollImage.color = ListColor;
        scroll.AddComponent<RectMask2D>();
        ScrollRect scrollRect = scroll.AddComponent<ScrollRect>();
        scrollRect.horizontal = false;
        LayoutElement scrollElement = scroll.AddComponent<LayoutElement>();
        scrollElement.flexibleHeight = 1f; // the list absorbs the leftover popup height

        GameObject list = CreateUIObject("AssignmentList", scroll.transform);
        RectTransform listRect = (RectTransform)list.transform;
        listRect.anchorMin = new Vector2(0f, 1f);
        listRect.anchorMax = new Vector2(1f, 1f);
        listRect.pivot = new Vector2(0.5f, 1f);
        listRect.anchoredPosition = Vector2.zero;
        listRect.sizeDelta = Vector2.zero;
        VerticalLayoutGroup listLayout = AddVerticalLayout(list, 16, 12f);
        listLayout.childAlignment = TextAnchor.UpperCenter;
        ContentSizeFitter fitter = list.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scrollRect.content = listRect;
        scrollRect.viewport = (RectTransform)scroll.transform;
        parts.assignmentList = list.transform;

        parts.rowTemplate = CreateButton("AssignmentRowTemplate", list.transform,
            "Mechanism — Forward", 32f, NeutralColor);
        SetLayoutHeight(parts.rowTemplate.gameObject, 64f);
        parts.rowTemplate.gameObject.SetActive(false); // template stays inactive; screen clones it

        parts.clearButton = CreateButton("ClearButton", assignmentPanel.transform,
            "Clear Assignment", 36f, NeutralColor);
        SetLayoutHeight(parts.clearButton.gameObject, 72f);
        parts.cancelButton = CreateButton("CancelButton", assignmentPanel.transform, "Cancel", 36f, AccentColor);
        SetLayoutHeight(parts.cancelButton.gameObject, 72f);

        assignmentPanel.SetActive(false);
        panel.SetActive(false); // ControllerConfigScreen.Open shows it
        return parts;
    }

    // Diagram button + the assignment caption below it (a sibling, so button tints don't dim it).
    private static Button CreateConfigButton(Transform parent, string name, string label,
        Vector2 position, Vector2 size, bool round, out TextMeshProUGUI caption)
    {
        GameObject go = CreateUIObject(name, parent);
        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;

        Image image = go.AddComponent<Image>();
        if (round)
        {
            image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");
        }
        else
        {
            image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
            image.type = Image.Type.Sliced;
        }
        image.color = NeutralColor;
        Button button = go.AddComponent<Button>();
        button.targetGraphic = image;

        TextMeshProUGUI text = CreateText("Label", go.transform, label, round ? 22f : 32f);
        text.fontStyle = FontStyles.Bold;
        text.raycastTarget = false;
        RectTransform textRect = text.rectTransform;
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;

        caption = CreateText(name + "_Assign", parent, string.Empty, 20f);
        caption.color = AccentColor;
        caption.raycastTarget = false;
        RectTransform captionRect = caption.rectTransform;
        captionRect.anchorMin = captionRect.anchorMax = new Vector2(0.5f, 0.5f);
        captionRect.pivot = new Vector2(0.5f, 0.5f);
        captionRect.anchoredPosition = new Vector2(position.x, position.y - size.y * 0.5f - 22f);
        captionRect.sizeDelta = new Vector2(220f, 26f);
        return button;
    }

    private static void AddDecorativeStick(Transform parent, string name, Vector2 position)
    {
        GameObject stick = CreateUIObject(name, parent);
        RectTransform rect = (RectTransform)stick.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = new Vector2(140f, 140f);
        Image image = stick.AddComponent<Image>();
        image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Knob.psd");
        image.color = NeutralColor;
        image.raycastTarget = false;

        TextMeshProUGUI caption = CreateText(name + "_Caption", parent, "Drive (fixed)", 22f);
        caption.raycastTarget = false;
        RectTransform captionRect = caption.rectTransform;
        captionRect.anchorMin = captionRect.anchorMax = new Vector2(0.5f, 0.5f);
        captionRect.pivot = new Vector2(0.5f, 0.5f);
        captionRect.anchoredPosition = new Vector2(position.x, position.y - 92f);
        captionRect.sizeDelta = new Vector2(240f, 28f);
    }

    // --- Control layout panel ---

    private class ControlsLayoutParts
    {
        public GameObject panel;
        public DraggableControlProxy[] proxies;
        public Button resetButton;
        public Button backButton;
    }

    // Near-fullscreen panel for repositioning the on-screen controls: a scaled 1920x1080 preview
    // of the field with one draggable proxy tile per control group (joysticks, shoulder pairs,
    // arrow diamond, XYAB diamond). The preview's LOCAL space is the 1920x1080 reference (a
    // localScale shrinks it to fit), so a proxy's anchoredPosition is in reference pixels and the
    // drag delta transfers 1:1 to the real control. DraggableControlProxy saves the deltas;
    // ControlsAppearance applies them in the field scene.
    private static ControlsLayoutParts BuildControlsLayoutPanel(Transform canvas)
    {
        var parts = new ControlsLayoutParts();

        GameObject panel = CreatePanel("ControlsLayoutPanel", canvas, new Vector2(1700f, 980f));
        parts.panel = panel;

        TextMeshProUGUI header = CreateText("LayoutHeader", panel.transform, "Edit Control Layout", 48f);
        header.fontStyle = FontStyles.Bold;
        RectTransform headerRect = header.rectTransform;
        headerRect.anchorMin = headerRect.anchorMax = new Vector2(0.5f, 1f);
        headerRect.pivot = new Vector2(0.5f, 1f);
        headerRect.anchoredPosition = new Vector2(0f, -20f);
        headerRect.sizeDelta = new Vector2(1200f, 60f);

        TextMeshProUGUI hint = CreateText("LayoutHint", panel.transform,
            "Drag each control to reposition it. Arrows and X/Y/A/B each move as one group.", 26f);
        RectTransform hintRect = hint.rectTransform;
        hintRect.anchorMin = hintRect.anchorMax = new Vector2(0.5f, 1f);
        hintRect.pivot = new Vector2(0.5f, 1f);
        hintRect.anchoredPosition = new Vector2(0f, -82f);
        hintRect.sizeDelta = new Vector2(1500f, 40f);

        // The preview: local space = 1920x1080 reference, scaled to fit under the header.
        GameObject preview = CreateUIObject("LayoutPreview", panel.transform);
        RectTransform previewRect = (RectTransform)preview.transform;
        previewRect.anchorMin = previewRect.anchorMax = new Vector2(0.5f, 0.5f);
        previewRect.pivot = new Vector2(0.5f, 0.5f);
        previewRect.anchoredPosition = new Vector2(0f, -20f);
        previewRect.sizeDelta = new Vector2(1920f, 1080f);
        previewRect.localScale = new Vector3(0.7f, 0.7f, 1f); // 1344x756 on screen
        Image previewImage = preview.AddComponent<Image>();
        previewImage.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/Background.psd");
        previewImage.type = Image.Type.Sliced;
        previewImage.color = ListColor;
        previewImage.raycastTarget = false; // drags belong to the proxies, not the backdrop

        parts.proxies = new DraggableControlProxy[ControlsLayout.Controls.Length];
        for (int i = 0; i < ControlsLayout.Controls.Length; i++)
            parts.proxies[i] = BuildLayoutProxy(previewRect, ControlsLayout.Controls[i]);

        parts.resetButton = CreateButton("LayoutResetButton", panel.transform, "Reset", 34f, NeutralColor);
        RectTransform resetRect = (RectTransform)parts.resetButton.transform;
        resetRect.anchorMin = resetRect.anchorMax = new Vector2(0.5f, 0f);
        resetRect.pivot = new Vector2(1f, 0f);
        resetRect.anchoredPosition = new Vector2(-12f, 18f);
        resetRect.sizeDelta = new Vector2(240f, 64f);

        parts.backButton = CreateButton("LayoutBackButton", panel.transform, "Back", 34f, AccentColor);
        RectTransform backRect = (RectTransform)parts.backButton.transform;
        backRect.anchorMin = backRect.anchorMax = new Vector2(0.5f, 0f);
        backRect.pivot = new Vector2(0f, 0f);
        backRect.anchoredPosition = new Vector2(12f, 18f);
        backRect.sizeDelta = new Vector2(240f, 64f);

        panel.SetActive(false); // ControlsLayoutScreen.Open shows it
        return parts;
    }

    // One draggable proxy tile (label + image) inside the preview, standing in for a field control.
    private static DraggableControlProxy BuildLayoutProxy(RectTransform previewRect, ControlsLayout.ControlInfo info)
    {
        GameObject go = CreateUIObject(info.name + "Proxy", previewRect);
        RectTransform rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = info.previewCenter;
        rect.sizeDelta = info.previewSize;

        Image image = go.AddComponent<Image>();
        image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        image.type = Image.Type.Sliced;
        image.color = AccentColor;
        // raycastTarget stays true: the image is the drag handle.

        TextMeshProUGUI label = CreateText("Label", go.transform, info.label, 30f);
        label.fontStyle = FontStyles.Bold;
        label.raycastTarget = false;
        RectTransform labelRect = label.rectTransform;
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;

        DraggableControlProxy proxy = go.AddComponent<DraggableControlProxy>();
        proxy.controlName = info.name;
        proxy.dragArea = previewRect;
        proxy.basePosition = info.previewCenter;
        return proxy;
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

        // Idempotency: never add a second button (and never touch the joystick objects). The
        // position IS re-asserted: Home used to live in the top-left corner, which now belongs
        // to the L1/L2 shoulder buttons (Build Drive Controls), so both tools must agree on the
        // top-center spot.
        Transform existingHome = canvasGo.transform.Find("HomeButton");
        if (existingHome != null)
        {
            RectTransform existingRect = (RectTransform)existingHome;
            if (existingRect.pivot == new Vector2(1f, 1f) &&
                existingRect.anchoredPosition == new Vector2(-12f, -24f))
                return "already present";
            Undo.RecordObject(existingRect, "Move Home Button");
            PositionHomeButton(existingRect);
            EditorSceneManager.MarkSceneDirty(sampleScene);
            added = true; // reuse the flag so the caller saves the scene
            return "re-positioned to the top center";
        }

        Button homeButton = CreateButton("HomeButton", canvasGo.transform, "Home", 32f, AccentColor);
        PositionHomeButton((RectTransform)homeButton.transform);

        SceneNavButton nav = homeButton.gameObject.AddComponent<SceneNavButton>();
        nav.sceneName = "HomeScene";
        UnityEventTools.AddPersistentListener(homeButton.onClick, nav.Load);

        Undo.RegisterCreatedObjectUndo(homeButton.gameObject, "Add Home Button");
        EditorSceneManager.MarkSceneDirty(sampleScene);
        added = true;
        return "added";
    }

    // Top center, in a pair with the Reset button that Build Drive Controls adds to its right.
    private static void PositionHomeButton(RectTransform rect)
    {
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = new Vector2(-12f, -24f);
        rect.sizeDelta = new Vector2(160f, 64f);
    }

    internal static RectTransform FindDescendantRect(Scene scene, string name)
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

    internal static GameObject CreateUIObject(string name, Transform parent)
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

    internal static TextMeshProUGUI CreateText(string name, Transform parent, string text, float fontSize)
    {
        GameObject go = CreateUIObject(name, parent);
        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = TextColor;
        tmp.alignment = TextAlignmentOptions.Center;
        return tmp;
    }

    internal static Button CreateButton(string name, Transform parent, string label, float fontSize, Color color)
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
