using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Builds a set-up robot into an AssetBundle, so it can reach a player without being compiled into
// the app.
//
// This slots into the existing pipeline rather than replacing it. Steps 1-5 are unchanged — download
// the submission, import it, Set Up Imported Robot, build the mechanisms by hand, Save As Robot
// Prefab. Then instead of "wait for the next App Store build", one more command here.
//
// WHAT CHANGES FOR A ROBOT THAT GOES THIS WAY:
//   - It stops being in the binary. That is the point: every accepted upload is otherwise on every
//     player's device forever, and one robot is ~226 MB of mesh data before decimation.
//   - A private one becomes genuinely private. It lives at an address derived from its owner code
//     (RobotBundleAddress), so a device without the code cannot name the file, let alone fetch it.
//   - It becomes version-coupled to the scripts on robot prefabs. This is the real cost and it is
//     not optional to think about: see RobotBundleFormat, and Rebuild All Robot Bundles below.
//
// TWO DESTINATIONS. StreamingAssets puts the bundle inside the app — no server, no cost, no auth —
// which is the way to prove the loading path works against real robots before any of it is exposed
// to a player. Storage is the real one.
//
// Usage: Tools > RoboSim > Robot > Build Robot Bundle.
public class BuildRobotBundlesWindow : EditorWindow
{
    private const string Title = "Build Robot Bundle";

    [SerializeField] private int entryIndex;
    [SerializeField] private bool remote = true;
    [SerializeField] private bool removeFromBinary = true;
    [SerializeField] private bool iOS = true;
    [SerializeField] private bool android = true;
    [SerializeField] private bool desktop;

    private string lastReport = string.Empty;
    private Vector2 scroll;

    [MenuItem("Tools/RoboSim/Robot/Build Robot Bundle", false, 30)]
    private static void ShowWindow()
    {
        BuildRobotBundlesWindow window = GetWindow<BuildRobotBundlesWindow>(Title);
        window.minSize = new Vector2(560f, 460f);
        window.Show();
    }

    private void OnGUI()
    {
        RobotModelCatalog catalog =
            AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(RoboSimPaths.RobotModelCatalog);
        if (catalog == null || catalog.models == null || catalog.models.Count == 0)
        {
            EditorGUILayout.HelpBox($"No catalog at {RoboSimPaths.RobotModelCatalog}.", MessageType.Error);
            return;
        }

        EditorGUILayout.HelpBox(
            "Builds a robot into a downloadable AssetBundle. Run Save As Robot Prefab first, and " +
            "Reduce Robot Meshes before that unless you want to publish a 226 MB download.",
            MessageType.Info);

        var names = new string[catalog.models.Count];
        for (int i = 0; i < names.Length; i++)
        {
            RobotModelCatalog.Entry e = catalog.models[i];
            names[i] = e == null ? "(null)" :
                $"{e.displayName} {(e.visibility == RobotModelCatalog.Visibility.Private ? "[private]" : string.Empty)}";
        }
        entryIndex = EditorGUILayout.Popup("Robot", Mathf.Clamp(entryIndex, 0, names.Length - 1), names);

        RobotModelCatalog.Entry entry = catalog.models[Mathf.Clamp(entryIndex, 0, names.Length - 1)];
        if (entry == null) return;

        GameObject source = BuildRobotBundles.SourcePrefab(entry);
        EditorGUILayout.LabelField("Source prefab", source == null ? "(none — save it as a prefab first)"
            : AssetDatabase.GetAssetPath(source));
        EditorGUILayout.LabelField("Address", RobotBundleAddress.RemotePath(entry, "…"));
        if (entry.visibility == RobotModelCatalog.Visibility.Private)
        {
            EditorGUILayout.HelpBox(
                "Private: the folder name above is derived from this robot's FIRST owner code. Change " +
                "the code and the address changes, so anything already published under the old one " +
                "becomes unreachable — republish after changing a code.", MessageType.Warning);
        }

        EditorGUILayout.Space();
        remote = EditorGUILayout.Toggle(new GUIContent("Serve From Storage",
            "On: upload it and players download on demand. Off: ship it inside the app in " +
            "StreamingAssets — no server and no cost, which is how to test the loading path."), remote);
        removeFromBinary = EditorGUILayout.Toggle(new GUIContent("Remove From Binary",
            "Clears the catalog's direct prefab reference, which is the thing that compiles the " +
            "robot into the app. Leave this ON — with it off the bundle is built and then never " +
            "used, because a direct reference always wins."), removeFromBinary);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Platforms", EditorStyles.boldLabel);
        iOS = Toggle(iOS, "iOS", BuildTarget.iOS);
        android = Toggle(android, "Android", BuildTarget.Android);
        desktop = Toggle(desktop, "macOS (for testing in the Editor)", BuildTarget.StandaloneOSX);

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(source == null))
        {
            if (GUILayout.Button("Build", GUILayout.Height(30)))
            {
                var targets = new List<BuildTarget>();
                if (iOS) targets.Add(BuildTarget.iOS);
                if (android) targets.Add(BuildTarget.Android);
                if (desktop) targets.Add(BuildTarget.StandaloneOSX);
                try { lastReport = BuildRobotBundles.Build(entry, targets, remote, removeFromBinary); }
                catch (System.Exception e) { lastReport = e.Message; Debug.LogException(e); }
            }
        }

