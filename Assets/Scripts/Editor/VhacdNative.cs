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
    public static List<Mesh> GenerateConvexMeshes(Mesh mesh, uint maxHulls, uint resolution)
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

        // Defaults mirror VHACD.h Parameters::Init(), with OpenCL off (not compiled in).
        Parameters parameters = new Parameters
        {
            concavity = 0.001,
            alpha = 0.05,
            beta = 0.05,
            minVolumePerCH = 0.0001,
            callback = IntPtr.Zero,
            logger = IntPtr.Zero,
            resolution = resolution,
            maxNumVerticesPerCH = 64,
            planeDownsampling = 4,
            convexhullDownsampling = 4,
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
                    hullVertices[v] = new Vector3(
                        (float)rawPoints[v * 3],
                        (float)rawPoints[v * 3 + 1],
                        (float)rawPoints[v * 3 + 2]);
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
