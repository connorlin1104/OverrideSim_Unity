using UnityEngine;

// Shared, PlayerPrefs-backed setting for whether the match loaders spawn automatically when the
// robot drives onto their tape (the default), or only when the player presses the field scene's
// Match Load button. Manual mode exists for drivers who want the piece to fall INTO the robot
// (the manual spawn adds extra height) instead of picking it off the loader.
//
// Stored in PlayerPrefs — not on any asset — so changing it never dirties the project and the
// choice persists per device across restarts (same approach as ControlsOpacitySettings).
public static class MatchLoadSettings
{
    public const string AutomaticPrefKey = "AutomaticMatchloading";
    public const bool DefaultAutomatic = true;

    public static bool Automatic
    {
        get => PlayerPrefs.GetInt(AutomaticPrefKey, DefaultAutomatic ? 1 : 0) != 0;
        set
        {
            PlayerPrefs.SetInt(AutomaticPrefKey, value ? 1 : 0);
            PlayerPrefs.Save(); // flush now so a force-quit doesn't lose the choice
        }
    }
}
