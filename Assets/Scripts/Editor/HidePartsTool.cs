using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Temporarily hide + de-collide part(s) for testing — the reliable way to get an obstruction out of
// the way in PLAY mode, not just the editor Scene view.
//
// Why the Hierarchy "eye" (Scene Visibility) didn't work: it only hides objects in the editor Scene
// view and has NO effect in Play mode or a build. To actually remove something at runtime you disable
// its Renderer (hides the mesh) and its Collider (stops it blocking things) — which is what these do,
// and that enabled-state persists into Play mode.
//
// Hide disables every Renderer + Collider under the selection; Show re-enables them. Undoable, and it
// only flips enabled flags (nothing is deleted or deactivated), so it's safe to use on robot parts
// while testing an intake/mechanism and flip back after. Note: it does NOT SetActive(false) — that
// would also disable an ArticulationBody link and can destabilize the rig; disabling the collider is
// enough to stop it obstructing. (Inertia was baked at rig time and isn't recomputed here, so
// re-enable before you re-rig.)
//
// If the field robot is spawned at runtime from a prefab, apply this inside the prefab (open it, or
// select parts in Prefab Mode) so the spawned copy inherits the change.
public static class HidePartsTool
{
    [MenuItem("Tools/RoboSim/Testing/Hide Selected (mesh + collision)", false, 1)]
    private static void Hide() => SetEnabled(false);

    [MenuItem("Tools/RoboSim/Testing/Show Selected (mesh + collision)", false, 2)]
    private static void Show() => SetEnabled(true);

    private static void SetEnabled(bool enabled)
    {
        GameObject[] roots = Selection.gameObjects;
        if (roots == null || roots.Length == 0)
        {
            EditorUtility.DisplayDialog("Hide / Show Parts",
                "Select the part or group to hide/show in the Hierarchy first.", "OK");
            return;
        }

        string verb = enabled ? "Show Parts" : "Hide Parts";
        int renderers = 0, colliders = 0;
        foreach (GameObject root in roots)
        {
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                Undo.RecordObject(r, verb);
                r.enabled = enabled;
                renderers++;
            }
            foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
            {
                Undo.RecordObject(c, verb);
                c.enabled = enabled;
                colliders++;
            }
            if (root.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.scene);
        }

        Debug.Log($"{verb}: {(enabled ? "enabled" : "disabled")} {renderers} renderer(s) and " +
                  $"{colliders} collider(s) under {roots.Length} selected object(s). Persists into Play mode.");
    }
}
