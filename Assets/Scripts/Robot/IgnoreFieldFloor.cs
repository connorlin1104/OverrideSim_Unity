using System.Collections.Generic;
using UnityEngine;

// Stops ONE mechanism link from standing on the field floor.
//
// The sibling of IgnoreRobotSelfCollision, for the other half of the same problem. That one keeps a
// mechanism from shoving its own robot around; this one keeps a mechanism from becoming a WHEEL.
//
// WHY. A driven roller hung low enough to pick pieces off the tiles is, to PhysX, a rigid box with
// friction that can carry the robot's weight. Measured on 654V_v3 (2026-09-04): full throttle into
// the perimeter wall or a goal, held, pitches the chassis about 5 degrees, lands it on the
// ScoringMech's `4thStage` roller and JACKS FOUR OF SIX WHEELS 14-22 mm off the ground. The wheels
// in the air free-spin at the full 1440 deg/s and keep spinning after the throttle is released,
// while the two still down stall — "there are two wheels on the right drivetrain that just don't
// spin with the rest", and a robot on two wheels cannot turn. 654V_v2 has no scoring intake and
// never lifts a wheel in the same test. Disabling this roller's colliders removes the lift
// completely (gap 0 mm, tilt 0.0 deg, on both the wall and the goal), which is what named it.
//
// WHY NOT JUST RAISE THE ROLLER, or delete its colliders. Raising it is a change to the robot's CAD
// that would have to be redone on every robot with an intake; deleting the colliders stops it
// intaking at all. The roller SHOULD touch game pieces — that is its whole job — and should never
// touch the floor, so that is exactly what this mutes, and nothing else. Walls, goals, pieces and
// the rest of the field still collide normally.
//
// The floor is matched by NAME because that is what the field builder writes
// (FieldSetupTools.GroundName). IgnoreFieldFloorValidation holds the name against the shipped scene
// so a rename cannot turn this into a silent no-op — which, for a component whose whole effect is
// invisible until a robot climbs something, is the only failure worth guarding.
[DefaultExecutionOrder(50)] // after the articulation exists, before gameplay settles
public class IgnoreFieldFloor : MonoBehaviour
{
    // The field's one ground slab. See TileSeamTool.GroundColliderName / FieldSetupTools.GroundName.
    public const string DefaultFloorName = "GroundCollider";

    [Tooltip("Name of the field's floor collider. Only colliders on an object with this exact name " +
             "are muted, so nothing else about this link's collisions changes.")]
    public string floorColliderName = DefaultFloorName;

    void Start() => IgnoreAgainstFloor();

    // Mutes every pair between this link's colliders and the field floor. Returns how many pairs it
    // muted, so a harness (or a validator) can tell "muted nothing" from "there was nothing to
    // mute". Public because Physics.Simulate never calls Start, and an edit-mode rig that skips
    // this is measuring a robot that never ships.
    public int IgnoreAgainstFloor()
    {
        var mine = new List<Collider>();
        foreach (Collider c in GetComponentsInChildren<Collider>(false))
            if (c.enabled) mine.Add(c);
        if (mine.Count == 0) return 0;
        var mineSet = new HashSet<Collider>(mine);

        int muted = 0;
        foreach (Collider other in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
        {
            if (other == null || !other.enabled || mineSet.Contains(other)) continue;
            if (other.gameObject.name != floorColliderName) continue;
            foreach (Collider m in mine) { Physics.IgnoreCollision(m, other, true); muted++; }
        }
        return muted;
    }
}
