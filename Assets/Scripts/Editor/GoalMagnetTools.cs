using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Adds a GoalStackMagnet (+ its GoalStackAnchor child) to every goal in the field scene, so pieces
// stacked on a goal get pulled to a visually perfect pose. A "goal" is any object named Goal* that
// carries a generated GoalFloor_Base child (built by FixGoals) — the anchor sits on top of that
// floor, aimed along the goal's up axis.
//
// Idempotent: re-running re-aims existing anchors and syncs the magnet wiring IN PLACE. Piece
// profiles (rest height / stack spacing) are only baked when a magnet has none yet, so per-goal
// tuning in the Inspector survives re-runs. Batch: -executeMethod FixGoalMagnets.RunBatch.
public static class FixGoalMagnets
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string AnchorName = "GoalStackAnchor";
    private const string FloorName = "GoalFloor_Base";

    // The generated goal floor box is 0.02 thick with its local forward along the goal's up
    // (FixGoals builds it at rotation = correctedOrientation, upAxis = correctedOrientation *
    // Vector3.forward), so the stack base sits half its thickness above its center.
    private const float FloorHalfThickness = 0.01f;

    [MenuItem("Tools/RoboSim/Field & Pieces/Add Goal Stack Magnets", false, 5)]
    private static void ApplyInteractive()
    {
        int touched = Apply(useUndo: true);
        EditorUtility.DisplayDialog("Add Goal Stack Magnets",
            touched > 0
                ? $"Stack magnets ensured on {touched} goal(s). Save the scene to keep them."
                : $"No goals found — this needs objects named Goal* with a {FloorName} child (run Rebuild Goal Colliders first).",
            "OK");
    }

    // Batch entry point for -executeMethod: throws on failure (nonzero exit).
    public static void RunBatch()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        int touched = Apply(useUndo: false);
        if (touched == 0)
            throw new System.InvalidOperationException(
                $"Add Goal Stack Magnets: no goals with a {FloorName} child found in {ScenePath}.");
        if (!EditorSceneManager.SaveScene(scene))
            throw new System.InvalidOperationException($"Add Goal Stack Magnets: failed to save {ScenePath}.");
        Debug.Log($"Add Goal Stack Magnets: magnets ensured on {touched} goal(s); scene saved.");
    }

    private static int Apply(bool useUndo)
    {
        // Bake-once profile defaults, measured from one cup and one pin actually in the scene.
        GoalStackMagnet.PieceProfile cupProfile = MeasureProfile("Cup");
        GoalStackMagnet.PieceProfile pinProfile = MeasureProfile("Pin");

        int touched = 0;
        foreach (GameObject root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("Goal")) continue;
                Transform floor = t.Find(FloorName);
                if (floor == null) continue; // the Goals/GoalsNeutral folders etc. have no floor child

                if (useUndo) Undo.RegisterFullObjectHierarchyUndo(t.gameObject, "Add Goal Stack Magnets");

                // Goal up axis from the generated floor's orientation; guard the sign so a flipped
                // import can't aim the stack downward.
                Vector3 up = floor.rotation * Vector3.forward;
                if (Vector3.Dot(up, Vector3.up) < 0f) up = -up;

                Transform anchor = t.Find(AnchorName);
                if (anchor == null)
                {
                    var anchorGo = new GameObject(AnchorName);
                    if (useUndo) Undo.RegisterCreatedObjectUndo(anchorGo, "Add Goal Stack Magnets");
                    anchor = anchorGo.transform;
                    anchor.SetParent(t, false);
                }
                anchor.SetPositionAndRotation(
                    floor.position + up * FloorHalfThickness,
                    Quaternion.FromToRotation(Vector3.up, up));

                GoalStackMagnet magnet = t.GetComponent<GoalStackMagnet>();
                if (magnet == null)
                    magnet = useUndo ? Undo.AddComponent<GoalStackMagnet>(t.gameObject) : t.gameObject.AddComponent<GoalStackMagnet>();
                magnet.stackAnchor = anchor;

                // These goals are stakes: pieces are rings caught near the POST TOP and guided down
                // around it. Bake the reach from the goal's own render height plus headroom — but
                // never below 2.8: the posts observably reach ~2.4 above the pocket while the goal
                // object's own renderers stop short of them (the post mesh lives elsewhere in the
                // FBX), and a ring deflecting off the post top must already be inside the window.
                float postTop = TryGetRendererBounds(t.gameObject, out Bounds goalBounds)
                    ? Vector3.Dot(goalBounds.max - anchor.position, up) : 0f;
                magnet.captureHeight = Mathf.Max(2.8f, postTop + 0.4f);

                // Bake measured defaults only into an empty list, so Inspector tuning survives.
                if (magnet.pieceProfiles == null || magnet.pieceProfiles.Count == 0)
                {
                    magnet.pieceProfiles = new System.Collections.Generic.List<GoalStackMagnet.PieceProfile>();
                    if (cupProfile != null) magnet.pieceProfiles.Add(Clone(cupProfile));
                    if (pinProfile != null) magnet.pieceProfiles.Add(Clone(pinProfile));
                }

                EditorUtility.SetDirty(magnet);
                EditorSceneManager.MarkSceneDirty(t.gameObject.scene);
                touched++;
            }
        }
        return touched;
    }

    // Default stack geometry for a piece type, measured from the first matching scene piece: its
    // standing height = the longest axis of its mesh bounds in world size. Rest height (center of
    // mass above the seat) is half that; stack advance (how much it raises the next slot) is the
    // full height. Starting points — the magnet's Inspector list is the place to fine-tune.
    private static GoalStackMagnet.PieceProfile MeasureProfile(string prefix)
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

            return new GoalStackMagnet.PieceProfile
            {
                namePrefix = prefix,
                restHeight = height * 0.5f,
                stackAdvance = height,
            };
        }
        Debug.LogWarning($"Add Goal Stack Magnets: no measurable '{prefix}*' piece in the scene — " +
                         $"that type gets no default profile (add one on the magnet manually).");
        return null;
    }

    private static GoalStackMagnet.PieceProfile Clone(GoalStackMagnet.PieceProfile p) =>
        new GoalStackMagnet.PieceProfile { namePrefix = p.namePrefix, restHeight = p.restHeight, stackAdvance = p.stackAdvance };

    private static bool TryGetRendererBounds(GameObject go, out Bounds bounds)
    {
        bounds = new Bounds();
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return false;
        bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        return true;
    }
}
