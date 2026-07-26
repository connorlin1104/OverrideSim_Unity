using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEditor;

// Validates the public/private robot rules.
//
// The risk this guards against is specific: a private robot is only actually hidden if EVERY reader
// of the catalog agrees. Miss one — the picker, the spawner's fallback, the controller-config screen,
// or either selection fallback — and a stale PlayerPrefs selection quietly puts the robot back on the
// field. These checks drive the real catalog API rather than re-implementing the filter.
//
// Runs entirely on an in-memory catalog, so it needs no scene and touches no project asset. It does
// use the real PlayerPrefs keys (that IS the storage), so it snapshots and restores them.
//
// Usage: Tools > RoboSim > Testing > Validate Robot Visibility.
// Batch: -executeMethod RobotVisibilityValidation.RunBatchValidate.
public static class RobotVisibilityValidation
{
    private const string PrivateCode = "654V-8213";

    [MenuItem("Tools/RoboSim/Testing/Validate Robot Visibility", false, 14)]
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

    private static string Validate()
    {
        // Snapshot the two prefs this test writes so a run never disturbs the real device state.
        string savedSelection = PlayerPrefs.GetString(RobotModelCatalog.SelectedModelPrefKey, string.Empty);
        string savedCodes = PlayerPrefs.GetString(RobotOwnerSettings.CodesPrefKey, string.Empty);

        var failures = new List<string>();
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
            };

            // --- locked: the private entries are invisible everywhere ---
            Check(failures, !VisibleIds(catalog).Contains("vis-private"),
                "a private robot is listed before its code is entered");
            Check(failures, !VisibleIds(catalog).Contains("vis-orphan"),
                "a private robot with no code is listed (nothing could ever reveal it)");
            Check(failures, VisibleIds(catalog).Contains("vis-public"),
                "the public robot is missing from the visible list");

            // The nastiest path: a device that had the private robot selected before it was locked.
            PlayerPrefs.SetString(RobotModelCatalog.SelectedModelPrefKey, "vis-private");
            Check(failures, catalog.SelectedModelId == "vis-public",
                $"a stale selection of a hidden robot survives (SelectedModelId = {catalog.SelectedModelId})");
            Check(failures, catalog.SelectedModel != null && catalog.SelectedModel.id == "vis-public",
                "SelectedModel returns the hidden robot for a stale selection");
            Check(failures, catalog.FirstVisibleWithPrefab()?.id == "vis-public",
                "the spawner's prefab fallback can reach a hidden robot");

            // --- unlocking: deliberately messy input, since this gets typed on a phone ---
            Check(failures, RobotOwnerSettings.AddCode("  654v-8213  "),
                "a valid code was rejected");
            Check(failures, VisibleIds(catalog).Contains("vis-private"),
                "the private robot is still hidden after its code was entered (normalization?)");
            Check(failures, !VisibleIds(catalog).Contains("vis-orphan"),
                "the code revealed an unrelated private robot");

            PlayerPrefs.SetString(RobotModelCatalog.SelectedModelPrefKey, "vis-private");
            Check(failures, catalog.SelectedModelId == "vis-private",
                "an unlocked robot cannot be selected");
            Check(failures, catalog.SelectedModel != null && catalog.SelectedModel.id == "vis-private",
                "SelectedModel does not return the unlocked robot");

            // --- re-locking ---
            Check(failures, RobotOwnerSettings.RemoveCode(PrivateCode), "removing a held code failed");
            Check(failures, !VisibleIds(catalog).Contains("vis-private"),
                "the robot stays visible after its code was forgotten");
            Check(failures, catalog.SelectedModelId == "vis-public",
                "the selection did not fall back to a visible robot after re-locking");

            // --- store hygiene ---
            Check(failures, !RobotOwnerSettings.AddCode("   "), "a blank code was accepted");
            RobotOwnerSettings.AddCode(PrivateCode);
            Check(failures, !RobotOwnerSettings.AddCode(PrivateCode.ToLowerInvariant()),
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

        if (failures.Count == 0) return "Robot visibility validation PASSED (13 checks).";

        var message = new StringBuilder($"FAILED: {failures.Count} robot visibility check(s):\n");
        foreach (string failure in failures) message.AppendLine("  - " + failure);
        return message.ToString();
    }

    private static List<string> VisibleIds(RobotModelCatalog catalog)
    {
        var ids = new List<string>();
        foreach (RobotModelCatalog.Entry entry in catalog.VisibleModels) ids.Add(entry.id);
        return ids;
    }

    private static void Check(List<string> failures, bool condition, string failureMessage)
    {
        if (!condition) failures.Add(failureMessage);
    }
}
