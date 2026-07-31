using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Reopens the scene you were working in when Unity comes up on a blank Untitled scene.
//
// THE PROBLEM THIS EXISTS FOR: Unity remembers the open scene in Library/LastSceneManagerSetup.txt and
// restores it at launch — except when it doesn't. An editor that is killed rather than quit never writes
// that file (there is a mono_crash report in this project's root), and a Library rebuild throws it away.
// Either way the next launch opens an empty Untitled scene, which looks exactly like the project has
// lost its field or its home screen, and the reflex is to re-run a build tool to "get it back" — a
// several-minute rebuild that regenerates and re-saves a scene that was never actually broken.
//
// So the last real scene is remembered HERE too, in EditorPrefs, where nothing in Library can take it
// with it. On launch, if the active scene is untitled, empty of unsaved work and alone, that scene is
// reopened. It falls back to the home screen if the remembered scene has since been deleted or renamed.
//
// It is deliberately timid: an untitled scene with unsaved changes in it, a second loaded scene, play
// mode, or an active scene that has a path (i.e. Unity DID restore something) all mean hands off. The
// worst case it can cause is opening a scene you were about to replace anyway; the case it prevents is
// mistaking a lost scene setup for a broken project.
//
// Toggle: Tools > RoboSim > Scenes > Reopen Last Scene on Launch.
[InitializeOnLoad]
public static class RestoreSceneOnLaunch
{
    private const string MenuPath = "Tools/RoboSim/Scenes/Reopen Last Scene on Launch";

    // EditorPrefs is shared by every project on the machine, so the remembered path has to be keyed by
    // which project it belongs to or two RoboSim checkouts would fight over one slot.
    private static readonly string LastScenePrefKey = "RoboSim.LastScene." + Application.dataPath;
    private const string EnabledPrefKey = "RoboSim.RestoreSceneOnLaunch.Enabled";

    // SessionState survives domain reloads but NOT an editor restart, which is exactly the lifetime
    // "have we already done the launch check" needs — [InitializeOnLoad] runs again on every recompile.
    private const string DoneThisSessionKey = "RoboSim.RestoreSceneOnLaunch.Done";

    static RestoreSceneOnLaunch()
    {
        EditorSceneManager.sceneOpened += (scene, _) => Remember(scene);
        EditorSceneManager.sceneSaved += Remember;

        if (SessionState.GetBool(DoneThisSessionKey, false)) return;
        EditorApplication.update += WaitThenRestore;
    }

    [MenuItem(MenuPath, false, 3)]
    private static void ToggleEnabled() => EditorPrefs.SetBool(EnabledPrefKey, !Enabled);

    [MenuItem(MenuPath, true)]
    private static bool ToggleChecked()
    {
        Menu.SetChecked(MenuPath, Enabled);
        return true;
    }

    private static bool Enabled => EditorPrefs.GetBool(EnabledPrefKey, true);

    private static void Remember(Scene scene)
    {
        // Untitled scenes have no path, and a scene that cannot be reopened is not worth remembering.
        if (!string.IsNullOrEmpty(scene.path)) EditorPrefs.SetString(LastScenePrefKey, scene.path);
    }

    // Runs until the editor has finished importing and compiling — checking before then would race
    // Unity's own scene restore and could open a scene on top of the one it was about to load.
    private static void WaitThenRestore()
    {
        if (EditorApplication.isUpdating || EditorApplication.isCompiling) return;

        EditorApplication.update -= WaitThenRestore;
        SessionState.SetBool(DoneThisSessionKey, true);

        // One more frame after the editor goes idle, so Unity's own restore has definitely had its turn.
        EditorApplication.delayCall += Restore;
    }

    private static void Restore()
    {
        if (!Enabled || EditorApplication.isPlayingOrWillChangePlaymode) return;

        Scene active = SceneManager.GetActiveScene();

        // Unity restored something, or there is more than one scene loaded, or the untitled scene has
        // work in it. Any of those and this is not the case we exist for.
        if (SceneManager.sceneCount != 1 || !string.IsNullOrEmpty(active.path) || active.isDirty) return;

        string remembered = EditorPrefs.GetString(LastScenePrefKey, string.Empty);
        string target = File.Exists(remembered) ? remembered
            : File.Exists(HomeScenePath) ? HomeScenePath
            : null;
        if (target == null) return;

        EditorSceneManager.OpenScene(target, OpenSceneMode.Single);
        Debug.Log($"Unity started on a blank Untitled scene (its saved scene setup didn't survive the " +
                  $"last shutdown), so '{target}' was reopened. Nothing was rebuilt and nothing was lost — " +
                  $"if a build tool looked necessary here before, it wasn't. Turn this off under {MenuPath}.");
    }

    private const string HomeScenePath = "Assets/Scenes/HomeScene.unity";
}
