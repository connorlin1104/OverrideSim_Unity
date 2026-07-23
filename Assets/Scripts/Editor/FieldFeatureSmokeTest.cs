using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

// Headless validation of the field-interaction features, without entering play mode (modeled on
// PhysicsSmokeTest): edit-mode scripted physics (Physics.simulationMode = Script + Physics.Simulate)
// runs four checks in the saved field scene:
//   - Magnet hit:  a cup dropped slightly off a goal's stack axis gets captured, centered, upright.
//   - Magnet hold: a lateral bump on the seated cup self-corrects (it stays seated and centered).
//   - Magnet miss: a cup dropped clearly off-axis is NOT captured (no teleport-in on a miss).
//   - Detent:      a slowly-spinning roller settles onto a 120-degree color-face stop.
//
// Edit-mode simulation never runs MonoBehaviours, so the loop calls the public
// GoalStackMagnet.StepMagnet / RollerSnap.StepDetent between Simulate steps — that is why those
// methods are public and dt-parameterized. The simulation mutates the open scene; it is ALWAYS
// reloaded from disk afterwards so simulated poses are never saved.
//
// Requires the scene fixes to have been applied first (Add Goal Stack Magnets, Attach Roller
// Detents). Batch: -executeMethod FieldFeatureSmokeTest.RunBatch (throws -> nonzero exit).
public static class FieldFeatureSmokeTest
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const float StepSeconds = 0.01f;   // matches the project's fixed timestep

    private const float MaxSeatedAxisError = 0.15f;   // world units off the stack axis once seated
    private const float MinUprightDot = 0.95f;        // cos of allowed tilt once seated
    private const float MaxDetentErrorDeg = 2f;
    private const float MaxDetentRestSpeed = 0.3f;    // rad/s about the axle once settled
    private const float MaxCupAxisError = 0.2f;       // world units off a cup's stack axis once held

    private static PieceStackMagnet[] _cupMagnets;

    [MenuItem("Tools/RoboSim/Field & Pieces/Validate Field Features (Magnets + Detents)", false, 6)]
    private static void ValidateMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        try
        {
            Run();
            EditorUtility.DisplayDialog("Validate Field Features",
                "All field-feature smoke tests PASSED (magnet hit, hold, miss; roller detent; cup magnet).\n" +
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
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        GoalStackMagnet[] magnets = Object.FindObjectsByType<GoalStackMagnet>(FindObjectsInactive.Exclude);
        if (magnets.Length == 0)
            throw new System.InvalidOperationException(
                "No GoalStackMagnet in the scene — run Tools > RoboSim > Field & Pieces > Add Goal Stack Magnets first.");
        RollerSnap[] snaps = Object.FindObjectsByType<RollerSnap>(FindObjectsInactive.Exclude);
        if (snaps.Length == 0)
            throw new System.InvalidOperationException(
                "No RollerSnap in the scene — run Tools > RoboSim > Field & Pieces > Attach Roller Detents first.");
        _cupMagnets = Object.FindObjectsByType<PieceStackMagnet>(FindObjectsInactive.Exclude);
        if (_cupMagnets.Length == 0)
            Debug.Log("FieldFeatureSmokeTest: no PieceStackMagnet in the scene — cup-magnet check " +
                      "skipped (run Add Cup Stack Magnets to include it).");

        var failures = new List<string>();
        SimulationMode previous = Physics.simulationMode;
        Physics.simulationMode = SimulationMode.Script;
        try
        {
            TestMagnetHitAndHold(magnets, snaps, failures);
            TestMagnetMiss(magnets, snaps, failures);
            TestDetent(magnets, snaps, failures);
            TestCupMagnet(magnets, snaps, failures);
        }
        finally
        {
            Physics.simulationMode = previous;
            // Discard every simulated pose — never save a simulated scene.
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        if (failures.Count > 0)
            throw new System.InvalidOperationException(
                "Field-feature smoke tests FAILED:\n  - " + string.Join("\n  - ", failures));
        Debug.Log("FieldFeatureSmokeTest: PASSED (magnet hit, hold, miss; roller detent; cup magnet).");
    }

    // One combined physics step: manual component ticks (edit-mode sim runs no MonoBehaviours),
    // then the world step — the same order FixedUpdate would give at play time.
    private static void Step(GoalStackMagnet[] magnets, RollerSnap[] snaps, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            foreach (GoalStackMagnet m in magnets) if (m != null) m.StepMagnet(StepSeconds);
            if (_cupMagnets != null)
                foreach (PieceStackMagnet c in _cupMagnets) if (c != null) c.StepMagnet(StepSeconds);
            foreach (RollerSnap s in snaps) if (s != null) s.StepDetent(StepSeconds);
            Physics.Simulate(StepSeconds);
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
                trace.Append($"[t={i * StepSeconds:0.0} h={Vector3.Dot(rel, up):0.00} " +
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
            if (axisError > MaxSeatedAxisError)
                failures.Add($"magnet hit: seated '{cup.name}' is {axisError:0.###}u off the stack axis (max {MaxSeatedAxisError})");
            if (magnet.keepDroppedOrientation)
            {
                // New default: the magnet keeps the piece's dropped attitude rather than standing it
                // upright, so don't assert upright — assert it is HELD STEADY (not tumbling) instead.
                float spin = cup.angularVelocity.magnitude;
                if (spin > MaxDetentRestSpeed)
                    failures.Add($"magnet hit: seated '{cup.name}' is still spinning ({spin:0.##} rad/s > {MaxDetentRestSpeed}) — the hold should freeze its dropped attitude");
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

    private static void TestDetent(GoalStackMagnet[] magnets, RollerSnap[] snaps, List<string> failures)
    {
        RollerSnap snap = snaps[0];
        Rigidbody rb = snap.GetComponent<Rigidbody>();
        HingeJoint hinge = snap.GetComponent<HingeJoint>();
        if (rb == null || hinge == null)
        {
            failures.Add("detent: roller has no Rigidbody/HingeJoint");
            return;
        }

        Vector3 axis = (snap.transform.rotation * hinge.axis).normalized;
        rb.angularVelocity = axis * 0.5f; // a slow leftover spin, well under the release speed
        Step(magnets, snaps, 300); // 3 s to decay into a detent

        // hinge.angle is NaN until PhysX steps the joint; after 3 s of spinning it must be real —
        // and NaN would otherwise slip through the comparisons below (NaN > x is false).
        if (float.IsNaN(hinge.angle))
        {
            failures.Add($"detent: roller '{snap.name}' hinge angle is still NaN after simulation");
            return;
        }
        float nearest = Mathf.Round(hinge.angle / 120f) * 120f;
        float errorDeg = Mathf.Abs(Mathf.DeltaAngle(hinge.angle, nearest));
        float axisSpeed = Mathf.Abs(Vector3.Dot(rb.angularVelocity, axis));
        if (errorDeg > MaxDetentErrorDeg)
            failures.Add($"detent: roller '{snap.name}' settled {errorDeg:0.#} deg off a 120-deg face (max {MaxDetentErrorDeg})");
        if (axisSpeed > MaxDetentRestSpeed)
            failures.Add($"detent: roller '{snap.name}' is still spinning at {axisSpeed:0.##} rad/s after 3 s");
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
