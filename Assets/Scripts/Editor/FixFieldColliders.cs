using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Builds a solid, thick collision shell around the field and removes the fragile imported
// colliders it replaces.
//
// The imported floor is thin (1cm) tile colliders and the walls are thin concave mesh panels,
// so game pieces make only shallow contact, sink/wedge into the floor (the robot then rides
// over them), and fast pieces tunnel through the walls. This tool:
//   - strips every MeshCollider under FloorTiles and Walls (the thin panels pieces clip through),
//   - adds a thick ground box under FloorTiles,
//   - adds four thick perimeter wall boxes under Perimeter, set slightly inward from the floor edge.
//
// Re-runnable: it removes its previous output first. The collider host objects are given a
// world-identity transform (via SetParent worldPositionStays), so BoxCollider centers are plain
// world coordinates even though the field root is rotated -90 X.
public class FixFieldColliders
{
    private const float GroundThickness = 2f;
    private const float WallThickness = 2f;
    private const float WallHeight = 3f;   // shorter so the walls clear the field rollers
    private const float WallInset = 0.2f;  // pull walls this far in from the floor edge, toward center

    private const string GroundName = "GroundCollider";
    private const string WallsName = "WallColliders";
    private const string LegacyRootName = "FieldPhysicsBounds"; // from the earlier version of this tool

    [MenuItem("Tools/RoboSim/Field/Rebuild Floor and Wall Bounds", false, 1)]
    private static void SetupBounds()
    {
        GameObject floorTiles = GameObject.Find("FloorTiles");
        GameObject perimeter = GameObject.Find("Perimeter");
        GameObject walls = GameObject.Find("Walls");
        if (floorTiles == null || perimeter == null)
        {
            EditorUtility.DisplayDialog("Setup Field Physics Bounds",
                "Couldn't find 'FloorTiles' and/or 'Perimeter' in the scene.", "OK");
            return;
        }

        // Floor world bounds from its renderers (robust to the collider removal below).
        if (!TryGetRendererBounds(floorTiles, out Bounds floor))
        {
            EditorUtility.DisplayDialog("Setup Field Physics Bounds",
                "FloorTiles has no renderers to measure the floor from.", "OK");
            return;
        }

        // Clean up previous output so re-running doesn't stack duplicates.
        DestroyIfExists(LegacyRootName);
        DestroyChildIfExists(floorTiles.transform, GroundName);
        DestroyChildIfExists(perimeter.transform, WallsName);

        // Strip the thin imported colliders that the solid boxes replace. On the floor remove
        // ALL colliders (the 1cm tile boxes too) — leaving them coplanar with the new ground box
        // makes the solver flip-flop and wedge pieces into the floor. On the walls remove the
        // thin mesh panels.
        int removed = RemoveAllColliders(floorTiles);
        if (walls != null) removed += RemoveMeshColliders(walls);

        float floorTop = floor.max.y;
        Vector3 c = floor.center;
        float width = floor.size.x;   // X span
        float depth = floor.size.z;   // Z span
        float minX = floor.min.x, maxX = floor.max.x;
        float minZ = floor.min.z, maxZ = floor.max.z;

        // Ground: top flush with the floor surface, extending downward, parented under FloorTiles.
        GameObject ground = CreateWorldIdentityChild(floorTiles.transform, GroundName);
        AddBox(ground, new Vector3(c.x, floorTop - GroundThickness * 0.5f, c.z),
                       new Vector3(width, GroundThickness, depth));

        // Four perimeter walls under Perimeter. Inner faces sit WallInset in from the floor edge.
        // The +/-X walls run the full Z length so they overlap the +/-Z walls at the corners.
        GameObject wallHost = CreateWorldIdentityChild(perimeter.transform, WallsName);
        float wallCenterY = floorTop + WallHeight * 0.5f;
        float longDepth = depth + WallThickness * 2f;

        AddBox(wallHost, new Vector3(maxX - WallInset + WallThickness * 0.5f, wallCenterY, c.z),
                         new Vector3(WallThickness, WallHeight, longDepth));   // +X
        AddBox(wallHost, new Vector3(minX + WallInset - WallThickness * 0.5f, wallCenterY, c.z),
                         new Vector3(WallThickness, WallHeight, longDepth));   // -X
        AddBox(wallHost, new Vector3(c.x, wallCenterY, maxZ - WallInset + WallThickness * 0.5f),
                         new Vector3(width, WallHeight, WallThickness));       // +Z
        AddBox(wallHost, new Vector3(c.x, wallCenterY, minZ + WallInset - WallThickness * 0.5f),
                         new Vector3(width, WallHeight, WallThickness));       // -Z

        EditorSceneManager.MarkSceneDirty(floorTiles.scene);
        Selection.activeGameObject = wallHost;

        Debug.Log($"Setup Field Physics Bounds: removed {removed} mesh collider(s) from floor/walls; " +
                  $"added ground box under FloorTiles and 4 walls (inset {WallInset}) under Perimeter. " +
                  $"floorTop={floorTop:F2}, footprint {width:F1}×{depth:F1}.");
    }

    private static bool TryGetRendererBounds(GameObject go, out Bounds bounds)
    {
        bounds = new Bounds();
        Renderer[] rs = go.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return false;
        bounds = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) bounds.Encapsulate(rs[i].bounds);
        return true;
    }

    private static int RemoveMeshColliders(GameObject go)
    {
        MeshCollider[] cols = go.GetComponentsInChildren<MeshCollider>(true);
        foreach (MeshCollider col in cols) Undo.DestroyObjectImmediate(col);
        return cols.Length;
    }

    private static int RemoveAllColliders(GameObject go)
    {
        Collider[] cols = go.GetComponentsInChildren<Collider>(true);
        foreach (Collider col in cols) Undo.DestroyObjectImmediate(col);
        return cols.Length;
    }

    private static void DestroyIfExists(string name)
    {
        GameObject go = GameObject.Find(name);
        if (go != null) Undo.DestroyObjectImmediate(go);
    }

    private static void DestroyChildIfExists(Transform parent, string name)
    {
        Transform child = parent.Find(name);
        if (child != null) Undo.DestroyObjectImmediate(child.gameObject);
    }

    // A child whose WORLD transform is identity, so its BoxCollider centers are world coordinates
    // even under the rotated field hierarchy.
    private static GameObject CreateWorldIdentityChild(Transform parent, string name)
    {
        GameObject go = new GameObject(name); // spawns at world origin, identity rotation
        Undo.RegisterCreatedObjectUndo(go, "Setup Field Physics Bounds");
        go.transform.SetParent(parent, true); // keep world-identity; local compensates for rotation
        return go;
    }

    private static void AddBox(GameObject host, Vector3 worldCenter, Vector3 size)
    {
        BoxCollider box = Undo.AddComponent<BoxCollider>(host);
        box.center = worldCenter;
        box.size = size;
    }
}
