using UnityEngine;

// Instantiates the robot the player picked on the home screen into the field scene, and keeps it
// on the field.
//
// The field scene no longer holds a robot directly — this spawner drops in the prefab named by
// RobotModelCatalog.SelectedModel (persisted in PlayerPrefs) at a fixed spawn pose. Every robot
// prefab is self-contained: its wheels/mechanisms are internal references and its input comes
// from the shared RobotControls.inputactions asset, so a spawned instance needs no post-spawn
// wiring. It is tagged "Player", which is all the match loaders and camera rely on.
//
// FALL RECOVERY: a robot that gets flipped over a field wall falls forever, and the only way out
// was quitting Unity. So once the robot is spawned it is watched every fixed step and put back at
// its spawn pose if it drops past the fall line (see fallDepth).
//
// That recovery deliberately does NOT go through the on-screen Reset button, which reloads the
// scene: firing a scene reload automatically would wipe the whole field's game pieces every time
// the bot tipped into a hole, and — worse — a robot that came back below the line would reload
// forever, which is the very hang this exists to prevent. Instead ResetRobot restores the pose the
// spawn placement already computed, stops all motion, and hands back anything the robot was
// carrying. It is public, so a "put my robot back" button can call it rather than growing a second
// way to do this; the Reset button stays the heavier "restart the whole match" reset.
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

    [Header("Fall recovery")]

    [Tooltip("Put the robot back at its spawn pose if it drops off the field. Turn off only to debug " +
             "a fall — with it off, a bot that goes over a wall falls forever.")]
    [SerializeField] private bool autoResetOnFall = true;

    [Tooltip("How far BELOW its spawn height (world units, 1 unit = 100 mm) the robot must drop to " +
             "count as off the field. Measured down from the spawn pose rather than from an absolute " +
             "Y so it tracks the field floor and any robot pivot. Keep it well over the height of the " +
             "tallest robot: the check reads the root's Y, and tipping a bot over drops that by up to " +
             "its own height, which must NOT read as a fall. 20 units = 2 m, ~4x a VEX bot.")]
    [SerializeField] private float fallDepth = 20f;

    [Tooltip("Seconds before another auto-reset may fire. Stops a robot that lands right on the fall " +
             "line from resetting every single fixed step.")]
    [SerializeField] private float resetCooldown = 1f;

    // Auto-resets this many times in a row without recovering and the watchdog gives up (see
    // CheckFallAndReset). Resetting forever would just trade one unplayable state for another.
    private const int MaxConsecutiveResets = 4;

    // Two resets closer together than resetCooldown * this count as consecutive — long enough that
    // a bot which resets, drives around and falls again later starts the count over.
    private const float ConsecutiveWindowFactor = 5f;

    private GameObject spawnedRobot;
    private ArticulationBody spawnedRoot;
    private ArticulationBody[] spawnedBodies;

    // The pose the spawn placement worked out, captured so a fall reset can restore exactly it
    // instead of re-deriving one from a robot that is currently upside down in the void — the
    // footprint a re-derivation would measure describes the tipped-over robot, not the upright one.
    private Vector3 restorePosition;
    private Quaternion restoreRotation = Quaternion.identity;

    // Below this world Y the robot counts as fallen. Negative infinity until a robot is placed, so
    // the watchdog can never fire before there is something to put back.
    private float fallThresholdY = float.NegativeInfinity;

    private float nextResetAllowed;
    private float consecutiveDeadline;
    private int consecutiveResets;
    private bool gaveUp;

    // World Y under which the robot is treated as having fallen off the field.
    public float FallThresholdY => fallThresholdY;

    // The pose a reset puts the robot back at — what the spawn placement worked out.
    public Vector3 RestorePosition => restorePosition;

    // The instance this spawner created and is watching, or null if nothing spawned.
    public GameObject SpawnedRobot => spawnedRobot;

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
        PlaceAndWatch(robot);
    }

    // Places `robot` at the geometry-derived spawn pose and starts watching it for falls. Awake
    // calls this on the instance it just made; the headless validator calls it on one it
    // instantiated itself, because edit-mode simulation never runs Awake.
    //
    // Expects `robot` to already carry the spawn rotation (Instantiate applies it) — the footprint
    // is measured in the robot's current orientation, so the pose this captures only describes an
    // upright robot if it was upright when measured.
    public void PlaceAndWatch(GameObject robot)
    {
        if (robot == null) return;

        spawnedRobot = robot;
        spawnedRoot = robot.GetComponent<ArticulationBody>();
        spawnedBodies = robot.GetComponentsInChildren<ArticulationBody>(true);

        // Where the robot sits if its footprint can't be measured: the pose Instantiate applied,
        // which is what RecenterFootprint leaves it at when it bails out.
        restorePosition = spawnPosition;
        restoreRotation = Quaternion.Euler(spawnEuler);

        RecenterFootprint(robot); // overwrites the restore pose with the geometry-derived one

        // The fall line hangs below the pose we just placed, not below an absolute Y: that pose is
        // already derived from the field floor (floorY + dropClearance) AND from where this robot's
        // FBX pivot sits relative to its geometry, so the same margin means the same thing for a bot
        // whose pivot is on its footprint and one whose pivot is metres away in CAD space. It also
        // makes a start-up reset loop structurally impossible — the robot begins exactly fallDepth
        // above its own trigger line, whatever the field looks like.
        fallThresholdY = restorePosition.y - fallDepth;

        nextResetAllowed = 0f;
        consecutiveDeadline = 0f;
        consecutiveResets = 0;
        gaveUp = false;
    }

    void FixedUpdate()
    {
        CheckFallAndReset(Time.time);
    }

    // The guarded per-step check: resets the robot and returns true if it has fallen off the field,
    // is off cooldown, and the watchdog hasn't given up. `now` is a parameter rather than read from
    // Time.time inside so the headless validator can drive the cooldown with a synthetic clock —
    // edit-mode simulation steps physics but never advances Time.
    public bool CheckFallAndReset(float now)
    {
        if (!ShouldReset(now)) return false;

        // Consecutive means "fell again before it had a chance to look recovered", which is the
        // signature of a robot being put back somewhere it can't survive.
        consecutiveResets = now < consecutiveDeadline ? consecutiveResets + 1 : 1;
        nextResetAllowed = now + resetCooldown;
        consecutiveDeadline = now + resetCooldown * ConsecutiveWindowFactor;

        ResetRobot($"auto-reset #{consecutiveResets}: fell to Y {spawnedRobot.transform.position.y:F2}, " +
                   $"past the {fallThresholdY:F2} fall line ({fallDepth:F0} units below its spawn height)");

        if (consecutiveResets >= MaxConsecutiveResets)
        {
            gaveUp = true;
            Debug.LogError(
                $"RobotSpawner: auto-reset gave up after {consecutiveResets} resets in a row — '{spawnedRobot.name}' " +
                "keeps falling off the field straight after being put back. Something is wrong with the spawn point " +
                "or the field floor, and resetting forever would only hide it. Use the Reset button to reload.", this);
        }
        return true;
    }

    // Whether the watchdog would fire right now. Cheap enough for every fixed step: a few flag
    // tests and one float compare.
    public bool ShouldReset(float now)
    {
        if (!autoResetOnFall || gaveUp || spawnedRobot == null) return false;
        // A NaN Y fails this compare and so counts as fallen, which is the right call — a robot
        // whose pose has gone non-finite is every bit as stuck as one that went over the wall, and
        // the reset writes finite values back over it.
        if (spawnedRobot.transform.position.y >= fallThresholdY) return false;
        return now >= nextResetAllowed;
    }

    // Puts the robot back at the pose the spawn placement computed, at rest and carrying nothing.
    // Ignores the cooldown — rate-limiting is CheckFallAndReset's job — so this is also the method
    // to call for a deliberate "put my robot back" that doesn't reload the scene.
    public void ResetRobot(string reason)
    {
        if (spawnedRobot == null) return;

        // Logged before the move so the pose that triggered it is still readable, and as a warning
        // so an auto-reset stands out in the console as itself rather than as a physics glitch.
        Debug.LogWarning($"RobotSpawner: robot reset — {reason}. Returning '{spawnedRobot.name}' to " +
                         $"{restorePosition} with all motion stopped.", this);

        ReleaseHeldPieces(spawnedRobot);

        // Moving the articulation ROOT needs TeleportRoot; a transform write is silently dropped
        // (the same trap RecenterFootprint documents). Rotation goes back too — the robot is
        // usually upside down by the time it has fallen this far.
        if (spawnedRoot != null) spawnedRoot.TeleportRoot(restorePosition, restoreRotation);
        else spawnedRobot.transform.SetPositionAndRotation(restorePosition, restoreRotation);

        StopAllMotion();
    }

    // Zeroes the articulation's whole motion state. TeleportRoot moves the bodies but KEEPS their
    // velocities, so without this the robot arrives carrying however fast it was falling and simply
    // launches itself off the field again.
    //
    // An articulation's motion lives in two separate stores: the root's world-space velocity, and
    // each joint's reduced-space velocity. Both have to go. The child links' world velocities are
    // derived from those two, not stored, which is why only the root's are written here.
    private void StopAllMotion()
    {
        if (spawnedRoot != null)
        {
            spawnedRoot.linearVelocity = Vector3.zero;
            spawnedRoot.angularVelocity = Vector3.zero;
        }

        if (spawnedBodies == null) return;
        foreach (ArticulationBody body in spawnedBodies)
        {
            if (body == null) continue;
            ArticulationReducedSpace joint = body.jointVelocity;
            if (joint.dofCount == 0) continue; // fixed links and the root itself
            for (int i = 0; i < joint.dofCount; i++) joint[i] = 0f;
            body.jointVelocity = joint;
        }
    }

    // Hands back every game piece the robot is carrying, through the holder's OWN release path.
    //
    // The claw and the intake hold a piece by making it kinematic and muting its collisions, and
    // both already undo all of that in OnDisable ("never leave a piece kinematic or half-muted").
    // Cycling `enabled` therefore runs exactly the release the holder wrote for this case and then
    // re-arms it. Freeing the pieces from here instead would restore isKinematic but not the muted
    // collision pairs — only the holder knows which ones it muted — and would leave its bookkeeping
    // pointing at bodies it no longer controls.
    private static void ReleaseHeldPieces(GameObject robot)
    {
        foreach (ClawGrab claw in robot.GetComponentsInChildren<ClawGrab>()) CycleEnabled(claw);
        foreach (IntakePull intake in robot.GetComponentsInChildren<IntakePull>()) CycleEnabled(intake);
    }

    private static void CycleEnabled(MonoBehaviour holder)
    {
        // Only cycle a holder that is actually running: flipping `enabled` on a disabled one would
        // switch it ON, and a mechanism that is off must stay off. A disabled holder has already
        // released everything anyway — that is what its OnDisable did on the way down.
        if (holder == null || !holder.isActiveAndEnabled) return;
        holder.enabled = false; // OnDisable -> releases and un-mutes everything it holds
        holder.enabled = true;
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

        // Remember what we placed, so a fall reset restores this exact pose rather than re-running
        // the derivation against a robot that is by then tumbling somewhere under the field.
        restorePosition = newPos;
        restoreRotation = robot.transform.rotation;

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
