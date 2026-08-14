using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Headless check that a goal is a SOLID OBSTACLE and not a shell with holes in it.
//
// THE BUG THIS EXISTS FOR: driving a robot at a goal put the robot INSIDE the goal, with no way back
// out. A goal's collision is flat BoxCollider panels laid on the edges of an octagon, and eight flat
// panels on an octagon's edges DO NOT MEET at the vertices — every ring on every shipped goal had a
// ~0.125-unit gap at all eight corners, full height, plus panels only 0.01 units thick to be pushed
// back out of. See SealGoalShell for the whole diagnosis.
//
// Nothing about that was visible: the colliders have no renderer, the goal you look at is a separate
// mesh, and a gap in a corner is not something anyone spots in the Scene view. So it is checked here.
//
// Two parts, and the first exists so the second cannot pass vacuously:
//   1. A synthetic ring built to the SHIPPED goal's own radii and widths, at the OLD sizes, must come
//      out with all eight corners OPEN — then the same ring, run through the real repair, must come out
//      closed. That proves the gap test can fail, and that the spec is what closes this specific gap.
//   2. Every ring on every goal in the real field scenes is closed and at spec.
//
// Overlap is asked of PhysX itself (Physics.ComputePenetration), not re-derived here — the question is
// literally "would the solver see these two panels as one wall", and a hand-rolled answer to that could
// be wrong in exactly the way the shipped field was.
//
// If part 2 fails, run Tools > RoboSim > Field & Pieces > Seal Goal Shells (Scene Fix).
//
// Usage: Tools > RoboSim > Validate > Validate Goal Shell, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod GoalShellValidation.RunBatchValidate
public static class GoalShellValidation
{
    // The shipped goals' outer bumper ring, measured off SampleScene: eight panels, alternating
    // cardinal/diagonal radius and width. Reproducing the REAL numbers is the point — a fixture with
    // invented ones could be gappy or closed by luck and would prove nothing about the field.
    private const float CardinalRadius = 0.45f;
    private const float DiagonalRadius = 0.62f;
    private const float CardinalWidth = 0.5f;
    private const float DiagonalWidth = 0.3f;
    private const float PanelHeight = 0.75f;

    // The Neutral/Central stakes: a well-formed octagon (diagonal/cardinal = 1.11, against a regular
    // octagon's 1.08), and the ring a robot spends the match driving into. FieldSetupTools' own
    // numbers.
    private const float NeutralCardinalRadius = 0.705f;
    private const float NeutralDiagonalRadius = 0.785f;
    private const float NeutralCardinalWidth = 0.8f;
    private const float NeutralDiagonalWidth = 0.4f;
    private const float OldThickness = GoalShellSpec.LegacyRingThickness;

