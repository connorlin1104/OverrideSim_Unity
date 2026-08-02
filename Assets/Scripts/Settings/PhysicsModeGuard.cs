using UnityEngine;

// The editor validators step physics by hand, which means flipping Physics.simulationMode to Script.
// That setter is NOT scope-local: it writes through to ProjectSettings/DynamicsManager.asset, Unity
// saves it to disk, and the value ships. When that happened the game never stepped physics at all —
// the robot hung wherever it spawned, half inside a wall, no piece settled, no mechanism moved and
// every button looked dead, with nothing in the log to say why.
//
// No test caught it and no test can: every physics test drives Physics.Simulate by hand, so all of
// them behave identically whether the project is on FixedUpdate or Script. The running game is the
// only thing that can tell the difference, so the check lives here.
//
// BeforeSceneLoad, so it lands ahead of every Awake in every scene, and it runs in a build as well as
// in play mode. Forcing the value rather than merely asserting it means a bad asset costs one line in
// the log instead of an unplayable build. ValidationUtil holds the matching editor-side guard.
internal static class PhysicsModeGuard
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ForceFixedUpdateSimulation()
    {
        if (Physics.simulationMode == SimulationMode.FixedUpdate) return;

        Debug.LogWarning($"Physics simulation mode was '{Physics.simulationMode}', not FixedUpdate, so " +
                         "nothing in the scene would have moved. Forcing FixedUpdate for this run — fix " +
                         "it for good in Edit > Project Settings > Physics > Simulation Mode.");
        Physics.simulationMode = SimulationMode.FixedUpdate;
    }
}
