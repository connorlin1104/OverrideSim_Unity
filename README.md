# RoboSimL

Drive the robot before you build it. Send your team's 3D model and get it back as a machine you can
actually drive — real joints, real weight, real drivetrain.

A competition-robotics simulator for iPhone and iPad, built in Unity. Robots are driven with twin
on-screen sticks and a twelve-button controller, on a field of physics pieces that can be picked up,
carried and scored.

- **App Store:** RoboSimL 1.0, submitted 2026-09-09
- **Support page:** <https://overridesimunity.web.app/>
- **The repo is named `OverrideSim_Unity`** because the project began as *OverrideSim*. The app was
  renamed to RoboSimL before release; the repo, the Firebase project id and the internal folder
  names kept the old one rather than break every path that referenced them

---

## Requirements

| | |
| --- | --- |
| Unity | **6000.5.0f1** — the exact version in `ProjectSettings/ProjectVersion.txt` |
| Target | iOS 15.0+, iPhone and iPad, **landscape only** |
| Git LFS | **required before cloning** — see below |

### Cloning

`.fbx`, `.dylib` and the generated meshes under `Assets/RobotMeshes/` are stored in Git LFS. Clone
without it and you get text pointer files where the robots should be, and Unity imports nothing.

```
git lfs install
git clone git@github.com:connorlin1104/OverrideSim_Unity.git
```

Already cloned without LFS? `git lfs install && git lfs pull`.

---

## Layout

| Path | What's in it |
| --- | --- |
| `Assets/Scripts/Drivetrain/` | Wheel torque, the turn/drive authority mix, the tyre friction model |
| `Assets/Scripts/Mechanisms/` | Lifts, intakes, pistons — the actuators a robot is built from |
| `Assets/Scripts/Robot/` | Robot assembly, the mechanism registry, control routing |
| `Assets/Scripts/FieldPieces/` | Scoreable pieces, goals, the field's latching rollers |
| `Assets/Scripts/Services/` | Robot upload, catalog sync, bundle delivery — the Firebase side |
| `Assets/Scripts/UI/` | Home screen, settings, the driving controls |
| `Assets/Scripts/Editor/` | The build tooling — over half the code. Rig builders, collider generation, mesh decimation, screenshot capture, validation rigs |
| `Assets/Robots/` | The four robots that ship, as prefabs |
| `Docs/` | Real procedure docs, not notes — see below |
| `Web/` | The support and privacy pages, deployed to Firebase Hosting |
| `storage.rules` | Firebase Storage rules — what actually guards uploads |

### Scenes

- **`HomeScene`** — entry point, index 0. Model picker, settings, Submit a Robot
- **`SampleScene`** — the full competition field
- **`LiteScene`** — a reduced field for lower-end devices

### Robots that ship

`360 RPM Drivetrain`, `654V v1`, `654V v2`, `654V v3`. All four are the author's own team's designs,
published with permission.

---

## Docs

Written to be followed, not skimmed. Each one exists because something in it was non-obvious enough
to cost real time.

| Doc | Covers |
| --- | --- |
| `Docs/App-Store-Submission.md` | Every App Store Connect field and the reasoning behind each answer |
| `Docs/TestFlight-Build.md` | Unity → Xcode → TestFlight, and the traps in that path |
| `Docs/Robot-Submissions.md` | What happens when a player sends a robot in |
| `Docs/Robot-Delivery.md` | Decimating, bundling and addressing a finished robot back to its team |
| `Docs/Model-Storage.md` | Where models live and why a prefab cannot outlive its FBX |
| `Docs/Fusion360-URDF-Export.md` | Getting a CAD assembly out as something the importer reads |
| `Docs/Pipeline-Dry-Run.md` | End-to-end rehearsal of the submission pipeline |

---

## Rights

All rights reserved. The robot designs, field models and source in this repository are the author's
and his team's, and are published here to be read rather than reused.

Not affiliated with, endorsed by, or sponsored by VEX Robotics, Innovation First International, or
the REC Foundation.
