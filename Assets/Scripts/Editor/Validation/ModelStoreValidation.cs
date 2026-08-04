using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

// Validates the model store: where a stowed model is allowed to live, and whether the census that
// guards a fetch can actually see the failures it claims to.
//
// Every check here is mutation-proven — the thing it asserts is deliberately broken first and the
// check is confirmed to go red — because a census that always passes is worse than no census. It
// would let a partial reconnection through while reporting that the robot was verified, and a robot
// missing an arm spawns, drives, and logs nothing.
//
// Three failures this pins:
//
//   - THE STORE IS SOMEWHERE IT MUST NOT BE. Inside the project (Unity imports it, git sees it, and
//     a root under Build/RobotBundles is rsynced into a PUBLIC bucket), behind a symlink (invisible
//     to the "outside the project" test), or in an iCloud/Dropbox folder (evicted to a stub that
//     reports the right size and then fails the read that was meant to verify it).
//
//   - THE CENSUS CANNOT COUNT. If the pointer regex misses, every count is zero, and zero == zero
//     passes at fetch time on a model that restored nothing.
//
//   - THE FINGERPRINT IS BLIND. Counts see a pointer that fails to resolve; only the fingerprint
//     sees a pointer that resolves to the WRONG object. It has to move when geometry moves, and it
//     has to NOT move for a model that is byte-identical — a false alarm is how an override becomes
//     routine, and an override is how the real failure gets waved through.
//
// Usage: Tools > RoboSim > Validation > Validate Model Store
// Batch: -executeMethod ModelStoreValidation.RunBatchValidate
public static class ModelStoreValidation
{
    // Pinned live shape. These are measured from the repo, and they are pinned rather than
    // recomputed because a census that derives its own expectation cannot fail.
    private const string FieldGuid = "f17a927cca8b54a15b3534415e569c99";

    private static readonly (string Prefab, string Fbx, string Guid, int Pointers)[] Robots =
    {
        ("Assets/Robots/360RpmDrivetrain.prefab", "Assets/Models/360 RPM Drivetrain.fbx",
            "9819a041fc6454a7cb45f44d66999380", 1042),
        ("Assets/Robots/654V_v1.prefab", "Assets/Models/654V v1.fbx",
            "95a97fe85b55b4a1482d933ecb40a1c5", 1609),
        ("Assets/Robots/654V_v2.prefab", "Assets/Models/Override 1.0.fbx",
            "b7fdd004ff27242e7aa22bebe04b3f48", 3288),
        ("Assets/Robots/654V_v3.prefab", "Assets/Models/Ryan_CascadeRobot.fbx",
            "b236d9f11b1d74f2a8fbe6a407be1550", 1534),
    };

    [MenuItem("Tools/RoboSim/Validation/Validate Model Store", false, 8)]
    private static void RunFromMenu()
    {
        string result = Validate();
        EditorUtility.DisplayDialog("Validate Model Store", result, "OK");
        Debug.Log(result);
    }

    public static void RunBatchValidate()
    {
        string result = Validate();
        Debug.Log(result);
        if (result.StartsWith("FAILED")) throw new InvalidOperationException(result);
    }

    private class Checks
    {
        public readonly List<string> Failures = new List<string>();
        public int Count;

        public void That(bool condition, string failureMessage)
        {
            Count++;
            if (!condition) Failures.Add(failureMessage);
        }

        // The mutation half: something that MUST be refused. A guard nobody has watched reject
        // anything is a guard nobody knows is wired up.
        public void Refuses(Action action, string what)
        {
            Count++;
            try { action(); }
            catch (Exception) { return; }
            Failures.Add($"{what} was accepted, but it must be refused");
        }
    }

