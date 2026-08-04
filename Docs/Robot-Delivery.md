# Getting a finished robot to a player

Setting a robot up is Editor work and always will be — see
[Robot-Submissions.md](Robot-Submissions.md). This is about the step after that: how the finished
robot reaches the person who sent it in.

There are two ways, and a robot uses exactly one of them.

| | Compiled in | Published as a bundle |
|---|---|---|
| Catalog entry holds | a direct `prefab` reference | a `bundle` id + version |
| Reaches a player | in the next App Store build | as a download, minutes after you publish |
| Ships to | **every** device that installs the app | only devices that ask for it |
| Private really means | hidden from the picker | absent from the device |
| Costs | app size, forever | egress, per download |

Robots that ship with the game stay compiled in. That's the right trade for a handful of robots
everyone is meant to have. Every robot a player sends in should be published.

## Why this exists at all

Measured on this project's own fleet (`Tools > RoboSim > Robot > Robot Mesh Report`):

```
robot                  meshes       tris      verts    runtime
360 RPM Drivetrain         39    468,050    356,527    39.4 MB
654V v1                   183  2,792,581  2,070,066   227.9 MB
654V v2                   220  2,931,499  2,178,307   246.3 MB
654V v3                   149  2,749,461  2,006,880   225.6 MB
                                                      -------
                                                      739.2 MB
```

Every one of those is inside the binary, on every device, forever, because
`RobotModelCatalog.Entry.prefab` is a direct reference and a direct reference is what pulls an asset
into a build. Four robots is already 739 MB. There is no version of "accept uploads" that survives
that.

It is also why a private robot was only ever *hidden*: its geometry shipped to everyone regardless,
and the picker just declined to list it.

## Step 1 — make the meshes smaller first

`Tools > RoboSim > Robot > Reduce Robot Meshes`

Do this before publishing anything. A Fusion export is tessellated for manufacturing: 2.8 million
triangles for a VEX robot is roughly thirty times what a phone screen can show, and it is the whole
of the number above.

The tool decimates each render mesh (quadric error metrics — `MeshDecimator`) and saves the results
as standalone `.asset` files, then points the robot's `MeshFilter`s at those.

- **Colliders are not touched.** Robot colliders are separately generated hulls under
  `Assets/RobotColliders/`, so this cannot change how a robot drives. That is the only reason it is
  safe to run on an already-tuned robot. If a `MeshCollider` is found sharing a render mesh, it is
  left alone and reported.
- **Holes survive.** Every boundary edge is pinned, because the cheapest collapses in a VEX part are
  the ones around a rim and an unconstrained run fills every hole in the robot.
- **Sharp edges survive.** Normals are rebuilt per smoothing cluster rather than averaged, or every
  machined edge comes back as a soft gradient.
- **UVs and tangents are dropped by default.** This project has no textures and no normal maps, so
  they are 24 bytes per vertex of nothing. Turn UVs back on if that ever stops being true.
- **The FBX stops being a build dependency** — but this is *not* a disk saving, and reading it as one
  gets the sign wrong. A prefab holds ~1,550 pointers into its source FBX; delete the file and you
  have correct joints with no geometry, and the decimated copies own their data instead. However,
  this project serializes as text (`m_SerializationMode: 2`), so a mesh `.asset` writes its vertex
  buffer as hex — **two ASCII characters per byte**. At full detail that turns ~226 MB of runtime
  mesh into 450+ MB of YAML replacing a 110 MB FBX, which is worse than doing nothing. Extraction
  only pays *after* the decimation is real. Keep the FBX either way: it is the only way to redo this
  at a different ratio. For getting source models off the disk, see
  [Model-Storage.md](Model-Storage.md) — that is a separate mechanism and it does not depend on
  decimation working.

**Read the `moved` column, not the `kept` column.** It is the distance from the original surface to
the nearest point on the decimated one, in mesh units, and it is the only quality signal available —
a triangle count says how much was removed, not whether the part still looks like itself. A mesh
marked *stopped on error* hit the quality ceiling before the ratio, which is the tool doing its job:
that part was already about as simple as its shape allows.

**Changing your mind about the ratio needs the original meshes back.** The tool refuses to decimate
its own output (compounding the error has no undo), so re-running at a different setting means
reverting the prefab first: `git checkout <commit> -- Assets/Robots/<Robot>.prefab`.

**It rewrites the source FBX's `.meta`.** Decimation reads vertex data, which needs Read/Write on the
model importer, so the tool turns it on, reimports (the slow part — a 100 MB FBX), and turns it back
off afterwards. Unity normalises a couple of unrelated importer fields while it is in there; on these
models that is inert (they are static CAD with no animation), but the `.meta` will show up in `git
status` and that is why.

Batch: `-executeMethod ReduceRobotMeshes.RunBatch -robot <PrefabName> [-keep 0.08] [-maxError 0.004]`

There is deliberately no "reduce every robot" command. The ratio is a judgement about how a
particular robot looks, and the sensible way to find it is one robot at a time with the result in
front of you.

## Step 2 — build the bundle

`Tools > RoboSim > Robot > Build Robot Bundle`

Pick the robot, pick the platforms, build. Two things to get right:

- **Serve From Storage off** puts the bundle in `Assets/StreamingAssets/` — inside the app, no
  server, no cost, no auth. That is how to prove the loading path works against a real robot before
  any of it is exposed to a player.
