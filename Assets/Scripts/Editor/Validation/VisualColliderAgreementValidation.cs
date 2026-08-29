using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// Headless check that every robot's visuals draw where its colliders say they are.
//
// THE BUG THIS EXISTS FOR: Darwinbot's parts drew far from their (correct) collider wireframes. A
// Fusion export places a part with its VERTICES — the mesh leaf sits at ~identity — so nothing in the
// transform hierarchy can move a visual; what can is the MeshFilter resolving to a different
// Body1.NNN sub-mesh of the same FBX after a re-import. The collider does not follow (a same-GO box
// is a serialized snapshot of mesh.bounds at generation time), so the mismatch is measurable — and
// nothing measured it. ModelStore's census fingerprints a model at stow time, so a scramble that
// predates the stow is invisible to it, and no validator compared a visual with its collider.
//
// Two parts, and the first exists so the second cannot pass vacuously:
//   1. A fixture built the way Darwinbot is built — identity leaf, placement in the vertices, box
//      snapshotted from the RIGHT mesh, MeshFilter bound to a WRONG copy in the same file — must
//      diagnose MOVED (a scaled copy RESHAPED, the right mesh OK), the repair must re-bind it to the
//      right mesh, and it must then diagnose OK. A second fixture with TWO coincident right meshes
//      must not be guessed between: the mesh stays, the part is shifted onto its collider.
//      A third fixture holds only the translated copy — the original is gone, as on Darwinbot — and
//      the repair must SHIFT the leaf onto its collider without moving the collider in the world; a
//      fourth makes the leaf a joint link and must be refused as MANUAL.
//   2. Every robot prefab has zero MOVED/RESHAPED parts. Never repairs or saves anything; a failing
//      line says what Realign Visuals To Colliders would do about it.
//
// Usage: Tools > RoboSim > Validate > Validate Visual Collider Agreement, or headless
//   Unity -batchmode -quit -projectPath . -executeMethod VisualColliderAgreementValidation.RunBatchValidate
public static class VisualColliderAgreementValidation
{
    private const string Title = "Validate Visual Collider Agreement";

    // Under the hull folder because it is the one folder of generated collider assets the repo already
    // has. Both files are deleted in finally, pass or throw.
    private const string HullFolder = "Assets/RobotColliders";
    private const string FixturePath = HullFolder + "/_VisualColliderFixture.asset";
    private const string AmbiguousFixturePath = HullFolder + "/_VisualColliderFixtureAmbiguous.asset";
    private const string ShiftFixturePath = HullFolder + "/_VisualColliderFixtureShift.asset";

    // Three different sides so an axis mix-up in the signature shows; a shift larger than any side so
    // the wrong copy is unmistakably a different place and not a tolerance question.
    private static readonly Vector3 PartSize = new Vector3(1f, 0.5f, 2f);
    private static readonly Vector3 Shift = new Vector3(3f, 0f, 0f);

    [MenuItem("Tools/RoboSim/Validate/Validate Visual Collider Agreement", false, 55)]
    private static void RunInteractive()
    {
        // The fixture is built in a fresh scene, so whatever is open would be thrown away unasked.
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        ValidationUtil.RunInteractive(Title, Run);
    }

    public static void RunBatchValidate() => ValidationUtil.RunBatch(Title, Run);

