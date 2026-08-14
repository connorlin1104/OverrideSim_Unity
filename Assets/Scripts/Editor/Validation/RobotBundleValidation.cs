using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

// Validates robot delivery: where a bundle lives, who can find it, and whether this build is allowed
// to open it.
//
// Every failure this guards against is silent from the developer's side, and several are permanent:
//
//   - THE ADDRESS MOVED. A private robot lives at a folder derived from a hash of its owner code.
//     Change how that hash is computed and every robot ever published becomes unreachable — the app
//     asks for a path nothing was ever written to, players see "hasn't been published yet", and no
//     amount of rebuilding fixes it because the old files are at the old address. The hash is
//     therefore PINNED to a literal, exactly as the inbox fingerprint is, so it can only change on
//     purpose.
//
//   - THE SCRIPTS MOVED. A bundle serializes the components on the robot prefab. Add a serialized
//     field to RobotMotorController and Unity deserializes every older bundle against the new
//     layout: the missing field comes back as its default, silently, and the robot drives wrong
//     rather than failing to load. CheckScriptLayout pins the shape of every script that sits on a
//     robot so this is caught here rather than on someone's phone.
//
//   - THE ADDRESS LEAKED. A private robot listed in the public index is not private, however well
//     the download is guarded.
//
// Usage: Tools > RoboSim > Validation > Validate Robot Bundles.
// Batch: -executeMethod RobotBundleValidation.RunBatchValidate
public static class RobotBundleValidation
{
    [MenuItem("Tools/RoboSim/Validation/Validate Robot Bundles", false, 7)]
    private static void RunFromMenu()
    {
        string result = Validate();
        EditorUtility.DisplayDialog("Validate Robot Bundles", result, "OK");
        Debug.Log(result);
    }

    public static void RunBatchValidate()
    {
        string result = Validate();
        Debug.Log(result);
        if (result.StartsWith("FAILED")) throw new System.InvalidOperationException(result);
    }

    private class Checks
    {
        public readonly List<string> Failures = new List<string>();
        public int Count;

        public void That(bool condition, string failureMessage)
        {
            Count++;
            if (!condition) Failures.Add(failureMessage);
        }
    }

    private static string Validate()
    {
        var checks = new Checks();

        CheckPaths(checks);
        CheckVersionGate(checks);
        CheckAddresses(checks);
        CheckPrivacy(checks);
        CheckIndexRoundTrip(checks);
        CheckCatalogResolution(checks);
        CheckScriptLayout(checks);

        if (checks.Failures.Count == 0)
            return $"PASSED: robot bundle validation ({checks.Count} checks).";

        var report = new StringBuilder();
        report.AppendLine($"FAILED: robot bundle validation ({checks.Failures.Count} of {checks.Count} checks).");
        foreach (string failure in checks.Failures) report.AppendLine("  - " + failure);
        return report.ToString();
    }

    // ---------------------------------------------------------------------------------------------

    // The path has to carry BOTH versions, because "can this app read that bundle" is answered by
    // whether the file exists — a check made before anything is downloaded rather than after tens of
    // megabytes have crossed a phone connection.
    private static void CheckPaths(Checks checks)
    {
        string path = RobotBundleFormat.RelativePath("654v-claw", "a1b2c3d4", 3);
        checks.That(path.Contains("/v3/"),
            $"The format version is not in the path: '{path}'. Without it an old app and a new one " +
            "ask for the same file, and one of them gets a robot it cannot read.");
        checks.That(path.Contains("a1b2c3d4"),
            $"The bundle version is not in the path: '{path}'. Without it a rebuilt robot has the " +
            "same URL as the old one and every device that already cached it keeps the old copy.");
        checks.That(path.EndsWith(RobotBundleFormat.Extension), $"Wrong extension: '{path}'.");
        checks.That(path.StartsWith(RobotBundleFormat.PlatformFolder() + "/"),
            $"The platform is not the first segment: '{path}'. iOS and Android bundles are not " +
            "interchangeable and must not be able to collide.");

        // Ids reach a URL and a file path. Anything that could open a path segment or truncate a
        // query has to be folded away, or a robot named "../../inbox" addresses someone else's file.
        checks.That(RobotBundleFormat.Sanitize("654V Claw!") == "654v-claw",
            $"Sanitize left something unsafe: '{RobotBundleFormat.Sanitize("654V Claw!")}'.");
        foreach (string nasty in new[] { "../../etc", "a/b", "a?b=1", "a#b", "a b", "  ", "..." })
        {
            string clean = RobotBundleFormat.Sanitize(nasty);
            checks.That(clean.IndexOf('/') < 0 && clean.IndexOf('?') < 0 && clean.IndexOf('#') < 0
                        && !clean.Contains(".."),
                $"Sanitize('{nasty}') gave '{clean}', which can still escape its path.");
        }

        // Case matters in a Storage object name and not on a phone keyboard.
        checks.That(RobotBundleFormat.Sanitize("654V") == RobotBundleFormat.Sanitize("654v"),
            "Sanitize is case-sensitive, so the same robot can be published at two addresses.");
    }

