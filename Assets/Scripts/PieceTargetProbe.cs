using UnityEngine;
using UnityEngine.Rendering;

// DIAGNOSTIC PROBE (a testing aid — safe to delete). Sends the nearest game piece (Cup*/Pin*) to a
// fixed world target point, so you can verify a piece can be placed *precisely* on a point, independent
// of the intake's mouth/hold-point setup.
//
// Like IntakePull, it glides the piece by its CENTER OF MASS, not its transform pivot: the field's
// Cup*/Pin* pieces keep the CAD origin as their pivot (~9-15 world units from the visible mesh), so
// aiming the pivot at a point leaves the mesh that far off. An orange marker sphere is drawn at the
// target; the piece's center should land on it.
//
// Usage: drop this on any GameObject (Add Component), enter Play, then right-click the component header
// and pick "Send Nearest Piece To Target". "Release Piece" hands it back to normal physics. Set
// worldTarget in the Inspector (default a visible point above the field; floor is ~Y 0.72).
public class PieceTargetProbe : MonoBehaviour
{
    [Tooltip("World point to send the piece's CENTER to. A marker sphere is drawn here. Field floor is ~Y 0.72 — keep this above it so you can see it. (0,0,0 is under the floor, so it's a poor default.)")]
    public Vector3 worldTarget = new Vector3(0f, 2f, 6f);

    [Tooltip("How fast the piece's center glides to the target (world units/sec). It's kinematic, so it can't overshoot.")]
    public float glideSpeed = 24f;

    [Tooltip("Only consider pieces within this distance of THIS object when picking the nearest one.")]
    public float searchRadius = 60f;

    [Tooltip("Ghost the piece's colliders while it travels, so it passes through the field instead of jamming.")]
    public bool passThrough = true;

    [Tooltip("World-space diameter of the target marker sphere.")]
    public float markerSize = 0.6f;

    // The piece currently being driven to the target.
    private Rigidbody piece;
    private Vector3 localCom;      // pivot→center offset, captured before ghosting
    private bool wasKinematic;
    private bool arrived;

    private Transform marker;

    void Start()
    {
        marker = MakeSphere(markerSize, new Color(1f, 0.45f, 0.1f, 1f), "PieceTargetProbeMarker").transform;
    }

    void LateUpdate()
    {
        if (marker != null) marker.position = worldTarget;   // pin the marker to the (world) target point
    }

    [ContextMenu("Send Nearest Piece To Target")]
    private void SendNearestPieceToTarget()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("PieceTargetProbe: enter Play mode first — the glide runs in FixedUpdate.", this);
            return;
        }
        Rigidbody found = FindNearestPiece();
        if (found == null)
        {
            Debug.LogWarning($"PieceTargetProbe: no Cup*/Pin* rigidbody found within {searchRadius}u of '{name}'.", this);
            return;
        }
        Capture(found);
    }

    [ContextMenu("Release Piece")]
    private void ReleasePiece()
    {
        if (piece != null)
        {
            piece.isKinematic = wasKinematic;
            if (passThrough) SetColliders(piece, true);
            Debug.Log($"PieceTargetProbe: released '{piece.name}'.", this);
        }
        piece = null;
        arrived = false;
    }

    void OnDisable()
    {
        ReleasePiece();   // never leave a piece kinematic/ghosted if the probe switches off
    }

    void FixedUpdate()
    {
        if (piece == null) return;

        // Same COM-space glide as IntakePull: move the piece's center toward the target, then place the
        // pivot so the center lands there. No rotation change here, so the piece keeps its orientation.
        Vector3 curCom = piece.position + piece.rotation * localCom;
        Vector3 nextCom = arrived ? worldTarget : Vector3.MoveTowards(curCom, worldTarget, glideSpeed * Time.fixedDeltaTime);
        piece.MovePosition(nextCom - piece.rotation * localCom);

        if (!arrived && (nextCom - worldTarget).sqrMagnitude < 1e-4f)
        {
            arrived = true;
            float pivotDelta = (piece.position - worldTarget).magnitude;
            float comDelta = (curCom - worldTarget).magnitude;
            Debug.Log($"PieceTargetProbe: '{piece.name}' reached target {worldTarget}. " +
                      $"comΔ={comDelta:0.###}u (center on the marker), pivotΔ={pivotDelta:0.#}u (the piece's off-center pivot). " +
                      "If comΔ≈0 and the mesh sits on the marker, precise placement works.", this);
        }
    }

    private void Capture(Rigidbody rb)
    {
        if (piece != null) ReleasePiece();          // one at a time
        localCom = rb.centerOfMass;                 // BEFORE ghosting — disabling colliders recomputes COM
        wasKinematic = rb.isKinematic;
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        if (passThrough) SetColliders(rb, false);
        piece = rb;
        arrived = false;
        Debug.Log($"PieceTargetProbe: captured '{rb.name}' (pivot→center offset {localCom.magnitude:0.#}u); gliding its center to {worldTarget}.", this);
    }

    private Rigidbody FindNearestPiece()
    {
        Rigidbody best = null;
        float bestSqr = searchRadius * searchRadius;
        Vector3 from = transform.position;
        foreach (Rigidbody rb in FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
        {
            if (rb == piece || !IsPiece(rb.gameObject)) continue;
            float d = (rb.worldCenterOfMass - from).sqrMagnitude;
            if (d < bestSqr) { bestSqr = d; best = rb; }
        }
        return best;
    }

    // Same convention as IntakePull.IsPiece (there is no GamePiece tag/layer yet).
    private static bool IsPiece(GameObject go)
    {
        string n = go.name;
        return n.StartsWith("Cup") || n.StartsWith("Pin");
    }

    private static void SetColliders(Rigidbody rb, bool enabled)
    {
        if (rb == null) return;
        foreach (Collider c in rb.GetComponentsInChildren<Collider>()) c.enabled = enabled;
    }

    // --- marker (mirrors IntakePull's style, kept self-contained so this file is drop-in/removable) ---

    private GameObject MakeSphere(float worldDiameter, Color color, string goName)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = goName;
        Collider col = go.GetComponent<Collider>();
        if (col != null) Destroy(col);
        go.transform.SetParent(transform, false);
        Vector3 ls = transform.lossyScale;   // counter-scale so world diameter is honored on a scaled host
        go.transform.localScale = new Vector3(worldDiameter / Nz(ls.x), worldDiameter / Nz(ls.y), worldDiameter / Nz(ls.z));
        MeshRenderer r = go.GetComponent<MeshRenderer>();
        r.sharedMaterial = UnlitMaterial(color);
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        return go;
    }

    private static Material UnlitMaterial(Color color)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Unlit");
        if (sh == null) sh = Shader.Find("Unlit/Color");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        Material m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
        if (m.HasProperty("_Color")) m.SetColor("_Color", color);
        return m;
    }

    private static float Nz(float v) => Mathf.Abs(v) < 1e-4f ? 1f : v;

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.45f, 0.1f, 0.9f);
        Gizmos.DrawWireSphere(worldTarget, Mathf.Max(0.1f, markerSize * 0.5f));
    }
#endif
}
