using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Collections.Generic;

// Field-setup editor tools, grouped under Tools > RoboSim > Field & Pieces:
//   • FixFieldColliders — Rebuild Floor and Wall Bounds
//   • FixGoals          — Rebuild Goal Colliders
//   • FixRollers        — Rig Rollers (Hinge Joints)
// These one-shot tools build the field's physics from the imported meshes; they don't depend on
// each other and were merged into one file purely to tidy the Editor folder.

// Builds a solid, thick collision shell around the field and removes the fragile imported
// colliders it replaces.
//
// The imported floor is thin (1cm) tile colliders and the walls are thin concave mesh panels,
// so game pieces make only shallow contact, sink/wedge into the floor (the robot then rides
// over them), and fast pieces tunnel through the walls. This tool:
//   - strips every MeshCollider under FloorTiles and Walls (the thin panels pieces clip through),
//   - adds a thick ground box under FloorTiles,
//   - adds four thick perimeter wall boxes under Perimeter, set slightly inward from the floor edge.
//
// Re-runnable: it removes its previous output first. The collider host objects are given a
// world-identity transform (via SetParent worldPositionStays), so BoxCollider centers are plain
// world coordinates even though the field root is rotated -90 X.
public class FixFieldColliders
{
    private const float GroundThickness = 2f;
    private const float WallThickness = 2f;
    private const float WallHeight = 3f;   // shorter so the walls clear the field rollers
    private const float WallInset = 0.2f;  // pull walls this far in from the floor edge, toward center

    private const string GroundName = "GroundCollider";
    private const string WallsName = "WallColliders";
    private const string LegacyRootName = "FieldPhysicsBounds"; // from the earlier version of this tool

    [MenuItem("Tools/RoboSim/Field & Pieces/Rebuild Floor and Wall Bounds", false, 1)]
    private static void SetupBounds()
    {
        GameObject floorTiles = GameObject.Find("FloorTiles");
        GameObject perimeter = GameObject.Find("Perimeter");
        GameObject walls = GameObject.Find("Walls");
        if (floorTiles == null || perimeter == null)
        {
            EditorUtility.DisplayDialog("Setup Field Physics Bounds",
                "Couldn't find 'FloorTiles' and/or 'Perimeter' in the scene.", "OK");
            return;
        }

        // Floor world bounds from its renderers (robust to the collider removal below).
        if (!TryGetRendererBounds(floorTiles, out Bounds floor))
        {
            EditorUtility.DisplayDialog("Setup Field Physics Bounds",
                "FloorTiles has no renderers to measure the floor from.", "OK");
            return;
        }

        // Clean up previous output so re-running doesn't stack duplicates.
        DestroyIfExists(LegacyRootName);
        DestroyChildIfExists(floorTiles.transform, GroundName);
        DestroyChildIfExists(perimeter.transform, WallsName);

        // Strip the thin imported colliders that the solid boxes replace. On the floor remove
        // ALL colliders (the 1cm tile boxes too) — leaving them coplanar with the new ground box
        // makes the solver flip-flop and wedge pieces into the floor. On the walls remove the
        // thin mesh panels.
        int removed = RemoveAllColliders(floorTiles);
        if (walls != null) removed += RemoveMeshColliders(walls);

        float floorTop = floor.max.y;
        Vector3 c = floor.center;
        float width = floor.size.x;   // X span
        float depth = floor.size.z;   // Z span
        float minX = floor.min.x, maxX = floor.max.x;
        float minZ = floor.min.z, maxZ = floor.max.z;

        // Ground: top flush with the floor surface, extending downward, parented under FloorTiles.
        GameObject ground = CreateWorldIdentityChild(floorTiles.transform, GroundName);
        AddBox(ground, new Vector3(c.x, floorTop - GroundThickness * 0.5f, c.z),
                       new Vector3(width, GroundThickness, depth));

        // Four perimeter walls under Perimeter. Inner faces sit WallInset in from the floor edge.
        // The +/-X walls run the full Z length so they overlap the +/-Z walls at the corners.
        GameObject wallHost = CreateWorldIdentityChild(perimeter.transform, WallsName);
        float wallCenterY = floorTop + WallHeight * 0.5f;
        float longDepth = depth + WallThickness * 2f;

        AddBox(wallHost, new Vector3(maxX - WallInset + WallThickness * 0.5f, wallCenterY, c.z),
                         new Vector3(WallThickness, WallHeight, longDepth));   // +X
        AddBox(wallHost, new Vector3(minX + WallInset - WallThickness * 0.5f, wallCenterY, c.z),
                         new Vector3(WallThickness, WallHeight, longDepth));   // -X
        AddBox(wallHost, new Vector3(c.x, wallCenterY, maxZ - WallInset + WallThickness * 0.5f),
                         new Vector3(width, WallHeight, WallThickness));       // +Z
        AddBox(wallHost, new Vector3(c.x, wallCenterY, minZ + WallInset - WallThickness * 0.5f),
                         new Vector3(width, WallHeight, WallThickness));       // -Z

        EditorSceneManager.MarkSceneDirty(floorTiles.scene);
        Selection.activeGameObject = wallHost;

        Debug.Log($"Setup Field Physics Bounds: removed {removed} mesh collider(s) from floor/walls; " +
                  $"added ground box under FloorTiles and 4 walls (inset {WallInset}) under Perimeter. " +
                  $"floorTop={floorTop:F2}, footprint {width:F1}×{depth:F1}.");
    }

