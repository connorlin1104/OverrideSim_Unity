using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

// Editor-only P/Invoke wrapper for V-HACD convex decomposition, backing the tight-collider
// generation of concave parts (GeneratePartColliders.TryBuildVhacdHulls).
//
// Why not the URDF importer package's MeshProcess.VHACD: the dylib it bundles for macOS is
// x86_64-only and fails to dlopen on Apple Silicon. This wrapper targets our own arm64 build
// of the SAME V-HACD source (Unity-Technologies/VHACD, src/dll/src/dll.cpp exports), shipped
// as Assets/Plugins/Editor/librobosim_vhacd.dylib — editor-only, never in a device build
// (hull meshes are baked to assets at edit time).
//
// The Parameters/ConvexHull structs mirror src/VHACD_Lib/public/VHACD.h field-for-field
// (4 doubles, 2 pointers, 9 uint32s, 1 byte-bool) — layout changes there would corrupt the
// call, so keep them in sync with the compiled source.
public static class VhacdNative
{
    private const string LibraryName = "robosim_vhacd";

    // --- thin-plate pre-scale (see GenerateConvexMeshes) ---
    // A part counts as a "thin plate" when its smallest bounding extent is under this fraction of its
    // MIDDLE extent (e.g. a 4.9 x 2.06 x 0.04 polycarb plate: 0.04 << 0.35 x 2.06).
    private const float PlateThinRatio = 0.35f;
    // When it is, the thin axis is stretched so its extent becomes this fraction of the middle extent
    // (aspect ratio ~2) — thick enough that VHACD will actually decompose it, then the hull vertices are
    // un-stretched. Higher = VHACD finds more concavity so a rounded/cut profile splits into MORE hulls
    // that hug the curve tighter (measured on the Goal Aligner plate: 0.3 -> 2 hulls / 7.6% overhang,
    // 0.5 -> 4 hulls / 3.1% overhang). Beyond ~0.5 it stops adding hulls at these params; to chord a
    // curve even finer, lower the caller's VhacdConcavity toward 0 (slower, and affects all plastics).
    private const float PlateTargetThicknessFrac = 0.5f;

    [StructLayout(LayoutKind.Sequential)]
    private struct Parameters
    {
        public double concavity;
        public double alpha;
        public double beta;
        public double minVolumePerCH;
        public IntPtr callback;
        public IntPtr logger;
        public uint resolution;
        public uint maxNumVerticesPerCH;
        public uint planeDownsampling;
        public uint convexhullDownsampling;
        public uint pca;
        public uint mode;
        public uint convexhullApproximation;
        public uint oclAcceleration;
        public uint maxConvexHulls;
        [MarshalAs(UnmanagedType.I1)] public bool projectHullVertices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConvexHull
    {
        public IntPtr points;    // double*, xyz interleaved
        public IntPtr triangles; // uint32*
        public uint nPoints;
        public uint nTriangles;
        public double volume;
        public double centerX;
        public double centerY;
        public double centerZ;
    }