    // A version-mismatched bundle usually LOADS. That is the whole danger: it hands back a robot
    // whose serialized fields have quietly reverted to their defaults, which is far worse than a
    // refusal because nobody can see it happen.
    private static void CheckVersionGate(Checks checks)
    {
        var current = new RobotModelCatalog.BundleRef
        { id = "claw", version = "abc", scriptVersion = RobotBundleFormat.Version };
        var older = new RobotModelCatalog.BundleRef
        { id = "claw", version = "abc", scriptVersion = RobotBundleFormat.Version - 1 };
        var newer = new RobotModelCatalog.BundleRef
        { id = "claw", version = "abc", scriptVersion = RobotBundleFormat.Version + 1 };

        checks.That(RobotBundleFormat.CanLoad(current), "A bundle at the current format is refused.");
        checks.That(!RobotBundleFormat.CanLoad(older),
            "A bundle built against an OLDER script layout is accepted. It will load, and the fields " +
            "added since will silently be their defaults.");
        checks.That(!RobotBundleFormat.CanLoad(newer),
            "A bundle built against a NEWER script layout is accepted.");
        checks.That(!RobotBundleFormat.CanLoad(new RobotModelCatalog.BundleRef()),
            "An empty bundle reference is accepted as loadable.");
        checks.That(!RobotBundleFormat.CanLoad(null), "A null bundle reference is accepted as loadable.");

        // The two mismatches need DIFFERENT advice: one is on the developer to rebuild, the other is
        // on the player to update. Telling a player to update when the fix is a rebuild leaves them
        // updating forever.
        checks.That(RobotBundleFormat.ExplainMismatch(newer).Contains("Update"),
            $"A too-new bundle doesn't tell the player to update: '{RobotBundleFormat.ExplainMismatch(newer)}'.");
        checks.That(RobotBundleFormat.ExplainMismatch(older).Contains("rebuilt"),
            $"A too-old bundle doesn't say it needs rebuilding: '{RobotBundleFormat.ExplainMismatch(older)}'.");
        checks.That(RobotBundleFormat.ExplainMismatch(current) == string.Empty,
            "A loadable bundle produced an error message anyway.");
    }

    // THE PINNED ADDRESS. If this hash changes, every private robot already published is orphaned:
    // the app asks for a path nothing was written to, and there is no rebuild that fixes it because
    // the old files are still at the old address under a name nobody can compute any more.
    private const string PinnedCode = "654V-1104";
    private const string PinnedFolder = "ce3d330822475faba6ae91da2f4404784";

