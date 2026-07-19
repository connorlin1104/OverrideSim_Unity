using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Per-part tight collider generation for the robot.
//
// The 3-box compound from Fix Robot Drive Collider drives well but is far too coarse for
// part-level contact (game pieces resting on the chassis, wheels rolling, future articulated
// arms). This tool rebuilds the robot's colliders from the actual part meshes:
//   - one SphereCollider per physical wheel (the FBX models each omni as two coincident halves —
//     RobotPartClassifier merges them into 6 clusters) with a grippy WheelPhysics material,
//   - one tight BoxCollider per structural part mesh: the mesh-local AABB, or — when the part sits
//     rotated inside its node (diagonal braces etc.) — a PCA-fitted oriented box on an
//     "_OBBCollider" child so the box hugs the part instead of its axis-aligned inflation,
//   - nothing for fasteners (spacers/screws/nuts/washers/collars/inserts — see
//     RobotPartClassifier.FastenerDenyList) and sub-millimeter decal meshes.
// Like Fix Robot Drive Collider it also writes the turn pivots, mass 30, and the Player tag —
// but only when those components exist, so it runs cleanly on URDF/ArticulationBody hierarchies.
//
// Usage: select the Robot root in the Hierarchy, then Tools > RoboSim > Robot > Advanced > Rebuild Part Colliders.
// Batch:  Unity -executeMethod GeneratePartColliders.RunBatchOnRobot (opens SampleScene, finds
//         the robot, regenerates, verifies 6 wheel clusters, saves).
public static class GeneratePartColliders
{
    private const string UndoName = "Generate Part Colliders";
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";

    // Major sub-assemblies used for the RobotDriveController turn pivots.
    private const string LeftGroup = "Drivetrain LS";
    private const string RightGroup = "Drivetrain RS";

    private const float RobotMass = 30f;
    private const int ExpectedWheelClusters = 6;

    // Wheel-ground contact must have real traction while the field's ZeroBounce material
    // (friction 0.6) uses the Minimum combine. Unity resolves each contact with the
    // higher-priority combine of the two materials — Average(0) < Multiply(1) < Minimum(2) <
    // Maximum(3) — so Maximum on the wheel wins and wheel-ground friction = max(0.8, 0.6) = 0.8.
    // With Average or Minimum the field would drag the wheels down to <= 0.6.
    // Known tradeoff: Maximum also outranks the pieces' 0.2/Minimum material, so a wheel that
    // touches a cup/pin grips it at 0.8 instead of the tuned 0.2 slide. Piece feel is tuned for
    // chassis/field contacts (which keep their combines); a gripping wheel reads as realistic
    // rubber-on-plastic, and PhysX has no per-pair combine override short of contact-modification
    // callbacks — accepted deliberately.
    private const string WheelMaterialPath = "Assets/WheelPhysics.physicMaterial";
    private const float WheelDynamicFriction = 0.8f;
    private const float WheelStaticFriction = 0.9f;

    // The chassis should slide along walls/pieces instead of sticking, so it takes the low
    // friction and the Minimum combine (the lower coefficient of any contact pair wins).
    private const string ChassisMaterialPath = "Assets/ChassisPhysics.physicMaterial";
    private const float ChassisFriction = 0.2f;

    // Meshes whose largest world-space extent is below this are stickers/decals painted onto a
    // part that already has its own collider — skip them. (0.005 world units = 0.5 mm real size
    // in this 10x-scale world.)
    private const float DecalMaxWorldExtent = 0.005f;

    // An oriented box replaces the axis-aligned one only when it is meaningfully tighter AND
    // meaningfully rotated; otherwise the plain AABB on the mesh's own GameObject is simpler.
    private const float ObbMaxVolumeRatio = 0.9f;
    private const float ObbMinAngleDegrees = 2f;
    private const string ObbChildName = "_OBBCollider";

    // --- Fill-ratio triage: tighter colliders for parts a single box grossly over-covers ---
    // fill = meshVolume / referenceBoxVolume (the AABB box). A PLASTIC part is convex-decomposed (VHACD)
    // UNLESS it reads as a near-solid block (fill at or above this), which keeps a cheap single box.
    // Hulls are the only thing that makes a dish/bend/cut physically real in every axis on a dynamic
    // articulation — non-convex MeshColliders are illegal there. The plastic gate is name-based
    // (IsUnderPlastic) on purpose: a purely geometric one hulled ~29 gears/motor caps for 282 colliders.
    //
    // This cutoff is deliberately HIGH (0.92, was 0.55). Polycarb is water-jet/router cut into a 2D
    // profile: a flat plate cut into an L / crescent / notched bracket fills 55-90% of its bounding
    // rectangle, so a 0.55 gate boxed exactly the cut plates the user needs hulled (the box fills in the
    // notch the part was cut to make). Only an essentially-full solid block (>= 0.92) now keeps a box.
    // Critically, an UNMEASURABLE mesh (open/non-manifold sheet, where the signed volume is garbage) is
    // treated as "decompose", NOT as "solid" — see TryMeasureFill. The old FillOf collapsed both to 1.0,
    // which silently boxed every non-watertight plastic plate however concave.
    private const float FillRatioPlasticHull = 0.92f;
    private const int MaxSlabs = 6;
    // Slab decomposition is kept only when its boxes' total volume beats the single box by at
    // least this margin; a FLAT panel fails this and falls back to its (already tight) box.
    private const float SlabAcceptRatio = 0.7f;
    private const string SlabChildName = "_SlabCollider";
    // Minimum emitted slab-box thickness (world units) so a thin sheet's slab isn't a near-zero-thickness
    // sliver that tunnels or reads as "not around the visual". ~2 mm at the 10x world scale.
    private const float MinSlabThickness = 0.02f;
    private const string GroupColliderChildName = "_GroupCollider";
    // Max convex hulls per PLASTIC part (VHACD is gated to plastic — see the structural loop). Polycarb is
    // usually router/water-jet cut into a specific 2D profile — moon slivers, hole-riddled web panels,
    // scalloped funnel lips — and only a hull cloud that follows that outline collides right; too few and a
    // piece rests on a collider that ignores the cut. This is a CAP, not a target: VHACD keeps splitting
    // only until it meets the concavity threshold, so a near-solid plate still yields a few hulls while a
    // heavily-cut profile is free to follow its curve. History: 10 in the first model, over-trimmed to 6
    // (too coarse for cut plates), then 32, now 64 (a complex aligner profile still read coarse at 32).
    // Plastic is a handful of parts so the extra hulls stay cheap — and it's a per-plastic-part cost, not
    // a per-robot one; the old "way too complex" blow-up came from hulling ~29 gears/motors, which the
    // plastic gate excludes. Bump this further if an intricate profile still reads coarse.
    private const uint VhacdMaxHulls = 64;
    // Voxel budget for the decomposition. Higher = each hull hugs the cut curve tighter and finer features
    // survive voxelization; needs to be generous enough that up to VhacdMaxHulls distinct regions actually
    // resolve (at 100k a thin sheet is only ~3 voxels thick). Editor-time only, a few plastic parts — cost
    // is fine; the single-part rebuild tool pays it for just one part.
    private const uint VhacdResolution = 1000000;
    // V-HACD split-sensitivity (see VhacdNative). NOTE: for a THIN cut plate these thresholds cannot make
    // it split — VHACD structurally collapses a flat part to one box-filling hull at ANY concavity (proven
    // directly against the dylib: a 122:1 plate stays 1 hull even at concavity=0, res=4M). VhacdNative
    // handles that by pre-stretching the plate's thin axis into a splittable aspect ratio; these knobs
    // then govern how finely the (now-thick) part decomposes. Values below are the measured sweet spot:
    // real cut plates split into 2 tight profile-following hulls; a genuinely solid block stays 1 hull.
    // Dial VhacdConcavity/VhacdDownsampling UP if decomposition gets too busy or too slow; DOWN if it
    // reads coarse. (For a plate that still reads as one blocky hull, the lever is VhacdNative's
    // PlateTargetThicknessFrac, not these.)
    private const double VhacdConcavity = 0.0005;
    private const uint VhacdDownsampling = 2;
    private const double VhacdMinVolumePerHull = 0.000001;
    // Hull meshes must be persisted or the saved scene's MeshColliders reference dead meshes.
    private const string HullAssetRootFolder = "Assets/RobotColliders";