        if (string.IsNullOrEmpty(lastReport)) return;
        EditorGUILayout.Space();
        scroll = EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.TextArea(lastReport, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();
    }

    // A platform whose build support isn't installed cannot produce a bundle, and finding that out
    // as an exception halfway through a five-minute build is a poor way to learn it.
    private static bool Toggle(bool value, string label, BuildTarget target)
    {
        bool supported = BuildPipeline.IsBuildTargetSupported(
            BuildPipeline.GetBuildTargetGroup(target), target);
        using (new EditorGUI.DisabledScope(!supported))
        {
            return EditorGUILayout.Toggle(new GUIContent(supported ? label
                : label + "  (build support not installed)"), supported && value);
        }
    }
}

public static class BuildRobotBundles
{
    // Where built bundles land on the way out. Under Build/ rather than Assets/ so Unity never
    // imports them — a bundle inside Assets/ would be re-serialized into the next one.
    public const string StagingFolder = "Build/RobotBundles";

    // Where a bundle is staged for upload. The staging tree is rsync'd to Storage VERBATIM, so every
    // path under it has to be the address the app will ask for — RobotBundleAddress.RemotePath, the
    // same function RobotBundleService.Load uses to build its download URL.
    //
    // This used to be Path.Combine(StagingFolder, RobotBundleAddress.Folder(entry), relative), which
    // drops the "robots/" prefix that RemotePath adds. The INDEX never had that bug (it goes through
    // IndexPath, which does prefix), so a publish would have produced a reachable index listing a
    // robot whose bundle 404s — discovery works, download doesn't. Nothing had been published yet,
    // so it was never hit, and the suite missed it because it only ever checked RemotePath against
    // itself. RobotBundleValidation now pins the publisher to the reader instead.
    public static string StagedRemotePath(RobotModelCatalog.Entry entry, string relative) =>
        Path.Combine(StagingFolder, RobotBundleAddress.RemotePath(entry, relative));

    public const string StreamingRoot = "Assets/StreamingAssets";

    // How big this robot would be to download, without publishing it or touching the catalog.
    //
    // Worth having as its own command: the decision to deliver a robot as a download is mostly a
    // decision about whether a player on a phone will actually wait for it, and that is a number
    // nobody can estimate from the triangle count. It is also the honest way to check what Reduce
    // Robot Meshes bought — the bundle is compressed, so the saving is never the same as the saving
    // in mesh bytes.
    //
    // Batch: -executeMethod BuildRobotBundles.RunBatchMeasure -robot <catalog id> [-platform iOS]
    public static void RunBatchMeasure()
    {
        string id = Argument("-robot");
        RobotModelCatalog catalog =
            AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(RoboSimPaths.RobotModelCatalog);
        RobotModelCatalog.Entry entry = catalog?.models?.Find(e => e != null && e.id == id);
        if (entry == null)
            throw new System.ArgumentException($"No catalog entry '{id}'.");

        string platform = Argument("-platform") ?? "StandaloneOSX";
        if (!System.Enum.TryParse(platform, out BuildTarget target))
            throw new System.ArgumentException($"Unknown platform '{platform}'.");

        GameObject source = SourcePrefab(entry);
        if (source == null) throw new System.InvalidOperationException($"'{id}' has no prefab.");

        BuildOne(AssetDatabase.GetAssetPath(source), RobotBundleFormat.Sanitize(entry.id), target,
            out string hash, out long bytes);

        Debug.Log($"Bundle measure: {entry.displayName} [{target}] = {Format(bytes)} " +
                  $"({bytes:N0} bytes), content {hash}. Nothing was published and the catalog is " +
                  "unchanged.");
    }

