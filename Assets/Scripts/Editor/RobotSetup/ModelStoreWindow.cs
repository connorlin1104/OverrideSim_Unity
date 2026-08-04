using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Stow a submitted model's source FBX out of the project, and fetch it back when a bundle has to be
// rebuilt. See ModelStore for why this exists and why it refuses anything that isn't under
// Assets/Models/Submitted/.
//
// THE ORDER OF OPERATIONS IS THE SAFETY. Every way this loses a model is a sequence where the copy
// in Assets/ is destroyed before a verified copy exists somewhere else, so:
//
//   write to staging -> re-hash it FROM DISK -> rename into place -> re-hash it FROM DISK AGAIN ->
//   only then delete from Assets/
//
// The second re-hash is not paranoia about the first. Between them sits a directory rename and an
// unbounded amount of wall-clock time in which a drive can be ejected, a network share can drop, or
// a disk can fill. Deleting a 110 MB model on the strength of a check made before all that is how
// the store ends up holding a manifest and no bytes.
//
// Fetch is the mirror image: hash the payload BEFORE anything is written into Assets/, then — after
// the import — count what actually reconnected and refuse if it disagrees with the manifest. A fetch
// that reconnects 1,500 of 1,534 pointers produces a robot that spawns, drives, and is missing an
// arm, with nothing in the console.
//
// Usage: Tools > RoboSim > Robot > Model Store
// Batch: -executeMethod ModelStoreWindow.RunBatchStow  -model <asset path> [-storeRoot <abs path>]
//                                                      [-allowUnpublished]
//        -executeMethod ModelStoreWindow.RunBatchFetch -guid <guid> [-storeRoot <abs path>]
public class ModelStoreWindow : EditorWindow
{
    private Vector2 scroll;
    private string root;
    private string status = string.Empty;

    [MenuItem("Tools/RoboSim/Robot/Model Store", false, 22)]
    private static void Open()
    {
        GetWindow<ModelStoreWindow>(true, "Model Store", true).minSize = new Vector2(560f, 420f);
    }

    private void OnEnable() => root = ModelStore.Root;

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.LabelField("Where stowed models live", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "An absolute path outside this project. This is the ONLY copy of a stowed model, so put " +
            "it somewhere you back up — a cloud-synced folder is refused, because storage " +
            "optimisation evicts the contents and leaves a stub that reads the right size and then " +
            "fails to open.", MessageType.None);

        EditorGUILayout.BeginHorizontal();
        root = EditorGUILayout.TextField("Store root", root);
        if (GUILayout.Button("Browse", GUILayout.Width(70f)))
        {
            string picked = EditorUtility.OpenFolderPanel("Model store root", root, string.Empty);
            if (!string.IsNullOrEmpty(picked)) root = picked;
        }
        EditorGUILayout.EndHorizontal();