- **Remove From Binary on** clears the catalog's direct `prefab` reference. Leave it on. With it off
  the bundle is built and then never used, because a direct reference always wins — and the robot is
  still in the binary, which was the thing you were trying to stop.

The prefab stays in `Assets/Robots/`. It is what the bundle gets rebuilt from, remembered by GUID
(`BundleRef.sourceGuid`) rather than by reference, because a reference is exactly what puts the robot
back in the build.

Bundle versions are **content hashes**. The same robot built twice produces the same version, so a
rebuild that changed nothing re-downloads nothing; a robot that did change gets a new URL and no
cache has to be invalidated by hand.

To find out what a robot would cost to download before committing to any of this:
`-executeMethod BuildRobotBundles.RunBatchMeasure -robot <catalog id> [-platform iOS]`.

## Step 3 — publish

Everything lands under `Build/RobotBundles/`, laid out exactly as it goes into the bucket:

```
robots/public/index.json
robots/public/iOS/v1/654v-claw-a1b2c3d4.bundle
robots/c<32 hex>/index.json
robots/c<32 hex>/iOS/v1/654v-claw-a1b2c3d4.bundle
```

```
gsutil -m rsync -r Build/RobotBundles gs://<bucket>/
```

**Publishing is never done from the app.** `/robots` is `allow write: if false` in `storage.rules`
and has to stay that way: this repo is public and the web API key with it, so anything the app is
permitted to write, any stranger is equally permitted to write — and this is the path players load
code and geometry from.

### How a private robot is private

A private robot's folder name is a SHA-256 of its owner code. Holding the code is what lets a device
*name* the file; nothing else can, and object listing is not granted by a read rule, so the folders
cannot be enumerated either.

This is not a rule Storage could enforce. A code here is a bearer token handed out on purpose, not an
identity, and Storage can only check who you signed in as — anonymous sign-up is open to anyone, so
requiring auth would add nothing. The address *is* the capability, which is the same model the rest
of the project already runs on.

What it does and doesn't buy, exactly:

- **Before:** the geometry was on every device that installed the app; the picker declined to list
  it.
- **Now:** the geometry never arrives unless the code was presented.
- **Still not covered:** someone with both the app files and the code. Once a device can fetch a
  robot, a device can keep it. That was always true.

Two consequences worth remembering:

- **Changing a robot's owner code moves its address.** Anything published under the old one becomes
  unreachable. Republish after changing a code.
- **The published index never contains a code.** It doesn't need one — the file was only reachable by
  computing its address from a code the device already holds. `RobotBundleValidation` asserts that.

### Discovery

The app reads `robots/public/index.json` plus one index per code the player holds, at launch. That is
what lets a robot appear in someone's picker without an app update.

One index per address is a privacy decision, not a filing one: a single world-readable list naming
every robot would hand over display names, team labels, and the bare fact that a given team had
submitted anything.

Entering a code in Settings also checks the network before rejecting it. The old rule — a code that
matches nothing is refused rather than banked — is still right, but "matches nothing" now has to
include asking, or a perfectly good code for a robot published after this build shipped would be
turned away.

## Step 4 — versions, and the thing that will bite you

**A bundle serializes the scripts on the robot prefab, not just its meshes.** Every serialized field
on `RobotMotorController`, `ClawGrab`, every mechanism — all of it, written against the layout those
scripts had on the day the bundle was built.

Change one of those fields and Unity deserializes every older bundle against the new layout. The
missing field comes back as its default. **Silently.** The robot spawns, drives wrong, and nothing
anywhere says why. That is a worse failure than not loading at all, and given how often this project
touches brake fractions and load transfer and mechanism roles, it is the likely one.

So:

- `RobotBundleFormat.Version` records the layout. It goes **in the path**, so an old build goes on
  asking for old bundles and can never pick up an incompatible new one, and "can this app read that
  bundle" is answered by a 404 before anything is downloaded rather than after 30 MB has crossed a
  phone connection.
- The app refuses a mismatch and says which kind it is — "needs rebuilding" to you, "update the app"
  to the player. Telling a player to update when the fix is a rebuild leaves them updating forever.
- `RobotBundleValidation` fingerprints the serialized shape of every script on every robot prefab and
  pins it. Change one and the check fails, names the script, and tells you what to do. Forgetting to
  bump the version is otherwise the kind of mistake that only surfaces on someone else's phone.

**When you change a robot script:** bump `RobotBundleFormat.Version` → `Tools > RoboSim > Robot >
Rebuild All Robot Bundles` → re-upload → re-pin the layout in `RobotBundleValidation`.

## What it costs

Egress runs roughly 5× storage, and until now it was closed entirely (`allow read: if false` on
`/uploads`). Publishing opens it deliberately, so it is worth pricing before switching on rather than
after.

The number that decides it is the bundle size, which is why step 1 comes first and why
`RunBatchMeasure` exists. A robot nobody will wait to download is not delivered.

Note also that the 30-day lifecycle delete in `storage-lifecycle.json` is scoped to the `uploads/`
prefix and **must stay scoped that way**. An age-based rule reaching `robots/` would delete players'
robots a month after they were published.

## Validation

```
-executeMethod MeshDecimationValidation.RunBatchValidate   # 41 checks
-executeMethod RobotBundleValidation.RunBatchValidate      # 55 checks
-executeMethod RobotVisibilityValidation.RunBatchValidate  # 33 checks
```
