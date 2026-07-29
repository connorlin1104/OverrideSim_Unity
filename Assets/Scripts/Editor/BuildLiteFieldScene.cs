using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using Scene = UnityEngine.SceneManagement.Scene;

// Builds Assets/Scenes/LiteScene.unity: the full field pruned down to ONE of each feature.
//
// Why prune a copy instead of composing a scene from parts: there are no field prefabs. The whole
// field is inline in SampleScene (imported from OverrideFieldVersion3.fbx and then rigged in place by
// the Field & Pieces tools), so "one cup" can only mean "the cup that is already there, minus the
// other 35". Re-running regenerates from the CURRENT SampleScene, so the lite scene never drifts.
//
// What it's for: SampleScene is ~4,000 GameObjects and ~1,400 shadow-casting renderers, with 45 stack
// magnets each running a Physics.OverlapSphere every fixed step at 100 Hz. The lite field keeps enough
// to exercise every mechanism — drive, match load, roller, intake, scoring — at roughly a tenth of
// the cost, so Play is quick and a physics change is easy to isolate.
//
// SampleScene itself is never written: the prune happens in memory and is saved to a NEW path.
//
// Usage: Tools > RoboSim > Scenes > Build Lite Field Scene.
// Batch: -executeMethod BuildLiteFieldScene.RunBatch.
public class BuildLiteFieldScene
{
    private const string LiteScenePath = "Assets/Scenes/LiteScene.unity";
    private const string HomeScenePath = "Assets/Scenes/HomeScene.unity";
    private const string FieldRootName = "OverrideFieldVersion3";

    // A goal counts as a full-height stake when its geometry reaches at least this fraction of the
    // tallest goal's height. The neutral goals are tall stakes (~2.2-2.9u of post); the alliance goals
    // are short (~1.5u). Scoring is all about the post — GoalStackMagnet's capture funnel is baked to
    // reach the post top — so the lite field keeps a stake even if a short goal happens to sit nearer
    // the robot's spawn.
    private const float StakeHeightFraction = 0.7f;

    [MenuItem("Tools/RoboSim/Scenes/Build Lite Field Scene", false, 4)]
    private static void BuildInteractive()
    {
        Build(true);
    }

    // Batch entry point for -executeMethod: no dialogs, throws on failure (nonzero exit).
    public static void RunBatch()
    {
        Build(false);
    }

