using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

// Turns a catalog entry into a robot prefab, wherever that robot happens to live.
//
// Three routes, tried in this order:
//   1. A direct prefab reference — the robot was compiled into the app. Instant, no I/O, and what
//      every robot shipped with the game uses.
//   2. An AssetBundle in StreamingAssets — shipped inside the app, but loaded on demand rather than
//      held in memory from launch.
//   3. An AssetBundle fetched from Storage — the robot was never in the app at all. This is the one
//      that makes a submitted robot arrive without an app update, and it is also what makes a
//      private robot genuinely private: the geometry never reaches a device that hasn't asked for
//      it with a code that computes the right path.
//
// WHY A CACHE IS NOT OPTIONAL: AssetBundle.LoadFromFile on a bundle that is already loaded does not
// return the existing one, it throws. Anything that spawns the same robot twice — reset the field,
// go back to the home screen and forward again — would fail on the second attempt with an error
// about the bundle already being loaded, which reads as nothing to do with spawning. So loaded
// bundles are held for the life of the process and handed back on the next request.
//
// NOTHING HERE UNLOADS. A robot bundle is tens of megabytes and the app only ever has a few of them,
// so the memory is bounded by the number of robots a player owns rather than by how long they play.
// Unloading between spawns would trade that for re-reading the whole bundle every time the field
// reloads, on a device where that read is the slowest thing in the app.
public static class RobotBundleService
{
    // Storage's REST download endpoint. Same shape RobotInboxService uses: the object path is one
    // flat percent-encoded string, so every '/' in it has to arrive as %2F or the request addresses
    // a folder that does not exist.
    private const string DownloadUrl = "https://firebasestorage.googleapis.com/v0/b/{0}/o/{1}?alt=media";

    private static readonly Dictionary<string, AssetBundle> loaded = new Dictionary<string, AssetBundle>();

    // Calls back with the robot prefab, or null and a sentence to show. The sentence is written for a
    // player, not a log: every failure here is something they might be able to act on (update the
    // app, get back on wifi) or at minimum something they deserve to be told about rather than
    // watching an empty field.
    public static IEnumerator Resolve(RobotModelCatalog.Entry entry, RobotUploadConfig config,
        Action<GameObject, string> onDone)
    {
        if (entry == null)
        {
            onDone?.Invoke(null, "No robot selected.");
            yield break;
        }

        // 1. Compiled in.
        if (entry.prefab != null)
        {
            onDone?.Invoke(entry.prefab, string.Empty);
            yield break;
        }

        RobotModelCatalog.BundleRef bundle = entry.bundle;
        if (bundle == null || !bundle.IsSet)
        {
            onDone?.Invoke(null, $"'{entry.displayName}' isn't built yet.");
            yield break;
        }

        // Checked BEFORE any I/O. A version-mismatched bundle would usually load perfectly well and
        // hand back a robot whose serialized fields had silently reverted to their defaults — see
        // RobotBundleFormat. Refusing early also means the player is told to update rather than
        // waiting through a download for a robot that was never going to work.
        if (!RobotBundleFormat.CanLoad(bundle))
        {
            onDone?.Invoke(null, RobotBundleFormat.ExplainMismatch(bundle));
            yield break;
        }

        string relative = RobotBundleFormat.RelativePath(bundle);

        if (loaded.TryGetValue(relative, out AssetBundle cached) && cached != null)
        {
            yield return Extract(cached, entry, onDone);
            yield break;
        }

        // 2. Shipped in StreamingAssets.
        string localPath = Path.Combine(Application.streamingAssetsPath,
            RobotBundleFormat.StreamingFolder, relative);

        // On Android StreamingAssets lives inside the APK and is not a real file, so File.Exists is
        // always false there — but LoadFromFile understands the compressed path. So on Android ask
        // the loader and let it answer; everywhere else File.Exists is both correct and quiet
        // (LoadFromFileAsync logs an error of its own for a path that isn't there, which on a
        // remote-only robot would be an error line at every spawn).
        if (IsInsideArchive(localPath) || File.Exists(localPath))
        {
            AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(localPath);
            yield return request;

            if (request.assetBundle != null)
            {
                loaded[relative] = request.assetBundle;
                yield return Extract(request.assetBundle, entry, onDone);
                yield break;
            }
        }

        // 3. Fetched from Storage.
        if (!bundle.remote)
        {
            onDone?.Invoke(null, $"'{entry.displayName}' is missing from this build.");
            yield break;
        }

        yield return Download(entry, bundle, relative, config, onDone);
    }