    private static string Validate()
    {
        var checks = new Checks();

        CheckStoreRoot(checks);
        CheckIgnoreRule(checks);
        CheckOwnership(checks);
        CheckCensusCounts(checks);
        CheckFingerprint(checks);

        if (checks.Failures.Count == 0)
            return $"PASSED: model store validation ({checks.Count} checks).";

        var report = new StringBuilder();
        report.AppendLine($"FAILED: model store validation ({checks.Failures.Count} of {checks.Count} checks).");
        foreach (string failure in checks.Failures) report.AppendLine("  - " + failure);
        return report.ToString();
    }

    // ---------------------------------------------------------------------------------------------

    private static void CheckStoreRoot(Checks checks)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string project = Directory.GetParent(Application.dataPath).FullName;

        // The default must be an absolute path built from the real home directory. '~' is a shell
        // convention that .NET does not expand, so a default written as "~/RoboSimModelStore" would
        // create <project>/~/RoboSimModelStore — inside the repo, matched by *.fbx filter=lfs, and
        // committed by the next `git add -A`.
        checks.That(Path.IsPathRooted(ModelStore.DefaultRoot),
            $"the default store root '{ModelStore.DefaultRoot}' is not absolute");
        checks.That(!ModelStore.DefaultRoot.Contains("~"),
            $"the default store root '{ModelStore.DefaultRoot}' contains a literal '~', which .NET " +
            "does not expand — it would resolve inside the project");

        // A good root is accepted. Without this the suite would pass with ValidateRoot throwing on
        // everything, which refuses correctly and is completely useless.
        checks.That(Accepts(() => ModelStore.ValidateRoot(ModelStore.DefaultRoot)),
            $"the default store root '{ModelStore.DefaultRoot}' was refused");
        checks.That(Accepts(() => ModelStore.ValidateRoot(Path.Combine(home, "RoboSimStoreTest"))),
            "a plain absolute path in the home directory was refused");

        // macOS ships /var and /tmp as system symlinks. The first version of the symlink guard
        // walked the whole ancestor chain and so refused every path under Path.GetTempPath() — a
        // guard firing on correct input, which is how an override becomes routine and how the real
        // failure eventually gets waved through. A link the user made in their own home is the case
        // worth catching; a system one is not this tool's business.
        checks.That(Accepts(() => ModelStore.ValidateRoot(Path.Combine(Path.GetTempPath(), "RoboSimStoreTest"))),
            "a path under the system temp directory was refused — the symlink guard is policing " +
            "system paths rather than the user's own home");

        checks.Refuses(() => ModelStore.ValidateRoot(null), "a null store root");
        checks.Refuses(() => ModelStore.ValidateRoot("  "), "a blank store root");
        checks.Refuses(() => ModelStore.ValidateRoot("ModelStore"), "a relative store root");
        checks.Refuses(() => ModelStore.ValidateRoot("~/RoboSimModelStore"),
            "a store root starting with an unexpanded '~'");
        checks.Refuses(() => ModelStore.ValidateRoot(project), "the project folder itself");
        checks.Refuses(() => ModelStore.ValidateRoot(Path.Combine(project, "Build", "RobotBundles")),
            "a store root under Build/RobotBundles (which is rsynced into the public bucket)");
        checks.Refuses(() => ModelStore.ValidateRoot(Path.Combine(project, "Assets", "Models")),
            "a store root inside Assets/");

        // Sibling, not child. A bare StartsWith would read this as being inside the project.
        checks.That(Accepts(() => ModelStore.ValidateRoot(project + "_Store")),
            "a sibling directory whose name merely starts with the project path was refused — the " +
            "'inside the project' test is missing its trailing separator");

