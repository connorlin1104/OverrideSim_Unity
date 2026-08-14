using System.Security.Cryptography;
using System.Text;
using UnityEngine;

// Where a robot's bundle lives in Storage — and, for a private robot, the reason a stranger cannot
// find it.
//
// THE PROBLEM. Firebase Storage Rules can check who you are signed in as. They cannot check whether
// you know a code: a code is a bearer token this project hands to players deliberately, not an
// identity, and there is no server here to trade one for a signed URL. So "only people with the code
// may download this robot" cannot be written as a rule.
//
// THE ANSWER, which is the same one the rest of this project already runs on: make the code a
// CAPABILITY. A private robot is published under a folder named for a hash of its owner code. Anyone
// holding the code can compute the folder and ask for it. Anyone who doesn't cannot name the thing
// they want, and the rules never have to decide — a request for a path you couldn't derive is a 404
// like any other. Listing is off, so the folder cannot be discovered by enumeration either.
//
// This is a real improvement on what shipped before, and it is worth being exact about what changed.
// Today every robot is compiled into the app and a private one is merely FILTERED OUT of the picker
// — the geometry is on every device that installs the game, and Connor accepted that caveat
// explicitly. With this, the geometry never arrives at all unless the code was presented. What it
// still does not survive is someone who has both the app files and the code, which is unavoidable:
// once a device can fetch a robot, a device can keep it.
//
// The salt is not a secret and is not pretending to be one. It exists so the folder names are not a
// plain hash of a short string like "654V-1104", which anyone could tabulate for every plausible
// team number without ever seeing this app. It raises the cost of a generic sweep; it does not
// defend against someone who has read this file.
public static class RobotBundleAddress
{
    private const string Salt = "robosim-robot-bundle-v1:";

    // Public robots all share one folder. There is nothing to protect — they are listed to everyone
    // by definition — and one folder keeps the index that describes them in one place.
    public const string PublicFolder = "public";

    // Folder names for private robots start with this so they are visibly not the public one, and so
    // a future scheme can be told apart from this one without guessing at hex.
    private const string PrivatePrefix = "c";

    // The folder a robot's bundle sits in, under RobotBundleFormat.RemoteFolder.
    public static string Folder(RobotModelCatalog.Entry entry)
    {
        if (entry == null) return PublicFolder;
        if (entry.visibility != RobotModelCatalog.Visibility.Private) return PublicFolder;

        // Published under the FIRST of its codes. An entry may carry several — its own one-off code
        // and its team's — and any of them reveals it in the picker, but the bundle has to live at
        // exactly one address. SplitCodes is order-preserving and canonical, so the tool that
        // publishes and the app that fetches derive the same folder from the same entry.
        var codes = RobotOwnerSettings.SplitCodes(entry.ownerCode);
        return codes.Count == 0 ? PublicFolder : FolderForCode(codes[0]);
    }

    public static string FolderForCode(string ownerCode)
    {
        string normalized = RobotOwnerSettings.Normalize(ownerCode);
        if (string.IsNullOrEmpty(normalized)) return PublicFolder;
        return PrivatePrefix + Hash(normalized);
    }

    // robots/<folder>/<platform>/v<scriptVersion>/<id>-<version>.bundle
    public static string RemotePath(RobotModelCatalog.Entry entry, string relative) =>
        $"{RobotBundleFormat.RemoteFolder}/{Folder(entry)}/{relative}";

    // The index that tells an app which robots exist at an address it can reach.
    //
    // Discovery has to be behind the same capability as the download, or it leaks the answer: a
    // world-readable list naming every private robot would hand over the display names, the team
    // labels, and the fact that a given team has submitted anything at all. So a private robot's
    // index lives in its own folder and is found the same way its bundle is.
    public static string IndexPath(string folder) =>
        $"{RobotBundleFormat.RemoteFolder}/{folder}/index.json";

    public static string PublicIndexPath() => IndexPath(PublicFolder);

    // SHA-256, truncated to 128 bits. Truncation is fine here — this is an address, not an integrity
    // check, and 128 bits is far past the point where guessing is the attack anyone would choose.
    //
    // NOT the FNV-1a fingerprint used for inbox keys: that is 32 bits, which is small enough to
    // sweep exhaustively over a single bucket, and this one has to resist exactly that. It is also
    // not string.GetHashCode, which .NET randomizes per process — an address that changed on every
    // launch would put a robot somewhere new each time and it would never be found again.
    private static string Hash(string text)
    {
        using (var sha = SHA256.Create())
        {
            byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(Salt + text));
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++) sb.Append(digest[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
