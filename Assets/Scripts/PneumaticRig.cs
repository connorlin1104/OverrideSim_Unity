using System.Collections.Generic;
using UnityEngine;

// Authoring record for one pneumatic built by Tools > RoboSim > Robot > Mechanisms > Build Pneumatic
// (roles). Lives on the DRIVEN link (the rod / the rotating part) and travels with the robot prefab,
// so the builder window can list every pneumatic on the robot and re-open one for editing — rebuild
// updates it in place, no manual clean step. Pure data: PneumaticActuator does the actual driving.
public class PneumaticRig : MonoBehaviour
{
    public enum RigMode { Linear, Rotary }
    public enum RigAxis { Auto, X, Y, Z, Custom }

    public RigMode mode = RigMode.Linear;
    [Tooltip("What the Configure Controller screen shows for this pneumatic.")]
    public string displayName = "Pneumatic";

    [Tooltip("Linear only: the stationary cylinder body the rod slides out of. Used for validation and the Auto axis; never rigged itself.")]
    public GameObject barrel;
    [Tooltip("Extra parts welded into the driven link so they move with it (set at build; they were reparented under the link).")]
    public List<GameObject> alsoMove = new List<GameObject>();

    [Tooltip("Linear: how far the rod extends, in millimeters (a real VEX cylinder is ~50 mm).")]
    public float strokeMm = 50f;
    [Tooltip("Rotary: joint angle (deg) when retracted.")]
    public float retractedDeg = 0f;
    [Tooltip("Rotary: joint angle (deg) when extended.")]
    public float extendedDeg = 90f;

    public RigAxis axisPreset = RigAxis.Auto;
    public Vector3 customAxis = Vector3.right;
    [Tooltip("Pivot/slide origin in the link's local space (used with a non-Auto axis, or when no pivot marker is set).")]
    public Vector3 anchor = Vector3.zero;
    [Tooltip("Rotary: optional child Transform marking the pivot point — overrides the anchor.")]
    public Transform pivotMarker;

    public bool reverse;
    public bool startExtended;
    public bool autoAssignButton = true;
}
