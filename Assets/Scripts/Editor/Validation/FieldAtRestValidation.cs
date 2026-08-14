using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

// A match must start on a field that is already at rest.
//
// This is the check that would have saved a wrong diagnosis. The reported symptom was "the tape is
// still slightly a problem, but it only lasts about two seconds and then it goes away", and it was
// not the tape at all: 77 loose pieces were authored fractionally out of place — every alliance pin
// 2.7 mm inside the ground box and 27.8 degrees off its resting angle — so PhysX spent the first
// 1.8 s of every match settling them. Nothing moved far (worst centre of mass: 15 mm) but a step
// cost 5.56 ms instead of 0.20 while it happened, and a frame that expensive feels like rough
// ground under the robot. The cause and the symptom were nowhere near each other.
//
// Two assertions, and they are not equal partners:
//   1. Nothing starts GROSSLY inside anything. A guard for a piece authored halfway through a wall,
//      which would be jammed rather than moving and so invisible to check 2. It does not catch the
//      defect above — see MaxStaticPenetration for why depth cannot.
//   2. Nothing MOVES once the clock starts. This is the real check. It measures the symptom itself
//      rather than a proxy for it, and it catches both the misplaced piece and the one balanced on
//      an edge that penetrates nothing and still falls over the moment the match begins.
//
// Measure the CENTRE OF MASS, never transform.position: these piece roots sit at their group's
// origin with the geometry in children, so a pin rotating in place swings its root through a metre.
// That is how the first pass of this investigation got "986 mm" out of a 15 mm move.
public static class FieldAtRestValidation
{
    [MenuItem("Tools/RoboSim/Validate/Validate Field At Rest", false, 41)]
    public static void Validate() => ValidationUtil.RunInteractive("Field At Rest", Run);

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Field At Rest", Run);

    private const int Steps = 200;             // 2.0 s — the window the symptom lives in

    // A piece may creep this far as the solver polishes its contacts. Chosen from the measured
    // split, which is not close: after settling the worst piece moves under 0.5 mm, and before
    // settling sixteen pins moved 15 mm each.
    private const float DriftTolerance = 0.03f;    // 3 mm

    // How deep a piece may sit inside STATIC geometry — the ground box, a goal wall, the perimeter.
    //
    // This is a GROSS-ERROR guard, and the honest note is that it does NOT catch the defect this
    // file was written for. Measured: the sixteen misplaced alliance pins sat 2.7 mm into the ground
    // box — and a correctly settled pin wedged in a goal's inner pocket also rests at 2.7 mm, held
    // there by the pocket walls. No depth separates them, because the bad pose was at the depth
    // PhysX would have settled it to anyway. Tightening this to 2 mm to catch the pins condemned two
    // healthy pins with it. The DRIFT check below is what detects this class, and it does so
    // directly: a piece in the wrong place moves, and moving is the thing that costs the frame.
    // So this is set where a settled field passes with room (worst real overlap 2.7 mm), and it is
    // here only for a piece authored grossly inside a wall — which would be jammed, not moving, and
    // therefore invisible to the drift check.
    private const float MaxStaticPenetration = 0.05f;   // 5 mm

    // Piece against piece is a different measurement and needs a looser bound. Two dynamic bodies
    // in a stable resting stack share their contact error between them and legitimately compress
    // into each other: the settled field has pins sitting 3.6 mm into cups and is provably asleep.
    // Judging that by the static number would condemn a field that is behaving correctly. This is
    // still tight enough to catch a piece spawned halfway through another one.
    private const float MaxPiecePenetration = 0.08f;    // 8 mm

