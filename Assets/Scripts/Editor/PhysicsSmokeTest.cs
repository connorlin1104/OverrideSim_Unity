using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;

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
// Usage: Tools > RoboSim > Robot > Validate Robot Physics (validates the robot in the active scene),
// or PhysicsSmokeTest.ValidateRobot(root) right after rigging one (Set Up Imported Robot does this).
public class PhysicsSmokeTest
{
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

    // A root-chassis collider with less than this much clearance above the drive wheels' lowest
    // point is treated as "holding the wheels off the ground" (units; ~5 mm at the 10x world scale).
    private const float GroundClearanceTolerance = 0.05f;

    // The field scene the robot actually runs in. Validation spawns the robot here when the editor
    // scene has none — the field scene holds no robot in edit mode (RobotSpawner instantiates one at
    // runtime), which is why validating it used to (misleadingly) report the drivetrain as unrigged.
    private const string FieldScenePath = "Assets/Scenes/SampleScene.unity";

    // Field interior point (above the floor, clear of the walls) used as the validation spawn, so the
    // robot settles/drives/turns on open floor regardless of its game spawn point or off-center pivot.
    private static readonly Vector3 ValidationSpawnPoint = new Vector3(0f, 0.974f, 6f);

    [MenuItem("Tools/RoboSim/Robot/Validate Robot Physics", false, 2)]
    private static void ValidateMenu()
    {
        // The simulation trashes the open scene and we reload it from disk afterwards, so give
        // the user the standard save-or-cancel prompt before touching anything.
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

        try
        {
            ValidateFromMenu();
            EditorUtility.DisplayDialog(MenuTitle,
                "All physics smoke tests PASSED (settle, turn, drive).\nSee the Console for details.", "OK");
        }
        catch (System.Exception e)
        {
            EditorUtility.DisplayDialog(MenuTitle, "Validation failed:\n\n" + e.Message, "OK");
        }
    }

    // Batch entry (-executeMethod PhysicsSmokeTest.RunBatchValidate): spawns the catalog's robot into
    // the field and validates it. Throws (nonzero editor exit) on failure.
    public static void RunBatchValidate()
    {
        GameObject prefab = ResolveRobotPrefab()
            ?? throw new System.InvalidOperationException("RunBatchValidate: no robot prefab to validate.");
        ValidateSpawnedPrefab(prefab);
        Debug.Log($"PhysicsSmokeTest: batch validation PASSED for '{prefab.name}'.");
    }

    // Validates the robot the user most likely means. The field scene spawns its robot at RUNTIME, so
    // in the editor there is usually nothing to test — in that case we spawn the robot prefab into the
    // field, test it, and discard it. Order: an open Prefab Mode robot, else a robot already in the
    // scene (e.g. right after Set Up Imported Robot), else the Project selection / catalog model.
    private static void ValidateFromMenu()
    {
        PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null)
        {
            GameObject staged = AssetDatabase.LoadAssetAtPath<GameObject>(stage.assetPath);
            if (staged == null)
                throw new System.InvalidOperationException(
                    "Save the prefab first — its asset couldn't be loaded to spawn into the field for testing.");
            ValidateSpawnedPrefab(staged);
            return;
        }

        ArticulationBody sceneRobot = FindRobotRootInActiveScene();
        if (sceneRobot != null) { ValidateBody(sceneRobot); return; }

