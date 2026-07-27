using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Headless checks for the drivetrain's feel math — DrivetrainTuning's motor model and
// RobotMotorController's input shaping.
//
// Worth proving without a Play session because the failure mode is silent: a wrong damping or a
// slew that isn't timestep-independent doesn't throw, it just makes the robot drive slightly wrong
// on some machines, which is exactly the kind of thing that gets reported as "feels off on my
// laptop" and never reproduces.
//
// Touches no PlayerPrefs, so unlike the binding validators it needs no snapshot/restore.
//
// Usage: Tools > RoboSim > Testing > Validate Drive Feel, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod DriveFeelValidation.RunBatchValidate
// which exits nonzero on the first failed check.
public static class DriveFeelValidation
{
    private const string RobotsFolder = "Assets/Robots";

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
    private const float Crr = DrivetrainTuning.DefaultRollingResistance;

    [MenuItem("Tools/RoboSim/Testing/Validate Drive Feel", false, 16)]
    private static void RunInteractive()
    {
        try
        {
            EditorUtility.DisplayDialog("Validate Drive Feel", Run(), "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Validate Drive Feel", "FAILED\n\n" + e.Message, "OK");
            Debug.LogException(e);
        }
    }

    public static void RunBatchValidate()
    {
        try
        {
            Debug.Log(Run());
        }
        catch (Exception e)
        {
            Debug.LogError("Validate Drive Feel FAILED: " + e.Message);
            EditorApplication.Exit(1);
            return;
        }
        EditorApplication.Exit(0);
    }

    private static string Run()
    {
        int checks = 0;
        checks += ShapeEndpoints();
        checks += ShapeCurve();
        checks += SlewRates();
        checks += SlewReversalTiming();
        checks += SlewTimestepInvariance();
        checks += ShippedTune();
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
                Near(RobotMotorController.Shape(0f, dz, expo), 0f, 1e-5f, $"centre must be dead (dz {dz}, expo {expo})");
                Near(RobotMotorController.Shape(dz, dz, expo), 0f, 1e-5f, $"the deadzone edge must still be zero (dz {dz})");
                Near(RobotMotorController.Shape(1f, dz, expo), 1f, 1e-5f, $"full stick must reach full output (dz {dz}, expo {expo})");
                Near(RobotMotorController.Shape(-1f, dz, expo), -1f, 1e-5f, $"full reverse must reach full output (dz {dz})");

                // Odd symmetry: pushing left must mirror pushing right, or the robot pulls to one side.
                for (float v = 0.05f; v <= 1f; v += 0.05f)
                {
                    Near(RobotMotorController.Shape(-v, dz, expo), -RobotMotorController.Shape(v, dz, expo),
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
                    Assert(shaped >= previous - 1e-5f,
                        $"Shape must never decrease as the stick moves out (dz {dz}, expo {expo}, at {v})");
                    Assert(shaped <= 1f + 1e-5f, $"Shape must never exceed 1 (dz {dz}, expo {expo}, at {v})");
                    previous = shaped;
                }
            }
        }

        // Halfway past the deadzone is half output: with a 0.5 deadzone and no expo, 0.75 sits
        // exactly midway between the dead edge and full, so it must read 0.5.
        Near(RobotMotorController.Shape(0.75f, 0.5f, 0f), 0.5f, 1e-5f,
            "with expo off, travel past the deadzone must be linear");

        // Fully cubic: half stick is an eighth of the output.
        Near(RobotMotorController.Shape(0.5f, 0f, 1f), 0.125f, 1e-5f,
            "expo 1 must be a pure cube");