    private static bool TryGetRendererBounds(GameObject go, out Bounds bounds)
    {
        bounds = new Bounds();
        Renderer[] rs = go.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return false;
        bounds = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) bounds.Encapsulate(rs[i].bounds);
        return true;
    }

    private static int RemoveMeshColliders(GameObject go)
    {
        MeshCollider[] cols = go.GetComponentsInChildren<MeshCollider>(true);
        foreach (MeshCollider col in cols) Undo.DestroyObjectImmediate(col);
        return cols.Length;
    }

    private static int RemoveAllColliders(GameObject go)
    {
        Collider[] cols = go.GetComponentsInChildren<Collider>(true);
        foreach (Collider col in cols) Undo.DestroyObjectImmediate(col);
        return cols.Length;
    }

    private static void DestroyIfExists(string name)
    {
        GameObject go = GameObject.Find(name);
        if (go != null) Undo.DestroyObjectImmediate(go);
    }

    private static void DestroyChildIfExists(Transform parent, string name)
    {
        Transform child = parent.Find(name);
        if (child != null) Undo.DestroyObjectImmediate(child.gameObject);
    }

    // A child whose WORLD transform is identity, so its BoxCollider centers are world coordinates
    // even under the rotated field hierarchy.
    private static GameObject CreateWorldIdentityChild(Transform parent, string name)
    {
        GameObject go = new GameObject(name); // spawns at world origin, identity rotation
        Undo.RegisterCreatedObjectUndo(go, "Setup Field Physics Bounds");
        go.transform.SetParent(parent, true); // keep world-identity; local compensates for rotation
        return go;
    }

    private static void AddBox(GameObject host, Vector3 worldCenter, Vector3 size)
    {
        BoxCollider box = Undo.AddComponent<BoxCollider>(host);
        box.center = worldCenter;
        box.size = size;
    }
}

public class FixGoals : EditorWindow
{
    [MenuItem("Tools/RoboSim/Field & Pieces/Rebuild Goal Colliders", false, 2)]
    public static void ShowWindow()
    {
        GetWindow<FixGoals>("Goal Fixer");
    }

