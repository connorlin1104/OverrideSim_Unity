using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Headless checks for the button-map mode/style logic in ControllerBindings — the part with no
// visible surface until you're in the field with a controller in your hands, so it's worth proving
// without a Play session.
//
// Covers: the per-type defaults, which functions each style exposes, that flipping a style rewrites
// a mechanism's EXISTING buttons in place (rather than orphaning them), that a gained function
// claims a free button, and that maps saved before styles existed still load and read correctly.
//
// Usage: Tools > RoboSim > Testing > Validate Control Styles, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod ControlStyleValidation.RunBatchValidate
// which exits nonzero on the first failed check.
public static class ControlStyleValidation
{
    // Its own PlayerPrefs key, deleted at the end, so a real robot's saved layout is never touched.
    private const string TestRobotId = "__controlstyle_validation__";

    private const string Motor = RobotMechanisms.TypeMotor;
    private const string Pneumatic = RobotMechanisms.TypePneumatic;

    [MenuItem("Tools/RoboSim/Testing/Validate Control Styles", false, 10)]
    private static void RunInteractive()
    {
        try
        {
            string report = Run();
            EditorUtility.DisplayDialog("Validate Control Styles", report, "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Validate Control Styles", "FAILED\n\n" + e.Message, "OK");
            Debug.LogException(e);
        }
    }

    public static void RunBatchValidate()
    {
        try
        {
            Debug.Log(Run());
        }
        catch (Exception e)
        {
            Debug.LogError("Validate Control Styles FAILED: " + e.Message);
            EditorApplication.Exit(1);
            return;
        }
        EditorApplication.Exit(0);
    }

    private static string Run()
    {
        int checks = 0;
        try
        {
            checks += DefaultsAndTables();
            checks += MotorStyleFlip();
            checks += PneumaticStyleFlip();
            checks += UnmappedMechanism();
            checks += LegacyMapsStillWork();
            checks += EveryModeHasLabels();
        }
        finally
        {
            PlayerPrefs.DeleteKey(ControllerMapSettings.PrefKey(TestRobotId));
            PlayerPrefs.Save();
        }
        return $"Validate Control Styles: PASSED ({checks} checks).";
    }

    // Motors default to hold fwd/rev on two buttons; pistons to a single toggle.
    private static int DefaultsAndTables()
    {
        var map = new ButtonMap();
        Assert(ControllerMapSettings.GetStyle(map, "intake", Motor) == ControllerMapSettings.StyleTwoButton,
            "a motor with no history should default to two-button");
        Assert(ControllerMapSettings.GetStyle(map, "doinker", Pneumatic) == ControllerMapSettings.StyleOneButton,
            "a piston with no history should default to one-button");

        AssertModes(Motor, ControllerMapSettings.StyleTwoButton,
            ControllerMapSettings.ModeForward, ControllerMapSettings.ModeReverse);
        AssertModes(Motor, ControllerMapSettings.StyleOneButton,
            ControllerMapSettings.ModeToggle, ControllerMapSettings.ModeToggleReverse);
        AssertModes(Pneumatic, ControllerMapSettings.StyleTwoButton,
            ControllerMapSettings.ModeExtend, ControllerMapSettings.ModeRetract);
        AssertModes(Pneumatic, ControllerMapSettings.StyleOneButton, ControllerMapSettings.ModeToggle);
        return 6;
    }

    // A motor's two hold buttons become two latching buttons and back, staying on the SAME buttons.
    private static int MotorStyleFlip()
    {
        var map = new ButtonMap();
        ControllerMapSettings.AddAssignment(map, ControllerButton.R1, "intake", ControllerMapSettings.ModeForward);
        ControllerMapSettings.AddAssignment(map, ControllerButton.R2, "intake", ControllerMapSettings.ModeReverse);

        ControllerMapSettings.SetStyle(map, "intake", Motor, ControllerMapSettings.StyleOneButton);
        Assert(ControllerMapSettings.GetStyle(map, "intake", Motor) == ControllerMapSettings.StyleOneButton,
            "style should read back as one-button");
        AssertHas(map, ControllerButton.R1, "intake", ControllerMapSettings.ModeToggle,
            "R1 should have become the latching toggle");
        AssertHas(map, ControllerButton.R2, "intake", ControllerMapSettings.ModeToggleReverse,
            "R2 should have become the latching reverse toggle");
        Assert(CountFor(map, "intake") == 2, "flipping style must not add or drop a motor's buttons");

        ControllerMapSettings.SetStyle(map, "intake", Motor, ControllerMapSettings.StyleTwoButton);
        AssertHas(map, ControllerButton.R1, "intake", ControllerMapSettings.ModeForward,
            "R1 should be hold-forward again");
        AssertHas(map, ControllerButton.R2, "intake", ControllerMapSettings.ModeReverse,
            "R2 should be hold-reverse again");
        Assert(CountFor(map, "intake") == 2, "flipping back must not add or drop buttons");
        return 7;
    }

    // A piston's one toggle button splits into extend + retract, and the new function claims a free
    // button rather than being left unreachable. Flipping back drops it again.
    private static int PneumaticStyleFlip()
    {
        var map = new ButtonMap();
        ControllerMapSettings.AddAssignment(map, ControllerButton.L1, "doinker", ControllerMapSettings.ModeToggle);

        ControllerMapSettings.SetStyle(map, "doinker", Pneumatic, ControllerMapSettings.StyleTwoButton);
        AssertHas(map, ControllerButton.L1, "doinker", ControllerMapSettings.ModeExtend,
            "the toggle button should have become extend");
        Assert(CountFor(map, "doinker") == 2,
            "splitting a piston onto two buttons should claim a button for retract");
        Assert(FindMode(map, "doinker", ControllerMapSettings.ModeRetract) != null,
            "retract should be on some button");
        Assert(FindMode(map, "doinker", ControllerMapSettings.ModeRetract).button != ControllerButton.L1.ToString(),
            "retract must not land on the button extend already uses");

        ControllerMapSettings.SetStyle(map, "doinker", Pneumatic, ControllerMapSettings.StyleOneButton);
        AssertHas(map, ControllerButton.L1, "doinker", ControllerMapSettings.ModeToggle,
            "extend should fold back into the single toggle");
        Assert(CountFor(map, "doinker") == 1, "the second button should be released");
        return 6;
    }

