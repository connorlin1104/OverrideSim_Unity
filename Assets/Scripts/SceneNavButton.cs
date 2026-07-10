using UnityEngine;
using UnityEngine.SceneManagement;

// Tiny scene-navigation helper for UI buttons. Loading a scene needs a scene-name argument,
// which a Button's persistent onClick can't pass to SceneManager directly — so this component
// carries the name and exposes a parameterless Load() to wire into onClick.
//
// Usage: add to a Button, set Scene Name (must be in Build Settings), wire onClick to Load().
public class SceneNavButton : MonoBehaviour
{
    public string sceneName;

    public void Load()
    {
        SceneManager.LoadScene(sceneName);
    }
}
