using UnityEngine;

// Marks the lift's TRAY / end-effector link — the top link of a DR4B (or a cascade/elevator top
// stage) that reaches highest and carries the scored game pieces. Its only job is to be a type
// IntakePull can whitelist: the intake's hold point + stack slot anchors are deliberately re-parented
// onto this link so the held stack rides UP with the lift, and IntakePull's self-heal (which normally
// yanks any anchor off a moving articulation link back onto the rigid chassis) treats a link marked
// here as a legal parent — see IntakePull.NeedsReanchor.
//
// A DR4B is a CLOSED kinematic loop; ArticulationBody is a strict TREE and can't represent one, so the
// linkage is faked with one powered revolute driver + transform followers (see Dr4bMoveFollower /
// PivotRotateFollower). This link is the highest coupled follower — where the tray sits. The
// carriage never drives itself (the driver's MotorActuator + the JointCoupler do); this component holds
// no control logic.
//
// Added by Tools > RoboSim > Robot > Mechanisms > Build DR4B Lift.
[DisallowMultipleComponent]
public class LiftCarriage : MonoBehaviour
{
    [Tooltip("The tray/end-effector joint this marks. Defaults to the ArticulationBody on this GameObject.")]
    public ArticulationBody body;

    void Reset() { if (body == null) body = GetComponent<ArticulationBody>(); }
    void Awake() { if (body == null) body = GetComponent<ArticulationBody>(); }

    // Current joint displacement (read-only convenience for a HUD/gizmo): degrees for a revolute tray,
    // world units (10x scale) for a prismatic one. jointPosition is radians (revolute) / meters
    // (prismatic); we return it raw here — callers know the tray's joint type.
    public float JointPosition
    {
        get
        {
            if (body == null) return 0f;
            ArticulationReducedSpace p = body.jointPosition;
            return p.dofCount > 0 ? p[0] : 0f;
        }
    }
}
