using System;
using UnityEditor;
using UnityEngine;

// The shared harness and assertion helpers for the editor validators. Every validator follows the
// same contract: a private Run() that returns a one-line PASSED summary or throws
// InvalidOperationException with a human-readable why — and these wrappers turn that into either
// a dialog (menu item) or a nonzero editor exit (-executeMethod batch run).
//
// Each validator keeps its OWN public RunBatchValidate() entry point delegating here, because CI
// invokes them by class+method name.
internal static class ValidationUtil
{
    // Interactive menu wrapper: the summary — or the failure — lands in a dialog, and failures
    // also go to the console with a stack.
    public static void RunInteractive(string title, Func<string> run)
    {
        try
        {
            EditorUtility.DisplayDialog(title, RestoringPhysicsMode(run), "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog(title, "FAILED\n\n" + e.Message, "OK");
            Debug.LogException(e);
        }
    }

    // Batch wrapper for -executeMethod: log the summary and exit 0, or log the failure and exit 1
    // so a headless run fails loudly instead of "passing" by not crashing.
    public static void RunBatch(string title, Func<string> run)
    {
        try
        {
            Debug.Log(RestoringPhysicsMode(run));
        }
        catch (Exception e)
        {
            Debug.LogError(title + " FAILED: " + e.Message);
            EditorApplication.Exit(1);
            return;
        }
        EditorApplication.Exit(0);
    }

    // Every edit-mode validator flips Physics.simulationMode to Script so it can step physics by hand.
    // That is NOT a scope-local switch: the setter writes through to ProjectSettings/DynamicsManager
    // .asset and Unity saves it to disk. A validator that throws — or just forgets its own finally —
    // therefore leaves the PROJECT on Script, and the game then never steps physics at all: nothing
    // settles, no mechanism moves, the robot hangs wherever it spawned, every button looks dead. That
    // shipped once, and no test caught it because every test drives physics by hand and so cannot tell
    // the difference. Hence both halves below:
    //
    //   - the finally forces the mode back, making it impossible for any validator to leak it again.
    //     It runs BEFORE EditorApplication.Exit, which does not run finally blocks.
    //   - the entry-time value IS the asset's value, because nothing has flipped it yet this process.
    //     Anything but FixedUpdate means the project is already broken, so heal it and then fail: a
    //     suite that "passes" against a project the game cannot run in is worth nothing.
    private static string RestoringPhysicsMode(Func<string> run)
    {
        SimulationMode projectMode = Physics.simulationMode;
        try
        {
            if (projectMode != SimulationMode.FixedUpdate)
                throw new InvalidOperationException(
                    $"ProjectSettings/DynamicsManager.asset has m_SimulationMode = {projectMode}, not " +
                    "FixedUpdate, so the game would never step physics: nothing settles, no mechanism " +
                    "moves, the robot hangs where it spawned and every button looks dead. It has been " +
                    "reset to FixedUpdate — keep that change, then run this again.");
            return run();
        }
        finally { Physics.simulationMode = SimulationMode.FixedUpdate; }
    }

    // The project's fixed timestep, and the size of every hand-driven Physics.Simulate step in the
    // suite. Declared once here because it was previously a private const in fifteen files under two
    // names (StepSeconds and Step) — and one of those copies had drifted to 0.02f, so the cascade
    // lift was being measured at half the rate the game actually runs at. A number that appears in
    // fifteen places is a number that is eventually wrong in one of them.
    //
    // PhysicsEngineValidation asserts this still equals Time.fixedDeltaTime, so changing the project
    // setting without changing this fails loudly instead of quietly making every timing in the suite
    // describe a game nobody plays.
    public const float StepSeconds = 0.01f;

    public static void Assert(bool condition, string why)
    {
        if (!condition) throw new InvalidOperationException(why);
    }

    // ACCUMULATING assertions, for validators that would rather report every failure than stop at the
    // first. Assert() above aborts the run, which is right when later checks depend on earlier ones
    // and wrong when they are independent: the first offender is then whichever asset the
    // AssetDatabase happened to return first, so a project-wide problem reads as one asset's problem
    // and the asset actually being complained about may never be reached.
    //
    // Was a private copy in five validators — four byte-identical, and ModelStore's a superset with
    // Refuses. The superset is the one worth having, so it is the one that lives here.
    public class Checks
    {
        public readonly System.Collections.Generic.List<string> Failures =
            new System.Collections.Generic.List<string>();
        public int Count;

        public void That(bool condition, string failureMessage)
        {
            Count++;
            if (!condition) Failures.Add(failureMessage);
        }

        // The mutation half: something that MUST be refused. A guard nobody has watched reject
        // anything is a guard nobody knows is wired up.
        public void Refuses(Action action, string what)
        {
            Count++;
            try { action(); }
            catch (Exception) { return; }
            Failures.Add($"{what} was accepted, but it must be refused");
        }
    }

    // Approximate equality with the actual/expected baked into the message. The NaN test is not
    // paranoia: NaN compares false against everything, so without it a NaN sails through the
    // tolerance check and "passes".
    public static void Near(float actual, float expected, float tolerance, string why)
    {
        if (float.IsNaN(actual) || Mathf.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException($"{why} — expected {expected}, got {actual}");
    }

    public static void AssertThrows(Action action, string what)
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

    // Finite AND non-negative — the shape every physics-tuning number must have before it goes
    // into PhysX (a NaN forceLimit takes the whole articulation down).
    public static void Finite(float value, string what)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
            throw new InvalidOperationException($"{what} must be finite and non-negative, got {value}");
    }

    // Test-fixture cube. The two overloads are deliberately NOT unified: the position-only form
    // leaves the box's rotation inherited from its parent (what the cascade/claw fixtures rely
    // on), while the rotation form pins the world rotation explicitly.
    public static GameObject MakeBox(Transform parent, string name, Vector3 position, Vector3 size)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = position;
        go.transform.localScale = size;
        return go;
    }

