using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Reduces a mesh's triangle count while keeping its shape, using quadric error metrics
// (Garland & Heckbert 1997) driven by the iterative edge-collapse loop from Sven Forstmann's
// Fast Quadric Mesh Simplification.
//
// WHY THIS EXISTS: a Fusion export is tessellated for manufacturing, not for a phone. Measured on
// this project's own robots, one 654V is ~2.8 MILLION triangles and ~226 MB of runtime mesh data,
// and there are three of them. Nothing else in the size story matters at that scale — you cannot
// download it, cannot hold it in memory on a phone, and cannot ship it in an app binary.
//
// WHAT A QUADRIC IS, briefly, because the code is unreadable otherwise: for each vertex, sum the
// squared-distance-to-plane functions of every triangle that touches it. That sum is a 4x4 symmetric
// matrix. Collapsing an edge means picking one point to replace both ends, and the sum of the two
// quadrics evaluated at that point IS the error the collapse introduces — so "which edge is cheapest
// to remove" becomes arithmetic instead of a guess. Collapse the cheapest, repeat.
//
// THE FOUR THINGS THAT GO WRONG, and what stops each here:
//  - Seams. A CAD import splits one corner into several vertices wherever the normal changes, so the
//    raw topology is a pile of disconnected shells with no shared edges to collapse. Welding by
//    POSITION first is what makes any reduction possible at all; without it this returns the mesh
//    almost unchanged. (Positions are exactly equal in an FBX export — the split happens downstream
//    of one B-rep vertex — so the weld is an exact match, not a tolerance that could pinch a thin
//    plate shut.)
//  - Holes. A VEX part is mostly holes, and an unconstrained collapse fills them in. Every edge used
//    by only one triangle gets a heavy extra quadric standing perpendicular to the surface, which
//    prices any collapse that would move the rim far above the threshold.
//  - Fold-over. The cheapest collapse by pure error is often one that turns a triangle inside out,
//    which reads as a black shard rather than as a smooth surface. Each candidate is rejected if it
//    would flip any adjacent face.
//  - Melted edges. Welding by position makes every machined edge a shared vertex, so plain
//    RecalculateNormals would average across it and turn a square tube into a soft cylinder. Normals
//    are rebuilt per smoothing cluster instead, which re-splits exactly the corners that need it.
//
// This does NOT touch physics. Robot colliders are separate generated hull assets under
// Assets/RobotColliders/ (see GeneratePartColliders), so decimating a render mesh cannot change how
// a robot drives — which is the only reason this is safe to run on already-tuned robots.
public static class MeshDecimator
{
    public class Options
    {
        // Fraction of the original triangles to keep.
        public float targetRatio = 0.08f;

        // Stop early if the cheapest remaining collapse would move the surface further than this
        // fraction of the mesh's bounding-box diagonal. This is what keeps a small, already-simple
        // part from being chewed to nothing just to hit the ratio.
        public float maxErrorFraction = 0.004f;

        // Never go below this many triangles, whatever the ratio says. A 40-triangle bracket reduced
        // by 92% is a 3-triangle bracket, which is not a bracket.
        public int floorTriangles = 24;

        // Angle below which neighbouring faces share a normal. 40-60 keeps machined faces flat and
        // their fillets round; 180 smooths everything; 0 makes every face flat-shaded.
        public float smoothingAngle = 55f;

        // Keep UV0. Off drops 8 bytes per vertex. Only turn it on if a material actually samples a
        // texture — UVs on an untextured part are pure weight, and they cannot survive a
        // position-weld intact anyway.
        public bool keepUv;

        // Recompute tangents. Only meaningful with UVs and a normal map; 16 bytes per vertex
        // otherwise, which on these robots is the single largest piece of dead weight in the file.
        public bool recalculateTangents;
    }

    // What happened, for the tool to show and for validation to assert on.
    public class Result
    {
        public Mesh mesh;
        public int sourceTriangles;
        public int sourceVertices;
        public int triangles;
        public int vertices;
        public float maxDeviation;      // how far the surface moved, at worst, in mesh units
        public float boundsDrift;       // how far the bounding box corners moved
        public bool hitErrorCeiling;    // stopped on quality rather than on the ratio
        public string note = string.Empty;

        public float TriangleRatio => sourceTriangles == 0 ? 1f : (float)triangles / sourceTriangles;
    }

