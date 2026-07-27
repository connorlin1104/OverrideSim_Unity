# Robot submissions

How a player's robot gets from their computer into OverrideSim, and what has to be switched on for
the in-game **Settings → Submit a Robot** screen to work.

## Why it's developer-in-the-loop

The app cannot set up a robot by itself, and no amount of work will change that. Three separate parts
of the pipeline are Unity Editor APIs that do not exist in a built player:

- **Importing the model.** Turning an FBX or URDF into Unity meshes is an editor importer.
- **Generating colliders.** The V-HACD convex decomposition runs through
  `Assets/Plugins/Editor/librobosim_vhacd.dylib`, which is Editor-gated and macOS-arm64 only.
- **Saving the result.** `PrefabUtility.SaveAsPrefabAsset` and `AssetDatabase` have no runtime
  equivalent, and `RobotModelCatalog.Entry.prefab` is a direct serialized reference — a robot that
  didn't exist when the app was built cannot be spawned.

On top of that, marking each moving part and assigning roles for a claw or lift is a judgement job.
So the flow is: player uploads → you set it up in the editor → it ships in the next update.

## Switching submissions on

1. Create a Firebase project. Enabling Storage requires the **Blaze** (pay-as-you-go) plan — Spark
   no longer includes it — so read *What it can cost* below before turning it on.
2. Enable **Authentication → Sign-in method → Anonymous**. The first uid a device is given is kept in
   PlayerPrefs and reused as the folder name for every later submission, so one player's robots stay
   together.
3. Enable **Storage**, and set rules so the app can write submissions and read only the inbox:

   ```
   rules_version = '2';
   service firebase.storage {
     match /b/{bucket}/o {
       match /uploads/{uid}/{file} {
         allow write: if request.auth != null
                      && request.resource.size < 250 * 1024 * 1024;
         allow read: if false;
       }
       match /inbox/{file} {
         allow read: if true;
         allow write: if false;
       }
     }
   }
   ```

   The write rule checks only that the caller signed in, **not** that the folder matches their uid.
   That is deliberate: anonymous sign-up mints a brand-new uid on every call, so a player who
   restored their id on a new device signs in as someone else entirely and would be locked out of
   their own folder. Anyone can obtain an anonymous sign-in, so treat `/uploads` as untrusted input —
   which it is anyway, being arbitrary player files.

   **The size cap is the only limit that is actually enforced.** `maxUploadMegabytes` in the config
   asset is a courtesy check inside the app; the rule is what a request that skips the app runs into.
   Keep the rule at or above the config value — if the rule is the smaller number, honest uploads
   pass the in-app check and then fail with an unexplained 403.

   **Restricting the file type here would not work, and would break your own uploads.** Rules never
   see the bytes, only the name, the declared `Content-Type`, the size, and the token — and the first
   two are chosen by the caller, so junk named `robot.fbx` satisfies any type rule. Meanwhile the
   model goes up as `application/octet-stream` and the sidecar as `<file>.json`, so an fbx/urdf/zip
   allowlist would reject both. For the same reason, don't add `resource == null` to make writes
   create-only: filenames carry no uniquifier, so a player who fixes their CAD and resends
   `robot.fbx` would be refused.

   `{file}` rather than `{file=**}` keeps both folders flat — uploads are always one level under the
   uid, so nobody has a reason to build a deep tree in the bucket.

4. In Unity, fill in `Assets/Settings/RobotUploadConfig.asset`:
   - **Storage Bucket** — from the Storage tab, e.g. `overridesim.firebasestorage.app`
   - **Web API Key** — Project settings → General
   - **Max Upload Megabytes** — 250 by default; keep the rule above in sync

   The web API key is not a secret. Firebase web keys are public identifiers; access is decided by
   the rules above, not by hiding the key. It ships inside every build and is committed to this
   repo deliberately — see *What it can cost* for what does and doesn't follow from that.

Until the bucket and key are set, the screen still opens and lets a player pick a file, and then says
submitting isn't switched on yet — it never fails halfway through an upload.

Uploads land as `uploads/<uid>/<filename>` with a `<filename>.json` sidecar next to it holding the
team, robot name, contact, notes, **who the uploader wants to be able to use it**, app version and
timestamp. Firebase does not notify you on its own; either check the Storage tab or add a Cloud
Function on finalize to email yourself.

## What it can cost, and capping it

Storage needs the Blaze plan, and Blaze is pay-as-you-go with no automatic ceiling. Since the web API
key is public by design and anonymous sign-in is open to anyone, a person who reads this repo can
write to the bucket without going through the app. That is inherent to the design, not a mistake in
it — but on Blaze it is worth bounding, because there is no plan limit to stop at. Check current
numbers in the console; pricing changes.

What is already in your favour: **the expensive axis is closed.** Egress costs roughly five times
what storage does, and `/uploads` is `allow read: if false`, so nobody can pull anything back out.
Only accumulated storage accrues, at a couple of cents per GB-month, against a free allowance of
several GB. Filling the bucket is slow and cheap to undo; there is no way to run up a large bill fast.

