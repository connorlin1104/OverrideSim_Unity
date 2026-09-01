using System;
using System.Collections.Generic;
using UnityEngine;

// Every player-facing setting, in one file. All of them are PlayerPrefs-backed — not stored on any
// asset — so changing one never dirties the project, and the choice persists per device across
// restarts. Each setting is its own small static class so call sites read as
// `DriveFeelSettings.TurnSensitivity`, not a grab-bag of unrelated keys.
//
// Every write flushes immediately (PlayerPrefs.Save) so a force-quit doesn't lose the choice; the
// SettingsPrefs helper is the single place that rule lives.

// The shared read/write shapes: bools stored as 0/1 ints, floats clamped on BOTH read and write so
// a stale or hand-edited pref can't smuggle an out-of-range value into the game.
internal static class SettingsPrefs
{
    public static bool GetBool(string key, bool defaultValue)
        => PlayerPrefs.GetInt(key, defaultValue ? 1 : 0) != 0;

    public static void SetBool(string key, bool value)
    {
        PlayerPrefs.SetInt(key, value ? 1 : 0);
        PlayerPrefs.Save(); // flush now so a force-quit doesn't lose the choice
    }

    public static float GetFloat(string key, float defaultValue, float min, float max)
        => Mathf.Clamp(PlayerPrefs.GetFloat(key, defaultValue), min, max);

    public static void SetFloat(string key, float value, float min, float max)
    {
        PlayerPrefs.SetFloat(key, Mathf.Clamp(value, min, max));
        PlayerPrefs.Save(); // flush now so a force-quit doesn't lose the choice
    }
}

// Which of the field scene's two camera views is active: the free-look field camera
// (TouchCameraController, the default) or the robot follow camera (RobotChaseCamera). Flipped by
// the field scene's camera button, CameraViewToggle. PlayerPrefs-backed so the Reset button, which
// reloads the scene, brings you back into the view you were driving in.
public static class CameraViewSettings
{
    public const string FollowRobotPrefKey = "CameraFollowRobot";
    public const bool DefaultFollowRobot = false;

    public static bool FollowRobot
    {
        get => SettingsPrefs.GetBool(FollowRobotPrefKey, DefaultFollowRobot);
        set => SettingsPrefs.SetBool(FollowRobotPrefKey, value);
    }
}

// How opaque the on-screen controls (joysticks AND controller buttons) are drawn in the field
// scene. Companion to JoystickSettings (size); ControlsAppearance applies both at scene load.
public static class ControlsOpacitySettings
{
    public const string OpacityPrefKey = "ControlsOpacity";

    // The default matches how the joysticks have always looked (their authored image alpha was
    // ~0.59). The floor keeps the controls faintly visible: a CanvasGroup at alpha 0 still
    // receives touches, and fully invisible-but-active controls are a trap.
    public const float MinOpacity = 0.2f;
    public const float MaxOpacity = 1f;
    public const float DefaultOpacity = 0.6f;

    public static float Opacity
    {
        get => SettingsPrefs.GetFloat(OpacityPrefKey, DefaultOpacity, MinOpacity, MaxOpacity);
        set => SettingsPrefs.SetFloat(OpacityPrefKey, value, MinOpacity, MaxOpacity);
    }
}

// How the drivetrain feels to drive. Unlike ReverseDriveSettings, RobotMotorController CACHES
// these at Awake rather than reading them live: they're consumed in FixedUpdate at 100 Hz, and
// entering the field scene always re-runs Awake, so a change still takes effect on the next Drive.
public static class DriveFeelSettings
{
    public const string DriveSensitivityPrefKey = "DriveSensitivity";
    public const string TurnSensitivityPrefKey = "TurnSensitivity";

    // Scales the throttle command. Below 1 the robot simply never commands full speed — useful on
    // a phone where a small on-screen stick makes fine control hard.
    public const float MinDriveSensitivity = 0.3f;
    public const float MaxDriveSensitivity = 1f;
    public const float DefaultDriveSensitivity = 1f;

