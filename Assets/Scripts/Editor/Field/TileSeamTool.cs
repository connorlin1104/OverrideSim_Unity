using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Draws the seams between the 36 floor tiles.
//
// The floor is 36 foam tiles imported as 36 FBX sub-meshes sharing one flat grey material, so in
// the game it renders as a single sheet: no edges, no sense of scale, nothing for the eye to track
// while the robot moves. Real foam tiles show a dark groove where their interlocking tabs meet.
// This puts that groove on the floor as a texture — an albedo that darkens the groove and a normal
// map that catches the light on its walls — and leaves the physics alone: the tiles have no
// colliders (RebuildFieldBounds replaced them with one ground box), so nothing the robot rides on
// changes and no dynamics test has to be re-measured.
//
// Three facts about the field shaped the tool:
//   • The tile material is EMBEDDED in OverrideFieldVersion3.fbx (read-only) and is the same
//     material the 386 perimeter renderers use. So it is cloned into Assets/Materials/FloorTiles
//     and assigned to the 36 tile renderers only — never swapped scene-wide, or the perimeter grows
//     seams too. Same recipe as TransparentMaterialTool: new Material(src) keeps URP/Lit and the
//     base colour, and the source's guid:fileID goes into the clone's importer userData.
//   • The FBX sub-meshes carry whatever UVs the CAD exporter produced, so each tile's mesh is
//     replaced by a generated slab whose top face runs u along world +X and v along world +Z across
//     that tile's own footprint. The slab is built from the ORIGINAL mesh's local bounds and keeps
//     them exactly: RebuildFieldBounds and PaintedTapeValidation measure the floor top from these
//     renderers, and the ground box is flush with it. Nothing else may ever render under
//     FloorTiles (no preview quads) for the same reason — every renderer there IS the floor.
//   • Only the half of the interlock inside each tile is drawn — the neighbour's tabs reaching in
//     to tabDepth on the bottom and left edges, to 1-tabDepth on the top and right, alternating
//     along the edge — so one texture on all 36 tiles, all facing the same way, lines up across
//     every seam and completes the neighbour's half. The wave is phased so that no step of it
//     lands on a tile corner (see SeamSegments): a step there would be split between two tiles
//     and come out half as wide as the rest of the groove.
//
// The texture generator is pure: a hash of the texel position is its only noise, so the same
// settings always produce byte-identical PNGs, and TileSeamValidation regenerates them from the
// settings the FloorTileSeams marker recorded and compares with what is on disk.
//
// Usage: Tools > RoboSim > Field & Pieces > Tile Seams… (five sliders, Apply, Remove).
// Batch: -executeMethod TileSeamTool.RunBatch bakes SampleScene at the defaults. Then run
// BuildLiteFieldScene.RunBatch: LiteScene is regenerated from SampleScene and never edited directly,
// so that is the only way the seams reach it.
public static class TileSeamTool
{
    private const string FolderRoot = "Assets/Materials";
    public const string FolderPath = "Assets/Materials/FloorTiles";
    public const string AlbedoPath = FolderPath + "/TileSeam_Albedo.png";
    public const string NormalPath = FolderPath + "/TileSeam_Normal.png";
    public const string MaterialPath = FolderPath + "/FloorTile_Seams.mat";
    public const string SlabPath = FolderPath + "/FloorTileSlab.asset";

    public const string FloorTilesName = "FloorTiles";
    public const string MeshInstancesName = "MeshInstances";
    public const string GroundColliderName = "GroundCollider";

    // 1024 texels across a 598 mm tile is 0.6 mm per texel: a 5 mm groove is eight texels wide,
    // enough for its anti-aliased edge and a flat bottom. 2048 would quadruple the two textures for
    // detail nobody sees from the chase camera.
    public const int TextureSize = 1024;

    // Foam is matte. The FBX import leaves URP/Lit's smoothness at its default 0.5, which puts a
    // broad highlight on the floor that washes the seams out under the field lights.
    private const float Smoothness = 0.15f;
    private const float BumpScale = 1f;

    // Anti-aliasing half-width of the groove edge, in texels. 1.5 gives a three-texel ramp: the
    // Sobel below reads the ramp as the groove wall, so this is also what sets how wide the lit
    // wall is.
    private const float AntiAliasTexels = 1.5f;

    // The foam speckle: each 4-texel cell (2.3 mm — foam grain, not pixel noise that mips to a flat
    // grey at any distance) is somewhere between white and 2 × SpeckleAmplitude below it. It can
    // only go darker because an 8-bit albedo cannot go above white, so the floor averages
    // SpeckleAmplitude darker than the bare base colour — 3 %, below what the eye can tell on a
    // mid grey. The seed is a constant on purpose: the generator must be a pure function of the
    // settings.
    private const float SpeckleAmplitude = 0.03f;
    private const int SpeckleCell = 4;
    private const uint SpeckleSeed = 0x5EA7F00Du;

    private const string UndoName = "Tile Seams";

    // --- Finding the tiles ---------------------------------------------------------------------

