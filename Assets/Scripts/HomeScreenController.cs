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

    [Header("Model List")]
    [Tooltip("Parent the model buttons are cloned under (has the VerticalLayoutGroup).")]
    [SerializeField] private Transform modelListParent;
    [Tooltip("Inactive template Button under the list parent; cloned once per catalog entry.")]
    [SerializeField] private Button modelButtonTemplate;

    [Header("Selection Tint")]
    [SerializeField] private Color selectedTint = new Color(0.24f, 0.49f, 0.92f); // accent blue
    [SerializeField] private Color normalTint = new Color(0.23f, 0.25f, 0.30f);   // neutral dark

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

    [Header("Controller Config")]
    [Tooltip("The Configure Controller sub-screen (button -> mechanism mapping).")]
    [SerializeField] private ControllerConfigScreen controllerConfig;

    // Clones built from the template, paired with the catalog id each one selects.
    private readonly List<KeyValuePair<Button, string>> modelButtons = new List<KeyValuePair<Button, string>>();

    void Start()
    {
        if (mainPanel != null) mainPanel.SetActive(true);
        if (settingsPanel != null) settingsPanel.SetActive(false);
        BuildModelList();
        InitJoystickSizeControl();
        InitControlsOpacityControl();
    }

    // --- Button hooks (wired as persistent onClick listeners by the Build Home Scene tool) ---

    public void OnDrivePressed()
    {
        SceneManager.LoadScene("SampleScene");
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
            if (label != null) label.text = entry.displayName;

            string id = entry.id; // capture per-iteration copy for the closure
            clone.onClick.AddListener(() => SelectModel(id));
            modelButtons.Add(new KeyValuePair<Button, string>(clone, id));
        }

        RefreshHighlight();
    }

    private void SelectModel(string id)
    {
        catalog.SelectedModelId = id;
        RefreshHighlight();
    }

    // Tint the selected entry's button with the accent color so the current choice is visible.
    private void RefreshHighlight()
    {
        string selected = catalog != null ? catalog.SelectedModelId : null;
        foreach (KeyValuePair<Button, string> pair in modelButtons)
        {
            if (pair.Key != null && pair.Key.image != null)
                pair.Key.image.color = pair.Value == selected ? selectedTint : normalTint;
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
}
