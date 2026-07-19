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
// A mechanism can instead be switched to one-button control (ControllerMapSettings styles): a
// motor's button then LATCHES — press to spin, press again to stop — and a piston's two buttons
// become one-that-extends and one-that-retracts. The latch is a router-level idea: the actuator
// components are unchanged, this just holds the input between presses. Latched state contributes
// to the same sum as held buttons, so a latched motor can still be overridden by holding its
// opposing button, and the latch always starts at 0 on enable (never spinning at spawn).
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
        // -1/0/+1 held by one-button (latching) assignments between presses.
        public int latch;
    }

    private readonly List<MotorBinding> motorBindings = new List<MotorBinding>();
    // Press-edge handlers (pneumatic fire/extend/retract, motor latch) are kept so OnDisable can
    // unsubscribe the exact delegates it added. EVERY edge handler must land in this one list or it
    // never gets unsubscribed.
    private readonly List<KeyValuePair<InputAction, Action<InputAction.CallbackContext>>> pressHandlers =
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
        // Latch the flag only once there's actually something to bind: setting it above the guard
        // would permanently empty the bindings if this ever ran before `mechanisms` resolved.
        if (mechanisms == null || buttonActions == null) return;
        bindingsBuilt = true;

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
                switch (assignment.mode)
                {
                    case ControllerMapSettings.ModeReverse:
                        binding.reverse.Add(reference.action);
                        break;
                    // One-button control: pressing flips the latch on, pressing again flips it off;
                    // pressing the opposite direction's button switches direction outright rather
                    // than making the player stop first.
                    case ControllerMapSettings.ModeToggle:
                        AddPressHandler(reference.action, () => binding.latch = binding.latch == 1 ? 0 : 1);
                        break;
                    case ControllerMapSettings.ModeToggleReverse:
                        AddPressHandler(reference.action, () => binding.latch = binding.latch == -1 ? 0 : -1);
                        break;
                    default: // ModeForward, and anything unrecognized
                        binding.forward.Add(reference.action);
                        break;
                }
            }
            else if (mechanism.type == RobotMechanisms.TypePneumatic && mechanism.pneumatic != null)
            {
                PneumaticActuator pneumatic = mechanism.pneumatic; // capture for the closure
                switch (assignment.mode)
                {
                    // Two-button control: each button drives the piston to ITS end and leaves it
                    // there, so the state only changes when the opposite button is pressed.
                    case ControllerMapSettings.ModeExtend:
                        AddPressHandler(reference.action, pneumatic.Extend);
                        break;
                    case ControllerMapSettings.ModeRetract:
                        AddPressHandler(reference.action, pneumatic.Retract);
                        break;
                    default: // ModeToggle, and anything unrecognized
                        AddPressHandler(reference.action, pneumatic.Toggle);
                        break;
                }
            }
        }
    }

    // Queues a press-edge action against a button. Wrapping here (rather than at each call site)
    // keeps every handler in the one list OnEnable/OnDisable iterate.
    private void AddPressHandler(InputAction action, Action onPress)
    {
        Action<InputAction.CallbackContext> handler = _ => onPress();
        pressHandlers.Add(
            new KeyValuePair<InputAction, Action<InputAction.CallbackContext>>(action, handler));
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
        foreach (KeyValuePair<InputAction, Action<InputAction.CallbackContext>> pair in pressHandlers)
            pair.Key.performed += pair.Value;
        // Never resume a latched motor across a disable — the robot must spawn with nothing running.
        foreach (MotorBinding binding in motorBindings) binding.latch = 0;
    }

    void OnDisable()
    {
        foreach (KeyValuePair<InputAction, Action<InputAction.CallbackContext>> pair in pressHandlers)
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
            binding.latch = 0;
            if (binding.motor != null) binding.motor.SetInput(0f);
        }
    }

    void Update()
    {
        foreach (MotorBinding binding in motorBindings)
        {
            if (binding.motor == null) continue;
            // Latched (one-button) state is just another contributor to the sum, so holding the
            // opposing button still cancels a latched motor the way two opposing buttons should.
            float input = binding.latch;
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
