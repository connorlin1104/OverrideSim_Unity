# From a submitted file to a robot a player can drive

The whole path, in order, for one submission. Four parts: get an FBX out of the CAD, set the robot
up, bundle it, reclaim the disk.

Background for each part lives elsewhere — [Robot-Submissions.md](Robot-Submissions.md) for what
arrives and why, [Robot-Delivery.md](Robot-Delivery.md) for bundles, and
[Model-Storage.md](Model-Storage.md) for the store. This is the checklist.

**Nothing here has ever been run end to end.** `Assets/StreamingAssets/` and `~/RoboSimModelStore`
do not exist yet. Steps 10 and 13 are the two that prove the pipeline; everything else is setup
that has been done before.

## A — CAD arrives

- **1. Pull it from the bucket** — the file plus its `.json` sidecar. The sidecar carries the
  `sharing` field needed in step 7.
- **2. Open it in Fusion.**
  - `.f3d` / `.f3z` → Upload to your project, then open. An `.f3z` is a distributed design: a zip
    of one or more `.f3d` files plus the components the parent links to. Fusion picks which of the
    two the sender gets, so expect `.f3z` for anything assembly-shaped.
  - `.step` / `.stp` → File ▸ Open. Fusion converts it to a design.
- **3. Export FBX at refinement Low or Medium**, UV export off if offered.
  - Refinement is the entire size lever, and this step is the reason CAD is asked for at all.
  - Medium first. Drop to Low if the FBX is still over ~50 MB.
- **4. Keep the CAD** outside the project — next to the model store is fine. Wrong tessellation
  means re-export, never decimate.

> A player who sends an FBX instead skips all of A. Surfaces cannot be recovered from a mesh, so
> what arrived is what you get. Tell them Low/Medium next time.

## B — Set it up

- **5. Copy the FBX into `Assets/Models/Submitted/`.** The `.fbx` only, never a `.fbx.meta`.
  - Being under that folder is a hard requirement for Stow (`ModelStoreWindow.cs:151`), and the
    folder is gitignored as a directory so nothing under it can reach LFS.
- **6. Drag it into SampleScene, select the root, run
  `Tools ▸ RoboSim ▸ Robot ▸ Set Up Imported Robot`.**
  - Expect "Detected: mesh/FBX robot."
  - **Wheel Name Contains** must match a token in this robot's wheel node names; comma-separate
    several. A wrong token gives "No wheel nodes matched" and it stops.
  - Leave **Save As Prefab After** ON.
  - One click gives colliders, motorized wheels, the catalog entry, `Assets/Robots/<Name>.prefab`,
    removal of the scene copy, **and the scene saved**.
- **7. Mechanisms, if it has any** — drag the prefab back in, `Robot ▸ Mechanisms ▸ …`, then
  `Robot ▸ Save As Robot Prefab`.
  - Set **Listed For** here: Public, or Private + owner code per the sidecar's `sharing`.
  - A plain drivetrain skips this step.
- **8. Confirm SampleScene has no robot instance.** Step 6 handles it; if you re-dragged one in for
  step 7, delete it and save. A model that a scene references can never be stowed.

## C — Bundle it

- **9. `Tools ▸ RoboSim ▸ Robot ▸ Build Robot Bundle`**, pick the robot:

  | setting | dry run | real publish |
  |---|---|---|
  | Serve From Storage | **OFF** — StreamingAssets, no bucket, no cost, no auth | ON |
  | Remove From Binary | **ON** | ON |
  | Platforms | **macOS only** | iOS + Android |

  - Expect a size line ending in
    `Assets/StreamingAssets/robots/StandaloneOSX/v1/<id>-<hash>.bundle`, and "Cleared the direct
    prefab reference."
  - **Record the size.** That is the per-download number the Firebase cost question is waiting on.
  - Unsupported platforms are greyed out, so a missing iOS build module shows up here.
- **10. Play, spawn it, drive it.** ⚠️ First time the bundle route has ever run in this project. A
  failure here has nothing to do with the Model Store — it means the delivery half is broken.
  - With Serve From Storage **OFF** this only exercises StreamingAssets. **Downloading is a
    different route and a separate test** — run it once with the toggle ON, and delete the
    StreamingAssets copy first or that route answers before the download is ever tried.
  - The download route starts at the field scene's **Upload Config**, which was null in both field
    scenes until 2026-08-19. Unset, a downloaded robot spawns as *a different robot* and says so
    only in the console. `Build Robot Prefabs & Spawner` now re-wires both field scenes on every
    run, and `RobotBundleValidation` fails if either one comes unwired again.

## D — Reclaim the disk

- **11. Pre-flight: `Robot ▸ Advanced ▸ Check Model Store Round-Trip`.** Builds a synthetic model
  under `Assets/Models/Submitted/RoundTripTest.*`, stows it, fetches it, cleans up. Confirmation
  dialog first, result in a dialog. A failure here is the tool, not your robot — stop.
- **12. `Robot ▸ Model Store` → Stow.** Root defaults to `~/RoboSimModelStore`. Expect a pointer
  count, a sha256, and an rsync line.

  | refusal | cause |
  |---|---|
  | "still has a direct prefab reference in the catalog" | step 9's Remove From Binary was off |
  | "referenced by 2 files" / "is a scene, not a prefab" | step 8 |
  | "only N of them resolve" | the robot was already broken before you stowed it |

- **13. Play again and spawn it, with the FBX now gone.** ⚠️ Must look and drive exactly like step
  10. This is the claim the Model Store exists to make true.
  - The prefab's dead pointers in the inspector are expected. The prefab is the rebuild source, not
    the delivery path — the bundle already holds the meshes.
- **14. When it later needs rebuilding:** Model Store → Fetch. Expect `N/N resolved` matching step
  12 and `finger <hash> (matches)`. Anything partial throws and leaves the file to inspect.

## Doing it as a dry run

Use the canonical plain-mesh robot rather than waiting for a submission — smallest of the five at
16 MB, and setup is fully automatic rather than hand-built mechanisms.

- Copy `Assets/Models/360 RPM Drivetrain.fbx` → `Assets/Models/Submitted/StoreDryRun.fbx`
- Start at step 5, skip step 7.
- Cleanup: delete the FBX, `Assets/Robots/StoreDryRun.prefab`,
  `Assets/StreamingAssets/robots/`, the store folder, then
  `git checkout -- Assets/Settings/RobotModelCatalog.asset`.
