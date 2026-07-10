using UnityEngine;
using UnityEngine.InputSystem;

// Generic pneumatic piston driver for a prismatic ArticulationBody joint.
//
// VEX pneumatic cylinders are binary: full pressure toward one of two positions, with force
// capped by air pressure. We model that as a position-target drive with high stiffness (snap
// to the endpoint), moderate damping (no ringing), and a forceLimit standing in for the
// air-pressure force cap — push against something too heavy and the piston stalls, just like
// real air.
//
// NOTE: nothing in the current scene uses this component yet. It exists so URDF-imported
// mechanisms (and future scratch-built lifts/claws) have a ready-made prismatic actuator.
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
    [Tooltip("Drive force limit — the air-pressure-limited force the cylinder can exert.")]
    public float cylinderForce = 500f;
    [Tooltip("Position spring gain. High, so the piston snaps between endpoints like real binary pneumatics.")]
    public float stiffness = 20000f;
    [Tooltip("Velocity damping. Enough to kill ringing at the endpoints without feeling sluggish.")]
    public float damping = 500f;
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
        ArticulationDrive d = body.xDrive;
        d.driveType = ArticulationDriveType.Target;
        d.stiffness = stiffness;
        d.damping = damping;
        d.forceLimit = cylinderForce;
        d.target = startExtended ? extendedTarget : retractedTarget;
        body.xDrive = d;
        IsExtended = startExtended;
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

        // xDrive is a struct: copy, modify, assign back or the change silently does nothing.
        ArticulationDrive d = body.xDrive;
        d.target = target;
        body.xDrive = d;
        IsExtended = extended;
    }
}
