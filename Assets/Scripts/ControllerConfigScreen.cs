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

    [Header("Tints")]
    [SerializeField] private Color assignedTint = new Color(0.24f, 0.49f, 0.92f); // accent blue
    [SerializeField] private Color unassignedTint = new Color(0.23f, 0.25f, 0.30f); // neutral dark

    private string robotId;
    private string robotDisplayName;
    private List<RobotModelCatalog.MechanismInfo> mechanisms = new List<RobotModelCatalog.MechanismInfo>();
    private ButtonMap map = new ButtonMap();
    private readonly List<GameObject> spawnedRows = new List<GameObject>();
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
        RefreshAllButtons();

        if (assignmentPanel != null) assignmentPanel.SetActive(false);
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
        pendingButtonIndex = index;
        if (assignmentHeader != null) assignmentHeader.text = $"Assign {(ControllerButton)index}";
        PopulateAssignmentRows();
        assignmentPanel.SetActive(true);
    }

    // Rebuilds one toggle row per mechanism function for the pending button. Called on open and
    // after every toggle/clear so the ✓ marks stay in sync; the popup stays open across taps.
    private void PopulateAssignmentRows()
    {
        foreach (GameObject row in spawnedRows) Destroy(row);
        spawnedRows.Clear();
        if (pendingButtonIndex < 0) return;
        ControllerButton button = (ControllerButton)pendingButtonIndex;

        foreach (RobotModelCatalog.MechanismInfo mechanism in mechanisms)
        {
            if (mechanism == null || string.IsNullOrEmpty(mechanism.id)) continue;
            if (mechanism.type == RobotMechanisms.TypePneumatic)
            {
                AddAssignmentRow(button, $"{mechanism.displayName} — Toggle", mechanism.id, ControllerMapSettings.ModeToggle);
            }
            else
            {
                AddAssignmentRow(button, $"{mechanism.displayName} — Forward", mechanism.id, ControllerMapSettings.ModeForward);
                AddAssignmentRow(button, $"{mechanism.displayName} — Reverse", mechanism.id, ControllerMapSettings.ModeReverse);
            }
        }
    }

    private void AddAssignmentRow(ControllerButton button, string label, string mechanismId, string mode)
    {
        bool assigned = ControllerMapSettings.HasAssignment(map, button, mechanismId, mode);
        Button row = Instantiate(assignmentRowTemplate, assignmentListParent);
        row.name = "Row_" + mechanismId + "_" + mode;
        row.gameObject.SetActive(true); // template itself stays inactive
        TMP_Text text = row.GetComponentInChildren<TMP_Text>(true);
        // Fixed-width prefix so checked/unchecked rows stay left-aligned.
        if (text != null) text.text = (assigned ? "✓ " : "    ") + label;
        row.onClick.AddListener(() => OnAssignmentRowToggled(mechanismId, mode));
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
        PopulateAssignmentRows();
    }

    private void OnClearPressed()
    {
        if (pendingButtonIndex < 0) return;
        ControllerMapSettings.ClearAssignment(map, (ControllerButton)pendingButtonIndex);
        ControllerMapSettings.Save(robotId, map);
        RefreshButton(pendingButtonIndex);
        PopulateAssignmentRows(); // reflect the cleared state; keep the popup open
    }

    private void CloseAssignmentPopup()
    {
        pendingButtonIndex = -1;
        if (assignmentPanel != null) assignmentPanel.SetActive(false);
    }

    // --- Refresh ---

    private void RefreshAllButtons()
    {
        for (int i = 0; i < ControllerMapSettings.ButtonCount; i++) RefreshButton(i);
    }

    // Assigned buttons show "<Mechanism> FWD/REV/TOG" under the button and tint accent. When a
    // button drives several mechanisms it shows the first plus a "+N" count (e.g. "DR4B REV +1").
    private void RefreshButton(int index)
    {
        List<ButtonAssignment> assignments = ControllerMapSettings.FindAll(map, (ControllerButton)index);

        ButtonAssignment first = null;
        RobotModelCatalog.MechanismInfo firstMechanism = null;
        int shown = 0;
        foreach (ButtonAssignment assignment in assignments)
        {
            RobotModelCatalog.MechanismInfo mechanism = FindMechanism(assignment.mechanismId);
            if (mechanism == null) continue; // stale (mechanism gone); PruneStaleAssignments clears it
            if (first == null) { first = assignment; firstMechanism = mechanism; }
            shown++;
        }

        string caption = string.Empty;
        if (firstMechanism != null)
        {
            string suffix = first.mode == ControllerMapSettings.ModeReverse ? "REV"
                : first.mode == ControllerMapSettings.ModeToggle ? "TOG" : "FWD";
            caption = $"{firstMechanism.displayName} {suffix}";
            if (shown > 1) caption += $" +{shown - 1}";
        }

        if (index < assignmentLabels.Length && assignmentLabels[index] != null)
            assignmentLabels[index].text = caption;
        if (index < buttons.Length && buttons[index] != null && buttons[index].image != null)
            buttons[index].image.color = firstMechanism != null ? assignedTint : unassignedTint;
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

    // Assignments to mechanisms the robot no longer has (re-import removed a joint) are dropped
    // from the persisted map so they don't linger invisibly.
    private void PruneStaleAssignments()
    {
        if (map == null || map.assignments == null) return;
        int removed = map.assignments.RemoveAll(a => a == null || FindMechanism(a.mechanismId) == null);
        if (removed > 0) ControllerMapSettings.Save(robotId, map);
    }
}