    // Scales the turn command, on TOP of the robot's own turn rates, so 1.0 means "whatever this
    // robot was built to do" — and since 2026-08-30 that is a FULL-SPEED pivot from rest
    // (RobotMotorController.pivotTurnRate, 1.0) blending to the calmer 0.5 at full throttle.
    //
    // ABOVE 1 IS A STICK CURVE, NOT MORE TURN, and the old comment here had that wrong: it said 1.5
    // let "a driver who wants snappier pivots get back to the full rate", which the code never did —
    // the command is clamped to ±1 BEFORE the rate applies, so no sensitivity can command more than
    // a full-stick turn. What 1.5 really does is make full stick arrive at two thirds of the travel,
    // partly undoing the 0.55 turn expo: the stick answers sooner, the ceiling is unchanged. That is
    // a real and useful thing for a small on-screen stick, so the ceiling stays — it just isn't what
    // it claimed to be. 100% is now a full-speed pivot on its own.
    public const float MinTurnSensitivity = 0.3f;
    public const float MaxTurnSensitivity = 1.5f;
    public const float DefaultTurnSensitivity = 1f;

    public static float DriveSensitivity
    {
        get => SettingsPrefs.GetFloat(DriveSensitivityPrefKey, DefaultDriveSensitivity,
            MinDriveSensitivity, MaxDriveSensitivity);
        set => SettingsPrefs.SetFloat(DriveSensitivityPrefKey, value,
            MinDriveSensitivity, MaxDriveSensitivity);
    }

    public static float TurnSensitivity
    {
        get => SettingsPrefs.GetFloat(TurnSensitivityPrefKey, DefaultTurnSensitivity,
            MinTurnSensitivity, MaxTurnSensitivity);
        set => SettingsPrefs.SetFloat(TurnSensitivityPrefKey, value,
            MinTurnSensitivity, MaxTurnSensitivity);
    }

    // Retired: "Smooth Acceleration" and "Coast When You Let Go" were checkboxes here, and are now
    // unconditional — they aren't features, they're what a drivetrain does. Wipe the stored ints,
    // because a getter's default only applies when the key is ABSENT: anyone who once unticked
    // either box has a 0 on disk, and without this they would be silently stuck with the old
    // snap-throttle and locked-wheel stop forever, with no control left to turn them back on.
    // (Same DeleteKey-on-upgrade shape as ControlsLayoutSettings.)
    private const string RetiredSmoothAccelerationKey = "SmoothAcceleration";
    private const string RetiredCoastOnReleaseKey = "DriveCoastOnRelease";

    // "My Robot Has Traction Wheels" joins them. It asked the player a question about their hardware
    // that the sim cannot read off the model, and answering it wrong changed the physics — so it is
    // no longer theirs to answer, and RobotMotorController.BrakeFraction is the omni number for
    // everyone. The wipe matters for the same reason as above: anyone who ticked the box has a 1 on
    // disk, and there is no longer a control that could untick it.
    private const string RetiredTractionWheelsKey = "TractionWheels";

    private static readonly string[] RetiredKeys =
    {
        RetiredSmoothAccelerationKey, RetiredCoastOnReleaseKey, RetiredTractionWheelsKey,
    };

    public static void ClearRetiredKeys()
    {
        bool any = false;
        foreach (string key in RetiredKeys) any |= PlayerPrefs.HasKey(key);
        if (!any) return;

        foreach (string key in RetiredKeys) PlayerPrefs.DeleteKey(key);
        PlayerPrefs.Save();
    }
}

