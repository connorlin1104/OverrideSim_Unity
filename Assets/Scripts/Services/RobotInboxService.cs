using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

// The one channel back to a player who sent a robot in.
//
// A submitted robot is set up by hand and ships inside a new app version, so nothing here downloads
// one. What this CAN do is carry two kinds of reply:
//
//   - **It arrived.** Names the robot and hands over the owner code that reveals it — the difference
//     between "did he ever get my robot?" and a robot that simply appears in the list.
//   - **It didn't work out.** A plain message with NO code: the model was a shell with no moving
//     parts, the arm is one welded lump, the file only had the chassis. Setup fails for ordinary
//     reasons and the player is the only one who can fix them, so the failure has to reach them —
//     silence is indistinguishable from "he never looked at it".
//
// Without this the only reply channel is the free-text contact field, and a typo there orphans a
// submission for good.
//
// One GET of inbox/<uploaderId>.json from the same Storage bucket the upload went to. 404 is the
// normal case (nothing waiting) and reports as an empty inbox, not an error.
//
// The file is written by hand from the Firebase console. Both shapes, in one file:
//   { "items": [
//       { "robotName": "654V Claw", "code": "654V-8213", "message": "" },
//       { "id": "654v-claw-2026-08", "robotName": "654V Claw",
//         "message": "The arm came in as one solid piece, so nothing can pivot. Re-export with the
//                     arm as its own component and send it again." }
//   ] }
//
// An item with a code is an arrival; an item with only a message is a note. `id` is optional and
// only matters for notes — see KeyFor.
//
// /inbox is world-readable and never written by the app; the enforcing text is storage.rules at the
// repo root, not a copy here.
//
// Public read is deliberate: the uploader id is a 28-character random string and is itself the only
// thing guarding an inbox. Scoping the rule to request.auth.uid instead would not work — anonymous
// sign-in mints a fresh uid on every call, so the signed-in uid never matches the stored one.
public static class RobotInboxService
{
    private const string DownloadUrl = "https://firebasestorage.googleapis.com/v0/b/{0}/o/{1}?alt=media";

    [Serializable]
    public class Item
    {
        public string id;        // optional; identifies a note so "already read" can stick. See KeyFor
        public string robotName; // what to call it on the home screen
        public string code;      // the owner code that reveals the catalog entry — absent on a note
        public string message;   // the note itself, or an aside alongside an arrival
    }

    [Serializable]
    public class Inbox
    {
        public List<Item> items = new List<Item>();
    }

    // An item that hands over a code carries its own memory: once the code is held it filters itself
    // out. A note has nothing like that, so it is remembered by key (RobotInboxSettings).
    public static bool IsArrival(Item item) => item != null && !string.IsNullOrWhiteSpace(item.code);

    public static bool IsNote(Item item)
        => item != null && string.IsNullOrWhiteSpace(item.code) && !string.IsNullOrWhiteSpace(item.message);

    // What "already read" is recorded against. The item's own `id` when it has one; otherwise a
    // fingerprint of its text, which means REWRITING a message shows it again — correct, because a
    // rewritten message is a new thing to say, and it saves having to remember to bump an id.
    public static string KeyFor(Item item)
    {
        if (item == null) return string.Empty;
        if (!string.IsNullOrWhiteSpace(item.id)) return item.id.Trim();

        return Fingerprint((item.robotName ?? string.Empty) + Separator + (item.message ?? string.Empty));
    }

    // Written as an escape, not as the byte: a raw NUL in a .cs file makes git treat the whole source
    // as binary. NUL rather than a space because neither field can contain one, so "A B"/"C" and
    // "A"/"B C" can't fingerprint alike.
    private const string Separator = "\u0000";

    // FNV-1a, not string.GetHashCode: .NET randomizes string hashing per process, so a GetHashCode
    // key would compare equal only within the launch that wrote it — and a dismissed note would come
    // straight back on the next launch, which is the exact bug this key exists to prevent.
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

    // Calls back with whatever is waiting (never null) plus an error string that is empty on success
    // AND on a plain "nothing waiting". Only a real failure — bad bucket, unreadable file — sets it.
    // Being offline is not an error: this runs at launch, and a home screen must not scold anyone for
    // opening the app on a plane.
    public static IEnumerator Fetch(RobotUploadConfig config, string uploaderId,
        Action<Inbox, string> onDone)
    {
        var empty = new Inbox();

        if (config == null || !config.IsConfigured || string.IsNullOrWhiteSpace(uploaderId))
        {
            onDone?.Invoke(empty, string.Empty);
            yield break;
        }
        if (Application.internetReachability == NetworkReachability.NotReachable)
        {
            onDone?.Invoke(empty, string.Empty);
            yield break;
        }

        // Storage object names are one flat string in the URL path, so the separating slash has to
        // arrive percent-encoded or the request addresses a folder that doesn't exist.
        string path = UnityWebRequest.EscapeURL($"inbox/{uploaderId.Trim()}.json");
        using (UnityWebRequest request = UnityWebRequest.Get(
                   string.Format(DownloadUrl, config.storageBucket, path)))
        {
            yield return request.SendWebRequest();

            // 404 means nothing has been put there yet — overwhelmingly the common case, and silent.
            if (request.responseCode == 404)
            {
                onDone?.Invoke(empty, string.Empty);
                yield break;
            }
            if (request.result != UnityWebRequest.Result.Success)
            {
                onDone?.Invoke(empty, request.error);
                yield break;
            }

            Inbox parsed = null;
            try { parsed = JsonUtility.FromJson<Inbox>(request.downloadHandler.text); }
            catch (Exception) { /* handled by the null check below */ }

            if (parsed == null || parsed.items == null)
            {
                onDone?.Invoke(empty, "The inbox file couldn't be read.");
                yield break;
            }
            onDone?.Invoke(parsed, string.Empty);
        }
    }
}
