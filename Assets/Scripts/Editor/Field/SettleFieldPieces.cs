using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// The field is authored not-quite-at-rest, and the player pays for it in the first two seconds of
// every match.
//
// Measured on SampleScene before this tool existed: 77 loose pieces, 53 of them starting INSIDE
// something, and all sixteen alliance pins sunk 2.7 mm into the ground box and lying 27.8 degrees
// off where they come to rest. At t=0 PhysX has to sort all of that out at once. Nothing travels
// far — the worst piece's centre of mass moves 15 mm — but 77 compound bodies carrying 924
// colliders stay awake while it happens, and a physics step costs 5.56 ms instead of 0.20. That is
// a 28x tax on the frame, it lasts until the last body sleeps at ~1.8 s, and what the driver feels
// is not "the pieces are twitching", it is the whole robot being rough for two seconds. It reads
// as bad ground, which is why it got reported as more painted tape.
//
// The fix is to do the settling once, here, and save the result. It is not a physics change and it
// does not move the match layout: the pieces finish exactly the motion they were going to finish
// anyway, two seconds earlier and off-camera.
//
// Deliberately NOT done here: forcing the bodies to sleep at Start. That hides an unsettled field
// rather than fixing it, and the next person to nudge a piece in the scene view would get the tax
// back with no way to see why. Settle, save, and let FieldAtRestValidation fail if it drifts.
//
// Also deliberately not done: settling unconditionally. This measures first and leaves an
// already-still field alone — see Settle() for the pin that a needless re-bake pushed from 0.8 mm
// of creep to 3.6.
public static class SettleFieldPieces
{
    // This tool's idea of "at rest" must be at least as strict as FieldAtRestValidation's, or it
    // signs off on a scene the validator then rejects. It did: the first pass accepted 0.01 u/s
    // held for 0.5 s, and PinYellowYellow10 — a pin resting on a stack — went on creeping 3.6 mm
    // through the validator's 2 s window. 0.005 u/s is 0.5 mm/s, so the worst a piece can drift
    // across that window is 1 mm against a 3 mm tolerance.
    private const float RestSpeed = 0.005f;

    // Long enough for a genuine settle, short enough that a field with something falling forever
    // reports that instead of hanging. 100 Hz, so 1200 steps is 12 s.
    private const int MaxSteps = 1200;

    // How still it has to be, for how long, before we believe it. A stack can pause mid-collapse,
    // and this window is deliberately longer than the validator's 2 s so a pause cannot be mistaken
    // for a settle.
    private const int RestSteps = 250;

    // How many times to settle-and-check before giving up and saying so. Measured: the field needs
    // one pass, and the one case that needed two (a pin standing in a cup) was done after the
    // second. More than this and something is genuinely not at rest, which is a report, not a
    // reason to keep simulating.
    private const int MaxPasses = 4;

    [MenuItem("Tools/RoboSim/Field & Pieces/Settle Pieces (bake the match start)", false, 30)]
    private static void SettleOpenScene()
    {
        try { Debug.Log(Settle()); }
        catch (System.InvalidOperationException e)
        {
            EditorUtility.DisplayDialog("Settle Field Pieces", e.Message, "OK");
        }
    }

    // Batch: -executeMethod SettleFieldPieces.RunBatch (settles and saves BOTH shipped scenes).
    public static void RunBatch()
    {
        var report = new List<string>();
        foreach (string scene in new[] { RoboSimPaths.MainScene, RoboSimPaths.LiteScene })
        {
            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
            report.Add($"=== {scene} ===\n{Settle()}");
            EditorSceneManager.SaveOpenScenes();
        }
        Debug.Log(string.Join("\n\n", report));
        EditorApplication.Exit(0);
    }

    internal static string Settle()
    {
        var bodies = new List<Rigidbody>(
            Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude));
        bodies.RemoveAll(rb => rb == null || rb.isKinematic);
        if (bodies.Count == 0)
            throw new System.InvalidOperationException(
                "No non-kinematic Rigidbody in this scene — nothing to settle. Either the wrong " +
                "scene is open or the field's pieces are no longer dynamic.");

        // The centre of mass, not transform.position. These piece roots sit at their GROUP's origin
        // with every collider in a child, so a pin that merely rotates in place swings its own
        // transform through a metre-long arc. Measuring the root reported one of these pins
        // "travelling 986 mm" when its centre of mass moved 15.
        // Hand any Transform a tool has already moved to PhysX before reading the bodies — outside
        // play mode that does not happen on its own, and the "how far did this piece travel" numbers
        // below would otherwise be measured from where the piece used to be.
        Physics.SyncTransforms();

