using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Collections.Generic;
using System.IO;

// Game-piece editor tools, grouped under Tools > RoboSim > Field & Pieces:
//   • FixCups         — Rebuild Cup Colliders
//   • FixAllPins      — Rebuild Pin Colliders (Any Color)
//   • FixPieceClamps  — Add Floor Clamp to Pieces
//   • TunePiecePhysics— Tune Roll and Friction
// One-shot tools that build/tune the cup & pin game pieces. Independent of each other; merged into
// one file to tidy the Editor folder.

public class FixCups : EditorWindow
{
    [MenuItem("Tools/RoboSim/Field & Pieces/Rebuild Cup Colliders", false, 20)]
    public static void CleanAndOptimizeCups()
    {
        GameObject selectedParent = Selection.activeGameObject;

        if (selectedParent == null)
        {
            EditorUtility.DisplayDialog("Selection Error", "Please select the 'Cups' parent object first.", "OK");
            return;
        }

        // ===================================================================
        // MANUAL CONTROLS: ADJUST HEIGHT, GAP, AND MASTER ROTATION
        // ===================================================================

        // 1. MASTER ASSEMBLY ROTATION
        // This rotates the ENTIRE collider system together around the cup's pivot point.
        // Change these values to find which axis snaps it to the cup length!
        // Try configurations like: (90f, 0f, 0f), (0f, 90f, 0f), or (0f, 0f, 90f)
        Vector3 assemblyRotationOffset = new Vector3(90f, 0f, 0f);

        // 2. OVERALL HEIGHT SHIFT (Moves the whole assembly along its corrected axis)
        float globalVerticalShift = -0.45f;

        // 3. EXTRA BOTTOM GAP (Pushes the bottom row further down away from the top)
        float extraBottomSeparation = 0.80f;

        // 4. COLLIDER BOX DIMENSIONS
        float cupHeight = 0.8f;
        float wallThickness = 0.01f;

        // 5. RADIAL FIT
        float outerRadius = 0.25f;
        float innerRadius = 0.25f;
        float taperAngle = 6.5f;

        // ===================================================================

        int fixedCount = 0;
        Undo.RegisterCompleteObjectUndo(selectedParent, "Fix Cups Spacing and Orientation");

        foreach (Transform child in selectedParent.transform)
        {
            Collider[] oldColliders = child.GetComponents<Collider>();
            foreach (var col in oldColliders) Undo.DestroyObjectImmediate(col);

            List<GameObject> oldGeneratedChildren = new List<GameObject>();
            foreach (Transform subChild in child)
            {
                if (subChild.name.StartsWith("CupWall_")) oldGeneratedChildren.Add(subChild.gameObject);
            }
            foreach (GameObject oldChild in oldGeneratedChildren) Undo.DestroyObjectImmediate(oldChild);

            Transform meshInstanceChild = null;
            foreach (Transform subChild in child)
            {
                if (subChild.name.StartsWith("MeshInstance"))
                {
                    meshInstanceChild = subChild;
                    break;
                }
            }

            if (meshInstanceChild == null) continue;

            Vector3 meshBaseCenter = meshInstanceChild.position;

            // FIX: Calculate a master corrected orientation using your manual offset variable
            Quaternion correctedOrientation = meshInstanceChild.rotation * Quaternion.Euler(assemblyRotationOffset);

            // Calculate direction vectors using the corrected unit matrix
            Vector3 worldUpDirection = correctedOrientation * Vector3.forward;

            // Apply placement offsets along the synchronized axis
            Vector3 worldTopCenter = meshBaseCenter + (worldUpDirection * globalVerticalShift);
            Vector3 worldBottomCenter = worldTopCenter - (worldUpDirection * extraBottomSeparation);

            int sides = 6;

            GenerateCupWalls(child, "Top", worldTopCenter, correctedOrientation, sides, outerRadius, innerRadius, -taperAngle, wallThickness, cupHeight);

            GenerateCupWalls(child, "Bottom", worldBottomCenter, correctedOrientation, sides, outerRadius, innerRadius, taperAngle, wallThickness, cupHeight);

            fixedCount++;
        }

        EditorUtility.SetDirty(selectedParent);
        EditorUtility.DisplayDialog("Success!", $"Successfully processed {fixedCount} cups!", "Awesome");
    }

