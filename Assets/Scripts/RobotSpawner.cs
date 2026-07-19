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

    [Tooltip("Keep the robot's collider footprint at least this far (world units) inside the field walls.")]
    [SerializeField] private float wallClearance = 0.5f;

    [Tooltip("Field floor surface Y. The robot is dropped so its LOWEST collider starts just above this " +
             "(then settles under gravity), so a bot whose FBX pivot sits anywhere still lands on the floor " +
             "instead of clipping into it. Matches the field floor used elsewhere (RobotDriveController / MinHeightClamp).")]
    [SerializeField] private float floorY = 0.72f;

    [Tooltip("How far above the floor the robot's lowest point starts before it settles.")]
    [SerializeField] private float dropClearance = 0.05f;

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
            entry = FirstEntryWithPrefab();
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
    // did). Two steps fix that: shift the root so the robot's collision footprint CENTER sits on the
    // spawn point, then pull the footprint fully inside the field walls so a wide bot near the wall
    // can't overlap it and get ejected on top. Y is set from the footprint so the robot's lowest point
    // starts just above the floor — an off-pivot bot no longer clips into or below the ground.
    //
    // The correction is applied via ArticulationBody.TeleportRoot: for an articulation ROOT, writing
    // transform.position is silently ignored (the physics engine owns the root pose), so the old
    // transform-only reposition was a no-op — a bot whose pivot sat on its footprint (the 360rpm dt)
    // needed no correction and looked fine, while the off-pivot 654V never moved and hung on the wall.
    // TeleportRoot is the supported move and rigidly carries every child link with it.
    private void RecenterFootprint(GameObject robot)
    {
        // Instantiate set the transform, but PhysX hasn't adopted it yet (Physics.autoSyncTransforms
        // is off by default), so Collider.bounds would read a stale pre-spawn pose — sync first.
        Physics.SyncTransforms();
        if (!TryGetWorldFootprint(robot, out Bounds bounds)) return;

        // 1) Center the footprint on the spawn point (X/Z), and set Y so the robot's LOWEST collider
        //    point starts just above the floor regardless of where the FBX pivot sits — then play-mode
        //    gravity settles it. The old fixed spawnPosition.y was baked for the original drivetrain's
        //    pivot and clipped an off-pivot bot into the ground. Move the footprint in-code — the
        //    colliders aren't re-read before the single apply below, so a pure translation keeps
        //    bounds.center/extents correct without another SyncTransforms.
        Vector3 delta = spawnPosition - bounds.center;
        delta.y = (floorY + dropClearance) - bounds.min.y;
        bounds.center += delta;

        // 2) Clamp the centered footprint inside the field walls. Uses the actual wall colliders, so
        //    it tracks the field if it moves, and does nothing if the walls aren't found.
        Vector3 push = Vector3.zero;
        if (TryGetFieldInterior(out float minX, out float maxX, out float minZ, out float maxZ))
        {
            push.x = ClampAxis(bounds.center.x, bounds.extents.x, minX, maxX, wallClearance);
            push.z = ClampAxis(bounds.center.z, bounds.extents.z, minZ, maxZ, wallClearance);
        }

        // 3) Apply both corrections exactly once, moving the articulation root (not just the
        //    transform). `robot` is the instantiated prefab root and carries the root body, so its
        //    ArticulationBody IS the articulation root — we don't gate on isRoot, which reflects
        //    native state that may not be ready this early after Instantiate (and would silently drop
        //    us back to the transform write that PhysX ignores). Fall back to the transform only when
        //    there's no body at all (a hypothetical non-articulated robot).
        Vector3 newPos = robot.transform.position + delta + push;
        ArticulationBody rootBody = robot.GetComponent<ArticulationBody>();
        if (rootBody != null)
            rootBody.TeleportRoot(newPos, robot.transform.rotation);
        else
            robot.transform.position = newPos;
    }

    // Combined WORLD-space collision footprint. Colliders (not renderers) are what actually contact
    // the wall — a wheel sphere or part box can exceed the visual mesh. Triggers are skipped, and it
    // falls back to renderers only if a bot somehow has no solid colliders yet.
    private static bool TryGetWorldFootprint(GameObject robot, out Bounds bounds)
    {
        bounds = new Bounds();
        bool has = false;
        foreach (Collider col in robot.GetComponentsInChildren<Collider>())
        {
            if (col.isTrigger) continue;
            if (!has) { bounds = col.bounds; has = true; }
            else bounds.Encapsulate(col.bounds);
        }
        if (has) return true;
        foreach (Renderer r in robot.GetComponentsInChildren<Renderer>(true))
        {
            if (!has) { bounds = r.bounds; has = true; }
            else bounds.Encapsulate(r.bounds);
        }
        return has;
    }

    // Interior playfield extent (world X/Z) from the field's perimeter wall boxes: each thin wall's
    // inner face bounds the play area. Returns false unless all four sides are found, so the caller
    // can fall back to the plain recenter. Matches the walls built by FixFieldColliders under
    // "Perimeter/WallColliders".
    private static bool TryGetFieldInterior(out float minX, out float maxX, out float minZ, out float maxZ)
    {
        minX = float.NegativeInfinity; maxX = float.PositiveInfinity;
        minZ = float.NegativeInfinity; maxZ = float.PositiveInfinity;

        GameObject perimeter = GameObject.Find("Perimeter");
        Transform wallHost = perimeter != null ? perimeter.transform.Find("WallColliders") : null;
        if (wallHost == null) return false;
        BoxCollider[] walls = wallHost.GetComponentsInChildren<BoxCollider>();
        if (walls.Length == 0) return false;

        // Field center, to tell a +X wall from a -X wall (and +Z from -Z).
        Vector3 sum = Vector3.zero;
        foreach (BoxCollider w in walls) sum += w.bounds.center;
        Vector3 fieldCenter = sum / walls.Length;

        foreach (BoxCollider w in walls)
        {
            Bounds b = w.bounds;
            if (b.size.x <= b.size.z) // thin along X -> a +/-X wall; inner face bounds the play area
            {
                if (b.center.x >= fieldCenter.x) maxX = Mathf.Min(maxX, b.min.x);
                else minX = Mathf.Max(minX, b.max.x);
            }
            else // thin along Z -> a +/-Z wall
            {
                if (b.center.z >= fieldCenter.z) maxZ = Mathf.Min(maxZ, b.min.z);
                else minZ = Mathf.Max(minZ, b.max.z);
            }
        }
        return !float.IsInfinity(minX) && !float.IsInfinity(maxX)
            && !float.IsInfinity(minZ) && !float.IsInfinity(maxZ);
    }

    // Delta to move a footprint of half-width `half` (centered at `center`) so it stays within
    // [min+clear, max-clear]. If the bot is too wide for that gap, it's centered in the gap instead.
    private static float ClampAxis(float center, float half, float min, float max, float clear)
    {
        float lo = min + clear + half;
        float hi = max - clear - half;
        float target = lo > hi ? (min + max) * 0.5f : Mathf.Clamp(center, lo, hi);
        return target - center;
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