    // PCA axis fitting samples at most this many vertices (min/max projection still uses all
    // vertices, so the box always contains the whole mesh).
    private const int MaxPcaSamples = 5000;

    // Per-run tally, returned so callers (batch entry, tests, other tools) can verify the result.
    public class Report
    {
        public int boxCount, obbChildCount, sphereCount, skippedFasteners, skippedDegenerate;
        public int slabParts, slabBoxCount, vhacdParts, hullCount;
        public List<RobotPartClassifier.WheelCluster> wheelClusters;
        // Human-readable reasons a PLASTIC-named part kept a box instead of getting convex hulls, so the
        // "why didn't my polycarb hull?" question is answered in the Console instead of by guessing.
        public List<string> plasticBoxNotes = new List<string>();
    }

    [MenuItem("Tools/RoboSim/Robot/Advanced/Rebuild Part Colliders", false, 1)]
    private static void GenerateFromSelection()
    {
        GameObject robot = Selection.activeGameObject;
        if (robot == null)
        {
            EditorUtility.DisplayDialog("Generate Part Colliders",
                "Select your Robot GameObject in the Hierarchy first.", "OK");
            return;
        }
        Generate(robot);
    }

    [MenuItem("Tools/RoboSim/Robot/Advanced/Remove Part Colliders", false, 2)]
    private static void RemoveCollidersFromSelection()
    {
        GameObject robot = Selection.activeGameObject;
        if (robot == null)
        {
            EditorUtility.DisplayDialog("Remove Part Colliders",
                "Select your Robot root in the Hierarchy first.", "OK");
            return;
        }

        int count = robot.GetComponentsInChildren<Collider>(true).Length;
        if (count == 0)
        {
            EditorUtility.DisplayDialog("Remove Part Colliders",
                $"'{robot.name}' has no colliders to remove.", "OK");
            return;
        }

        if (!EditorUtility.DisplayDialog("Remove Part Colliders",
            $"Remove ALL {count} collider(s) from '{robot.name}'?\n\n" +
            "Deletes every generated collider (wheel spheres, part boxes, the _OBBCollider/_SlabCollider " +
            "child objects, and convex-hull MeshColliders) plus this robot's saved hull meshes under " +
            "Assets/RobotColliders. Meshes and the rig are kept. Use this to clear over-generated colliders, " +
            "then run Rebuild Part Colliders to make clean ones.\n\n" +
            "Undo restores the box/sphere colliders, but the deleted plastic hull meshes are NOT restored — " +
            "undoing leaves the plastic parts with empty MeshColliders, so just run Rebuild Part Colliders " +
            "instead of undoing.",
            "Remove them", "Cancel"))
            return;

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Remove Part Colliders");
        int group = Undo.GetCurrentGroup();
        int removed = StripColliders(robot, useUndo: true);
        Undo.CollapseUndoOperations(group);

        EditorUtility.SetDirty(robot);
        EditorUtility.DisplayDialog("Remove Part Colliders",
            $"Removed {removed} collider(s) from '{robot.name}'.\n\n" +
            "Next: run Rebuild Part Colliders when you want fresh ones.", "OK");
        Debug.Log($"Remove Part Colliders: removed {removed} collider(s) from '{robot.name}'.", robot);
    }

    [MenuItem("Tools/RoboSim/Robot/Advanced/Rebuild Selected Part Colliders", false, 3)]
    private static void RebuildSelectedPart()
    {
        GameObject part = Selection.activeGameObject;
        if (part == null)
        {
            EditorUtility.DisplayDialog("Rebuild Selected Part Colliders",
                "Select the PART in the Hierarchy first — its named component node (e.g. \"Goal Aligner\" " +
                "or a wheel), not the whole robot.", "OK");
            return;
        }

        // If the user picked a generated collider holder (_OBBCollider/_SlabCollider/_GroupCollider),
        // walk up to the real part that owns it — a holder has no meshes of its own to rebuild.
        while (part != null &&
               (part.name == ObbChildName || part.name == SlabChildName || part.name == GroupColliderChildName))
            part = part.transform.parent != null ? part.transform.parent.gameObject : null;
        if (part == null)
        {
            EditorUtility.DisplayDialog("Rebuild Selected Part Colliders",
                "Select the named part node, not a generated _OBBCollider/_SlabCollider holder.", "OK");
            return;
        }

        // The whole robot? Send them to the whole-robot tool — this one is for a single part and would
        // strip every collider under the selection then rebuild only what it re-detects.
        if (part.GetComponent<RobotMotorController>() != null ||
            part.GetComponent<RobotDriveController>() != null ||
            part.GetComponent<RobotMechanisms>() != null)
        {
            EditorUtility.DisplayDialog("Rebuild Selected Part Colliders",
                $"'{part.name}' is the whole robot. Use Rebuild Part Colliders for the entire bot, or " +
                "select a single part under it for this tool.", "OK");
            return;
        }

        GameObject root = ResolveRobotRoot(part);
        if (root == null) root = part;
        // Resolve the wheel/structural decision up front (same call RebuildPart makes) so the dialog can
        // show it — a wheel becomes ONE sphere, so a mis-selected group is visible before it runs.
        bool asWheel = IsWheelPart(part, null);

        if (!EditorUtility.DisplayDialog("Rebuild Selected Part Colliders",
            $"Rebuild colliders for '{part.name}' only?\n\n" +
            $"(part of robot '{root.name}')\n\n" +
            $"Resolves to: {(asWheel ? "a WHEEL → one rolling sphere over the part" : "structural → plastic → convex hulls, everything else → one box")}\n\n" +
            "Auto-cleans just this part first — its colliders and its own saved hull meshes — then " +
            "regenerates only this part. The rest of the robot's colliders are untouched, so you can " +
            "re-run one bad part after tuning instead of rebuilding the whole bot.",
            "Rebuild it", "Cancel"))
            return;

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("Rebuild Selected Part Colliders");
        int group = Undo.GetCurrentGroup();
        Report report = RebuildPart(part, root);
        Undo.CollapseUndoOperations(group);

        EditorUtility.DisplayDialog("Rebuild Selected Part Colliders",
            $"Rebuilt '{part.name}':\n" +
            $"  {report.hullCount} convex hull(s) on {report.vhacdParts} concave part(s)\n" +
            $"  {report.boxCount + report.obbChildCount} box(es), {report.slabBoxCount} slab box(es)\n" +
            $"  {report.sphereCount} wheel sphere(s)\n" +
            $"  skipped {report.skippedFasteners} fastener(s), {report.skippedDegenerate} decal(s)", "OK");
    }

