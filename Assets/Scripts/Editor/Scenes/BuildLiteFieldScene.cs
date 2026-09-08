using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using Scene = UnityEngine.SceneManagement.Scene;

// Builds Assets/Scenes/LiteScene.unity: the full field pruned down to ONE of every structure it is
// built out of, spread a structure to a quadrant so nothing crowds anything else.
//
// Why prune a copy instead of composing a scene from parts: there are no field prefabs. The whole
// field is inline in SampleScene (imported from OverrideFieldVersion3.fbx and then rigged in place by
// the Field & Pieces tools), so "one cup" can only mean "the cup that is already there, minus the
// other 35". Re-running regenerates from the CURRENT SampleScene, so the lite scene never drifts.
//
// What it's for: SampleScene is ~4,000 GameObjects and ~1,400 renderers, with 45 stack magnets each
// running a Physics.OverlapSphere every fixed step at 100 Hz. The lite field keeps enough to exercise
// every mechanism — drive, match load, roller, intake, scoring against all three sizes of goal — at a
// fraction of the cost, so Play is quick and a physics change is easy to isolate.
//
// What survives, and where. The quadrants are of the playing surface, whose centre is NOT the world
// origin (the floor is centred on z = 5.9), so they are measured off the floor rather than assumed:
//
//     (-X,+Z)  one cup with four pins lying flat around it   (+X,+Z)  an alliance goal (the short one)
//     (-X,-Z)  three cups in a row against the wall with     (+X,-Z)  a neutral goal (the mid stake)
//              one pin standing in the middle cup
//     centre   the tall central stake, and one cup with a pin standing in it
//
// and on the perimeter, which is shared ground rather than any one quadrant's: all four walls, all
// four corners, all four white tape lines, one roller, and one match loader — the one nearest the
// spawn, still wired to its own tape.
//
// Structures are found by GEOMETRY, not by name: "three cups within a row's span of each other with an
// upright pin in the middle one" survives a re-authored or renamed field, where a name list would
// silently keep nothing. The same is true of the goals, which are told apart by height.
//
// Whole structures are kept, never a piece out of the middle of one. That is not tidiness: the earlier
// version kept a lone pin and deleted the cups it was standing in, and the pin was left 85 mm in the
// air until the settle pass dropped it somewhere else.
//
// SampleScene itself is never written: the prune happens in memory and is saved to a NEW path.
//
// Usage: Tools > RoboSim > Scenes > Build Lite Field Scene.
// Batch: -executeMethod BuildLiteFieldScene.RunBatch.
public class BuildLiteFieldScene
{
    private const string LiteScenePath = RoboSimPaths.LiteScene;
    private const string FieldRootName = "OverrideFieldVersion3";

    // --- What "one of each" means, in world units (the project runs at 10x scale) ---

    // Two cups belong to the same row when their centres are this close. Measured on the shipped
    // field: cups in a row sit 0.80 apart and the ends of a row 1.60, while the nearest cup in ANY
    // other structure is 5.99 away. The gap this has to split is enormous, so the exact value is not
    // delicate.
    private const float RowSpan = 1.75f;

    // A pin STANDING IN a cup is within this of the cup's centre. Measured worst case 0.06.
    private const float PinInCupRadius = 0.4f;

    // A pin LYING FLAT AROUND a cup is within this of it. Measured 1.29-1.31 for all sixteen of them.
    private const float PinRingRadius = 1.8f;

    // Goal heights fall into three flat bands — one tall central stake, four mid-height neutral goals,
    // four short alliance goals. Sorting by height and cutting wherever the drop exceeds this fraction
    // of the tallest separates them: the real drops are 27% and 35% of the tallest, and inside a band
    // the spread is under 1%. Height rather than name so a re-authored goal still classifies, and
    // bands rather than a fixed threshold so it does not matter how tall the tallest happens to be.
    private const float GoalBandGap = 0.15f;

    // How close two candidates have to be before "nearest" is called a tie, ~1 mm at this scale. Ties
    // are the normal case, not an edge case — see Beats().
    private const float TieEpsilon = 0.01f;

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
        Kept kept = Prune(scene, report);

        // Pruning can take the floor out from under a piece, so settling has to happen HERE, after the
        // prune and before the save: settling SampleScene cannot fix a drop the prune created, and
        // settling LiteScene by hand afterwards would be undone the next time this tool runs. Keeping
        // whole structures means there should be nothing left to settle, which is exactly why this
        // stays — if the structure detection ever starts cutting one in half, the settle report is
        // where that shows up.
        report.AppendLine(SettleFieldPieces.Settle());

