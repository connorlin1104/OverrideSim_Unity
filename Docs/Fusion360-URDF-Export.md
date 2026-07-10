# Exporting Your Fusion 360 Robot into the Simulator (URDF)

This is the checklist for getting a VEX robot out of Fusion 360 and driving in RoboSim.
The path is: Fusion 360 → ACDC4Robot add-in → URDF folder → Unity URDF importer →
the VEX post-process tool. Follow it top to bottom the first time; after that you'll
mostly repeat steps 2–4.

## 1. One-time setup: install the exporter

1. In Fusion 360, open the **Autodesk Fusion App Store** (Utilities > Add-Ins > Fusion App Store).
2. Search for **ACDC4Robot** and install it (it's free and actively maintained).
3. Restart Fusion; the add-in appears under Utilities > Add-Ins.

## 2. Prepare the design in Fusion

URDF is much stricter than Fusion about how a robot is structured. Do these before every export:

1. **Set document units to meters** (Document Settings > Units). The exporter writes whatever
   the document uses, and the importer assumes meters — a millimeter document imports 1000x too big.
2. **Assign a physical material to EVERY component** (right-click > Physical Material).
   A component without one exports with **zero mass and inertia**, and the physics engine
   chokes on massless links.
3. **Flatten to one component per rigid part.** Each thing that moves as a unit (chassis,
   each wheel, lift arm, claw half) must be a single top-level component — **no nested
   components**. Use Combine/Join to merge bracket-and-panel sub-assemblies first.
4. **Connect the parts with assembly joints**, because the exporter turns Fusion joints
   into URDF joints:
   - **Revolute** joints for motor axles. Name every driven wheel component with
     **"wheel"** somewhere in its name (e.g. `wheel_FL`) — the post-processor finds the
     drivetrain by that substring.
   - **Slider** joints for pneumatic pistons.
5. **Ground the chassis** (or follow the add-in's `base_link` naming convention) so the
   exporter knows which link is the robot's base.
6. **Turn OFF design history** (right-click the root > Do not capture Design History)
   before exporting — the exporter operates on the final solids, and parametric timelines
   confuse it. Save a copy first if you want to keep the history.

## 3. Export

1. Run **ACDC4Robot** from the Add-Ins menu and choose **URDF** as the output format.
   Ignore the SDF and MJCF options — Unity only reads URDF.
2. The add-in writes a folder containing the `.urdf` file plus a `meshes/` folder.
3. Drag that **entire folder** into this project's `Assets/` folder (the `.urdf` must stay
   next to its meshes) and let Unity finish importing the meshes.

## 4. Import and post-process in Unity

1. In Unity: **GameObject > 3D Object > URDF Model (import)**, pick your `.urdf` file,
   keep the default axis (Y) and import. A robot hierarchy appears in the scene.
2. Select the imported robot root, then run **Tools > VEX > Post-Process Imported URDF
   Robot** and press **Run**. The options:
   - **Scale Factor (10)** — this project's world is 10x life size (1 unit = 0.1 m,
     gravity −98.1) so small VEX parts don't jitter in the physics engine. Real-meter
     URDFs must be baked up to match; leave it at 10.
   - **Replace Colliders With Part Boxes (on)** — swaps the importer's convex mesh hulls
     for tight per-part boxes and wheel spheres. Cheaper and rounder-rolling; turn it off
     only if your robot has weird shapes the boxes get wrong.
   - **Wheel Name Substring ("wheel")** — how the tool finds drive wheels. Must match the
     naming from step 2.4.
3. The tool scales the rig, rebuilds inertia, wires the wheels to on-screen joystick
   driving, tags the robot for the match loaders, and adds it to the home-screen robot
   list. Press Play and drive.

## 5. Pneumatics

Pistons import as prismatic joints but need an actuator component after post-processing:

1. Select the piston link (the one with the Slider joint) and add **PneumaticActuator**.
2. Set **Extended Target** and **Retracted Target** (the joint positions for the two
   endpoints — check the joint's lower/upper limits in the ArticulationBody).
3. Optionally set **Cylinder Force** (how hard the air pushes), **Start Extended**, and
   bind **Toggle Action** to a controller button.

## 6. Known caveats

- The Unity URDF importer hasn't been updated since ~2022, but it still works fine in
  Unity 6. Expect rough edges, not failures.
- Articulated robots only support **convex** colliders. Concave shapes (goal pockets,
  C-channels) become filled-in convex hulls unless the post-processor's part boxes handle them.
- **Resetting the robot's pose at runtime is awkward** (importer issue #216) — prefer
  reloading the scene over teleporting the robot.
- **If the robot explodes or won't move on Play**, check that the root ArticulationBody is
  the link you intended as the base (importer issue #210 family). The post-processor
  force-enables movement on the root, but a wrong base link needs re-grounding in Fusion.
- Collision meshes become VHACD convex-hull piles (slow, lumpy) unless you keep
  **Replace Colliders With Part Boxes** enabled.