    // Rebuilds colliders for a SINGLE part subtree without touching the rest of the robot — the per-part
    // counterpart to Generate. Auto-cleans first: deletes THIS part's own persisted hull meshes (found via
    // its MeshColliders, so the other parts' hulls survive — a folder-wide wipe like StripColliders would
    // kill them all), strips this part's colliders and _OBB/_Slab/_Group holders, then re-runs the same
    // triage over just this subtree (wheel → sphere, plastic → hulls, else one box per component). `root`
    // is the robot root, used only for ancestor-chain classification + the hull folder name so the part
    // classifies identically to a whole-bot pass. Hull-asset delete/create is outside Undo (see Generate's
    // TryBuildVhacdHulls note) — the collider changes undo, but re-running is cleaner than undoing.
    public static Report RebuildPart(GameObject part, GameObject root, string wheelNamePrefix = null)
    {
        if (part == null) throw new System.ArgumentNullException(nameof(part));
        if (root == null) root = part;

        PhysicsMaterial wheelMat = GetOrCreateMaterial(WheelMaterialPath, "WheelPhysics",
            WheelDynamicFriction, WheelStaticFriction, PhysicsMaterialCombine.Maximum);
        PhysicsMaterial chassisMat = GetOrCreateMaterial(ChassisMaterialPath, "ChassisPhysics",
            ChassisFriction, ChassisFriction, PhysicsMaterialCombine.Minimum);
        AssetDatabase.SaveAssets();

        var report = new Report { wheelClusters = new List<RobotPartClassifier.WheelCluster>() };

        // 1) Auto-clean — delete THIS part's persisted hull meshes (the assets its MeshColliders point at,
        //    and only ones under our hull folder — never a shared FBX mesh), then strip its colliders and
        //    the _OBB/_Slab/_Group child holders. The robot's other hull assets are left in place.
        foreach (MeshCollider mc in part.GetComponentsInChildren<MeshCollider>(true))
        {
            if (mc == null || mc.sharedMesh == null) continue;
            string assetPath = AssetDatabase.GetAssetPath(mc.sharedMesh);
            if (!string.IsNullOrEmpty(assetPath) &&
                assetPath.StartsWith(HullAssetRootFolder + "/", System.StringComparison.Ordinal))
                AssetDatabase.DeleteAsset(assetPath);
        }
        foreach (Transform t in part.GetComponentsInChildren<Transform>(true))
        {
            // Skip `part` itself: GetComponentsInChildren includes the root, and if the caller passed a
            // holder-named node, destroying it here would invalidate `part` before the collider sweep.
            if (t != null && t != part.transform && t.gameObject != null &&
                (t.name == ObbChildName || t.name == SlabChildName || t.name == GroupColliderChildName))
                DestroyObject(t.gameObject, useUndo: true);
        }
        foreach (Collider col in part.GetComponentsInChildren<Collider>(true))
            if (col != null) DestroyObject(col, useUndo: true);

        // 2) A wheel gets ONE rolling sphere over the whole part (a boxed wheel can't roll); anything else
        //    goes through the structural triage scoped to this subtree.
        if (IsWheelPart(part, wheelNamePrefix))
            BuildWheelSphere(part, wheelMat, report);
        else
            BuildStructuralColliders(part, root, null, chassisMat, hullConcaveStructural: true, report);

        AssetDatabase.SaveAssets(); // flush any new hull meshes
        EditorUtility.SetDirty(part);
        if (part.scene.IsValid()) EditorSceneManager.MarkSceneDirty(part.scene);

        Debug.Log($"Rebuild Selected Part Colliders: '{part.name}' → {report.sphereCount} sphere(s), " +
                  $"{report.boxCount + report.obbChildCount} box(es), {report.slabBoxCount} slab box(es), " +
                  $"{report.hullCount} hull(s) on {report.vhacdParts} concave part(s).", part);
        LogPlasticBoxNotes(report, part);
        return report;
    }

    // The robot root for a selected part: the highest ancestor carrying rig data, else the selection
    // itself. Mirrors CleanRobotRig.ResolveRobotRoot so the per-part tool resolves the same root a full
    // rebuild ran on (matching hull-folder name + ancestor-chain classification).
    private static GameObject ResolveRobotRoot(GameObject sel)
    {
        if (sel == null) return null;
        RobotMechanisms reg = sel.GetComponentInParent<RobotMechanisms>();
        if (reg != null) return reg.gameObject;
        RobotMotorController motor = sel.GetComponentInParent<RobotMotorController>();
        if (motor != null) return motor.gameObject;
        RobotDriveController drive = sel.GetComponentInParent<RobotDriveController>();
        if (drive != null) return drive.gameObject;
        ArticulationBody ab = sel.GetComponentInParent<ArticulationBody>();
        if (ab != null)
        {
            Transform top = ab.transform;
            for (Transform t = top.parent; t != null; t = t.parent)
                if (t.GetComponent<ArticulationBody>() != null) top = t;
            return top.gameObject;
        }
        return sel; // nothing rigged yet — treat the selection as the robot
    }

    // True when the SELECTED node itself reads as a wheel by name — so the per-part rebuild gives it a
    // rolling sphere instead of a box. Matches the selected node's OWN name only, never its subtree: a
    // group/rail/robot-root that merely CONTAINS a wheel must go through the structural triage, or the
    // whole selection would be wrapped in one giant sphere. Uses the broad DefaultWheelTokens so it catches
    // omni/traction/flex on any bot; a wheel-assembly node ("Flexwheel w/ inserts", "3.25 AS Omni ...") is
    // itself wheel-named, so selecting the wheel still spheres correctly.
    private static bool IsWheelPart(GameObject part, string wheelNamePrefix)
    {
        string[] tokens = (string.IsNullOrEmpty(wheelNamePrefix)
            ? RobotPartClassifier.DefaultWheelTokens : wheelNamePrefix).Split(',');
        string n = RobotPartClassifier.NormalizeName(part.name);
        foreach (string tok in tokens)
        {
            string trimmed = tok.Trim();
            if (trimmed.Length > 0 && n.IndexOf(trimmed, System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    // One rolling SphereCollider sized to the part's whole renderer bounds, on the part node — the
    // single-part equivalent of the wheel-cluster sphere in Generate (which merges coincident omni halves;
    // here the user's selection already IS the wheel, so its combined bounds are the wheel).
    private static void BuildWheelSphere(GameObject part, PhysicsMaterial wheelMat, Report report)
    {
        Renderer[] rends = part.GetComponentsInChildren<Renderer>(true);
        if (rends.Length == 0) return;
        Bounds b = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);

        Transform node = part.transform;
        SphereCollider sphere = Undo.AddComponent<SphereCollider>(node.gameObject);
        sphere.center = node.InverseTransformPoint(b.center);
        Vector3 lossy = node.lossyScale;
        float scale = Mathf.Max(Mathf.Max(Mathf.Abs(lossy.x), Mathf.Abs(lossy.y)), Mathf.Abs(lossy.z));
        Vector3 s = b.size;
        float worldRadius = Mathf.Max(s.x, Mathf.Max(s.y, s.z)) * 0.5f;
        sphere.radius = worldRadius / Mathf.Max(scale, 1e-6f);
        sphere.sharedMaterial = wheelMat;
        report.sphereCount++;
    }

    // Removes every collider from the robot: the _OBBCollider/_SlabCollider child objects first (so we
    // don't orphan empties), then every remaining Collider, then this robot's persisted hull meshes
    // under Assets/RobotColliders. Returns how many colliders were present (and removed). Shared by
    // Generate (which rebuilds after) and the Remove Part Colliders menu item.
    public static int StripColliders(GameObject root, bool useUndo)
    {
        if (root == null) throw new System.ArgumentNullException(nameof(root));

        int removed = root.GetComponentsInChildren<Collider>(true).Length;

        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t != null && t.gameObject != null &&
                (t.name == ObbChildName || t.name == SlabChildName || t.name == GroupColliderChildName))
                DestroyObject(t.gameObject, useUndo);
        }
        foreach (Collider col in root.GetComponentsInChildren<Collider>(true))
        {
            if (col != null) DestroyObject(col, useUndo);
        }
        AssetDatabase.DeleteAsset(HullAssetRootFolder + "/" + SanitizeFileName(root.name));
        return removed;
    }

    private static void DestroyObject(Object obj, bool useUndo)
    {
        if (useUndo) Undo.DestroyObjectImmediate(obj);
        else Object.DestroyImmediate(obj);
    }

    // Batch entry for -executeMethod: opens the scene, finds the robot on its own, regenerates,
    // and throws (nonzero exit) if anything is off, so CI catches a broken robot import.
    public static void RunBatchOnRobot()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        // The robot is the tag-Player object carrying the drive controller; fall back to the
        // plain name in case a previous step has not tagged it yet.
        GameObject robot = null;
        foreach (RobotDriveController drive in Object.FindObjectsByType<RobotDriveController>(
                     FindObjectsInactive.Include))
        {
            if (robot == null || drive.CompareTag("Player")) robot = drive.gameObject;
        }
        if (robot == null) robot = GameObject.Find("Robot");
        if (robot == null)
            throw new System.InvalidOperationException(
                $"Generate Part Colliders: no GameObject 'Robot' with RobotDriveController in {ScenePath}.");

        Report report = Generate(robot);

        if (report.wheelClusters.Count != ExpectedWheelClusters)
            throw new System.InvalidOperationException(
                $"Generate Part Colliders: expected {ExpectedWheelClusters} wheel clusters but found " +
                $"{report.wheelClusters.Count} — scene NOT saved. Check the '{RobotPartClassifier.WheelNamePrefix}' nodes.");

        if (!EditorSceneManager.SaveScene(scene))
            throw new System.InvalidOperationException($"Generate Part Colliders: failed to save {ScenePath}.");

        Debug.Log($"Generate Part Colliders (batch): '{robot.name}' in {ScenePath} → {report.sphereCount} wheel " +
                  $"sphere(s), {report.boxCount} box(es), {report.obbChildCount} OBB box(es), " +
                  $"{report.slabBoxCount} slab box(es), {report.hullCount} convex hull(s); skipped " +
                  $"{report.skippedFasteners} fastener(s), {report.skippedDegenerate} decal(s). Scene saved.");
    }