        // Save As: the in-memory scene becomes LiteScene and SampleScene.unity on disk is untouched.
        if (!EditorSceneManager.SaveScene(scene, LiteScenePath))
            throw new IOException($"Build Lite Field Scene: failed to save {LiteScenePath}.");

        RegisterInBuildSettings();

        // Re-open from disk and prove the pruned scene is still playable. A silent mis-prune (a match
        // loader without its tape, a goal without its magnet, a structure cut in half) would otherwise
        // only show up as "the feature quietly does nothing" during a Play test.
        string problems = VerifySavedScene(kept);
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

    // What the prune left behind, so the verify pass can hold the saved scene to what was intended
    // instead of to numbers typed in twice.
    private sealed class Kept
    {
        public int WallPanels;
        public int Corners;
        public int TapeLines;
        public int Goals;
        public int Cups;
        public int Pins;
    }

    // --- Pruning ---

    private static Kept Prune(Scene scene, StringBuilder report)
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

        Transform floorTiles = fieldRoot.Find("FloorTiles");
        Transform cupsGroup = fieldRoot.Find("Cups");
        Transform pinsGroup = fieldRoot.Find("Pins");
        Transform goalsGroup = fieldRoot.Find("Goals");
        Transform rollersGroup = fieldRoot.Find("Rollers");
        Transform loadersGroup = fieldRoot.Find("MatchLoaders");
        Transform tapesGroup = fieldRoot.Find("TapeDetectors");
        Transform perimeter = fieldRoot.Find("Perimeter");
        Transform staticObjects = fieldRoot.Find("StaticObjects");

        Bounds surface = PlayingSurface(floorTiles, fieldRoot);
        Vector3 spawn = SpawnAnchor(spawner);

        // Goals: one from each height band, so the lite field has all three sizes to score on. The
        // tall central stake goes wherever it already is (the middle of the field); the other two take
        // a quadrant each.
        List<Transform> goals = InstancesOf<GoalStackMagnet>(goalsGroup);
        List<List<Transform>> bands = GoalHeightBands(goals);
        Transform centralGoal = NearestTo(Band(bands, 0), surface.center);
        Transform neutralGoal = NearestTo(Band(bands, 1), Quadrant(surface, 1f, -1f));
        Transform allianceGoal = NearestTo(Band(bands, 2), Quadrant(surface, 1f, 1f));
        var keptGoals = new List<Transform> { centralGoal, neutralGoal, allianceGoal };
        KeepOnly(goals, keptGoals, goalsGroup, "goals", report);

        // Pieces: one of each of the three ways the field arranges cups and pins.
        List<Piece> cups = Measure(InstancesOf<PieceStackMagnet>(cupsGroup));
        List<Piece> pins = Measure(PinInstances(pinsGroup));

        Structure row = NearestStructure(WallRows(cups, pins), Quadrant(surface, -1f, -1f));
        Structure ring = NearestStructure(PinRings(cups, pins), Quadrant(surface, -1f, 1f));
        // The lone cup with a pin in it is the smallest structure on the field, so it is the one that
        // can sit beside the central stake without crowding it — which also stages it for scoring.
        Structure stack = NearestStructure(StackedSingles(cups, pins), surface.center);

        var keptPieces = new List<Transform>();
        Collect(keptPieces, row);
        Collect(keptPieces, ring);
        Collect(keptPieces, stack);
        KeepOnly(Nodes(cups), keptPieces, cupsGroup, "cups", report);
        KeepOnly(Nodes(pins), keptPieces, pinsGroup, "pins", report);

        // The roller and the match loader are things the driver has to reach, so those two are kept
        // nearest the spawn rather than spread out.
        List<Transform> rollers = InstancesOf<RollerSnap>(rollersGroup);
        Transform roller = NearestTo(rollers, spawn);
        KeepOnly(rollers, new List<Transform> { roller }, rollersGroup, "roller", report);

        List<Transform> loaders = InstancesOf<MatchLoaderController>(loadersGroup);
        Transform loader = NearestTo(loaders, spawn);
        KeepOnly(loaders, new List<Transform> { loader }, loadersGroup, "match loader", report);

        int tapeLines = KeepTapeLines(tapesGroup, loader, report);
        KeepPerimeter(perimeter, report, out int wallPanels, out int corners);
        PruneStaticObjects(staticObjects, surface, report);

        // The copied Reset button still points at SampleScene — pressing it mid-test would drop the
        // player back into the heavy field without any hint that's what happened.
        RetargetSceneNavButtons(scene, report);

