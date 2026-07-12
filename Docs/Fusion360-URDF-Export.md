# Setting Up a New Robot (Fusion 360 → RoboSim)

Two ways to get a VEX robot out of Fusion 360 and driving in RoboSim:

- **FBX (recommended — no CAD restructuring).** Export the robot as a mesh and author the moving
  joints in Unity. Your Fusion hierarchy, components, and joints are irrelevant; nothing upstream has
  to change. Best when people built their robot however they liked. See **§A**.
- **URDF (precise — optional).** Export via the ACDC4Robot add-in. It carries exact joint axes,
  limits, and (optionally) CAD masses — but URDF *is* a strict links-and-joints tree, so it forces
  the design into that shape (one component per moving part, joints on components only, flat
  hierarchy). Worth it only when a team already authored clean Fusion joints and wants CAD-accurate
  motion. See **§1** onward.

Both paths end the same way: **Set Up Imported Robot** rigs it, **Add or Fix Mechanism Joint** marks
any moving parts, **Save As Robot Prefab** puts it in the picker.

---

## A. FBX path (no CAD restructuring)

Nothing in Fusion needs reorganizing — FBX just bakes whatever geometry exists, so component nesting,
joints-on-bodies, and half-built assemblies all import fine.

### A.1 Export from Fusion
1. Keep the design in its **native units** (VEX parts are inches — do **not** switch to meters; that
   is a URDF-only step and would mis-scale the mesh).
2. Optionally hide or delete parts you don't want — or leave them and skip them in Unity.
3. **File ▸ Export ▸ FBX.** No joints, no flattening, no grounding, no add-in.

### A.2 Import into Unity
1. Drop the `.fbx` into `Assets/Models/`.
2. Select it → the **Model** tab, and match the existing `360 RPM Drivetrain.fbx` settings. The ones
   that matter: **Scale Factor = 1**, **Use File Scale = on**, **Convert Units = on**, **Bake Axis
   Conversion = off**. Apply.
3. Drag it into the scene and **compare its size to the 360 RPM Drivetrain**. The FBX carries the
   world scale from your CAD units, so a robot modeled in the same units as the other bots lands at
   the right size. If it is ~10× off, that's a units mismatch — fix it with **Scale Factor** or
   re-export in matching units.
4. If it comes in lying on its back, that's Fusion Z-up vs Unity Y-up — rotate the root −90° about X
   (or turn **Bake Axis Conversion** on and reimport).

### A.3 Set it up
1. Select the root → **Tools ▸ RoboSim ▸ Robot ▸ Set Up Imported Robot** → **Set Up Robot**.
   - **Wheel Name Contains** — comma-separated tokens matched anywhere in your wheel node names
     (e.g. `Omni`, or `3.25 AS Omni, Traction` for a mixed drivetrain). Those nodes become the
     motor-driven wheels; everything else becomes the rigid chassis. If it reports no wheels, set
     this to a word from your wheel parts and run again.
   - It builds colliders, rigs the wheels to the joysticks, sizes masses, and registers the robot
     (mechanisms registry + button router + home-screen catalog entry). The drivetrain drives
     immediately.
2. **Mark each moving part** (arm, intake, flywheel, piston) with **Advanced ▸ Add or Fix Mechanism
   Joint** (see §5.1). On a mesh robot this **splits the part off the chassis** into a new moving
   link: pick the part's group node, choose Revolute / Continuous / Prismatic, set the axis and the
   **Anchor** at the hinge/slide line, then **Apply**. It's wired to a controller button
   automatically — no axle needs to exist in the CAD, the joint replaces it.
3. **Save As Robot Prefab** (§6) to put it in the home-screen picker.

That's the whole no-restructuring flow: drop the FBX, set it up, mark the DOFs, save. The rest of
this doc (§1 onward) is the optional URDF path.

---

## Quickstart (URDF path)

1. In Fusion: **units = meters**, one component per moving part, add the joints you can
   (Revolute on axles named with `wheel`, Slider on pistons), ground the chassis, history off.