    private static string Argument(string flag)
    {
        string[] args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    // Rebuilds every robot that is already delivered as a bundle.
    //
    // THIS IS THE COMMAND THAT MAKES BUNDLES SAFE TO USE. A bundle serializes the scripts on the
    // robot prefab, so any change to a serialized field on RobotMotorController, ClawGrab, a
    // mechanism — the kind of change this project makes constantly — invalidates every bundle built
    // before it. Without one command that redoes all of them, the fleet drifts out of date one robot
    // at a time and the drift is invisible until a player reports that their robot drives wrong.
    //
    // Bump RobotBundleFormat.Version, run this, republish. RobotBundleValidation fails the build if
    // the scripts changed shape and the version didn't, so the first half is checked rather than
    // remembered.
    [MenuItem("Tools/RoboSim/Robot/Rebuild All Robot Bundles", false, 31)]
    private static void RebuildAllFromMenu()
    {
        RobotModelCatalog catalog =
            AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(RoboSimPaths.RobotModelCatalog);
        List<RobotModelCatalog.Entry> bundled = Bundled(catalog);

        if (bundled.Count == 0)
        {
            EditorUtility.DisplayDialog("Rebuild All Robot Bundles",
                "No robot in the catalog is delivered as a bundle yet.", "OK");
            return;
        }

        var targets = new List<BuildTarget>();
        foreach (BuildTarget target in new[] { BuildTarget.iOS, BuildTarget.Android, BuildTarget.StandaloneOSX })
        {
            if (BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target))
                targets.Add(target);
        }

        if (!EditorUtility.DisplayDialog("Rebuild All Robot Bundles",
                $"Rebuild {bundled.Count} robot(s) for {string.Join(", ", targets)} at format " +
                $"v{RobotBundleFormat.Version}?\n\nThis takes a while, and every rebuilt robot has to " +
                "be re-uploaded afterwards.", "Rebuild", "Cancel"))
            return;

        Debug.Log(RebuildAll(catalog, targets));
    }

    public static string RebuildAll(RobotModelCatalog catalog, List<BuildTarget> targets)
    {
        var report = new StringBuilder();
        report.AppendLine($"Rebuild All Robot Bundles — format v{RobotBundleFormat.Version}");
        report.AppendLine();

        List<RobotModelCatalog.Entry> bundled = Bundled(catalog);
        int done = 0;
        var failures = new List<string>();

        foreach (RobotModelCatalog.Entry entry in bundled)
        {
            try
            {
                // removeFromBinary is false here: these entries have already had their direct
                // reference cleared, and passing true would only re-clear it. Passing the entry's
                // OWN remote flag keeps a StreamingAssets robot in StreamingAssets.
                Build(entry, targets, entry.bundle.remote, false);
                done++;
                report.AppendLine($"  rebuilt   {entry.displayName}  -> {entry.bundle.version}");
            }
            catch (System.Exception e)
            {
                failures.Add($"{entry.displayName}: {e.Message}");
                report.AppendLine($"  FAILED    {entry.displayName}  {e.Message}");
            }
        }

        report.AppendLine();
        report.AppendLine($"{done} of {bundled.Count} rebuilt.");
        if (failures.Count > 0)
            report.AppendLine($"{failures.Count} failed — those robots are still published at their " +
                              "old versions, which this build of the app will refuse to load.");
        report.AppendLine("Re-upload " + StagingFolder + " for the changes to reach anyone.");
        return report.ToString();
    }

