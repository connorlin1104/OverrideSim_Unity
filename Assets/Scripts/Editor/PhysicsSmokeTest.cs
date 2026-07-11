using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

// Headless validation of the ArticulationBody-rigged robot, without entering play mode.
//
// Uses edit-mode scripted physics (Physics.simulationMode = Script + Physics.Simulate) to run
// three checks against the rigged robot:
//   - Settle: 2 s of gravity — the robot must not explode, fall through the floor, or slide.
//   - Drive:  all wheels at +2160 deg/s (360 RPM) for 2 s — the robot must translate and the
//             wheels must actually spin. Which direction it went (vs. wrapper.forward) is
//             LOGGED, not failed: a backwards result just means the motor controller needs
//             its invert flags set.
//   - Turn:   left side +2160, right side -2160 for 2 s — the robot must yaw.
//
// IMPORTANT: edit-mode simulation never runs MonoBehaviours (no Awake/FixedUpdate), so this
// drives the wheel joints' xDrives directly instead of going through RobotMotorController —
// the rig tool already baked the drive parameters (velocity drive, damping, forceLimit) into
// the serialized ArticulationBodies.
//
// The simulation mutates the open scene; it is ALWAYS reloaded from disk afterwards so the
// simulated poses are never saved.
//
// Usage: Tools > RoboSim > Robot > Validate Robot Physics (validates the robot in the active scene).
// Batch: -executeMethod PhysicsSmokeTest.RunBatchValidate (opens SampleScene; throws on FAIL,
// which exits the editor nonzero).
public class PhysicsSmokeTest
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string MenuTitle = "Validate Rigged Robot";

    private const float StepSeconds = 0.01f;   // matches the project's fixed timestep
    private const int StepsPerPhase = 200;     // 2 s of simulation per phase
    private const float DriveDegPerSec = 2160f; // 360 RPM in the degrees/s revolute drives use

    // Settle-phase explosion/fall-through thresholds.
    private const float MaxSettleVertical = 1.5f;
    private const float MaxSettleHorizontal = 2f;

    // Drive/turn-phase minimums.
    private const float MinDrivePlanar = 2f;
    private const float MinWheelSpinRad = Mathf.PI / 2f; // 90° — jointPosition reads in RADIANS
    private const float MinTurnYawDeg = 15f;

    [MenuItem("Tools/RoboSim/Robot/Validate Robot Physics", false, 2)]
    private static void ValidateMenu()
    {
        // The simulation trashes the open scene and we reload it from disk afterwards, so give
        // the user the standard save-or-cancel prompt before touching anything.
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        try
        {
            ValidateActiveScene();
            EditorUtility.DisplayDialog(MenuTitle,
                "All physics smoke tests PASSED (settle, turn, drive).\nSee the Console for details.", "OK");
        }
        catch (System.Exception e)
        {
            EditorUtility.DisplayDialog(MenuTitle, "Validation failed:\n\n" + e.Message, "OK");
        }
    }

    // Batch entry for -executeMethod: opens the main scene and validates. Throws on any FAIL.
    public static void RunBatchValidate()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        ValidateActiveScene();
        Debug.Log("PhysicsSmokeTest: ALL PASS (settle, turn, drive).");
    }

    // Runs all three phases against the rigged robot in the active scene. Throws on FAIL.
    private static void ValidateActiveScene()
    {
        ValidateBody(FindRiggedRobotRoot());
    }

    // Validates one specific robot, for callers that just built it (Set Up Imported Robot) and
    // know which one they mean — FindRiggedRobotRoot would be ambiguous with two robots present.
    //
    // IMPORTANT: the scene is reloaded from disk afterwards to discard the simulated poses, so
    // the CALLER MUST SAVE THE SCENE FIRST or the robot it just built is thrown away.
    public static void ValidateRobot(GameObject robotRoot)
    {
        if (robotRoot == null) throw new System.ArgumentNullException(nameof(robotRoot));

        ArticulationBody body = robotRoot.GetComponent<ArticulationBody>()
                                ?? robotRoot.GetComponentInChildren<ArticulationBody>(true);
        if (body == null)
            throw new System.InvalidOperationException(
                $"PhysicsSmokeTest: '{robotRoot.name}' has no ArticulationBody — it is not a rigged robot.");

        ValidateBody(body);
    }

    private static void ValidateBody(ArticulationBody root)
    {
        string scenePath = root.gameObject.scene.path;
        if (string.IsNullOrEmpty(scenePath))
            throw new System.InvalidOperationException(
                "PhysicsSmokeTest: the scene must be saved to disk — it is reloaded afterwards to discard the simulated state.");

        SimulationMode previousMode = Physics.simulationMode;
        try
        {
            ArticulationBody[] wheels = FindWheels(root, out ArticulationBody[] leftWheels, out ArticulationBody[] rightWheels);
            Debug.Log($"PhysicsSmokeTest: validating '{root.name}' — {leftWheels.Length} left / {rightWheels.Length} right wheel links.");

            Physics.simulationMode = SimulationMode.Script;

            // Turn BEFORE drive: the turn is measured in place at the clean settled start pose.
            // Driving first sent the robot ~12 units across the populated field, and what it
            // plowed into decided whether the turn could happen at all — with the funnel's
            // concave colliders actually catching pieces now, the robot ended the drive wedged
            // on one and the turn assert failed for environmental (not rig) reasons.
            RunSettleTest(root);
            RunTurnTest(root, leftWheels, rightWheels);
            RunDriveTest(root, wheels);
        }
        finally
        {
            Physics.simulationMode = previousMode;
            // Discard everything the simulation moved by reloading the scene from disk.
            // Never save here — the simulated poses must not leak into the asset.
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        }
    }

    // --- Phases ---------------------------------------------------------------------------

    private static void RunSettleTest(ArticulationBody root)
    {
        Vector3 startPos = root.transform.position;

        Step(StepsPerPhase);

        FailIfNotFinite();
        Vector3 delta = root.transform.position - startPos;
        float vertical = Mathf.Abs(delta.y);
        float horizontal = Planar(delta);
        if (vertical > MaxSettleVertical)
            Fail($"settle: root moved {vertical:F2} units vertically (limit {MaxSettleVertical}) — fell through the floor or exploded.");
        if (horizontal > MaxSettleHorizontal)
            Fail($"settle: root slid {horizontal:F2} units horizontally (limit {MaxSettleHorizontal}) — unstable rig.");

        Debug.Log($"PhysicsSmokeTest PASS settle: vertical {vertical:F3}, horizontal {horizontal:F3} after {StepsPerPhase} steps.");
        LogSupportDiagnostics(root);
    }

    // What is the robot actually standing on? Logs each wheel sphere's lowest point and the
    // five lowest non-wheel colliders — if a chassis box bottoms out at or below the wheel
    // spheres, the wheels are unloaded and can only spin in place.
    private static void LogSupportDiagnostics(ArticulationBody root)
    {
        var wheelBottoms = new List<string>();
        float lowestWheelY = float.PositiveInfinity;
        foreach (ArticulationBody ab in root.GetComponentsInChildren<ArticulationBody>())
        {
            if (ab.isRoot || ab.jointType != ArticulationJointType.RevoluteJoint) continue;
            foreach (Collider c in ab.GetComponentsInChildren<Collider>())
            {
                lowestWheelY = Mathf.Min(lowestWheelY, c.bounds.min.y);
                wheelBottoms.Add($"{ab.name}:{c.GetType().Name} bottom {c.bounds.min.y:F3}");
            }
        }

        var chassis = new List<(float y, string desc)>();
        foreach (Collider c in root.GetComponentsInChildren<Collider>())
        {
            if (c.GetComponentInParent<ArticulationBody>().isRoot)
                chassis.Add((c.bounds.min.y, $"{c.transform.parent?.name}/{c.name} bottom {c.bounds.min.y:F3}"));
        }
        chassis.Sort((a, b) => a.y.CompareTo(b.y));

        Debug.Log("PhysicsSmokeTest wheels: " + string.Join("; ", wheelBottoms));
        Debug.Log($"PhysicsSmokeTest lowest wheel point {lowestWheelY:F3}; lowest chassis colliders: " +
                  string.Join("; ", chassis.GetRange(0, Mathf.Min(5, chassis.Count)).ConvertAll(t => t.desc)));
    }

    private static void RunDriveTest(ArticulationBody root, ArticulationBody[] wheels)
    {
        Vector3 startPos = root.transform.position;
        Vector3 forward = root.transform.forward; // wrapper.forward at drive start

        float[] jointStart = new float[wheels.Length];
        for (int i = 0; i < wheels.Length; i++) jointStart[i] = JointPositionRad(wheels[i]);

        // Same sign on BOTH sides: the rig aligns every axle to the wrapper's +X, so positive
        // rotation drives both sides the same direction (see RobotMotorController's header).
        foreach (ArticulationBody wheel in wheels)
            wheel.SetDriveTargetVelocity(ArticulationDriveAxis.X, DriveDegPerSec);

        Step(StepsPerPhase);

        float maxSpinRad = 0f;
        for (int i = 0; i < wheels.Length; i++)
            maxSpinRad = Mathf.Max(maxSpinRad, Mathf.Abs(JointPositionRad(wheels[i]) - jointStart[i]));

        Vector3 delta = root.transform.position - startPos;
        float planar = Planar(delta);

        // Sentinel BEFORE the pass/fail checks: if literally nothing responded, edit-mode
        // scripted simulation doesn't support articulation drives in this editor version and
        // the caller must pivot to play-mode testing (exact string is matched externally).
        if (maxSpinRad < 1e-4f && delta.magnitude < 1e-3f)
            throw new System.Exception(
                "EDITMODE_SIM_UNSUPPORTED — articulation drives did not respond to edit-mode Physics.Simulate");

        // Direction is informational: backwards just means the invert flags need setting.
        float forwardDot = Vector3.Dot(delta, forward);
        if (forwardDot >= 0f)
            Debug.Log($"PhysicsSmokeTest drive direction: FORWARD along wrapper.forward (dot {forwardDot:F2}).");
        else
            Debug.LogWarning($"PhysicsSmokeTest drive direction: BACKWARD along wrapper.forward (dot {forwardDot:F2}) — " +
                             "set invertLeft AND invertRight on RobotMotorController. (WARN, not FAIL.)");

        FailIfNotFinite();
        if (planar <= MinDrivePlanar)
        {
            var spins = new List<string>();
            for (int i = 0; i < wheels.Length; i++)
                spins.Add($"{wheels[i].name} spun {(JointPositionRad(wheels[i]) - jointStart[i]) * Mathf.Rad2Deg:F0}°, " +
                          $"vel {(wheels[i].jointVelocity.dofCount > 0 ? wheels[i].jointVelocity[0] : 0f):F1} rad/s, " +
                          $"worldAngVel {wheels[i].angularVelocity.ToString("F1")}");
            Debug.Log("PhysicsSmokeTest drive diagnostics: " + string.Join("; ", spins));
            Debug.Log($"PhysicsSmokeTest drive diagnostics: root delta {delta.ToString("F4")}, " +
                      $"root linVel {root.linearVelocity.ToString("F3")}, root angVel {root.angularVelocity.ToString("F3")}");
            Fail($"drive: planar displacement {planar:F2} units (need > {MinDrivePlanar}) — wheels are not propelling the robot.");
        }
        if (maxSpinRad <= MinWheelSpinRad)
            Fail($"drive: max wheel spin {maxSpinRad * Mathf.Rad2Deg:F1}° (need > 90°) — joints are not rotating.");

        Debug.Log($"PhysicsSmokeTest PASS drive: planar {planar:F2} units, max wheel spin {maxSpinRad * Mathf.Rad2Deg:F0}°.");
    }

    private static void RunTurnTest(ArticulationBody root, ArticulationBody[] leftWheels, ArticulationBody[] rightWheels)
    {
        float yawStart = root.transform.eulerAngles.y;

        foreach (ArticulationBody wheel in leftWheels)
            wheel.SetDriveTargetVelocity(ArticulationDriveAxis.X, DriveDegPerSec);
        foreach (ArticulationBody wheel in rightWheels)
            wheel.SetDriveTargetVelocity(ArticulationDriveAxis.X, -DriveDegPerSec);

        Step(StepsPerPhase);

        FailIfNotFinite();
        float yawDelta = Mathf.DeltaAngle(yawStart, root.transform.eulerAngles.y);
        if (Mathf.Abs(yawDelta) <= MinTurnYawDeg)
            Fail($"turn: yaw changed {yawDelta:F1}° (need |change| > {MinTurnYawDeg}°) — differential drive is not turning the robot.");

        Debug.Log($"PhysicsSmokeTest PASS turn: yaw changed {yawDelta:F1}°.");
    }

    // --- Helpers --------------------------------------------------------------------------

    private static void Step(int steps)
    {
        for (int i = 0; i < steps; i++) Physics.Simulate(StepSeconds);
    }

    private static ArticulationBody FindRiggedRobotRoot()
    {
        // Unity 6000.5 deprecated the FindObjectsSortMode overload; FindObjectsInactive.Exclude
        // matches the old SortMode.None behavior (active objects only).
        foreach (ArticulationBody ab in Object.FindObjectsByType<ArticulationBody>(FindObjectsInactive.Exclude))
        {
            if (ab.isRoot && ab.CompareTag("Player")) return ab;
        }
        throw new System.InvalidOperationException(
            "PhysicsSmokeTest: no root ArticulationBody tagged 'Player' in the scene — run Tools > RoboSim > Robot > Advanced > Rig Motors and Wheel Joints first.");
    }

    // All revolute links under the root, split by side (link names carry LS/RS; fall back to
    // the sign of their root-local X, where +X is robot right).
    private static ArticulationBody[] FindWheels(ArticulationBody root, out ArticulationBody[] left, out ArticulationBody[] right)
    {
        List<ArticulationBody> all = new List<ArticulationBody>();
        List<ArticulationBody> leftList = new List<ArticulationBody>();
        List<ArticulationBody> rightList = new List<ArticulationBody>();

        foreach (ArticulationBody ab in root.GetComponentsInChildren<ArticulationBody>())
        {
            if (ab.isRoot || ab.jointType != ArticulationJointType.RevoluteJoint) continue;
            all.Add(ab);

            bool isLeft = ab.name.Contains("LS") ||
                          (!ab.name.Contains("RS") && root.transform.InverseTransformPoint(ab.transform.position).x < 0f);
            (isLeft ? leftList : rightList).Add(ab);
        }

        if (all.Count == 0)
            throw new System.InvalidOperationException(
                "PhysicsSmokeTest: the rigged robot has no revolute wheel links to test.");

        left = leftList.ToArray();
        right = rightList.ToArray();
        return all.ToArray();
    }

    // jointPosition is in reduced coordinates: RADIANS for revolute joints (drives use degrees).
    private static float JointPositionRad(ArticulationBody ab)
    {
        return ab.jointPosition.dofCount > 0 ? ab.jointPosition[0] : 0f;
    }

    private static void FailIfNotFinite()
    {
        foreach (ArticulationBody ab in Object.FindObjectsByType<ArticulationBody>(FindObjectsInactive.Exclude))
        {
            Vector3 p = ab.transform.position;
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
                float.IsInfinity(p.x) || float.IsInfinity(p.y) || float.IsInfinity(p.z))
                Fail($"body '{ab.name}' position is not finite ({p}) — the simulation exploded.");
        }
    }

    private static float Planar(Vector3 delta)
    {
        return new Vector2(delta.x, delta.z).magnitude;
    }

    private static void Fail(string message)
    {
        throw new System.InvalidOperationException("PhysicsSmokeTest FAIL: " + message);
    }
}
