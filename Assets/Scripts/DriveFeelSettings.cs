using UnityEngine;

// Shared, PlayerPrefs-backed settings for how the drivetrain feels to drive.
//
// Stored in PlayerPrefs — not on any asset — so changing them never dirties the project and the
// choice persists per device across restarts (same approach as ReverseDriveSettings /
// ControlsOpacitySettings / MatchLoadSettings).
//
// Unlike ReverseDriveSettings, RobotMotorController CACHES these at Awake rather than reading them
// live: they're consumed in FixedUpdate at 100 Hz, and entering the field scene always re-runs
// Awake, so a change still takes effect on the next Drive — the same contract Joystick Size and
// Lite Field already have.
public static class DriveFeelSettings
{
    public const string DriveSensitivityPrefKey = "DriveSensitivity";
    public const string TurnSensitivityPrefKey = "TurnSensitivity";

    // Scales the throttle command. Below 1 the robot simply never commands full speed — useful on
    // a phone where a small on-screen stick makes fine control hard.
    public const float MinDriveSensitivity = 0.3f;
    public const float MaxDriveSensitivity = 1f;
    public const float DefaultDriveSensitivity = 1f;

    // Scales the turn command, on TOP of the robot's own turnRate (0.5 on every shipped prefab),
    // so 1.0 means "whatever this robot was built to do". Allowed above 1 because turnRate is
    // already halved — a driver who wants snappier pivots can get back to the full rate.
    public const float MinTurnSensitivity = 0.3f;
    public const float MaxTurnSensitivity = 1.5f;
    public const float DefaultTurnSensitivity = 1f;

    public static float DriveSensitivity
    {
        get => Mathf.Clamp(
            PlayerPrefs.GetFloat(DriveSensitivityPrefKey, DefaultDriveSensitivity),
            MinDriveSensitivity, MaxDriveSensitivity);
        set
        {
            PlayerPrefs.SetFloat(DriveSensitivityPrefKey,
                Mathf.Clamp(value, MinDriveSensitivity, MaxDriveSensitivity));
            PlayerPrefs.Save(); // flush now so a force-quit doesn't lose the choice
        }
    }

    public static float TurnSensitivity
    {
        get => Mathf.Clamp(
            PlayerPrefs.GetFloat(TurnSensitivityPrefKey, DefaultTurnSensitivity),
            MinTurnSensitivity, MaxTurnSensitivity);
        set
        {
            PlayerPrefs.SetFloat(TurnSensitivityPrefKey,
                Mathf.Clamp(value, MinTurnSensitivity, MaxTurnSensitivity));
            PlayerPrefs.Save();
        }
    }

    // Retired: "Smooth Acceleration" and "Coast When You Let Go" were checkboxes here, and are now
    // unconditional — they aren't features, they're what a drivetrain does. Wipe the stored ints,
    // because a getter's default only applies when the key is ABSENT: anyone who once unticked
    // either box has a 0 on disk, and without this they would be silently stuck with the old
    // snap-throttle and locked-wheel stop forever, with no control left to turn them back on.
    // (Same DeleteKey-on-upgrade shape as ControlsLayoutSettings.)
    private const string RetiredSmoothAccelerationKey = "SmoothAcceleration";
    private const string RetiredCoastOnReleaseKey = "DriveCoastOnRelease";

    public static void ClearRetiredKeys()
    {
        if (!PlayerPrefs.HasKey(RetiredSmoothAccelerationKey)
            && !PlayerPrefs.HasKey(RetiredCoastOnReleaseKey)) return;

        PlayerPrefs.DeleteKey(RetiredSmoothAccelerationKey);
        PlayerPrefs.DeleteKey(RetiredCoastOnReleaseKey);
        PlayerPrefs.Save();
    }
}
