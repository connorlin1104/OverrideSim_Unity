using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

// Touch camera for inspecting the field. The camera orbits a focus point that sits on the ground:
//   1 finger  drag  -> orbit/pivot around that point
//   2 fingers pinch -> zoom (dolly the camera in/out along its view)
//   2 fingers drag  -> pan the focus point laterally across the field
// It ignores any finger that lands on a UI element, so touching the on-screen drive joysticks
// never moves the camera. A mouse fallback (left-drag orbit / right- or middle-drag pan / scroll
// zoom) lets you test in the plain Game view without the Device Simulator.
[RequireComponent(typeof(Camera))]
public class TouchCameraController : MonoBehaviour
{
    [Header("Focus")]
    [Tooltip("World Y of the ground plane the camera pivots over (RoboSim floor ≈ 0.72).")]
    [SerializeField] private float groundY = 0.72f;

    [Header("Orbit (1 finger / left-drag)")]
    [Tooltip("Degrees of rotation per screen pixel dragged.")]
    [SerializeField] private float orbitSpeed = 0.2f;
    [SerializeField] private float minPitch = 12f;   // how flat you can get (near horizontal)
    [SerializeField] private float maxPitch = 85f;   // how close to straight-down you can get

    [Header("Zoom (pinch / scroll)")]
    [Tooltip("Fraction of the current distance changed per pixel of pinch. Higher = faster zoom.")]
    [SerializeField] private float pinchZoomSpeed = 0.005f;
    [SerializeField] private float scrollZoomSpeed = 0.12f; // mouse wheel notch strength
    [SerializeField] private float minDistance = 8f;
    [SerializeField] private float maxDistance = 160f;

    [Header("Pan (2 fingers / right- or middle-drag)")]
    [Tooltip("Extra multiplier on the physically-correct pan (1 = fingers track the ground exactly).")]
    [SerializeField] private float panSpeed = 1f;

    private Camera cam;
    private Vector3 focus;   // the ground point the camera looks at and orbits
    private float distance;  // camera-to-focus distance
    private float yaw;       // rotation around world Y
    private float pitch;     // tilt down from horizontal

    // Reused each frame for the UI hit-test so we don't allocate.
    private readonly List<RaycastResult> uiHits = new List<RaycastResult>();
    private PointerEventData pointerData;

    void Awake() => cam = GetComponent<Camera>();

    void OnEnable() => EnhancedTouchSupport.Enable();
    void OnDisable() => EnhancedTouchSupport.Disable();

    void Start()
    {
        // Pivot around the point the camera is currently looking at on the ground, so nothing jumps
        // when play starts — we just decompose the existing transform into orbit angles + distance.
        Vector3 fwd = transform.forward;
        float t = Mathf.Abs(fwd.y) > 0.001f ? (groundY - transform.position.y) / fwd.y : -1f;
        focus = t > 0f ? transform.position + fwd * t : transform.position + fwd * 50f;

        distance = Mathf.Clamp(Vector3.Distance(transform.position, focus), minDistance, maxDistance);
        Vector3 e = transform.eulerAngles;
        yaw = e.y;
        pitch = Mathf.Clamp(NormalizePitch(e.x), minPitch, maxPitch);
        ApplyTransform();
    }

    void LateUpdate()
    {
        // Touch drives on device; mouse is a convenience for the editor Game view.
        if (!HandleTouch())
            HandleMouse();

        ApplyTransform();
    }

    // Returns true if touch input was present (so we skip the mouse fallback).
    private bool HandleTouch()
    {
        // Only fingers that AREN'T on a joystick / UI element control the camera.
        var active = Touch.activeTouches;
        int usable = 0;
        Touch a = default, b = default;
        foreach (var touch in active)
        {
            if (IsOverUI(touch.screenPosition)) continue;
            if (usable == 0) a = touch;
            else if (usable == 1) b = touch;
            usable++;
        }

        if (usable == 0) return active.Count > 0; // fingers exist but all on UI: eat them, no camera move

        if (usable == 1)
        {
            // One finger: orbit. delta is the pixels moved since last frame.
            Orbit(a.delta);
            return true;
        }

        // Two fingers: pinch-zoom + pan together (both feel natural simultaneously).
        Vector2 p0 = a.screenPosition, p1 = b.screenPosition;
        Vector2 prev0 = p0 - a.delta, prev1 = p1 - b.delta;

        float pinch = Vector2.Distance(p0, p1) - Vector2.Distance(prev0, prev1);
        Zoom(pinch * pinchZoomSpeed * distance);

        Vector2 centroidDelta = ((p0 + p1) - (prev0 + prev1)) * 0.5f;
        Pan(centroidDelta);
        return true;
    }

    private void HandleMouse()
    {
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null) return;
        if (IsOverUI(mouse.position.ReadValue())) return;

        Vector2 delta = mouse.delta.ReadValue();
        if (mouse.leftButton.isPressed) Orbit(delta);
        else if (mouse.rightButton.isPressed || mouse.middleButton.isPressed) Pan(delta);

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) > 0.01f) Zoom(scroll * scrollZoomSpeed * distance / 120f);
    }

    private void Orbit(Vector2 pixels)
    {
        yaw += pixels.x * orbitSpeed;
        pitch = Mathf.Clamp(pitch - pixels.y * orbitSpeed, minPitch, maxPitch);
    }

    private void Zoom(float worldDelta)
    {
        distance = Mathf.Clamp(distance - worldDelta, minDistance, maxDistance);
    }

    private void Pan(Vector2 pixels)
    {
        // Convert pixels to world units so the ground tracks the fingers regardless of zoom level.
        float worldPerPixel = 2f * distance * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Screen.height;

        // Move along the ground plane using the camera's flattened right/forward axes.
        Vector3 right = transform.right; right.y = 0f; right.Normalize();
        Vector3 fwd = transform.forward; fwd.y = 0f; fwd.Normalize();

        // Drag content WITH the fingers, so a rightward drag slides the view left.
        focus -= (right * pixels.x + fwd * pixels.y) * worldPerPixel * panSpeed;
    }

    private void ApplyTransform()
    {
        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
        transform.SetPositionAndRotation(focus + rot * new Vector3(0f, 0f, -distance), rot);
    }

    // True if this screen position is over any UI element (e.g. an on-screen drive joystick).
    private bool IsOverUI(Vector2 screenPos)
    {
        if (EventSystem.current == null) return false;
        pointerData ??= new PointerEventData(EventSystem.current);
        pointerData.position = screenPos;
        uiHits.Clear();
        EventSystem.current.RaycastAll(pointerData, uiHits);
        return uiHits.Count > 0;
    }

    // Euler X comes back as 0..360; fold it to -180..180 so clamping against min/max pitch works.
    private static float NormalizePitch(float x) => x > 180f ? x - 360f : x;
}