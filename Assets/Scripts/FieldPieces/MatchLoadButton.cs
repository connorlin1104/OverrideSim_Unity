using UnityEngine;
using UnityEngine.UI;

// The field scene's "Match Load" button, shown only when Automatic Matchloading is OFF in
// Settings. Pressing it spawns a match load from the loader whose tape the robot is currently
// on (the tape trigger still tracks which loader the robot is at in manual mode); the manual
// spawn drops from extra height so it can fall into the robot.
//
// The setting is read once in Start: the field scene is reloaded fresh on every entry, so a
// one-shot read is always current (same rationale as ControlsAppearance). The onClick listener
// is added at runtime, so the scene builder never stacks persistent listeners.
// Created by Tools > RoboSim > Scenes > Build Drive Controls.
[RequireComponent(typeof(Button))]
public class MatchLoadButton : MonoBehaviour
{
    private Button button;
    private MatchLoaderController[] loaders;

    void Awake()
    {
        button = GetComponent<Button>();
        button.onClick.AddListener(OnPressed);
    }

    void Start()
    {
        if (MatchLoadSettings.Automatic)
        {
            gameObject.SetActive(false); // automatic mode: the loaders spawn on their own
            return;
        }
        loaders = FindObjectsByType<MatchLoaderController>(FindObjectsInactive.Exclude);
        if (loaders.Length == 0)
            Debug.LogWarning("MatchLoadButton: no MatchLoaderController in the scene — the button will stay disabled.", this);
    }

    void Update()
    {
        // Live-enable only while some loader could actually spawn (robot on its tape, previous
        // piece carried away) so a dead press is visibly impossible. Four loaders — a trivial poll.
        bool any = false;
        if (loaders != null)
            foreach (MatchLoaderController loader in loaders)
                if (loader != null && loader.CanManualSpawn) { any = true; break; }
        button.interactable = any;
    }

    private void OnPressed()
    {
        if (loaders == null) return;
        foreach (MatchLoaderController loader in loaders)
            if (loader != null && loader.RequestManualSpawn()) return; // first ready loader wins
    }
}
