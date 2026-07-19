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
    public string mode;        // one of ControllerMapSettings.Mode*
}

// How many buttons a mechanism is driven from. The MEANING differs by mechanism type (see
// ControllerMapSettings.StyleOneButton), which is why this stores a count-flavored style rather
// than the behavior itself.
[Serializable]
public class MechanismStyle
{
    public string mechanismId; // RobotMechanisms.Mechanism.id
    public string style;       // ControllerMapSettings.StyleOneButton / StyleTwoButton
}

// JsonUtility can't serialize dictionaries, so the map is a flat assignment list; lookups go
// through ControllerMapSettings.Find. Maps are tiny (<= 12 entries), so linear scans are fine.
[Serializable]
public class ButtonMap
{
    public List<ButtonAssignment> assignments = new List<ButtonAssignment>();
    // Per-mechanism control style. Absent means "the default for this mechanism's type", so old
    // saved maps read correctly with no migration.
    public List<MechanismStyle> styles = new List<MechanismStyle>();
}

public static class ControllerMapSettings
{
    // Modes are strings, not an enum, so the persisted JSON stays readable and old saves keep
    // working if the set ever grows.
    //
    // ModeToggle means different things on the two mechanism types, which is safe because
    // ButtonRouter branches on the mechanism's TYPE before it looks at the mode: on a motor it
    // latches the motor on/off, on a piston it flips extended<->retracted.
    public const string ModeForward = "forward";              // motor: hold to run forward
    public const string ModeReverse = "reverse";              // motor: hold to run reverse
    public const string ModeToggle = "toggle";                // motor: latch fwd on/off | piston: flip
    public const string ModeToggleReverse = "toggle_reverse"; // motor: latch reverse on/off
    public const string ModeExtend = "extend";                // piston: go extended and stay
    public const string ModeRetract = "retract";              // piston: go retracted and stay

    // How many buttons drive one mechanism. What that means per type:
    //   motor  — one: press to spin, press again to stop | two: hold forward / hold reverse
    //   piston — one: press flips extend<->retract        | two: one extends & stays, one retracts
    // Motors default to two (hold-to-run is what a driver expects of an intake or lift); pistons
    // default to one (a real VEX solenoid button is a toggle).
    public const string StyleOneButton = "one";
    public const string StyleTwoButton = "two";

    public const int ButtonCount = 12;

    // Preferred order for auto-assignment: shoulder/trigger pairs first (natural for hold-to-run
    // motors), then the d-pad, then the face buttons. Lives here rather than in the editor tool
    // because flipping a control style at runtime also needs to claim a free button.
    private static readonly ControllerButton[] AssignOrder =
    {
        ControllerButton.R1, ControllerButton.R2, ControllerButton.L1, ControllerButton.L2,
        ControllerButton.Up, ControllerButton.Down, ControllerButton.Left, ControllerButton.Right,
        ControllerButton.X, ControllerButton.A, ControllerButton.B, ControllerButton.Y,
    };

    // Next button with nothing on it at all. A button carrying ANY assignment counts as taken.
    public static bool TryNextFree(ButtonMap map, out ControllerButton free)
    {
        foreach (ControllerButton b in AssignOrder)
        {
            if (Find(map, b) == null) { free = b; return true; }
        }
        free = ControllerButton.L1;
        return false;
    }

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
            // Maps saved before control styles existed have no "styles" key at all.
            if (map.styles == null) map.styles = new List<MechanismStyle>();
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

    // --- Control style ---------------------------------------------------------------------

    // What a mechanism uses when the player hasn't chosen: motors get hold-to-run forward/reverse on
    // two buttons, pistons get a single toggle button.
    public static string DefaultStyle(string mechanismType)
        => mechanismType == RobotMechanisms.TypePneumatic ? StyleOneButton : StyleTwoButton;

    // The ordered set of button functions a mechanism exposes in a given style. Switching style maps
    // the old list onto the new one POSITION BY POSITION, which is what lets button choices carry
    // over across a flip (a motor's forward button becomes its toggle button, not a fresh one).
    public static string[] ModesFor(string mechanismType, string style)
    {
        bool one = style == StyleOneButton;
        if (mechanismType == RobotMechanisms.TypePneumatic)
            return one ? new[] { ModeToggle } : new[] { ModeExtend, ModeRetract };
        return one ? new[] { ModeToggle, ModeToggleReverse } : new[] { ModeForward, ModeReverse };
    }

