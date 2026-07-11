using System.Collections.Generic;
using UnityEngine;

// Applies the player's on-screen control settings — size (JoystickSettings.Scale), opacity
// (ControlsOpacitySettings.Opacity), and per-group position (ControlsLayoutSettings) — to the
// field scene's joysticks and controller buttons.
// Supersedes the old JoystickScaler (size-only, sticks-only). Lives on the field scene's
// Canvas; the Build Drive Controls tool wires the joystick backgrounds, the four button
// cluster roots, and the CanvasGroups that carry the opacity.
//
// Each control root's pivot sits at its own screen corner/edge, so scaling localScale grows it
// inward from that anchor — nothing slides off-screen. The button clusters cap their scale
// below the joystick maximum AND below whatever fits the shrinking center band between the
// sticks, so oversized sticks never sit under the bottom-center diamonds (which would render
// on top and steal their touches).
public class ControlsAppearance : MonoBehaviour
{
    public const float ButtonMaxScale = 1.4f;
    // Small clusters stay tappable even when giant sticks squeeze the band. At the very top of
    // the stick range (scale > ~2.48) this floor concedes a ~5px pad/stick overlap sliver.
    public const float ButtonMinScale = 0.5f;

    // Field-scene layout facts the band cap derives from — keep in sync with the authored
    // joystick rects and BuildDriveControls: sticks sit 150px from the screen side at 200px
    // wide (scaling from their corner pivots, inward); each pad cluster's center sits 230px
    // from screen center with a 150px half-width at scale 1 (scaling from its bottom-center
    // pivot). All at the 1920x1080 canvas reference resolution.
    private const float ScreenHalfWidth = 960f;
    private const float StickEdgeOffset = 150f;
    private const float StickSize = 200f;
    private const float PadCenterOffset = 230f;
    private const float PadHalfWidth = 150f;
    private const float PadStickGap = 10f;

    [Tooltip("Joystick background RectTransforms to scale (LeftJoystick_BG, RightJoystick_BG).")]
    [SerializeField] private RectTransform[] joysticks;

    [Tooltip("Button cluster roots to scale (shoulder pairs + the two diamond pads); capped at ButtonMaxScale.")]
    [SerializeField] private RectTransform[] buttonClusters;

    [Tooltip("CanvasGroups on every on-screen control root; alpha = the opacity setting.")]
    [SerializeField] private CanvasGroup[] opacityGroups;

    // Each control's authored anchoredPosition, captured the first time Apply() runs (the scene
    // loads fresh with the authored values, before any offset is applied). Saved offsets are
    // added to THIS base every Apply, so re-applying never compounds.
    private readonly Dictionary<RectTransform, Vector2> basePositions = new Dictionary<RectTransform, Vector2>();

    void OnEnable()
    {
        Apply();
    }

    // Public so a future in-scene control could re-apply live; today it runs on enable, which is
    // enough because the field scene is loaded fresh every time it's entered from the home screen.
    public void Apply()
    {
        float scale = JoystickSettings.Scale;
        if (joysticks != null)
        {
            foreach (RectTransform joystick in joysticks)
            {
                if (joystick == null) continue;
                joystick.localScale = new Vector3(scale, scale, 1f);
                ApplyPosition(joystick);
            }
        }

        // The sticks' inner edge sits ScreenHalfWidth - StickEdgeOffset - StickSize*scale from
        // screen center; the pads' outer edge at cluster scale c is PadCenterOffset +
        // PadHalfWidth*c. Solve for the largest c that keeps a gap between them.
        float bandCap = (ScreenHalfWidth - StickEdgeOffset - StickSize * scale - PadStickGap - PadCenterOffset)
                        / PadHalfWidth;
        float buttonScale = Mathf.Clamp(Mathf.Min(scale, bandCap), ButtonMinScale, ButtonMaxScale);
        if (buttonClusters != null)
        {
            foreach (RectTransform cluster in buttonClusters)
            {
                if (cluster == null) continue;
                cluster.localScale = new Vector3(buttonScale, buttonScale, 1f);
                ApplyPosition(cluster);
            }
        }

        float opacity = ControlsOpacitySettings.Opacity;
        if (opacityGroups != null)
        {
            foreach (CanvasGroup group in opacityGroups)
            {
                if (group != null) group.alpha = opacity;
            }
        }
    }

    // Offset a control from its authored position by the player's saved layout delta. The base is
    // captured once (keyed by the control's GameObject name, which matches ControlsLayout), so
    // repeated Apply() calls never accumulate. The delta is a screen-space (right/up) offset in
    // reference pixels — the same axes as anchoredPosition — so it transfers straight through.
    private void ApplyPosition(RectTransform control)
    {
        if (!basePositions.TryGetValue(control, out Vector2 basePosition))
        {
            basePosition = control.anchoredPosition;
            basePositions[control] = basePosition;
        }
        control.anchoredPosition = basePosition + ControlsLayoutSettings.GetOffset(control.name);
    }
}
