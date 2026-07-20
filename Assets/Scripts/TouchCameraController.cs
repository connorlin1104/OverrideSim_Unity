using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

// Free-look touch camera for inspecting the field. The camera orbits a focus point that sits on
// the ground:
//   1 finger  drag  -> orbit/pivot around that point
//   2 fingers pinch -> zoom (dolly the camera in/out along its view)
//   2 fingers drag  -> pan the focus point laterally across the field
// CameraDragArbiter decides which fingers are the camera's: a drag that STARTS on a UI element
// (the on-screen drive joysticks) belongs to that element for its whole life, so the camera never
// steals it. A mouse fallback (left-drag orbit / right- or middle-drag pan / scroll zoom) lets you
// test in the plain Game view without the Device Simulator.
//
// This is the DEFAULT of the field scene's two views; RobotChaseCamera is the robot-follow one and
// CameraViewToggle switches between them.
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

    private readonly CameraDragArbiter drag = new CameraDragArbiter();

    // The authored view is decomposed in AWAKE, not Start, because this component is disabled for as
    // long as the player is in the follow view. Unity runs Awake even on a disabled behaviour but
    // withholds Start until it is first enabled — reading the transform in Start would therefore
    // sample whatever pose RobotChaseCamera had left the camera in, and the free view would come
    // back somewhere other than where the player left it.
    void Awake()
    {
        cam = GetComponent<Camera>();

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

    void OnEnable()
    {
        EnhancedTouchSupport.Enable();
        drag.BeginSession();

        // Restore the view the player left, which the follow camera has since moved the transform
        // away from. focus/distance/yaw/pitch survive being disabled, so this is exact.
        ApplyTransform();
    }

    void OnDisable() => EnhancedTouchSupport.Disable();

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
        // Only fingers that didn't START on a joystick / UI element control the camera.
        int usable = drag.CollectTouches(out bool anyTouches, out Touch a, out Touch b);
        if (!anyTouches) return false;
        if (usable == 0) return true; // fingers exist but all on UI: eat them, no camera move

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
        // Left-drag orbits, right/middle-drag pans.
        if (drag.TryGetMouseDrag(out Vector2 delta, out bool leftButton))
        {
            if (leftButton) Orbit(delta);
            else Pan(delta);
        }

        float scroll = drag.GetScroll();
        if (scroll != 0f) Zoom(scroll * scrollZoomSpeed * distance / 120f);
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

    // Euler X comes back as 0..360; fold it to -180..180 so clamping against min/max pitch works.
    private static float NormalizePitch(float x) => x > 180f ? x - 360f : x;
}