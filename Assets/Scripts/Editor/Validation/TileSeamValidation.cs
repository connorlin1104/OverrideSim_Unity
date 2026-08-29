using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// The floor tile seams are on the floor, in BOTH scenes, and cost nothing they were not meant to.
//
// Three ways this silently goes wrong, and the check that catches each:
//
//   THE LITE SCENE. LiteScene is regenerated from SampleScene by Build Lite Field Scene and never
//   edited directly, so a seam pass (or a retune) that reached SampleScene is simply absent from
//   the field the "Lite Field (faster)" setting plays on until that tool is re-run. Both scenes are
//   opened and held to the same checks; the textures on disk are regenerated from the settings each
//   scene's FloorTileSeams marker recorded and compared texel for texel, so the two scenes cannot
//   quietly describe two different floors.
//
//   THE FLOOR TOP. RebuildFieldBounds and PaintedTapeValidation.FloorTop measure the floor from
//   the FloorTiles renderers, and the ground box is flush with what they measured. A generated slab
//   that does not keep the FBX mesh bounds exactly — or any extra renderer under FloorTiles, a
//   preview quad say — moves that measurement, and the robot's wheels visibly float or sink. The
//   renderer-bounds top is compared to the GroundCollider's top, and each tile's world bounds to
//   the bounds of the mesh it replaced.
//
//   THE SHADER. URP/Lit only samples _BumpMap under the _NORMALMAP keyword, and BaseShaderGUI
//   only sets that keyword from the Inspector; a normal map imported as a plain texture is read as
//   raw colour; a mesh without tangents lights a normal map wrongly; and a top face wound the
//   wrong way is culled from above, which on a floor means invisible. None of those throws
//   anywhere. Each is a check here — and the winding one is measured against RecalculateNormals,
//   not against the same derivation the slab builder used.
//
// Usage: Tools > RoboSim > Validate > Validate Tile Seams, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod TileSeamValidation.RunBatchValidate
public static class TileSeamValidation
{
    // The floor is 6 × 6. Fewer means a tile lost its mesh or the field was restructured; more
    // means something that is not a tile is being treated as one.
    public const int ExpectedTiles = 36;

    private const float UvTolerance = 1e-3f;
    private const float HeightTolerance = 1e-3f;