    [DllImport(LibraryName)] private static extern IntPtr CreateVHACD();
    [DllImport(LibraryName)] private static extern void DestroyVHACD(IntPtr vhacd);
    [DllImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool ComputeFloat(IntPtr vhacd, float[] points, uint countPoints,
        uint[] triangles, uint countTriangles, ref Parameters parameters);
    [DllImport(LibraryName)] private static extern uint GetNConvexHulls(IntPtr vhacd);
    [DllImport(LibraryName)] private static extern void GetConvexHull(IntPtr vhacd, uint index, ref ConvexHull hull);

    // Decomposes the mesh into at most maxHulls convex hull meshes (mesh-local space).
    // Returns null/empty when decomposition fails; throws DllNotFoundException when the native
    // plugin is missing — callers are expected to catch and fall back.
    //
    // THIN CUT PLATES (polycarb) need special handling. This VHACD build structurally refuses to
    // decompose a thin/flat part no matter the concavity/resolution/down-sampling: measured directly
    // against the dylib, an L-notched plate splits into 2 hulls at thickness >= ~0.25 of its width but
    // collapses to ONE box-filling hull once it gets thinner (and a real polycarb plate at 122:1 aspect
    // stays 1 hull even at concavity=0, res=4M). So instead of tuning thresholds (which cannot work), we
    // STRETCH the plate's thin axis into a splittable aspect ratio before decomposing, then un-stretch
    // the resulting hull vertices. Affine axis scaling preserves convexity, so the thin hulls still
    // follow the 2D cut profile. Non-plate parts (funnels/dishes, whose extents are comparable) are left
    // unscaled and decompose normally. maxHulls still caps the count.
    public static List<Mesh> GenerateConvexMeshes(Mesh mesh, uint maxHulls, uint resolution,
        double concavity, uint downsampling, double minVolumePerHull)
    {
        Vector3[] vertices = mesh.vertices;
        int[] meshTriangles = mesh.triangles;
        if (vertices == null || vertices.Length < 4 || meshTriangles == null || meshTriangles.Length < 12)
            return null;

        float[] points = new float[vertices.Length * 3];
        for (int i = 0; i < vertices.Length; i++)
        {
            points[i * 3] = vertices[i].x;
            points[i * 3 + 1] = vertices[i].y;
            points[i * 3 + 2] = vertices[i].z;
        }
        uint[] triangles = new uint[meshTriangles.Length];
        for (int i = 0; i < meshTriangles.Length; i++) triangles[i] = (uint)meshTriangles[i];

        // Thin-plate pre-scale: stretch the smallest bounding axis so VHACD will decompose it (see the
        // method header). Computed from the mesh AABB; scaleK stays 1 for non-plate parts.
        Vector3 mn = vertices[0], mx = vertices[0];
        for (int i = 1; i < vertices.Length; i++) { mn = Vector3.Min(mn, vertices[i]); mx = Vector3.Max(mx, vertices[i]); }
        Vector3 ext = mx - mn;
        int thinAxis = 0;
        if (ext[1] < ext[thinAxis]) thinAxis = 1;
        if (ext[2] < ext[thinAxis]) thinAxis = 2;
        float thin = ext[thinAxis];
        float median = ext[0] + ext[1] + ext[2]
                       - Mathf.Min(ext[0], Mathf.Min(ext[1], ext[2]))
                       - Mathf.Max(ext[0], Mathf.Max(ext[1], ext[2]));
        double scaleK = 1.0;
        if (thin > 1e-9f && thin < PlateThinRatio * median)
        {
            scaleK = (PlateTargetThicknessFrac * median) / thin;
            for (int i = 0; i < vertices.Length; i++)
                points[i * 3 + thinAxis] = (float)(points[i * 3 + thinAxis] * scaleK);
        }

        // Defaults mirror VHACD.h Parameters::Init() (OpenCL off), EXCEPT the split-sensitivity knobs
        // (concavity / down-sampling / minVolumePerCH), which the caller tunes for thin cut plates.
        Parameters parameters = new Parameters
        {
            concavity = concavity,
            alpha = 0.05,
            beta = 0.05,
            minVolumePerCH = minVolumePerHull,
            callback = IntPtr.Zero,
            logger = IntPtr.Zero,
            resolution = resolution,
            maxNumVerticesPerCH = 64,
            planeDownsampling = downsampling,
            convexhullDownsampling = downsampling,
            pca = 0,
            mode = 0, // voxel-based (recommended)
            convexhullApproximation = 1,
            oclAcceleration = 0,
            maxConvexHulls = maxHulls,
            projectHullVertices = true,
        };

        IntPtr vhacd = CreateVHACD();
        try
        {
            if (!ComputeFloat(vhacd, points, (uint)vertices.Length, triangles,
                    (uint)(meshTriangles.Length / 3), ref parameters))
                return null;

            uint hullCount = GetNConvexHulls(vhacd);
            var result = new List<Mesh>((int)hullCount);
            for (uint i = 0; i < hullCount; i++)
            {
                ConvexHull hull = default;
                GetConvexHull(vhacd, i, ref hull);
                if (hull.nPoints < 4 || hull.nTriangles < 4) continue;

                // Copy out immediately — the buffers belong to the native context and die
                // with DestroyVHACD below.
                double[] rawPoints = new double[hull.nPoints * 3];
                Marshal.Copy(hull.points, rawPoints, 0, rawPoints.Length);
                int[] indices = new int[hull.nTriangles * 3];
                Marshal.Copy(hull.triangles, indices, 0, indices.Length);

                Vector3[] hullVertices = new Vector3[hull.nPoints];
                for (int v = 0; v < hullVertices.Length; v++)
                {
                    Vector3 hv = new Vector3(
                        (float)rawPoints[v * 3],
                        (float)rawPoints[v * 3 + 1],
                        (float)rawPoints[v * 3 + 2]);
                    // Undo the thin-plate pre-scale so the hull sits back in the mesh's real dimensions.
                    if (scaleK != 1.0) hv[thinAxis] = (float)(hv[thinAxis] / scaleK);
                    hullVertices[v] = hv;
                }

                Mesh hullMesh = new Mesh();
                hullMesh.SetVertices(hullVertices);
                hullMesh.SetTriangles(indices, 0);
                hullMesh.RecalculateNormals();
                hullMesh.RecalculateBounds();
                result.Add(hullMesh);
            }
            return result;
        }
        finally
        {
            DestroyVHACD(vhacd); // Clean() + Release() on the native side
        }
    }
}
