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
// The anonymous uid is cached in PlayerPrefs so repeat submissions from one device group together —
// it is an upload identity, not an account, and it does not survive a reinstall.
//
// Suggested Storage Rules (writes only into your own folder, no reads from the app):
//   match /uploads/{uid}/{file=**} { allow write: if request.auth.uid == uid; allow read: if false; }
public static class RobotUploadService
{
    public const string UploaderIdPrefKey = "RobotUploaderId";

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

        // Keep the first uid we're ever given, so a player's later submissions land in one folder.
        string storedUid = PlayerPrefs.GetString(UploaderIdPrefKey, string.Empty);
        if (string.IsNullOrEmpty(storedUid))
        {
            PlayerPrefs.SetString(UploaderIdPrefKey, uid);
            PlayerPrefs.Save();
        }
        info.uploaderId = uid;

        onProgress?.Invoke(0.05f);

        // --- 2. the model itself ---
        string folder = $"uploads/{uid}";
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

    public static Submission DescribeThisDevice(string teamName, string robotName, string contact,
        string notes, string fileName, long fileBytes, string submittedAtUtc)
    {
        return new Submission
        {
            teamName = teamName,
            robotName = robotName,
            contact = contact,
            notes = notes,
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