    private static void Build(bool interactive)
    {
        if (!File.Exists(RoboSimPaths.MainScene))
            throw new FileNotFoundException($"Build Lite Field Scene: {RoboSimPaths.MainScene} is missing.");

        string previousScenePath = SceneManager.GetActiveScene().path;
        if (interactive && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
        {
            Debug.Log("Build Lite Field Scene: cancelled at the save prompt; nothing changed.");
            return;
        }

        Scene scene = EditorSceneManager.OpenScene(RoboSimPaths.MainScene, OpenSceneMode.Single);
        var report = new StringBuilder();
        Prune(scene, report);

        // Save As: the in-memory scene becomes LiteScene and SampleScene.unity on disk is untouched.
        if (!EditorSceneManager.SaveScene(scene, LiteScenePath))
            throw new IOException($"Build Lite Field Scene: failed to save {LiteScenePath}.");

        RegisterInBuildSettings();

        // Re-open from disk and prove the pruned scene is still playable. A silent mis-prune (a match
        // loader without its tape, a goal without its magnet) would otherwise only show up as "the
        // feature quietly does nothing" during a Play test.
        string problems = VerifySavedScene();
        if (!string.IsNullOrEmpty(problems))
        {
            string message = "Build Lite Field Scene: the saved scene failed its checks:\n" + problems;
            if (!interactive) throw new System.InvalidOperationException(message);
            Debug.LogError(message);
            EditorUtility.DisplayDialog("Build Lite Field Scene", message, "OK");
            return;
        }

        if (interactive && !string.IsNullOrEmpty(previousScenePath) && previousScenePath != LiteScenePath)
            EditorSceneManager.OpenScene(previousScenePath, OpenSceneMode.Single);

        Debug.Log($"Build Lite Field Scene: saved {LiteScenePath} and registered it in Build Settings.\n{report}" +
                  "Switch to it in-game with the \"Lite Field (faster)\" setting, or just open the scene and press Play.");
    }

    // --- Pruning ---

    private static void Prune(Scene scene, StringBuilder report)
    {
        Transform fieldRoot = null;
        RobotSpawner spawner = null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (root.name == FieldRootName) fieldRoot = root.transform;
            if (spawner == null) spawner = root.GetComponentInChildren<RobotSpawner>(true);
        }
        if (fieldRoot == null)
            throw new System.InvalidOperationException(
                $"Build Lite Field Scene: no '{FieldRootName}' root in {RoboSimPaths.MainScene}.");

        // Everything is kept nearest to where the robot actually spawns, so the whole lite field is
        // within driving distance instead of scattered across a 36-unit field.
        Vector3 anchor = SpawnAnchor(spawner);

        Transform cups = fieldRoot.Find("Cups");
        Transform pins = fieldRoot.Find("Pins");
        Transform goals = fieldRoot.Find("Goals");
        Transform rollers = fieldRoot.Find("Rollers");
        Transform matchLoaders = fieldRoot.Find("MatchLoaders");
        Transform tapes = fieldRoot.Find("TapeDetectors");
        Transform perimeter = fieldRoot.Find("Perimeter");
        Transform staticObjects = fieldRoot.Find("StaticObjects");

        Transform keptCup = KeepNearest(InstancesOf<PieceStackMagnet>(cups), null, anchor, cups, "cup", report);
        Transform keptPin = KeepNearest(PinInstances(pins), null, anchor, pins, "pin", report);

        List<Transform> allGoals = InstancesOf<GoalStackMagnet>(goals);
        Transform keptGoal = KeepNearest(allGoals, StakesAmong(allGoals), anchor, goals, "goal", report);

        Transform keptRoller = KeepNearest(InstancesOf<RollerSnap>(rollers), null, anchor, rollers, "roller", report);
        Transform keptLoader = KeepNearest(InstancesOf<MatchLoaderController>(matchLoaders), null, anchor,
            matchLoaders, "match loader", report);

        KeepPairedTape(tapes, keptLoader, report);
        PrunePerimeter(perimeter, anchor, report);
        PruneStaticObjects(staticObjects, report);

        // The copied Reset button still points at SampleScene — pressing it mid-test would drop the
        // player back into the heavy field without any hint that's what happened.
        RetargetSceneNavButtons(scene, report);

        report.AppendLine($"  kept: cup={Name(keptCup)}, pin={Name(keptPin)}, goal={Name(keptGoal)}, " +
                          $"roller={Name(keptRoller)}, loader={Name(keptLoader)}");
    }

    private static Vector3 SpawnAnchor(RobotSpawner spawner)
    {
        if (spawner == null) return Vector3.zero;
        SerializedProperty position = new SerializedObject(spawner).FindProperty("spawnPosition");
        return position != null ? position.vector3Value : spawner.transform.position;
    }

    // The top-level object for each instance of a feature: climb from each marker component until the
    // parent would sweep in a sibling instance. This is depth-agnostic on purpose — cups sit one level
    // under their group while pins sit two (Pins/PinsYellowYellow/PinYellowYellow*), and goals are
    // split across GoalsNeutral and GoalAlliance.
    private static List<Transform> InstancesOf<T>(Transform group) where T : Component
    {
        var roots = new List<Transform>();
        if (group == null) return roots;

        foreach (T marker in group.GetComponentsInChildren<T>(true))
        {
            Transform node = marker.transform;
            while (node.parent != null && node.parent != group &&
                   node.parent.GetComponentsInChildren<T>(true).Length == 1)
            {
                node = node.parent;
            }
            if (!roots.Contains(node)) roots.Add(node);
        }
        return roots;
    }

    // Pins carry no marker component of their own — they're plain Rigidbodies named Pin*. Their
    // collider shells are also named PinCollider_*, but those have no Rigidbody, so requiring one is
    // enough to pick out the 37 actual pieces.
    private static List<Transform> PinInstances(Transform group)
    {
        var roots = new List<Transform>();
        if (group == null) return roots;

        foreach (Rigidbody body in group.GetComponentsInChildren<Rigidbody>(true))
        {
            if (!GamePiece.IsPiece(body.gameObject)) continue;
            if (!roots.Contains(body.transform)) roots.Add(body.transform);
        }
        return roots;
    }

