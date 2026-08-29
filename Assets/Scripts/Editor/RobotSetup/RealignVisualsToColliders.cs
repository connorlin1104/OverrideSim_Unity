using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Puts a robot's visuals back where its colliders say they belong.
//
// THE SYMPTOM: on Darwinbot some parts draw far from their (correct) green collider wireframes. No
// transform edit can do that on a Fusion export: the mesh leaves sit at ~identity apart from the
// importer's uniform 0.3937 unit scale (all 1,639 of Darwinbot's carry it) and the VERTICES carry the
// placement. What can do it is the MeshFilter resolving to a different Body1.NNN sub-mesh of the
// same FBX. The FBX was deleted and fetched back (08-23) and stowed/fetched again (08-27); a re-import
// re-mints the sub-mesh fileIDs, so a pointer that resolves at all may resolve to the wrong part.
//
// The collider does not follow, and that is the whole lever. A same-GO BoxCollider's center/size are
// a SERIALIZED SNAPSHOT of mesh.bounds at generation time, an _OBBCollider child's frame is a snapshot
// of the PCA fit, the hull .assets under Assets/RobotColliders are baked copies in the part's own
// space — so the collider still says where the part WAS, which is exactly the signature needed to find
// the part again. (ModelStore's census cannot: its fingerprint is captured at stow time, so a scramble
// that predates the stow is invisible to it.)
//
// Two halves, and the first is the deliverable:
//   1. DIAGNOSE. For every MeshFilter, derive where the collider says the mesh-local geometry was
//      (the Evidence kinds below) and compare with where the current mesh's bounds say it is:
//      OK / MOVED (same size, different centre) / RESHAPED (different size) / UNVERIFIABLE (nothing on
//      the chain — the generator never gives fasteners or decals a collider, so most of a robot's
//      leaves are honestly unverifiable; the report tallies them by part rather than listing them).
//   2. REPAIR by re-binding. Candidates are every Mesh in the SAME asset file as the current mesh
//      whose bounds satisfy the signature. Exactly one → re-bind the MeshFilter. Several → AMBIGUOUS,
//      never guess: two coincident copies of one part (an omni's two halves) is the common case, and
//      a guess would draw the wrong half with no way to tell. None → UNMATCHED. The diagnosis re-runs
//      afterwards and reports what remains.
//
// Group boxes (one box on a named node over several generic leaves) and wheel spheres are judged but
// never repaired: a union or a containment test cannot pick one candidate. They still count as
// MOVED/RESHAPED, so a displaced wheel half fails the batch repair and the validator rather than
// hiding behind "detect-only".
//
// Usage: select the robot (Hierarchy, Prefab Mode, or the prefab asset in the Project window), then
//   Tools > RoboSim > Robot > Advanced > Realign Visuals To Colliders               (repairs, asks first)
//   Tools > RoboSim > Robot > Advanced > Realign Visuals To Colliders (Report Only)
// Batch: -executeMethod RealignVisualsToColliders.RunBatchReport   prints every robot's table, never saves
//        -executeMethod RealignVisualsToColliders.RunBatchRepair   re-binds or shifts, saves changed prefabs,
//                                                                   and throws if any MOVED/RESHAPED remains
//
// THE SECOND REPAIR — SHIFT. On Darwinbot the re-bind found nothing: all 166 MOVED parts were UNMATCHED,
// the same size as their snapshot but translated, left/right twins by identical amounts, and the big
// ones exactly the parts a CAD user repositions (hard stops, axle supports, a cylinder). That is not
// a scrambled pointer; it is a different EXPORT of the same robot under colliders generated from
// another. The vertices moved, so the fix moves the leaf's transform by the same amount and puts its
// colliders back where they were — see TryShift for the arithmetic and why not a mesh copy.
public static class RealignVisualsToColliders
{
    private const string Title = "Realign Visuals To Colliders";
    private const string UndoName = Title;
    private const string HullFolderPrefix = "Assets/RobotColliders/";

    // Tolerances, per evidence. A same-GO box IS mesh.bounds at generation time, so it must agree to
    // float precision plus a hair. Hulls are VHACD's approximation of the part, slabs are inflated to
    // MinSlabThickness on their thin axis, and a group box unions leaves through two transforms — those
    // get 5 % + 0.01 (mesh-local; the table's world column applies the leaf's lossyScale).
    private const float BoxRelTol = 0.01f;
    private const float BoxAbsTol = 1e-4f;
    private const float LooseRelTol = 0.05f;
    private const float LooseAbsTol = 0.01f;
    // "Inside the OBB": the bounds centre may sit 10 % of the OBB's LONGEST side outside any face. Per-
    // axis slack would be wrong for a thin plate — its AABB centre legitimately sits off the plate's
    // plane by more than the plate is thick when the outline is asymmetric.
    private const float ObbSlack = 0.10f;
    private const float ObbDiagonalTol = 1.05f;
    // A wheel sphere covers a cluster of bodies (two coincident halves, hub, inserts), so the only thing
    // it pins is that each body's centre is inside it. World units; 0.05 = 5 mm real.
    private const float WheelExpand = 1.10f;
    private const float WheelAbsTol = 0.05f;

    public enum PartClass { Ok, Moved, Reshaped, Unverifiable }
    public enum Evidence { None, Box, Hulls, Obb, Slabs, GroupBox, WheelSphere }
    public enum RepairOutcome { NotAttempted, Rebound, Ambiguous, Unmatched, DetectOnly, Shifted, Manual }

    // Where the collider says the geometry was. Box/Hulls/Slabs/GroupBox carry an expected AABB (in
    // mesh-local space, or the group node's space); Obb carries a frame the bounds centre must sit
    // inside; WheelSphere a world-space sphere. Classify() takes the ACTUAL bounds in that same frame,
    // and it is one function on purpose: the repair uses it to test candidates, so a re-bound part is
    // guaranteed to re-diagnose OK with the same rule that flagged it.
    internal sealed class Signature
    {
        public Evidence evidence;
        public bool detectOnly;
        public Bounds expected;
        public float relTol, absTol;
        public Vector3 obbCenter, obbSize;
        public Quaternion obbRotation = Quaternion.identity;
        public Vector3 sphereCenter;
        public float sphereRadius;

