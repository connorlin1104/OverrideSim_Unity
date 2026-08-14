using UnityEditor;
using UnityEngine;

// TEMPORARY. Sweeps RobotMotorController.backDriveTractionMultiple and prints what each value costs,
// so the shipped default is read off a curve instead of guessed at.
//
// The two ends are already measured and both are wrong. At 3 (no cap, what shipped) the inner wheel
// of a moving turn is handed three times the tyres' grip to enforce a dead stop the robot's momentum
// is fighting, and it locks: 654V_v3 goes from 28 wheel direction changes spinning on the spot to
// 75 with throttle added, and 654V_v2's mean slip goes 2.79 -> 6.73 u/s. At 1 — exactly the traction
// limit — the chatter vanishes completely and so does the turn: v1, v2 and v3 all yaw ZERO degrees
// while travelling at 13.7 u/s, because the sim models omni wheels as isotropic spheres and yawing
// therefore has to break lateral grip on every tyre at once (the same fact that makes
// DefaultDriveForceTractionMultiple 3 in the first place).
//
// So the question is not "cap or not", it is where between them the robot still turns without the
// inner wheel locking, and whether such a point exists at all. Delete this once that is answered.
public static class TurnAuthoritySweepProbe
{
    private const int SettleSteps = 60;
    private const int AccelSteps = 150;
    private const int TurnSteps = 200;
    private const float WheelRateNoiseFloor = 30f;

    private static readonly float[] Multiples = { 3f, 2.5f, 2f, 1.75f, 1.5f, 1.25f, 1f };

    [MenuItem("Tools/RoboSim/Validate/Probes/Turn Authority Sweep", false, 70)]
    public static void Probe() => ValidationUtil.RunInteractive("Turn Authority Sweep", Run);

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Turn Authority Sweep", Run);

    private static string Run()
    {
        var lines = new System.Text.StringBuilder();

        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;

            lines.AppendLine($"'{prefab.name}':");

            // The control: spinning on the spot, where the report says it is smooth. Unaffected by
            // the cap (nothing is back-driven from a standstill) but measured at every multiple
            // anyway, because a control that is assumed rather than measured is not a control.
            foreach (float multiple in Multiples)
            {
                Turn standing = Measure(prefab, multiple, accelFirst: false, throttleInTurn: 0f);
                Turn moving = Measure(prefab, multiple, accelFirst: true, throttleInTurn: 1f);
                lines.AppendLine(
                    $"    x{multiple:0.00}   standing {standing.wheelReversals,3} changes / slip " +
                    $"{standing.meanAbsSlip:0.00} / yaw {standing.yawDeg,4:0}   ·   moving " +
                    $"{moving.wheelReversals,3} changes / slip {moving.meanAbsSlip:0.00} / yaw " +
                    $"{moving.yawDeg,4:0} at {moving.meanSpeed:0.0} u/s");
            }
        }

        return "Turn authority sweep (backDriveTractionMultiple):\n" + lines.ToString().TrimEnd();
    }

    private struct Turn
    {
        public int wheelReversals;
        public float meanAbsSlip;
        public float yawDeg;
        public float meanSpeed;
    }

    private static Turn Measure(GameObject prefab, float multiple, bool accelFirst, float throttleInTurn)
    {
        SimulationMode previousMode = Physics.simulationMode;
        try
        {
            ArticulationBody root = ValidationUtil.SpawnOnBareFloor(prefab, out RobotMotorController motor);
            motor.backDriveTractionMultiple = multiple;   // before Initialise, which derives the torque
            motor.Initialise();

            Physics.simulationMode = SimulationMode.Script;
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);
            TipOverValidation.RaiseLifts(root, motor);
            TipOverValidation.StepDriven(motor, 0f, 0f, SettleSteps);
            if (accelFirst) TipOverValidation.StepDriven(motor, 1f, 0f, AccelSteps);

            ArticulationBody[] wheels = PhysicsSmokeTest.FindWheels(root, out _, out _);
            float radius = DrivetrainTuning.MeasureWheelRadius(wheels);
            var lastSpin = new float[wheels.Length];
            for (int w = 0; w < wheels.Length; w++) lastSpin[w] = Spin(wheels[w]);

            var result = new Turn();
            float lastYaw = root.transform.eulerAngles.y;
            float slipSum = 0f, speedSum = 0f;
            int slipSamples = 0;

            for (int i = 0; i < TurnSteps; i++)
            {
                TipOverValidation.StepDriven(motor, throttleInTurn, 1f, 1);
                Vector3 rollAxis = Vector3.ProjectOnPlane(root.transform.forward, Vector3.up).normalized;

                speedSum += new Vector2(root.linearVelocity.x, root.linearVelocity.z).magnitude;
                float yawNow = root.transform.eulerAngles.y;
                result.yawDeg += Mathf.Abs(Mathf.DeltaAngle(lastYaw, yawNow));
                lastYaw = yawNow;

                for (int w = 0; w < wheels.Length; w++)
                {
                    if (wheels[w] == null) continue;
                    float spin = Spin(wheels[w]);
                    float rate = (spin - lastSpin[w]) / ValidationUtil.StepSeconds;
                    if (i > 0 && Mathf.Sign(spin) != Mathf.Sign(lastSpin[w])
                        && Mathf.Abs(rate) > WheelRateNoiseFloor) result.wheelReversals++;
                    lastSpin[w] = spin;

                    float surface = spin * Mathf.Deg2Rad * radius;
                    Vector3 contact = wheels[w].transform.position - Vector3.up * radius;
                    slipSum += Mathf.Abs(surface - Vector3.Dot(root.GetPointVelocity(contact), rollAxis));
                    slipSamples++;
                }
            }

            result.meanAbsSlip = slipSamples > 0 ? slipSum / slipSamples : 0f;
            result.meanSpeed = speedSum / TurnSteps;
            return result;
        }
        finally { Physics.simulationMode = previousMode; }
    }

    private static float Spin(ArticulationBody wheel)
        => wheel != null && wheel.jointVelocity.dofCount > 0
            ? wheel.jointVelocity[0] * Mathf.Rad2Deg : 0f;
}
