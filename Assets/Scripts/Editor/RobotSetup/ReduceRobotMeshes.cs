using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

// Replaces a robot's imported render meshes with decimated copies saved as standalone assets.
//
// This is the first step of making robots deliverable at all: measured on this project, one 654V is
// ~2.8 million triangles and ~226 MB of runtime mesh data. Every later idea — downloading a robot,
// keeping the binary from growing, holding three robots on a phone — is arithmetic that doesn't work
// until this number comes down. It is also worth doing on its own: that geometry is what the
// renderer chews through every frame on the field scene.
//
// TWO THINGS HAPPEN HERE and it's worth being clear they're separate:
//  1. The meshes get smaller (MeshDecimator).
//  2. They stop being sub-assets of the FBX. The prefab used to hold ~1,550 pointers into
//     Ryan_CascadeRobot.fbx, which made the source file a permanent build dependency — delete it and
//     the robot is correct joints with no geometry. The decimated copies are ordinary .asset files
//     that own their data, so after this the FBX is an archive rather than a dependency.
//
// WHAT IT DOES NOT TOUCH: colliders. Robot colliders are separately generated hulls under
// Assets/RobotColliders/ (see GeneratePartColliders), so nothing here can change how a robot drives.
// If a MeshCollider is found sharing a render mesh, it is left alone and reported — silently swapping
// a collider's mesh would retune a robot behind your back.
//
// Usage: select the robot root in the scene, or a prefab under Assets/Robots/, then
// Tools > RoboSim > Robot > Reduce Robot Meshes.
public class ReduceRobotMeshesWindow : EditorWindow
{
    private const string Title = "Reduce Robot Meshes";

    [SerializeField] private GameObject robot;
    [SerializeField] private float targetRatio = 0.08f;
    [SerializeField] private float maxErrorFraction = 0.004f;
    [SerializeField] private float smoothingAngle = 55f;
    [SerializeField] private bool keepUv;

    private string lastReport = string.Empty;
    private Vector2 scroll;

    [MenuItem("Tools/RoboSim/Robot/Reduce Robot Meshes", false, 3)]
    private static void ShowWindow()
    {
        ReduceRobotMeshesWindow window = GetWindow<ReduceRobotMeshesWindow>(Title);
        window.minSize = new Vector2(520f, 420f);
        window.Show();
    }

    private void OnEnable()
    {
        if (robot == null) robot = Selection.activeGameObject;
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Decimates a robot's render meshes and saves them as standalone assets, then points the " +
            "robot at the copies. Colliders are untouched, so this cannot change how the robot " +
            "drives. Run it after Save As Robot Prefab.", MessageType.Info);

        robot = (GameObject)EditorGUILayout.ObjectField("Robot", robot, typeof(GameObject), true);
        if (robot == null)
        {
            EditorGUILayout.HelpBox("Pick the robot root in the scene, or a prefab under Assets/Robots/.",
                MessageType.Warning);
            return;
        }

        EditorGUILayout.Space();
        targetRatio = EditorGUILayout.Slider(new GUIContent("Keep",
            "Fraction of the original triangles to aim for. 0.08 takes 2.8M down to about 220k, " +
            "which is still far more than a phone screen can show."), targetRatio, 0.01f, 1f);
        maxErrorFraction = EditorGUILayout.Slider(new GUIContent("Max Error",
            "Stop early if the next collapse would move the surface further than this fraction of " +
            "the part's own size. This is the safety catch: a simple bracket stops at its natural " +
            "shape instead of being reduced to a triangle to hit the ratio."),
            maxErrorFraction, 0.0002f, 0.05f);
        smoothingAngle = EditorGUILayout.Slider(new GUIContent("Smoothing Angle",
            "Faces meeting at a sharper angle than this keep separate normals. Too high and machined " +
            "edges melt into curves."), smoothingAngle, 0f, 180f);
        keepUv = EditorGUILayout.Toggle(new GUIContent("Keep UVs",
            "Only if a material actually samples a texture. This project has none, so leaving this " +
            "off drops 8 bytes a vertex for nothing."), keepUv);

        EditorGUILayout.Space();
        if (GUILayout.Button("Reduce Meshes", GUILayout.Height(30)))
        {
            var options = new MeshDecimator.Options
            {
                targetRatio = targetRatio,
                maxErrorFraction = maxErrorFraction,
                smoothingAngle = smoothingAngle,
                keepUv = keepUv,
            };
            try { lastReport = ReduceRobotMeshes.Run(robot, options); }
            catch (System.Exception e) { lastReport = e.Message; Debug.LogException(e); }
        }

        if (string.IsNullOrEmpty(lastReport)) return;
        EditorGUILayout.Space();
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.TextArea(lastReport, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }
}

public static class ReduceRobotMeshes
{
    private const string MeshFolder = "Assets/RobotMeshes";

