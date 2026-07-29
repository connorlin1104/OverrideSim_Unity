using UnityEngine;

// Shared geometry measurement for the Cup*/Pin* field pieces. One implementation of "which way
// does this piece stand", used by every auto-upright hold: GoalStackMagnet, PieceStackMagnet and
// IntakePull each carried their own copy of this measurement before.
public static class PieceGeometry
{
    // The piece's "standing" axis expressed in its RIGIDBODY-local frame. It's the explicit
    // override axis when given (in MESH local space), else the longest axis of the mesh's local
    // bounds, mapped through the mesh child's rotation into the root's frame — so it's fixed no
    // matter how the piece is later rotated. The per-instance mapping matters: the field's pins
    // share ONE mesh but every instance carries a different child rotation, so no single
    // per-type axis could stand them all up.
    //
    // Returns zero if there's no mesh to measure (callers then leave that piece's rotation alone).
    public static Vector3 MeasureUpAxis(Rigidbody rb, Vector3 meshAxisOverride = default)
    {
        MeshFilter mf = rb != null ? rb.GetComponentInChildren<MeshFilter>() : null;
        Transform meshTf = mf != null ? mf.transform : null;
        Mesh mesh = mf != null ? mf.sharedMesh : null;
        if (meshTf == null) return Vector3.zero;

        Vector3 axisMeshLocal;
        if (meshAxisOverride.sqrMagnitude > 1e-6f)
            axisMeshLocal = meshAxisOverride.normalized;
        else if (mesh != null)
        {
            Vector3 s = mesh.bounds.size;                                   // longest local bounds axis
            axisMeshLocal = (s.x >= s.y && s.x >= s.z) ? Vector3.right
                          : (s.y >= s.z) ? Vector3.up : Vector3.forward;
        }
        else return Vector3.zero;

        Vector3 world = meshTf.rotation * axisMeshLocal;                    // the standing axis in world now
        return (Quaternion.Inverse(rb.rotation) * world).normalized;       // → rigidbody-local (rotation-stable)
    }
}
