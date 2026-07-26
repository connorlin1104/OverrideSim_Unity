using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Finding the robot file a player wants to submit.
//
// Unity has no built-in file picker, and adding a native one is a third-party package decision — so
// this deliberately works with what's already there:
//
//   - In the editor: a normal open-file dialog.
//   - On device: the app's own documents folder. The player copies their FBX in from the Files app
//     (iOS needs UIFileSharingEnabled + LSSupportsOpeningDocumentsInPlace in Info.plist for the
//     folder to be visible there) and it shows up in the list here.
//
// If a native picker is added later, ROBOSIM_NATIVE_FILE_PICKER is the seam: implement Browse() over
// it and everything above keeps working.
public static class RobotFilePicker
{
    // What a robot can arrive as. URDF needs its meshes too, hence the archive formats.
    public static readonly string[] AcceptedExtensions = { "fbx", "urdf", "zip" };

    // True when the platform can open a real file dialog; false means the player uses the inbox.
    public static bool CanBrowse
    {
        get
        {
#if UNITY_EDITOR
            return true;
#else
            return false;
#endif
        }
    }

    // Opens a file dialog and returns the chosen path, or null if unavailable/cancelled.
    public static string Browse()
    {
#if UNITY_EDITOR
        string path = UnityEditor.EditorUtility.OpenFilePanelWithFilters(
            "Choose a robot file", "",
            new[] { "Robot files", string.Join(",", AcceptedExtensions), "All files", "*" });
        return string.IsNullOrEmpty(path) ? null : path;
#else
        return null;
#endif
    }

    // Robot files sitting in the app's documents folder, newest first so a file just copied in is
    // the first thing offered.
    public static List<string> InboxFiles()
    {
        var found = new List<string>();
        string root = Application.persistentDataPath;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return found;

        foreach (string extension in AcceptedExtensions)
        {
            try { found.AddRange(Directory.GetFiles(root, "*." + extension, SearchOption.TopDirectoryOnly)); }
            catch (IOException) { /* unreadable folder — just offer nothing */ }
        }

        found.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
        return found;
    }

    public static string InboxLocation => Application.persistentDataPath;

    public static bool LooksLikeRobotFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        foreach (string accepted in AcceptedExtensions)
        {
            if (extension == accepted) return true;
        }
        return false;
    }

    // Reads the file, or returns null and a reason. Robot FBX files here run 100-200 MB, so this is a
    // real allocation — the caller shows progress around it.
    public static byte[] TryRead(string path, out string error)
    {
        error = null;
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException e)
        {
            error = "Couldn't read that file: " + e.Message;
            return null;
        }
        catch (System.UnauthorizedAccessException)
        {
            error = "This app isn't allowed to read that file.";
            return null;
        }
    }

    public static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0L; }
    }
}
