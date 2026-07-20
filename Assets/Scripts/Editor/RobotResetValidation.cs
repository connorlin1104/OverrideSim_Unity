using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Headless validation of RobotSpawner's fall recovery (modeled on PhysicsSmokeTest): edit-mode
// scripted physics (Physics.simulationMode = Script + Physics.Simulate) drops the real robot off
// the bottom of the field and checks that the spawner puts it back.
//
//   - Placed:   a robot handed to PlaceAndWatch lands at the spawn pose, above its own fall line.
//   - Trigger:  pushed below that line, the watchdog reports it as fallen.
//   - Recover:  the reset returns it to the spawn pose with the root's world velocity and every
//               joint velocity zeroed, and it is still there 2 s later — a robot that carried its
//               falling speed through the teleport would launch straight back off the field.
//   - Debounce: a second fall inside the cooldown does not reset again; past the cooldown one does.
//   - Give up:  a robot that keeps falling stops being reset instead of resetting forever.
//
// Edit-mode simulation never runs MonoBehaviours, so this calls RobotSpawner.CheckFallAndReset
// directly — which is why that method takes `now` rather than reading Time.time, a clock edit mode
// never advances. For the same reason the held-piece release inside the reset is only smoke-checked
// here (it must not throw): ClawGrab/IntakePull release from OnDisable, which edit mode never fires.
//
// The simulation mutates the open scene; it is ALWAYS reloaded from disk afterwards so the
// simulated poses are never saved. Batch: -executeMethod RobotResetValidation.RunBatch (throws ->
// nonzero exit).
public static class RobotResetValidation
{
    private const string MenuTitle = "Validate Fall Reset";
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";

    private const float StepSeconds = 0.01f;   // matches the project's fixed timestep
    private const int SettleSteps = 200;       // 2 s

    // Field interior point (above the floor, clear of the walls) the robot is parked at just long
    // enough for PhysX to build the articulation, before the spawner's own placement runs.
    private static readonly Vector3 PreSettlePoint = new Vector3(0f, 0.974f, 6f);

    private const float MaxPlanarDrift = 3f;   // world units a settled robot may slide from its spawn
    private const float MaxRestSpeed = 0.01f;  // velocity that still counts as "stopped"

    [MenuItem("Tools/RoboSim/Robot/Advanced/Validate Fall Reset", false, 5)]
    private static void ValidateMenu()
    {
        // The simulation trashes the open scene and we reload it from disk afterwards, so give the
        // user the standard save-or-cancel prompt before touching anything.
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        try
        {
            Run();
            EditorUtility.DisplayDialog(MenuTitle,
                "Fall-reset smoke tests PASSED (place, trigger, recover, debounce, give up).\n" +
                "See the Console for details.", "OK");
        }
        catch (System.Exception e)
        {
            EditorUtility.DisplayDialog(MenuTitle, "Validation failed:\n\n" + e.Message, "OK");
        }
    }

    public static void RunBatch() => Run();

    private static void Run()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        GameObject prefab = ResolveRobotPrefab()
            ?? throw new System.InvalidOperationException(
                "RobotResetValidation: no robot prefab to test. Pick a robot on the home screen, or select " +
                "one (e.g. Assets/Robots/654V_v1.prefab) in the Project window.");

        // The field scene's own spawner, so the test runs against the REAL spawn point, floor height
        // and wall clearance the player gets. Falls back to a default-configured one if the scene
        // hasn't had Build Robot Prefabs & Spawner run on it.
        RobotSpawner spawner = Object.FindFirstObjectByType<RobotSpawner>(FindObjectsInactive.Include);
        if (spawner == null)
        {
            Debug.Log("RobotResetValidation: no RobotSpawner in the field scene — testing a default-configured " +
                      "one instead (run Build Robot Prefabs & Spawner to add the real one).");
            spawner = new GameObject("TempRobotSpawner").AddComponent<RobotSpawner>();
        }

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        if (instance == null)
            throw new System.InvalidOperationException($"Could not instantiate '{prefab.name}' into the field scene.");

        SimulationMode previous = Physics.simulationMode;
        Physics.simulationMode = SimulationMode.Script;
        try
        {
            ArticulationBody root = instance.GetComponent<ArticulationBody>()
                ?? throw new System.InvalidOperationException(
                    $"'{prefab.name}' has no ArticulationBody on its root — it isn't a set-up robot.");

            TestPlacement(spawner, instance, root);
            TestFallAndRecover(spawner, instance, root);
            TestDebounce(spawner, instance, root);
            TestGivesUp(spawner, instance, root);
        }
        finally
        {
            Physics.simulationMode = previous;
            // Discard every simulated pose — never save a simulated scene.
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        Debug.Log("RobotResetValidation: PASSED (place, trigger, recover, debounce, give up).");
    }

