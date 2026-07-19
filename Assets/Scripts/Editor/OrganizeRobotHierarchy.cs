using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Tidies an imported robot's hierarchy for readability and manual mechanism authoring: at the SELECTED
// object and every sub-assembly nested under it, groups that level's loose parts into collapsible
// "folder" GameObjects by what each part is — a collapsible Hardware/ wrapper (screws, spacers, nuts,
// standoffs, washers, shaft collars, bearings) plus Structure / Motion / Electronics / Wheels / Plastic
// — and, within each bucket, collapses duplicate instances of the same part into a per-name sub-folder.
//
// Design constraints (from the user):
//   - Group IN PLACE: folders are created under the scanned parent and children keep their world pose,
//     so nothing is relocated to a different branch. This also preserves ArticulationBody membership —
//     a plain folder inserted under the same parent doesn't change a node's nearest-AB ancestor.
//   - Operates on the Hierarchy SELECTION (a folder or the robot root) and RECURSES: it groups that
//     node's loose parts, then descends into each sub-assembly and does the same, so one click on the
//     robot root organizes every level in place. Folders we create and single part groups (whose
//     children are just meshes) are never re-entered; hand-made sub-folders are descended into and
//     their loose parts grouped, but the folders themselves are never moved or renamed.
//   - Reversible: one collapsed Undo step. Idempotent: once parts are grouped they're no longer loose
//     direct children, so a second run is a no-op; a re-run only sweeps in newly-loose parts, reusing
//     the existing folders.
//
// Classification reuses RobotPartClassifier (the single source of truth for VEX part names) and the
// reparent helper reuses MechanismBuildUtil.EnsureChildOf (world-pose-preserving, undo-aware).
public static class OrganizeRobotHierarchy
{
    private const string Title = "Organize Hierarchy";

    // Hardware tokens -> the sub-folder they nest under (inside the Hardware/ wrapper). Checked
    // most-specific first so "Shaft Collar" wins over a bare "Shaft", etc. Reuses the classifier's
    // FastenerDenyList vocabulary plus "Standoff": the classifier deliberately keeps standoffs OUT of
    // FastenerDenyList (they keep colliders), but grouping is purely cosmetic and the user wants
    // standoffs collapsed with the rest of the hardware — this never touches collider logic.
    private static readonly (string token, string folder)[] HardwareTypes =
    {
        ("Shaft Collar", "Shaft Collars"),
        ("Bearing",      "Bearings"),
        ("Standoff",     "Standoffs"),
        ("Screw",        "Screws"),
        ("Spacer",       "Spacers"),
        ("Washer",       "Washers"),
        ("Nut",          "Nuts"),
    };

    // Powered/rotating drivetrain internals (a subset of RobotPartClassifier.SimpleShapeTokens).
    private static readonly string[] MotionTokens = { "Gear", "Sprocket", "Pinion", "Motor", "Gearbox", "Cartridge" };
    // Electronics (the rest of SimpleShapeTokens).
    private static readonly string[] ElectronicsTokens = { "Sensor", "Vision", "Brain", "Battery", "Radio" };
    // Wheel name tokens — reuse the classifier's default so this agrees with the drivetrain rigger.
    private static readonly string[] WheelTokens = SplitTokens(RobotPartClassifier.DefaultWheelTokens);

    // Every folder name this tool manages. Direct children matching one of these are our own folders
    // from a prior run — skip them during classification so we never nest a bucket inside another.
    private static readonly HashSet<string> ReservedFolderNames = new HashSet<string>
    {
        "Hardware", "Structure", "Motion", "Electronics", "Wheels", "Plastic",
        "Screws", "Spacers", "Nuts", "Standoffs", "Washers", "Shaft Collars", "Bearings",
    };

    [MenuItem("Tools/RoboSim/Robot/Advanced/Organize Hierarchy", false, 4)]
    private static void OrganizeSelected()
    {
        GameObject target = Selection.activeGameObject;
        if (target == null)
        {
            EditorUtility.DisplayDialog(Title,
                "Select the folder (or robot root) you want to organize, in the Hierarchy first.\n\n" +
                "It groups that object and everything nested under it, organizing each level in place — " +
                "existing folders are kept and their contents grouped, not moved.",
                "OK");
            return;
        }

        string report = Organize(target, useUndo: true);
        EditorUtility.DisplayDialog(Title, report, "OK");
        Debug.Log($"{Title}: {report}", target);
    }

