using UnityEngine;
using UnityEngine.EventSystems;

// A draggable proxy tile on the home screen's "Edit Control Layout" preview. Each proxy stands
// in for one field-scene control group (a joystick or a button cluster); dragging it saves a
// position offset that ControlsAppearance applies to the real control in the field scene.
//
// The proxy lives inside a preview RectTransform whose local space IS the 1920x1080 reference
// (scaled to fit on screen), so the proxy's anchoredPosition is directly in reference pixels and
// the drag delta is a screen-space delta that transfers 1:1 to the real control's anchoredPosition.
[RequireComponent(typeof(RectTransform))]
public class DraggableControlProxy : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
{
    [Tooltip("Field-scene control name this proxy represents (the offset key). See ControlsLayout.")]
    public string controlName;

    [Tooltip("The preview area the proxy is dragged within; its local space is the 1920x1080 reference.")]
    public RectTransform dragArea;

    [Tooltip("Authored (zero-offset) position of this proxy, from the preview center, in reference px.")]
    public Vector2 basePosition;

    private RectTransform rect;
    private Vector2 grabOffset; // pointer-to-proxy gap at drag start, so the tile doesn't jump

    void Awake()
    {
        rect = (RectTransform)transform;
    }

    // Snap the proxy to base + the saved offset. Called by ControlsLayoutScreen when the screen
    // opens and after a Reset.
    public void ApplySavedOffset()
    {
        if (rect == null) rect = (RectTransform)transform;
        rect.anchoredPosition = basePosition + ControlsLayoutSettings.GetOffset(controlName);
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (TryGetLocalPoint(eventData, out Vector2 local))
            grabOffset = rect.anchoredPosition - local;
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (TryGetLocalPoint(eventData, out Vector2 local))
            rect.anchoredPosition = Clamp(local + grabOffset);
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        ControlsLayoutSettings.SetOffset(controlName, rect.anchoredPosition - basePosition);
    }

    private bool TryGetLocalPoint(PointerEventData eventData, out Vector2 local)
    {
        local = Vector2.zero;
        if (dragArea == null) return false;
        // pressEventCamera is null for a ScreenSpaceOverlay canvas, which is correct here.
        return RectTransformUtility.ScreenPointToLocalPointInRectangle(
            dragArea, eventData.position, eventData.pressEventCamera, out local);
    }

    // Keep the proxy's center inside the preview.
    private Vector2 Clamp(Vector2 position)
    {
        if (dragArea == null) return position;
        Vector2 areaHalf = dragArea.rect.size * 0.5f;
        Vector2 selfHalf = rect.rect.size * 0.5f;
        return new Vector2(
            Mathf.Clamp(position.x, -areaHalf.x + selfHalf.x, areaHalf.x - selfHalf.x),
            Mathf.Clamp(position.y, -areaHalf.y + selfHalf.y, areaHalf.y - selfHalf.y));
    }
}
