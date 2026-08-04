# Where a submitted model's FBX lives

Setting a robot up needs its source FBX. Rebuilding its bundle needs it again. Everything in
between doesn't — and "everything in between" is almost all of the time, which is the only reason
this is solvable at all.

## The number this exists for

A Fusion export is around 110 MB, and it lands on the setup machine **three times**:

| copy | per model | reclaimed by deleting the FBX? |
|---|---|---|
| `Assets/Models/…` working tree | ~110 MB | yes |
| `.git/lfs/objects` | ~110 MB | **no — the object is permanent** |
| `Library/Artifacts` | ~130 MB | only by reimporting from a file you no longer have |

That is ~350 MB per robot before a single bundle is built. Ten submissions is 3.5 GB; forty is 14 GB.
Resubmissions are worse, because every revision adds another permanent LFS object.

## Submitted models are never in git

They go in `Assets/Models/Submitted/`, which `.gitignore` covers **as a directory**. Nothing under it
is ever tracked, so there is no LFS object, nothing to untrack, and nothing for `git lfs prune` to
delete out from under you.

That rule has to stay a directory rule. Three things go wrong the moment models are ignored by
filename instead:

- **A Fusion export is called `export.fbx`.** `RobotUploadService` says so itself — it is why uploads
  are renamed on arrival. Ignore one player's `Assets/Models/export.fbx` and the *next* player's
  export at that path is silently skipped by `git add -A`: set up, given colliders, tuned, and never
  committed. That is how a submitted robot is lost outright, and `git status` never mentions it.
  `core.ignorecase` is on, so `Export.fbx` collides too.
- **`git lfs prune` deletes archived objects.** Not "won't touch them" — it deletes them, as soon as
  the commit that removed the file stops being a ref tip. It is also the exact command anyone
  reaches for when trying to free disk, which is the entire premise of this document.
- **GitHub's free LFS allowance is 1 GB, and this repo is past half of it.** Ten more models do not
  fit. A tripped quota blocks LFS for the whole repository at once, so "just get it back out of git
  history" stops working for *every* model simultaneously — including the four that are in there
  legitimately.

**The five models already in git stay in git.** The four robots are compiled into the app, so their
FBX is needed at every build; the field is referenced 2,854 times by `SampleScene` and 515 by
`LiteScene`, so it can never leave. `Stow Model` refuses anything outside `Assets/Models/Submitted/`
for exactly this reason.

## Stow and fetch

`Tools > RoboSim > Robot > Model Store`

The store root is an absolute path outside the project. It is refused if it is inside the project
(Unity would import the payloads, and a root under `Build/RobotBundles/` gets rsynced into a
**public** bucket), if any component of it is a symlink, or if it is under `Desktop`, `Documents`,
`Dropbox`, `iCloud Drive` or similar — those evict contents to a stub that reports the right file
size and then fails the read that was supposed to verify it.

**Stow** refuses a model that is still compiled in. `RobotModelCatalog.Entry.prefab` is a direct
reference, and a direct reference is what pulls an asset into a build, so a stowed model with that
reference still set means the next app build has no geometry. Publish it as a bundle first.

The order is the safety:

```
write to .staging/ → re-hash it FROM DISK → rename into place → re-hash it FROM DISK AGAIN
                                                              → only then delete from Assets/
```

The second re-hash is not paranoia about the first. Between them sit a directory rename and an
unbounded amount of wall-clock time in which a drive can be ejected or a disk can fill. Deleting
110 MB on the strength of a check made before all that is how the store ends up holding a manifest
and no bytes.

**Fetch** hashes the payload *before* writing anything into `Assets/`, writes the `.meta` and the FBX
inside a single `StartAssetEditing` block so Unity never sees one without the other, and then counts
what actually reconnected.

## Why the reconnection count is the whole point

An FBX sub-asset's `fileID` is **not stored in the `.meta`** — every model here has
`internalIDToNameTable: []`. Unity derives it from the node's name and place in the hierarchy. So:

- Identical bytes reproduce identical `fileID`s, and all ~1,500 pointers reconnect.
- A **re-export** from Fusion with two parts renamed reproduces most of them and drops the rest.

The second case is the one worth being afraid of. The robot spawns. It drives correctly. It is
missing an arm, and nothing in the console says so, because a null mesh is not an error. That is why
Fetch refuses on any disagreement rather than warning.

The count alone is not enough either. Counts see a pointer that fails to resolve; they cannot see a
pointer that resolves to the **wrong object** — a reimport under a newer Unity can hand back the same
ids with the same names and different geometry. So the manifest also carries a fingerprint over
vertex count, sub-mesh layout, index counts, bounds, and each material's shader. All of those read
with `Read/Write` off, so it costs nothing.

`ModelStoreValidation` mutation-proves both halves: it removes one pointer from a copy of a real
prefab and requires the count to drop by exactly one, and it requires the fingerprint to carry real
vertex counts. A census that always passes is worse than no census.

## The store is the only copy

Stowed bytes are not in git, not in LFS, and not on GitHub. That is deliberate — see above — but it
means the store is load-bearing:

```
rsync -a --delete ~/RoboSimModelStore/ <backup, external drive, or bucket>/
```

If that goes to Cloud Storage, use a prefix **outside** `uploads/`. The 30-day lifecycle rule in
`storage-lifecycle.json` is scoped to `uploads/` and would delete the archive a month after it was
written.

## Batch

```
-executeMethod ModelStoreWindow.RunBatchStow  -model <asset path> [-storeRoot <abs>] [-allowUnpublished]
-executeMethod ModelStoreWindow.RunBatchFetch -guid <guid>        [-storeRoot <abs>]
-executeMethod ModelStoreValidation.RunBatchValidate
```

## What this does not fix

- **The field.** 205 MB, referenced by both scenes, needed at every build. It is one file that does
  not grow with the number of submissions, so it is a fixed cost rather than the problem.
- **`Library/Artifacts`.** Stowing removes the source, but the import artifact for a model stays
  until the model is stowed *and* the artifact is evicted. Deleting `Library/` wholesale reclaims it
  and costs a full reimport.
- **Remote LFS quota.** Archiving buys local disk. It does not shrink anything already in git
  history, which is permanent.
