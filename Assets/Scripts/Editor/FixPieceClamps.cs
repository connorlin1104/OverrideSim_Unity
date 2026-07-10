using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Adds a MinHeightClamp to every pre-placed cup/pin body in the scene so they can't be crushed
// through the floor, and syncs the existing ones to the values below. Spawned pieces get the clamp
// automatically from MatchLoaderController; this tool covers the ~85 pieces already placed on the
// field. It filters by name so the robot and the field rollers (which also have Rigidbodies) are
// skipped.
//
// Re-runnable: tweak the constants, re-run, and every cup/pin on the field picks up the new values.
// To dial VisualLift in by eye instead, select one cup and drag its MinHeightClamp > Visual Lift in
// the Inspector (it updates live, no play mode needed), then paste the value you liked here and
// re-run to apply it to the whole field.
public class FixPieceClamps
{
    private const float FloorY = 0.72f;
    private const float Tolerance = 0.05f;
    private const float VisualLift = 0.03f; // cosmetic mesh lift only — see MinHeightClamp

    [MenuItem("Tools/VEX/Add Height Clamp to Pieces")]
    private static void AddClamps()
    {
        Rigidbody[] bodies = Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude);
        int added = 0, synced = 0;
        foreach (Rigidbody body in bodies)
        {
            string n = body.gameObject.name;
            // Cup bodies are named "Cup"; pin bodies "Pin<colors>". Rollers/robot don't match.
            if (!(n.StartsWith("Cup") || n.StartsWith("Pin"))) continue;

            // The lift moves the piece's mesh children, so record the whole hierarchy for undo.
            Undo.RegisterFullObjectHierarchyUndo(body.gameObject, "Add Height Clamp to Pieces");

            MinHeightClamp clamp = body.GetComponent<MinHeightClamp>();
            if (clamp == null) { clamp = Undo.AddComponent<MinHeightClamp>(body.gameObject); added++; }
            else synced++;

            clamp.floorY = FloorY;
            clamp.tolerance = Tolerance;
            clamp.visualLift = VisualLift;
            clamp.ApplyVisualLift(); // no-op when the mesh already carries this lift
            EditorUtility.SetDirty(clamp);
        }

        if (bodies.Length > 0) EditorSceneManager.MarkSceneDirty(bodies[0].gameObject.scene);
        Debug.Log($"Add Height Clamp to Pieces: added MinHeightClamp to {added} cup/pin bodies, " +
                  $"synced {synced} existing (visualLift={VisualLift}, floorY={FloorY}, " +
                  $"tolerance={Tolerance}); scanned {bodies.Length} rigidbodies.");
    }
}