using UnityEngine;

// Base for the DR4B visual followers. Captures a CHASSIS-LOCAL rest pose at the linkage's rest
// position, then re-evaluates it LIVE each frame — so a follower tracks the robot as it drives and
// yaws, and is invariant to RobotSpawner's Awake teleport of the articulation root. Concrete followers
// (TranslateUpFollower, PivotRotateFollower) compose their pose from this rest + the controller's theta.
//
// Never reparents the model; poses are computed purely from the chassis + captured rest, so nesting or
// the spawn footprint scan can't perturb them.
public abstract class Dr4bFollower : MonoBehaviour
{
    protected Transform chassis;
    protected Vector3 restChassisPos;
    protected Quaternion restChassisRot;
    protected bool captured;

    // Capture the current (REST) pose in the chassis's local frame. Called by Dr4bLift.Awake.
    public virtual void CaptureRest(Transform chassisT)
    {
        if (chassisT == null) return;
        chassis = chassisT;
        restChassisPos = chassis.InverseTransformPoint(transform.position);
        restChassisRot = Quaternion.Inverse(chassis.rotation) * transform.rotation;
        captured = true;
    }

    // The rest pose expressed live in world space (follows the chassis as the bot drives/turns).
    protected Vector3 RestPosW => chassis.TransformPoint(restChassisPos);
    protected Quaternion RestRotW => chassis.rotation * restChassisRot;

    public abstract void Apply(Dr4bLift lift);
}