    private static string Run()
    {
        string previousScenePath = SceneManager.GetActiveScene().path;
        string fixture;
        string sweep;
        try
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            fixture = TheRepairIsWhatRealignsAPart();
            sweep = EveryRobotDrawsWhereItsCollidersAre();
        }
        finally
        {
            // The fixture scene is throwaway; always put the user back where they were.
            if (!string.IsNullOrEmpty(previousScenePath))
                EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);
        }
        return $"{Title}: PASSED\n\n{fixture}\n{sweep}";
    }

    // --- 1. The diagnosis, and the repair, as a check -----------------------------------------------

    private static string TheRepairIsWhatRealignsAPart()
    {
        bool hadFolder = AssetDatabase.IsValidFolder(HullFolder);
        if (!hadFolder) AssetDatabase.CreateFolder("Assets", "RobotColliders");
        GameObject root = null;
        try
        {
            // One file, three meshes: RIGHT is what the box was generated from, WRONG is the same box
            // translated (a re-import pointing the MeshFilter at a sibling copy of the part — the
            // Darwinbot case), BIGGER is a scaled copy at the same centre. Sub-assets of ONE file,
            // because the repair only ever searches the file the current mesh came from.
            Mesh[] meshes = CreateFixtureFile(FixturePath,
                BoxMesh("Right", Vector3.zero, PartSize),
                BoxMesh("Wrong", Shift, PartSize),
                BoxMesh("Bigger", Vector3.zero, PartSize * 1.5f));
            Mesh right = meshes[0], wrong = meshes[1], bigger = meshes[2];
            root = BuildRobot("VisualColliderFixtureBot", bound: wrong, boxFrom: right, out MeshFilter mf);

            // Diagnosis. MOVED here is the tautology guard for the whole validator: the fixture
            // reproduces the symptom, so a classifier that stopped seeing it goes red HERE and not as
            // a clean sweep over robots it can no longer judge.
            RealignVisualsToColliders.PartRow row = SingleRow(root);
            ValidationUtil.Assert(row.evidence == RealignVisualsToColliders.Evidence.Box,
                $"TAUTOLOGY GUARD: a BoxCollider on the mesh's own GameObject must be read as box evidence, got '{row.evidence}'");
            ValidationUtil.Assert(row.cls == RealignVisualsToColliders.PartClass.Moved,
                "TAUTOLOGY GUARD: a MeshFilter bound to a translated copy of the part its box was generated from " +
                $"must diagnose MOVED — that IS the Darwinbot symptom — got '{row.cls}'");
            ValidationUtil.Near(row.centreDelta, Shift.magnitude, 1e-3f,
                "the reported |Δcentre| must be the translation between the two copies");

            mf.sharedMesh = bigger;
            row = SingleRow(root);
            ValidationUtil.Assert(row.cls == RealignVisualsToColliders.PartClass.Reshaped,
                $"a copy with a different bounds SIZE at the same centre must diagnose RESHAPED, got '{row.cls}'");

            mf.sharedMesh = right;
            row = SingleRow(root);
            ValidationUtil.Assert(row.cls == RealignVisualsToColliders.PartClass.Ok,
                $"the mesh the box was generated from must diagnose OK, got '{row.cls}' — a classifier that " +
                "flags everything would have passed the two checks above for the wrong reason");

            // The repair, from both wrong bindings. It searches the file, and Right is the only mesh in
            // it with the box's bounds.
            foreach (Mesh start in new[] { wrong, bigger })
            {
                mf.sharedMesh = start;
                RealignVisualsToColliders.RobotReport report = RealignVisualsToColliders.Repair(root, useUndo: false);
                ValidationUtil.Assert(report.reboundCount == 1 && report.ambiguousCount == 0 && report.unmatchedCount == 0,
                    $"repair from '{start.name}' must re-bind exactly one MeshFilter — {report.SummaryLine()}");
                ValidationUtil.Assert(mf.sharedMesh == right,
                    $"repair from '{start.name}' must re-bind to '{right.name}', the one mesh in the file with the " +
                    $"box's bounds; the MeshFilter now holds '{(mf.sharedMesh != null ? mf.sharedMesh.name : "null")}'");
                ValidationUtil.Assert(report.remaining.Count == 0,
                    $"the re-diagnosis after repair from '{start.name}' must find nothing left, found {report.remaining.Count}");
                row = SingleRow(root);
                ValidationUtil.Assert(row.cls == RealignVisualsToColliders.PartClass.Ok,
                    $"a fresh diagnosis after repair from '{start.name}' must read OK, got '{row.cls}'");
            }
            Object.DestroyImmediate(root);
            root = null;

            // Ambiguity: two coincident copies of the right part (an omni's two halves). Several
            // candidates must never be guessed between — a guess draws the wrong half with no way to
            // tell — so the repair keeps the mesh the part HAS and shifts it onto its collider instead
            // (a translation is right for whichever copy it is; Darwinbot's four shafts were this).
            Mesh[] twins = CreateFixtureFile(AmbiguousFixturePath,
                BoxMesh("RightA", Vector3.zero, PartSize),
                BoxMesh("RightB", Vector3.zero, PartSize),
                BoxMesh("Wrong", Shift, PartSize));
            root = BuildRobot("VisualColliderAmbiguousBot", bound: twins[2], boxFrom: twins[0], out mf);
            Vector3 ambiguousLeafBefore = mf.transform.localPosition;
            RealignVisualsToColliders.RobotReport ambiguous = RealignVisualsToColliders.Repair(root, useUndo: false);
            ValidationUtil.Assert(ambiguous.reboundCount == 0 && ambiguous.ambiguousCount == 0 && ambiguous.shiftedCount == 1,
                $"two same-bounds candidates must re-bind NOTHING and shift the part instead — {ambiguous.SummaryLine()}");
            ValidationUtil.Assert(mf.sharedMesh == twins[2],
                "a part with several candidates must keep the mesh it had (no guessing); the MeshFilter now holds " +
                $"'{(mf.sharedMesh != null ? mf.sharedMesh.name : "null")}'");
            ValidationUtil.Near((mf.transform.localPosition - ambiguousLeafBefore + Shift).magnitude, 0f, 1e-4f,
                "the ambiguous part must have been shifted onto its collider by −Shift");
            ValidationUtil.Assert(ambiguous.remaining.Count == 0,
                $"after the shift nothing must remain, got {ambiguous.remaining.Count}");
            ValidationUtil.Assert(ambiguous.rows[0].outcomeNote != null && ambiguous.rows[0].outcomeNote.Contains("candidate"),
                "the table must say the shift was chosen over an ambiguous re-bind");

            Object.DestroyImmediate(root);
            root = null;

            // The Darwinbot case proper: the mesh the box was generated from is NOT in the file any
            // more (the geometry changed under the collider — another export), only the translated
            // one. Nothing to re-bind to, so the repair must SHIFT: move the leaf by Δ = c_b − c_m and
            // put the box back where it was in the world.
            Mesh[] changed = CreateFixtureFile(ShiftFixturePath,
                BoxMesh("Wrong", Shift, PartSize),
                BoxMesh("Bigger", Vector3.zero, PartSize * 1.5f));
            Mesh template = BoxMesh("RightTemplate", Vector3.zero, PartSize);   // never saved: the file has no mesh with its bounds
            root = BuildRobot("VisualColliderShiftBot", bound: changed[0], boxFrom: template, out mf);
            Transform leaf = mf.transform;
            BoxCollider box = leaf.GetComponent<BoxCollider>();
            Vector3 colliderWorldBefore = leaf.TransformPoint(box.center);
            Vector3 leafLocalBefore = leaf.localPosition;
            RealignVisualsToColliders.RobotReport shifted = RealignVisualsToColliders.Repair(root, useUndo: false);
            ValidationUtil.Assert(shifted.shiftedCount == 1 && shifted.reboundCount == 0 && shifted.unmatchedCount == 0 && shifted.manualCount == 0,
                $"a MOVED part with no candidate in its file must be SHIFTED, nothing else — {shifted.SummaryLine()}");
            ValidationUtil.Near((leaf.localPosition - leafLocalBefore + Shift).magnitude, 0f, 1e-4f,
                "the leaf must move by Δ = (box centre − mesh centre) = −Shift in its own frame");
            ValidationUtil.Near((box.center - mf.sharedMesh.bounds.center).magnitude, 0f, 1e-4f,
                "after the shift the box must be the snapshot of the CURRENT mesh (centre = mesh.bounds.center)");
            ValidationUtil.Near((leaf.TransformPoint(box.center) - colliderWorldBefore).magnitude, 0f, 1e-4f,
                "the collider's WORLD centre must not move — the shift is the visual catching up, not the collider leaving");
            ValidationUtil.Near((leaf.TransformPoint(mf.sharedMesh.bounds.center) - colliderWorldBefore).magnitude, 0f, 1e-4f,
                "the visual's world centre must now sit exactly on the collider's");
            ValidationUtil.Assert(shifted.remaining.Count == 0,
                $"the re-diagnosis after a shift must find nothing left, found {shifted.remaining.Count}");
            row = SingleRow(root);
            ValidationUtil.Assert(row.cls == RealignVisualsToColliders.PartClass.Ok,
                $"a fresh diagnosis after the shift must read OK, got '{row.cls}'");
            Object.DestroyImmediate(root);
            root = null;

            // Refused: a leaf that is a joint link. Its transform is its joint frame — shifting it would
            // move the joint — so the tool must say MANUAL and touch nothing.
            root = BuildRobot("VisualColliderLinkBot", bound: changed[0], boxFrom: template, out mf);
            root.AddComponent<ArticulationBody>();
            mf.gameObject.AddComponent<ArticulationBody>();
            Vector3 linkLocalBefore = mf.transform.localPosition;
            RealignVisualsToColliders.RobotReport manual = RealignVisualsToColliders.Repair(root, useUndo: false);
            ValidationUtil.Assert(manual.manualCount == 1 && manual.shiftedCount == 0,
                $"a MOVED part that is a joint link must be reported MANUAL and not shifted — {manual.SummaryLine()}");
            ValidationUtil.Near((mf.transform.localPosition - linkLocalBefore).magnitude, 0f, 1e-6f,
                "a MANUAL part must be left exactly where it was");
            ValidationUtil.Assert(manual.remaining.Count == 1,
                $"a MANUAL part must still be reported as remaining, got {manual.remaining.Count}");

            return "Fixture: a translated copy diagnoses MOVED, a scaled one RESHAPED, the original OK; the repair " +
                   "re-binds both to the original and re-diagnoses OK; two coincident originals are never guessed between (shifted instead); " +
                   "a translated part whose original is gone is SHIFTED onto its collider (collider world centre unmoved); " +
                   "a joint link is refused as MANUAL.";
        }
        finally
        {
            if (root != null) Object.DestroyImmediate(root);
            if (File.Exists(FixturePath)) AssetDatabase.DeleteAsset(FixturePath);
            if (File.Exists(AmbiguousFixturePath)) AssetDatabase.DeleteAsset(AmbiguousFixturePath);
            if (File.Exists(ShiftFixturePath)) AssetDatabase.DeleteAsset(ShiftFixturePath);
            if (!hadFolder) AssetDatabase.DeleteAsset(HullFolder);
        }
    }

    // One .asset holding every mesh as a sub-asset, read BACK from disk: the repair searches the file
    // the current mesh came from, so the meshes handed to the fixture must be the ones that search sees.
    private static Mesh[] CreateFixtureFile(string path, params Mesh[] meshes)
    {
        // Names first: the import may hand back fresh objects, and the in-memory ones are not touched after it.
        string[] names = meshes.Select(m => m.name).ToArray();
        if (File.Exists(path)) AssetDatabase.DeleteAsset(path); // a run that died before its finally
        AssetDatabase.CreateAsset(meshes[0], path);
        for (int i = 1; i < meshes.Length; i++) AssetDatabase.AddObjectToAsset(meshes[i], meshes[0]);
        AssetDatabase.ImportAsset(path);

        List<Mesh> loaded = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToList();
        ValidationUtil.Assert(loaded.Count == names.Length,
            $"{path} must hold {names.Length} meshes after import and holds {loaded.Count} — the sub-asset route " +
            "(AddObjectToAsset + ImportAsset) is not producing the pool the repair searches, so nothing below " +
            "would be testing the repair");
        var result = new Mesh[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            // The MAIN object of a .asset file takes the file's name on import; only the sub-assets
            // keep theirs. The first mesh is the main asset, so it is found by role, not by name.
            result[i] = i == 0 ? loaded.Find(m => AssetDatabase.IsMainAsset(m)) : loaded.Find(m => m.name == names[i]);
            ValidationUtil.Assert(result[i] != null, $"'{names[i]}' did not come back from {path}");
            ValidationUtil.Assert(AssetDatabase.GetAssetPath(result[i]) == path,
                $"'{names[i]}' must report {path} as its asset path — that path is the repair's search key");
        }
        return result;
    }

    // A 24-vertex box (four per face, as an exported cube is), with its placement in the vertices.
    private static Mesh BoxMesh(string name, Vector3 centre, Vector3 size)
    {
        Vector3 e = size * 0.5f;
        var verts = new List<Vector3>(24);
        var tris = new List<int>(36);
        Vector3[] normals = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
        foreach (Vector3 n in normals)
        {
            Vector3 u = Mathf.Abs(n.x) > 0.5f ? Vector3.up : Mathf.Abs(n.y) > 0.5f ? Vector3.forward : Vector3.right;
            Vector3 v = Vector3.Cross(n, u);
            int b = verts.Count;
            verts.Add(centre + Vector3.Scale(n - u - v, e));
            verts.Add(centre + Vector3.Scale(n + u - v, e));
            verts.Add(centre + Vector3.Scale(n + u + v, e));
            verts.Add(centre + Vector3.Scale(n - u + v, e));
            tris.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
        }
        var mesh = new Mesh { name = name };
        mesh.SetVertices(verts);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        ValidationUtil.Near((mesh.bounds.center - centre).magnitude, 0f, 1e-5f, $"'{name}' bounds centre");
        ValidationUtil.Near((mesh.bounds.size - size).magnitude, 0f, 1e-5f, $"'{name}' bounds size");
        return mesh;
    }

    // The Darwinbot shape: a registry on the root, one leaf at IDENTITY carrying the mesh, and a box on
    // that same GameObject holding what GeneratePartColliders.BuildSingleMeshBox wrote — the bounds of
    // the mesh it saw at the time, verbatim.
    private static GameObject BuildRobot(string name, Mesh bound, Mesh boxFrom, out MeshFilter mf)
    {
        var root = new GameObject(name);
        root.AddComponent<RobotMechanisms>();
        var leaf = new GameObject("Plate");
        leaf.transform.SetParent(root.transform, false);
        mf = leaf.AddComponent<MeshFilter>();
        mf.sharedMesh = bound;
        leaf.AddComponent<MeshRenderer>();
        BoxCollider box = leaf.AddComponent<BoxCollider>();
        box.center = boxFrom.bounds.center;
        box.size = boxFrom.bounds.size;
        return root;
    }

    private static RealignVisualsToColliders.PartRow SingleRow(GameObject root)
    {
        RealignVisualsToColliders.RobotReport report = RealignVisualsToColliders.Diagnose(root);
        ValidationUtil.Assert(report.rows.Count == 1,
            $"one MeshFilter with one box must diagnose as exactly one row, got {report.rows.Count}");
        return report.rows[0];
    }

    // --- 2. Every shipped robot -----------------------------------------------------------------------

    private static string EveryRobotDrawsWhereItsCollidersAre()
    {
        var checks = new ValidationUtil.Checks();
        var lines = new List<string>();
        int robots = 0, judged = 0;
        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                robots++;
                string name = Path.GetFileNameWithoutExtension(path);
                RealignVisualsToColliders.RobotReport report = RealignVisualsToColliders.Diagnose(root);
                if (report.Mismatches > 0)
                {
                    // Say what the tool would do about it. This edits the loaded copy, which is unloaded
                    // below without ever being saved; the prefab on disk is what is being judged.
                    report = RealignVisualsToColliders.Repair(root, useUndo: false);
                    report.assetPath = path;
                    Debug.Log(report.Table());
                }
                lines.Add("  " + report.SummaryLine());
                judged += report.okCount + report.Mismatches;

                // A robot with colliders and no judged part means the evidence derivation has stopped
                // seeing them — and a sweep that judges nothing passes nothing.
                bool hasColliders = root.GetComponentInChildren<Collider>(true) != null;
                checks.That(!hasColliders || report.okCount + report.Mismatches > 0,
                    $"{name} carries colliders, yet none of its {report.meshFilters} MeshFilter(s) could be judged " +
                    "against them — the evidence derivation no longer sees this robot's colliders");
                checks.That(report.Mismatches == 0,
                    $"{name}: {report.movedCount} MOVED, {report.reshapedCount} RESHAPED part(s) draw away from their " +
                    $"colliders ({report.reboundCount} re-bindable, {report.shiftedCount} shiftable, {report.ambiguousCount} AMBIGUOUS, " +
                    $"{report.unmatchedCount} UNMATCHED, {report.manualCount} MANUAL, {report.detectOnlyCount} detect-only; table in the log) — run " +
                    "Tools > RoboSim > Robot > Advanced > Realign Visuals To Colliders, or headless " +
                    "-executeMethod RealignVisualsToColliders.RunBatchRepair");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        ValidationUtil.Assert(robots > 0,
            $"no robot prefabs under {RoboSimPaths.RobotsFolder} — a sweep over zero robots is not a pass");
        ValidationUtil.Assert(checks.Failures.Count == 0,
            $"{checks.Failures.Count} of {checks.Count} robot check(s) failed:\n  " +
            string.Join("\n  ", checks.Failures) + "\n\n" + string.Join("\n", lines));
        return $"Robots: {robots} prefab(s), {judged} part(s) judged against their colliders, none MOVED or RESHAPED:\n" +
               string.Join("\n", lines);
    }
}