2. Run **ACDC4Robot** → export **URDF**. Drag the whole exported folder into `Assets/`.
3. **GameObject ▸ 3D Object ▸ URDF Model (import)** → pick the `.urdf`, keep axis **Y**.
4. Select the imported root → **Tools ▸ RoboSim ▸ Robot ▸ Set Up Imported Robot** → **Set Up Robot**.
5. Any joint that came in wrong or welded? **Tools ▸ RoboSim ▸ Robot ▸ Advanced ▸ Add or Fix
   Mechanism Joint**.
6. Put it in the home-screen picker: **Tools ▸ RoboSim ▸ Robot ▸ Save As Robot Prefab** (see §6).
7. Press Play and drive.

---

## 1. One-time: install the exporter

1. In Fusion 360, open the **Autodesk Fusion App Store** (Utilities ▸ Add-Ins ▸ Fusion App Store).
2. Search for **ACDC4Robot** and install it (free, actively maintained).
3. Restart Fusion; the add-in appears under Utilities ▸ Add-Ins. It exports URDF — the only format
   the Unity importer reads (ignore its SDF/MJCF options).

## 2. Prepare the design in Fusion

URDF is much stricter than Fusion about structure. Do these before every export:

1. **Set document units to meters** (Document Settings ▸ Units). The importer assumes meters — a
   millimeter document imports 1000× too big.
2. **(Optional) Assign a physical material** (right-click ▸ Physical Material) — only if you want
   CAD-accurate mass. You no longer *have* to. A part exported without one comes in with zero mass
   (the importer silently clamps it to 0.1 kg), so **Set Up Imported Robot** instead computes each
   link's mass from its mesh volume × a density looked up from the part **name**. Keep your CAD
   library's material words in the part names (Polycarb, Nylon, Aluminum, C-Channel, Steel, …) so
   the lookup hits. Prefer the CAD's own masses? Assign materials and turn on **Keep URDF
   Inertials** at import instead.
3. **Flatten to one component per rigid part** — each thing that moves as a unit (chassis, each
   wheel, lift arm, claw half) is a single top-level component, **no nested components**. Use
   Combine/Join to merge bracket-and-panel sub-assemblies first.
4. **Connect the parts with assembly joints** — the exporter turns Fusion joints into URDF joints:
   - **Revolute** joints for motor axles. Name every driven wheel component with **`wheel`** in its
     name (e.g. `wheel_FL`) — the setup tool finds the drivetrain by that substring.
   - **Slider** joints for pneumatic pistons.

   You don't have to get every joint right in Fusion — anything you miss or mis-type you can add or
   fix in Unity afterward (see §5.1). That's the easy way to handle the handful of cylinders on a
   typical bot.
5. **Ground the chassis** (or follow the add-in's `base_link` convention) so the exporter knows the
   base link.
6. **Turn OFF design history** (right-click the root ▸ Do not capture Design History) before
   exporting. Save a copy first if you want to keep the timeline.

## 3. Export

1. Run **ACDC4Robot** from the Add-Ins menu, choose **URDF**.
2. It writes a folder containing the `.urdf` plus a `meshes/` folder.
3. Drag that **entire folder** into this project's `Assets/` (the `.urdf` must stay next to its
   meshes) and let Unity finish importing.

## 4. Import and set up in Unity

1. **GameObject ▸ 3D Object ▸ URDF Model (import)**, pick your `.urdf`, keep the default axis (**Y**),
   import. A robot hierarchy appears in the scene.
2. Select the imported root, then **Tools ▸ RoboSim ▸ Robot ▸ Set Up Imported Robot** → press **Set
   Up Robot**. This one tool does everything: bakes the 10× world scale (1 unit = 0.1 m, gravity
   −98.1), computes/keeps mass, rebuilds inertia, swaps the importer's lumpy hulls for tight
   per-part boxes + rolling wheel spheres, wires the wheels to the joysticks, auto-wires every
   arm/piston mechanism to the controller buttons, tags the robot `Player` for the match loaders,
   and registers it in the home-screen robot list.
   - **Wheel Name Contains ("wheel")** — how it finds your drive wheels. Must match step 2.4.
   - **Compute Mass From Geometry (on)** — mass from mesh volume × a density matched to the part
     name, so you can skip Fusion materials. Turn it off (or use **Keep URDF Inertials**) if your
     CAD masses are trustworthy. Watch the Console: it warns for any part that fell back to the
     default density so you can rename it or assign a material.
   - **Validate Physics After (on)** — saves the scene, then simulates the robot headlessly to
     confirm it settles, drives, and turns before you press Play.