        public PartClass Classify(Bounds actual, out float centreDelta)
        {
            switch (evidence)
            {
                case Evidence.Box:
                case Evidence.Hulls:
                case Evidence.Slabs:
                case Evidence.GroupBox:
                    return ClassifyAabb(actual, out centreDelta);
                case Evidence.Obb:
                    return ClassifyObb(actual, out centreDelta);
                case Evidence.WheelSphere:
                    centreDelta = (actual.center - sphereCenter).magnitude;
                    return centreDelta <= sphereRadius * WheelExpand + WheelAbsTol ? PartClass.Ok : PartClass.Moved;
                default:
                    centreDelta = 0f;
                    return PartClass.Unverifiable;
            }
        }

        // Size first: a different size is a different part (RESHAPED) whatever its centre says, and
        // the repair's candidate test has to reject a same-centre different-size mesh for that reason.
        private PartClass ClassifyAabb(Bounds actual, out float centreDelta)
        {
            float tol = relTol * MaxComponent(expected.size) + absTol;
            Vector3 dCentre = actual.center - expected.center;
            centreDelta = dCentre.magnitude;
            if (MaxAbsComponent(actual.size - expected.size) > tol) return PartClass.Reshaped;
            if (MaxAbsComponent(dCentre) > tol) return PartClass.Moved;
            return PartClass.Ok;
        }

        // The OBB is tight around the vertices along its own axes, so three things hold for the mesh it
        // was fitted to and fail for a different part: no AABB extent exceeds the OBB diagonal (a chord
        // of the box), the AABB diagonal is at least the OBB's longest side (the two extreme vertices
        // along that axis are at least that far apart), and the bounds centre sits inside the box.
        private PartClass ClassifyObb(Bounds actual, out float centreDelta)
        {
            Vector3 offset = actual.center - obbCenter;
            centreDelta = offset.magnitude;
            float diagonal = obbSize.magnitude;
            float longest = MaxComponent(obbSize);
            if (MaxComponent(actual.size) > diagonal * ObbDiagonalTol + absTol) return PartClass.Reshaped;
            if (actual.size.magnitude < longest / ObbDiagonalTol - absTol) return PartClass.Reshaped;
            Vector3 local = Quaternion.Inverse(obbRotation) * offset;
            for (int i = 0; i < 3; i++)
                if (Mathf.Abs(local[i]) > 0.5f * obbSize[i] + ObbSlack * longest + absTol) return PartClass.Moved;
            return PartClass.Ok;
        }
    }

    // One judged part: a single MeshFilter, or — for a group box — the group node and every leaf the
    // box was unioned from.
    public sealed class PartRow
    {
        public string path;                 // hierarchy path from the robot root
        public Evidence evidence;
        public PartClass cls;
        public bool detectOnly;
        public float centreDelta;           // |Δcentre| in the signature's frame (mesh-local for the repairable kinds)
        public float centreDeltaWorld;      // the same, in world units — what the Scene view shows
        public MeshFilter filter;           // null for a group row
        public List<MeshFilter> members;    // group rows only
        public Transform groupNode;         // group rows only: the node the box sits on (the union's frame)
        internal Signature signature;
        public RepairOutcome outcome;
        public string outcomeNote;

        public bool IsMismatch => cls == PartClass.Moved || cls == PartClass.Reshaped;
    }

    public sealed class RobotReport
    {
        public string robotName;
        public string assetPath;
        public readonly List<PartRow> rows = new List<PartRow>();
        public int meshFilters;
        public int okCount, movedCount, reshapedCount, unverifiableCount;
        public int groupRows, groupLeaves, wheelRows;
        // UNVERIFIABLE leaves tallied by the named part they sit under, so 900 of them read as one
        // line of "Screw ×301, Nut ×200" rather than 900 lines saying nothing.
        public readonly Dictionary<string, int> unverifiableByPart = new Dictionary<string, int>();
        // Filled by Repair().
        public bool repaired;
        public int reboundCount, shiftedCount, ambiguousCount, unmatchedCount, detectOnlyCount, manualCount;
        public readonly List<PartRow> remaining = new List<PartRow>();

        public int Mismatches => movedCount + reshapedCount;
        public bool Changed => reboundCount + shiftedCount > 0;

        public string SummaryLine()
        {
            string tail = repaired
                ? $"; repair: {reboundCount} re-bound, {shiftedCount} SHIFTED, {ambiguousCount} AMBIGUOUS, {unmatchedCount} UNMATCHED, " +
                  $"{manualCount} MANUAL, {detectOnlyCount} detect-only → {remaining.Count} remain"
                : "";
            return $"{robotName}: {meshFilters} mesh(es) — {okCount} OK, {movedCount} MOVED, {reshapedCount} RESHAPED, " +
                   $"{unverifiableCount} UNVERIFIABLE{tail}";
        }

        // The human-readable table. Mismatches always; OK rows only when asked (the batch report asks,
        // so the log shows coverage as well as failures).
        public string Table(bool includeOk = false)
        {
            var sb = new StringBuilder();
            sb.Append(Title).Append(" — ").Append(robotName);
            if (!string.IsNullOrEmpty(assetPath)) sb.Append(" (").Append(assetPath).Append(')');
            sb.AppendLine();
            sb.Append("  ").Append(meshFilters).Append(" MeshFilter(s) in ").Append(rows.Count)
              .Append(" row(s): ").Append(okCount).Append(" OK, ").Append(movedCount).Append(" MOVED, ")
              .Append(reshapedCount).Append(" RESHAPED, ").Append(unverifiableCount).Append(" UNVERIFIABLE");
            if (groupRows > 0 || wheelRows > 0)
                sb.Append(" — ").Append(groupRows).Append(" group-box row(s) covering ").Append(groupLeaves)
                  .Append(" mesh(es) and ").Append(wheelRows).Append(" wheel-sphere row(s), judged but not repairable");
            sb.AppendLine();

            if (Mismatches > 0)
            {
                sb.AppendLine("  class      evidence  |Δcentre| local /   world   part");
                foreach (PartRow r in rows)
                    if (r.IsMismatch) sb.AppendLine(Line(r));
            }
            if (repaired)
            {
                sb.Append("  repair: ").Append(reboundCount).Append(" re-bound, ").Append(shiftedCount).Append(" SHIFTED, ")
                  .Append(ambiguousCount).Append(" AMBIGUOUS, ").Append(unmatchedCount).Append(" UNMATCHED, ")
                  .Append(manualCount).Append(" MANUAL, ").Append(detectOnlyCount)
                  .Append(" detect-only; re-diagnosed: ").Append(remaining.Count).Append(" MOVED/RESHAPED remain")
                  .AppendLine(remaining.Count > 0 ? ":" : ".");
                foreach (PartRow r in remaining) sb.AppendLine(Line(r));
            }
            if (unverifiableByPart.Count > 0)
            {
                var ordered = unverifiableByPart.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).ToList();
                const int cap = 40;
                sb.Append("  unverifiable (no collider of its own: the generator skips fasteners and decals, and a wheel sphere swallows what sits inside it), by part: ")
                  .Append(string.Join(", ", ordered.Take(cap).Select(kv => $"{kv.Key} ×{kv.Value}")));
                if (ordered.Count > cap) sb.Append($", +{ordered.Count - cap} more part name(s)");
                sb.AppendLine();
            }
            if (includeOk)
            {
                sb.AppendLine("  OK rows:");
                foreach (PartRow r in rows)
                    if (r.cls == PartClass.Ok) sb.AppendLine(Line(r));
            }
            return sb.ToString().TrimEnd();
        }