    [MenuItem("Tools/RoboSim/Validate/Validate Tile Seams", false, 46)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive("Validate Tile Seams", Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Validate Tile Seams", Run);

    private static string Run()
    {
        string previousScenePath = SceneManager.GetActiveScene().path;
        var checks = new ValidationUtil.Checks();
        var report = new List<string>();
        var texturesProven = new List<TileSeamSettings>();
        try
        {
            foreach (string scenePath in new[] { RoboSimPaths.MainScene, RoboSimPaths.LiteScene })
            {
                checks.That(File.Exists(scenePath),
                    $"{scenePath} is missing — LiteScene is built by Tools > RoboSim > Scenes > Build Lite Field Scene.");
                if (!File.Exists(scenePath)) continue;
                EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                CheckScene(Path.GetFileNameWithoutExtension(scenePath), checks, texturesProven, report);
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(previousScenePath) && File.Exists(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
        }

        if (checks.Failures.Count > 0)
            throw new System.InvalidOperationException(
                $"Validate Tile Seams: {checks.Failures.Count} of {checks.Count} check(s) failed:\n  " +
                string.Join("\n  ", checks.Failures));
        return $"Validate Tile Seams: PASSED ({checks.Count} checks).\n" + string.Join("\n", report);
    }

    private static void CheckScene(string tag, ValidationUtil.Checks checks, List<TileSeamSettings> texturesProven,
        List<string> report)
    {
        List<MeshRenderer> tiles = TileSeamTool.FindTiles(out GameObject floorTiles);
        checks.That(floorTiles != null,
            $"{tag}: no '{TileSeamTool.FloorTilesName}' object — the field has been restructured or the wrong " +
            "scene is open, and every check below would otherwise pass by finding nothing.");
        if (floorTiles == null) return;

        checks.That(tiles.Count == ExpectedTiles,
            $"{tag}: {tiles.Count} tile renderer(s) under {TileSeamTool.FloorTilesName}/{TileSeamTool.MeshInstancesName}, " +
            $"not {ExpectedTiles} — the floor is 6 × 6. Fewer: a tile lost its mesh (a deleted slab asset shows " +
            "up here first). More: something that is not a tile has been parented under MeshInstances.");
        if (tiles.Count == 0) return;

        // Nothing but the tiles may render under FloorTiles.
        Renderer[] everything = floorTiles.GetComponentsInChildren<Renderer>(true);
        var extras = new List<string>();
        foreach (Renderer r in everything) if (!tiles.Contains(r as MeshRenderer)) extras.Add(r.name);
        checks.That(extras.Count == 0,
            $"{tag}: {extras.Count} renderer(s) under FloorTiles that are not tiles ({string.Join(", ", extras)}). " +
            "RebuildFieldBounds and PaintedTapeValidation.FloorTop encapsulate EVERY renderer under FloorTiles " +
            "to find the floor top, so a preview quad or a debug plane there moves the measured floor.");

        // The marker: what Remove would put back.
        FloorTileSeams marker = floorTiles.GetComponent<FloorTileSeams>();
        checks.That(marker != null,
            $"{tag}: FloorTiles has no FloorTileSeams marker — the seams were never applied here. Run " +
            "Tools > RoboSim > Field & Pieces > Tile Seams… on SampleScene (or TileSeamTool.RunBatch), then " +
            "Build Lite Field Scene so LiteScene gets them too.");
        if (marker != null)
        {
            bool sameTiles = marker.tiles != null && marker.tiles.Length == tiles.Count;
            if (sameTiles) foreach (MeshRenderer t in tiles) if (System.Array.IndexOf(marker.tiles, t) < 0) sameTiles = false;
            checks.That(sameTiles,
                $"{tag}: the FloorTileSeams marker lists {(marker.tiles == null ? 0 : marker.tiles.Length)} tile(s) " +
                $"but the floor has {tiles.Count}, or lists different ones — Remove would restore the wrong tiles.");

            int unrecorded = 0;
            if (marker.originalMeshes == null || marker.originalMeshes.Length != tiles.Count) unrecorded = tiles.Count;
            else foreach (Mesh m in marker.originalMeshes) if (m == null || TileSeamTool.IsGenerated(m)) unrecorded++;
            checks.That(unrecorded == 0,
                $"{tag}: {unrecorded} tile(s) have no original FBX mesh recorded on the FloorTileSeams marker (null, " +
                "or a generated slab recorded as the 'original'). The FBX sub-meshes have generic names; this " +
                "record is the only thing that says which tile had which, and Remove cannot work without it.");
            checks.That(marker.originalMaterial != null && !TileSeamTool.IsGenerated(marker.originalMaterial),
                $"{tag}: the FloorTileSeams marker's originalMaterial is " +
                $"{(marker.originalMaterial == null ? "null" : "the generated seam material itself")} — Remove " +
                "would leave the seams on, or strip the tiles to nothing.");
        }

        // The material, and the texture/keyword plumbing URP/Lit needs to actually show a normal map.
        Material seam = AssetDatabase.LoadAssetAtPath<Material>(TileSeamTool.MaterialPath);
        checks.That(seam != null, $"{TileSeamTool.MaterialPath} is missing — Tile Seams has not been applied, or the folder was deleted.");

        var offMaterial = new List<string>();
        foreach (MeshRenderer tile in tiles)
        {
            Material[] mats = tile.sharedMaterials;
            if (mats.Length != 1 || mats[0] == null || mats[0] != seam)
                offMaterial.Add($"{tile.name} → {(mats.Length == 0 || mats[0] == null ? "none" : mats[0].name)}" +
                                (mats.Length > 1 ? $" (+{mats.Length - 1} slot(s))" : ""));
        }
        checks.That(offMaterial.Count == 0,
            $"{tag}: {offMaterial.Count} of {tiles.Count} tile(s) do not use {TileSeamTool.MaterialPath}: " +
            $"{string.Join(", ", offMaterial)}. They render as the flat sheet the seams were made to fix — and a " +
            "tile still on the embedded FBX material shares it with 386 perimeter renderers, which is why the " +
            "material is cloned per tile and never edited in place.");

        if (seam != null)
        {
            Texture albedo = seam.HasProperty("_BaseMap") ? seam.GetTexture("_BaseMap") : null;
            Texture normal = seam.HasProperty("_BumpMap") ? seam.GetTexture("_BumpMap") : null;
            checks.That(albedo != null && AssetDatabase.GetAssetPath(albedo) == TileSeamTool.AlbedoPath,
                $"{TileSeamTool.MaterialPath}: _BaseMap is {(albedo == null ? "empty" : AssetDatabase.GetAssetPath(albedo))}, " +
                $"not {TileSeamTool.AlbedoPath} — the groove darkening lives in that texture.");
            checks.That(normal != null && AssetDatabase.GetAssetPath(normal) == TileSeamTool.NormalPath,
                $"{TileSeamTool.MaterialPath}: _BumpMap is {(normal == null ? "empty" : AssetDatabase.GetAssetPath(normal))}, " +
                $"not {TileSeamTool.NormalPath} — the groove walls only catch the light through that texture.");
            checks.That(seam.IsKeywordEnabled("_NORMALMAP"),
                $"{TileSeamTool.MaterialPath}: the _NORMALMAP keyword is off. URP/Lit samples _BumpMap only in that " +
                "variant, and BaseShaderGUI sets the keyword only from the Inspector — a material written from " +
                "code must EnableKeyword itself, or the normal map is assigned and never used.");
            checks.That(seam.HasProperty("_BumpScale") && seam.GetFloat("_BumpScale") > 0f,
                $"{TileSeamTool.MaterialPath}: _BumpScale is {(seam.HasProperty("_BumpScale") ? seam.GetFloat("_BumpScale").ToString("0.##") : "absent")} — " +
                "at 0 the normal map is multiplied away.");

            var normalImporter = AssetImporter.GetAtPath(TileSeamTool.NormalPath) as TextureImporter;
            checks.That(normalImporter != null && normalImporter.textureType == TextureImporterType.NormalMap,
                $"{TileSeamTool.NormalPath} is imported as " +
                $"{(normalImporter == null ? "nothing" : normalImporter.textureType.ToString())}, not NormalMap — " +
                "the shader would read its raw RGB as a normal and light the whole floor as if it were tilted.");
            checks.That(normalImporter != null && normalImporter.wrapMode == TextureWrapMode.Clamp && normalImporter.mipmapEnabled,
                $"{TileSeamTool.NormalPath}: wrap must be Clamp with mipmaps on (is " +
                $"{(normalImporter == null ? "missing" : $"{normalImporter.wrapMode}, mipmaps {normalImporter.mipmapEnabled}")}) — " +
                "the top-face UVs run exactly 0..1, and Repeat bleeds the far edge's groove into the near one.");
            var albedoImporter = AssetImporter.GetAtPath(TileSeamTool.AlbedoPath) as TextureImporter;
            checks.That(albedoImporter != null && albedoImporter.sRGBTexture &&
                        albedoImporter.wrapMode == TextureWrapMode.Clamp && albedoImporter.mipmapEnabled,
                $"{TileSeamTool.AlbedoPath}: must import sRGB, Clamp, mipmaps on (is " +
                $"{(albedoImporter == null ? "missing" : $"sRGB {albedoImporter.sRGBTexture}, {albedoImporter.wrapMode}, mipmaps {albedoImporter.mipmapEnabled}")}).");
        }

        // The slab meshes: UVs span the tile, tangents exist, and the winding agrees with the normals.
        var noMesh = new List<string>();
        var unreadable = new List<string>();
        var uvOff = new List<string>();
        var noTangents = new List<string>();
        var windingOff = new List<string>();
        var notStatic = new List<string>();
        foreach (MeshRenderer tile in tiles)
        {
            Mesh mesh = tile.GetComponent<MeshFilter>().sharedMesh;
            if (mesh == null) { noMesh.Add(tile.name); continue; }
            if (!mesh.isReadable) { unreadable.Add(tile.name); continue; }

            Matrix4x4 toWorld = tile.transform.localToWorldMatrix;
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector2[] uvs = mesh.uv;
            if (normals.Length != vertices.Length || uvs.Length != vertices.Length)
            {
                uvOff.Add($"{tile.name} ({uvs.Length} uv / {normals.Length} normals for {vertices.Length} vertices)");
                continue;
            }

            // Top-face UV span: the vertices whose assigned normal points up in the world.
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            int topVertices = 0;
            bool anyOutside = false;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector2 uv = uvs[i];
                if (uv.x < -UvTolerance || uv.x > 1f + UvTolerance || uv.y < -UvTolerance || uv.y > 1f + UvTolerance)
                    anyOutside = true;
                if (Vector3.Dot(toWorld.MultiplyVector(normals[i]).normalized, Vector3.up) < 0.9f) continue;
                topVertices++;
                min = Vector2.Min(min, uv);
                max = Vector2.Max(max, uv);
            }
            bool spans = topVertices >= 4 && !anyOutside &&
                         Mathf.Abs(min.x) <= UvTolerance && Mathf.Abs(min.y) <= UvTolerance &&
                         Mathf.Abs(max.x - 1f) <= UvTolerance && Mathf.Abs(max.y - 1f) <= UvTolerance;
            if (!spans)
                uvOff.Add($"{tile.name} (top-face uv [{min.x:0.###}..{max.x:0.###}] × [{min.y:0.###}..{max.y:0.###}], " +
                          $"{topVertices} up-facing vertices{(anyOutside ? ", some uv outside 0..1" : "")})");

            if (mesh.tangents.Length != vertices.Length) noTangents.Add(tile.name);

            // Winding vs assigned normals, judged by Unity's own convention rather than the slab
            // builder's: RecalculateNormals derives a normal from the triangle order, and on a mesh
            // with unshared per-face vertices it must agree with the normal the builder assigned.
            Mesh probe = Object.Instantiate(mesh);
            try
            {
                probe.RecalculateNormals();
                Vector3[] wound = probe.normals;
                int disagree = 0;
                for (int i = 0; i < vertices.Length && i < wound.Length; i++)
                    if (Vector3.Dot(wound[i], normals[i]) < 0.9f) disagree++;
                if (wound.Length != vertices.Length || disagree > 0)
                    windingOff.Add($"{tile.name} ({disagree} of {vertices.Length} vertices)");
            }
            finally { Object.DestroyImmediate(probe); }

            if ((GameObjectUtility.GetStaticEditorFlags(tile.gameObject) & StaticEditorFlags.BatchingStatic) == 0)
                notStatic.Add(tile.name);
        }
        checks.That(noMesh.Count == 0, $"{tag}: {noMesh.Count} tile(s) have no mesh: {string.Join(", ", noMesh)}.");
        checks.That(unreadable.Count == 0,
            $"{tag}: {unreadable.Count} tile mesh(es) are not readable, so their UVs cannot be checked: " +
            $"{string.Join(", ", unreadable)}. The generated slab is a code-built .asset and stays readable; an " +
            "FBX sub-mesh here means the slab swap did not happen.");
        checks.That(uvOff.Count == 0,
            $"{tag}: {uvOff.Count} tile(s) whose top-face UVs do not span exactly [0,1]² (±{UvTolerance}): " +
            $"{string.Join("; ", uvOff)}. The seam texture is drawn for one whole tile; a face that does not " +
            "cover it shows a scaled or shifted pattern that no longer meets its neighbour's.");
        checks.That(noTangents.Count == 0,
            $"{tag}: {noTangents.Count} tile mesh(es) have no tangents: {string.Join(", ", noTangents)}. Without a " +
            "tangent basis URP has nothing to orient the normal map in, and the groove walls light at random.");
        checks.That(windingOff.Count == 0,
            $"{tag}: {windingOff.Count} tile mesh(es) whose triangle winding disagrees with their normals: " +
            $"{string.Join("; ", windingOff)}. A top face wound backwards is back-face culled from above — on a " +
            "floor that is invisible, and it lights as if facing down.");
        checks.That(notStatic.Count == 0,
            $"{tag}: {notStatic.Count} tile(s) without Batching Static: {string.Join(", ", notStatic)}. Only the tile " +
            "PARENTS carry static flags from the import; static batching reads the renderer's own object, so " +
            "without this the 36 identical slabs are 36 draw calls.");

        // NO TWO TILES OVERLAP. This is the regression that shipped: the slab was built at the FBX
        // mesh's own footprint, which is wider than the grid pitch (the CAD tiles interlock), so every
        // tile overlapped its neighbours by ~0.2 u. Coplanar overlap of two DIFFERENT halves of the
        // seam pattern Z-fights — the flicker, and "only half of it shows". Measured from the actual
        // top-face GEOMETRY (not renderer.bounds, which is deliberately still the full FBX bounds so
        // the floor top does not move), in the floor plane. A hair of shared edge is fine; a
        // centimetre of face is the bug.
        const float overlapTol = 0.01f;
        var footprints = new List<(string name, float minA, float maxA, float minB, float maxB)>();
        foreach (MeshRenderer tile in tiles)
        {
            Mesh mesh = tile.GetComponent<MeshFilter>().sharedMesh;
            if (mesh == null || !mesh.isReadable) continue;
            Matrix4x4 toWorld = tile.transform.localToWorldMatrix;
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (Vector3.Dot(toWorld.MultiplyVector(normals[i]).normalized, Vector3.up) < 0.9f) continue;
                Vector3 w = toWorld.MultiplyPoint3x4(vertices[i]);   // up is world Y; horizontal is X,Z
                minA = Mathf.Min(minA, w.x); maxA = Mathf.Max(maxA, w.x);
                minB = Mathf.Min(minB, w.z); maxB = Mathf.Max(maxB, w.z);
            }
            if (minA <= maxA) footprints.Add((tile.name, minA, maxA, minB, maxB));
        }
        var overlaps = new List<string>();
        for (int i = 0; i < footprints.Count; i++)
        for (int j = i + 1; j < footprints.Count; j++)
        {
            var p = footprints[i]; var q = footprints[j];
            float oa = Mathf.Min(p.maxA, q.maxA) - Mathf.Max(p.minA, q.minA);
            float ob = Mathf.Min(p.maxB, q.maxB) - Mathf.Max(p.minB, q.minB);
            if (oa > overlapTol && ob > overlapTol)
                overlaps.Add($"{p.name}∩{q.name} ({Mathf.Min(oa, ob) * 100f:0.0} mm)");
        }
        checks.That(overlaps.Count == 0,
            $"{tag}: {overlaps.Count} pair(s) of tiles overlap in the floor plane by >{overlapTol * 100f:0} cm: " +
            $"{string.Join(", ", overlaps.Take(8))}{(overlaps.Count > 8 ? ", …" : "")}. Coplanar overlap of the seam " +
            "pattern Z-fights and flickers — the slab must be built to the grid pitch, not the FBX footprint.");

        // The floor top: renderer bounds vs the ground box, and vs the meshes the slabs replaced.
        BoxCollider ground = null;
        foreach (BoxCollider box in floorTiles.GetComponentsInChildren<BoxCollider>(true))
            if (box.name == TileSeamTool.GroundColliderName) ground = box;
        checks.That(ground != null,
            $"{tag}: no '{TileSeamTool.GroundColliderName}' BoxCollider under FloorTiles — run Rebuild Floor and Wall Bounds.");
        float floorTop = TileSeamTool.FloorTop(floorTiles);
        if (ground != null)
        {
            float groundTop = ground.bounds.max.y;
            checks.That(Mathf.Abs(floorTop - groundTop) <= HeightTolerance,
                $"{tag}: the FloorTiles renderers reach Y={floorTop:F4} but the GroundCollider's top is Y={groundTop:F4} " +
                $"(tolerance {HeightTolerance}). The slab must keep the FBX mesh bounds exactly — RebuildFieldBounds " +
                "and PaintedTapeValidation measure the floor from these renderers, and a floor drawn above or below " +
                "the box the wheels ride on has the robot visibly floating or sunk.");
        }
        if (marker != null && marker.originalMeshes != null && marker.originalMeshes.Length == tiles.Count)
        {
            var moved = new List<string>();
            for (int i = 0; i < tiles.Count; i++)
            {
                Mesh original = marker.originalMeshes[i];
                MeshRenderer tile = marker.tiles != null && i < marker.tiles.Length ? marker.tiles[i] : null;
                if (original == null || tile == null) continue;
                Bounds was = WorldBounds(original.bounds, tile.transform.localToWorldMatrix);
                Bounds now = tile.bounds;
                float worst = Mathf.Max((was.min - now.min).magnitude, (was.max - now.max).magnitude);
                if (worst > HeightTolerance) moved.Add($"{tile.name} ({worst:F4})");
            }
            checks.That(moved.Count == 0,
                $"{tag}: {moved.Count} tile(s) whose world bounds differ from the FBX mesh they replaced by more than " +
                $"{HeightTolerance}: {string.Join(", ", moved)}. The slab is supposed to be that mesh's bounds, bit for bit.");
        }

        // The textures on disk are what the generator makes at the settings this scene recorded.
        // Proven once per distinct settings — the second scene normally records the same numbers.
        if (marker != null)
        {
            bool proven = false;
            foreach (TileSeamSettings s in texturesProven) if (s.SameAs(marker.settings)) proven = true;
            if (!proven)
            {
                TileSeamTool.GeneratePixels(marker.settings, TileSeamTool.TextureSize, out Color32[] albedo, out Color32[] normal);
                CompareWithDisk(tag, TileSeamTool.AlbedoPath, albedo, marker.settings, checks);
                CompareWithDisk(tag, TileSeamTool.NormalPath, normal, marker.settings, checks);
                texturesProven.Add(marker.settings);
            }
        }

        report.Add($"  {tag}: {tiles.Count} tiles on {(seam != null ? seam.name : "no material")}, floor top Y={floorTop:F4}" +
                   (marker != null ? $", seams at {marker.settings}" : ", no seams"));
    }

    // PNG is lossless, so decode(encode(pixels)) is pixels: the file on disk must decode to exactly
    // what the generator produces for the recorded settings, or the scene and the assets disagree.
    private static void CompareWithDisk(string tag, string path, Color32[] expected, TileSeamSettings settings,
        ValidationUtil.Checks checks)
    {
        if (!File.Exists(path))
        {
            checks.That(false, $"{path} is missing on disk — {tag}'s FloorTileSeams marker says it was baked at {settings}.");
            return;
        }
        int size = TileSeamTool.TextureSize;
        var texture = new Texture2D(2, 2, TextureFormat.RGB24, false);
        try
        {
            bool loaded = texture.LoadImage(File.ReadAllBytes(path), false);
            checks.That(loaded && texture.width == size && texture.height == size,
                $"{path}: {(loaded ? $"{texture.width}×{texture.height}" : "failed to decode")}, expected {size}×{size}.");
            if (!loaded || texture.width != size || texture.height != size) return;

            Color32[] disk = texture.GetPixels32();
            int differing = 0, first = -1;
            for (int i = 0; i < expected.Length; i++)
            {
                if (disk[i].r == expected[i].r && disk[i].g == expected[i].g && disk[i].b == expected[i].b) continue;
                if (first < 0) first = i;
                differing++;
            }
            string where = first < 0 ? "" :
                $", first at ({first % size}, {first / size}): disk ({disk[first].r},{disk[first].g},{disk[first].b}) " +
                $"vs generated ({expected[first].r},{expected[first].g},{expected[first].b})";
            checks.That(differing == 0,
                $"{path} is not what the generator produces at the settings {tag} recorded ({settings}): {differing} of " +
                $"{expected.Length} texel(s) differ{where}. Either the seams were retuned in one scene and Build Lite " +
                "Field Scene was not re-run, the PNG was edited or re-saved by something else, or the generator has " +
                "stopped being deterministic.");
        }
        finally { Object.DestroyImmediate(texture); }
    }

    private static Bounds WorldBounds(Bounds local, Matrix4x4 toWorld)
    {
        Vector3 min = local.min, max = local.max;
        var bounds = new Bounds(toWorld.MultiplyPoint3x4(min), Vector3.zero);
        for (int c = 1; c < 8; c++)
        {
            Vector3 corner = new Vector3((c & 1) == 0 ? min.x : max.x, (c & 2) == 0 ? min.y : max.y, (c & 4) == 0 ? min.z : max.z);
            bounds.Encapsulate(toWorld.MultiplyPoint3x4(corner));
        }
        return bounds;
    }
}
