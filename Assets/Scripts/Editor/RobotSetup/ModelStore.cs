using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

// Where a submitted robot's source FBX lives when it is not being worked on.
//
// THE PROBLEM. A robot prefab stores POINTERS into its source FBX — `{fileID: N, guid: <fbx>,
// type: 3}`, 1,042 to 3,288 of them per robot, one per mesh and one per material slot. Delete the
// FBX and every one of them goes null: correct joints, correct masses, zero geometry. So the source
// has to be kept. But a Fusion export is ~110 MB, and it lands on this Mac three times over — the
// working tree, a Git LFS object, and a Library import artifact — which is ~350 MB per robot before
// anything is published. Ten submissions is 3.5 GB, forty is 14 GB, and none of it can be thrown
// away because the prefab needs it back to rebuild a bundle.
//
// THE FIX. Keep the bytes, move them somewhere Unity is not looking. A model that is not being
// worked on lives in a store OUTSIDE the project; the prefab keeps working because nothing about it
// changes; and Fetch puts the bytes back at the exact path with the exact .meta when a rebuild needs
// them. Only the store grows, and the store can live on a drive that is not the boot SSD.
//
// WHY SUBMITTED MODELS ARE NEVER IN GIT, and why Stow refuses anything that isn't under
// Assets/Models/Submitted/. Three things go wrong the moment a stowable model is a tracked file:
//
//   - .gitignore can only name a PATH, and a Fusion export is called `export.fbx` more often than
//     it is called anything else (RobotUploadService says so itself). Ignore one model's path and
//     the NEXT player's export at that same path is silently skipped by `git add -A` — set up,
//     given colliders, and never committed. That is how a submitted robot is lost outright.
//   - `git lfs prune` deletes the object as soon as the deletion commit stops being the tip. It is
//     also the exact command anyone reaches for when trying to reclaim disk, which is the whole
//     premise of this file.
//   - GitHub's free LFS allowance is 1 GB and this repo is already past half of it. Ten more
//     models do not fit, and a tripped quota blocks LFS for the whole repo at once — so every
//     model's "just get it back out of git history" stops working simultaneously.
//
// Under Assets/Models/Submitted/ none of that can happen: the folder is ignored as a DIRECTORY, so
// no filename can collide with a rule, nothing is ever tracked, and there is no LFS object to prune.
// The five models already in git (the four robots and the field) stay exactly where they are —
// see Docs/Model-Storage.md for why extracting those is a different and much worse problem.
//
// THE ONE FAILURE THIS FILE EXISTS TO PREVENT is a fetch that reconnects MOST of the pointers. An
// FBX sub-asset's fileID is derived from its node name and hierarchy, not stored in the .meta
// (`internalIDToNameTable: []` — verified on every model here). Identical bytes therefore reproduce
// identical fileIDs; a re-export from Fusion with two parts renamed reproduces most of them and
// silently drops the rest. The robot spawns, drives correctly, and is missing an arm. Nothing is
// null enough to log. Hence the census below, and hence the refusal to write anything into Assets/
// before the hash matches.
internal static class ModelStore
{
    // Bumped when a manifest field's MEANING changes. Fetch refuses a format it does not know
    // rather than reading a field that moved under it.
    public const int Format = 1;

    // The only place Stow will take a model from. See the header: this is what keeps every stowable
    // model out of git, and it is a DIRECTORY rule so no filename can ever collide with it.
    public const string SubmittedFolder = "Assets/Models/Submitted";

    public const string RootPrefKey = "RoboSim.ModelStoreRoot";

    // Payload filenames deliberately do NOT end in .fbx/.fbx.meta. The stored sidecar contains
    // `guid: <the live asset's guid>`, so if this folder ever lands inside Assets/ — a symlinked
    // store, a mistyped rsync destination — Unity sees two assets claiming one guid and reassigns
    // one of them. If it reassigns the live one, all ~1,500 prefab pointers die at once. A name the
    // importer will never claim makes that impossible rather than unlikely.
    public const string PayloadName = "model.fbxdata";
    public const string SidecarName = "model.meta.txt";
    public const string ManifestName = "manifest.json";

    [Serializable]
    public class Manifest
    {
        public int format;
        public string storedUtc;
        public string unityVersion;   // the one fileID input that cannot be pinned; recorded so a
                                      // mismatch is a decision rather than a surprise
        public string tool;

