using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Headless checks for WHERE the drivetrain rig puts the links it builds.
//
// The rig used to hoist every new wheel link to the robot root. That littered the top of a
// 1200-node hierarchy with six WheelLink_* empties and tore each wheel's geometry out of the
// structure the model was drawn with, so the links are now created in place — beside the wheel,
// under whatever folder the wheel already lived in.
//
// That is a hierarchy change to the one system with the most delicate tuning in the project, and it
// has exactly two ways to go wrong, both silent:
//
//   TOPOLOGY. An articulation link joints to the nearest ArticulationBody ABOVE it. Nest a wheel
//   link under a folder that happens to sit inside another link and the wheel joints to THAT — a
//   drivetrain that drives the lift around. Nothing in the editor shows it; you find out in Play.
//
//   SCALE. These CAD imports carry a 1/2.54 inch conversion on hundreds of nodes, so a link that
//   simply inherits its new parent's chain lands at 0.39 world scale instead of 1, and every anchor
//   coordinate measured in its frame silently means something else.
//
// Pure hierarchy arithmetic, so it needs no physics — which is the point: it stays green and fast,
// and it is checkable in a way "the robot still drives" is not.
//
// The file has two halves. The first builds synthetic fixtures and checks the PLACEMENT arithmetic
// above. The second sweeps the robot prefabs that actually shipped and checks the rig is COMPLETE —
// added after Darwinbot shipped with six wheels and five links (2 on the left rail, 3 on the right).
// Nothing caught it: the rig logs its link count and moves on, the robot looks right in every editor
// view, and the missing wheel's only symptoms are "the turning went weird" and a robot that rides at
// an angle. Both halves are hierarchy arithmetic; only the second needs real robots, and it needs
// them because the defect is per-robot and lives in serialized data, not in the code.
//
// Usage: Tools > RoboSim > Validate > Validate Drivetrain Rig, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod DrivetrainRigValidation.RunBatchValidate
public static class DrivetrainRigValidation
{
    [MenuItem("Tools/RoboSim/Validate/Validate Drivetrain Rig", false, 11)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive("Validate Drivetrain Rig", Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Validate Drivetrain Rig", Run);

    private static string Run()
    {
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            int checks = WheelLinksLandBesideTheirWheel();
            checks += LinkKeepsTheWrappersScale();
            checks += EveryShippedRobotIsFullyRigged(out string rigReport);
            return $"Validate Drivetrain Rig: PASSED ({checks} checks).\n{rigReport}";
        }
        finally
        {
            // The scratch scene is never saved, so the fixtures die with it.
        }
    }

    // --- Where the link goes ----------------------------------------------------------------------

    private static int WheelLinksLandBesideTheirWheel()
    {
        Transform wrapper = new GameObject("Robot").transform;
        wrapper.gameObject.AddComponent<ArticulationBody>();   // the chassis, the only body allowed above a wheel

        // The normal case, and the whole point of the change: a wheel buried in CAD folders gets its
        // link as a SIBLING, so the folders above it survive being rigged.
        Transform folder = Child(wrapper, "Drivetrain LS");
        Transform subFolder = Child(folder, "Wheel Assembly");
        Transform wheel = Child(subFolder, "omni 2.75");
        ValidationUtil.Assert(RigDrivetrainArticulation.WheelMount(wheel, wrapper) == subFolder,
            "a buried wheel's link must be created beside it, not hoisted to the robot root");

        // A wheel already at the top gets what it always got.
        Transform flat = Child(wrapper, "FlatWheel");
        ValidationUtil.Assert(RigDrivetrainArticulation.WheelMount(flat, wrapper) == wrapper,
            "a wheel directly under the wrapper must still mount on the wrapper");

        // THE GUARD. A wheel drawn inside another mechanism's link cannot mount there, or it joints
        // to that mechanism instead of the chassis. The mount has to fall back to the wrapper.
        Transform liftStage = Child(wrapper, "LiftStage");
        liftStage.gameObject.AddComponent<ArticulationBody>();
        Transform onLift = Child(Child(liftStage, "StageFolder"), "wheel");
        ValidationUtil.Assert(RigDrivetrainArticulation.WheelMount(onLift, wrapper) == wrapper,
            "a wheel inside another articulation link must fall back to the wrapper — mounting it " +
            "there would joint the drivetrain to that link instead of the chassis");

        // ...and the same one step further down, because the guard has to walk the WHOLE chain up to
        // the wrapper, not just check the immediate parent.
        Transform deepOnLift = Child(Child(Child(liftStage, "A"), "B"), "wheel");
        ValidationUtil.Assert(RigDrivetrainArticulation.WheelMount(deepOnLift, wrapper) == wrapper,
            "the intervening-body guard must walk the whole chain, not just the wheel's parent");

        // Degenerate inputs still produce a usable mount rather than a null parent.
        Transform loose = new GameObject("NotOnTheRobot").transform;
        ValidationUtil.Assert(RigDrivetrainArticulation.WheelMount(loose, wrapper) == wrapper,
            "a part outside the robot must mount on the wrapper, not scatter links into the scene");
        ValidationUtil.Assert(RigDrivetrainArticulation.WheelMount(null, wrapper) == wrapper,
            "a null wheel node must fall back to the wrapper");
        ValidationUtil.Assert(RigDrivetrainArticulation.WheelMount(wrapper, wrapper) == wrapper,
            "the wrapper itself has no parent inside the robot, so it mounts on itself");
        return 7;
    }

    // --- The link's own frame ---------------------------------------------------------------------

    // Nesting must change WHERE the link is and nothing else, so its world scale has to come out at
    // the wrapper's however many inch-conversion folders it now sits under.
    private static int LinkKeepsTheWrappersScale()
    {
        Transform wrapper = new GameObject("ScaleRobot").transform;
        Transform inches = Child(wrapper, "InchFolder");
        inches.localScale = Vector3.one * 0.39370078f;   // the conversion these FBX imports carry
        Transform nested = Child(inches, "Deeper");
        nested.localScale = Vector3.one * 2f;

        Transform link = Child(nested, "WheelLink_LS0");
        link.localScale = RigDrivetrainArticulation.InverseScale(nested, wrapper.lossyScale);

        Vector3 world = link.lossyScale;
        ValidationUtil.Near(world.x, 1f, 1e-3f, "the link's world X scale must match the wrapper's");
        ValidationUtil.Near(world.y, 1f, 1e-3f, "the link's world Y scale must match the wrapper's");
        ValidationUtil.Near(world.z, 1f, 1e-3f, "the link's world Z scale must match the wrapper's");

        // The tautology guard: prove the compensation is doing something. A plain localScale of one
        // under this chain would land at 0.787, so a no-op InverseScale would fail the checks above.
        ValidationUtil.Assert(Mathf.Abs(link.localScale.x - 1f) > 0.1f,
            $"this fixture must actually need compensating (localScale came out {link.localScale.x}), " +
            "or the checks above would pass for an InverseScale that returned its input");

        // A zero-scale folder must not produce an infinite link — the Nz guard the builders share.
        Transform flattened = Child(wrapper, "Flattened");
        flattened.localScale = new Vector3(0f, 1f, 1f);
        Vector3 safe = RigDrivetrainArticulation.InverseScale(flattened, Vector3.one);
        ValidationUtil.Finite(safe.x, "a zero-scale parent must not give the link an infinite scale");
        return 5;
    }

    // --- The robots that actually shipped -----------------------------------------------------------

    // Four properties, per robot, that a complete drivetrain rig has and a partial one does not.
    // Every one of them was FALSE on Darwinbot and true on the other four, which is what makes them
    // worth asserting rather than restating: they discriminate on the real fleet, today.
    //
    // Read straight off the prefab assets — no LoadPrefabContents, because nothing here writes and
    // the arrays and the hierarchy both resolve fine on the asset.
    private static int EveryShippedRobotIsFullyRigged(out string report)
    {
        var lines = new System.Text.StringBuilder();
        int checks = 0;
        int robots = 0;

        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            RobotMotorController motor = prefab.GetComponent<RobotMotorController>();
            if (motor == null) continue; // not a drivable robot
            robots++;

            int left = CountWheels(motor.leftWheels);
            int right = CountWheels(motor.rightWheels);

            // 1) THE RAILS MATCH. Per-wheel stall torque is the traction budget over the wheel COUNT
            //    (DrivetrainTuning.Compute), so an uneven rig makes one side permanently weaker: the
            //    robot pulls to the short side driving straight and turns at two different rates.
            ValidationUtil.Assert(left == right,
                $"'{prefab.name}' has {left} left / {right} right wheel links — per-wheel torque is " +
                "the traction budget divided by the wheel count, so uneven rails make one side " +
                "weaker than the other at every stick position. Run Rig Missing Drive Wheels.");

            // 2) NO WHEEL LEFT BEHIND. The rails can also match while a PAIR is missing, so this asks
            //    the robot's own geometry: is there another copy of a rigged wheel sitting outside
            //    every link?
            System.Collections.Generic.List<Transform> unrigged =
                RobotPartClassifier.FindUnriggedWheels(prefab);
            ValidationUtil.Assert(unrigged.Count == 0,
                $"'{prefab.name}' has {unrigged.Count} wheel(s) that are another instance of a rigged " +
                $"drive wheel but sit outside every wheel link{FirstName(unrigged)} — they are drawn, " +
                "they are in the right place, and they do nothing. Run Rig Missing Drive Wheels.");

            // 3) EVERY LINK IS WIRED. A link the controller does not hold is a wheel with a joint and
            //    no drive: it free-spins while the rest of the robot pushes it along.
            int links = 0;
            foreach (Transform t in prefab.GetComponentsInChildren<Transform>(true))
                if (t.name.StartsWith(RobotPartClassifier.WheelLinkNamePrefix)) links++;
            ValidationUtil.Assert(links == left + right,
                $"'{prefab.name}' has {links} {RobotPartClassifier.WheelLinkNamePrefix}* objects but " +
                $"{left + right} wired into RobotMotorController — an unwired link is an undriven wheel.");

            // 4) EVERY DRIVEN WHEEL CAN REACH THE FLOOR. This is the one the whole file is here for.
            //    Generate Part Colliders finds wheels with the same classifier the rig does, so a
            //    wheel one missed the other missed too: rig it anyway and you get full drive torque
            //    against no contact at all. Nothing about a mid-air wheel looks wrong in the editor.
            int uncollided = 0;
            string firstBald = null;
            foreach (ArticulationBody wheel in AllWheels(motor))
            {
                if (wheel.GetComponentInChildren<Collider>(true) != null) continue;
                uncollided++;
                if (firstBald == null) firstBald = wheel.name;
            }
            ValidationUtil.Assert(uncollided == 0,
                $"'{prefab.name}' has {uncollided} driven wheel(s) with no collider (e.g. " +
                $"'{firstBald}') — a driven wheel that cannot touch the ground spins in mid-air at " +
                "full torque while the robot rides on the others.");

            checks += 4;
            lines.AppendLine($"  {prefab.name}: {left} left / {right} right, {links} link(s), " +
                             "every wheel collided and rigged.");
        }

        // A sweep that found no robots is not a pass. The Robots folder has moved before.
        ValidationUtil.Assert(robots > 0,
            $"no robot prefabs with a RobotMotorController under {RoboSimPaths.RobotsFolder} — this " +
            "check passed over nothing, which is not the same as passing.");
        checks++;

        report = $"  Drivetrain rigs, {robots} robot(s):\n{lines.ToString().TrimEnd()}";
        return checks;
    }

    private static int CountWheels(ArticulationBody[] wheels)
    {
        if (wheels == null) return 0;
        int n = 0;
        foreach (ArticulationBody w in wheels) if (w != null) n++;
        return n;
    }

    private static System.Collections.Generic.IEnumerable<ArticulationBody> AllWheels(RobotMotorController motor)
    {
        if (motor.leftWheels != null)
            foreach (ArticulationBody w in motor.leftWheels) if (w != null) yield return w;
        if (motor.rightWheels != null)
            foreach (ArticulationBody w in motor.rightWheels) if (w != null) yield return w;
    }

    // Names the first offender, because "1 wheel is unrigged" and "'2.75 Omni on Shaved 48T
    // Assembly:3' is unrigged" are different amounts of help at 2am.
    private static string FirstName(System.Collections.Generic.List<Transform> parts)
        => parts.Count == 0 ? string.Empty : $" (e.g. '{parts[0].name}')";

    private static Transform Child(Transform parent, string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }
}
