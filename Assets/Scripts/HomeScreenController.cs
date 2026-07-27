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
    [Tooltip("Checkbox for the lite field (persisted via FieldSceneSettings). Loads the stripped-down LiteScene instead of the full field — far cheaper to run.")]
    [SerializeField] private Toggle liteFieldToggle;

    [Header("Team Code")]
    [Tooltip("Where the player types an owner code to reveal a private robot (RobotOwnerSettings).")]
    [SerializeField] private TMP_InputField teamCodeInput;
    [Tooltip("Feedback line under the code box: what the last Unlock did, and how many codes are held.")]
    [SerializeField] private TMP_Text teamCodeStatusLabel;

    [Header("Recovery ID")]
    [Tooltip("Shows this device's uploader id — the only thing linking a player to robots they've sent in.")]
    [SerializeField] private TMP_Text recoveryIdLabel;
    [Tooltip("Copies the uploader id to the clipboard so the player can keep it somewhere safe.")]
    [SerializeField] private Button copyRecoveryIdButton;
    [Tooltip("Where a player pastes an uploader id from an old install to reclaim their submissions.")]
    [SerializeField] private TMP_InputField recoveryIdInput;

    [Header("Robot Inbox")]
    [Tooltip("Where submissions go, and where the inbox is read from. Same asset as SubmitRobotScreen's.")]
    [SerializeField] private RobotUploadConfig uploadConfig;
    [Tooltip("Notice shown over the main panel when a robot the player sent in has arrived.")]
    [SerializeField] private GameObject inboxNotice;
    [Tooltip("Line inside the notice naming what arrived.")]
    [SerializeField] private TMP_Text inboxLabel;

    [Header("Controller Config")]
    [Tooltip("The Configure Controller sub-screen (button -> mechanism mapping).")]
    [SerializeField] private ControllerConfigScreen controllerConfig;

    [Header("Controls Layout")]
    [Tooltip("The Edit Control Layout sub-screen (drag on-screen controls to reposition them).")]
    [SerializeField] private ControlsLayoutScreen controlsLayout;

    [Header("Submit a Robot")]
    [Tooltip("The Submit a Robot sub-screen (send your own FBX/URDF in to be set up).")]
    [SerializeField] private SubmitRobotScreen submitRobot;

    // Clones built from the template, paired with the catalog id each one selects.
    private readonly List<KeyValuePair<Button, string>> modelButtons = new List<KeyValuePair<Button, string>>();

    // Guards against the field scene being loaded twice from repeated Drive taps.
    private bool isLoading;

    // While true, the model list is in edit mode: tapping a row deletes that model from the catalog.
    private bool editMode;

    // Inbox items that would actually reveal something on this device — see OnInboxFetched.
    private readonly List<RobotInboxService.Item> pendingInbox = new List<RobotInboxService.Item>();

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
        InitLiteFieldControl();
        ShowHeldCodeCount();
        ShowRecoveryId();
        CheckInbox();
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
        // Full field or the lite one, per the Settings checkbox; FieldSceneSettings falls back to the
        // full field when the lite scene hasn't been built yet.
        AsyncOperation op = SceneManager.LoadSceneAsync(FieldSceneSettings.ActiveFieldScene);
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

    public void OnSubmitRobotPressed()
    {
        if (submitRobot == null) return; // older scene without the submit screen
        if (settingsPanel != null) settingsPanel.SetActive(false);
        submitRobot.Open();
    }

    public void OnSubmitBackPressed()
    {
        if (submitRobot != null) submitRobot.Close();
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

        // VisibleModels, not models: private entries stay out of the list until their owner enters
        // the code in Settings. Every other reader of the catalog filters the same way.
        foreach (RobotModelCatalog.Entry entry in catalog.VisibleModels)
        {
            Button clone = Instantiate(modelButtonTemplate, modelListParent);
            clone.name = "Model_" + entry.id;
            clone.gameObject.SetActive(true); // template itself stays inactive

            string title = string.IsNullOrWhiteSpace(entry.ownerLabel)
                ? entry.displayName
                : $"{entry.displayName}  ({entry.ownerLabel})";
            TMP_Text label = clone.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = editMode ? "Remove  " + title : title;

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

    // --- Team code (private robots) ---

    // Wired as a persistent onClick by the Build Home Scene tool. A code is only stored when it
    // actually matches a robot in this build: silently banking a typo'd code and showing no new
    // models would look identical to the feature being broken.
    public void OnUnlockCodePressed()
    {
        string code = RobotOwnerSettings.Normalize(teamCodeInput != null ? teamCodeInput.text : null);
        if (code.Length == 0)
        {
            SetCodeStatus("Type the code you were given, then press Unlock.");
            return;
        }
        if (RobotOwnerSettings.HasCode(code))
        {
            SetCodeStatus("That code is already entered.");
            return;
        }

        int matches = CountModelsWithCode(code);
        if (matches == 0)
        {
            SetCodeStatus("No robot in this app uses that code.");
            return;
        }

        RobotOwnerSettings.AddCode(code);
        if (teamCodeInput != null) teamCodeInput.text = string.Empty;
        RebuildModelList();
        SetCodeStatus(matches == 1 ? "Unlocked 1 robot." : $"Unlocked {matches} robots.");
    }

    // Clears every code entered on this device — for handing the phone to someone else, or just to
    // check what a teammate sees.
    public void OnForgetCodesPressed()
    {
        List<string> held = RobotOwnerSettings.AllCodes();
        if (held.Count == 0)
        {
            SetCodeStatus("No codes are entered on this device.");
            return;
        }

        int count = held.Count;
        foreach (string code in new List<string>(held)) RobotOwnerSettings.RemoveCode(code);
        RebuildModelList();
        SetCodeStatus(count == 1 ? "Forgot 1 code." : $"Forgot {count} codes.");
    }

    // Counts against the FULL catalog, not the visible subset — the whole point is to find the
    // entries this device currently can't see. An entry can name several codes (its own and its
    // team's), and matching any one of them counts, which is what makes one team code unlock a set.
    private int CountModelsWithCode(string normalizedCode)
    {
        if (catalog == null || catalog.models == null || string.IsNullOrEmpty(normalizedCode)) return 0;

        int matches = 0;
        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry == null || entry.visibility != RobotModelCatalog.Visibility.Private) continue;
            if (entry.OwnerCodes.Contains(normalizedCode)) matches++;
        }
        return matches;
    }

    private void ShowHeldCodeCount()
    {
        int held = RobotOwnerSettings.AllCodes().Count;
        if (held == 0) SetCodeStatus(string.Empty);
        else SetCodeStatus(held == 1 ? "1 code entered on this device." : $"{held} codes entered on this device.");
    }

    private void SetCodeStatus(string message)
    {
        if (teamCodeStatusLabel != null) teamCodeStatusLabel.text = message;
    }

    // --- Recovery ID ---

    // The uploader id is minted on the first submission and lives only in PlayerPrefs, so a reinstall
    // or a new phone loses it — and with it the link between a player and the robot they sent in.
    // Showing it, and letting them paste it back, IS the account system here: a bearer code they own,
    // with no sign-up, no email and nothing to verify.
    private void ShowRecoveryId()
    {
        if (recoveryIdLabel == null) return; // older HomeScene built before this row existed

        string id = RobotUploadService.UploaderId;
        recoveryIdLabel.text = string.IsNullOrEmpty(id)
            ? "No ID yet — you get one when you send a robot in."
            : id;
        if (copyRecoveryIdButton != null) copyRecoveryIdButton.interactable = !string.IsNullOrEmpty(id);
    }

    // Wired as a persistent onClick by the Build Home Scene tool.
    public void OnCopyRecoveryIdPressed()
    {
        string id = RobotUploadService.UploaderId;
        if (string.IsNullOrEmpty(id)) return;

        GUIUtility.systemCopyBuffer = id;
        if (recoveryIdLabel != null) recoveryIdLabel.text = id + "   (copied)";
    }

    public void OnRestoreRecoveryIdPressed()
    {
        string typed = recoveryIdInput != null ? recoveryIdInput.text : null;
        if (!RobotUploadService.AdoptUploaderId(typed))
        {
            if (recoveryIdLabel != null)
                recoveryIdLabel.text = "That doesn't look like an ID — check for missing characters.";
            return;
        }

        if (recoveryIdInput != null) recoveryIdInput.text = string.Empty;
        ShowRecoveryId();
        CheckInbox(); // the restored id may already have a robot waiting under it
    }

    // --- Robot inbox ---

    // A submitted robot ships inside an app update, so nothing here downloads one: the inbox only says
    // "it arrived" and hands over the code that reveals it. Deliberately silent about every failure —
    // this runs at launch, and an offline start or an unconfigured build must not put an error on the
    // home screen.
    private void CheckInbox()
    {
        SetInboxNoticeVisible(false);
        if (uploadConfig == null || !uploadConfig.IsConfigured) return;

        string id = RobotUploadService.UploaderId;
        if (string.IsNullOrEmpty(id)) return;

        StartCoroutine(RobotInboxService.Fetch(uploadConfig, id, OnInboxFetched));
    }

    private void OnInboxFetched(RobotInboxService.Inbox inbox, string error)
    {
        pendingInbox.Clear();
        if (!string.IsNullOrEmpty(error) || inbox == null || inbox.items == null) return;

        // Only keep items that would actually change something. A code already entered, or one no
        // robot in this build uses yet (the update carrying it hasn't landed), would make the notice
        // a lie — "your robot is ready", tap, and nothing appears.
        foreach (RobotInboxService.Item item in inbox.items)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.code)) continue;
            if (RobotOwnerSettings.HasCode(item.code)) continue;
            if (CountModelsWithCode(RobotOwnerSettings.Normalize(item.code)) == 0) continue;
            pendingInbox.Add(item);
        }

        if (pendingInbox.Count == 0) return;

        if (inboxLabel != null)
        {
            RobotInboxService.Item first = pendingInbox[0];
            string title = string.IsNullOrWhiteSpace(first.robotName) ? "Your robot" : first.robotName;
            inboxLabel.text = pendingInbox.Count == 1
                ? $"{title} is ready."
                : $"{title} and {pendingInbox.Count - 1} more are ready.";
        }
        SetInboxNoticeVisible(true);
    }

    // Wired as a persistent onClick by the Build Home Scene tool.
    public void OnInboxUnlockPressed()
    {
        int unlocked = 0;
        foreach (RobotInboxService.Item item in pendingInbox)
        {
            if (RobotOwnerSettings.AddCode(item.code)) unlocked++;
        }

        pendingInbox.Clear();
        SetInboxNoticeVisible(false);
        RebuildModelList();
        SetCodeStatus(unlocked == 1 ? "Unlocked 1 robot." : $"Unlocked {unlocked} robots.");
    }

    private void SetInboxNoticeVisible(bool visible)
    {
        if (inboxNotice != null) inboxNotice.SetActive(visible);
    }

    // --- Lite field ---

    // Same pattern again; guarded so an older HomeScene still runs without the toggle. OnDrivePressed
    // reads the setting at load time, so flipping it takes effect on the next Drive.
    private void InitLiteFieldControl()
    {
        if (liteFieldToggle == null) return;

        liteFieldToggle.SetIsOnWithoutNotify(FieldSceneSettings.UseLiteField);
        liteFieldToggle.onValueChanged.AddListener(value => FieldSceneSettings.UseLiteField = value);
    }
}