    [MenuItem("Tools/RoboSim/Validate/Validate Goal Shell", false, 43)]
    private static void RunInteractive()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive("Validate Goal Shell", Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Validate Goal Shell", Run);

    private static string Run()
    {
        string previousScenePath = SceneManager.GetActiveScene().path;
        int rings;
        int scenes;
        int panels;
        int checks;
        try
        {
            checks = TheSealIsWhatClosesARing();
            rings = EveryShippedGoalRingIsClosed(out scenes, out panels);
        }
        finally
        {
            // Fixture scenes are throwaway; always put the user back where they were.
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
        }

        return $"Validate Goal Shell: PASSED ({checks + rings} checks) — the old panel sizes really do " +
               $"leave every octagon corner open, sealing really does close them, and all {rings} rings " +
               $"({panels} panels) across {scenes} field scene(s) are closed and at spec.";
    }

    // --- 1. The diagnosis, and the repair, as a check -----------------------------------------------

    private static int TheSealIsWhatClosesARing()
    {
        // The bug, reproduced from the shipped goal's own radii and widths. If this ever comes back zero
        // the fixture has stopped modelling the real goal, and every "the ring is closed" check below
        // would be passing against nothing.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        ValidationUtil.Assert(OpenCorners(BuildRings(OldThickness)) == 8,
            "TAUTOLOGY GUARD: a ring built to the shipped goal's own radii and widths, at the OLD 0.01 " +
            "thickness, must come out with all EIGHT corners open — that IS the bug, and if this fixture " +
            "cannot reproduce it then the closed-ring checks below prove nothing");

        // Thickness alone does NOT fix it, which is why the repair also widens. Worth pinning, because
        // "make the walls thicker" is the obvious fix and it leaves all eight holes exactly where they were.
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        ValidationUtil.Assert(OpenCorners(BuildRings(GoalShellSpec.RingThickness)) == 8,
            "thickening the panels alone must NOT close the corners — the gap runs ALONG the ring, not " +
            "across it, so a fix that only thickened would ship with all eight holes still in it");

        // The real repair, driven through the real entry point rather than a copy of its arithmetic.
        Scene fixtureScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        List<BoxCollider> ring = BuildRings(OldThickness);
        float[] outerFaceBefore = OuterFaceRadii(ring);
        int sealedPanels = SealGoalShell.SealOpenScene(fixtureScene, out _, out int skipped);
        ValidationUtil.Assert(skipped == 0, "the fixture's panels must be the shape the repair understands");
        // At least one change per panel across both rings. Not an equality: the repair now counts
        // two kinds of change — bringing a panel to thickness/offset spec, and fitting its width to
        // its neighbours — so a panel needing both is counted twice. The point of the check is that
        // it did not silently match NOTHING (which would leave everything below testing the
        // fixture's own construction) or match only the outer ring (which would leave the lower base
        // rim open on the real field).
        ValidationUtil.Assert(sealedPanels >= 16,
            $"the repair only made {sealedPanels} changes across 16 fixture panels in two rings — it has " +
            "matched nothing, or matched only one of the two rings");
        ValidationUtil.Assert(OpenCorners(ring) == 0,
            "...and after sealing, NO corner may be open. That is the whole fix: a robot has to meet one " +
            "continuous wall, not eight panels with slots between them");

        // ...and the sealing must not have MOVED the wall while it was closing it. This is the check
        // the first pass of this work did not have, and its absence is how the shell ended up
        // standing 0.045 units proud of the goal you can see: a BoxCollider grows about its centre,
        // the generator puts that centre on the tuned radius, so thickening alone pushes the face a
        // robot touches outward by half the increase. Asserted per panel and at full precision,
        // because the whole claim of this repair is that it changes collision behaviour and nothing
        // else — 4.5 mm on a 0.705-unit stake is 6% of its radius.
        float[] outerFaceAfter = OuterFaceRadii(ring);
        for (int i = 0; i < ring.Count; i++)
            ValidationUtil.Near(outerFaceAfter[i], outerFaceBefore[i], GoalShellSpec.Epsilon,
                $"'{ring[i].name}': sealing moved the panel's OUTER face, so the collision shell no " +
                "longer sits where the goal's visual surface does. The box must be offset inward by " +
                "half the added thickness — see GoalShellSpec.RingPanelCenter");

        // Idempotence, because this gets re-run every time anyone regenerates a goal — and it RESIZES.
        int secondPass = SealGoalShell.SealOpenScene(fixtureScene, out int alreadyOk, out _);
        ValidationUtil.Assert(secondPass == 0 && alreadyOk == 16,
            $"re-running the repair must be a no-op, but the second pass made {secondPass} change(s) and " +
            $"found {alreadyOk} of 16 panels already at spec (first pass widths: " +
            $"{string.Join(", ", ring.ConvertAll(b => b.size.x.ToString("0.00000")))}). It resizes panels, " +
            "so a second pass that 'fixed' them again would change every goal a little each time anyone ran it");

        // AND NO PANEL MAY STAND OUTSIDE THE CORNER IT MEETS. This is the check the blanket +0.2
        // width bonus needed and never had: it closed every corner, and it closed them by leaving
        // each panel end 0.093 units past the seam on the shipped Neutral goals — a collision shell
        // wider than the goal you can see, at all eight corners, which is what a driver hits.
        //
        // Closed AND flush are different properties, and a future "just add a bit more width" must
        // not be able to pass by trading one for the other. Checked on a NEUTRAL-radius ring: that is
        // the well-formed octagon the stakes actually use, and the shape the fit is meant for. The
        // Alliance fixture above deliberately is NOT flush — see MinPanelWidth.
        Scene neutralScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        List<BoxCollider> neutral = BuildRings(OldThickness, 0f,
            NeutralCardinalRadius, NeutralDiagonalRadius, NeutralCardinalWidth, NeutralDiagonalWidth);
        ValidationUtil.Assert(OpenCorners(neutral) == 8,
            "TAUTOLOGY GUARD: the Neutral-radius fixture must start with all eight corners open too, " +
            "or the flush check below is measuring a ring that never needed fitting");
        SealGoalShell.SealOpenScene(neutralScene, out _, out int neutralSkipped);
        ValidationUtil.Assert(neutralSkipped == 0,
            "the Neutral fixture's panels must be the shape the repair understands");
        ValidationUtil.Assert(OpenCorners(neutral) == 0, "the Neutral fixture's corners must close");

        for (int i = 0; i < neutral.Count; i++)
        {
            BoxCollider left = neutral[(i - 1 + neutral.Count) % neutral.Count];
            BoxCollider right = neutral[(i + 1) % neutral.Count];
            float overhang = Mathf.Max(GoalShellSpec.CornerOverhang(neutral[i], left),
                                       GoalShellSpec.CornerOverhang(neutral[i], right));
            ValidationUtil.Assert(overhang <= GoalShellSpec.CornerSeal + GoalShellSpec.Epsilon,
                $"'{neutral[i].name}' ends {overhang:0.000} units OUTSIDE its neighbour's outer face " +
                $"(limit {GoalShellSpec.CornerSeal}). The corner is sealed, but the seal is sticking out " +
                "past the goal's own silhouette — fit each panel to its neighbours' planes instead of " +
                "adding a constant to every width. See GoalShellSpec.FitRingWidths.");
        }

        // The half-sealed state specifically: thick, but never offset inward. That is what the
        // shipped scenes were in, and the repair has to recognise it as WORK TO DO rather than as
        // "already at spec".
        Scene halfScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        List<BoxCollider> halfSealed = BuildRings(GoalShellSpec.RingThickness);
        float[] halfFaceBefore = OuterFaceRadii(halfSealed);
        int repaired = SealGoalShell.SealOpenScene(halfScene, out _, out _);
        ValidationUtil.Assert(repaired >= 16,
            "a panel that is already THICK but has never been offset inward must still be repaired — " +
            "that is exactly the state the shipped field was left in, and treating it as sealed is how " +
            "it stayed that way");
        ValidationUtil.Assert(OuterFaceRadii(halfSealed)[0] < halfFaceBefore[0] - 0.04f,
            "...and it must actually pull the outer face back in, by half the thickness it had gained");

        // 17 as before, + 3 for the Neutral fixture's guard/skip/closed checks, + one flush check per
        // panel in it.
        return 17 + 3 + 8;
    }

