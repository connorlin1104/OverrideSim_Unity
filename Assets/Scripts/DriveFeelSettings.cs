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
    public const string SmoothAccelerationPrefKey = "SmoothAcceleration";
    public const string CoastOnReleasePrefKey = "DriveCoastOnRelease";

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

    // Off restores the pre-tuning behaviour: the command jumps straight to the stick instead of
    // ramping. Kept as an option because a few drivers liked the snap.
    public const bool DefaultSmoothAcceleration = true;

    // On: releasing the sticks RELEASES the wheels and the robot glides. Off: it brakes.
    public const bool DefaultCoastOnRelease = true;

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

    public static bool SmoothAcceleration
    {
        get => PlayerPrefs.GetInt(SmoothAccelerationPrefKey, DefaultSmoothAcceleration ? 1 : 0) != 0;
        set
        {
            PlayerPrefs.SetInt(SmoothAccelerationPrefKey, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }

    public static bool CoastOnRelease
    {
        get => PlayerPrefs.GetInt(CoastOnReleasePrefKey, DefaultCoastOnRelease ? 1 : 0) != 0;
        set
        {
            PlayerPrefs.SetInt(CoastOnReleasePrefKey, value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }
}
