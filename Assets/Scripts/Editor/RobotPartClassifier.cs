using System.Collections.Generic;
using UnityEngine;
using Unity.Robotics.UrdfImporter;

// Shared name-based classification for the imported robot drivetrain FBX.
//
// The FBX (Fusion-style export) parents every mesh under a named part group ("18 - 2x C-Chan v1",
// "0.375 OD Spacer, 0.500 (Nylon) v1", ...) whose meshes sit on generic "Body1".."BodyN" leaf
// nodes. Duplicate part instances get importer suffixes (":3", " (2)"). This utility centralizes
// the naming rules so every collider/rigging tool agrees on:
//   - which parts are fasteners (spacers/screws/nuts/... — physically irrelevant, skip colliders),
//   - which nodes are wheels and how the coincident omni-wheel halves group into wheel clusters,
//   - how to measure a named sub-group's bounds in the robot root's local space
//     (same semantics as FixRobotCollider, shared here for reuse).
//
// Editor-only (lives in Editor/); used by Tools > RoboSim > Robot > Advanced > Rebuild Part Colliders and rigging tools.
public static class RobotPartClassifier
{
    // Name tokens (checked case-insensitively against normalized names) for hardware that should
    // never get its own collider: it is tiny, always buried inside/against a structural part, and
    // hundreds of them would only bloat the compound collider. Deliberately NOT here:
    //   - "Standoff" — standoffs are structural spacers that hold panels apart; they can contact
    //     field elements, so they keep colliders.
    //   - plain "Round Insert" — that would also deny the structural gear
    //     "60T HS Gear, Round Insert (v2) v1"; the longer "HS Round Insert" token matches only the
    //     fastener "HS Round Insert (ABS) v2".
    //   - plain "Shaft" — "LS Shaft" is a structural axle; only "Shaft Collar" is hardware.
    public static readonly string[] FastenerDenyList =
    {
        "Spacer",
        "Screw",
        "Nut",
        "Washer",
        "Bearing Flat",
        "HS Round Insert",
        "Shaft Collar",
    };

    // Wheel group nodes are named "3.25 AS Omni, Round Insert v1" (plus duplicate suffixes).
    public const string WheelNamePrefix = "3.25 AS Omni";

    // Coincident omni halves sit essentially on top of each other; anything within this world
    // distance is the same physical wheel. Wheels on the same rail are several units apart
    // (world is 10x scale), so there is a wide safe margin on both sides of this value.
    private const float WheelClusterRadius = 0.5f;

    // The 360 RPM drivetrain has 12 wheel-named nodes forming 6 coincident pairs.
    private const int ExpectedWheelClusters = 6;

    // One physical wheel: the (usually 2) coincident wheel-named nodes, their combined renderer
    // bounds in world space, and the shallowest node — the right place to hang one collider.
    public class WheelCluster
    {
        public Transform topmost;
        public List<Transform> nodes;
        public Bounds worldBounds;
        public Vector3 Center => worldBounds.center;
    }

    // Strips the importer's duplicate-instance suffixes — ":N" and a trailing " (N)" — so name
    // rules can match every instance of a part. Loops because suffixes stack
    // ("HS Round Insert (ABS) v2 (1):1" → "HS Round Insert (ABS) v2"). Non-numeric parentheses
    // like "(Nylon)" or "(v2)" are left alone.
    public static string NormalizeName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return string.Empty;

