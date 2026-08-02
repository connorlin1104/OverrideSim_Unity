using System.Collections.Generic;
using Unity.Robotics.UrdfImporter;
using UnityEditor;
using UnityEngine;

// Computes each URDF link's mass from its visual mesh volume and a density looked up from the
// part name, so a Fusion component exported WITHOUT a physical material still gets a sane mass.
//
// Why this is needed: a materialless Fusion part exports with mass 0, and the Unity URDF importer
// silently clamps that to 0.1 kg (UrdfInertial.minMass). It keeps no record of the original value,
// so a clamped 0.1 kg is indistinguishable from a genuinely-authored one — the only robust fix is
// to compute mass ourselves. VEX teams share a CAD library with consistent part names, which makes
// name -> density (RobotPartClassifier.DensityByToken) a reliable source.
//
// Units: ComputeMeshVolume returns raw mesh-LOCAL volume. This runs AFTER the post-processor's 10x
// scale bake, so a link's geometry lives in the project's scaled units (1 unit = 0.1 m). Convert
// mesh-local -> root-local with the transform determinant, then root-local units^3 -> m^3 with
// (1/scaleFactor)^3. Root-relative (not world) so a robot placed/scaled in the scene still masses
// correctly. ArticulationBody.mass is real kg (scale-independent) — set it directly; the caller
// then rebuilds each inertia tensor from the colliders so it matches the new mass.
public static class RobotMassFromGeometry
{
    private const string UndoName = "Compute Mass From Geometry";

    // The importer's zero-mass clamp (UrdfInertial.minMass). A link at exactly this reads as
    // "had no material in Fusion", so we may overwrite it; anything heavier is trusted as authored.
    private const float ClampMass = 0.1f;
    private const float ClampEpsilon = 1e-4f;

    // Floor so a thin/tiny part never lands at literally 0 kg (PhysX dislikes zero-mass links);
    // and a ceiling above which a computed mass is almost certainly an open/garbage mesh.
    private const float MinMass = 1e-3f;
    private const float MaxPlausibleKg = 25f;

    public struct Report
    {
        public int massedFromToken;   // name matched a density token
        public int keptAuthored;      // no token, but a real (> clamp) mass was already present
        public int usedDefault;       // no token and no authored mass -> DefaultDensity guess
        public int noVolume;          // no closed visual mesh to size from -> mass left as-is
        public float totalKg;
        public float minKg;
        public float maxKg;

        public string Summarize()
        {
            int massed = massedFromToken + usedDefault;
            return $"massed {massed} link(s) from geometry ({massedFromToken} by material name, " +
                   $"{usedDefault} by default density), kept {keptAuthored} authored mass(es), " +
                   $"{noVolume} without a usable mesh; total {totalKg:F2} kg " +
                   $"(min {(minKg == float.PositiveInfinity ? 0f : minKg):F3}, max {maxKg:F3})";
        }
    }

