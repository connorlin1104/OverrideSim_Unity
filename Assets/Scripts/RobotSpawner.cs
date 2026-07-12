using UnityEngine;

// Instantiates the robot the player picked on the home screen into the field scene.
//
// The field scene no longer holds a robot directly — this spawner drops in the prefab named by
// RobotModelCatalog.SelectedModel (persisted in PlayerPrefs) at a fixed spawn pose. Every robot
// prefab is self-contained: its wheels/mechanisms are internal references and its input comes
// from the shared RobotControls.inputactions asset, so a spawned instance needs no post-spawn
// wiring. It is tagged "Player", which is all the match loaders and camera rely on.
//
// Build the prefabs and drop this component into SampleScene with the
// Tools > RoboSim > Robot > Build Robot Prefabs & Spawner tool.
public class RobotSpawner : MonoBehaviour
{
    [Tooltip("The model catalog; the SelectedModel's prefab is spawned. Same asset the home screen uses.")]
    [SerializeField] private RobotModelCatalog catalog;

    [Tooltip("World position the robot is spawned at (the old inline robot's pose).")]
    [SerializeField] private Vector3 spawnPosition = new Vector3(15.99f, 0.974f, 7.91f);

    [Tooltip("World rotation (Euler degrees) the robot is spawned with.")]
    [SerializeField] private Vector3 spawnEuler = Vector3.zero;

    void Awake()
    {
        if (catalog == null)
        {
            Debug.LogWarning("RobotSpawner: no catalog assigned — no robot spawned.", this);
            return;
        }

        RobotModelCatalog.Entry entry = catalog.SelectedModel;

        // Fall back to the first entry that actually has a prefab, so a selection whose prefab
        // hasn't been built yet still puts *a* robot on the field instead of an empty scene.
        if (entry == null || entry.prefab == null)
        {
            RobotModelCatalog.Entry fallback = FirstEntryWithPrefab();
            if (entry == null || entry.prefab == null) entry = fallback;
        }

        if (entry == null || entry.prefab == null)
        {
            Debug.LogWarning("RobotSpawner: no catalog entry has a prefab — no robot spawned.", this);
            return;
        }

        GameObject robot = Instantiate(entry.prefab, spawnPosition, Quaternion.Euler(spawnEuler));
        RecenterFootprint(robot);
    }

    // Instantiate places the prefab's ROOT at the spawn point, but a prefab's root origin is its
    // CAD/FBX pivot — which Fusion often puts well off the robot's footprint (e.g. a corner). Left
    // as-is, an off-pivot bot lands offset from the spawn point and can drop onto a wall (the 654V
    // did). Shift the root horizontally so the robot's footprint CENTER sits on the spawn point, so
    // one standard spawn point works for any bot regardless of where its CAD origin is. Y is left
    // alone so the drop height is unchanged. Done before the first physics step, so setting the
    // root transform is the correct way to place the freshly-instantiated articulation.
    private void RecenterFootprint(GameObject robot)
    {
        Renderer[] renderers = robot.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return;
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

        Vector3 delta = spawnPosition - bounds.center;
        delta.y = 0f; // re-center the footprint only; keep the spawn Y as the drop height
        robot.transform.position += delta;
    }

    private RobotModelCatalog.Entry FirstEntryWithPrefab()
    {
        if (catalog.models == null) return null;
        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry != null && entry.prefab != null) return entry;
        }
        return null;
    }
}
