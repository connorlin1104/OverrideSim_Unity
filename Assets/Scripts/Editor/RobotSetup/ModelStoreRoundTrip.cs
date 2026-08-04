using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// End-to-end proof that Stow and Fetch round-trip a real FBX with every prefab pointer intact.
//
// RUN THIS AFTER A UNITY UPGRADE, before trusting any fetch. Sub-asset fileIDs are derived by the
// importer from node names and hierarchy, so the whole scheme rests on "the same bytes reimport to
// the same ids" — which is true for a fixed Unity and is exactly what an editor upgrade is entitled
// to change. Nothing else in the suite would notice: a robot whose meshes came back with different
// geometry under the same ids passes every count.
//
// DELIBERATELY NOT A VALIDATOR, and it must stay out of the suite. It exercises the delete path, and
// a test that removes a model from Assets/ on every routine run is the most dangerous thing that
// could live in a suite people run without watching. It only ever touches the throwaway copy it
// makes itself under Assets/Models/Submitted/RoundTripTest.*, never a real model, and it cleans up
// in both the pass and fail paths.
//
// Batch: -executeMethod ModelStoreRoundTrip.Run
public static class ModelStoreRoundTrip
{
    private const string Source = "Assets/Models/360 RPM Drivetrain.fbx";
    private const string TestFbx = "Assets/Models/Submitted/RoundTripTest.fbx";
    private const string TestPrefab = "Assets/Models/Submitted/RoundTripTest.prefab";

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "RoboSimStoreRoundTrip");
        string log = string.Empty;
        try
        {
            log = Execute(root);
            Debug.Log(log);
        }
        catch (Exception e)
        {
            Debug.LogError("ROUND TRIP FAILED: " + e.Message + "\n\n" + log);
            Cleanup(root);
            EditorApplication.Exit(1);
            return;
        }
        Cleanup(root);
        EditorApplication.Exit(0);
    }

    private static string Execute(string root)
    {
        Cleanup(root);
        Directory.CreateDirectory(Path.GetDirectoryName(TestFbx));

        // A real Fusion export, copied so it gets its own guid and its own sub-asset ids.
        File.Copy(Source, TestFbx, true);
        AssetDatabase.ImportAsset(TestFbx,
            ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(TestFbx);
        if (model == null) throw new InvalidOperationException($"'{TestFbx}' did not import.");

        // Saving an instance of the imported model produces a prefab whose MeshFilters and
        // MeshRenderers point straight into the FBX — the same {fileID, guid, type: 3} pointers a
        // real robot prefab carries, which is the thing under test.
        GameObject instance = UnityEngine.Object.Instantiate(model);
        try { PrefabUtility.SaveAsPrefabAsset(instance, TestPrefab); }
        finally { UnityEngine.Object.DestroyImmediate(instance); }
        AssetDatabase.Refresh();

        string guid = AssetDatabase.AssetPathToGUID(TestFbx);
        ModelStore.Census before = ModelStore.Take(TestPrefab, TestFbx, guid);
        if (before.Pointers == 0)
            throw new InvalidOperationException(
                "the test prefab has no pointers into the test FBX, so this proves nothing");

        string beforeHash = ModelStore.Sha256OfFile(TestFbx);
        long beforeLength = new FileInfo(TestFbx).Length;

        string stow = ModelStoreWindow.Stow(TestFbx, root, false);

        if (File.Exists(TestFbx))
            throw new InvalidOperationException("Stow reported success but the FBX is still in Assets/");
        string payload = Path.Combine(ModelStore.FolderFor(root, guid), ModelStore.PayloadName);
        if (!File.Exists(payload))
            throw new InvalidOperationException($"Stow reported success but '{payload}' is missing");
        if (ModelStore.Sha256OfFile(payload) != beforeHash)
            throw new InvalidOperationException("the stored payload does not hash to the original bytes");

        // With the model gone, every pointer must now fail to resolve. If they still resolved, the
        // census would be reading a stale Library artifact and the fetch check would be worthless.
        ModelStore.Census gone = ModelStore.Take(TestPrefab, TestFbx, guid);
        if (gone.Resolved != 0)
            throw new InvalidOperationException(
                $"with the FBX stowed, {gone.Resolved} pointers still resolve — the census is reading " +
                "something other than the file on disk, so it cannot detect a bad fetch");

        string fetch = ModelStoreWindow.Fetch(guid, root);

        if (!File.Exists(TestFbx))
            throw new InvalidOperationException("Fetch reported success but the FBX is not in Assets/");
        if (ModelStore.Sha256OfFile(TestFbx) != beforeHash)
            throw new InvalidOperationException("the fetched FBX does not hash to the original bytes");
        if (new FileInfo(TestFbx).Length != beforeLength)
            throw new InvalidOperationException("the fetched FBX is a different length");
        if (AssetDatabase.AssetPathToGUID(TestFbx) != guid)
            throw new InvalidOperationException("the fetched FBX came back with a different guid");

        ModelStore.Census after = ModelStore.Take(TestPrefab, TestFbx, guid);
        if (after.Pointers != before.Pointers || after.Resolved != before.Resolved)
            throw new InvalidOperationException(
                $"reconnected {after.Resolved}/{after.Pointers}, was {before.Resolved}/{before.Pointers}");
        if (after.Fingerprint != before.Fingerprint)
            throw new InvalidOperationException(
                $"the fingerprint moved across a round trip: {before.Fingerprint} -> {after.Fingerprint}");

        return "ROUND TRIP PASSED\n\n" +
               $"  pointers   {before.Resolved}/{before.Pointers} before, " +
               $"{gone.Resolved} while stowed, {after.Resolved}/{after.Pointers} after\n" +
               $"  sha256     {beforeHash} unchanged\n" +
               $"  fingerprint {before.Fingerprint} unchanged\n\n" +
               "--- stow ---\n" + stow + "\n--- fetch ---\n" + fetch;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (File.Exists(TestPrefab)) AssetDatabase.DeleteAsset(TestPrefab);
            if (File.Exists(TestFbx)) AssetDatabase.DeleteAsset(TestFbx);
            if (Directory.Exists(root)) Directory.Delete(root, true);
            AssetDatabase.Refresh();
        }
        catch (Exception e) { Debug.LogWarning("round trip cleanup: " + e.Message); }
    }
}