    // Builds one taper-oriented ring of BoxCollider walls around a cup. The Top and Bottom passes
    // are identical apart from the section name, world center, and taper sign, so both call this.
    private static void GenerateCupWalls(Transform child, string section, Vector3 worldCenter, Quaternion correctedOrientation, int sides, float outerRadius, float innerRadius, float taperAngle, float wallThickness, float cupHeight)
    {
        for (int i = 0; i < sides; i++)
        {
            bool isWing = (i % 2 == 0);
            string sideType = isWing ? "Outer" : "Inner";

            GameObject colHolder = new GameObject($"CupWall_{section}_{sideType}_{i}");
            Undo.RegisterCreatedObjectUndo(colHolder, $"Create Cup {section} Wall");

            colHolder.transform.SetParent(child);
            colHolder.transform.position = worldCenter;
            colHolder.transform.rotation = correctedOrientation * Quaternion.Euler(0f, 0f, i * (360f / sides));

            float currentRadius = isWing ? outerRadius : innerRadius;
            float currentWidth = isWing ? 0.38f : 0.30f;

            colHolder.transform.Translate(0f, currentRadius, 0f, Space.Self);
            colHolder.transform.Rotate(taperAngle, 0f, 180f, Space.Self);

            BoxCollider box = colHolder.AddComponent<BoxCollider>();
            box.size = new Vector3(currentWidth, wallThickness, cupHeight);
        }
    }
}

public class FixAllPins : EditorWindow
{
    private struct CustomColliderDef
    {
        public string name;
        public Vector3 position;
        public Vector3 rotation;
    }

