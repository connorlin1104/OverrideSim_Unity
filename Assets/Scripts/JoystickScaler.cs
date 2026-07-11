using UnityEngine;

// LEGACY — superseded by ControlsAppearance (size AND opacity, sticks AND buttons). The build
// tools now remove this component from the field Canvas and wire ControlsAppearance instead;
// the class is kept only so an unmigrated scene still compiles. Safe to delete once
// Build Drive Controls has run and the scene is saved without it.
//
// Applies the player's chosen joystick size (JoystickSettings.Scale) to the on-screen joysticks
// in the field scene. Lives on the field scene's Canvas; the Build Home Screen tool wires the
// two joystick background RectTransforms (LeftJoystick_BG, RightJoystick_BG) into it.
//
// Each joystick's pivot sits at its own screen corner, so scaling localScale grows it inward
// from that corner — it stays anchored in place and never slides off-screen. Scaling the
// background also scales its child handle, so the OnScreenStick's visual travel grows with it
// while its normalized output (full deflection == 1.0) is unchanged.
public class JoystickScaler : MonoBehaviour
{
    [Tooltip("Joystick background RectTransforms to scale (LeftJoystick_BG, RightJoystick_BG).")]
    [SerializeField] private RectTransform[] joysticks;

    void OnEnable()
    {
        Apply();
    }

    // Public so a future in-scene control could re-apply live; today it runs on enable, which is
    // enough because the field scene is loaded fresh every time it's entered from the home screen.
    public void Apply()
    {
        if (joysticks == null) return;
        float scale = JoystickSettings.Scale;
        foreach (RectTransform joystick in joysticks)
        {
            if (joystick != null) joystick.localScale = new Vector3(scale, scale, 1f);
        }
    }
}
