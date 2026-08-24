using UnityEditor;
using UnityEngine;

// TEMPORARY. Prints, for every robot, how each drive wheel sits on the floor once the robot has
// settled, and how freely it spins under full throttle.
//
// Written to settle a report that had two plausible explanations and no measurement behind either:
// "only the 4 corner wheels spin continuously, the middle two are like spinning only when joystick
// is held". A wheel that is off the ground has nothing to slow it, so it tracks the drive target
// exactly and looks like it is spinning for free; a wheel carrying load is dragged below the target
// by the very traction that makes it useful. So "spins continuously" and "spins only under load"
// are the two halves of ONE measurement — the gap between the wheel and the floor — and this prints
// it rather than reasoning about it.
//
// Delete once that question is closed. Nothing asserts here on purpose: this reports, and the
// assertions that came out of it live in DrivetrainRigValidation.
public static class WheelGroundContactProbe
{
    private const int SettleSteps = 300;   // 3 s — long enough for a robot to find its resting tilt
    private const int DriveSteps = 100;    // 1 s of full throttle
    private const float FloorTopY = 0f;    // ValidationUtil's rig floor: a box whose top face is y=0

    // A wheel this far off the floor is not carrying anything. Sized well under a wheel radius so it
    // cannot be confused with "resting", and well over solver penetration, which is sub-millimetre.
    private const float AirborneGap = 0.02f;

    [MenuItem("Tools/RoboSim/Validate/Probes/Wheel Ground Contact", false, 72)]
    public static void Probe() => ValidationUtil.RunInteractive("Wheel Ground Contact", Run);

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Wheel Ground Contact", Run);

    private static string Run()
    {
        var lines = new System.Text.StringBuilder();

        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;
            lines.AppendLine(Measure(prefab));
        }

        return "Wheel ground contact (settled on a bare floor, then 1 s at full throttle):\n"
               + lines.ToString().TrimEnd();
    }

    private static string Measure(GameObject prefab)
    {
        var lines = new System.Text.StringBuilder();
        SimulationMode previous = Physics.simulationMode;
        try
        {
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
            motor.Initialise();
            Physics.simulationMode = SimulationMode.Script;

            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);

            ArticulationBody[] wheels = RobotPhysicsValidation.FindWheels(root, out ArticulationBody[] left,
                out ArticulationBody[] right);

            // Resting attitude, about the axes the drivetrain actually uses. A robot short a contact
            // point settles onto the ones it has left, which is a TILT, not a translation — so the
            // interesting number is the angle, and its sign says which corner dropped.
            Vector3 forward = Vector3.ProjectOnPlane(root.transform.forward, Vector3.up).normalized;
            Vector3 across = Vector3.Cross(Vector3.up, forward);
            float roll = Vector3.SignedAngle(Vector3.up,
                Vector3.ProjectOnPlane(root.transform.up, forward), forward);
            float pitch = Vector3.SignedAngle(Vector3.up,
                Vector3.ProjectOnPlane(root.transform.up, across), across);

            lines.AppendLine($"'{prefab.name}': {left.Length} left / {right.Length} right wheels, " +
                             $"settled at roll {roll:+0.00;-0.00} deg, pitch {pitch:+0.00;-0.00} deg");

            // Gap per wheel, measured off the wheel's own sphere rather than a fitted radius — the
            // sphere IS the contact shape, so its lowest point is the only honest "how far off the
            // ground is this".
            var gaps = new float[wheels.Length];
            for (int i = 0; i < wheels.Length; i++) gaps[i] = GapToFloor(wheels[i]);

            // Then drive. An airborne wheel has nothing to load it, so it reaches the commanded speed
            // and stays there; a wheel with weight on it is held below the target by traction.
            var before = new float[wheels.Length];
            for (int i = 0; i < wheels.Length; i++) before[i] = Spin(wheels[i]);
            var spinSum = new float[wheels.Length];
            for (int s = 0; s < DriveSteps; s++)
            {
                TipOverValidation.StepDriven(motor, 1f, 0f, 1);
                for (int i = 0; i < wheels.Length; i++) spinSum[i] += Mathf.Abs(Spin(wheels[i]));
            }

            float commanded = motor.maxWheelRpm * 6f;   // deg/s at full stick
            int airborne = 0;
            for (int i = 0; i < wheels.Length; i++)
            {
                if (wheels[i] == null) continue;
                bool off = gaps[i] > AirborneGap;
                if (off) airborne++;
                float mean = spinSum[i] / DriveSteps;
                lines.AppendLine($"    {wheels[i].name,-24} gap {gaps[i],7:0.0000}{(off ? "  AIRBORNE" : "  on floor")}" +
                                 $"   spun at {(commanded > 0f ? mean / commanded : 0f),6:0.0%} of the commanded speed");
            }
            lines.Append($"    -> {airborne} of {wheels.Length} drive wheel(s) never touched the floor.");
            return lines.ToString();
        }
        finally { Physics.simulationMode = previous; }
    }

    // Lowest point of the wheel's own sphere collider, above the floor's top face. Falls back to the
    // link origin when a wheel has no sphere at all — which is itself the finding, so it is labelled.
    private static float GapToFloor(ArticulationBody wheel)
    {
        if (wheel == null) return float.NaN;
        float lowest = float.PositiveInfinity;
        foreach (SphereCollider sphere in wheel.GetComponentsInChildren<SphereCollider>(true))
        {
            Vector3 centre = sphere.transform.TransformPoint(sphere.center);
            Vector3 lossy = sphere.transform.lossyScale;
            float scale = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Max(Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));
            lowest = Mathf.Min(lowest, centre.y - sphere.radius * scale);
        }
        if (float.IsPositiveInfinity(lowest)) lowest = wheel.transform.position.y;
        return lowest - FloorTopY;
    }

    private static float Spin(ArticulationBody wheel)
        => wheel != null && wheel.jointVelocity.dofCount > 0
            ? wheel.jointVelocity[0] * Mathf.Rad2Deg : 0f;
}