    // Keep one instance and destroy the rest, then drop any container the deletions emptied
    // (PinsRedBlue, GoalAlliance, ...). Returns the survivor.
    //
    // Selection is nearest-to-anchor so the whole lite field ends up within driving distance of the
    // spawn. `preferred` narrows what may be CHOSEN without narrowing what gets deleted — everything
    // in `candidates` that isn't the survivor still goes.
    private static Transform KeepNearest(List<Transform> candidates, List<Transform> preferred,
        Vector3 anchor, Transform group, string label, StringBuilder report)
    {
        if (candidates == null || candidates.Count == 0)
        {
            report.AppendLine($"  {label}: none found — nothing pruned");
            return null;
        }

        List<Transform> choices = (preferred != null && preferred.Count > 0) ? preferred : candidates;

        Transform best = null;
        float bestDistance = float.MaxValue;
        foreach (Transform candidate in choices)
        {
            float distance = Vector3.SqrMagnitude(WorldCenter(candidate) - anchor);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = candidate;
        }

        int removed = 0;
        foreach (Transform candidate in candidates)
        {
            if (candidate == best || candidate == null) continue;
            Object.DestroyImmediate(candidate.gameObject);
            removed++;
        }
        removed += DestroyEmptyContainers(group);

        report.AppendLine($"  {label}: kept 1 of {candidates.Count} (removed {removed} objects)");
        return best;
    }

    // A direct child of a group that has lost every child and carries nothing but its Transform is a
    // leftover container. Marker empties (GoalStackAnchor, SpawnPoint) are never direct children of a
    // group, so they can't be caught by this.
    private static int DestroyEmptyContainers(Transform group)
    {
        if (group == null) return 0;

        var doomed = new List<GameObject>();
        foreach (Transform child in group)
        {
            if (child.childCount != 0) continue;
            if (child.GetComponents<Component>().Length > 1) continue; // more than just the Transform
            doomed.Add(child.gameObject);
        }
        foreach (GameObject go in doomed) Object.DestroyImmediate(go);
        return doomed.Count;
    }

    // Each match loader is wired 1:1 to its own tape trigger, so the kept loader's tape has to survive
    // with it — the loader is driven only by that trigger, and dropping it silently disables match
    // loading. Pairing is read from the trigger's serialized link rather than guessed from names.
    private static void KeepPairedTape(Transform tapes, Transform keptLoader, StringBuilder report)
    {
        if (tapes == null || keptLoader == null)
        {
            report.AppendLine("  tape: skipped (no tape group or no match loader kept)");
            return;
        }

        MatchLoaderController loader = keptLoader.GetComponentInChildren<MatchLoaderController>(true);
        List<Transform> triggers = InstancesOf<MatchLoadTrigger>(tapes);

        Transform paired = null;
        foreach (Transform trigger in triggers)
        {
            MatchLoadTrigger component = trigger.GetComponentInChildren<MatchLoadTrigger>(true);
            SerializedProperty link = new SerializedObject(component).FindProperty("mainController");
            if (link == null) continue;
            var linked = link.objectReferenceValue as MatchLoaderController;
            if (linked != null && linked == loader) { paired = trigger; break; }
        }

        if (paired == null)
        {
            report.AppendLine("  tape: WARNING — no tape is linked to the kept match loader; " +
                              "keeping the nearest one instead");
            paired = KeepNearest(triggers, null, WorldCenter(keptLoader), tapes, "tape (fallback)", report);
            return;
        }

        int removed = 0;
        foreach (Transform trigger in triggers)
        {
            if (trigger == paired || trigger == null) continue;
            Object.DestroyImmediate(trigger.gameObject);
            removed++;
        }
        removed += DestroyEmptyContainers(tapes);
        report.AppendLine($"  tape: kept {paired.name} (the kept loader's own trigger; removed {removed})");
    }

    // Perimeter is the one place "one of each" has to be read loosely. The visible Walls carry NO
    // colliders — all four physics walls live on the single WallColliders object, which RobotSpawner
    // also looks up BY NAME to clamp the spawn footprint inside the field. So the colliders stay whole
    // (they're 4 boxes, essentially free) and only the heavy visible geometry is thinned: one wall
    // panel, no corners. Keeping literally one wall would let the robot drive off the field.
    private static void PrunePerimeter(Transform perimeter, Vector3 anchor, StringBuilder report)
    {
        if (perimeter == null)
        {
            report.AppendLine("  perimeter: not found");
            return;
        }

        Transform walls = perimeter.Find("Walls");
        if (walls != null)
        {
            var panels = new List<Transform>();
            foreach (Transform panel in walls) panels.Add(panel);
            KeepNearest(panels, null, anchor, walls, "wall panel", report);
        }

        Transform corners = perimeter.Find("Corners");
        if (corners != null)
        {
            Object.DestroyImmediate(corners.gameObject);
            report.AppendLine("  corners: removed (20 concave mesh colliders, purely decorative)");
        }

        report.AppendLine(perimeter.Find("WallColliders") != null
            ? "  wall colliders: kept intact (all 4 boxes — spawn clamp + field containment)"
            : "  wall colliders: WARNING — WallColliders is missing; the robot will not be contained");
    }