        GameObject prefab = ResolveRobotPrefab();
        if (prefab == null)
            throw new System.InvalidOperationException(
                "No robot to validate. The field scene spawns its robot at runtime, so there is none in the " +
                "editor to test. Select the robot prefab (e.g. Assets/Robots/654V_v1.prefab) in the Project " +
                "window — or pick the robot on the home screen — then run this again. It spawns the robot into " +
                "the field and tests it.");
        ValidateSpawnedPrefab(prefab);
    }

    // Spawns a robot prefab into the field scene, validates it, and lets ValidateBody's scene-reload
    // discard the temporary instance — mirroring how the robot reaches the field at runtime.
    private static void ValidateSpawnedPrefab(GameObject prefab)
    {
        if (prefab == null) throw new System.ArgumentNullException(nameof(prefab));
        if (prefab.GetComponentInChildren<ArticulationBody>(true) == null)
            throw new System.InvalidOperationException(
                $"'{prefab.name}' has no ArticulationBody — it isn't a set-up robot. Run Set Up Imported Robot first.");

        // The tests need the field's floor + walls, so validate in the field scene; open it if the
        // active scene isn't it (the menu already offered to save the current scene).
        Scene field = SceneManager.GetActiveScene();
        if (field.path != FieldScenePath)
            field = EditorSceneManager.OpenScene(FieldScenePath, OpenSceneMode.Single);

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, field);
        if (instance == null)
            throw new System.InvalidOperationException($"Could not instantiate '{prefab.name}' into the field scene.");

        ArticulationBody root = PlaceForValidation(instance);
        if (root == null)
            throw new System.InvalidOperationException($"Spawned '{prefab.name}' has no ArticulationBody to validate.");
        ValidateBody(root); // reloads FieldScenePath from disk in its finally -> discards `instance`
    }

    // Places a freshly-instantiated robot over the middle of the field, clear of the walls, and lets
    // it fall and come to rest so validation runs on a settled robot regardless of its game spawn
    // point or CAD pivot. Edit-mode transform writes stick because no Physics.Simulate has built the
    // articulation yet; a physics-based pre-settle then drops it onto the floor without needing to
    // know the floor height (edit-mode raycasts before the first Simulate are unreliable).
    private static ArticulationBody PlaceForValidation(GameObject instance)
    {
        instance.transform.SetPositionAndRotation(ValidationSpawnPoint, Quaternion.identity);
        Physics.SyncTransforms();

        if (TryGetFootprint(instance, out Bounds b))
        {
            // Center the footprint on the interior point (X/Z), clear of the walls.
            Vector3 shift = ValidationSpawnPoint - b.center;
            shift.y = 0f;
            instance.transform.position += shift;
            Physics.SyncTransforms();
        }

        ArticulationBody root = RootBodyOf(instance);
        float yBefore = root != null ? root.transform.position.y : 0f;

        // Pre-settle: let it fall and rest BEFORE the settle phase measures stability, so that phase
        // sees an already-settled robot (small delta) instead of the initial drop onto the floor,
        // which would trip its "fell through / exploded" guard.
        SimulationMode prev = Physics.simulationMode;
        Physics.simulationMode = SimulationMode.Script;
        try { for (int i = 0; i < StepsPerPhase; i++) Physics.Simulate(StepSeconds); }
        finally { Physics.simulationMode = prev; }

        if (root != null)
            Debug.Log($"PhysicsSmokeTest pre-settle: root Y {yBefore:F3} -> {root.transform.position.y:F3} " +
                      $"(dropped {yBefore - root.transform.position.y:F3}).");
        return root;
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

        // Warn as soon as we settle if something is holding the drive wheels off the ground — this
        // is the usual cause of the drive test failing later, and it's far clearer to name the part
        // here than to leave the driver staring at the raw collider list above.
        string belowWheels = DescribeBelowWheelParts(root);
        if (belowWheels != null)
            Debug.LogWarning("PhysicsSmokeTest ground clearance: " + belowWheels);
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

    // A root-chassis collider sitting at or below the drive wheels' lowest point: something that
    // holds the drive wheels off the ground so they only spin in place.
    private struct BelowWheelPart
    {
        public string desc;  // "parentName/colliderName"
        public float below;  // how far below the lowest wheel point, in units (>= ~0)
    }

    // Root-chassis colliders whose bottom is within GroundClearanceTolerance of (or below) the
    // lowest drive-wheel point, worst (lowest) first. Non-drive parts — odometry/tracking wheels,
    // intakes, low brackets — get box/hull colliders on the ROOT body (only the drive wheels become
    // sphere links), so if one pokes below the wheels it unloads them. Returns empty when the wheels
    // are clear or there are no drive wheels. lowestWheelY is output for callers that want to log it.
    private static List<BelowWheelPart> FindPartsBelowWheels(ArticulationBody root, out float lowestWheelY)
    {
        lowestWheelY = float.PositiveInfinity;
        foreach (ArticulationBody ab in root.GetComponentsInChildren<ArticulationBody>())
        {
            if (ab.isRoot || ab.jointType != ArticulationJointType.RevoluteJoint) continue;
            foreach (Collider c in ab.GetComponentsInChildren<Collider>())
                lowestWheelY = Mathf.Min(lowestWheelY, c.bounds.min.y);
        }

        var below = new List<BelowWheelPart>();
        if (float.IsInfinity(lowestWheelY)) return below; // no drive-wheel links to compare against

        foreach (Collider c in root.GetComponentsInChildren<Collider>())
        {
            ArticulationBody owner = c.GetComponentInParent<ArticulationBody>();
            if (owner == null || !owner.isRoot) continue; // only colliders on the chassis (root) body
            float bottom = c.bounds.min.y;
            if (bottom <= lowestWheelY + GroundClearanceTolerance)
                below.Add(new BelowWheelPart { desc = $"{c.transform.parent?.name}/{c.name}", below = lowestWheelY - bottom });
        }
        below.Sort((a, b) => b.below.CompareTo(a.below)); // worst (lowest) first
        return below;
    }

    // One-line, human-readable explanation of what's holding the drive wheels up, or null when the
    // wheels are clear. Used both as a settle-phase warning and appended to the drive-test failure.
    private static string DescribeBelowWheelParts(ArticulationBody root)
    {
        List<BelowWheelPart> below = FindPartsBelowWheels(root, out _);
        if (below.Count == 0) return null;
        List<string> worst = below.GetRange(0, Mathf.Min(3, below.Count))
                                  .ConvertAll(p => $"'{p.desc}' ({p.below:F2} below)");
        return $"{below.Count} chassis part(s) sit at or below the drive wheels and hold them off the " +
               $"ground — worst: {string.Join(", ", worst)}. These are non-drive parts (likely an " +
               "odometry/tracking wheel, an intake, or a low bracket) that got box/hull colliders on the " +
               "chassis. Raise them, or exclude them from colliders, so only the drive wheels touch the ground.";
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

            // If the wheels spun but the robot didn't move, the usual cause is that something below
            // the drive wheels is holding them off the ground. Name it in the failure so the driver
            // doesn't have to reverse-engineer it from the collider dump above.
            string belowWheels = DescribeBelowWheelParts(root);
            string cause = maxSpinRad > MinWheelSpinRad && belowWheels != null
                ? " The wheels spun but the robot didn't move: " + belowWheels
                : "";
            Fail($"drive: planar displacement {planar:F2} units (need > {MinDrivePlanar}) — wheels are not propelling the robot.{cause}");
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

    // The robot root already present in the active scene, found via RobotMotorController (the reliable
    // set-up marker) rather than ArticulationBody.isRoot, which is unreliable before the articulation
    // is built. Null when the scene has no robot (the usual field-scene case — it spawns at runtime).
    private static ArticulationBody FindRobotRootInActiveScene()
    {
        foreach (RobotMotorController c in Object.FindObjectsByType<RobotMotorController>(FindObjectsInactive.Exclude))
        {
            ArticulationBody body = c.GetComponent<ArticulationBody>();
            if (body != null) return body;
        }
        return null;
    }

    // The robot's root ArticulationBody (RobotMotorController's body, else the topmost body).
    private static ArticulationBody RootBodyOf(GameObject robot)
    {
        RobotMotorController controller = robot.GetComponentInChildren<RobotMotorController>(true);
        ArticulationBody body = controller != null ? controller.GetComponent<ArticulationBody>() : null;
        return body != null ? body : robot.GetComponentInChildren<ArticulationBody>(true);
    }

    // The robot prefab to spawn for validation: the Project selection if it's a set-up robot prefab,
    // else the catalog's selected model, else the first catalog entry with a prefab.
    private static GameObject ResolveRobotPrefab()
    {
        GameObject selected = Selection.activeObject as GameObject;
        if (selected != null && PrefabUtility.IsPartOfPrefabAsset(selected) &&
            selected.GetComponentInChildren<ArticulationBody>(true) != null)
            return selected;

        RobotModelCatalog catalog = LoadCatalog();
        if (catalog == null) return null;
        if (catalog.SelectedModel != null && catalog.SelectedModel.prefab != null)
            return catalog.SelectedModel.prefab;
        if (catalog.models != null)
            foreach (RobotModelCatalog.Entry entry in catalog.models)
                if (entry != null && entry.prefab != null) return entry.prefab;
        return null;
    }

    private static RobotModelCatalog LoadCatalog()
    {
        foreach (string guid in AssetDatabase.FindAssets("t:RobotModelCatalog"))
            return AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(AssetDatabase.GUIDToAssetPath(guid));
        return null;
    }

    // Combined world-space collision footprint (non-trigger colliders), for centering the robot in
    // the field before validation. Mirrors RobotSpawner.TryGetWorldFootprint.
    private static bool TryGetFootprint(GameObject robot, out Bounds bounds)
    {
        bounds = new Bounds();
        bool has = false;
        foreach (Collider c in robot.GetComponentsInChildren<Collider>())
        {
            if (c.isTrigger) continue;
            if (!has) { bounds = c.bounds; has = true; }
            else bounds.Encapsulate(c.bounds);
        }
        return has;
    }

    // All revolute links under the root, split by side (link names carry LS/RS; fall back to
    // the sign of their root-local X, where +X is robot right).
    private static ArticulationBody[] FindWheels(ArticulationBody root, out ArticulationBody[] left, out ArticulationBody[] right)
    {
        // Prefer the DRIVE wheels the rig recorded on RobotMotorController. Driving only these keeps
        // the robot's other revolute joints (rollers, arms, coupled followers) out of the drive/turn
        // tests — otherwise every revolute reads as a wheel and the mechanisms get flailed, which
        // muddies the result for a robot that has any mechanism (like the 654V).
        RobotMotorController controller = root.GetComponentInChildren<RobotMotorController>(true);
        if (controller != null)
        {
            List<ArticulationBody> l = NonNull(controller.leftWheels);
            List<ArticulationBody> r = NonNull(controller.rightWheels);
            if (l.Count + r.Count > 0)
            {
                left = l.ToArray();
                right = r.ToArray();
                var driveWheels = new List<ArticulationBody>(l);
                driveWheels.AddRange(r);
                return driveWheels.ToArray();
            }
        }

        // Fallback for a hand-rigged robot with no controller wheel lists: every non-root revolute is
        // a wheel, split by side from the LS/RS name token or the sign of its root-local X. (root skip
        // is by identity — isRoot is unreliable before the articulation builds.)
        List<ArticulationBody> all = new List<ArticulationBody>();
        List<ArticulationBody> leftList = new List<ArticulationBody>();
        List<ArticulationBody> rightList = new List<ArticulationBody>();
        foreach (ArticulationBody ab in root.GetComponentsInChildren<ArticulationBody>())
        {
            if (ab == root || ab.jointType != ArticulationJointType.RevoluteJoint) continue;
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

    private static List<ArticulationBody> NonNull(ArticulationBody[] arr)
    {
        var list = new List<ArticulationBody>();
        if (arr != null)
            foreach (ArticulationBody a in arr)
                if (a != null) list.Add(a);
        return list;
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