Three controls, in order of how much they buy:

- **A lifecycle rule on the bucket** — Google Cloud console → the bucket → Lifecycle → delete objects
  older than 30 days. This is the one that matters. Intake is *download the file, set it up, done*,
  so `/uploads` has no reason to retain anything; the rule makes steady-state cost flat no matter how
  much arrives, with no code and nothing to remember.
- **A budget alert** in Google Cloud Billing, set low — a few dollars. It emails you within a day or
  two of anything odd, which is all the reaction time this situation needs. Note it only *notifies*;
  the documented hard stop is a budget → Pub/Sub → Cloud Function that detaches the billing account,
  which takes the whole project offline and is more than this is worth.
- **App Check**, before real users. It attests that a request came from your genuine app binary — App
  Attest on iOS, Play Integrity on Android — and rejects everything else before rules even run. It is
  the actual answer to "anyone with the key can call this", and the only one of the three that stops
  the writes rather than bounding their cost. It needs a debug token to keep the Editor working.

## How a player picks a file

Unity has no built-in file picker and no native picker package is installed, so `RobotFilePicker`
uses what already exists:

- **In the editor** — a normal open-file dialog.
- **On device** — the app's own documents folder. The player copies their file in from the Files app
  and it appears in the list; tapping **Choose File** steps through what's there.

For that folder to be visible in the iOS Files app the build needs `UIFileSharingEnabled` and
`LSSupportsOpeningDocumentsInPlace` set to `YES` in Info.plist (Xcode, or a post-build script).
If a native picker is added later, `ROBOSIM_NATIVE_FILE_PICKER` is the seam in `RobotFilePicker`.

Accepted: `.fbx`, `.urdf`, `.zip` (a URDF needs its meshes, hence the archive).

**Size is the real constraint.** The robot FBX files in this project run 100–205 MB. A phone upload
that size takes a while, so the screen shows progress and checks for a connection first.

## Setting up a submission when it arrives

1. Download the file and its `.json` sidecar from the Storage bucket.
2. Drop the model in `Assets/Models/` and follow `Docs/Fusion360-URDF-Export.md` — §A is the FBX
   path, which is the recommended one because it imposes no structure on the sender's CAD.
3. `Tools ▸ RoboSim ▸ Robot ▸ Set Up Imported Robot` (colliders, drivetrain, catalog entry, prefab,
   physics smoke test — one click).
4. Add the mechanisms by hand: `Tools ▸ RoboSim ▸ Robot ▸ Mechanisms ▸ …`.
5. `Tools ▸ RoboSim ▸ Robot ▸ Save As Robot Prefab`, and set **Listed For** per the sidecar's
   `sharing` field — **Public** for "Anyone", **Private** with an owner code otherwise.
6. Tell the player it's ready, by writing their inbox file (below).

## Telling a player their robot arrived

The app can't download a finished robot — it ships inside the update — but it can say the robot is
here and enter the code for them. Otherwise the only reply channel is the free-text contact field,
and a typo there orphans a submission for good.

When the update carrying their robot goes out, upload a file to `inbox/<uploaderId>.json` in the same
bucket, where `<uploaderId>` is the folder name their submission arrived under:

```json
{ "items": [ { "robotName": "654V Claw", "code": "654V-8213", "message": "" } ] }
```

At the next launch the home screen shows *"654V Claw is ready"* with a button that enters the code.
Items are ignored when the code is already held or when no robot in that build uses it, so writing
the file early is harmless — the notice simply appears once the update lands.

The uploader id is the only thing guarding an inbox (the rules above make `/inbox` publicly
readable), so it is treated as a secret. Players can see and copy theirs under **Settings → Your ID**,
and paste it back on a new phone with **Restore** — that bearer code is the whole account system.

## Team codes

An entry's **Owner Code(s)** field takes a comma-separated list, and holding *any* one of them
reveals the robot. Two consequences worth using:

- Give five of a team's robots the same `654V-TEAM` code and one code unlocks all five.
- Give a robot both its own one-off code and its team's — `CLAW-9F2K, 654V-TEAM` — and either works,
  so an individual robot can be shared without handing over the team's whole set.

Nothing verifies team membership, and nothing can: a self-declared team number is unfalsifiable.
Treat a code as a **capability**, not an identity claim — access starts with whoever sent the robot
in and spreads only because they passed the code on. That makes lying about your team pointless
rather than dangerous. The residual risk is a code leaking, which is the same risk as a teammate
screenshotting the robot, and no software prevents it.

## What "private" does and doesn't mean

A private robot still ships inside the app; it is filtered out of the model picker, the controller
config screen and the spawner until its owner enters the code. That stops one player casually
copying another's design. It does **not** stop someone extracting the model from the app's files.
Genuine privacy needs the robot to live on the server and download only after the uploader is
verified — the same backend as above, plus accounts.
