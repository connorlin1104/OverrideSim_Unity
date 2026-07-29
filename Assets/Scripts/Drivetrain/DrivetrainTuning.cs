using System.Collections.Generic;
using UnityEngine;

// The drivetrain's motor model, in one place.
//
// The same numbers are needed in three: RobotMotorController.Awake (play mode),
// RigDrivetrainArticulation (the EDIT-TIME bake, which is what PhysicsSmokeTest measures — Awake
// never runs in edit mode), and DriveFeelValidation. They used to be three sets of magic constants
// that could drift; now they're all derived here from the robot's own mass, wheel radius, wheel
// count and gearing, so a new robot is correct by construction and the shipped prefabs need no
// hand edits.
//
// WHY THIS EXISTS AT ALL — the drivetrain used to behave like an on/off switch:
//
//   A velocity drive produces torque = damping * (targetVelocity - currentVelocity), clamped to
//   forceLimit. With the old forceLimit 700 / damping 1000, the clamp binds until the speed error
//   drops below 700/1000 = 0.7 deg/s out of a 1440 deg/s free speed — i.e. it was a bang-bang
//   torque source for 99.95% of every acceleration, at ANY stick position. Half stick accelerated
//   exactly as hard as full stick, which is what "too sensitive / clunky" meant.
//
//   Setting damping = stallTorque / freeSpeed makes torque fall linearly to zero AT free speed:
//   the drive becomes the real DC motor curve T(w) = T_stall * (1 - w/w_free). Now half stick
//   commands half the target speed and therefore half the standstill torque, so acceleration is
//   proportional to how far the stick has moved.
//
//   And with 6 wheels at 700 torque on a 0.37 radius the old drive could push 11,356 force units
//   against a traction cap of mu*m*g = 2,354 — nearly 5x more motor than the tyres can put down,
//   so every start broke the tyres loose. Peak force is now chosen as a FRACTION of that traction
//   budget, which scales correctly for any robot's mass, wheel count and wheel size.
//
// UNITS — the one thing to get right here. A ROTATIONAL ArticulationDrive speaks DEGREES: its
// targetVelocity and the velocity it is differenced against are both deg/s, so `damping` is torque
// per (deg/s). Joint limits and maxJointVelocity speak RADIANS. Deriving damping from a rad/s free
// speed makes it 57.3x too large, which silently restores the bang-bang drive this whole file
// exists to remove — see the deg-vs-rad note in the ArticulationBody rules.
//
// The rest is the project's 10x world: 1 unit = 100 mm, gravity 98.1, 100 Hz fixed timestep.
public static class DrivetrainTuning
{
    // Stall force at full stick, as a MULTIPLE of the tyres' grip (mu*m*g).
    //
    // Above 1 on purpose, and this is the part that looks wrong until you know why: the sim models
    // omni wheels as plain isotropic spheres at mu 0.8, so they grip sideways exactly as hard as
    // they grip forwards. A real omni's rollers make a point turn nearly free; here every wheel has
    // to be SCRUBBED sideways, and for a 6-wheel layout spread front-to-back the resisting moment
    // is larger than the moment a traction-limited drive can produce. At or below 1.0 the robot
    // physically cannot turn — measured, not theorised: PhysicsSmokeTest yawed 0.1 degrees.
    //
    // So the drivetrain must be able to break traction, exactly as a real one can. What makes this
    // NOT the old on/off throttle is the damping above, which the original 700/1000 pairing got
    // wrong by ~57x.
    //
    // Why 3 specifically: it puts the traction crossover at one third of stick travel. Below a
    // third the drive is motor-limited, so acceleration is proportional to the stick and fine
    // control is real; above it the robot uses everything the tyres have. Fine at the bottom, full
    // authority at the top, which is how a real drivetrain behaves. See Result.motorLimitedStick.
    public const float DefaultDriveForceTractionMultiple = 3.0f;

    // Braking authority as a fraction of the tyres' grip, used whenever the command opposes (or
    // trails) the wheels' spin — a hard reversal, and, with centre-stick as the brake pedal, every
    // release of the sticks.
    //
    // This is the braking quadrant, and a real motor is weakest there: driven backwards against
    // its own rotation it is limited by its current limit and its own back-EMF, nowhere near the
    // stall torque it makes when accelerating. The sim used to give a stop the FULL
    // driveForceTractionMultiple (3x the tyres' grip), so the tyres simply slipped and the robot
    // stopped at the friction limit — 0.8 g, in ~0.12 s, with no sense of carrying any momentum.
    //
    // Below 1.0 on purpose, so the MOTOR is the binding constraint on a stop instead of the
    // ground: the force builds progressively with the command rather than saturating instantly,
    // and the robot's own inertia is what the driver feels. 0.7 gives ~0.56 g, comfortably inside
    // the 0.8 g friction cone.
    public const float DefaultBrakeTractionFraction = 0.7f;