// RETIRED: WheelTypeSettings lived here. It stored whether the player's robot ran traction wheels
// rather than omnis — the one thing about a drivetrain the sim cannot read off the model, since
// every wheel is a sphere collider with one friction coefficient — and it picked between
// DrivetrainTuning's two brake fractions.
//
// It is gone because it was a physics switch dressed as a preference: a player who ticked it (or
// left it ticked from a robot they no longer drive) got a different stop with no way to tell that
// was why. Every robot now brakes on the omni number, which is what the box defaulted to and what
// almost every robot actually runs; DrivetrainTuning keeps both constants and DriveFeelValidation
// keeps checking their ordering, so putting a per-robot answer back is a small change.
// DriveFeelSettings.ClearRetiredKeys wipes the stored value.

// Which field scene Drive loads: the full competition field, or the stripped-down "lite" field.
//
// The full field is ~4,000 GameObjects and ~1,400 shadow-casting renderers, and its 45 stack magnets
// run a Physics.OverlapSphere each every fixed step (100 Hz). The lite field keeps one of each
// feature — one cup, pin, goal, wall, match loader and roller — which is enough to exercise every
// mechanism while running an order of magnitude cheaper. Build it with
// Tools > RoboSim > Scenes > Build Lite Field Scene.
public static class FieldSceneSettings
{
    public const string UseLiteFieldPrefKey = "UseLiteField";
    public const bool DefaultUseLiteField = false;

    // Scene names as registered in Build Settings (SceneManager.LoadScene takes the name, not path).
    public const string FullFieldSceneName = "SampleScene";
    public const string LiteFieldSceneName = "LiteScene";