    private static void CheckAddresses(Checks checks)
    {
        checks.That(RobotBundleAddress.FolderForCode(PinnedCode) == PinnedFolder,
            $"The address of code '{PinnedCode}' changed: got " +
            $"'{RobotBundleAddress.FolderForCode(PinnedCode)}', expected '{PinnedFolder}'. Every " +
            "private robot already published is now unreachable, and republishing is the only fix.");

        // Codes are typed on a phone, and the picker already treats them case- and space-insensitively.
        // The address has to agree, or a code that reveals the entry fails to find the download.
        checks.That(RobotBundleAddress.FolderForCode(" 654v-1104 ") == PinnedFolder,
            "Case and whitespace change the address, so a correctly-typed code can't find its robot.");

        // Distinctness. Two codes sharing an address would put one team's robot in another's folder.
        checks.That(RobotBundleAddress.FolderForCode("654V-1104") != RobotBundleAddress.FolderForCode("654V-1105"),
            "Two different codes produce the same address.");

        // The folder is a path segment.
        string folder = RobotBundleAddress.FolderForCode(PinnedCode);
        checks.That(folder.IndexOf('/') < 0 && folder.IndexOf('.') < 0,
            $"The address '{folder}' is not a single safe path segment.");
        checks.That(folder.Length >= 32,
            $"The address '{folder}' is short enough to sweep exhaustively — it is the only thing " +
            "keeping a private robot private.");

        // A blank or absent code has no address of its own and must fall back to public rather than
        // to a hash of the empty string, which would be one shared folder every codeless robot
        // landed in.
        foreach (string blank in new[] { null, "", "   " })
        {
            checks.That(RobotBundleAddress.FolderForCode(blank) == RobotBundleAddress.PublicFolder,
                $"A blank code ('{blank}') got its own private address instead of the public one.");
        }
    }

    // The address only means anything if nothing else gives it away.
    private static void CheckPrivacy(Checks checks)
    {
        var publicEntry = new RobotModelCatalog.Entry
        { id = "a", visibility = RobotModelCatalog.Visibility.Public, ownerCode = "654V-1104" };
        var privateEntry = new RobotModelCatalog.Entry
        { id = "b", visibility = RobotModelCatalog.Visibility.Private, ownerCode = "654V-1104" };

        checks.That(RobotBundleAddress.Folder(publicEntry) == RobotBundleAddress.PublicFolder,
            "A public robot was given a private address, so nobody can find it.");
        checks.That(RobotBundleAddress.Folder(privateEntry) == PinnedFolder,
            $"A private robot's address doesn't match its code's: '{RobotBundleAddress.Folder(privateEntry)}'.");
        checks.That(RobotBundleAddress.Folder(privateEntry) != RobotBundleAddress.PublicFolder,
            "A private robot is published to the PUBLIC folder — its geometry is downloadable by " +
            "anyone and it is listed in the world-readable index.");

        // Several codes reveal one robot, but it can only live at one address, so the choice has to
        // be deterministic — and both the publisher and the app have to make the same one.
        var multi = new RobotModelCatalog.Entry
        {
            id = "c", visibility = RobotModelCatalog.Visibility.Private,
            ownerCode = "654V-1104, 654V-TEAM",
        };
        checks.That(RobotBundleAddress.Folder(multi) == RobotBundleAddress.FolderForCode("654V-1104"),
            "A robot with several codes is not published under its first one, so the publisher and " +
            "the app disagree about where it lives.");
        var reordered = new RobotModelCatalog.Entry
        {
            id = "c", visibility = RobotModelCatalog.Visibility.Private,
            ownerCode = "  654v-1104 ,654V-TEAM ",
        };
        checks.That(RobotBundleAddress.Folder(reordered) == RobotBundleAddress.Folder(multi),
            "Whitespace and case in the code list move the robot's address.");

        // A private robot with no code cannot be addressed at all. Falling back to public would
        // publish it to everyone, which is the exact opposite of what the setting asked for.
        var codeless = new RobotModelCatalog.Entry
        { id = "d", visibility = RobotModelCatalog.Visibility.Private, ownerCode = "" };
        checks.That(RobotBundleAddress.Folder(codeless) == RobotBundleAddress.PublicFolder,
            "A private robot with no code got a private-looking address it can never be found at.");

        // Index paths sit inside their own folder, so a listing can't reveal robots you hold no code
        // for.
        checks.That(RobotBundleAddress.IndexPath(PinnedFolder).Contains(PinnedFolder),
            "A private index is not inside its own address.");
        checks.That(RobotBundleAddress.PublicIndexPath() != RobotBundleAddress.IndexPath(PinnedFolder),
            "The public index and a private one are the same file.");
        checks.That(RobotBundleAddress.RemotePath(privateEntry, "iOS/v1/b-abc.bundle")
                .StartsWith($"{RobotBundleFormat.RemoteFolder}/{PinnedFolder}/"),
            "A private robot's bundle is not under its own address.");

        // THE PUBLISHER MUST WRITE WHERE THE APP READS. Everything above checks RemotePath against
        // itself, which is why it stayed green while Build Robot Bundle staged bundles one directory
        // level too high — it used Folder(entry) directly and so dropped the "robots/" prefix that
        // RemotePath adds. The index went through IndexPath and DID get the prefix, so the failure
        // would have been a reachable index pointing at a 404: the one shape of bug that looks like
        // a working publish right up until a phone tries to download. Pin the two together.
        // The expectation is spelled out from the PIECES (staging root / remote folder / address /
        // relative) rather than by calling RemotePath, because StagedRemotePath is implemented in
        // terms of RemotePath — comparing the two would re-derive the formula under test and pass
        // no matter what either side did. Written this way, dropping the "robots/" prefix from
        // either the publisher or the reader fails the check.
        foreach (RobotModelCatalog.Entry entry in new[] { publicEntry, privateEntry })
        {
            const string relative = "iOS/v1/b-abc.bundle";
            string staged = BuildRobotBundles.StagedRemotePath(entry, relative).Replace('\\', '/');
            string expected = $"{BuildRobotBundles.StagingFolder}/{RobotBundleFormat.RemoteFolder}/" +
                              $"{RobotBundleAddress.Folder(entry)}/{relative}";
            checks.That(staged == expected,
                $"Build Robot Bundle stages a {entry.visibility} robot at '{staged}', but the app " +
                $"downloads from '{expected}'. The staging tree is uploaded verbatim, so a mismatch " +
                "here is a bundle that 404s even though the index listing it resolves fine.");
        }

        // ...and the same shape, stated once more against the function the APP actually calls, so a
        // change to RemotePath alone can't silently move the download away from the upload.
        checks.That(BuildRobotBundles.StagedRemotePath(privateEntry, "iOS/v1/b-abc.bundle")
                .Replace('\\', '/')
                .EndsWith(RobotBundleAddress.RemotePath(privateEntry, "iOS/v1/b-abc.bundle")),
            "The staged path no longer ends with the address RobotBundleService downloads from.");
    }

