using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// The tape on the floor is paint. Nothing FLAT under StaticObjects may collide.
//
// The field CAD carries three flat sheets there — one 3029 x 0 x 3087 mm sitting 0.2 mm above the
// floor, and two 914 x 1 x 2946 mm strips just under it — and every one had a MeshCollider. A sheet
// a fifth of a millimetre above the ground box is the worst thing to hand the solver: it is not a
// ledge the wheel climbs, it is a second floor at the same height as the first, and the contact
// alternates between them every step. The robot shakes, and only where the tape is.
//
// The other 92 colliders under StaticObjects are 77 x 88 x 80 mm blocks standing 71 to 149 mm proud.
// Those are structure, not paint, and this check must never be widened to cover them — stripping
// them because they share a folder with the tape would let robots drive straight through them.
// Thickness is the test because thickness is what decides whether a thing is a surface or a sticker.
//
// It also cost a wrong diagnosis, which is the real reason this file exists. Every turn measurement
// in this project is taken on TipOverValidation's bare floor, so the harness was smooth in exactly
// the place the game was rough, and the difference was not the robot — it was that the game has tape
// and the harness does not. A fixture that cannot reproduce the player's floor will keep sending
// people to look at the drivetrain.
//
// TapeDetectors is the OPPOSITE case and is asserted to still be there. Those four boxes are
// triggers driving MatchLoadTrigger -> MatchLoaderController.OnTapeEntered, which is how the game
// knows a robot is standing on the tape to be fed. A trigger produces no contact forces, so it can
// never cause the shake — and a well-meaning cleanup that removed them along with the solid ones
// would silently break match loading with nothing to catch it. Both halves are pinned here so
// neither can be undone by mistake.
public static class PaintedTapeValidation
{
    [MenuItem("Tools/RoboSim/Validate/Validate Painted Tape", false, 42)]
    public static void Validate() => ValidationUtil.RunInteractive("Painted Tape", Run);

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Painted Tape", Run);

    private static string Run()
    {
        EditorSceneManager.OpenScene(RoboSimPaths.MainScene, OpenSceneMode.Single);

        GameObject decals = GameObject.Find(RebuildFieldBounds.StaticObjectsName);
        ValidationUtil.Assert(decals != null,
            $"no '{RebuildFieldBounds.StaticObjectsName}' in {RoboSimPaths.MainScene}. That is where " +
            "the painted tape lives, so either the field has been restructured or the wrong scene is " +
            "open — and this check would otherwise pass by finding nothing.");

        float floorTop = FloorTop();
        var flat = new List<string>();
        int triggers = 0, structure = 0;
        foreach (Collider col in decals.GetComponentsInChildren<Collider>(true))
        {
            if (col == null) continue;
            if (col.isTrigger) { triggers++; continue; }

            // Thickness is the test, not height above the floor. The three tape sheets here are 0 to
            // 1 mm thick; everything else under this folder is an 88 mm block standing 71 to 149 mm
            // proud, which is real structure and must keep colliding. Renderer bounds come along in
            // the message because a collider much bigger than the thing it is drawn as is its own
            // kind of wrong, and this is the report someone will read when the field CAD changes.
            if (col.bounds.size.y > DecalThickness) { structure++; continue; }

            Vector3 size = col.bounds.size;
            Renderer rend = col.GetComponent<Renderer>();
            string drawn = rend != null
                ? $"drawn {rend.bounds.size.x * 100f:0}x{rend.bounds.size.y * 100f:0}x" +
                  $"{rend.bounds.size.z * 100f:0} mm"
                : "NO RENDERER";
            flat.Add($"{Path(col.transform)} ({col.GetType().Name}) " +
                     $"collider {size.x * 100f:0}x{size.y * 100f:0.0}x{size.z * 100f:0} mm, " +
                     $"top {(col.bounds.max.y - floorTop) * 100f:+0.0;-0.0} mm off the floor, {drawn}");
        }

        ValidationUtil.Assert(flat.Count == 0,
            $"{flat.Count} FLAT collider(s) under {RebuildFieldBounds.StaticObjectsName} are solid. A " +
            "sheet a fraction of a millimetre off the floor is not a ledge the robot climbs — it is a " +
            "SECOND FLOOR at the same height as the first, and each wheel's contact alternates between " +
            "the two every step, which the driver feels as the robot shaking on the tape. Run Tools > " +
            "RoboSim > Field & Pieces > Rebuild Floor and Wall Bounds, which strips exactly these and " +
            "leaves the thicker objects alone.\n    " +
            string.Join("\n    ", flat.GetRange(0, Mathf.Min(flat.Count, 12))) +
            (flat.Count > 12 ? $"\n    ...and {flat.Count - 12} more" : ""));

        // The other half: the triggers that must survive any such cleanup.
        var detectors = new List<MatchLoadTrigger>();
        foreach (MatchLoadTrigger t in Object.FindObjectsByType<MatchLoadTrigger>(
                     FindObjectsInactive.Include)) detectors.Add(t);

        ValidationUtil.Assert(detectors.Count > 0,
            "there are no MatchLoadTrigger components in the field at all, so no robot can ever be " +
            "detected as standing on the tape and match loads would never arrive. If these were " +
            "removed while clearing the SOLID tape colliders, that is the mistake this check exists " +
            "for: those are triggers, they generate no contact forces, and they were never the shake.");

        int withoutCollider = 0;
        foreach (MatchLoadTrigger t in detectors)
        {
            Collider c = t.GetComponent<Collider>();
            if (c == null || !c.isTrigger) withoutCollider++;
        }
        ValidationUtil.Assert(withoutCollider == 0,
            $"{withoutCollider} of {detectors.Count} MatchLoadTrigger(s) have no trigger collider, so " +
            "OnTriggerEnter can never fire and standing on the tape does nothing.");

        return $"Painted Tape Is Not Solid: PASSED.\n" +
               $"  {RebuildFieldBounds.StaticObjectsName}: 0 flat colliders, {structure} thicker " +
               $"object(s) left collidable, {triggers} trigger(s).\n" +
               $"  {detectors.Count} MatchLoadTrigger(s) intact, all with trigger colliders.";
    }

    // Anything thicker than this is structure, not paint. Same number RebuildFieldBounds strips by,
    // and the measured gap is wide: 0-1 mm of tape against 88 mm blocks, nothing in between.
    private const float DecalThickness = 0.2f;

    // The top of the tiles, from their renderers, exactly as RebuildFieldBounds measures it.
    private static float FloorTop()
    {
        GameObject floorTiles = GameObject.Find("FloorTiles");
        if (floorTiles == null) return 0f;
        Renderer[] rs = floorTiles.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return 0f;
        Bounds b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return b.max.y;
    }

    private static string Path(Transform t)
    {
        string path = t.name;
        for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }
}
