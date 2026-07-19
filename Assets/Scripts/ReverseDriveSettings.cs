using UnityEngine;

// Shared, PlayerPrefs-backed setting for which end of the robot the drive controls treat as "front".
// Off (default): forward stick drives the robot's normal front. On: the control frame is flipped 180°,
// so the opposite end becomes front — some drivers want the intake in front, others the scoring end.
//
// Stored in PlayerPrefs — not on any asset — so changing it never dirties the project and the choice
// persists per device across restarts (same approach as MatchLoadSettings / ControlsOpacitySettings).
// RobotMotorController reads it live in FixedUpdate, so no spawner wiring is needed.
public static class ReverseDriveSettings
{
    public const string ReversedPrefKey = "ReverseDriveDirection";
    public const bool DefaultReversed = false;

    public static bool Reversed
    {
        get => PlayerPrefs.GetInt(ReversedPrefKey, DefaultReversed ? 1 : 0) != 0;
        set
        {
            PlayerPrefs.SetInt(ReversedPrefKey, value ? 1 : 0);
            PlayerPrefs.Save(); // flush now so a force-quit doesn't lose the choice
        }
    }
}