    private static IEnumerator Download(RobotModelCatalog.Entry entry,
        RobotModelCatalog.BundleRef bundle, string relative, RobotUploadConfig config,
        Action<GameObject, string> onDone)
    {
        if (config == null || !config.IsConfigured)
        {
            onDone?.Invoke(null, $"'{entry.displayName}' has to be downloaded, and downloading " +
                                 "isn't switched on in this build.");
            yield break;
        }
        if (Application.internetReachability == NetworkReachability.NotReachable)
        {
            onDone?.Invoke(null, $"'{entry.displayName}' hasn't been downloaded yet, and there's no " +
                                 "internet connection.");
            yield break;
        }

        // The folder a private robot lives in is derived from its owner code, so only a device that
        // holds the code can work out where to ask. See RobotBundleAddress.
        string objectPath = RobotBundleAddress.RemotePath(entry, relative);
        string url = string.Format(DownloadUrl, config.storageBucket, UnityWebRequest.EscapeURL(objectPath));

        // Unity's own bundle cache keys on the version, and the version is already in the path, so a
        // given (robot, build) pair is downloaded exactly once per device and read from disk every
        // time after. Passing a version of 1 alongside a versioned URL is deliberate: the URL is
        // what changes when the content changes, so the cache never has to be invalidated by hand.
        using (UnityWebRequest request = UnityWebRequestAssetBundle.GetAssetBundle(url, 1))
        {
            yield return request.SendWebRequest();

            if (request.responseCode == 404)
            {
                onDone?.Invoke(null, $"'{entry.displayName}' hasn't been published for this version " +
                                     "of the app yet.");
                yield break;
            }
            if (request.responseCode == 403)
            {
                // The path is derived from the code, so a 403/404 here usually means the code this
                // device holds isn't the one that robot was published under.
                onDone?.Invoke(null, $"'{entry.displayName}' couldn't be downloaded — the code this " +
                                     "device has doesn't open it.");
                yield break;
            }
            if (request.result != UnityWebRequest.Result.Success)
            {
                onDone?.Invoke(null, $"Couldn't download '{entry.displayName}': {request.error}");
                yield break;
            }

            AssetBundle downloaded = DownloadHandlerAssetBundle.GetContent(request);
            if (downloaded == null)
            {
                onDone?.Invoke(null, $"'{entry.displayName}' downloaded but couldn't be opened.");
                yield break;
            }

            loaded[relative] = downloaded;
            yield return Extract(downloaded, entry, onDone);
        }
    }

    // Pulls the robot out of an opened bundle. A robot bundle holds exactly one robot; the search is
    // by component rather than by name so that renaming a prefab doesn't break the entry pointing at
    // it, and so a bundle that accidentally picked up a second GameObject still resolves.
    private static IEnumerator Extract(AssetBundle bundle, RobotModelCatalog.Entry entry,
        Action<GameObject, string> onDone)
    {
        AssetBundleRequest request = bundle.LoadAllAssetsAsync<GameObject>();
        yield return request;

        GameObject fallback = null;
        foreach (UnityEngine.Object asset in request.allAssets)
        {
            if (asset is not GameObject candidate) continue;
            fallback ??= candidate;
            if (candidate.GetComponent<RobotMotorController>() == null) continue;

            onDone?.Invoke(candidate, string.Empty);
            yield break;
        }

        if (fallback != null)
        {
            // Loadable, but not a set-up robot: it will spawn and then do nothing at all, which is a
            // far more confusing thing to hand a player than a refusal.
            onDone?.Invoke(null, $"'{entry.displayName}' downloaded, but it isn't a working robot.");
            yield break;
        }

        onDone?.Invoke(null, $"'{entry.displayName}' downloaded but is empty.");
    }

    // StreamingAssets on Android is a path inside the APK rather than a file on disk, so File.Exists
    // is meaningless there and LoadFromFile has to be asked directly.
    private static bool IsInsideArchive(string path) =>
        path.Contains("jar:") || path.Contains("://") || Application.platform == RuntimePlatform.Android;

    // For tests and for the "clear downloads" path a settings screen might grow. Bundles cannot be
    // reloaded while still open, so anything holding an instantiated robot must be gone first.
    public static void UnloadAll(bool unloadInstances)
    {
        foreach (AssetBundle bundle in loaded.Values)
        {
            if (bundle != null) bundle.Unload(unloadInstances);
        }
        loaded.Clear();
    }
}