    // StaticObjects is 95 uniform ~0.8u barrier blocks ringing the field, stacked in pairs, spanning
    // 60u across — WIDER than the 36u floor, so they sit outside the playable area entirely, and the
    // nearest one is ~4.8u from any feature the lite scene keeps. They contribute 95 renderers and 95
    // concave mesh colliders and nothing else, so the whole group goes. (The goals' stake posts are
    // inside the goal subtrees, not here — keeping a goal keeps its post.)
    private static void PruneStaticObjects(Transform staticObjects, StringBuilder report)
    {
        if (staticObjects == null)
        {
            report.AppendLine("  static objects: not found");
            return;
        }

        int removed = staticObjects.childCount;
        Object.DestroyImmediate(staticObjects.gameObject);
        report.AppendLine($"  static objects: removed the whole group ({removed} barrier blocks outside the field)");
    }

    // The goals that are full-height stakes rather than the short alliance goals. Height is measured
    // from the geometry rather than the name so a renamed or re-authored goal still classifies.
    private static List<Transform> StakesAmong(List<Transform> goals)
    {
        var stakes = new List<Transform>();
        if (goals == null || goals.Count == 0) return stakes;

        float tallest = 0f;
        foreach (Transform goal in goals) tallest = Mathf.Max(tallest, WorldHeight(goal));
        if (tallest <= 0f) return stakes;

        foreach (Transform goal in goals)
        {
            if (WorldHeight(goal) >= tallest * StakeHeightFraction) stakes.Add(goal);
        }
        return stakes;
    }