    // Rebuilds all colliders under root. One collapsed Undo group so a single Ctrl+Z reverts it.
    // wheelNamePrefix selects which nodes get rolling SphereColliders instead of boxes; null
    // uses this project's drivetrain wheel name. Pass a new robot's wheel prefix when importing.
    // hullConcaveStructural true: concave PLASTIC (funnels/webs that pieces rest in) gets convex-hull
    // mesh colliders that follow their shape (the first-model behavior); false: boxes only. Either way
    // wheels get spheres and EVERY other component (metal, standoffs, sensors, motors, misc) gets ONE
    // box covering its whole part, oriented by its own node; fasteners and decals are skipped.
    public static Report Generate(GameObject root, string wheelNamePrefix = null, bool hullConcaveStructural = true)
    {
        Undo.SetCurrentGroupName(UndoName);
        int undoGroup = Undo.GetCurrentGroup();

        PhysicsMaterial wheelMat = GetOrCreateMaterial(WheelMaterialPath, "WheelPhysics",
            WheelDynamicFriction, WheelStaticFriction, PhysicsMaterialCombine.Maximum);
        PhysicsMaterial chassisMat = GetOrCreateMaterial(ChassisMaterialPath, "ChassisPhysics",
            ChassisFriction, ChassisFriction, PhysicsMaterialCombine.Minimum);
        AssetDatabase.SaveAssets();

        var report = new Report();

        // 1) Strip everything a previous run (or the old fix tools) left behind: the _OBBCollider
        //    and _SlabCollider child objects, every remaining collider, and the previous run's
        //    persisted hull meshes. Shared with the Remove Part Colliders menu item.
        StripColliders(root, useUndo: true);

        // 2) Wheels: one sphere per physical wheel, on the cluster's shallowest node.
        var clusters = RobotPartClassifier.FindWheelClusters(root, wheelNamePrefix);
        report.wheelClusters = clusters;
        var consumed = new HashSet<MeshFilter>();
        foreach (RobotPartClassifier.WheelCluster cluster in clusters)
        {
            SphereCollider sphere = Undo.AddComponent<SphereCollider>(cluster.topmost.gameObject);
            sphere.center = cluster.topmost.InverseTransformPoint(cluster.worldBounds.center);

            // The wheel's largest bounds dimension is its diameter. Converting the world radius
            // to local via the max |lossyScale| component matches how PhysX scales a sphere
            // radius; the FBX nodes are uniformly scaled (10,10,10) so all components agree.
            Vector3 lossy = cluster.topmost.lossyScale;
            float scale = Mathf.Max(Mathf.Max(Mathf.Abs(lossy.x), Mathf.Abs(lossy.y)), Mathf.Abs(lossy.z));
            Vector3 worldSize = cluster.worldBounds.size;
            float worldRadius = Mathf.Max(worldSize.x, Mathf.Max(worldSize.y, worldSize.z)) * 0.5f;
            sphere.radius = worldRadius / Mathf.Max(scale, 1e-6f);
            sphere.sharedMaterial = wheelMat;
            report.sphereCount++;

            // Everything physically inside the wheel is already covered by the sphere. Consume by WORLD
            // CONTAINMENT (a mesh whose bounds-center sits within the wheel sphere), not just the
            // wheel-named node's subtree — so a hub/insert/hex-adapter that is a SIBLING of the wheel
            // node (e.g. under a "Flexwheel w/ inserts" group) doesn't get its own box collider inside
            // the rolling wheel. Using the bounds CENTER keeps a long axle that merely passes through
            // the wheel from being consumed. Position-based, so it's robust to the post-rig hierarchy.
            foreach (Transform node in cluster.nodes)
                foreach (MeshFilter mf in node.GetComponentsInChildren<MeshFilter>(true))
                    consumed.Add(mf);
            Vector3 wheelCenter = cluster.worldBounds.center;
            float wheelR2 = worldRadius * worldRadius;
            float wheelDiameter = 2f * worldRadius;
            foreach (MeshFilter innerMf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (consumed.Contains(innerMf)) continue;
                Renderer rend = innerMf.GetComponent<Renderer>();
                if (rend == null) continue; // can't size it safely — leave its box
                Bounds b = rend.bounds;
                // Consume only a SMALL part CENTERED inside the wheel (a hub/insert/hex-adapter), not a
                // large part (a roller/bracket/sensor) that merely happens to be centered near the hub
                // but extends well past the tyre — bounding the extent keeps that part's own collider.
                if ((b.center - wheelCenter).sqrMagnitude <= wheelR2 &&
                    Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z)) <= wheelDiameter)
                    consumed.Add(innerMf);
            }
        }

        // 3) Structural parts — the first-model (654V v1) approach: concave PLASTIC (funnels / web panels
        //    pieces rest in) gets shape-following convex hulls; EVERYTHING else gets ONE box per whole
        //    component. Shared with the single-part rebuild tool (Rebuild Selected Part Colliders) via
        //    BuildStructuralColliders. Fasteners + decals are skipped.
        BuildStructuralColliders(root, root, consumed, chassisMat, hullConcaveStructural, report);
        AssetDatabase.SaveAssets(); // flush any hull meshes written above

        // 4) Drive setup — all conditional, so the tool also runs cleanly on URDF/ArticulationBody
        //    hierarchies that have none of these components.
        RobotDriveController drive = root.GetComponent<RobotDriveController>();
        if (drive != null)
        {
            // Legacy RobotDriveController path: drivetrain rail centers are the turn pivots,
            // whole-robot center is the straight-drive pivot; missing groups fall back to it.
            Vector3 overallCenter = Vector3.zero;
            if (RobotPartClassifier.TryGetGroupLocalBounds(root, null, out Vector3 allCenter, out _))
                overallCenter = allCenter;
            Vector3 leftPivot = RobotPartClassifier.TryGetGroupLocalBounds(root, LeftGroup, out Vector3 leftCenter, out _)
                ? leftCenter : overallCenter;
            Vector3 rightPivot = RobotPartClassifier.TryGetGroupLocalBounds(root, RightGroup, out Vector3 rightCenter, out _)
                ? rightCenter : overallCenter;

            SerializedObject so = new SerializedObject(drive);
            so.FindProperty("leftPivotOffset").vector3Value = leftPivot;
            so.FindProperty("rightPivotOffset").vector3Value = rightPivot;
            so.FindProperty("centerOffset").vector3Value = overallCenter;
            so.ApplyModifiedProperties();
        }

        Rigidbody rb = root.GetComponent<Rigidbody>();
        if (rb != null)
        {
            // Heavier robot pushes light pieces (mass 1) instead of riding up over them.
            Undo.RecordObject(rb, UndoName);
            rb.mass = RobotMass;
        }

        if (drive != null || rb != null)
        {
            // Tag so the match loaders can filter for the driving robot (not URDF imports).
            Undo.RecordObject(root, UndoName);
            root.tag = "Player";
        }

        EditorUtility.SetDirty(root);
        if (root.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.scene);
        Undo.CollapseUndoOperations(undoGroup);

        Debug.Log($"Generate Part Colliders: '{root.name}' → {report.sphereCount} wheel sphere(s), " +
                  $"{report.boxCount} AABB box(es), {report.obbChildCount} OBB child box(es), " +
                  $"{report.slabBoxCount} slab box(es) on {report.slabParts} sheet part(s), " +
                  $"{report.hullCount} convex hull(s) on {report.vhacdParts} concave part(s); skipped " +
                  $"{report.skippedFasteners} fastener mesh(es), {report.skippedDegenerate} decal mesh(es).", root);
        LogPlasticBoxNotes(report, root);
        return report;
    }

    // Signed tetrahedron sum over all triangles (all submeshes). Exact for closed manifold
    // meshes; garbage (near zero or negative) for open ones — callers must guard on the result.
    // Result is in the mesh's own local units cubed; scale by the transform determinant to get
    // world/real volume. Public so RobotMassFromGeometry can size link masses from it.
    public static float ComputeMeshVolume(Mesh mesh)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        if (vertices == null || triangles == null || triangles.Length < 3) return -1f;
        double volume = 0;
        for (int i = 0; i < triangles.Length; i += 3)
        {
            Vector3 a = vertices[triangles[i]];
            Vector3 b = vertices[triangles[i + 1]];
            Vector3 c = vertices[triangles[i + 2]];
            volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
        }
        return Mathf.Abs((float)volume);
    }

    // --- Part-group boxing helpers -------------------------------------------------------

    // Measures fill = meshVolume / AABB-box volume and reports whether the measurement is TRUSTWORTHY.
    // A closed manifold mesh gives a signed volume in (0, refVol]; an open/non-manifold sheet gives
    // garbage (<= 0, NaN, or bigger than its own box). Callers must NOT read the returned fill as "solid"
    // when this returns false — for a plastic part, unmeasurable means "decompose", not "box". The old
    // FillOf collapsed both cases to fill = 1.0, which silently boxed every non-watertight plastic plate
    // however concave; that was the main reason cut polycarb refused to hull.
    private static bool TryMeasureFill(Mesh mesh, out float fill)
    {
        Vector3 s = mesh.bounds.size;
        float refVol = s.x * s.y * s.z;
        float v = ComputeMeshVolume(mesh);
        if (v > 0f && !float.IsNaN(v) && v <= refVol && refVol > 1e-12f)
        {
            fill = v / refVol;
            return true;
        }
        fill = 1f;
        return false;
    }

    // The named component a mesh belongs to. Meshes sit on generic "Body1".."BodyN" leaves under a
    // named part group ("V5 Vision Sensor v1", "18 - 2x C-Chan v1", "0.750 Standoff"); return that
    // named node so ONE box covers the whole component instead of one box per mesh. `boundary` is the
    // highest node the walk may reach — the robot root in the whole-robot path, the selected part in a
    // single-part rebuild (they are equal in the whole-robot path). Never returns a node above it, so a
    // single-part rebuild can't escape the selection; falls back to the mesh's own node at the boundary.
    private static Transform PartGroupOf(Transform meshNode, Transform boundary)
    {
        Transform t = meshNode;
        while (t != null && t != boundary && IsGenericBodyName(t.name)) t = t.parent;
        return (t != null && t != boundary) ? t : meshNode;
    }

    // "Body", "Body1", "Body23" (+ importer ":N"/" (N)" suffixes NormalizeName strips) — the generic
    // per-body leaf names Fusion exports, which carry no part identity.
    private static bool IsGenericBodyName(string name)
    {
        string n = RobotPartClassifier.NormalizeName(name);
        if (n.Length < 4 || !n.StartsWith("Body", System.StringComparison.OrdinalIgnoreCase)) return false;
        for (int i = 4; i < n.Length; i++) if (!char.IsDigit(n[i])) return false;
        return true;
    }

    // The structural-collider triage, shared by the whole-robot Generate and the single-part rebuild.
    // For every non-consumed mesh under `scope`: fasteners + decals are skipped; concave PLASTIC gets
    // shape-following convex hulls (else falls through); everything else accumulates by component so it
    // gets ONE box. `root` is the robot root — used for the ancestor-chain classification (fastener /
    // plastic / simple-shape) and the hull asset folder name — so a part rebuilt in isolation classifies
    // and files its hulls exactly as it would in a whole-bot pass. `consumed` (may be null) is the set of
    // wheel-covered meshes to skip.
    private static void BuildStructuralColliders(GameObject scope, GameObject root,
        HashSet<MeshFilter> consumed, PhysicsMaterial chassisMat, bool hullConcaveStructural, Report report)
    {
        var partGroups = new Dictionary<Transform, List<MeshFilter>>();
        // How each PLASTIC-named part's meshes classified, keyed by the part's readable label. A Fusion
        // part is many generic "BodyN" leaf meshes under one named group, so tally per part and emit ONE
        // accurate note after the loop instead of a duplicate (and possibly self-contradictory) line per leaf.
        var plasticOutcomes = new Dictionary<string, PlasticPartOutcome>();
        foreach (MeshFilter mf in scope.GetComponentsInChildren<MeshFilter>(true))
        {
            if (consumed != null && consumed.Contains(mf)) continue;
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;

            // Meshes sit on generic "Body1" leaf nodes, so the fastener name lives on an ancestor
            // group node — test the whole chain up to the root.
            if (IsUnderFastener(mf.transform, root.transform))
            {
                report.skippedFasteners++;
                continue;
            }

            Vector3 absLossy = Abs(mf.transform.lossyScale);
            Vector3 worldExtents = Vector3.Scale(mesh.bounds.extents, absLossy);
            if (Mathf.Max(worldExtents.x, Mathf.Max(worldExtents.y, worldExtents.z)) < DecalMaxWorldExtent)
            {
                report.skippedDegenerate++;
                continue;
            }

            // Concave PLASTIC -> convex hulls that follow the shape (a dish/bend/cut a piece can sit in or
            // against), the one thing a box can't reproduce. Falls back to slab boxes, then the component
            // box below. Gated to plastic only (like the first model), and never on a motor/gear/sensor
            // (SimpleShape) even when it's bundled under a plastic assembly. hullConcaveStructural off
            // = boxes only. When a plastic part ends up boxed anyway, record WHY (report.plasticBoxNotes)
            // so "why didn't my polycarb hull?" is answered in the Console, not by digging in the scene.
            if (hullConcaveStructural && IsUnderPlastic(mf.transform, root.transform))
            {
                // Tally this leaf's outcome under its named part so ONE summary note is emitted later.
                PlasticPartOutcome outcome = GetPlasticOutcome(
                    plasticOutcomes, NamedPartLabel(mf.transform, root.transform));
                if (IsUnderSimpleShape(mf.transform, root.transform))
                {
                    // A motor/gear/sensor ancestor forces a box even on plastic — remember which one, since a
                    // mis-named CAD subassembly ("...Gearbox", "...Sensor Mount") shadowing a plate is a
                    // common and otherwise-invisible reason a polycarb part refuses to hull. Falls to the box.
                    outcome.simpleBox++;
                    outcome.simpleAncestor = FirstSimpleShapeAncestorName(mf.transform, root.transform);
                }
                else
                {
                    // Decompose unless the part is a measured near-solid block. An UNMEASURABLE mesh
                    // (open/non-manifold sheet) is decomposed, not assumed solid — that trap is exactly
                    // what boxed non-watertight cut plates before (see FillRatioPlasticHull / TryMeasureFill).
                    bool measured = TryMeasureFill(mesh, out float fill);
                    if (!measured || fill < FillRatioPlasticHull)
                    {
                        if (TryBuildVhacdHulls(mf, chassisMat, root.name, report)) { outcome.hulled++; continue; }
                        if (TryBuildSlabColliders(mf, mesh, Quaternion.identity, chassisMat, report))
                        { outcome.slabbed++; continue; }
                        outcome.failBox++; // VHACD + slab both declined; falls to the box below
                    }
                    else
                    {
                        outcome.solidBox++; // reads as a solid block; a single box is right, falls below
                        outcome.lastSolidFill = fill;
                    }
                }
            }

            // Everything else (metal, standoffs, sensors, motors, gears, misc, solid plastic) ->
            // collect the component's meshes so it gets ONE box (below). Bounded by `scope` (not `root`):
            // in the whole-robot path scope==root so this is unchanged, but in a single-part rebuild it
            // stops the box-owner walk at the selected part so a box is never placed on an ancestor
            // OUTSIDE the selection (which would touch "the rest of the robot" the tool promised not to).
            Transform group = PartGroupOf(mf.transform, scope.transform);
            if (!partGroups.TryGetValue(group, out List<MeshFilter> groupList))
            {
                groupList = new List<MeshFilter>();
                partGroups[group] = groupList;
            }
            groupList.Add(mf);
        }

        // One box per component (a standoff, C-channel, sensor, motor, ...), covering all its meshes.
        foreach (KeyValuePair<Transform, List<MeshFilter>> kv in partGroups)
            BuildPartGroupBox(kv.Key, kv.Value, chassisMat, report);

        // One diagnostic line per plastic part that did NOT fully convert to hulls (fully-hulled or
        // fully-slabbed parts stay silent — they came out shape-following, nothing to explain).
        foreach (KeyValuePair<string, PlasticPartOutcome> kv in plasticOutcomes)
        {
            string note = ComposePlasticNote(kv.Key, kv.Value);
            if (note != null) report.plasticBoxNotes.Add(note);
        }
    }

    // Per-named-part tally of how its meshes classified, so the plastic diagnostic emits one accurate note
    // per part (a Fusion part is many BodyN leaf meshes under one named group) rather than a duplicate,
    // possibly-contradictory line per leaf.
    private class PlasticPartOutcome
    {
        public int hulled, slabbed, solidBox, failBox, simpleBox;
        public string simpleAncestor;
        public float lastSolidFill;
    }

    private static PlasticPartOutcome GetPlasticOutcome(Dictionary<string, PlasticPartOutcome> map, string key)
    {
        if (!map.TryGetValue(key, out PlasticPartOutcome o)) { o = new PlasticPartOutcome(); map[key] = o; }
        return o;
    }

    // One human-readable line summarizing why a plastic part kept box collider(s), reflecting the mix
    // across its meshes (e.g. "'Goal Aligner': 1 region hulled, 2 kept a box: reads as a solid block ...").
    // Returns null when nothing was boxed (every mesh hulled or slabbed) — that part needs no explanation.
    private static string ComposePlasticNote(string name, PlasticPartOutcome o)
    {
        int boxed = o.solidBox + o.failBox + o.simpleBox;
        if (boxed == 0) return null;

        var reasons = new List<string>();
        if (o.simpleBox > 0)
            reasons.Add($"a simple-shape ancestor '{o.simpleAncestor}' (motor/gear/sensor) forces a box — " +
                        "rename it if the part is really plastic");
        if (o.solidBox > 0)
            reasons.Add($"reads as a solid block (fill {o.lastSolidFill:0.00} >= {FillRatioPlasticHull:0.00}), " +
                        "no cut-out to follow");
        if (o.failBox > 0)
            reasons.Add("VHACD and slab produced nothing (mesh may be non-manifold, or the VHACD plugin is missing)");

        int decomposed = o.hulled + o.slabbed;
        string prefix = decomposed > 0
            ? $"'{name}': {decomposed} region(s) decomposed, {boxed} kept a box"
            : $"'{name}' kept a box";
        return $"{prefix}: {string.Join("; ", reasons)}";
    }

    // One box for a whole component. A single-mesh component keeps the tight per-mesh box (PCA-oriented
    // when the part is modeled diagonally). A multi-mesh component (a sensor/motor exported as many
    // meshes) gets ONE box on the component node, sized in that node's own frame so it is oriented
    // ("angled") and centered on the whole part — not on the union of its offset mount hardware.
    private static void BuildPartGroupBox(Transform group, List<MeshFilter> meshes,
        PhysicsMaterial mat, Report report)
    {
        if (meshes.Count == 1)
        {
            BuildSingleMeshBox(meshes[0], meshes[0].sharedMesh, mat, report);
            return;
        }

        Matrix4x4 worldToGroup = group.worldToLocalMatrix;
        bool has = false;
        Vector3 min = Vector3.zero, max = Vector3.zero;
        foreach (MeshFilter mf in meshes)
        {
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;
            Matrix4x4 m = worldToGroup * mf.transform.localToWorldMatrix;
            Vector3 c = mesh.bounds.center, e = mesh.bounds.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = c + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y,
                    (i & 4) == 0 ? -e.z : e.z);
                Vector3 g = m.MultiplyPoint3x4(corner);
                if (!has) { min = max = g; has = true; }
                else { min = Vector3.Min(min, g); max = Vector3.Max(max, g); }
            }
        }
        if (!has) return;

        BoxCollider box = Undo.AddComponent<BoxCollider>(group.gameObject);
        box.center = (min + max) * 0.5f;
        box.size = max - min;
        box.sharedMaterial = mat;
        report.boxCount++;
    }

    // One tight box for a single mesh: a PCA-oriented box on an "_OBBCollider" child when the part is
    // modeled diagonally (and the transform preserves handedness), else the mesh-local AABB on the
    // mesh's own object. This is the first-model behavior for every structural part.
    private static void BuildSingleMeshBox(MeshFilter mf, Mesh mesh, PhysicsMaterial mat, Report report)
    {
        if (mesh == null) return;
        Vector3 aabbSize = mesh.bounds.size;
        float aabbVolume = aabbSize.x * aabbSize.y * aabbSize.z;
        bool useObb = TryComputeObb(mesh, out Vector3 obbCenter, out Quaternion obbRotation, out Vector3 obbSize)
            && obbSize.x * obbSize.y * obbSize.z < ObbMaxVolumeRatio * aabbVolume
            && Quaternion.Angle(Quaternion.identity, obbRotation) > ObbMinAngleDegrees
            && mf.transform.localToWorldMatrix.determinant >= 0f;
        if (useObb)
        {
            GameObject child = new GameObject(ObbChildName);
            Undo.RegisterCreatedObjectUndo(child, UndoName);
            child.transform.SetParent(mf.transform, false);
            child.transform.localPosition = obbCenter;
            child.transform.localRotation = obbRotation;
            BoxCollider box = Undo.AddComponent<BoxCollider>(child);
            box.center = Vector3.zero;
            box.size = obbSize;
            box.sharedMaterial = mat;
            report.obbChildCount++;
        }
        else
        {
            BoxCollider box = Undo.AddComponent<BoxCollider>(mf.gameObject);
            box.center = mesh.bounds.center;
            box.size = mesh.bounds.size;
            box.sharedMaterial = mat;
            report.boxCount++;
        }
    }

    // Slab decomposition along the part's longest axis (in the OBB frame when one exists):
    // splits the vertices into MaxSlabs bins and fits one tight box per occupied bin. Every
    // triangle edge that spans a bin boundary contributes its crossing point to BOTH sides, so
    // no geometry escapes between two slab boxes. Accepted only when the slabs' total volume
    // beats the single reference box by SlabAcceptRatio — a flat panel fails and falls back, a
    // BENT panel's boxes hug the bend.
    private static bool TryBuildSlabColliders(MeshFilter mf, Mesh mesh, Quaternion frame,
        PhysicsMaterial material, Report report)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;
        if (vertices == null || vertices.Length < 3) return false;

        Vector3 axis0 = frame * Vector3.right;
        Vector3 axis1 = frame * Vector3.up;
        Vector3 axis2 = frame * Vector3.forward;

        var projected = new Vector3[vertices.Length];
        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 v = vertices[i];
            projected[i] = new Vector3(Vector3.Dot(v, axis0), Vector3.Dot(v, axis1), Vector3.Dot(v, axis2));
            min = Vector3.Min(min, projected[i]);
            max = Vector3.Max(max, projected[i]);
        }
        Vector3 frameSize = max - min;
        int longAxis = frameSize.x >= frameSize.y && frameSize.x >= frameSize.z
            ? 0 : (frameSize.y >= frameSize.z ? 1 : 2);
        float slabWidth = frameSize[longAxis] / MaxSlabs;
        if (slabWidth <= 1e-6f) return false;

        var slabMin = new Vector3[MaxSlabs];
        var slabMax = new Vector3[MaxSlabs];
        var occupied = new bool[MaxSlabs];
        for (int s = 0; s < MaxSlabs; s++)
        {
            slabMin[s] = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            slabMax[s] = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        }

        int SlabOf(Vector3 p) =>
            Mathf.Clamp((int)((p[longAxis] - min[longAxis]) / slabWidth), 0, MaxSlabs - 1);
        void Grow(int slab, Vector3 p)
        {
            slabMin[slab] = Vector3.Min(slabMin[slab], p);
            slabMax[slab] = Vector3.Max(slabMax[slab], p);
            occupied[slab] = true;
        }

        foreach (Vector3 p in projected) Grow(SlabOf(p), p);

        if (triangles != null)
        {
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                for (int e = 0; e < 3; e++)
                {
                    Vector3 a = projected[triangles[t + e]];
                    Vector3 b = projected[triangles[t + (e + 1) % 3]];
                    int slabA = SlabOf(a);
                    int slabB = SlabOf(b);
                    if (slabA == slabB) continue;
                    int lo = Mathf.Min(slabA, slabB);
                    int hi = Mathf.Max(slabA, slabB);
                    float denominator = b[longAxis] - a[longAxis];
                    if (Mathf.Abs(denominator) < 1e-9f) continue;
                    for (int s = lo; s < hi; s++)
                    {
                        float boundary = min[longAxis] + slabWidth * (s + 1);
                        Vector3 crossing = Vector3.Lerp(a, b, Mathf.Clamp01((boundary - a[longAxis]) / denominator));
                        Grow(s, crossing);
                        Grow(s + 1, crossing);
                    }
                }
            }
        }

        float slabVolumeSum = 0f;
        int slabCount = 0;
        for (int s = 0; s < MaxSlabs; s++)
        {
            if (!occupied[s]) continue;
            Vector3 size = Vector3.Max(slabMax[s] - slabMin[s], Vector3.one * 1e-4f);
            slabVolumeSum += size.x * size.y * size.z;
            slabCount++;
        }
        if (slabCount < 2) return false;
        float referenceVolume = frameSize.x * frameSize.y * frameSize.z;
        if (slabVolumeSum >= SlabAcceptRatio * referenceVolume) return false;

        for (int s = 0; s < MaxSlabs; s++)
        {
            if (!occupied[s]) continue;
            Vector3 size = Vector3.Max(slabMax[s] - slabMin[s], Vector3.one * MinSlabThickness);
            Vector3 mid = (slabMax[s] + slabMin[s]) * 0.5f;
            GameObject child = new GameObject(SlabChildName);
            Undo.RegisterCreatedObjectUndo(child, UndoName);
            child.transform.SetParent(mf.transform, false);
            child.transform.localPosition = axis0 * mid.x + axis1 * mid.y + axis2 * mid.z;
            child.transform.localRotation = frame;
            BoxCollider box = Undo.AddComponent<BoxCollider>(child);
            box.center = Vector3.zero;
            box.size = size;
            box.sharedMaterial = material;
            report.slabBoxCount++;
        }
        report.slabParts++;
        return true;
    }

    // VHACD convex decomposition (our own arm64 build — see VhacdNative; editor-time only)
    // for hollow plastic parts like the Polycarb Funnel: several convex MeshColliders reproduce
    // the concavity — a dish a game piece can actually sit inside — which no single box or
    // hull can, and a dynamic articulation cannot carry non-convex MeshColliders. Hull meshes
    // are persisted under Assets/RobotColliders/<root>/ (a scene MeshCollider referencing an
    // in-memory mesh serializes as a dead reference).
    //
    // Known limitation: those assets are deleted/recreated OUTSIDE the Undo system, so undoing
    // a regeneration restores the previous MeshColliders but their hull assets may already be
    // gone (dead references). Re-run the tool instead of undoing it.
    private static bool TryBuildVhacdHulls(MeshFilter mf, PhysicsMaterial material, string rootName,
        Report report)
    {
        List<Mesh> hulls;
        try
        {
            hulls = VhacdNative.GenerateConvexMeshes(mf.sharedMesh, VhacdMaxHulls, VhacdResolution,
                VhacdConcavity, VhacdDownsampling, VhacdMinVolumePerHull);
        }
        catch (System.Exception e)
        {
            // Native plugin missing/failed: the slab/box fallbacks still produce a usable robot.
            Debug.LogWarning($"Generate Part Colliders: VHACD failed on '{mf.name}' ({e.Message}); " +
                             "falling back to slab boxes.", mf);
            return false;
        }
        if (hulls == null || hulls.Count == 0)
        {
            // VHACD ran but decomposed to nothing (usually a non-manifold/degenerate mesh). Warn so the
            // slab/box fallback that follows is never fully silent — mirrors the exception path above.
            Debug.LogWarning($"Generate Part Colliders: VHACD produced 0 hulls for '{mf.name}' " +
                             "(mesh may be non-manifold); falling back to slab boxes.", mf);
            return false;
        }

        string folder = EnsureHullFolder(rootName);
        // Mesh leaf nodes repeat generic names (Body1...), so the per-run part index keeps the
        // asset paths unique and deterministic.
        string baseName = $"{SanitizeFileName(mf.name)}_{report.vhacdParts}";
        for (int i = 0; i < hulls.Count; i++)
        {
            Mesh hull = hulls[i];
            hull.name = $"{baseName}_hull{i}";
            // A full rebuild wiped this folder first so the base name is free; a SINGLE-part rebuild does
            // NOT wipe the folder (it must preserve the other parts' hulls), so two parts whose meshes
            // share a generic BodyN name could otherwise write the same path and clobber each other's
            // asset. GenerateUniqueAssetPath appends " 1"/" 2" to avoid that.
            AssetDatabase.CreateAsset(hull, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{hull.name}.asset"));
            MeshCollider collider = Undo.AddComponent<MeshCollider>(mf.gameObject);
            collider.sharedMesh = hull;
            collider.convex = true;
            collider.sharedMaterial = material;
            report.hullCount++;
        }
        report.vhacdParts++;
        return true;
    }

    private static string EnsureHullFolder(string rootName)
    {
        if (!AssetDatabase.IsValidFolder(HullAssetRootFolder))
            AssetDatabase.CreateFolder("Assets", "RobotColliders");
        string sub = SanitizeFileName(rootName);
        string folder = HullAssetRootFolder + "/" + sub;
        if (!AssetDatabase.IsValidFolder(folder))
            AssetDatabase.CreateFolder(HullAssetRootFolder, sub);
        return folder;
    }

    private static string SanitizeFileName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.Length > 0 ? sb.ToString() : "part";
    }

    // True when the node or any ancestor up to (and including) root is deny-listed hardware.
    private static bool IsUnderFastener(Transform node, Transform root)
    {
        for (Transform t = node; t != null; t = t.parent)
        {
            if (RobotPartClassifier.IsFastener(t.name)) return true;
            if (t == root) break;
        }
        return false;
    }

    // True when the node or any ancestor up to (and including) root reads as a plastic part.
    private static bool IsUnderPlastic(Transform node, Transform root)
    {
        for (Transform t = node; t != null; t = t.parent)
        {
            if (RobotPartClassifier.IsPlastic(t.name)) return true;
            if (t == root) break;
        }
        return false;
    }

    // True when the node or any ancestor up to (and including) root is a motor/gearbox that should
    // stay a single simple box.
    private static bool IsUnderSimpleShape(Transform node, Transform root)
    {
        for (Transform t = node; t != null; t = t.parent)
        {
            if (RobotPartClassifier.IsSimpleShape(t.name)) return true;
            if (t == root) break;
        }
        return false;
    }

    // The first node on the chain node..root (inclusive) whose name reads as a motor/gear/sensor — the
    // ancestor responsible for a plastic part being forced to a single box. Diagnostics only.
    private static string FirstSimpleShapeAncestorName(Transform node, Transform root)
    {
        for (Transform t = node; t != null; t = t.parent)
        {
            if (RobotPartClassifier.IsSimpleShape(t.name)) return t.name;
            if (t == root) break;
        }
        return "(unknown)";
    }

    // A readable label for a mesh: the nearest non-generic ("BodyN") named ancestor up to root, else the
    // mesh's own node name. Diagnostics only — so a note names "Goal Aligner", not its "Body3" leaf.
    private static string NamedPartLabel(Transform node, Transform root)
    {
        for (Transform t = node; t != null; t = t.parent)
        {
            if (!IsGenericBodyName(t.name)) return t.name;
            if (t == root) break;
        }
        return node.name;
    }

    // One Console warning listing every plastic part that kept a box (and why), so the reason is visible
    // after a rebuild instead of requiring a dig through the hierarchy. No-op when every plastic hulled.
    private static void LogPlasticBoxNotes(Report report, Object context)
    {
        if (report == null || report.plasticBoxNotes == null || report.plasticBoxNotes.Count == 0) return;
        Debug.LogWarning(
            $"Part Colliders: {report.plasticBoxNotes.Count} plastic part(s) did not fully convert to " +
            "convex hulls:\n  - " + string.Join("\n  - ", report.plasticBoxNotes), context);
    }

    private static PhysicsMaterial GetOrCreateMaterial(string path, string name,
        float dynamicFriction, float staticFriction, PhysicsMaterialCombine frictionCombine)
    {
        PhysicsMaterial mat = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
        if (mat == null)
        {
            mat = new PhysicsMaterial(name);
            AssetDatabase.CreateAsset(mat, path);
        }
        // Always (re)apply the values so this tool stays the single authority for them.
        mat.dynamicFriction = dynamicFriction;
        mat.staticFriction = staticFriction;
        mat.bounciness = 0f;
        mat.frictionCombine = frictionCombine;
        mat.bounceCombine = PhysicsMaterialCombine.Minimum;
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // PCA-fits an oriented box to the mesh, in mesh-local space. Axes come from the vertex
    // covariance (sampled), the box from projecting ALL vertices onto those axes, so the result
    // always contains the mesh. Editor-time vertex reads work even with isReadable: 0.
    private static bool TryComputeObb(Mesh mesh, out Vector3 center, out Quaternion rotation, out Vector3 size)
    {
        center = Vector3.zero;
        rotation = Quaternion.identity;
        size = Vector3.zero;

        Vector3[] vertices = mesh.vertices;
        if (vertices == null || vertices.Length < 3) return false;

        int stride = Mathf.Max(1, vertices.Length / MaxPcaSamples);

        // Sample mean.
        double mx = 0, my = 0, mz = 0;
        int sampleCount = 0;
        for (int i = 0; i < vertices.Length; i += stride)
        {
            mx += vertices[i].x; my += vertices[i].y; mz += vertices[i].z;
            sampleCount++;
        }
        mx /= sampleCount; my /= sampleCount; mz /= sampleCount;

        // Symmetric 3x3 covariance of the sampled vertices.
        double[,] cov = new double[3, 3];
        for (int i = 0; i < vertices.Length; i += stride)
        {
            double dx = vertices[i].x - mx, dy = vertices[i].y - my, dz = vertices[i].z - mz;
            cov[0, 0] += dx * dx; cov[0, 1] += dx * dy; cov[0, 2] += dx * dz;
            cov[1, 1] += dy * dy; cov[1, 2] += dy * dz;
            cov[2, 2] += dz * dz;
        }
        cov[1, 0] = cov[0, 1]; cov[2, 0] = cov[0, 2]; cov[2, 1] = cov[1, 2];

        JacobiEigenvectors(cov, out Vector3 e0, out Vector3 e1, out Vector3 e2);

        // Orthonormalize (Gram-Schmidt; the third axis by cross product guarantees right-handed).
        Vector3 axis0 = e0.normalized;
        if (axis0.sqrMagnitude < 0.5f) return false; // degenerate covariance (all points coincident)
        Vector3 axis1 = e1 - Vector3.Dot(e1, axis0) * axis0;
        if (axis1.sqrMagnitude < 1e-12f) axis1 = Mathf.Abs(axis0.x) < 0.9f ? Vector3.right : Vector3.up;
        axis1 = (axis1 - Vector3.Dot(axis1, axis0) * axis0).normalized;
        Vector3 axis2 = Vector3.Cross(axis0, axis1);

        // Tight min/max along the axes, over every vertex.
        Vector3 min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        Vector3 max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
        foreach (Vector3 v in vertices)
        {
            Vector3 p = new Vector3(Vector3.Dot(v, axis0), Vector3.Dot(v, axis1), Vector3.Dot(v, axis2));
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        Vector3 mid = (min + max) * 0.5f;
        center = axis0 * mid.x + axis1 * mid.y + axis2 * mid.z;
        // Basis with right = axis0, up = axis1, forward = axis2 (right-handed by construction).
        rotation = Quaternion.LookRotation(axis2, axis1);
        size = max - min;
        return true;
    }

    // Eigenvectors of a symmetric 3x3 matrix via cyclic Jacobi rotations: repeatedly zero the
    // largest off-diagonal entries with Givens rotations; the accumulated rotations' columns
    // converge to the eigenvectors. A handful of sweeps is plenty for 3x3.
    private static void JacobiEigenvectors(double[,] a, out Vector3 e0, out Vector3 e1, out Vector3 e2)
    {
        double[,] v = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        for (int sweep = 0; sweep < 32; sweep++)
        {
            double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
            if (off < 1e-24) break;
            for (int p = 0; p < 2; p++)
            {
                for (int q = p + 1; q < 3; q++)
                {
                    if (System.Math.Abs(a[p, q]) < 1e-18) continue;
                    double theta = (a[q, q] - a[p, p]) / (2.0 * a[p, q]);
                    double t = theta >= 0
                        ? 1.0 / (theta + System.Math.Sqrt(theta * theta + 1.0))
                        : -1.0 / (-theta + System.Math.Sqrt(theta * theta + 1.0));
                    double c = 1.0 / System.Math.Sqrt(t * t + 1.0);
                    double s = t * c;

                    // a = Jᵀ a J ; v = v J with J the (p,q) Givens rotation. Explicit 3x3
                    // multiplies keep this obviously correct; cost is negligible.
                    double[,] j = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
                    j[p, p] = c; j[q, q] = c; j[p, q] = s; j[q, p] = -s;
                    double[,] aj = Multiply(a, j);
                    double[,] jt = Transpose(j);
                    CopyInto(Multiply(jt, aj), a);
                    CopyInto(Multiply(v, j), v);
                }
            }
        }
        e0 = new Vector3((float)v[0, 0], (float)v[1, 0], (float)v[2, 0]);
        e1 = new Vector3((float)v[0, 1], (float)v[1, 1], (float)v[2, 1]);
        e2 = new Vector3((float)v[0, 2], (float)v[1, 2], (float)v[2, 2]);
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        double[,] r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int k = 0; k < 3; k++)
                for (int j = 0; j < 3; j++)
                    r[i, j] += a[i, k] * b[k, j];
        return r;
    }

    private static double[,] Transpose(double[,] a)
    {
        double[,] r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i, j] = a[j, i];
        return r;
    }

    private static void CopyInto(double[,] source, double[,] target)
    {
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                target[i, j] = source[i, j];
    }

    private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));
}