        public string assetPath;      // exactly where Fetch puts it back
        public string guid;           // the identity. Never the filename — "Override 1.0.fbx"
                                      // belongs to a prefab called 654V_v2.
        public long byteLength;
        public string sha256;
        public string metaSha256;
        public string metaText;       // the .fbx.meta verbatim. Carries the guid, globalScale: 10
                                      // and materialLocation — lose it and a reimport at defaults
                                      // gives a fresh guid and a 1/10-scale robot.

        public string prefabPath;
        public string prefabGuid;
        public string catalogId;
        public string displayName;

        // ---- the reconnection contract ----
        // Read from the prefab TEXT, never from a loaded prefab: a loaded prefab can be stale, and
        // a loaded prefab can be saved.
        public int pointerCount;
        public int meshPointerCount;
        public int materialPointerCount;
        public int otherPointerCount;   // expected 0, reported rather than silently dropped
        public int resolvedCount;       // must equal pointerCount at stow time or it was already broken
        public string targetFingerprint;
    }

    // ---------------------------------------------------------------------------------------------
    // The store root
    // ---------------------------------------------------------------------------------------------

    // .NET does not expand '~'. Building the default by string concatenation would create
    // "<project>/~/RoboSimModelStore" — inside the repo, matched by `*.fbx filter=lfs`, and swept
    // up by the next `git add -A`. The tool would report success while putting the model back in
    // the repository it was removing it from.
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "RoboSimModelStore");

    public static string Root
    {
        get
        {
            string stored = EditorPrefs.GetString(RootPrefKey, string.Empty);
            return string.IsNullOrWhiteSpace(stored) ? DefaultRoot : stored;
        }
        set => EditorPrefs.SetString(RootPrefKey, value ?? string.Empty);
    }

    // Folders whose contents macOS or a sync client is entitled to evict or move. A stub file in
    // one of these reports the right length from File.Exists/Length and then blocks or throws on
    // read — so the payload the store calls primary is the one that isn't there when it matters.
    private static readonly string[] SyncedFolders =
    {
        "Library/Mobile Documents", "Desktop", "Documents", "Dropbox",
        "Google Drive", "OneDrive", "Box Sync",
    };

    // Throws with a sentence naming the specific failure, rather than returning a bool nobody reads.
    public static void ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("The model store root is empty. Set it in the window.");

        // In batch mode the working directory is the invoking shell's, not the project's, so a
        // relative root resolves somewhere neither the operator nor this code can predict.
        if (!Path.IsPathRooted(root))
            throw new InvalidOperationException(
                $"The model store root must be an absolute path, got '{root}'. In batch mode a " +
                "relative path resolves against the invoking shell's working directory, not the " +
                "project, so the same command would write to a different place depending on where " +
                "it was run from.");

        string full = Path.GetFullPath(root);

        // Application.dataPath, not GetCurrentDirectory(): in a batch run the working directory is
        // whatever shell invoked Unity, so a check for "inside the project" based on it would be
        // comparing against the wrong tree entirely.
        string project = Path.GetFullPath(Directory.GetParent(Application.dataPath).FullName);

        string home = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string homePrefix = home.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        // Path.GetFullPath collapses ".." but does NOT resolve symlinks, so a store at
        // ~/RoboSimModelStore that is a symlink into the project would pass the prefix test below
        // while every payload landed inside Assets/. .NET's netstandard2.1 surface has no realpath,
        // so the link is refused rather than followed.
        //
        // ONLY under the home directory, though. macOS ships /var and /tmp as system symlinks, so
        // checking the whole chain refuses every path under Path.GetTempPath() — which is not a
        // threat, just a false alarm, and a guard that fires on correct input is how an override
        // becomes routine. A system symlink is not something this tool gets to have an opinion
        // about; one the user made in their own home is exactly the case worth catching.
        foreach (string component in AncestorsOf(full))
        {
            if (!component.StartsWith(homePrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (!Directory.Exists(component) && !File.Exists(component)) continue;
            if ((File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    $"'{component}' is a symlink, and the model store path must not contain one " +
                    "inside your home directory. A link is invisible to the check that keeps the " +
                    "store outside the project, so a linked store could put ~110 MB payloads inside " +
                    "Assets/ — where Unity would see two assets claiming one guid and kill the " +
                    "prefab's pointers.");
        }

        // APFS is case-insensitive by default, so an ordinal prefix test misses
        // /users/connor/robosim_unity/... The trailing separator matters too: without it
        // "/Users/connor/RoboSim_Unity_Store" reads as being inside "/Users/connor/RoboSim_Unity".
        string projectPrefix = project.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.Equals(project, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The model store root '{full}' is inside the project. That defeats the point — " +
                "Unity would import the payloads, git would see them, and a root under " +
                "Build/RobotBundles/ would be rsynced into the public bucket.");

        foreach (string synced in SyncedFolders)
        {
            string candidate = Path.GetFullPath(Path.Combine(home, synced))
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)) continue;

            throw new InvalidOperationException(
                $"The model store root '{full}' is inside '{synced}', which is synced or " +
                "cloud-managed. With storage optimisation on, the payload is evicted to a stub: it " +
                "still reports the right size, and then the read that verifies it blocks or throws. " +
                "Put the store on a plain local folder or an external drive, and sync it yourself.");
        }
    }

    private static IEnumerable<string> AncestorsOf(string full)
    {
        for (string p = full; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            yield return p;
            if (Path.GetPathRoot(p) == p) break;
        }
    }

    public static string FolderFor(string root, string guid) => Path.Combine(root, guid);

    // ---------------------------------------------------------------------------------------------
    // Hashing
    // ---------------------------------------------------------------------------------------------

    public static string Sha256OfFile(string path)
    {
        using (var sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
            return ToHex(sha.ComputeHash(stream));
    }

    private static string ToHex(byte[] hash)
    {
        var sb = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // Re-opens the payload and hashes it FROM DISK. Hashing the buffer that was written verifies the
    // buffer, not the file — and every way this loses a model routes through the difference: a full
    // disk that silently truncated, an ejected drive, a sync client that evicted the contents after
    // the write returned. Called at three points in Stow, including immediately before the delete.
    public static void VerifyPayload(string root, Manifest manifest) =>
        VerifyPayloadIn(FolderFor(root, manifest.guid), manifest);

    public static void VerifyPayloadIn(string folder, Manifest manifest)
    {
        string payload = Path.Combine(folder, PayloadName);

        if (!File.Exists(payload))
            throw new InvalidOperationException($"The stored payload is missing: {payload}");

        var info = new FileInfo(payload);
        if (info.Length != manifest.byteLength)
            throw new InvalidOperationException(
                $"The stored payload is {info.Length} bytes, but the manifest says " +
                $"{manifest.byteLength}. Do not delete the copy in Assets/.");

        string actual = Sha256OfFile(payload);
        if (actual != manifest.sha256)
            throw new InvalidOperationException(
                $"The stored payload's SHA-256 is {actual}, but the manifest says {manifest.sha256}. " +
                "The stored copy is damaged. Do not delete the copy in Assets/.");

        string sidecar = Path.Combine(folder, SidecarName);
        if (!File.Exists(sidecar))
            throw new InvalidOperationException($"The stored .meta sidecar is missing: {sidecar}");

        string metaActual = Sha256OfFile(sidecar);
        if (metaActual != manifest.metaSha256)
            throw new InvalidOperationException(
                $"The stored .meta sidecar's SHA-256 is {metaActual}, but the manifest says " +
                $"{manifest.metaSha256}. Without the exact .meta the model reimports with a fresh " +
                "guid at the wrong scale, and every prefab pointer dies.");
    }

    // ---------------------------------------------------------------------------------------------
    // The census — what the prefab points at, and whether it is still there
    // ---------------------------------------------------------------------------------------------

    public class Census
    {
        public int Pointers;
        public int MeshPointers;
        public int MaterialPointers;
        public int OtherPointers;
        public int Resolved;
        public string Fingerprint;
        public readonly List<string> Lines = new List<string>();
    }

    private static readonly Regex PointerPattern = new Regex(
        @"\{fileID:\s*(-?\d+),\s*guid:\s*([0-9a-fA-F]{32}),\s*type:\s*(\d+)\}",
        RegexOptions.Compiled);

    // Counts what `prefabPath` points at inside `fbxPath`, and fingerprints what those pointers
    // actually LAND on.
    //
    // The counts alone are not enough and the fingerprint is why. Counts see a pointer that fails to
    // resolve; they cannot see a pointer that resolves to the WRONG THING — a reimport under a newer
    // Unity, or with a nudged importer setting, can hand back the same fileIDs with the same names
    // and different geometry. So the fingerprint includes vertex count, sub-mesh count, index counts
    // and bounds for meshes, and the shader for materials. All of those are readable with
    // `isReadable: 0`, unlike mesh.vertices, so this costs nothing.
    public static Census Take(string prefabPath, string fbxPath, string fbxGuid)
    {
        var census = new Census();
        string text = File.ReadAllText(prefabPath);

        Dictionary<long, UnityEngine.Object> byFileId = SubAssetsOf(fbxPath);

        foreach (Match match in PointerPattern.Matches(text))
        {
            if (!string.Equals(match.Groups[2].Value, fbxGuid, StringComparison.OrdinalIgnoreCase))
                continue;

            census.Pointers++;
            if (!long.TryParse(match.Groups[1].Value, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out long fileId))
                continue;

            if (!byFileId.TryGetValue(fileId, out UnityEngine.Object target) || target == null)
                continue;

            census.Resolved++;
            switch (target)
            {
                case Mesh mesh:
                    census.MeshPointers++;
                    census.Lines.Add(MeshLine(fileId, mesh));
                    break;
                case Material material:
                    census.MaterialPointers++;
                    census.Lines.Add(MaterialLine(fileId, material));
                    break;
                default:
                    census.OtherPointers++;
                    census.Lines.Add($"{fileId}|{target.GetType().Name}|{target.name}");
                    break;
            }
        }

        // Ordinal, NOT the default comparer. These lines begin with signed longs, and hyphen
        // collation is the most famous ICU-vs-invariant divergence there is: the default sort
        // orders "-100|..." and "-1|..." differently depending on the ICU version Unity shipped
        // with. That would move the fingerprint for a model that is byte-perfect, and a false alarm
        // phrased as a catastrophe is exactly how the override flag becomes routine.
        census.Lines.Sort(StringComparer.Ordinal);
        census.Fingerprint = Fnv1a(string.Join("\n", census.Lines));
        return census;
    }

    private static string MeshLine(long fileId, Mesh mesh)
    {
        var sb = new StringBuilder();
        sb.Append(fileId).Append("|Mesh|").Append(mesh.name)
          .Append('|').Append(mesh.vertexCount.ToString(CultureInfo.InvariantCulture))
          .Append('|').Append(mesh.subMeshCount.ToString(CultureInfo.InvariantCulture));

        // SubMeshDescriptor, not the index buffer — this reads with Read/Write off.
        for (int i = 0; i < mesh.subMeshCount; i++)
            sb.Append(',').Append(mesh.GetSubMesh(i).indexCount.ToString(CultureInfo.InvariantCulture));

        Bounds b = mesh.bounds;
        sb.Append('|').Append(Vec(b.center)).Append('|').Append(Vec(b.extents));
        return sb.ToString();
    }

    // A material slot is half of every robot's pointers, and a material that comes back with a
    // different shader renders the robot magenta or invisible while every count stays green.
    private static string MaterialLine(long fileId, Material material)
    {
        string shader = material.shader != null ? material.shader.name : "<none>";
        return $"{fileId}|Material|{material.name}|{shader}";
    }

    private static string Vec(Vector3 v) =>
        string.Format(CultureInfo.InvariantCulture, "{0:F4},{1:F4},{2:F4}", v.x, v.y, v.z);

    private static Dictionary<long, UnityEngine.Object> SubAssetsOf(string fbxPath)
    {
        var map = new Dictionary<long, UnityEngine.Object>();
        if (!File.Exists(fbxPath)) return map;

        foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
        {
            if (asset == null) continue;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long id))
                map[id] = asset;
        }
        return map;
    }

    // FNV-1a, for the reason RobotBundleValidation gives about its own fingerprint:
    // string.GetHashCode is randomized per process, so a value derived from it means nothing
    // across runs and cannot be pinned.
    public static string Fnv1a(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash.ToString("x8", CultureInfo.InvariantCulture);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Finding the one prefab that owns a model
    // ---------------------------------------------------------------------------------------------

    // Every robot FBX in this project is referenced by exactly one prefab and nothing else — that is
    // what makes stowing a bounded operation. The field FBX is the counterexample and the reason
    // this returns a list rather than a single path: it is referenced by SampleScene 2,854 times, by
    // LiteScene, and by four match-load prefabs, so it can never be stowed at all.
    public static List<string> ReferrersOf(string fbxGuid)
    {
        var referrers = new List<string>();
        foreach (string path in Directory.GetFiles("Assets", "*.prefab", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(path).IndexOf(fbxGuid, StringComparison.OrdinalIgnoreCase) >= 0)
                referrers.Add(path.Replace('\\', '/'));
        }
        foreach (string path in Directory.GetFiles("Assets", "*.unity", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(path).IndexOf(fbxGuid, StringComparison.OrdinalIgnoreCase) >= 0)
                referrers.Add(path.Replace('\\', '/'));
        }
        return referrers;
    }
}
