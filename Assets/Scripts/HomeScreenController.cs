using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Drives the home screen UI: a main panel (Drive / Settings) and a settings panel where the
// player picks a robot model from the RobotModelCatalog.
//
// The model list is built at runtime by cloning an inactive template Button per catalog entry,
// so adding a model to the catalog asset needs no scene edit. The selection is persisted via
// RobotModelCatalog.SelectedModelId (PlayerPrefs-backed) and shown by tinting the selected
// entry's button image with the accent color.
//
// Usage: the Tools > RoboSim > Scenes > Build Home Screen tool creates the HomeScene, adds this component,
// and wires all references + button onClicks. Drive loads SampleScene.
public class HomeScreenController : MonoBehaviour
{
    [Header("Data")]
    [SerializeField] private RobotModelCatalog catalog;

    [Header("Panels")]
    [SerializeField] private GameObject mainPanel;
    [SerializeField] private GameObject settingsPanel;
    [Tooltip("Full-screen loading overlay shown when Drive is pressed. Its click-blocking backdrop stops spam-taps while the field scene loads.")]
    [SerializeField] private GameObject loadingOverlay;

    [Header("Model List")]
    [Tooltip("Parent the model buttons are cloned under (has the VerticalLayoutGroup).")]
    [SerializeField] private Transform modelListParent;
    [Tooltip("Inactive template Button under the list parent; cloned once per catalog entry.")]
    [SerializeField] private Button modelButtonTemplate;
    [Tooltip("Toggles edit mode: while on, tapping a model removes it from the catalog instead of selecting it.")]
    [SerializeField] private Button editModelsButton;

    [Header("Selection Tint")]
    [SerializeField] private Color selectedTint = new Color(0.24f, 0.49f, 0.92f); // accent blue
    [SerializeField] private Color normalTint = new Color(0.23f, 0.25f, 0.30f);   // neutral dark
    [Tooltip("Row tint while Edit mode is on, signalling that tapping a model deletes it.")]
    [SerializeField] private Color deleteTint = new Color(0.72f, 0.25f, 0.25f);   // delete red

    [Header("Joystick Size")]
    [Tooltip("Slider that scales the on-screen controls (persisted via JoystickSettings).")]
    [SerializeField] private Slider joystickSizeSlider;
    [Tooltip("Label above the slider; shows the current size as a percentage.")]
    [SerializeField] private TMP_Text joystickSizeLabel;

    [Header("Controls Opacity")]
    [Tooltip("Slider for the on-screen controls' opacity (persisted via ControlsOpacitySettings).")]
    [SerializeField] private Slider controlsOpacitySlider;
    [Tooltip("Label above the slider; shows the current opacity as a percentage.")]
    [SerializeField] private TMP_Text controlsOpacityLabel;

    [Header("Match Loading")]
    [Tooltip("Checkbox for Automatic Matchloading (persisted via MatchLoadSettings). When off, the field scene shows a Match Load button for manual spawns.")]
    [SerializeField] private Toggle automaticMatchloadToggle;

    [Header("Drive")]
    [Tooltip("Checkbox for Reverse Drive Direction (persisted via ReverseDriveSettings). Flips which end of the robot the drive controls treat as front.")]
    [SerializeField] private Toggle reverseDriveToggle;

    [Header("Controller Config")]
    [Tooltip("The Configure Controller sub-screen (button -> mechanism mapping).")]
    [SerializeField] private ControllerConfigScreen controllerConfig;

    [Header("Controls Layout")]
    [Tooltip("The Edit Control Layout sub-screen (drag on-screen controls to reposition them).")]
    [SerializeField] private ControlsLayoutScreen controlsLayout;

    // Clones built from the template, paired with the catalog id each one selects.
    private readonly List<KeyValuePair<Button, string>> modelButtons = new List<KeyValuePair<Button, string>>();

    // Guards against the field scene being loaded twice from repeated Drive taps.
    private bool isLoading;

    // While true, the model list is in edit mode: tapping a row deletes that model from the catalog.
    private bool editMode;

    void Start()
    {
        if (mainPanel != null) mainPanel.SetActive(true);
        if (settingsPanel != null) settingsPanel.SetActive(false);
        if (loadingOverlay != null) loadingOverlay.SetActive(false);
        BuildModelList();
        UpdateEditButtonLabel();
        InitJoystickSizeControl();
        InitControlsOpacityControl();
        InitAutomaticMatchloadControl();
        InitReverseDriveControl();
    }

    // --- Button hooks (wired as persistent onClick listeners by the Build Home Scene tool) ---

