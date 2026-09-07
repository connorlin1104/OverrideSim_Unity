using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Headless checks for the drivetrain's feel math — DrivetrainTuning's motor model and
// RobotMotorController's input shaping, mixing and authority decisions.
//
// Worth proving without a Play session because the failure mode is silent: a wrong damping, a mix
// that quietly throws away the turn differential, or a slew that isn't timestep-independent
// doesn't throw — it just makes the robot drive slightly wrong on some machines, which is exactly
// the kind of thing that gets reported as "feels off on my laptop" and never reproduces.
//
// Touches no PlayerPrefs, so unlike the binding validators it needs no snapshot/restore.
//
// Usage: Tools > RoboSim > Validate > Validate Drive Feel, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod DriveFeelValidation.RunBatchValidate
// which exits nonzero on the first failed check.
public static class DriveFeelValidation
{
    // A FIXED reference configuration, not a live reading — deliberately, because everything the
    // "shipped tune" check asserts is hand-computed from these numbers, and re-reading them from a
    // prefab would turn the check into the formula agreeing with itself.
    //
    // It is the 654V_v3 as it was when the drivetrain was tuned. If the robots are re-massed (see
    // RobotBalanceWindow) these stop describing any real robot, and that is fine: the per-prefab
    // assertions in ShippedPrefabs are what track the actual fleet.
    private const float Mass = 30f;      // root 24 + 6 wheel links x 1, the pre-rebalance masses
    private const float Radius = 0.37f;  // world units (1 unit = 100 mm)
    private const int Wheels = 6;
    private const float Rpm = 240f;
    private const float Mu = 0.8f;
    private const float G = 98.1f;

    [MenuItem("Tools/RoboSim/Validate/Validate Drive Feel", false, 12)]
    private static void RunInteractive() => ValidationUtil.RunInteractive("Validate Drive Feel", Run);

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Validate Drive Feel", Run);

    private static string Run()
    {
        int checks = 0;
        checks += ShapeEndpoints();
        checks += ShapeCurve();
        checks += SlewRates();
        checks += SlewReversalTiming();
        checks += SlewTimestepInvariance();
        checks += MixPreservesTurn();
        checks += PivotBlend();
        checks += AuthorityRule();
        checks += ParkHandoff();
        checks += ShippedTune();
        checks += BrakeRollout();
        checks += TuningInvariants();
        checks += ScaleInvariance();
        checks += DegenerateInputs();
        checks += ShippedPrefabs();
        return $"Validate Drive Feel: PASSED ({checks} checks).";
    }

    // --- Input shaping -------------------------------------------------------------------------

    // The identities that make a deadzone invisible to the driver: centre is dead, the far edge of
    // the deadzone is still zero, and full stick still reaches full output. The last one is the
    // one that silently breaks — without the rescale an 0.08 deadzone caps the robot at 92% speed
    // forever, which nobody notices until they race someone.
    private static int ShapeEndpoints()
    {
        int checks = 0;
        foreach (float dz in new[] { 0f, 0.08f, 0.25f })
        {
            foreach (float expo in new[] { 0f, 0.35f, 1f })
            {
                ValidationUtil.Near(RobotMotorController.Shape(0f, dz, expo), 0f, 1e-5f, $"centre must be dead (dz {dz}, expo {expo})");
                ValidationUtil.Near(RobotMotorController.Shape(dz, dz, expo), 0f, 1e-5f, $"the deadzone edge must still be zero (dz {dz})");
                ValidationUtil.Near(RobotMotorController.Shape(1f, dz, expo), 1f, 1e-5f, $"full stick must reach full output (dz {dz}, expo {expo})");
                ValidationUtil.Near(RobotMotorController.Shape(-1f, dz, expo), -1f, 1e-5f, $"full reverse must reach full output (dz {dz})");

                // Odd symmetry: pushing left must mirror pushing right, or the robot pulls to one side.
                for (float v = 0.05f; v <= 1f; v += 0.05f)
                {
                    ValidationUtil.Near(RobotMotorController.Shape(-v, dz, expo), -RobotMotorController.Shape(v, dz, expo),
                        1e-5f, $"Shape must be odd-symmetric at {v} (dz {dz}, expo {expo})");
                }
                checks += 5;
            }
        }
        return checks;
    }

    // Monotonic (more stick is never less output), and two hand-picked points that pin the two
    // halves of the curve down.
    private static int ShapeCurve()
    {
        foreach (float dz in new[] { 0f, 0.08f, 0.25f })
        {
            foreach (float expo in new[] { 0f, 0.35f, 1f })
            {
                float previous = -1f;
                for (float v = 0f; v <= 1.0001f; v += 0.01f)
                {
                    float shaped = RobotMotorController.Shape(v, dz, expo);
                    ValidationUtil.Assert(shaped >= previous - 1e-5f,
                        $"Shape must never decrease as the stick moves out (dz {dz}, expo {expo}, at {v})");
                    ValidationUtil.Assert(shaped <= 1f + 1e-5f, $"Shape must never exceed 1 (dz {dz}, expo {expo}, at {v})");
                    previous = shaped;
                }
            }
        }

        // Halfway past the deadzone is half output: with a 0.5 deadzone and no expo, 0.75 sits
        // exactly midway between the dead edge and full, so it must read 0.5.
        ValidationUtil.Near(RobotMotorController.Shape(0.75f, 0.5f, 0f), 0.5f, 1e-5f,
            "with expo off, travel past the deadzone must be linear");

        // Fully cubic: half stick is an eighth of the output.
        ValidationUtil.Near(RobotMotorController.Shape(0.5f, 0f, 1f), 0.125f, 1e-5f,
            "expo 1 must be a pure cube");

        // ...and expo must only soften the middle, never the ends (checked at 1 above) or the sign.
        ValidationUtil.Assert(RobotMotorController.Shape(0.5f, 0f, 1f) < RobotMotorController.Shape(0.5f, 0f, 0f),
            "expo must give FINER control near centre, not coarser");
        return 12;
    }

    // --- Slew --------------------------------------------------------------------------------

