using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// Captures App Store screenshots at Apple's exact required pixel sizes, straight out of the Game
// view, without an iOS device or the Xcode simulator.
//
// This works because nothing in this project reads Screen.safeArea. The UI is a single
// ScaleWithScreenSize canvas (1920x1080 reference, match 0.5), so what it lays out is a pure
// function of the render resolution — set the Game view to 2752x2064 and you get the same pixels an
// iPad renders. Note that this is a real difference, not a stretch: at 4:3 the match-0.5 scaler
// picks a different scale factor than it does at iPhone's 19.5:9, so an iPhone shot resized to iPad
// dimensions would show the wrong layout. That is the whole reason to render at the real size.
//
// The one thing this cannot reproduce is the OS drawing rounded corners and the home indicator over
// the app. Apple does not require those in a screenshot, so it does not matter.
//
// IF THAT SAFE-AREA ASSUMPTION EVER STOPS HOLDING — someone adds Screen.safeArea handling, or a
// notched-iPhone inset — the Game view stops matching the device and these shots become wrong in a
// way that still looks plausible. Capture on hardware at that point.
public static class StoreScreenshotCapture
{
    // Relative to the project root, i.e. beside Assets/ — deliberately NOT inside Assets/, so Unity
    // does not import multi-megabyte PNGs as textures and drag them into the build.
    const string OutputDir = "StoreScreenshots";

    // The only two sizes App Store Connect asks for. Apple takes screenshots for the largest device
    // in each family it is being submitted for and scales down from there, so this is the full list
    // — as long as targetDevice stays at 2 (iPhone and iPad). iPhone only? The iPad row goes away.
    static readonly (string Label, int Width, int Height)[] RequiredSizes =
    {
        ("iPhone 6.9\"", 2868, 1320),
        ("iPad 13\"", 2752, 2064),
    };

    [MenuItem("RoboSim/Screenshots/Capture Game View %#s")]
    static void Capture()
    {
        // ScreenCapture.CaptureScreenshot grabs whatever the Game view last rendered. Outside Play
        // mode the Game view may not have rendered at all, or may hold a stale frame from before the
        // last recompile, and the capture silently succeeds with the wrong contents.
        if (!EditorApplication.isPlaying)
        {
            Debug.LogWarning("[Screenshots] Enter Play mode first — the capture takes the Game " +
                             "view's last rendered frame, and outside Play mode that is stale or absent.");
            return;
        }

        Directory.CreateDirectory(OutputDir);

        // Captured at a temporary name because the size is not known until the PNG exists: the Game
        // view's resolution dropdown is what decides it, and nothing in this API reports it back.
        // The real name is assigned in Finish(), once the header has been read.
        string pending = Path.Combine(OutputDir, "pending.png");
        if (File.Exists(pending)) File.Delete(pending);

        ScreenCapture.CaptureScreenshot(pending);

        // The write happens at the end of the current frame, on a thread we do not control, so the
        // file is neither present nor complete when this method returns. Poll instead of guessing a
        // delay — a big PNG on a slow disk takes longer than any fixed wait worth hardcoding.
        EditorApplication.update += WaitForFile;
    }

    static int _framesWaited;

    static void WaitForFile()
    {
        const int TimeoutFrames = 600; // ~10s at 60fps; far past any plausible PNG write

        string pending = Path.Combine(OutputDir, "pending.png");

        // A PNG whose header is not written yet is shorter than 24 bytes, so this length check
        // doubles as "is the file complete enough to read a size out of".
        if (File.Exists(pending) && new FileInfo(pending).Length > 24)
        {
            EditorApplication.update -= WaitForFile;
            _framesWaited = 0;
            Finish(pending);
            return;
        }

        if (++_framesWaited > TimeoutFrames)
        {
            EditorApplication.update -= WaitForFile;
            _framesWaited = 0;
            Debug.LogError("[Screenshots] Timed out waiting for the capture to be written.");
        }
    }

    static void Finish(string pending)
    {
        if (!TryReadPngSize(pending, out int width, out int height))
        {
            Debug.LogError($"[Screenshots] {pending} is not a readable PNG.");
            return;
        }

        // Matching a required size is what makes the file uploadable, so it goes in the filename —
        // an unmatched shot is worth knowing about here, not after App Store Connect rejects it.
        string match = null;
        foreach (var size in RequiredSizes)
            if (size.Width == width && size.Height == height)
                match = size.Label;

        string slug = match == null ? "WRONG-SIZE" : match.Replace("\"", "").Replace(" ", "-");
        string final = Path.Combine(OutputDir, $"{slug}-{width}x{height}-{NextIndex(slug):00}.png");
        File.Move(pending, final);

        if (match != null)
        {
            Debug.Log($"[Screenshots] {match} — {width}x{height} — saved to {final}");
        }
        else
        {
            // Overwhelmingly the cause: the Game view is on "Free Aspect", or on a fixed resolution
            // whose Scale slider is above 1x. Both render at the window's size, not the target's.
            Debug.LogWarning(
                $"[Screenshots] Captured {width}x{height}, which is not a size App Store Connect " +
                $"accepts. Set the Game view to a fixed resolution — {Expected()} — with the Scale " +
                "slider at 1x, then capture again. Saved anyway to " + final);
        }
    }

    static string Expected()
    {
        string[] parts = new string[RequiredSizes.Length];
        for (int i = 0; i < RequiredSizes.Length; i++)
            parts[i] = $"{RequiredSizes[i].Width}x{RequiredSizes[i].Height} ({RequiredSizes[i].Label})";
        return string.Join(" or ", parts);
    }

    // Counts existing shots of the same size rather than using a timestamp, so the files sort in
    // capture order and a filename tells you which shot it is.
    static int NextIndex(string slug)
    {
        int n = 1;
        while (Directory.GetFiles(OutputDir, $"{slug}-*-{n:00}.png").Length > 0) n++;
        return n;
    }

    // PNG stores width and height as big-endian 32-bit ints at fixed offsets in the IHDR chunk,
    // which is always first. Reading 24 bytes beats importing the file as a Texture2D just to ask
    // it how big it is — that would pull a multi-megabyte image into the editor for two integers.
    static bool TryReadPngSize(string path, out int width, out int height)
    {
        width = height = 0;

        byte[] header = new byte[24];
        using (var stream = File.OpenRead(path))
            if (stream.Read(header, 0, 24) != 24) return false;

        // 8-byte signature, then a 4-byte length and the literal "IHDR".
        if (header[0] != 0x89 || header[1] != 'P' || header[2] != 'N' || header[3] != 'G')
            return false;

        width = BitConverter.ToInt32(new[] { header[19], header[18], header[17], header[16] }, 0);
        height = BitConverter.ToInt32(new[] { header[23], header[22], header[21], header[20] }, 0);
        return width > 0 && height > 0;
    }
}
