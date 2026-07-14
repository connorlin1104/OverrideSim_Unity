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
    // Everything below works from fill = meshVolume / referenceBoxVolume (the tighter of the
    // AABB/OBB). Solid-ish parts (gears ~0.79, boxes 1.0) keep the classic single box.
    private const float FillRatioSingleBox = 0.55f;
    // Below this fill, PLASTIC parts (RobotPartClassifier.IsPlastic: the Polycarb Funnel and
    // the 276-* Web panels) get VHACD convex hulls — the only shape that makes a dish/bend
    // physically real in every axis on a dynamic articulation (non-convex MeshColliders are
    // illegal there). The gate is name-based on purpose: a purely geometric one either missed
    // the funnel (a near-flat dish) or hulled ~29 gears/motor caps for 282 colliders.
    private const float FillRatioVhacd = 0.4f;
    private const int MaxSlabs = 6;
    // Slab decomposition is kept only when its boxes' total volume beats the single box by at
    // least this margin; a FLAT panel fails this and falls back to its (already tight) box.
    private const float SlabAcceptRatio = 0.7f;
    private const string SlabChildName = "_SlabCollider";
    private const uint VhacdMaxHulls = 10;
    private const uint VhacdResolution = 100000;
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
    public static Report Generate(GameObject root, string wheelNamePrefix = null)
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
        //    and _SlabCollider child objects first (so we don't orphan empties), then every
        //    remaining collider, then the previous run's persisted hull meshes.
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t != null && t.gameObject != null && (t.name == ObbChildName || t.name == SlabChildName))
                Undo.DestroyObjectImmediate(t.gameObject);
        }
        foreach (Collider col in root.GetComponentsInChildren<Collider>(true))
        {
            if (col != null) Undo.DestroyObjectImmediate(col);
        }
        AssetDatabase.DeleteAsset(HullAssetRootFolder + "/" + SanitizeFileName(root.name));

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

            // Everything inside the wheel subtrees is covered by the sphere.
            foreach (Transform node in cluster.nodes)
                foreach (MeshFilter mf in node.GetComponentsInChildren<MeshFilter>(true))
                    consumed.Add(mf);
        }

        // 3) Structural parts: one tight box per remaining mesh.
        foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
        {
            if (consumed.Contains(mf)) continue;
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;

            // Meshes sit on generic "Body1" leaf nodes, so the fastener name lives on an
            // ancestor group node — test the whole chain up to the root.
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

            // Prefer a PCA-oriented box when it is clearly tighter than the axis-aligned one
            // (long parts modeled diagonally inside their node). Both boxes are computed in mesh
            // space; an ancestor's uniform 10x scale applies to both identically.
            Vector3 aabbSize = mesh.bounds.size;
            float aabbVolume = aabbSize.x * aabbSize.y * aabbSize.z;
            bool useObb = TryComputeObb(mesh, out Vector3 obbCenter, out Quaternion obbRotation, out Vector3 obbSize)
                && obbSize.x * obbSize.y * obbSize.z < ObbMaxVolumeRatio * aabbVolume
                && Quaternion.Angle(Quaternion.identity, obbRotation) > ObbMinAngleDegrees;

            // Fill-ratio triage: how much of its reference box does the part actually occupy?
            // Open/non-manifold meshes yield garbage volumes (<= 0 or bigger than the box) —
            // those read as "solid" and keep the single-box path.
            Vector3 refSize = useObb ? obbSize : aabbSize;
            float refVolume = refSize.x * refSize.y * refSize.z;
            float meshVolume = ComputeMeshVolume(mesh);
            float fill = (meshVolume > 0f && !float.IsNaN(meshVolume) && meshVolume <= refVolume)
                ? meshVolume / Mathf.Max(refVolume, 1e-12f)
                : 1f;

            if (fill < FillRatioSingleBox)
            {
                // Very hollow plastic (the funnel's dish, the webs' bends): convex decomposition
                // follows the shape in every axis. Falls through to slabs when VHACD is
                // unavailable or produces nothing.
                if (fill < FillRatioVhacd && IsUnderPlastic(mf.transform, root.transform) &&
                    TryBuildVhacdHulls(mf, chassisMat, root.name, report))
                    continue;

                // Everything else hollow: slab boxes along the long axis; the volume-acceptance
                // test inside falls back to the single box when slabs don't actually win.
                if (TryBuildSlabColliders(mf, mesh, useObb ? obbRotation : Quaternion.identity,
                        chassisMat, report))
                    continue;
            }

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
                box.sharedMaterial = chassisMat;
                report.obbChildCount++;
            }
            else
            {
                BoxCollider box = Undo.AddComponent<BoxCollider>(mf.gameObject);
                box.center = mesh.bounds.center;
                box.size = mesh.bounds.size;
                box.sharedMaterial = chassisMat;
                report.boxCount++;
            }
        }
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
            Vector3 size = Vector3.Max(slabMax[s] - slabMin[s], Vector3.one * 1e-4f);
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
            hulls = VhacdNative.GenerateConvexMeshes(mf.sharedMesh, VhacdMaxHulls, VhacdResolution);
        }
        catch (System.Exception e)
        {
            // Native plugin missing/failed: the slab/box fallbacks still produce a usable robot.
            Debug.LogWarning($"Generate Part Colliders: VHACD failed on '{mf.name}' ({e.Message}); " +
                             "falling back to slab boxes.", mf);
            return false;
        }
        if (hulls == null || hulls.Count == 0) return false;

        string folder = EnsureHullFolder(rootName);
        // Mesh leaf nodes repeat generic names (Body1...), so the per-run part index keeps the
        // asset paths unique and deterministic.
        string baseName = $"{SanitizeFileName(mf.name)}_{report.vhacdParts}";
        for (int i = 0; i < hulls.Count; i++)
        {
            Mesh hull = hulls[i];
            hull.name = $"{baseName}_hull{i}";
            AssetDatabase.CreateAsset(hull, $"{folder}/{hull.name}.asset");
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
