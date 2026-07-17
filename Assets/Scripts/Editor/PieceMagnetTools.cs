using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Adds a PieceStackMagnet to every cup, so a piece stacked on a RESTING cup snaps to a clean pose
// the way pieces snap onto goals. Covers authored cups in the field scene AND the match-load cup
// prefabs (so spawned cups are magnetic too).
//
// Idempotent: re-running re-bakes each cup's base offset / up axis but leaves any Inspector-tuned
// piece profiles alone. Prefabs are only re-saved when a magnet is newly added, to avoid churn.
// Batch: -executeMethod FixCupMagnets.RunBatch.
public static class FixCupMagnets
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string PrefabFolder = "Assets/Models/MatchLoadPreFabs";

    [MenuItem("Tools/RoboSim/Field & Pieces/Add Cup Stack Magnets", false, 7)]
    private static void ApplyInteractive()
    {
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        MeasureProfiles(out var cup, out var pin);
        int scene = ApplyScene(cup, pin, useUndo: true, out bool sceneChanged);
        if (sceneChanged) EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        int prefabs = ApplyPrefabs(cup, pin);
        EditorUtility.DisplayDialog("Add Cup Stack Magnets",
            scene + prefabs > 0
                ? $"Cup magnets ensured on {scene} scene cup(s) and {prefabs} cup prefab(s). " +
                  "Save the scene to keep the scene ones."
                : "No Cup* pieces found in the scene or the match-load prefabs.",
            "OK");
    }

    // Batch entry point for -executeMethod: throws on failure (nonzero exit).
    public static void RunBatch()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        MeasureProfiles(out var cup, out var pin);
        int sceneCups = ApplyScene(cup, pin, useUndo: false, out bool sceneChanged);
        int prefabCups = ApplyPrefabs(cup, pin);
        if (sceneCups == 0 && prefabCups == 0)
            throw new System.InvalidOperationException(
                "Add Cup Stack Magnets: no Cup* pieces found in the scene or the match-load prefabs.");
        if (sceneChanged && !EditorSceneManager.SaveScene(scene))
            throw new System.InvalidOperationException("Add Cup Stack Magnets: failed to save " + ScenePath);
        Debug.Log($"Add Cup Stack Magnets: {sceneCups} scene cup(s), {prefabCups} cup prefab(s); scene saved.");
    }

    private static int ApplyScene(PieceStackMagnet.PieceProfile cup, PieceStackMagnet.PieceProfile pin,
                                  bool useUndo, out bool changed)
    {
        changed = false;
        int n = 0;
        foreach (Rigidbody rb in Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include))
        {
            if (!rb.name.StartsWith("Cup")) continue;
            if (Configure(rb.gameObject, cup, pin, useUndo, out _)) { n++; changed = true; }
        }
        return n;
    }

    private static int ApplyPrefabs(PieceStackMagnet.PieceProfile cup, PieceStackMagnet.PieceProfile pin)
    {
        int n = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { PrefabFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                bool addedAny = false;
                // The match-load prefabs are GROUPS (e.g. BluePreFabNorth) whose cup piece is a
                // child named Cup* with its own Rigidbody — so search the whole prefab, not the root.
                foreach (Rigidbody rb in root.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (!rb.name.StartsWith("Cup")) continue;
                    if (Configure(rb.gameObject, cup, pin, useUndo: false, out bool added))
                    {
                        n++;
                        addedAny |= added;
                    }
                }
                if (addedAny) PrefabUtility.SaveAsPrefabAsset(root, path); // only re-save on a real add
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        return n;
    }

    // Ensure a PieceStackMagnet on this cup, its base/up axis re-baked from the mesh. `added` = the
    // component was newly created (vs. an existing one refreshed). Returns false if it isn't a cup
    // with a measurable mesh.
    private static bool Configure(GameObject go, PieceStackMagnet.PieceProfile cup,
                                  PieceStackMagnet.PieceProfile pin, bool useUndo, out bool added)
    {
        added = false;
        if (go.GetComponent<Rigidbody>() == null) return false;
        if (!MeasureBase(go, out Vector3 localBase, out Vector3 localUp)) return false;

        PieceStackMagnet magnet = go.GetComponent<PieceStackMagnet>();
        added = magnet == null;
        if (added)
            magnet = useUndo ? Undo.AddComponent<PieceStackMagnet>(go) : go.AddComponent<PieceStackMagnet>();

        magnet.localBaseOffset = localBase;
        magnet.localUpAxis = localUp;
        if (magnet.pieceProfiles == null || magnet.pieceProfiles.Count == 0)
        {
            magnet.pieceProfiles = new List<PieceStackMagnet.PieceProfile>();
            if (cup != null) magnet.pieceProfiles.Add(Clone(cup));
            if (pin != null) magnet.pieceProfiles.Add(Clone(pin));
        }
        EditorUtility.SetDirty(magnet);
        return true;
    }

    // Top-center of the cup and its standing axis, in the cup's OWN local frame (so both track the
    // cup as it moves). Measured from the longest axis of the mesh bounds.
    private static bool MeasureBase(GameObject go, out Vector3 localBase, out Vector3 localUp)
    {
        localBase = Vector3.zero;
        localUp = Vector3.up;
        MeshFilter mf = go.GetComponentInChildren<MeshFilter>();
        Mesh mesh = mf != null ? mf.sharedMesh : null;
        if (mesh == null) return false;

        Vector3 s = mesh.bounds.size;
        Vector3 axisMeshLocal = (s.x >= s.y && s.x >= s.z) ? Vector3.right : (s.y >= s.z) ? Vector3.up : Vector3.forward;
        float halfAlongAxis = Vector3.Dot(mesh.bounds.extents,
            new Vector3(Mathf.Abs(axisMeshLocal.x), Mathf.Abs(axisMeshLocal.y), Mathf.Abs(axisMeshLocal.z)));

        Vector3 worldCenter = mf.transform.TransformPoint(mesh.bounds.center);
        Vector3 worldTop = mf.transform.TransformPoint(mesh.bounds.center + axisMeshLocal * halfAlongAxis);
        Vector3 worldUp = (worldTop - worldCenter).sqrMagnitude > 1e-8f
            ? (worldTop - worldCenter).normalized : mf.transform.up;

        Transform root = go.transform;
        localBase = root.InverseTransformPoint(worldTop);
        localUp = root.InverseTransformDirection(worldUp).normalized;
        return true;
    }

    // Default stack geometry for a piece type, measured from the first matching scene piece.
    private static void MeasureProfiles(out PieceStackMagnet.PieceProfile cup, out PieceStackMagnet.PieceProfile pin)
    {
        cup = MeasureProfile("Cup");
        pin = MeasureProfile("Pin");
    }

    private static PieceStackMagnet.PieceProfile MeasureProfile(string prefix)
    {
        foreach (Rigidbody rb in Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude))
        {
            if (!rb.name.StartsWith(prefix)) continue;
            MeshFilter mf = rb.GetComponentInChildren<MeshFilter>();
            Mesh mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) continue;

            Vector3 size = Vector3.Scale(mesh.bounds.size, mf.transform.lossyScale);
            float height = Mathf.Max(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
            if (height <= 0f) continue;

            return new PieceStackMagnet.PieceProfile
            {
                namePrefix = prefix,
                restHeight = height * 0.5f,
                stackAdvance = height,
            };
        }
        Debug.LogWarning($"Add Cup Stack Magnets: no measurable '{prefix}*' piece in the scene — " +
                         $"that type gets no default profile (add one on the magnet manually).");
        return null;
    }

    private static PieceStackMagnet.PieceProfile Clone(PieceStackMagnet.PieceProfile p) =>
        new PieceStackMagnet.PieceProfile { namePrefix = p.namePrefix, restHeight = p.restHeight, stackAdvance = p.stackAdvance };
}
