using System.Collections.Generic;
using UnityEngine;

// Safety net that stops a game piece from being crushed down through the floor — e.g. when the
// heavy robot drives over a knocked-over cup, the solver can't eject the light piece fast enough
// and it wedges into the ground. This keeps the piece's LOWEST collider point at or above the
// floor surface each physics step, regardless of how the piece is lying, so it can never sink
// through. It only acts when the piece is actually below the floor, so normal resting/tumbling
// is untouched.
//
// It also carries the piece's cosmetic lift. The imported cup/pin meshes hang slightly lower than
// the box colliders that hold them up, so a piece resting correctly on the ground still LOOKS
// half-buried. `visualLift` raises the mesh children relative to the colliders to close that gap.
// It is purely visual: no Rigidbody, collider or contact is touched, so friction, rolling and the
// clamp below all behave exactly as they did before.
[RequireComponent(typeof(Rigidbody))]
public class MinHeightClamp : MonoBehaviour
{
    [Header("Floor (physics)")]
    [Tooltip("World Y of the floor surface. The piece's lowest point is pushed back to this when it sinks well below. ~0.72 for RoboSim's field.")]
    public float floorY = 0.72f;

    [Tooltip("Dead zone: only correct once the piece is this far BELOW floorY. Keeps normal resting contact (which penetrates slightly) from triggering constant micro-corrections that shake stacked pieces apart.")]
    public float tolerance = 0.05f;

    [Header("Visual only")]
    [Tooltip("How far to raise this piece's meshes above its colliders, in world units (1 unit ≈ 0.10 m, so 0.03 ≈ 3 mm real). Drag this up until the cup stops looking sunk into the floor. Does not affect physics.")]
    public float visualLift = 0.03f;

    // How much lift is already baked into the mesh transforms. Serialized so a lift applied in the
    // editor isn't applied a second time on scene load or on entering play mode.
    [SerializeField, HideInInspector] private float appliedLift;

    private Rigidbody rb;
    private Collider[] cols;
    private Transform[] visualRoots;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        cols = GetComponentsInChildren<Collider>();
        ApplyVisualLift();
    }

    void FixedUpdate()
    {
        if (cols == null || cols.Length == 0) return;

        float bottom = float.PositiveInfinity;
        foreach (Collider c in cols) bottom = Mathf.Min(bottom, c.bounds.min.y);

        // Only act when genuinely sunk through the floor — normal resting/tumbling stays untouched,
        // so pieces stacked on each other (e.g. a pin standing in a cup) aren't jittered apart.
        if (bottom < floorY - tolerance)
        {
            rb.position += new Vector3(0f, floorY - bottom, 0f);
            Vector3 v = rb.linearVelocity;
            if (v.y < 0f) { v.y = 0f; rb.linearVelocity = v; }
        }
    }

    // Moves the meshes by the difference between the requested lift and the lift already baked in,
    // so repeated calls converge instead of stacking offsets. The offset is captured in the body's
    // local space while the piece sits in its authored upright pose, which keeps the mesh glued to
    // the colliders once the piece is knocked over.
    public void ApplyVisualLift()
    {
        float delta = visualLift - appliedLift;
        if (Mathf.Abs(delta) < 1e-6f) return;

        Vector3 localOffset = transform.InverseTransformVector(Vector3.up * delta);
        foreach (Transform root in VisualRoots()) root.localPosition += localOffset;
        appliedLift = visualLift;
    }

    // The piece's mesh-only children (the imported MeshInstance_* groups). Children that carry a
    // collider are left alone — moving those would be a physics change, not a visual one.
    private Transform[] VisualRoots()
    {
        if (visualRoots != null) return visualRoots;

        List<Transform> roots = new List<Transform>();
        foreach (Transform child in transform)
        {
            if (child.GetComponentInChildren<Renderer>(true) == null) continue;
            if (child.GetComponentInChildren<Collider>(true) != null) continue;
            roots.Add(child);
        }
        return visualRoots = roots.ToArray();
    }

#if UNITY_EDITOR
    // Lets you drag visualLift in the Inspector and watch the piece rise without entering play mode.
    void OnValidate()
    {
        if (Application.isPlaying) return;
        visualRoots = null; // the hierarchy may have changed since we last looked
        ApplyVisualLift();
    }
#endif
}