    [MenuItem("Tools/RoboSim/Field & Pieces/Rebuild Pin Colliders (Any Color)", false, 21)]
    public static void CleanAndOptimizePins()
    {
        GameObject selectedParent = Selection.activeGameObject;

        if (selectedParent == null)
        {
            EditorUtility.DisplayDialog("Selection Error", "Please select a pin parent folder first.", "OK");
            return;
        }

        Vector3 templateMeshLocalPos = new Vector3(0f, -5.9f, -2.05f); //

        CustomColliderDef[] relativeBottomDefs = new CustomColliderDef[]
        {
            new CustomColliderDef { name = "PinCollider_Wing_0",   position = new Vector3(0.059f, -5.684f, -2.461f) - templateMeshLocalPos, rotation = new Vector3(-10f, 3f, -13.5f) }, //
            new CustomColliderDef { name = "PinCollider_Indent_1", position = new Vector3(-0.119f, -5.776f, -2.465f) - templateMeshLocalPos, rotation = new Vector3(-2.777f, -3.271f, 45.351f) }, //
            new CustomColliderDef { name = "PinCollider_Wing_2",   position = new Vector3(-0.209f, -5.959f, -2.477f) - templateMeshLocalPos, rotation = new Vector3(2.779f, -9.485f, 107.578f) }, //
            new CustomColliderDef { name = "PinCollider_Indent_3", position = new Vector3(-0.044f, -6.072f, -2.466f) - templateMeshLocalPos, rotation = new Vector3(4.7f, -0.8f, -195f) }, //
            new CustomColliderDef { name = "PinCollider_Wing_4",   position = new Vector3(0.15f, -6.055f, -2.465f) - templateMeshLocalPos, rotation = new Vector3(6.345f, 6.637f, -137.179f) }, //
            new CustomColliderDef { name = "PinCollider_Indent_5", position = new Vector3(0.169f, -5.856f, -2.461f) - templateMeshLocalPos, rotation = new Vector3(-1.375f, 4.725f, -73.962f) } //
        };

        Vector3 boxSize = new Vector3(0.2f, 0.01f, 0.8f);
        int fixedCount = 0;

        Undo.RegisterCompleteObjectUndo(selectedParent, "Apply Precise Rotated Pin Colliders");

        foreach (Transform child in selectedParent.transform)
        {
            if (child.name.StartsWith("Pin"))
            {
                Rigidbody rb = child.GetComponent<Rigidbody>();
                if (rb == null) rb = child.gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = false;
                rb.useGravity = true;

                // Damping controls how quickly a knocked pin coasts/rolls to a stop. Keep it low so
                // pins roll a while when hit; the old 1/5 killed rolls almost instantly. The single
                // source of truth for piece feel is Tools > RoboSim > Field & Pieces > Tune Roll and Friction — keep these in sync.
                rb.linearDamping = 0.1f;   // Replaces rb.drag
                rb.angularDamping = 0.1f;  // Replaces rb.angularDrag

                Collider[] oldColliders = child.GetComponents<Collider>();
                foreach (var col in oldColliders) Undo.DestroyObjectImmediate(col);

                List<GameObject> oldGeneratedChildren = new List<GameObject>();
                foreach (Transform subChild in child)
                {
                    if (subChild.name.StartsWith("PinCollider_")) oldGeneratedChildren.Add(subChild.gameObject);
                }
                foreach (GameObject oldChild in oldGeneratedChildren) Undo.DestroyObjectImmediate(oldChild);

                Transform meshInstanceChild = null;
                foreach (Transform subChild in child)
                {
                    if (subChild.name.StartsWith("MeshInstance"))
                    {
                        meshInstanceChild = subChild;
                        break;
                    }
                }

                if (meshInstanceChild == null) continue;

                // FIX: Apply a 90-degree correction step on the local Y-axis rotation matrix to fix the perpendicular offset
                Quaternion correctedMeshRotation = meshInstanceChild.rotation * Quaternion.Euler(90f, 0f, 0f);

                foreach (var def in relativeBottomDefs)
                {
                    Vector3 worldPos = meshInstanceChild.transform.position + (correctedMeshRotation * def.position);
                    Quaternion worldRot = correctedMeshRotation * Quaternion.Euler(def.rotation);

                    CreateColliderChild(child, def.name, child.InverseTransformPoint(worldPos), (Quaternion.Inverse(child.rotation) * worldRot).eulerAngles, boxSize);
                }

                foreach (var def in relativeBottomDefs)
                {
                    Vector3 relativeMirroredPos = new Vector3(def.position.x, def.position.y, -def.position.z);
                    Vector3 worldMirroredPos = meshInstanceChild.transform.position + (correctedMeshRotation * relativeMirroredPos);

                    Vector3 mirroredRotDef = new Vector3(-def.rotation.x, -def.rotation.y, def.rotation.z);
                    Quaternion worldMirroredRot = correctedMeshRotation * Quaternion.Euler(mirroredRotDef);

                    string topHalfName = def.name.Replace("PinCollider_", "PinCollider_Top_");
                    CreateColliderChild(child, topHalfName, child.InverseTransformPoint(worldMirroredPos), (Quaternion.Inverse(child.rotation) * worldMirroredRot).eulerAngles, boxSize);
                }

                fixedCount++;
            }
        }

        EditorUtility.SetDirty(selectedParent);
        EditorUtility.DisplayDialog("Success!", $"Successfully oriented {fixedCount} pins perfectly parallel to their assets!", "Awesome");
    }

    private static void CreateColliderChild(Transform parent, string name, Vector3 localPos, Vector3 localRot, Vector3 size)
    {
        GameObject colHolder = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(colHolder, "Create Precise Wall");
        colHolder.transform.SetParent(parent);
        colHolder.transform.localPosition = localPos;
        colHolder.transform.localRotation = Quaternion.Euler(localRot);
        colHolder.AddComponent<BoxCollider>().size = size;
    }
}