    public static GameObject MakeBox(Transform parent, string name, Vector3 position, Quaternion rotation,
        Vector3 size)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(position, rotation);
        go.transform.localScale = size;
        return go;
    }

    // THE BARE-FLOOR RIG: an empty scene with one floor, and nothing else at all.
    //
    // Lived inside TipOverValidation, which meant five other validators reached into a 1350-line test
    // file to get their fixture, and a third copy (RaisedLiftOverlapProbe) re-implemented it rather than
    // do that — dropping the floor-material assertion on the way, so it was silently measuring grip
    // against PhysX's default friction instead of the project's.
    //
    // The bare floor is deliberately NOT the field: the field's tape, walls and settled pieces all
    // perturb a dynamics measurement. It has no tape either, which has cost a diagnosis before — if
    // what you are measuring depends on the surface the robot is standing on, check which rig you are
    // on first.
    public const float RigFloorSize = 400f;      // 40 m: nothing can drive off it in 3 s
    public const float RigDropHeight = 2f;
    public const string RigFloorMaterialPath = "Assets/ZeroBounce.physicMaterial";

    public static ArticulationBody SpawnOnBareFloor(GameObject prefab, out RobotMotorController motor)
    {
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(
            UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
            UnityEditor.SceneManagement.NewSceneMode.Single);

        var floor = new GameObject("Floor");
        floor.transform.position = new Vector3(0f, -0.5f, 0f);
        BoxCollider fc = floor.AddComponent<BoxCollider>();
        fc.size = new Vector3(RigFloorSize, 1f, RigFloorSize);
        fc.sharedMaterial = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(RigFloorMaterialPath);
        Assert(fc.sharedMaterial != null,
            $"the rig's floor material is missing at {RigFloorMaterialPath} — without it the floor " +
            "takes PhysX's default friction and every grip number here is measured against the wrong " +
            "surface");

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(
                prefab, UnityEngine.SceneManagement.SceneManager.GetActiveScene())
            ?? throw new InvalidOperationException($"could not instantiate '{prefab.name}'");
        instance.transform.position = new Vector3(0f, RigDropHeight, 0f);
        Physics.SyncTransforms();

        ArticulationBody root = instance.GetComponent<ArticulationBody>()
            ?? throw new InvalidOperationException($"'{prefab.name}' has no root ArticulationBody");
        motor = root.GetComponent<RobotMotorController>()
            ?? throw new InvalidOperationException($"'{prefab.name}' has no RobotMotorController");
        return root;
    }

    // A big static floor for edit-mode physics fixtures, so a simulated robot has something to
    // land on. No renderer — validation scenes are never looked at.
    public static void CreateGroundPlane()
    {
        GameObject ground = new GameObject("Ground");
        BoxCollider box = ground.AddComponent<BoxCollider>();
        box.size = new Vector3(200f, 1f, 200f);
        ground.transform.position = new Vector3(0f, -0.5f, 0f);
    }
}