        var startCom = new List<Vector3>();
        var startLow = new List<float>();
        foreach (Rigidbody rb in bodies)
        {
            startCom.Add(rb.worldCenterOfMass);
            startLow.Add(LowestPoint(rb));
        }

        // MEASURE FIRST, and settle only if the measurement says to.
        //
        // This order is the whole point, and it was learned the expensive way. A field that is
        // already still does not need baking, and baking it anyway is not free: settling simulates
        // until every body has been slow for 2.5 s and then writes down wherever each one happened
        // to be, which for a piece that micro-slides is a pose part-way through a slide rather than
        // the end of one. Measured on the pin standing in Cup1: authored, it crept 0.8 mm over the
        // validator's window; re-baked by an unconditional settle, 3.6 mm — over budget, from a
        // pose that had been comfortably inside it. Every one of the field's sixteen pins-in-cups
        // is already under 1 mm, so on the shipped field this now does nothing at all, which is the
        // correct amount of work.
        //
        // When it DOES need to settle, one pass is not always enough — "slow right now" is not
        // "will stay put", since a body can be under the speed threshold on its way somewhere — so
        // it re-measures and goes again. The check is FieldAtRestValidation's own, so this settles
        // to exactly the standard the field is held to rather than to a private idea of rest.
        int passes = 0;
        int settledAt = -1;
        float drift = WorstDrift(bodies, out string worstPiece);
        while (drift > FieldAtRestValidation.DriftTolerance && passes < MaxPasses)
        {
            passes++;
            settledAt = SimulateToRest(bodies);
            drift = WorstDrift(bodies, out worstPiece);
        }

        if (passes == 0)
            return $"Settle Field Pieces: {bodies.Count} dynamic piece(s), ALREADY AT REST — nothing " +
                   $"was moved. Worst piece ({worstPiece}) creeps {drift * 100f:0.00} mm over the next " +
                   $"{FieldAtRestValidation.Steps * ValidationUtil.StepSeconds:0.0} s, inside the " +
                   $"{FieldAtRestValidation.DriftTolerance * 100f:0.0} mm budget.";

        var moved = new List<(float d, string line)>();
        float total = 0f;
        int over5 = 0, sunk = 0;
        for (int i = 0; i < bodies.Count; i++)
        {
            if (bodies[i] == null) continue;
            float d = Vector3.Distance(bodies[i].worldCenterOfMass, startCom[i]);
            float rose = LowestPoint(bodies[i]) - startLow[i];
            total += d;
            if (d > 0.05f) over5++;
            if (rose > 0.01f) sunk++;
            moved.Add((d, $"{bodies[i].name}: centre of mass {d * 100f:0.0} mm, " +
                          $"lifted {rose * 100f:+0.0;-0.0} mm out of the floor"));
        }
        moved.Sort((a, b) => b.d.CompareTo(a.d));

        EditorSceneManager.MarkSceneDirty(bodies[0].gameObject.scene);

        string when = settledAt < 0
            ? $"NOTHING SETTLED within {MaxSteps * ValidationUtil.StepSeconds:0.0} s — something in this field is still " +
              "moving, and baking that pose would only freeze a frame of it. Look at the fastest " +
              "piece before trusting this scene"
            : $"came to rest after {settledAt * ValidationUtil.StepSeconds:0.00} s of simulation";

        string held = drift <= FieldAtRestValidation.DriftTolerance
            ? $"holds: worst piece ({worstPiece}) creeps {drift * 100f:0.00} mm over the next " +
              $"{FieldAtRestValidation.Steps * ValidationUtil.StepSeconds:0.0} s, under the " +
              $"{FieldAtRestValidation.DriftTolerance * 100f:0.0} mm a field at rest is allowed"
            : $"DOES NOT HOLD after {passes} pass(es): {worstPiece} still creeps {drift * 100f:0.00} mm " +
              $"over the next {FieldAtRestValidation.Steps * ValidationUtil.StepSeconds:0.0} s. " +
              "Field At Rest will fail on this scene — find what that piece is balanced on";