    private void OnGUI()
    {
        GUILayout.Label("VEX Goal Collider Generator & Clean-up", EditorStyles.boldLabel);
        GUILayout.Label("Select an individual goal or an entire parent folder.", EditorStyles.miniLabel);
        GUILayout.Space(10);

        // Alliance parameters modified to generate both the upper sloped walls AND the lower short flat rim
        if (GUILayout.Button("Spawn Alliance Goal Setup (Short)", GUILayout.Height(40)))
        {
            ProcessSelection(
                assemblyRotation: new Vector3(-90f, 0f, 0f),
                outerCardinalRadius: 0.45f, outerDiagonalRadius: 0.6f,
                outerHeight: 0.75f, outerZShift: 0.45f, cardWidth: 0.5f, diagWidth: 0.3f,
                outerDiagTilt: -18.0f, // Upper diagonal tilt
                innerRadius: 0.25f, innerHeight: 0.83f, innerZShift: 0.405f,
                goalType: "Alliance",

                // NEW: Parameters dedicated strictly to your second, flat lower ring base
                hasLowerBase: true,
                lowerCardinalRadius: 0.705f, lowerDiagonalRadius: 0.785f,
                lowerHeight: 0.11f, lowerZShift: 0.04f, lowerCardWidth: 0.8f, lowerDiagWidth: 0.4f
            );
        }

        GUILayout.Space(5);

        // YOUR PERFECTLY TUNED EXACT NUMBERS!
        if (GUILayout.Button("Spawn Neutral Goal Setup (Standard)", GUILayout.Height(40)))
        {
            ProcessSelection(
                assemblyRotation: new Vector3(-90f, 0f, 0f),
                outerCardinalRadius: 0.705f, outerDiagonalRadius: 0.785f,
                outerHeight: 0.7f, outerZShift: -0.28f, cardWidth: 0.8f, diagWidth: 0.4f,
                outerDiagTilt: 0f,
                innerRadius: 0.25f, innerHeight: 0.83f, innerZShift: 0.405f,
                goalType: "Neutral",

                // Unused for Neutral
                hasLowerBase: false, 0f, 0f, 0f, 0f, 0f, 0f
            );
        }

        GUILayout.Space(5);

        // Central goals scaled up significantly across all dimensions
        if (GUILayout.Button("Spawn Central Goal Setup (Large)", GUILayout.Height(40)))
        {
            ProcessSelection(
                assemblyRotation: new Vector3(-90f, 0f, 0f),
                outerCardinalRadius: 0.705f, outerDiagonalRadius: 0.785f,
                outerHeight: 1.4f, outerZShift: -0.7f, cardWidth: .8f, diagWidth: 0.4f,
                outerDiagTilt: 0f,
                innerRadius: 0.25f, innerHeight: 0.83f, innerZShift: 0.405f,
                goalType: "Central",

                // Unused for Central
                hasLowerBase: false, 0f, 0f, 0f, 0f, 0f, 0f
            );
        }
    }

