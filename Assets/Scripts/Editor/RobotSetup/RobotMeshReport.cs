using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

// What each robot's geometry actually costs, per robot and per mesh.
//
// This exists because "the robot is 113 MB" is not an actionable number — it doesn't say whether the
// cost is a few enormous meshes (decimate them), a thousand small ones (the per-mesh overhead and
// the draw calls are the problem), or uncompressed vertex channels nothing reads (strip them). Those
// have three different fixes and picking the wrong one is a week of work for nothing.
//
// Read the report top-down: the per-robot totals say how big the problem is, the biggest-meshes list
// says whether it is concentrated, and the channel columns say how much of each vertex is dead
// weight. Runtime bytes are what a device actually holds; the asset on disk is a different (usually
// smaller) number, so don't compare this to the FBX file size directly.
//
// Usage: Tools > RoboSim > Robot > Robot Mesh Report.
// Batch: -executeMethod RobotMeshReport.RunBatch
public static class RobotMeshReport
{
    [MenuItem("Tools/RoboSim/Robot/Robot Mesh Report", false, 40)]
    private static void RunFromMenu()
    {
        string report = Build();
        Debug.Log(report);
        EditorUtility.DisplayDialog("Robot Mesh Report",
            "Written to the Console — it's a table, and this dialog would wrap it.", "OK");
    }

    public static void RunBatch()
    {
        Debug.Log(Build());
    }

    // One mesh, counted once however many renderers share it. Sharing is the norm in a CAD export
    // (every identical screw points at one mesh), so counting per-renderer would multiply the true
    // cost by ten and send you decimating something that is already cheap.
    public class MeshCost
    {
        public Mesh mesh;
        public int users;
        public long bytes;
        public int triangles;
        public int vertices;
        public string robot;
    }

    public static string Build()
    {
        RobotModelCatalog catalog =
            AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(RoboSimPaths.RobotModelCatalog);
        if (catalog == null || catalog.models == null)
            return $"Robot Mesh Report: no catalog at {RoboSimPaths.RobotModelCatalog}.";

        var report = new StringBuilder();
        report.AppendLine("Robot Mesh Report");
        report.AppendLine();
        report.AppendLine(
            $"{"robot",-24} {"meshes",7} {"tris",10} {"verts",10} {"runtime",10}  channels");

        var everything = new List<MeshCost>();
        long grandTotal = 0;

        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry == null || entry.prefab == null) continue;

            List<MeshCost> costs = Measure(entry.prefab, entry.displayName);
            everything.AddRange(costs);

            long bytes = 0;
            long tris = 0;
            long verts = 0;
            foreach (MeshCost c in costs) { bytes += c.bytes; tris += c.triangles; verts += c.vertices; }
            grandTotal += bytes;

