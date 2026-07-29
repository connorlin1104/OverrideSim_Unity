using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The field scene's camera button: switches between the free-look field camera and the robot
// follow camera, and labels itself with the view you are currently in.
//
// The onClick listener is added at runtime, so the scene builder never stacks persistent listeners
// (same rationale as MatchLoadButton). Created and wired by
// Tools > RoboSim > Scenes > Build Drive Controls.
[RequireComponent(typeof(Button))]
public class CameraViewToggle : MonoBehaviour
{
    private const string FollowLabel = "Follow Cam";
    private const string FreeLabel = "Free Cam";

    [Tooltip("The free-look field camera (carries TouchCameraController).")]
    [SerializeField] private Camera freeCamera;

    [Tooltip("The robot follow camera (carries RobotChaseCamera).")]
    [SerializeField] private Camera chaseCamera;

    private TextMeshProUGUI label;

    // Applied in Awake, not Start, so the saved view is live before the first frame renders —
    // starting in follow view must not flash a frame of the field overview.
    void Awake()
    {
        label = GetComponentInChildren<TextMeshProUGUI>();
        GetComponent<Button>().onClick.AddListener(Toggle);

        if (chaseCamera == null || freeCamera == null)
        {
            // An older field scene that predates the follow camera: hide the button rather than
            // offer a switch that would blank the screen.
            Debug.LogWarning("CameraViewToggle: camera references are not wired — run " +
                             "Tools > RoboSim > Scenes > Build Drive Controls. Hiding the button.", this);
            gameObject.SetActive(false);
            return;
        }

        ApplyView(CameraViewSettings.FollowRobot);
    }

    private void Toggle()
    {
        bool follow = !CameraViewSettings.FollowRobot;
        CameraViewSettings.FollowRobot = follow;
        ApplyView(follow);
    }

    private void ApplyView(bool follow)
    {
        // Switch the CAMERA COMPONENTS, not the GameObjects: the free camera's object carries the
        // scene's only AudioListener, and deactivating it would take the audio with it. Each view's
        // controller is switched alongside its camera so the idle view stops claiming drags — two
        // live arbiters would both consume the same finger.
        freeCamera.enabled = !follow;
        chaseCamera.enabled = follow;

        TouchCameraController freeController = freeCamera.GetComponent<TouchCameraController>();
        if (freeController != null) freeController.enabled = !follow;

        RobotChaseCamera chaseController = chaseCamera.GetComponent<RobotChaseCamera>();
        if (chaseController != null) chaseController.enabled = follow;

        if (label != null) label.text = follow ? FollowLabel : FreeLabel;
    }
}