    public static bool UseLiteField
    {
        get => SettingsPrefs.GetBool(UseLiteFieldPrefKey, DefaultUseLiteField);
        set => SettingsPrefs.SetBool(UseLiteFieldPrefKey, value);
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

// How large the on-screen driving joysticks are drawn. The joysticks are authored at a fixed pixel
// size that reads fine on a large editor Game view but feels cramped on a physically small phone,
// since the canvas scales by screen size and keeps them the same *proportion* everywhere.
// ControlsAppearance applies the value in the field scene.
public static class JoystickSettings
{
    public const string ScalePrefKey = "JoystickScale";

    // 1.0 == the size the joysticks are authored at in the scene. The default leans larger than
    // authored so the sticks are comfortable on a physically small phone out of the box; the
    // bounds keep them usable: never so tiny they can't be hit, never so huge they swallow the
    // field view.
    public const float MinScale = 0.6f;
    public const float MaxScale = 2.5f;
    public const float DefaultScale = 1.4f;

    // Multiplier applied to the joysticks' authored size.
    public static float Scale
    {
        get => SettingsPrefs.GetFloat(ScalePrefKey, DefaultScale, MinScale, MaxScale);
        set => SettingsPrefs.SetFloat(ScalePrefKey, value, MinScale, MaxScale);
    }
}

// Whether the match loaders spawn automatically when the robot drives onto their tape (the
// default), or only when the player presses the field scene's Match Load button. Manual mode
// exists for drivers who want the piece to fall INTO the robot (the manual spawn adds extra
// height) instead of picking it off the loader.
public static class MatchLoadSettings
{
    public const string AutomaticPrefKey = "AutomaticMatchloading";
    public const bool DefaultAutomatic = true;

    public static bool Automatic
    {
        get => SettingsPrefs.GetBool(AutomaticPrefKey, DefaultAutomatic);
        set => SettingsPrefs.SetBool(AutomaticPrefKey, value);
    }
}

// Which end of the robot the drive controls treat as "front". Off (default): forward stick drives
// the robot's normal front. On: the control frame is flipped 180°, so the opposite end becomes
// front — some drivers want the intake in front, others the scoring end. RobotMotorController
// reads it live in FixedUpdate, so no spawner wiring is needed.
public static class ReverseDriveSettings
{
    public const string ReversedPrefKey = "ReverseDriveDirection";
    public const bool DefaultReversed = false;

    public static bool Reversed
    {
        get => SettingsPrefs.GetBool(ReversedPrefKey, DefaultReversed);
        set => SettingsPrefs.SetBool(ReversedPrefKey, value);
    }
}

// Per-device saved positions for the on-screen control groups, edited on the home screen's
// "Edit Control Layout" preview and applied in the field scene by ControlsAppearance.
//
// Each control's saved value is an OFFSET (dx, dy) in 1920x1080 reference pixels from its
// authored position (x right, y up — the same axes as RectTransform.anchoredPosition), keyed by
// the control's field-scene GameObject name (see ControlsLayout). Storing a delta rather than an
// absolute position means a Reset just clears the keys, and re-running the Build Drive Controls
// tool (which re-authors the base positions) doesn't strand a saved layout.
public static class ControlsLayoutSettings
{
    private const string KeyPrefix = "ControlsPos_";

    public static Vector2 GetOffset(string controlName)
    {
        return new Vector2(
            PlayerPrefs.GetFloat(KeyPrefix + controlName + "_x", 0f),
            PlayerPrefs.GetFloat(KeyPrefix + controlName + "_y", 0f));
    }

    public static void SetOffset(string controlName, Vector2 offset)
    {
        PlayerPrefs.SetFloat(KeyPrefix + controlName + "_x", offset.x);
        PlayerPrefs.SetFloat(KeyPrefix + controlName + "_y", offset.y);
        PlayerPrefs.Save(); // flush now so a force-quit doesn't lose the layout
    }

    // Clears every saved control position so the layout returns to the authored defaults.
    public static void Reset()
    {
        foreach (ControlsLayout.ControlInfo control in ControlsLayout.Controls)
        {
            PlayerPrefs.DeleteKey(KeyPrefix + control.name + "_x");
            PlayerPrefs.DeleteKey(KeyPrefix + control.name + "_y");
        }
        PlayerPrefs.Save();
    }
}

// The owner codes entered on THIS device, which is what unlocks private robots in the model picker.
//
// Why codes rather than accounts: teams don't want their designs hole-counted, but the app has no
// backend and no sign-in. A private robot still ships inside the app — it's just filtered out of the
// picker until its owner types the code they were given. That stops casual copying between players;
// it is NOT protection against someone digging through the app's files, and it isn't meant to be.
// Real privacy needs the robot to live on a server and download only after the uploader is verified.
//
// Stored in PlayerPrefs as one JSON string, following the ControllerBindings convention (JsonUtility
// can't serialize a Dictionary or a bare List, so it goes through a small [Serializable] holder).
public static class RobotOwnerSettings
{
    public const string CodesPrefKey = "UnlockedRobotCodes";

    [Serializable]
    private class CodeList
    {
        public List<string> codes = new List<string>();
    }

    // Codes are compared case- and whitespace-insensitively: they get read off a screenshot or a
    // Discord message and typed on a phone keyboard, so "654v-8213 " must match "654V-8213".
    public static string Normalize(string code)
    {
        return string.IsNullOrWhiteSpace(code) ? string.Empty : code.Trim().ToUpperInvariant();
    }

    public static bool HasCode(string code)
    {
        string wanted = Normalize(code);
        if (wanted.Length == 0) return false;

        foreach (string held in Load().codes)
        {
            if (Normalize(held) == wanted) return true;
        }
        return false;
    }

    // A catalog entry may name SEVERAL codes that reveal it — typically its own one-off code plus the
    // code for the team that owns it — so one team code can open every robot that team uploaded.
    //
    // Separators are comma, semicolon and whitespace. '-' is deliberately NOT one: codes look like
    // "654V-8213", and splitting on the dash would turn one code into two that match nothing.
    private static readonly char[] Separators = { ',', ';', ' ', '\t', '\n', '\r' };

    // The normalized, de-duplicated codes named by `codes`. Empty for a null/blank field.
    public static List<string> SplitCodes(string codes)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(codes)) return result;

        foreach (string part in codes.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            string normalized = Normalize(part);
            if (normalized.Length > 0 && !result.Contains(normalized)) result.Add(normalized);
        }
        return result;
    }

