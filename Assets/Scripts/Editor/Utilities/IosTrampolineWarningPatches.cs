#if UNITY_IOS
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Callbacks;

// Re-applies the Xcode warning fixes to Unity's generated iOS trampoline after every build.
//
// The trampoline (Classes/, Libraries/) is regenerated on export — Append included — so a warning
// fixed by hand in Xcode comes back on the next build, and the person who fixed it has no way of
// knowing. That is the same failure mode IosPlistPostProcessor was written for: a manual step that
// survives exactly as long as the one person who remembers it is the one pressing Build.
//
// Every patch here is diagnostic-only. They suppress deprecation warnings in Unity's own code; none
// changes runtime behaviour, struct layout or ABI. If a Unity upgrade moves an anchor the patch is
// skipped with a warning and the build carries on — a returning compiler warning is not worth
// failing a build over.
//
// Not fixable from here, do not try: the ~24 "libtool: warning: 'xxxx.o' has no symbols" lines in
// the Run Script phase come from inside IL2CPP's own bee/libtool invocation, which the exported
// project does not control.
public static class IosTrampolineWarningPatches
{
    // A literal before/after replacement in one trampoline file.
    private struct Patch
    {
        public string File;     // relative to the exported project
        public string What;     // for the log line
        public string Before;
        public string After;
        public bool Optional;   // true = file belongs to a package that may not be installed
    }

    [PostProcessBuild(1000)] // after IosPlistPostProcessor(999); they touch different files, but keep the order stated
    public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
    {
        if (target != BuildTarget.iOS) return;

        var applied = new List<string>();
        var alreadyPresent = new List<string>();
        var missed = new List<string>();

        foreach (Patch patch in Patches())
            Apply(pathToBuiltProject, patch, applied, alreadyPresent, missed);

        PatchRenderingHeader(pathToBuiltProject, applied, alreadyPresent, missed);

        UnityEngine.Debug.Log(
            $"[iOS] Trampoline warning patches: {applied.Count} applied, " +
            $"{alreadyPresent.Count} already present, {missed.Count} skipped." +
            (applied.Count > 0 ? "\n  applied: " + string.Join(", ", applied) : ""));

        foreach (string m in missed)
            UnityEngine.Debug.LogWarning(
                $"[iOS] Trampoline warning patch skipped — anchor not found and fix not already " +
                $"present: {m}. A Unity upgrade probably changed the trampoline; the warning it " +
                $"silenced will be back in Xcode. Harmless to ship, worth re-deriving.");
    }

