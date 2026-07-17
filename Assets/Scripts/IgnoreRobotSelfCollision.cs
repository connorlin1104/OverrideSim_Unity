using System.Collections.Generic;
using UnityEngine;

// Stops a mechanism link from colliding with the REST OF ITS OWN ROBOT.
//
// ArticulationBody links that aren't in a direct parent-child relationship still collide with each
// other. So a piston rod or doinker arm whose CAD is rough/oversized (it clips a drivetrain wheel or
// the chassis) pushes the robot around — e.g. the bot creeps backward while the piston is retracted
// and overlapping a front wheel, then stops once it extends clear. Ganking the whole robot like that
// is never intended: a mechanism should interact with the FIELD and game pieces, not its own frame.
//
// This ignores collision between this object's colliders and every other collider under the robot
// root, once at startup. The link still collides with everything external (pieces, walls, floor).
[DefaultExecutionOrder(50)] // after the articulation exists, before gameplay settles
public class IgnoreRobotSelfCollision : MonoBehaviour
{
    void Start()
    {
        RobotMechanisms root = GetComponentInParent<RobotMechanisms>();
        if (root == null) return;

        // Only active colliders — IgnoreCollision warns on disabled ones, and the cosmetic cylinder
        // parts are deliberately collider-disabled anyway.
        var mine = new List<Collider>();
        foreach (Collider c in GetComponentsInChildren<Collider>(false))
            if (c.enabled) mine.Add(c);
        if (mine.Count == 0) return;
        var mineSet = new HashSet<Collider>(mine);

        foreach (Collider other in root.GetComponentsInChildren<Collider>(false))
        {
            if (other == null || !other.enabled || mineSet.Contains(other)) continue;
            foreach (Collider m in mine) Physics.IgnoreCollision(m, other, true);
        }
    }
}