        private static string Line(PartRow r)
        {
            string label = r.cls == PartClass.Ok ? "OK" : r.cls == PartClass.Moved ? "MOVED" : r.cls == PartClass.Reshaped ? "RESHAPED" : "UNVERIFIABLE";
            var sb = new StringBuilder();
            sb.Append("  ").Append(label.PadRight(10)).Append(' ').Append(EvidenceLabel(r.evidence).PadRight(9))
              .Append(' ').Append(r.centreDelta.ToString("0.0000").PadLeft(9)).Append(" / ")
              .Append(r.centreDeltaWorld.ToString("0.000").PadLeft(8)).Append("  ").Append(r.path);
            if (r.members != null) sb.Append("  [group of ").Append(r.members.Count).Append(" mesh(es)]");
            switch (r.outcome)
            {
                case RepairOutcome.Rebound: sb.Append("  → REPAIRED ").Append(r.outcomeNote); break;
                case RepairOutcome.Ambiguous: sb.Append("  → AMBIGUOUS ").Append(r.outcomeNote); break;
                case RepairOutcome.Unmatched: sb.Append("  → UNMATCHED ").Append(r.outcomeNote); break;
                case RepairOutcome.DetectOnly: sb.Append("  → not repairable from this evidence; fix by hand"); break;
                case RepairOutcome.Shifted: sb.Append("  → SHIFTED ").Append(r.outcomeNote); break;
                case RepairOutcome.Manual: sb.Append("  → MANUAL ").Append(r.outcomeNote); break;
            }
            return sb.ToString();
        }
    }

    private static string EvidenceLabel(Evidence e) => e switch
    {
        Evidence.Box => "box",
        Evidence.Hulls => "hulls",
        Evidence.Obb => "obb",
        Evidence.Slabs => "slabs",
        Evidence.GroupBox => "group",
        Evidence.WheelSphere => "wheel",
        _ => "none",
    };

    // --- Menu half ---------------------------------------------------------------------------------

    [MenuItem("Tools/RoboSim/Robot/Advanced/Realign Visuals To Colliders", false, 12)]
    private static void RepairFromMenu() => RunFromMenu(repair: true);

    [MenuItem("Tools/RoboSim/Robot/Advanced/Realign Visuals To Colliders (Report Only)", false, 13)]
    private static void ReportFromMenu() => RunFromMenu(repair: false);

    private static void RunFromMenu(bool repair)
    {
        if (!TryResolveTarget(out GameObject sceneRoot, out string prefabPath, out string why))
        {
            EditorUtility.DisplayDialog(Title, why, "OK");
            return;
        }

        if (prefabPath != null)
        {
            // A prefab asset picked in the Project window. It is edited the only way a prefab asset can
            // be — loaded into an isolated copy and saved back — which the Undo system cannot see, so
            // the confirmation says so.
            GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                RobotReport report = Diagnose(root);
                report.assetPath = prefabPath;
                Debug.Log(report.Table());
                if (!repair || report.Mismatches == 0)
                {
                    EditorUtility.DisplayDialog(Title, Describe(report, repair), "OK");
                    return;
                }
                if (!EditorUtility.DisplayDialog(Title,
                        Describe(report, repair) + "\n\nRe-bind those MeshFilters and save " +
                        Path.GetFileName(prefabPath) + "? Undo cannot reach a prefab asset; git has the previous version.",
                        "Repair and save", "Cancel"))
                    return;
                report = Repair(root, useUndo: false);
                report.assetPath = prefabPath;
                Debug.Log(report.Table());
                if (report.Changed) PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                AssetDatabase.SaveAssets();   // shifted hull .assets
                EditorUtility.DisplayDialog(Title, Describe(report, repair), "OK");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
            return;
        }

        RobotReport sceneReport = Diagnose(sceneRoot);
        Debug.Log(sceneReport.Table(), sceneRoot);
        if (!repair || sceneReport.Mismatches == 0)
        {
            EditorUtility.DisplayDialog(Title, Describe(sceneReport, repair), "OK");
            return;
        }
        if (!EditorUtility.DisplayDialog(Title,
                Describe(sceneReport, repair) + "\n\nRe-bind those MeshFilters? (one Undo step)",
                "Repair", "Cancel"))
            return;

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName(UndoName);
        int group = Undo.GetCurrentGroup();
        sceneReport = Repair(sceneRoot, useUndo: true);
        Undo.CollapseUndoOperations(group);
        AssetDatabase.SaveAssets();   // shifted hull .assets
        Debug.Log(sceneReport.Table(), sceneRoot);
        EditorUtility.DisplayDialog(Title, Describe(sceneReport, repair), "OK");
    }

    // Order: a prefab asset selected in the Project window → its path; anything else selected → the
    // robot root it sits under (Hierarchy or Prefab Mode); nothing selected but Prefab Mode open → the
    // staged root.
    private static bool TryResolveTarget(out GameObject sceneRoot, out string prefabPath, out string why)
    {
        sceneRoot = null;
        prefabPath = null;
        why = null;
        GameObject sel = Selection.activeGameObject;
        if (sel != null && EditorUtility.IsPersistent(sel))
        {
            string path = AssetDatabase.GetAssetPath(sel);
            if (!path.EndsWith(".prefab", System.StringComparison.OrdinalIgnoreCase))
            {
                why = $"'{sel.name}' is a model asset, not a robot prefab. Select the robot prefab under " +
                      $"{RoboSimPaths.RobotsFolder}, or open it in Prefab Mode.";
                return false;
            }
            prefabPath = path;
            return true;
        }
        if (sel != null)
        {
            sceneRoot = MechanismBuildUtil.ResolveRobotRoot(sel);
            return sceneRoot != null;
        }
        PrefabStage stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && stage.prefabContentsRoot != null)
        {
            sceneRoot = stage.prefabContentsRoot;
            return true;
        }
        why = "Select the robot first — in the Hierarchy, in Prefab Mode, or its prefab asset in the Project window.";
        return false;
    }

    private static string Describe(RobotReport report, bool repairMode)
    {
        var sb = new StringBuilder();
        sb.AppendLine(report.SummaryLine());
        if (report.Mismatches == 0 && !report.repaired)
            sb.AppendLine("\nEvery verifiable part draws where its collider says it is.");
        else if (!report.repaired)
        {
            sb.AppendLine();
            foreach (PartRow r in report.rows.Where(r => r.IsMismatch).Take(8))
                sb.AppendLine($"  {r.cls} ({EvidenceLabel(r.evidence)}) {r.path}");
            if (report.Mismatches > 8) sb.AppendLine($"  …and {report.Mismatches - 8} more (full table in the Console)");
            if (!repairMode) sb.AppendLine("\nRun Realign Visuals To Colliders (without Report Only) to re-bind them.");
        }
        else if (report.remaining.Count > 0)
        {
            sb.AppendLine($"\n{report.remaining.Count} still disagree — see the Console table. AMBIGUOUS parts have " +
                          "several same-bounds meshes in the file (coincident copies); UNMATCHED have none; " +
                          "group boxes and wheel spheres are judged but never re-bound.");
        }
        return sb.ToString().TrimEnd();
    }

    // --- Batch half --------------------------------------------------------------------------------

    // Report only: prints every robot's table (with OK rows, so coverage is visible in the log) and
    // exits 0 whatever it finds — its job is to show the mechanism, not to gate. Throws only when
    // there are no robot prefabs at all, which means the folder moved, not that nothing is wrong.
    public static void RunBatchReport()
    {
        var summary = new StringBuilder();
        int robots = 0, mismatches = 0;
        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                robots++;
                RobotReport report = Diagnose(root);
                report.assetPath = path;
                LogTable(report);
                mismatches += report.Mismatches;
                summary.Append("  ").AppendLine(report.SummaryLine());
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
        if (robots == 0)
            throw new System.InvalidOperationException($"{Title}: no robot prefabs under {RoboSimPaths.RobotsFolder}.");
        Debug.Log($"{Title} (report): {robots} robot(s), {mismatches} MOVED/RESHAPED row(s) in total.\n{summary.ToString().TrimEnd()}");
    }

    // Repair: re-binds, saves only the prefabs that changed, and throws (nonzero exit) if any
    // MOVED/RESHAPED row survives — AMBIGUOUS, UNMATCHED, or detect-only — so a robot that still draws
    // wrong cannot slip through as "repair ran".
    public static void RunBatchRepair()
    {
        var summary = new StringBuilder();
        var unrepaired = new List<string>();
        int robots = 0, saved = 0, rebound = 0, shiftedTotal = 0;
        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                robots++;
                RobotReport report = Repair(root, useUndo: false);
                report.assetPath = path;
                LogTable(report);
                rebound += report.reboundCount;
                shiftedTotal += report.shiftedCount;
                if (report.Changed)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    saved++;
                }
                foreach (PartRow r in report.remaining)
                    unrepaired.Add($"{report.robotName}: {r.cls} ({EvidenceLabel(r.evidence)}) {r.path}");
                summary.Append("  ").AppendLine(report.SummaryLine());
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }
        // A shift edits hull .assets (vertices moved back by Δ); SaveAsPrefabAsset writes the prefab
        // only. Without this the prefab lands on disk with the leaf moved and the hull not — the exact
        // mismatch the tool exists to remove, and a second run would shift the leaf AGAIN.
        AssetDatabase.SaveAssets();
        if (robots == 0)
            throw new System.InvalidOperationException($"{Title}: no robot prefabs under {RoboSimPaths.RobotsFolder}.");
        Debug.Log($"{Title} (repair): {rebound} MeshFilter(s) re-bound, {shiftedTotal} shifted across {saved} of {robots} robot(s) saved.\n" +
                  summary.ToString().TrimEnd());
        if (unrepaired.Count > 0)
            throw new System.InvalidOperationException(
                $"{Title}: {unrepaired.Count} MOVED/RESHAPED part(s) remain after repair:\n  " +
                string.Join("\n  ", unrepaired));
    }

    // The Console truncates a single entry, so the OK rows go out in chunks after the headline entry;
    // the log file keeps all of it.
    private static void LogTable(RobotReport report)
    {
        Debug.Log(report.Table(includeOk: false));
        const int chunk = 250;
        List<PartRow> ok = report.rows.Where(r => r.cls == PartClass.Ok).ToList();
        for (int i = 0; i < ok.Count; i += chunk)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{Title} — {report.robotName}: OK rows {i + 1}–{Mathf.Min(i + chunk, ok.Count)} of {ok.Count}");
            foreach (PartRow r in ok.Skip(i).Take(chunk))
                sb.AppendLine($"  OK {EvidenceLabel(r.evidence).PadRight(6)} {r.path}");
            Debug.Log(sb.ToString().TrimEnd());
        }
    }

    // --- Diagnosis ---------------------------------------------------------------------------------

    public static RobotReport Diagnose(GameObject root)
    {
        if (root == null) throw new System.ArgumentNullException(nameof(root));
        var report = new RobotReport { robotName = root.name };
        Transform rootT = root.transform;

        var filters = new List<MeshFilter>();
        foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            if (mf != null && mf.sharedMesh != null) filters.Add(mf);
        report.meshFilters = filters.Count;

        // Every ancestor of a mesh-bearing node. A BoxCollider on a node with a mesh BELOW it is a group
        // box over those leaves, not a snapshot of the node's own mesh — the single-mesh path only ever
        // wrote the box onto a leaf.
        var ancestorsOfMeshes = new HashSet<Transform>();
        foreach (MeshFilter mf in filters)
            for (Transform a = mf.transform.parent; a != null; a = a.parent)
                if (!ancestorsOfMeshes.Add(a)) break;

        // Pass 1: parts with their own evidence. Pass 2 groups the rest under the named node the
        // generator would have boxed them on. The split reproduces the generator: a hulled/slabbed/OBB
        // part 'continue'd out of the loop before grouping, so it was never part of a group's union.
        var pending = new List<MeshFilter>();
        foreach (MeshFilter mf in filters)
        {
            Transform t = mf.transform;
            Mesh mesh = mf.sharedMesh;
            Signature sig;
            BoxCollider ownBox = t.GetComponent<BoxCollider>();
            if (ownBox != null && !ancestorsOfMeshes.Contains(t)) sig = BoxSignature(ownBox, BoxRelTol, BoxAbsTol);
            else if (!TryHullSignature(t, out sig) && !TryObbSignature(t, out sig) && !TrySlabSignature(t, out sig))
            {
                pending.Add(mf);
                continue;
            }
            AddRow(report, mf, sig, mesh.bounds, PathOf(t, rootT), MaxAbsComponent(t.lossyScale));
        }

        // Pass 2 has to reproduce what the generator SKIPPED before it grouped, not only how it
        // grouped: a wheel's subtree was consumed by its sphere, so was any mesh whose centre sat
        // inside one, fasteners were denied by name, and decals were too small to bother with. None
        // of those ever entered a group box's union, so none may enter it here — a screw tucked
        // beside a motor would otherwise stretch the union past the box and read as a moved motor.
        // Wheel-subtree meshes are judged against the sphere; the rest are honestly unverifiable.
        List<SphereCollider> spheres = root.GetComponentsInChildren<SphereCollider>(true).Where(s => s != null).ToList();
        var groups = new Dictionary<Transform, List<MeshFilter>>();
        foreach (MeshFilter mf in pending)
        {
            Transform t = mf.transform;
            if (TryWheelSphere(t, rootT, out Signature sphere))
            {
                Bounds world = TransformedAabb(t.localToWorldMatrix, mf.sharedMesh.bounds);
                AddRow(report, mf, sphere, world, PathOf(t, rootT), 1f);
                report.wheelRows++;
                continue;
            }
            if (IsUnderFastener(t, rootT) || IsDecalSized(mf) || InsideAnyWheelSphere(mf, spheres))
            {
                TallyUnverifiable(report, t, rootT);
                continue;
            }
            Transform g = PartGroupOf(t, rootT);
            if (!groups.TryGetValue(g, out List<MeshFilter> list)) groups[g] = list = new List<MeshFilter>();
            list.Add(mf);
        }

        foreach (KeyValuePair<Transform, List<MeshFilter>> kv in groups)
        {
            Transform g = kv.Key;
            List<MeshFilter> members = kv.Value;
            if (TryGroupBox(g, out BoxCollider gbox, out Transform frame))
            {
                // One member sitting ON the group node is the generator's count==1 case: it wrote a
                // plain single-mesh box there, so judge (and repair) it as one.
                if (members.Count == 1 && members[0].transform == g && frame == g)
                {
                    AddRow(report, members[0], BoxSignature(gbox, BoxRelTol, BoxAbsTol), members[0].sharedMesh.bounds,
                        PathOf(g, rootT), MaxAbsComponent(g.lossyScale));
                    continue;
                }
                Signature sig = BoxSignature(gbox, LooseRelTol, LooseAbsTol);
                sig.evidence = Evidence.GroupBox;
                sig.detectOnly = true;
                Bounds union = UnionInFrame(frame, members);
                PartRow row = AddRow(report, null, sig, union, PathOf(frame, rootT), MaxAbsComponent(frame.lossyScale));
                row.members = members;
                row.groupNode = frame;
                report.groupRows++;
                report.groupLeaves += members.Count;
                continue;
            }

            foreach (MeshFilter mf in members) TallyUnverifiable(report, mf.transform, rootT);
        }

        report.rows.Sort((a, b) => string.CompareOrdinal(a.path, b.path));
        return report;
    }

    private static PartRow AddRow(RobotReport report, MeshFilter mf, Signature sig, Bounds actual, string path, float worldScale)
    {
        PartClass cls = sig.Classify(actual, out float delta);
        var row = new PartRow
        {
            path = path,
            evidence = sig.evidence,
            cls = cls,
            detectOnly = sig.detectOnly,
            centreDelta = delta,
            centreDeltaWorld = delta * worldScale,
            filter = mf,
            signature = sig,
        };
        report.rows.Add(row);
        switch (cls)
        {
            case PartClass.Ok: report.okCount++; break;
            case PartClass.Moved: report.movedCount++; break;
            case PartClass.Reshaped: report.reshapedCount++; break;
            default: report.unverifiableCount++; break;
        }
        return row;
    }

    private static Signature BoxSignature(BoxCollider box, float relTol, float absTol) => new Signature
    {
        evidence = Evidence.Box,
        expected = new Bounds(box.center, box.size),
        relTol = relTol,
        absTol = absTol,
    };

    // Convex hulls on the part's own GameObject whose meshes are the baked .assets under
    // Assets/RobotColliders. A MeshCollider sharing the RENDER mesh (the case Reduce Robot Meshes
    // warns about) is excluded: it moves with the visual and so says nothing about where it was.
    private static bool TryHullSignature(Transform t, out Signature sig)
    {
        sig = null;
        bool has = false;
        Vector3 min = Vector3.zero, max = Vector3.zero;
        foreach (MeshCollider mc in t.GetComponents<MeshCollider>())
        {
            if (mc == null || !mc.convex || mc.sharedMesh == null) continue;
            string path = AssetDatabase.GetAssetPath(mc.sharedMesh);
            if (string.IsNullOrEmpty(path) || !path.StartsWith(HullFolderPrefix, System.StringComparison.Ordinal)) continue;
            Bounds b = mc.sharedMesh.bounds;
            if (!has) { min = b.min; max = b.max; has = true; }
            else { min = Vector3.Min(min, b.min); max = Vector3.Max(max, b.max); }
        }
        if (!has) return false;
        sig = new Signature
        {
            evidence = Evidence.Hulls,
            expected = new Bounds((min + max) * 0.5f, max - min),
            relTol = LooseRelTol,
            absTol = LooseAbsTol,
        };
        return true;
    }

    private static bool TryObbSignature(Transform t, out Signature sig)
    {
        sig = null;
        foreach (Transform child in t)
        {
            if (child.name != GeneratePartColliders.ObbChildName) continue;
            BoxCollider box = child.GetComponent<BoxCollider>();
            if (box == null) continue;
            sig = new Signature
            {
                evidence = Evidence.Obb,
                obbCenter = child.localPosition + child.localRotation * Vector3.Scale(box.center, child.localScale),
                obbRotation = child.localRotation,
                obbSize = Vector3.Scale(box.size, Abs(child.localScale)),
                absTol = LooseAbsTol,
            };
            return true;
        }
        return false;
    }

    // Slabs tile the part along its long axis and are tight in the other two, so their union is the
    // mesh AABB (inflated on a thin axis to MinSlabThickness — inside the loose tolerance).
    private static bool TrySlabSignature(Transform t, out Signature sig)
    {
        sig = null;
        bool has = false;
        Vector3 min = Vector3.zero, max = Vector3.zero;
        foreach (Transform child in t)
        {
            if (child.name != GeneratePartColliders.SlabChildName) continue;
            BoxCollider box = child.GetComponent<BoxCollider>();
            if (box == null) continue;
            Matrix4x4 m = Matrix4x4.TRS(child.localPosition, child.localRotation, child.localScale);
            Bounds b = TransformedAabb(m, new Bounds(box.center, box.size));
            if (!has) { min = b.min; max = b.max; has = true; }
            else { min = Vector3.Min(min, b.min); max = Vector3.Max(max, b.max); }
        }
        if (!has) return false;
        sig = new Signature
        {
            evidence = Evidence.Slabs,
            expected = new Bounds((min + max) * 0.5f, max - min),
            relTol = LooseRelTol,
            absTol = LooseAbsTol,
        };
        return true;
    }

    // A group box lives on the group node itself, or on a _GroupCollider child of it.
    private static bool TryGroupBox(Transform g, out BoxCollider box, out Transform frame)
    {
        box = g.GetComponent<BoxCollider>();
        frame = g;
        if (box != null) return true;
        foreach (Transform child in g)
        {
            if (child.name != GeneratePartColliders.GroupColliderChildName) continue;
            box = child.GetComponent<BoxCollider>();
            if (box == null) continue;
            frame = child;
            return true;
        }
        return false;
    }

    // The wheel sphere sits on the cluster's topmost node; anything in that subtree is a wheel body.
    private static bool TryWheelSphere(Transform t, Transform root, out Signature sig)
    {
        sig = null;
        for (Transform a = t; a != null; a = a.parent)
        {
            SphereCollider s = a.GetComponent<SphereCollider>();
            if (s != null)
            {
                sig = new Signature
                {
                    evidence = Evidence.WheelSphere,
                    detectOnly = true,
                    sphereCenter = a.TransformPoint(s.center),
                    sphereRadius = s.radius * MaxAbsComponent(a.lossyScale),
                };
                return true;
            }
            if (a == root) break;
        }
        return false;
    }

    // GeneratePartColliders.PartGroupOf, verbatim: the nearest ancestor that is not a generic BodyN
    // leaf, never above the root, falling back to the mesh's own node at the boundary. This is what
    // decides which leaves one group box was unioned from, so it must match the generator exactly.
    private static Transform PartGroupOf(Transform meshNode, Transform boundary)
    {
        Transform t = meshNode;
        while (t != null && t != boundary && GeneratePartColliders.IsGenericBodyName(t.name)) t = t.parent;
        return (t != null && t != boundary) ? t : meshNode;
    }

    // The readable part a leaf belongs to, for the unverifiable tally. Looser than IsGenericBodyName on
    // purpose: "Body1.294" is not generic to the generator (the ".294" makes it its own group) but it
    // still names nothing a human can act on, so keep walking while the name starts with "Body".
    private static string PartLabel(Transform t, Transform root)
    {
        for (Transform a = t; a != null && a != root; a = a.parent)
        {
            string n = RobotPartClassifier.NormalizeName(a.name);
            if (!n.StartsWith("Body", System.StringComparison.OrdinalIgnoreCase)) return n;
        }
        return RobotPartClassifier.NormalizeName(t.name);
    }

    // The generator's three skips, reproduced so a group's membership here is the membership its box
    // was unioned from. IsUnderFastener and the decal size are GeneratePartColliders' own rules
    // (IsUnderFastener/DecalMaxWorldExtent); the sphere test is its wheel consumption by containment.
    private static bool IsUnderFastener(Transform node, Transform root)
    {
        for (Transform t = node; t != null; t = t.parent)
        {
            if (RobotPartClassifier.IsFastener(t.name)) return true;
            if (t == root) break;
        }
        return false;
    }

    private static bool IsDecalSized(MeshFilter mf)
    {
        Vector3 worldExtents = Vector3.Scale(mf.sharedMesh.bounds.extents, Abs(mf.transform.lossyScale));
        return MaxComponent(worldExtents) < GeneratePartColliders.DecalMaxWorldExtent;
    }

    private static bool InsideAnyWheelSphere(MeshFilter mf, List<SphereCollider> spheres)
    {
        if (spheres.Count == 0) return false;
        Vector3 centre = TransformedAabb(mf.transform.localToWorldMatrix, mf.sharedMesh.bounds).center;
        foreach (SphereCollider s in spheres)
        {
            float radius = s.radius * MaxAbsComponent(s.transform.lossyScale);
            if ((centre - s.transform.TransformPoint(s.center)).sqrMagnitude <= radius * radius) return true;
        }
        return false;
    }

    private static void TallyUnverifiable(RobotReport report, Transform t, Transform root)
    {
        report.unverifiableCount++;
        string label = PartLabel(t, root);
        report.unverifiableByPart.TryGetValue(label, out int n);
        report.unverifiableByPart[label] = n + 1;
    }

    private static Bounds UnionInFrame(Transform frame, List<MeshFilter> members)
    {
        Matrix4x4 worldToFrame = frame.worldToLocalMatrix;
        bool has = false;
        Vector3 min = Vector3.zero, max = Vector3.zero;
        foreach (MeshFilter mf in members)
        {
            if (mf == null || mf.sharedMesh == null) continue;
            Bounds b = TransformedAabb(worldToFrame * mf.transform.localToWorldMatrix, mf.sharedMesh.bounds);
            if (!has) { min = b.min; max = b.max; has = true; }
            else { min = Vector3.Min(min, b.min); max = Vector3.Max(max, b.max); }
        }
        return new Bounds((min + max) * 0.5f, max - min);
    }

    // AABB of a box's eight corners after a transform.
    private static Bounds TransformedAabb(Matrix4x4 m, Bounds b)
    {
        Vector3 c = b.center, e = b.extents;
        Vector3 min = Vector3.zero, max = Vector3.zero;
        for (int i = 0; i < 8; i++)
        {
            Vector3 corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
            Vector3 p = m.MultiplyPoint3x4(corner);
            if (i == 0) { min = max = p; }
            else { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        }
        return new Bounds((min + max) * 0.5f, max - min);
    }

    private static string PathOf(Transform t, Transform root)
    {
        var parts = new List<string>();
        for (Transform a = t; a != null && a != root; a = a.parent) parts.Add(a.name);
        parts.Reverse();
        return parts.Count == 0 ? t.name : string.Join("/", parts);
    }

    private static float MaxComponent(Vector3 v) => Mathf.Max(v.x, Mathf.Max(v.y, v.z));
    private static float MaxAbsComponent(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));
    private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    // --- Repair ------------------------------------------------------------------------------------

    // Diagnoses, re-binds every repairable MOVED/RESHAPED part whose signature picks exactly one mesh
    // in the same asset file, then diagnoses again. The returned report carries the BEFORE rows with
    // their outcomes and the AFTER mismatches in `remaining`.
    public static RobotReport Repair(GameObject root, bool useUndo)
    {
        RobotReport report = Diagnose(root);
        var poolByPath = new Dictionary<string, List<Mesh>>();

        foreach (PartRow row in report.rows)
        {
            if (!row.IsMismatch) continue;
            if (row.detectOnly || row.filter == null)
            {
                // A group box cannot pick a candidate mesh, but a MOVED group (its leaves' union sits
                // off the box by one offset) is the same re-export as a moved leaf: shift the node.
                if (row.evidence == Evidence.GroupBox && row.cls == PartClass.Moved && row.groupNode != null &&
                    TryShiftGroup(row, root, useUndo, out string groupNote))
                {
                    row.outcome = RepairOutcome.Shifted;
                    row.outcomeNote = groupNote;
                    report.shiftedCount++;
                }
                else
                {
                    row.outcome = RepairOutcome.DetectOnly;
                    report.detectOnlyCount++;
                }
                continue;
            }

            Mesh current = row.filter.sharedMesh;
            string path = AssetDatabase.GetAssetPath(current);
            if (string.IsNullOrEmpty(path))
            {
                row.outcome = RepairOutcome.Unmatched;
                row.outcomeNote = "(the current mesh is not an asset, so there is no file to search)";
                report.unmatchedCount++;
                continue;
            }
            if (!poolByPath.TryGetValue(path, out List<Mesh> pool))
                poolByPath[path] = pool = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToList();

            // An OBB frame is a containment test, not a snapshot: on Darwinbot it accepted 51 unrelated
            // meshes for one shaft. It cannot pick a candidate, so a MOVED OBB part goes straight to the
            // shift, and a RESHAPED one is honestly unmatched.
            List<Mesh> matches = row.evidence == Evidence.Obb
                ? new List<Mesh>()
                : pool.Where(m => m != null && m != current &&
                                  row.signature.Classify(m.bounds, out _) == PartClass.Ok).ToList();
            if (matches.Count > 1)
            {
                // Several meshes share the signature. Prefer a copy of the same part (same vertex
                // count as what is bound now) only if that leaves exactly one; otherwise refuse.
                List<Mesh> sameCount = matches.Where(m => m.vertexCount == current.vertexCount).ToList();
                if (sameCount.Count == 1) matches = sameCount;
            }

            if (matches.Count == 0)
            {
                // No mesh in the file has the snapshot's bounds: the GEOMETRY changed under the collider
                // (a newer or older export of the same robot — Darwinbot, 166 parts, all of them this).
                // Same size, different centre is a translation, and a translation is repaired by moving
                // the leaf's transform and re-snapshotting its colliders — see Shift.
                string shiftNote = null;
                bool shifted = row.cls == PartClass.Moved && TryShift(row, root, useUndo, out shiftNote);
                if (shifted)
                {
                    row.outcome = RepairOutcome.Shifted;
                    row.outcomeNote = shiftNote;
                    report.shiftedCount++;
                }
                else if (row.cls == PartClass.Moved && shiftNote != null)
                {
                    row.outcome = RepairOutcome.Manual;
                    row.outcomeNote = shiftNote;
                    report.manualCount++;
                }
                else
                {
                    row.outcome = RepairOutcome.Unmatched;
                    row.outcomeNote = $"(no mesh in {Path.GetFileName(path)} has these bounds — the geometry itself changed size)";
                    report.unmatchedCount++;
                }
            }
            else if (matches.Count > 1)
            {
                // Several meshes carry the signature. Re-binding to one of them is a guess; moving the
                // part that IS bound onto its collider is not (same size, same place, whatever copy it
                // is), so a MOVED part is shifted instead. A RESHAPED one has no translation to apply.
                string shiftNote = null;
                if (row.cls == PartClass.Moved && TryShift(row, root, useUndo, out shiftNote))
                {
                    row.outcome = RepairOutcome.Shifted;
                    row.outcomeNote = $"{shiftNote} — {matches.Count} candidate meshes, so re-binding would have been a guess";
                    report.shiftedCount++;
                }
                else
                {
                    row.outcome = RepairOutcome.Ambiguous;
                    row.outcomeNote = $"({matches.Count} candidates: " +
                                      string.Join(", ", matches.Take(5).Select(m => $"'{m.name}' {m.vertexCount}v")) +
                                      (matches.Count > 5 ? ", …" : "") + (shiftNote != null ? "; " + shiftNote : "") + ")";
                    report.ambiguousCount++;
                }
            }
            else
            {
                Rebind(row.filter, matches[0], useUndo);
                row.outcome = RepairOutcome.Rebound;
                row.outcomeNote = $"'{current.name}' → '{matches[0].name}'";
                report.reboundCount++;
            }
        }

        report.repaired = true;
        RobotReport after = Diagnose(root);
        foreach (PartRow r in after.rows)
            if (r.IsMismatch) report.remaining.Add(r);
        return report;
    }

    // --- Shift: the repair for geometry that changed under its collider ---------------------------
    //
    // THE MATH. Let T be the leaf's transform, V the current mesh (bounds centre c_m, mesh-local) and
    // c_b the collider's snapshot centre. The visual draws at T·V; the collider sits at T·c_b, which is
    // where the part is supposed to be. With Δ = c_b − c_m, the new transform T' = T·Translate(Δ) puts
    // the visual's centre at T·(c_m + Δ) = T·c_b — exactly on the collider. Every collider on the leaf
    // must then be moved BACK by Δ in mesh-local space to stay where it was in the world: a box's centre
    // becomes c_b − Δ = c_m, i.e. the snapshot the generator would take of the current mesh; a hull's
    // vertices become v − Δ; a child holder's localPosition loses Δ.
    //
    // WHY A TRANSFORM AND NOT A TRANSLATED MESH COPY: no mesh readability flag to flip, no copied
    // vertex buffers on disk, the FBX stays the single source of geometry, and Rebuild Part Colliders
    // afterwards produces the very same answer (box = mesh.bounds on a leaf whose transform is now the
    // truth). The leaf's transform stops being identity — which is fine: it only ever was identity
    // because the exporter baked placement into the vertices, and this is that placement corrected.
    //
    // Refused (MANUAL) when the leaf IS a joint link (its transform is its joint frame — moving it moves
    // the joint), when a mesh or link hangs under it (their placement would change too), or when a hull
    // mesh is shared with another collider (shifting it would move that one).
    private static bool TryShift(PartRow row, GameObject root, bool useUndo, out string note)
    {
        note = null;
        MeshFilter mf = row.filter;
        Transform leaf = mf.transform;
        Mesh mesh = mf.sharedMesh;
        if (leaf.GetComponent<ArticulationBody>() != null)
        {
            note = "(MANUAL: this part is a joint link — its transform is its joint frame, so re-rig it instead)";
            return false;
        }
        foreach (Transform child in leaf)
        {
            if (child.GetComponentInChildren<MeshFilter>(true) != null || child.GetComponentInChildren<ArticulationBody>(true) != null)
            {
                note = $"(MANUAL: '{child.name}' under this part carries a mesh or a link that would move with it)";
                return false;
            }
        }

        Vector3 delta;
        string fit = "";
        switch (row.evidence)
        {
            case Evidence.Box:
            case Evidence.Hulls:
            case Evidence.Slabs:
                delta = row.signature.expected.center - mesh.bounds.center;
                if (row.evidence != Evidence.Box) fit = row.evidence == Evidence.Hulls ? " (±hull fit)" : " (±slab fit)";
                break;
            case Evidence.Obb:
            {
                // The OBB's own axis-aligned box stands in for the AABB the generator saw; the two
                // centres differ by the part's asymmetry inside its OBB, a fraction of the part.
                Matrix4x4 m = Matrix4x4.TRS(row.signature.obbCenter, row.signature.obbRotation, Vector3.one);
                delta = TransformedAabb(m, new Bounds(Vector3.zero, row.signature.obbSize)).center - mesh.bounds.center;
                fit = " (±OBB fit)";
                break;
            }
            default:
                note = "(MANUAL: no shiftable evidence)";
                return false;
        }

        // Hull meshes: ours to edit (standalone .assets under Assets/RobotColliders), but never shared.
        var hulls = new List<(MeshCollider collider, Mesh hull)>();
        foreach (MeshCollider mc in leaf.GetComponents<MeshCollider>())
        {
            if (mc == null || mc.sharedMesh == null) continue;
            string hullPath = AssetDatabase.GetAssetPath(mc.sharedMesh);
            if (string.IsNullOrEmpty(hullPath) || !hullPath.StartsWith(HullFolderPrefix, System.StringComparison.Ordinal)) continue;
            hulls.Add((mc, mc.sharedMesh));
        }
        if (hulls.Count > 0)
        {
            var mine = new HashSet<Mesh>(hulls.Select(h => h.hull));
            foreach (MeshCollider other in root.GetComponentsInChildren<MeshCollider>(true))
                if (other != null && other.transform != leaf && other.sharedMesh != null && mine.Contains(other.sharedMesh))
                {
                    note = $"(MANUAL: hull '{other.sharedMesh.name}' is shared with '{other.name}', so shifting it would move that part's collider)";
                    return false;
                }
        }

        // 1. The leaf, in its own frame.
        if (useUndo) Undo.RecordObject(leaf, UndoName);
        leaf.localPosition += leaf.localRotation * Vector3.Scale(leaf.localScale, delta);
        EditorUtility.SetDirty(leaf);

        // 2. Every collider on it back by Δ, so the world does not see them move.
        foreach (BoxCollider box in leaf.GetComponents<BoxCollider>())
        {
            if (useUndo) Undo.RecordObject(box, UndoName);
            box.center -= delta;
            EditorUtility.SetDirty(box);
        }
        foreach ((MeshCollider collider, Mesh hull) in hulls)
        {
            if (useUndo) Undo.RecordObject(hull, UndoName);
            Vector3[] v = hull.vertices;
            for (int i = 0; i < v.Length; i++) v[i] -= delta;
            hull.vertices = v;
            hull.RecalculateBounds();
            EditorUtility.SetDirty(hull);
            // Re-assign so PhysX re-cooks the collider from the moved vertices.
            if (useUndo) Undo.RecordObject(collider, UndoName);
            collider.sharedMesh = null;
            collider.sharedMesh = hull;
            EditorUtility.SetDirty(collider);
        }
        foreach (Transform child in leaf)
        {
            if (useUndo) Undo.RecordObject(child, UndoName);
            child.localPosition -= delta;
            EditorUtility.SetDirty(child);
        }
        if (useUndo && leaf.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(leaf.gameObject.scene);

        float world = Vector3.Scale(delta, Abs(leaf.lossyScale)).magnitude;
        note = $"by ({delta.x:0.####}, {delta.y:0.####}, {delta.z:0.####}) local = {world:0.000} u{fit}";
        return true;
    }

    // A MOVED group: the leaves' union sits off the group box by one offset, in the node's frame.
    // Shift the node (everything under it moves as the one CAD part it is) and re-snapshot the box.
    private static bool TryShiftGroup(PartRow row, GameObject root, bool useUndo, out string note)
    {
        note = null;
        Transform node = row.groupNode;
        if (node.GetComponentInChildren<ArticulationBody>(true) != null)
        {
            note = "(MANUAL: a joint link sits on or under this group node)";
            return false;
        }
        BoxCollider box = node.GetComponent<BoxCollider>();
        if (box == null) { note = "(MANUAL: the group box is not on the node itself)"; return false; }
        Bounds union = UnionInFrame(node, row.members);
        Vector3 delta = row.signature.expected.center - union.center;

        if (useUndo) Undo.RecordObject(node, UndoName);
        node.localPosition += node.localRotation * Vector3.Scale(node.localScale, delta);
        EditorUtility.SetDirty(node);
        if (useUndo) Undo.RecordObject(box, UndoName);
        box.center -= delta;
        EditorUtility.SetDirty(box);
        if (useUndo && node.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(node.gameObject.scene);

        float world = Vector3.Scale(delta, Abs(node.lossyScale)).magnitude;
        note = $"by ({delta.x:0.####}, {delta.y:0.####}, {delta.z:0.####}) in the group frame = {world:0.000} u (whole part)";
        return true;
    }

    private static void Rebind(MeshFilter mf, Mesh mesh, bool useUndo)
    {
        if (useUndo) Undo.RecordObject(mf, UndoName);
        mf.sharedMesh = mesh;
        EditorUtility.SetDirty(mf);
        // Prefab Mode and scene objects need the dirty flag on their scene; prefab contents loaded by
        // LoadPrefabContents live in a preview scene that is saved through SaveAsPrefabAsset instead.
        if (useUndo && mf.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(mf.gameObject.scene);
    }
}
