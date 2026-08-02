using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// Scene-view preview and drag handle for a match loader's Spawn Tilt.
//
// A few degrees of lean is the difference between the human feed dropping cleanly into a scooper and
// bouncing off its lip, and there is no way to judge that from three Euler numbers in the Inspector. So
// the loader draws what it is actually going to do: the piece's own silhouette, measured from the
// prefab it spawns, at the pose it will be born in — at the manual drop height, at the anchor, and the
// fall between them — with the lean called out in degrees against the anchor's own aim.
//
// Everything drawn comes from MatchLoaderController.SpawnRotation, which is the same value
// SpawnSingleMatchLoad passes to Instantiate. A preview computed any other way would eventually promise
// a pose the spawn doesn't produce, which is worse than no preview.
//
// The rotation handle writes back to the serialized spawnTilt (with Undo), so the angle can be dialled
// in by dragging against the real goal/robot geometry instead of typed and re-checked in Play.
//
// Past SelfRightingLimitDegrees the whole preview turns red: a piece that misses the robot has to be
// able to settle upright on the floor by itself, and past that lean it stays tipped over.
[CustomEditor(typeof(MatchLoaderController))]
public class MatchLoaderControllerEditor : Editor
{
    // Fallback silhouette when the loader has no element prefab assigned yet (a cup is ~0.86 world
    // units tall and ~1.1 across at this project's 10x scale).
    private static readonly Vector3 DefaultPieceSize = new Vector3(1.1f, 0.86f, 1.1f);

    private static readonly Color AimColor = new Color(0.35f, 0.85f, 0.45f);      // the anchor's own up
    private static readonly Color TiltColor = new Color(1f, 0.82f, 0.15f);        // the leaned spawn pose
    private static readonly Color OverTiltColor = new Color(1f, 0.35f, 0.3f);     // ...past self-righting

    // Prefab silhouettes are measured by walking every mesh in the asset, which is not something to do
    // on every Scene-view repaint.
    private static readonly Dictionary<GameObject, Bounds> pieceBoundsCache = new Dictionary<GameObject, Bounds>();

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var loader = (MatchLoaderController)target;
        if (loader.SpawnAnchor == null)
        {
            EditorGUILayout.HelpBox("Assign Spawn Point to preview and drag the spawn angle in the " +
                                    "Scene view.", MessageType.Info);
            return;
        }

