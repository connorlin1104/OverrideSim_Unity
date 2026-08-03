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
    // The CAD formats lead because they are the only ones that still hold the exact surfaces. An FBX
    // is already tessellated — whatever refinement the sender's export happened to use is baked in,
    // and the only thing anyone can do to it afterwards is decimate, which is a lossy guess at
    // geometry that was exact one step earlier. From a .step or .f3d the mesh is generated at
    // whatever density the simulator wants, and small features like screw holes come out right by
    // construction rather than by being preserved. It is also about 100x smaller: the same robot is
    // ~100 MB of triangles or a couple of MB of surfaces.
    //
    // .f3d is Fusion's own archive; .step is what every other CAD package exports. Both keep the
    // component names the setup tools read, which is the only structure this project needs.
    // '.stp' is the same format as '.step' — exporters are split roughly evenly between the two
    // spellings, and a player whose file is silently refused has no way to work out why.
    //
    // FBX/URDF/ZIP stay accepted because they are what people already have. URDF needs its meshes
    // alongside it, hence the archive format.
    public static readonly string[] AcceptedExtensions =
        { "step", "stp", "f3d", "fbx", "urdf", "zip" };

    // The preference, said in full where a player is choosing a file. Lives here rather than in the
    // screen so the advice and the list it describes cannot drift apart.
    public const string FormatAdvice =
        "Best: send your CAD, not an export of it. Made in Fusion 360 — export a .f3d. " +
        "Any other CAD — export a .step. Both are far smaller and keep the exact shapes, so the " +
        "robot comes out sharper. FBX, URDF and ZIP still work.";

    // The short form, for one-line status messages. '.stp' is left out on purpose: it is accepted,
    // but naming both spellings in a list this size reads as two formats rather than one.
    public const string AcceptedList = ".step, .f3d, .fbx, .urdf or .zip";

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
