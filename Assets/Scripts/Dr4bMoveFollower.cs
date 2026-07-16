using UnityEngine;

// A DR4B part that TRANSLATES with the lift while keeping its rest orientation (stays level for free):
// the moving C-channel, sprockets, connect pivots, "rises up" parts, scoring, and the stack carriage.
//
// The movement is ADDITIVE over two stages:
//   Stage 1 — up + forward. Everything that moves follows this.
//   Stage 2 — the opposing / crane reach, ADDED ON TOP for the second-stage subset + scoring.
// A part sets the stage flags it belongs to; its offset is the sum. So a first-stage-only part moves
// Stage1, and a second-stage part moves Stage1 + Stage2 (the most). Exactly the additive model.
//
// Optionally spins about the hinge axis (for a sprocket that visibly turns: +1 driven, -1 the counter-
// rotating second sprocket). At rest (angle 0) it sits exactly at its authored pose.
public class Dr4bMoveFollower : Dr4bFollower
{
    [Tooltip("Move with the first stage (up + forward). True for anything that rises.")]
    public bool followsStage1 = true;
    [Tooltip("ALSO move with the second stage (the opposing/crane reach, added on top). True for the second-stage subset + scoring.")]
    public bool followsStage2 = false;
    [Tooltip("Optional: spin about the hinge axis by armAngle x this, for a sprocket that visibly turns (+1 driven, -1 second). 0 = no spin.")]
    public float spinRatio = 0f;

    public override void Apply(Dr4bLift lift)
    {
        if (lift == null) return;
        if (!captured) CaptureRest(lift.Chassis);
        if (!captured) return;

        Vector3 move = Vector3.zero;
        if (followsStage1) move += lift.Stage1Move;
        if (followsStage2) move += lift.Stage2Move;

        Quaternion spin = Mathf.Abs(spinRatio) > 1e-6f
            ? Quaternion.AngleAxis(lift.ArmAngleDeg * spinRatio, lift.AxisWorld)
            : Quaternion.identity;
        transform.SetPositionAndRotation(RestPosW + move, spin * RestRotW);
    }
}