    public void OnDrivePressed()
    {
        // Ignore repeat taps: loading SampleScene is a visible hitch, and without feedback players
        // spam Drive. Show the overlay (its backdrop also swallows further taps), then load async so
        // the overlay actually renders before the hitch instead of the frame freezing on a blocking
        // LoadScene.
        if (isLoading) return;
        isLoading = true;
        if (loadingOverlay != null) loadingOverlay.SetActive(true);
        StartCoroutine(LoadFieldScene());
    }

    private IEnumerator LoadFieldScene()
    {
        yield return null; // let the overlay paint one frame first
        AsyncOperation op = SceneManager.LoadSceneAsync("SampleScene");
        while (op != null && !op.isDone) yield return null;
    }

    public void OnSettingsPressed()
    {
        if (mainPanel != null) mainPanel.SetActive(false);
        if (settingsPanel != null) settingsPanel.SetActive(true);
    }

    public void OnBackPressed()
    {
        if (settingsPanel != null) settingsPanel.SetActive(false);
        if (mainPanel != null) mainPanel.SetActive(true);
    }

    public void OnConfigureControllerPressed()
    {
        if (controllerConfig == null) return; // older scene without the config screen
        if (settingsPanel != null) settingsPanel.SetActive(false);
        controllerConfig.Open();
    }

    public void OnConfigBackPressed()
    {
        if (controllerConfig != null) controllerConfig.Close();
        if (settingsPanel != null) settingsPanel.SetActive(true);
    }

    public void OnEditLayoutPressed()
    {
        if (controlsLayout == null) return; // older scene without the layout screen
        if (settingsPanel != null) settingsPanel.SetActive(false);
        controlsLayout.Open();
    }

    public void OnLayoutBackPressed()
    {
        if (controlsLayout != null) controlsLayout.Close();
        if (settingsPanel != null) settingsPanel.SetActive(true);
    }

    // --- Model list ---

    private void BuildModelList()
    {
        if (catalog == null || modelListParent == null || modelButtonTemplate == null)
        {
            Debug.LogWarning("HomeScreenController: catalog / model list references are not assigned; " +
                             "model list not built.", this);
            return;
        }

        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry == null || string.IsNullOrEmpty(entry.id)) continue;

            Button clone = Instantiate(modelButtonTemplate, modelListParent);
            clone.name = "Model_" + entry.id;
            clone.gameObject.SetActive(true); // template itself stays inactive

            TMP_Text label = clone.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = editMode ? "Remove  " + entry.displayName : entry.displayName;

