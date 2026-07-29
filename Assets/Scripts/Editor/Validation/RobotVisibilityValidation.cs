using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEditor;

// Validates the public/private robot rules, including team grouping.
//
// The risk this guards against is specific: a private robot is only actually hidden if EVERY reader
// of the catalog agrees. Miss one — the picker, the spawner's fallback, the controller-config screen,
// or either selection fallback — and a stale PlayerPrefs selection quietly puts the robot back on the
// field. These checks drive the real catalog API rather than re-implementing the filter.
//
// Runs entirely on an in-memory catalog, so it needs no scene and touches no project asset. It does
// use the real PlayerPrefs keys (that IS the storage), so it snapshots and restores them.
//
// Usage: Tools > RoboSim > Validation > Validate Robot Visibility.
// Batch: -executeMethod RobotVisibilityValidation.RunBatchValidate.
public static class RobotVisibilityValidation
{
    private const string PrivateCode = "654V-8213";
    private const string TeamCode = "654V-TEAM";
    private const string SoloCode = "CLAW-9F2K";

    [MenuItem("Tools/RoboSim/Validation/Validate Robot Visibility", false, 4)]
    private static void RunFromMenu()
    {
        string result = Validate();
        EditorUtility.DisplayDialog("Validate Robot Visibility", result, "OK");
        Debug.Log(result);
    }

    public static void RunBatchValidate()
    {
        string result = Validate();
        Debug.Log(result);
        if (result.StartsWith("FAILED")) throw new System.InvalidOperationException(result);
    }

    // Counts itself, so adding a check never leaves the summary line lying about how many ran.
    private class Checks
    {
        public readonly List<string> Failures = new List<string>();
        public int Count;

        public void That(bool condition, string failureMessage)
        {
            Count++;
            if (!condition) Failures.Add(failureMessage);
        }
    }

