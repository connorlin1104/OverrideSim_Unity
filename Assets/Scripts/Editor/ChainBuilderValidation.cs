using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Headless check of the Build Chain core (ChainBuilder.Apply) against a synthetic robot built in a
// scratch scene — no real robot prefab is touched.
//
// The thing this exists to catch is the AXIS SIGN. Which way an axle's long dimension points in
// world depends on how that instance happened to be placed in CAD, and the joint core bakes that
// sign into the joint's positive rotation direction — so a chain whose axles disagree would have a
// subset of its sprockets spinning backwards. That is invisible until you're in Play watching the
// robot, so station 2 here is deliberately mounted 180 degrees flipped.
//
// Usage: Tools > RoboSim > Testing > Validate Build Chain, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod ChainBuilderValidation.RunBatchValidate
public static class ChainBuilderValidation
{
    private const string TestRobotId = "__chain_validation__";
    private const string CatalogPath = "Assets/Settings/RobotModelCatalog.asset";

    [MenuItem("Tools/RoboSim/Testing/Validate Build Chain", false, 11)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        try
        {
            EditorUtility.DisplayDialog("Validate Build Chain", Run(), "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog("Validate Build Chain", "FAILED\n\n" + e.Message, "OK");
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
            Debug.LogError("Validate Build Chain FAILED: " + e.Message);
            EditorApplication.Exit(1);
            return;
        }
        EditorApplication.Exit(0);
    }

    private static string Run()
    {
        bool hadEntry = HasCatalogEntry(TestRobotId);
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            return BuildAndAssert();
        }
        finally
        {
            PlayerPrefs.DeleteKey(ControllerMapSettings.PrefKey(TestRobotId));
            PlayerPrefs.Save();
            if (!hadEntry) RemoveCatalogEntry(TestRobotId);
            // The scratch scene is never saved, so the synthetic robot dies with it.
        }
    }

