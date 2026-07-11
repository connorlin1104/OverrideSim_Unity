using UnityEngine;

// Shared, PlayerPrefs-backed setting for how large the on-screen driving joysticks are drawn.
//
// The joysticks in SampleScene are authored at a fixed pixel size that reads fine on a large
// editor Game view but feels cramped on a physically small phone, since the canvas scales by
// screen size and keeps them the same *proportion* everywhere. This setting lets the player
// scale them up (or down) from the home screen; JoystickScaler applies the value in the field
// scene.
//
// Stored in PlayerPrefs — not on any asset — so changing it never dirties the project and the
// choice persists per device across restarts (same approach as RobotModelCatalog's selection).
public static class JoystickSettings
{
    public const string ScalePrefKey = "JoystickScale";

    // 1.0 == the size the joysticks are authored at in the scene. The default leans larger than
    // authored so the sticks are comfortable on a physically small phone out of the box; the
    // bounds keep them usable: never so tiny they can't be hit, never so huge they swallow the
    // field view.
    public const float MinScale = 0.6f;
    public const float MaxScale = 2.5f;
    public const float DefaultScale = 1.4f;

    // Multiplier applied to the joysticks' authored size. Reads are clamped so a stale or
    // hand-edited pref can't push the sticks off-screen or collapse them to nothing.
    public static float Scale
    {
        get => Mathf.Clamp(PlayerPrefs.GetFloat(ScalePrefKey, DefaultScale), MinScale, MaxScale);
        set
        {
            PlayerPrefs.SetFloat(ScalePrefKey, Mathf.Clamp(value, MinScale, MaxScale));
            PlayerPrefs.Save(); // flush now so a force-quit doesn't lose the choice
        }
    }
}