        if (GUILayout.Button("Use this root"))
        {
            try
            {
                ModelStore.ValidateRoot(root);
                ModelStore.Root = root;
                status = "Store root set to " + root;
            }
            catch (Exception e) { status = "REFUSED\n\n" + e.Message; }
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("In the project, stowable", EditorStyles.boldLabel);

        List<string> stowable = StowableModels();
        if (stowable.Count == 0)
        {
            EditorGUILayout.HelpBox(
                $"Nothing in {ModelStore.SubmittedFolder}/. Submitted models go there — it is " +
                "gitignored as a whole directory, which is what keeps them out of Git LFS.",
                MessageType.Info);
        }
        foreach (string path in stowable)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Path.GetFileName(path), GUILayout.MinWidth(220f));
            EditorGUILayout.LabelField(Megabytes(new FileInfo(path).Length), GUILayout.Width(80f));
            if (GUILayout.Button("Stow", GUILayout.Width(70f))) Run(() => Stow(path, root, false));
            EditorGUILayout.EndHorizontal();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Stowed", EditorStyles.boldLabel);
        foreach (ModelStore.Manifest manifest in StowedModels(root))
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(Path.GetFileName(manifest.assetPath), GUILayout.MinWidth(220f));
            EditorGUILayout.LabelField(Megabytes(manifest.byteLength), GUILayout.Width(80f));
            if (GUILayout.Button("Fetch", GUILayout.Width(70f))) Run(() => Fetch(manifest.guid, root));
            EditorGUILayout.EndHorizontal();
        }

        if (!string.IsNullOrEmpty(status))
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(status,
                status.StartsWith("REFUSED") || status.StartsWith("FAILED")
                    ? MessageType.Error : MessageType.Info);
        }

        EditorGUILayout.EndScrollView();
    }

    private void Run(Func<string> operation)
    {
        try { status = operation(); }
        catch (Exception e)
        {
            status = "FAILED\n\n" + e.Message;
            Debug.LogException(e);
        }
        Repaint();
    }

    private static string Megabytes(long bytes) => $"{bytes / 1024f / 1024f:F1} MB";

    // ---------------------------------------------------------------------------------------------
    // Stow
    // ---------------------------------------------------------------------------------------------

    public static string Stow(string assetPath, string root, bool allowUnpublished)
    {
        ModelStore.ValidateRoot(root);

        // An externally changed file — a `git checkout`, a re-drop of a corrected export — is not
        // seen by SaveAssets, only by Refresh. Without this the census reads the stale Library
        // artifact (old ids, old fingerprint) while the hash reads the new bytes, and the manifest
        // pairs the two. The mismatch would surface months later, at a fetch, unrecoverably.
        AssetDatabase.Refresh();

        assetPath = assetPath.Replace('\\', '/');
        if (!File.Exists(assetPath))
            throw new InvalidOperationException($"There is no file at '{assetPath}'.");

        string submittedPrefix = ModelStore.SubmittedFolder + "/";
        if (!assetPath.StartsWith(submittedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"'{assetPath}' is not under {ModelStore.SubmittedFolder}/, so it is a tracked file " +
                "and stowing it would mean fighting git for it. Only submitted models are stowable " +
                "— the four robots and the field are in git and stay there. See Docs/Model-Storage.md.");

        string guid = AssetDatabase.AssetPathToGUID(assetPath);
        if (string.IsNullOrEmpty(guid))
            throw new InvalidOperationException($"Unity has no guid for '{assetPath}' — is it imported?");

        List<string> referrers = ModelStore.ReferrersOf(guid);
        if (referrers.Count == 0)
            throw new InvalidOperationException(
                $"Nothing references '{assetPath}'. Stowing it would be safe but pointless — if it " +
                "really is unused, delete it instead so there is no manifest claiming otherwise.");
        if (referrers.Count > 1)
            throw new InvalidOperationException(
                $"'{assetPath}' is referenced by {referrers.Count} files:\n  " +
                string.Join("\n  ", referrers) +
                "\n\nThis tool only handles a model owned by exactly one prefab. A model referenced " +
                "by a scene is needed at every app build and can never be stowed.");

        string prefabPath = referrers[0];
        if (!prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"'{assetPath}' is referenced by '{prefabPath}', which is a scene, not a prefab. A " +
                "scene dependency is pulled into every app build, so the model has to stay.");

        RobotModelCatalog.Entry entry = EntryFor(prefabPath);
        if (entry != null && entry.prefab != null && !allowUnpublished)
            throw new InvalidOperationException(
                $"'{entry.displayName}' still has a direct prefab reference in the catalog, which is " +
                "what pulls a robot into the app build — so the FBX is needed every time the app is " +
                "built and stowing it would break that build. Publish it as a bundle first " +
                "(Build Robot Bundle, with Remove From Binary on).");

        ModelStore.Census census = ModelStore.Take(prefabPath, assetPath, guid);
        if (census.Pointers == 0)
            throw new InvalidOperationException(
                $"'{prefabPath}' contains no pointers into this model, which means the census is " +
                "measuring nothing and would 'pass' after a fetch that restored an empty file.");
        if (census.Resolved != census.Pointers)
            throw new InvalidOperationException(
                $"'{prefabPath}' has {census.Pointers} pointers into this model but only " +
                $"{census.Resolved} of them resolve. The robot is ALREADY broken, and stowing it " +
                "now would bake that into the manifest as the expected state. Fix it first.");

        string metaPath = assetPath + ".meta";
        if (!File.Exists(metaPath))
            throw new InvalidOperationException(
                $"'{metaPath}' is missing. Without the exact .meta a fetch reimports at defaults " +
                "with a fresh guid, and every prefab pointer dies.");

        var manifest = new ModelStore.Manifest
        {
            format = ModelStore.Format,
            storedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            unityVersion = Application.unityVersion,
            tool = "ModelStore/" + ModelStore.Format,
            assetPath = assetPath,
            guid = guid,
            byteLength = new FileInfo(assetPath).Length,
            sha256 = ModelStore.Sha256OfFile(assetPath),
            metaSha256 = ModelStore.Sha256OfFile(metaPath),
            metaText = File.ReadAllText(metaPath),
            prefabPath = prefabPath,
            prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath),
            catalogId = entry != null ? entry.id : string.Empty,
            displayName = entry != null ? entry.displayName : Path.GetFileNameWithoutExtension(assetPath),
            pointerCount = census.Pointers,
            meshPointerCount = census.MeshPointers,
            materialPointerCount = census.MaterialPointers,
            otherPointerCount = census.OtherPointers,
            resolvedCount = census.Resolved,
            targetFingerprint = census.Fingerprint,
        };

        string destination = ModelStore.FolderFor(root, guid);
        if (Directory.Exists(destination)) VerifyResumable(destination, manifest);

        // Staging is a name no fetch will ever look for, so a run killed at any point before the
        // rename leaves debris rather than a folder that looks like a valid store entry.
        string staging = Path.Combine(root, ".staging",
            guid + "." + DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            Directory.CreateDirectory(staging);
            File.Copy(assetPath, Path.Combine(staging, ModelStore.PayloadName), true);
            File.Copy(metaPath, Path.Combine(staging, ModelStore.SidecarName), true);
            File.WriteAllText(Path.Combine(staging, ModelStore.ManifestName),
                JsonUtility.ToJson(manifest, true));

            ModelStore.VerifyPayloadIn(staging, manifest);

            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            Directory.Move(staging, destination);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }

        // The second verification, after the move. Everything above proved the bytes were written
        // correctly; this proves they are still there, at the final address, right now.
        ModelStore.VerifyPayload(root, manifest);

        if (!AssetDatabase.DeleteAsset(assetPath))
            throw new InvalidOperationException(
                $"The model was stored and verified at '{destination}', but Unity refused to delete " +
                $"'{assetPath}'. Nothing is lost — delete it by hand once you have checked the store.");

        AssetDatabase.Refresh();

        var report = new StringBuilder();
        report.AppendLine($"Stowed {manifest.displayName} — {Megabytes(manifest.byteLength)} out of the project.");
        report.AppendLine();
        report.AppendLine($"  from     {assetPath}");
        report.AppendLine($"  to       {destination}");
        report.AppendLine($"  sha256   {manifest.sha256}");
        report.AppendLine($"  pointers {manifest.pointerCount} " +
                          $"({manifest.meshPointerCount} mesh, {manifest.materialPointerCount} material)");
        report.AppendLine($"  finger   {manifest.targetFingerprint}");
        report.AppendLine();
        report.AppendLine("THIS IS NOW THE ONLY COPY. It was never in git — that is deliberate — so " +
                          "nothing else on this machine or on GitHub has these bytes. Copy the store " +
                          "somewhere else before you need it:");
        report.AppendLine($"  rsync -a --delete \"{root}/\" <backup or bucket>/");
        return report.ToString();
    }

    // A store folder that already exists is either a crashed run to resume or a different model at
    // the same address, and the difference decides whether deleting the copy in Assets/ is safe. The
    // check re-hashes the payload FROM DISK rather than trusting the numbers in its manifest.json:
    // a manifest is 8 KB and survives almost everything, while the 110 MB payload beside it is what
    // a full disk truncates and a sync client evicts.
    private static void VerifyResumable(string destination, ModelStore.Manifest fresh)
    {
        string manifestPath = Path.Combine(destination, ModelStore.ManifestName);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException(
                $"'{destination}' already exists but has no {ModelStore.ManifestName}. That is " +
                "debris from an interrupted run, and this tool will not guess. Move it aside.");

        var existing = JsonUtility.FromJson<ModelStore.Manifest>(File.ReadAllText(manifestPath));
        if (existing == null || existing.sha256 != fresh.sha256 || existing.byteLength != fresh.byteLength)
            throw new InvalidOperationException(
                $"'{destination}' already holds a DIFFERENT version of this model " +
                $"({existing?.sha256} vs {fresh.sha256}). Overwriting it would destroy the only copy " +
                "of the stored one. Move it aside and decide which you want.");

        // The bytes match, but the importer settings might not — and they are invisible to every
        // other check here because they change geometry, not identity. A model fetched, given
        // Read/Write for a mesh pass, and stowed again has the same FBX and a different .meta;
        // resuming onto the old payload would silently discard the new settings and hand back a
        // 1/10-scale robot at the next fetch.
        if (existing.metaSha256 != fresh.metaSha256)
            throw new InvalidOperationException(
                $"'{destination}' holds the same FBX bytes but a different .meta. The importer " +
                "settings changed since it was stored, and keeping the old ones would silently " +
                "reimport at the wrong scale or the wrong material mode. Move it aside.");

        ModelStore.VerifyPayloadIn(destination, existing);
    }

    // ---------------------------------------------------------------------------------------------
    // Fetch
    // ---------------------------------------------------------------------------------------------

    public static string Fetch(string guid, string root)
    {
        ModelStore.ValidateRoot(root);

        string folder = ModelStore.FolderFor(root, guid);
        string manifestPath = Path.Combine(folder, ModelStore.ManifestName);
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"Nothing is stored for '{guid}' at '{folder}'.");

        var manifest = JsonUtility.FromJson<ModelStore.Manifest>(File.ReadAllText(manifestPath));
        if (manifest == null)
            throw new InvalidOperationException($"'{manifestPath}' could not be read.");
        if (manifest.format != ModelStore.Format)
            throw new InvalidOperationException(
                $"'{manifestPath}' is format {manifest.format}, and this tool understands " +
                $"{ModelStore.Format}. Reading it anyway would mean reading a field that moved.");

        // BEFORE anything is written into Assets/. A truncated or evicted payload imported as an FBX
        // "succeeds" as an empty model with the correct guid still attached, so every pointer
        // resolves to null and the robot spawns invisible.
        ModelStore.VerifyPayload(root, manifest);

        if (File.Exists(manifest.assetPath))
            throw new InvalidOperationException(
                $"'{manifest.assetPath}' already exists. Fetching would overwrite it, and this tool " +
                "will not decide which copy you meant. Move it aside first.");

        if (manifest.unityVersion != Application.unityVersion)
            Debug.LogWarning(
                $"'{manifest.displayName}' was stowed under Unity {manifest.unityVersion} and this is " +
                $"{Application.unityVersion}. Sub-asset ids come from the importer, so read the " +
                "reconnection numbers below rather than assuming — that is what they are for.");

        Directory.CreateDirectory(Path.GetDirectoryName(manifest.assetPath));

        // Both files land while the asset database is held, so Unity never sees the FBX without its
        // .meta. It it did, it would mint a fresh random guid, and all ~1,500 prefab pointers would
        // be pointing at a model that no longer answers to that name.
        AssetDatabase.StartAssetEditing();
        try
        {
            File.WriteAllText(manifest.assetPath + ".meta", manifest.metaText);
            File.Copy(Path.Combine(folder, ModelStore.PayloadName), manifest.assetPath, false);
        }
        finally { AssetDatabase.StopAssetEditing(); }

        AssetDatabase.ImportAsset(manifest.assetPath,
            ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

        string landedGuid = AssetDatabase.AssetPathToGUID(manifest.assetPath);
        if (landedGuid != manifest.guid)
            throw new InvalidOperationException(
                $"'{manifest.assetPath}' imported with guid {landedGuid}, but the prefab points at " +
                $"{manifest.guid}. The .meta did not take. Delete the file and its .meta and fetch again.");

        ModelStore.Census census = ModelStore.Take(manifest.prefabPath, manifest.assetPath, manifest.guid);

        var problems = new List<string>();
        if (census.Pointers != manifest.pointerCount)
            problems.Add($"the prefab now has {census.Pointers} pointers, not {manifest.pointerCount} " +
                         "— the prefab changed while the model was away");
        if (census.Resolved != manifest.resolvedCount)
            problems.Add($"only {census.Resolved} of {census.Pointers} pointers resolve, and " +
                         $"{manifest.resolvedCount} did when it was stowed — this is a partial " +
                         "reconnection, which is the failure that leaves a robot missing parts");
        if (census.MeshPointers != manifest.meshPointerCount)
            problems.Add($"{census.MeshPointers} mesh pointers, expected {manifest.meshPointerCount}");
        if (census.MaterialPointers != manifest.materialPointerCount)
            problems.Add($"{census.MaterialPointers} material pointers, expected {manifest.materialPointerCount}");
        if (census.Fingerprint != manifest.targetFingerprint)
            problems.Add($"the fingerprint is {census.Fingerprint}, expected {manifest.targetFingerprint} " +
                         "— the pointers resolve, but to objects with different geometry, different " +
                         "sub-mesh layout, or a different shader than the ones stowed");

        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"'{manifest.displayName}' was fetched to '{manifest.assetPath}' and the file is " +
                "correct — its SHA-256 matched before it was written — but it did not reconnect the " +
                "way it was stowed:\n  - " + string.Join("\n  - ", problems) +
                "\n\nThe file has been LEFT IN PLACE so you can look at it. Do not publish this robot " +
                "until the numbers agree.");

        var report = new StringBuilder();
        report.AppendLine($"Fetched {manifest.displayName} — {Megabytes(manifest.byteLength)} back into the project.");
        report.AppendLine();
        report.AppendLine($"  to       {manifest.assetPath}");
        report.AppendLine($"  guid     {landedGuid}");
        report.AppendLine($"  pointers {census.Resolved}/{census.Pointers} resolved " +
                          $"({census.MeshPointers} mesh, {census.MaterialPointers} material)");
        report.AppendLine($"  finger   {census.Fingerprint} (matches)");
        report.AppendLine();
        report.AppendLine("Stow it again when the rebuild is done, or it goes back to costing " +
                          $"{Megabytes(manifest.byteLength)} in the project and the same again in Library/.");
        return report.ToString();
    }

    // ---------------------------------------------------------------------------------------------

    private static List<string> StowableModels()
    {
        var found = new List<string>();
        if (!Directory.Exists(ModelStore.SubmittedFolder)) return found;

        foreach (string path in Directory.GetFiles(ModelStore.SubmittedFolder, "*.fbx",
                     SearchOption.AllDirectories))
            found.Add(path.Replace('\\', '/'));
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static List<ModelStore.Manifest> StowedModels(string root)
    {
        var found = new List<ModelStore.Manifest>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return found;

        foreach (string folder in Directory.GetDirectories(root))
        {
            string manifestPath = Path.Combine(folder, ModelStore.ManifestName);
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var manifest = JsonUtility.FromJson<ModelStore.Manifest>(File.ReadAllText(manifestPath));
                if (manifest != null && !string.IsNullOrEmpty(manifest.guid)) found.Add(manifest);
            }
            catch (Exception) { /* a folder that isn't ours; the window just doesn't list it */ }
        }
        return found;
    }

    private static RobotModelCatalog.Entry EntryFor(string prefabPath)
    {
        var catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(RoboSimPaths.RobotModelCatalog);
        if (catalog == null) return null;

        string prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
        foreach (RobotModelCatalog.Entry entry in catalog.models)
        {
            if (entry == null) continue;
            if (entry.prefab != null && AssetDatabase.GetAssetPath(entry.prefab) == prefabPath) return entry;
            if (entry.bundle != null && entry.bundle.sourceGuid == prefabGuid) return entry;
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------
    // Batch
    // ---------------------------------------------------------------------------------------------

    public static void RunBatchStow()
    {
        try
        {
            string model = Arg("-model");
            if (string.IsNullOrEmpty(model))
                throw new InvalidOperationException("-model <asset path> is required.");

            string storeRoot = Arg("-storeRoot");
            if (string.IsNullOrEmpty(storeRoot)) storeRoot = ModelStore.Root;

            Debug.Log(Stow(model, storeRoot, HasFlag("-allowUnpublished")));
        }
        catch (Exception e)
        {
            Debug.LogError("Stow FAILED: " + e.Message);
            EditorApplication.Exit(1);
            return;
        }
        EditorApplication.Exit(0);
    }

    public static void RunBatchFetch()
    {
        try
        {
            string guid = Arg("-guid");
            if (string.IsNullOrEmpty(guid))
                throw new InvalidOperationException("-guid <guid> is required.");

            string storeRoot = Arg("-storeRoot");
            if (string.IsNullOrEmpty(storeRoot)) storeRoot = ModelStore.Root;

            Debug.Log(Fetch(guid, storeRoot));
        }
        catch (Exception e)
        {
            Debug.LogError("Fetch FAILED: " + e.Message);
            EditorApplication.Exit(1);
            return;
        }
        EditorApplication.Exit(0);
    }

    private static string Arg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }

    private static bool HasFlag(string name) => Array.IndexOf(Environment.GetCommandLineArgs(), name) >= 0;
}
