using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

// The published list of robots that exist but weren't shipped with this build.
//
// This is the half of downloadable robots that has nothing to do with downloading: before an app can
// fetch a robot it has to know the robot is there. The catalog inside the build only ever knows
// about robots that existed when it was compiled, which is exactly the constraint the whole exercise
// is trying to remove.
[Serializable]
public class RobotCatalogIndex
{
    [Serializable]
    public class Robot
    {
        public string id;
        public string displayName;
        public string ownerLabel;
        public string bundleId;
        public string bundleVersion;
        public int scriptVersion;
        public List<RobotModelCatalog.MechanismInfo> mechanisms = new List<RobotModelCatalog.MechanismInfo>();
    }

    public List<Robot> robots = new List<Robot>();
}

// Fetches those lists and merges them into the catalog for this session.
//
// ONE INDEX PER ADDRESS, and that is a privacy decision rather than a filing one. A single
// world-readable index naming every robot would hand over the display names, team labels and the
// bare fact that a given team has submitted anything — for robots whose whole point is that they are
// private. So the public index lists public robots, and a private robot is listed only inside the
// folder derived from its own owner code. A device asks the addresses it can compute: the public
// one, plus one per code the player holds. Everything else is unnameable.
//
// FAILURE IS SILENT, on purpose. This runs at launch alongside the inbox fetch. Being offline, being
// on a plane, a bucket that isn't configured, a 404 because nothing has ever been published — none
// of those are the player's problem and none of them should produce a message on a home screen. What
// is NOT silent is a robot that appears in the list and then can't be loaded; that is reported at
// the point the player asks for it, by RobotBundleService.
public static class RobotCatalogSync
{
    private const string DownloadUrl = "https://firebasestorage.googleapis.com/v0/b/{0}/o/{1}?alt=media";

    // Merges every reachable index into `catalog` and calls back with how many robots were added.
    public static IEnumerator Sync(RobotModelCatalog catalog, RobotUploadConfig config,
        Action<int> onDone)
    {
        if (catalog == null || config == null || !config.IsConfigured ||
            Application.internetReachability == NetworkReachability.NotReachable)
        {
            onDone?.Invoke(0);
            yield break;
        }

        catalog.ClearSynced();
        int added = 0;

        yield return Merge(catalog, config, RobotBundleAddress.PublicIndexPath(),
            RobotModelCatalog.Visibility.Public, null, count => added += count);

        // One request per held code. A player holds a handful at most — they are typed in by hand —
        // so this is a few requests at launch, all of which 404 cheaply when nothing is published.
        foreach (string code in RobotOwnerSettings.AllCodes())
        {
            string folder = RobotBundleAddress.FolderForCode(code);
            if (folder == RobotBundleAddress.PublicFolder) continue;

            yield return Merge(catalog, config, RobotBundleAddress.IndexPath(folder),
                RobotModelCatalog.Visibility.Private, code, count => added += count);
        }

        onDone?.Invoke(added);
    }

    // Just one code's address, without clearing what's already been synced.
    //
    // This is what a code typed into Settings needs. The rule there is that a code matching nothing
    // is rejected rather than banked — "stored, but nothing appeared" is indistinguishable from a
    // broken feature — and that rule quietly became wrong the moment a robot could exist without
    // being in the build: the code is perfectly good, its robot is simply published rather than
    // shipped. Asking the network is how "matches nothing" stays an honest answer.
    public static IEnumerator SyncCode(RobotModelCatalog catalog, RobotUploadConfig config,
        string ownerCode, Action<int> onDone)
    {
        string folder = RobotBundleAddress.FolderForCode(ownerCode);
        if (catalog == null || config == null || !config.IsConfigured ||
            folder == RobotBundleAddress.PublicFolder ||
            Application.internetReachability == NetworkReachability.NotReachable)
        {
            onDone?.Invoke(0);
            yield break;
        }

        yield return Merge(catalog, config, RobotBundleAddress.IndexPath(folder),
            RobotModelCatalog.Visibility.Private, ownerCode, onDone);
    }

    private static IEnumerator Merge(RobotModelCatalog catalog, RobotUploadConfig config,
        string objectPath, RobotModelCatalog.Visibility visibility, string ownerCode,
        Action<int> onCount)
    {
        // Storage object names are one flat string in the URL path, so every '/' has to arrive
        // percent-encoded or the request addresses a folder that doesn't exist.
        string url = string.Format(DownloadUrl, config.storageBucket,
            UnityWebRequest.EscapeURL(objectPath));

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            yield return request.SendWebRequest();

            // 404 is the ordinary case for an address nothing has been published to yet.
            if (request.result != UnityWebRequest.Result.Success)
            {
                onCount?.Invoke(0);
                yield break;
            }

            RobotCatalogIndex index = null;
            try { index = JsonUtility.FromJson<RobotCatalogIndex>(request.downloadHandler.text); }
            catch (Exception) { /* handled by the null check below */ }

            if (index == null || index.robots == null)
            {
                onCount?.Invoke(0);
                yield break;
            }

            int added = 0;
            foreach (RobotCatalogIndex.Robot robot in index.robots)
            {
                if (robot == null || string.IsNullOrWhiteSpace(robot.id)) continue;

                // Skip anything this build cannot open, rather than listing it and failing later.
                // A robot in the picker that always errors is worse than one that isn't there —
                // it looks like the app is broken rather than like it needs updating.
                //
                // The exception is a bundle built for an OLDER format: that one is listed, because
                // "update the app" is not the fix and the player would otherwise never learn the
                // robot exists. RobotBundleFormat.ExplainMismatch says which case it is.
                if (robot.scriptVersion > RobotBundleFormat.Version) continue;

                var entry = new RobotModelCatalog.Entry
                {
                    id = robot.id,
                    displayName = string.IsNullOrWhiteSpace(robot.displayName) ? robot.id : robot.displayName,
                    ownerLabel = robot.ownerLabel,
                    visibility = visibility,
                    // The code isn't in the file. It doesn't need to be: this index was only
                    // reachable by computing its address from a code this device already holds, so
                    // the code that opened the door is the code that reveals what's behind it.
                    ownerCode = ownerCode ?? string.Empty,
                    mechanisms = robot.mechanisms ?? new List<RobotModelCatalog.MechanismInfo>(),
                    bundle = new RobotModelCatalog.BundleRef
                    {
                        id = robot.bundleId,
                        version = robot.bundleVersion,
                        scriptVersion = robot.scriptVersion,
                        remote = true,
                    },
                };

                if (!entry.bundle.IsSet) continue;
                if (catalog.AddSynced(entry)) added++;
            }

            onCount?.Invoke(added);
        }
    }
}