    // Growing away from zero uses the rise rate; shrinking back toward it uses the (faster) fall
    // rate. Backwards rates would make the robot lazy to start and lazy to stop, which is the
    // worst of both.
    private static int SlewRates()
    {
        ValidationUtil.Near(RobotMotorController.Slew(0f, 1f, 4f, 8f, 0.1f), 0.4f, 1e-5f,
            "rising from rest must use the rise rate");
        ValidationUtil.Near(RobotMotorController.Slew(1f, 0.5f, 4f, 8f, 0.01f), 0.92f, 1e-5f,
            "easing off must use the fall rate");
        ValidationUtil.Near(RobotMotorController.Slew(-1f, 0f, 4f, 8f, 0.01f), -0.92f, 1e-5f,
            "releasing from reverse must use the fall rate too");

        // Never overshoot, however big the step.
        ValidationUtil.Near(RobotMotorController.Slew(0f, 1f, 100f, 100f, 1f), 1f, 1e-5f, "a huge step must land ON the target");
        ValidationUtil.Near(RobotMotorController.Slew(0.3f, 0.3f, 4f, 8f, 0.1f), 0.3f, 1e-5f, "already there must stay there");

        // A zero rate must not divide by itself into a NaN.
        float stuck = RobotMotorController.Slew(0.5f, -0.5f, 0f, 0f, 0.1f);
        ValidationUtil.Assert(!float.IsNaN(stuck) && !float.IsInfinity(stuck), "zero rates must not produce NaN");
        return 6;
    }

    // A full reversal has to spend part of the step falling and part rising, so it takes exactly
    // 1/fall + 1/rise seconds. Getting this wrong is invisible at 100 Hz and obvious at 20 fps,
    // which is precisely the machine the complaint came from.
    private static int SlewReversalTiming()
    {
        const float rise = 4f, fall = 8f, dt = 0.005f;
        // 1 -> 0 at 8/s is 0.125 s (25 steps); 0 -> -1 at 4/s is 0.25 s (50 steps).
        const int expectedSteps = 75;

        float justShort = Integrate(1f, -1f, rise, fall, dt, expectedSteps - 1);
        ValidationUtil.Assert(justShort > -1f + 1e-4f, $"a reversal must not finish early (was {justShort} after {expectedSteps - 1} steps)");

        float onTime = Integrate(1f, -1f, rise, fall, dt, expectedSteps);
        ValidationUtil.Near(onTime, -1f, 1e-4f, $"a reversal must take exactly 1/fall + 1/rise seconds ({expectedSteps} steps)");

        // It must actually pass through zero rather than jumping the sign.
        float mid = Integrate(1f, -1f, rise, fall, dt, 25);
        ValidationUtil.Near(mid, 0f, 1e-5f, "the falling half of a reversal must land on zero");
        return 3;
    }

    // Stepping the same 0.2 s at 100 Hz and at 10 Hz must land in the same place. If it doesn't,
    // the drivetrain literally feels different at different frame rates.
    private static int SlewTimestepInvariance()
    {
        var cases = new[]
        {
            new { from = 1f, to = -1f },   // reversal: the case with two rates in one step
            new { from = 0f, to = 1f },    // pure rise
            new { from = 1f, to = 0f },    // pure fall
            new { from = -0.4f, to = 0.9f },
        };
        foreach (var c in cases)
        {
            float fine = Integrate(c.from, c.to, 4f, 8f, 0.002f, 100);  // 0.2 s in 100 steps
            float coarse = Integrate(c.from, c.to, 4f, 8f, 0.05f, 4);   // 0.2 s in 4 steps
            ValidationUtil.Near(coarse, fine, 1e-4f,
                $"Slew must be timestep-independent ({c.from} -> {c.to}: 100 Hz gave {fine}, 20 Hz gave {coarse})");
        }
        return cases.Length;
    }

    private static float Integrate(float start, float target, float rise, float fall, float dt, int steps)
    {
        float v = start;
        for (int i = 0; i < steps; i++) v = RobotMotorController.Slew(v, target, rise, fall, dt);
        return v;
    }

    // --- Mixing ------------------------------------------------------------------------------

    // The proportional arcade mix: when throttle + turn ask for more than a wheel has, BOTH give way
    // together and neither is spent to pay for the other.
    //
    // WHAT THIS CHECK USED TO PIN, and why that had to go. Under the previous turn-priority mix the
    // property asserted here was "the commanded differential survives at every throttle" — the
    // throttle was shaved to make room and the turn always got what it asked for. That is the same
    // fact as "at full throttle with a full turn stick the inner side is commanded to a DEAD STOP",
    // which the authority rule then enforces at stall torque because a held stick is full authority.
    // Connor, 2026-09-06: "the rotation overpowers the forward and just turns. The forward should be
    // more overpowering." So the differential is deliberately no longer preserved through the corner.
    //
    // What replaces it is a strictly stronger statement about the DRIVER'S input rather than about
    // one of its two components: the mix is a pure scaling. There is a single k for both sides, so
    // the steering-to-forward BALANCE the driver asked for is what survives, and only the overall
    // magnitude gives way. Turn priority fails this; so does clamp(throttle ± turn); so does
    // throttle priority. It is the one of the four that is a scaling.
    private static int MixPreservesTurn()
    {
        // Hand-computed pins. At full throttle + half turn both sides scale by 1/1.5.
        RobotMotorController.MixArcade(1f, 0.5f, out float l, out float r);
        ValidationUtil.Near(l, 1f, 1e-5f, "full throttle + half turn: outer side must hold full speed");
        ValidationUtil.Near(r, 1f / 3f, 1e-5f, "full throttle + half turn: inner side must keep turning, not stop dead");

        RobotMotorController.MixArcade(-1f, 0.5f, out l, out r);
        ValidationUtil.Near(l, -1f / 3f, 1e-5f, "full reverse + half turn: outer side must keep turning");
        ValidationUtil.Near(r, -1f, 1e-5f, "full reverse + half turn: inner side must hold full speed");

        RobotMotorController.MixArcade(1f, 0f, out l, out r);
        ValidationUtil.Near(l, 1f, 1e-5f, "straight ahead must be untouched (left)");
        ValidationUtil.Near(r, 1f, 1e-5f, "straight ahead must be untouched (right)");

        RobotMotorController.MixArcade(0f, 0.75f, out l, out r);
        ValidationUtil.Near(l, 0.75f, 1e-5f, "a point turn must pass straight through (left)");
        ValidationUtil.Near(r, -0.75f, 1e-5f, "a point turn must pass straight through (right)");

        int checks = 8;

        // The invariants, over the whole input square.
        for (int ti = -4; ti <= 4; ti++)
        {
            for (int ui = -4; ui <= 4; ui++)
            {
                float th = ti * 0.25f;
                float tu = ui * 0.25f;
                RobotMotorController.MixArcade(th, tu, out float left, out float right);

                ValidationUtil.Assert(Mathf.Abs(left) <= 1f + 1e-5f && Mathf.Abs(right) <= 1f + 1e-5f,
                    $"the mix must stay inside ±1 (throttle {th}, turn {tu})");

                // THE property this mix exists for: ONE scale factor for both sides, so the driver's
                // balance of steering against forward is what survives — never one at the other's
                // expense. Derived from the overflow, then required to explain BOTH outputs.
                float k = 1f / Mathf.Max(1f, Mathf.Max(Mathf.Abs(th + tu), Mathf.Abs(th - tu)));
                ValidationUtil.Near(left + right, 2f * k * th, 1e-5f,
                    $"throttle must survive scaled by the same k as the turn (throttle {th}, turn {tu})");
                ValidationUtil.Near(left - right, 2f * k * tu, 1e-5f,
                    $"the differential must survive scaled by the same k as the throttle (throttle {th}, turn {tu})");

                // THE FAULT THIS MIX WAS WRITTEN TO REMOVE, stated exactly so it cannot come back
                // quietly. A side is commanded to a standstill exactly when the driver asked for one
                // — |throttle| == |turn|, which is a pivot with drive on it — and never as a side
                // effect of the mix making room. Under turn priority the inner side read zero at
                // EVERY stick position past the square (throttle 1 + turn 0.5 gave it too), and that
                // zero is what the authority rule then pinned at stall torque. A pure scaling cannot
                // introduce a zero, which is the whole reason this mix is a scaling; both the old mix
                // and a throttle-priority one fail this line.
                ValidationUtil.Assert((Mathf.Abs(right) < 1e-6f) == Mathf.Approximately(th, tu),
                    $"the right side may only be commanded to a standstill when the driver asked for " +
                    $"one (throttle {th}, turn {tu}, right {right})");
                ValidationUtil.Assert((Mathf.Abs(left) < 1e-6f) == Mathf.Approximately(th, -tu),
                    $"...and the left side likewise (throttle {th}, turn {tu}, left {left})");

                // Odd symmetry, or the robot turns differently left vs right.
                RobotMotorController.MixArcade(-th, -tu, out float ml, out float mr);
                ValidationUtil.Near(ml, -left, 1e-5f, $"mix must be odd-symmetric (throttle {th}, turn {tu})");
                ValidationUtil.Near(mr, -right, 1e-5f, $"mix must be odd-symmetric (throttle {th}, turn {tu})");

                // And nothing gives way until it must: inside the square, k is exactly 1.
                if (Mathf.Abs(th) + Mathf.Abs(tu) <= 1f + 1e-5f)
                {
                    ValidationUtil.Near(left + right, 2f * th, 1e-5f,
                        $"throttle must pass through untouched when the mix fits (throttle {th}, turn {tu})");
                    ValidationUtil.Near(left - right, 2f * tu, 1e-5f,
                        $"the turn must pass through untouched when the mix fits (throttle {th}, turn {tu})");
                    checks += 2;
                }
                checks += 7;
            }
        }
        return checks;
    }