    private static void ProcessSelection(
        Vector3 assemblyRotation,
        float outerCardinalRadius, float outerDiagonalRadius,
        float outerHeight, float outerZShift, float cardWidth, float diagWidth,
        float outerDiagTilt,
        float innerRadius, float innerHeight, float innerZShift,
        string goalType,

        // Lower base parameters passed here to preserve structure signatures
        bool hasLowerBase,
        float lowerCardinalRadius, float lowerDiagonalRadius,
        float lowerHeight, float lowerZShift, float lowerCardWidth, float lowerDiagWidth)
    {
        GameObject selection = Selection.activeGameObject;

        if (selection == null)
        {
            EditorUtility.DisplayDialog("Selection Error", "Please select a Goal object or folder first.", "OK");
            return;
        }

        List<GameObject> targetsToProcess = new List<GameObject>();

        bool isFolderFolder = false;
        foreach (Transform child in selection.transform)
        {
            foreach (Transform sub in child)
            {
                if (sub.name.StartsWith("MeshInstance") || sub.name.StartsWith("GoalCenter")) { isFolderFolder = true; break; }
            }
            if (isFolderFolder) break;
        }

        if (isFolderFolder)
        {
            foreach (Transform child in selection.transform) targetsToProcess.Add(child.gameObject);
        }
        else
        {
            targetsToProcess.Add(selection);
        }

        int processedCount = 0;

        foreach (GameObject targetGoal in targetsToProcess)
        {
            Undo.RegisterCompleteObjectUndo(targetGoal, $"Clean and Generate {goalType} Goal");

            MeshCollider[] existingMeshColliders = targetGoal.GetComponentsInChildren<MeshCollider>(true);
            foreach (MeshCollider mc in existingMeshColliders)
            {
                Undo.DestroyObjectImmediate(mc);
            }

            List<GameObject> oldGeneratedObjects = new List<GameObject>();
            foreach (Transform child in targetGoal.transform)
            {
                if (child.name.StartsWith("GoalWall_") || child.name.StartsWith("GoalFloor"))
                    oldGeneratedObjects.Add(child.gameObject);
            }
            foreach (GameObject old in oldGeneratedObjects) Undo.DestroyObjectImmediate(old);

            Transform meshInstanceChild = null;
            foreach (Transform subChild in targetGoal.transform)
            {
                if (subChild.name.StartsWith("GoalCenter") || subChild.name.StartsWith("MeshInstance"))
                {
                    meshInstanceChild = subChild;
                    break;
                }
            }

            float wallThickness = 0.01f;
            float innerTaperAngle = 6.5f;

            Vector3 basePosition = (meshInstanceChild != null) ? meshInstanceChild.position : targetGoal.transform.position;
            Quaternion rawRotation = (meshInstanceChild != null) ? meshInstanceChild.rotation : targetGoal.transform.rotation;
            Quaternion correctedOrientation = rawRotation * Quaternion.Euler(assemblyRotation);

            Vector3 upAxis = correctedOrientation * Vector3.forward;

            Vector3 outerAssemblyCenter = basePosition + (upAxis * outerZShift);
            Vector3 innerAssemblyCenter = basePosition + (upAxis * innerZShift);

            // ===================================================================
            // STEP 2A: MAIN/UPPER OUTER BUMPER WALL GENERATION
            // ===================================================================
            int outerSides = 8;
            for (int i = 0; i < outerSides; i++)
            {
                GameObject wall = new GameObject($"GoalWall_Outer_Octagon_{i}");
                Undo.RegisterCreatedObjectUndo(wall, "Create Outer Goal Wall");
                wall.transform.SetParent(targetGoal.transform);

                wall.transform.position = outerAssemblyCenter;
                wall.transform.rotation = correctedOrientation * Quaternion.Euler(0f, 0f, i * (360f / outerSides));

                bool isCardinal = (i % 2 == 0);
                float currentRadius = isCardinal ? outerCardinalRadius : outerDiagonalRadius;

                wall.transform.Translate(0f, currentRadius, 0f, Space.Self);

                if (!isCardinal && outerDiagTilt != 0f)
                {
                    wall.transform.Rotate(-outerDiagTilt, 0f, 0f, Space.Self);
                }

                BoxCollider box = wall.AddComponent<BoxCollider>();
                float currentWidth = isCardinal ? cardWidth : diagWidth;

                box.size = new Vector3(currentWidth, wallThickness, outerHeight);
            }

            // ===================================================================
            // STEP 2B: LOWER BASE RIM GENERATION (8 PIECES - UNTILTED)
            // ===================================================================
            if (hasLowerBase)
            {
                Vector3 lowerAssemblyCenter = basePosition + (upAxis * lowerZShift);

                for (int i = 0; i < outerSides; i++)
                {
                    GameObject wall = new GameObject($"GoalWall_Lower_Base_Octagon_{i}");
                    Undo.RegisterCreatedObjectUndo(wall, "Create Lower Base Goal Wall");
                    wall.transform.SetParent(targetGoal.transform);

                    wall.transform.position = lowerAssemblyCenter;
                    wall.transform.rotation = correctedOrientation * Quaternion.Euler(0f, 0f, i * (360f / outerSides));

                    bool isCardinal = (i % 2 == 0);
                    float currentRadius = isCardinal ? lowerCardinalRadius : lowerDiagonalRadius;

                    wall.transform.Translate(0f, currentRadius, 0f, Space.Self);

                    // No tilt applied here—stays perfectly flat-vertical like the neutral goals!

                    BoxCollider box = wall.AddComponent<BoxCollider>();
                    float currentWidth = isCardinal ? lowerCardWidth : lowerDiagWidth;

                    box.size = new Vector3(currentWidth, wallThickness, lowerHeight);
                }
            }

            // ===================================================================
            // STEP 3: INNER POCKET GENERATION
            // ===================================================================
            int innerSides = 6;
            for (int i = 0; i < innerSides; i++)
            {
                GameObject wall = new GameObject($"GoalWall_Inner_Pocket_{i}");
                Undo.RegisterCreatedObjectUndo(wall, "Create Inner Goal Wall");
                wall.transform.SetParent(targetGoal.transform);

                wall.transform.position = innerAssemblyCenter;
                wall.transform.rotation = correctedOrientation * Quaternion.Euler(0f, 0f, i * (360f / innerSides));

                wall.transform.Translate(0f, innerRadius, 0f, Space.Self);
                wall.transform.Rotate(-innerTaperAngle, 0f, 180f, Space.Self);

                BoxCollider box = wall.AddComponent<BoxCollider>();
                float innerPanelWidth = (i % 2 == 0) ? 0.38f : 0.30f;
                box.size = new Vector3(innerPanelWidth, wallThickness, innerHeight);
            }

            // ===================================================================
            // STEP 4: GOAL FLOOR BASE
            // ===================================================================
            GameObject floor = new GameObject("GoalFloor_Base");
            Undo.RegisterCreatedObjectUndo(floor, "Create Goal Floor");
            floor.transform.SetParent(targetGoal.transform);

            floor.transform.position = innerAssemblyCenter - (upAxis * (innerHeight * 0.5f));
            floor.transform.rotation = correctedOrientation;

            BoxCollider floorBox = floor.AddComponent<BoxCollider>();
            floorBox.size = new Vector3(innerRadius * 2f, innerRadius * 2f, 0.02f);

            EditorUtility.SetDirty(targetGoal);
            processedCount++;
        }

        EditorUtility.DisplayDialog("Success!", $"Processed {processedCount} target objects. Cleaned old mesh colliders and generated optimized shapes!", "Awesome");
    }
}

