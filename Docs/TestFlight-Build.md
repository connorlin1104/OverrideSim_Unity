# TestFlight build

Getting build 4 onto the phone and running the two on-device tests that
`App-Store-Submission.md`'s pre-flight checklist still has open.

- TestFlight is the only route — the phone is restricted, so Xcode-to-device install and anything
  needing a cable (Finder file sharing, Developer Mode) is off the table
- **Run the tests in this order: Submit-a-Robot first, airplane launch second.** Not obvious, and
  the checklist does not say it — `HomeScreenController.CheckInbox()` returns early when
  `RobotUploadService.UploaderId` is empty, so on a phone that has never submitted, the inbox fetch
  the airplane test is meant to prove silent **never fires**. Submit first and the id exists.

---

## Why this build is a Replace, not an Append

`~/OverrideSim_iOS_Build` is from 22-23 Jul and predates four separate changes:

| In that folder's `Info.plist` | Now |
| --- | --- |
| `CFBundleDisplayName` = `OverrideSim` | `RoboSimL` — and that string *is* the folder name you look for in Files |
| `CFBundleShortVersionString` = `0.1.0` | `1.0.0` |
| none of `UIFileSharingEnabled`, `LSSupportsOpeningDocumentsInPlace`, `ITSAppUsesNonExemptEncryption` | all three, written by `IosPlistPostProcessor` — that build predates the script |
| `NSCameraUsageDescription` "…photos for your in-game profile", `NSMicrophoneUsageDescription` "…voice chat" | both cleared (`ProjectSettings.asset:589-591`) — they were false purpose strings, a 5.1.1 flag |

`VEX-Robotics-Arm_0.jpg` is also still sitting in that project's `AppIcon.appiconset`. It is inert
(`Contents.json` does not reference it) but it is the tell that the folder is stale.

- **Replace this once.** Costs a full IL2CPP compile, ~10-25 min
- **Append every build after this one.** That is the fast path, and it is correct once the folder
  is not carrying a stale plist forward

---

## A. Unity

1. Switch the active platform to **iOS** — required, see *Platform switch* below
2. Player Settings -> Other Settings -> Identification: Version `1.0.0`, Build `3` -> `4` ✅ done
3. **File -> Save Project** — Player Settings live in memory until you do
   ```
   grep -n "bundleVersion" ProjectSettings/ProjectSettings.asset
   grep -n -A5 "buildNumber:" ProjectSettings/ProjectSettings.asset      # want iPhone: 4
   ```
4. Build Profiles -> iOS -> **Build** -> `~/OverrideSim_iOS_Build` -> **Replace**

### Platform switch

You cannot build iOS from an active macOS target, and there is a second reason that matters more:

- `IosPlistPostProcessor.cs` is wrapped in `#if UNITY_IOS`. That define only exists while iOS is the
  **active** build target. Build with macOS active and the file is not compiled, the post-processor
  does not run, and all three Info.plist keys are silently missing — which is exactly the failure
  the script's own header comment was written to prevent
- Build Profiles -> iOS -> **Switch Profile** (button is where **Build** will be once it is active)
- Cost is small here: 4 textures and 6 FBX in `Assets/`. Minutes, not the hour a texture-heavy
  project would take. The long part is the first IL2CPP compile in step 4

### Build-time toggles

| Setting | Want | Why |
| --- | --- | --- |
| Development Build | **off** | brings the profiler, a watermark and a larger binary; everything nested under it (Autoconnect Profiler, Deep Profiling, Script Debugging, Wait For Managed Debugger) is then moot |
| Symlink Sources | **off** | points the Xcode project at your Unity install instead of copying — the archive is not distributable |
| Run in Xcode as | Release | Archive uses Release regardless, but do not leave it on Debug |
| Scenes In Build | HomeScene **first**, SampleScene, LiteScene, all ticked | HomeScene at index 0 is the app's entry point |

---

## B. Verify the output before you archive

Thirty seconds here saves a 30-minute round trip — every one of these fails *after* the upload
finishes, at processing or in review.

```
plutil -p ~/OverrideSim_iOS_Build/Info.plist | grep -E "UIFileSharing|LSSupportsOpening|ITSApp|CFBundleDisplayName|CFBundleShortVersion|CFBundleVersion|UsageDescription"
ls ~/OverrideSim_iOS_Build/Unity-iPhone/Images.xcassets/AppIcon.appiconset/
```

- Want: `UIFileSharingEnabled => 1`, `LSSupportsOpeningDocumentsInPlace => 1`,
  `ITSAppUsesNonExemptEncryption => 0`, `CFBundleDisplayName => RoboSimL`,
  `CFBundleShortVersionString => 1.0.0`, `CFBundleVersion => 4`
- Want **no** Camera or Microphone usage description lines
- Want **no** VEX jpg in the appiconset
- `UIFileSharingEnabled` missing means test E is impossible — there is no folder to copy into, and
  Choose File shows an empty list forever with no error

The icon needs nothing: `Assets/Icons/AppIcon.png` is 1024x1024 with **no alpha**, which is the
thing that gets an upload rejected at processing. It fills 4 slots plus the project Default Icon.
`AppIcon-Dark.png` is unassigned and optional.

---

## C. Archive and upload

1. Open `~/OverrideSim_iOS_Build/Unity-iPhone.xcodeproj`
2. Destination menu -> **Any iOS Device (arm64)**. Archive is greyed out on any simulator destination
3. **Unity-iPhone** target -> Signing & Capabilities -> Automatically manage signing, team
   **Ansis Atteka (F4BSZ5A7JP)** — the account holder's team, not a personal one. Confirmed
   against the cached profiles: that team already holds a live Store provisioning profile for
   `com.connorlin.overridesim`, so the App ID is registered and distribution-ready. Same team on
   the `UnityFramework` target if it complains