    private static IEnumerable<Patch> Patches()
    {
        // 'hasAttitudeAndRotationRate' is deprecated in iOS 14.
        //
        // The pop sits between the if-condition and its brace, which reads oddly but is where it has
        // to be: the deprecated use is in the condition, so the pragma has to close after it and
        // before the body. This is the form that was verified to compile clean — don't tidy it.
        yield return new Patch
        {
            File = "Classes/iPhone_Sensors.mm",
            What = "iPhone_Sensors.mm hasAttitudeAndRotationRate",
            Before =
@"    bool gotRotationData = false;
    if (motion.hasAttitudeAndRotationRate)
    {",
            After =
@"    bool gotRotationData = false;
    #pragma clang diagnostic push
    #pragma clang diagnostic ignored ""-Wdeprecated-declarations""
    if (motion.hasAttitudeAndRotationRate)
    #pragma clang diagnostic pop
    {",
        };

        // 'controllerPausedHandler' is deprecated in iOS 13.
        yield return new Patch
        {
            File = "Classes/iPhone_Sensors.mm",
            What = "iPhone_Sensors.mm controllerPausedHandler",
            Before =
@"    if (controller.controllerPausedHandler == nil)
        controller.controllerPausedHandler = gControllerHandler;",
            After =
@"    #pragma clang diagnostic push
    #pragma clang diagnostic ignored ""-Wdeprecated-declarations""
    if (controller.controllerPausedHandler == nil)
        controller.controllerPausedHandler = gControllerHandler;
    #pragma clang diagnostic pop",
        };

        // 'UnityRegisterRenderingPluginV5' is deprecated, renamed to UnityRegisterPlugin. Suppressed
        // rather than renamed so nothing about the call changes. Optional: the file ships with the
        // Mock HMD XR package, which nothing here requires.
        yield return new Patch
        {
            File = "Libraries/com.unity.xr.mock-hmd/Runtime/ios/UnityMockHMD.m",
            What = "UnityMockHMD.m UnityRegisterRenderingPluginV5",
            Optional = true,
            Before = @"    UnityRegisterRenderingPluginV5(UnityPluginLoad, NULL);",
            After =
@"    #pragma clang diagnostic push
    #pragma clang diagnostic ignored ""-Wdeprecated-declarations""
    UnityRegisterRenderingPluginV5(UnityPluginLoad, NULL);
    #pragma clang diagnostic pop",
        };
    }

    private static void Apply(string root, Patch patch,
        List<string> applied, List<string> alreadyPresent, List<string> missed)
    {
        string path = Path.Combine(root, patch.File);
        if (!File.Exists(path))
        {
            if (!patch.Optional) missed.Add(patch.What + " (file not found)");
            return;
        }

        string text = File.ReadAllText(path);

        if (text.Contains(patch.After)) { alreadyPresent.Add(patch.What); return; }
        if (!text.Contains(patch.Before)) { missed.Add(patch.What); return; }

        File.WriteAllText(path, text.Replace(patch.Before, patch.After));
        applied.Add(patch.What);
    }

    // --- UnityRendering.h -------------------------------------------------------------------------
    //
    // 'useCVTextureCache' is deprecated, reported while compiling UnityView.mm and DisplayManager.mm.
    // No named use triggers it: it fires on the implicit struct copy clang synthesises when
    // RenderingSurfaceParams is passed by value, and clang blames the struct definition. Pragmas at
    // the use sites do not suppress it, and neither does a file-level pragma in the .mm files. The
    // only source-level fix is dropping the attribute in the header. It is diagnostic-only, on
    // fields Unity's own comment describes as kept "only to avoid breaking compilation".
    //
    // SCOPED TO SIX NAMED FIELDS ON PURPOSE. The obvious implementation — strip every
    // __attribute__((deprecated)) in the file — is wrong twice over. This header holds ten more of
    // them in UnityDisplaySurfaceMTL, behind UNITY_DISPLAY_SURFACE_MTL_BACKWARD_COMPATIBILITY, which
    // are a live deprecation Unity uses to push plugin authors off those fields; nothing here needs
    // them silenced. And because those ten always remain, "the file still contains
    // __attribute__((deprecated))" can never mean "not yet patched" — an idempotence check written
    // that way reports the patch missing on every build and reapplies it forever.
    private static readonly Regex DeprecatedOnTargetFields = new Regex(
        @"(\b(?:systemDepthBuffer|useCVTextureCache|cvTextureCache|cvTextureCacheTexture|cvPixelBuffer)\b[^;\r\n]*?)" +
        @"\s*__attribute__\(\(deprecated\)\)\s*;",
        RegexOptions.Compiled);

    private static void PatchRenderingHeader(string root,
        List<string> applied, List<string> alreadyPresent, List<string> missed)
    {
        const string what = "UnityRendering.h useCVTextureCache et al";

        string path = Path.Combine(root, "Classes/Unity/UnityRendering.h");
        if (!File.Exists(path)) { missed.Add(what + " (file not found)"); return; }

        string text = File.ReadAllText(path);
        int hits = DeprecatedOnTargetFields.Matches(text).Count;

        // Nothing to strip. Either it is done, or the fields themselves are gone — tell those apart,
        // because the second one means the header changed shape and this patch needs re-deriving.
        if (hits == 0)
        {
            if (text.Contains("useCVTextureCache")) alreadyPresent.Add(what);
            else missed.Add(what + " (target fields not in this header)");
            return;
        }

        File.WriteAllText(path, DeprecatedOnTargetFields.Replace(text, "$1;"));
        applied.Add($"{what} ({hits} attributes)");
    }
}
#endif
