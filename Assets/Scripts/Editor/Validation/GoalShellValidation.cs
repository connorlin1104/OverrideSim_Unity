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
// Usage: Tools > RoboSim > Validation > Validate Goal Shell, or headless
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
    private const float OldThickness = 0.01f;

    [MenuItem("Tools/RoboSim/Validation/Validate Goal Shell", false, 17)]
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
        int sealedPanels = SealGoalShell.SealOpenScene(fixtureScene, out _, out int skipped);
        ValidationUtil.Assert(skipped == 0, "the fixture's panels must be the shape the repair understands");
        ValidationUtil.Assert(sealedPanels == 16,
            "the repair must have actually touched all 16 panels of both fixture rings — one that silently " +
            "matched nothing would leave the check below testing the fixture's own construction, and one " +
            "that matched only the outer ring would leave the lower base rim open on the real field");
        ValidationUtil.Assert(OpenCorners(ring) == 0,
            "...and after sealing, NO corner may be open. That is the whole fix: a robot has to meet one " +
            "continuous wall, not eight panels with slots between them");

        // Idempotence, because this gets re-run every time anyone regenerates a goal — and it WIDENS.
        int secondPass = SealGoalShell.SealOpenScene(fixtureScene, out int alreadyOk, out _);
        ValidationUtil.Assert(secondPass == 0 && alreadyOk == 16,
            "re-running the repair must be a no-op. It widens panels, so a second pass that 'fixed' them " +
            "again would grow every goal a little each time anyone ran the tool");

        return 6;
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
    private static List<BoxCollider> BuildRings(float thickness)
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
                wall.transform.Translate(0f, cardinal ? CardinalRadius : DiagonalRadius, 0f, Space.Self);

                BoxCollider box = wall.AddComponent<BoxCollider>();
                box.size = new Vector3(cardinal ? CardinalWidth : DiagonalWidth, thickness, PanelHeight);
                ring.Add(box);
            }
            outer ??= ring;
            ringZ -= PanelHeight;
        }
        return outer;
    }
}
