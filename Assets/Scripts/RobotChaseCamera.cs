using UnityEngine;
using UnityEngine.InputSystem.EnhancedTouch;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;

// The field scene's second view: a chase camera anchored on the robot, raised above and behind it,
// that the player can swing all the way around the bot.
//   1 finger  drag  -> orbit (yaw around the robot, pitch up/down)
//   2 fingers pinch -> pull in / push out
// There is deliberately NO pan: the focus point IS the robot, and letting it slide off would turn
// this back into the free camera (TouchCameraController), which is the other view.
//
// Distances are in the project's ~10x world scale — 1 unit = 0.1 m = 100 mm — so an 18" VEX robot
// is only ~4.6 units across and the whole field ~36.6. The 14-unit default sits about 1.4 m back,
// roughly three robot-widths, which frames the bot with enough field around it to aim.
//
// Added to the ChaseCamera object by Tools > RoboSim > Scenes > Build Drive Controls; the on-screen
// CameraViewToggle enables this camera or the free one, never both.
[RequireComponent(typeof(Camera))]
public class RobotChaseCamera : MonoBehaviour
{
    // Both the wrapper and every articulation LINK are tagged this (the match loaders identify the
    // robot by the tag on a collider's body), so a tag lookup can land on a wheel — see ResolveRoot.
    private const string RobotTag = "Player";
    private const float SearchInterval = 0.5f;

    [Header("Framing")]
    [Tooltip("Camera-to-robot distance in world units (1 unit = 100 mm), adjusted by pinch/scroll.")]
    [SerializeField] private float distance = 10f;
    [SerializeField] private float minDistance = 6f;
    [SerializeField] private float maxDistance = 40f;

    [Tooltip("Start tilt, in degrees below horizontal. ~28 looks over the robot without going top-down.")]
    [SerializeField] private float pitch = 28f;
    [SerializeField] private float minPitch = 5f;    // how flat you can get
    [SerializeField] private float maxPitch = 82f;   // stops short of straight down, so it can't invert

    [Tooltip("Raise the aim point this far (world units) above the robot's collider center, so the " +
             "bot sits slightly low in frame and you see where it is driving.")]
    [SerializeField] private float focusRise = 1.5f;

    [Header("Follow")]
    [Tooltip("Seconds for the camera to catch up to the robot. Small enough not to feel floaty.")]
    [SerializeField] private float followSmoothTime = 0.12f;

    [Tooltip("Keep the camera behind the robot as it turns, the way a driver's-eye view would. " +
             "Off = the camera holds a fixed world angle and the robot rotates within the frame.")]
    [SerializeField] private bool followRobotHeading = true;

    [Tooltip("Seconds for the camera to swing around to a new robot heading. Damped because a VEX " +
             "bot can spin far faster than a camera should whip around.")]
    [SerializeField] private float headingSmoothTime = 0.25f;

    [Header("Input")]
    [Tooltip("Degrees of rotation per screen pixel dragged (matches the free camera).")]
    [SerializeField] private float orbitSpeed = 0.2f;
    [SerializeField] private float pinchZoomSpeed = 0.005f;
    [SerializeField] private float scrollZoomSpeed = 0.12f;

    [Header("Limits")]
    [Tooltip("Field floor surface Y — the camera is never allowed below it (RoboSim floor ≈ 0.72).")]
    [SerializeField] private float groundY = 0.72f;
    [SerializeField] private float floorClearance = 1.5f;

    private Transform robot;

    // Aim point as an offset in the ROBOT'S local space, captured once when the robot is found.
    private Vector3 focusLocal;

    [Tooltip("Where the camera sits around the robot before you drag it, in degrees. The robot's +Z " +
             "is its side rather than its nose on these CAD imports, so this is a quarter turn round " +
             "to put the claw end in frame — and the far quarter, so the camera looks AT the claw " +
             "from behind the bot rather than standing in front of it.")]
    [SerializeField] private float startYawOffset = -90f;