    private static string Validate()
    {
        // Snapshot the two prefs this test writes so a run never disturbs the real device state.
        string savedSelection = PlayerPrefs.GetString(RobotModelCatalog.SelectedModelPrefKey, string.Empty);
        string savedCodes = PlayerPrefs.GetString(RobotOwnerSettings.CodesPrefKey, string.Empty);

        var checks = new Checks();
        RobotModelCatalog catalog = null;
        GameObject publicPrefab = null;
        GameObject privatePrefab = null;

        try
        {
            PlayerPrefs.DeleteKey(RobotOwnerSettings.CodesPrefKey);

            publicPrefab = new GameObject("ValidationPublicRobot");
            privatePrefab = new GameObject("ValidationPrivateRobot");

            catalog = ScriptableObject.CreateInstance<RobotModelCatalog>();
            catalog.models = new List<RobotModelCatalog.Entry>
            {
                new RobotModelCatalog.Entry
                {
                    id = "vis-public", displayName = "Public Bot", prefab = publicPrefab,
                    visibility = RobotModelCatalog.Visibility.Public,
                },
                new RobotModelCatalog.Entry
                {
                    id = "vis-private", displayName = "Private Bot", prefab = privatePrefab,
                    visibility = RobotModelCatalog.Visibility.Private, ownerCode = PrivateCode,
                },
                new RobotModelCatalog.Entry
                {
                    id = "vis-orphan", displayName = "Private Bot With No Code",
                    visibility = RobotModelCatalog.Visibility.Private, ownerCode = string.Empty,
                },
                // Team grouping: two entries sharing one code, so it unlocks a SET rather than a robot.
                new RobotModelCatalog.Entry
                {
                    id = "vis-team-a", displayName = "Team Bot A",
                    visibility = RobotModelCatalog.Visibility.Private, ownerCode = TeamCode,
                },
                // Its own one-off code AND the team's, deliberately spaced and mixed-case the way an
                // authoring field gets typed. Either should reveal it.
                new RobotModelCatalog.Entry
                {
                    id = "vis-team-b", displayName = "Team Bot B",
                    visibility = RobotModelCatalog.Visibility.Private,
                    ownerCode = " claw-9f2k ,  " + TeamCode + " ",
                },
            };

            // --- locked: the private entries are invisible everywhere ---
            checks.That(!VisibleIds(catalog).Contains("vis-private"),
                "a private robot is listed before its code is entered");
            checks.That(!VisibleIds(catalog).Contains("vis-orphan"),
                "a private robot with no code is listed (nothing could ever reveal it)");
            checks.That(VisibleIds(catalog).Contains("vis-public"),
                "the public robot is missing from the visible list");
            checks.That(!VisibleIds(catalog).Contains("vis-team-a") &&
                        !VisibleIds(catalog).Contains("vis-team-b"),
                "a team's robots are listed before the team code is entered");

            // The nastiest path: a device that had the private robot selected before it was locked.
            PlayerPrefs.SetString(RobotModelCatalog.SelectedModelPrefKey, "vis-private");
            checks.That(catalog.SelectedModelId == "vis-public",
                $"a stale selection of a hidden robot survives (SelectedModelId = {catalog.SelectedModelId})");
            checks.That(catalog.SelectedModel != null && catalog.SelectedModel.id == "vis-public",
                "SelectedModel returns the hidden robot for a stale selection");
            checks.That(catalog.FirstVisibleWithPrefab()?.id == "vis-public",
                "the spawner's prefab fallback can reach a hidden robot");

            // --- unlocking: deliberately messy input, since this gets typed on a phone ---
            checks.That(RobotOwnerSettings.AddCode("  654v-8213  "),
                "a valid code was rejected");
            checks.That(VisibleIds(catalog).Contains("vis-private"),
                "the private robot is still hidden after its code was entered (normalization?)");
            checks.That(!VisibleIds(catalog).Contains("vis-orphan"),
                "the code revealed an unrelated private robot");
            checks.That(!VisibleIds(catalog).Contains("vis-team-a"),
                "one robot's code revealed a different team's robot");

            PlayerPrefs.SetString(RobotModelCatalog.SelectedModelPrefKey, "vis-private");
            checks.That(catalog.SelectedModelId == "vis-private",
                "an unlocked robot cannot be selected");
            checks.That(catalog.SelectedModel != null && catalog.SelectedModel.id == "vis-private",
                "SelectedModel does not return the unlocked robot");

            // --- team grouping: one code, a whole set ---
            checks.That(RobotOwnerSettings.AddCode("  654v-team "), "the team code was rejected");
            List<string> withTeam = VisibleIds(catalog);
            checks.That(withTeam.Contains("vis-team-a") && withTeam.Contains("vis-team-b"),
                "the team code did not unlock every robot that names it");
            checks.That(!withTeam.Contains("vis-orphan"),
                "the team code revealed a robot that doesn't name it");

            // Removing the team code must re-hide the one that ONLY had the team code, while the entry
            // carrying its own code as well stays hidden too (its solo code was never entered).
            checks.That(RobotOwnerSettings.RemoveCode(TeamCode), "removing the team code failed");
            checks.That(!VisibleIds(catalog).Contains("vis-team-a"),
                "a team robot stays visible after the team code was forgotten");
            checks.That(!VisibleIds(catalog).Contains("vis-team-b"),
                "a multi-code robot stays visible when none of its codes are held");

            // ...and the solo code alone reveals only the entry that names it.
            checks.That(RobotOwnerSettings.AddCode(SoloCode), "the one-off code was rejected");
            checks.That(VisibleIds(catalog).Contains("vis-team-b"),
                "a robot's own code does not reveal it when it also names a team code");
            checks.That(!VisibleIds(catalog).Contains("vis-team-a"),
                "a one-off code leaked into the rest of the team's robots");
            checks.That(RobotOwnerSettings.RemoveCode(SoloCode), "removing the one-off code failed");

            // --- splitting, straight from the entry ---
            RobotModelCatalog.Entry multi = catalog.models.Find(e => e.id == "vis-team-b");
            checks.That(multi.OwnerCodes.Count == 2,
                $"a two-code entry split into {multi.OwnerCodes.Count} code(s)");
            checks.That(multi.OwnerCodes.Contains(SoloCode) && multi.OwnerCodes.Contains(TeamCode),
                "splitting an owner-code field lost or mangled a code");
            checks.That(RobotOwnerSettings.SplitCodes("654V-8213").Count == 1,
                "a single code with a dash in it was split apart");
            checks.That(RobotOwnerSettings.SplitCodes(null).Count == 0, "a null owner-code field split into codes");
            checks.That(RobotOwnerSettings.SplitCodes("A-1, a-1 ,A-1").Count == 1,
                "a repeated code was not de-duplicated");

            // --- re-locking ---
            checks.That(RobotOwnerSettings.RemoveCode(PrivateCode), "removing a held code failed");
            checks.That(!VisibleIds(catalog).Contains("vis-private"),
                "the robot stays visible after its code was forgotten");
            checks.That(catalog.SelectedModelId == "vis-public",
                "the selection did not fall back to a visible robot after re-locking");

            // --- store hygiene ---
            checks.That(!RobotOwnerSettings.AddCode("   "), "a blank code was accepted");
            RobotOwnerSettings.AddCode(PrivateCode);
            checks.That(!RobotOwnerSettings.AddCode(PrivateCode.ToLowerInvariant()),
                "the same code was accepted twice in a different case");
        }
        finally
        {
            if (catalog != null) Object.DestroyImmediate(catalog);
            if (publicPrefab != null) Object.DestroyImmediate(publicPrefab);
            if (privatePrefab != null) Object.DestroyImmediate(privatePrefab);

            if (string.IsNullOrEmpty(savedSelection)) PlayerPrefs.DeleteKey(RobotModelCatalog.SelectedModelPrefKey);
            else PlayerPrefs.SetString(RobotModelCatalog.SelectedModelPrefKey, savedSelection);
            if (string.IsNullOrEmpty(savedCodes)) PlayerPrefs.DeleteKey(RobotOwnerSettings.CodesPrefKey);
            else PlayerPrefs.SetString(RobotOwnerSettings.CodesPrefKey, savedCodes);
            PlayerPrefs.Save();
        }

        if (checks.Failures.Count == 0)
            return $"Robot visibility validation PASSED ({checks.Count} checks).";

        var message = new StringBuilder($"FAILED: {checks.Failures.Count} of {checks.Count} robot " +
                                        "visibility check(s):\n");
        foreach (string failure in checks.Failures) message.AppendLine("  - " + failure);
        return message.ToString();
    }

    private static List<string> VisibleIds(RobotModelCatalog catalog)
    {
        var ids = new List<string>();
        foreach (RobotModelCatalog.Entry entry in catalog.VisibleModels) ids.Add(entry.id);
        return ids;
    }
}