    private static void RetargetSceneNavButtons(Scene scene, StringBuilder report)
    {
        int retargeted = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (SceneNavButton nav in root.GetComponentsInChildren<SceneNavButton>(true))
            {
                if (nav.sceneName != FieldSceneSettings.FullFieldSceneName) continue;
                nav.sceneName = FieldSceneSettings.LiteFieldSceneName;
                EditorUtility.SetDirty(nav);
                retargeted++;
            }
        }
        report.AppendLine($"  reset button: retargeted {retargeted} nav button(s) to LiteScene");
    }

    // --- Build settings ---

    private static void RegisterInBuildSettings()
    {
        foreach (EditorBuildSettingsScene existing in EditorBuildSettings.scenes)
        {
            if (existing.path == LiteScenePath) return; // already registered
        }

        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes)
        {
            new EditorBuildSettingsScene(LiteScenePath, true)
        };
        EditorBuildSettings.scenes = scenes.ToArray();
    }

    // --- Verification ---

    // Re-opens the saved scene and checks the things a mis-prune would break. Returns an empty string
    // when everything holds, otherwise one line per problem.
    private static string VerifySavedScene()
    {
        Scene scene = EditorSceneManager.OpenScene(LiteScenePath, OpenSceneMode.Single);
        var loaders = new List<MatchLoaderController>();
        var triggers = new List<MatchLoadTrigger>();
        var magnets = new List<GoalStackMagnet>();
        var detents = new List<RollerSnap>();
        var pieceMagnets = new List<PieceStackMagnet>();
        var spawners = new List<RobotSpawner>();
        var pins = new List<Rigidbody>();
        Transform wallColliders = null;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            loaders.AddRange(root.GetComponentsInChildren<MatchLoaderController>(true));
            triggers.AddRange(root.GetComponentsInChildren<MatchLoadTrigger>(true));
            magnets.AddRange(root.GetComponentsInChildren<GoalStackMagnet>(true));
            detents.AddRange(root.GetComponentsInChildren<RollerSnap>(true));
            pieceMagnets.AddRange(root.GetComponentsInChildren<PieceStackMagnet>(true));
            spawners.AddRange(root.GetComponentsInChildren<RobotSpawner>(true));
            foreach (Rigidbody body in root.GetComponentsInChildren<Rigidbody>(true))
            {
                if (body.name.StartsWith("Pin")) pins.Add(body);
            }
            if (wallColliders == null && root.name == FieldRootName)
            {
                Transform perimeter = root.transform.Find("Perimeter");
                if (perimeter != null) wallColliders = perimeter.Find("WallColliders");
            }
        }

        var problems = new StringBuilder();
        Expect(problems, loaders.Count == 1, $"expected 1 MatchLoaderController, found {loaders.Count}");
        Expect(problems, triggers.Count == 1, $"expected 1 MatchLoadTrigger, found {triggers.Count}");
        Expect(problems, magnets.Count == 1, $"expected 1 GoalStackMagnet, found {magnets.Count}");
        Expect(problems, detents.Count == 1, $"expected 1 RollerSnap, found {detents.Count}");
        Expect(problems, pieceMagnets.Count == 1, $"expected 1 cup (PieceStackMagnet), found {pieceMagnets.Count}");
        Expect(problems, pins.Count == 1, $"expected 1 pin Rigidbody, found {pins.Count}");
        Expect(problems, spawners.Count == 1, $"expected 1 RobotSpawner, found {spawners.Count}");

        if (loaders.Count == 1)
        {
            SerializedObject loaderSo = new SerializedObject(loaders[0]);
            Expect(problems, IsRefSet(loaderSo, "movingAssembly"), "the kept match loader lost movingAssembly");
            Expect(problems, IsRefSet(loaderSo, "spawnPoint"), "the kept match loader lost spawnPoint");
            Expect(problems, IsRefSet(loaderSo, "elementPrefab"), "the kept match loader lost elementPrefab");
        }
        if (loaders.Count == 1 && triggers.Count == 1)
        {
            SerializedProperty link = new SerializedObject(triggers[0]).FindProperty("mainController");
            Expect(problems, link != null && link.objectReferenceValue as MatchLoaderController == loaders[0],
                "the kept tape trigger is not wired to the kept match loader");
        }
        if (magnets.Count == 1)
            Expect(problems, magnets[0].stackAnchor != null, "the kept goal magnet lost its stackAnchor");
        if (spawners.Count == 1)
            Expect(problems, IsRefSet(new SerializedObject(spawners[0]), "catalog"),
                "RobotSpawner lost its RobotModelCatalog — no robot would spawn");

        Expect(problems, wallColliders != null, "Perimeter/WallColliders is missing (spawn clamp + containment)");
        if (wallColliders != null)
        {
            int boxes = wallColliders.GetComponentsInChildren<BoxCollider>(true).Length;
            Expect(problems, boxes >= 4, $"Perimeter/WallColliders has {boxes} box colliders, expected at least 4");
        }

        return problems.ToString();
    }

    private static void Expect(StringBuilder problems, bool condition, string message)
    {
        if (!condition) problems.AppendLine("  - " + message);
    }

    private static bool IsRefSet(SerializedObject so, string propertyName)
    {
        SerializedProperty property = so.FindProperty(propertyName);
        return property != null && property.objectReferenceValue != null;
    }

    // --- Geometry helpers ---

    // Where an object actually IS on the field, and how tall it stands. Field pieces keep their CAD
    // origin as the transform pivot (units away from their own mesh), so transform.position is not a
    // usable location for them — renderer/collider bounds are.
    private static Vector3 WorldCenter(Transform node)
    {
        return TryWorldBounds(node, out Bounds bounds) ? bounds.center : node.position;
    }

    private static float WorldHeight(Transform node)
    {
        return TryWorldBounds(node, out Bounds bounds) ? bounds.size.y : 0f;
    }

    private static bool TryWorldBounds(Transform node, out Bounds bounds)
    {
        bool found = false;
        bounds = new Bounds();
        if (node == null) return false;

        foreach (Renderer renderer in node.GetComponentsInChildren<Renderer>(true))
        {
            if (!found) { bounds = renderer.bounds; found = true; }
            else bounds.Encapsulate(renderer.bounds);
        }
        if (!found)
        {
            foreach (Collider collider in node.GetComponentsInChildren<Collider>(true))
            {
                if (!found) { bounds = collider.bounds; found = true; }
                else bounds.Encapsulate(collider.bounds);
            }
        }
        return found;
    }

    private static string Name(Transform node)
    {
        return node != null ? node.name : "(none)";
    }
}