    // Structural, not by name pattern or material: FloorTiles (the FieldSetupTools lookup) → its
    // MeshInstances child → every MeshRenderer with a mesh under that. The tile objects are named
    // Body1.NNN, which is also what a thousand other field parts are called, and the material they
    // use is also the perimeter's — neither identifies a tile. Hierarchy order, so the marker's
    // arrays line up with the Inspector.
    public static List<MeshRenderer> FindTiles(out GameObject floorTiles)
    {
        var tiles = new List<MeshRenderer>();
        floorTiles = GameObject.Find(FloorTilesName);
        if (floorTiles == null) return tiles;
        Transform instances = floorTiles.transform.Find(MeshInstancesName);
        if (instances == null) return tiles;
        foreach (MeshRenderer renderer in instances.GetComponentsInChildren<MeshRenderer>(true))
        {
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) continue;
            tiles.Add(renderer);
        }
        return tiles;
    }

    // --- Apply / Remove / batch ----------------------------------------------------------------

    // Returns how many tiles were given seams; 0 when the open scene has no floor. Throws when the
    // floor is in a state the tool cannot honestly record (see the messages) rather than guessing.
    // Re-runnable: the originals come from the existing FloorTileSeams marker when there is one,
    // the material and slab assets are updated in place (same GUIDs, so LiteScene's references stay
    // valid), and the textures are simply regenerated.
    public static int Apply(TileSeamSettings settings, bool useUndo)
    {
        List<MeshRenderer> tiles = FindTiles(out GameObject floorTiles);
        if (tiles.Count == 0) return 0;
        settings = settings.Clamped();

        // The originals: what each tile had BEFORE any seam pass. On a re-apply the tile already
        // carries a slab and the seam material, so the truth lives on the marker, not the tile.
        FloorTileSeams marker = floorTiles.GetComponent<FloorTileSeams>();
        var originalMeshes = new Mesh[tiles.Count];
        var originalFlags = new int[tiles.Count];
        Material originalMaterial = null;
        for (int i = 0; i < tiles.Count; i++)
        {
            MeshRenderer tile = tiles[i];
            MeshFilter filter = tile.GetComponent<MeshFilter>();
            int at = marker != null && marker.tiles != null ? System.Array.IndexOf(marker.tiles, tile) : -1;
            bool hasMesh = at >= 0 && marker.originalMeshes != null && at < marker.originalMeshes.Length &&
                           marker.originalMeshes[at] != null;
            bool hasFlags = at >= 0 && marker.originalStaticFlags != null && at < marker.originalStaticFlags.Length;

            originalMeshes[i] = hasMesh ? marker.originalMeshes[at] : filter.sharedMesh;
            originalFlags[i] = hasFlags ? marker.originalStaticFlags[at]
                                        : (int)GameObjectUtility.GetStaticEditorFlags(tile.gameObject);
            Material current = at >= 0 && marker.originalMaterial != null ? marker.originalMaterial : tile.sharedMaterial;

            if (IsGenerated(originalMeshes[i]))
                throw new System.InvalidOperationException(
                    $"Tile Seams: '{tile.name}' already carries the generated slab but FloorTiles has no " +
                    "FloorTileSeams record of the FBX mesh it replaced. The marker was deleted by hand; " +
                    "restore the scene from version control before re-applying, or Remove has nothing to " +
                    "put back.");
            if (current == null || IsGenerated(current))
                throw new System.InvalidOperationException(
                    $"Tile Seams: '{tile.name}' has {(current == null ? "no material" : "the seam material")} " +
                    "and no FloorTileSeams record of the embedded material it replaced — the marker was " +
                    "deleted by hand. Restore the scene from version control before re-applying.");
            if (originalMaterial == null) originalMaterial = current;
            else if (current != originalMaterial)
                throw new System.InvalidOperationException(
                    $"Tile Seams: the floor tiles do not share one material ('{tile.name}' uses " +
                    $"'{current.name}', an earlier tile uses '{originalMaterial.name}'). One seam material " +
                    "is cloned from the tiles' one embedded material and keeps its base colour; with two " +
                    "sources one set of tiles would silently take the other's colour.");
        }
        if (!originalMaterial.HasProperty("_BaseMap") || !originalMaterial.HasProperty("_BumpMap"))
            throw new System.InvalidOperationException(
                $"Tile Seams: the tile material '{originalMaterial.name}' uses shader " +
                $"'{originalMaterial.shader.name}', which has no _BaseMap/_BumpMap. The seams are written " +
                "into URP/Lit's albedo and normal slots; another shader needs its own property names here.");

        EnsureFolder();

        GeneratePixels(settings, TextureSize, out Color32[] albedo, out Color32[] normal);
        WritePng(AlbedoPath, albedo, TextureSize);
        WritePng(NormalPath, normal, TextureSize);
        Texture2D albedoTex = ImportTexture(AlbedoPath, normalMap: false);
        Texture2D normalTex = ImportTexture(NormalPath, normalMap: true);

        Material seam = EnsureSeamMaterial(originalMaterial, albedoTex, normalTex);
        Mesh[] slabs = EnsureSlabAssets(tiles, originalMeshes, out int shared);
        AssetDatabase.SaveAssets();

        for (int i = 0; i < tiles.Count; i++)
        {
            MeshRenderer tile = tiles[i];
            MeshFilter filter = tile.GetComponent<MeshFilter>();
            if (useUndo)
            {
                Undo.RecordObject(filter, UndoName);
                Undo.RecordObject(tile, UndoName);
                Undo.RecordObject(tile.gameObject, UndoName);
            }
            filter.sharedMesh = slabs[i];
            tile.sharedMaterials = new[] { seam };
            // Only the tile PARENTS were flagged static (the FBX importer's doing), and static
            // batching reads the renderer's own GameObject — so 36 identical slabs with one material
            // were drawn as 36 draw calls. Batching Static alone: the parents already carry the rest.
            GameObjectUtility.SetStaticEditorFlags(tile.gameObject,
                (StaticEditorFlags)originalFlags[i] | StaticEditorFlags.BatchingStatic);
            EditorUtility.SetDirty(filter);
            EditorUtility.SetDirty(tile);
            EditorUtility.SetDirty(tile.gameObject);
        }

        marker = MechanismBuildUtil.AddOrGet<FloorTileSeams>(floorTiles, useUndo);
        if (useUndo) Undo.RegisterCompleteObjectUndo(marker, UndoName);
        marker.tiles = tiles.ToArray();
        marker.originalMeshes = originalMeshes;
        marker.originalMaterial = originalMaterial;
        marker.originalStaticFlags = originalFlags;
        marker.settings = settings;
        EditorUtility.SetDirty(marker);
        EditorSceneManager.MarkSceneDirty(floorTiles.scene);

        Debug.Log($"Tile Seams: {tiles.Count} tile(s) → {MaterialPath} (albedo + normal {TextureSize}², " +
                  $"{settings}); slab {(shared == tiles.Count ? "shared by all" : $"shared by {shared}, per-tile for {tiles.Count - shared}")}; " +
                  $"floor top Y={FloorTop(floorTiles):F4}. Save the scene, then Build Lite Field Scene.");
        return tiles.Count;
    }

    // Puts the FBX meshes, the embedded material and the static flags back from the marker's
    // record, then removes the marker. The generated assets stay on disk — a later Apply reuses
    // them, and nothing else references them.
    public static int Remove(bool useUndo)
    {
        GameObject floorTiles = GameObject.Find(FloorTilesName);
        FloorTileSeams marker = floorTiles != null ? floorTiles.GetComponent<FloorTileSeams>() : null;
        if (marker == null) return 0;

        int restored = 0;
        int count = marker.tiles != null ? marker.tiles.Length : 0;
        for (int i = 0; i < count; i++)
        {
            MeshRenderer tile = marker.tiles[i];
            if (tile == null) continue;
            MeshFilter filter = tile.GetComponent<MeshFilter>();
            if (useUndo)
            {
                if (filter != null) Undo.RecordObject(filter, UndoName);
                Undo.RecordObject(tile, UndoName);
                Undo.RecordObject(tile.gameObject, UndoName);
            }
            if (filter != null && marker.originalMeshes != null && i < marker.originalMeshes.Length &&
                marker.originalMeshes[i] != null)
                filter.sharedMesh = marker.originalMeshes[i];
            if (marker.originalMaterial != null) tile.sharedMaterials = new[] { marker.originalMaterial };
            if (marker.originalStaticFlags != null && i < marker.originalStaticFlags.Length)
                GameObjectUtility.SetStaticEditorFlags(tile.gameObject, (StaticEditorFlags)marker.originalStaticFlags[i]);
            if (filter != null) EditorUtility.SetDirty(filter);
            EditorUtility.SetDirty(tile);
            EditorUtility.SetDirty(tile.gameObject);
            restored++;
        }

        if (useUndo) Undo.DestroyObjectImmediate(marker);
        else Object.DestroyImmediate(marker);
        EditorSceneManager.MarkSceneDirty(floorTiles.scene);
        Debug.Log($"Tile Seams: removed — {restored} tile(s) back on their FBX meshes and material.");
        return restored;
    }

    // Batch entry point for -executeMethod: opens SampleScene, applies the defaults, saves; throws
    // on failure (nonzero exit). LiteScene is NOT touched here — BuildLiteFieldScene.RunBatch
    // regenerates it from this scene and is the only writer it has.
    public static void RunBatch()
    {
        var scene = EditorSceneManager.OpenScene(RoboSimPaths.MainScene, OpenSceneMode.Single);
        int tiles = Apply(TileSeamSettings.Defaults, useUndo: false);
        if (tiles == 0)
            throw new System.InvalidOperationException(
                $"Tile Seams: no floor tiles found under {FloorTilesName}/{MeshInstancesName} in {RoboSimPaths.MainScene}.");
        if (!EditorSceneManager.SaveScene(scene))
            throw new System.InvalidOperationException($"Tile Seams: failed to save {RoboSimPaths.MainScene}.");
        Debug.Log($"Tile Seams: seams applied to {tiles} tile(s) at the defaults; {RoboSimPaths.MainScene} saved. " +
                  "Run BuildLiteFieldScene.RunBatch to carry them into LiteScene.");
    }

    // --- Textures: a pure function of the settings ---------------------------------------------

    // Both textures for one tile, as pixel rows bottom-up (what Texture2D.SetPixels32 and
    // EncodeToPNG expect; row 0 is v = 0). Deterministic: the only randomness is a hash of the
    // texel position with a fixed seed, so the validator can call this again and expect the PNG on
    // disk to decode to exactly these bytes.
    public static void GeneratePixels(TileSeamSettings settings, int size, out Color32[] albedo, out Color32[] normal)
    {
        settings = settings.Clamped();
        float[] groove = GrooveMask(settings, size);   // 1 at the groove floor, 0 on the foam
        albedo = new Color32[size * size];
        normal = new Color32[size * size];

        float Height(int x, int y)
        {
            x = Mathf.Clamp(x, 0, size - 1);
            y = Mathf.Clamp(y, 0, size - 1);
            return 1f - groove[y * size + x];
        }

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = y * size + x;
                float g = groove[i];

                // Albedo: white foam with speckle, so the material's own base colour (the FBX grey)
                // is what shows; the groove is that times grooveShade.
                float speckle = 1f - 2f * SpeckleAmplitude +
                                2f * SpeckleAmplitude * Hash01(x / SpeckleCell, y / SpeckleCell, SpeckleSeed);
                float shade = Mathf.Lerp(1f, settings.grooveShade, g);
                byte c = ToByte(speckle * shade);
                albedo[i] = new Color32(c, c, c, 255);

                // Normal: Sobel of the height field (height 1 on the foam, 0 at the groove floor),
                // tilted by grooveDepth. A slope that descends toward +x faces +x, hence the minus.
                // +y in the texture is +v, which is the OpenGL/Unity green-up convention.
                float gx = (Height(x + 1, y - 1) + 2f * Height(x + 1, y) + Height(x + 1, y + 1)
                          - Height(x - 1, y - 1) - 2f * Height(x - 1, y) - Height(x - 1, y + 1)) / 8f;
                float gy = (Height(x - 1, y + 1) + 2f * Height(x, y + 1) + Height(x + 1, y + 1)
                          - Height(x - 1, y - 1) - 2f * Height(x, y - 1) - Height(x + 1, y - 1)) / 8f;
                Vector3 n = new Vector3(-gx * settings.grooveDepth, -gy * settings.grooveDepth, 1f).normalized;
                normal[i] = new Color32(ToByte(n.x * 0.5f + 0.5f), ToByte(n.y * 0.5f + 0.5f),
                                        ToByte(n.z * 0.5f + 0.5f), 255);
            }
        }
    }

    // An axis-aligned piece of the seam path, in tile UV units, a ≤ b on both axes.
    private struct Segment
    {
        public float ax, ay, bx, by;

        public float SqrDistance(float u, float v)
        {
            float dx = Mathf.Max(Mathf.Max(ax - u, u - bx), 0f);
            float dy = Mathf.Max(Mathf.Max(ay - v, v - by), 0f);
            return dx * dx + dy * dy;
        }
    }

    // The half of the interlock that belongs inside this tile.
    //
    // The boundary between two tiles is one square wave: alternately +tabDepth into one tile and
    // -tabDepth into the other, tabsPerEdge half-periods along the edge, with a step back across
    // the edge line between each pair. Each tile draws only the parts on its own side — the flat
    // runs at tabDepth from its edge plus, at every step, the half of the step from the edge line
    // to that run — and clips the groove at the edge. Parity decides which runs are its own: the
    // bottom and left edges take the even pieces, the top and right the odd ones, so that when the
    // same texture sits on the tile above, ITS bottom edge (even, in its own frame) complements
    // THIS tile's top edge (odd) and the wave is continuous across the seam. Both tiles draw a half
    // of every step and the halves meet at the edge line, so the groove never breaks.
    //
    // The wave is phased so its steps sit at (k + ½)/n, never at a tile corner. A step ON the
    // corner would lie along the perpendicular seam as well, so each of the two tiles beside it
    // could only draw half the groove's width, and the two halves would only touch corner to
    // corner (measured: the groove network of a 3 × 3 tiling fell into four pieces). With this
    // phase the corner falls in the middle of a piece, that piece is shared by the two tiles
    // meeting there — half of it in each, at the same level — and the two seams cross at full
    // width one tabDepth inside the notch-notch tile, the way real tiles do. Being one piece at
    // both ends of the edge is also why tabsPerEdge must be even: the wave has to come back to
    // the level it started on by the far corner, or it jumps there.
    private static List<Segment> SeamSegments(TileSeamSettings s)
    {
        var segments = new List<Segment>();
        float a = s.tabDepth;
        int n = s.tabsPerEdge;   // even, by TileSeamSettings.Clamped
        void Add(float ax, float ay, float bx, float by) =>
            segments.Add(new Segment { ax = Mathf.Min(ax, bx), ay = Mathf.Min(ay, by), bx = Mathf.Max(ax, bx), by = Mathf.Max(ay, by) });

        // The flat runs: piece p spans (p - ½)/n .. (p + ½)/n, clipped to the tile, so pieces 0
        // and n are the half-pieces at the corners.
        for (int p = 0; p <= n; p++)
        {
            float t0 = Mathf.Max(0f, (p - 0.5f) / n), t1 = Mathf.Min(1f, (p + 0.5f) / n);
            if ((p & 1) == 0)
            {
                Add(t0, a, t1, a);                 // bottom edge, v = 0: the neighbour's tab reaches in
                Add(a, t0, a, t1);                 // left edge, u = 0
            }
            else
            {
                Add(t0, 1f - a, t1, 1f - a);       // top edge, v = 1
                Add(1f - a, t0, 1f - a, t1);       // right edge, u = 1
            }
        }
        // The steps: this tile's half of every one, on all four edges.
        for (int k = 0; k < n; k++)
        {
            float t = (k + 0.5f) / n;
            Add(t, 0f, t, a);                      // bottom
            Add(t, 1f - a, t, 1f);                 // top
            Add(0f, t, a, t);                      // left
            Add(1f - a, t, 1f, t);                 // right
        }
        return segments;
    }

    // Groove coverage per texel: 1 inside the groove, 0 outside, a linear ramp AntiAliasTexels
    // either side of the groove edge. Only texels within reach of an edge are visited — the
    // interior of the tile is foam by construction.
    private static float[] GrooveMask(TileSeamSettings s, int size)
    {
        List<Segment> segments = SeamSegments(s);
        float halfWidth = s.grooveWidth * 0.5f;
        float aa = AntiAliasTexels / size;
        float band = s.tabDepth + halfWidth + aa + 1f / size;
        var mask = new float[size * size];

        for (int y = 0; y < size; y++)
        {
            float v = (y + 0.5f) / size;
            bool nearV = v < band || v > 1f - band;
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size;
                if (!nearV && u >= band && u <= 1f - band) continue;

                float sqr = float.MaxValue;
                for (int i = 0; i < segments.Count; i++)
                {
                    float d = segments[i].SqrDistance(u, v);
                    if (d < sqr) sqr = d;
                }
                float dist = Mathf.Sqrt(sqr);
                mask[y * size + x] = Mathf.Clamp01((halfWidth + aa - dist) / (2f * aa));
            }
        }
        return mask;
    }

    // Integer hash → [0,1). Order-independent and seedable, which System.Random is not across a
    // loop that might one day be reordered or parallelised.
    private static float Hash01(int x, int y, uint seed)
    {
        unchecked
        {
            uint h = seed ^ 0x9E3779B9u;
            h ^= (uint)x * 0x85EBCA6Bu; h ^= h >> 15; h *= 0xC2B2AE35u;
            h ^= (uint)y * 0x27D4EB2Fu; h ^= h >> 13; h *= 0x165667B1u;
            h ^= h >> 16;
            return (h & 0x00FFFFFFu) / 16777216f;
        }
    }

    private static byte ToByte(float unit) => (byte)Mathf.Clamp(Mathf.RoundToInt(unit * 255f), 0, 255);

    private static void WritePng(string path, Color32[] pixels, int size)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGB24, false);
        try
        {
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
        }
        finally { Object.DestroyImmediate(texture); }
    }

    // Import settings the shader depends on. NormalMap type is not cosmetic: without it URP reads
    // the PNG's raw RGB as a normal and lights the whole floor as if it were tilted. Clamp because
    // the top-face UVs run exactly 0..1 and Repeat would bleed the far edge's groove into the near
    // one under bilinear filtering.
    private static Texture2D ImportTexture(string path, bool normalMap)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
            throw new System.InvalidOperationException($"Tile Seams: {path} did not import as a texture.");
        importer.textureType = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
        importer.sRGBTexture = !normalMap;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.mipmapEnabled = true;
        importer.alphaSource = TextureImporterAlphaSource.None;
        if (importer.maxTextureSize < TextureSize) importer.maxTextureSize = TextureSize;
        importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path)
               ?? throw new System.InvalidOperationException($"Tile Seams: {path} failed to load after import.");
    }

    // --- Material -------------------------------------------------------------------------------

    // One clone of the tiles' embedded material, updated in place on later runs so its GUID (and
    // LiteScene's reference to it) never changes.
    private static Material EnsureSeamMaterial(Material source, Texture2D albedo, Texture2D normal)
    {
        Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
        bool created = material == null;
        if (created) material = new Material(source) { name = Path.GetFileNameWithoutExtension(MaterialPath) };

        material.SetTexture("_BaseMap", albedo);
        material.SetTextureScale("_BaseMap", Vector2.one);
        material.SetTextureOffset("_BaseMap", Vector2.zero);
        material.SetTexture("_BumpMap", normal);
        material.SetFloat("_BumpScale", BumpScale);
        // BaseShaderGUI only sets this keyword from the Inspector; a material written from code has
        // to switch the normal-map variant on itself or _BumpMap is simply never sampled.
        material.EnableKeyword("_NORMALMAP");
        if (material.HasProperty("_Smoothness"))
            material.SetFloat("_Smoothness", Mathf.Min(material.GetFloat("_Smoothness"), Smoothness));

        // Created only once it is fully set up, the way TransparentMaterialTool does it: the asset
        // file is what a reimport reads back, so it must already hold the textures and the keyword.
        if (created) AssetDatabase.CreateAsset(material, MaterialPath);
        else EditorUtility.SetDirty(material);

        // Remember which embedded material this came from (guid:fileID in the importer's userData),
        // so the read-only original is findable by identity.
        AssetImporter importer = AssetImporter.GetAtPath(MaterialPath);
        if (importer != null && string.IsNullOrEmpty(importer.userData) &&
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long fileId))
        {
            // SaveAndReimport re-reads the .mat from disk, which would throw away the edits above
            // on a material that already existed — flush them first.
            AssetDatabase.SaveAssets();
            importer.userData = $"{guid}:{fileId}";
            importer.SaveAndReimport();
            material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath)
                       ?? throw new System.InvalidOperationException($"Tile Seams: {MaterialPath} failed to load after import.");
        }
        return material;
    }

    // --- Slab mesh ------------------------------------------------------------------------------

    internal struct SlabData
    {
        public Vector3[] vertices;
        public Vector3[] normals;
        public Vector2[] uvs;
        public Vector4[] tangents;
        public int[] triangles;
        public Bounds bounds;
        public int topFace;
    }

    private static readonly Vector3[] FaceNormals =
        { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
    private static readonly Vector3[] FaceRights =
        { Vector3.forward, Vector3.forward, Vector3.right, Vector3.right, Vector3.right, Vector3.right };

    // A box exactly filling `local` (the FBX mesh's own bounds, in the tile's mesh space): 24
    // vertices, four per face, per-face normals. The top face is whichever face's world normal is
    // most +Y — decided from the tile's transform, not assumed, because the field root is rotated
    // -90° about X and mesh-local +Z is what points up here. Its UVs are each vertex's place in the
    // tile's own world XZ footprint (u along +X, v along +Z), so every tile's pattern faces the same
    // way whatever its local frame does. The other five faces get (0.5, 0.5): the centre of the
    // texture is flat foam, so they are plain grey with an unperturbed normal.
    //
    // Winding: with u = Cross(r, n), the quad (c-r-u, c-r+u, c+r+u, c+r-u) is front-facing along
    // n in Unity's clockwise convention. TileSeamValidation checks this against RecalculateNormals
    // rather than trusting the derivation.
    internal static SlabData BuildSlab(Bounds local, Transform tile)
    {
        Vector3 min = local.min, max = local.max;
        Matrix4x4 toWorld = tile.localToWorldMatrix;

        Vector3 Corner(Vector3 sign) =>
            new Vector3(sign.x > 0f ? max.x : min.x, sign.y > 0f ? max.y : min.y, sign.z > 0f ? max.z : min.z);

        float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
        for (int c = 0; c < 8; c++)
        {
            Vector3 p = toWorld.MultiplyPoint3x4(Corner(new Vector3((c & 1) == 0 ? -1f : 1f,
                                                                    (c & 2) == 0 ? -1f : 1f,
                                                                    (c & 4) == 0 ? -1f : 1f)));
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minZ = Mathf.Min(minZ, p.z); maxZ = Mathf.Max(maxZ, p.z);
        }
        float width = maxX - minX, depth = maxZ - minZ;
        if (width < 1e-4f || depth < 1e-4f)
            throw new System.InvalidOperationException(
                $"Tile Seams: '{tile.name}' has no XZ footprint ({width:F5} × {depth:F5}) — its mesh bounds are degenerate.");

        int top = 0;
        float bestUp = float.MinValue;
        for (int f = 0; f < 6; f++)
        {
            float up = Vector3.Dot(toWorld.MultiplyVector(FaceNormals[f]).normalized, Vector3.up);
            if (up > bestUp) { bestUp = up; top = f; }
        }

        // Tangent basis for the top face: the mesh-local directions of world +X (u) and +Z (v),
        // projected into the face. Unity's bitangent is Cross(normal, tangent) * w, so w is whichever
        // sign makes that point along +v — computed, because it comes out -1 on this field.
        Vector3 topN = FaceNormals[top];
        Vector3 uDir = tile.InverseTransformDirection(Vector3.right);
        uDir = (uDir - topN * Vector3.Dot(uDir, topN)).normalized;
        Vector3 vDir = tile.InverseTransformDirection(Vector3.forward);
        vDir = (vDir - topN * Vector3.Dot(vDir, topN)).normalized;
        float topW = Vector3.Dot(Vector3.Cross(topN, uDir), vDir) >= 0f ? 1f : -1f;

        var data = new SlabData
        {
            vertices = new Vector3[24], normals = new Vector3[24], uvs = new Vector2[24],
            tangents = new Vector4[24], triangles = new int[36], bounds = local, topFace = top,
        };
        for (int f = 0; f < 6; f++)
        {
            Vector3 n = FaceNormals[f];
            Vector3 r = FaceRights[f];
            Vector3 u = Vector3.Cross(r, n);
            Vector3[] corners = { Corner(n - r - u), Corner(n - r + u), Corner(n + r + u), Corner(n + r - u) };
            for (int i = 0; i < 4; i++)
            {
                int v = f * 4 + i;
                data.vertices[v] = corners[i];
                data.normals[v] = n;
                if (f == top)
                {
                    Vector3 p = toWorld.MultiplyPoint3x4(corners[i]);
                    data.uvs[v] = new Vector2((p.x - minX) / width, (p.z - minZ) / depth);
                    data.tangents[v] = new Vector4(uDir.x, uDir.y, uDir.z, topW);
                }
                else
                {
                    data.uvs[v] = new Vector2(0.5f, 0.5f);
                    data.tangents[v] = new Vector4(r.x, r.y, r.z, 1f);
                }
            }
            int b = f * 4, t = f * 6;
            data.triangles[t] = b; data.triangles[t + 1] = b + 1; data.triangles[t + 2] = b + 2;
            data.triangles[t + 3] = b; data.triangles[t + 4] = b + 2; data.triangles[t + 5] = b + 3;
        }
        return data;
    }

    private static bool SameSlab(SlabData a, SlabData b)
    {
        if (a.topFace != b.topFace) return false;
        for (int i = 0; i < 24; i++)
        {
            if ((a.vertices[i] - b.vertices[i]).sqrMagnitude > 1e-8f) return false;
            if ((a.uvs[i] - b.uvs[i]).sqrMagnitude > 1e-8f) return false;
            if ((a.tangents[i] - b.tangents[i]).sqrMagnitude > 1e-6f) return false;
        }
        return true;
    }

    // One FloorTileSlab.asset when every tile's slab comes out identical (the 36 sub-meshes share
    // their local bounds and the 36 parents share one rotation, so it does); a per-tile asset for
    // any tile that differs, so a re-modelled tile still gets a correct slab instead of a wrong one.
    private static Mesh[] EnsureSlabAssets(List<MeshRenderer> tiles, Mesh[] originals, out int shared)
    {
        var data = new SlabData[tiles.Count];
        for (int i = 0; i < tiles.Count; i++) data[i] = BuildSlab(originals[i].bounds, tiles[i].transform);

        Mesh sharedMesh = EnsureMeshAsset(SlabPath, Path.GetFileNameWithoutExtension(SlabPath), data[0]);
        var meshes = new Mesh[tiles.Count];
        shared = 0;
        for (int i = 0; i < tiles.Count; i++)
        {
            if (SameSlab(data[0], data[i]))
            {
                meshes[i] = sharedMesh;
                shared++;
                continue;
            }
            string name = $"FloorTileSlab_{i:00}_{Sanitize(tiles[i].name)}";
            meshes[i] = EnsureMeshAsset($"{FolderPath}/{name}.asset", name, data[i]);
            Debug.LogWarning($"Tile Seams: '{tiles[i].name}' does not match the shared slab (different bounds " +
                             $"or orientation); it got its own {name}.asset.", tiles[i]);
        }
        return meshes;
    }

    private static Mesh EnsureMeshAsset(string path, string name, SlabData data)
    {
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
        bool created = mesh == null;
        if (created) mesh = new Mesh();
        mesh.Clear();
        mesh.name = name;
        mesh.vertices = data.vertices;
        mesh.normals = data.normals;
        mesh.uv = data.uvs;
        mesh.tangents = data.tangents;
        mesh.triangles = data.triangles;
        mesh.bounds = data.bounds;   // the FBX mesh's bounds, bit for bit — the floor top must not move
        if (created) AssetDatabase.CreateAsset(mesh, path);
        else EditorUtility.SetDirty(mesh);
        return mesh;
    }

    // --- Small helpers --------------------------------------------------------------------------

    public static bool IsGenerated(Object asset) =>
        asset != null && AssetDatabase.GetAssetPath(asset).StartsWith(FolderPath + "/");

    // The floor top exactly as RebuildFieldBounds and PaintedTapeValidation measure it.
    public static float FloorTop(GameObject floorTiles)
    {
        Renderer[] renderers = floorTiles.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return float.NaN;
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b.max.y;
    }

    private static void EnsureFolder()
    {
        if (!AssetDatabase.IsValidFolder(FolderRoot)) AssetDatabase.CreateFolder("Assets", "Materials");
        if (!AssetDatabase.IsValidFolder(FolderPath)) AssetDatabase.CreateFolder(FolderRoot, "FloorTiles");
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}

// The five numbers, Apply and Remove. Opens on the settings recorded on the open scene's floor
// (the FloorTileSeams marker) when there are any, so a retune starts from what is actually there.
public class TileSeamWindow : EditorWindow
{
    private TileSeamSettings settings = TileSeamSettings.Defaults;

    [MenuItem("Tools/RoboSim/Field & Pieces/Tile Seams…", false, 54)]
    private static void Open()
    {
        var window = GetWindow<TileSeamWindow>(true, "Tile Seams");
        window.minSize = new Vector2(440f, 380f);
        window.LoadFromScene();
        window.Show();
    }

    private void OnEnable() => LoadFromScene();

    private void LoadFromScene()
    {
        GameObject floorTiles = GameObject.Find(TileSeamTool.FloorTilesName);
        FloorTileSeams marker = floorTiles != null ? floorTiles.GetComponent<FloorTileSeams>() : null;
        if (marker != null) settings = marker.settings.Clamped();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Floor tile seams", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Draws the groove between the 36 floor tiles as a texture (albedo + normal map) and gives " +
            "each tile a generated slab mesh so the pattern lines up. Physics is untouched.\n\n" +
            "Apply writes Assets/Materials/FloorTiles and re-points the tiles in the OPEN scene — open " +
            "SampleScene, Apply, save, then run Tools > RoboSim > Scenes > Build Lite Field Scene so " +
            "LiteScene gets the same floor. Re-apply after any slider change; Remove puts the FBX " +
            "meshes and material back.", MessageType.Info);

        settings.grooveWidth = EditorGUILayout.Slider(new GUIContent("Groove width",
            "Fraction of one tile. 0.008 is a 5 mm gap on the real 598 mm tile."),
            settings.grooveWidth, TileSeamSettings.MinGrooveWidth, TileSeamSettings.MaxGrooveWidth);
        settings.tabsPerEdge = TileSeamSettings.EvenTabs(EditorGUILayout.IntSlider(new GUIContent("Tabs per edge",
            "Tab-and-notch segments along one edge (a tab or a notch each). Even numbers only — the pattern " +
            "has to repeat exactly once per tile."),
            settings.tabsPerEdge, TileSeamSettings.MinTabsPerEdge, TileSeamSettings.MaxTabsPerEdge));
        settings.tabDepth = EditorGUILayout.Slider(new GUIContent("Tab depth",
            "How far a tab reaches into the neighbouring tile, as a fraction of the tile. 0 is a straight seam."),
            settings.tabDepth, TileSeamSettings.MinTabDepth, TileSeamSettings.MaxTabDepth);
        settings.grooveShade = EditorGUILayout.Slider(new GUIContent("Groove shade",
            "Albedo multiplier at the groove floor. Lower is darker; 1 hides the groove in the albedo entirely."),
            settings.grooveShade, TileSeamSettings.MinGrooveShade, TileSeamSettings.MaxGrooveShade);
        settings.grooveDepth = EditorGUILayout.Slider(new GUIContent("Groove depth (normal map)",
            "How steeply the groove walls catch the light. 0 flattens the normal map."),
            settings.grooveDepth, TileSeamSettings.MinGrooveDepth, TileSeamSettings.MaxGrooveDepth);

        EditorGUILayout.Space();
        List<MeshRenderer> tiles = TileSeamTool.FindTiles(out GameObject floorTiles);
        FloorTileSeams marker = floorTiles != null ? floorTiles.GetComponent<FloorTileSeams>() : null;
        EditorGUILayout.LabelField(
            floorTiles == null ? "Open scene: no FloorTiles object — open SampleScene."
            : marker == null ? $"Open scene: {tiles.Count} tile(s), no seams yet."
            : $"Open scene: seams on {tiles.Count} tile(s) ({marker.settings}).",
            EditorStyles.miniLabel);

        EditorGUILayout.Space();
        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(tiles.Count == 0))
            {
                if (GUILayout.Button("Apply", GUILayout.Height(32f))) ApplyClicked();
            }
            using (new EditorGUI.DisabledScope(marker == null))
            {
                if (GUILayout.Button("Remove", GUILayout.Height(32f))) RemoveClicked();
            }
            if (GUILayout.Button("Reset Sliders", GUILayout.Height(32f), GUILayout.Width(110f)))
                settings = TileSeamSettings.Defaults;
        }
    }

    private void ApplyClicked()
    {
        try
        {
            int count = TileSeamTool.Apply(settings, useUndo: true);
            EditorUtility.DisplayDialog("Tile Seams", count == 0
                ? "No floor tiles found: open SampleScene (FloorTiles/MeshInstances) first."
                : $"Seams applied to {count} tile(s). Save the scene to keep them, then run Build Lite " +
                  "Field Scene so LiteScene gets the same floor.", "OK");
        }
        catch (System.InvalidOperationException e)
        {
            Debug.LogError(e.Message);
            EditorUtility.DisplayDialog("Tile Seams", e.Message, "OK");
        }
    }

    private void RemoveClicked()
    {
        int count = TileSeamTool.Remove(useUndo: true);
        EditorUtility.DisplayDialog("Tile Seams", count == 0
            ? "No seams on the open scene's floor (no FloorTileSeams marker on FloorTiles)."
            : $"{count} tile(s) back on their FBX meshes and material. Save the scene to keep it; the " +
              "generated assets in Assets/Materials/FloorTiles were left for the next Apply.", "OK");
    }
}