// Adds a MinHeightClamp to every pre-placed cup/pin body in the scene so they can't be crushed
// through the floor, and syncs the existing ones to the values below. Spawned pieces get the clamp
// automatically from MatchLoaderController; this tool covers the ~85 pieces already placed on the
// field. It filters by name so the robot and the field rollers (which also have Rigidbodies) are
// skipped.
//
// Re-runnable: tweak the constants, re-run, and every cup/pin on the field picks up the new values.
// To dial VisualLift in by eye instead, select one cup and drag its MinHeightClamp > Visual Lift in
// the Inspector (it updates live, no play mode needed), then paste the value you liked here and
// re-run to apply it to the whole field.
public class FixPieceClamps
{
    private const float FloorY = 0.72f;
    private const float Tolerance = 0.05f;
    private const float VisualLift = 0.03f; // cosmetic mesh lift only — see MinHeightClamp

    [MenuItem("Tools/RoboSim/Field & Pieces/Add Floor Clamp to Pieces", false, 22)]
    private static void AddClamps()
    {
        Rigidbody[] bodies = Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude);
        int added = 0, synced = 0;
        foreach (Rigidbody body in bodies)
        {
            string n = body.gameObject.name;
            // Cup bodies are named "Cup"; pin bodies "Pin<colors>". Rollers/robot don't match.
            if (!(n.StartsWith("Cup") || n.StartsWith("Pin"))) continue;

            // The lift moves the piece's mesh children, so record the whole hierarchy for undo.
            Undo.RegisterFullObjectHierarchyUndo(body.gameObject, "Add Height Clamp to Pieces");

            MinHeightClamp clamp = body.GetComponent<MinHeightClamp>();
            if (clamp == null) { clamp = Undo.AddComponent<MinHeightClamp>(body.gameObject); added++; }
            else synced++;

            clamp.floorY = FloorY;
            clamp.tolerance = Tolerance;
            clamp.visualLift = VisualLift;
            clamp.ApplyVisualLift(); // no-op when the mesh already carries this lift
            EditorUtility.SetDirty(clamp);
        }

        if (bodies.Length > 0) EditorSceneManager.MarkSceneDirty(bodies[0].gameObject.scene);
        Debug.Log($"Add Height Clamp to Pieces: added MinHeightClamp to {added} cup/pin bodies, " +
                  $"synced {synced} existing (visualLift={VisualLift}, floorY={FloorY}, " +
                  $"tolerance={Tolerance}); scanned {bodies.Length} rigidbodies.");
    }
}

// Tunes the "ground feel" of the cup/pin game pieces so the robot knocks them into a lively roll
// instead of them stopping dead on contact. Two knobs from two different physics systems:
//
//   1) Rigidbody linear/angular DAMPING = global velocity decay, applied every frame like air drag
//      (NOT ground friction). Angular damping is the "keeps rolling" knob — the pins were left at 5
//      by FixAllPins, which kills a roll almost instantly. We bring both to 0.1 so spin/coast
//      persist.
//   2) A shared low-FRICTION PhysicsMaterial assigned to every piece collider. FrictionCombine is
//      Minimum, so the piece's low friction wins regardless of what the ground uses (Unity's default
//      is a grippy 0.6) — that lets us tune the pieces without touching the field colliders at all.
//
// Applies to BOTH the pieces already placed in the scene AND the 4 match-load prefabs, so spawned
// pieces feel the same. Filters by name (Cup*/Pin*) so the robot and rollers are left alone.
// Re-runnable. Dial the feel with the constants below.
public static class TunePiecePhysics
{
    // ---- Tune the feel here ----
    // Note on values: gravity is ~-98 (10x, because the world is 10x scale), so friction decelerates
    // a piece 10x faster in world units than real life — pieces feel "sticky" and you need lower
    // coefficients than intuition suggests to get a good slide/roll.
    private const float LinearDamping = 0.05f;  // translation coast (0 = slides forever, high = stops fast)
    private const float AngularDamping = 0.05f; // roll/spin persistence — a main "rolls more" knob
    private const float DynamicFriction = 0.2f; // sliding grip against the ground — the OTHER main knob
    private const float StaticFriction = 0.2f;  // grip to start moving from rest
    private const float Bounciness = 0f;        // pieces shouldn't trampoline

    private const string MaterialPath = "Assets/PiecePhysics.physicMaterial";
    private const string PrefabFolder = "Assets/Models/MatchLoadPreFabs";