    // A mechanism's control style: the player's explicit choice if there is one, else inferred from
    // the modes already on its buttons (so a map saved before styles existed reads correctly with no
    // migration), else the type's default.
    public static string GetStyle(ButtonMap map, string mechanismId, string mechanismType)
    {
        if (map != null && map.styles != null)
        {
            foreach (MechanismStyle entry in map.styles)
            {
                if (entry != null && entry.mechanismId == mechanismId && !string.IsNullOrEmpty(entry.style))
                    return entry.style;
            }
        }
        if (map != null && map.assignments != null)
        {
            foreach (ButtonAssignment a in map.assignments)
            {
                if (a == null || a.mechanismId != mechanismId) continue;
                if (a.mode == ModeToggle || a.mode == ModeToggleReverse) return StyleOneButton;
                if (a.mode == ModeExtend || a.mode == ModeRetract) return StyleTwoButton;
                if (a.mode == ModeForward || a.mode == ModeReverse) return StyleTwoButton;
            }
        }
        return DefaultStyle(mechanismType);
    }

    // Switches a mechanism between one- and two-button control. The buttons it's already on keep
    // driving it — only what they DO changes. Going one -> two claims a free button for the function
    // that gained a button (but only for a mechanism that was already mapped; an unmapped one just
    // records the choice). Going two -> one drops the function that lost its button.
    public static void SetStyle(ButtonMap map, string mechanismId, string mechanismType, string style)
    {
        if (map == null || string.IsNullOrEmpty(mechanismId)) return;
        if (style != StyleOneButton && style != StyleTwoButton) return;
        if (map.assignments == null) map.assignments = new List<ButtonAssignment>();
        if (map.styles == null) map.styles = new List<MechanismStyle>();

        string[] from = ModesFor(mechanismType, GetStyle(map, mechanismId, mechanismType));
        string[] to = ModesFor(mechanismType, style);

        // Record the choice first, so GetStyle is authoritative even for a mechanism with no buttons
        // yet — the assignment popup needs to offer the right pair of rows either way.
        MechanismStyle entry = null;
        foreach (MechanismStyle s in map.styles)
            if (s != null && s.mechanismId == mechanismId) { entry = s; break; }
        if (entry == null) map.styles.Add(new MechanismStyle { mechanismId = mechanismId, style = style });
        else entry.style = style;

        bool wasMapped = false;
        foreach (ButtonAssignment a in map.assignments)
            if (a != null && a.mechanismId == mechanismId) { wasMapped = true; break; }

        // Rewrite each function's buttons in place; functions past the end of the new style's list
        // are dropped by the sweep below.
        for (int i = 0; i < from.Length && i < to.Length; i++)
        {
            foreach (ButtonAssignment a in map.assignments)
                if (a != null && a.mechanismId == mechanismId && a.mode == from[i]) a.mode = to[i];
        }

        // Anything left on a mode this style doesn't use (a dropped function, or a hand-edited map)
        // goes, so a mechanism's assignments always match its style.
        var valid = new List<string>(to);
        map.assignments.RemoveAll(a => a != null && a.mechanismId == mechanismId && !valid.Contains(a.mode));

        // Gained a function: give it a button so the flip works without a trip to the popup.
        if (wasMapped)
        {
            for (int i = from.Length; i < to.Length; i++)
            {
                if (!TryNextFree(map, out ControllerButton free)) break;
                AddAssignment(map, free, mechanismId, to[i]);
            }
        }
    }

    // Forgets a mechanism's style choice (it falls back to inference/default). Used when its
    // bindings are cleared or the mechanism itself is gone, so entries can't accumulate.
    public static void RemoveStyle(ButtonMap map, string mechanismId)
    {
        if (map == null || map.styles == null) return;
        map.styles.RemoveAll(s => s == null || s.mechanismId == mechanismId);
    }

    // --- Mode labels ----------------------------------------------------------------------
    // One table, so the config screen, the editor overview window and the builders' result
    // dialogs can't drift apart on what a mode is called.

    // Sentence-case name for prose ("R1 = forward, R2 = reverse").
    public static string ModeLabel(string mode)
    {
        switch (mode)
        {
            case ModeReverse: return "reverse";
            case ModeToggle: return "toggle";
            case ModeToggleReverse: return "toggle reverse";
            case ModeExtend: return "extend";
            case ModeRetract: return "retract";
            default: return "forward";
        }
    }

    // Three-or-so letter tag for the controller diagram's button captions.
    public static string ModeCaption(string mode)
    {
        switch (mode)
        {
            case ModeReverse: return "REV";
            case ModeToggle: return "TOG";
            case ModeToggleReverse: return "TOG REV";
            case ModeExtend: return "EXT";
            case ModeRetract: return "RET";
            default: return "FWD";
        }
    }
}