        var lines = new List<string>
        {
            $"Settle Field Pieces: {bodies.Count} dynamic piece(s), {when} " +
            $"({passes} pass(es)).",
            $"  {over5} piece(s) moved more than 5 mm; total centre-of-mass travel " +
            $"{total * 100f:0} mm, mean {total / bodies.Count * 100f:0.0} mm.",
            $"  {sunk} piece(s) were starting below where they rest — i.e. inside the floor or a " +
            "goal — and are now on top of it.",
            $"  {held}."
        };
        for (int i = 0; i < Mathf.Min(moved.Count, 10); i++) lines.Add("    " + moved[i].line);
        if (moved.Count > 10) lines.Add($"    ...and {moved.Count - 10} more");
        return string.Join("\n", lines);
    }

    // Simulate until every body has been slower than RestSpeed for RestSteps in a row, then bake the
    // poses it reached. Returns the step it settled on, or -1 if it never did.
    private static int SimulateToRest(List<Rigidbody> bodies)
    {
        var pose = new Dictionary<Transform, (Vector3 pos, Quaternion rot)>();
        int settledAt = -1;

        SimulationMode previous = Physics.simulationMode;
        Physics.simulationMode = SimulationMode.Script;
        try
        {
            Stop(bodies);
            int still = 0;
            for (int s = 0; s < MaxSteps; s++)
            {
                Physics.Simulate(ValidationUtil.StepSeconds);

                float fastest = 0f;
                foreach (Rigidbody rb in bodies)
                    if (rb != null) fastest = Mathf.Max(fastest, rb.linearVelocity.magnitude);

                still = fastest < RestSpeed ? still + 1 : 0;
                if (still < RestSteps) continue;
                settledAt = s + 1;
                break;
            }

            // Read the poses out while the simulation still owns them.
            foreach (Rigidbody rb in bodies)
                if (rb != null) pose[rb.transform] = (rb.transform.position, rb.transform.rotation);
        }
        finally { Physics.simulationMode = previous; }

        // Write them back through Undo so the menu path is reversible, and so the scene records the
        // change as an edit rather than as whatever the simulation happened to leave behind.
        var transforms = new List<Transform>(pose.Keys);
        Undo.RecordObjects(transforms.ToArray(), "Settle Field Pieces");
        foreach (Transform t in transforms)
        {
            (Vector3 pos, Quaternion rot) p = pose[t];
            t.SetPositionAndRotation(p.pos, p.rot);
        }
        Physics.SyncTransforms();
        return settledAt;
    }

    // How far the worst piece creeps over the validator's window, starting from the poses currently
    // in the scene. Puts every pose back afterwards, so measuring costs nothing but time — this
    // asks "would the saved scene pass?", it does not get to change the answer.
    private static float WorstDrift(List<Rigidbody> bodies, out string worstPiece)
    {
        // Outside play mode a Transform written by a tool has NOT reached the body yet, and
        // worldCenterOfMass reads the body. Without this the measurement is taken from wherever the
        // piece used to be: a mutation test that lifted a pin 50 mm into the air was reported as
        // "already at rest", because the 50 mm had not been handed to PhysX and the fall it then
        // simulated looked like the piece arriving rather than leaving.
        Physics.SyncTransforms();

        var startCom = new List<Vector3>();
        var pose = new List<(Vector3 pos, Quaternion rot)>();
        foreach (Rigidbody rb in bodies)
        {
            startCom.Add(rb != null ? rb.worldCenterOfMass : Vector3.zero);
            pose.Add(rb != null
                ? (rb.transform.position, rb.transform.rotation)
                : (Vector3.zero, Quaternion.identity));
        }

        SimulationMode previous = Physics.simulationMode;
        Physics.simulationMode = SimulationMode.Script;
        try
        {
            // The validator measures from a freshly opened scene, so it starts from a dead stop.
            // Leaving the previous pass's velocities in place would measure something else.
            Stop(bodies);
            for (int i = 0; i < FieldAtRestValidation.Steps; i++)
                Physics.Simulate(ValidationUtil.StepSeconds);
        }
        finally { Physics.simulationMode = previous; }

        float worst = 0f;
        worstPiece = null;
        for (int i = 0; i < bodies.Count; i++)
        {
            if (bodies[i] == null) continue;
            float d = Vector3.Distance(bodies[i].worldCenterOfMass, startCom[i]);
            if (d <= worst && worstPiece != null) continue;
            worst = d;
            worstPiece = bodies[i].name;
        }

        for (int i = 0; i < bodies.Count; i++)
        {
            if (bodies[i] == null) continue;
            bodies[i].transform.SetPositionAndRotation(pose[i].pos, pose[i].rot);
        }
        Physics.SyncTransforms();
        Stop(bodies);
        return worst;
    }

    private static void Stop(List<Rigidbody> bodies)
    {
        foreach (Rigidbody rb in bodies)
        {
            if (rb == null) continue;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    private static float LowestPoint(Rigidbody rb)
    {
        float low = float.MaxValue;
        foreach (Collider c in rb.GetComponentsInChildren<Collider>(true))
            if (c != null && !c.isTrigger) low = Mathf.Min(low, c.bounds.min.y);
        return low == float.MaxValue ? 0f : low;
    }
}