    // Headless entry: -executeMethod ReduceRobotMeshes.RunBatch -robot <name> [-keep 0.08]
    //
    // Reducing every robot at once is a one-line loop away, and deliberately not offered: the ratio
    // is a judgement about how a particular robot looks, and the sensible way to find it is one
    // robot at a time with the result in front of you. A flag that restyled the whole fleet in one
    // command would get used before anyone had looked at the first one.
    public static void RunBatch()
    {
        string name = Argument("-robot");
        if (string.IsNullOrEmpty(name))
            throw new System.ArgumentException("ReduceRobotMeshes.RunBatch needs -robot <prefab name>.");

        string path = $"{RoboSimPaths.RobotsFolder}/{name}.prefab";
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        if (prefab == null) throw new System.ArgumentException($"No robot prefab at {path}.");

        var options = new MeshDecimator.Options();
        string keep = Argument("-keep");
        if (!string.IsNullOrEmpty(keep) && float.TryParse(keep, out float ratio)) options.targetRatio = ratio;
        string error = Argument("-maxError");
        if (!string.IsNullOrEmpty(error) && float.TryParse(error, out float maxError))
            options.maxErrorFraction = maxError;

        Debug.Log(Run(prefab, options));
    }

    private static string Argument(string flag)
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    // Decimates every render mesh under `robot` and repoints it at the results. Works on a scene
    // object or on a prefab asset; a prefab is opened, edited and saved. Returns a report.
    public static string Run(GameObject robot, MeshDecimator.Options options)
    {
        if (robot == null) throw new System.ArgumentNullException(nameof(robot));

        string assetPath = AssetDatabase.GetAssetPath(robot);
        bool isPrefabAsset = !string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".prefab");
        if (!isPrefabAsset) return Reduce(robot, robot.name, options);

