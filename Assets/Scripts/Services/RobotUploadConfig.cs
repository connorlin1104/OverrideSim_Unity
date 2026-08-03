using UnityEngine;

// Where in-game robot submissions are sent.
//
// The submission flow is deliberately developer-in-the-loop: a player's FBX/URDF lands in cloud
// storage, Connor sets it up in the Unity editor (collider generation, the drivetrain rig and the
// mechanism joints are all Editor-only APIs), and the finished robot is published back to the same
// project the app syncs its catalog from. Nothing about an upload can be processed in the app.
//
// Backend is Firebase, driven over plain REST with UnityWebRequest — no SDK, no extra packages.
// Anonymous Auth gives each device a stable uploader id; Storage takes the file.
//
// The web API key is NOT a secret — Firebase web keys are public identifiers, and access is decided
// by Storage Rules — so it is fine to keep here in the project.
//
// Created (empty) by Tools > RoboSim > Scenes > Build Home Screen. Fill it in from the Firebase
// console: Project settings > General for the key and bucket.
[CreateAssetMenu(menuName = "RoboSim/Robot Upload Config", fileName = "RobotUploadConfig")]
public class RobotUploadConfig : ScriptableObject
{
    [Tooltip("Firebase Storage bucket, e.g. overridesim.appspot.com (Storage tab in the console).")]
    public string storageBucket;

    [Tooltip("Firebase Web API key (Project settings > General). Public by design — access is " +
             "controlled by Storage Rules, not by hiding this.")]
    public string webApiKey;

    [Tooltip("Reject files larger than this. Robot FBX files in this project run 100-200 MB, so a " +
             "generous cap is realistic — but a phone upload that size takes a while.")]
    public int maxUploadMegabytes = 250;

    // NOTE: a `turnaroundNote` string used to live here, shown on the submit screen so players knew
    // what happens after they send a robot. It is gone: the screen says it in its own hint now, and
    // a second copy of the same sentence sat right above the Back button. A config knob whose text
    // nothing reads is worse than no knob — someone edits it and nothing changes.

    // Everything needed to actually upload. When false the submit screen still works as far as
    // picking a file, then says the destination hasn't been set up yet rather than failing mid-upload.
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(storageBucket) && !string.IsNullOrWhiteSpace(webApiKey);
}
