using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Headless checks for the shipped-default button layer in ControllerMapSettings.
//
// The rule it guards is small but easy to get subtly wrong, and every wrong version is silent:
// seed too eagerly and a player's carefully built layout is wiped on launch; seed too timidly and
// an improved default never reaches anyone; use a boolean "seeded" flag and a player who
// deliberately cleared every button gets them all back on the next launch. None of those throw —
// they just make someone's controller wrong.
//
// Snapshots and restores the PlayerPrefs keys it touches (the RobotVisibilityValidation pattern)
// rather than deleting them, because this writes TWO keys per robot and the ids are synthetic but
// a collision would still cost someone their real layout.
//
// Usage: Tools > RoboSim > Testing > Validate Default Bindings, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod DefaultBindingsValidation.RunBatchValidate
public static class DefaultBindingsValidation
{
    private const string TestRobotId = "__defaultbindings_validation__";
    private const string Motor = RobotMechanisms.TypeMotor;

    [MenuItem("Tools/RoboSim/Testing/Validate Default Bindings", false, 17)]
    private static void RunInteractive()
    {
        try
        {
            EditorUtility.DisplayDialog("Validate Default Bindings", Run(), "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Validate Default Bindings", "FAILED\n\n" + e.Message, "OK");
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
            Debug.LogError("Validate Default Bindings FAILED: " + e.Message);
            EditorApplication.Exit(1);
            return;
        }
        EditorApplication.Exit(0);
    }

    private static string Run()
    {
        string mapKey = ControllerMapSettings.PrefKey(TestRobotId);
        string seedKey = ControllerMapSettings.SeedPrefKey(TestRobotId);
        bool hadMap = PlayerPrefs.HasKey(mapKey), hadSeed = PlayerPrefs.HasKey(seedKey);
        string savedMap = PlayerPrefs.GetString(mapKey, string.Empty);
        string savedSeed = PlayerPrefs.GetString(seedKey, string.Empty);

        int checks = 0;
        try
        {
            checks += CloneIsDeep();
            checks += FreshDeviceGetsTheDefault();
            checks += CustomisedLayoutSurvives();
            checks += ClearedLayoutStaysCleared();
            checks += UntouchedLayoutTakesAnUpdate();
            checks += NoDefaultIsANoOp();
            checks += ResetAlwaysOverwrites();
            checks += NullsAreNoOps();
        }
        finally
        {
            PlayerPrefs.DeleteKey(mapKey);
            PlayerPrefs.DeleteKey(seedKey);
            if (hadMap) PlayerPrefs.SetString(mapKey, savedMap);
            if (hadSeed) PlayerPrefs.SetString(seedKey, savedSeed);
            PlayerPrefs.Save();
        }
        return $"Validate Default Bindings: PASSED ({checks} checks).";
    }

    // The catalog's ButtonMap is a live asset object. If Clone were shallow, the config screen
    // mutating a loaded map would reach back into the asset and rewrite the shipped default —
    // in the editor, invisibly, and it would ship that way.
    private static int CloneIsDeep()
    {
        ButtonMap source = MapWith(ControllerButton.R1, "intake", ControllerMapSettings.ModeForward);
        ControllerMapSettings.SetStyle(source, "intake", Motor, ControllerMapSettings.StyleOneButton);

        ButtonMap copy = ControllerMapSettings.Clone(source);
        Assert(!ReferenceEquals(copy, source), "Clone must return a new map");
        Assert(!ReferenceEquals(copy.assignments, source.assignments), "Clone must copy the assignment list");
        Assert(!ReferenceEquals(copy.styles, source.styles), "Clone must copy the style list");
        Assert(copy.assignments.Count == source.assignments.Count, "Clone must keep every assignment");
        Assert(copy.styles.Count == source.styles.Count, "Clone must keep every style");

        copy.assignments[0].mechanismId = "tampered";
        copy.assignments.Add(new ButtonAssignment { button = "L1", mechanismId = "x", mode = "forward" });
        ControllerMapSettings.RemoveStyle(copy, "intake");
        Assert(source.assignments[0].mechanismId == "intake",
            "mutating a clone's assignment must not reach the original");
        Assert(source.assignments.Count == 1, "adding to a clone must not reach the original");
        Assert(source.styles.Count == 1, "removing a clone's style must not reach the original");

        Assert(ControllerMapSettings.Clone(null) != null, "Clone(null) must return an empty map, not null");
        return 9;
    }