    private static string BuildAndAssert()
    {
        // --- A synthetic robot: fixed chassis, three spinning groups, three axles alongside -------
        GameObject root = new GameObject("ChainTestBot");
        RobotMechanisms registry = root.AddComponent<RobotMechanisms>();
        registry.robotId = TestRobotId;
        ArticulationBody chassis = root.AddComponent<ArticulationBody>();
        chassis.immovable = true;
        MakeBox(root.transform, "ChassisMesh", Vector3.zero, Quaternion.identity, new Vector3(6f, 1f, 6f));

        // Station 2's axle is mounted 180 degrees about Y, so its long axis points the OPPOSITE way
        // in world from the other two. Un-canonicalized, its joint would run backwards.
        var stations = new System.Collections.Generic.List<ChainBuilder.Station>();
        Quaternion[] axleRotations =
        {
            Quaternion.identity,
            Quaternion.Euler(0f, 180f, 0f),
            Quaternion.identity,
        };
        for (int i = 0; i < 3; i++)
        {
            float x = i * 4f;
            GameObject spins = new GameObject($"Sprocket{i + 1}");
            spins.transform.SetParent(root.transform, false);
            spins.transform.position = new Vector3(x, 2f, 0f);
            // A disc-ish sprocket, so the no-axle fallback would guess something too.
            MakeBox(spins.transform, $"SprocketMesh{i + 1}", new Vector3(x, 2f, 0f), Quaternion.identity,
                new Vector3(1.5f, 1.5f, 0.3f));

            // Axles start OUTSIDE their station, to prove they get folded into the spinning link.
            GameObject axle = MakeBox(root.transform, $"HS Axle {i + 1}", new Vector3(x, 2f, 0f),
                axleRotations[i], new Vector3(0.2f, 0.2f, 5f));

            stations.Add(new ChainBuilder.Station { spins = spins, axle = axle, ratio = 1f });
        }

        // Sanity: the raw axle reading really does disagree in sign before canonicalization, or this
        // test proves nothing.
        ChainBuilder.TryAxleWorldAxis(stations[0].axle, out Vector3 raw0, out _);
        ChainBuilder.TryAxleWorldAxis(stations[1].axle, out Vector3 raw1, out _);
        Assert(Vector3.Dot(raw0, raw1) < -0.9f,
            "the fixture is wrong: station 2's axle should read as pointing the opposite way");

        // --- Build ------------------------------------------------------------------------------
        string report = ChainBuilder.Apply(stations, new ChainBuilder.Options
        {
            mechanismName = "Test Intake",
            reverseDirection = false,
            autoAssignButton = true,
        }, useUndo: false);

        // --- Powered station ---------------------------------------------------------------------
        GameObject driver = stations[0].spins;
        Assert(driver.name == "Test Intake", "the powered station should have been renamed to the mechanism name");
        ArticulationBody driverBody = driver.GetComponent<ArticulationBody>();
        Assert(driverBody != null, "the powered station should have become a joint");
        Assert(driverBody.jointType == ArticulationJointType.RevoluteJoint, "a chain station must be revolute");
        Assert(driverBody.twistLock == ArticulationDofLock.FreeMotion,
            "a chain station must free-spin (Continuous), not be limited");
        Assert(driver.GetComponent<MotorActuator>() != null, "the powered station needs a motor");
        Assert(driver.GetComponent<JointCoupler>() == null, "the powered station must not be coupled to anything");

        RobotMechanisms.Mechanism mech = registry.Find("test-intake");
        Assert(mech != null, "the chain should be registered as a mechanism (id 'test-intake')");
        Assert(mech.displayName == "Test Intake", "the mechanism should carry the given display name");
        Assert(mech.type == RobotMechanisms.TypeMotor, "a chain is a motor mechanism");

        // --- Chained stations --------------------------------------------------------------------
        for (int i = 1; i < stations.Count; i++)
        {
            GameObject follower = stations[i].spins;
            ArticulationBody body = follower.GetComponent<ArticulationBody>();
            Assert(body != null, $"station {i + 1} should have become a joint");
            Assert(body.twistLock == ArticulationDofLock.FreeMotion, $"station {i + 1} must free-spin");
            Assert(follower.GetComponent<MotorActuator>() == null,
                $"station {i + 1} is chained, so it must not also be button-driven");
            JointCoupler coupler = follower.GetComponent<JointCoupler>();
            Assert(coupler != null, $"station {i + 1} should be coupled to the powered station");
            Assert(coupler.driver == driverBody, $"station {i + 1} should follow the powered station");
            Assert(coupler.follower == body, $"station {i + 1}'s coupler should drive its own body");
            Assert(coupler.mode == JointCoupler.CoupleMode.Velocity,
                $"station {i + 1} should match SPEED (a chain), not angle");
        }

        // --- The point of the exercise: every station turns the same way ------------------------
        Vector3 reference = WorldSpinAxis(driverBody);
        for (int i = 1; i < stations.Count; i++)
        {
            Vector3 axis = WorldSpinAxis(stations[i].spins.GetComponent<ArticulationBody>());
            float dot = Vector3.Dot(axis, reference);
            Assert(dot > 0.9f,
                $"station {i + 1} spins about {axis} against the powered station's {reference} " +
                $"(dot {dot:F2}) — the chain would run backwards there");
        }

        // --- Housekeeping the build is responsible for -------------------------------------------
        foreach (ChainBuilder.Station s in stations)
        {
            Assert(s.spins.GetComponent<IgnoreRobotSelfCollision>() != null,
                $"'{s.spins.name}' needs IgnoreRobotSelfCollision — sibling links collide and would " +
                "shove the robot around");
            Assert(s.axle.transform.IsChildOf(s.spins.transform),
                $"'{s.axle.name}' should have been folded into the link it spins with");
        }

        ButtonMap map = ControllerMapSettings.Load(TestRobotId);
        Assert(ControllerMapSettings.HasAssignment(map, ControllerButton.R1, "test-intake",
            ControllerMapSettings.ModeForward), "a motor chain should auto-assign hold-forward to R1");
        Assert(ControllerMapSettings.HasAssignment(map, ControllerButton.R2, "test-intake",
            ControllerMapSettings.ModeReverse), "a motor chain should auto-assign hold-reverse to R2");

        // --- Rejections that must not silently succeed -------------------------------------------
        AssertThrows(() => ChainBuilder.Apply(
            new[] { stations[0] }, default, false), "a single station isn't a chain");
        AssertThrows(() => ChainBuilder.Apply(
            new[] { stations[0], stations[0] }, default, false), "the same part listed twice");
        // Nested inside ANOTHER STATION in the same list — caught by the pairwise check.
        AssertThrows(() => ChainBuilder.Apply(
            new[]
            {
                stations[0],
                new ChainBuilder.Station { spins = FirstChild(stations[0].spins), ratio = 1f },
            }, default, false), "a station nested inside another station in the list");

        // Nested inside a link that ISN'T in the list — only the shared-parent check sees this one,
        // and it's the case that would silently spin at double speed.
        AssertThrows(() => ChainBuilder.Apply(
            new[]
            {
                stations[0],
                new ChainBuilder.Station { spins = FirstChild(stations[1].spins), ratio = 1f },
            }, default, false), "a station buried inside a link that isn't a station");

        return "Validate Build Chain: PASSED — 3 stations rigged, the 180-degree-flipped station was " +
               "corrected to spin with the others, axles folded in, self-collision guarded, buttons " +
               $"assigned.\n\nTool report was:\n{report}";
    }

    // The joint's twist axis in world. ConfigureJointLink stores it as
    // anchorRotation = FromToRotation(right, -axisLocal), so the local axis reads back negated.
    private static Vector3 WorldSpinAxis(ArticulationBody body)
    {
        Vector3 local = -(body.anchorRotation * Vector3.right);
        return (body.transform.rotation * local).normalized;
    }

    private static GameObject FirstChild(GameObject go) => go.transform.GetChild(0).gameObject;

    private static GameObject MakeBox(Transform parent, string name, Vector3 position, Quaternion rotation,
        Vector3 size)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(position, rotation);
        go.transform.localScale = size;
        return go;
    }

    private static bool HasCatalogEntry(string id)
    {
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
        return catalog != null && catalog.models != null &&
               catalog.models.Exists(e => e != null && e.id == id);
    }

    // The build registers the synthetic robot in the shared catalog asset; drop it again so a
    // validation run leaves no trace in a committed asset.
    private static void RemoveCatalogEntry(string id)
    {
        RobotModelCatalog catalog = AssetDatabase.LoadAssetAtPath<RobotModelCatalog>(CatalogPath);
        if (catalog == null || catalog.models == null) return;
        if (catalog.models.RemoveAll(e => e != null && e.id == id) == 0) return;
        EditorUtility.SetDirty(catalog);
        AssetDatabase.SaveAssets();
    }

    private static void Assert(bool condition, string why)
    {
        if (!condition) throw new InvalidOperationException(why);
    }

    private static void AssertThrows(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            return; // rejected, as it should be
        }
        throw new InvalidOperationException($"'{what}' was accepted, but it should have been rejected");
    }
}
