using UnityEngine;

// Marks ONE mechanism link that may push field objects but may never stand on them.
//
// The third member of the family, after IgnoreRobotSelfCollision (a mechanism must not shove its
// own robot) and IgnoreFieldFloor (a mechanism must not stand on the tiles). This one covers what
// IgnoreFieldFloor cannot: everything ELSE a mechanism can end up perched on — goal walls, cup
// piles, pins — without stopping it touching them, because an intake that cannot touch a cup is
// not an intake. See NonSupportingLinkModel for the measurement that motivated it and the rule.
//
// Prefer this to IgnoreFieldFloor on any new link. IgnoreFieldFloor stays as-is on 4thStage: it is
// proven, shipped, and mutes a single named collider outright, which is cheaper than a per-contact
// test for the one case it covers.
[DefaultExecutionOrder(50)] // after the articulation exists, before gameplay settles
public class NonSupportingLink : MonoBehaviour
{
    void OnEnable() => NonSupportingLinkModel.Register(this);
    void OnDisable() => NonSupportingLinkModel.Unregister(this);

    // Physics.Simulate never calls OnEnable, so an edit-mode rig that skips this is measuring a
    // robot that never ships. Harnesses call it explicitly; the same reasoning as
    // IgnoreFieldFloor.IgnoreAgainstFloor.
    public void RegisterNow() => NonSupportingLinkModel.Register(this);
}
