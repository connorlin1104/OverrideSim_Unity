using UnityEngine;
using UnityEngine.InputSystem;

// Generic pneumatic piston driver for a prismatic ArticulationBody joint.
//
// VEX pneumatic cylinders are binary: full pressure snaps toward one of two positions. We model that
// as a position-target drive with high stiffness (snap to the endpoint) and moderate damping (no
// ringing). The drive's force is UNCAPPED (forceLimit = infinite) so the piston ALWAYS reaches its
// target — it never stalls partway (which also showed up as "starts slightly extended and won't fully
// retract" when a low force cap couldn't overcome contact/friction).
//
// Usage: put it on a prismatic ArticulationBody link (or assign one), set the extended and
// retracted joint positions, then call Extend()/Retract()/Toggle() from code — or bind the
// optional toggle input action to fire it from a controller button.
public class PneumaticActuator : MonoBehaviour
{
    [Header("Piston")]
    [Tooltip("The prismatic ArticulationBody link to drive. Defaults to the one on this GameObject.")]
    public ArticulationBody body;
    [Tooltip("Joint position (meters/world units along the prismatic axis) when extended.")]
    public float extendedTarget;
    [Tooltip("Joint position (meters/world units along the prismatic axis) when retracted.")]
    public float retractedTarget;

    [Header("Drive Settings")]
    [Tooltip("Position spring gain. High, so the piston snaps between endpoints like real binary pneumatics.")]
    public float stiffness = 20000f;
    [Tooltip("Velocity damping. Enough to kill ringing at the endpoints without feeling sluggish.")]
    public float damping = 500f;
    [Tooltip("Seconds the piston takes to travel end to end. 0 = snap, which is how a real VEX " +
             "cylinder behaves and is right for a jaw. Raise it for a big motion like a 180 degree " +
             "claw flip, which is over before the eye catches it and reads as 'nothing happened'.")]
    public float travelSeconds;
    [Tooltip("Start the match with the piston extended instead of retracted.")]
    public bool startExtended;

    [Header("Input (optional)")]
    [Tooltip("Optional button action; each 'performed' toggles the piston.")]
    public InputActionReference toggleAction;

    public bool IsExtended { get; private set; }

    void Awake()
    {
        if (body == null) body = GetComponent<ArticulationBody>();
        if (body == null)
        {
            Debug.LogWarning("PneumaticActuator: no ArticulationBody assigned or found on this GameObject.", this);
            return;
        }

        // Bake the cylinder model into the joint's X drive (struct: copy, modify, assign back).
        // forceLimit is uncapped so the piston always reaches its target — no air-pressure stall.
        // Set here at runtime, so it also overrides any lower cap baked into an older prefab's drive.
        ArticulationDrive d = body.xDrive;
        d.driveType = ArticulationDriveType.Target;
        d.stiffness = stiffness;
        d.damping = damping;
        d.forceLimit = float.MaxValue;
        d.target = startExtended ? extendedTarget : retractedTarget;
        body.xDrive = d;
        IsExtended = startExtended;
        goalTarget = d.target;
    }

    // Where the drive is being walked TO. Only differs from the live target while a timed travel is
    // running: the joint itself stays as stiff as ever, it's the goal that's moved gradually, so the
    // piston sweeps under full authority instead of going soft and sagging under load.
    private float goalTarget;

    void FixedUpdate()
    {
        if (body == null || travelSeconds <= 0f) return;

        ArticulationDrive d = body.xDrive;
        if (Mathf.Approximately(d.target, goalTarget)) return;

        // Paced over the FULL stroke, so a half-stroke move takes half the time rather than every
        // move taking the same wall-clock however far it goes.
        float span = Mathf.Abs(extendedTarget - retractedTarget);
        float step = span > 0f ? span / travelSeconds * Time.fixedDeltaTime : float.MaxValue;
        d.target = Mathf.MoveTowards(d.target, goalTarget, step);
        body.xDrive = d;
    }

    void OnEnable()
    {
        // The toggle action is optional (pistons are often fired from code), so no warning here.
        if (toggleAction != null)
        {
            toggleAction.action.performed += OnTogglePerformed;
            toggleAction.action.Enable();
        }
    }

    void OnDisable()
    {
        if (toggleAction != null)
        {
            toggleAction.action.performed -= OnTogglePerformed;
            toggleAction.action.Disable();
        }
    }

    private void OnTogglePerformed(InputAction.CallbackContext _) => Toggle();

    public void Extend() => SetTarget(extendedTarget, true);

    public void Retract() => SetTarget(retractedTarget, false);

    public void Toggle()
    {
        if (IsExtended) Retract();
        else Extend();
    }

    private void SetTarget(float target, bool extended)
    {
        if (body == null) return;

        goalTarget = target;
        // IsExtended is INTENT, flipped the moment the button is pressed even when the travel is
        // timed — everything downstream (the grab, the cosmetic cylinder) keys off what was asked
        // for, and a claw that only counts as closed once the jaws arrive would drop what it caught.
        IsExtended = extended;

        // A timed travel is walked to the goal in FixedUpdate; writing it here as well would jump
        // the joint straight there and there would be nothing left to animate.
        if (travelSeconds > 0f) return;

        // xDrive is a struct: copy, modify, assign back or the change silently does nothing.
        ArticulationDrive d = body.xDrive;
        d.target = target;
        body.xDrive = d;
    }
}