            string id = entry.id; // capture per-iteration copy for the closure
            clone.onClick.AddListener(() => OnModelButtonPressed(id));
            modelButtons.Add(new KeyValuePair<Button, string>(clone, id));
        }

        RefreshHighlight();
    }

    // Destroy the current clones and rebuild the list — after a delete or an edit-mode toggle, so the
    // labels, tints, and click behavior all reflect the current mode.
    private void RebuildModelList()
    {
        foreach (KeyValuePair<Button, string> pair in modelButtons)
        {
            if (pair.Key == null) continue;
            pair.Key.gameObject.SetActive(false); // hide now; Destroy is deferred to frame end
            Destroy(pair.Key.gameObject);
        }
        modelButtons.Clear();
        BuildModelList();
    }

    // A model row does one of two things depending on the mode: pick it, or (in edit mode) delete it.
    private void OnModelButtonPressed(string id)
    {
        if (editMode) DeleteModel(id);
        else SelectModel(id);
    }

    private void SelectModel(string id)
    {
        catalog.SelectedModelId = id;
        RefreshHighlight();
    }

    // Remove a model entry from the catalog. In the Editor (where robots are set up, including Play
    // mode) this is persisted to the catalog asset so it stays gone across restarts; in a player build
    // the asset is read-only, so it is an in-memory removal for the session. The selection self-heals:
    // RobotModelCatalog.SelectedModelId / SelectedModel fall back to the first entry when the saved id
    // is gone, and RobotSpawner falls back to the first entry with a prefab. Only the catalog entry is
    // removed — the prefab and mesh assets on disk are left untouched.
    private void DeleteModel(string id)
    {
        if (catalog == null || catalog.models == null) return;
        int removed = catalog.models.RemoveAll(e => e != null && e.id == id);
        if (removed == 0) return;

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(catalog);
        UnityEditor.AssetDatabase.SaveAssets();
#endif

        RebuildModelList();
    }

    // --- Edit mode ---

    // Wired as a persistent onClick by the Build Home Scene tool. Toggles whether tapping a model
    // selects or deletes it, and rebuilds the list so the rows show the current mode.
    public void OnEditModelsPressed()
    {
        if (catalog == null) return; // nothing to edit
        editMode = !editMode;
        UpdateEditButtonLabel();
        RebuildModelList();
    }

    private void UpdateEditButtonLabel()
    {
        if (editModelsButton == null) return; // older HomeScene built before this button existed
        TMP_Text label = editModelsButton.GetComponentInChildren<TMP_Text>(true);
        if (label != null) label.text = editMode ? "Done" : "Edit Models";
    }

    // Tint the selected entry with the accent color so the current choice is visible; in edit mode
    // every row takes the delete tint so it reads as "tap to remove".
    private void RefreshHighlight()
    {
        string selected = catalog != null ? catalog.SelectedModelId : null;
        foreach (KeyValuePair<Button, string> pair in modelButtons)
        {
            if (pair.Key == null || pair.Key.image == null) continue;
            pair.Key.image.color = editMode ? deleteTint : (pair.Value == selected ? selectedTint : normalTint);
        }
    }

    // --- Joystick size ---

    // Point the slider at the saved size and keep the label in sync. Guarded so an older
    // HomeScene built before this control existed (slider unassigned) still runs without error.
    private void InitJoystickSizeControl()
    {
        if (joystickSizeSlider == null) return;

        joystickSizeSlider.minValue = JoystickSettings.MinScale;
        joystickSizeSlider.maxValue = JoystickSettings.MaxScale;
        joystickSizeSlider.wholeNumbers = false;
        joystickSizeSlider.SetValueWithoutNotify(JoystickSettings.Scale); // don't persist on the initial set
        joystickSizeSlider.onValueChanged.AddListener(OnJoystickSizeChanged);
        UpdateJoystickSizeLabel(JoystickSettings.Scale);
    }

    private void OnJoystickSizeChanged(float value)
    {
        JoystickSettings.Scale = value; // JoystickScaler reads this when the field scene loads
        UpdateJoystickSizeLabel(value);
    }

    private void UpdateJoystickSizeLabel(float value)
    {
        if (joystickSizeLabel != null)
            joystickSizeLabel.text = $"Joystick Size — {Mathf.RoundToInt(value * 100f)}%";
    }

    // --- Controls opacity ---

    // Same pattern as the size control; guarded so an older HomeScene still runs without it.
    private void InitControlsOpacityControl()
    {
        if (controlsOpacitySlider == null) return;

        controlsOpacitySlider.minValue = ControlsOpacitySettings.MinOpacity;
        controlsOpacitySlider.maxValue = ControlsOpacitySettings.MaxOpacity;
        controlsOpacitySlider.wholeNumbers = false;
        controlsOpacitySlider.SetValueWithoutNotify(ControlsOpacitySettings.Opacity);
        controlsOpacitySlider.onValueChanged.AddListener(OnControlsOpacityChanged);
        UpdateControlsOpacityLabel(ControlsOpacitySettings.Opacity);
    }

    private void OnControlsOpacityChanged(float value)
    {
        ControlsOpacitySettings.Opacity = value; // ControlsAppearance reads this in the field scene
        UpdateControlsOpacityLabel(value);
    }

    private void UpdateControlsOpacityLabel(float value)
    {
        if (controlsOpacityLabel != null)
            controlsOpacityLabel.text = $"Controls Opacity — {Mathf.RoundToInt(value * 100f)}%";
    }

    // --- Automatic matchloading ---

    // Same pattern as the sliders; guarded so an older HomeScene still runs without the toggle.
    // MatchLoadButton and MatchLoaderController read the setting when the field scene loads.
    private void InitAutomaticMatchloadControl()
    {
        if (automaticMatchloadToggle == null) return;

        automaticMatchloadToggle.SetIsOnWithoutNotify(MatchLoadSettings.Automatic);
        automaticMatchloadToggle.onValueChanged.AddListener(value => MatchLoadSettings.Automatic = value);
    }

    // --- Reverse drive direction ---

    // Same pattern as the matchloading toggle; guarded so an older HomeScene still runs without it.
    // RobotMotorController reads the setting live when driving in the field scene.
    private void InitReverseDriveControl()
    {
        if (reverseDriveToggle == null) return;

        reverseDriveToggle.SetIsOnWithoutNotify(ReverseDriveSettings.Reversed);
        reverseDriveToggle.onValueChanged.AddListener(value => ReverseDriveSettings.Reversed = value);
    }
}
