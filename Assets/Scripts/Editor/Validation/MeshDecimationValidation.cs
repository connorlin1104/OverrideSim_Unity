using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

// Validates MeshDecimator against synthetic shapes whose correct answers are known in advance.
//
// Why synthetic and not the real robots: a decimator's failures are VISUAL, and nothing in a batch
// run can look at a robot. What can be checked is the arithmetic underneath — did the hole survive,
// did the part stay the size it was, did any triangle turn inside out, did the sharp edges stay
// sharp — and each of those maps to a shape whose expected numbers can be written down. A 654V would
// only tell us that something happened, not whether it was right.
//
// The specific disasters these are aimed at:
//   - No welding. A CAD import splits every corner by normal, so a mesh with no shared edges cannot
//     collapse at all. The failure is silent: it returns almost exactly what it was given, and the
//     size problem this whole exercise exists for simply doesn't improve.
//   - Filled holes. A VEX part is mostly holes. The cheapest collapses in the whole mesh are the
//     ones around a small rim, so an unconstrained run eats every hole first and hands back a solid
//     slab that still passes any triangle-count check.
//   - Melted edges. Welding by position makes machined edges shared vertices, and averaging normals
//     across them turns a square tube into a soft cylinder — right triangle count, wrong robot.
//   - Fold-over. A collapse that turns triangles inside out reads as black shards.
//
// Usage: Tools > RoboSim > Validation > Validate Mesh Decimation.
// Batch: -executeMethod MeshDecimationValidation.RunBatchValidate
public static class MeshDecimationValidation
{
    [MenuItem("Tools/RoboSim/Validation/Validate Mesh Decimation", false, 6)]
    private static void RunFromMenu()
    {
        string result = Validate();
        EditorUtility.DisplayDialog("Validate Mesh Decimation", result, "OK");
        Debug.Log(result);
    }

    public static void RunBatchValidate()
    {
        string result = Validate();
        Debug.Log(result);
        if (result.StartsWith("FAILED")) throw new System.InvalidOperationException(result);
    }

    private class Checks
    {
        public readonly List<string> Failures = new List<string>();
        public int Count;

        public void That(bool condition, string failureMessage)
        {
            Count++;
            if (!condition) Failures.Add(failureMessage);
        }
    }

    private static string Validate()
    {
        var checks = new Checks();

        CheckWelding(checks);
        CheckHolesSurvive(checks);
        CheckSizeIsKept(checks);
        CheckNoBrokenTriangles(checks);
        CheckSharpEdgesStaySharp(checks);
        CheckSubmeshesSurvive(checks);
        CheckLimits(checks);
        CheckSourceIsNotModified(checks);
        CheckUnreadableMeshIsRefused(checks);

        if (checks.Failures.Count == 0)
            return $"PASSED: mesh decimation validation ({checks.Count} checks).";

        var report = new StringBuilder();
        report.AppendLine($"FAILED: mesh decimation validation ({checks.Failures.Count} of {checks.Count} checks).");
        foreach (string failure in checks.Failures) report.AppendLine("  - " + failure);
        return report.ToString();
    }

    private static MeshDecimator.Options Options(float ratio = 0.1f, float maxError = 0.01f,
        float smoothing = 55f) => new MeshDecimator.Options
    {
        targetRatio = ratio,
        maxErrorFraction = maxError,
        smoothingAngle = smoothing,
        floorTriangles = 8,
    };

    // ---------------------------------------------------------------------------------------------

