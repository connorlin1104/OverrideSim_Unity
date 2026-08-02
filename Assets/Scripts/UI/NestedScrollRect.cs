using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// A ScrollRect that hands the drag back to the scroll view above it when it has nothing of its own
// to scroll.
//
// Why this has to exist: the split model picker is a short scrolling list INSIDE the settings page,
// which is itself a scrolling list, and both scroll vertically. uGUI delivers a drag to the first
// ancestor that handles it and stops there — so with a plain ScrollRect on the inner list, a drag
// that starts anywhere over the picker is swallowed. With three robots the inner list doesn't scroll
// at all, so the gesture would simply do nothing, and the settings page would look frozen over a
// third of its own area.
//
// The rule: if this list can't scroll in the dragged direction, the parent gets the whole gesture.
// That makes the common case (a list shorter than its viewport) behave exactly as if this component
// weren't here, and only once a column genuinely overflows does it start claiming drags.
//
// Deliberately NOT doing the other common trick — forwarding once the inner list hits its end
// mid-drag. Handing a gesture over part-way through means deciding what to do with the momentum, and
// getting it wrong reads as the list "sticking"; a rule you can state in one sentence is worth more
// here than the extra scroll distance.
[RequireComponent(typeof(RectTransform))]
public class NestedScrollRect : ScrollRect
{
    // Resolved on first use rather than in Awake: ScrollRect's own Awake is not virtual, and the
    // parent can't change without a reparent, which nothing here does.
    private ScrollRect parentScroll;
    private bool parentSearched;

    // Decided once per gesture, at OnBeginDrag, so a list that resizes mid-drag can't hand the
    // gesture over halfway and drop it.
    private bool routeToParent;

    public override void OnInitializePotentialDrag(PointerEventData eventData)
    {
        base.OnInitializePotentialDrag(eventData);
        // Unconditional: this only stops inertia, and stopping the parent's inertia when the player
        // puts a finger down is what every scroll view does.
        ParentScroll()?.OnInitializePotentialDrag(eventData);
    }

    public override void OnBeginDrag(PointerEventData eventData)
    {
        routeToParent = !CanScrollVertically();
        if (routeToParent) ParentScroll()?.OnBeginDrag(eventData);
        else base.OnBeginDrag(eventData);
    }

    public override void OnDrag(PointerEventData eventData)
    {
        if (routeToParent) ParentScroll()?.OnDrag(eventData);
        else base.OnDrag(eventData);
    }

    public override void OnEndDrag(PointerEventData eventData)
    {
        if (routeToParent) ParentScroll()?.OnEndDrag(eventData);
        else base.OnEndDrag(eventData);
        routeToParent = false;
    }

    // The mouse wheel in the editor. Decided per event, not per gesture, because a wheel notch has
    // no begin and end to be consistent between.
    public override void OnScroll(PointerEventData data)
    {
        if (CanScrollVertically()) base.OnScroll(data);
        else ParentScroll()?.OnScroll(data);
    }

    // The 1-unit slack keeps a list whose content is exactly its viewport height — the usual result
    // of a layout group and a fitter agreeing — from claiming a drag it would move zero pixels.
    private bool CanScrollVertically()
    {
        if (!vertical || content == null || viewport == null) return false;

        return content.rect.height > viewport.rect.height + 1f;
    }

    private ScrollRect ParentScroll()
    {
        if (parentSearched) return parentScroll;

        parentSearched = true;
        // From the PARENT, not from here: GetComponentInParent starts at self and would find this.
        if (transform.parent != null)
            parentScroll = transform.parent.GetComponentInParent<ScrollRect>();
        return parentScroll;
    }
}
