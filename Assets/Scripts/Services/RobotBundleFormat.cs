using System.Text;
using UnityEngine;

// The contract between a robot AssetBundle and the app that loads it: where a bundle lives, and
// which builds of the app are allowed to open it.
//
// THE PROBLEM THIS EXISTS FOR. An AssetBundle does not contain "a robot". It contains the serialized
// state of the robot prefab, INCLUDING the components on it — RobotMotorController's brake fractions,
// ClawGrab's mute list, every mechanism's role assignments. That state is written against the field
// layout the scripts had on the day the bundle was built. Add a serialized field, rename one, change
// a type, and Unity deserializes the old bundle against the new layout: the missing field comes back
// as its default, silently. A robot that spawns with its drive tuning zeroed is far worse than one
// that doesn't spawn, because nobody can tell it happened.
//
// So every bundle records the layout it was built against, and the app opens only its own. That
// turns an invisible, permanent miscompile into a message the player can act on, and it means an old
// app keeps working: the versions live in the PATH, so a build shipped last month goes on asking for
// last month's bundles and never sees the new ones at all.
//
// WHEN TO BUMP Version: any change to a serialized field on a script that sits on a robot prefab.
// Adding one counts. RobotBundleValidation computes a fingerprint of every such field and fails if
// the fingerprint moved without the version moving, so this is checked rather than remembered —
// forgetting it is exactly the kind of mistake that only shows up on someone else's phone.
public static class RobotBundleFormat
{
    // Bumped whenever the serialized shape of the scripts on a robot prefab changes.
    public const int Version = 1;

    // Where bundles sit inside the app, under Application.streamingAssetsPath.
    public const string StreamingFolder = "robots";

    // The Storage prefix bundles are served from. Distinct from /uploads, which is a write-only drop
    // box — this one is read-only and never written by the app at all.
    public const string RemoteFolder = "robots";

    public const string Extension = ".bundle";

    // Bundles are platform-specific: the shaders and the serialized layout are compiled for one
    // target and simply will not load on another. One folder per platform, so iOS and Android can
    // both be published without either being able to pick up the other's.
    public static string PlatformFolder(RuntimePlatform platform)
    {
        switch (platform)
        {
            case RuntimePlatform.IPhonePlayer: return "iOS";
            case RuntimePlatform.Android: return "Android";
            case RuntimePlatform.OSXPlayer:
            case RuntimePlatform.OSXEditor: return "StandaloneOSX";
            case RuntimePlatform.WindowsPlayer:
            case RuntimePlatform.WindowsEditor: return "StandaloneWindows64";
            case RuntimePlatform.WebGLPlayer: return "WebGL";
            default: return platform.ToString();
        }
    }

    public static string PlatformFolder() => PlatformFolder(Application.platform);

    // <platform>/v<scriptVersion>/<id>-<bundleVersion>.bundle
    //
    // Both versions are in the path rather than inside the file so that "this app cannot read that
    // bundle" is answered by a 404 before anything is downloaded — on a robot that may be tens of
    // megabytes, finding out after the transfer is not an answer.
    public static string RelativePath(string bundleId, string bundleVersion, int scriptVersion)
    {
        string id = Sanitize(bundleId);
        string version = Sanitize(bundleVersion);
        string file = string.IsNullOrEmpty(version) ? id : $"{id}-{version}";
        return $"{PlatformFolder()}/v{scriptVersion}/{file}{Extension}";
    }

    public static string RelativePath(RobotModelCatalog.BundleRef bundle) =>
        bundle == null ? string.Empty
            : RelativePath(bundle.id, bundle.version, bundle.scriptVersion);

    // Whether this build can open that bundle at all. The caller reports the answer rather than
    // attempting the load: a version-mismatched bundle usually DOES load, and hands back a robot
    // with fields quietly reset.
    public static bool CanLoad(RobotModelCatalog.BundleRef bundle) =>
        bundle != null && bundle.IsSet && bundle.scriptVersion == Version;

    public static string ExplainMismatch(RobotModelCatalog.BundleRef bundle)
    {
        if (bundle == null || !bundle.IsSet) return "This robot has no bundle to load.";
        if (bundle.scriptVersion == Version) return string.Empty;
        return bundle.scriptVersion < Version
            ? "This robot was built for an older version of the app and needs to be rebuilt."
            : "This robot needs a newer version of the app. Update from the App Store and it'll " +
              "be here.";
    }

    // Bundle ids and versions end up in a URL and in a file path, so anything that could open a
    // path segment or truncate a query has to go. Lower-cased because Storage object names are
    // case-sensitive and a phone keyboard is not.
    public static string Sanitize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '.') sb.Append(c);
            else if (sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-', '.');
    }
}