    // Choosing a style for a mechanism nobody has mapped yet records the choice without silently
    // grabbing buttons for it.
    private static int UnmappedMechanism()
    {
        var map = new ButtonMap();
        ControllerMapSettings.SetStyle(map, "flywheel", Motor, ControllerMapSettings.StyleOneButton);
        Assert(ControllerMapSettings.GetStyle(map, "flywheel", Motor) == ControllerMapSettings.StyleOneButton,
            "an unmapped mechanism should still remember its style");
        Assert(CountFor(map, "flywheel") == 0, "an unmapped mechanism must not be given buttons");

        ControllerMapSettings.RemoveStyle(map, "flywheel");
        Assert(ControllerMapSettings.GetStyle(map, "flywheel", Motor) == ControllerMapSettings.StyleTwoButton,
            "removing the style should fall back to the type default");
        return 3;
    }

    // Maps written before control styles existed have no "styles" key at all. They must load without
    // a null list and read as the style their existing modes imply.
    private static int LegacyMapsStillWork()
    {
        const string legacy = "{\"assignments\":[" +
            "{\"button\":\"R1\",\"mechanismId\":\"intake\",\"mode\":\"forward\"}," +
            "{\"button\":\"L1\",\"mechanismId\":\"doinker\",\"mode\":\"toggle\"}]}";
        PlayerPrefs.SetString(ControllerMapSettings.PrefKey(TestRobotId), legacy);
        PlayerPrefs.Save();

        ButtonMap loaded = ControllerMapSettings.Load(TestRobotId);
        Assert(loaded.styles != null, "a map with no styles key must still load a usable list");
        Assert(loaded.assignments.Count == 2, "legacy assignments should survive the load");
        Assert(ControllerMapSettings.GetStyle(loaded, "intake", Motor) == ControllerMapSettings.StyleTwoButton,
            "a legacy hold-forward motor should read as two-button");
        Assert(ControllerMapSettings.GetStyle(loaded, "doinker", Pneumatic) == ControllerMapSettings.StyleOneButton,
            "a legacy toggle piston should read as one-button");

        // And a style set now survives a save/load round trip.
        ControllerMapSettings.SetStyle(loaded, "intake", Motor, ControllerMapSettings.StyleOneButton);
        ControllerMapSettings.Save(TestRobotId, loaded);
        ButtonMap reloaded = ControllerMapSettings.Load(TestRobotId);
        Assert(ControllerMapSettings.GetStyle(reloaded, "intake", Motor) == ControllerMapSettings.StyleOneButton,
            "a style choice should survive save/load");
        AssertHas(reloaded, ControllerButton.R1, "intake", ControllerMapSettings.ModeToggle,
            "the rewritten mode should survive save/load");
        return 6;
    }

    // Every mode a style can produce needs a caption, or the controller diagram mislabels a button
    // (the old code fell through to "FWD" for anything it didn't recognize).
    private static int EveryModeHasLabels()
    {
        var modes = new List<string>();
        foreach (string type in new[] { Motor, Pneumatic })
        {
            foreach (string style in new[] { ControllerMapSettings.StyleOneButton, ControllerMapSettings.StyleTwoButton })
                modes.AddRange(ControllerMapSettings.ModesFor(type, style));
        }
        var captions = new Dictionary<string, string>();
        foreach (string mode in modes)
        {
            string caption = ControllerMapSettings.ModeCaption(mode);
            Assert(!string.IsNullOrEmpty(caption), $"mode '{mode}' has no caption");
            Assert(!string.IsNullOrEmpty(ControllerMapSettings.ModeLabel(mode)), $"mode '{mode}' has no label");
            if (captions.TryGetValue(caption, out string other) && other != mode)
                throw new InvalidOperationException(
                    $"modes '{other}' and '{mode}' share the caption '{caption}' — two different " +
                    "functions would look identical on the controller diagram");
            captions[caption] = mode;
        }
        return modes.Count;
    }

    // --- helpers ---

    private static void AssertModes(string type, string style, params string[] expected)
    {
        string[] actual = ControllerMapSettings.ModesFor(type, style);
        Assert(actual.Length == expected.Length,
            $"{type}/{style} should expose {expected.Length} function(s), got {actual.Length}");
        for (int i = 0; i < expected.Length; i++)
            Assert(actual[i] == expected[i],
                $"{type}/{style} function {i} should be '{expected[i]}', got '{actual[i]}'");
    }

    private static void AssertHas(ButtonMap map, ControllerButton button, string id, string mode, string why)
        => Assert(ControllerMapSettings.HasAssignment(map, button, id, mode), why);

    private static int CountFor(ButtonMap map, string mechanismId)
    {
        int n = 0;
        foreach (ButtonAssignment a in map.assignments)
            if (a != null && a.mechanismId == mechanismId) n++;
        return n;
    }

    private static ButtonAssignment FindMode(ButtonMap map, string mechanismId, string mode)
    {
        foreach (ButtonAssignment a in map.assignments)
            if (a != null && a.mechanismId == mechanismId && a.mode == mode) return a;
        return null;
    }

    private static void Assert(bool condition, string why)
    {
        if (!condition) throw new InvalidOperationException(why);
    }
}
