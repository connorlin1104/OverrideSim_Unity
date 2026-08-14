#if UNITY_IOS
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using System.IO;

// Writes the three Info.plist keys an iOS build needs that Unity's Player Settings has no field for.
//
// All three used to be "remember to do it in Xcode after every build", which is the kind of step
// that survives exactly as long as the person who knows about it is the one pressing the button. A
// build made from a clean checkout, or on CI, or by anyone else, silently lost all three — and two
// of the three fail in ways you don't see until App Review does.
public static class IosPlistPostProcessor
{
    [PostProcessBuild(999)] // late, so nothing else overwrites what we set
    public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
    {
        if (target != BuildTarget.iOS) return;

        string plistPath = Path.Combine(pathToBuiltProject, "Info.plist");
        var plist = new PlistDocument();
        plist.ReadFromFile(plistPath);
        PlistElementDict root = plist.root;

        // --- Export compliance -------------------------------------------------------------------
        //
        // US export law (EAR Category 5 Part 2) treats encryption as controlled technology, so App
        // Store Connect asks about it on EVERY build upload. This app only makes HTTPS calls through
        // the OS's own networking (Firebase Storage, via UnityWebRequest) and links no cryptography
        // library of its own, which is the standard exemption — so the honest answer is "exempt",
        // and this key IS that answer, recorded where Apple reads it off the binary.
        //
        // Without it the build lands in App Store Connect flagged "Missing Compliance" and cannot be
        // submitted or sent to a TestFlight tester until someone answers the prompt by hand. With
        // it, the prompt never appears.
        //
        // FALSE IS A LEGAL DECLARATION, AND IT COVERS THIRD-PARTY CODE TOO. It is true today because
        // nothing in this project ships a crypto implementation. If a plugin or SDK is ever added
        // that does its own encryption — not just HTTPS — this key has to be re-examined rather than
        // inherited.
        root.SetBoolean("ITSAppUsesNonExemptEncryption", false);

        // --- The file picker's documents folder ---------------------------------------------------
        //
        // Submit a Robot has no native file picker (see Docs/Robot-Submissions.md): RobotFilePicker
        // lists the app's own Documents folder and the player copies their CAD in from the Files
        // app. These two keys are what puts that folder in Files in the first place.
        //
        // Without them the folder does not exist as far as Files is concerned, so there is no way to
        // get a file in, and "Choose File" shows an empty list forever. It fails as a feature that
        // does nothing rather than as an error, which is the worst way for it to fail — a reviewer
        // taps it, sees nothing, and files it as broken.
        root.SetBoolean("UIFileSharingEnabled", true);
        root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);

        plist.WriteToFile(plistPath);

        UnityEngine.Debug.Log(
            "[iOS] Info.plist: ITSAppUsesNonExemptEncryption=false, UIFileSharingEnabled=true, " +
            "LSSupportsOpeningDocumentsInPlace=true");
    }
}
#endif
