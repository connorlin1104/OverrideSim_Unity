using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// IS THE PHYSICS ENGINE ON AT ALL? Run this FIRST — before any validator that simulates.
//
// Every other physics test assumes the engine works and asks a question about a ROBOT. When the
// engine itself does nothing, all of them still "run", and every one of them reports a confident,
// specific, WRONG diagnosis about the robot: RobotPhysicsValidation said the drive wasn't wired to the
// wheels, TipOverValidation said a chassis part was hanging below the wheels and carrying the
// robot's weight. Both are things that send you into a prefab looking for a defect that isn't there.
// The truth was that no rigid body existed at all.
//
// TWO WAYS IT HAS ACTUALLY HAPPENED, both a single line in ProjectSettings/DynamicsManager.asset:
//   m_SimulationMode      — flipped to Script (2) by a validator that set it and never restored it.
//                           Nothing steps physics unless someone calls Physics.Simulate by hand, so
//                           the GAME freezes while every validator, which does call it, stays green.
//   m_CurrentBackendId    — repointed at a physics backend that is not loaded. PhysX then creates no
//                           bodies whatsoever: a Rigidbody reports position (0,0,0), Collider.bounds
//                           degenerates, Physics.Simulate is a no-op, and NOTHING moves anywhere.
//
// So this asserts the one thing everything else takes for granted, on the simplest object there is:
// a sphere, in mid-air, with gravity. If it does not fall, stop — nothing else measured in this
// session means anything.
public static class PhysicsEngineValidation
{
    private const int Steps = 50;          // 0.5 s
    private const float DropFrom = 100f;   // high enough that nothing can be resting on anything
    private const float MinFall = 1f;      // at -98.1 u/s^2 a real fall is ~12 u; 1 u is unmissable

    [MenuItem("Tools/RoboSim/Validate/Validate Physics Engine", false, 0)]
    private static void RunInteractive()
        => ValidationUtil.RunInteractive("Physics Engine Is Running", Run);

    public static void RunBatchValidate()
        => ValidationUtil.RunBatch("Physics Engine Is Running", Run);

    public static string Run()
    {
        SimulationMode previousMode = Physics.simulationMode;
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var probe = new GameObject("PhysicsLivenessProbe");
            probe.transform.position = new Vector3(0f, DropFrom, 0f);
            probe.AddComponent<SphereCollider>();
            Rigidbody body = probe.AddComponent<Rigidbody>();
            body.useGravity = true;
            Physics.SyncTransforms();

            Physics.simulationMode = SimulationMode.Script;
            for (int i = 0; i < Steps; i++) Physics.Simulate(ValidationUtil.StepSeconds);

            float fell = DropFrom - probe.transform.position.y;

            // Reported together on purpose: which of the two is wrong tells you which line to fix,
            // and a body that never got a physics representation reads position (0,0,0) rather than
            // the transform it was placed at.
            ValidationUtil.Assert(Mathf.Abs(Physics.gravity.y) > 1e-3f,
                $"gravity is {Physics.gravity} — nothing will ever fall. Check " +
                "ProjectSettings/DynamicsManager.asset m_Gravity (this project runs at -98.1, " +
                "because 1 unit = 0.1 m).");

            // Every hand-driven Physics.Simulate in the suite steps by ValidationUtil.StepSeconds, so
            // that number has to BE the project's fixed timestep — otherwise every duration, speed and
            // settle count measured here describes a game running at a rate nobody plays at. This was
            // worth adding because it had already happened: one validator's private copy of the
            // constant said 0.02f, and it measured a cascade lift at half the real rate for as long
            // as nothing compared the two.
            ValidationUtil.Near(ValidationUtil.StepSeconds, Time.fixedDeltaTime, 1e-4f,
                $"ValidationUtil.StepSeconds is {ValidationUtil.StepSeconds} but the project's fixed " +
                $"timestep is {Time.fixedDeltaTime} (ProjectSettings/TimeManager.asset). The whole " +
                "suite simulates at the wrong rate until these agree");

            ValidationUtil.Assert(fell >= MinFall,
                $"a sphere dropped in mid-air with gravity fell {fell:0.00} units in " +
                $"{Steps * ValidationUtil.StepSeconds:0.0} s — the physics engine is not simulating, and its " +
                $"Rigidbody reports position {body.position} against a transform at " +
                $"{probe.transform.position}. A body at (0,0,0) that was never placed there means " +
                "PhysX created no body at all. Check ProjectSettings/DynamicsManager.asset: " +
                "m_CurrentBackendId must name a backend that is actually loaded (this project's is " +
                "4072204805), and m_SimulationMode must be 0 (FixedUpdate) for the game to step " +
                "itself. Do not trust any other physics validator until this passes — they will all " +
                "blame the robot.");

            return $"Physics Engine Is Running: PASSED (3 checks). A sphere fell {fell:0.0} units in " +
                   $"{Steps * ValidationUtil.StepSeconds:0.0} s under gravity {Physics.gravity.y:0.0}, " +
                   $"stepping at the project's {Time.fixedDeltaTime:0.###} s timestep.";
        }
        finally { Physics.simulationMode = previousMode; }
    }
}
