using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The home screen's "Configure Controller" sub-screen: a tappable controller diagram where the
// player assigns each button to one or more of the selected robot's mechanism functions — motor
// forward, motor reverse, or pneumatic toggle. The assignment popup is a multi-toggle: tapping a
// function adds/removes it (a ✓ marks the ones on this button) and stays open, so one button can
// drive several mechanisms — e.g. a mirrored DR4B's two sides, one reversed. Assignments persist
// per robot via ControllerMapSettings (PlayerPrefs JSON keyed by the catalog id); ButtonRouter
// reads the same map in the field scene and sums every assignment per motor.
//
// The "Control Style" button reuses the same popup to switch a mechanism between one- and
// two-button control (a motor between hold fwd/rev and a latching toggle; a piston between one
// toggle and separate extend/retract buttons). Style lives in the same saved map, NOT in the robot
// prefab, so it applies to every robot already built with no rebuild — and which functions the
// assignment popup offers follows from it.
//
// The mechanism list comes from the catalog entry's metadata (written by the URDF
// post-processor), so no field-scene loading is needed here. Robots without mechanisms — like
// the built-in drivetrain — get an explanatory empty state; their buttons still open the popup
// (with only Clear/Cancel) so the flow is discoverable.
//
// Usage: built and fully wired (panel, 12 diagram buttons + captions in ControllerButton
// order, assignment popup) by the Build Home Screen tool. HomeScreenController opens/closes it.
public class ControllerConfigScreen : MonoBehaviour
{
    [Header("Data")]
    [SerializeField] private RobotModelCatalog catalog;

    [Header("Panel")]
    [SerializeField] private GameObject panel;
    [SerializeField] private TMP_Text headerLabel;
    [SerializeField] private GameObject emptyStateLabel;

    [Header("Diagram (ControllerButton order: L1 L2 R1 R2 Up Down Left Right X B A Y)")]
    [SerializeField] private Button[] buttons = new Button[ControllerMapSettings.ButtonCount];
    [SerializeField] private TMP_Text[] assignmentLabels = new TMP_Text[ControllerMapSettings.ButtonCount];

    [Header("Assignment Popup")]
    [SerializeField] private GameObject assignmentPanel;
    [SerializeField] private TMP_Text assignmentHeader;
    [SerializeField] private Transform assignmentListParent;
    [SerializeField] private Button assignmentRowTemplate;
    [SerializeField] private Button clearButton;
    [SerializeField] private Button cancelButton;
    [Tooltip("Opens the same popup in control-style mode. Optional: a home scene built before " +
             "control styles existed has no such button and simply keeps the per-type defaults.")]
    [SerializeField] private Button controlStyleButton;

    [Header("Tints")]
    [SerializeField] private Color assignedTint = new Color(0.24f, 0.49f, 0.92f); // accent blue
    [SerializeField] private Color unassignedTint = new Color(0.23f, 0.25f, 0.30f); // neutral dark
    [Tooltip("Popup rows already on the pending button are filled with this, so what's picked reads " +
             "at a glance.")]
    [SerializeField] private Color selectedRowTint = new Color(0.20f, 0.62f, 0.35f); // green
    [SerializeField] private Color rowTint = new Color(0.23f, 0.25f, 0.30f);         // neutral dark

    // What the shared popup is currently showing.
    private enum PopupMode { Assign, Style }

    private string robotId;
    private string robotDisplayName;
    private List<RobotModelCatalog.MechanismInfo> mechanisms = new List<RobotModelCatalog.MechanismInfo>();
    private ButtonMap map = new ButtonMap();
    private readonly List<GameObject> spawnedRows = new List<GameObject>();
    private PopupMode popupMode = PopupMode.Assign;
    private int pendingButtonIndex = -1;