    // A fresh install must come up bound. This is the whole point of the feature.
    private static int FreshDeviceGetsTheDefault()
    {
        Wipe();
        RobotModelCatalog.Entry entry = EntryWith(
            (ControllerButton.R1, "intake", ControllerMapSettings.ModeForward),
            (ControllerButton.R2, "intake", ControllerMapSettings.ModeReverse));

        Assert(ControllerMapSettings.SeedDefault(entry), "a device with no layout must be seeded");
        ButtonMap loaded = ControllerMapSettings.Load(TestRobotId);
        Assert(loaded.assignments.Count == 2, "the seeded layout should have both buttons");
        AssertHas(loaded, ControllerButton.R1, "intake", ControllerMapSettings.ModeForward);
        AssertHas(loaded, ControllerButton.R2, "intake", ControllerMapSettings.ModeReverse);

        Assert(!ControllerMapSettings.SeedDefault(entry),
            "seeding twice must be a no-op — it runs on every launch");
        return 5;
    }

    // The failure that would matter most: never overwrite a layout someone built.
    private static int CustomisedLayoutSurvives()
    {
        Wipe();
        RobotModelCatalog.Entry entry = EntryWith(
            (ControllerButton.R1, "intake", ControllerMapSettings.ModeForward));
        ControllerMapSettings.SeedDefault(entry);

        // The player moves it to a different button.
        ButtonMap mine = ControllerMapSettings.Load(TestRobotId);
        ControllerMapSettings.ClearAssignment(mine, ControllerButton.R1);
        ControllerMapSettings.AddAssignment(mine, ControllerButton.L2, "intake", ControllerMapSettings.ModeForward);
        ControllerMapSettings.Save(TestRobotId, mine);

        Assert(!ControllerMapSettings.SeedDefault(entry), "a customised layout must not be re-seeded");
        ButtonMap after = ControllerMapSettings.Load(TestRobotId);
        AssertHas(after, ControllerButton.L2, "intake", ControllerMapSettings.ModeForward);
        Assert(ControllerMapSettings.Find(after, ControllerButton.R1) == null,
            "the player's move must survive seeding");
        return 3;
    }

    // A player who deliberately unbinds everything has an EMPTY map, which is exactly what a naive
    // "is there a map?" check reads as "never seeded" — and refills on the next launch, forever.
    private static int ClearedLayoutStaysCleared()
    {
        Wipe();
        RobotModelCatalog.Entry entry = EntryWith(
            (ControllerButton.R1, "intake", ControllerMapSettings.ModeForward));
        ControllerMapSettings.SeedDefault(entry);

        ControllerMapSettings.Save(TestRobotId, new ButtonMap());

        Assert(!ControllerMapSettings.SeedDefault(entry),
            "a deliberately cleared layout must stay cleared, not be refilled on the next launch");
        Assert(ControllerMapSettings.Load(TestRobotId).assignments.Count == 0,
            "the cleared layout must still be empty");
        return 2;
    }

    // The other side of the coin: a player who never touched the defaults SHOULD pick up an
    // improved layout shipped in a later app version.
    private static int UntouchedLayoutTakesAnUpdate()
    {
        Wipe();
        RobotModelCatalog.Entry entry = EntryWith(
            (ControllerButton.R1, "intake", ControllerMapSettings.ModeForward));
        ControllerMapSettings.SeedDefault(entry);

        // A later version ships a better default for the same robot.
        entry.defaultButtonMap = MapWith(ControllerButton.L1, "intake", ControllerMapSettings.ModeForward);

        Assert(ControllerMapSettings.SeedDefault(entry),
            "an untouched device should take a newly shipped default");
        ButtonMap after = ControllerMapSettings.Load(TestRobotId);
        AssertHas(after, ControllerButton.L1, "intake", ControllerMapSettings.ModeForward);
        Assert(ControllerMapSettings.Find(after, ControllerButton.R1) == null,
            "the superseded default should be gone");
        Assert(!ControllerMapSettings.SeedDefault(entry), "the update must itself only land once");
        return 4;
    }