    // ---------------------------------------------------------------------------------------------
    // The 4x4 symmetric quadric, held as its 10 distinct entries. Doubles, not floats: these are sums
    // of products of coordinates over thousands of faces, and in float the small differences that
    // decide between two nearly-equal collapses vanish into the rounding.
    // ---------------------------------------------------------------------------------------------
    private struct Quadric
    {
        public double m0, m1, m2, m3, m4, m5, m6, m7, m8, m9;

        // The quadric of a single plane ax+by+cz+d=0: the outer product of [a,b,c,d] with itself.
        public static Quadric FromPlane(double a, double b, double c, double d) => new Quadric
        {
            m0 = a * a, m1 = a * b, m2 = a * c, m3 = a * d,
            m4 = b * b, m5 = b * c, m6 = b * d,
            m7 = c * c, m8 = c * d,
            m9 = d * d,
        };

        public static Quadric operator +(Quadric a, Quadric b) => new Quadric
        {
            m0 = a.m0 + b.m0, m1 = a.m1 + b.m1, m2 = a.m2 + b.m2, m3 = a.m3 + b.m3,
            m4 = a.m4 + b.m4, m5 = a.m5 + b.m5, m6 = a.m6 + b.m6,
            m7 = a.m7 + b.m7, m8 = a.m8 + b.m8,
            m9 = a.m9 + b.m9,
        };

        public static Quadric operator *(Quadric q, double s) => new Quadric
        {
            m0 = q.m0 * s, m1 = q.m1 * s, m2 = q.m2 * s, m3 = q.m3 * s,
            m4 = q.m4 * s, m5 = q.m5 * s, m6 = q.m6 * s,
            m7 = q.m7 * s, m8 = q.m8 * s,
            m9 = q.m9 * s,
        };

        // vᵀ Q v — the squared distance from v to all the planes this quadric accumulated. Never
        // negative in exact arithmetic; clamped because it can land at -1e-15 and the caller
        // compares it against a threshold.
        public double Evaluate(double x, double y, double z)
        {
            double e = m0 * x * x + 2 * m1 * x * y + 2 * m2 * x * z + 2 * m3 * x
                     + m4 * y * y + 2 * m5 * y * z + 2 * m6 * y
                     + m7 * z * z + 2 * m8 * z
                     + m9;
            return e < 0 ? 0 : e;
        }
    }

    private struct Face
    {
        public int v0, v1, v2;
        public int submesh;
        public Vector3 normal;
        public double err0, err1, err2, errMin;
        public bool deleted;
        public bool dirty;
    }

    private struct Corner
    {
        public int face;
        public int slot; // 0/1/2 — which of the face's three vertices this is
    }

    private struct Vert
    {
        public Vector3 position;
        public Vector2 uv;
        public Quadric quadric;
        public int cornerStart, cornerCount;
        public bool border;
    }

    private static Face[] faces;
    private static Vert[] verts;

    // The corner index: every vertex's incident faces, as one flat array with a start/count per
    // vertex. A List<int> per vertex would be two million small allocations.
    //
    // Collapses APPEND a vertex's new run at the end rather than editing in place (the old run has
    // no room to grow), so this only ever gets longer during a pass — which is exactly what the
    // periodic rebuild in Collapse reclaims. Growth is by doubling: Array.Resize per collapse is
    // O(n) each time and turned a 20-second run into minutes, all of it copying.
    private static Corner[] corners = Array.Empty<Corner>();
    private static int cornerFill;

    // Scratch reused across the collapse loop, for the same reason.
    private static readonly List<bool> deletedA = new List<bool>();
    private static readonly List<bool> deletedB = new List<bool>();
    private static readonly List<Corner> survivors = new List<Corner>();

    // ---------------------------------------------------------------------------------------------

    // Returns a NEW mesh; `source` is never modified. Null in, null out.
    public static Result Simplify(Mesh source, Options options)
    {
        if (source == null) return null;
        options ??= new Options();

        var result = new Result
        {
            sourceTriangles = TriangleCount(source),
            sourceVertices = source.vertexCount,
        };

        // A mesh Unity imported with Read/Write off has no CPU-side copy to read, and asking for
        // .vertices returns an empty array rather than throwing — which would silently produce an
        // empty mesh. Hand the original straight back and say why.
        if (!source.isReadable)
        {
            result.mesh = source;
            result.triangles = result.sourceTriangles;
            result.vertices = result.sourceVertices;
            result.note = "not readable — turn Read/Write on for the source model to decimate it";
            return result;
        }

        Build(source, options);

        float diagonal = source.bounds.size.magnitude;
        if (diagonal <= 0f) diagonal = 1f;
        double errorCeiling = (double)options.maxErrorFraction * diagonal;
        errorCeiling *= errorCeiling; // quadrics are SQUARED distances; compare in the same space

        int target = Mathf.Max(options.floorTriangles,
            Mathf.RoundToInt(result.sourceTriangles * Mathf.Clamp01(options.targetRatio)));

        result.hitErrorCeiling = Collapse(target, errorCeiling);

        Mesh simplified = Compact(source, options);
        result.mesh = simplified;
        result.triangles = TriangleCount(simplified);
        result.vertices = simplified.vertexCount;
        result.maxDeviation = Deviation(source, simplified);
        result.boundsDrift = Vector3.Distance(source.bounds.min, simplified.bounds.min)
                           + Vector3.Distance(source.bounds.max, simplified.bounds.max);

        faces = null;
        verts = null;
        cornerFill = 0;
        return result;
    }

