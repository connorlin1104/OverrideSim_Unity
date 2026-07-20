using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

// Decides which fingers and mouse drags a camera is allowed to consume, so a camera never steals a
// gesture that belongs to the on-screen driver controls.
//
// The rule that matters: a drag is claimed by the UI at the moment it STARTS and stays claimed for
// its whole life. Testing "is the pointer over UI right now" every frame is not enough — a joystick
// drag routinely travels well outside the stick's own graphic, so a per-frame test hands the drag
// to the camera partway through (the "joysticks also move the camera" bug).
//
// Extracted from TouchCameraController so the free-look and robot-follow views arbitrate input by
// exactly the same rule instead of keeping two copies of it. Each camera owns one instance and
// calls BeginSession() when it takes over the view.
public sealed class CameraDragArbiter
{
    // Fingers that landed on a UI element. Kept for the finger's whole lifetime, not just the frame
    // it touched down on.
    private readonly HashSet<int> uiTouchIds = new HashSet<int>();
    private bool mouseDragStartedOverUI;

    // Reused for the UI hit-test so the per-frame check doesn't allocate.
    private readonly List<RaycastResult> uiHits = new List<RaycastResult>();
    private PointerEventData pointerData;

    // Call when a camera takes over the view (OnEnable). Any gesture already in flight — a finger
    // holding a joystick, a mouse button already down — belongs to whoever started it and never to
    // the camera that just appeared, so it is disowned until released. Without this, tapping the
    // view-toggle button while the other hand drives would hand the driver's joystick finger
    // straight to the incoming camera, because that camera never saw the finger's Began phase.
    public void BeginSession()
    {
        uiTouchIds.Clear();
        if (EnhancedTouchSupport.enabled)
        {
            foreach (Touch touch in Touch.activeTouches) uiTouchIds.Add(touch.touchId);
        }

        Mouse mouse = Mouse.current;
        mouseDragStartedOverUI = mouse != null &&
            (mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed);
    }

    // Collects the fingers this camera owns. Returns how many there are (only the first two are
    // handed back); `anyTouches` reports whether ANY finger is down, so the caller can skip its
    // mouse fallback while a touch gesture is in progress even if every finger belongs to the UI.
    public int CollectTouches(out bool anyTouches, out Touch first, out Touch second)
    {
        first = default;
        second = default;

        var active = Touch.activeTouches;
        anyTouches = active.Count > 0;
        if (!anyTouches)
        {
            uiTouchIds.Clear();
            return 0;
        }

        int usable = 0;
        foreach (Touch touch in active)
        {
            UnityEngine.InputSystem.TouchPhase phase = touch.phase;

            // Claim the finger for the UI on the frame it lands; it stays claimed for its whole life.
            if (phase == UnityEngine.InputSystem.TouchPhase.Began && IsOverUI(touch.screenPosition))
                uiTouchIds.Add(touch.touchId);

            // A lifted finger is still listed for this one frame: release its id and ignore it.
            if (phase == UnityEngine.InputSystem.TouchPhase.Ended ||
                phase == UnityEngine.InputSystem.TouchPhase.Canceled)
            {
                uiTouchIds.Remove(touch.touchId);
                continue;
            }

            if (uiTouchIds.Contains(touch.touchId)) continue;

            if (usable == 0) first = touch;
            else if (usable == 1) second = touch;
            usable++;
        }
        return usable;
    }

    // A camera-owned mouse drag, for testing in the plain Game view without the Device Simulator.
    // `primaryButton` is true for the left button, which each camera maps to its own main gesture.
    // The over-UI decision is made once, when the button goes down, for the same reason touches are.
    public bool TryGetMouseDrag(out Vector2 delta, out bool primaryButton)
    {
        delta = Vector2.zero;
        primaryButton = false;

        Mouse mouse = Mouse.current;
        if (mouse == null) return false;

        bool held = mouse.leftButton.isPressed || mouse.rightButton.isPressed || mouse.middleButton.isPressed;
        bool pressedNow = mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame ||
                          mouse.middleButton.wasPressedThisFrame;

        if (pressedNow) mouseDragStartedOverUI = IsOverUI(mouse.position.ReadValue());
        if (!held) mouseDragStartedOverUI = false;

        if (!held || mouseDragStartedOverUI) return false;

        delta = mouse.delta.ReadValue();
        primaryButton = mouse.leftButton.isPressed;
        return true;
    }

    // Wheel notches this camera owns. Scrolling has no press/release to latch a claim onto, so it
    // just checks where the cursor is right now.
    public float GetScroll()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null) return 0f;

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) <= 0.01f) return 0f;
        return IsOverUI(mouse.position.ReadValue()) ? 0f : scroll;
    }

    // True if this screen position is over any UI element (e.g. an on-screen drive joystick).
    public bool IsOverUI(Vector2 screenPos)
    {
        if (EventSystem.current == null) return false;
        pointerData ??= new PointerEventData(EventSystem.current);
        pointerData.position = screenPos;
        uiHits.Clear();
        EventSystem.current.RaycastAll(pointerData, uiHits);
        return uiHits.Count > 0;
    }
}