// Attaches the RollerSnap detent to the 4 field rollers' hinge bodies. The saved scene was rigged by
// FixRollers at some point but WITHOUT the RollerSnap component (its guid appears nowhere in the scene),
// so the rollers spin freely today. Re-running the full Rig Rollers tool would work too, but it
// regenerates bodies/joints/colliders from a selection and can't run headless — this targeted fix only
// ensures the detent component and its damping. Idempotent: re-running syncs the existing components.
// Batch: -executeMethod FixRollerDetents.RunBatch (opens and saves SampleScene).
public static class FixRollerDetents
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private static readonly string[] RollerNames = { "RollerNorth", "RollerSouth", "RollerEast", "RollerWest" };

    [MenuItem("Tools/RoboSim/Field & Pieces/Attach Roller Detents (Scene Fix)", false, 4)]
    private static void AttachInteractive()
    {
        int touched = Apply(useUndo: true);
        EditorUtility.DisplayDialog("Attach Roller Detents",
            touched > 0
                ? $"RollerSnap detents ensured on {touched} roller(s). Save the scene to keep them."
                : "No rigged rollers found — open SampleScene and run Rig Rollers (Hinge Joints) first.",
            "OK");
    }

    // Batch entry point for -executeMethod: throws on failure (nonzero exit).
    public static void RunBatch()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        int touched = Apply(useUndo: false);
        if (touched == 0)
            throw new System.InvalidOperationException(
                $"Attach Roller Detents: no rigged rollers found in {ScenePath}.");
        if (!EditorSceneManager.SaveScene(scene))
            throw new System.InvalidOperationException($"Attach Roller Detents: failed to save {ScenePath}.");
        Debug.Log($"Attach Roller Detents: RollerSnap ensured on {touched} roller(s); scene saved.");
    }

    private static int Apply(bool useUndo)
    {
        int touched = 0;
        foreach (string name in RollerNames)
        {
            GameObject roller = GameObject.Find(name);
            if (roller == null)
            {
                Debug.LogWarning($"Attach Roller Detents: no '{name}' in the open scene — skipped.");
                continue;
            }

            // The hinge body (RollerFace*) is the spinning link the detent belongs on.
            HingeJoint hinge = roller.GetComponentInChildren<HingeJoint>(true);
            if (hinge == null)
            {
                Debug.LogWarning($"Attach Roller Detents: '{name}' has no HingeJoint — run Rig Rollers first; skipped.");
                continue;
            }

            GameObject face = hinge.gameObject;
            RollerSnap snap = face.GetComponent<RollerSnap>();
            if (snap == null)
                snap = useUndo ? Undo.AddComponent<RollerSnap>(face) : face.AddComponent<RollerSnap>();

            Rigidbody rb = face.GetComponent<Rigidbody>();
            if (rb != null)
            {
                if (useUndo) Undo.RecordObject(rb, "Attach Roller Detents");
                rb.angularDamping = snap.FreeSpinDamping;
            }

            EditorUtility.SetDirty(face);
            EditorSceneManager.MarkSceneDirty(face.scene);
            touched++;
        }
        return touched;
    }
}