    public static int TriangleCount(Mesh mesh)
    {
        int indices = 0;
        for (int i = 0; i < mesh.subMeshCount; i++) indices += (int)mesh.GetIndexCount(i);
        return indices / 3;
    }

    // ---------------------------------------------------------------------------------------------
    // Build: weld by position, drop the degenerates that welding creates, index the corners, and
    // accumulate one quadric per vertex.
    // ---------------------------------------------------------------------------------------------
    private static void Build(Mesh source, Options options)
    {
        Vector3[] positions = source.vertices;
        Vector2[] uvs = options.keepUv ? source.uv : null;
        if (uvs != null && uvs.Length != positions.Length) uvs = null;

        // Exact position equality. Vector3's default equality comparer is component-wise ==, and an
        // FBX exporter emits byte-identical coordinates for the corners it split off one B-rep
        // vertex, so this catches every seam without a tolerance.
        var welded = new Dictionary<Vector3, int>(positions.Length);
        var remap = new int[positions.Length];
        var vertList = new List<Vert>(positions.Length);

        for (int i = 0; i < positions.Length; i++)
        {
            if (welded.TryGetValue(positions[i], out int existing)) { remap[i] = existing; continue; }

            int index = vertList.Count;
            welded[positions[i]] = index;
            remap[i] = index;
            vertList.Add(new Vert
            {
                position = positions[i],
                uv = uvs != null ? uvs[i] : Vector2.zero,
            });
        }
        verts = vertList.ToArray();

        var faceList = new List<Face>();
        for (int sub = 0; sub < source.subMeshCount; sub++)
        {
            if (source.GetTopology(sub) != MeshTopology.Triangles) continue;
            int[] indices = source.GetTriangles(sub);
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                int a = remap[indices[i]], b = remap[indices[i + 1]], c = remap[indices[i + 2]];
                // Welding collapses a sliver whose three corners shared two positions into a line.
                // Carrying it forward would give it a zero-length normal and poison every quadric it
                // touches with NaN.
                if (a == b || b == c || a == c) continue;
                faceList.Add(new Face { v0 = a, v1 = b, v2 = c, submesh = sub });
            }
        }
        faces = faceList.ToArray();

