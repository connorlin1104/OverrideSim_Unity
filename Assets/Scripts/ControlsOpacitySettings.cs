using UnityEngine;

// Shared, PlayerPrefs-backed setting for how opaque the on-screen controls (joysticks AND
// controller buttons) are drawn in the field scene. Companion to JoystickSettings (size);
// ControlsAppearance applies both when the field scene loads.
//
// Stored in PlayerPrefs — not on any asset — so changing it never dirties the project and the
// choice persists per device across restarts (same approach as JoystickSettings).
public static class ControlsOpacitySettings
{
    public const string OpacityPrefKey = "ControlsOpacity";

    // The default matches how the joysticks have always looked (their authored image alpha was
    // ~0.59). The floor keeps the controls faintly visible: a CanvasGroup at alpha 0 still
    // receives touches, and fully invisible-but-active controls are a trap.
    public const float MinOpacity = 0.2f;
    public const float MaxOpacity = 1f;
    public const float DefaultOpacity = 0.6f;

    // Reads are clamped so a stale or hand-edited pref can't make the controls invisible.
    public static float Opacity
    {
        get => Mathf.Clamp(PlayerPrefs.GetFloat(OpacityPrefKey, DefaultOpacity), MinOpacity, MaxOpacity);
        set
        {
            PlayerPrefs.SetFloat(OpacityPrefKey, Mathf.Clamp(value, MinOpacity, MaxOpacity));
            PlayerPrefs.Save(); // flush now so a force-quit doesn't lose the choice
        }
    }
}