    public static Report Apply(GameObject root, float scaleFactor, float defaultDensity, bool useUndo)
    {
        float metersPerUnit = 1f / Mathf.Max(scaleFactor, 1e-6f);
        float m3PerUnit3 = metersPerUnit * metersPerUnit * metersPerUnit;
        Matrix4x4 worldToRoot = root.transform.worldToLocalMatrix;

        var report = new Report { minKg = float.PositiveInfinity, maxKg = 0f };

        foreach (UrdfLink link in root.GetComponentsInChildren<UrdfLink>(true))
        {
            ArticulationBody body = link.GetComponent<ArticulationBody>();
            if (body == null) continue;

            // Sum the real-world volume of this link's OWN visual meshes. Visuals groups are direct
            // children of the link; child LINKS are siblings of those groups, so this never pulls in
            // a descendant link's geometry.
            float volM3 = 0f;
            bool sawValidMesh = false;
            foreach (Transform child in link.transform)
            {
                if (child.GetComponent<UrdfVisuals>() == null) continue;
                foreach (MeshFilter mf in child.GetComponentsInChildren<MeshFilter>(true))
                {
                    Mesh mesh = mf.sharedMesh;
                    if (mesh == null) continue;
                    float vRaw = GeneratePartColliders.ComputeMeshVolume(mesh);
                    if (vRaw <= 0f || float.IsNaN(vRaw)) continue; // open/degenerate mesh — skip
                    float meshLocalToRoot = Mathf.Abs((worldToRoot * mf.transform.localToWorldMatrix).determinant);
                    volM3 += vRaw * meshLocalToRoot * m3PerUnit3;
                    sawValidMesh = true;
                }
            }

            if (useUndo) Undo.RecordObject(body, UndoName);

            bool authoredReal = body.mass > ClampMass + ClampEpsilon;
            if (!sawValidMesh)
            {
                report.noVolume++;
                Debug.LogWarning($"Mass From Geometry: '{link.name}' has no closed visual mesh to " +
                                 $"size mass from — kept {body.mass:F3} kg.", body);
            }
            else if (RobotPartClassifier.TryGetLinkDensity(link, out float density))
            {
                body.mass = Mathf.Max(volM3 * density, MinMass);
                report.massedFromToken++;
            }
            else if (authoredReal)
            {
                report.keptAuthored++; // a real material was assigned in Fusion — trust it
            }
            else
            {
                body.mass = Mathf.Max(volM3 * defaultDensity, MinMass);
                report.usedDefault++;
                Debug.LogWarning($"Mass From Geometry: '{link.name}' matched no material token and had " +
                                 $"no authored mass — used the default density {defaultDensity:F0} kg/m^3 " +
                                 $"-> {body.mass:F3} kg. Put a material name in the part (e.g. Polycarb, " +
                                 "Aluminum, Nylon, Steel) or assign a physical material in Fusion.", body);
            }

            if (body.mass < MinMass * 1.5f || body.mass > MaxPlausibleKg)
                Debug.LogWarning($"Mass From Geometry: '{link.name}' mass {body.mass:F4} kg looks " +
                                 "implausible — check for an open/non-manifold visual mesh.", body);

            report.totalKg += body.mass;
            report.minKg = Mathf.Min(report.minKg, body.mass);
            report.maxKg = Mathf.Max(report.maxKg, body.mass);
        }

        if (report.totalKg > 0f && (report.totalKg < 0.5f || report.totalKg > MaxPlausibleKg))
            Debug.LogWarning($"Mass From Geometry: total robot mass {report.totalKg:F2} kg is outside the " +
                             "typical VEX range (~0.5-25 kg) — check material names and mesh integrity.", root);

        return report;
    }

    // Real-world mass (kg) for one mesh subtree, using the same volume->density math as Apply but
    // for a plain FBX part (no UrdfLink/UrdfVisuals). Sums every MeshFilter under linkNode that
    // isn't owned by a deeper ArticulationBody (a child link's geometry). density is kg/m^3
    // (RobotPartClassifier density lookup); scaleFactor = 10 (1 unit = 0.1 m). Returns 0 when no
    // closed mesh is found, so the caller can fall back to a default mass. Used when the Add/Fix
    // Mechanism Joint tool splits a moving link off a mesh robot's chassis.
    public static float MassForLinkNode(GameObject linkNode, Transform robotRoot, float scaleFactor, float density)
    {
        if (linkNode == null || robotRoot == null) return 0f;
        float metersPerUnit = 1f / Mathf.Max(scaleFactor, 1e-6f);
        float m3PerUnit3 = metersPerUnit * metersPerUnit * metersPerUnit;
        Matrix4x4 worldToRoot = robotRoot.worldToLocalMatrix;

        float volM3 = 0f;
        foreach (MeshFilter mf in linkNode.GetComponentsInChildren<MeshFilter>(true))
        {
            if (IsUnderNestedBody(mf.transform, linkNode.transform)) continue;
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;
            float vRaw = GeneratePartColliders.ComputeMeshVolume(mesh);
            if (vRaw <= 0f || float.IsNaN(vRaw)) continue; // open/degenerate mesh — skip
            float meshLocalToRoot = Mathf.Abs((worldToRoot * mf.transform.localToWorldMatrix).determinant);
            volM3 += vRaw * meshLocalToRoot * m3PerUnit3;
        }
        return volM3 * density;
    }

