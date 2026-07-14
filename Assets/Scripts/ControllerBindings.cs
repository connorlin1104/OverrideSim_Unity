using System;
using System.Collections.Generic;
using UnityEngine;

// Data model + persistence for the controller button -> robot mechanism mapping.
//
// Every on-screen (and physical) controller button can be assigned to one or more mechanism
// functions: a motor run forward, a motor run reverse (both hold-to-run), or a pneumatic toggle.
// A button may drive SEVERAL mechanisms at once — e.g. a DR4B whose two mirrored sides are two
// motors, driven from one button with one side reversed. ButtonRouter sums every assignment per
// motor and fires every pneumatic toggle, so N-per-button "just works" at runtime. Conversely,
// any number of buttons may target the same mechanism (the classic R1 = intake in / R2 =
// intake out layout is two assignments to one motor).
//
// Maps are stored per robot in PlayerPrefs as JSON ("ButtonMap_<robotId>"), keyed by the same
// catalog slug the URDF post-processor writes to RobotMechanisms.robotId — so a mapping made
// on the home screen for the selected robot is found by the ButtonRouter driving that robot
// in the field scene, and each robot keeps its own layout.

// Canonical button order. The index of each value doubles as the index into
// ButtonRouter.buttonActions and the config screen's button arrays, and the enum names match
// the action names in Assets/RobotControls.inputactions — keep all three in sync.
public enum ControllerButton
{
    L1, L2, R1, R2,
    Up, Down, Left, Right,
    X, B, A, Y,
}

[Serializable]
public class ButtonAssignment
{
    public string button;      // ControllerButton enum name
    public string mechanismId; // RobotMechanisms.Mechanism.id
    public string mode;        // ControllerMapSettings.ModeForward / ModeReverse / ModeToggle
}

// JsonUtility can't serialize dictionaries, so the map is a flat assignment list; lookups go
// through ControllerMapSettings.Find. Maps are tiny (<= 12 entries), so linear scans are fine.
[Serializable]
public class ButtonMap
{
    public List<ButtonAssignment> assignments = new List<ButtonAssignment>();
}

public static class ControllerMapSettings
{
    // Modes are strings, not an enum, so the persisted JSON stays readable and old saves keep
    // working if the set ever grows.
    public const string ModeForward = "forward";
    public const string ModeReverse = "reverse";
    public const string ModeToggle = "toggle";

    public const int ButtonCount = 12;

    public static string PrefKey(string robotId) => "ButtonMap_" + robotId;

    // Loads the saved map for a robot; a missing/corrupt pref yields an empty map, never null.
    public static ButtonMap Load(string robotId)
    {
        if (string.IsNullOrEmpty(robotId)) return new ButtonMap();
        string json = PlayerPrefs.GetString(PrefKey(robotId), string.Empty);
        if (string.IsNullOrEmpty(json)) return new ButtonMap();
        try
        {
            ButtonMap map = JsonUtility.FromJson<ButtonMap>(json);
            if (map == null) return new ButtonMap();
            if (map.assignments == null) map.assignments = new List<ButtonAssignment>();
            return map;
        }
        catch (Exception)
        {
            return new ButtonMap(); // a hand-edited/corrupt pref must not brick the config UI
        }
    }

    public static void Save(string robotId, ButtonMap map)
    {
        if (string.IsNullOrEmpty(robotId) || map == null) return;
        PlayerPrefs.SetString(PrefKey(robotId), JsonUtility.ToJson(map));
        PlayerPrefs.Save(); // flush now so a force-quit doesn't lose the layout
    }

    // Assigning always replaces the button's previous assignment (one function per button). Used
    // by auto-assign, which wants a clean forward/reverse pair. For multi-action buttons the
    // config UI uses AddAssignment/RemoveAssignment below instead.
    public static void SetAssignment(ButtonMap map, ControllerButton button, string mechanismId, string mode)
    {
        if (map == null) return;
        ClearAssignment(map, button);
        map.assignments.Add(new ButtonAssignment
        {
            button = button.ToString(),
            mechanismId = mechanismId,
            mode = mode,
        });
    }

    // Adds a mechanism function to a button WITHOUT clearing what's already there, so one button
    // can drive several mechanisms. Exact duplicates (same button+mechanism+mode) are ignored.
    public static void AddAssignment(ButtonMap map, ControllerButton button, string mechanismId, string mode)
    {
        if (map == null || string.IsNullOrEmpty(mechanismId)) return;
        if (map.assignments == null) map.assignments = new List<ButtonAssignment>();
        if (HasAssignment(map, button, mechanismId, mode)) return;
        map.assignments.Add(new ButtonAssignment
        {
            button = button.ToString(),
            mechanismId = mechanismId,
            mode = mode,
        });
    }

    // Removes one specific mechanism function from a button, leaving the button's other
    // assignments intact (the counterpart to AddAssignment).
    public static void RemoveAssignment(ButtonMap map, ControllerButton button, string mechanismId, string mode)
    {
        if (map == null || map.assignments == null) return;
        string name = button.ToString();
        map.assignments.RemoveAll(a => a != null && a.button == name
            && a.mechanismId == mechanismId && a.mode == mode);
    }

    // True if the button already carries this exact mechanism function (drives the config UI's
    // toggle checkmarks).
    public static bool HasAssignment(ButtonMap map, ControllerButton button, string mechanismId, string mode)
    {
        if (map == null || map.assignments == null) return false;
        string name = button.ToString();
        foreach (ButtonAssignment assignment in map.assignments)
        {
            if (assignment != null && assignment.button == name
                && assignment.mechanismId == mechanismId && assignment.mode == mode) return true;
        }
        return false;
    }

    public static void ClearAssignment(ButtonMap map, ControllerButton button)
    {
        if (map == null || map.assignments == null) return;
        string name = button.ToString();
        map.assignments.RemoveAll(a => a != null && a.button == name);
    }

    public static ButtonAssignment Find(ButtonMap map, ControllerButton button)
    {
        if (map == null || map.assignments == null) return null;
        string name = button.ToString();
        foreach (ButtonAssignment assignment in map.assignments)
        {
            if (assignment != null && assignment.button == name) return assignment;
        }
        return null;
    }

    // All assignments on a button, in map order — a button may drive several mechanisms.
    public static List<ButtonAssignment> FindAll(ButtonMap map, ControllerButton button)
    {
        List<ButtonAssignment> result = new List<ButtonAssignment>();
        if (map == null || map.assignments == null) return result;
        string name = button.ToString();
        foreach (ButtonAssignment assignment in map.assignments)
        {
            if (assignment != null && assignment.button == name) result.Add(assignment);
        }
        return result;
    }
}