    // The index is written by an editor tool and read by JsonUtility on a phone. JsonUtility silently
    // drops what it cannot map, so a field that survives the round trip in one direction and not the
    // other produces a robot that lists with no name, or one that can't be downloaded.
    private static void CheckIndexRoundTrip(Checks checks)
    {
        var index = new RobotCatalogIndex();
        index.robots.Add(new RobotCatalogIndex.Robot
        {
            id = "654v-claw",
            displayName = "654V Claw",
            ownerLabel = "654V",
            bundleId = "654v-claw",
            bundleVersion = "a1b2c3d4",
            scriptVersion = RobotBundleFormat.Version,
            mechanisms = new List<RobotModelCatalog.MechanismInfo>
            {
                new RobotModelCatalog.MechanismInfo { id = "lift", displayName = "DR4B Lift", type = "motor" },
            },
        });

        var parsed = JsonUtility.FromJson<RobotCatalogIndex>(JsonUtility.ToJson(index));
        checks.That(parsed != null && parsed.robots != null && parsed.robots.Count == 1,
            "The index didn't survive a JSON round trip at all.");
        if (parsed == null || parsed.robots.Count != 1) return;

        RobotCatalogIndex.Robot robot = parsed.robots[0];
        checks.That(robot.id == "654v-claw", $"id was lost: '{robot.id}'.");
        checks.That(robot.displayName == "654V Claw", $"displayName was lost: '{robot.displayName}'.");
        checks.That(robot.ownerLabel == "654V", $"ownerLabel was lost: '{robot.ownerLabel}'.");
        checks.That(robot.bundleId == "654v-claw", $"bundleId was lost: '{robot.bundleId}'.");
        checks.That(robot.bundleVersion == "a1b2c3d4", $"bundleVersion was lost: '{robot.bundleVersion}'.");
        checks.That(robot.scriptVersion == RobotBundleFormat.Version,
            $"scriptVersion was lost: {robot.scriptVersion}. Without it every bundle looks like " +
            "format 0 and none of them load.");
        checks.That(robot.mechanisms != null && robot.mechanisms.Count == 1
                    && robot.mechanisms[0].displayName == "DR4B Lift",
            "The mechanism list was lost, so the controller-config screen has nothing to bind.");

        // The index must never carry an owner code. It doesn't need to — the file was only reachable
        // by computing its address from a code the device already holds — and writing one in would
        // mean the code was recoverable from a file rather than only from the person who was given it.
        checks.That(!JsonUtility.ToJson(index).Contains("ownerCode"),
            "The published index has an ownerCode field. Codes must not be written to Storage.");
    }

