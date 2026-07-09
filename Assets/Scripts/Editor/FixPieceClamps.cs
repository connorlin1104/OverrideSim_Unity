using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Adds a MinHeightClamp to every pre-placed cup/pin body in the scene so they can't be crushed
// through the floor. Spawned pieces get the clamp automatically from MatchLoaderController; this
// tool covers the ~85 pieces already placed on the field. It filters by name so the robot and
// the field rollers (which also have Rigidbodies) are skipped.
public class FixPieceClamps
{
    [MenuItem("Tools/VEX/Add Height Clamp to Pieces")]
    private static void AddClamps()
    {
        Rigidbody[] bodies = Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
        int added = 0;
        foreach (Rigidbody body in bodies)
        {
            string n = body.gameObject.name;
            // Cup bodies are named "Cup"; pin bodies "Pin<colors>". Rollers/robot don't match.
            if (!(n.StartsWith("Cup") || n.StartsWith("Pin"))) continue;
            if (body.GetComponent<MinHeightClamp>() != null) continue;

            Undo.AddComponent<MinHeightClamp>(body.gameObject);
            added++;
        }

        if (bodies.Length > 0) EditorSceneManager.MarkSceneDirty(bodies[0].gameObject.scene);
        Debug.Log($"Add Height Clamp to Pieces: added MinHeightClamp to {added} cup/pin bodies " +
                  $"(scanned {bodies.Length} rigidbodies).");
    }
}