        report.AppendLine("  layout:");
        report.AppendLine($"    centre        {Name(centralGoal)} (the tall stake) + {Label(stack)}");
        report.AppendLine($"    {Where(surface, neutralGoal)}  {Name(neutralGoal)} (neutral goal)");
        report.AppendLine($"    {Where(surface, allianceGoal)}  {Name(allianceGoal)} (alliance goal)");
        report.AppendLine($"    {Where(surface, row)}  {Label(row)} (three cups against the wall, pin in the middle)");
        report.AppendLine($"    {Where(surface, ring)}  {Label(ring)} (cup ringed by four pins lying flat)");

        return new Kept
        {
            WallPanels = wallPanels,
            Corners = corners,
            TapeLines = tapeLines,
            Goals = CountNonNull(keptGoals),
            Cups = CountCups(row) + CountCups(ring) + CountCups(stack),
            Pins = CountPins(row) + CountPins(ring) + CountPins(stack),
        };
    }

    private static Vector3 SpawnAnchor(RobotSpawner spawner)
    {
        if (spawner == null) return Vector3.zero;
        SerializedProperty position = new SerializedObject(spawner).FindProperty("spawnPosition");
        return position != null ? position.vector3Value : spawner.transform.position;
    }

    // The playing surface in world space: what the quadrants are measured against. Its centre is not
    // the world origin, so nothing here may assume Vector3.zero. Falls back to the whole field root if
    // the floor group is gone, which keeps the split roughly right instead of collapsing every anchor
    // onto the origin.
    private static Bounds PlayingSurface(Transform floorTiles, Transform fieldRoot)
    {
        if (floorTiles != null && TryWorldBounds(floorTiles, out Bounds floor)) return floor;
        if (TryWorldBounds(fieldRoot, out Bounds whole)) return whole;
        return new Bounds(Vector3.zero, new Vector3(36f, 0f, 36f));
    }

    // The middle of one quadrant of the playing surface — the point a structure is chosen nearest to.
    private static Vector3 Quadrant(Bounds surface, float x, float z)
    {
        return surface.center + new Vector3(x * surface.extents.x * 0.5f, 0f, z * surface.extents.z * 0.5f);
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

    // Destroy every candidate that isn't in `keep`, then drop any container the deletions emptied
    // (PinsRedBlue, GoalAlliance, ...).
    private static void KeepOnly(List<Transform> candidates, List<Transform> keep, Transform group,
        string label, StringBuilder report)
    {
        if (candidates == null || candidates.Count == 0)
        {
            report.AppendLine($"  {label}: none found — nothing pruned");
            return;
        }

        var survivors = new HashSet<Transform>();
        foreach (Transform node in keep)
        {
            if (node != null) survivors.Add(node);
        }

        var names = new List<string>();
        int removed = 0;
        foreach (Transform candidate in candidates)
        {
            if (candidate == null) continue;
            if (survivors.Contains(candidate)) { names.Add(candidate.name); continue; }
            Object.DestroyImmediate(candidate.gameObject);
            removed++;
        }
        removed += DestroyEmptyContainers(group);

        names.Sort(string.CompareOrdinal);
        report.AppendLine($"  {label}: kept {names.Count} of {candidates.Count} " +
                          $"({string.Join(", ", names)}); removed {removed} objects");
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

    // The white tape lines are field markings, and all four stay — a floor with one line on it reads
    // as unfinished, which is what the lite field looked like when this kept only the live one.
    //
    // Only the kept loader's own tape stays LIVE. A match loader is driven solely by its 1:1 trigger,
    // so that one has to survive or match loading silently does nothing; the other three would be
    // trigger volumes wired to loaders that no longer exist. MatchLoadTrigger null-checks its link so
    // leaving them would not throw — it would just leave three dead volumes for the physics engine to
    // test against every step. Pairing is read from the trigger's serialized link, not guessed from
    // names. Returns how many tape lines were kept.
    private static int KeepTapeLines(Transform tapes, Transform keptLoader, StringBuilder report)
    {
        if (tapes == null)
        {
            report.AppendLine("  tape lines: no TapeDetectors group — nothing kept");
            return 0;
        }

        MatchLoaderController loader = keptLoader != null
            ? keptLoader.GetComponentInChildren<MatchLoaderController>(true)
            : null;
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

        if (paired == null && triggers.Count > 0)
        {
            report.AppendLine("  tape lines: WARNING — no tape is linked to the kept match loader; " +
                              "leaving the nearest one live instead");
            paired = NearestTo(triggers, keptLoader != null ? WorldCenter(keptLoader) : Vector3.zero);
        }

        int stripped = 0;
        foreach (Transform trigger in triggers)
        {
            if (trigger == null || trigger == paired) continue;
            foreach (MatchLoadTrigger dead in trigger.GetComponentsInChildren<MatchLoadTrigger>(true))
            {
                GameObject host = dead.gameObject;
                Object.DestroyImmediate(dead);
                foreach (Collider volume in host.GetComponents<Collider>())
                {
                    if (volume.isTrigger) Object.DestroyImmediate(volume);
                }
                stripped++;
            }
        }

        report.AppendLine($"  tape lines: kept all {tapes.childCount} (they are floor markings); " +
                          $"{Name(paired)} stays live for the kept loader, {stripped} orphan trigger " +
                          "volume(s) stripped");
        return tapes.childCount;
    }

    // The perimeter is kept whole. It is the most expensive group in the lite scene by renderer count,
    // and it is still worth it: one wall panel and no corners is what the field looked like before, and
    // it read as a room with three sides missing. None of it casts a shadow (SampleScene already turns
    // that off for the whole group), and none of the visible geometry carries physics — all four
    // physics walls live on the single WallColliders object, which RobotSpawner also looks up BY NAME
    // to clamp the spawn footprint. The corners' own mesh colliders are the exception and get disabled:
    // they are concave meshes, the expensive kind to instantiate at load, and WallColliders' four boxes
    // already run the full length of each side, corners included, so nothing can reach them.
    private static void KeepPerimeter(Transform perimeter, StringBuilder report,
        out int wallPanels, out int corners)
    {
        wallPanels = 0;
        corners = 0;
        if (perimeter == null)
        {
            report.AppendLine("  perimeter: not found");
            return;
        }

        Transform walls = perimeter.Find("Walls");
        if (walls != null)
        {
            wallPanels = walls.childCount;
            report.AppendLine($"  walls: kept all {wallPanels} panels");
        }
        else
        {
            report.AppendLine("  walls: WARNING — Perimeter/Walls is missing");
        }

        Transform cornerGroup = perimeter.Find("Corners");
        if (cornerGroup != null)
        {
            corners = cornerGroup.childCount;
            int disabled = 0;
            foreach (MeshCollider shell in cornerGroup.GetComponentsInChildren<MeshCollider>(true))
            {
                if (!shell.enabled) continue;
                shell.enabled = false;
                EditorUtility.SetDirty(shell);
                disabled++;
            }
            report.AppendLine($"  corners: kept all {corners} (four walls with no corners between them " +
                              $"read as gaps); {disabled} decorative mesh collider(s) disabled");
        }
        else
        {
            report.AppendLine("  corners: WARNING — Perimeter/Corners is missing");
        }

        report.AppendLine(perimeter.Find("WallColliders") != null
            ? "  wall colliders: kept intact (all 4 boxes — spawn clamp + field containment)"
            : "  wall colliders: WARNING — WallColliders is missing; the robot will not be contained");
    }

    // StaticObjects is the field SURROUND — barrier blocks and booths ringing the field, all of them
    // outside the walls, each with its own unique mesh and a concave mesh collider. All of that goes.
    //
    // One member is not surround: the foot the central goal stands on, which is on the playing surface.
    // Deleting the whole group (which this used to do) left the lite field's central stake with no
    // base. So the test is geometric — anything standing ON the floor stays.
    private static void PruneStaticObjects(Transform staticObjects, Bounds surface, StringBuilder report)
    {
        if (staticObjects == null)
        {
            report.AppendLine("  static objects: not found");
            return;
        }

        var doomed = new List<GameObject>();
        var kept = new List<string>();
        foreach (Transform child in staticObjects)
        {
            Vector3 center = WorldCenter(child);
            bool onTheField = Mathf.Abs(center.x - surface.center.x) <= surface.extents.x &&
                              Mathf.Abs(center.z - surface.center.z) <= surface.extents.z;
            if (onTheField) kept.Add(child.name);
            else doomed.Add(child.gameObject);
        }
        foreach (GameObject go in doomed) Object.DestroyImmediate(go);

        if (staticObjects.childCount == 0)
        {
            Object.DestroyImmediate(staticObjects.gameObject);
            report.AppendLine($"  static objects: removed the whole group ({doomed.Count} outside the walls)");
            return;
        }

        report.AppendLine($"  static objects: removed {doomed.Count} outside the walls, kept " +
                          $"{kept.Count} standing on the floor ({string.Join(", ", kept)})");
    }

    // --- Structures ---

    // A cup or a pin, measured once. Field pieces keep their CAD origin as the transform pivot (units
    // away from their own mesh), so transform.position is not a usable location for them — bounds are.
    private sealed class Piece
    {
        public Transform Node;
        public Vector3 Center;
        public bool Upright; // taller than it is wide: standing, rather than lying flat
    }

    // One arrangement of pieces, kept or deleted as a unit.
    private sealed class Structure
    {
        public string Label;
        public Vector3 Center;
        public readonly List<Transform> Cups = new List<Transform>();
        public readonly List<Transform> Pins = new List<Transform>();
    }

    private static List<Piece> Measure(List<Transform> nodes)
    {
        var pieces = new List<Piece>();
        foreach (Transform node in nodes)
        {
            bool measured = TryWorldBounds(node, out Bounds bounds);
            pieces.Add(new Piece
            {
                Node = node,
                Center = measured ? bounds.center : node.position,
                // Measured on the shipped field: a standing pin is 1.65 tall by 0.6 across, a flat one
                // 1.67 long by 0.6 tall. Nothing sits in between, so the comparison is not delicate.
                Upright = measured && bounds.size.y > Mathf.Max(bounds.size.x, bounds.size.z),
            });
        }
        return pieces;
    }

    private static List<Transform> Nodes(List<Piece> pieces)
    {
        var nodes = new List<Transform>();
        foreach (Piece piece in pieces) nodes.Add(piece.Node);
        return nodes;
    }

    // Three cups in a row with a pin standing in the middle one. On the shipped field these are always
    // against a wall, but that isn't what identifies them — the row is.
    private static List<Structure> WallRows(List<Piece> cups, List<Piece> pins)
    {
        var rows = new List<Structure>();
        var claimed = new HashSet<Transform>();

        foreach (Piece cup in cups)
        {
            if (claimed.Contains(cup.Node)) continue;

            // The ends of a row are within RowSpan of each other too, so this finds the whole row from
            // any member of it.
            var row = new List<Piece>();
            foreach (Piece other in cups)
            {
                if (Flat(other.Center - cup.Center).magnitude <= RowSpan) row.Add(other);
            }
            if (row.Count < 3) continue;
            foreach (Piece member in row) claimed.Add(member.Node);

            Piece middle = Middle(row);
            Piece standing = PinIn(middle, pins);
            if (standing == null) continue;

            var structure = new Structure { Label = middle.Node.name, Center = middle.Center };
            foreach (Piece member in row) structure.Cups.Add(member.Node);
            structure.Pins.Add(standing.Node);
            rows.Add(structure);
        }
        return rows;
    }

    // One cup with four or more pins lying flat around it.
    private static List<Structure> PinRings(List<Piece> cups, List<Piece> pins)
    {
        var rings = new List<Structure>();
        foreach (Piece cup in cups)
        {
            var around = new List<Piece>();
            foreach (Piece pin in pins)
            {
                if (pin.Upright) continue;
                if (Flat(pin.Center - cup.Center).magnitude <= PinRingRadius) around.Add(pin);
            }
            if (around.Count < 4) continue;

            var structure = new Structure { Label = cup.Node.name, Center = cup.Center };
            structure.Cups.Add(cup.Node);
            foreach (Piece pin in around) structure.Pins.Add(pin.Node);
            rings.Add(structure);
        }
        return rings;
    }

    // A cup standing on its own with a pin standing in it — no neighbouring cup, no ring of flat pins.
    private static List<Structure> StackedSingles(List<Piece> cups, List<Piece> pins)
    {
        var stacks = new List<Structure>();
        foreach (Piece cup in cups)
        {
            if (HasNeighbourCup(cup, cups)) continue;
            if (HasFlatPinAround(cup, pins)) continue;

            Piece standing = PinIn(cup, pins);
            if (standing == null) continue;

            var structure = new Structure { Label = cup.Node.name, Center = cup.Center };
            structure.Cups.Add(cup.Node);
            structure.Pins.Add(standing.Node);
            stacks.Add(structure);
        }
        return stacks;
    }

    private static bool HasNeighbourCup(Piece cup, List<Piece> cups)
    {
        foreach (Piece other in cups)
        {
            if (other == cup) continue;
            if (Flat(other.Center - cup.Center).magnitude <= RowSpan) return true;
        }
        return false;
    }

    private static bool HasFlatPinAround(Piece cup, List<Piece> pins)
    {
        foreach (Piece pin in pins)
        {
            if (pin.Upright) continue;
            if (Flat(pin.Center - cup.Center).magnitude <= PinRingRadius) return true;
        }
        return false;
    }

    private static Piece PinIn(Piece cup, List<Piece> pins)
    {
        if (cup == null) return null;
        foreach (Piece pin in pins)
        {
            if (!pin.Upright) continue;
            if (Flat(pin.Center - cup.Center).magnitude <= PinInCupRadius) return pin;
        }
        return null;
    }

    // The cup nearest all the others in the row.
    private static Piece Middle(List<Piece> row)
    {
        Piece middle = null;
        float best = float.MaxValue;
        foreach (Piece candidate in row)
        {
            float spread = 0f;
            foreach (Piece other in row) spread += Flat(other.Center - candidate.Center).magnitude;
            if (spread >= best) continue;
            best = spread;
            middle = candidate;
        }
        return middle;
    }

    private static void Collect(List<Transform> into, Structure structure)
    {
        if (structure == null) return;
        into.AddRange(structure.Cups);
        into.AddRange(structure.Pins);
    }

    // --- Goals ---

    // The goals grouped into height bands, tallest band first. See GoalBandGap for why bands and not a
    // fixed threshold.
    private static List<List<Transform>> GoalHeightBands(List<Transform> goals)
    {
        var bands = new List<List<Transform>>();
        if (goals == null || goals.Count == 0) return bands;

        var heights = new Dictionary<Transform, float>();
        foreach (Transform goal in goals) heights[goal] = WorldHeight(goal);

        var sorted = new List<Transform>(goals);
        sorted.Sort((a, b) => heights[b].CompareTo(heights[a]));

        float tallest = heights[sorted[0]];
        if (tallest <= 0f)
        {
            bands.Add(sorted);
            return bands;
        }

        var band = new List<Transform> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            if (heights[sorted[i - 1]] - heights[sorted[i]] > tallest * GoalBandGap)
            {
                bands.Add(band);
                band = new List<Transform>();
            }
            band.Add(sorted[i]);
        }
        bands.Add(band);
        return bands;
    }

    // Band `index`, or the shortest one there is. A field with fewer than three sizes of goal keeps
    // whatever it has rather than dropping a slot on the floor.
    private static List<Transform> Band(List<List<Transform>> bands, int index)
    {
        if (bands == null || bands.Count == 0) return new List<Transform>();
        return bands[Mathf.Min(index, bands.Count - 1)];
    }

    // --- Choosing ---

    private static Transform NearestTo(List<Transform> options, Vector3 anchor)
    {
        Transform best = null;
        float bestDistance = 0f;
        foreach (Transform option in options)
        {
            if (option == null) continue;
            float distance = Flat(WorldCenter(option) - anchor).magnitude;
            if (best != null && !Beats(distance, option.name, bestDistance, best.name)) continue;
            best = option;
            bestDistance = distance;
        }
        return best;
    }

    private static Structure NearestStructure(List<Structure> options, Vector3 anchor)
    {
        Structure best = null;
        float bestDistance = 0f;
        foreach (Structure option in options)
        {
            if (option == null) continue;
            float distance = Flat(option.Center - anchor).magnitude;
            if (best != null && !Beats(distance, option.Label, bestDistance, best.Label)) continue;
            best = option;
            bestDistance = distance;
        }
        return best;
    }

    // Nearest wins; an exact tie is broken by name. Ties are the NORMAL case here, not an edge case:
    // the field is four-way symmetric, so a quadrant's centre is routinely the same distance from two
    // candidates to the millimetre. Without a stable tie-break, which one survives would turn on float
    // noise and could change from one run to the next for no reason a reader could see.
    private static bool Beats(float distance, string label, float bestDistance, string bestLabel)
    {
        if (distance < bestDistance - TieEpsilon) return true;
        if (distance > bestDistance + TieEpsilon) return false;
        return string.CompareOrdinal(label, bestLabel) < 0;
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

    // Re-opens the saved scene and checks the things a mis-prune would break. The piece checks re-run
    // the same structure detection against what was actually written to disk, so they assert the
    // property ("there is one row of three cups with a pin in the middle") rather than a count that
    // would still pass if the prune had kept three unrelated cups. Returns an empty string when
    // everything holds, otherwise one line per problem.
    private static string VerifySavedScene(Kept expected)
    {
        Scene scene = EditorSceneManager.OpenScene(LiteScenePath, OpenSceneMode.Single);
        var loaders = new List<MatchLoaderController>();
        var triggers = new List<MatchLoadTrigger>();
        var magnets = new List<GoalStackMagnet>();
        var detents = new List<RollerSnap>();
        var spawners = new List<RobotSpawner>();
        Transform fieldRoot = null;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            loaders.AddRange(root.GetComponentsInChildren<MatchLoaderController>(true));
            triggers.AddRange(root.GetComponentsInChildren<MatchLoadTrigger>(true));
            magnets.AddRange(root.GetComponentsInChildren<GoalStackMagnet>(true));
            detents.AddRange(root.GetComponentsInChildren<RollerSnap>(true));
            spawners.AddRange(root.GetComponentsInChildren<RobotSpawner>(true));
            if (root.name == FieldRootName) fieldRoot = root.transform;
        }

        var problems = new StringBuilder();
        Expect(problems, fieldRoot != null, $"the saved scene has no '{FieldRootName}' root");
        Expect(problems, loaders.Count == 1, $"expected 1 MatchLoaderController, found {loaders.Count}");
        Expect(problems, triggers.Count == 1,
            $"expected 1 live MatchLoadTrigger, found {triggers.Count} — the tape lines other than the " +
            "kept loader's should be geometry only");
        Expect(problems, detents.Count == 1, $"expected 1 RollerSnap, found {detents.Count}");
        Expect(problems, spawners.Count == 1, $"expected 1 RobotSpawner, found {spawners.Count}");

        if (detents.Count == 1)
        {
            // LiteScene is built from SampleScene, so a roller tuned in code but not baked into
            // SampleScene arrives here stale too — and FieldFeatureValidation only ever opens
            // SampleScene, so this is the only check the Lite copy gets.
            bool detentMatches = AttachRollerDetents.MatchesCodeDefaults(detents[0], out string detentDiff);
            Expect(problems, detentMatches,
                $"the kept RollerSnap disagrees with RollerSnap.cs ({detentDiff}) — run Attach or Tune " +
                "Roller Detents on SampleScene, then rebuild");
        }

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
        if (spawners.Count == 1)
        {
            SerializedObject spawnerSo = new SerializedObject(spawners[0]);
            Expect(problems, IsRefSet(spawnerSo, "catalog"),
                "RobotSpawner lost its RobotModelCatalog — no robot would spawn");
            // Not the same severity as losing the catalog, and far harder to see: without the config
            // a downloaded robot can't even start its download, so the spawner quietly puts a
            // different robot on the field and says so only in the console.
            Expect(problems, IsRefSet(spawnerSo, "uploadConfig"),
                "RobotSpawner lost its RobotUploadConfig — a downloaded robot would spawn as some " +
                "other robot instead");
        }

        // Goals: one of each size, each with the anchor its magnet stacks against.
        Expect(problems, magnets.Count == expected.Goals,
            $"expected {expected.Goals} goals, found {magnets.Count}");
        var goalNodes = new List<Transform>();
        foreach (GoalStackMagnet magnet in magnets)
        {
            goalNodes.Add(magnet.transform);
            Expect(problems, magnet.stackAnchor != null, $"'{magnet.name}' lost its stackAnchor");
        }
        List<List<Transform>> bands = GoalHeightBands(goalNodes);
        Expect(problems, bands.Count == magnets.Count,
            $"the {magnets.Count} kept goals fall into {bands.Count} height band(s), not {magnets.Count} " +
            "— two of them are the same size, so the lite field is missing a size of goal to score on");

        if (fieldRoot == null) return problems.ToString();

        // Pieces: one of each arrangement, and nothing left over from any other.
        Bounds surface = PlayingSurface(fieldRoot.Find("FloorTiles"), fieldRoot);
        List<Piece> cups = Measure(InstancesOf<PieceStackMagnet>(fieldRoot.Find("Cups")));
        List<Piece> pins = Measure(PinInstances(fieldRoot.Find("Pins")));
        List<Structure> rows = WallRows(cups, pins);
        List<Structure> rings = PinRings(cups, pins);
        List<Structure> stacks = StackedSingles(cups, pins);

        Expect(problems, rows.Count == 1,
            $"expected 1 row of cups with a pin standing in the middle one, found {rows.Count}");
        Expect(problems, rings.Count == 1,
            $"expected 1 cup ringed by pins lying flat, found {rings.Count}");
        Expect(problems, stacks.Count == 1,
            $"expected 1 lone cup with a pin standing in it, found {stacks.Count}");
        Expect(problems, cups.Count == expected.Cups,
            $"expected {expected.Cups} cups, found {cups.Count}");
        Expect(problems, pins.Count == expected.Pins,
            $"expected {expected.Pins} pins, found {pins.Count}");

        int accountedCups = 0;
        int accountedPins = 0;
        foreach (Structure structure in rows) { accountedCups += structure.Cups.Count; accountedPins += structure.Pins.Count; }
        foreach (Structure structure in rings) { accountedCups += structure.Cups.Count; accountedPins += structure.Pins.Count; }
        foreach (Structure structure in stacks) { accountedCups += structure.Cups.Count; accountedPins += structure.Pins.Count; }
        Expect(problems, accountedCups == cups.Count && accountedPins == pins.Count,
            $"{cups.Count - accountedCups} cup(s) and {pins.Count - accountedPins} pin(s) are not part of " +
            "any structure — a structure was cut in half, or a stray piece survived");

        // The four quadrant structures each got their own quadrant.
        if (rows.Count == 1 && rings.Count == 1 && bands.Count >= 3)
        {
            var corners = new List<string>
            {
                Where(surface, rows[0]),
                Where(surface, rings[0]),
                Where(surface, Band(bands, 1)[0]),
                Where(surface, Band(bands, 2)[0]),
            };
            var distinct = new HashSet<string>(corners);
            Expect(problems, distinct.Count == 4,
                $"the four quadrant structures share quadrants ({string.Join(" ", corners)}) — they were " +
                "meant to land one per quadrant so each has room around it");
        }

        // The perimeter, whole.
        Transform perimeter = fieldRoot.Find("Perimeter");
        Expect(problems, perimeter != null, "Perimeter is missing");
        if (perimeter != null)
        {
            Transform walls = perimeter.Find("Walls");
            Expect(problems, walls != null && walls.childCount == expected.WallPanels,
                $"expected all {expected.WallPanels} wall panels, found {(walls == null ? 0 : walls.childCount)}");

            Transform cornerGroup = perimeter.Find("Corners");
            Expect(problems, cornerGroup != null && cornerGroup.childCount == expected.Corners,
                $"expected all {expected.Corners} corners, found {(cornerGroup == null ? 0 : cornerGroup.childCount)}");

            Transform wallColliders = perimeter.Find("WallColliders");
            Expect(problems, wallColliders != null,
                "Perimeter/WallColliders is missing (spawn clamp + containment)");
            if (wallColliders != null)
            {
                int boxes = wallColliders.GetComponentsInChildren<BoxCollider>(true).Length;
                Expect(problems, boxes >= 4,
                    $"Perimeter/WallColliders has {boxes} box colliders, expected at least 4");
            }
        }

        // The tape lines, all of them, still visible.
        Transform tapes = fieldRoot.Find("TapeDetectors");
        Expect(problems, tapes != null && tapes.childCount == expected.TapeLines,
            $"expected all {expected.TapeLines} tape lines, found {(tapes == null ? 0 : tapes.childCount)}");
        if (tapes != null)
        {
            int drawn = tapes.GetComponentsInChildren<MeshRenderer>(true).Length;
            Expect(problems, drawn >= expected.TapeLines,
                $"only {drawn} of {expected.TapeLines} tape lines still have a renderer — stripping the " +
                "orphan triggers took the visible line with it");
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

    private static Vector3 Flat(Vector3 v)
    {
        return new Vector3(v.x, 0f, v.z);
    }

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

    // --- Reporting ---

    // Which quadrant of the playing surface a point is in. Deliberately written as axis signs rather
    // than compass letters: the field's own object names disagree with the axes (GoalNeutralWest sits
    // at +X), so "west" in a report would mean two different things on the same line.
    private static string Where(Bounds surface, Vector3 point)
    {
        string x = point.x >= surface.center.x ? "+X" : "-X";
        string z = point.z >= surface.center.z ? "+Z" : "-Z";
        return $"({x},{z})";
    }

    private static string Where(Bounds surface, Transform node)
    {
        return node == null ? "(none)" : Where(surface, WorldCenter(node));
    }

    private static string Where(Bounds surface, Structure structure)
    {
        return structure == null ? "(none)" : Where(surface, structure.Center);
    }

    private static string Name(Transform node)
    {
        return node != null ? node.name : "(none)";
    }

    private static string Label(Structure structure)
    {
        return structure != null ? structure.Label : "(none)";
    }

    private static int CountNonNull(List<Transform> nodes)
    {
        var distinct = new HashSet<Transform>();
        foreach (Transform node in nodes)
        {
            if (node != null) distinct.Add(node);
        }
        return distinct.Count;
    }

    private static int CountCups(Structure structure)
    {
        return structure != null ? structure.Cups.Count : 0;
    }

    private static int CountPins(Structure structure)
    {
        return structure != null ? structure.Pins.Count : 0;
    }
}