        // Lean is what the limit is about; the total includes yaw, which cannot tip anything.
        float lean = loader.SpawnLeanDegrees;
        float total = loader.SpawnTiltDegrees;
        EditorGUILayout.HelpBox(
            $"Spawn aim: {total:0.#}° from the anchor, of which {lean:0.#}° is LEAN\n" +
            (lean > MatchLoaderController.SelfRightingLimitDegrees
                ? $"Past ~{MatchLoaderController.SelfRightingLimitDegrees:0}° of lean, a piece that " +
                  "misses the robot will STAY tipped over on the floor instead of settling upright."
                : "Yaw is free — only lean can tip a piece. Drag the rotation handle in the Scene " +
                  "view to aim it."),
            lean > MatchLoaderController.SelfRightingLimitDegrees ? MessageType.Warning : MessageType.Info);
    }

    private void OnSceneGUI()
    {
        var loader = (MatchLoaderController)target;
        Transform anchor = loader.SpawnAnchor;
        if (anchor == null) return;

        // The loader's markers live inside the field CAD, so draw through it — same reason the intake
        // builder forces zTest Always on its markers.
        CompareFunction wasZTest = Handles.zTest;
        Handles.zTest = CompareFunction.Always;

        Quaternion spawnRot = loader.SpawnRotation;
        float tilt = loader.SpawnTiltDegrees;
        bool tooFar = loader.SpawnLeanDegrees > MatchLoaderController.SelfRightingLimitDegrees;
        Color tiltColor = tooFar ? OverTiltColor : TiltColor;

        Vector3 restAt = anchor.position;                                                  // automatic spawn
        Vector3 dropAt = restAt + Vector3.up * loader.ManualSpawnExtraHeight;              // manual spawn
        Bounds piece = PieceBounds(loader.ElementPrefab);
        float handle = HandleUtility.GetHandleSize(restAt);

        // 1) The piece itself, at the pose it is born in, in both places it can be born. Instantiate
        //    puts prefab-local point p at spawnPos + rotation * p, so the silhouette follows the same rule.
        DrawPiece(dropAt, spawnRot, piece, tiltColor);
        DrawPiece(restAt, spawnRot, piece, new Color(tiltColor.r, tiltColor.g, tiltColor.b, 0.45f));

        // 2) The fall a manual spawn makes, from the drop height down to the anchor.
        Handles.color = new Color(tiltColor.r, tiltColor.g, tiltColor.b, 0.7f);
        Handles.DrawDottedLine(dropAt, restAt, 4f);

        // 3) The lean itself: the anchor's own aim against the pose the spawn actually gets, and the arc
        //    between them. With no tilt the two arrows lie on top of each other and the arc vanishes —
        //    which is exactly what "no lean" should look like.
        Vector3 aimUp = anchor.rotation * Vector3.up;
        Vector3 tiltUp = spawnRot * Vector3.up;
        float ray = handle * 1.4f;

        Handles.color = AimColor;
        Handles.DrawLine(restAt, restAt + aimUp * ray);
        Handles.Label(restAt + aimUp * ray, " anchor aim (0°)");

        Handles.color = tiltColor;
        Handles.ArrowHandleCap(0, restAt, Quaternion.LookRotation(tiltUp), ray, EventType.Repaint);

        if (tilt > 0.01f)
        {
            Vector3 arcAxis = Vector3.Cross(aimUp, tiltUp);
            if (arcAxis.sqrMagnitude > 1e-8f)
            {
                Handles.color = new Color(tiltColor.r, tiltColor.g, tiltColor.b, 0.25f);
                Handles.DrawSolidArc(restAt, arcAxis.normalized, aimUp, tilt, ray * 0.75f);
            }
        }
        Handles.color = tiltColor;
        Handles.Label(restAt + tiltUp * ray * 1.15f,
            tooFar ? $" spawn lean {tilt:0.#}°  — too far to self-right" : $" spawn lean {tilt:0.#}°");

        // 4) Drag it. The handle turns the WORLD spawn pose; what gets stored is that pose expressed
        //    back in the anchor's frame, so re-aiming the anchor later keeps the lean relative to it.
        EditorGUI.BeginChangeCheck();
        Quaternion turned = Handles.RotationHandle(spawnRot, restAt);
        if (EditorGUI.EndChangeCheck())
        {
            serializedObject.Update();   // pick up anything the Inspector changed since the last repaint
            SerializedProperty tiltProperty = serializedObject.FindProperty("spawnTilt");
            tiltProperty.vector3Value = Signed((Quaternion.Inverse(anchor.rotation) * turned).eulerAngles);
            serializedObject.ApplyModifiedProperties();   // records the Undo step itself
        }

        Handles.zTest = wasZTest;
    }

    // A wire box the size of the piece, at the pose Instantiate will give it.
    private static void DrawPiece(Vector3 spawnPos, Quaternion rotation, Bounds local, Color color)
    {
        using (new Handles.DrawingScope(color,
                   Matrix4x4.TRS(spawnPos + rotation * local.center, rotation, Vector3.one)))
            Handles.DrawWireCube(Vector3.zero, local.size);
    }

    // Euler angles come back 0..360, which shows up in the Inspector as a 5° lean written "355".
    private static Vector3 Signed(Vector3 euler) =>
        new Vector3(Mathf.DeltaAngle(0f, euler.x), Mathf.DeltaAngle(0f, euler.y), Mathf.DeltaAngle(0f, euler.z));

    // The spawned unit's extents in its own prefab-root space, measured from its meshes. Falls back to a
    // cup-sized box when there is no prefab (or it has no renderable geometry) so the preview still reads.
    private static Bounds PieceBounds(GameObject prefab)
    {
        if (prefab == null) return new Bounds(Vector3.zero, DefaultPieceSize);
        if (pieceBoundsCache.TryGetValue(prefab, out Bounds cached)) return cached;

        Bounds result = default;
        bool any = false;
        Matrix4x4 toRoot = prefab.transform.worldToLocalMatrix;
        foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>(true))
        {
            Mesh mesh = filter.sharedMesh;
            if (mesh == null) continue;

            // The mesh's local AABB, walked corner by corner into the prefab root's frame — a mesh
            // hung off a rotated child has an AABB that means nothing until it is mapped across.
            Matrix4x4 m = toRoot * filter.transform.localToWorldMatrix;
            Bounds local = mesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 point = m.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents,
                    new Vector3((corner & 1) == 0 ? -1f : 1f,
                                (corner & 2) == 0 ? -1f : 1f,
                                (corner & 4) == 0 ? -1f : 1f)));
                if (!any) { result = new Bounds(point, Vector3.zero); any = true; }
                else result.Encapsulate(point);
            }
        }

        if (!any) result = new Bounds(Vector3.zero, DefaultPieceSize);
        pieceBoundsCache[prefab] = result;
        return result;
    }
}