    // --- Phases ---------------------------------------------------------------------------

    // The spawner must place the robot where it says it did. This is the check that would have
    // caught the original transform-write-on-an-articulation-root bug: a silently dropped move
    // leaves the robot at the prefab's authored pose while restorePosition claims otherwise.
    private static void TestPlacement(RobotSpawner spawner, GameObject instance, ArticulationBody root)
    {
        // Park the robot over open floor and step once so PhysX BUILDS the articulation: TeleportRoot
        // only moves a built one, while a plain transform write is what sticks before the first
        // Simulate. Centering the footprint keeps an off-pivot bot from starting inside a wall.
        instance.transform.SetPositionAndRotation(PreSettlePoint, Quaternion.identity);
        Physics.SyncTransforms();
        if (TryGetFootprint(instance, out Bounds pre))
        {
            Vector3 shift = PreSettlePoint - pre.center;
            shift.y = 0f;
            instance.transform.position += shift;
            Physics.SyncTransforms();
        }
        Step(SettleSteps);

        spawner.PlaceAndWatch(instance);
        Step(SettleSteps);

        Vector3 spawn = spawner.RestorePosition;
        float drift = PlanarDistance(instance.transform.position, spawn);
        Require(drift < MaxPlanarDrift,
            $"after PlaceAndWatch the robot settled {drift:F2} units (planar) from the spawn pose it reported " +
            $"({spawn}) — the placement move did not take effect.");
        Require(instance.transform.position.y > spawner.FallThresholdY,
            $"a settled robot is already below its own fall line (Y {instance.transform.position.y:F2} " +
            $"vs {spawner.FallThresholdY:F2}) — it would reset forever.");
        Require(!spawner.ShouldReset(0f), "a settled robot must not read as fallen.");

        Debug.Log($"RobotResetValidation place: '{instance.name}' settled at Y {instance.transform.position.y:F3}, " +
                  $"fall line {spawner.FallThresholdY:F2} (spawn {spawn}).");
    }

    // The fall the user hit: the robot goes over a wall, drops off the field, and has to come back
    // stopped — not carrying the speed it built up on the way down.
    private static void TestFallAndRecover(RobotSpawner spawner, GameObject instance, ArticulationBody root)
    {
        DropOffTheField(spawner, instance, root);
        Require(spawner.ShouldReset(0f),
            $"a robot at Y {instance.transform.position.y:F2}, below the {spawner.FallThresholdY:F2} fall line, " +
            "must read as fallen.");

        float fallSpeed = root.linearVelocity.magnitude;
        Require(fallSpeed > 1f,
            $"the test robot only reached {fallSpeed:F2} units/s falling — too slow to prove the reset stops it.");

        Require(spawner.CheckFallAndReset(0f), "CheckFallAndReset must fire for a fallen robot off cooldown.");

        // Asserted before stepping: one step of gravity would put velocity back on it either way.
        Require(root.linearVelocity.magnitude < MaxRestSpeed && root.angularVelocity.magnitude < MaxRestSpeed,
            $"the reset left the root moving (lin {root.linearVelocity.magnitude:F3}, " +
            $"ang {root.angularVelocity.magnitude:F3} units/s) — it would relaunch itself.");
        float spin = MaxJointSpeed(instance);
        Require(spin < MaxRestSpeed,
            $"the reset left a joint spinning at {spin:F3} rad/s — reduced-space joint velocities survive " +
            "a TeleportRoot and have to be zeroed separately from the root's world velocity.");

        // TeleportRoot writes the pose into PhysX; the Transform only catches up when a simulation
        // step writes the bodies back, so step once before reading the position (velocities above
        // come straight from the body and are readable immediately). At play time the physics step
        // that follows FixedUpdate does this for free.
        Step(1);
        Require(instance.transform.position.y > spawner.FallThresholdY,
            $"the reset left the robot at Y {instance.transform.position.y:F2}, still below the fall line.");

        // It has to STAY back: settle it again and confirm it neither shot off nor fell through.
        Step(SettleSteps);
        Require(instance.transform.position.y > spawner.FallThresholdY,
            $"the robot fell back off the field within 2 s of being reset (Y {instance.transform.position.y:F2}).");
        float drift = PlanarDistance(instance.transform.position, spawner.RestorePosition);
        Require(drift < MaxPlanarDrift,
            $"2 s after the reset the robot had travelled {drift:F2} units from the spawn point — it was " +
            "relaunched rather than put back.");

        Debug.Log($"RobotResetValidation recover: fell at {fallSpeed:F1} units/s, came back to " +
                  $"Y {instance.transform.position.y:F3} stopped, and stayed put for 2 s.");
    }

