using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Headless validation of the field-interaction features, without entering play mode (modeled on
// RobotPhysicsValidation): edit-mode scripted physics (Physics.simulationMode = Script + Physics.Simulate)
// runs four checks in the saved field scene:
//   - Magnet hit:  a cup dropped slightly off a goal's stack axis gets captured, centered, upright.
//   - Magnet hold: a lateral bump on the seated cup self-corrects (it stays seated and centered).
//   - Magnet miss: a cup dropped clearly off-axis is NOT captured (no teleport-in on a miss).
//   - Roller latch: on the North roller — it holds a face (teleported 30 deg off, it comes back),
//                   it catches a spin (15 rad/s is stopped on a face inside a budget of travel), and
//                   a robot can still turn it (a 240 rpm wheel pressed on a face turns it two faces),
//                   plus a logged sweep of the hook strength behind holdCorrectionPerStep's default.
//
// Edit-mode simulation never runs MonoBehaviours, so the loop calls the public
// GoalStackMagnet.StepMagnet / RollerSnap.StepDetent between Simulate steps — that is why those
// methods are public and dt-parameterized. The simulation mutates the open scene; it is ALWAYS
// reloaded from disk afterwards so simulated poses are never saved.
//
// Also asserts SCENE = CODE for every RollerSnap: a serialized scene value wins over the C# default,
// and the scene once sat at maxCorrectionPerStep 0.2 while the code said 0.35 — a retune nobody
// could see. Attach or Tune Roller Detents copies the code onto the scene; this fails if it was not run.
//
// Requires the scene fixes to have been applied first (Add Goal Stack Magnets, Attach or Tune Roller
// Detents). Batch: -executeMethod FieldFeatureValidation.RunBatch (throws -> nonzero exit).
public static class FieldFeatureValidation
{

    // How long a freshly seated piece is given to finish arriving before the steady-state checks run.
    // Its arrival turn is over inside one second; this is generous on purpose, because the cost of
    // being too short is a test that fails on physics doing exactly what it should.
    private const int SeatedSettleSteps = 150; // 1.5 s

    // Total angle a seated, undisturbed piece may turn through in one second. Measured 0.000 deg on a
    // held piece; a piece the hold has stopped gripping tumbles through hundreds.
    private const float MaxSeatedRotationPerSecond = 2f;
    private const float MaxSeatedAxisError = 0.15f;   // world units off the stack axis once seated
    private const float MinUprightDot = 0.95f;        // cos of allowed tilt once seated
    private const float MaxDetentErrorDeg = 2f;
    private const float MaxDetentRestSpeed = 0.3f;    // rad/s about the axle once settled
    private const float MaxCupAxisError = 0.2f;       // world units off a cup's stack axis once held

    private static PieceStackMagnet[] _cupMagnets;

    [MenuItem("Tools/RoboSim/Validate/Validate Field Features", false, 40)]
    private static void ValidateMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        try
        {
            Run();
            EditorUtility.DisplayDialog("Validate Field Features",
                "All field-feature smoke tests PASSED (magnet hit, hold, miss; roller latch: holds a face, catches a\n" +
                "spin, a robot can still turn it; cup magnet; seated pieces sit still; scene rollers match the code).\n" +
                "See the Console for details.", "OK");
        }
        catch (System.Exception e)
        {
            EditorUtility.DisplayDialog("Validate Field Features", "Validation failed:\n\n" + e.Message, "OK");
        }
    }

    public static void RunBatch() => Run();

    private static void Run()
    {
        EditorSceneManager.OpenScene(RoboSimPaths.MainScene, OpenSceneMode.Single);

        GoalStackMagnet[] magnets = Object.FindObjectsByType<GoalStackMagnet>(FindObjectsInactive.Exclude);
        if (magnets.Length == 0)
            throw new System.InvalidOperationException(
                "No GoalStackMagnet in the scene — run Tools > RoboSim > Field & Pieces > Add Goal Stack Magnets first.");
        RollerSnap[] snaps = Object.FindObjectsByType<RollerSnap>(FindObjectsInactive.Exclude);
        if (snaps.Length == 0)
            throw new System.InvalidOperationException(
                "No RollerSnap in the scene — run Tools > RoboSim > Field & Pieces > Attach or Tune Roller Detents first.");
        _cupMagnets = Object.FindObjectsByType<PieceStackMagnet>(FindObjectsInactive.Exclude);
        if (_cupMagnets.Length == 0)
            Debug.Log("FieldFeatureValidation: no PieceStackMagnet in the scene — cup-magnet check " +
                      "skipped (run Add Cup Stack Magnets to include it).");

        var failures = new List<string>();

        // SCENE = CODE, checked before anything is simulated and ACCUMULATED rather than thrown: the
        // roller tests below then still run, on the scene's values, and the report says both that
        // the scene is stale and what the stale scene does.
        foreach (RollerSnap snap in snaps)
        {
            if (AttachRollerDetents.MatchesCodeDefaults(snap, out string diff)) continue;
            // The component sits on the face body (RollerFace1…); the roller Connor knows by name is its parent.
            Transform rollerRoot = snap.transform.parent;
            string rollerName = rollerRoot != null ? $"{rollerRoot.name}/{snap.name}" : snap.name;
            failures.Add($"roller '{rollerName}' disagrees with RollerSnap.cs ({diff}) — run Tools > RoboSim > " +
                         "Field & Pieces > Attach or Tune Roller Detents, save, then Build Lite Field Scene; every " +
                         "roller number in this run was measured on the stale scene values");
        }

        SimulationMode previous = Physics.simulationMode;
        Physics.simulationMode = SimulationMode.Script;
        try
        {
            TestMagnetHitAndHold(magnets, snaps, failures);
            TestMagnetMiss(magnets, snaps, failures);
            TestRollerLatch(magnets, snaps, failures);
            TestCupMagnet(magnets, snaps, failures);
            TestSeatedPiecesSitStill(magnets, snaps, failures);
        }
        finally
        {
            Physics.simulationMode = previous;
            // Discard every simulated pose — never save a simulated scene.
            EditorSceneManager.OpenScene(RoboSimPaths.MainScene, OpenSceneMode.Single);
        }

        if (failures.Count > 0)
            throw new System.InvalidOperationException(
                "Field-feature smoke tests FAILED:\n  - " + string.Join("\n  - ", failures));
        Debug.Log("FieldFeatureValidation: PASSED (magnet hit, hold, miss; roller latch: holds a face, catches a "
                  + "spin, a robot can still turn it; cup magnet; seated pieces sit still; scene rollers match the code).");
    }

    // One combined physics step: manual component ticks (edit-mode sim runs no MonoBehaviours),
    // then the world step — the same order FixedUpdate would give at play time.
    private static void Step(GoalStackMagnet[] magnets, RollerSnap[] snaps, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            foreach (GoalStackMagnet m in magnets) if (m != null) m.StepMagnet(ValidationUtil.StepSeconds);
            if (_cupMagnets != null)
                foreach (PieceStackMagnet c in _cupMagnets) if (c != null) c.StepMagnet(ValidationUtil.StepSeconds);
            foreach (RollerSnap s in snaps) if (s != null) s.StepDetent(ValidationUtil.StepSeconds);
            Physics.Simulate(ValidationUtil.StepSeconds);
        }
    }

    private static void TestMagnetHitAndHold(GoalStackMagnet[] magnets, RollerSnap[] snaps, List<string> failures)
    {
        GoalStackMagnet magnet = PickMagnet(magnets);
        Rigidbody cup = FindLoosePiece("Cup");
        if (magnet.stackAnchor == null || cup == null)
        {
            failures.Add("magnet hit: no stack anchor or no loose Cup* piece to test with");
            return;
        }

        Vector3 up = magnet.stackAnchor.up;
        Vector3 lateral = Vector3.Cross(up, Vector3.forward).sqrMagnitude > 1e-4f
            ? Vector3.Cross(up, Vector3.forward).normalized : Vector3.right;

        // Drop the cup from 2.5 units above the stack base, 0.3 off-axis — clear of the goal's wall
        // geometry, falling straight down through the capture window (the fall-speed gate must
        // catch it on the way in, before it can ricochet off the snug pocket).
        PlacePieceCenter(cup, magnet.stackAnchor.position + up * 2.5f + lateral * 0.3f);
        bool everClaimed = false;
        var trace = new System.Text.StringBuilder();
        for (int i = 0; i < 400 && !(everClaimed && i > 250); i++) // up to 4 s to fall, capture, settle
        {
            Step(magnets, snaps, 1);
            everClaimed |= GoalStackMagnet.IsClaimed(cup);
            if (i % 20 == 0)
            {
                Vector3 rel = cup.worldCenterOfMass - magnet.stackAnchor.position;
                trace.Append($"[t={i * ValidationUtil.StepSeconds:0.0} h={Vector3.Dot(rel, up):0.00} " +
                             $"r={(rel - up * Vector3.Dot(rel, up)).magnitude:0.00} vy={Vector3.Dot(cup.linearVelocity, up):0.0} " +
                             $"stack={magnet.SeatedCount} claimed={GoalStackMagnet.IsClaimed(cup)}] ");
            }
        }

        float axisError = AxisDistance(magnet.stackAnchor, cup.worldCenterOfMass);
        if (!GoalStackMagnet.IsClaimed(cup))
            failures.Add($"magnet hit: '{cup.name}' was not captured on '{magnet.name}' " +
                         $"(ever claimed during the drop: {everClaimed}; anchor {magnet.stackAnchor.position}, " +
                         $"up {magnet.stackAnchor.up}; cup ended at {cup.worldCenterOfMass}, axis error " +
                         $"{axisError:0.###}u; stack: {magnet.DescribeStack()})\n    trace: {trace}");
        else
        {
            // LET IT ARRIVE BEFORE ASKING WHETHER IT IS STILL. The capture loop above exits as soon as
            // the piece has been claimed for 250 steps, and a piece pulled onto the stake is still
            // travelling then — it makes one last settling turn as it comes down onto its slot.
            // Measured, from exactly the point this test used to start asserting:
            //     second 1: turned through 8.468 deg (net 8.359)   <- the arrival, one monotonic turn
            //     second 2 onward: 0.000 deg, every second, forever
            // So the old failure was measuring the arrival and calling it a failure to hold. Everything
            // below is a claim about the STEADY state, which is what the magnet promises.
            Step(magnets, snaps, SeatedSettleSteps);
            axisError = AxisDistance(magnet.stackAnchor, cup.worldCenterOfMass);

            if (axisError > MaxSeatedAxisError)
                failures.Add($"magnet hit: seated '{cup.name}' is {axisError:0.###}u off the stack axis (max {MaxSeatedAxisError})");
            if (magnet.keepDroppedOrientation)
            {
                // New default: the magnet keeps the piece's dropped attitude rather than standing it
                // upright, so don't assert upright — assert it is HELD STEADY (not tumbling) instead.
                //
                // MEASURED AS ANGLE TRAVELLED, NOT AS ANGULAR VELOCITY, and that distinction is the
                // whole check (2026-08-19). Sampling rb.angularVelocity here reported 0.38-1.53 rad/s
                // on a piece that was rotationally FROZEN, and failed this test for months on that
                // basis. Two things conspire: AddTorque(VelocityChange) queues a velocity change that
                // PhysX applies at the START of the next step, so the hold zeroes the spin before any
                // of it is integrated; and the contact solver then leaves a fresh residual at the END
                // of the step, which is what a post-step sample reads. The residual is never turned
                // into rotation — it is cancelled again before the next integration. Verified by
                // measuring both on the same seated cup over 2 s: 1.239 rad/s reported, 0.000 degrees
                // travelled, and identical with the hold's attitude correction disabled entirely.
                //
                // Angle travelled is also the STRONGER assertion, not a softened one: it catches a
                // slow steady creep that an instantaneous sample can miss between steps, and it
                // catches a back-and-forth buzz that would average to nothing. It is the property the
                // message actually claims — "the hold should freeze its dropped attitude".
                //
                // WHAT IT DOES NOT GUARD, so nobody reads more into a green here than is there:
                // deleting the hold's AddTorque entirely still passes this. A cup seated in a goal is
                // a hex ring in a hex pocket threaded on the post — the GEOMETRY holds its attitude,
                // and it comes to rotational rest whether or not the magnet is doing anything. A
                // disturbance the magnet alone must answer could not be constructed here for the same
                // reason: the pocket will not let the piece turn in the first place. Where the hold
                // genuinely carries the attitude is higher in a stack, where pieces are collision-muted
                // against each other and nothing but the magnet is holding them — that is where a test
                // with teeth would have to live.
                Quaternion previous = cup.rotation;
                float travelledDeg = 0f;
                for (int i = 0; i < 100; i++)   // 1 s
                {
                    Step(magnets, snaps, 1);
                    travelledDeg += Quaternion.Angle(previous, cup.rotation);
                    previous = cup.rotation;
                }
                if (travelledDeg > MaxSeatedRotationPerSecond)
                    failures.Add($"magnet hit: seated '{cup.name}' turned through {travelledDeg:0.##} deg in 1 s " +
                                 $"(budget {MaxSeatedRotationPerSecond}) — the hold should freeze its dropped attitude");
            }
            else
            {
                float uprightDot = UprightDot(cup, up);
                if (uprightDot < MinUprightDot)
                    failures.Add($"magnet hit: seated '{cup.name}' is tilted (upright dot {uprightDot:0.###} < {MinUprightDot})");
            }

            // Casual bump: a sideways shove within the magnet's strength must self-correct.
            cup.linearVelocity += lateral * 2f;
            Step(magnets, snaps, 150); // 1.5 s to recover
            float afterBump = AxisDistance(magnet.stackAnchor, cup.worldCenterOfMass);
            if (!GoalStackMagnet.IsClaimed(cup))
                failures.Add("magnet hold: a 2 u/s bump knocked the seated cup off the goal");
            else if (afterBump > MaxSeatedAxisError)
                failures.Add($"magnet hold: cup did not re-center after a bump ({afterBump:0.###}u off-axis)");
        }
    }

    private static void TestMagnetMiss(GoalStackMagnet[] magnets, RollerSnap[] snaps, List<string> failures)
    {
        GoalStackMagnet magnet = PickMagnet(magnets);
        Rigidbody pin = FindLoosePiece("Pin");
        if (magnet.stackAnchor == null || pin == null)
        {
            failures.Add("magnet miss: no stack anchor or no loose Pin* piece to test with");
            return;
        }

        Vector3 up = magnet.stackAnchor.up;
        Vector3 lateral = Vector3.Cross(up, Vector3.forward).sqrMagnitude > 1e-4f
            ? Vector3.Cross(up, Vector3.forward).normalized : Vector3.right;

        // A clear miss: 2.5 units off-axis (capture radius is ~0.6) — must NOT get pulled in.
        PlacePieceCenter(pin, magnet.stackAnchor.position + up * 1.8f + lateral * 2.5f);
        Step(magnets, snaps, 200); // 2 s
        if (GoalStackMagnet.IsClaimed(pin))
            failures.Add($"magnet miss: '{pin.name}' dropped 2.5u off-axis was captured — misses must stay out");
    }

    // --- Roller latch -----------------------------------------------------------------------------
    //
    // Three claims on ONE roller, found BY NAME: the four rollers' hinge axes differ (North/South
    // spin about local X, East/West about local Y) and FindObjectsByType's order is not a contract,
    // so snaps[0] is a different roller from run to run. All three run in the shared simulated scene,
    // third in line on purpose: by then the Hit/Hold and Miss tests have stepped the scene enough
    // that PhysX has built the roller's joint, hinge.angle is a number (NaN until then) and the
    // authored pose is joint zero.

    private const string LatchRollerName = "RollerNorth";
    private const float LatchTeleportDeg = 30f;      // HoldsAFace: how far off a face the roller is put
    private const float LatchSettleSeconds = 0.5f;   // ...and how long it has to be back within MaxDetentErrorDeg
    private const float LatchSpinRadPerSec = 15f;    // CatchesASpin: harder than any robot flicks it
    private const float LatchSpinSeconds = 1f;       // ...and how long it has to be at rest on a face
    private const float MaxSpinTravelDeg = 240f;     // ...having travelled at most this (old model: ~525)
    private const float MinSpinTravelDeg = 5f;       // ...but at least this, or the spin never registered
    private const float BumpSeconds = 2f;            // RobotBumpsItToTheNextFace: the whole bump, then the catch
    private const float MinBumpAdvanceDeg = 100f;    // ...must click it most of one face forward
    private const float MaxBumpTravelDeg = 400f;     // ...and never past three faces (that is a free spin)
    private const float BumperMass = 7f;             // a robot: Darwinbot is 6.5 kg, 654V_v3 11.8
    private const float ClickSpeed = 4f;             // u/s, a firm hit — about half a drivetrain's top speed
    private static readonly float[] BumpSpeeds = { 1f, 2f, 3f, 4f, 6f };   // the feel table: u/s -> faces
    private const float BumperSize = 1f;             // a 100 mm cube of bumper
    private const float BumperBite = 0.08f;          // how far its underside sits below the roller's highest point
    private const float BumperRunUp = 1.5f;          // starts this far to the side of the axle
    private const int ReseatSteps = 100;             // 1 s for the detent alone to put the roller back on a face
    private const string BumperMaterialPath = "Assets/ChassisPhysics.physicMaterial";

    // The hook strengths the fixture is run at after its assertion, one logged line each: the
    // measured table behind RollerSnap.holdCorrectionPerStep's default, re-measured every run so a
    // retune has numbers. Change the values here; the code default should sit at 40-50% of the
    // largest one the wheel still beats.

    private static void TestRollerLatch(GoalStackMagnet[] magnets, RollerSnap[] snaps, List<string> failures)
    {
        GameObject roller = GameObject.Find(LatchRollerName);
        HingeJoint hinge = roller != null ? roller.GetComponentInChildren<HingeJoint>(true) : null;
        RollerSnap snap = hinge != null ? hinge.GetComponent<RollerSnap>() : null;
        Rigidbody rb = hinge != null ? hinge.GetComponent<Rigidbody>() : null;
        if (hinge == null || snap == null || rb == null)
        {
            failures.Add($"roller latch: '{LatchRollerName}' with a HingeJoint + Rigidbody + RollerSnap on its " +
                         "face was not found — run Rig Rollers, then Attach or Tune Roller Detents");
            return;
        }
        PrimeHingeTracker(magnets, snaps, hinge, rb);
        if (float.IsNaN(hinge.angle))
        {
            failures.Add($"roller latch: '{LatchRollerName}' hinge angle is still NaN after a 0.5 rad/s nudge and " +
                         "two steps — PhysX never built its joint");
            return;
        }
        Vector3 axisW = (hinge.transform.rotation * hinge.axis).normalized;

        HoldsAFace(magnets, snaps, failures, hinge, snap, rb, axisW);
        CatchesASpin(magnets, snaps, failures, hinge, snap, rb, axisW);
        RobotBumpsItToTheNextFace(magnets, snaps, failures, hinge, snap, rb, axisW);
    }

    // PhysX fills HingeJoint.angle only once the joint has actually MOVED. A roller nothing has
    // touched reads NaN however many steps the scene took around it, and WakeUp() alone does not
    // change that (measured: still NaN after WakeUp + 2 steps) — the old detent test only ever read
    // a number because the 0.5 rad/s spin it applied WAS the movement. So every measurement primes
    // the tracker the same way, on purpose: a 0.5 rad/s nudge about the axle for two steps is 0.6 deg
    // of travel the detent erases, and after it a NaN is the real failure (a joint PhysX refused to
    // build), not a body that was resting — or, in a fixture, one the wheel never woke.
    private const float PrimeRadPerSec = 0.5f;
    private static void PrimeHingeTracker(GoalStackMagnet[] magnets, RollerSnap[] snaps, HingeJoint hinge, Rigidbody rb)
    {
        Vector3 axis = (hinge.transform.rotation * hinge.axis).normalized;
        rb.angularVelocity = axis * PrimeRadPerSec;
        Step(magnets, snaps, 2);
    }

    // A roller nudged off a face comes back onto it. The nudge is a TELEPORT about the hinge anchor:
    // the anchors sit up to 19 u from the transform origins, so rotating the transform in place would
    // carry the axle with it and measure PhysX dragging the joint back together, not the detent.
    private static void HoldsAFace(GoalStackMagnet[] magnets, RollerSnap[] snaps, List<string> failures,
        HingeJoint hinge, RollerSnap snap, Rigidbody rb, Vector3 axisW)
    {
        Vector3 pivotW = hinge.transform.TransformPoint(hinge.anchor);
        hinge.transform.RotateAround(pivotW, axisW, LatchTeleportDeg);
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        Physics.SyncTransforms();

        // One step so the joint tracker sees the new pose (the detent gets one between-face step
        // in, a third of a degree).
        Step(magnets, snaps, 1);
        float start = Mathf.Abs(FaceErrorDeg(hinge, snap));
        // Tautology guard: the teleport has to register as JOINT ANGLE. Rotated about the wrong point
        // PhysX projects the joint back together, the roller reads as barely off the face, and a test
        // that starts on the face proves nothing about getting back to one.
        if (start < LatchTeleportDeg - 10f)
        {
            failures.Add($"roller latch (holds a face): a {LatchTeleportDeg} deg teleport about the hinge anchor " +
                         $"reads as only {start:0.#} deg of joint angle, so this test would measure nothing — " +
                         "rotate about transform.TransformPoint(hinge.anchor), not the transform origin");
            return;
        }

        int settleSteps = Mathf.RoundToInt(LatchSettleSeconds / ValidationUtil.StepSeconds);
        int landedStep = -1;
        for (int i = 1; i <= settleSteps; i++)
        {
            Step(magnets, snaps, 1);
            if (landedStep < 0 && Mathf.Abs(FaceErrorDeg(hinge, snap)) <= MaxDetentErrorDeg) landedStep = i;
        }
        float end = Mathf.Abs(FaceErrorDeg(hinge, snap));
        float speed = AxleSpeed(rb, axisW);
        if (end > MaxDetentErrorDeg)
            failures.Add($"roller latch (holds a face): put {start:0.#} deg off a face, '{hinge.name}' is still " +
                         $"{end:0.#} deg off after {LatchSettleSeconds} s (max {MaxDetentErrorDeg}) — the detent " +
                         "must drag it back onto the face");
        if (speed > MaxDetentRestSpeed)
            failures.Add($"roller latch (holds a face): '{hinge.name}' is still turning at {speed:0.##} rad/s " +
                         $"after {LatchSettleSeconds} s (max {MaxDetentRestSpeed})");
        Debug.Log($"FieldFeatureValidation roller latch (holds a face): {start:0.#} deg off -> within " +
                  $"{MaxDetentErrorDeg} deg at {(landedStep > 0 ? landedStep * ValidationUtil.StepSeconds : float.NaN):0.00} s; " +
                  $"{end:0.##} deg off and {speed:0.###} rad/s at {LatchSettleSeconds} s.");
    }

    // A spun roller is CAUGHT, not coasted down. 15 rad/s is harder than any robot lets go of it at;
    // the old model disengaged above 4 rad/s and let damping alone bring it down to that, ~525 deg
    // later — the number the travel budget is written against. Travel is the joint's own angle,
    // unwrapped step by step, rather than a velocity integral: it is what PhysX actually turned.
    private static void CatchesASpin(GoalStackMagnet[] magnets, RollerSnap[] snaps, List<string> failures,
        HingeJoint hinge, RollerSnap snap, Rigidbody rb, Vector3 axisW)
    {
        rb.angularVelocity = axisW * LatchSpinRadPerSec;
        float previous = hinge.angle;
        float path = 0f;
        int restStep = -1;
        int steps = Mathf.RoundToInt(LatchSpinSeconds / ValidationUtil.StepSeconds);
        for (int i = 1; i <= steps; i++)
        {
            path += Mathf.Abs(StepAndTurn(magnets, snaps, hinge, ref previous));
            if (restStep < 0 && AxleSpeed(rb, axisW) < MaxDetentRestSpeed &&
                Mathf.Abs(FaceErrorDeg(hinge, snap)) <= MaxDetentErrorDeg)
                restStep = i;
        }
        float end = Mathf.Abs(FaceErrorDeg(hinge, snap));
        float speed = AxleSpeed(rb, axisW);

        // Tautology guard: the spin has to have registered. A velocity the joint swallowed would sit
        // inside every budget below without the detent doing a thing.
        if (path < MinSpinTravelDeg)
        {
            failures.Add($"roller latch (catches a spin): a {LatchSpinRadPerSec} rad/s spin turned '{hinge.name}' " +
                         $"only {path:0.#} deg in {LatchSpinSeconds} s — the spin never registered, so this " +
                         "measured nothing");
            return;
        }
        if (path > MaxSpinTravelDeg)
            failures.Add($"roller latch (catches a spin): spun at {LatchSpinRadPerSec} rad/s, '{hinge.name}' " +
                         $"travelled {path:0.#} deg before {LatchSpinSeconds} s was up (budget {MaxSpinTravelDeg}) — " +
                         "it is coasting; the hook must never disengage");
        if (end > MaxDetentErrorDeg)
            failures.Add($"roller latch (catches a spin): '{hinge.name}' is {end:0.#} deg off a face after " +
                         $"{LatchSpinSeconds} s (max {MaxDetentErrorDeg})");
        if (speed > MaxDetentRestSpeed)
            failures.Add($"roller latch (catches a spin): '{hinge.name}' is still turning at {speed:0.##} rad/s " +
                         $"after {LatchSpinSeconds} s (max {MaxDetentRestSpeed})");
        Debug.Log($"FieldFeatureValidation roller latch (catches a spin): {LatchSpinRadPerSec} rad/s -> travelled " +
                  $"{path:0.#} deg, at rest on a face at {(restStep > 0 ? restStep * ValidationUtil.StepSeconds : float.NaN):0.00} s; " +
                  $"{end:0.##} deg off and {speed:0.###} rad/s at {LatchSpinSeconds} s.");
    }


    // A robot does not spin this roller by FRICTION, and no detent setting changes that. The roller
    // is a three-faced prism — measured off its renderers by Attach or Tune Roller Detents (one
    // 0.08-thick face, two thicker ones, corners 0.29-0.41 from the axle) — and so is its collider.
    // A wheel pressing a flat face from a fixed direction pins it about 14 deg past the face whatever
    // the load: the contact walks off-centre as the face tilts, and the normal force there restores
    // the face faster than friction turns it (mu (d cos t - R) = d sin t; the first version of this
    // test pressed a 240 rpm wheel on with 200 of preload and got 13.4 deg at EVERY hook strength
    // from 1 to 12). What a robot actually does is BUMP it: drive into a corner, click it over to
    // the next face, and then the hook has to catch it there instead of letting it run on. So the
    // fixture is a robot-mass cube, coasting at a deliberate speed with the momentum a drivetrain
    // gives it, hitting the top corner from the free side; the bump must advance the roller most of
    // a face, the hook must stop it within three, and it must be sitting on a face at the end.
    private static void RobotBumpsItToTheNextFace(GoalStackMagnet[] magnets, RollerSnap[] snaps, List<string> failures,
        HingeJoint hinge, RollerSnap snap, Rigidbody rb, Vector3 axisW)
    {
        float travel = BumpAndMeasure(magnets, snaps, hinge, axisW, ClickSpeed, out string why);
        if (why != null)
        {
            failures.Add($"roller latch (a bump clicks it over): could not build the bumper — {why}");
            return;
        }
        float end = Mathf.Abs(FaceErrorDeg(hinge, snap));
        float rest = AxleSpeed(rb, axisW);
        if (float.IsNaN(travel) || Mathf.Abs(travel) < MinBumpAdvanceDeg)
            failures.Add($"roller latch (a bump clicks it over): a {BumperMass} kg bumper at {ClickSpeed} u/s turned " +
                         $"'{hinge.name}' only {Mathf.Abs(travel):0.#} deg (min {MinBumpAdvanceDeg}) — the roller is refusing a " +
                         "firm hit; lower RollerSnap.maxCorrectionPerStep, the between-face pull (see the speed table)");
        else if (Mathf.Abs(travel) > MaxBumpTravelDeg)
            failures.Add($"roller latch (a bump clicks it over): one bump ran '{hinge.name}' on {Mathf.Abs(travel):0.#} deg " +
                         $"(max {MaxBumpTravelDeg}) — the hook is not catching it");
        if (end > MaxDetentErrorDeg || rest > MaxDetentRestSpeed)
            failures.Add($"roller latch (a bump clicks it over): '{hinge.name}' ended {end:0.#} deg off a face at " +
                         $"{rest:0.##} rad/s {BumpSeconds} s after the bump — not caught on a face");
        Debug.Log($"FieldFeatureValidation roller latch (a bump clicks it over): a {BumperMass} kg bumper at {ClickSpeed} u/s " +
                  $"turned '{hinge.name}' {Mathf.Abs(travel):0.#} deg; {end:0.##} deg off a face and {rest:0.###} rad/s " +
                  $"at {BumpSeconds} s.");

        SpeedTable(magnets, snaps, hinge, snap, rb, axisW);
    }

    // Informational — the feel table behind RollerSnap.maxCorrectionPerStep. A bump kicks the roller
    // to about (speed / corner radius) rad/s and the between-face pull then decides whether that
    // carries it over the 60-degree midpoint to the next face or drags it back to the one it left,
    // so the number a driver feels is the speed at which a hit starts to click. One line per speed:
    // faces advanced, and whether it clicked, was refused, or ran on. The roller is re-seated
    // between bumps; Run()'s finally reloads the scene from disk regardless.
    private static void SpeedTable(GoalStackMagnet[] magnets, RollerSnap[] snaps, HingeJoint hinge, RollerSnap snap,
        Rigidbody rb, Vector3 axisW)
    {
        var table = new System.Text.StringBuilder();
        table.AppendLine($"FieldFeatureValidation roller speed table — a {BumperMass} kg bumper hitting '{hinge.name}' " +
                         "on its top corner, by speed (u/s -> degrees, faces):");
        foreach (float speed in BumpSpeeds)
        {
            string seat = Reseat(magnets, snaps, hinge, snap, rb);
            float turned = BumpAndMeasure(magnets, snaps, hinge, axisW, speed, out string why);
            if (why != null)
            {
                table.AppendLine($"  {speed,4:0.#} u/s -> fixture failed: {why}");
                continue;
            }
            float faces = Mathf.Abs(turned) / RollerSnap.FaceSpacingDeg;
            string verdict = float.IsNaN(turned) || Mathf.Abs(turned) < MinBumpAdvanceDeg ? "refused"
                : Mathf.Abs(turned) > MaxBumpTravelDeg ? "RUNS ON" : "clicks";
            table.AppendLine($"  {speed,4:0.#} u/s -> {Mathf.Abs(turned),6:0.#} deg = {faces:0.0} face(s)  {verdict}{seat}");
        }
        Reseat(magnets, snaps, hinge, snap, rb);
        Debug.Log(table.ToString());
    }


    // One bump: a cube of robot mass, no gravity and no drive of its own (it coasts on the momentum a
    // drivetrain gave it, which is what pushes a hook over — a body driven at constant velocity would
    // be an infinite force and could not refuse), released BumperRunUp to the side of the axle with
    // its underside just above the axle plane, so its leading face meets the roller's top corner.
    // The free side is the one where the start box is clear of colliders — a roller in a wall has
    // exactly one. Returns the roller's net travel over BumpSeconds (NaN if its tracker never read),
    // and destroys the cube whatever happens.
    private static float BumpAndMeasure(GoalStackMagnet[] magnets, RollerSnap[] snaps, HingeJoint hinge, Vector3 axisW,
        float speed, out string why)
    {
        why = null;
        Vector3 axleW = hinge.transform.TransformPoint(hinge.anchor);
        Vector3 across = Vector3.Cross(Vector3.up, axisW);
        if (across.sqrMagnitude < 1e-4f)
        {
            why = "the roller's axle is vertical, so there is no level direction to bump it from";
            return float.NaN;
        }
        across.Normalize();
        Vector3 up = Vector3.Cross(axisW, across).normalized;
        if (Vector3.Dot(up, Vector3.up) < 0f) up = -up;
        // Its underside sits BumperBite below the roller's highest point AT THIS POSE, so the leading
        // face bites the top corner. Height matters more than anything else here: at its stops the
        // prism presents a flat face to the field, and a cube driving straight into that face pushes
        // through the axle line — zero torque, the hook holds trivially, and the first version of
        // this fixture (underside 0.05 above the axle) read 0.0 deg at every hook strength.
        float top = float.NegativeInfinity;
        foreach (BoxCollider panel in hinge.GetComponentsInChildren<BoxCollider>())
        {
            if (panel.isTrigger || !panel.enabled) continue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = panel.center + Vector3.Scale(panel.size * 0.5f,
                    new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                top = Mathf.Max(top, Vector3.Dot(panel.transform.TransformPoint(corner) - axleW, up));
            }
        }
        if (float.IsNegativeInfinity(top))
        {
            why = $"'{hinge.name}' has no BoxCollider panels to aim the bumper at";
            return float.NaN;
        }
        Vector3 lift = up * (top - BumperBite + BumperSize * 0.5f);
        Vector3 half = Vector3.one * (BumperSize * 0.5f);
        Quaternion facing = Quaternion.LookRotation(across, up);

        // The free side is the one from which a cast toward the axle meets the ROLLER first — not
        // merely the side where the start box is clear: a roller set in the perimeter has a clear
        // start beyond the wall too, and a cube released there bounces off the back of the wall
        // with the roller untouched (measured: 0.0 deg at every hook strength, cube back at 0.65 u/s).
        Vector3 start = Vector3.zero, travelDir = Vector3.zero;
        bool found = false;
        foreach (float side in new[] { 1f, -1f })
        {
            Vector3 candidate = axleW + across * (side * BumperRunUp) + lift;
            Vector3 dir = -across * side;
            if (Physics.CheckBox(candidate, half, facing)) continue;
            if (!Physics.BoxCast(candidate, half, dir, out RaycastHit hit, facing, BumperRunUp + BumperSize)) continue;
            if (!hit.collider.transform.IsChildOf(hinge.transform)) continue;   // a wall, a frame — not the roller
            start = candidate;
            travelDir = dir;
            found = true;
            break;
        }
        if (!found)
        {
            why = $"no side of '{hinge.name}' has a clear run-up ending on the roller itself {BumperRunUp} u out from the axle";
            return float.NaN;
        }
        PhysicsMaterial chassis = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(BumperMaterialPath);
        if (chassis == null)
        {
            why = $"no physic material at {BumperMaterialPath} — the bumper would carry PhysX's default friction, not a robot's";
            return float.NaN;
        }

        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "RollerBumper";
        cube.transform.SetPositionAndRotation(start, facing);
        cube.transform.localScale = Vector3.one * BumperSize;
        cube.GetComponent<BoxCollider>().sharedMaterial = chassis;
        Rigidbody body = cube.AddComponent<Rigidbody>();
        body.mass = BumperMass;
        body.useGravity = false;
        body.linearDamping = 0f;
        body.angularDamping = 0f;
        body.constraints = RigidbodyConstraints.FreezeRotation;   // a chassis does not roll over a roller
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        try
        {
            Physics.SyncTransforms();
            body.linearVelocity = travelDir * speed;
            PrimeHingeTracker(magnets, snaps, hinge, hinge.GetComponent<Rigidbody>());
            float previous = hinge.angle;
            float net = 0f;
            int steps = Mathf.RoundToInt(BumpSeconds / ValidationUtil.StepSeconds);
            for (int i = 0; i < steps; i++) net += StepAndTurn(magnets, snaps, hinge, ref previous);
            return net;
        }
        finally
        {
            Object.DestroyImmediate(cube);
        }
    }


    // Stop the roller and let the detent alone put it back on a face, so every sweep entry starts
    // from the same place. Returns a note when it did not get there — the entry is then measured
    // from wherever it sat, and says so.
    private static string Reseat(GoalStackMagnet[] magnets, RollerSnap[] snaps, HingeJoint hinge, RollerSnap snap,
        Rigidbody rb)
    {
        rb.angularVelocity = Vector3.zero;
        rb.linearVelocity = Vector3.zero;
        Step(magnets, snaps, ReseatSteps);
        float off = Mathf.Abs(FaceErrorDeg(hinge, snap));
        return off <= MaxDetentErrorDeg ? "" : $"   (started {off:0.#} deg off a face)";
    }




    // Signed degrees from the nearest face, against the same stops the detent uses.
    private static float FaceErrorDeg(HingeJoint hinge, RollerSnap snap)
    {
        float angle = hinge.angle;
        float nearest = Mathf.Round((angle - snap.AngleOffsetDeg) / RollerSnap.FaceSpacingDeg) * RollerSnap.FaceSpacingDeg
                        + snap.AngleOffsetDeg;
        return Mathf.DeltaAngle(angle, nearest);
    }

    private static float AxleSpeed(Rigidbody rb, Vector3 axisW) => Mathf.Abs(Vector3.Dot(rb.angularVelocity, axisW));



    private static float StepAndTurn(GoalStackMagnet[] magnets, RollerSnap[] snaps, HingeJoint hinge, ref float previousAngle)
    {
        Step(magnets, snaps, 1);
        float turned = Mathf.DeltaAngle(previousAngle, hinge.angle);
        previousAngle = hinge.angle;
        return turned;
    }

    // A scored piece must SIT there. Connor's report, standing for months: cups pulled onto a goal
    // "are like jittery and most of the time vibrating".
    //
    // The cause was two magnets holding one piece. GoalStackMagnet and PieceStackMagnet each keep a
    // private claim registry, and neither used to consult the other, so a cup could be seated on a
    // goal AND stacked on the cup below it at once. Two holds do not average out: each is a deadbeat
    // (desiredVel = toSlot/step) aimed at a DIFFERENT slot, each does one AddForce per step, and the
    // last writer wins — alternately. Measured on three cups seated on one goal and left 2 s:
    //     goal-claimed only        0 direction reversals, peak |v| 0.000 u/s  (the body sleeps)
    //     goal + cup claimed     199 direction reversals in 200 steps, peak |v| 4.0 u/s
    // 40 u of path to end up 0.4 u from where it started, at half the physics rate — a visible buzz.
    //
    // TWO ASSERTIONS, because either alone is weak. The double-claim sweep is the sharp one: it is
    // exact, deterministic, and names the actual invariant. The motion budget is the one that still
    // holds if some future third holder invents a new way to fight over a piece — and the gap it is
    // policing is three orders of magnitude (8000 mm of path against 1), so a generous budget is
    // still a real check rather than a threshold nobody can trip.
    private static void TestSeatedPiecesSitStill(GoalStackMagnet[] magnets, RollerSnap[] snaps,
        List<string> failures)
    {
        GoalStackMagnet magnet = PickMagnet(magnets);
        if (magnet == null || magnet.stackAnchor == null)
        {
            failures.Add("seated stillness: no goal magnet with a stack anchor to test with");
            return;
        }

        Vector3 up = magnet.stackAnchor.up;
        Vector3 lateral = Vector3.Cross(up, Vector3.forward).sqrMagnitude > 1e-4f
            ? Vector3.Cross(up, Vector3.forward).normalized : Vector3.right;

        // Stack three, because ONE is not enough to reproduce this: the bottom piece rests on the
        // goal itself and is held by a real contact, and it was quiet even while the pieces above it
        // buzzed. The conflict needs a piece sitting on another piece.
        var seated = new List<Rigidbody>();
        for (int c = 0; c < 3; c++)
        {
            Rigidbody cup = FindLoosePieceExcluding("Cup", seated);
            if (cup == null) break;
            PlacePieceCenter(cup, magnet.stackAnchor.position + up * (2.5f + c * 0.4f) + lateral * 0.15f);
            Step(magnets, snaps, 300);
            if (GoalStackMagnet.IsClaimed(cup)) seated.Add(cup);
        }
        if (seated.Count < 2)
        {
            failures.Add($"seated stillness: only {seated.Count} cup(s) seated on '{magnet.name}' — " +
                         "cannot measure a stack. Usually means capture itself is broken; the magnet " +
                         "hit test above says which.");
            return;
        }
        Step(magnets, snaps, 300); // settle

        // ONE PIECE, ONE MAGNET. Exact, and it is the invariant that actually regressed.
        var doubled = new List<string>();
        foreach (Rigidbody rb in Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude))
            if (GoalStackMagnet.IsClaimed(rb) && PieceStackMagnet.IsClaimed(rb)) doubled.Add(rb.name);
        if (doubled.Count > 0)
            failures.Add($"seated stillness: {doubled.Count} piece(s) held by a goal AND a cup magnet " +
                         $"at once ({string.Join(", ", doubled)}). Two deadbeat holds aimed at " +
                         "different slots alternate one AddForce per step and buzz the piece at half " +
                         "the physics rate. Both TryCapture filters must skip what the other holds.");

        // ...and the felt form: a piece at rest travels essentially nowhere over a second.
        foreach (Rigidbody cup in seated)
        {
            if (!GoalStackMagnet.IsClaimed(cup)) continue;   // drifted off on its own — not this test's claim
            Vector3 previous = cup.worldCenterOfMass;
            float path = 0f;
            for (int i = 0; i < 100; i++)
            {
                Step(magnets, snaps, 1);
                Vector3 p = cup.worldCenterOfMass;
                path += (p - previous).magnitude;
                previous = p;
            }
            if (path > MaxSeatedPathPerSecond)
                failures.Add($"seated stillness: '{cup.name}' travelled {path:0.00} u in 1 s while " +
                             $"seated and undisturbed (budget {MaxSeatedPathPerSecond:0.00} u). A scored " +
                             "piece must sit still — something is fighting the magnet for it.");
        }
    }

    // How far a seated, undisturbed piece may travel in one second. Measured: 0.001 u when one
    // magnet holds it, 40 u when two do.
    private const float MaxSeatedPathPerSecond = 0.5f;

    private static Rigidbody FindLoosePieceExcluding(string prefix, List<Rigidbody> exclude)
    {
        foreach (Rigidbody rb in Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude))
        {
            if (!rb.name.StartsWith(prefix) || rb.isKinematic || exclude.Contains(rb)) continue;
            if (GoalStackMagnet.IsClaimed(rb) || PieceStackMagnet.IsClaimed(rb)) continue;
            return rb;
        }
        return null;
    }

    // Cup magnet: drop a pin onto a cup held perfectly still + upright (re-pinned each step at a
    // clear high spot, so the test isolates the magnet's capture/hold from the cup settling itself),
    // and require the pin to be captured, centered on the cup's axis, and to survive a small bump.
    // Skipped (not failed) if no cup magnet is in the scene.
    private static void TestCupMagnet(GoalStackMagnet[] magnets, RollerSnap[] snaps, List<string> failures)
    {
        PieceStackMagnet cupMag = null;
        foreach (PieceStackMagnet m in _cupMagnets)
        {
            if (m == null) continue;
            foreach (PieceStackMagnet.PieceProfile p in m.pieceProfiles)
                if (p != null && p.namePrefix == "Pin") { cupMag = m; break; }
            if (cupMag != null) break;
        }
        if (cupMag == null) return; // none applied — Run() logged the skip

        Rigidbody cup = cupMag.GetComponent<Rigidbody>();
        Rigidbody pin = FindLoosePieceForCup("Pin", cup);
        if (cup == null || pin == null)
        {
            failures.Add("cup magnet: no cup rigidbody or no loose Pin* piece to test with");
            return;
        }

        // Hold the cup upright and still at a clear high spot (2 m up), re-pinned every step.
        Vector3 cupPos = new Vector3(0f, 20f, 0f);
        Vector3 cupWorldUp = cupMag.transform.TransformDirection(cupMag.localUpAxis);
        cupWorldUp = cupWorldUp.sqrMagnitude > 1e-4f ? cupWorldUp.normalized : cupMag.transform.up;
        Quaternion cupRot = Quaternion.FromToRotation(cupWorldUp, Vector3.up) * cup.transform.rotation;

        float pinRest = 0.8f;
        foreach (PieceStackMagnet.PieceProfile p in cupMag.pieceProfiles)
            if (p != null && p.namePrefix == "Pin") pinRest = p.restHeight;

        PinCup(cup, cupPos, cupRot);
        Vector3 basePos = cupMag.transform.TransformPoint(cupMag.localBaseOffset);
        PlacePieceCenter(pin, basePos + Vector3.up * (pinRest + 0.4f));
        pin.linearVelocity = Vector3.down * 2f;

        bool everClaimed = false;
        for (int i = 0; i < 300 && !(everClaimed && i > 200); i++)
        {
            PinCup(cup, cupPos, cupRot);           // keep the base perfectly at rest + upright
            Step(magnets, snaps, 1);
            everClaimed |= PieceStackMagnet.IsClaimed(pin);
        }

        if (!PieceStackMagnet.IsClaimed(pin))
        {
            float off = AxisDistanceUp(cupMag.transform.TransformPoint(cupMag.localBaseOffset), pin.worldCenterOfMass);
            failures.Add($"cup magnet: '{pin.name}' dropped onto cup '{cup.name}' was not held " +
                         $"(ever claimed: {everClaimed}; pin ended {off:0.###}u off the cup axis)");
            return;
        }

        float axisError = AxisDistanceUp(cupMag.transform.TransformPoint(cupMag.localBaseOffset), pin.worldCenterOfMass);
        if (axisError > MaxCupAxisError)
            failures.Add($"cup magnet: held '{pin.name}' is {axisError:0.###}u off the cup's stack axis (max {MaxCupAxisError})");

        // Casual bump: a sideways shove within the magnet's strength must self-correct.
        pin.linearVelocity += Vector3.forward * 2f;
        for (int i = 0; i < 150; i++) { PinCup(cup, cupPos, cupRot); Step(magnets, snaps, 1); }
        if (!PieceStackMagnet.IsClaimed(pin))
            failures.Add("cup magnet hold: a 2 u/s bump knocked the pin off the cup");
    }

    // Force a cup to a fixed, motionless, upright pose (the deterministic resting base for the test).
    private static void PinCup(Rigidbody cup, Vector3 pos, Quaternion rot)
    {
        cup.transform.SetPositionAndRotation(pos, rot);
        cup.linearVelocity = Vector3.zero;
        cup.angularVelocity = Vector3.zero;
        Physics.SyncTransforms();
    }

    // Horizontal distance of a point from a vertical axis through basePos (world up).
    private static float AxisDistanceUp(Vector3 basePos, Vector3 point)
    {
        Vector3 delta = point - basePos;
        return (delta - Vector3.up * Vector3.Dot(delta, Vector3.up)).magnitude;
    }

    // A loose, unclaimed dynamic piece by prefix, excluding a specific body (the test cup).
    private static Rigidbody FindLoosePieceForCup(string prefix, Rigidbody exclude)
    {
        foreach (Rigidbody rb in Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude))
        {
            if (!rb.name.StartsWith(prefix) || rb.isKinematic || rb == exclude) continue;
            if (GoalStackMagnet.IsClaimed(rb) || PieceStackMagnet.IsClaimed(rb)) continue;
            return rb;
        }
        return null;
    }

    // A deterministic test goal: prefer a Neutral goal (standard geometry, sits flat mid-field)
    // over whatever FindObjectsByType happens to return first.
    private static GoalStackMagnet PickMagnet(GoalStackMagnet[] magnets)
    {
        foreach (GoalStackMagnet m in magnets)
            if (m.name.Contains("Neutral") && m.stackAnchor != null) return m;
        return magnets[0];
    }

    // A dynamic scene piece by name prefix that no magnet has already claimed (the authored field
    // may legitimately have pieces sitting in goals).
    private static Rigidbody FindLoosePiece(string prefix)
    {
        foreach (Rigidbody rb in Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude))
        {
            if (!rb.name.StartsWith(prefix) || rb.isKinematic) continue;
            if (GoalStackMagnet.IsClaimed(rb)) continue;
            return rb;
        }
        return null;
    }

    // Teleport a piece so its CENTER OF MASS lands on target (the pieces keep off-center CAD
    // pivots), and zero its motion.
    private static void PlacePieceCenter(Rigidbody rb, Vector3 targetCom)
    {
        rb.transform.position += targetCom - rb.worldCenterOfMass;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        Physics.SyncTransforms();
    }

    private static float AxisDistance(Transform anchor, Vector3 point)
    {
        Vector3 delta = point - anchor.position;
        Vector3 up = anchor.up;
        return (delta - up * Vector3.Dot(delta, up)).magnitude;
    }

    // How upright the piece stands: 1 = its measured standing axis is exactly along the stack axis.
    private static float UprightDot(Rigidbody rb, Vector3 up)
    {
        MeshFilter mf = rb.GetComponentInChildren<MeshFilter>();
        Mesh mesh = mf != null ? mf.sharedMesh : null;
        if (mesh == null) return 1f; // nothing measurable — don't fail on it
        Vector3 s = mesh.bounds.size;
        Vector3 axisLocal = (s.x >= s.y && s.x >= s.z) ? Vector3.right : (s.y >= s.z) ? Vector3.up : Vector3.forward;
        return Mathf.Abs(Vector3.Dot((mf.transform.rotation * axisLocal).normalized, up));
    }
}
