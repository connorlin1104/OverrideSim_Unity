using UnityEngine;

// The canonical set of repositionable on-screen control groups, shared by everything that
// touches control layout:
//   - ControlsLayoutSettings keys its saved offsets by these names,
//   - ControlsAppearance applies the offsets to the field controls of these names,
//   - the Build Home Screen tool lays out one draggable proxy per entry on the layout preview.
//
// Name = the field-scene GameObject name of the control's root (joystick background or button
// cluster root). previewCenter is a SCHEMATIC position on the 1920x1080 preview, measured from
// the preview center (x right, y up) — it only needs to roughly match the real layout, because
// what persists is the DRAG DELTA from this base, which is applied verbatim to the real control.
public static class ControlsLayout
{
    public const string LeftJoystick = "LeftJoystick_BG";
    public const string RightJoystick = "RightJoystick_BG";
    public const string ShoulderLeft = "ShoulderButtonsLeft";
    public const string ShoulderRight = "ShoulderButtonsRight";
    public const string ArrowPad = "ButtonPadLeft";   // the four arrows, kept as one bundle
    public const string ButtonPad = "ButtonPadRight";  // X / Y / A / B, kept as one bundle

    public struct ControlInfo
    {
        public string name;         // field-scene root name = offset key
        public string label;        // shown on the preview proxy tile
        public Vector2 previewCenter; // schematic position, from preview center, px (x right, y up)
        public Vector2 previewSize;   // proxy tile size on the preview, px
    }

    // Ordered for a stable proxy layout; also the list Reset() clears.
    public static readonly ControlInfo[] Controls =
    {
        new ControlInfo { name = LeftJoystick,   label = "L Stick", previewCenter = new Vector2(-710f, -290f), previewSize = new Vector2(200f, 200f) },
        new ControlInfo { name = RightJoystick,  label = "R Stick", previewCenter = new Vector2( 710f, -290f), previewSize = new Vector2(200f, 200f) },
        new ControlInfo { name = ShoulderLeft,   label = "L1 / L2", previewCenter = new Vector2(-830f,  418f), previewSize = new Vector2(180f, 130f) },
        new ControlInfo { name = ShoulderRight,  label = "R1 / R2", previewCenter = new Vector2( 830f,  418f), previewSize = new Vector2(180f, 130f) },
        new ControlInfo { name = ArrowPad,       label = "Arrows",  previewCenter = new Vector2(-230f, -360f), previewSize = new Vector2(210f, 210f) },
        new ControlInfo { name = ButtonPad,      label = "X Y A B", previewCenter = new Vector2( 230f, -360f), previewSize = new Vector2(210f, 210f) },
    };
}
