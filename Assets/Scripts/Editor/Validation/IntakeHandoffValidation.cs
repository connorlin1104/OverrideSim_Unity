using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Headless checks for handing a game piece from one intake to another — the floor intake gathers, the
// scoring mechanism takes what it gathered and lifts it.
//
// THE BUG THIS EXISTS FOR. Capturing a piece makes it kinematic and switches its colliders OFF. That is
// what lets it glide through the CAD, but it also means the piece stops existing as far as detection is
// concerned: it trips no trigger and no overlap query can find it. So a second intake pointed straight at
// the first one's stack found nothing there, and the bot could not load its own scoring mechanism. Carried
// pieces are now registered (IntakePull.CarrierOf), which keeps them KNOWN while they are non-physical,
// and the taker measures its mouth box against the piece's centre by hand instead of asking PhysX.
//
// The two things a handoff must not lose are the reason it isn't just "capture it again": a ghosted piece
// reports its centre of mass at its PIVOT (9-15u from the mesh on these field pieces) and reports itself
// as ALREADY KINEMATIC. Re-measuring either would teleport the piece or leave it hanging in the air
// forever, so both travel with it — and both are checked below.
//
// No physics stepping and no robot: this drives the real capture/handoff/release calls on a synthetic
// pair of intakes, so it stays fast and covers exactly what was broken.
//
// Usage: Tools > RoboSim > Validation > Validate Intake Handoff, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod IntakeHandoffValidation.RunBatchValidate
public static class IntakeHandoffValidation
{
    [MenuItem("Tools/RoboSim/Validation/Validate Intake Handoff", false, 16)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive("Validate Intake Handoff", Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Validate Intake Handoff", Run);

    private static string Run()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        int checks = ACarriedPieceIsInvisibleButStillKnown();
        checks += TheScoringIntakeTakesWhatTheFloorIntakeGathered();
        checks += AHandedOverPieceStillFallsWhenItIsDropped();
        checks += WhatTheHandoffRefuses();
        return $"Validate Intake Handoff: PASSED ({checks} checks) — a carried piece stays known while it " +
               "is non-physical, the second intake takes it with the centre of mass and the pre-capture " +
               "kinematic flag intact, and the mouth box, the capacity and the ping-pong guard all hold.";
    }

    // --- 1. The diagnosis, as a check ---------------------------------------------------------------

    private static int ACarriedPieceIsInvisibleButStillKnown()
    {
        MakePair(out IntakePull floor, out IntakePull scoring);
        Rigidbody piece = MakePiece("Cup_Known", new Vector3(0f, 3f, 0f));

        // Tautology guard: the fixture's pivot really is off-centre, like every field piece, so the
        // centre-of-mass checks further down cannot pass by both sides being zero.
        ValidationUtil.Assert(piece.centerOfMass.magnitude > 1f,
            "this fixture must have an OFF-CENTRE centre of mass — that is the property every field " +
            "piece has, and the thing a handoff can lose");

        ValidationUtil.Assert(floor.TryCapture(piece), "the floor intake must be able to grab a loose piece");
        ValidationUtil.Assert(piece.isKinematic, "a captured piece is kinematic — that is the carry model");
        ValidationUtil.Assert(!AnyColliderLive(piece),
            "a carried piece has NO live colliders, which is exactly why the second intake could not " +
            "see it: no trigger fires and no overlap query returns it");
        ValidationUtil.Assert(IntakePull.CarrierOf(piece) == floor,
            "...so the registry has to know who is carrying it. That is what keeps a carried piece a " +
            "PIECE while it is non-physical");
        ValidationUtil.Assert(floor.IsCarrying(piece) && !scoring.IsCarrying(piece),
            "one intake carries it, the other does not");

        Cleanup();
        return 6;
    }

    // --- 2. The handoff -----------------------------------------------------------------------------

    private static int TheScoringIntakeTakesWhatTheFloorIntakeGathered()
    {
        MakePair(out IntakePull floor, out IntakePull scoring);
        Rigidbody piece = MakePiece("Cup_Handoff", new Vector3(0f, 3f, 0f));
        ValidationUtil.Assert(floor.TryCapture(piece), "the fixture starts with the floor intake holding it");

        // A piece somebody else is carrying must not be grabbable through the ordinary path — going
        // through the handoff is what makes the other intake let go. This also makes the checks below
        // meaningful: the only thing that can move this piece is TakeHandoffs.
        ValidationUtil.Assert(!scoring.TryCapture(piece),
            "an ordinary capture must REFUSE a piece another intake is carrying, or both would carry it " +
            "and fight over its position every step");

        // Simulate what PhysX does to a ghosted piece: with no live colliders the automatic centre of
        // mass collapses back to the pivot. Forcing it here means the inheritance check below fails if
        // the taker re-measures — in edit mode as well as at play.
        //
        // The body has to be made dynamic for the write, and that is not a detail: PhysX SILENTLY IGNORES
        // a centre-of-mass write on a kinematic body, and the piece is kinematic because the floor intake
        // is carrying it. Written the obvious way this fixture changed nothing at all, so "inherit" and
        // "re-measure" produced the same number and a mutant that re-measured passed the check below.
        Vector3 centreBefore = floor.CarriedCenter(piece);
        piece.isKinematic = false;
        piece.centerOfMass = Vector3.zero;
        piece.isKinematic = true;
        ValidationUtil.Assert(piece.centerOfMass.sqrMagnitude < 1e-6f,
            "TAUTOLOGY GUARD: the fixture must really be able to move this piece's centre of mass onto " +
            "its pivot, the way ghosting does at play. If it cannot, inheriting and re-measuring read the " +
            "same value and the check below proves nothing");

        scoring.TakeHandoffs();

        ValidationUtil.Assert(IntakePull.CarrierOf(piece) == scoring,
            "with the scoring intake's mouth over the piece, holding its button must take the piece");
        ValidationUtil.Assert(scoring.IsCarrying(piece) && !floor.IsCarrying(piece),
            "the first intake has to LET GO in the same act — two carriers would tear the piece between " +
            "two slots");
        ValidationUtil.Near((scoring.CarriedCenter(piece) - centreBefore).magnitude, 0f, 1e-3f,
            "the taker must INHERIT the pre-ghost centre of mass. Re-reading it gets the pivot instead, " +
            "9-15u from the mesh on these pieces, and the piece jumps that far on handoff");
        ValidationUtil.Assert(piece.isKinematic,
            "the piece stays kinematic across the handoff — a step as a loose body is a step where it " +
            "can drop or bounce out of the mechanism");
        ValidationUtil.Assert(!AnyColliderLive(piece), "...and stays ghosted, for the same reason");

        Cleanup();
        return 8;
    }

    // --- 3. Release ---------------------------------------------------------------------------------

    private static int AHandedOverPieceStillFallsWhenItIsDropped()
    {
        MakePair(out IntakePull floor, out IntakePull scoring);
        Rigidbody piece = MakePiece("Cup_Drop", new Vector3(0f, 3f, 0f));
        ValidationUtil.Assert(floor.TryCapture(piece) && piece.isKinematic,
            "the fixture starts with a captured, kinematic piece");
        scoring.TakeHandoffs();
        ValidationUtil.Assert(scoring.IsCarrying(piece), "the fixture needs the handoff to have happened");

        scoring.ScoreDrop();   // the same release path a reverse-drop uses, for the whole stack at once

        ValidationUtil.Assert(!piece.isKinematic,
            "dropping a handed-over piece must give it back to GRAVITY. If the taker had re-read " +
            "isKinematic while the piece was already held it would have recorded 'was kinematic', and " +
            "the piece would hang in mid-air for the rest of the match");
        ValidationUtil.Assert(AllCollidersLive(piece),
            "...and its colliders come back, or it falls through the field");
        ValidationUtil.Assert(IntakePull.CarrierOf(piece) == null && !scoring.IsCarrying(piece),
            "a dropped piece is nobody's — leaving it in the registry would make it un-grabbable forever");

        Cleanup();
        return 5;
    }

    // --- 4. The limits ------------------------------------------------------------------------------

    private static int WhatTheHandoffRefuses()
    {
        MakePair(out IntakePull floor, out IntakePull scoring);
        Rigidbody piece = MakePiece("Cup_Reach", new Vector3(0f, 3f, 0f));
        ValidationUtil.Assert(floor.TryCapture(piece), "the fixture starts with the floor intake holding it");

        // OUT OF REACH. The scoring mouth is a box, not a magnet: a piece carried somewhere else is not
        // handed over just because both intakes are on the same robot. JUST outside on purpose — the
        // mouth's half-extent here is 0.5 local (1.0 world, under the arm's 2x scale) and this sits at
        // 0.6 local. Parking it far away instead would pass with the box maths wrong by any factor.
        MovePieceCentre(piece, floor, new Vector3(0f, 3f, 1.2f));
        scoring.TakeHandoffs();
        ValidationUtil.Assert(floor.IsCarrying(piece),
            "a piece carried OUTSIDE the mouth box must not be taken — the box is the zone the player " +
            "aims with");

        // ...and back in, which proves the check above wasn't a fixture that could never hand anything
        // over — and that the box maths survives the mouth's parent scale.
        MovePieceCentre(piece, floor, new Vector3(0f, 3f, 0f));
        scoring.TakeHandoffs();
        ValidationUtil.Assert(scoring.IsCarrying(piece),
            "the same piece moved back inside the mouth MUST be taken, or the refusal above proves nothing");

        // NO PING-PONG. Both buttons held, two overlapping mouths: without the cooldown the piece would
        // change hands every physics step.
        floor.TakeHandoffs();
        ValidationUtil.Assert(scoring.IsCarrying(piece) && !floor.IsCarrying(piece),
            "a piece just handed over must not be handed straight back — two overlapping mouths would " +
            "trade it every step for as long as both buttons are held");

        // TAUTOLOGY GUARD for the line above. Same two mouths, same spot, but a piece the scoring intake
        // picked up itself rather than took, so no cooldown is running: the floor intake DOES take this
        // one. That is what proves the refusal above came from the cooldown and not from a floor mouth
        // that never covered the stack point at all.
        Rigidbody fresh = MakePiece("Cup_Cooldown", new Vector3(0f, 3f, 0f));
        ValidationUtil.Assert(scoring.TryCapture(fresh), "the guard needs the scoring intake to hold a second piece");
        floor.TakeHandoffs();
        ValidationUtil.Assert(floor.IsCarrying(fresh),
            "with no cooldown running the floor mouth must be able to take this piece back — otherwise " +
            "the ping-pong check above is refusing nothing and would pass with the cooldown deleted");

        // THE OPT-OUT. Same geometry, same overlap, switch off.
        Rigidbody second = MakePiece("Cup_OptOut", new Vector3(0f, 3f, 0f));
        ValidationUtil.Assert(floor.TryCapture(second), "the fixture needs a second carried piece");
        scoring.takeFromOtherIntakes = false;
        scoring.TakeHandoffs();
        ValidationUtil.Assert(floor.IsCarrying(second),
            "Take From Other Intakes OFF must stop the handoff cold — it is the switch for two intakes " +
            "that are meant to work independently");
        scoring.takeFromOtherIntakes = true;

        // FULL IS FULL. The scoring intake is already carrying the first piece from this case.
        scoring.maxHeld = 1;
        scoring.TakeHandoffs();
        ValidationUtil.Assert(floor.IsCarrying(second) && !scoring.IsCarrying(second),
            "a full intake must not take another piece — capacity is what stops a stack overflowing its " +
            "slots");

        Cleanup();
        return 9;
    }

    // --- Fixtures -----------------------------------------------------------------------------------

    private static readonly List<GameObject> spawned = new List<GameObject>();

    // Two intakes whose zones overlap the way the real pair does: the floor intake holds its stack at
    // (0,3,0), and the scoring intake's mouth is a box centred on that spot. The scoring mouth sits under
    // a SCALED parent on purpose — the mouth box is measured in local units, and the robots this runs on
    // are full of non-unit scales, so the conversion has to be exercised.
    //
    // The floor mouth is TALL so that it too covers the stack point. Both mouths have to reach the same
    // place or the ping-pong check below is refused by geometry rather than by the cooldown it means to
    // test — which is exactly how a mutant with the cooldown deleted survived the first run.
    private static void MakePair(out IntakePull floor, out IntakePull scoring)
    {
        Transform robot = Track(new GameObject("HandoffRobot")).transform;

        floor = MakeIntake(robot, "IntakeMouth", new Vector3(0f, 1f, 0f), new Vector3(2f, 6f, 2f),
            new Vector3(0f, 3f, 0f));

        Transform arm = new GameObject("ScoringArm").transform;
        arm.SetParent(robot, false);
        arm.localScale = new Vector3(2f, 2f, 2f);
        scoring = MakeIntake(arm, "Intake2Mouth", new Vector3(0f, 3f, 0f), Vector3.one,
            new Vector3(0f, 9f, 0f));
        scoring.reverseDropsInPlace = true;
    }

    private static IntakePull MakeIntake(Transform parent, string name, Vector3 mouthAt, Vector3 boxSize,
        Vector3 holdAt)
    {
        GameObject mouth = new GameObject(name);
        mouth.transform.SetParent(parent, false);
        mouth.transform.position = mouthAt;
        BoxCollider box = mouth.AddComponent<BoxCollider>();
        box.isTrigger = true;
        box.size = boxSize;

        IntakePull pull = mouth.AddComponent<IntakePull>();
        pull.logEvents = false;
        pull.maxHeld = 2;
        pull.slotSpacing = 1.5f;
        pull.stabilizeHoldPoint = false;   // no articulation here; the anchor rule has its own validator

        GameObject hold = new GameObject(name + "Hold");
        hold.transform.SetParent(parent, false);
        hold.transform.position = holdAt;
        pull.holdPoint = hold.transform;
        pull.slotAnchors = new[] { hold.transform };
        return pull;
    }

    // A piece shaped like the field's: the Rigidbody's pivot is at the CAD origin and the mesh/collider
    // is pushed 9 units away, so the pivot and the visible piece are nowhere near each other. `centreAt`
    // places the piece's CENTRE, which is what the intake aims and what the mouth box tests.
    private static Rigidbody MakePiece(string name, Vector3 centreAt)
    {
        GameObject go = Track(new GameObject(name));
        Rigidbody rb = go.AddComponent<Rigidbody>();
        rb.useGravity = false;

        GameObject shell = GameObject.CreatePrimitive(PrimitiveType.Cube);
        shell.name = name + "Mesh";
        shell.transform.SetParent(go.transform, false);
        shell.transform.localPosition = new Vector3(9f, 0f, 0f);

        // Explicit, not measured: edit-mode mass properties are not worth depending on, and the trap
        // being tested is what a GHOSTED piece reports when asked for its centre of mass.
        rb.centerOfMass = new Vector3(9f, 0f, 0f);
        // Both, and the body's own position is the one that matters: everything in IntakePull measures
        // from rb.position, and in EDIT MODE a Rigidbody does not pick up a transform assignment — there
        // is no simulation step to sync them, so a body left at the origin reads as 9 units from where
        // the piece visibly is and every mouth-box test lands somewhere else entirely.
        go.transform.position = centreAt - rb.centerOfMass;
        rb.position = centreAt - rb.centerOfMass;
        return rb;
    }

    // Put a carried piece's centre at a world point, the way its carrier's glide would.
    private static void MovePieceCentre(Rigidbody rb, IntakePull carrier, Vector3 centreAt)
    {
        Vector3 offset = carrier.CarriedCenter(rb) - rb.position;
        Vector3 pivot = centreAt - offset;
        rb.transform.position = pivot;   // edit mode: no simulation step to sync transform <-> body
        rb.position = pivot;
    }

    private static bool AnyColliderLive(Rigidbody rb)
    {
        foreach (Collider c in rb.GetComponentsInChildren<Collider>()) if (c.enabled) return true;
        return false;
    }

    private static bool AllCollidersLive(Rigidbody rb)
    {
        foreach (Collider c in rb.GetComponentsInChildren<Collider>()) if (!c.enabled) return false;
        return true;
    }

    private static GameObject Track(GameObject go)
    {
        spawned.Add(go);
        return go;
    }

    // Cases run in one scene, and the carrier registry is STATIC and global — a piece left in an earlier
    // case's stack at the same coordinates would be a candidate for a later case's handoff and would make
    // the capacity/opt-out checks lie. So each case drops what it holds and deletes its fixture.
    private static void Cleanup()
    {
        foreach (GameObject go in spawned)
        {
            if (go == null) continue;
            foreach (IntakePull pull in go.GetComponentsInChildren<IntakePull>(true)) pull.ScoreDrop();
        }
        foreach (GameObject go in spawned) if (go != null) Object.DestroyImmediate(go);
        spawned.Clear();
    }
}