    void Awake()
    {
        // The diagram buttons need their index, which persistent onClicks can't carry — wire
        // the listeners here from the serialized array instead.
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i; // per-iteration copy for the closure
            if (buttons[i] != null) buttons[i].onClick.AddListener(() => OnDiagramButtonPressed(index));
        }
        if (clearButton != null) clearButton.onClick.AddListener(OnClearPressed);
        if (controlStyleButton != null) controlStyleButton.onClick.AddListener(OnControlStylePressed);
        if (cancelButton != null)
        {
            cancelButton.onClick.AddListener(CloseAssignmentPopup);
            // Every toggle saves immediately now, so this button just closes the popup — relabel
            // it "Done" (the built prefab says "Cancel", which reads as "discard my changes").
            TMP_Text cancelText = cancelButton.GetComponentInChildren<TMP_Text>(true);
            if (cancelText != null) cancelText.text = "Done";
        }
    }

    // Called by HomeScreenController when the player opens the config screen. Reads the
    // selected robot's mechanisms from the catalog and its saved map from PlayerPrefs.
    public void Open()
    {
        RobotModelCatalog.Entry entry = null;
        robotId = catalog != null ? catalog.SelectedModelId : null;
        if (catalog != null && catalog.models != null && !string.IsNullOrEmpty(robotId))
        {
            foreach (RobotModelCatalog.Entry candidate in catalog.models)
            {
                if (candidate != null && candidate.id == robotId) { entry = candidate; break; }
            }
        }
        robotDisplayName = entry != null ? entry.displayName : "No Robot";
        mechanisms = entry != null && entry.mechanisms != null
            ? entry.mechanisms
            : new List<RobotModelCatalog.MechanismInfo>();

        map = ControllerMapSettings.Load(robotId);
        PruneStaleAssignments();

        if (headerLabel != null) headerLabel.text = $"Controller — {robotDisplayName}";
        if (emptyStateLabel != null) emptyStateLabel.SetActive(mechanisms.Count == 0);
        // Style switching needs mechanisms to act on, so hide the entry point when there are none.
        if (controlStyleButton != null) controlStyleButton.gameObject.SetActive(mechanisms.Count > 0);
        RefreshAllButtons();

        CloseAssignmentPopup(); // a popup left open from a previous robot must not carry over
        if (panel != null) panel.SetActive(true);
    }

    public void Close()
    {
        if (assignmentPanel != null) assignmentPanel.SetActive(false);
        if (panel != null) panel.SetActive(false);
    }

    // --- Assignment popup ---

    private void OnDiagramButtonPressed(int index)
    {
        if (assignmentPanel == null || assignmentRowTemplate == null || assignmentListParent == null) return;
        popupMode = PopupMode.Assign;
        pendingButtonIndex = index;
        if (assignmentHeader != null) assignmentHeader.text = $"Assign {(ControllerButton)index}";
        if (clearButton != null) clearButton.gameObject.SetActive(true);
        PopulateRows();
        assignmentPanel.SetActive(true);
    }

    // Opens the same popup listing mechanisms instead of button functions. pendingButtonIndex is
    // cleared because there IS no pending button here — OnClearPressed would otherwise wipe
    // whichever button happened to be open last.
    private void OnControlStylePressed()
    {
        if (assignmentPanel == null || assignmentRowTemplate == null || assignmentListParent == null) return;
        popupMode = PopupMode.Style;
        pendingButtonIndex = -1;
        if (assignmentHeader != null) assignmentHeader.text = "Control Style";
        if (clearButton != null) clearButton.gameObject.SetActive(false); // nothing to clear here
        PopulateRows();
        assignmentPanel.SetActive(true);
    }

    private void PopulateRows()
    {
        foreach (GameObject row in spawnedRows) Destroy(row);
        spawnedRows.Clear();
        if (popupMode == PopupMode.Style) PopulateStyleRows();
        else PopulateAssignmentRows();
    }

    // One toggle row per mechanism function for the pending button. Which functions a mechanism
    // offers depends on its control style, so a motor switched to one-button shows Toggle rows
    // rather than Forward/Reverse.
    private void PopulateAssignmentRows()
    {
        if (pendingButtonIndex < 0) return;
        ControllerButton button = (ControllerButton)pendingButtonIndex;

        foreach (RobotModelCatalog.MechanismInfo mechanism in mechanisms)
        {
            if (mechanism == null || string.IsNullOrEmpty(mechanism.id)) continue;
            string style = ControllerMapSettings.GetStyle(map, mechanism.id, mechanism.type);
            foreach (string mode in ControllerMapSettings.ModesFor(mechanism.type, style))
            {
                AddRow($"{mechanism.displayName} — {FunctionLabel(mode)}",
                    mechanism.id + "_" + mode,
                    ControllerMapSettings.HasAssignment(map, button, mechanism.id, mode),
                    () => OnAssignmentRowToggled(mechanism.id, mode));
            }
        }
    }

    // One row per mechanism showing how many buttons drive it; tapping switches to the other style
    // and rewrites its existing bindings to match, so the change takes effect without a trip back
    // to the diagram.
    private void PopulateStyleRows()
    {
        foreach (RobotModelCatalog.MechanismInfo mechanism in mechanisms)
        {
            if (mechanism == null || string.IsNullOrEmpty(mechanism.id)) continue;
            string style = ControllerMapSettings.GetStyle(map, mechanism.id, mechanism.type);
            // A style row is a "tap to switch" action, not a selected state, so it never tints.
            AddRow($"{mechanism.displayName} — {StyleLabel(mechanism.type, style)}",
                mechanism.id + "_style", false,
                () => OnStyleRowToggled(mechanism.id, mechanism.type, style));
        }
    }

    // Clones the row template, filling it green when this function is already on the pending button.
    //
    // The mark used to be a "✓ " prefix on the label — but the project font (LiberationSans SDF, 250
    // glyphs) has no U+2713 and TMP Settings defines no fallback, so it rendered as the missing-glyph
    // box: the "white box next to it". Tinting the row is both unambiguous and font-proof.
    private void AddRow(string label, string idSuffix, bool selected, UnityEngine.Events.UnityAction onClick)
    {
        Button row = Instantiate(assignmentRowTemplate, assignmentListParent);
        row.name = "Row_" + idSuffix;
        row.gameObject.SetActive(true); // template itself stays inactive
        TMP_Text text = row.GetComponentInChildren<TMP_Text>(true);
        if (text != null) text.text = label;
        if (row.image != null) row.image.color = selected ? selectedRowTint : rowTint;
        row.onClick.AddListener(onClick);
        spawnedRows.Add(row.gameObject);
    }

    // Adds the function if the button doesn't have it, removes it if it does — so several
    // functions can be stacked on one button. Saves and re-renders in place (popup stays open).
    private void OnAssignmentRowToggled(string mechanismId, string mode)
    {
        if (pendingButtonIndex < 0) return;
        ControllerButton button = (ControllerButton)pendingButtonIndex;
        if (ControllerMapSettings.HasAssignment(map, button, mechanismId, mode))
            ControllerMapSettings.RemoveAssignment(map, button, mechanismId, mode);
        else
            ControllerMapSettings.AddAssignment(map, button, mechanismId, mode);
        ControllerMapSettings.Save(robotId, map);
        RefreshButton(pendingButtonIndex);
        PopulateRows();
    }

    // Flips the mechanism to the other style. Every diagram caption refreshes because the rewrite
    // can move a function onto a newly-claimed button.
    private void OnStyleRowToggled(string mechanismId, string mechanismType, string currentStyle)
    {
        string next = currentStyle == ControllerMapSettings.StyleOneButton
            ? ControllerMapSettings.StyleTwoButton
            : ControllerMapSettings.StyleOneButton;
        ControllerMapSettings.SetStyle(map, mechanismId, mechanismType, next);
        ControllerMapSettings.Save(robotId, map);
        RefreshAllButtons();
        PopulateRows();
    }

    private void OnClearPressed()
    {
        if (popupMode != PopupMode.Assign || pendingButtonIndex < 0) return;
        ControllerMapSettings.ClearAssignment(map, (ControllerButton)pendingButtonIndex);
        ControllerMapSettings.Save(robotId, map);
        RefreshButton(pendingButtonIndex);
        PopulateRows(); // reflect the cleared state; keep the popup open
    }

    private void CloseAssignmentPopup()
    {
        popupMode = PopupMode.Assign;
        pendingButtonIndex = -1;
        if (clearButton != null) clearButton.gameObject.SetActive(true);
        if (assignmentPanel != null) assignmentPanel.SetActive(false);
    }

    // Row copy. ModeLabel/ModeCaption stay lowercase for prose and captions; these are the
    // title-case, player-facing names for the popup.
    private static string FunctionLabel(string mode)
    {
        switch (mode)
        {
            case ControllerMapSettings.ModeReverse: return "Reverse (hold)";
            case ControllerMapSettings.ModeToggle: return "Toggle";
            case ControllerMapSettings.ModeToggleReverse: return "Toggle Reverse";
            case ControllerMapSettings.ModeExtend: return "Extend";
            case ControllerMapSettings.ModeRetract: return "Retract";
            default: return "Forward (hold)";
        }
    }

    private static string StyleLabel(string mechanismType, string style)
    {
        bool one = style == ControllerMapSettings.StyleOneButton;
        if (mechanismType == RobotMechanisms.TypePneumatic)
            return one ? "1 button (toggle)" : "2 buttons (extend / retract)";
        return one ? "1 button (toggle on/off)" : "2 buttons (hold fwd / rev)";
    }

    // --- Refresh ---

    private void RefreshAllButtons()
    {
        for (int i = 0; i < ControllerMapSettings.ButtonCount; i++) RefreshButton(i);
    }

    // Assigned buttons tint accent and list EVERY function they drive under the diagram, one per
    // line ("DR4B REV" / "Claw Clamp TOG") — a button can legitimately drive several mechanisms, and
    // showing only the first left the rest invisible. The caption rect is top-anchored and three
    // lines tall (BuildHomeScene.CreateConfigButton), so past that they fold into a "+N" tail rather
    // than running into the row below.
    private const int MaxCaptionLines = 3;

    private void RefreshButton(int index)
    {
        List<ButtonAssignment> assignments = ControllerMapSettings.FindAll(map, (ControllerButton)index);

        var lines = new List<string>();
        int shown = 0;
        foreach (ButtonAssignment assignment in assignments)
        {
            RobotModelCatalog.MechanismInfo mechanism = FindMechanism(assignment.mechanismId);
            if (mechanism == null) continue; // stale (mechanism gone); PruneStaleAssignments clears it
            shown++;
            if (lines.Count < MaxCaptionLines)
                lines.Add($"{mechanism.displayName} {ControllerMapSettings.ModeCaption(assignment.mode)}");
        }
        if (shown > lines.Count && lines.Count > 0) lines[lines.Count - 1] += $" +{shown - lines.Count}";

        if (index < assignmentLabels.Length && assignmentLabels[index] != null)
            assignmentLabels[index].text = string.Join("\n", lines);
        if (index < buttons.Length && buttons[index] != null && buttons[index].image != null)
            buttons[index].image.color = shown > 0 ? assignedTint : unassignedTint;
    }

    private RobotModelCatalog.MechanismInfo FindMechanism(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (RobotModelCatalog.MechanismInfo mechanism in mechanisms)
        {
            if (mechanism != null && mechanism.id == id) return mechanism;
        }
        return null;
    }

    // Assignments and style choices for mechanisms the robot no longer has (re-import removed a
    // joint) are dropped from the persisted map so they don't linger invisibly.
    private void PruneStaleAssignments()
    {
        if (map == null || map.assignments == null) return;
        int removed = map.assignments.RemoveAll(a => a == null || FindMechanism(a.mechanismId) == null);
        if (map.styles != null)
            removed += map.styles.RemoveAll(s => s == null || FindMechanism(s.mechanismId) == null);
        if (removed > 0) ControllerMapSettings.Save(robotId, map);
    }
}
