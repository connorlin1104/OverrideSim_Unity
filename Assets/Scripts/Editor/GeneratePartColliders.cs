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

    // Major sub-assemblies used for the RobotDriveController turn pivots (see FixRobotCollider).
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

    // PCA axis fitting samples at most this many vertices (min/max projection still uses all
    // vertices, so the box always contains the whole mesh).
    private const int MaxPcaSamples = 5000;

    // Per-run tally, returned so callers (batch entry, tests, other tools) can verify the result.
    public class Report
    {
        public int boxCount, obbChildCount, sphereCount, skippedFasteners, skippedDegenerate;
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
                  $"sphere(s), {report.boxCount} box(es), {report.obbChildCount} OBB box(es); skipped " +
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
        //    child objects first (so we don't orphan empties), then every remaining collider.
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t != null && t.gameObject != null && t.name == ObbChildName)
                Undo.DestroyObjectImmediate(t.gameObject);
        }
        foreach (Collider col in root.GetComponentsInChildren<Collider>(true))
        {
            if (col != null) Undo.DestroyObjectImmediate(col);
        }

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
            if (TryComputeObb(mesh, out Vector3 obbCenter, out Quaternion obbRotation, out Vector3 obbSize)
                && obbSize.x * obbSize.y * obbSize.z < ObbMaxVolumeRatio * aabbVolume
                && Quaternion.Angle(Quaternion.identity, obbRotation) > ObbMinAngleDegrees)
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

        // 4) Drive setup — all conditional, so the tool also runs cleanly on URDF/ArticulationBody
        //    hierarchies that have none of these components.
        RobotDriveController drive = root.GetComponent<RobotDriveController>();
        if (drive != null)
        {
            // Same semantics as FixRobotCollider: drivetrain rail centers are the turn pivots,
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
                  $"{report.boxCount} AABB box(es), {report.obbChildCount} OBB child box(es); skipped " +
                  $"{report.skippedFasteners} fastener mesh(es), {report.skippedDegenerate} decal mesh(es).", root);
        return report;
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