    // The pivot blend. Standing still the turn stick is worth pivotTurnRate of wheel speed, at full
    // throttle turnRate, linearly between. Both ends moved on 2026-09-06 (1.0/0.5 -> 0.65/0.35) and
    // the mix underneath them changed with them, so what this pins moved too: it used to assert that
    // the pair reproduced a plain clamped arcade drive at every throttle, which was true and was also
    // the same fact as "the inner side is commanded to a dead stop at full throttle". See MixArcade.
    // What is pinned now is the thing the blend is actually for — one continuous rate across the
    // whole throttle range, worth more standing still than at speed, with the arc keeping most of its
    // forward speed.
    private static int PivotBlend()
    {
        const float pivot = RobotMotorController.DefaultPivotTurnRate;   // 0.65
        const float moving = RobotMotorController.DefaultTurnRate;       // 0.35
        int checks = 0;

        ValidationUtil.Near(RobotMotorController.TurnRateFor(0f, pivot, moving), pivot, 1e-6f,
            "standing still, the pivot rate applies in full");
        ValidationUtil.Near(RobotMotorController.TurnRateFor(1f, pivot, moving), moving, 1e-6f,
            "at full throttle the moving rate applies in full");
        ValidationUtil.Near(RobotMotorController.TurnRateFor(-1f, pivot, moving), moving, 1e-6f,
            "full reverse is full throttle too — the blend reads |throttle|");
        ValidationUtil.Near(RobotMotorController.TurnRateFor(0.5f, pivot, moving),
            0.5f * (pivot + moving), 1e-6f, "half throttle sits halfway between the two rates");
        ValidationUtil.Near(RobotMotorController.TurnRateFor(2f, pivot, moving), moving, 1e-6f,
            "a throttle past 1 lands on the moving rate, never beyond it");
        ValidationUtil.Assert(pivot > moving,
            "a pivot must be worth more stick than a turn at speed, or the blend has nothing to blend");
        checks += 6;

        // One continuous rate across the range: monotone, no step, and never outside the two ends.
        float previous = float.PositiveInfinity;
        for (int ti = 0; ti <= 8; ti++)
        {
            float t = ti * 0.125f;
            float rate = RobotMotorController.TurnRateFor(t, pivot, moving);
            ValidationUtil.Assert(rate <= previous + 1e-6f,
                $"the turn rate must fall as the throttle rises, with no step (throttle {t})");
            ValidationUtil.Assert(rate >= moving - 1e-6f && rate <= pivot + 1e-6f,
                $"the turn rate must stay between the two ends (throttle {t})");
            previous = rate;
            checks += 2;
        }

        // AND THE ARC KEEPS ITS FORWARD SPEED. This is the number Connor chose on 2026-09-06 against
        // "the rotation overpowers the forward and just turns": at full throttle with a full turn
        // stick the robot holds about three quarters of its speed through the corner. The old
        // turn-priority mix at turnRate 0.5 held exactly half and stopped the inner wheel dead, so
        // the literal below is the regression, named.
        RobotMotorController.MixArcade(1f, RobotMotorController.TurnRateFor(1f, pivot, moving),
            out float al, out float ar);
        ValidationUtil.Near(0.5f * (al + ar), 0.741f, 1e-3f,
            "a full-throttle full-stick arc must keep about three quarters of its forward speed");
        ValidationUtil.Assert(0.5f * (al + ar) > 0.5f + 1e-3f,
            "...which is more than the half the turn-priority mix left, or nothing has changed");
        ValidationUtil.Assert(Mathf.Abs(ar) > 1e-3f,
            "...and the inner side must still be turning, not stopped dead");
        checks += 3;

        // A pivot is a pivot: from rest the sides are equal and opposite, and worth more than the
        // same stick is at speed by exactly the ratio of the two rates.
        RobotMotorController.MixArcade(0f, RobotMotorController.TurnRateFor(0f, pivot, moving),
            out float pl, out float pr);
        ValidationUtil.Near(pl, pivot, 1e-6f, "a full-stick pivot from rest runs the left side at the pivot rate");
        ValidationUtil.Near(pr, -pivot, 1e-6f, "...and the right side the same, the other way");
        RobotMotorController.MixArcade(0f, moving, out float ol, out float orr);   // the same stick at speed
        ValidationUtil.Near(pl - pr, (pivot / moving) * (ol - orr), 1e-5f,
            "the pivot differential must be pivotTurnRate/turnRate times what the same stick gets at speed");
        checks += 3;

        // ...AND THE DRIVE ACTUALLY USES IT. Everything above is arithmetic on a static method, which
        // stays green if someone reverts either MixArcade call to the bare turnRate — the wiring is
        // the part that regresses silently. A controller with no wheels still runs the whole command
        // path (the brake early-returns on an empty robot), so this drives the REAL ApplyStep,
        // through the slew, and reads back what the mix commanded.
        checks += DriveAppliesTheBlend();

        return checks;
    }