    // Groups target and everything nested under it (see OrganizeNode). useUndo=false for headless/batch
    // callers. Returns a short report.
    public static string Organize(GameObject target, bool useUndo)
    {
        if (target == null) throw new System.ArgumentNullException(nameof(target));

        int group = 0;
        if (useUndo)
        {
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Title);
            group = Undo.GetCurrentGroup();
        }

        // counts keyed by leaf folder name (Screws, Spacers, Structure, ...), aggregated across the
        // whole subtree; stats also tracks recursion + skips for the report.
        var counts = new SortedDictionary<string, int>();
        var stats = new Stats();
        OrganizeNode(target.transform, useUndo, counts, stats);

        if (useUndo)
        {
            Undo.CollapseUndoOperations(group);
            if (target.scene.IsValid()) EditorSceneManager.MarkSceneDirty(target.scene);
        }
        EditorUtility.SetDirty(target);

        return BuildReport(target.name, counts, stats);
    }

    // Groups one node's direct loose leaf-parts into folders under it, then recurses into each of its
    // sub-assembly children — so clicking the robot root cascades through every level. Never descends
    // into the folders we create or into a single part group (whose children are just meshes).
    private static void OrganizeNode(Transform node, bool useUndo, SortedDictionary<string, int> counts, Stats stats)
    {
        // Snapshot direct children before any reparenting changes the child list.
        List<Transform> children = new List<Transform>();
        foreach (Transform c in node) children.Add(c);

        // Split this level's children into: leaf-parts to bucket, and sub-assemblies to recurse into.
        var buckets = new Dictionary<string, List<Transform>>();
        var bucketPaths = new Dictionary<string, string[]>();
        var containers = new List<Transform>();

        foreach (Transform child in children)
        {
            if (child == null) continue;
            if (ReservedFolderNames.Contains(child.name)) continue;                 // our folder — already organized
            if (child.GetComponent<ArticulationBody>() != null) { stats.skippedRigged++; continue; } // don't disturb a rig

            // A sub-assembly (named, non-mesh children) is recursed into, never bucketed — so a whole
            // assembly is never swept into a bucket by a coincidental name-token match.
            if (IsContainer(child)) { containers.Add(child); continue; }

            string[] path = Classify(child.name);
            if (path == null) continue;                                             // unrecognized -> leave loose in place

            string key = string.Join("/", path);
            if (!buckets.TryGetValue(key, out List<Transform> list))
            {
                list = new List<Transform>();
                buckets[key] = list;
                bucketPaths[key] = path;
            }
            list.Add(child);
        }

        foreach (KeyValuePair<string, List<Transform>> bucket in buckets)
        {
            string[] path = bucketPaths[bucket.Key];

            // Ensure the folder chain under this node (e.g. Hardware/Screws), reusing existing folders.
            Transform leaf = node;
            foreach (string segment in path) leaf = GetOrCreateFolder(leaf, segment, useUndo);

            // Per-name sub-grouping: instances of the same base part collapse into their own folder.
            var byName = new Dictionary<string, List<Transform>>();
            foreach (Transform part in bucket.Value)
            {
                string norm = RobotPartClassifier.NormalizeName(part.name);
                if (!byName.TryGetValue(norm, out List<Transform> g))
                {
                    g = new List<Transform>();
                    byName[norm] = g;
                }
                g.Add(part);
            }

            foreach (KeyValuePair<string, List<Transform>> nameGroup in byName)
            {
                // Reuse an existing per-name sub-folder; otherwise wrap only when there are 2+ copies
                // (a lone part sits directly in the bucket rather than in a folder of one).
                Transform dest = leaf;
                Transform existingSub = leaf.Find(nameGroup.Key);
                if (existingSub != null) dest = existingSub;
                else if (nameGroup.Value.Count >= 2) dest = GetOrCreateFolder(leaf, nameGroup.Key, useUndo);

                foreach (Transform part in nameGroup.Value)
                    MechanismBuildUtil.EnsureChildOf(part, dest, useUndo);
            }

            string leafName = path[path.Length - 1];
            counts[leafName] = (counts.TryGetValue(leafName, out int c) ? c : 0) + bucket.Value.Count;
        }

        // Descend into sub-assemblies so a single click on the top level organizes every level.
        foreach (Transform container in containers)
        {
            stats.containers++;
            OrganizeNode(container, useUndo, counts, stats);
        }
    }

    // A node worth descending into: it has a named (non-mesh) child, i.e. it's a sub-assembly or a
    // user folder rather than a single part. Part groups — whose children are just mesh leaves or
    // generic "BodyN" nodes — return false, so they're bucketed whole and their meshes are never dug into.
    private static bool IsContainer(Transform node)
    {
        foreach (Transform child in node)
        {
            if (child.GetComponent<MeshFilter>() != null) continue; // a mesh leaf doesn't make node a container
            if (IsGenericBodyName(child.name)) continue;            // generic "BodyN" mesh node
            return true;                                            // a named, non-mesh child -> sub-assembly/folder
        }
        return false;
    }

    // Mirrors GeneratePartColliders.IsGenericBodyName: "Body", "Body1", "Body23" — the importer's
    // generic mesh-leaf names that carry no part identity.
    private static bool IsGenericBodyName(string rawName)
    {
        string n = RobotPartClassifier.NormalizeName(rawName);
        if (n.Length < 4 || !n.StartsWith("Body", System.StringComparison.OrdinalIgnoreCase)) return false;
        for (int i = 4; i < n.Length; i++) if (!char.IsDigit(n[i])) return false;
        return true;
    }

    private class Stats
    {
        public int skippedRigged;
        public int containers;
    }

    // The folder chain (under the scanned target) a part belongs in, or null to leave it loose.
    // Precedence: hardware (most specific) -> wheels -> motion -> electronics -> structure -> plastic.
    private static string[] Classify(string rawName)
    {
        string name = RobotPartClassifier.NormalizeName(rawName);

        foreach ((string token, string folder) in HardwareTypes)
            if (Contains(name, token)) return new[] { "Hardware", folder };

        if (ContainsAny(name, WheelTokens)) return new[] { "Wheels" };
        if (ContainsAny(name, MotionTokens)) return new[] { "Motion" };
        if (ContainsAny(name, ElectronicsTokens)) return new[] { "Electronics" };
        if (RobotPartClassifier.IsMetal(rawName)) return new[] { "Structure" };
        if (RobotPartClassifier.IsPlastic(rawName)) return new[] { "Plastic" };
        return null;
    }

    // Finds an existing direct child folder by exact name, or creates an empty one at the parent's
    // local origin. Children moved in later keep their world pose (EnsureChildOf), so the folder's
    // own transform doesn't matter.
    private static Transform GetOrCreateFolder(Transform parent, string name, bool useUndo)
    {
        Transform existing = parent.Find(name);
        if (existing != null) return existing;

        GameObject go = new GameObject(name);
        if (useUndo) Undo.RegisterCreatedObjectUndo(go, Title);
        go.transform.SetParent(parent, false); // local identity under parent
        return go.transform;
    }

    private static string BuildReport(string targetName, SortedDictionary<string, int> counts, Stats stats)
    {
        var sb = new StringBuilder();
        if (counts.Count == 0)
        {
            sb.AppendLine($"Nothing to group under '{targetName}'.");
            sb.AppendLine("No loose, recognized parts were found in it or any sub-group —");
            sb.AppendLine("already-organized folders and unrecognized parts are left untouched.");
        }
        else
        {
            sb.AppendLine($"Organized '{targetName}':");
            foreach (KeyValuePair<string, int> kv in counts) sb.AppendLine($"  {kv.Key}: {kv.Value}");
            sb.AppendLine();
            if (stats.containers > 0)
                sb.AppendLine($"Recursed through {stats.containers} sub-group(s), grouping in place at each level.");
            else
                sb.AppendLine("Grouped in place under the selected object.");
            sb.AppendLine("Press Undo once to reverse it.");
        }
        if (stats.skippedRigged > 0)
            sb.AppendLine($"Skipped {stats.skippedRigged} rigged part(s) (ArticulationBody) so the physics rig isn't disturbed.");
        return sb.ToString();
    }

    private static bool Contains(string name, string token) =>
        name.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool ContainsAny(string name, string[] tokens)
    {
        foreach (string t in tokens)
            if (Contains(name, t)) return true;
        return false;
    }

    // Split a comma-separated token list, trimming blanks (mirrors RobotPartClassifier's wheel parsing).
    private static string[] SplitTokens(string commaSeparated)
    {
        var list = new List<string>();
        foreach (string t in commaSeparated.Split(','))
        {
            string s = t.Trim();
            if (s.Length > 0) list.Add(s);
        }
        return list.ToArray();
    }
}