    // A robot resting right on the fall line must not reset every fixed step.
    private static void TestDebounce(RobotSpawner spawner, GameObject instance, ArticulationBody root)
    {
        DropOffTheField(spawner, instance, root);
        Require(!spawner.CheckFallAndReset(0f), "a second fall inside the cooldown must NOT reset again.");
        Require(!spawner.ShouldReset(0.5f), "the cooldown must still hold half a second in.");
        Require(instance.transform.position.y < spawner.FallThresholdY,
            "the debounced call moved the robot anyway — it must be a no-op, not a quiet reset.");

        Require(spawner.CheckFallAndReset(2f), "past the cooldown a still-fallen robot must reset again.");
        Step(1); // let the teleported pose reach the Transform (see TestFallAndRecover)
        Require(instance.transform.position.y > spawner.FallThresholdY, "that reset must actually move the robot.");

        Debug.Log("RobotResetValidation debounce: blocked inside the cooldown, fired once past it.");
    }

    // The safety net's own safety net: if putting the robot back never sticks, the watchdog stops
    // rather than resetting forever — the failure mode that would recreate the hang it prevents.
    private static void TestGivesUp(RobotSpawner spawner, GameObject instance, ArticulationBody root)
    {
        Debug.Log("RobotResetValidation give-up: the RobotSpawner ERROR below is expected — the give-up " +
                  "breaker is being exercised on purpose.");

        // Two resets are already on the clock from the phases above (t=0 and t=2), so keep falling
        // on the cooldown until the breaker trips or we have clearly given it more tries than
        // MaxConsecutiveResets. The bounded loop is the point: this must terminate.
        bool trippedOut = false;
        for (float now = 3f; now <= 12f; now += 1f)
        {
            DropOffTheField(spawner, instance, root);
            if (!spawner.CheckFallAndReset(now)) { trippedOut = true; break; }
        }

        Require(trippedOut,
            "the watchdog kept resetting a robot that fell straight back off every time — it must give up.");
        Require(!spawner.ShouldReset(60f),
            "having given up, the watchdog must stay off rather than resuming later.");

        Debug.Log("RobotResetValidation give-up: stopped resetting after repeated failures, as intended.");
    }

    // --- Helpers --------------------------------------------------------------------------

    // Puts the robot well below the fall line and lets it accelerate, so the reset is tested against
    // a robot that is genuinely falling fast rather than one merely parked at a low Y.
    private static void DropOffTheField(RobotSpawner spawner, GameObject instance, ArticulationBody root)
    {
        // X/Z come from the spawn pose rather than the live Transform, which may still be one step
        // behind a reset that just teleported the robot back (see TestFallAndRecover).
        Vector3 spawn = spawner.RestorePosition;
        root.TeleportRoot(new Vector3(spawn.x, spawner.FallThresholdY - 10f, spawn.z), instance.transform.rotation);
        Step(30); // 0.3 s of gravity at -98: ~29 units/s of fall speed to be zeroed
    }

    private static void Step(int steps)
    {
        for (int i = 0; i < steps; i++) Physics.Simulate(StepSeconds);
    }

    private static float MaxJointSpeed(GameObject robot)
    {
        float worst = 0f;
        foreach (ArticulationBody body in robot.GetComponentsInChildren<ArticulationBody>(true))
        {
            ArticulationReducedSpace v = body.jointVelocity;
            for (int i = 0; i < v.dofCount; i++) worst = Mathf.Max(worst, Mathf.Abs(v[i]));
        }
        return worst;
    }

    private static float PlanarDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f; b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new System.InvalidOperationException("RobotResetValidation FAILED: " + message);
    }

    // Combined world-space collision footprint (non-trigger colliders). Mirrors
    // RobotSpawner.TryGetWorldFootprint / PhysicsSmokeTest.TryGetFootprint.
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

    // The robot the user most likely means: an explicitly selected robot prefab, else the model
    // picked on the home screen, else the first catalog entry that has a prefab.
    private static GameObject ResolveRobotPrefab()
    {
        GameObject selected = Selection.activeObject as GameObject;
        if (selected != null && PrefabUtility.IsPartOfPrefabAsset(selected) &&
            selected.GetComponentInChildren<ArticulationBody>(true) != null)
            return selected;

        foreach (string guid in AssetDatabase.FindAssets("t:RobotModelCatalog"))
        {
            RobotModelCatalog catalog =
                AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(AssetDatabase.GUIDToAssetPath(guid));
            if (catalog == null) continue;
            if (catalog.SelectedModel != null && catalog.SelectedModel.prefab != null)
                return catalog.SelectedModel.prefab;
            if (catalog.models != null)
                foreach (RobotModelCatalog.Entry entry in catalog.models)
                    if (entry != null && entry.prefab != null) return entry.prefab;
        }
        return null;
    }
}
