using UnityEngine;

// Which field scene Drive loads: the full competition field, or the stripped-down "lite" field.
//
// The full field is ~4,000 GameObjects and ~1,400 shadow-casting renderers, and its 45 stack magnets
// run a Physics.OverlapSphere each every fixed step (100 Hz). The lite field keeps one of each
// feature — one cup, pin, goal, wall, match loader and roller — which is enough to exercise every
// mechanism while running an order of magnitude cheaper. Build it with
// Tools > RoboSim > Scenes > Build Lite Field Scene.
//
// Stored in PlayerPrefs — not on any asset — so toggling it never dirties the project and the choice
// persists per device (same approach as ReverseDriveSettings / MatchLoadSettings).
public static class FieldSceneSettings
{
    public const string UseLiteFieldPrefKey = "UseLiteField";
    public const bool DefaultUseLiteField = false;

    // Scene names as registered in Build Settings (SceneManager.LoadScene takes the name, not path).
    public const string FullFieldSceneName = "SampleScene";
    public const string LiteFieldSceneName = "LiteScene";

    public static bool UseLiteField
    {
        get => PlayerPrefs.GetInt(UseLiteFieldPrefKey, DefaultUseLiteField ? 1 : 0) != 0;
        set
        {
            PlayerPrefs.SetInt(UseLiteFieldPrefKey, value ? 1 : 0);
            PlayerPrefs.Save(); // flush now so a force-quit doesn't lose the choice
        }
    }

    // The scene Drive should load. Falls back to the full field when the lite scene hasn't been
    // built (or wasn't added to Build Settings) — the setting can be switched on before the tool has
    // ever run, and a LoadScene on a name that isn't in the build is a hard error, not a warning.
    public static string ActiveFieldScene
    {
        get
        {
            if (!UseLiteField) return FullFieldSceneName;
            if (Application.CanStreamedLevelBeLoaded(LiteFieldSceneName)) return LiteFieldSceneName;
            Debug.LogWarning($"FieldSceneSettings: '{LiteFieldSceneName}' is not in Build Settings — " +
                             "run Tools > RoboSim > Scenes > Build Lite Field Scene. Loading the full field.");
            return FullFieldSceneName;
        }
    }
}
