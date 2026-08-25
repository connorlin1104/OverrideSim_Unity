# From a submitted file to a robot a player can drive

The whole path, in order, for one submission. Four parts: get the mesh down to size, set the robot
up, bundle it, reclaim the disk.

Background for each part lives elsewhere — [Robot-Submissions.md](Robot-Submissions.md) for what
arrives and why, [Robot-Delivery.md](Robot-Delivery.md) for bundles, and
[Model-Storage.md](Model-Storage.md) for the store. This is the checklist.

**Status 2026-08-24: run once for real, most of the way.** Darwinbot has been through steps 4–9 —
built as a bundle, spawned and driven from StreamingAssets — and step 11's stow ran 2026-08-18 with
the fetch round-tripping byte-identical. `Assets/StreamingAssets/` and `~/RoboSimModelStore` both
exist now. Still unproven: step 12 (spawn with the FBX stowed) and the Serve From Storage download
route.

## A — the model arrives

Submissions are FBX now. CAD was accepted until 2026-08-17 and is not any more — the Fusion
round-trip it needed cost more than the smaller upload saved. See *Why a submission is 100 MB* in
[Robot-Submissions.md](Robot-Submissions.md).

- **1. Pull it from the bucket** — the file plus its `.json` sidecar. The sidecar carries the
  `sharing` field needed in step 6.
- **2. Decimate it if it is over ~50 MB.** Two places, and Blender is the easier one:
  - **Blender, before Unity sees it** — import the FBX, add a **Decimate** modifier (Collapse) to
    the heavy objects, export FBX. Ratio is the whole size lever; better than half off is routine.
    Doing it here also keeps `Assets/Models/Submitted/` and the model store small.
  - **`Robot ▸ Reduce Robot Meshes`, after import** — the same class of tool inside the editor, with
    a Hausdorff error reported per mesh. It also lifts the meshes out of the FBX into standalone
    assets, which is a second, separate win.
- **3. Keep the file you decimated from**, outside the project — next to the model store is fine. It
  is the only thing a re-decimation can start from, and with CAD no longer accepted there is nothing
  behind it to fall back on.

## B — Set it up

- **4. Copy the FBX into `Assets/Models/Submitted/`.** The `.fbx` only, never a `.fbx.meta`.
  - Being under that folder is a hard requirement for Stow (`ModelStoreWindow.cs:151`), and the
    folder is gitignored as a directory so nothing under it can reach LFS.
- **5. Drag it into SampleScene, then right-click the instance ▸ `Prefab ▸ Unpack Completely`.**
  Select the root and run `Tools ▸ RoboSim ▸ Robot ▸ Set Up Imported Robot`.
  - **Unpack first, every time.** A dragged-in FBX is a model-prefab instance, and rigging the
    drivetrain reparents each wheel's nodes under a new link object. Restructuring is not something a
    prefab instance can record as an override — the Claw and Cascade builders warn about exactly this
    and Set Up Imported Robot does not, so it is on you here.
  - **If it imports lying on its side**, leave it alone — tick **Bake Axis Conversion** on the FBX
    (Model tab) if you want the fix in the mesh, or just rotate the root. `RobotSpawner` composes the
    prefab root's rotation onto its own spawn heading, so an authored orientation now survives spawn.
    It did not always: until 2026-08-18 `Instantiate` overwrote it, and the robot stood upright
    everywhere in the editor and lay down the moment you pressed Play.
  - Expect "Detected: mesh/FBX robot."
  - **Wheel Name Contains** must match a token in this robot's wheel node names; comma-separate
    several. A wrong token gives "No wheel nodes matched" and it stops.
  - Leave **Save As Prefab After** ON.
  - One click gives colliders, motorized wheels, the catalog entry, `Assets/Robots/<Name>.prefab`,
    removal of the scene copy, **and the scene saved**.
- **6. Mechanisms, if it has any** — open the prefab (double-click it: the builders that reparent
  need Prefab Mode), `Robot ▸ Mechanisms ▸ …`, then `Robot ▸ Save As Robot Prefab`.
  - Set **Listed For** here: Public, or Private + owner code per the sidecar's `sharing`.
  - A plain drivetrain skips this step.
- **7. Confirm SampleScene has no robot instance.** Step 5 handles it; if you dragged one back in,
  delete it and save. A model that a scene references can never be stowed.

## C — Bundle it

- **8. `Tools ▸ RoboSim ▸ Robot ▸ Build Robot Bundle`**, pick the robot:

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
- **9. Play, spawn it, drive it.** ⚠️ First time the bundle route has ever run in this project. A
  failure here has nothing to do with the Model Store — it means the delivery half is broken.
  - With Serve From Storage **OFF** this only exercises StreamingAssets. **Downloading is a
    different route and a separate test** — run it once with the toggle ON, and delete the
    StreamingAssets copy first or that route answers before the download is ever tried.
  - The download route starts at the field scene's **Upload Config**, which was null in both field
    scenes until 2026-08-19. Unset, a downloaded robot spawns as *a different robot* and says so
    only in the console. `Build Robot Prefabs & Spawner` now re-wires both field scenes on every
    run, and `RobotBundleValidation` fails if either one comes unwired again.

## D — Reclaim the disk

- **10. Pre-flight: `Robot ▸ Advanced ▸ Check Model Store Round-Trip`.** Builds a synthetic model
  under `Assets/Models/Submitted/RoundTripTest.*`, stows it, fetches it, cleans up. Confirmation
  dialog first, result in a dialog. A failure here is the tool, not your robot — stop.
- **11. `Robot ▸ Model Store` → Stow.** Root defaults to `~/RoboSimModelStore`. Expect a pointer
  count, a sha256, and an rsync line.

  | refusal | cause |
  |---|---|
  | "still has a direct prefab reference in the catalog" | step 8's Remove From Binary was off |
  | "referenced by 2 files" / "is a scene, not a prefab" | step 7 |
  | "only N of them resolve" | the robot was already broken before you stowed it |

- **12. Play again and spawn it, with the FBX now gone.** ⚠️ Must look and drive exactly like step
  9. This is the claim the Model Store exists to make true.
  - The prefab's dead pointers in the inspector are expected. The prefab is the rebuild source, not
    the delivery path — the bundle already holds the meshes.
- **13. When it later needs rebuilding:** Model Store → Fetch. Expect `N/N resolved` matching step
  11 and `finger <hash> (matches)`. Anything partial throws and leaves the file to inspect.

**Starting a robot over:** `Robot ▸ Delete Robot`. Deleting only the prefab is the trap — the
catalog entry survives pointing at nothing, resolves to null, and `RobotSpawner` falls through to the
old bundle and spawns the previous version. That looks exactly like the editor caching something.

**The FBX is never deletable, only movable.** Stow moves it out of the project and into the store;
it does not make it disposable. Rebuilding the robot after any change to the setup tools starts from
that file, and nothing upstream can regenerate it now that CAD is refused. The store lives on one
Mac — treat it as something to back up, not as the backup.

## Doing it as a dry run

Use the canonical plain-mesh robot rather than waiting for a submission — smallest of the five at
16 MB, and setup is fully automatic rather than hand-built mechanisms.

- Copy `Assets/Models/360 RPM Drivetrain.fbx` → `Assets/Models/Submitted/StoreDryRun.fbx`
- Start at step 4, skip step 6.
- Cleanup: `Robot ▸ Delete Robot`, pick it, delete — that takes the prefab, the catalog entry and
  every bundle in one go. Then delete the FBX and the store folder, which it deliberately leaves.
