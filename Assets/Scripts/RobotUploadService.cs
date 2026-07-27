using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

// Sends a player's robot file to Firebase Storage over REST, plus a small JSON sidecar describing who
// sent it and what it is. Two requests, no SDK:
//
//   1. POST identitytoolkit.googleapis.com/v1/accounts:signUp   -> anonymous uid + idToken
//   2. POST firebasestorage.googleapis.com/v0/b/<bucket>/o?name=... -> the bytes
//
// The first anonymous uid a device is given is cached in PlayerPrefs and reused as the folder name
// for every later submission, so one player's robots stay together. It is an upload identity, not an
// account: it does not survive a reinstall on its own, which is why it is shown to the player as a
// recovery id they can write down and paste back (AdoptUploaderId). That bearer code IS the account.
//
// Suggested Storage Rules — a drop box that players write to and never read:
//   match /uploads/{uid}/{file=**} { allow write: if request.auth != null; allow read: if false; }
//   match /inbox/{file=**}         { allow read:  if true;  allow write: if false; }
//
// The write rule checks only that the caller signed in, not that the folder matches their uid: a
// player who restores an id from an old device signs in fresh (a NEW auth uid) but must still land in
// their original folder. Anyone can obtain an anonymous sign-in, so treat /uploads as untrusted input
// — which it is regardless, being arbitrary player files.
public static class RobotUploadService
{
    public const string UploaderIdPrefKey = "RobotUploaderId";

    // What the uploader wants done with their robot once it's set up — it decides whether the finished
    // catalog entry ships Public or Private, so it has to be their call rather than a guess. Plain
    // sentences because a human reads them off the sidecar. The default is the most private option:
    // guessing wrong the other way publishes a design someone wanted kept.
    public static readonly string[] SharingOptions =
    {
        "Only me",
        "My team — anyone I give the code to",
        "Anyone — list it publicly",
    };

    private const string SignUpUrl = "https://identitytoolkit.googleapis.com/v1/accounts:signUp?key=";
    private const string StorageUrl = "https://firebasestorage.googleapis.com/v0/b/{0}/o?name={1}";

    [Serializable]
    private class SignUpResponse
    {
        public string idToken;
        public string localId;
    }

    // What the developer needs in order to know whose robot this is and what to do with it. Uploaded
    // alongside the model as <file>.json.
    [Serializable]
    public class Submission
    {
        public string teamName;
        public string robotName;
        public string contact;
        public string notes;
        public string sharing; // one of SharingOptions — who the uploader wants to be able to use it
        public string fileName;
        public long fileBytes;
        public string uploaderId;
        public string appVersion;
        public string devicePlatform;
        public string submittedAtUtc;
    }

    // Uploads `bytes` as `info.fileName`. Reports 0..1 progress and finishes with (ok, message).
    // Never throws: every failure path comes back through onDone so the screen can show it.
    public static IEnumerator Submit(RobotUploadConfig config, Submission info, byte[] bytes,
        Action<float> onProgress, Action<bool, string> onDone)
    {
        if (config == null || !config.IsConfigured)
        {
            onDone?.Invoke(false, "Uploading isn't switched on in this build yet — the destination " +
                                  "hasn't been configured.");
            yield break;
        }
        if (bytes == null || bytes.Length == 0)
        {
            onDone?.Invoke(false, "That file is empty.");
            yield break;
        }

        long limit = (long)Mathf.Max(1, config.maxUploadMegabytes) * 1024L * 1024L;
        if (bytes.LongLength > limit)
        {
            onDone?.Invoke(false, $"That file is {Format(bytes.LongLength)}, over the " +
                                  $"{config.maxUploadMegabytes} MB limit.");
            yield break;
        }
        if (Application.internetReachability == NetworkReachability.NotReachable)
        {
            onDone?.Invoke(false, "No internet connection.");
            yield break;
        }

        onProgress?.Invoke(0f);

        // --- 1. anonymous sign-in ---
        string idToken = null;
        string uid = null;
        using (UnityWebRequest auth = UnityWebRequest.Post(SignUpUrl + config.webApiKey,
                   "{\"returnSecureToken\":true}", "application/json"))
        {
            yield return auth.SendWebRequest();
            if (auth.result != UnityWebRequest.Result.Success)
            {
                onDone?.Invoke(false, "Couldn't reach the upload service: " + Describe(auth));
                yield break;
            }

            SignUpResponse response = null;
            try { response = JsonUtility.FromJson<SignUpResponse>(auth.downloadHandler.text); }
            catch (Exception) { /* handled by the null check below */ }

            if (response == null || string.IsNullOrEmpty(response.idToken))
            {
                onDone?.Invoke(false, "The upload service didn't accept this app. Check that " +
                                      "Anonymous sign-in is enabled in Firebase Authentication.");
                yield break;
            }
            idToken = response.idToken;
            uid = response.localId;
        }

        // Keep the first uid we're ever given and keep USING it, rather than the uid this particular
        // sign-in returned. Anonymous sign-up mints a new uid every call, so the stored one is the only
        // stable handle a player has — it names their upload folder, it is what their inbox is keyed
        // on, and it is what a restore on a new device brings back.
        string storedUid = PlayerPrefs.GetString(UploaderIdPrefKey, string.Empty);
        if (string.IsNullOrEmpty(storedUid))
        {
            storedUid = uid;
            PlayerPrefs.SetString(UploaderIdPrefKey, storedUid);
            PlayerPrefs.Save();
        }
        info.uploaderId = storedUid;

        onProgress?.Invoke(0.05f);

        // --- 2. the model itself ---
        string folder = $"uploads/{storedUid}";
        string modelPath = $"{folder}/{SanitizeFileName(info.fileName)}";
        using (UnityWebRequest upload = new UnityWebRequest(
                   string.Format(StorageUrl, config.storageBucket, UnityWebRequest.EscapeURL(modelPath)),
                   UnityWebRequest.kHttpVerbPOST))
        {
            upload.uploadHandler = new UploadHandlerRaw(bytes) { contentType = "application/octet-stream" };
            upload.downloadHandler = new DownloadHandlerBuffer();
            upload.SetRequestHeader("Authorization", "Firebase " + idToken);

            UnityWebRequestAsyncOperation operation = upload.SendWebRequest();
            while (!operation.isDone)
            {
                // Leave the last slice of the bar for the metadata request that follows.
                onProgress?.Invoke(0.05f + upload.uploadProgress * 0.9f);
                yield return null;
            }

            if (upload.result != UnityWebRequest.Result.Success)
            {
                onDone?.Invoke(false, "The upload failed: " + Describe(upload));
                yield break;
            }
        }

        onProgress?.Invoke(0.95f);

        // --- 3. the sidecar describing it (best effort: the model is already safely up) ---
        string metaPath = $"{folder}/{SanitizeFileName(info.fileName)}.json";
        byte[] metaBytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(info, true));
        using (UnityWebRequest meta = new UnityWebRequest(
                   string.Format(StorageUrl, config.storageBucket, UnityWebRequest.EscapeURL(metaPath)),
                   UnityWebRequest.kHttpVerbPOST))
        {
            meta.uploadHandler = new UploadHandlerRaw(metaBytes) { contentType = "application/json" };
            meta.downloadHandler = new DownloadHandlerBuffer();
            meta.SetRequestHeader("Authorization", "Firebase " + idToken);
            yield return meta.SendWebRequest();

            if (meta.result != UnityWebRequest.Result.Success)
            {
                // Don't call this a failure — the robot arrived, just without its label.
                Debug.LogWarning("RobotUploadService: the model uploaded but its details didn't: " +
                                 Describe(meta));
            }
        }