4. **Product -> Archive** — 5-15 min
5. Organizer opens -> **Distribute App -> App Store Connect -> Upload** -> accept automatic signing
6. Processing 5-20 min, email on completion
   - **No export-compliance prompt** — `ITSAppUsesNonExemptEncryption` answers it off the binary
   - "build number already used" -> bump the number, save, rebuild, re-archive. **Bump it in Unity**
     (Player Settings -> Identification -> Build), then **File -> Save Project**. Bumping it in
     Xcode looks like it worked and is undone by the next export: Unity rewrites `Info.plist` from
     Player Settings every build, Append included, which is the same reason
     `IosPlistPostProcessor` has to re-add its keys each time. A number already uploaded cannot be
     reused, so skip past every one you spent in Xcode
   - Uploading does **not** submit for review

---

## D. Install

1. App Store Connect -> RoboSimL -> **TestFlight** -> the build appears once processed
2. **Internal Testing** -> add yourself as a tester. Internal testers skip Beta App Review, so it is
   available immediately
3. TestFlight app on the phone -> Install
4. **Launch it once and back out.** `Application.persistentDataPath` is created on first run, so the
   `RoboSimL` row does not exist in Files until the app has run

---

## E. Test 1 — Submit a Robot, end to end

**Getting the file across without a cable:** AirDrop `Assets/Models/360 RPM Drivetrain.fbx` (15 MB,
the smallest real robot) from the Mac -> **Save to Files** -> **On My iPhone -> RoboSimL**. iCloud
Drive works too. Either way it must land at the **top level** of `RoboSimL` — `InboxFiles()` uses
`SearchOption.TopDirectoryOnly`, so a file in a subfolder is invisible with no error.

1. Home -> Submit a Robot
2. **Choose File** -> expect `360 RPM Drivetrain.fbx  (15 MB)`, and the robot name field to
   self-populate from the filename
3. Team `TEST`, robot name `TestBot`
4. Tap the sharing row twice — the label cycles and comes back; it is a real control, not a label
5. **Send** -> progress bar travels -> `Sent 15 MB. TestBot is on its way.`
6. Verify in the Firebase console, Storage, under `uploads/<stamp>_TEST_<uploaderId>/`:
   - **both** objects — the FBX *and* the `.json` sidecar
   - a missing sidecar is only a `Debug.LogWarning`; the player still sees success, so the console
     is the only place this shows
7. Delete the test folder afterwards

Failure strings worth recognising:
- "Submitting isn't switched on in this build yet." -> the config asset lost its scene reference.
  It is wired today (`RobotUploadConfig.asset` guid `6c85f27a…`, referenced twice in `HomeScene`)
- "No robot files found…" -> the file is not at the top level of `RoboSimL`

---

## F. Test 2 — airplane launch

1. **Force-quit the app first** — `CheckInbox()` and `CheckForPublishedRobots()` are in `Start()`,
   so a resumed app proves nothing
2. Airplane mode on, and check **Wi-Fi is actually off** — iOS remembers Wi-Fi across airplane mode
3. Launch. Want:
   - home screen, no error banner, no spinner that never resolves
   - the model picker fully populated — every catalog entry is `remote: 0`, so all shipping robots
     are compiled in and must appear offline
   - Drive works, field loads
   - type a nonsense owner code -> **"No robot uses that code."**, promptly. Not a stuck "Checking…"
4. Sit on the home screen ~90 s. Nothing should appear late

### What this does and does not prove

Airplane mode exercises the `NetworkReachability.NotReachable` guard in `RobotCatalogSync` and
`RobotInboxService` — both return early without a request. It does **not** exercise the path where
a request fires and fails, which is what the review notes promise ("fully usable offline and on a
restricted network").

- The strict test is Network Link Conditioner at 100% Loss, under Settings -> Developer. **Not
  available** — Developer Mode needs a Mac connection
- Substitutes: join a captive-portal Wi-Fi and do not sign in, or run Internet Sharing on the Mac
  from an unplugged Ethernet port
- If neither is convenient, plain airplane mode satisfies the checklist item as written

---

## Settings verified 2026-09-08

Read out of `ProjectSettings/ProjectSettings.asset` — no action needed, listed so a later change
that breaks one is visible as a diff.

| Setting | Value | Note |
| --- | --- | --- |
| `targetDevice` | `2` (iPhone + iPad) | **this is why the iPad 13" screenshots are required.** Change it to iPhone-only and that row drops off — but decide before you archive, it changes the binary |
| `iOSTargetOSVersionString` | `15.0` | |
| `appleEnableAutomaticSigning` / `appleDeveloperTeamID` | `1` / `F4BSZ5A7JP` | **Ansis Atteka's team.** Not changeable to a personal one — Apple Developer Program enrollment needs 18+, and the App ID and the App Store Connect record both live under this team. A different team fails the upload, and the bundle id is frozen once any build is uploaded |
| `camera` / `location` / `microphone` / `bluetoothUsageDescription` | all empty | no purpose string ships, so no unused-permission question |
| `iOSRequireARKit` | `0` | and iPhone XR loaders are `m_Loaders: []` — ar-foundation is in the project but nothing ARKit links or is required |
| `stripEngineCode` | `1` | |
| orientation | landscape left + right only | so the screenshots must be landscape too |
| `useOnDemandResources` | `0` | no ODR to configure |
| `bundleVersion` / `buildNumber.iPhone` | `1.0.0` / `4` | |

---

## After the tests

Tick the two boxes in `App-Store-Submission.md`'s pre-flight checklist with what you actually saw,
not with "done".
