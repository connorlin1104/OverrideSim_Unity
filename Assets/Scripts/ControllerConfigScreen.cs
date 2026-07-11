using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The home screen's "Configure Controller" sub-screen: a tappable controller diagram where the
// player assigns each button to one of the selected robot's mechanisms — motor forward, motor
// reverse, or pneumatic toggle. Assignments persist per robot via ControllerMapSettings
// (PlayerPrefs JSON keyed by the catalog id); ButtonRouter reads the same map in the field scene.
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
        if (cancelButton != null) cancelButton.onClick.AddListener(CloseAssignmentPopup);
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

        foreach (GameObject row in spawnedRows) Destroy(row);
        spawnedRows.Clear();

        foreach (RobotModelCatalog.MechanismInfo mechanism in mechanisms)
        {
            if (mechanism == null || string.IsNullOrEmpty(mechanism.id)) continue;
            if (mechanism.type == RobotMechanisms.TypePneumatic)
            {
                AddAssignmentRow($"{mechanism.displayName} — Toggle", mechanism.id, ControllerMapSettings.ModeToggle);
            }
            else
            {
                AddAssignmentRow($"{mechanism.displayName} — Forward", mechanism.id, ControllerMapSettings.ModeForward);
                AddAssignmentRow($"{mechanism.displayName} — Reverse", mechanism.id, ControllerMapSettings.ModeReverse);
            }
        }

        assignmentPanel.SetActive(true);
    }

    private void AddAssignmentRow(string label, string mechanismId, string mode)
    {
        Button row = Instantiate(assignmentRowTemplate, assignmentListParent);
        row.name = "Row_" + mechanismId + "_" + mode;
        row.gameObject.SetActive(true); // template itself stays inactive
        TMP_Text text = row.GetComponentInChildren<TMP_Text>(true);
        if (text != null) text.text = label;
        row.onClick.AddListener(() => OnAssignmentRowPressed(mechanismId, mode));
        spawnedRows.Add(row.gameObject);
    }

    private void OnAssignmentRowPressed(string mechanismId, string mode)
    {
        if (pendingButtonIndex >= 0)
        {
            ControllerMapSettings.SetAssignment(map, (ControllerButton)pendingButtonIndex, mechanismId, mode);
            ControllerMapSettings.Save(robotId, map);
            RefreshButton(pendingButtonIndex);
        }
        CloseAssignmentPopup();
    }

    private void OnClearPressed()
    {
        if (pendingButtonIndex >= 0)
        {
            ControllerMapSettings.ClearAssignment(map, (ControllerButton)pendingButtonIndex);
            ControllerMapSettings.Save(robotId, map);
            RefreshButton(pendingButtonIndex);
        }
        CloseAssignmentPopup();
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

    // Assigned buttons show "<Mechanism> FWD/REV/TOG" under the button and tint accent.
    private void RefreshButton(int index)
    {
        ButtonAssignment assignment = ControllerMapSettings.Find(map, (ControllerButton)index);
        RobotModelCatalog.MechanismInfo mechanism =
            assignment != null ? FindMechanism(assignment.mechanismId) : null;

        string caption = string.Empty;
        if (mechanism != null)
        {
            string suffix = assignment.mode == ControllerMapSettings.ModeReverse ? "REV"
                : assignment.mode == ControllerMapSettings.ModeToggle ? "TOG" : "FWD";
            caption = $"{mechanism.displayName} {suffix}";
        }

        if (index < assignmentLabels.Length && assignmentLabels[index] != null)
            assignmentLabels[index].text = caption;
        if (index < buttons.Length && buttons[index] != null && buttons[index].image != null)
            buttons[index].image.color = mechanism != null ? assignedTint : unassignedTint;
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