    // The field surfaces use this material, and it was set to FrictionCombine = Maximum — which
    // OVERRIDES the piece's friction back up (Unity uses the higher-priority combine mode of the two
    // contacting materials; priority is Average < Multiply < Minimum < Maximum). We flip it to
    // Minimum so the lower of the two wins and the piece material above becomes the single authority.
    private const string FieldMaterialPath = "Assets/ZeroBounce.physicMaterial";

    [MenuItem("Tools/RoboSim/Field & Pieces/Tune Roll and Friction", false, 23)]
    private static void Tune()
    {
        PhysicsMaterial mat = GetOrCreateMaterial();
        bool fieldFixed = NeutralizeFieldMaterial();

        // Pieces already placed on the field (the ones the robot mostly drives into).
        int sceneBodies = 0;
        foreach (Rigidbody body in Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude))
        {
            if (!IsPiece(body.gameObject.name)) continue;
            Undo.RecordObject(body, "Tune Piece Physics");
            ApplyToBody(body, mat, recordUndo: true);
            sceneBodies++;
        }
        if (sceneBodies > 0) EditorSceneManager.MarkAllScenesDirty();

        // The prefabs, so match-load spawns inherit the same feel.
        int prefabBodies = TunePrefabs(mat);

        Debug.Log($"Tune Piece Physics: damping {LinearDamping}/{AngularDamping}, friction {DynamicFriction} " +
                  $"via '{Path.GetFileName(MaterialPath)}' → {sceneBodies} scene bodies, {prefabBodies} prefab bodies. " +
                  $"Field material combine fixed: {fieldFixed}.");
    }

    // The field's ZeroBounce material was set to FrictionCombine = Maximum, which forces every
    // piece-on-field contact up to the field's 0.6 friction regardless of the piece material —
    // the reason lowering the piece friction "did nothing". Flip it to Minimum so the piece wins.
    private static bool NeutralizeFieldMaterial()
    {
        PhysicsMaterial field = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(FieldMaterialPath);
        if (field == null) return false;
        field.frictionCombine = PhysicsMaterialCombine.Minimum;
        EditorUtility.SetDirty(field);
        AssetDatabase.SaveAssets();
        return true;
    }

    // Cup bodies are named "Cup*", pin bodies "Pin*". Robot/rollers don't match, so they're skipped.
    private static bool IsPiece(string n) => n.StartsWith("Cup") || n.StartsWith("Pin");

    private static void ApplyToBody(Rigidbody body, PhysicsMaterial mat, bool recordUndo)
    {
        body.linearDamping = LinearDamping;
        body.angularDamping = AngularDamping;
        foreach (Collider c in body.GetComponentsInChildren<Collider>())
        {
            if (recordUndo) Undo.RecordObject(c, "Tune Piece Physics");
            c.sharedMaterial = mat;
        }
    }

    private static int TunePrefabs(PhysicsMaterial mat)
    {
        int count = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { PrefabFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject root = PrefabUtility.LoadPrefabContents(path);
            bool changed = false;
            foreach (Rigidbody body in root.GetComponentsInChildren<Rigidbody>(true))
            {
                if (!IsPiece(body.gameObject.name)) continue;
                ApplyToBody(body, mat, recordUndo: false);
                changed = true;
                count++;
            }
            if (changed) PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
        }
        return count;
    }

    private static PhysicsMaterial GetOrCreateMaterial()
    {
        PhysicsMaterial mat = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(MaterialPath);
        if (mat == null)
        {
            mat = new PhysicsMaterial("PiecePhysics");
            AssetDatabase.CreateAsset(mat, MaterialPath);
        }
        mat.dynamicFriction = DynamicFriction;
        mat.staticFriction = StaticFriction;
        mat.bounciness = Bounciness;
        // Minimum: the lower friction of the two contacting materials wins, so the piece's low value
        // applies even against the default-0.6 ground/other pieces without us editing those.
        mat.frictionCombine = PhysicsMaterialCombine.Minimum;
        mat.bounceCombine = PhysicsMaterialCombine.Minimum;
        EditorUtility.SetDirty(mat);
        AssetDatabase.SaveAssets();
        return mat;
    }
}