        // ...and expo must only soften the middle, never the ends (checked at 1 above) or the sign.
        Assert(RobotMotorController.Shape(0.5f, 0f, 1f) < RobotMotorController.Shape(0.5f, 0f, 0f),
            "expo must give FINER control near centre, not coarser");
        return 12;
    }

    // --- Slew --------------------------------------------------------------------------------

    // Growing away from zero uses the rise rate; shrinking back toward it uses the (faster) fall
    // rate. Backwards rates would make the robot lazy to start and lazy to stop, which is the
    // worst of both.
    private static int SlewRates()
    {
        Near(RobotMotorController.Slew(0f, 1f, 4f, 8f, 0.1f), 0.4f, 1e-5f,
            "rising from rest must use the rise rate");
        Near(RobotMotorController.Slew(1f, 0.5f, 4f, 8f, 0.01f), 0.92f, 1e-5f,
            "easing off must use the fall rate");
        Near(RobotMotorController.Slew(-1f, 0f, 4f, 8f, 0.01f), -0.92f, 1e-5f,
            "releasing from reverse must use the fall rate too");

        // Never overshoot, however big the step.
        Near(RobotMotorController.Slew(0f, 1f, 100f, 100f, 1f), 1f, 1e-5f, "a huge step must land ON the target");
        Near(RobotMotorController.Slew(0.3f, 0.3f, 4f, 8f, 0.1f), 0.3f, 1e-5f, "already there must stay there");

        // A zero rate must not divide by itself into a NaN.
        float stuck = RobotMotorController.Slew(0.5f, -0.5f, 0f, 0f, 0.1f);
        Assert(!float.IsNaN(stuck) && !float.IsInfinity(stuck), "zero rates must not produce NaN");
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
        Assert(justShort > -1f + 1e-4f, $"a reversal must not finish early (was {justShort} after {expectedSteps - 1} steps)");

        float onTime = Integrate(1f, -1f, rise, fall, dt, expectedSteps);
        Near(onTime, -1f, 1e-4f, $"a reversal must take exactly 1/fall + 1/rise seconds ({expectedSteps} steps)");

        // It must actually pass through zero rather than jumping the sign.
        float mid = Integrate(1f, -1f, rise, fall, dt, 25);
        Near(mid, 0f, 1e-5f, "the falling half of a reversal must land on zero");
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
            Near(coarse, fine, 1e-4f,
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

    // --- Tuning ------------------------------------------------------------------------------

    // The shipped 654V numbers, hand-computed from the constants at the top of this file. This is
    // the check that notices someone "just nudging" a default and quietly changing how every robot
    // drives.
    private static int ShippedTune()
    {
        DrivetrainTuning.Result t = Shipped();

        Near(t.tractionForce, 2354.4f, 1f, "traction budget should be mu*m*g = 0.8 * 30 * 98.1");
        Near(t.peakForce, 7063.2f, 2f, "peak drive force should be 3x the traction budget");
        Near(t.stallTorque, 435.56f, 0.1f, "per-wheel stall torque");
        Near(t.damping, 0.30247f, 0.0005f, "per-wheel velocity gain, in torque per deg/s");
        Near(t.topSpeed, 9.30f, 0.02f, "top speed should be ~0.93 m/s for a 2.75in omni at 240 RPM");
        Near(t.motorLimitedStick, 1f / 3f, 0.005f,
            "the first third of stick travel should be motor-limited, so fine control is real");
        // Coast, from rolling resistance: F = Crr*m*g = 0.086 * 30 * 98.1 = 253.1 over 6 wheels at
        // r 0.37. The cap must NOT bind here, or the coast the feel was tuned against silently
        // stops being the coast that ships.
        Near(t.coastTorque, 15.61f, 0.05f, "coast torque should be Crr*m*g*r/N");
        Assert(t.coastTorque < t.stallTorque * DrivetrainTuning.MaxCoastTorqueFraction - 1e-3f,
            "the coast cap must not bind on a 654V-speed robot — rolling resistance should be met " +
            "exactly there");

        // The numbers a human actually judges the coast by, hand-computed: a = Crr*g = 8.44 u/s^2,
        // so 9.30 u/s takes 1.10 s over 9.30^2/(2*8.44) = 5.12 u.
        Near(t.coastSeconds, 1.10f, 0.02f, "a 654V should coast to rest in about a second");
        Near(t.coastDistance, 5.12f, 0.05f, "...over about half a metre");

        // Braking: 0.7 of the traction budget, i.e. 0.7*2354.4 = 1648 over 6 wheels at r 0.37.
        Near(t.brakeTorque, 101.63f, 0.2f, "per-wheel braking-quadrant torque");
        Near(t.tractionG, 0.80f, 0.005f, "the tyres' grip is mu, so the friction cone is 0.8 g");
        Near(t.brakeG, 0.56f, 0.01f, "braking should be 0.7 of the friction cone");

        // THE invariant behind "a reversal should feel like it carries momentum": the motor, not
        // the ground, has to be what limits it. Above the friction cone the tyres just slip, the
        // deceleration pins at mu*g however hard the driver slams the stick, and every direction
        // change costs the same nothing. Below it the force builds with the command.
        Assert(t.brakeG < t.tractionG,
            $"braking ({t.brakeG:0.00} g) must stay inside the friction cone ({t.tractionG:0.00} g) " +
            "or a reversal is traction-limited and instantaneous again");

        // And coasting must be decisively gentler than braking, or "coast" is a lie.
        Assert(t.coastTorque < t.brakeTorque,
            $"coast torque ({t.coastTorque:0.#}) must be well under brake torque ({t.brakeTorque:0.#})");

        // A wheel must be able to break traction. Below mu*m*g*r/N it cannot slip at all, and a
        // 6-wheel robot with isotropic (non-omni-modelled) wheels then cannot complete a point
        // turn — measured, not theorised: at 1.0x traction PhysicsSmokeTest yawed 0.1 degrees.
        float slipThreshold = t.tractionForce * Radius / Wheels;
        Assert(t.stallTorque > slipThreshold,
            $"per-wheel stall torque ({t.stallTorque:0.#}) must exceed the slip threshold " +
            $"({slipThreshold:0.#}) or the drivetrain seizes and the robot cannot turn");

        // ...but not so far above that the whole stick range is traction-saturated, which is the
        // "throttle is an on/off switch" complaint in numbers.
        Assert(t.motorLimitedStick > 0.15f,
            $"only the top {(1f - t.motorLimitedStick):P0} of stick travel may be traction-limited; " +
            "past that there is no fine control left");
        return 17;
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
                    Mass, Radius, Wheels, rpm, Mu, G, multiple,
                    DrivetrainTuning.DefaultRollingResistance);

                Near(t.peakForce / t.tractionForce, multiple, 1e-3f,
                    $"peak force must be exactly the requested multiple of traction (rpm {rpm}, multiple {multiple})");

                // damping * freeSpeed == stallTorque is what makes drive torque fall linearly to
                // zero at free speed — i.e. what makes this a motor curve instead of a switch.
                //
                // In DEGREES per second: a rotational ArticulationDrive differences targetVelocity
                // against the joint velocity in degrees, so damping is torque per (deg/s). Deriving
                // it from rad/s leaves it 57.3x too large and quietly restores the bang-bang drive.
                float freeSpeedDeg = rpm * 6f;
                Near(t.damping * freeSpeedDeg, t.stallTorque, t.stallTorque * 1e-3f,
                    $"torque must reach zero exactly at free speed, in the drive's own deg/s units (rpm {rpm})");

                // The behaviour all of that exists to produce: half stick pulls half as hard.
                // torque = damping * (target - current), so at a standstill with half the target
                // speed the drive must make exactly half its stall torque — and must NOT be
                // sitting on the force limit, which is what "the throttle is an on/off switch"
                // looks like in numbers.
                float halfStickTorque = t.damping * (freeSpeedDeg * 0.5f);
                Near(halfStickTorque, t.stallTorque * 0.5f, t.stallTorque * 1e-3f,
                    $"half stick must command half the torque (rpm {rpm}, multiple {multiple})");
                Assert(halfStickTorque < t.stallTorque,
                    $"half stick must not saturate the force limit (rpm {rpm}, multiple {multiple})");

                // Headroom above free speed, or a coasting wheel gets clamped and reads as a brake.
                float freeSpeed = rpm * Mathf.PI * 2f / 60f;
                Assert(t.maxJointVelocity > freeSpeed,
                    $"maxJointVelocity must exceed free speed (rpm {rpm})");

                // Coast must be gentler than braking at EVERY gearing, or "coast" is a lie. Under
                // the rolling-resistance model coast torque no longer grows with gearing, so this
                // only bites on a drivetrain too weak to overcome its own rolling resistance —
                // which is precisely when it matters most.
                Assert(t.coastTorque <= t.stallTorque * DrivetrainTuning.MaxCoastTorqueFraction + 1e-4f,
                    $"coast torque must stay under its share of stall torque (rpm {rpm}, multiple {multiple}): " +
                    $"got {t.coastTorque} against stall {t.stallTorque}");

                // Braking stays inside the friction cone at every gearing, so a reversal is always
                // motor-limited rather than a skid — see ShippedTune for why that is the point.
                Assert(t.brakeG < t.tractionG + 1e-4f,
                    $"braking must stay inside the friction cone (rpm {rpm}, multiple {multiple}): " +
                    $"got {t.brakeG:0.000} g against {t.tractionG:0.000} g");
                Assert(t.brakeTorque <= t.stallTorque + 1e-4f,
                    $"a motor cannot brake harder than it can drive (rpm {rpm}, multiple {multiple})");
                checks += 9;
            }
        }
        return checks;
    }

    // The model is expressed in traction multiples and times precisely so a heavier or faster robot
    // correct without a hand tune. These are the two claims that buys.
    private static int ScaleInvariance()
    {
        DrivetrainTuning.Result baseline = Shipped();

        // Twice the mass: twice the grip, so twice the force, so the SAME acceleration curve.
        DrivetrainTuning.Result heavy = DrivetrainTuning.Compute(
            Mass * 2f, Radius, Wheels, Rpm, Mu, G,
            DrivetrainTuning.DefaultDriveForceTractionMultiple, DrivetrainTuning.DefaultRollingResistance);
        Near(heavy.stallTorque, baseline.stallTorque * 2f, 0.05f, "twice the mass needs twice the torque");
        Near(heavy.secondsTo95, baseline.secondsTo95, 1e-3f, "a heavier robot must accelerate on the same curve");
        Near(heavy.topSpeed, baseline.topSpeed, 1e-3f, "mass must not change top speed");

        // Rolling resistance is Crr*m*g, so twice the mass is twice the retarding force — and
        // therefore the SAME deceleration and the same glide. Mass cancels, exactly as it does
        // when a loaded and an empty shopping trolley roll the same distance.
        Near(heavy.coastTorque, baseline.coastTorque * 2f, 0.05f, "twice the mass is twice the drag");
        Near(heavy.coastDistance, baseline.coastDistance, 1e-2f, "...so it glides exactly as far");

        // Twice the gearing: twice the top speed, twice as long to reach it, same peak force.
        DrivetrainTuning.Result fast = DrivetrainTuning.Compute(
            Mass, Radius, Wheels, Rpm * 2f, Mu, G,
            DrivetrainTuning.DefaultDriveForceTractionMultiple, DrivetrainTuning.DefaultRollingResistance);
        Near(fast.topSpeed, baseline.topSpeed * 2f, 1e-3f, "twice the RPM is twice the top speed");
        Near(fast.stallTorque, baseline.stallTorque, 1e-3f, "gearing must not change the traction-limited force");
        Near(fast.secondsTo95, baseline.secondsTo95 * 2f, 1e-3f, "a taller-geared robot takes proportionally longer");

        // THE assertion this model exists for, and it is the exact inverse of what used to be
        // asserted here ("coast must stay a fixed duration regardless of gearing"). Coast used to
        // be a stop TIME, which made a robot's tyres 79% draggier for no reason but a taller
        // cartridge. Rolling resistance is a property of the wheel and the field: the drag is
        // unchanged, so a robot that goes twice as fast takes twice as long to stop and rolls four
        // times as far. If this ever flips back, the physics has been undone.
        Near(fast.coastTorque, baseline.coastTorque, 1e-3f,
            "a taller gearbox cannot make the tyres draggier — coast drag must be gearing-invariant");
        Near(fast.coastSeconds, baseline.coastSeconds * 2f, 1e-2f,
            "twice the top speed takes twice as long to roll off at a fixed deceleration");
        Near(fast.coastDistance, baseline.coastDistance * 4f, 1e-1f,
            "...and covers v^2/(2a), so four times the distance");

        // The cap, which now means something different: not "a tall gearbox needs more coast
        // torque than it has", but "this drivetrain is weaker than its own rolling resistance".
        // At 0.2x traction the motors make 29 units of stall torque against 15.6 of rolling drag,
        // so the coast is clamped rather than out-braking the brake.
        DrivetrainTuning.Result weak = DrivetrainTuning.Compute(
            Mass, Radius, Wheels, Rpm, Mu, G, 0.2f, DrivetrainTuning.DefaultRollingResistance);
        Near(weak.coastTorque,
            weak.stallTorque * DrivetrainTuning.MaxCoastTorqueFraction, 0.05f,
            "a drivetrain weaker than its own rolling resistance must have its coast capped at its " +
            "share of stall torque");
        return 12;
    }

    // A half-rigged robot (no wheels wired yet, colliders not generated, a placeholder mass) must
    // still produce values PhysX can accept. A NaN forceLimit takes the whole articulation down.
    private static int DegenerateInputs()
    {
        var cases = new List<(string what, DrivetrainTuning.Result r)>
        {
            ("no wheels", DrivetrainTuning.Compute(Mass, Radius, 0, Rpm, Mu, G, 3f, Crr)),
            ("zero radius", DrivetrainTuning.Compute(Mass, 0f, Wheels, Rpm, Mu, G, 3f, Crr)),
            ("zero rpm", DrivetrainTuning.Compute(Mass, Radius, Wheels, 0f, Mu, G, 3f, Crr)),
            ("zero mass", DrivetrainTuning.Compute(0f, Radius, Wheels, Rpm, Mu, G, 3f, Crr)),
            ("frictionless", DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, 0f, G, 3f, Crr)),
            ("zero gravity", DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, Mu, 0f, 3f, Crr)),
            ("negative gravity", DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, Mu, -G, 3f, Crr)),
            ("zero multiple", DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, Mu, G, 0f, Crr)),
            ("zero rolling resistance", DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, Mu, G, 3f, 0f)),
            ("zero brake fraction", DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, Mu, G, 3f, Crr, 0f)),
            ("everything zero", DrivetrainTuning.Compute(0f, 0f, 0, 0f, 0f, 0f, 0f, 0f, 0f)),
        };

        foreach ((string what, DrivetrainTuning.Result r) in cases)
        {
            Finite(r.stallTorque, $"{what}: stallTorque");
            Finite(r.damping, $"{what}: damping");
            Finite(r.coastTorque, $"{what}: coastTorque");
            Finite(r.brakeTorque, $"{what}: brakeTorque");
            Finite(r.maxJointVelocity, $"{what}: maxJointVelocity");
            Finite(r.secondsTo95, $"{what}: secondsTo95");
            Finite(r.coastSeconds, $"{what}: coastSeconds");
            Finite(r.coastDistance, $"{what}: coastDistance");
            Finite(r.brakeG, $"{what}: brakeG");
            Finite(r.tractionG, $"{what}: tractionG");
            Assert(r.maxJointVelocity > 0f, $"{what}: maxJointVelocity must stay positive or the wheels can't turn");
        }

        // Unity reports gravity as NEGATIVE y, and callers pass Physics.gravity.y straight in.
        // Sign errors here would silently invert the whole traction budget.
        // Same arguments as Shipped() apart from the sign, so this tests the sign and nothing else.
        DrivetrainTuning.Result down = DrivetrainTuning.Compute(Mass, Radius, Wheels, Rpm, Mu, -G,
            DrivetrainTuning.DefaultDriveForceTractionMultiple, DrivetrainTuning.DefaultRollingResistance);
        Near(down.stallTorque, Shipped().stallTorque, 1e-3f, "gravity's sign must not change the tune");
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
        if (!AssetDatabase.IsValidFolder(RobotsFolder))
            throw new InvalidOperationException($"{RobotsFolder} is missing — robot prefabs moved?");

        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { RobotsFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            RobotMotorController motor = prefab != null ? prefab.GetComponent<RobotMotorController>() : null;
            if (motor == null) continue; // not a drivable robot

            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            Assert(motor.autoTuneDrive,
                $"'{name}' has Auto Tune Drive switched off, so it falls back to the serialized " +
                $"{motor.wheelStallTorque}/{motor.velocityDriveDamping} — the on/off-switch drivetrain. " +
                "Turn it back on unless this robot is deliberately hand-tuned.");

            var wheels = new List<ArticulationBody>();
            if (motor.leftWheels != null) foreach (ArticulationBody w in motor.leftWheels) if (w != null) wheels.Add(w);
            if (motor.rightWheels != null) foreach (ArticulationBody w in motor.rightWheels) if (w != null) wheels.Add(w);
            Assert(wheels.Count > 0, $"'{name}' has no wheels wired to its RobotMotorController");

            float radius = DrivetrainTuning.MeasureWheelRadius(wheels);
            DrivetrainTuning.Result expected = DrivetrainTuning.Compute(
                DrivetrainTuning.MeasureTotalMass(prefab.GetComponent<ArticulationBody>()),
                radius,
                wheels.Count,
                motor.maxWheelRpm,
                DrivetrainTuning.MeasureFriction(wheels),
                Physics.gravity.y,
                motor.driveForceTractionMultiple,
                motor.wheelRollingResistanceCrr,
                motor.brakeTractionFraction);

            // The two design rules, checked against each REAL robot rather than one hand-written
            // configuration — a default that's fine for the 654V can still seize a robot with
            // different wheels or mass, and this is the only place that would notice.

            // A wheel must be able to break traction, or a 6-wheel robot with isotropic
            // (non-omni-modelled) wheels cannot complete a point turn. Measured, not theorised:
            // at 1.0x traction PhysicsSmokeTest yawed 0.1 degrees instead of 80.
            float slipThreshold = expected.tractionForce * radius / wheels.Count;
            Assert(expected.stallTorque > slipThreshold,
                $"'{name}': per-wheel stall torque ({expected.stallTorque:0.#}) must exceed the slip " +
                $"threshold ({slipThreshold:0.#}) or the drivetrain seizes and the robot cannot turn. " +
                $"Raise Drive Force Traction Multiple (currently {motor.driveForceTractionMultiple}).");

            // ...but not so far above that the entire stick range is traction-saturated, which is
            // the "throttle is an on/off switch" complaint expressed in numbers.
            Assert(expected.motorLimitedStick > 0.15f,
                $"'{name}': only {expected.motorLimitedStick:P0} of stick travel is motor-limited, so " +
                "there is almost no fine control left. Lower Drive Force Traction Multiple " +
                $"(currently {motor.driveForceTractionMultiple}).");

            foreach (ArticulationBody wheel in wheels)
            {
                ArticulationDrive d = wheel.xDrive;
                Assert(Mathf.Abs(d.forceLimit - expected.stallTorque) < Mathf.Max(expected.stallTorque * 0.02f, 0.01f),
                    $"'{name}' wheel '{wheel.name}' has a stale serialized forceLimit " +
                    $"({d.forceLimit:0.##}, expected {expected.stallTorque:0.##}). Run " +
                    "Tools > RoboSim > Robot > Advanced > Apply Drive Tuning (All Prefabs).");
                Assert(Mathf.Abs(d.damping - expected.damping) < Mathf.Max(expected.damping * 0.02f, 0.01f),
                    $"'{name}' wheel '{wheel.name}' has a stale serialized damping " +
                    $"({d.damping:0.###}, expected {expected.damping:0.###}). Run " +
                    "Tools > RoboSim > Robot > Advanced > Apply Drive Tuning (All Prefabs).");
            }
            checked_ += 4 + wheels.Count * 2;
        }

        if (checked_ == 0)
            throw new InvalidOperationException(
                $"No robot prefabs with a RobotMotorController under {RobotsFolder} — nothing was checked.");
        return checked_;
    }

    // --- helpers ---

    private static DrivetrainTuning.Result Shipped() => DrivetrainTuning.Compute(
        Mass, Radius, Wheels, Rpm, Mu, G,
        DrivetrainTuning.DefaultDriveForceTractionMultiple, DrivetrainTuning.DefaultRollingResistance);

    private static void Near(float actual, float expected, float tolerance, string why)
    {
        if (float.IsNaN(actual) || Mathf.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException($"{why} — expected {expected}, got {actual}");
    }

    private static void Finite(float value, string what)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
            throw new InvalidOperationException($"{what} must be finite and non-negative, got {value}");
    }

    private static void Assert(bool condition, string why)
    {
        if (!condition) throw new InvalidOperationException(why);
    }
}