        IndexCorners();
        ComputeQuadrics();
    }

    // Rebuilds the corner index from `faces`, which is the authoritative state. Doubles as the
    // compaction step: everything appended by collapses since the last rebuild is reclaimed.
    private static void IndexCorners()
    {
        for (int i = 0; i < verts.Length; i++) verts[i].cornerCount = 0;

        for (int f = 0; f < faces.Length; f++)
        {
            if (faces[f].deleted) continue;
            verts[faces[f].v0].cornerCount++;
            verts[faces[f].v1].cornerCount++;
            verts[faces[f].v2].cornerCount++;
        }

        int running = 0;
        for (int i = 0; i < verts.Length; i++)
        {
            verts[i].cornerStart = running;
            running += verts[i].cornerCount;
            verts[i].cornerCount = 0; // reused as a write cursor below
        }

        if (corners.Length < running) corners = new Corner[Mathf.NextPowerOfTwo(running + 1)];
        cornerFill = running;

        for (int f = 0; f < faces.Length; f++)
        {
            if (faces[f].deleted) continue;
            Place(faces[f].v0, f, 0);
            Place(faces[f].v1, f, 1);
            Place(faces[f].v2, f, 2);
        }
    }

    // Which vertex a corner currently points at. Read back from the face rather than trusted from
    // the corner, because a corner is a (face, slot) pair and the face's slots move under it.
    private static int VertexAt(Corner corner)
    {
        Face face = faces[corner.face];
        return corner.slot == 0 ? face.v0 : corner.slot == 1 ? face.v1 : face.v2;
    }

    private static void Place(int vertex, int face, int slot)
    {
        corners[verts[vertex].cornerStart + verts[vertex].cornerCount] =
            new Corner { face = face, slot = slot };
        verts[vertex].cornerCount++;
    }

    // Replaces `vertex`'s corner run with `run`, appended at the end of the array.
    private static void ReplaceRun(int vertex, List<Corner> run)
    {
        int needed = cornerFill + run.Count;
        if (needed > corners.Length)
        {
            int size = Mathf.Max(corners.Length * 2, Mathf.NextPowerOfTwo(needed + 1));
            Array.Resize(ref corners, size);
        }

        for (int i = 0; i < run.Count; i++) corners[cornerFill + i] = run[i];
        verts[vertex].cornerStart = cornerFill;
        verts[vertex].cornerCount = run.Count;
        cornerFill = needed;
    }

    private static void ComputeQuadrics()
    {
        for (int i = 0; i < verts.Length; i++) verts[i].quadric = default;

        for (int f = 0; f < faces.Length; f++)
        {
            if (faces[f].deleted) continue;
            Face face = faces[f];
            Vector3 p0 = verts[face.v0].position;

            Vector3 cross = Vector3.Cross(verts[face.v1].position - p0, verts[face.v2].position - p0);
            float area = cross.magnitude;
            if (area <= 0f) { faces[f].deleted = true; continue; }
            Vector3 n = cross / area;
            faces[f].normal = n;

            // Area weighting. Without it a dense patch of tiny triangles outvotes the single large
            // face they sit next to, and the flat face is the one that gets bent.
            Quadric q = Quadric.FromPlane(n.x, n.y, n.z, -Vector3.Dot(n, p0)) * (area * 0.5);
            verts[face.v0].quadric += q;
            verts[face.v1].quadric += q;
            verts[face.v2].quadric += q;
        }

        MarkBordersAndPin();
        RefreshAllEdgeErrors();
    }

    private static void RefreshAllEdgeErrors()
    {
        for (int f = 0; f < faces.Length; f++)
        {
            if (faces[f].deleted) continue;
            RefreshEdgeErrors(f);
        }
    }

    private static void RefreshEdgeErrors(int f)
    {
        faces[f].err0 = EdgeError(faces[f].v0, faces[f].v1, out _);
        faces[f].err1 = EdgeError(faces[f].v1, faces[f].v2, out _);
        faces[f].err2 = EdgeError(faces[f].v2, faces[f].v0, out _);
        faces[f].errMin = Math.Min(faces[f].err0, Math.Min(faces[f].err1, faces[f].err2));
    }

    // An edge belonging to exactly one face is an open boundary: the rim of a hole, or the edge of a
    // plate. Both ends get a quadric for the plane standing perpendicular to the surface along that
    // edge, weighted heavily, so any collapse that would drag the rim sideways prices itself out.
    // This is the difference between a decimated VEX plate and a decimated dinner plate.
    private static void MarkBordersAndPin()
    {
        var edgeUses = new Dictionary<long, int>();
        for (int f = 0; f < faces.Length; f++)
        {
            if (faces[f].deleted) continue;
            Count(edgeUses, faces[f].v0, faces[f].v1);
            Count(edgeUses, faces[f].v1, faces[f].v2);
            Count(edgeUses, faces[f].v2, faces[f].v0);
        }

        for (int i = 0; i < verts.Length; i++) verts[i].border = false;

        foreach (KeyValuePair<long, int> pair in edgeUses)
        {
            if (pair.Value != 1) continue;
            int a = (int)(pair.Key >> 32);
            int b = (int)(pair.Key & 0xFFFFFFFF);
            verts[a].border = true;
            verts[b].border = true;

            Vector3 along = verts[b].position - verts[a].position;
            float length = along.magnitude;
            if (length <= 0f) continue;

            // Perpendicular to the edge and to the surface it bounds. Any incident face works — a
            // boundary edge has exactly one.
            Vector3 wall = Vector3.Cross(along / length, BorderFaceNormal(a, b));
            if (wall.sqrMagnitude <= 1e-12f) continue;
            wall.Normalize();

            // The weight only has to be large enough that a boundary collapse never wins on price.
            // Its absolute size is arbitrary; what matters is the ratio to the area weights above.
            Quadric q = Quadric.FromPlane(wall.x, wall.y, wall.z, -Vector3.Dot(wall, verts[a].position))
                        * (length * 1000.0);
            verts[a].quadric += q;
            verts[b].quadric += q;
        }
    }

    private static Vector3 BorderFaceNormal(int a, int b)
    {
        for (int i = 0; i < verts[a].cornerCount; i++)
        {
            Face face = faces[corners[verts[a].cornerStart + i].face];
            if (face.deleted) continue;
            if (face.v0 == b || face.v1 == b || face.v2 == b) return face.normal;
        }
        return Vector3.up;
    }

    private static void Count(Dictionary<long, int> edges, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        edges[key] = edges.TryGetValue(key, out int n) ? n + 1 : 1;
    }

    // ---------------------------------------------------------------------------------------------
    // Collapse: repeated sweeps with a rising price ceiling.
    //
    // A real priority queue would need decrease-key on every neighbour of every collapse, and the
    // bookkeeping costs more than it saves. Instead each pass admits every collapse under a
    // threshold that grows with the pass number, so the cheap ones all go first anyway and the
    // expensive ones are only reached if the target hasn't been met. Returns true if it stopped
    // because quality ran out rather than because the target was hit.
    // ---------------------------------------------------------------------------------------------
    private static bool Collapse(int target, double errorCeiling)
    {
        int live = 0;
        for (int f = 0; f < faces.Length; f++) if (!faces[f].deleted) live++;

        const int maxPasses = 120;
        for (int pass = 0; pass < maxPasses; pass++)
        {
            if (live <= target) return false;

            // Every fifth pass is Forstmann's figure and holds up here: more often and the rebuild
            // dominates, less often and the sweeps spend their time walking dead faces.
            if (pass % 5 == 0 && pass > 0) IndexCorners();

            for (int f = 0; f < faces.Length; f++) faces[f].dirty = false;

            // 1e-9 * (pass+3)^7 climbs steeply, so the first sweeps take only collapses that are
            // nearly free and later ones take whatever is left. The ceiling is what stops it.
            double threshold = 1e-9 * Math.Pow(pass + 3, 7);
            bool atCeiling = threshold >= errorCeiling;
            if (atCeiling) threshold = errorCeiling;

            int before = live;

            for (int f = 0; f < faces.Length && live > target; f++)
            {
                if (faces[f].deleted || faces[f].dirty || faces[f].errMin > threshold) continue;

                for (int slot = 0; slot < 3; slot++)
                {
                    double err = slot == 0 ? faces[f].err0 : slot == 1 ? faces[f].err1 : faces[f].err2;
                    if (err > threshold) continue;

                    int v0 = slot == 0 ? faces[f].v0 : slot == 1 ? faces[f].v1 : faces[f].v2;
                    int v1 = slot == 0 ? faces[f].v1 : slot == 1 ? faces[f].v2 : faces[f].v0;

                    // Never merge a boundary vertex into an interior one: that is exactly the move
                    // that fills a hole. Two boundary vertices may merge with each other — a rim
                    // needs to be able to simplify along itself — and the wall quadrics keep that
                    // honest.
                    if (verts[v0].border != verts[v1].border) continue;

                    EdgeError(v0, v1, out Vector3 merged);

                    deletedA.Clear();
                    deletedB.Clear();
                    if (WouldFlip(merged, v0, v1, deletedA)) continue;
                    if (WouldFlip(merged, v1, v0, deletedB)) continue;

                    // One collapse removes the faces on BOTH sides of the edge, so a run that only
                    // checks `live > target` between collapses can step straight past the target and
                    // land under it. That matters where the target is the floor: a caller asking for
                    // at least 100 triangles got 99, which is the difference between "a limit" and
                    // "roughly a limit". The two WouldFlip passes already worked out which faces die,
                    // so the check costs a count.
                    if (live - Doomed(deletedA, deletedB) < target) continue;

                    verts[v0].position = merged;
                    verts[v0].quadric += verts[v1].quadric;
                    verts[v0].border |= verts[v1].border;

                    // Both retargets contribute to ONE new corner run for v0: its own surviving
                    // faces, plus v1's faces now pointing at it.
                    survivors.Clear();
                    live -= Retarget(v0, v0, deletedA, survivors);
                    live -= Retarget(v0, v1, deletedB, survivors);
                    ReplaceRun(v0, survivors);
                    verts[v1].cornerCount = 0;
                    break;
                }
            }

            // Nothing moved at full price: every remaining collapse costs more than the ceiling
            // allows, and further passes would only raise a threshold that is already clamped.
            if (live == before && atCeiling) return true;
        }
        return true;
    }

    // How many faces a pending collapse would remove. Both lists describe the same shared faces from
    // the two ends of the edge, so a face flagged on both sides must not be counted twice — hence
    // the max rather than the sum. In a manifold interior that is 2; on a boundary edge, 1.
    private static int Doomed(List<bool> a, List<bool> b)
    {
        int countA = 0, countB = 0;
        foreach (bool dead in a) if (dead) countA++;
        foreach (bool dead in b) if (dead) countB++;
        return Math.Max(countA, countB);
    }

    // Would replacing `keep`'s position with `to` turn any face that touches it inside out? A
    // collapse that passes on price and fails here is the common case, not a rare one — the cheapest
    // move on a fold is usually the one that completes the fold.
    //
    // Fills `deleted` with one entry per corner in `keep`'s run, in order, flagging the faces this
    // collapse would remove rather than move. Retarget walks the same run and relies on that
    // alignment.
    private static bool WouldFlip(Vector3 to, int keep, int other, List<bool> deleted)
    {
        for (int i = 0; i < verts[keep].cornerCount; i++)
        {
            Corner corner = corners[verts[keep].cornerStart + i];
            Face face = faces[corner.face];
            deleted.Add(false);
            if (face.deleted) continue;
            // A run can hold entries that an earlier collapse in this same pass made stale — the
            // face still exists but that slot is somebody else's vertex now. Acting on one would
            // rewrite an unrelated corner. They are swept up by the next IndexCorners; until then,
            // ignore them.
            if (VertexAt(corner) != keep) continue;

            int a = corner.slot == 0 ? face.v1 : corner.slot == 1 ? face.v2 : face.v0;
            int b = corner.slot == 0 ? face.v2 : corner.slot == 1 ? face.v0 : face.v1;

            // The face holds both ends of the edge, so the collapse removes it rather than moving it.
            if (a == other || b == other) { deleted[i] = true; continue; }

            Vector3 d1 = (verts[a].position - to).normalized;
            Vector3 d2 = (verts[b].position - to).normalized;
            // Collinear: the face would have zero area. Treat as a flip — a zero-area face has no
            // normal, and keeping it would put a NaN into the next quadric.
            if (Mathf.Abs(Vector3.Dot(d1, d2)) > 0.9999f) return true;

            if (Vector3.Dot(Vector3.Cross(d1, d2).normalized, face.normal) < 0.2f) return true;
        }
        return false;
    }

    // Rewrites every face in `from`'s run to use `keep`, dropping the ones the collapse removes, and
    // appends the survivors to `run`. Returns how many faces died.
    private static int Retarget(int keep, int from, List<bool> deleted, List<Corner> run)
    {
        int died = 0;

        for (int i = 0; i < verts[from].cornerCount; i++)
        {
            Corner corner = corners[verts[from].cornerStart + i];
            if (faces[corner.face].deleted) continue;
            if (VertexAt(corner) != from) continue; // stale — see WouldFlip

            if (i < deleted.Count && deleted[i])
            {
                faces[corner.face].deleted = true;
                died++;
                continue;
            }

            if (corner.slot == 0) faces[corner.face].v0 = keep;
            else if (corner.slot == 1) faces[corner.face].v1 = keep;
            else faces[corner.face].v2 = keep;

            faces[corner.face].dirty = true;
            RefreshEdgeErrors(corner.face);
            run.Add(corner);
        }

        return died;
    }

    // The cost of merging v0 and v1, and where the survivor should sit. Three candidates rather than
    // solving for the true minimum: inverting the quadric is ill-conditioned exactly where it
    // matters most (a flat region, where the matrix is near-singular) and produces a point somewhere
    // out in space. Snapping to one of the two ends or the midpoint is a hair worse on smooth
    // surfaces and far more stable on the machined flats these robots are made of. Picking an END
    // also means most survivors keep an original vertex position exactly.
    private static double EdgeError(int v0, int v1, out Vector3 position)
    {
        Quadric q = verts[v0].quadric + verts[v1].quadric;
        Vector3 p0 = verts[v0].position;
        Vector3 p1 = verts[v1].position;
        Vector3 mid = (p0 + p1) * 0.5f;

        double e0 = q.Evaluate(p0.x, p0.y, p0.z);
        double e1 = q.Evaluate(p1.x, p1.y, p1.z);
        double em = q.Evaluate(mid.x, mid.y, mid.z);

        if (e0 <= e1 && e0 <= em) { position = p0; return e0; }
        if (e1 <= em) { position = p1; return e1; }
        position = mid;
        return em;
    }

    // ---------------------------------------------------------------------------------------------
    // Compact: surviving faces back into a Mesh, submesh by submesh so materials still line up.
    //
    // Normals are rebuilt here rather than by Mesh.RecalculateNormals, which has no smoothing angle
    // and would average across every machined edge — the weld made them all shared vertices, so a
    // square tube would come back a soft cylinder. Each vertex's incident faces are grouped into
    // clusters no wider than the smoothing angle, and each cluster becomes its own output vertex.
    // That re-splits exactly the corners that need splitting and no others.
    // ---------------------------------------------------------------------------------------------
    private static Mesh Compact(Mesh source, Options options)
    {
        IndexCorners();

        float cosLimit = Mathf.Cos(Mathf.Clamp(options.smoothingAngle, 0f, 180f) * Mathf.Deg2Rad);

        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = options.keepUv ? new List<Vector2>() : null;

        // For each vertex, the output index assigned to each of its clusters, and that cluster's
        // running normal sum. Both are small — a vertex has a handful of clusters at most.
        var clusterNormals = new List<Vector3>();
        var clusterOutput = new List<int>();
        // face -> output index, per slot. Written while clustering, read while emitting indices.
        var faceCorner = new int[faces.Length * 3];

        for (int v = 0; v < verts.Length; v++)
        {
            if (verts[v].cornerCount == 0) continue;
            clusterNormals.Clear();
            clusterOutput.Clear();

            for (int i = 0; i < verts[v].cornerCount; i++)
            {
                Corner corner = corners[verts[v].cornerStart + i];
                Vector3 n = FaceNormal(corner.face);

                int cluster = -1;
                for (int c = 0; c < clusterNormals.Count; c++)
                {
                    if (Vector3.Dot(clusterNormals[c].normalized, n) >= cosLimit) { cluster = c; break; }
                }
                if (cluster < 0)
                {
                    cluster = clusterNormals.Count;
                    clusterNormals.Add(Vector3.zero);
                    clusterOutput.Add(positions.Count);
                    positions.Add(verts[v].position);
                    normals.Add(Vector3.zero);
                    uvs?.Add(verts[v].uv);
                }

                // Unnormalized face normals accumulate area weighting for free — a big face should
                // pull the shared normal further than the sliver beside it.
                clusterNormals[cluster] += n * FaceArea(corner.face);
                normals[clusterOutput[cluster]] = clusterNormals[cluster];
                faceCorner[corner.face * 3 + corner.slot] = clusterOutput[cluster];
            }
        }

        for (int i = 0; i < normals.Count; i++)
        {
            normals[i] = normals[i].sqrMagnitude > 0f ? normals[i].normalized : Vector3.up;
        }

        var perSubmesh = new List<int>[Mathf.Max(1, source.subMeshCount)];
        for (int i = 0; i < perSubmesh.Length; i++) perSubmesh[i] = new List<int>();

        for (int f = 0; f < faces.Length; f++)
        {
            if (faces[f].deleted) continue;
            List<int> indices = perSubmesh[Mathf.Clamp(faces[f].submesh, 0, perSubmesh.Length - 1)];
            indices.Add(faceCorner[f * 3]);
            indices.Add(faceCorner[f * 3 + 1]);
            indices.Add(faceCorner[f * 3 + 2]);
        }

        var mesh = new Mesh { name = source.name };
        // 16-bit indices where they fit: half the index memory, and after decimation almost
        // everything fits. Must be set before the index data.
        mesh.indexFormat = positions.Count > 65534 ? IndexFormat.UInt32 : IndexFormat.UInt16;
        mesh.SetVertices(positions);
        mesh.SetNormals(normals);
        if (uvs != null) mesh.SetUVs(0, uvs);

        mesh.subMeshCount = perSubmesh.Length;
        for (int i = 0; i < perSubmesh.Length; i++) mesh.SetTriangles(perSubmesh[i], i, false);

        if (options.recalculateTangents && uvs != null) mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Vector3 FaceNormal(int f)
    {
        Vector3 p0 = verts[faces[f].v0].position;
        Vector3 cross = Vector3.Cross(verts[faces[f].v1].position - p0, verts[faces[f].v2].position - p0);
        return cross.sqrMagnitude > 0f ? cross.normalized : Vector3.up;
    }

    private static float FaceArea(int f)
    {
        Vector3 p0 = verts[faces[f].v0].position;
        return Vector3.Cross(verts[faces[f].v1].position - p0,
                             verts[faces[f].v2].position - p0).magnitude * 0.5f;
    }

    // How far the SURFACE actually moved: for a sample of original vertices, the distance to the
    // nearest point on the decimated surface. This is the only quality signal there is — a triangle
    // count says how much was removed, not whether the part still looks like itself.
    //
    // It measures to the nearest TRIANGLE, not the nearest surviving vertex, and the difference is
    // not academic. Decimating a flat plate moves its surface by exactly zero while moving its
    // vertices arbitrarily far apart, so a vertex-to-vertex measure reports a large number for the
    // one case that is provably perfect — and then everything it says has to be discounted, which
    // means nobody reads it. On this project's own robots that mislabelled untouched flat brackets
    // as having moved 80 mm.
    //
    // Sampled rather than exhaustive: two million source vertices against a spatial query each is
    // minutes of work for a diagnostic, and the maximum over a few thousand well-spread samples
    // finds the same bad case.
    private const int DeviationSamples = 4000;

    private static float Deviation(Mesh source, Mesh simplified)
    {
        Vector3[] after = simplified.vertices;
        int[] indices = simplified.triangles;
        Vector3[] before = source.vertices;
        if (after.Length == 0 || indices.Length == 0 || before.Length == 0) return 0f;

        float cell = Mathf.Max(source.bounds.size.magnitude / 16f, 1e-5f);

        // Each triangle goes in every cell its bounding box touches, so a large triangle — which is
        // exactly what decimation produces — is still found from anywhere along its span.
        var grid = new Dictionary<Vector3Int, List<int>>();
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            Vector3 a = after[indices[t]], b = after[indices[t + 1]], c = after[indices[t + 2]];
            Vector3Int lo = Cell(Vector3.Min(Vector3.Min(a, b), c), cell);
            Vector3Int hi = Cell(Vector3.Max(Vector3.Max(a, b), c), cell);

            for (int x = lo.x; x <= hi.x; x++)
            for (int y = lo.y; y <= hi.y; y++)
            for (int z = lo.z; z <= hi.z; z++)
            {
                var key = new Vector3Int(x, y, z);
                if (!grid.TryGetValue(key, out List<int> bucket)) grid[key] = bucket = new List<int>();
                bucket.Add(t);
            }
        }

        int stride = Mathf.Max(1, before.Length / DeviationSamples);
        float worst = 0f;

        for (int i = 0; i < before.Length; i += stride)
        {
            Vector3 p = before[i];
            Vector3Int key = Cell(p, cell);
            float best = float.MaxValue;

            // Widen the ring until something is found. One ring is almost always enough; the loop
            // exists so a lone vertex in an empty corner reports its real distance instead of
            // silently contributing nothing.
            for (int ring = 1; ring <= 4 && best == float.MaxValue; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                for (int dy = -ring; dy <= ring; dy++)
                for (int dz = -ring; dz <= ring; dz++)
                {
                    if (!grid.TryGetValue(new Vector3Int(key.x + dx, key.y + dy, key.z + dz),
                            out List<int> bucket)) continue;
                    foreach (int t in bucket)
                    {
                        float d = SqrDistanceToTriangle(p, after[indices[t]], after[indices[t + 1]],
                            after[indices[t + 2]]);
                        if (d < best) best = d;
                    }
                }
            }

            if (best < float.MaxValue && best > worst) worst = best;
        }
        return Mathf.Sqrt(worst);
    }

    // Squared distance from a point to a triangle — the closest-point case analysis from Ericson,
    // Real-Time Collision Detection. Seven regions: three vertices, three edges, and the face.
    private static float SqrDistanceToTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) return (p - a).sqrMagnitude;

        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) return (p - b).sqrMagnitude;

        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            return (p - (a + ab * (d1 / (d1 - d3)))).sqrMagnitude;

        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) return (p - c).sqrMagnitude;

        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            return (p - (a + ac * (d2 / (d2 - d6)))).sqrMagnitude;

        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            return (p - (b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))).sqrMagnitude;

        float denom = 1f / (va + vb + vc);
        return (p - (a + ab * (vb * denom) + ac * (vc * denom))).sqrMagnitude;
    }

    private static Vector3Int Cell(Vector3 p, float size) => new Vector3Int(
        Mathf.FloorToInt(p.x / size), Mathf.FloorToInt(p.y / size), Mathf.FloorToInt(p.z / size));
}
