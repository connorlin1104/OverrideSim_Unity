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
    // What a robot can arrive as, best first.
    //
    // CAD (.step/.stp/.f3d/.f3z) used to lead this list and is now not on it at all. The argument for
    // it was sound on paper: a CAD file still holds the exact surfaces, so the mesh can be generated
    // at whatever density the simulator wants instead of at whatever the sender's exporter happened
    // to use, and it crosses the wire at roughly a hundredth of the size. What that left out is who
    // pays for it. Nothing in this project can read any of those formats — Unity imports none of
    // them — so every CAD submission meant a manual round-trip through Fusion, on one machine,
    // before a single triangle reached the editor, and getting a whole assembly down to a usable
    // refinement there is slow and fiddly work. Decimating the FBX afterwards is neither: measured on
    // the first robot through it, Blender took the file down by more than half in minutes.
    //
    // So the ask is the mesh, and the size lever moves to this side of the pipeline — which is
    // where the person who knows what the simulator needs was always the one holding it.
    //
    // URDF and ZIP stay because they are a different route in rather than a CAD one: the URDF
    // importer reads them directly. A URDF needs its meshes alongside it, hence the archive.
    public static readonly string[] AcceptedExtensions = { "fbx", "urdf", "zip" };

    // The preference, said in full where a player is choosing a file. Lives here rather than in the
    // screen so the advice and the list it describes cannot drift apart.
    //
    // It asks for a refinement setting rather than a file size because refinement is the control the
    // sender actually has in front of them. "Keep it under 100 MB" is a number nobody can act on
    // without knowing which slider moves it.
    public const string FormatAdvice =
        "Send your robot as an FBX — export it from your CAD at Low or Medium refinement. " +
        "URDF and ZIP also work.";

    // The short form, for one-line status messages. Every accepted extension is named: the list is
    // three long now, so there is nothing to leave out for brevity's sake.
    public const string AcceptedList = ".fbx, .urdf or .zip";

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
