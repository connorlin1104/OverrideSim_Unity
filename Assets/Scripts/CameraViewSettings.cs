using UnityEngine;

// Shared, PlayerPrefs-backed setting for which of the field scene's two camera views is active:
// the free-look field camera (TouchCameraController, the default) or the robot follow camera
// (RobotChaseCamera). Flipped by the field scene's camera button, CameraViewToggle.
//
// Stored in PlayerPrefs — not on any asset — so changing it never dirties the project and the
// choice persists per device across restarts (same approach as MatchLoadSettings /
// ReverseDriveSettings). It also means the Reset button, which reloads the scene, brings you back
// into the view you were driving in rather than snapping to the field overview.
public static class CameraViewSettings
{
    public const string FollowRobotPrefKey = "CameraFollowRobot";
    public const bool DefaultFollowRobot = false;

    public static bool FollowRobot
    {
        get => PlayerPrefs.GetInt(FollowRobotPrefKey, DefaultFollowRobot ? 1 : 0) != 0;
        set
        {
            PlayerPrefs.SetInt(FollowRobotPrefKey, value ? 1 : 0);
            PlayerPrefs.Save(); // flush now so a force-quit doesn't lose the choice
        }
    }
}
