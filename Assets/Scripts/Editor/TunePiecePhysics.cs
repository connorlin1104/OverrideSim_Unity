using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.IO;

// Tunes the "ground feel" of the cup/pin game pieces so the robot knocks them into a lively roll
// instead of them stopping dead on contact. Two knobs from two different physics systems:
//
//   1) Rigidbody linear/angular DAMPING = global velocity decay, applied every frame like air drag
//      (NOT ground friction). Angular damping is the "keeps rolling" knob — the pins were left at 5
//      by FixYellowPins, which kills a roll almost instantly. We bring both to 0.1 so spin/coast
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

    [MenuItem("Tools/VEX/Tune Piece Physics")]
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
