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
        checks += AuthorityDecision();
        checks += ShippedTune();
        checks += WheelTypeBrakes();
        checks += PlowRamp();
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

    // The renormalized arcade mix: when throttle + turn ask for more than a wheel has, the
    // THROTTLE gives way, never the turn. The old clamped mix silently threw away up to half the
    // commanded differential at full throttle — which is what "the forward momentum doesn't allow
    // turning" felt like: the outer wheel was already at free speed, the clamp ate the difference,
    // and the robot ploughed straight on.
    private static int MixPreservesTurn()
    {
        // Hand-computed pins. At full throttle + half turn the outer side stays at full and the
        // inner side gives up the WHOLE differential (old mix: (1.0, 0.5) — half of it lost).
        RobotMotorController.MixArcade(1f, 0.5f, out float l, out float r);
        ValidationUtil.Near(l, 1f, 1e-5f, "full throttle + half turn: outer side must hold full speed");
        ValidationUtil.Near(r, 0f, 1e-5f, "full throttle + half turn: inner side must carry the whole differential");

        RobotMotorController.MixArcade(-1f, 0.5f, out l, out r);
        ValidationUtil.Near(l, 0f, 1e-5f, "full reverse + half turn: outer side must carry the whole differential");
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

                // THE property this mix exists for: the differential survives at every throttle.
                ValidationUtil.Near(left - right, 2f * tu, 1e-5f,
                    $"the commanded differential must survive at every throttle (throttle {th}, turn {tu})");

                // Odd symmetry, or the robot turns differently left vs right.
                RobotMotorController.MixArcade(-th, -tu, out float ml, out float mr);
                ValidationUtil.Near(ml, -left, 1e-5f, $"mix must be odd-symmetric (throttle {th}, turn {tu})");
                ValidationUtil.Near(mr, -right, 1e-5f, $"mix must be odd-symmetric (throttle {th}, turn {tu})");

                // And throttle only gives way when it must: inside the square, it passes through.
                if (Mathf.Abs(th) + Mathf.Abs(tu) <= 1f + 1e-5f)
                {
                    ValidationUtil.Near(left + right, 2f * th, 1e-5f,
                        $"throttle must pass through untouched when the mix fits (throttle {th}, turn {tu})");
                    checks++;
                }
                checks += 4;
            }
        }
        return checks;
    }

    // The per-wheel authority decision: brake when back-driven at speed, EXCEPT while steering.
    // The inner wheel of a moving turn is by definition commanded slower than it spins — demoting
    // it to brake torque (23% of stall) was the single biggest reason a moving robot wouldn't
    // turn.
    private static int AuthorityDecision()
    {
        const float gate = 216f; // 15% of the 654V's 1440 deg/s free speed, in deg/s
        float thr = RobotMotorController.TurnAuthorityThreshold;

        ValidationUtil.Near(thr, 0.05f, 1e-6f,
            "the shipped steering-override threshold — nudging it changes when every robot brakes");

        // The brake pedal: released sticks command zero against a spinning wheel.
        ValidationUtil.Assert(RobotMotorController.DecideAuthority(0f, 1440f, gate, 0f, thr)
            == RobotMotorController.DriveAuthority.Brake, "released at speed must brake");
        ValidationUtil.Assert(RobotMotorController.DecideAuthority(0f, -1440f, gate, 0f, thr)
            == RobotMotorController.DriveAuthority.Brake, "released at reverse speed must brake too");

        // ...and below the moving gate the wheel parks under full Drive authority instead.
        ValidationUtil.Assert(RobotMotorController.DecideAuthority(0f, 10f, gate, 0f, thr)
            == RobotMotorController.DriveAuthority.Drive, "a stopped wheel must hold, not chatter on the brake");

        // A hard reversal from speed is back-driven — the case the quadrant exists for.
        ValidationUtil.Assert(RobotMotorController.DecideAuthority(-1440f, 1440f, gate, 0f, thr)
            == RobotMotorController.DriveAuthority.Brake, "a reversal from speed must be brake-limited");

        // Steering override: the inner wheel of a moving turn keeps full authority.
        ValidationUtil.Assert(RobotMotorController.DecideAuthority(720f, 1440f, gate, 0.5f, thr)
            == RobotMotorController.DriveAuthority.Drive, "steering must never be brake-limited");

        // Acceleration is never braked.
        ValidationUtil.Assert(RobotMotorController.DecideAuthority(1440f, 720f, gate, 0f, thr)
            == RobotMotorController.DriveAuthority.Drive, "accelerating must keep full authority");

        // The threshold edge, both sides.
        ValidationUtil.Assert(RobotMotorController.DecideAuthority(720f, 1440f, gate, 0.049f, thr)
            == RobotMotorController.DriveAuthority.Brake, "just under the threshold is not steering");
        ValidationUtil.Assert(RobotMotorController.DecideAuthority(720f, 1440f, gate, 0.05f, thr)
            == RobotMotorController.DriveAuthority.Drive, "at the threshold the override must engage");

        return 9;
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

        // The far end of the brake ramp: a slammed reversal, at the full traction budget — 2354.4
        // over 6 wheels at r 0.37. This is the number that decides whether a robot can be made to
        // tip by driving at all; at the coast's 0.16 g the longitudinal force is roughly a fifth of
        // what it takes to lift a rear wheel, so no mass distribution could ever get there.
        ValidationUtil.Near(t.plowTorque, 145.19f, 0.1f, "per-wheel torque for a full-stick reversal");
        ValidationUtil.Near(t.plowG, 0.80f, 0.005f, "a slam should spend the whole friction cone");

        // THE invariant behind "a stop should feel progressive, not a skid": for a COAST the motor,
        // not the ground, has to be what limits it. Above the friction cone the tyres just slip, the
        // deceleration pins at mu*g however far the stick has moved, and every release costs the
        // same nothing. Below it the force builds with the command.
        ValidationUtil.Assert(t.brakeG < t.tractionG,
            $"coasting ({t.brakeG:0.00} g) must stay inside the friction cone ({t.tractionG:0.00} g) " +
            "or letting go is traction-limited and instantaneous again");

        // The plow is the deliberate exception, and it is bounded on both sides: never weaker than
        // the coast (or slamming reverse would stop the robot LESS than letting go), never past the
        // cone (which would be force the ground cannot transmit — the excess is a lie the solver
        // would have to absorb).
        ValidationUtil.Assert(t.plowG >= t.brakeG,
            $"a slam ({t.plowG:0.00} g) must never brake softer than a release ({t.brakeG:0.00} g)");
        ValidationUtil.Assert(t.plowG <= t.tractionG + 1e-4f,
            $"a slam ({t.plowG:0.00} g) cannot exceed the friction cone ({t.tractionG:0.00} g)");

        // A wheel must be able to break traction. Below mu*m*g*r/N it cannot slip at all, and a
        // 6-wheel robot with isotropic (non-omni-modelled) wheels then cannot complete a point
        // turn — measured, not theorised: at 1.0x traction PhysicsSmokeTest yawed 0.1 degrees.
        float slipThreshold = t.tractionForce * Radius / Wheels;
        ValidationUtil.Assert(t.stallTorque > slipThreshold,
            $"per-wheel stall torque ({t.stallTorque:0.#}) must exceed the slip threshold " +
            $"({slipThreshold:0.#}) or the drivetrain seizes and the robot cannot turn");

        // ...but not so far above that the whole stick range is traction-saturated, which is the
        // "throttle is an on/off switch" complaint in numbers.
        ValidationUtil.Assert(t.motorLimitedStick > 0.15f,
            $"only the top {(1f - t.motorLimitedStick):P0} of stick travel may be traction-limited; " +
            "past that there is no fine control left");
        return 16;
    }

    // The wheel-type split — the difference between "the brake is too powerful" and a drivetrain
    // that rolls on the way an all-omni drive really does.
    //
    // The Settings checkbox that used to choose between these two is gone (see the retirement note
    // in RoboSimSettings); every robot now brakes on the omni number. Both fractions are still
    // checked, because both constants are still there and the ordering between them is the whole
    // reason the shipped one is the drifty one — a retune that swapped them would silently give
    // every robot the firm stop.
    //
    // Asserted as ROLL-OUT DISTANCE, not as torque, because distance is the thing the driver
    // actually feels and the only form in which "considerable drift" is a checkable claim. Constant
    // deceleration is the right model here: the brake clamp binds from full speed all the way down
    // to the moving gate (brakeTorque is 6.7% of stall against a drive that reaches stall at free
    // speed), so the wheel decelerates at brakeG for essentially the whole stop.
    private static int WheelTypeBrakes()
    {
        DrivetrainTuning.Result omni = WithBrake(DrivetrainTuning.DefaultOmniBrakeFraction);
        DrivetrainTuning.Result traction = WithBrake(DrivetrainTuning.DefaultTractionBrakeFraction);

        // brakeG is exactly mu * fraction, which is what makes these two numbers predictable from
        // the constants rather than emergent. Pinned so nobody re-tunes the feel by accident.
        ValidationUtil.Near(omni.brakeG, 0.16f, 0.005f, "all-omni braking should be 0.2 of the 0.8 g cone");
        ValidationUtil.Near(traction.brakeG, 0.56f, 0.005f, "traction-wheel braking should be 0.7 of it");

        float omniRollout = RolloutUnits(omni);
        float tractionRollout = RolloutUnits(traction);

        // The felt numbers for the reference 240 RPM robot: ~0.28 m of roll versus ~0.08 m.
        ValidationUtil.Near(omniRollout, 2.754f, 0.02f, "an all-omni robot's roll-out from full speed");
        ValidationUtil.Near(tractionRollout, 0.787f, 0.02f, "a traction-wheel robot's roll-out");

        // The two checks below are DERIVED guards, and under mutation the exact pins above fire
        // first and shadow them — so they look redundant today. They aren't: they exist for the
        // NEXT retune. Whoever moves a fraction will re-pin the exact values along with it (that is
        // what re-pinning is for), and these are what still catch them if the new feel is one a
        // driver can't use. Verified that way: mutating the fraction AND every pin with it leaves
        // exactly these two standing, and both fire.
        //
        // The two fractions have to stay far enough apart to mean different things. If they ever
        // converge, the second one has stopped being an alternative and should be deleted rather
        // than kept parked.
        ValidationUtil.Assert(omniRollout > tractionRollout * 3f,
            $"the traction-wheel stop ({tractionRollout:0.00} u) must be at least 3x shorter than the " +
            $"all-omni one ({omniRollout:0.00} u), or the two fractions are the same setting twice");

        // ...and the other side of it, which is the failure mode of tuning drift by feel: a robot
        // that rolls for two thirds of a metre on a 240 RPM drive has stopped being drifty and
        // started ignoring the driver. The old coast-on-release model died of exactly this.
        ValidationUtil.Assert(omniRollout < 6f,
            $"an all-omni robot rolls {omniRollout:0.0} units ({omniRollout * 0.1f:0.00} m) after the " +
            "sticks are released — past this it reads as 'the brake does nothing', not as drift");

        // Both stops stay inside the friction cone, so the MOTOR is what limits them and the force
        // builds with the command. Above the cone the tyres just slip and every stop costs the
        // driver the same nothing, whichever wheels are declared.
        ValidationUtil.Assert(omni.brakeG < omni.tractionG && traction.brakeG < traction.tractionG,
            $"both stops must stay under the {omni.tractionG:0.00} g friction cone " +
            $"(omni {omni.brakeG:0.00} g, traction {traction.brakeG:0.00} g)");

        // A motor cannot brake harder than it can drive, on either setting.
        ValidationUtil.Assert(traction.brakeTorque <= traction.stallTorque + 1e-4f,
            "the traction-wheel brake must not exceed stall torque");

        // Ordering, pinned against the consts themselves rather than their consequences: swapping
        // the two defaults would make the drifty setting the firm one and pass everything above.
        ValidationUtil.Assert(
            DrivetrainTuning.DefaultOmniBrakeFraction < DrivetrainTuning.DefaultTractionBrakeFraction,
            "omnis must be the weaker brake — they are the default, and they are the drifty one");

        return 9;
    }

    // Distance to a standstill from top speed under a constant brakeG, in world units.
    private static float RolloutUnits(DrivetrainTuning.Result t) => RolloutUnits(t, t.brakeG);

    private static float RolloutUnits(DrivetrainTuning.Result t, float decelG)
    {
        float decel = decelG * G;
        return decel > 1e-6f ? t.topSpeed * t.topSpeed / (2f * decel) : float.PositiveInfinity;
    }

    // The brake ramp itself — which of the braking quadrant's three situations gets which torque.
    //
    // This is the check that guards the user-visible promise made when the ramp was added: the
    // coast is untouched and ONLY a deliberate reversal changed. Symbolic torques (10 and 100)
    // rather than a real tune, on purpose — the point is which end of the ramp is selected and how
    // it interpolates, and feeding it Shipped() would just be the formula agreeing with itself.
    private static int PlowRamp()
    {
        const float brake = 10f, plow = 100f, full = 1440f;
        float At(float target, float spin) =>
            RobotMotorController.BrakeForceLimit(target, spin, full, brake, plow);

        // THE one that must not move: centre stick against a spinning wheel is the brake pedal, and
        // every roll-out distance in WheelTypeBrakes assumes it pulls exactly the coast torque.
        ValidationUtil.Near(At(0f, full), brake, 1e-4f,
            "centre stick must pull exactly the coast torque — the brake pedal is not a reversal");

        // Trailing, not opposing: eased off the throttle, and also the inner wheel of every moving
        // turn. Handing this a plow torque would re-break moving turns the same way capping the
        // inner wheel at brake torque once did.
        ValidationUtil.Near(At(full * 0.5f, full), brake, 1e-4f,
            "a command that merely trails the spin is a coast, not a slam");

        // Full stick the other way: the whole point of the split.
        ValidationUtil.Near(At(-full, full), plow, 1e-4f, "a full-stick reversal must pull the plow torque");
        ValidationUtil.Near(At(full, -full), plow, 1e-4f, "...in both directions");

        // ...and it is a RAMP, so half a reversal is half of it. Without this the split could be a
        // step function and every check above would still pass, which is the on/off throttle this
        // drivetrain was retuned to get rid of.
        ValidationUtil.Near(At(-full * 0.5f, full), 55f, 1e-3f, "half a reversal should pull half way up the ramp");
        ValidationUtil.Near(At(-full * 0.25f, full), 32.5f, 1e-3f, "...and a quarter, a quarter of the way");

        // A sensitivity slider above 1 can shape a target past full stick; the ramp must saturate
        // rather than extrapolate past the traction budget.
        ValidationUtil.Near(At(-full * 2f, full), plow, 1e-4f, "beyond full stick the ramp must clamp");

        // A stopped wheel has no direction to oppose. It never reaches here (DecideAuthority's
        // moving gate hands it to the parking hold first) — this pins the safe answer if it ever does.
        ValidationUtil.Near(At(-full, 0f), brake, 1e-4f, "a stopped wheel cannot be reversing");

        // Degenerate gearing: a robot with maxWheelRpm 0 must not divide by it.
        ValidationUtil.Near(RobotMotorController.BrakeForceLimit(-full, full, 0f, brake, plow), brake, 1e-4f,
            "a zero free speed must fall back to the coast torque, not a NaN force limit");

        // The felt consequence, on the real tune: a slam has to actually stop the robot in a
        // distance a driver reads as a stop. 0.55 u is 5.5 cm against the all-omni coast's 2.75 u.
        DrivetrainTuning.Result shipped = Shipped();
        ValidationUtil.Near(RolloutUnits(shipped, shipped.plowG), 0.551f, 0.01f,
            "a slammed reversal's stopping distance from full speed");

        // And the ordering that makes the wheel-type checkbox still mean something: a slam is the
        // one input where both wheel types spend everything, so it must beat even the traction coast.
        ValidationUtil.Assert(shipped.plowG > WithBrake(DrivetrainTuning.DefaultTractionBrakeFraction).brakeG,
            "a slam must stop harder than a traction-wheel robot's coast, or the plow ramp is pointless");

        // Pinned against the const itself, not its consequences: dropping the plow fraction to or
        // below the traction brake fraction would make the two indistinguishable and pass most of
        // the above through the Max clamp in Compute.
        ValidationUtil.Assert(
            DrivetrainTuning.DefaultPlowTractionFraction > DrivetrainTuning.DefaultTractionBrakeFraction,
            "the plow fraction must exceed both coast fractions — it is the far end of their ramp");

        return 12;
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
            ("zero plow fraction", DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, Mu, G, 3f, 0.2f, 0f)),
            ("inverted plow fraction", DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, Mu, G, 3f, 0.7f, 0.1f)),
            ("everything zero", DrivetrainTuning.Compute(0f, 0f, 0, 0f, 0f, 0f, 0f, 0f, 0f)),
        };

        foreach ((string what, DrivetrainTuning.Result r) in cases)
        {
            ValidationUtil.Finite(r.stallTorque, $"{what}: stallTorque");
            ValidationUtil.Finite(r.damping, $"{what}: damping");
            ValidationUtil.Finite(r.brakeTorque, $"{what}: brakeTorque");
            ValidationUtil.Finite(r.plowTorque, $"{what}: plowTorque");
            ValidationUtil.Finite(r.maxJointVelocity, $"{what}: maxJointVelocity");
            ValidationUtil.Finite(r.secondsTo95, $"{what}: secondsTo95");
            ValidationUtil.Finite(r.brakeG, $"{what}: brakeG");
            ValidationUtil.Finite(r.plowG, $"{what}: plowG");
            ValidationUtil.Finite(r.tractionG, $"{what}: tractionG");
            ValidationUtil.Assert(r.maxJointVelocity > 0f, $"{what}: maxJointVelocity must stay positive or the wheels can't turn");

            // The Max clamp in Compute, checked where it matters: a plow fraction set BELOW the
            // brake fraction must not invert the ramp, or BrakeForceLimit interpolates backwards
            // and a harder slam brakes softer. "inverted plow fraction" above is that case.
            ValidationUtil.Assert(r.plowTorque >= r.brakeTorque - 1e-4f,
                $"{what}: the plow torque must never fall below the coast torque");
        }

        // Unity reports gravity as NEGATIVE y, and callers pass Physics.gravity.y straight in.
        // Sign errors here would silently invert the whole traction budget.
        // Same arguments as Shipped() apart from the sign, so this tests the sign and nothing else.
        DrivetrainTuning.Result down = DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, Mu, -G,
            DrivetrainTuning.DefaultDriveForceTractionMultiple);
        ValidationUtil.Near(down.stallTorque, Shipped().stallTorque, 1e-3f, "gravity's sign must not change the tune");
        return cases.Count * 11 + 1;
    }

    // --- Shipped prefabs -----------------------------------------------------------------------

    // Every robot must be on the derived tune, and its SERIALIZED drives must agree with it.
    //
    // The serialized half matters because PhysicsSmokeTest simulates in edit mode, where Awake
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
                motor.omniBrakeFraction,
                motor.plowFraction);

            // The two design rules, checked against each REAL robot rather than one hand-written
            // configuration — a default that's fine for the 654V can still seize a robot with
            // different wheels or mass, and this is the only place that would notice.

            // A wheel must be able to break traction, or a 6-wheel robot with isotropic
            // (non-omni-modelled) wheels cannot complete a point turn. Measured, not theorised:
            // at 1.0x traction PhysicsSmokeTest yawed 0.1 degrees instead of 80.
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
            // What this robot ACTUALLY brakes on. The wheel-type checkbox is gone and BrakeFraction
            // no longer branches, so this is the line that would catch a traction branch quietly
            // finding its way back in — every robot ships on the omni number.
            ValidationUtil.Assert(Mathf.Approximately(motor.BrakeFraction, motor.omniBrakeFraction),
                $"'{name}' brakes on {motor.BrakeFraction} rather than its Omni Brake Fraction " +
                $"({motor.omniBrakeFraction}) — the traction fraction is parked, not shipped.");

            // The stop has to stay motor-limited, and the parked traction option has to remain the
            // firmer of the two: a prefab carrying a hand-tuned pair the wrong way round would make
            // the alternative stop the robot LESS, which is not an alternative worth keeping.
            ValidationUtil.Assert(motor.omniBrakeFraction < motor.tractionBrakeFraction,
                $"'{name}' has Omni Brake Fraction ({motor.omniBrakeFraction}) at or above Traction " +
                $"Brake Fraction ({motor.tractionBrakeFraction}) — the firm stop is meant to be the " +
                "firmer one.");
            float mu = DrivetrainTuning.MeasureFriction(wheels);
            ValidationUtil.Assert(motor.tractionBrakeFraction < 1f,
                $"'{name}': a brake fraction of {motor.tractionBrakeFraction} is at or past the " +
                $"friction cone (mu {mu:0.##}), so a stop skids at the traction limit instead of " +
                "building progressively with the command.");

            // The plow is the far end of that same ramp, so it has to be above both ends of it.
            // A prefab whose plow fraction had drifted under its brake fraction would silently fall
            // back to a flat coast through the Max clamp in Compute — the pre-split drivetrain,
            // with no symptom except that the robot stopped being able to tip.
            ValidationUtil.Assert(motor.plowFraction > motor.tractionBrakeFraction,
                $"'{name}' has Plow Fraction ({motor.plowFraction}) at or below Traction Brake " +
                $"Fraction ({motor.tractionBrakeFraction}) — a slammed reversal would then be no " +
                "harder than letting go, and no robot on this drivetrain could tip itself.");

            // The reason the tip targets are reachable at all. Below the cone a slam is
            // motor-limited and the robot can only ever decelerate at plowFraction * mu * g, which
            // on the shipped geometry is short of the moment it takes to lift a rear wheel.
            ValidationUtil.Assert(expected.plowG > 0.5f * expected.tractionG,
                $"'{name}': a slammed reversal only reaches {expected.plowG:0.00} g against a " +
                $"{expected.tractionG:0.00} g friction cone. Raise Plow Fraction (currently " +
                $"{motor.plowFraction}) or the drivetrain cannot generate a tipping moment.");

            checked_ += 9 + wheels.Count * 2;
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

    private static DrivetrainTuning.Result WithBrake(float brakeFraction) => DrivetrainTuning.Compute(
        Mass, Radius, Wheels, Rpm, Mu, G,
        DrivetrainTuning.DefaultDriveForceTractionMultiple, brakeFraction);
}
