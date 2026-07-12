using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Obvious press feedback for an on-screen control button: while held it sinks in (a small downward
// offset), shrinks slightly, and flashes brighter — then springs back on release. The stock
// UI.Button ColorTint transition only multiplies the sprite color by ~0.78 on press, which is
// nearly invisible on the dark control buttons; this makes a press unmistakable.
//
// Attached to each on-screen control by BuildDriveControls, which also sets the Button's transition
// to None so its ColorTint doesn't fight the color written here. Driven by pointer (touch/mouse)
// events, independent of the OnScreenButton input path — both receive the same pointer events.
//
// Safe against ControlsAppearance: that scales the cluster roots and sets CanvasGroup opacity, none
// of which touch an individual button's own localScale / anchoredPosition / graphic color — the
// three things animated here.
[RequireComponent(typeof(RectTransform))]
public class PressFeedback : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    [Tooltip("Local scale while pressed (1 = no change). 0.88 = 12% smaller, reads as pushed in.")]
    public float pressedScale = 0.88f;
    [Tooltip("Anchored-position offset while pressed, in canvas pixels — the 'indent'. Negative Y sinks the button down.")]
    public Vector2 pressedOffset = new Vector2(0f, -7f);
    [Tooltip("Color the target graphic flashes to while pressed (lerped toward). Bright so the press pops on dark buttons.")]
    public Color pressedColor = new Color(0.85f, 0.9f, 1f, 1f);
    [Tooltip("How strongly to blend toward pressedColor (0 = keep base color, 1 = full pressedColor).")]
    [Range(0f, 1f)]
    public float pressedColorBlend = 0.75f;
    [Tooltip("Spring speed to/from the pressed state (higher = snappier).")]
    public float lerpSpeed = 22f;

    private RectTransform rect;
    private Graphic graphic;          // the button's image; tinted on press (may be null)
    private Vector3 baseScale = Vector3.one;
    private Vector2 basePos;
    private Color baseColor = Color.white;
    private bool pressed;

    void Awake()
    {
        rect = (RectTransform)transform;
        baseScale = rect.localScale;
        basePos = rect.anchoredPosition;

        // Prefer the Button's target graphic (the sprite), else the first Graphic on this object.
        Button button = GetComponent<Button>();
        graphic = (button != null ? button.targetGraphic as Graphic : null) ?? GetComponent<Graphic>();
        if (graphic != null) baseColor = graphic.color;
    }

    public void OnPointerDown(PointerEventData eventData) => pressed = true;
    public void OnPointerUp(PointerEventData eventData) => pressed = false;

    void OnDisable()
    {
        // Snap back so a button hidden/disabled mid-press doesn't stay stuck indented.
        pressed = false;
        if (rect != null)
        {
            rect.localScale = baseScale;
            rect.anchoredPosition = basePos;
        }
        if (graphic != null) graphic.color = baseColor;
    }

    void Update()
    {
        // Frame-rate-independent approach to the target (unscaled so it works even if paused).
        float t = 1f - Mathf.Exp(-lerpSpeed * Time.unscaledDeltaTime);

        Vector3 targetScale = pressed ? baseScale * pressedScale : baseScale;
        Vector2 targetPos = pressed ? basePos + pressedOffset : basePos;
        rect.localScale = Vector3.Lerp(rect.localScale, targetScale, t);
        rect.anchoredPosition = Vector2.Lerp(rect.anchoredPosition, targetPos, t);

        if (graphic != null)
        {
            Color targetColor = pressed ? Color.Lerp(baseColor, pressedColor, pressedColorBlend) : baseColor;
            targetColor.a = baseColor.a; // keep the button's own alpha; opacity is a CanvasGroup concern
            graphic.color = Color.Lerp(graphic.color, targetColor, t);
        }
    }
}