    // Mass, geometric centre and world extent for a SET of part nodes, massed mesh by mesh with
    // each mesh's OWN material density — a lift is aluminium channel plus motors plus plastic, and
    // one blanket density over the lot is how a lift ends up weighing what a bag of screws does.
    //
    // TWO DIFFERENT WEIGHTINGS, on purpose. MASS skips meshes whose signed volume is garbage — the
    // open, non-manifold sheets a router-cut plate exports as — because a wrong mass is worse than
    // a missing one. The CENTRE cannot skip them: a plate still has a position, and dropping it
    // would drag the answer toward whichever parts happened to be watertight, which on a lift is
    // the motors and gears down at the bottom. So the centre is weighted by each mesh's BOUNDS
    // volume, which over-states a thin plate equally wherever it sits, and a centroid only needs
    // relative weights. Same approximation RobotBalanceWindow's ColliderVolume makes for convex
    // hulls, for the same reason.
    //
    // `worldBounds` is axis-aligned and built from each mesh's own scaled bounds, so a rotated part
    // inflates it — good enough for the radius-of-gyration estimate it exists for, not for contact.
    // Returns kg; the out params are meaningless when nothing was found (worldBounds.size is zero).
    public static float MassAndCentre(IEnumerable<GameObject> nodes, Transform robotRoot,
        float scaleFactor, out Vector3 worldCentre, out Bounds worldBounds)
    {
        worldCentre = robotRoot != null ? robotRoot.position : Vector3.zero;
        worldBounds = new Bounds(worldCentre, Vector3.zero);
        if (nodes == null || robotRoot == null) return 0f;

        float metersPerUnit = 1f / Mathf.Max(scaleFactor, 1e-6f);
        float m3PerUnit3 = metersPerUnit * metersPerUnit * metersPerUnit;
        Matrix4x4 worldToRoot = robotRoot.worldToLocalMatrix;

        float kg = 0f;
        Vector3 weighted = Vector3.zero;
        float weight = 0f;
        bool sawMesh = false;

        foreach (GameObject go in nodes)
        {
            if (go == null) continue;

            foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = mf.sharedMesh;
                if (mesh == null) continue;
                if (IsUnderNestedBody(mf.transform, go.transform)) continue;

                // Density PER MESH, walking up from the mesh to the node — not one density for the
                // whole node, which is what MassForLinkNode does and what its callers can get away
                // with because they name the node after the part. These nodes are named for their
                // ROLE ("ScoringMech", "FirstStageArms", "SecondStageArms"), which matches no
                // material token at all, so a single lookup silently masses an aluminium C-channel
                // assembly at the 1250 kg/m^3 default and the whole lift comes out weighing half
                // what it does. The part names are one level down, where the CAD put them.
                float density = RobotPartClassifier.DefaultDensity;
                for (Transform t = mf.transform; t != null; t = t.parent)
                {
                    if (RobotPartClassifier.TryGetDensity(t.name, out float d)) { density = d; break; }
                    if (t == go.transform) break;
                }

                float vRaw = GeneratePartColliders.ComputeMeshVolume(mesh);
                if (vRaw > 0f && !float.IsNaN(vRaw))
                {
                    float meshLocalToRoot =
                        Mathf.Abs((worldToRoot * mf.transform.localToWorldMatrix).determinant);
                    kg += vRaw * meshLocalToRoot * m3PerUnit3 * density;
                }

                Bounds local = mesh.bounds;
                Vector3 centre = mf.transform.TransformPoint(local.center);
                Vector3 lossy = mf.transform.lossyScale;
                var size = new Vector3(
                    Mathf.Abs(local.size.x * lossy.x),
                    Mathf.Abs(local.size.y * lossy.y),
                    Mathf.Abs(local.size.z * lossy.z));
                float v = size.x * size.y * size.z;
                if (v <= 0f) continue;

                weighted += centre * v;
                weight += v;
                var box = new Bounds(centre, size);
                if (!sawMesh) { worldBounds = box; sawMesh = true; }
                else worldBounds.Encapsulate(box);
            }
        }

        if (weight > 0f) worldCentre = weighted / weight;
        return kg;
    }

    // True when t is owned by an ArticulationBody strictly below linkRoot (a nested child link), so
    // its mesh shouldn't count toward linkRoot's mass. Reaching linkRoot first means t is linkRoot's.
    private static bool IsUnderNestedBody(Transform t, Transform linkRoot)
    {
        for (Transform p = t; p != null; p = p.parent)
        {
            if (p == linkRoot) return false;
            if (p.GetComponent<ArticulationBody>() != null) return true;
        }
        return false;
    }
}
