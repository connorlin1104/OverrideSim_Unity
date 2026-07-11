using UnityEngine;

// Per-device saved positions for the on-screen control groups, edited on the home screen's
// "Edit Control Layout" preview and applied in the field scene by ControlsAppearance.
//
// Each control's saved value is an OFFSET (dx, dy) in 1920x1080 reference pixels from its
// authored position (x right, y up — the same axes as RectTransform.anchoredPosition), keyed by
// the control's field-scene GameObject name (see ControlsLayout). Storing a delta rather than an
// absolute position means a Reset just clears the keys, and re-running the Build Drive Controls
// tool (which re-authors the base positions) doesn't strand a saved layout.
//
// Stored in PlayerPrefs — not on any asset — so editing never dirties the project and the choice
// persists across restarts (same approach as ControlsOpacitySettings / JoystickSettings).
public static class ControlsLayoutSettings
{
    private const string KeyPrefix = "ControlsPos_";

    public static Vector2 GetOffset(string controlName)
    {
        return new Vector2(
            PlayerPrefs.GetFloat(KeyPrefix + controlName + "_x", 0f),
            PlayerPrefs.GetFloat(KeyPrefix + controlName + "_y", 0f));
    }

    public static void SetOffset(string controlName, Vector2 offset)
    {
        PlayerPrefs.SetFloat(KeyPrefix + controlName + "_x", offset.x);
        PlayerPrefs.SetFloat(KeyPrefix + controlName + "_y", offset.y);
        PlayerPrefs.Save(); // flush now so a force-quit doesn't lose the layout
    }

    // Clears every saved control position so the layout returns to the authored defaults.
    public static void Reset()
    {
        foreach (ControlsLayout.ControlInfo control in ControlsLayout.Controls)
        {
            PlayerPrefs.DeleteKey(KeyPrefix + control.name + "_x");
            PlayerPrefs.DeleteKey(KeyPrefix + control.name + "_y");
        }
        PlayerPrefs.Save();
    }
}