    // True when ANY of the codes named by `codes` is held on this device — holding the team code is
    // as good as holding the robot's own code.
    public static bool HasAnyCode(string codes)
    {
        foreach (string code in SplitCodes(codes))
        {
            if (HasCode(code)) return true;
        }
        return false;
    }

    public static List<string> AllCodes()
    {
        return Load().codes;
    }

    // Returns false when the code is blank or already held, so the UI can say so instead of silently
    // appearing to work.
    public static bool AddCode(string code)
    {
        string normalized = Normalize(code);
        if (normalized.Length == 0) return false;
        if (HasCode(normalized)) return false;

        CodeList list = Load();
        list.codes.Add(normalized);
        Save(list);
        return true;
    }

    public static bool RemoveCode(string code)
    {
        string normalized = Normalize(code);
        CodeList list = Load();
        int removed = list.codes.RemoveAll(held => Normalize(held) == normalized);
        if (removed == 0) return false;

        Save(list);
        return true;
    }

    private static CodeList Load()
    {
        string json = PlayerPrefs.GetString(CodesPrefKey, string.Empty);
        if (string.IsNullOrEmpty(json)) return new CodeList();

        try
        {
            CodeList list = JsonUtility.FromJson<CodeList>(json);
            if (list == null) return new CodeList();
            if (list.codes == null) list.codes = new List<string>();
            return list;
        }
        catch (Exception)
        {
            // A corrupt pref must not lock the player out of the picker entirely — start clean.
            return new CodeList();
        }
    }

    private static void Save(CodeList list)
    {
        PlayerPrefs.SetString(CodesPrefKey, JsonUtility.ToJson(list));
        PlayerPrefs.Save(); // flush now so a force-quit doesn't lose an unlock the player just typed
    }
}

// Which inbox notices this device has already read.
//
// A notice that hands over an owner code needs no memory of its own: once the code is held, the item
// filters itself out on the next launch. A notice that is only a MESSAGE — "I couldn't get your robot
// running, here's what went wrong" — leaves no such trace, so without this it would reappear every
// single launch, forever, with no way to make it stop.
//
// Keyed by RobotInboxService.KeyFor, which is the item's own id when it has one and a fingerprint of
// its text otherwise. Fingerprinting on purpose: rewriting a message SHOULD show it again, because a
// rewritten message is a new thing to say.
public static class RobotInboxSettings
{
    public const string SeenPrefKey = "SeenInboxNotices";

    [Serializable]
    private class KeyList
    {
        public List<string> keys = new List<string>();
    }

    public static bool HasSeen(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        foreach (string seen in Load().keys)
        {
            if (seen == key) return true;
        }
        return false;
    }

    // False when the key is blank or already recorded, so a caller can count what it actually did.
    public static bool MarkSeen(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (HasSeen(key)) return false;

        KeyList list = Load();
        list.keys.Add(key);
        Save(list);
        return true;
    }

    public static void Forget()
    {
        if (!PlayerPrefs.HasKey(SeenPrefKey)) return;

        PlayerPrefs.DeleteKey(SeenPrefKey);
        PlayerPrefs.Save();
    }

    private static KeyList Load()
    {
        string json = PlayerPrefs.GetString(SeenPrefKey, string.Empty);
        if (string.IsNullOrEmpty(json)) return new KeyList();

        try
        {
            KeyList list = JsonUtility.FromJson<KeyList>(json);
            if (list == null) return new KeyList();
            if (list.keys == null) list.keys = new List<string>();
            return list;
        }
        catch (Exception)
        {
            // A corrupt pref re-shows an old notice, which is a far smaller harm than throwing on
            // the launch path — so start clean rather than propagate.
            return new KeyList();
        }
    }

    private static void Save(KeyList list)
    {
        PlayerPrefs.SetString(SeenPrefKey, JsonUtility.ToJson(list));
        PlayerPrefs.Save();
    }
}