    // THE test. A flat grid whose every triangle carries its own three vertices is what an FBX
    // actually looks like, and it is entirely coplanar — so every collapse on it costs exactly zero
    // and a working decimator should take it to the floor. If positions aren't welded first there
    // are no shared edges, no collapse is possible, and it comes back untouched.
    private static void CheckWelding(Checks checks)
    {
        Mesh split = SplitGrid(24);
        int before = MeshDecimator.TriangleCount(split);
        checks.That(before == 24 * 24 * 2, $"The test grid is not the size it should be: {before}.");
        checks.That(split.vertexCount == before * 3,
            $"The test grid is not fully split: {split.vertexCount} vertices for {before} triangles.");

        MeshDecimator.Result result = MeshDecimator.Simplify(split, Options(0.02f));

        checks.That(result.triangles < before / 4,
            $"A fully-split coplanar grid barely reduced: {before} -> {result.triangles}. Positions " +
            "are not being welded, so there are no shared edges to collapse and no CAD mesh will " +
            "ever get smaller.");
        checks.That(result.vertices < split.vertexCount / 4,
            $"Vertex count barely moved: {split.vertexCount} -> {result.vertices}.");
        // Coplanar means zero error, so nothing should stop it before the ratio.
        checks.That(!result.hitErrorCeiling,
            "A flat plane hit the error ceiling, which is impossible for coplanar geometry — the " +
            "error is being computed against something other than the surface.");

        // Decimating a flat plate moves its SURFACE by exactly zero, however far apart its vertices
        // end up. This is the case that tells a real deviation measure from a fake one: measuring to
        // the nearest surviving VERTEX reports a large number here — on this project's own robots it
        // labelled untouched flat brackets as having moved 80 mm — and a diagnostic that is loudest
        // where the result is provably perfect is one nobody reads.
        checks.That(result.maxDeviation < 0.001f * split.bounds.size.magnitude,
            $"A flat grid reports a deviation of {result.maxDeviation:F4} over a " +
            $"{split.bounds.size.magnitude:F2}-unit part. Its surface did not move at all, so this " +
            "is measuring distance to the nearest vertex rather than to the nearest triangle.");
    }

    // A VEX part is mostly holes. The rim of a small hole is the cheapest thing in the mesh to
    // collapse, so this is the failure that happens by default rather than the one that happens
    // rarely: hole gone, triangle count on target, part wrong.
    private static void CheckHolesSurvive(Checks checks)
    {
        Mesh annulus = Annulus(64, 0.4f, 1f);
        checks.That(BoundaryLoops(annulus) == 2,
            $"The test annulus does not have two rims to begin with: {BoundaryLoops(annulus)}.");

        float areaBefore = SurfaceArea(annulus);
        MeshDecimator.Result result = MeshDecimator.Simplify(annulus, Options(0.2f));

        checks.That(BoundaryLoops(result.mesh) == 2,
            $"The hole did not survive: {BoundaryLoops(result.mesh)} boundary loop(s) left, expected 2. " +
            "A part with its holes filled in is the wrong part, however good the triangle count looks.");

        // Belt and braces on the same failure from the other side: filling this hole adds ~19% area.
        float areaAfter = SurfaceArea(result.mesh);
        checks.That(areaAfter < areaBefore * 1.05f,
            $"Surface area grew from {areaBefore:F3} to {areaAfter:F3} — geometry was added, which " +
            "for a flat ring means the hole was covered over.");
        checks.That(areaAfter > areaBefore * 0.85f,
            $"Surface area collapsed from {areaBefore:F3} to {areaAfter:F3} — the rims were pulled " +
            "inward instead of being held in place.");
    }