    // Robots that ship no default (every robot, before this feature) must be left completely alone —
    // including not creating a stray pref that later reads as "seeded".
    private static int NoDefaultIsANoOp()
    {
        Wipe();
        var entry = new RobotModelCatalog.Entry { id = TestRobotId, displayName = "test" };
        Assert(!entry.HasDefaultButtonMap, "a new entry must not claim to have a default");
        Assert(!ControllerMapSettings.SeedDefault(entry), "an entry with no default must not seed");
        Assert(!PlayerPrefs.HasKey(ControllerMapSettings.PrefKey(TestRobotId)),
            "seeding nothing must not create a map pref");
        Assert(!PlayerPrefs.HasKey(ControllerMapSettings.SeedPrefKey(TestRobotId)),
            "seeding nothing must not create a seed marker");

        // An entry whose default was set and then emptied is the same case.
        entry.defaultButtonMap = new ButtonMap();
        Assert(!entry.HasDefaultButtonMap, "an empty default must not count as a default");
        Assert(!ControllerMapSettings.ResetToDefault(entry), "there is nothing to reset to");
        return 6;
    }

    // Reset is an explicit player action, so unlike seeding it always overwrites.
    private static int ResetAlwaysOverwrites()
    {
        Wipe();
        RobotModelCatalog.Entry entry = EntryWith(
            (ControllerButton.R1, "intake", ControllerMapSettings.ModeForward));

        ButtonMap mine = MapWith(ControllerButton.Y, "intake", ControllerMapSettings.ModeReverse);
        ControllerMapSettings.Save(TestRobotId, mine);

        Assert(ControllerMapSettings.ResetToDefault(entry), "reset must report that it wrote");
        ButtonMap after = ControllerMapSettings.Load(TestRobotId);
        AssertHas(after, ControllerButton.R1, "intake", ControllerMapSettings.ModeForward);
        Assert(ControllerMapSettings.Find(after, ControllerButton.Y) == null,
            "reset must discard the player's layout");

        // And after a reset the device counts as untouched again, so future updates still land.
        Assert(!ControllerMapSettings.SeedDefault(entry),
            "the layout right after a reset already matches, so seeding is a no-op");
        entry.defaultButtonMap = MapWith(ControllerButton.B, "intake", ControllerMapSettings.ModeForward);
        Assert(ControllerMapSettings.SeedDefault(entry),
            "a reset device must still be treated as untouched by a later update");
        return 5;
    }

    // Called at launch with whatever the scene happens to hold, so nulls must not throw.
    private static int NullsAreNoOps()
    {
        Assert(!ControllerMapSettings.SeedDefault(null), "SeedDefault(null) must be a no-op");
        Assert(!ControllerMapSettings.ResetToDefault(null), "ResetToDefault(null) must be a no-op");
        Assert(ControllerMapSettings.SeedDefaults(null) == 0, "SeedDefaults(null) must be a no-op");

        var catalog = ScriptableObject.CreateInstance<RobotModelCatalog>();
        try
        {
            catalog.models = new List<RobotModelCatalog.Entry> { null, new RobotModelCatalog.Entry() };
            Assert(ControllerMapSettings.SeedDefaults(catalog) == 0,
                "a catalog of null / empty entries must not throw or seed");
            catalog.models = null;
            Assert(ControllerMapSettings.SeedDefaults(catalog) == 0,
                "a catalog with a null model list must not throw");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(catalog);
        }
        return 5;
    }

    // --- helpers ---

    private static void Wipe()
    {
        PlayerPrefs.DeleteKey(ControllerMapSettings.PrefKey(TestRobotId));
        PlayerPrefs.DeleteKey(ControllerMapSettings.SeedPrefKey(TestRobotId));
        PlayerPrefs.Save();
    }

    private static ButtonMap MapWith(ControllerButton button, string mechanismId, string mode)
    {
        var map = new ButtonMap();
        ControllerMapSettings.AddAssignment(map, button, mechanismId, mode);
        return map;
    }

    private static RobotModelCatalog.Entry EntryWith(
        params (ControllerButton button, string mechanismId, string mode)[] assignments)
    {
        var map = new ButtonMap();
        foreach ((ControllerButton button, string mechanismId, string mode) in assignments)
            ControllerMapSettings.AddAssignment(map, button, mechanismId, mode);
        return new RobotModelCatalog.Entry
        {
            id = TestRobotId,
            displayName = "test",
            defaultButtonMap = map,
        };
    }

    private static void AssertHas(ButtonMap map, ControllerButton button, string id, string mode)
        => Assert(ControllerMapSettings.HasAssignment(map, button, id, mode),
            $"{button} should drive {id} ({mode})");

    private static void Assert(bool condition, string why)
    {
        if (!condition) throw new InvalidOperationException(why);
    }
}