        GameObject contents = PrefabUtility.LoadPrefabContents(assetPath);
        try
        {
            string report = Reduce(contents, System.IO.Path.GetFileNameWithoutExtension(assetPath), options);
            PrefabUtility.SaveAsPrefabAsset(contents, assetPath);
            return report;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(contents);
        }
    }

    private static string Reduce(GameObject root, string robotName, MeshDecimator.Options options)
    {
        MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
        if (filters.Length == 0) return $"'{robotName}' has no MeshFilters — nothing to reduce.";

        // One decimation per distinct mesh, not per renderer. Sharing is the norm in a CAD export
        // (every identical screw points at one mesh), so doing it per renderer would repeat the same
        // expensive work five times and then write five identical assets.
        var sources = new List<Mesh>();
        var seen = new HashSet<Mesh>();
        foreach (MeshFilter filter in filters)
        {
            if (filter.sharedMesh == null || !seen.Add(filter.sharedMesh)) continue;
            // Already one of ours from a previous run: decimating a decimated mesh compounds the
            // error and there is no way back without re-importing.
            if (AssetDatabase.GetAssetPath(filter.sharedMesh).StartsWith(MeshFolder)) continue;
            sources.Add(filter.sharedMesh);
        }
        if (sources.Count == 0)
            return $"'{robotName}' is already using reduced meshes — nothing left to do.";

        List<string> unlocked = MakeReadable(sources);
        string folder = EnsureFolder(robotName);

        var replacements = new Dictionary<Mesh, Mesh>();
        var report = new StringBuilder();
        report.AppendLine($"Reduce Robot Meshes — {robotName}");
        report.AppendLine();
        report.AppendLine($"{"mesh",-28} {"tris in",10} {"tris out",10} {"kept",6} {"moved",10}");

        long trisBefore = 0, trisOut = 0, vertsBefore = 0, vertsOut = 0;
        var usedNames = new HashSet<string>();
        int stoppedEarly = 0;

        try
        {
            for (int i = 0; i < sources.Count; i++)
            {
                Mesh source = sources[i];
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Reduce Robot Meshes", $"{source.name}  ({i + 1}/{sources.Count})",
                        (float)i / sources.Count))
                {
                    report.AppendLine();
                    report.AppendLine($"CANCELLED after {i} of {sources.Count}. What was written is " +
                                      "kept and linked; run it again to finish the rest.");
                    break;
                }

                MeshDecimator.Result result = MeshDecimator.Simplify(source, options);
                trisBefore += result.sourceTriangles;
                vertsBefore += result.sourceVertices;

                if (!string.IsNullOrEmpty(result.note))
                {
                    report.AppendLine($"{Trim(source.name, 28),-28} {result.sourceTriangles,10:N0} " +
                                      $"{"-",10} {"-",6}  {result.note}");
                    trisOut += result.sourceTriangles;
                    vertsOut += result.sourceVertices;
                    continue;
                }

                string path = $"{folder}/{UniqueName(source.name, usedNames)}.asset";
                AssetDatabase.CreateAsset(result.mesh, path);
                replacements[source] = result.mesh;

                trisOut += result.triangles;
                vertsOut += result.vertices;
                if (result.hitErrorCeiling) stoppedEarly++;

                report.AppendLine(
                    $"{Trim(source.name, 28),-28} {result.sourceTriangles,10:N0} {result.triangles,10:N0} " +
                    $"{result.TriangleRatio,6:P0} {result.maxDeviation,10:F4}" +
                    (result.hitErrorCeiling ? "  (stopped on error)" : string.Empty));
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.SaveAssets();
            RestoreReadable(unlocked);
        }

        int repointed = 0;
        foreach (MeshFilter filter in filters)
        {
            if (filter.sharedMesh == null) continue;
            if (!replacements.TryGetValue(filter.sharedMesh, out Mesh replacement)) continue;
            Undo.RecordObject(filter, "Reduce Robot Meshes");
            filter.sharedMesh = replacement;
            EditorUtility.SetDirty(filter);
            repointed++;
        }

        // A MeshCollider sharing a render mesh means physics reads the geometry we just changed.
        // Nothing here rewrites it — that would retune the robot silently — but you need to know.
        var physicsSharers = new List<string>();
        foreach (MeshCollider collider in root.GetComponentsInChildren<MeshCollider>(true))
        {
            if (collider.sharedMesh != null && replacements.ContainsKey(collider.sharedMesh))
                physicsSharers.Add(collider.name);
        }

        report.AppendLine();
        report.AppendLine($"triangles  {trisBefore,12:N0} -> {trisOut,12:N0}   " +
                          $"({(trisBefore == 0 ? 0f : 1f - (float)trisOut / trisBefore):P1} removed)");
        report.AppendLine($"vertices   {vertsBefore,12:N0} -> {vertsOut,12:N0}");
        report.AppendLine($"{repointed} of {filters.Length} MeshFilters repointed at {replacements.Count} " +
                          $"new meshes in {folder}.");
        if (stoppedEarly > 0)
            report.AppendLine($"{stoppedEarly} mesh(es) stopped on the error ceiling before reaching the " +
                              "ratio — they were already about as simple as their shape allows.");
        if (physicsSharers.Count > 0)
        {
            report.AppendLine();
            report.AppendLine($"WARNING: {physicsSharers.Count} MeshCollider(s) share a render mesh and " +
                              "were NOT changed, so they now differ from what is drawn: " +
                              string.Join(", ", physicsSharers));
        }
        report.AppendLine();
        report.AppendLine("The source FBX is no longer referenced by these renderers. Keep it archived " +
                          "anyway — it is the only way to redo this at a different ratio.");

        return report.ToString();
    }

    // Decimation reads vertex data, and Unity only keeps a CPU copy when the importer says to. This
    // turns Read/Write on for the source models, which forces a reimport of a 100 MB+ FBX and is the
    // slow part of the whole operation, then puts the setting back so builds don't carry the extra
    // copy. Returns the paths it changed.
    private static List<string> MakeReadable(List<Mesh> meshes)
    {
        var changed = new List<string>();
        var considered = new HashSet<string>();

        foreach (Mesh mesh in meshes)
        {
            if (mesh.isReadable) continue;
            string path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path) || !considered.Add(path)) continue;
            if (AssetImporter.GetAtPath(path) is not ModelImporter importer || importer.isReadable) continue;

            importer.isReadable = true;
            importer.SaveAndReimport();
            changed.Add(path);
        }
        return changed;
    }

    private static void RestoreReadable(List<string> paths)
    {
        foreach (string path in paths)
        {
            if (AssetImporter.GetAtPath(path) is not ModelImporter importer) continue;
            importer.isReadable = false;
            importer.SaveAndReimport();
        }
    }

    private static string EnsureFolder(string robotName)
    {
        if (!AssetDatabase.IsValidFolder(MeshFolder))
            AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(MeshFolder));

        string safe = Sanitize(robotName);
        string folder = $"{MeshFolder}/{safe}";
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(MeshFolder, safe);
        return folder;
    }

    // CAD exports name half a robot "Body1", so the mesh name alone collides constantly and
    // CreateAsset would overwrite the previous one without a word.
    private static string UniqueName(string name, HashSet<string> used)
    {
        string stem = Sanitize(string.IsNullOrWhiteSpace(name) ? "Mesh" : name);
        if (used.Add(stem)) return stem;
        for (int i = 2; ; i++)
        {
            string candidate = $"{stem}_{i}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '_' || c == '-') sb.Append(c);
            else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
        }
        string s = sb.ToString().Trim('_');
        return s.Length == 0 ? "Mesh" : s;
    }

    private static string Trim(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
    }
}