            report.AppendLine(
                $"{Trim(entry.displayName, 24),-24} {costs.Count,7} {tris,10:N0} {verts,10:N0} " +
                $"{Bytes(bytes),10}  {Channels(costs)}");
        }

        report.AppendLine();
        report.AppendLine($"Total runtime mesh data across every robot: {Bytes(grandTotal)}");

        // Is the cost concentrated or spread? If the top 20 meshes are most of the total, decimation
        // is the lever. If they're a rounding error, the count is the problem and decimation won't
        // move the number no matter how hard it is run.
        everything.Sort((a, b) => b.bytes.CompareTo(a.bytes));
        report.AppendLine();
        report.AppendLine("Biggest single meshes:");
        report.AppendLine($"{"mesh",-40} {"robot",-16} {"tris",9} {"runtime",10} {"used",5}");
        for (int i = 0; i < Mathf.Min(20, everything.Count); i++)
        {
            MeshCost c = everything[i];
            report.AppendLine(
                $"{Trim(c.mesh.name, 40),-40} {Trim(c.robot, 16),-16} {c.triangles,9:N0} " +
                $"{Bytes(c.bytes),10} {c.users,5}");
        }

        long top20 = 0;
        for (int i = 0; i < Mathf.Min(20, everything.Count); i++) top20 += everything[i].bytes;
        if (grandTotal > 0)
        {
            report.AppendLine();
            report.AppendLine($"Those 20 are {100f * top20 / grandTotal:F1}% of all robot mesh data " +
                              $"({everything.Count} meshes in total).");
        }

        report.AppendLine();
        report.AppendLine(ImporterSettings());
        return report.ToString();
    }

    // Every distinct mesh under `root`, with what it costs. Includes inactive children: a mechanism
    // parked inactive in the prefab still ships its geometry.
    public static List<MeshCost> Measure(GameObject root, string robotName)
    {
        var byMesh = new Dictionary<Mesh, MeshCost>();

        foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            Add(byMesh, filter.sharedMesh, robotName);
        foreach (SkinnedMeshRenderer skin in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            Add(byMesh, skin.sharedMesh, robotName);

        return new List<MeshCost>(byMesh.Values);
    }

    private static void Add(Dictionary<Mesh, MeshCost> byMesh, Mesh mesh, string robotName)
    {
        if (mesh == null) return;
        if (byMesh.TryGetValue(mesh, out MeshCost existing)) { existing.users++; return; }

        byMesh[mesh] = new MeshCost
        {
            mesh = mesh,
            users = 1,
            robot = robotName,
            bytes = Profiler.GetRuntimeMemorySizeLong(mesh),
            triangles = TriangleCount(mesh),
            vertices = mesh.vertexCount,
        };
    }

    // Sum over submeshes. mesh.triangles allocates the whole index array, which on a CAD import is
    // millions of ints per mesh — GetIndexCount reads the descriptor instead.
    private static int TriangleCount(Mesh mesh)
    {
        int indices = 0;
        for (int i = 0; i < mesh.subMeshCount; i++) indices += (int)mesh.GetIndexCount(i);
        return indices / 3;
    }

    // Which vertex channels are present anywhere in this robot. A channel nobody reads is pure
    // weight: tangents alone are 16 bytes on every vertex, and a Fusion export has no normal map to
    // need them.
    private static string Channels(List<MeshCost> costs)
    {
        bool normals = false, tangents = false, uv = false, uv2 = false, colors = false, readable = false;
        foreach (MeshCost c in costs)
        {
            if (c.mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal)) normals = true;
            if (c.mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent)) tangents = true;
            if (c.mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord0)) uv = true;
            if (c.mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.TexCoord1)) uv2 = true;
            if (c.mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Color)) colors = true;
            if (c.mesh.isReadable) readable = true;
        }

        var parts = new List<string>();
        if (normals) parts.Add("normal");
        if (tangents) parts.Add("tangent");
        if (uv) parts.Add("uv");
        if (uv2) parts.Add("uv2");
        if (colors) parts.Add("color");
        if (readable) parts.Add("READ/WRITE");
        return parts.Count == 0 ? "position only" : string.Join("+", parts);
    }

    // The importer settings on every model file the robots draw from. Mesh Compression defaults to
    // Off, which is why imported data measured LARGER than the source FBX.
    private static string ImporterSettings()
    {
        var report = new StringBuilder();
        report.AppendLine("Model importer settings:");
        foreach (string guid in AssetDatabase.FindAssets("t:Model"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (AssetImporter.GetAtPath(path) is not ModelImporter importer) continue;
            report.AppendLine($"  {Trim(System.IO.Path.GetFileName(path), 40),-40} " +
                              $"compression={importer.meshCompression} " +
                              $"readWrite={importer.isReadable} " +
                              $"optimize={importer.optimizeMeshPolygons} " +
                              $"weld={importer.weldVertices} " +
                              $"normals={importer.importNormals} tangents={importer.importTangents}");
        }
        return report.ToString();
    }

    private static string Bytes(long bytes)
    {
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024f * 1024f):F1} MB";
        if (bytes >= 1024L) return $"{bytes / 1024f:F0} KB";
        return $"{bytes} B";
    }

    private static string Trim(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
    }
}