    private float yawOffset;      // the player's orbit, on top of the robot's heading
    private float heading;        // damped robot heading the camera trails
    private float headingVelocity;
    private Vector3 focus;        // damped aim point
    private Vector3 focusVelocity;
    private bool anchored;        // false until the robot is found and the camera has snapped to it
    private float nextSearchTime;
    private bool warnedNoRobot;

    private readonly CameraDragArbiter drag = new CameraDragArbiter();

    void Awake() => yawOffset = startYawOffset; // authored resting angle; dragging moves it from here

    void OnEnable()
    {
        EnhancedTouchSupport.Enable();
        drag.BeginSession();

        // Re-anchor on every switch into this view so the camera arrives already framed on the
        // robot instead of sweeping in from wherever the free camera was left.
        anchored = false;
        nextSearchTime = 0f;
    }

    void OnDisable() => EnhancedTouchSupport.Disable();

    void LateUpdate()
    {
        if (robot == null) FindRobot();
        if (robot == null) return; // no robot in the scene: hold the last pose rather than snapping to origin

        // Touch drives on device; mouse is a convenience for the editor Game view.
        if (!HandleTouch())
            HandleMouse();

        Follow();
    }

    // --- Input ------------------------------------------------------------------------------

    // Returns true if touch input was present (so we skip the mouse fallback). CameraDragArbiter
    // keeps fingers that landed on a joystick or button out of this entirely.
    private bool HandleTouch()
    {
        int usable = drag.CollectTouches(out bool anyTouches, out Touch a, out Touch b);
        if (!anyTouches) return false;
        if (usable == 0) return true; // fingers exist but all on UI: eat them, no camera move

        if (usable == 1)
        {
            Orbit(a.delta);
            return true;
        }

        // Two fingers: pinch only. Panning would unhook the camera from the robot.
        Vector2 p0 = a.screenPosition, p1 = b.screenPosition;
        Vector2 prev0 = p0 - a.delta, prev1 = p1 - b.delta;
        float pinch = Vector2.Distance(p0, p1) - Vector2.Distance(prev0, prev1);
        Zoom(pinch * pinchZoomSpeed * distance);
        return true;
    }

    private void HandleMouse()
    {
        // Any camera-owned drag orbits — unlike the free camera there is no pan to reserve the
        // right button for.
        if (drag.TryGetMouseDrag(out Vector2 delta, out _))
            Orbit(delta);

        float scroll = drag.GetScroll();
        if (scroll != 0f) Zoom(scroll * scrollZoomSpeed * distance / 120f);
    }

    private void Orbit(Vector2 pixels)
    {
        // Wrapped so a long session of one-way spinning can't drift into float mush.
        yawOffset = Mathf.Repeat(yawOffset + pixels.x * orbitSpeed, 360f);
        pitch = Mathf.Clamp(pitch - pixels.y * orbitSpeed, minPitch, maxPitch);
    }

    private void Zoom(float worldDelta)
    {
        distance = Mathf.Clamp(distance - worldDelta, minDistance, maxDistance);
    }

    // --- Follow -----------------------------------------------------------------------------

    private void Follow()
    {
        Vector3 focusTarget = robot.TransformPoint(focusLocal) + Vector3.up * focusRise;
        float headingTarget = RobotHeading();

        if (!anchored)
        {
            // First frame on this robot: land exactly on target, then let the damping take over.
            focus = focusTarget;
            heading = headingTarget;
            focusVelocity = Vector3.zero;
            headingVelocity = 0f;
            anchored = true;
        }
        else
        {
            focus = Vector3.SmoothDamp(focus, focusTarget, ref focusVelocity, followSmoothTime);
            heading = Mathf.SmoothDampAngle(heading, headingTarget, ref headingVelocity, headingSmoothTime);
        }

        float yaw = followRobotHeading ? heading + yawOffset : yawOffset;

        // Never let the orbit dip below the field floor. Rather than clamping the final position —
        // which would break the aim — solve the shallowest pitch that still clears the floor at the
        // current distance, so the camera slides along the floor instead of through it.
        float rise = (groundY + floorClearance) - focus.y;
        float effectivePitch = pitch;
        if (rise > 0f && distance > 0.01f)
            effectivePitch = Mathf.Max(pitch, Mathf.Asin(Mathf.Clamp01(rise / distance)) * Mathf.Rad2Deg);

        Quaternion rotation = Quaternion.Euler(effectivePitch, yaw, 0f);
        transform.SetPositionAndRotation(focus + rotation * new Vector3(0f, 0f, -distance), rotation);
    }