    // Two fallbacks with different jobs, and swapping them is the kind of mistake that only shows up
    // on a slow connection.
    private static void CheckCatalogResolution(Checks checks)
    {
        var catalog = ScriptableObject.CreateInstance<RobotModelCatalog>();
        try
        {
            catalog.models = new List<RobotModelCatalog.Entry>
            {
                new RobotModelCatalog.Entry
                {
                    id = "bundled", displayName = "Bundled",
                    bundle = new RobotModelCatalog.BundleRef { id = "bundled", version = "v", remote = true },
                },
            };

            checks.That(catalog.FirstVisibleSpawnable() != null,
                "FirstVisibleSpawnable ignores a bundled robot, so a build whose robots are all " +
                "downloaded puts nothing on the field.");
            checks.That(catalog.FirstVisibleWithPrefab() == null,
                "FirstVisibleWithPrefab returned a robot with no prefab. It is the last resort used " +
                "when a download has ALREADY failed — one that can itself need the network is not a " +
                "fallback.");

            // A synced robot is visible like any other, and is filtered by its code like any other.
            checks.That(catalog.AddSynced(new RobotModelCatalog.Entry
            {
                id = "synced", displayName = "Synced",
                visibility = RobotModelCatalog.Visibility.Private,
                ownerCode = "NOBODY-HOLDS-THIS",
                bundle = new RobotModelCatalog.BundleRef { id = "synced", version = "v", remote = true },
            }), "AddSynced refused a new robot.");

            int visible = 0;
            foreach (RobotModelCatalog.Entry _ in catalog.VisibleModels) visible++;
            checks.That(visible == 1,
                $"{visible} robots are visible; a synced PRIVATE robot whose code isn't held must be " +
                "filtered out exactly like a shipped one.");

            // A robot that is both shipped and published must not appear twice, and the shipped copy
            // is the one to keep — it is guaranteed loadable and needs no network.
            checks.That(!catalog.AddSynced(new RobotModelCatalog.Entry
            {
                id = "bundled", displayName = "Bundled (published)",
            }), "AddSynced added a second entry for an id the catalog already had.");

            catalog.ClearSynced();
            int afterClear = 0;
            foreach (RobotModelCatalog.Entry _ in catalog.AllModels) afterClear++;
            checks.That(afterClear == 1,
                $"ClearSynced left {afterClear} entries; synced robots must not accumulate across " +
                "syncs, or an unpublished robot never disappears.");
        }
        finally
        {
            Object.DestroyImmediate(catalog);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // The script layout pin.
    //
    // A bundle contains the serialized state of the components on a robot prefab. Change the shape
    // of one of those components and every bundle built before the change deserializes against the
    // new shape — fields that didn't exist come back as defaults, and the robot drives wrong instead
    // of failing to load.
    //
    // So the shape of every script that sits on a robot is fingerprinted and pinned here. A pinned
    // type that CHANGED means bundles are stale: bump RobotBundleFormat.Version, run Rebuild All
    // Robot Bundles, republish, then re-pin. A type that is merely NEW (or gone) means a robot was
    // added or removed and there is nothing to rebuild — just re-pin.
    // ---------------------------------------------------------------------------------------------
    private static readonly string[] PinnedScriptLayout =
    {
        "ButtonRouter=c87004c4",
        "CascadeLift=eda59298",
        "CascadeRig=aa11ad67",
        "ClawGrab=cd531bc6",
        "ClawRig=097b5931",
        "Dr4bBallast=5ba0a193",
        "Dr4bLift=e1c0ca0c",
        "Dr4bMoveFollower=8205b931",
        "IgnoreRobotSelfCollision=811c9dc5",
        "IntakePull=e72f6705",
        "JointCoupler=c2cbd390",
        "MotorActuator=99f59dc3",
        "PivotRotateFollower=d9dc14d3",
        "PneumaticActuator=4f6be775",
        "PneumaticCylinderFollower=514a5135",
        "PneumaticRig=ce2b7834",
        "PneumaticSlideFollower=d49ceb33",
        "RobotMechanisms=a9139d32",
        "RobotMotorController=6a478539",
    };

    private static void CheckScriptLayout(Checks checks)
    {
        SortedDictionary<string, string> actual = ScriptLayouts();
        var pinned = new SortedDictionary<string, string>();
        foreach (string line in PinnedScriptLayout)
        {
            int split = line.IndexOf('=');
            if (split > 0) pinned[line.Substring(0, split)] = line.Substring(split + 1);
        }

        if (pinned.Count == 0)
        {
            // Nothing pinned yet: report the list to paste in rather than passing silently, which
            // would leave the whole protection switched off and looking green.
            checks.That(false,
                "No script layout is pinned, so nothing is protecting bundles from a script change. " +
                "Paste this into PinnedScriptLayout:\n" + Paste(actual));
            return;
        }

        var changed = new List<string>();
        var added = new List<string>();
        foreach (KeyValuePair<string, string> pair in actual)
        {
            if (!pinned.TryGetValue(pair.Key, out string was)) { added.Add(pair.Key); continue; }
            if (was != pair.Value) changed.Add($"{pair.Key} ({was} -> {pair.Value})");
        }

        var removed = new List<string>();
        foreach (KeyValuePair<string, string> pair in pinned)
        {
            if (!actual.ContainsKey(pair.Key)) removed.Add(pair.Key);
        }

        checks.That(changed.Count == 0,
            $"The serialized shape of {changed.Count} robot script(s) changed: {string.Join(", ", changed)}.\n" +
            "    Every bundle built before this change will load with those fields silently reset.\n" +
            $"    Bump RobotBundleFormat.Version (now {RobotBundleFormat.Version}), run Tools > " +
            "RoboSim > Robot > Rebuild All Robot Bundles, republish, then re-pin:\n" + Paste(actual));

        checks.That(added.Count == 0 && removed.Count == 0,
            $"The set of scripts on robot prefabs changed (new: {Join(added)}; gone: {Join(removed)}).\n" +
            "    Nothing is stale — a robot was added or removed — so no version bump is needed. " +
            "Re-pin:\n" + Paste(actual));
    }

    private static string Join(List<string> items) => items.Count == 0 ? "none" : string.Join(", ", items);

    private static string Paste(SortedDictionary<string, string> layouts)
    {
        var sb = new StringBuilder();
        foreach (KeyValuePair<string, string> pair in layouts)
            sb.AppendLine($"        \"{pair.Key}={pair.Value}\",");
        return sb.ToString();
    }

    // One fingerprint per component type found on a robot prefab, over the serialized property
    // SHAPE — path and type — and never the values, which differ from robot to robot and change
    // every time anything is tuned.
    private static SortedDictionary<string, string> ScriptLayouts()
    {
        var layouts = new SortedDictionary<string, string>();

        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { RoboSimPaths.RobotsFolder }))
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            if (prefab == null) continue;

            foreach (MonoBehaviour behaviour in prefab.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                string type = behaviour.GetType().FullName;
                if (layouts.ContainsKey(type)) continue;
                layouts[type] = Fingerprint(Shape(behaviour));
            }
        }
        return layouts;
    }

    private static string Shape(MonoBehaviour behaviour)
    {
        var paths = new SortedSet<string>();
        var serialized = new SerializedObject(behaviour);
        SerializedProperty property = serialized.GetIterator();

        while (property.NextVisible(true))
        {
            // Unity's own bookkeeping (m_Script, m_Enabled) is not part of what a robot script
            // changes, and array ELEMENTS vary with how many mechanisms a robot happens to have —
            // both would make the fingerprint move for reasons that have nothing to do with layout.
            if (property.propertyPath.StartsWith("m_")) continue;
            if (property.propertyPath.Contains(".Array.data[")) continue;
            paths.Add($"{property.propertyPath}:{property.propertyType}");
        }
        serialized.Dispose();

        return string.Join(",", paths);
    }

    // FNV-1a, for the same reason RobotInboxService uses it: string.GetHashCode is randomized per
    // process, so a pinned value computed with it would differ on the next run and fail every time.
    private static string Fingerprint(string text)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash.ToString("x8");
        }
    }
}