    private static int DriveAppliesTheBlend()
    {
        // Reverse Drive is a 180-degree control-frame flip, and it is a PlayerPrefs value this
        // machine may well have set. Read it to orient the expectation — the claim under test is the
        // blend, not the flip, and pretending the flag cannot be on would make this fail on a device
        // where a driver had turned it on.
        float sign = ReverseDriveSettings.Reversed ? -1f : 1f;
        const float pivot = RobotMotorController.DefaultPivotTurnRate;
        const float moving = RobotMotorController.DefaultTurnRate;

        var go = new GameObject("PivotBlendRig");
        try
        {
            RobotMotorController motor = go.AddComponent<RobotMotorController>();

            // Long enough for the turn slew (3/s) to reach full stick, held so the command settles.
            for (int i = 0; i < 100; i++) { motor.SetManualInput(0f, 1f); motor.ApplyStep(0.01f); }
            ValidationUtil.Near(motor.LeftCommand, sign * pivot, 1e-3f,
                "a pivot from rest must run the LEFT side at the pivot rate — the drive is not " +
                "applying it, whatever TurnRateFor computes");
            ValidationUtil.Near(motor.RightCommand, sign * -pivot, 1e-3f,
                "...and the RIGHT side the same, the other way");

            // Full throttle: back to the moving rate, through the proportional mix.
            RobotMotorController.MixArcade(1f, moving, out float el, out float er);
            for (int i = 0; i < 100; i++) { motor.SetManualInput(1f, 1f); motor.ApplyStep(0.01f); }
            ValidationUtil.Near(motor.LeftCommand, sign * el, 1e-3f,
                "at full throttle the outer side must hold full speed");
            ValidationUtil.Near(motor.RightCommand, sign * er, 1e-3f,
                "...and the inner side must keep turning at the mix's scaled command, not stop dead");
            return 4;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // THE AUTHORITY RULE: an accelerated wheel gets stall torque; a back-driven wheel gets a limit
    // that runs from the coast torque at centre stick to stall torque at full stick, on how far
    // EITHER stick is thrown; below the moving gate every wheel parks at stall. Symbolic torques
    // (10 and 100) rather than a real tune, on purpose — the point is which end of the ramp is
    // selected and how it interpolates, and feeding it Shipped() would be the formula agreeing
    // with itself.
    private static int AuthorityRule()
    {
        // A stand-in gate, not the shipped one — this check is about WHICH SIDE of a gate each
        // case lands on, and ParkHandoff is what pins where the gate actually goes.
        const float gate = 216f, full = 1440f, brake = 10f, stall = 100f;
        // chassisParked defaults TRUE so every case below reads as it did before the chassis term
        // existed; the cases that turn it off are grouped at the end, where the term is the subject.
        float At(float command, float spin, float stickThrow, bool chassisParked = true) =>
            RobotMotorController.DriveForceLimit(command, spin, gate, stickThrow, brake, stall, chassisParked);

        // THE one that must not move: centre stick against a spinning wheel is the brake pedal, and
        // every roll-out distance in BrakeRollout assumes it pulls exactly the coast torque.
        ValidationUtil.Near(At(0f, full, 0f), brake, 1e-4f, "released at speed must pull exactly the coast torque");
        ValidationUtil.Near(At(0f, -full, 0f), brake, 1e-4f, "...at reverse speed too");

        // ...and below the moving gate, on a robot that has also stopped, the wheel parks under full
        // authority instead.
        ValidationUtil.Near(At(0f, 10f, 0f), stall, 1e-4f, "a stopped wheel must hold, not chatter on the brake");

        // Acceleration is never limited, whatever the throw.
        ValidationUtil.Near(At(full, 720f, 0f), stall, 1e-4f, "accelerating must keep full authority");
        ValidationUtil.Near(At(full, 720f, 1f), stall, 1e-4f, "...at full stick too");
        ValidationUtil.Near(At(full, full, 1f), stall, 1e-4f, "a wheel exactly on its command is not back-driven");

        // A held stick that trails the spin — the inner side of a full-stick arc, a pivot entered
        // from speed — gets full authority to slow its wheels against the robot's momentum. This is
        // what the old steering exemption was for, without the exemption. (Capping this at the
        // tyre's grip was tried: the over-spun OUTER side of a turn is back-driven too, brakes just
        // as hard, and the moving turn cancels to zero — see DriveForceLimit.)
        ValidationUtil.Near(At(720f, full, 1f), stall, 1e-4f, "the inner side of a full-stick arc gets full authority");
        ValidationUtil.Near(At(-full, full, 1f), stall, 1e-4f, "a full-stick reversal gets full authority (the tyre is the ceiling)");

        // ...and it is a RAMP, so half a throw is half of it. Without this the rule could be a step
        // function and everything above would still pass, which is the on/off throttle this
        // drivetrain was retuned to get rid of.
        ValidationUtil.Near(At(720f, full, 0.5f), 55f, 1e-3f, "half a stick throw should be half way up the ramp");
        ValidationUtil.Near(At(-full, full, 0.25f), 32.5f, 1e-3f, "...and a quarter, a quarter of the way");

        // Continuous in the stick: just off centre is just off the brake. The old exemption had a
        // threshold at 0.05 a stick could sit either side of.
        ValidationUtil.Near(At(720f, full, 0.01f), brake + 0.9f, 1e-3f, "just off centre must be just off the brake — no cliff");

        // A sensitivity slider above 1 can shape a target past full stick; the ramp must saturate.
        ValidationUtil.Near(At(720f, full, 2f), stall, 1e-4f, "beyond full stick the ramp must clamp");

        // THE PARKING HOLD NEEDS THE ROBOT PARKED, NOT JUST THE WHEEL (2026-09-06). A wheel that has
        // reached zero while the chassis is still travelling is not a parked robot resisting a shove,
        // it is a locked wheel skidding — measured on 654V_v3, wheels pinned below a tenth of their
        // rail's spin with the chassis at 2.7-6.4 u/s. It stays on the brake ramp instead, which at
        // centre stick is the coast torque: a hold, not a lock.
        ValidationUtil.Near(At(0f, 10f, 0f, chassisParked: false), brake, 1e-4f,
            "a stopped wheel on a MOVING robot must coast, not lock — the hold is for a parked robot");
        ValidationUtil.Near(At(0f, 10f, 1f, chassisParked: false), stall, 1e-4f,
            "...but a held stick still commands it, so full authority is the driver's to ask for");

        // And the wheel gate still binds: a robot that has stopped does not park a wheel that has not.
        ValidationUtil.Near(At(0f, full, 0f), brake, 1e-4f,
            "a spinning wheel on a stopped robot is still on the brake — BOTH have to be below the gate");

        // The quadrant predicate itself, both ways round.
        ValidationUtil.Assert(RobotMotorController.BackDriven(0f, full), "commanded to a stop at speed is back-driven");
        ValidationUtil.Assert(RobotMotorController.BackDriven(-full, full), "commanded into reverse is back-driven");
        ValidationUtil.Assert(RobotMotorController.BackDriven(720f, full), "commanded slower is back-driven");
        ValidationUtil.Assert(!RobotMotorController.BackDriven(full, 720f), "commanded faster is not");
        return 19;
    }

    // The handoff from the brake to the parking hold. This is the check that "the end part just
    // comes to a sudden stop" (2026-08-19, reported on a 360 RPM robot) cannot come back.
    //
    // The parking hold is a PhysX velocity drive at target 0 with forceLimit raised from brakeTorque
    // to stallTorque, and an articulation drive is solved implicitly — it is a velocity CONSTRAINT
    // bounded by its force limit, not a spring pulling at damping * error. So it does not ease the
    // last of the speed away, it DELETES it, in one physics step, and the only question that matters
    // is how much speed is left when the swap happens. Traced on the 360 RPM robot at the old gate:
    // 0.771 u/s gone in one 10 ms step, a 0.76 g spike at the end of an otherwise flat 0.19 g stop.
    //
    // So the gate has to be one brake-step of speed, and this asserts exactly that: the deceleration
    // implied by deleting everything below the gate in a single step must be the same brakeG the
    // rest of the stop already runs at. Written as a deceleration rather than as the gate formula
    // repeated back, so it is a claim about what the driver feels and not the code agreeing with
    // itself.
    private static int ParkHandoff()
    {
        const float Dt = 0.01f; // the project's 100 Hz step — ValidationUtil.StepSeconds
        DrivetrainTuning.Result t = Shipped();
        float fullStick = Rpm * 6f;
        float gate = RobotMotorController.ParkGateDegPerSec(fullStick, t.brakeG, t.topSpeed, G, Dt);

        // The whole claim, in one line: the last step of the stop decelerates at brakeG like every
        // step before it. Below the gate the wheel is stopped outright, so the deceleration that
        // costs is (gate speed) / dt — and that has to come out at the coast figure.
        float gateSpeed = t.topSpeed * gate / fullStick;
        ValidationUtil.Near(gateSpeed / Dt / G, t.brakeG, 0.005f,
            $"stopping the last {gateSpeed:0.000} u/s outright in one {Dt * 1000:0.} ms step is " +
            $"{gateSpeed / Dt / G:0.00} g against a coast of {t.brakeG:0.00} g — the parking hold is " +
            "taking a bite the driver can feel, which IS the sudden stop");

        // Same claim at every gearing. The old gate was a fraction of free speed, so the SPEED it
        // dumped scaled with the robot: invisible at 240 RPM, a jolt at 360. This one is a fixed
        // speed by construction, and the loop is what proves the RPM cancels.
        float reference = 0f;
        foreach (float rpm in new[] { 200f, 240f, 300f, 360f, 600f })
        {
            DrivetrainTuning.Result r = DrivetrainTuning.Compute(Mass, Radius, Wheels, rpm, Mu, G,
                DrivetrainTuning.DefaultDriveForceTractionMultiple);
            float gateAt = RobotMotorController.ParkGateDegPerSec(rpm * 6f, r.brakeG, r.topSpeed, G, Dt);
            float speedAt = r.topSpeed * gateAt / (rpm * 6f);
            if (reference == 0f) reference = speedAt;
            ValidationUtil.Near(speedAt, reference, 1e-3f,
                $"a {rpm:0.} RPM robot must hand over to the parking hold at the same {reference:0.000} u/s " +
                "a 200 RPM one does — a gate that scales with gearing is what made the fast robot jolt");
        }

        // The gate is a real speed inside the range, not a degenerate end. Zero would delete the
        // parking hold (a stopped robot left chattering on the brake); free speed would delete the
        // brake (every stop a one-step skid).
        ValidationUtil.Assert(gate > 0f && gate < fullStick * 0.05f,
            $"the parking gate ({gate:0.#} deg/s) must be a slow crawl, not an end of the range or a " +
            $"meaningful fraction of the {fullStick:0.} deg/s free speed");

        // It has to move with the timestep, because it is defined per step: at half the rate the
        // brake covers twice the speed before the next step, so the gate doubles. A gate that
        // ignored dt would put the cliff back on anyone not running 100 Hz.
        ValidationUtil.Near(
            RobotMotorController.ParkGateDegPerSec(fullStick, t.brakeG, t.topSpeed, G, Dt * 2f),
            gate * 2f, 0.01f,
            "the parking gate is one brake-step of speed, so halving the physics rate must double it");

        // Degenerate rigs must not divide by zero into a NaN force limit, which takes the whole
        // articulation with it.
        ValidationUtil.Near(RobotMotorController.ParkGateDegPerSec(fullStick, t.brakeG, 0f, G, Dt), 0f,
            1e-6f, "a robot with no top speed must get a zero gate, not a NaN");
        ValidationUtil.Near(RobotMotorController.ParkGateDegPerSec(fullStick, 1e6f, t.topSpeed, G, Dt),
            fullStick, 1e-3f, "an absurd brake must clamp the gate to free speed, not overshoot it");

        return 10;
    }

    // --- Tuning ------------------------------------------------------------------------------

    // The shipped 654V numbers, hand-computed from the constants at the top of this file. This is
    // the check that notices someone "just nudging" a default and quietly changing how every robot
    // drives.
    private static int ShippedTune()
    {
        DrivetrainTuning.Result t = Shipped();

        ValidationUtil.Near(t.tractionForce, 2354.4f, 1f, "traction budget should be mu*m*g = 0.8 * 30 * 98.1");
        ValidationUtil.Near(t.peakForce, 7063.2f, 2f, "peak drive force should be 3x the traction budget");
        ValidationUtil.Near(t.stallTorque, 435.56f, 0.1f, "per-wheel stall torque");
        ValidationUtil.Near(t.damping, 0.30247f, 0.0005f, "per-wheel velocity gain, in torque per deg/s");
        ValidationUtil.Near(t.topSpeed, 9.30f, 0.02f, "top speed should be ~0.93 m/s for a 2.75in omni at 240 RPM");
        ValidationUtil.Near(t.motorLimitedStick, 1f / 3f, 0.005f,
            "the first third of stick travel should be motor-limited, so fine control is real");

        // Braking: the all-omni default, 0.2 of the traction budget — 0.2*2354.4 = 471 over 6
        // wheels at r 0.37. With centre-stick as the brake pedal this is also the deceleration a
        // release commands, which is why it is the number the wheel type gets to change.
        ValidationUtil.Near(t.brakeTorque, 29.04f, 0.1f, "per-wheel braking-quadrant torque");
        ValidationUtil.Near(t.tractionG, 0.80f, 0.005f, "the tyres' grip is mu, so the friction cone is 0.8 g");
        ValidationUtil.Near(t.brakeG, 0.16f, 0.005f, "an all-omni robot should coast at 0.2 of the friction cone");

        // One traction limit's worth of torque at this wheel — 2354.4 over 6 wheels at r 0.37, a
        // third of stall — the diagnostic the probes print beside a force limit.
        ValidationUtil.Near(t.gripTorque, 145.19f, 0.1f, "per-wheel grip torque");
        ValidationUtil.Assert(t.gripTorque < t.stallTorque, "the grip sits below stall by the traction multiple");

        // THE invariant behind "a stop should feel progressive, not a skid": for a COAST the motor,
        // not the ground, has to be what limits it. Above the friction cone the tyres just slip, the
        // deceleration pins at mu*g however far the stick has moved, and every release costs the
        // same nothing. Below it the force builds with the command.
        ValidationUtil.Assert(t.brakeG < t.tractionG,
            $"coasting ({t.brakeG:0.00} g) must stay inside the friction cone ({t.tractionG:0.00} g) " +
            "or letting go is traction-limited and instantaneous again");

        // A wheel must be able to break traction: below mu*m*g*r/N it cannot spin its tyres at all,
        // and a full-throttle launch is meant to.
        float slipThreshold = t.tractionForce * Radius / Wheels;
        ValidationUtil.Assert(t.stallTorque > slipThreshold,
            $"per-wheel stall torque ({t.stallTorque:0.#}) must exceed the slip threshold " +
            $"({slipThreshold:0.#}) or the drivetrain seizes and the robot cannot turn");

        // ...but not so far above that the whole stick range is traction-saturated, which is the
        // "throttle is an on/off switch" complaint in numbers.
        ValidationUtil.Assert(t.motorLimitedStick > 0.15f,
            $"only the top {(1f - t.motorLimitedStick):P0} of stick travel may be traction-limited; " +
            "past that there is no fine control left");
        return 14;
    }

    // The brake, as the distance the driver feels. Asserted as ROLL-OUT DISTANCE, not as torque,
    // because distance is the thing a driver actually feels and the only form in which "the drift an
    // all-omni drive has" is a checkable claim. Constant deceleration is the right model: the brake
    // clamp binds from full speed all the way down to the moving gate (brakeTorque is 6.7% of stall
    // against a drive that reaches stall at free speed), so the wheel decelerates at brakeG for
    // essentially the whole stop.
    //
    // ONE number for every robot, traction pair or not: where a traction wheel differs is its
    // sideways grip, which lives in WheelTyreModel. The old second fraction (0.7, a firm 0.08 m stop)
    // went with the checkbox that selected it — see the retirement note in RoboSimSettings.
    private static int BrakeRollout()
    {
        DrivetrainTuning.Result omni = Shipped();

        // brakeG is exactly mu * fraction, which is what makes this predictable from the constants
        // rather than emergent. Pinned so nobody re-tunes the feel by accident.
        ValidationUtil.Near(omni.brakeG, 0.16f, 0.005f, "all-omni braking should be 0.2 of the 0.8 g cone");

        // The felt number for the reference 240 RPM robot: ~0.28 m of roll.
        float omniRollout = RolloutUnits(omni);
        ValidationUtil.Near(omniRollout, 2.754f, 0.02f, "an all-omni robot's roll-out from full speed");

        // The failure mode of tuning drift by feel: a robot that rolls for two thirds of a metre on
        // a 240 RPM drive has stopped being drifty and started ignoring the driver. The old
        // coast-on-release model died of exactly this.
        ValidationUtil.Assert(omniRollout < 6f,
            $"an all-omni robot rolls {omniRollout:0.0} units ({omniRollout * 0.1f:0.00} m) after the " +
            "sticks are released — past this it reads as 'the brake does nothing', not as drift");

        // The stop stays inside the friction cone, so the MOTOR is what limits it and the force
        // builds with the command. Above the cone the tyres just slip and every stop costs the
        // driver the same nothing.
        ValidationUtil.Assert(omni.brakeG < omni.tractionG,
            $"the stop must stay under the {omni.tractionG:0.00} g friction cone (got {omni.brakeG:0.00} g)");

        // A motor cannot brake harder than it can drive.
        ValidationUtil.Assert(omni.brakeTorque <= omni.stallTorque + 1e-4f,
            "the brake must not exceed stall torque");
        return 5;
    }

    // Distance to a standstill from top speed under a constant brakeG, in world units.
    private static float RolloutUnits(DrivetrainTuning.Result t) => RolloutUnits(t, t.brakeG);

    private static float RolloutUnits(DrivetrainTuning.Result t, float decelG)
    {
        float decel = decelG * G;
        return decel > 1e-6f ? t.topSpeed * t.topSpeed / (2f * decel) : float.PositiveInfinity;
    }

    // The two structural properties the model rests on, checked across a spread of robots rather
    // than just the 654V.
    private static int TuningInvariants()
    {
        int checks = 0;
        foreach (float rpm in new[] { 200f, 240f, 300f, 360f, 600f })
        {
            foreach (float multiple in new[] { 1.5f, 3f, 4.8f, 6f })
            {
                DrivetrainTuning.Result t = DrivetrainTuning.Compute(
                    Mass, Radius, Wheels, rpm, Mu, G, multiple);

                ValidationUtil.Near(t.peakForce / t.tractionForce, multiple, 1e-3f,
                    $"peak force must be exactly the requested multiple of traction (rpm {rpm}, multiple {multiple})");

                // damping * freeSpeed == stallTorque is what makes drive torque fall linearly to
                // zero at free speed — i.e. what makes this a motor curve instead of a switch.
                //
                // In DEGREES per second: a rotational ArticulationDrive differences targetVelocity
                // against the joint velocity in degrees, so damping is torque per (deg/s). Deriving
                // it from rad/s leaves it 57.3x too large and quietly restores the bang-bang drive.
                float freeSpeedDeg = rpm * 6f;
                ValidationUtil.Near(t.damping * freeSpeedDeg, t.stallTorque, t.stallTorque * 1e-3f,
                    $"torque must reach zero exactly at free speed, in the drive's own deg/s units (rpm {rpm})");

                // The behaviour all of that exists to produce: half stick pulls half as hard.
                // torque = damping * (target - current), so at a standstill with half the target
                // speed the drive must make exactly half its stall torque — and must NOT be
                // sitting on the force limit, which is what "the throttle is an on/off switch"
                // looks like in numbers.
                float halfStickTorque = t.damping * (freeSpeedDeg * 0.5f);
                ValidationUtil.Near(halfStickTorque, t.stallTorque * 0.5f, t.stallTorque * 1e-3f,
                    $"half stick must command half the torque (rpm {rpm}, multiple {multiple})");
                ValidationUtil.Assert(halfStickTorque < t.stallTorque,
                    $"half stick must not saturate the force limit (rpm {rpm}, multiple {multiple})");

                // Headroom above free speed, or a braking wheel gets clamped and reads as a harder
                // brake than the tune says.
                float freeSpeed = rpm * Mathf.PI * 2f / 60f;
                ValidationUtil.Assert(t.maxJointVelocity > freeSpeed,
                    $"maxJointVelocity must exceed free speed (rpm {rpm})");

                // Braking stays inside the friction cone at every gearing, so a stop is always
                // motor-limited rather than a skid — see ShippedTune for why that is the point.
                ValidationUtil.Assert(t.brakeG < t.tractionG + 1e-4f,
                    $"braking must stay inside the friction cone (rpm {rpm}, multiple {multiple}): " +
                    $"got {t.brakeG:0.000} g against {t.tractionG:0.000} g");
                ValidationUtil.Assert(t.brakeTorque <= t.stallTorque + 1e-4f,
                    $"a motor cannot brake harder than it can drive (rpm {rpm}, multiple {multiple})");
                checks += 8;
            }
        }
        return checks;
    }

    // The model is expressed in traction multiples precisely so a heavier or faster robot is
    // correct without a hand tune. These are the claims that buys.
    private static int ScaleInvariance()
    {
        DrivetrainTuning.Result baseline = Shipped();

        // Twice the mass: twice the grip, so twice the force, so the SAME acceleration curve —
        // and the same braking deceleration, because the brake is sized off the same grip.
        DrivetrainTuning.Result heavy = DrivetrainTuning.Compute(
            Mass * 2f, Radius, Wheels, Rpm, Mu, G,
            DrivetrainTuning.DefaultDriveForceTractionMultiple);
        ValidationUtil.Near(heavy.stallTorque, baseline.stallTorque * 2f, 0.05f, "twice the mass needs twice the torque");
        ValidationUtil.Near(heavy.secondsTo95, baseline.secondsTo95, 1e-3f, "a heavier robot must accelerate on the same curve");
        ValidationUtil.Near(heavy.topSpeed, baseline.topSpeed, 1e-3f, "mass must not change top speed");
        ValidationUtil.Near(heavy.brakeTorque, baseline.brakeTorque * 2f, 0.05f, "twice the mass is twice the brake torque");
        ValidationUtil.Near(heavy.brakeG, baseline.brakeG, 1e-3f, "...so it stops at the same deceleration");

        // Twice the gearing: twice the top speed, twice as long to reach it, same peak force —
        // and the same brake, because grip doesn't know what gear ratio is behind it.
        DrivetrainTuning.Result fast = DrivetrainTuning.Compute(
            Mass, Radius, Wheels, Rpm * 2f, Mu, G,
            DrivetrainTuning.DefaultDriveForceTractionMultiple);
        ValidationUtil.Near(fast.topSpeed, baseline.topSpeed * 2f, 1e-3f, "twice the RPM is twice the top speed");
        ValidationUtil.Near(fast.stallTorque, baseline.stallTorque, 1e-3f, "gearing must not change the traction-limited force");
        ValidationUtil.Near(fast.secondsTo95, baseline.secondsTo95 * 2f, 1e-3f, "a taller-geared robot takes proportionally longer");
        ValidationUtil.Near(fast.brakeTorque, baseline.brakeTorque, 1e-3f, "gearing must not change the brake");
        return 9;
    }

    // A half-rigged robot (no wheels wired yet, colliders not generated, a placeholder mass) must
    // still produce values PhysX can accept. A NaN forceLimit takes the whole articulation down.
    private static int DegenerateInputs()
    {
        var cases = new List<(string what, DrivetrainTuning.Result r)>
        {
            ("no wheels", DrivetrainTuning.Compute(Mass, Radius, 0, Rpm, Mu, G, 3f)),
            ("zero radius", DrivetrainTuning.Compute(Mass, 0f, Wheels, Rpm, Mu, G, 3f)),
            ("zero rpm", DrivetrainTuning.Compute(Mass, Radius, Wheels, 0f, Mu, G, 3f)),
            ("zero mass", DrivetrainTuning.Compute(0f, Radius, Wheels, Rpm, Mu, G, 3f)),
            ("frictionless", DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, 0f, G, 3f)),
            ("zero gravity", DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, Mu, 0f, 3f)),
            ("negative gravity", DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, Mu, -G, 3f)),
            ("zero multiple", DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, Mu, G, 0f)),
            ("zero brake fraction", DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, Mu, G, 3f, 0f)),
            ("everything zero", DrivetrainTuning.Compute(0f, 0f, 0, 0f, 0f, 0f, 0f, 0f)),
        };

        foreach ((string what, DrivetrainTuning.Result r) in cases)
        {
            ValidationUtil.Finite(r.stallTorque, $"{what}: stallTorque");
            ValidationUtil.Finite(r.damping, $"{what}: damping");
            ValidationUtil.Finite(r.brakeTorque, $"{what}: brakeTorque");
            ValidationUtil.Finite(r.gripTorque, $"{what}: gripTorque");
            ValidationUtil.Finite(r.maxJointVelocity, $"{what}: maxJointVelocity");
            ValidationUtil.Finite(r.secondsTo95, $"{what}: secondsTo95");
            ValidationUtil.Finite(r.brakeG, $"{what}: brakeG");
            ValidationUtil.Finite(r.tractionG, $"{what}: tractionG");
            ValidationUtil.Assert(r.maxJointVelocity > 0f, $"{what}: maxJointVelocity must stay positive or the wheels can't turn");
        }

        // Unity reports gravity as NEGATIVE y, and callers pass Physics.gravity.y straight in.
        // Sign errors here would silently invert the whole traction budget.
        // Same arguments as Shipped() apart from the sign, so this tests the sign and nothing else.
        DrivetrainTuning.Result down = DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, Mu, -G,
            DrivetrainTuning.DefaultDriveForceTractionMultiple);
        ValidationUtil.Near(down.stallTorque, Shipped().stallTorque, 1e-3f, "gravity's sign must not change the tune");
        return cases.Count * 9 + 1;
    }

    // --- Shipped prefabs -----------------------------------------------------------------------

    // Every robot must be on the derived tune, and its SERIALIZED drives must agree with it.
    //
    // The serialized half matters because RobotPhysicsValidation simulates in edit mode, where Awake
    // never runs — it reads xDrive straight off the prefab. If these drift apart, the smoke test
    // silently measures a drivetrain nobody ships.
    private static int ShippedPrefabs()
    {
        int checked_ = 0;
        if (!AssetDatabase.IsValidFolder(RoboSimPaths.RobotsFolder))
            throw new InvalidOperationException($"{RoboSimPaths.RobotsFolder} is missing — robot prefabs moved?");

        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            RobotMotorController motor = prefab != null ? prefab.GetComponent<RobotMotorController>() : null;
            if (motor == null) continue; // not a drivable robot

            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            ValidationUtil.Assert(motor.autoTuneDrive,
                $"'{name}' has Auto Tune Drive switched off, so it falls back to the serialized " +
                $"{motor.wheelStallTorque}/{motor.velocityDriveDamping} — the on/off-switch drivetrain. " +
                "Turn it back on unless this robot is deliberately hand-tuned.");

            var wheels = new List<ArticulationBody>();
            if (motor.leftWheels != null) foreach (ArticulationBody w in motor.leftWheels) if (w != null) wheels.Add(w);
            if (motor.rightWheels != null) foreach (ArticulationBody w in motor.rightWheels) if (w != null) wheels.Add(w);
            ValidationUtil.Assert(wheels.Count > 0, $"'{name}' has no wheels wired to its RobotMotorController");

            float radius = DrivetrainTuning.MeasureWheelRadius(wheels);
            DrivetrainTuning.Result expected = DrivetrainTuning.Compute(
                DrivetrainTuning.MeasureTotalMass(prefab.GetComponent<ArticulationBody>()),
                radius,
                wheels.Count,
                motor.maxWheelRpm,
                DrivetrainTuning.MeasureFriction(wheels),
                Physics.gravity.y,
                motor.driveForceTractionMultiple,
                motor.omniBrakeFraction);

            // The two design rules, checked against each REAL robot rather than one hand-written
            // configuration — a default that's fine for the 654V can still seize a robot with
            // different wheels or mass, and this is the only place that would notice.

            // A wheel must be able to break traction, or a full-throttle launch cannot spin its
            // tyres the way a real one does.
            float slipThreshold = expected.tractionForce * radius / wheels.Count;
            ValidationUtil.Assert(expected.stallTorque > slipThreshold,
                $"'{name}': per-wheel stall torque ({expected.stallTorque:0.#}) must exceed the slip " +
                $"threshold ({slipThreshold:0.#}) or the drivetrain seizes and the robot cannot turn. " +
                $"Raise Drive Force Traction Multiple (currently {motor.driveForceTractionMultiple}).");

            // ...but not so far above that the entire stick range is traction-saturated, which is
            // the "throttle is an on/off switch" complaint expressed in numbers.
            ValidationUtil.Assert(expected.motorLimitedStick > 0.15f,
                $"'{name}': only {expected.motorLimitedStick:P0} of stick travel is motor-limited, so " +
                "there is almost no fine control left. Lower Drive Force Traction Multiple " +
                $"(currently {motor.driveForceTractionMultiple}).");

            foreach (ArticulationBody wheel in wheels)
            {
                ArticulationDrive d = wheel.xDrive;
                ValidationUtil.Assert(Mathf.Abs(d.forceLimit - expected.stallTorque) < Mathf.Max(expected.stallTorque * 0.02f, 0.01f),
                    $"'{name}' wheel '{wheel.name}' has a stale serialized forceLimit " +
                    $"({d.forceLimit:0.##}, expected {expected.stallTorque:0.##}). Run " +
                    "Tools > RoboSim > Robot > Advanced > Apply Drive Tuning (All Prefabs).");
                ValidationUtil.Assert(Mathf.Abs(d.damping - expected.damping) < Mathf.Max(expected.damping * 0.02f, 0.01f),
                    $"'{name}' wheel '{wheel.name}' has a stale serialized damping " +
                    $"({d.damping:0.###}, expected {expected.damping:0.###}). Run " +
                    "Tools > RoboSim > Robot > Advanced > Apply Drive Tuning (All Prefabs).");
            }
            // What this robot ACTUALLY brakes on: one number for every robot, traction pair or not.
            // A traction pair changes the TYRE (WheelTyreModel), never the brake, so this is the line
            // that would catch a brake branch quietly finding its way back in.
            ValidationUtil.Assert(Mathf.Approximately(motor.BrakeFraction, motor.omniBrakeFraction),
                $"'{name}' brakes on {motor.BrakeFraction} rather than its Omni Brake Fraction " +
                $"({motor.omniBrakeFraction}) — there is one brake, and this is it.");

            checked_ += 5 + wheels.Count * 2;
        }

        if (checked_ == 0)
            throw new InvalidOperationException(
                $"No robot prefabs with a RobotMotorController under {RoboSimPaths.RobotsFolder} — nothing was checked.");
        return checked_;
    }

    // --- helpers ---

    private static DrivetrainTuning.Result Shipped() => DrivetrainTuning.Compute(
        Mass, Radius, Wheels, Rpm, Mu, G,
        DrivetrainTuning.DefaultDriveForceTractionMultiple);

}