        onProgress?.Invoke(1f);
        onDone?.Invoke(true, $"Sent {Format(bytes.LongLength)}. {(string.IsNullOrWhiteSpace(info.robotName) ? "Your robot" : info.robotName)} is on its way.");
    }

    // The uploader id held on this device, or empty if nothing has ever been sent from it. This is the
    // only thing tying a player to their submissions and to their inbox, so it is worth showing them:
    // PlayerPrefs is all that holds it, and a reinstall wipes it.
    public static string UploaderId => PlayerPrefs.GetString(UploaderIdPrefKey, string.Empty);

    // Take on an id from another install, so a new phone reclaims the robots the old one sent in.
    // Permissive about case and surrounding space (it gets read off a screenshot), strict about shape,
    // so a mistyped id is rejected here rather than silently pointing the inbox at nothing. Firebase
    // uids are 28 alphanumeric characters; the bounds are loose in case that ever changes.
    public static bool AdoptUploaderId(string id)
    {
        string trimmed = string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim();
        if (trimmed.Length < 20 || trimmed.Length > 64) return false;
        foreach (char c in trimmed)
        {
            if (!char.IsLetterOrDigit(c)) return false;
        }

        PlayerPrefs.SetString(UploaderIdPrefKey, trimmed);
        PlayerPrefs.Save();
        return true;
    }

    public static Submission DescribeThisDevice(string teamName, string robotName, string contact,
        string notes, string sharing, string fileName, long fileBytes, string submittedAtUtc)
    {
        return new Submission
        {
            teamName = teamName,
            robotName = robotName,
            contact = contact,
            notes = notes,
            sharing = sharing,
            fileName = fileName,
            fileBytes = fileBytes,
            uploaderId = PlayerPrefs.GetString(UploaderIdPrefKey, string.Empty),
            appVersion = Application.version,
            devicePlatform = Application.platform.ToString(),
            submittedAtUtc = submittedAtUtc,
        };
    }

    public static string Format(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024f * 1024f * 1024f):F1} GB";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024f * 1024f):F0} MB";
        if (bytes >= 1024L) return $"{bytes / 1024f:F0} KB";
        return $"{bytes} bytes";
    }

    // Storage object names are a flat string, so a stray slash would silently create a subfolder and
    // a stray '#'/'?' would truncate the name.
    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "robot.bin";

        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
        {
            if (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-') sb.Append(c);
            else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
        }
        string cleaned = sb.ToString().Trim('_');
        return cleaned.Length == 0 ? "robot.bin" : cleaned;
    }

    private static string Describe(UnityWebRequest request)
    {
        string body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
        if (!string.IsNullOrEmpty(body) && body.Length > 300) body = body.Substring(0, 300);
        return string.IsNullOrEmpty(body) ? request.error : $"{request.error} ({body})";
    }
}