    private static string Run()
    {
        var report = new List<string>();
        foreach (string scene in new[] { RoboSimPaths.MainScene, RoboSimPaths.LiteScene })
        {
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            var bodies = new List<Rigidbody>(
                Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude));
            bodies.RemoveAll(rb => rb == null || rb.isKinematic);

            ValidationUtil.Assert(bodies.Count > 0,
                $"{scene} has no dynamic pieces at all. Either the field lost its game pieces or " +
                "the wrong scene is open — and this check would otherwise pass by finding nothing.");

            // 1. Nothing starts inside anything.
            var inside = new List<string>();
            foreach (Rigidbody rb in bodies)
            {
                float worst = 0f; string worstWith = null; bool worstStatic = false;
                foreach (Collider mine in rb.GetComponentsInChildren<Collider>(true))
                {
                    if (mine == null || mine.isTrigger) continue;
                    foreach (Collider other in Physics.OverlapBox(
                                 mine.bounds.center, mine.bounds.extents, Quaternion.identity,
                                 ~0, QueryTriggerInteraction.Ignore))
                    {
                        if (other == null || other.isTrigger || other.attachedRigidbody == rb) continue;
                        if (!Physics.ComputePenetration(
                                mine, mine.transform.position, mine.transform.rotation,
                                other, other.transform.position, other.transform.rotation,
                                out _, out float depth)) continue;

                        Rigidbody theirs = other.attachedRigidbody;
                        bool isStatic = theirs == null || theirs.isKinematic;
                        float allowed = isStatic ? MaxStaticPenetration : MaxPiecePenetration;
                        // Rank by how far past its OWN budget each overlap is, so a 3 mm bite into
                        // the floor outranks a 4 mm rest against another piece.
                        float over = depth - allowed;
                        if (over > worst - (worstStatic ? MaxStaticPenetration : MaxPiecePenetration)
                            || worstWith == null)
                        { worst = depth; worstWith = other.name; worstStatic = isStatic; }
                    }
                }
                float budget = worstStatic ? MaxStaticPenetration : MaxPiecePenetration;
                if (worst > budget)
                    inside.Add($"{rb.name} starts {worst * 100f:0.0} mm inside " +
                               $"{(worstStatic ? "STATIC" : "piece")} '{worstWith}' " +
                               $"(limit {budget * 100f:0} mm)");
            }

            ValidationUtil.Assert(inside.Count == 0,
                $"{inside.Count} piece(s) in {scene} start INSIDE other geometry — deeper than " +
                $"{MaxStaticPenetration * 100f:0} mm into something static, or " +
                $"{MaxPiecePenetration * 100f:0} mm into another piece. PhysX has to push them out " +
                "on frame one, which is work the player pays for at the exact moment they start " +
                "driving. Run Tools > RoboSim > Field & Pieces > Settle Pieces.\n    " +
                string.Join("\n    ", inside.GetRange(0, Mathf.Min(inside.Count, 10))) +
                (inside.Count > 10 ? $"\n    ...and {inside.Count - 10} more" : ""));

            // 2. Nothing moves once the clock starts.
            var startCom = new List<Vector3>();
            foreach (Rigidbody rb in bodies) startCom.Add(rb.worldCenterOfMass);

            var sw = new Stopwatch();
            SimulationMode previous = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            try
            {
                sw.Start();
                for (int i = 0; i < Steps; i++) Physics.Simulate(ValidationUtil.StepSeconds);
                sw.Stop();
            }
            finally { Physics.simulationMode = previous; }

            var drifted = new List<string>();
            float worstDrift = 0f;
            for (int i = 0; i < bodies.Count; i++)
            {
                if (bodies[i] == null) continue;
                float d = Vector3.Distance(bodies[i].worldCenterOfMass, startCom[i]);
                worstDrift = Mathf.Max(worstDrift, d);
                if (d > DriftTolerance) drifted.Add($"{bodies[i].name} moved {d * 100f:0.0} mm");
            }

            ValidationUtil.Assert(drifted.Count == 0,
                $"{drifted.Count} of {bodies.Count} piece(s) in {scene} MOVE in the first " +
                $"{Steps * ValidationUtil.StepSeconds:0.0} s, worst {worstDrift * 100f:0.0} mm. The match is being played " +
                "on a field that is still falling into place: every one of those bodies stays awake " +
                "and the physics step costs many times what it does once they sleep, which the " +
                "driver feels as the ground being rough rather than as pieces moving. Run Tools > " +
                "RoboSim > Field & Pieces > Settle Pieces.\n    " +
                string.Join("\n    ", drifted.GetRange(0, Mathf.Min(drifted.Count, 10))) +
                (drifted.Count > 10 ? $"\n    ...and {drifted.Count - 10} more" : ""));

            report.Add($"  {scene}: {bodies.Count} piece(s), none starting inside anything, " +
                       $"worst drift {worstDrift * 100f:0.00} mm over {Steps * ValidationUtil.StepSeconds:0.0} s " +
                       $"({sw.Elapsed.TotalMilliseconds / Steps:0.00} ms/step).");
        }

        return "Field Starts At Rest: PASSED.\n" + string.Join("\n", report);
    }
}
