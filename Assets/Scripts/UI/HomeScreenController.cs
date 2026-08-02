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
    [Tooltip("Parent for the PUBLIC half of the picker (has the VerticalLayoutGroup).")]
    [SerializeField] private Transform publicModelListParent;
    [Tooltip("Parent for the PRIVATE half — the robots unlocked by a code entered under Account.")]
    [SerializeField] private Transform privateModelListParent;
    [Tooltip("Shown instead of the private column's rows when this device holds no codes.")]
    [SerializeField] private GameObject privateEmptyLabel;
    [Tooltip("Sizes the public column's scrolling viewport. Set from the row count — see ResizeModelColumns.")]
    [SerializeField] private LayoutElement publicListViewport;
    [Tooltip("The same, for the private column. Both are given the SAME height so the halves match.")]
    [SerializeField] private LayoutElement privateListViewport;
    [Tooltip("Inactive template Button under the public list; cloned once per catalog entry, into " +
             "whichever column matches that entry's visibility.")]
    [SerializeField] private Button modelButtonTemplate;
    // Catalog AUTHORING — removing a model, and publishing a robot's default button layout — is
    // not something a player should be able to do: the catalog is a read-only asset in a build, so
    // an in-game delete either looked permanent and wasn't, or (in the editor) silently wrote the
    // removal of a shipped public robot straight into the committed asset. Both live in
    // Tools > RoboSim > Robot > Model Catalog now.

    [Header("Settings Tabs")]
    [Tooltip("Tab buttons across the top of the settings panel, in the same order as Settings Tab Pages.")]
    [SerializeField] private Button[] settingsTabButtons;
    [Tooltip("One scroll content per tab. Exactly one is active at a time and the ScrollRect is " +
             "pointed at it, so each page scrolls independently and none nests inside another.")]
    [SerializeField] private GameObject[] settingsTabPages;
    [Tooltip("The settings panel's ScrollRect. Its content is repointed at whichever page is showing.")]
    [SerializeField] private ScrollRect settingsScroll;

    [Header("Drive Feel")]
    [Tooltip("Scales the throttle command (persisted via DriveFeelSettings).")]
    [SerializeField] private Slider driveSensitivitySlider;
    [SerializeField] private TMP_Text driveSensitivityLabel;
    [Tooltip("Scales the turn command, on top of the robot's own turn rate.")]
    [SerializeField] private Slider turnSensitivitySlider;
    [SerializeField] private TMP_Text turnSensitivityLabel;

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

    [Header("Match Loading")]
    [Tooltip("Checkbox for Automatic Matchloading (persisted via MatchLoadSettings). When off, the field scene shows a Match Load button for manual spawns.")]
    [SerializeField] private Toggle automaticMatchloadToggle;

    [Header("Drive")]
    [Tooltip("Checkbox for Reverse Drive Direction (persisted via ReverseDriveSettings). Flips which end of the robot the drive controls treat as front.")]
    [SerializeField] private Toggle reverseDriveToggle;
    [Tooltip("Checkbox for the lite field (persisted via FieldSceneSettings). Loads the stripped-down LiteScene instead of the full field — far cheaper to run.")]
    [SerializeField] private Toggle liteFieldToggle;

    [Header("Robot Codes")]
    [Tooltip("Where the player types an owner code to reveal a private robot (RobotOwnerSettings).")]
    [SerializeField] private TMP_InputField robotCodeInput;
    [Tooltip("Feedback line under the code box: what the last Unlock did.")]
    [SerializeField] private TMP_Text robotCodeStatusLabel;
    [Tooltip("Lists the codes held on this device, and what each one unlocks, so they can be shared on.")]
    [SerializeField] private TMP_Text yourCodesLabel;

    [Header("Robot Inbox")]
    [Tooltip("Where submissions go, and where the inbox is read from. Same asset as SubmitRobotScreen's.")]
    [SerializeField] private RobotUploadConfig uploadConfig;
    [Tooltip("Notice shown over the main panel when a robot the player sent in has arrived.")]
    [SerializeField] private GameObject inboxNotice;
    [Tooltip("Line inside the notice naming what arrived.")]
    [SerializeField] private TMP_Text inboxLabel;
    [Tooltip("Body text under it: a note from the developer, and the whole message when a robot couldn't be set up.")]
    [SerializeField] private TMP_Text inboxMessageLabel;
    [Tooltip("Sizes the message's scrolling viewport — see ShowInboxNotice. Hidden when there's no message.")]
    [SerializeField] private LayoutElement inboxMessageViewport;
    [Tooltip("Caption on the notice's button — 'Add it to my list' for an arrival, 'Got it' for a note.")]
    [SerializeField] private TMP_Text inboxActionLabel;

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

    // Inbox items that would actually reveal something on this device — see OnInboxFetched.
    private readonly List<RobotInboxService.Item> pendingInbox = new List<RobotInboxService.Item>();

    void Start()
    {
        if (mainPanel != null) mainPanel.SetActive(true);
        if (settingsPanel != null) settingsPanel.SetActive(false);
        if (loadingOverlay != null) loadingOverlay.SetActive(false);
        // Before anything reads a map: give every robot that ships a default layout to a device
        // that hasn't got one. HomeScene is build index 0, so this is the normal path in a build.
        ControllerMapSettings.SeedDefaults(catalog);
        // Coasting and smooth acceleration used to be checkboxes and are now unconditional; drop
        // any stored "off" so an old choice can't outlive the control that set it.
        DriveFeelSettings.ClearRetiredKeys();
        BuildModelList();
        InitJoystickSizeControl();
        InitControlsOpacityControl();
        InitAutomaticMatchloadControl();
        InitReverseDriveControl();
        InitLiteFieldControl();
        InitDriveFeelControls();
        SetTab(0);
        SetCodeStatus(string.Empty);
        ShowHeldCodes();
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

    // --- Settings tabs ---

    // Wired as persistent onClicks by the Build Home Scene tool. Driving used to be tab 2; it held
    // the drive-feel sliders (now under Controls), the drive-direction toggle (now under Robot >
    // Match, beside the other things that are true of this robot) and a wheel-type checkbox that
    // asked the player a physics question and is gone entirely.
    public void OnRobotTabPressed() => SetTab(0);
    public void OnControlsTabPressed() => SetTab(1);
    public void OnAccountTabPressed() => SetTab(2);

    // Show one page and point the shared ScrollRect at it.
    //
    // The pages are siblings rather than one long column because the settings screen was ~2.4
    // screens tall with no scrollbar, so most of it was simply invisible. Guarded throughout so an
    // older HomeScene built before the tabs still runs.
    private void SetTab(int index)
    {
        if (settingsTabPages == null || settingsTabPages.Length == 0) return;
        index = Mathf.Clamp(index, 0, settingsTabPages.Length - 1);

        for (int i = 0; i < settingsTabPages.Length; i++)
        {
            if (settingsTabPages[i] != null) settingsTabPages[i].SetActive(i == index);
        }

        if (settingsScroll != null && settingsTabPages[index] != null)
        {
            settingsScroll.content = (RectTransform)settingsTabPages[index].transform;
            // Without this the new page opens wherever the previous one was scrolled to, which
            // looks like a page with its heading missing.
            settingsScroll.verticalNormalizedPosition = 1f;
        }

        if (settingsTabButtons == null) return;
        for (int i = 0; i < settingsTabButtons.Length; i++)
        {
            if (settingsTabButtons[i] != null && settingsTabButtons[i].image != null)
                settingsTabButtons[i].image.color = i == index ? selectedTint : normalTint;
        }
    }

    // --- Model list ---

    private void BuildModelList()
    {
        if (catalog == null || publicModelListParent == null || modelButtonTemplate == null)
        {
            Debug.LogWarning("HomeScreenController: catalog / model list references are not assigned; " +
                             "model list not built.", this);
            return;
        }

        int privateCount = 0;

        // VisibleModels, not models: private entries stay out of the list until their owner enters
        // the code in Settings. Every other reader of the catalog filters the same way.
        //
        // The column an entry lands in is its VISIBILITY, not whether it needed a code to appear:
        // a robot in the Private column is one that isn't listed for everyone, which is exactly the
        // thing the split is there to tell the player.
        foreach (RobotModelCatalog.Entry entry in catalog.VisibleModels)
        {
            bool isPrivate = entry.visibility == RobotModelCatalog.Visibility.Private;
            // Fall back to the public column if the scene was built before the split, so a robot is
            // never simply missing from the picker.
            Transform column = isPrivate && privateModelListParent != null
                ? privateModelListParent : publicModelListParent;
            if (isPrivate) privateCount++;

            Button clone = Instantiate(modelButtonTemplate, column);
            clone.name = "Model_" + entry.id;
            clone.gameObject.SetActive(true); // template itself stays inactive

            string title = string.IsNullOrWhiteSpace(entry.ownerLabel)
                ? entry.displayName
                : $"{entry.displayName}  ({entry.ownerLabel})";
            TMP_Text label = clone.GetComponentInChildren<TMP_Text>(true);
            if (label != null) label.text = title;

            string id = entry.id; // capture per-iteration copy for the closure
            clone.onClick.AddListener(() => SelectModel(id));
            modelButtons.Add(new KeyValuePair<Button, string>(clone, id));
        }

        if (privateEmptyLabel != null) privateEmptyLabel.SetActive(privateCount == 0);
        ResizeModelColumns();
        RefreshHighlight();
    }

    // How tall the picker's two columns are allowed to get before they start scrolling instead of
    // growing. Four rows plus a sliver of the fifth: the part-row is the whole affordance — a column
    // cut cleanly between rows reads as a complete list, and nobody scrolls a list they think they
    // can already see all of.
    private const float ModelColumnMaxHeight = 400f;

    // The floor stops a single-robot column from collapsing to something that looks broken next to a
    // full one, and matches the empty-state label's own height.
    private const float ModelColumnMinHeight = 120f;

    // Both columns get the SAME height — the taller one's, capped. Sizing them independently would
    // put a short Private column next to a tall Public one, and two mismatched boxes read as a
    // layout bug rather than as "you have fewer of these".
    //
    // Measured off the built rows rather than computed from a row count, so the row height, the
    // spacing and the empty label's size all stay owned by the scene builder alone: this can't drift
    // out of step with BuildHomeScene the way a duplicated 84f would.
    private void ResizeModelColumns()
    {
        if (publicListViewport == null || privateListViewport == null) return;

        float tallest = Mathf.Max(NaturalHeight(publicModelListParent),
                                  NaturalHeight(privateModelListParent));
        float height = Mathf.Clamp(tallest, ModelColumnMinHeight, ModelColumnMaxHeight);
        publicListViewport.preferredHeight = height;
        privateListViewport.preferredHeight = height;
    }

    // A scroll content's height comes from its ContentSizeFitter, which hasn't run yet on the frame
    // the rows are created — so force it, or every column measures zero on the first build and the
    // picker comes up at its minimum until something else dirties the layout.
    private static float NaturalHeight(Transform list)
    {
        if (list == null) return 0f;

        var rect = (RectTransform)list;
        LayoutRebuilder.ForceRebuildLayoutImmediate(rect);
        return rect.rect.height;
    }

    // Destroy the current clones and rebuild the list — after a team code is entered or forgotten,
    // which is what changes who is in VisibleModels.
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

    private void SelectModel(string id)
    {
        catalog.SelectedModelId = id;
        RefreshHighlight();
    }

    // Tint the selected entry with the accent color so the current choice is visible.
    private void RefreshHighlight()
    {
        string selected = catalog != null ? catalog.SelectedModelId : null;
        foreach (KeyValuePair<Button, string> pair in modelButtons)
        {
            if (pair.Key == null || pair.Key.image == null) continue;
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
        JoystickSettings.Scale = value; // ControlsAppearance applies this when the field scene loads
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

    // --- Drive feel ---

    // Same guarded pattern as the other settings controls. RobotMotorController snapshots these at
    // Awake rather than reading them live at 100 Hz, so a change lands on the next Drive — the same
    // contract Joystick Size and Lite Field already have.
    private void InitDriveFeelControls()
    {
        if (driveSensitivitySlider != null)
        {
            driveSensitivitySlider.minValue = DriveFeelSettings.MinDriveSensitivity;
            driveSensitivitySlider.maxValue = DriveFeelSettings.MaxDriveSensitivity;
            driveSensitivitySlider.wholeNumbers = false;
            driveSensitivitySlider.SetValueWithoutNotify(DriveFeelSettings.DriveSensitivity);
            driveSensitivitySlider.onValueChanged.AddListener(OnDriveSensitivityChanged);
            UpdateDriveSensitivityLabel(DriveFeelSettings.DriveSensitivity);
        }

        if (turnSensitivitySlider != null)
        {
            turnSensitivitySlider.minValue = DriveFeelSettings.MinTurnSensitivity;
            turnSensitivitySlider.maxValue = DriveFeelSettings.MaxTurnSensitivity;
            turnSensitivitySlider.wholeNumbers = false;
            turnSensitivitySlider.SetValueWithoutNotify(DriveFeelSettings.TurnSensitivity);
            turnSensitivitySlider.onValueChanged.AddListener(OnTurnSensitivityChanged);
            UpdateTurnSensitivityLabel(DriveFeelSettings.TurnSensitivity);
        }
    }

    private void OnDriveSensitivityChanged(float value)
    {
        DriveFeelSettings.DriveSensitivity = value;
        UpdateDriveSensitivityLabel(value);
    }

    private void UpdateDriveSensitivityLabel(float value)
    {
        if (driveSensitivityLabel != null)
            driveSensitivityLabel.text = $"Drive Sensitivity — {Mathf.RoundToInt(value * 100f)}%";
    }

    private void OnTurnSensitivityChanged(float value)
    {
        DriveFeelSettings.TurnSensitivity = value;
        UpdateTurnSensitivityLabel(value);
    }

    private void UpdateTurnSensitivityLabel(float value)
    {
        if (turnSensitivityLabel != null)
            turnSensitivityLabel.text = $"Turn Sensitivity — {Mathf.RoundToInt(value * 100f)}%";
    }

    // --- Robot codes (private robots) ---

    // Wired as a persistent onClick by the Build Home Scene tool. A code is only stored when it
    // actually matches a robot in this build: silently banking a typo'd code and showing no new
    // models would look identical to the feature being broken.
    public void OnUnlockCodePressed()
    {
        string code = RobotOwnerSettings.Normalize(robotCodeInput != null ? robotCodeInput.text : null);
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
        if (robotCodeInput != null) robotCodeInput.text = string.Empty;
        RebuildModelList();
        ShowHeldCodes();
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
        ShowHeldCodes();
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

    // Spell the held codes out, each with what it unlocks. A code is a bearer token meant to be
    // passed on — that is the entire sharing model — and once it had been typed it existed nowhere
    // the player could read it back, so losing the message it arrived in meant losing the ability to
    // share their own robot. This is also the whole of the recovery story now: a new phone re-enters
    // these lines, which is why they name the robot as well as the code.
    private void ShowHeldCodes()
    {
        if (yourCodesLabel == null) return; // older HomeScene built before this row existed

        List<string> held = RobotOwnerSettings.AllCodes();
        if (held.Count == 0)
        {
            yourCodesLabel.text = "No codes entered on this device.";
            return;
        }

        var lines = new List<string>();
        foreach (string code in held)
        {
            string unlocks = NamesUnlockedBy(RobotOwnerSettings.Normalize(code));
            lines.Add(string.IsNullOrEmpty(unlocks)
                // Held, valid, and pointing at a robot this version doesn't carry yet — an update
                // away. Saying so beats listing a code next to nothing.
                ? $"{code}  —  not in this version yet"
                : $"{code}  —  {unlocks}");
        }
        yourCodesLabel.text = string.Join("\n", lines);
    }

    // Comma-separated display names of the private entries a code reveals. Reads the FULL catalog:
    // the entry is visible precisely because this code is held, so filtering to VisibleModels would
    // work but would also quietly hide the answer if a second code were forgotten mid-session.
    private string NamesUnlockedBy(string normalizedCode)
    {
        if (catalog == null || catalog.models == null || string.IsNullOrEmpty(normalizedCode))
            return string.Empty;

        var names = new List<string>();
        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry == null || entry.visibility != RobotModelCatalog.Visibility.Private) continue;
            if (!entry.OwnerCodes.Contains(normalizedCode)) continue;
            names.Add(string.IsNullOrWhiteSpace(entry.displayName) ? entry.id : entry.displayName);
        }
        return string.Join(", ", names);
    }

    private void SetCodeStatus(string message)
    {
        if (robotCodeStatusLabel != null) robotCodeStatusLabel.text = message;
    }

    // NOTE: a "Your ID" section used to live here — the uploader id shown to be written down, copied
    // to the clipboard, and pasted back on a new device (RobotUploadService.AdoptUploaderId). It was
    // an account system for a link the player never has to follow: what comes back from a submission
    // is a CODE, the codes are listed above, and a new phone just re-enters them. The id is still
    // minted and still used to check the inbox — it is simply no longer the player's to look after.

    // --- Robot inbox ---

    // A submitted robot ships inside a new app version, so nothing here downloads one. The inbox
    // carries the two things that CAN come back: "it arrived, here's the code", and "it couldn't be
    // set up, here's why". Deliberately silent about every failure of its own — this runs at launch,
    // and an offline start or an unconfigured build must not put an error on the home screen.
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

        // Only keep items that would actually change something.
        //
        // An ARRIVAL is dropped when its code is already held, or when no robot in this build uses it
        // yet (the version carrying it hasn't landed). Either would make the notice a lie — "your
        // robot is ready", tap, and nothing appears.
        //
        // A NOTE has no such self-filter: nothing about the device changes when it's read, so it is
        // dropped once it has been dismissed and not before.
        foreach (RobotInboxService.Item item in inbox.items)
        {
            if (RobotInboxService.IsArrival(item))
            {
                if (RobotOwnerSettings.HasCode(item.code)) continue;
                if (CountModelsWithCode(RobotOwnerSettings.Normalize(item.code)) == 0) continue;
            }
            else if (RobotInboxService.IsNote(item))
            {
                if (RobotInboxSettings.HasSeen(RobotInboxService.KeyFor(item))) continue;
            }
            else
            {
                continue; // neither a code nor anything to say
            }
            pendingInbox.Add(item);
        }

        if (pendingInbox.Count == 0) return;

        // Visible BEFORE the text is measured: layout doesn't rebuild on an inactive object, so a
        // message sized while the dialog is hidden measures zero and comes up collapsed.
        SetInboxNoticeVisible(true);
        ShowInboxNotice();
    }

    // The banner says one of two things, and the difference is whether anything can be unlocked.
    private void ShowInboxNotice()
    {
        int arrivals = 0;
        foreach (RobotInboxService.Item item in pendingInbox)
        {
            if (RobotInboxService.IsArrival(item)) arrivals++;
        }

        if (inboxLabel != null)
        {
            RobotInboxService.Item first = pendingInbox[0];
            string title = string.IsNullOrWhiteSpace(first.robotName) ? "Your robot" : first.robotName;
            int others = pendingInbox.Count - 1;

            if (arrivals > 0)
            {
                inboxLabel.text = others == 0
                    ? $"{title} is ready."
                    : $"{title} and {others} more are ready.";
            }
            else
            {
                // No code to hand over: this is a message about a robot that couldn't be set up, and
                // the headline must not promise otherwise.
                inboxLabel.text = others == 0
                    ? $"About {title}"
                    : $"About {title}, and {others} more";
            }
        }

        // Every message in the batch, arrivals included: a note alongside a working robot ("the arm
        // is simplified — the CAD had it as one piece") is worth as much as one about a failure, and
        // until now the field was carried all the way from the JSON and then never shown.
        //
        // Each message is prefixed with its robot's name when the batch has more than one, because
        // two paragraphs of instructions with nothing between them read as one long paragraph about
        // whichever robot the headline named.
        if (inboxMessageLabel != null)
        {
            var notes = new List<string>();
            foreach (RobotInboxService.Item item in pendingInbox)
            {
                if (string.IsNullOrWhiteSpace(item.message)) continue;

                string body = item.message.Trim();
                notes.Add(pendingInbox.Count > 1 && !string.IsNullOrWhiteSpace(item.robotName)
                    ? $"<b>{item.robotName.Trim()}</b>\n{body}"
                    : body);
            }
            inboxMessageLabel.text = string.Join("\n\n", notes);
            ShowInboxMessage(notes.Count > 0);
        }

        if (inboxActionLabel != null)
            inboxActionLabel.text = arrivals > 0 ? "Add it to my list" : "Got it";
    }

    // How much of a message is shown before it scrolls instead of growing the dialog. The developer
    // writing it decides its length — a one-line aside and a numbered list of re-export steps are
    // both legitimate — so the dialog cannot be sized to fit whatever arrives.
    private const float InboxMessageMaxHeight = 340f;

    private void ShowInboxMessage(bool visible)
    {
        if (inboxMessageViewport == null)
        {
            // No viewport ref (a scene built before the message scrolled): show the text as-is rather
            // than hiding a failure explanation because the layout is old.
            if (inboxMessageLabel != null) inboxMessageLabel.gameObject.SetActive(visible);
            return;
        }

        inboxMessageViewport.gameObject.SetActive(visible);
        if (!visible) return;

        // Measure the text at its real width, then take the smaller of that and the cap: short
        // messages get exactly their own height with no empty box under them, long ones scroll.
        var textRect = (RectTransform)inboxMessageLabel.transform;
        LayoutRebuilder.ForceRebuildLayoutImmediate(textRect);
        inboxMessageViewport.preferredHeight = Mathf.Min(textRect.rect.height, InboxMessageMaxHeight);
    }

    // Wired as a persistent onClick by the Build Home Scene tool. Adds every code the batch handed
    // over and marks every note read, so one tap always clears the whole banner — a button that left
    // part of the notice behind would look like it hadn't worked.
    public void OnInboxUnlockPressed()
    {
        int unlocked = 0;
        foreach (RobotInboxService.Item item in pendingInbox)
        {
            if (RobotInboxService.IsArrival(item)) { if (RobotOwnerSettings.AddCode(item.code)) unlocked++; }
            else RobotInboxSettings.MarkSeen(RobotInboxService.KeyFor(item));
        }

        pendingInbox.Clear();
        SetInboxNoticeVisible(false);
        RebuildModelList();
        ShowHeldCodes();

        // Only when something was actually unlocked: the Account status line is about codes, and
        // "Unlocked 0 robots" after reading a message about a robot that DIDN'T work is the worst
        // possible thing to say next.
        if (unlocked > 0)
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
