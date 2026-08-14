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
// Pure hierarchy arithmetic, so it needs no physics and no robot — which is the point: it stays
// green and fast, and it is checkable in a way "the robot still drives" is not.
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
            return $"Validate Drivetrain Rig: PASSED ({checks} checks).";
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

    private static Transform Child(Transform parent, string name)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }
}