    // Used when a robot's colliders/materials can't be measured (a robot rigged before
    // GeneratePartColliders, or a unit test with no scene). These are the 654V numbers.
    public const float FallbackWheelRadius = 0.37f;
    public const float FallbackFriction = 0.8f;
    public const float FallbackTotalMass = 30f;

    public struct Result
    {
        public float stallTorque;       // ArticulationDrive.forceLimit, per wheel
        public float damping;           // velocity-tracking gain == stallTorque / freeSpeed
        public float brakeTorque;       // forceLimit while the command opposes/trails the spin
        public float maxJointVelocity;  // rad/s

        // Diagnostics — not applied to anything, but they're what the validator asserts on and
        // what the rig tool reports, so a bad tune is visible instead of just feeling wrong.
        public float peakForce;
        public float tractionForce;
        public float topSpeed;

        // Motor-model time to 95% of top speed. Only exact below motorLimitedStick; past that the
        // tyres are the limit, not the motor, and the real robot gets there sooner.
        public float secondsTo95;

        // How far the stick can travel before the drive asks for more force than the tyres can
        // deliver. Below this, acceleration is proportional to the stick; above it, everything
        // accelerates the same and only the target SPEED still scales. This is the number that
        // says how much fine control a driver actually has.
        public float motorLimitedStick;

        // Braking deceleration as a multiple of g, and the friction cone it has to stay inside.
        // brakeG < tractionG is the invariant that keeps a stop motor-limited (progressive)
        // instead of traction-limited (an instant skid).
        public float brakeG;
        public float tractionG;
    }

    public static Result Compute(float totalMass, float wheelRadius, int wheelCount,
        float maxWheelRpm, float friction, float gravity,
        float driveForceTractionMultiple, float brakeTractionFraction = DefaultBrakeTractionFraction)
    {
        // Everything is clamped rather than guarded-and-returned: a half-rigged robot must still
        // produce finite, non-negative values, because these go straight into PhysX and a NaN
        // forceLimit takes the whole articulation with it.
        int wheels = Mathf.Max(wheelCount, 1);
        float mass = Mathf.Max(totalMass, 0f);
        float radius = Mathf.Max(wheelRadius, 0f);
        float mu = Mathf.Max(friction, 0f);
        float g = Mathf.Abs(gravity);
        float multiple = Mathf.Max(driveForceTractionMultiple, 0f);
        float freeSpeed = Mathf.Max(maxWheelRpm * Mathf.PI * 2f / 60f, 0.01f); // rad/s — joint limits
        float freeSpeedDeg = Mathf.Max(maxWheelRpm * 6f, 0.01f);               // deg/s — drive targets
        float brakeFraction = Mathf.Max(brakeTractionFraction, 0f);

        Result r = default;
        r.topSpeed = freeSpeed * radius;
        r.tractionForce = mu * mass * g;

        // Floor the peak force so a zero-mass or frictionless robot still gets a drive that turns
        // the wheels at all, instead of a silently dead drivetrain.
        r.peakForce = Mathf.Max(r.tractionForce * multiple, 1e-3f);
        r.motorLimitedStick = Mathf.Clamp01(r.tractionForce / r.peakForce);

        r.stallTorque = r.peakForce * radius / wheels;

        // The line that makes it a motor instead of a switch: torque reaches exactly zero at free
        // speed, so it falls off linearly on the way there.
        //
        // freeSpeedDEG, not freeSpeed: a rotational drive differences its targetVelocity against
        // the joint velocity in DEGREES per second, so damping is torque per (deg/s). Using the
        // rad/s figure here makes damping 57.3x too big, the force limit saturates over almost the
        // whole speed range, and the drive is a switch again — with no symptom except that it
        // "feels wrong".
        r.damping = r.stallTorque / freeSpeedDeg;

        // Braking quadrant: what the drive may pull when the command opposes or trails the
        // wheel's current spin — which, with centre-stick as the brake pedal, is also every stop.
        // Sized as a fraction of the tyres' grip so the motor, not the ground, is what limits it
        // — see DefaultBrakeTractionFraction. Never above stall torque: a motor cannot brake
        // harder than it can drive.
        r.brakeTorque = Mathf.Min(r.tractionForce * brakeFraction * radius / wheels, r.stallTorque);

        r.tractionG = g > 1e-6f && mass > 0f ? r.tractionForce / (mass * g) : 0f;
        r.brakeG = g > 1e-6f && mass > 0f && radius > 0f
            ? r.brakeTorque * wheels / (radius * mass * g) : 0f;

        // Headroom above free speed so a coasting or back-driven wheel isn't clamped by the joint
        // limit (which would read as an invisible brake).
        r.maxJointVelocity = freeSpeed * 1.25f;

        // m*dv/dt = F_peak*(1 - v/v_max) is first-order with tau = m*v_max/F_peak, so 95% of top
        // speed lands at ln(20)*tau.
        float tau = mass * r.topSpeed / r.peakForce;
        r.secondsTo95 = Mathf.Log(20f) * tau;

        return r;
    }