    // Each panel's outward face distance from the ring's axis. Measured through the panel's own
    // rotation and box centre rather than assumed, because "which way is out" is precisely what the
    // offset depends on: the generator steps each wall out along its LOCAL +Y, which is what makes
    // +Y the outward normal (verified on the shipped scene at 1.0000 for all eight).
    private static float[] OuterFaceRadii(List<BoxCollider> ring)
    {
        var radii = new float[ring.Count];
        for (int i = 0; i < ring.Count; i++)
        {
            Transform t = ring[i].transform;
            Vector3 face = t.TransformPoint(ring[i].center + Vector3.up * (ring[i].size.y * 0.5f));
            // The fixture's rings are centred on the world origin in X/Y; Z is the ring's height.
            radii[i] = new Vector2(face.x, face.y).magnitude;
        }
        return radii;
    }

    // --- 2. The real field ---------------------------------------------------------------------------

    private static int EveryShippedGoalRingIsClosed(out int scenesChecked, out int panelsChecked)
    {
        scenesChecked = 0;
        panelsChecked = 0;
        int ringsChecked = 0;
        var failures = new List<string>();

        foreach (string scenePath in new[] { RoboSimPaths.MainScene, RoboSimPaths.LiteScene })
        {
            if (!System.IO.File.Exists(scenePath)) continue;
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            scenesChecked++;

            foreach (KeyValuePair<string, List<BoxCollider>> ring in RingsIn(scene))
            {
                ringsChecked++;
                panelsChecked += ring.Value.Count;

                foreach (BoxCollider panel in ring.Value)
                {
                    if (panel.size.y < GoalShellSpec.RingThickness - GoalShellSpec.Epsilon)
                        failures.Add($"{ring.Key}: '{panel.name}' is only {panel.size.y} thick — a robot " +
                                     "part that penetrates one this thin can be pushed back out the wrong side");
                    else if (!GoalShellSpec.IsPanelAtSpec(panel))
                        failures.Add($"{ring.Key}: '{panel.name}' is thick enough but its box centre is " +
                                     $"{panel.center} instead of {GoalShellSpec.RingPanelCenter} — all the " +
                                     "added thickness went outward, so the collision shell stands proud of " +
                                     "the goal's visual surface and the robot stops short of touching it");
                }

                int open = OpenCorners(ring.Value);
                if (open > 0)
                    failures.Add($"{ring.Key}: {open} of {ring.Value.Count} corners are OPEN — a robot can " +
                                 "slip through the gap and get stuck inside the goal");
            }
        }

        ValidationUtil.Assert(scenesChecked > 0, "no field scene found to check");
        ValidationUtil.Assert(ringsChecked > 0,
            "no goal ring found in any field scene — the check must have stopped matching the generated " +
            "wall names, in which case it is passing by finding nothing");
        ValidationUtil.Assert(failures.Count == 0,
            "goal collision shells are not sealed. Run Tools > RoboSim > Field & Pieces > Seal Goal " +
            "Shells (Scene Fix).\n  - " + string.Join("\n  - ", failures));

        return ringsChecked;
    }

