using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Routes the 12 controller button actions to the robot's mechanisms according to the
// player's saved ButtonMap (made on the home screen's Configure Controller panel).
//
// Motors are hold-to-run: every held forward button contributes +1, every held reverse
// button -1, the clamped sum drives MotorActuator.SetInput — so R1(fwd)+R2(rev) held
// together cancels to a hold, which is what two opposing buttons physically do. Pneumatics
// fire on the press edge (performed), toggling like real VEX solenoid buttons.
//
// The buttons reach this component as InputActions bound to <Gamepad> paths, fed either by
// the on-screen OnScreenButtons (which write a shared virtual gamepad, same as the sticks)
// or by a real Bluetooth controller — both work with zero extra wiring.
//
// Usage: added to the robot root and fully wired (mechanisms + the 12 button actions in
// ControllerButton order) by the URDF post-processor. Assignments naming mechanisms that no
// longer exist on the robot are skipped silently.
public class ButtonRouter : MonoBehaviour
{
    [Tooltip("The robot's mechanism registry. Defaults to the one on this GameObject.")]
    public RobotMechanisms mechanisms;

    [Tooltip("Button actions in ControllerButton enum order: L1 L2 R1 R2 Up Down Left Right X B A Y.")]
    public InputActionReference[] buttonActions = new InputActionReference[ControllerMapSettings.ButtonCount];

    // Per-motor accumulation of the buttons mapped to it.
    private class MotorBinding
    {
        public MotorActuator motor;
        public readonly List<InputAction> forward = new List<InputAction>();
        public readonly List<InputAction> reverse = new List<InputAction>();
    }

    private readonly List<MotorBinding> motorBindings = new List<MotorBinding>();
    // Toggle handlers are kept so OnDisable can unsubscribe the exact delegates it added.
    private readonly List<KeyValuePair<InputAction, Action<InputAction.CallbackContext>>> toggleHandlers =
        new List<KeyValuePair<InputAction, Action<InputAction.CallbackContext>>>();
    private bool bindingsBuilt;

    void Awake()
    {
        if (mechanisms == null) mechanisms = GetComponent<RobotMechanisms>();
        BuildBindings();
    }

    private void BuildBindings()
    {
        if (bindingsBuilt) return;
        bindingsBuilt = true;
        if (mechanisms == null || buttonActions == null) return;

        ButtonMap map = ControllerMapSettings.Load(mechanisms.robotId);
        var motorLookup = new Dictionary<MotorActuator, MotorBinding>();

        foreach (ButtonAssignment assignment in map.assignments)
        {
            if (assignment == null) continue;
            if (!Enum.TryParse(assignment.button, out ControllerButton button)) continue;
            int index = (int)button;
            if (index < 0 || index >= buttonActions.Length) continue;
            InputActionReference reference = buttonActions[index];
            if (reference == null || reference.action == null) continue;

            RobotMechanisms.Mechanism mechanism = mechanisms.Find(assignment.mechanismId);
            if (mechanism == null) continue; // stale assignment from an older robot revision

            if (mechanism.type == RobotMechanisms.TypeMotor && mechanism.motor != null)
            {
                if (!motorLookup.TryGetValue(mechanism.motor, out MotorBinding binding))
                {
                    binding = new MotorBinding { motor = mechanism.motor };
                    motorLookup.Add(mechanism.motor, binding);
                    motorBindings.Add(binding);
                }
                if (assignment.mode == ControllerMapSettings.ModeReverse) binding.reverse.Add(reference.action);
                else binding.forward.Add(reference.action);
            }
            else if (mechanism.type == RobotMechanisms.TypePneumatic && mechanism.pneumatic != null)
            {
                PneumaticActuator pneumatic = mechanism.pneumatic; // capture for the closure
                Action<InputAction.CallbackContext> handler = _ => pneumatic.Toggle();
                toggleHandlers.Add(
                    new KeyValuePair<InputAction, Action<InputAction.CallbackContext>>(reference.action, handler));
            }
        }
    }

    void OnEnable()
    {
        BuildBindings(); // no-op after Awake; keeps enable-before-awake ordering safe
        if (buttonActions != null)
        {
            foreach (InputActionReference reference in buttonActions)
            {
                if (reference != null && reference.action != null) reference.action.Enable();
            }
        }
        foreach (KeyValuePair<InputAction, Action<InputAction.CallbackContext>> pair in toggleHandlers)
            pair.Key.performed += pair.Value;
    }

    void OnDisable()
    {
        foreach (KeyValuePair<InputAction, Action<InputAction.CallbackContext>> pair in toggleHandlers)
            pair.Key.performed -= pair.Value;
        if (buttonActions != null)
        {
            foreach (InputActionReference reference in buttonActions)
            {
                if (reference != null && reference.action != null) reference.action.Disable();
            }
        }
        foreach (MotorBinding binding in motorBindings)
        {
            if (binding.motor != null) binding.motor.SetInput(0f);
        }
    }

    void Update()
    {
        foreach (MotorBinding binding in motorBindings)
        {
            if (binding.motor == null) continue;
            float input = 0f;
            foreach (InputAction action in binding.forward)
                if (action.IsPressed()) input += 1f;
            foreach (InputAction action in binding.reverse)
                if (action.IsPressed()) input -= 1f;
            input = Mathf.Clamp(input, -1f, 1f);
            // Drive targets persist on the joint, so only write on change.
            if (!Mathf.Approximately(binding.motor.CurrentInput, input))
                binding.motor.SetInput(input);
        }
    }
}