    // --- Measuring the robot -------------------------------------------------------------------

    // Total mass of the whole articulation, not just root + wheels: a robot with a lift, claw and
    // intake carries real mass in those links, and the traction budget is mu * TOTAL weight.
    public static float MeasureTotalMass(ArticulationBody root)
    {
        if (root == null) return FallbackTotalMass;
        float total = 0f;
        foreach (ArticulationBody body in root.GetComponentsInChildren<ArticulationBody>(true))
        {
            if (body != null) total += body.mass;
        }
        return total > 0f ? total : FallbackTotalMass;
    }

    // Average wheel radius in WORLD units.
    //
    // GeneratePartColliders stores a LOCAL radius (it divides the measured world radius by the
    // node's max |lossyScale| so PhysX scales it back — see the comment there), so reading
    // SphereCollider.radius alone gives 0.0439 on a 10x-scaled node instead of 0.4395. Scaling
    // back up here is not optional.
    public static float MeasureWheelRadius(IEnumerable<ArticulationBody> wheels)
    {
        if (wheels == null) return FallbackWheelRadius;
        float sum = 0f;
        int found = 0;
        foreach (ArticulationBody wheel in wheels)
        {
            if (wheel == null) continue;
            float best = 0f;
            foreach (SphereCollider sphere in wheel.GetComponentsInChildren<SphereCollider>(true))
            {
                if (sphere == null) continue;
                Vector3 lossy = sphere.transform.lossyScale;
                float scale = Mathf.Max(Mathf.Abs(lossy.x),
                    Mathf.Max(Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));
                best = Mathf.Max(best, sphere.radius * scale);
            }
            if (best > 0f) { sum += best; found++; }
        }
        return found > 0 ? sum / found : FallbackWheelRadius;
    }

    // Average wheel dynamic friction.
    //
    // WheelPhysics uses PhysicsMaterialCombine.Maximum, which outranks the field floor's
    // Minimum/Multiply, so the wheel material's own value IS the effective coefficient — no need
    // to find and combine with whatever surface the robot happens to be standing on.
    public static float MeasureFriction(IEnumerable<ArticulationBody> wheels)
    {
        if (wheels == null) return FallbackFriction;
        float sum = 0f;
        int found = 0;
        foreach (ArticulationBody wheel in wheels)
        {
            if (wheel == null) continue;
            foreach (Collider collider in wheel.GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || collider.sharedMaterial == null) continue;
                sum += collider.sharedMaterial.dynamicFriction;
                found++;
                break; // one material per wheel; a wheel has exactly one sphere
            }
        }
        return found > 0 && sum > 0f ? sum / found : FallbackFriction;
    }

    // --- Centre of mass -------------------------------------------------------------------------

    // The whole articulation's centre of mass, in world space, mass-weighted across every link.
    //
    // Every shipped link serializes m_ImplicitCom: 1 (automaticCenterOfMass), so PhysX derives
    // each link's own COM from its colliders and this aggregate is the robot's true physical
    // centre — it rises when a lift extends, in proportion to how much of the robot's mass is
    // actually on the lift. Nothing in the project read it before; the chase camera now aims at
    // it, because unlike a collider bounding box it can't be dragged around by a long intake or
    // frozen at whatever pose the robot happened to be in.
    //
    // RUNTIME ONLY. worldCenterOfMass is PhysX state: it is meaningless in edit mode and may be
    // unpopulated on the very first frame after Instantiate, hence the bool return rather than a
    // silent zero. Pass a CACHED array — GetComponentsInChildren on a robot walks thousands of
    // transforms and allocates.
    public static bool TryMeasureCompositeCom(IEnumerable<ArticulationBody> bodies, out Vector3 com)
    {
        com = Vector3.zero;
        if (bodies == null) return false;

        Vector3 weighted = Vector3.zero;
        float total = 0f;
        foreach (ArticulationBody body in bodies)
        {
            if (body == null) continue;
            float m = body.mass;
            if (m <= 0f) continue;
            weighted += body.worldCenterOfMass * m;
            total += m;
        }
        if (total <= 0f) return false;

        com = weighted / total;
        // A single NaN link mass would poison the whole sum and then the camera transform.
        return !float.IsNaN(com.x) && !float.IsNaN(com.y) && !float.IsNaN(com.z);
    }
}
