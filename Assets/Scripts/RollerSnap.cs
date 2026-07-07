using UnityEngine;

public class RollerSnap : MonoBehaviour
{
    private Rigidbody rb;
    private HingeJoint hinge;

    [Header("Snap Configuration")]
    [SerializeField] private float snapSpeed = 8f;           // How aggressively it pulls home
    [SerializeField] private float velocityThreshold = 1.5f; // Stops snapping if a robot spins it fast

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        hinge = GetComponent<HingeJoint>();
    }

    void FixedUpdate()
    {
        // Fail-safe check in case components are missing
        if (rb == null || hinge == null) return;

        // Only trigger the magnetic snap pull if the roller is slowing down
        if (rb.angularVelocity.magnitude < velocityThreshold)
        {
            // FIX: Use the Hinge Joint's internal 1D tracker to completely 
            // bypass unstable 3D Euler angle flipping bugs across the field.
            float currentAngle = hinge.angle;
            
            // Find the nearest target increment of 120 degrees (0, 120, 240, -120, etc.)
            float targetAngle = Mathf.Round(currentAngle / 120f) * 120f;
            
            // Calculate the absolute shortest path to get to that flat face
            float angleDifference = Mathf.DeltaAngle(currentAngle, targetAngle);
            
            // Apply a clean corrective torque strictly along its local axle path
            rb.AddTorque(transform.right * angleDifference * snapSpeed * Time.fixedDeltaTime, ForceMode.VelocityChange);
        }
    }
}