3. Press Play and drive.

> The individual stages live under **Robot ▸ Advanced** if you ever need to redo just one. Never
> use anything under **Legacy — Old Velocity Drive**; those rebuild the pre-motor setup and would
> strip the rig (Unity greys them out on a motor-driven robot).

## 5. Mechanisms wire themselves

You do **not** add any actuator components by hand. Set Up Imported Robot wires every non-wheel
joint automatically:

- **revolute / continuous** joints become hold-to-run **motors** (`MotorActuator`),
- **prismatic (slider)** joints become binary **pneumatic cylinders** (`PneumaticActuator`), with
  the extended/retracted targets taken straight from the joint's limits.

Each mechanism is registered so it appears on the home-screen controller-config screen, ready to
map to a button (per robot). To tune one, select its joint link and adjust the **MotorActuator**
(max RPM, invert) or **PneumaticActuator** (cylinder force, start extended) in the Inspector.

### 5.1 Fixing or adding a joint in Unity

If a part came in welded (a **fixed** joint) or with the wrong axis/limits — common when you didn't
finish jointing it in Fusion — fix it in Unity, no re-export needed:

1. **Tools ▸ RoboSim ▸ Robot ▸ Advanced ▸ Add or Fix Mechanism Joint**.
2. Pick the **Child Link** (the part that should move), the **Joint Type** (Revolute / Continuous /
   Prismatic / Fixed), and the **Axis** in the link's local frame.
3. Set the limits — **degrees** for a revolute, **scaled units** for a prismatic (the window shows
   the metric equivalent; 1 unit = 0.1 m). Press **Apply**.

It configures the joint and wires the actuator + button mapping exactly like an imported joint, so
the mechanism shows up in the controller-config screen immediately. Re-applying to the same link
replaces its mechanism, and **Fixed** removes it. On a **URDF** robot it retypes an existing link
(including one that imported as fixed). On a **mesh/FBX** robot the part has no joint yet, so it
**splits a new moving link off the chassis** — the part's meshes and colliders leave the chassis
body and become their own link, jointed where you set the anchor. Either way you never leave Unity;
the only thing it can't do is invent geometry that isn't there (model the part in CAD first).

## 6. Make it selectable in the home-screen picker

Set Up Imported Robot adds a **catalog entry** so your robot lists in the model picker, but the
picker spawns the entry's **prefab**, which a fresh import doesn't have yet. One tool does both:

1. Select the set-up robot root, then **Tools ▸ RoboSim ▸ Robot ▸ Save As Robot Prefab**.
2. Press **Save Prefab & Link to Picker**. It saves the robot to `Assets/Robots/<name>.prefab`,
   links that prefab to its catalog entry, and — with **Remove Inline Copy** on — deletes the scene
   instance so SampleScene's RobotSpawner doesn't spawn a duplicate at Play. Save the scene.

That's it: the robot now appears in the home-screen picker and spawns when selected.

> By hand instead: drag the robot into `Assets/Robots/` to make a prefab, then set that prefab on
> the robot's entry (Id = slug of its name) in `Assets/Settings/RobotModelCatalog.asset`. (The
> **Build Robot Prefabs & Spawner** tool only handles the built-in 360 drivetrain.)

## 7. Known caveats

- The Unity URDF importer hasn't been updated since ~2022 but still works in Unity 6. Expect rough
  edges, not failures.
- Articulated robots only support **convex** colliders. Concave shapes (goal pockets, C-channels)
  become filled-in convex hulls unless the setup's per-part boxes handle them.
- **Resetting the robot's pose at runtime is awkward** (importer issue #216) — prefer reloading the
  scene over teleporting the robot.
- **If the robot explodes or won't move on Play**, check that the root ArticulationBody is the link
  you intended as the base. The setup force-enables movement on the root, but a wrong base link
  needs re-grounding in Fusion.
- **A part is invisibly light or heavy?** Check the Console after setup — every link that fell back
  to the default density is logged by name. Rename the part to include its material, or assign a
  physical material in Fusion and re-run with **Keep URDF Inertials**.