    // Which way the robot is being driven, flattened onto the ground plane so a bot climbing a
    // ramp — or tipped over — never rolls or pitches the camera.
    private float RobotHeading()
    {
        // The rig aligns every wheel link's local +X with the robot root's +X (robot right), so the
        // root's +Z is the driving-forward axis — see RobotMotorController's sign convention.
        Vector3 forward = Vector3.ProjectOnPlane(robot.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f) return heading; // nose-up: keep the heading we had

        float value = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;

        // "Reverse Drive Direction" (Settings) makes the opposite end the front. Follow the end the
        // driver is actually steering from, or the camera would sit in front of the robot and every
        // input would read backwards. Read live from PlayerPrefs, as RobotMotorController does.
        return ReverseDriveSettings.Reversed ? value + 180f : value;
    }

    // --- Finding the spawned robot ------------------------------------------------------------

    // The robot is instantiated at runtime by RobotSpawner from the catalog prefab the player
    // picked, so there is nothing to reference from the scene — it has to be found by tag. The
    // spawn happens in Awake, so it is already there by the first LateUpdate; the retry exists for
    // the case where the catalog has no prefab built yet and a robot never appears at all.
    private void FindRobot()
    {
        if (Time.unscaledTime < nextSearchTime) return;
        nextSearchTime = Time.unscaledTime + SearchInterval;

        GameObject tagged = GameObject.FindGameObjectWithTag(RobotTag);
        if (tagged == null)
        {
            if (!warnedNoRobot)
            {
                warnedNoRobot = true;
                Debug.LogWarning("RobotChaseCamera: no object tagged 'Player' in the scene — the " +
                                 "follow camera will hold still until a robot is spawned.", this);
            }
            return;
        }

        robot = ResolveRoot(tagged.transform);
        focusLocal = ComputeFocusLocal(robot);
        anchored = false; // snap onto the robot on the next Follow()
    }

    // The tag is on the wrapper AND on every articulation link, so FindGameObjectWithTag can hand
    // back a wheel. Walk up to the topmost ArticulationBody: that is the articulation root, which is
    // the prefab root the spawner placed and the transform that carries the robot's heading.
    private static Transform ResolveRoot(Transform tagged)
    {
        ArticulationBody body = tagged.GetComponentInParent<ArticulationBody>();
        if (body == null) return tagged.root; // hypothetical non-articulated robot

        while (true)
        {
            Transform parent = body.transform.parent;
            ArticulationBody above = parent != null ? parent.GetComponentInParent<ArticulationBody>() : null;
            if (above == null) return body.transform;
            body = above;
        }
    }

    // Aim at the CENTER OF THE ROBOT'S COLLIDERS rather than its transform origin: a prefab root's
    // origin is the CAD/FBX pivot, which Fusion often puts well off the robot itself (RobotSpawner
    // recenters the spawn for the same reason), so aiming there frames the bot badly off-center.
    //
    // Captured ONCE and stored in the robot's local space: re-measuring every frame would make the
    // camera bob as a lift raises or a claw swings, and would cost a GetComponentsInChildren per
    // frame. The framing stays locked to the chassis instead.
    private static Vector3 ComputeFocusLocal(Transform root)
    {
        Bounds bounds = default;
        bool has = false;
        foreach (Collider col in root.GetComponentsInChildren<Collider>())
        {
            if (col.isTrigger) continue; // triggers (intake zones) reach well outside the robot
            if (!has) { bounds = col.bounds; has = true; }
            else bounds.Encapsulate(col.bounds);
        }
        return has ? root.InverseTransformPoint(bounds.center) : Vector3.zero;
    }
}
