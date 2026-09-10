# App Store submission

Everything App Store Connect asks for, filled in, plus what will fail review if it ships as-is.
Copy fenced blocks verbatim. `<ANGLE_BRACKETS>` = a decision only you can make.

- **Done, nothing further needed** — support + privacy URLs, App Privacy questionnaire, export
  compliance, the three Info.plist keys, screenshot capture at Apple's sizes without a device
- **All blockers closed** — the name sweep landed 2026-09-02 (commit `7b2f324`: app, scene,
  Player Settings, web pages deployed; App Store Connect fields re-entered)
- **Next** — screenshots are captured and both on-device tests have passed (2026-09-09). What is
  left is the robot-rights question at the bottom of the checklist, and pressing Submit

---

## Blockers

- **3. Name sweep — DONE 2026-09-02.** Everything below is the record of what changed
  - The store listing, the app and the pasted listing copy all say `RoboSimL` now
  - Not urgent until submission (Apple only needs the two names to be *recognisably* related), but
    a mismatch is a guideline 2.3.7 flag and reads as unfinished

  - **In App Store Connect** — the fields you already pasted still contain the old name:
    - Description — re-paste the block below; it is now name-agnostic ("A driving practice tool
      for competition robotics teams…"), so it needs no edit after the name changes again
    - App Review notes — same, re-paste; the block below opens "This is a single-player…" and says
      "the app's folder in Files" rather than naming it
    - Promotional text and subtitle — check, neither contains the name
    - The app Name field itself

  - **In the project** — user-visible, must change:
    - `ProjectSettings.asset` -> `productName` (home screen label, and the Files-app folder name)
    - `Assets/Scenes/HomeScene.unity` -> the title text object
    - `Assets/Scripts/Editor/Scenes/BuildHomeScene.cs` -> the title string
    - `Web/index.html` -> `<title>`, meta description, `<h1>`, the "RoboSim folder" instruction
    - `Web/privacy.html` -> `<title>`, meta description, tagline, opening sentence
    - Then `firebase deploy --only hosting` (URLs do not change)

  - **Internal — leave alone:** the `RoboSim >` editor menu paths, the `RoboSim.*` pref keys, the
    project folder, the other files in `Docs/`. None are user-visible.

  - Edit the scene **and** the builder. `HomeSceneIsValid()` treats a scene with the right objects
    as up to date, so a text-only change never triggers a rebuild and the old title keeps shipping.

- **Done:** ~~app icon~~ (`AppIcon.png` assigned, VEX jpg deleted) · ~~iPad icon slots~~ ·
  ~~`microphoneUsageDescription`~~ · ~~`bundleVersion` 1.0.0~~ · ~~`companyName`~~

  - **Verify these were flushed to disk.** Unity keeps Player Settings in memory and writes
    `ProjectSettings.asset` on **File -> Save Project**. Until it does, the changes exist only in
    the editor and are not in git. Check with:

    ```
    grep -n "companyName\|bundleVersion" ProjectSettings/ProjectSettings.asset
    grep -c e2cf615d243674b8bab3366a56b8b6a8 ProjectSettings/ProjectSettings.asset   # want 0
    ```

    That guid is the deleted VEX jpg. If it is still referenced, the icon slots point at an asset
    that no longer exists — which fails the build rather than shipping a bad icon, but either way
    it has to be 0 before you upload.

---

## Choosing the name

**Current: `RoboSimL` (8 / 30).** Fine to ship. Revisit only if you want to.

- Names are globally unique across every app record, including ones **reserved but never published**
  - That is why `RoboSim` failed even though no app called RoboSim exists in the store
  - So availability cannot be checked from outside — the App Store Connect field is the only oracle
  - Work down a ranked list rather than betting on one idea
- Candidates, roughly in order:

| Name | Chars | Note |
| --- | --- | --- |
| `RoboSim Driver` | 14 | Safest. Keeps the brand already in the codebase; a second word usually clears a reservation the bare name could not. |
| `RoboSim: Driver Practice` | 24 | Better for search — "driver practice" is a phrase teams type. Long, and the colon reads like a misplaced subtitle. |
| `RoboSim Field` | 13 | Works. "Field" earns nothing in search. |
| `Drive Team` | 10 | The real term for the driver-and-coach pair, but "drive" collides with rideshare and trucking apps. |

- Ruled out after checking the store — **Sprocket** (HP owns the results), **Pit Bay** (taken),
  **Freespin** (buried under casino apps), **RoboPilot** (taken), **Driver Practice** (drowns in DMV
  permit-test apps; worst option for search)
- Whatever you pick, changing it is blocker 3 — see the file list there

---

## App Information

| Field | Value |
| --- | --- |
| Name | `RoboSimL` (8 / 30). Fine to ship; see **Choosing the name** if you want to revisit. |
| Subtitle | `Drive your own custom robot` (27 / 30) |
| Bundle ID | `com.connorlin.overridesim` — **keep it.** A bundle id cannot be changed once a build has been uploaded, it is never shown to users, and it does not have to match the app name. |
| Developer name | **`Ansis Atteka`** — shown on the product page under the app name, and **not editable**. The app ships on his individual Apple Developer team (`F4BSZ5A7JP`) because Program enrollment requires 18+; an individual account always displays the holder's legal name. Changing it means an Organization account, which needs a D-U-N-S number and a legal entity — post-1.0 at the earliest. Not a review risk, just don't be surprised by it on the live listing. |
| SKU | `ROBOSIM-IOS-001` (internal only, never shown) |
| Primary language | English (U.S.) |
| Primary category | **Games** → subcategory **Simulation**, second subcategory left blank |
| Secondary category | **Education** |
| Content rights | "No, it does not contain, show, or access third-party content" — the VEX icon is gone, so this is now clean. Player-submitted robots are third-party content you host, but they are submitted to you under the in-app sharing choice, which is the licence. If you would rather not argue it, answering "Yes" costs nothing but a rights-confirmation checkbox. |
| Privacy Policy URL | `https://overridesimunity.web.app/privacy` — **live** |
| Price | Free |

Games as the primary category is the right read: a reviewer opens it, drives a robot around a field
and sees a game. Education as secondary keeps it findable by the audience that actually wants it.

Leaving the second subcategory blank is fine — it is optional, and Sports was a stretch anyway. The
only honest alternatives are Sports (competition robotics is played as a sport) and Family; neither
adds much, and a subcategory that does not fit costs credibility in browse rankings. Both can be
changed later without a new build.

---

## Version Information (1.0)

### Promotional text (170 max — editable later without a new build)

Use this one. One line — don't paste the wrap:

```
Drive the robot before you build it. Send your team's 3D model and get it back as a machine you can actually drive — real joints, real weight, real drivetrain.
```

- 159 / 170
- First sentence is the whole pitch; it is the only part most people read
- **It said "Send your CAD" until 2026-09-09.** The app does not take CAD — `AcceptedExtensions`
  is fbx/urdf/zip and the picker will not list anything else — so that line was an instruction a
  reader could follow all the way to a file the app cannot see. "3D model" is the version that
  stays true for a browsing reader; the description is where the format gets named outright
- No adjectives and no claim that the app is exciting — that is what made the old one read as an ad
- Alternates, if you want a different angle:
  - Driver-practice angle (141) — `Put hours on the sticks before the robot exists. Send your team's CAD and drive it on a full field — same joints, same mass, same drivetrain.`
  - Design-iteration angle, lands harder with older students (155) — `Test the design before you cut a single piece. Send your CAD, drive it on a full field, and find out how it handles while there is still time to change it.`

### Description (4000 max)

```
A driving practice tool for competition robotics teams.

Every robot in the app began as a team's own CAD. Each one is rebuilt part by part — real joints,
real pivots, real drivetrain geometry — so what you drive on your phone moves the way the machine on
the field moves.

DRIVE
Twin on-screen sticks and a set of mechanism buttons. Pick a robot, tap Drive, and you are on a
full-size field with cups, pins and stakes to move.

MECHANISMS THAT WORK LIKE THE REAL ONES
Claws open and close and actually hold a game piece. Cascade lifts and double reverse four-bars run
through their real travel. Pneumatic cylinders snap between two positions the way a solenoid does,
in the real 20 / 50 / 90 mm stroke classes. Intakes pull pieces in. Nothing here is an animation —
each mechanism is a physics joint being driven by a motor model.

PHYSICS TUNED AGAINST THE MACHINE
Wheel speed is set from the drivetrain's real free-spin RPM. Slam the sticks into reverse and the
robot plows to the traction limit instead of stopping dead. Raise a lift and the robot rolls in
turns, exactly as a tall robot does. Robots have real mass, and a heavy arm out front changes how
the whole thing drives.

CONTROLS YOU CAN MAKE YOURS
Resize the sticks, change their opacity, drag every button where your thumbs actually are, and
reassign what each one does. Switch a mechanism between one-button toggle and two-button hold. Set
drive and turn sensitivity. Choose which end of the robot the sticks treat as the front.

YOUR ROBOT IN THE APP
Send us your robot from inside the app — an FBX exported out of your CAD, or a URDF — and we will
build it into a drivable robot and send it back to you. Choose whether it is listed for everyone or unlocked only by a code you pass to your
own team. It takes a few days and we tell you when it is done — or tell you what to re-export if
the file cannot be made to drive.

NO ACCOUNT, NO ADS, NO PURCHASES
There is nothing to sign up for and nothing to buy. Your settings never leave your device. The app
works offline.

Not affiliated with, endorsed by, or sponsored by VEX Robotics, Innovation First International, or
the REC Foundation. Robot designs remain the property of the teams that built them.
```

### Keywords (100 max, comma-separated, no spaces)

**Pick the row that matches the name you end up with** — Apple already indexes every word in the
name and subtitle for free, so a keyword repeating one of them is spent for nothing.

| If the name contains… | Keywords | Chars |
| --- | --- | --- |
| neither "practice" nor "driver"<br>(`RoboSimL`, `Drive Team`, `RoboSim Field`) | `robotics,simulator,driver,drivetrain,claw,lift,pneumatic,practice,stem,competition,cad,physics,team` | 99 |
| **"Practice"**<br>(`Practice Bot`, `Practice Robot`) | `robotics,simulator,driver,drivetrain,claw,lift,pneumatic,stem,competition,cad,physics,team,joystick` | 99 |
| **"Driver"**<br>(`RoboSim Driver`, `RoboSim: Driver Practice`) | `robotics,simulator,drivetrain,claw,lift,pneumatic,practice,stem,competition,cad,physics,team,chassis` | 100 |

No spaces after the commas — a space costs a character and buys nothing.

Notes on what is in and what is not:

- **"competition" replaced "engineering".** The old subtitle said "competition robot", so the word
  was already free; the new one says "custom", so it has to be bought back. It earns its 11
  characters because Apple builds phrases out of keyword pairs, and "robotics competition" is what
  this audience actually types.
- **Plain "robot" is absent, "robotics" is present.** "Robot" is in the subtitle already.
- **No "VEX", and no "FRC" / "FTC" / "FLL" either.** All are someone else's trademark, and putting a
  competitor's or a governing body's mark in the keyword field is a named rejection reason, not a
  grey area.
- **"stem" and "cad" are cheap and specific** — four and three characters for terms that describe
  the exact buyer. Keep them through any future edit.

### URLs

| Field | Value |
| --- | --- |
| Support URL | `https://overridesimunity.web.app/` — **live** |
| Marketing URL | Leave blank. Optional, and the support page already serves that purpose. |

### Copyright

```
2026 Connor Lin
```

### Screenshots

Landscape only (the app is landscape-locked).

| Device | Size | Count |
| --- | --- | --- |
| iPhone 6.9" | 2868 x 1320 | 3-10 (do at least 4) |
| iPad 13" | 2752 x 2064 | 3-10 — required, iPad is a supported device |

- Shoot, in this order: robot mid-drive with controls visible; claw holding a cup; lift raised;
  the controls config screen; the robot picker
  - A shot showing the on-screen controls beats a pretty render — controls are what a reviewer looks for
- App previews (video) are optional. Skip for 1.0.

**Capturing both sizes with no device and no Xcode simulator:**

1. Game view -> resolution dropdown -> `+` -> add `Fixed Resolution` `2868 x 1320` and `2752 x 2064`
2. **Set the Scale slider to 1x** — above 1x Unity renders at the window's size, not the target's,
   and you get a correctly-framed shot at the wrong pixel count
3. Enter Play mode, drive to the shot, press **Cmd+Shift+S**
   (or **Tools -> RoboSim -> Utilities -> Capture Game View**)

- Output: `StoreScreenshots/` beside `Assets/`, named for the size captured
- A capture that misses an accepted size is named `WRONG-SIZE` and warns in the console
- Why this is valid, not a shortcut:
  - Nothing reads `Screen.safeArea`, and the UI is one `ScaleWithScreenSize` canvas
    (1920x1080 ref, match 0.5) — layout is a pure function of render resolution
  - So rendering at 2752x2064 gives the pixels an iPad gives
  - It also means you **cannot** resize an iPhone shot: at 4:3 the match-0.5 scaler picks a
    different scale factor than at 19.5:9, so it would show a layout the app never displays
  - If safe-area handling is ever added this silently stops being true — noted in
    `StoreScreenshotCapture.cs`
- Alternatives, for the record:
  - Borrow an iPad — works, only needed if you want the OS chrome, which Apple does not require
  - `targetDevice: 1` (iPhone only) — drops the requirement, but a tablet is the better driving
    surface; wrong trade for a screenshot
  - Xcode simulator — worst option: large download, and a Unity IL2CPP build will not run in it
    without changing the SDK and architecture first

---

## App Review Information

| Field | Value |
| --- | --- |
| Sign-in required | **No** |
| First / last name | Connor Lin |
| Phone | `<PHONE>` |
| Email | `<REVIEW_EMAIL>` (not published — Apple's contact for you) |
| Attachment | None needed |

### Notes

```
This is a single-player robot driving simulator for school robotics teams. There is
no account, no sign-in, and nothing to purchase. Tap Drive and you are on the field.

WHAT TO TEST
1. Home > Drive. The left stick drives, the right stick turns, and the buttons on the right run the
   robot's mechanisms (claw, lift, intake). Drive into the cups and pins on the field and pick one
   up with the claw.
2. Settings > Robot chooses which robot to drive. Settings > Controls resizes, re-binds and
   repositions the on-screen controls.
3. Everything else is optional and is explained below.

NO USER ACCOUNTS
The app has no sign-in and no user accounts. Firebase Anonymous Authentication is used only to
authorise a file upload in the optional "Submit a Robot" flow. It is created silently at that
moment, contains no personal data, and is never surfaced to the user.

"ROBOT CODES" ARE NOT ACCOUNTS
Settings > Account holds short codes we issue to a team after we have set their robot up; entering
one adds that robot to the picker. A code reaches the team either by email or as a one-line notice
inside the app, read from the inbox described under NETWORK USE below. A code is a capability, not a
login. No code is required to use the app, and every robot present at launch is drivable without
one.

USER-GENERATED CONTENT (Guideline 1.2)
Players may send us their own robot CAD from Settings > Account > Submit a Robot. Nothing a player
uploads is ever shown to another player automatically. Converting a CAD file into a drivable robot
requires Unity Editor tooling that the app does not contain and cannot contain, so every submission
is downloaded, opened and rebuilt by hand by the developer before it can appear in the app. 100% of
in-app content is therefore human-reviewed by us prior to publication.

Uploads are private to us — the Cloud Storage rules deny all reads on the upload path — and the app
has no write access to the path robots are published from. There is no chat, no comments, no
profiles, no way for one user to contact another, and no user-visible content stream to moderate.
Abuse or takedown requests reach us at the support address in the listing.

TESTING "SUBMIT A ROBOT" (optional)
The form asks for a team name, an optional robot name, an email address, optional notes, a choice of
who may use the finished robot, and a model file. The app has no native file picker: it lists files
in its own Documents folder, which is exposed to the iOS Files app. To exercise it you would first
need to copy a .fbx, .urdf or .zip file into the app's folder in Files — the form states the path,
Files > On My iPhone (or iPad) > RoboSimL. This flow is not required for any other part of the app
and skipping it affects nothing.

NETWORK USE
On launch the app makes two read-only HTTPS requests to Firebase Cloud Storage: one for the index of
published robots, one for any message addressed to this device's upload ID. Both fail silently. The
app is fully usable offline and on a restricted network.

DATA COLLECTED
Only what a player types into the Submit a Robot form: team name, robot name, email address,
free-text notes, their choice of who may use the finished robot, and the file itself — plus the app
version, the device platform string, the time of submission, and the anonymous upload ID the file is
filed under. It is used to build the robot and to reply. All settings are stored on-device and never
transmitted. There is no analytics SDK, no advertising SDK, and no tracking of any kind.

AUDIENCE
Middle- and high-school robotics teams. No violence, no mature themes, no social features.
```

---

## App Privacy (the questionnaire, answered)

This is a separate section from the listing and it blocks submission. Answer **"Yes, we collect data
from this app"**, then declare exactly three types.

| Data type | Collected? | Linked to identity? | Used for tracking? | Purpose |
| --- | --- | --- | --- | --- |
| **Contact Info → Email Address** | Yes | **Yes** | No | App Functionality (replying about a submission) |
| **User Content → Other User Content** (the CAD file, team name, robot name, notes) | Yes | **Yes** | No | App Functionality |
| **Identifiers → User ID** (the anonymous Firebase upload ID) | Yes | **Yes** | No | App Functionality |

Everything else — Health, Financial, Location, Contacts, Browsing, Search, Purchases, Usage Data,
Diagnostics, Advertising Data, Sensitive Info, Device ID — is **not collected**.

Notes on the two answers people get wrong:

- **"Linked to identity" is Yes.** The email address is a real-world identifier, and it sits in the
  same record as the file and the team name. Do not talk yourself into "No" because there is no
  login — linkage is about whether the data can be tied to a person, not whether you run an account
  system.
- **"Used for tracking" is No**, correctly: nothing is shared with a data broker or joined with
  third-party data for advertising, and there is no ATT prompt.

Do **not** claim the optional-disclosure exemption for the email field. It requires that the data is
not used for personalisation *and* is deleted on request *and* that the collection is prominently
optional — the first two hold, but the exemption is more argument than it is worth here. Declare it.

### Verify before you submit

Unity Analytics, Unity Ads, IAP, Cloud Diagnostics and Performance Reporting are all disabled in
`UnityConnectSettings.asset` — good, and it is why the table above is this short. `m_Enabled: 1` at
the top of that file is Unity Connect itself, not analytics. Confirm nothing re-enables them when
you switch the build target.

---

## Age Rating questionnaire

Answer **None** to every violence, sexual content, profanity, horror, gambling, drug, alcohol and
tobacco question. Then the ones that are not obvious:

| Question | Answer |
| --- | --- |
| Unrestricted web access | **No** |
| Gambling | **No** |
| Contests | **No** |
| User-generated content / user interaction | **No** — see below |
| Messaging / chat | **No** |
| User-generated content sharing to social networks | **No** |
| Location sharing | **No** |

Result: **4+**.

**On the user-generated-content question.** Apple means content users create and share *with each
other inside the app*. Here, a submission goes to the developer, is rebuilt by hand, and is
published by the developer — players cannot post to each other, cannot see each other, and cannot
publish anything themselves. That is authored content, not UGC, so **No** is the honest answer. The
review notes above spell out the whole flow so the answer is never a surprise; that transparency is
what keeps it from looking like a dodged question.

**Do not opt into the Kids Category.** The app is fine for children, but the Kids Category forbids
collecting personal information from children without verifiable parental consent, and the submit
form asks for an email address. 4+ outside the Kids Category is the correct placement and carries
none of that.

---

## Export compliance — nothing for you to do

**Cross it off. No field to find, no form to fill in.**

- **Why you cannot find it:** it attaches to a *build*, not to the app, and you have not uploaded one
  - Not in App Information, not on the version page, not in App Privacy
  - It appears only after a build finishes processing, as a yellow **Missing Compliance** label next
    to that build in TestFlight and in the version page's Build section
- **What it actually asks:** does your app contain encryption the US government wants to know about
  before it leaves the country?
  - US export law (EAR Cat. 5 Pt. 2) classifies strong encryption as controlled technology; Apple
    ships to ~175 countries so it must collect an answer from everyone
  - Nearly every app qualifies, because HTTPS is encryption — so the law exempts software that only
    *uses* the OS's encryption instead of shipping its own
  - This app: HTTPS to Firebase via `UnityWebRequest`, no crypto library of its own -> exempt
  - By hand it would be: uses encryption -> **Yes** -> qualifies for an exemption -> **Yes**
- **Why you will never see it:** `IosPlistPostProcessor.cs` writes
  `ITSAppUsesNonExemptEncryption = false` into Info.plist on every iOS build
  - That key *is* the answer; Apple reads it off the binary and never asks
  - Without it every build and every TestFlight distribution stops and waits for a manual click
- **Caveat:** `false` is a legal self-declaration and it covers third-party code too
  - True today because nothing here ships a crypto implementation
  - If an SDK is ever added that does its own encryption — not merely HTTPS — re-examine it rather
    than inheriting it

---

## The two URLs — live

| | |
| --- | --- |
| Support URL | <https://overridesimunity.web.app/> |
| Privacy Policy URL | <https://overridesimunity.web.app/privacy> |

Source is in `Web/` (`index.html`, `privacy.html`, `style.css`), served by Firebase Hosting from the
`hosting` block in `firebase.json`. Redeploy after any edit with:

```
firebase deploy --only hosting
```

The privacy policy is the only copy — there is no markdown twin, deliberately. Two copies of a
document that must legally match is a drift you would not notice until it mattered.

The domain still says `overridesimunity` because that is the Firebase project id and it cannot be
renamed. It is not user-visible anywhere in the app and Apple does not care. If it bothers you, add
a second Hosting site (`firebase hosting:sites:create robosim`) and point the listing at
`robosim.web.app` instead.

---

## Pre-flight checklist

- **In Unity**
  - [x] ~~App icon assigned, VEX jpg deleted~~
  - [x] ~~`bundleVersion` -> `1.0.0`~~
  - [x] ~~`companyName`~~
  - [x] ~~**File -> Save Project**, so Player Settings actually reach disk and git~~
  - [x] ~~`productName` + home-screen title -> the final name (blocker 3)~~
  - [x] ~~`microphoneUsageDescription` cleared~~
- **Screenshots**
  - [x] ~~iPhone 6.9" — 2868 x 1320~~ — 8 captured 2026-09-09, every file verified at exactly
        2868 x 1320
  - [x] ~~iPad 13" — 2752 x 2064, Game view Scale slider at 1x~~ — 8 captured, all exactly
        2752 x 2064. First real use of `StoreScreenshotCapture`, and the 4:3 layout it renders had
        never been seen before: nothing clips, and the lowest controls clear the bottom edge by
        ~20 px, which is where the home indicator would sit
- **Web** (only once the name is final)
  - [x] ~~`Web/index.html` and `Web/privacy.html` renamed, then `firebase deploy --only hosting`~~
  - [ ] **Redeploy** — `Web/index.html` was edited 2026-09-09 and the live page is still the old
        one. It told players to send `.step` or `.f3d` "if you can", which the app has never
        accepted; anyone who followed it copied a file into the folder and got "No robot files
        found." Run `firebase deploy --only hosting`
  - URLs do not change, so nothing gets re-entered in App Store Connect
- **App Store Connect**
  - [x] ~~Final name entered~~
  - [x] ~~Description and review notes re-pasted (the ones you pasted name the app `RoboSim`)~~
  - [x] ~~Keyword row matching that name — the three lists differ~~
  - [x] ~~Privacy Policy URL + Support URL~~
  - [x] ~~App Privacy questionnaire~~
  - [ ] **Re-paste three blocks changed 2026-09-09** — promotional text and the description both
        said "send your CAD", and the review notes were behind the app on two points: how a robot
        code reaches a team (in-app now, not email only) and what the sidecar actually carries.
        Free to edit now; after 1.0 is live, description edits need a new version
- **Test on device before submitting** — the whole procedure, build to result, is
  `Docs/TestFlight-Build.md`. TestFlight is the only route to the phone; run these **in this order**
  - [x] ~~Submit-a-Robot end to end~~ — 2026-09-09, from the phone on TestFlight. Two FBX sent under
        different names; both landed in `uploads/` with their `.json` sidecars. The **inbox reply
        channel was proven at the same time**, which the checklist never asked for and should have:
        an arrival and a note written to `inbox/<uploaderId>.json` both appeared on the home screen,
        and the button entered the code. See `Robot-Submissions.md` for the one trap — an arrival
        whose code no robot in the installed build uses is dropped in silence
  - [x] ~~Launch in airplane mode~~ — 2026-09-09, run by Connor on the TestFlight build after
        the submit test, so the uploader id existed and the inbox fetch was really exercised.
        Reported working; no failure or hang seen
  - [ ] Every robot in the shipping build is one you have the right to ship. Four ship:
        `360 RPM Drivetrain`, `654V v1` (private, code `654V-1104`), `654V v2`, and `654V v3`
        — whose catalog id is `ryan-cascaderobot`. That last one is the only entry naming
        somebody else, so it is the one to be sure about. Content rights in **App Information**
        is answered "No third-party content", which is the answer that assumes all four are
        yours or your team's to publish

Needs nothing from you: export compliance, and the three Info.plist keys.
