using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Headless checks for the two halves of scoring a piece: getting it OUT of the mechanism, and getting it
// DOWN onto the goal.
//
// THE BUGS THESE EXIST FOR, both reported off the same robot:
//   1. Reversing the scoring mechanism handed the cup back to physics in the same instant it let go, so
//      it turned solid while still between the rollers, found itself interpenetrating plastic, and
//      jammed. It has to FALL first and turn solid a beat later (IntakePull.dropGhostSeconds).
//   2. A piece dropped into a goal was caught at the post top and then crawled down it at the magnet's
//      gentle sideways glide speed — "floating to the goal instead of using gravity to drop it in". The
//      descent is its own speed now (GoalStackMagnet.pullInFallSpeed), easing off only for the landing.
//
// Both are timing, so both are measured rather than asserted structurally, and both are compared against
// the OLD setting on the same fixture — a check that only says "it seats eventually" would pass just as
// happily with the fix reverted.
//
// Usage: Tools > RoboSim > Validate > Validate Scoring Drop, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod ScoringDropValidation.RunBatchValidate
public static class ScoringDropValidation
{

    [MenuItem("Tools/RoboSim/Validate/Validate Scoring Drop", false, 45)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive("Validate Scoring Drop", Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Validate Scoring Drop", Run);

    private static string Run()
    {
        string previousScenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        int checks;
        SimulationMode previousMode = Physics.simulationMode;
        try
        {
            checks = ADroppedPieceFallsBeforeItTurnsSolid();
            checks += TheGhostWindowIsOptional();

            Physics.simulationMode = SimulationMode.Script;
            checks += APieceDropsIntoTheGoalInsteadOfFloatingDown();
        }
        finally
        {
            Physics.simulationMode = previousMode;
            // Don't leave the editor sitting on the throwaway fixture scene.
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
        }

        return $"Validate Scoring Drop: PASSED ({checks} checks) — a piece released by the outtake is " +
               "falling under gravity while it is still ghosted and only turns solid once it is clear, " +
               "and a piece dropped into a goal descends the post several times faster than the old " +
               "glide while still seating and staying seated.";
    }

    // --- 1. Out of the mechanism ---------------------------------------------------------------------

    private static int ADroppedPieceFallsBeforeItTurnsSolid()
    {
        IntakePull outtake = MakeOuttake(out Rigidbody piece);
        ValidationUtil.Assert(outtake.TryCapture(piece), "the fixture starts with the outtake holding a piece");
        ValidationUtil.Assert(piece.isKinematic && !AnyColliderLive(piece),
            "a held piece is kinematic and ghosted — that is the carry model this releases FROM");

        ValidationUtil.Assert(outtake.EjectOneNow(), "reversing must release the piece it is holding");

        // The whole point, and the two halves are opposites: gravity back NOW...
        ValidationUtil.Assert(!piece.isKinematic,
            "the released piece must be DYNAMIC the instant it is let go — it is supposed to be falling " +
            "the whole time it is ghosted, not hanging in the mechanism waiting to become solid");
        ValidationUtil.Assert(IntakePull.CarrierOf(piece) == null,
            "...and nobody's piece any more, or nothing could ever pick it up again");

        // ...solidity NOT back yet. This is the jam: turning solid here puts it inside the rollers.
        ValidationUtil.Assert(!AnyColliderLive(piece),
            "it must still be GHOSTED at the moment of release. Turning solid in the same step is what " +
            "wedged the cup in the mechanism — PhysX finds it interpenetrating plastic and either jams " +
            "it there or pops it out sideways");

        // One step short of the window: still ghosted. Without this, a window of any length — including
        // one that expired immediately — would satisfy the check below.
        StepFor(outtake, outtake.dropGhostSeconds - ValidationUtil.StepSeconds * 2f);
        ValidationUtil.Assert(!AnyColliderLive(piece),
            "it must still be ghosted just BEFORE the window runs out, or 'wait for the window' is not " +
            "what turns it solid and dropGhostSeconds means nothing");

        StepFor(outtake, ValidationUtil.StepSeconds * 3f);
        ValidationUtil.Assert(AllCollidersLive(piece),
            "...and solid once the window has run out. A piece that stayed ghosted would fall straight " +
            "through the goal it was dropped onto");

        Cleanup();
        return 7;
    }

    // The escape hatch, and the proof that the window is doing the delaying rather than something else
    // in the release path.
    private static int TheGhostWindowIsOptional()
    {
        IntakePull outtake = MakeOuttake(out Rigidbody piece);
        outtake.dropGhostSeconds = 0f;
        ValidationUtil.Assert(outtake.TryCapture(piece) && !AnyColliderLive(piece),
            "the fixture starts with a captured, ghosted piece");

        ValidationUtil.Assert(outtake.EjectOneNow(), "the fixture needs the release to happen");
        ValidationUtil.Assert(AllCollidersLive(piece) && !piece.isKinematic,
            "with the window set to 0 the piece must be solid immediately — the old behaviour. If this " +
            "also came back ghosted, the delay above would be coming from somewhere other than " +
            "dropGhostSeconds and setting it to 0 would not turn the delay off");

        Cleanup();
        return 3;
    }

    // --- 2. Down onto the goal -----------------------------------------------------------------------

    private static int APieceDropsIntoTheGoalInsteadOfFloatingDown()
    {
        // Same fixture, same drop, two settings. The OLD behaviour is exactly "the descent is capped at
        // the sideways glide speed", so setting pullInFallSpeed to pullInSpeed reproduces it.
        float fastSteps = StepsToSeat(24f, out float fastFinalError, out float fastDriftDeg, out float fastSpin);
        float slowSteps = StepsToSeat(4f, out _, out float slowDriftDeg, out float slowSpin);

        ValidationUtil.Assert(fastSteps > 0f,
            "the fixture must actually seat the piece at the shipped descent speed, or nothing below " +
            "means anything");
        ValidationUtil.Assert(slowSteps > 0f,
            "TAUTOLOGY GUARD: the OLD slow descent must seat it too. If it never seated, the comparison " +
            "below would be 'fast works, slow is broken' rather than 'fast is faster'");

        ValidationUtil.Assert(slowSteps / fastSteps >= 2.5f,
            $"the drop must be MUCH faster than the old glide, not a little — that is the whole " +
            $"complaint. Descending at 24 took {fastSteps} steps, at the old 4 it took {slowSteps} " +
            $"({slowSteps / fastSteps:0.0}x); anything under 2.5x means the descent is still being " +
            "governed by the sideways speed");

        // Fast must not mean flung: it has to arrive and STAY, not punch through the seat and bounce out.
        ValidationUtil.Assert(fastFinalError < 0.2f,
            $"...and after seating it must still be sitting on its slot 50 steps later (was {fastFinalError} " +
            "off). A descent that only looks fast because the piece blew through the goal is worse than a " +
            "slow one");

        // A seated piece must HOLD ITS ATTITUDE. Measured as actual rotation over 50 steps, deliberately
        // NOT as angularVelocity: the rigid hold is a deadbeat controller, so it commands exactly the
        // spin that would erase the remaining error in ONE step (angleError / dt). At 100 Hz that
        // multiplies the error by a hundred — a piece sitting 0.2 degrees off its held pose reads as
        // ~0.35 rad/s of "spin" while not visibly moving at all. Reading the velocity therefore says very
        // little about whether the piece is steady, which is exactly the trap FieldFeatureSmokeTest's
        // marginal 0.3 rad/s assertion falls into. Drift is the honest measurement.
        ValidationUtil.Assert(fastDriftDeg < 2f,
            $"a seated piece must hold the attitude it was dropped in — it turned {fastDriftDeg:0.##}° " +
            "over 50 steps after seating");
        ValidationUtil.Assert(slowDriftDeg < 2f,
            $"...at the old descent speed too, so the check above is about seating rather than about " +
            $"which speed was used (old drifted {slowDriftDeg:0.##}°)");

        Debug.Log($"ScoringDropValidation: descent {slowSteps / fastSteps:0.0}x faster than the old glide " +
                  $"({fastSteps} steps vs {slowSteps}), settling {fastFinalError:0.###}u off the slot. " +
                  $"Seated attitude drift over 50 steps — fast {fastDriftDeg:0.###}° (reported " +
                  $"angularVelocity {fastSpin:0.##} rad/s), old {slowDriftDeg:0.###}° (reported " +
                  $"{slowSpin:0.##} rad/s). A large reported velocity with near-zero drift is the " +
                  "deadbeat hold's correction term, not motion.");

        Cleanup();
        return 6;
    }

    // Drives a real GoalStackMagnet over a real dropped piece and returns how many steps it took to reach
    // its slot, or 0 if it never did. `finalError` is how far off the slot it is 50 steps AFTER seating,
    // which is what separates "landed" from "went through". `driftDeg` is how far it actually TURNED over
    // those 50 steps and `finalSpin` the angular velocity reported at the end — reported separately
    // because the deadbeat hold makes those two numbers mean very different things.
    private static float StepsToSeat(float fallSpeed, out float finalError, out float driftDeg,
        out float finalSpin)
    {
        Cleanup();
        GoalStackMagnet magnet = MakeGoal(fallSpeed, out Transform anchor);
        Rigidbody piece = MakePiece("Cup_Drop", anchor.position + Vector3.up * 3f);
        piece.isKinematic = false;

        Vector3 slot = anchor.position + anchor.up * (CupRestHeight + magnet.stackClearance);
        finalError = float.PositiveInfinity;
        driftDeg = float.PositiveInfinity;
        finalSpin = float.PositiveInfinity;

        // The project runs with Auto Sync Transforms OFF, so the fixture's poses have to be pushed into
        // PhysX before the magnet's first overlap scan goes looking for a piece to capture.
        Physics.SyncTransforms();

        const int MaxSteps = 600;   // 6 seconds — twice the magnet's own pull-in timeout
        int seatedAt = 0;
        Quaternion seatedRotation = Quaternion.identity;
        for (int step = 1; step <= MaxSteps; step++)
        {
            magnet.StepMagnet(ValidationUtil.StepSeconds);
            Physics.Simulate(ValidationUtil.StepSeconds);

            if (seatedAt == 0 && (piece.worldCenterOfMass - slot).magnitude < 0.15f)
            {
                seatedAt = step;
                seatedRotation = piece.rotation;
            }
            if (seatedAt != 0 && step >= seatedAt + 50)
            {
                finalError = (piece.worldCenterOfMass - slot).magnitude;
                driftDeg = Quaternion.Angle(seatedRotation, piece.rotation);
                finalSpin = piece.angularVelocity.magnitude;
                break;
            }
        }
        return seatedAt;
    }

    // --- Fixtures ------------------------------------------------------------------------------------

    private const float CupRestHeight = 0.43f;   // the shipped Cup profile, baked from the piece meshes

    private static readonly List<GameObject> spawned = new List<GameObject>();

    private static GameObject Track(GameObject go) { spawned.Add(go); return go; }

    private static void Cleanup()
    {
        foreach (GameObject go in spawned) if (go != null) Object.DestroyImmediate(go);
        spawned.Clear();
    }

    // A scoring mechanism set up the way the real one is: reverse LETS GO rather than launching.
    private static IntakePull MakeOuttake(out Rigidbody piece)
    {
        Cleanup();
        GameObject mouth = Track(new GameObject("Intake2Mouth"));
        BoxCollider box = mouth.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = new Vector3(4f, 4f, 4f);

        IntakePull outtake = mouth.AddComponent<IntakePull>();
        outtake.logEvents = false;
        outtake.stabilizeHoldPoint = false;   // no articulation here; the anchor rule has its own validator
        outtake.reverseDropsInPlace = true;
        outtake.maxHeld = 2;

        piece = MakePiece("Cup_Outtake", Vector3.zero);
        return outtake;
    }

    // A goal with its stack anchor at the origin, aimed up. Left at the component's own defaults apart
    // from the descent speed under test and the piece profile, which has no default.
    private static GoalStackMagnet MakeGoal(float fallSpeed, out Transform anchor)
    {
        GameObject goal = Track(new GameObject("Goal"));
        GoalStackMagnet magnet = goal.AddComponent<GoalStackMagnet>();
        magnet.pullInFallSpeed = fallSpeed;
        magnet.pieceProfiles = new List<GoalStackMagnet.PieceProfile>
        {
            new GoalStackMagnet.PieceProfile { namePrefix = "Cup", restHeight = CupRestHeight, stackAdvance = 0.86f },
        };

        anchor = new GameObject("GoalStackAnchor").transform;
        anchor.SetParent(goal.transform, false);
        magnet.stackAnchor = anchor;
        return magnet;
    }

    // A game piece: named so GamePiece.IsPiece sees it, with a solid collider and a real Rigidbody.
    private static Rigidbody MakePiece(string name, Vector3 at)
    {
        GameObject go = Track(new GameObject(name));
        go.transform.position = at;
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.size = new Vector3(0.5f, 0.86f, 0.5f);
        Rigidbody rb = go.AddComponent<Rigidbody>();
        rb.useGravity = true;
        return rb;
    }

    private static void StepFor(IntakePull intake, float seconds)
    {
        for (float t = 0f; t < seconds; t += ValidationUtil.StepSeconds) intake.StepEjected(ValidationUtil.StepSeconds);
    }

    private static bool AnyColliderLive(Rigidbody rb)
    {
        foreach (Collider c in rb.GetComponentsInChildren<Collider>(true)) if (c.enabled) return true;
        return false;
    }

    private static bool AllCollidersLive(Rigidbody rb)
    {
        foreach (Collider c in rb.GetComponentsInChildren<Collider>(true)) if (!c.enabled) return false;
        return true;
    }
}