    // Decimation moves the surface; it must not move the object. A part that shrinks has changed
    // where it sits relative to the colliders that were generated from the original.
    private static void CheckSizeIsKept(Checks checks)
    {
        Mesh sphere = Sphere(32, 16);
        Bounds before = sphere.bounds;
        MeshDecimator.Result result = MeshDecimator.Simplify(sphere, Options(0.15f));
        Bounds after = result.mesh.bounds;

        float tolerance = before.size.magnitude * 0.05f;
        checks.That(Vector3.Distance(before.center, after.center) < tolerance,
            $"The centre moved from {before.center} to {after.center}.");
        checks.That(Vector3.Distance(before.size, after.size) < tolerance,
            $"The size changed from {before.size} to {after.size}.");
        checks.That(result.maxDeviation < before.size.magnitude * 0.2f,
            $"The surface moved {result.maxDeviation:F4}, which is a large fraction of a " +
            $"{before.size.magnitude:F2}-unit object.");

        // A tighter error budget must actually produce a tighter result, or the ceiling is decorative.
        MeshDecimator.Result loose = MeshDecimator.Simplify(Sphere(32, 16), Options(0.02f, 0.2f));
        MeshDecimator.Result tight = MeshDecimator.Simplify(Sphere(32, 16), Options(0.02f, 0.0005f));
        checks.That(tight.triangles > loose.triangles,
            $"A 400x tighter error budget kept the same or fewer triangles ({tight.triangles} vs " +
            $"{loose.triangles}) — the ceiling is not stopping anything.");
        checks.That(tight.maxDeviation <= loose.maxDeviation + 1e-4f,
            $"A tighter error budget moved the surface further ({tight.maxDeviation:F5} vs " +
            $"{loose.maxDeviation:F5}).");
    }

    // Zero-area and inside-out triangles are what a collapse produces when it isn't checked, and
    // both render as black. They also poison anything downstream that computes a normal.
    private static void CheckNoBrokenTriangles(Checks checks)
    {
        foreach (Mesh source in new[] { Sphere(24, 12), SplitGrid(16), Annulus(48, 0.3f, 1f) })
        {
            MeshDecimator.Result result = MeshDecimator.Simplify(source, Options(0.1f));
            Mesh mesh = result.mesh;
            Vector3[] positions = mesh.vertices;
            Vector3[] normals = mesh.normals;
            int[] indices = mesh.triangles;

            int degenerate = 0;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                Vector3 a = positions[indices[i]], b = positions[indices[i + 1]], c = positions[indices[i + 2]];
                if (indices[i] == indices[i + 1] || indices[i + 1] == indices[i + 2] ||
                    indices[i] == indices[i + 2]) { degenerate++; continue; }
                if (Vector3.Cross(b - a, c - a).magnitude <= 1e-9f) degenerate++;
            }
            checks.That(degenerate == 0,
                $"{degenerate} degenerate triangle(s) in a decimated {source.name}.");

            int badNormals = 0;
            foreach (Vector3 n in normals)
            {
                if (float.IsNaN(n.x) || float.IsNaN(n.y) || float.IsNaN(n.z)) { badNormals++; continue; }
                if (Mathf.Abs(n.magnitude - 1f) > 0.01f) badNormals++;
            }
            checks.That(badNormals == 0,
                $"{badNormals} normal(s) in a decimated {source.name} are NaN or not unit length.");

            int nonFinite = 0;
            foreach (Vector3 p in positions)
                if (float.IsNaN(p.x) || float.IsInfinity(p.x) ||
                    float.IsNaN(p.y) || float.IsInfinity(p.y) ||
                    float.IsNaN(p.z) || float.IsInfinity(p.z)) nonFinite++;
            checks.That(nonFinite == 0, $"{nonFinite} non-finite position(s) in a decimated {source.name}.");
        }