        foreach (string synced in new[] { "Desktop", "Documents", "Dropbox" })
            checks.Refuses(() => ModelStore.ValidateRoot(Path.Combine(home, synced, "RoboSimModelStore")),
                $"a store root under ~/{synced}, which is cloud-managed and evicts to a stub");
    }

    // The ignore rule is what keeps submitted models out of git, and it has to be a DIRECTORY rule.
    // A per-filename rule cannot work: Fusion exports are called export.fbx, so a rule written for
    // one player's model silently swallows the next player's.
    private static void CheckIgnoreRule(Checks checks)
    {
        string gitignore = Path.Combine(Directory.GetParent(Application.dataPath).FullName, ".gitignore");
        checks.That(File.Exists(gitignore), ".gitignore is missing");
        if (!File.Exists(gitignore)) return;

        string text = File.ReadAllText(gitignore);
        checks.That(text.Contains("[Ss]ubmitted/") || text.Contains("Submitted/"),
            $"nothing in .gitignore covers {ModelStore.SubmittedFolder}/, so a submitted model would " +
            "be committed into Git LFS — which deleting it later does not reclaim, and which the " +
            "repo's 1 GB free allowance cannot hold ten of");
    }

    // The field is the reason ReferrersOf returns a list. It is referenced by both scenes and four
    // match-load prefabs, so it is needed at every app build and can never be stowed; each robot is
    // referenced by exactly one prefab, which is what makes stowing bounded.
    private static void CheckOwnership(Checks checks)
    {
        List<string> fieldReferrers = ModelStore.ReferrersOf(FieldGuid);
        checks.That(fieldReferrers.Count > 1,
            $"the field FBX has {fieldReferrers.Count} referrers — it is supposed to be referenced " +
            "by both scenes and the match-load prefabs, which is what structurally prevents it from " +
            "ever being stowed");

        foreach ((string prefab, string fbx, string guid, int _) in Robots)
        {
            if (!File.Exists(fbx)) continue;  // stowed, which is allowed
            List<string> referrers = ModelStore.ReferrersOf(guid);
            checks.That(referrers.Count == 1 && referrers[0] == prefab,
                $"'{fbx}' is referenced by {referrers.Count} files, expected exactly '{prefab}' — " +
                "a model with more than one owner cannot be stowed");
        }
    }

    // Pinned counts, plus the mutation that proves the counter counts. Without the mutation, a regex
    // that matched nothing would report 0 == 0 and pass, and the same bug at fetch time would wave
    // through a model that restored nothing at all.
    private static void CheckCensusCounts(Checks checks)
    {
        foreach ((string prefab, string fbx, string guid, int pointers) in Robots)
        {
            if (!File.Exists(fbx) || !File.Exists(prefab)) continue;

            ModelStore.Census census = ModelStore.Take(prefab, fbx, guid);
            checks.That(census.Pointers == pointers,
                $"'{prefab}' has {census.Pointers} pointers into '{fbx}', expected {pointers}");
            checks.That(census.Resolved == census.Pointers,
                $"'{prefab}' resolves only {census.Resolved} of its {census.Pointers} pointers into " +
                $"'{fbx}' — that robot is already missing geometry");
            checks.That(census.OtherPointers == 0,
                $"'{prefab}' has {census.OtherPointers} pointers into '{fbx}' that are neither a mesh " +
                "nor a material, which is not a relationship this tool models");

            // A wrong guid must count nothing. If it counted anything, the census would be matching
            // on shape rather than on identity and every model would look like every other.
            checks.That(ModelStore.Take(prefab, fbx, new string('0', 32)).Pointers == 0,
                $"'{prefab}' reported pointers for a guid that is not in it");
        }

        MutateOnePointerAway(checks);
    }

    // Delete a single pointer from a copy of a real prefab and require the count to drop by exactly
    // one. This is the check that proves a partial reconnection would be VISIBLE — it is the same
    // arithmetic a fetch does, run against a file where the answer is known.
    private static void MutateOnePointerAway(Checks checks)
    {
        (string prefab, string fbx, string guid, int pointers) = Robots[3];  // 654V v3
        if (!File.Exists(prefab) || !File.Exists(fbx)) return;

        string text = File.ReadAllText(prefab);
        int at = text.IndexOf("guid: " + guid, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            checks.That(false, $"could not find a pointer to mutate in '{prefab}'");
            return;
        }

        // Break just the guid of one pointer, so the file stays otherwise identical.
        string mutated = text.Substring(0, at) + "guid: " + new string('f', 32) +
                         text.Substring(at + 6 + guid.Length);

        string scratch = Path.Combine(Path.GetTempPath(), "robosim-census-mutation.prefab");
        try
        {
            File.WriteAllText(scratch, mutated);
            ModelStore.Census census = ModelStore.Take(scratch, fbx, guid);
            checks.That(census.Pointers == pointers - 1,
                $"removing one pointer from '{prefab}' gave a count of {census.Pointers}, expected " +
                $"{pointers - 1} — the census cannot see a missing pointer, so it cannot see a " +
                "partial reconnection either");
        }
        finally { if (File.Exists(scratch)) File.Delete(scratch); }
    }

    // The fingerprint's whole job is catching pointers that resolve to the WRONG object, which no
    // count can see. Two properties have to hold at once, and they pull in opposite directions.
    private static void CheckFingerprint(Checks checks)
    {
        // Stable: the same input twice is the same hash. FNV rather than string.GetHashCode, which
        // is randomized per process and would make every fingerprint meaningless across runs.
        checks.That(ModelStore.Fnv1a("robot") == ModelStore.Fnv1a("robot"),
            "the fingerprint is not stable across two calls in one process");
        checks.That(ModelStore.Fnv1a("robot") != ModelStore.Fnv1a("robo"),
            "the fingerprint did not change for different input");

        var seen = new Dictionary<string, string>();
        foreach ((string prefab, string fbx, string guid, int _) in Robots)
        {
            if (!File.Exists(fbx) || !File.Exists(prefab)) continue;

            ModelStore.Census first = ModelStore.Take(prefab, fbx, guid);
            ModelStore.Census second = ModelStore.Take(prefab, fbx, guid);

            // Not stable => every fetch is a false alarm, and a tool that cries wolf gets overridden.
            checks.That(first.Fingerprint == second.Fingerprint,
                $"'{prefab}' fingerprints differently on two consecutive reads " +
                $"({first.Fingerprint} then {second.Fingerprint}) — a spurious mismatch at fetch " +
                "time would be reported as the catastrophic case");

            // Two robots must not share one. If they did, the fingerprint would be measuring the
            // shape of the census rather than the contents of the model.
            if (seen.TryGetValue(first.Fingerprint, out string other))
                checks.That(false,
                    $"'{prefab}' and '{other}' have the same fingerprint {first.Fingerprint}");
            else seen[first.Fingerprint] = prefab;

            // Content, not just names. The failure the fingerprint exists for is a reimport that
            // hands back the same ids with the same names and different geometry, so the line has to
            // carry the geometry. Checked by finding the vertex count independently, on the mesh
            // itself, and requiring it to appear.
            checks.That(CarriesGeometry(first, fbx),
                $"'{prefab}'s fingerprint lines do not carry mesh vertex counts, so a reimport that " +
                "changed the geometry but not the names would fingerprint identically");
        }
    }

    // Matched by fileID, NOT by name. Mesh names are not unique inside these exports — the first
    // version of this check matched on name, picked a different sub-asset that happened to share
    // one, and reported that the fingerprint was missing geometry it was in fact carrying. That is
    // also the reason the census keys on fileID everywhere: a name identifies nothing here.
    //
    // The vertex count is read from the end of the line rather than by field index, so a part name
    // containing a '|' shifts nothing.
    private static bool CarriesGeometry(ModelStore.Census census, string fbxPath)
    {
        foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
        {
            if (asset is not Mesh mesh) continue;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string _, out long fileId))
                continue;

            string prefix = fileId.ToString(CultureInfo.InvariantCulture) + "|Mesh|";
            foreach (string line in census.Lines)
            {
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;

                string[] parts = line.Split('|');
                if (parts.Length < 7) return false;
                return parts[parts.Length - 4] == mesh.vertexCount.ToString(CultureInfo.InvariantCulture);
            }
        }
        return false;
    }

    private static bool Accepts(Action action)
    {
        try { action(); return true; }
        catch (Exception) { return false; }
    }
}
