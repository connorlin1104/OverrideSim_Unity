using UnityEngine;

// Safety net that stops a game piece from being crushed down through the floor — e.g. when the
// heavy robot drives over a knocked-over cup, the solver can't eject the light piece fast enough
// and it wedges into the ground. This keeps the piece's LOWEST collider point at or above the
// floor surface each physics step, regardless of how the piece is lying, so it can never sink
// through. It only acts when the piece is actually below the floor, so normal resting/tumbling
// is untouched.
[RequireComponent(typeof(Rigidbody))]
public class MinHeightClamp : MonoBehaviour
{
    [Tooltip("World Y of the floor surface. The piece's lowest point is pushed back to this when it sinks well below. ~0.72 for RoboSim's field.")]
    public float floorY = 0.72f;

    [Tooltip("Dead zone: only correct once the piece is this far BELOW floorY. Keeps normal resting contact (which penetrates slightly) from triggering constant micro-corrections that shake stacked pieces apart.")]
    public float tolerance = 0.05f;

    private Rigidbody rb;
    private Collider[] cols;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        cols = GetComponentsInChildren<Collider>();
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
}