        // Facing: a decimated sphere's normals must still point outward. Wholesale inversion is what
        // a winding-order mistake in the rebuild looks like, and it survives every count-based check.
        MeshDecimator.Result ball = MeshDecimator.Simplify(Sphere(24, 12), Options(0.15f));
        Vector3[] ballPositions = ball.mesh.vertices;
        Vector3[] ballNormals = ball.mesh.normals;
        int outward = 0;
        for (int i = 0; i < ballPositions.Length; i++)
            if (Vector3.Dot(ballNormals[i], ballPositions[i].normalized) > 0f) outward++;
        checks.That(outward > ballPositions.Length * 0.95f,
            $"Only {outward} of {ballPositions.Length} normals on a decimated sphere point outward — " +
            "the winding or the normals are inverted.");
    }

    // Welding by position merges the two sides of every machined edge into one vertex. Recomputing
    // normals from that without a smoothing angle averages across the edge and rounds it off: right
    // triangle count, wrong-looking robot. A cube is the clearest case — it must come back with six
    // distinct face normals, not eight smeared vertex normals.
    private static void CheckSharpEdgesStaySharp(Checks checks)
    {
        Mesh cube = Cube();
        MeshDecimator.Result sharp = MeshDecimator.Simplify(cube, Options(1f, 0.01f, 55f));

        checks.That(sharp.mesh.vertexCount >= 24,
            $"A cube came back with {sharp.mesh.vertexCount} vertices. Under 24 means the corners " +
            "were shared across faces, so every edge of every machined part is now a soft gradient.");
        checks.That(DistinctNormals(sharp.mesh) == 6,
            $"A cube has {DistinctNormals(sharp.mesh)} distinct normals, expected 6.");

        // And the opposite setting must actually do the opposite, or the angle is being ignored.
        // A fully-smoothed cube is 8 vertices — one per corner, its normal the corner diagonal.
        // (Still 8 DISTINCT normals, which is why this counts vertices instead: the corners point
        // eight different ways whether or not the faces were split.)
        MeshDecimator.Result smooth = MeshDecimator.Simplify(Cube(), Options(1f, 0.01f, 180f));
        checks.That(smooth.mesh.vertexCount == 8,
            $"At a 180-degree smoothing angle a cube has {smooth.mesh.vertexCount} vertices, expected " +
            "8 — the angle is not being applied at all.");
        checks.That(smooth.mesh.vertexCount < sharp.mesh.vertexCount,
            $"Smoothing everything did not reduce the vertex count ({smooth.mesh.vertexCount} vs " +
            $"{sharp.mesh.vertexCount}).");
    }

    // Submesh index is what picks the material. Lose it and a robot comes back one colour, or with
    // its plastic and metal swapped.
    private static void CheckSubmeshesSurvive(Checks checks)
    {
        Mesh two = TwoMaterialGrid(16);
        checks.That(two.subMeshCount == 2, "The test mesh does not have two submeshes.");

        MeshDecimator.Result result = MeshDecimator.Simplify(two, Options(0.2f));
        checks.That(result.mesh.subMeshCount == 2,
            $"Submesh count changed from 2 to {result.mesh.subMeshCount}.");
        checks.That(result.mesh.GetIndexCount(0) > 0 && result.mesh.GetIndexCount(1) > 0,
            $"A submesh was emptied: {result.mesh.GetIndexCount(0)} and {result.mesh.GetIndexCount(1)} " +
            "indices. The material on the empty one draws nothing.");

        // Triangles must not migrate between submeshes — that swaps materials rather than losing one,
        // which no count check would catch. Half the grid is submesh 0, so the split stays near even.
        float share = result.mesh.GetIndexCount(0) /
                      (float)(result.mesh.GetIndexCount(0) + result.mesh.GetIndexCount(1));
        checks.That(share > 0.3f && share < 0.7f,
            $"Submesh 0 ended up with {share:P0} of the triangles, from an even split — triangles " +
            "moved between materials.");
    }

    // The floor and the ratio are the two things a caller sets, so they have to mean what they say.
    private static void CheckLimits(Checks checks)
    {
        var floored = new MeshDecimator.Options
        {
            targetRatio = 0.01f, maxErrorFraction = 1f, smoothingAngle = 55f, floorTriangles = 100,
        };
        MeshDecimator.Result result = MeshDecimator.Simplify(SplitGrid(24), floored);
        checks.That(result.triangles >= 100,
            $"The floor of 100 triangles was ignored: {result.triangles} left. A small part reduced " +
            "past its own shape is not a part.");

        // A ratio of 1 must be a no-op, not a light trim: this is what lets a caller run the tool
        // over a whole robot to detach the meshes from the FBX without changing any geometry.
        MeshDecimator.Result untouched = MeshDecimator.Simplify(Sphere(16, 8), Options(1f));
        checks.That(untouched.triangles == MeshDecimator.TriangleCount(Sphere(16, 8)),
            $"Asking to keep 100% still removed triangles: {untouched.triangles} of " +
            $"{MeshDecimator.TriangleCount(Sphere(16, 8))}.");

        // Halving the ratio must roughly halve the result, or the ratio is a suggestion.
        MeshDecimator.Result half = MeshDecimator.Simplify(SplitGrid(32), Options(0.5f, 1f));
        MeshDecimator.Result quarter = MeshDecimator.Simplify(SplitGrid(32), Options(0.25f, 1f));
        checks.That(quarter.triangles < half.triangles,
            $"A smaller ratio did not produce fewer triangles ({quarter.triangles} vs {half.triangles}).");
    }

    // The source is shared: several renderers point at one mesh, and the tool decimates it once and
    // repoints them all. Modifying it in place would corrupt whatever else still holds it, including
    // the FBX sub-asset itself.
    private static void CheckSourceIsNotModified(Checks checks)
    {
        Mesh source = Sphere(24, 12);
        int triangles = MeshDecimator.TriangleCount(source);
        int vertices = source.vertexCount;
        Bounds bounds = source.bounds;

        MeshDecimator.Result result = MeshDecimator.Simplify(source, Options(0.1f));

        checks.That(MeshDecimator.TriangleCount(source) == triangles,
            $"Simplify changed the source mesh's triangle count to {MeshDecimator.TriangleCount(source)}.");
        checks.That(source.vertexCount == vertices,
            $"Simplify changed the source mesh's vertex count to {source.vertexCount}.");
        checks.That(source.bounds == bounds, "Simplify changed the source mesh's bounds.");
        checks.That(result.mesh != source, "Simplify returned the source mesh itself.");
    }

    // A mesh imported with Read/Write off has no CPU-side copy, and Unity returns an EMPTY vertex
    // array for it rather than throwing. Decimating that would silently produce a robot with no
    // geometry — and it would look like a decimation bug, not a settings one.
    private static void CheckUnreadableMeshIsRefused(Checks checks)
    {
        Mesh mesh = Sphere(16, 8);
        int triangles = MeshDecimator.TriangleCount(mesh);
        mesh.UploadMeshData(true); // markNoLongerReadable

        MeshDecimator.Result result = MeshDecimator.Simplify(mesh, Options(0.1f));
        checks.That(result.mesh != null && MeshDecimator.TriangleCount(result.mesh) == triangles,
            "An unreadable mesh did not come back unchanged — it was silently emptied.");
        checks.That(!string.IsNullOrEmpty(result.note),
            "An unreadable mesh came back with no explanation, so the tool would report it as done.");
    }

    // ---------------------------------------------------------------------------------------------
    // Shapes with known answers.
    // ---------------------------------------------------------------------------------------------

    // A flat grid where every triangle owns its three vertices — what an FBX importer produces once
    // it has split by normal and UV. Nothing here shares an edge until positions are welded.
    private static Mesh SplitGrid(int cells)
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        for (int x = 0; x < cells; x++)
        for (int z = 0; z < cells; z++)
        {
            Vector3 a = new Vector3(x, 0f, z);
            Vector3 b = new Vector3(x + 1, 0f, z);
            Vector3 c = new Vector3(x + 1, 0f, z + 1);
            Vector3 d = new Vector3(x, 0f, z + 1);
            AddSplitTriangle(positions, indices, a, b, c);
            AddSplitTriangle(positions, indices, a, c, d);
        }

        return Finish("SplitGrid", positions, indices);
    }

    private static void AddSplitTriangle(List<Vector3> positions, List<int> indices,
        Vector3 a, Vector3 b, Vector3 c)
    {
        indices.Add(positions.Count); positions.Add(a);
        indices.Add(positions.Count); positions.Add(b);
        indices.Add(positions.Count); positions.Add(c);
    }

    // Same grid, split down the middle into two submeshes.
    private static Mesh TwoMaterialGrid(int cells)
    {
        var positions = new List<Vector3>();
        var left = new List<int>();
        var right = new List<int>();

        for (int x = 0; x < cells; x++)
        for (int z = 0; z < cells; z++)
        {
            List<int> target = x < cells / 2 ? left : right;
            Vector3 a = new Vector3(x, 0f, z);
            Vector3 b = new Vector3(x + 1, 0f, z);
            Vector3 c = new Vector3(x + 1, 0f, z + 1);
            Vector3 d = new Vector3(x, 0f, z + 1);
            AddSplitTriangle(positions, target, a, b, c);
            AddSplitTriangle(positions, target, a, c, d);
        }

        var mesh = new Mesh { name = "TwoMaterialGrid", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(positions);
        mesh.subMeshCount = 2;
        mesh.SetTriangles(left, 0);
        mesh.SetTriangles(right, 1);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // A flat ring: an outer rim and an inner rim, and nothing else. Two boundary loops, and the
    // inner one is a hole in exactly the sense a VEX plate has holes.
    private static Mesh Annulus(int segments, float inner, float outer)
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        // Ring points are precomputed and indexed modulo `segments` so the seam closes on the SAME
        // float, not on cos(2pi) which is a hair away from cos(0). A ring that misses its own start
        // is an open strip: the two rims join up round the back and this stops being an annulus at
        // all — which is exactly what the first version of this did, and it read as the decimator
        // filling the hole.
        var ring = new Vector3[segments * 2];
        for (int i = 0; i < segments; i++)
        {
            float t = 2f * Mathf.PI * i / segments;
            ring[i] = new Vector3(Mathf.Cos(t) * inner, 0f, Mathf.Sin(t) * inner);
            ring[segments + i] = new Vector3(Mathf.Cos(t) * outer, 0f, Mathf.Sin(t) * outer);
        }

        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            AddSplitTriangle(positions, indices, ring[i], ring[segments + i], ring[segments + next]);
            AddSplitTriangle(positions, indices, ring[i], ring[segments + next], ring[next]);
        }

        return Finish("Annulus", positions, indices);
    }

    // A closed UV sphere: no boundaries at all, so it exercises the ordinary interior collapse path
    // without any border pinning in the way.
    private static Mesh Sphere(int segments, int rings)
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        for (int r = 0; r < rings; r++)
        for (int s = 0; s < segments; s++)
        {
            Vector3 a = SpherePoint(s, r, segments, rings);
            Vector3 b = SpherePoint(s + 1, r, segments, rings);
            Vector3 c = SpherePoint(s + 1, r + 1, segments, rings);
            Vector3 d = SpherePoint(s, r + 1, segments, rings);
            if (r > 0) AddSplitTriangle(positions, indices, a, b, c);
            if (r < rings - 1) AddSplitTriangle(positions, indices, a, c, d);
        }

        return Finish("Sphere", positions, indices);
    }

    private static Vector3 SpherePoint(int s, int r, int segments, int rings)
    {
        // s wraps so the seam closes exactly — see Annulus. A sphere with an open seam has boundary
        // edges, which would put border pinning in the way of the interior-collapse path this shape
        // exists to exercise.
        float theta = 2f * Mathf.PI * (s % segments) / segments;
        float phi = Mathf.PI * r / rings;
        return new Vector3(Mathf.Sin(phi) * Mathf.Cos(theta), Mathf.Cos(phi), Mathf.Sin(phi) * Mathf.Sin(theta));
    }

    // Six flat faces, twelve triangles. The reference for "did the sharp edges survive".
    private static Mesh Cube()
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        Vector3[] dirs = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        foreach (Vector3 n in dirs)
        {
            Vector3 u = Mathf.Abs(n.y) > 0.5f ? Vector3.right : Vector3.up;
            Vector3 v = Vector3.Cross(n, u);
            Vector3 c = n * 0.5f;
            Vector3 p0 = c - u * 0.5f - v * 0.5f;
            Vector3 p1 = c + u * 0.5f - v * 0.5f;
            Vector3 p2 = c + u * 0.5f + v * 0.5f;
            Vector3 p3 = c - u * 0.5f + v * 0.5f;
            AddSplitTriangle(positions, indices, p0, p1, p2);
            AddSplitTriangle(positions, indices, p0, p2, p3);
        }

        return Finish("Cube", positions, indices);
    }

    private static Mesh Finish(string name, List<Vector3> positions, List<int> indices)
    {
        var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(positions);
        mesh.SetTriangles(indices, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    // ---------------------------------------------------------------------------------------------
    // Measurements the checks assert on.
    // ---------------------------------------------------------------------------------------------

    // Rims, counted as connected loops of edges used by exactly one triangle. Two for an annulus,
    // zero for a sphere, one for a disc — so "did the hole survive" is a number rather than a look.
    private static int BoundaryLoops(Mesh mesh)
    {
        Vector3[] positions = mesh.vertices;
        int[] indices = mesh.triangles;

        // Weld by position first: the caller's meshes are split per triangle, so raw indices would
        // make every single edge look like a boundary.
        var welded = new Dictionary<Vector3, int>();
        var remap = new int[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            if (!welded.TryGetValue(positions[i], out int index))
            {
                index = welded.Count;
                welded[positions[i]] = index;
            }
            remap[i] = index;
        }

        var uses = new Dictionary<long, int>();
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            CountEdge(uses, remap[indices[i]], remap[indices[i + 1]]);
            CountEdge(uses, remap[indices[i + 1]], remap[indices[i + 2]]);
            CountEdge(uses, remap[indices[i + 2]], remap[indices[i]]);
        }

        // Adjacency among boundary vertices only, then count connected components.
        var neighbours = new Dictionary<int, List<int>>();
        foreach (KeyValuePair<long, int> pair in uses)
        {
            if (pair.Value != 1) continue;
            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFF);
            Link(neighbours, a, b);
            Link(neighbours, b, a);
        }

        var visited = new HashSet<int>();
        int loops = 0;
        foreach (int start in neighbours.Keys)
        {
            if (!visited.Add(start)) continue;
            loops++;
            var stack = new Stack<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                int current = stack.Pop();
                foreach (int next in neighbours[current])
                    if (visited.Add(next)) stack.Push(next);
            }
        }
        return loops;
    }

    private static void Link(Dictionary<int, List<int>> map, int a, int b)
    {
        if (!map.TryGetValue(a, out List<int> list)) map[a] = list = new List<int>();
        list.Add(b);
    }

    private static void CountEdge(Dictionary<long, int> edges, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        edges[key] = edges.TryGetValue(key, out int n) ? n + 1 : 1;
    }

    private static float SurfaceArea(Mesh mesh)
    {
        Vector3[] positions = mesh.vertices;
        int[] indices = mesh.triangles;
        float area = 0f;
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            Vector3 a = positions[indices[i]], b = positions[indices[i + 1]], c = positions[indices[i + 2]];
            area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
        }
        return area;
    }

    // Normals binned to a coarse grid, so "six faces" reads as 6 rather than as 6-ish.
    private static int DistinctNormals(Mesh mesh)
    {
        var seen = new HashSet<Vector3Int>();
        foreach (Vector3 n in mesh.normals)
        {
            seen.Add(new Vector3Int(Mathf.RoundToInt(n.x * 10f), Mathf.RoundToInt(n.y * 10f),
                Mathf.RoundToInt(n.z * 10f)));
        }
        return seen.Count;
    }
}