        string name = rawName.Trim();
        bool stripped = true;
        while (stripped)
        {
            stripped = false;

            // ":N" duplicate suffix (e.g. "Torx (Star) Screw (8-32, Steel) v1:3").
            int colon = name.LastIndexOf(':');
            if (colon > 0 && IsAllDigits(name, colon + 1, name.Length))
            {
                name = name.Substring(0, colon).TrimEnd();
                stripped = true;
                continue;
            }

            // " (N)" duplicate suffix (e.g. "276-8026-002 Web (10)").
            if (name.Length > 3 && name[name.Length - 1] == ')')
            {
                int open = name.LastIndexOf('(');
                if (open > 1 && name[open - 1] == ' ' && IsAllDigits(name, open + 1, name.Length - 1))
                {
                    name = name.Substring(0, open - 1).TrimEnd();
                    stripped = true;
                }
            }
        }
        return name;
    }

    // True when the (normalized) name contains any deny-list token, case-insensitively.
    // NOTE: meshes live on generic "Body1" leaf nodes, so callers should test the whole ancestor
    // chain of a mesh, not just the mesh's own GameObject name.
    public static bool IsFastener(string rawName)
    {
        string name = NormalizeName(rawName);
        foreach (string token in FastenerDenyList)
        {
            if (name.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    // Plastic/polycarb structural parts (funnels, web panels) — the parts whose shape matters
    // enough for convex-hull decomposition instead of boxes. Name tokens observed in this
    // project's Fusion exports: "Polycarb Funnel", "276-*-00N Web"; "Plastic"/"Lexan" cover
    // other CAD naming conventions.
    private static readonly string[] PlasticTokens = { "Polycarb", "Funnel", "Web", "Plastic", "Lexan" };

    // True when the (normalized) name reads as a plastic/polycarb part. Same ancestor-chain
    // caveat as IsFastener: meshes live on generic "Body1" leaves, test the whole chain.
    public static bool IsPlastic(string rawName)
    {
        string name = NormalizeName(rawName);
        foreach (string token in PlasticTokens)
        {
            if (name.IndexOf(token, System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    // --- Material density lookup (kg/m^3) for computing link mass from mesh volume ---
    // VEX teams share a CAD library with consistent part names, so the part/material name is a
    // reliable density source when a Fusion component was exported WITHOUT a physical material —
    // those parts import at the URDF importer's 0.1 kg clamp (see RobotMassFromGeometry), which
    // silently corrupts the robot's mass. Tokens are matched against a separator-normalized name
    // (NormalizeForTokens). First match wins, so list more specific tokens before generic ones.
    public static readonly (string token, float density)[] DensityByToken =
    {
        ("polycarbonate", 1200f), ("polycarb", 1200f), ("lexan", 1200f),
        ("acetal", 1410f), ("delrin", 1410f),
        ("nylon", 1140f),
        ("abs", 1050f),
        ("petg", 1270f), ("pvc", 1380f),
        ("uhmw", 950f), ("hdpe", 950f),
        ("stainless", 8000f), ("titanium", 4500f), ("brass", 8500f),
        ("aluminium", 2700f), ("aluminum", 2700f), ("6061", 2700f), ("c chan", 2700f),
        ("steel", 7850f),
    };

    // Neutral fallback (~rigid plastic) for a part whose name matches no token and that carries no
    // genuinely-authored mass to trust. Exposed as an override on the setup tools.
    public const float DefaultDensity = 1250f;

    // Lower-case; map '_' '-' '/' and whitespace runs to single spaces; pad one space each side so
    // a short token can be matched as " abs " (word boundary) without catching "absorber".
    public static string NormalizeForTokens(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return " ";
        var sb = new System.Text.StringBuilder(rawName.Length + 2);
        sb.Append(' ');
        bool lastSpace = true;
        foreach (char c in rawName)
        {
            char lc = char.ToLowerInvariant(c);
            bool sep = lc == '_' || lc == '-' || lc == '/' || char.IsWhiteSpace(lc);
            if (sep)
            {
                if (!lastSpace) { sb.Append(' '); lastSpace = true; }
            }
            else { sb.Append(lc); lastSpace = false; }
        }
        if (!lastSpace) sb.Append(' ');
        return sb.ToString();
    }

    // Density for a single name. Tokens <= 3 chars match only on word boundaries (padded spaces),
    // longer tokens match as substrings. False when nothing matches.
    public static bool TryGetDensity(string rawName, out float density)
    {
        string name = NormalizeForTokens(rawName);
        foreach ((string token, float d) in DensityByToken)
        {
            string needle = token.Length <= 3 ? " " + token + " " : token;
            if (name.IndexOf(needle, System.StringComparison.Ordinal) >= 0) { density = d; return true; }
        }
        density = 0f;
        return false;
    }

    // Density for a URDF link: its own name first (ACDC4Robot writes the Fusion component name
    // there), then any node names under its visuals — mirrors the ancestor-chain search
    // IsFastener/IsPlastic rely on. False when neither yields a token.
    public static bool TryGetLinkDensity(UrdfLink link, out float density)
    {
        density = 0f;
        if (link == null) return false;
        if (TryGetDensity(link.name, out density)) return true;
        foreach (Transform child in link.transform)
        {
            if (child.GetComponent<UrdfVisuals>() == null) continue;
            foreach (Transform node in child.GetComponentsInChildren<Transform>(true))
            {
                if (TryGetDensity(node.name, out density)) return true;
            }
        }
        return false;
    }

    // Finds every wheel-named node under root and greedily merges the ones whose subtree renderer
    // bounds centers coincide (the FBX models each omni wheel as two stacked halves). Greedy
    // clustering is enough here: pair members are essentially concentric while distinct wheels are
    // several world units apart, so the first-match assignment can never straddle two wheels.
    // wheelNamePrefix defaults to this project's drivetrain ("3.25 AS Omni"); pass the wheel
    // node prefix of a different robot to reuse the clustering on a new import.
    public static List<WheelCluster> FindWheelClusters(GameObject root, string wheelNamePrefix = null)
    {
        if (string.IsNullOrEmpty(wheelNamePrefix)) wheelNamePrefix = WheelNamePrefix;
        // Accept a comma-separated list of name tokens matched anywhere in the node name, so one
        // field catches a mixed drivetrain ("Omni, Traction") or any team's wheel naming — not just
        // this project's single "3.25 AS Omni" prefix. The default token still matches its nodes.
        string[] wheelTokens = wheelNamePrefix.Split(',');
        var clusters = new List<WheelCluster>();

        foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
        {
            if (!MatchesAnyToken(NormalizeName(node.name), wheelTokens)) continue;

            // Combined world bounds of the wheel subtree's renderers.
            Renderer[] renderers = node.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) continue; // empty group node — nothing physical to wrap
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            WheelCluster home = null;
            foreach (WheelCluster cluster in clusters)
            {
                if ((cluster.worldBounds.center - bounds.center).sqrMagnitude <= WheelClusterRadius * WheelClusterRadius)
                {
                    home = cluster;
                    break;
                }
            }

            if (home == null)
            {
                home = new WheelCluster { topmost = node, nodes = new List<Transform>(), worldBounds = bounds };
                clusters.Add(home);
            }
            else
            {
                home.worldBounds.Encapsulate(bounds);
                // The shallowest node owns the cluster: one collider there covers the whole pair.
                if (Depth(node, root.transform) < Depth(home.topmost, root.transform)) home.topmost = node;
            }
            home.nodes.Add(node);
        }

        // The 6-wheel expectation only describes THIS project's drivetrain, so it is only worth
        // warning about when we searched with its wheel name. A different robot can legitimately
        // have any number of wheels — but zero always means the name didn't match anything.
        bool usingDefaultPrefix = wheelNamePrefix == WheelNamePrefix;
        if (clusters.Count == 0)
        {
            Debug.LogWarning($"RobotPartClassifier: no wheel nodes under '{root.name}' start with " +
                             $"'{wheelNamePrefix}'. Wheels will get box colliders and cannot be rigged " +
                             "as motors — check the wheel node names.", root);
        }
        else if (usingDefaultPrefix && clusters.Count != ExpectedWheelClusters)
        {
            Debug.LogWarning($"RobotPartClassifier: expected {ExpectedWheelClusters} wheel clusters under " +
                             $"'{root.name}' but found {clusters.Count}. Check '{WheelNamePrefix}' node names " +
                             "and the cluster radius.", root);
        }
        return clusters;
    }

    // Combined renderer bounds of a named sub-group (or the whole robot when groupName is null),
    // expressed in the root's local space as a BoxCollider-style center + size.
    // Same semantics as FixRobotCollider.TryGetGroupLocalBounds, shared here for reuse.
    public static bool TryGetGroupLocalBounds(GameObject root, string groupName, out Vector3 center, out Vector3 size)
    {
        center = Vector3.zero;
        size = Vector3.zero;

        // Collect the renderers that belong to the group (or all of them).
        List<Renderer> renderers = new List<Renderer>();
        if (groupName == null)
        {
            renderers.AddRange(root.GetComponentsInChildren<Renderer>());
        }
        else
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>())
            {
                if (child.name.Contains(groupName))
                {
                    renderers.AddRange(child.GetComponentsInChildren<Renderer>());
                }
            }
        }

        if (renderers.Count == 0) return false;

        // World-space AABB over the group's renderers.
        Bounds world = renderers[0].bounds;
        for (int i = 1; i < renderers.Count; i++) world.Encapsulate(renderers[i].bounds);

        // Convert into the root's local space (root is unrotated/unscaled in practice,
        // but Abs keeps the size sane if that ever changes).
        Transform t = root.transform;
        center = t.InverseTransformPoint(world.center);
        Vector3 local = t.InverseTransformVector(world.size);
        size = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
        return true;
    }

    // True when the (already-normalized) name contains ANY of the comma-split tokens, case-
    // insensitively. Blank tokens are ignored so "Omni," or trailing commas don't match everything.
    private static bool MatchesAnyToken(string normalizedName, string[] tokens)
    {
        foreach (string token in tokens)
        {
            string t = token.Trim();
            if (t.Length > 0 && normalizedName.IndexOf(t, System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }
        return false;
    }

    private static bool IsAllDigits(string s, int start, int end)
    {
        if (start >= end) return false;
        for (int i = start; i < end; i++)
        {
            if (s[i] < '0' || s[i] > '9') return false;
        }
        return true;
    }

    private static int Depth(Transform node, Transform root)
    {
        int depth = 0;
        for (Transform t = node; t != null && t != root; t = t.parent) depth++;
        return depth;
    }
}