    private static List<RobotModelCatalog.Entry> Bundled(RobotModelCatalog catalog)
    {
        var bundled = new List<RobotModelCatalog.Entry>();
        if (catalog == null || catalog.models == null) return bundled;
        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry != null && entry.bundle != null && entry.bundle.IsSet) bundled.Add(entry);
        }
        return bundled;
    }

    // Builds `entry` for each target and points its catalog entry at the result.
    public static string Build(RobotModelCatalog.Entry entry, List<BuildTarget> targets,
        bool remote, bool removeFromBinary)
    {
        if (entry == null) throw new System.ArgumentNullException(nameof(entry));
        if (targets == null || targets.Count == 0)
            throw new System.ArgumentException("Pick at least one platform to build for.");

        GameObject source = SourcePrefab(entry);
        if (source == null)
            throw new System.InvalidOperationException(
                $"'{entry.displayName}' has no prefab to build from. Run Save As Robot Prefab first.");

        string prefabPath = AssetDatabase.GetAssetPath(source);
        string bundleId = RobotBundleFormat.Sanitize(entry.id);
        if (string.IsNullOrEmpty(bundleId))
            throw new System.InvalidOperationException($"'{entry.displayName}' has no usable id.");

        var report = new StringBuilder();
        report.AppendLine($"Build Robot Bundle — {entry.displayName}");
        report.AppendLine($"  source     {prefabPath}");
        report.AppendLine($"  format     v{RobotBundleFormat.Version}");
        report.AppendLine();

        string version = null;
        var written = new List<string>();

        foreach (BuildTarget target in targets)
        {
            if (!BuildPipeline.IsBuildTargetSupported(BuildPipeline.GetBuildTargetGroup(target), target))
            {
                report.AppendLine($"  {target,-20} SKIPPED — build support isn't installed.");
                continue;
            }

            string built = BuildOne(prefabPath, bundleId, target, out string hash, out long bytes);
            // Every platform's bundle carries the same version so one catalog entry addresses all of
            // them. The first target's content hash decides it: the platforms differ in their
            // compiled shaders, not in what the robot IS, so keying off any one of them tracks the
            // same changes.
            version ??= hash;

            string relative = RobotBundleFormat.RelativePath(bundleId, version, RobotBundleFormat.Version);
            relative = ReplacePlatform(relative, PlatformFolderFor(target));

            string destination = remote
                ? StagedRemotePath(entry, relative)
                : Path.Combine(StreamingRoot, RobotBundleFormat.StreamingFolder, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.Copy(built, destination, true);
            written.Add(destination);

            report.AppendLine($"  {target,-20} {Format(bytes),10}  {destination}");
        }

        if (written.Count == 0)
            throw new System.InvalidOperationException(
                "Nothing was built — none of the chosen platforms have build support installed.");

        entry.bundle ??= new RobotModelCatalog.BundleRef();
        entry.bundle.id = bundleId;
        entry.bundle.version = version;
        entry.bundle.scriptVersion = RobotBundleFormat.Version;
        entry.bundle.remote = remote;
        entry.bundle.sourceGuid = AssetDatabase.AssetPathToGUID(prefabPath);

        if (removeFromBinary)
        {
            entry.prefab = null;
            report.AppendLine();
            report.AppendLine("Cleared the direct prefab reference — this robot is no longer compiled " +
                              "into the app. The prefab stays in Assets/Robots/ as the thing the " +
                              "bundle is rebuilt from.");
        }
        else
        {
            report.AppendLine();
            report.AppendLine("NOTE: the direct prefab reference is still set, so the robot is STILL " +
                              "in the binary and the bundle will never be loaded — a direct reference " +
                              "always wins. That's the right setting only while testing.");
        }

        RobotModelCatalog catalog =
            AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(RoboSimPaths.RobotModelCatalog);
        if (catalog != null) EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
        if (!remote) AssetDatabase.Refresh();

        if (remote)
        {
            report.AppendLine();
            report.AppendLine(WriteIndex(catalog, entry));
            report.AppendLine();
            report.AppendLine(UploadInstructions(entry));
        }

        return report.ToString();
    }

    // Builds one bundle into a scratch folder and reports its content hash. Content-addressed on
    // purpose: the same robot built twice produces the same version, so nothing re-downloads for a
    // rebuild that changed nothing, and a robot that DID change gets a new URL automatically rather
    // than needing a cache to be invalidated by hand.
    private static string BuildOne(string prefabPath, string bundleId, BuildTarget target,
        out string hash, out long bytes)
    {
        string scratch = Path.Combine(StagingFolder, ".build", target.ToString());
        Directory.CreateDirectory(scratch);

        var build = new AssetBundleBuild
        {
            assetBundleName = bundleId + RobotBundleFormat.Extension,
            assetNames = new[] { prefabPath },
        };

        // The explicit-array overload builds exactly this one bundle. The other overload builds
        // every bundle name assigned anywhere in the project, which here would mean rebuilding the
        // whole fleet every time one robot changed.
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            scratch, new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode,
            target);

        if (manifest == null)
            throw new System.InvalidOperationException(
                $"BuildAssetBundles returned nothing for {target} — see the Console for why.");

        string path = Path.Combine(scratch, build.assetBundleName);
        if (!File.Exists(path))
            throw new System.InvalidOperationException($"No bundle was produced at {path}.");

        hash = manifest.GetAssetBundleHash(build.assetBundleName).ToString().Substring(0, 8);
        bytes = new FileInfo(path).Length;
        return path;
    }

    // The prefab a bundle is built from. Held as a GUID rather than a reference so that recording it
    // doesn't put the robot back in the build — the direct reference is exactly what we removed.
    public static GameObject SourcePrefab(RobotModelCatalog.Entry entry)
    {
        if (entry == null) return null;
        if (entry.prefab != null) return entry.prefab;

        if (entry.bundle != null && !string.IsNullOrEmpty(entry.bundle.sourceGuid))
        {
            string path = AssetDatabase.GUIDToAssetPath(entry.bundle.sourceGuid);
            if (!string.IsNullOrEmpty(path))
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null) return prefab;
            }
        }

        // Last resort: the prefab Save As Robot Prefab would have written for this display name.
        // Covers an entry whose guid was never recorded, which is every entry built before bundles
        // existed.
        return AssetDatabase.LoadAssetAtPath<GameObject>(
            SaveRobotPrefab.PrefabPathFor(entry.displayName ?? entry.id));
    }

    // The index a client reads to discover robots it was never shipped with. One per address: the
    // public one lists everything public, and a private robot's index lives in its own code-derived
    // folder, so a listing can't leak the existence of robots you don't hold a code for.
    private static string WriteIndex(RobotModelCatalog catalog, RobotModelCatalog.Entry entry)
    {
        string folder = RobotBundleAddress.Folder(entry);
        var index = new RobotCatalogIndex();

        foreach (RobotModelCatalog.Entry candidate in catalog.models)
        {
            if (candidate == null || candidate.bundle == null || !candidate.bundle.IsSet) continue;
            if (!candidate.bundle.remote) continue;
            if (RobotBundleAddress.Folder(candidate) != folder) continue;

            index.robots.Add(new RobotCatalogIndex.Robot
            {
                id = candidate.id,
                displayName = candidate.displayName,
                ownerLabel = candidate.ownerLabel,
                bundleId = candidate.bundle.id,
                bundleVersion = candidate.bundle.version,
                scriptVersion = candidate.bundle.scriptVersion,
                mechanisms = candidate.mechanisms,
            });
        }

        string path = Path.Combine(StagingFolder, RobotBundleAddress.IndexPath(folder));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, JsonUtility.ToJson(index, true));

        return $"Index for this address ({index.robots.Count} robot(s)): {path}";
    }

    private static string UploadInstructions(RobotModelCatalog.Entry entry)
    {
        return
            "To publish, copy the robots/ tree under " + StagingFolder + " into the bucket, keeping " +
            "the paths. robots/ only: .build/ beside it is Unity's scratch output (~100 MB) and must " +
            "not go up.\n" +
            $"    gcloud storage rsync --recursive {StagingFolder}/robots gs://<bucket>/robots\n\n" +
            "Uploading is NOT done from the app. /robots is read-only in storage.rules and has to " +
            "stay that way: this repo is public and the web API key with it, so anything the app is " +
            "allowed to write, anyone is allowed to write.\n" +
            (entry.visibility == RobotModelCatalog.Visibility.Private
                ? "This robot is private, so its folder name is the only thing keeping it private. " +
                  "Do not paste that path anywhere the code isn't already known."
                : "This robot is public, so anyone may download it.");
    }

    private static string PlatformFolderFor(BuildTarget target)
    {
        switch (target)
        {
            case BuildTarget.iOS: return RobotBundleFormat.PlatformFolder(RuntimePlatform.IPhonePlayer);
            case BuildTarget.Android: return RobotBundleFormat.PlatformFolder(RuntimePlatform.Android);
            case BuildTarget.StandaloneOSX: return RobotBundleFormat.PlatformFolder(RuntimePlatform.OSXPlayer);
            case BuildTarget.StandaloneWindows64:
                return RobotBundleFormat.PlatformFolder(RuntimePlatform.WindowsPlayer);
            case BuildTarget.WebGL: return RobotBundleFormat.PlatformFolder(RuntimePlatform.WebGLPlayer);
            default: return target.ToString();
        }
    }

    // RelativePath builds the path for the platform the EDITOR is running on, which is not the
    // platform being built for. Swapping the first segment keeps one definition of the layout
    // instead of a second copy that could drift from it.
    private static string ReplacePlatform(string relative, string platform)
    {
        int slash = relative.IndexOf('/');
        return slash < 0 ? relative : platform + relative.Substring(slash);
    }

    private static string Format(long bytes)
    {
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024f * 1024f):F1} MB";
        if (bytes >= 1024L) return $"{bytes / 1024f:F0} KB";
        return $"{bytes} B";
    }
}