    // --- Geometry -----------------------------------------------------------------------------------

    // How many neighbouring panel pairs in a ring do NOT overlap. Asked of PhysX, because "does the
    // solver see one wall here" is exactly the question, and re-deriving it risks being wrong the same
    // way the field was.
    private static int OpenCorners(List<BoxCollider> ring)
    {
        int open = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            BoxCollider a = ring[i];
            BoxCollider b = ring[(i + 1) % ring.Count];
            bool touching = Physics.ComputePenetration(
                a, a.transform.position, a.transform.rotation,
                b, b.transform.position, b.transform.rotation,
                out _, out _);
            if (!touching) open++;
        }
        return open;
    }

    // Every generated ring in the scene, grouped by the goal it belongs to and which ring it is, ordered
    // around the ring by the index the generator put in the name. Only the two shells a ROBOT can hit —
    // the inner pocket is a piece-only surface and is left thin on purpose.
    private static Dictionary<string, List<BoxCollider>> RingsIn(Scene scene)
    {
        var rings = new Dictionary<string, List<BoxCollider>>();
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!GoalShellSpec.IsRingWall(t.name)) continue;
                BoxCollider box = t.GetComponent<BoxCollider>();
                if (box == null) continue;

                int underscore = t.name.LastIndexOf('_');
                string prefix = underscore > 0 ? t.name.Substring(0, underscore) : t.name;
                string goal = t.parent != null ? t.parent.name : "<no parent>";
                string key = $"{goal}/{prefix}";
                if (!rings.TryGetValue(key, out List<BoxCollider> ring)) rings[key] = ring = new List<BoxCollider>();
                ring.Add(box);
            }
        }
        foreach (List<BoxCollider> ring in rings.Values) ring.Sort((a, b) => IndexOf(a.name).CompareTo(IndexOf(b.name)));
        return rings;
    }

    private static int IndexOf(string wallName)
    {
        int underscore = wallName.LastIndexOf('_');
        return underscore >= 0 && int.TryParse(wallName.Substring(underscore + 1), out int i) ? i : 0;
    }

    // --- Fixture ------------------------------------------------------------------------------------

    // Both generated ring shells, built the way FieldSetupTools builds them — rotate about the local Z by
    // i*45 degrees, then step out along the local +Y by that vertex's radius — and named the way it names
    // them, so the real repair recognises them. BOTH rings exist because the repair handles both, and a
    // fixture with only one would not notice it silently skipping the lower base rim on the real field.
    // The outer ring is what comes back, and it is the one the corner checks are made against.
    // `extraWidth` reproduces a HALF-sealed panel: one an earlier pass already thickened and widened
    // but never offset inward. Defaults to 0, which is the original pre-repair goal.
    private static List<BoxCollider> BuildRings(float thickness, float extraWidth = 0f)
        => BuildRings(thickness, extraWidth, CardinalRadius, DiagonalRadius, CardinalWidth, DiagonalWidth);

    // Radii and widths are parameters because the two goal families are geometrically different
    // animals, and the corner fit behaves differently on each. The Alliance numbers (0.45 / 0.62)
    // put the diagonal faces 1.38x the cardinal radius where a regular octagon is 1.08, so the
    // cardinal planes nearly meet and the diagonals fit down to slivers; the Neutral numbers
    // (0.705 / 0.785) are a well-formed octagon where every panel fits cleanly. Testing only the
    // first would leave the ordinary case unproven, and only the second would miss the edge case
    // that the min-width tripwire exists for.
    private static List<BoxCollider> BuildRings(float thickness, float extraWidth,
        float cardinalRadius, float diagonalRadius, float cardinalWidth, float diagonalWidth)
    {
        List<BoxCollider> outer = null;
        float ringZ = 0f;
        foreach (string prefix in new[] { "GoalWall_Outer_Octagon", "GoalWall_Lower_Base_Octagon" })
        {
            var ring = new List<BoxCollider>();
            for (int i = 0; i < 8; i++)
            {
                bool cardinal = i % 2 == 0;
                GameObject wall = new GameObject($"{prefix}_{i}");
                wall.transform.position = new Vector3(0f, 0f, ringZ);   // the two rings sit at different heights
                wall.transform.rotation = Quaternion.Euler(0f, 0f, i * 45f);
                wall.transform.Translate(0f, cardinal ? cardinalRadius : diagonalRadius, 0f, Space.Self);

                BoxCollider box = wall.AddComponent<BoxCollider>();
                box.size = new Vector3((cardinal ? cardinalWidth : diagonalWidth) + extraWidth,
                    thickness, PanelHeight);
                ring.Add(box);
            }
            outer ??= ring;
            ringZ -= PanelHeight;
        }
        return outer;
    }
}
