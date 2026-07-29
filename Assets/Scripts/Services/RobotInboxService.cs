using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

// Checks whether a robot a player sent in has come back yet.
//
// A submitted robot is set up by hand and ships inside an app update, so nothing here downloads one.
// What this CAN do is tell the player it has arrived and hand over the owner code that reveals it —
// which is the difference between "did he ever get my robot?" and a robot that simply appears in the
// list. Without it the only reply channel is the free-text contact field, and a typo there orphans a
// submission for good.
//
// One GET of inbox/<uploaderId>.json from the same Storage bucket the upload went to. 404 is the
// normal case (nothing waiting) and reports as an empty inbox, not an error.
//
// The file is written by hand from the Firebase console when the robot ships:
//   { "items": [ { "robotName": "654V Claw", "code": "654V-8213", "message": "" } ] }
//
// /inbox is world-readable and never written by the app; the enforcing text is storage.rules at the
// repo root, not a copy here.
//
// Public read is deliberate: the uploader id is a 28-character random string and is itself the only
// thing guarding an inbox, so it is treated as a secret (shown to the player as a recovery code, not
// as a name). Scoping the rule to request.auth.uid instead would not work — anonymous sign-in mints a
// fresh uid on every call, so the signed-in uid never matches the stored one.
public static class RobotInboxService
{
    private const string DownloadUrl = "https://firebasestorage.googleapis.com/v0/b/{0}/o/{1}?alt=media";

    [Serializable]
    public class Item
    {
        public string robotName; // what to call it on the home screen
        public string code;      // the owner code that reveals the catalog entry
        public string message;   // optional note from the developer
    }

    [Serializable]
    public class Inbox
    {
        public List<Item> items = new List<Item>();
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