public class FixRollers : EditorWindow
{
    [MenuItem("Tools/RoboSim/Field & Pieces/Rig Rollers (Hinge Joints)", false, 3)]
    public static void ShowWindow()
    {
        GetWindow<FixRollers>("Roller Fixer");
    }

    private void OnGUI()
    {
        GUILayout.Label("VEX Dynamic Roller Rigging Tool", EditorStyles.boldLabel);
        GUILayout.Label("Select individual Rollers or the entire parent folder.", EditorStyles.miniLabel);
        GUILayout.Space(10);

        if (GUILayout.Button("Assemble Selected Rollers", GUILayout.Height(40)))
        {
            ProcessSelection();
        }
    }

    private static void ProcessSelection()
    {
        GameObject selection = Selection.activeGameObject;

        if (selection == null)
        {
            EditorUtility.DisplayDialog("Selection Error", "Please select a Roller object or folder first.", "OK");
            return;
        }

        // ===================================================================
        //   MANUAL CONTROLS: ADJUST INDEPENDENT ROLLER DIMENSIONS
        // ===================================================================
        float rollerRadius = 0.15f;     // Extrusion radius from center axle
        float rollerLength = 6f;        // Horizontal length along field parallel alignment
        float panelWidth = 0.5f;        // Face thickness span for each of the 3 panels
        float wallThickness = 0.04f;    // Thickness of the individual box panels

        // If your 3-sided mesh faces ever need a universal slight rotational nudge,
        // you can change this offset value here. Default is clean zero.
        Vector3 assemblyRotationOffset = new Vector3(0f, 0f, 0f);
        // ===================================================================

        List<GameObject> targetsToProcess = new List<GameObject>();

        // Smart Selection Handler: Checks if a root folder is highlighted
        bool isFolder = false;
        foreach (Transform child in selection.transform)
        {
            if (child.name.StartsWith("RollerFrame") || child.name.Contains("Roller"))
            {
                isFolder = true;
                break;
            }
        }

        if (isFolder && !selection.name.StartsWith("RollerNorth") && !selection.name.StartsWith("RollerSouth") && !selection.name.StartsWith("RollerEast") && !selection.name.StartsWith("RollerWest"))
        {
            foreach (Transform child in selection.transform) targetsToProcess.Add(child.gameObject);
        }
        else
        {
            // If a nested child panel or component was clicked, climb out to find the true directional root
            GameObject trueTarget = selection;
            while (trueTarget.transform.parent != null && !trueTarget.name.StartsWith("RollerNorth") && !trueTarget.name.StartsWith("RollerSouth") && !trueTarget.name.StartsWith("RollerEast") && !trueTarget.name.StartsWith("RollerWest"))
            {
                trueTarget = trueTarget.transform.parent.gameObject;
            }
            targetsToProcess.Add(trueTarget);
        }

        int processedCount = 0;

        foreach (GameObject rollerParent in targetsToProcess)
        {
            Undo.RegisterCompleteObjectUndo(rollerParent, "Rig Roller Physics");

            Transform faceTarget = null;
            foreach (Transform child in rollerParent.transform)
            {
                if (child.name.StartsWith("RollerFace")) faceTarget = child; //
            }

            if (faceTarget == null) continue;

            // 1. Clear old runtime components smoothly
            RollerSnap oldSnapScript = faceTarget.GetComponent<RollerSnap>();
            if (oldSnapScript != null) Undo.DestroyObjectImmediate(oldSnapScript);

            // 2. Strip messy joints and rigidbodies to prevent physics binding
            Rigidbody[] existingBodies = rollerParent.GetComponentsInChildren<Rigidbody>(true);
            foreach (Rigidbody rb in existingBodies) Undo.DestroyObjectImmediate(rb);

            Joint[] existingJoints = rollerParent.GetComponentsInChildren<Joint>(true);
            foreach (Joint j in existingJoints) Undo.DestroyObjectImmediate(j);

            // 3. CLEANUP BUG FIX: Find and vaporize any old master pivot containers to completely avoid duplicates
            Transform oldPivot = faceTarget.Find("Roller_Collider_Pivot");
            if (oldPivot != null) Undo.DestroyObjectImmediate(oldPivot.gameObject);

            // Also clean up loose old-style nested face panels if they exist
            List<GameObject> loosePanels = new List<GameObject>();
            foreach (Transform child in faceTarget)
            {
                if (child.name.StartsWith("RollerFace_Panel_")) loosePanels.Add(child.gameObject);
            }
            foreach (GameObject lp in loosePanels) Undo.DestroyObjectImmediate(lp);

            // 4. Strip any leftover stray colliders
            Collider[] existingColliders = rollerParent.GetComponentsInChildren<Collider>(true);
            foreach (Collider c in existingColliders) Undo.DestroyObjectImmediate(c);

            // 5. Track down visual MeshInstance baseline to extract precise local coordinates
            Transform meshInstanceChild = null;
            foreach (Transform subChild in faceTarget)
            {
                if (subChild.name.StartsWith("MeshInstance"))
                {
                    meshInstanceChild = subChild;
                    break;
                }
            }

            Vector3 targetPivotLocalPos = (meshInstanceChild != null) ? meshInstanceChild.localPosition : Vector3.zero;
            Quaternion targetPivotLocalRot = (meshInstanceChild != null) ? meshInstanceChild.localRotation : Quaternion.identity;

            // ===================================================================
            // CONFIGURATION: GENERATE SYNCHRONIZED REF PIVOT CONTAINER
            // ===================================================================
            GameObject colliderPivot = new GameObject("Roller_Collider_Pivot");
            Undo.RegisterCreatedObjectUndo(colliderPivot, "Create Roller Collider Pivot");
            colliderPivot.transform.SetParent(faceTarget);

            // Automatically adapts your green wireframe boxes perfectly to match the rotated meshes
            colliderPivot.transform.localPosition = targetPivotLocalPos;
            colliderPivot.transform.localRotation = targetPivotLocalRot * Quaternion.Euler(assemblyRotationOffset);

            // ===================================================================
            // STEP 1: GENERATE DYNAMIC 3-SIDED COLLIDER PANELS
            // ===================================================================
            int sides = 3;

            for (int i = 0; i < sides; i++)
            {
                GameObject triFace = new GameObject($"RollerFace_Panel_{i}");
                Undo.RegisterCreatedObjectUndo(triFace, "Create Roller Face Panel");

                triFace.transform.SetParent(colliderPivot.transform);
                triFace.transform.localPosition = Vector3.zero;
                triFace.transform.localRotation = Quaternion.Euler(i * (360f / sides), 0f, 0f);

                triFace.transform.Translate(0f, rollerRadius, 0f, Space.Self);

                BoxCollider faceBox = triFace.AddComponent<BoxCollider>();
                faceBox.size = new Vector3(rollerLength, wallThickness, panelWidth);
            }

            // ===================================================================
            // STEP 2: RIGIDBODY & CRITICAL JOINT FIX
            // ===================================================================
            Rigidbody spinnerRb = faceTarget.gameObject.AddComponent<Rigidbody>();
            spinnerRb.isKinematic = false;
            spinnerRb.useGravity = false;
            spinnerRb.angularDamping = 0.5f;

            HingeJoint hinge = faceTarget.gameObject.AddComponent<HingeJoint>();

            // Lock anchor precisely down the local middle center line of the mesh
            hinge.anchor = targetPivotLocalPos;

            // Forces a pristine, perfectly un-skewed local spin vector parallel to the axle
            hinge.axis = new Vector3(1f, 0f, 0f);

            // ===================================================================
            // STEP 3: ATTACH ROTATIONAL DETENT SNAP SCRIPT
            // ===================================================================
            faceTarget.gameObject.AddComponent<RollerSnap>();

            EditorUtility.SetDirty(rollerParent);
            processedCount++;
        }

        EditorUtility.DisplayDialog("Success!", $"Perfect structure and clean local physics set up across {processedCount} Rollers!", "Awesome");
    }
}
