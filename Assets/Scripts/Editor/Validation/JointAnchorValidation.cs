using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// A JOINT'S ANCHOR MUST STILL MATCH WHERE THE PART ACTUALLY IS.
//
// An ArticulationBody joint is defined twice: once in the child (anchorPosition/anchorRotation) and
// once in the parent (parentAnchorPosition/parentAnchorRotation). At joint position zero PhysX makes
// those two frames coincide — so if they disagree with the part's actual Transform, PhysX does not
// complain, it MOVES THE PART. The link teleports to wherever the joint says it belongs the instant
// physics starts, dragging its colliders with it, while the mesh you positioned in the editor is
// simply somewhere else than the collider that now represents it.
//
// THIS HAPPENED. 654V_v3's pneumatic goal aligner had a parentAnchorPosition 25.4 mm away from the
// link's own local position — purely along X, which is also the joint's travel axis. Every working
// prismatic on the same robot (CascadeStage1/2/3) matched to 0.0 mm. So the aligner started 25.4 mm
// off, ran its 90 mm stroke from the wrong zero, and finished deep enough into the drive wheels for
// RobotMotorController.IgnoreBuiltInSelfOverlaps to have to clear it — which it could not, because
// that scan runs once at spawn with everything stowed. The part looked fine and behaved as if its
// collider were somewhere else, because it was.
//
// IT WAS A BUILDER BUG, and this file said otherwise for a while. The formula in
// MechanismBuildUtil.RederiveParentAnchors is correct and always was — but the method opened with
// `if (body == null || body.isRoot) return;`, and isRoot reflects NATIVE articulation state, which
// on a prefab that was never instantiated reads true for every body. So on those paths the one
// method that writes the parent anchor did nothing, and the joint kept the anchor it was born with.
// 654V_v3's CascadeMotor still carries the untouched default (0,0,0) for exactly that reason: not a
// value that went stale, a value that was never written. The guard is gone; the hierarchy walk that
// follows it was always the correct root test.
//
// A stale anchor is still possible the other way — the anchor is a CACHED DERIVATIVE of the
// transform, so nudging a rigged part in the scene view silently invalidates it and nothing
// re-derives it. That is unpoliceable by hand across robots arriving from other people's CAD.
//
// So: check it, on every robot, every run. The repair is Tools > RoboSim > Repair > Joint Anchors,
// which re-derives each offending anchor from where its part now sits.
public static class JointAnchorValidation
{
    // 0.1 mm at this project's scale (1 unit = 100 mm). Tight enough to catch a real nudge, loose
    // enough to ignore the float noise of a round-trip through a prefab's serialized quaternion.
    private const float PositionToleranceUnits = 0.001f;
    private const float RotationToleranceDeg = 0.1f;

    [MenuItem("Tools/RoboSim/Validate/Joint Anchors Match The Parts", false, 1)]
    private static void RunInteractive()
        => ValidationUtil.RunInteractive("Joint Anchors Match The Parts", Run);

    public static void RunBatchValidate()
        => ValidationUtil.RunBatch("Joint Anchors Match The Parts", Run);

    public static string Run()
    {
        var offenders = new List<string>();
        int robots = 0, joints = 0;

        foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { RoboSimPaths.RobotsFolder }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || prefab.GetComponent<ArticulationBody>() == null) continue;
            robots++;

            foreach (ArticulationBody body in prefab.GetComponentsInChildren<ArticulationBody>(true))
            {
                if (body == null) continue;

                // A FIXED joint pins the child rigidly and has no travel to run from the wrong zero,
                // but a mismatched anchor still teleports it, so it is checked too. matchAnchors
                // bodies are skipped: Unity recomputes the parent anchor itself, so the serialized
                // value is not what PhysX uses and comparing it would report phantom failures.
                if (body.matchAnchors) continue;

                // Rootness is decided from the HIERARCHY, never from ArticulationBody.isRoot: that
                // property reflects native articulation state, and on a prefab ASSET — which is
                // never instantiated, so no articulation is ever built — it reads true for every
                // body. Gating on it silently skipped all four robots and reported "nothing was
                // checked", which is the only reason that assert exists.
                Transform child = body.transform;
                Transform parent = FindParentBody(body);
                if (parent == null) continue;   // no ancestor body: this IS the root
                joints++;

                Vector3 expectedPos =
                    parent.InverseTransformPoint(child.TransformPoint(body.anchorPosition));
                Quaternion expectedRot =
                    Quaternion.Inverse(parent.rotation) * (child.rotation * body.anchorRotation);

                float posOff = Vector3.Distance(expectedPos, body.parentAnchorPosition);
                float rotOff = Quaternion.Angle(expectedRot, body.parentAnchorRotation);

                if (posOff > PositionToleranceUnits || rotOff > RotationToleranceDeg)
                    offenders.Add(
                        $"{prefab.name}/{body.name} ({DescribeJoint(body)}): anchor is " +
                        $"{posOff * 100f:0.0} mm and {rotOff:0.0} deg away from where the part sits — " +
                        $"PhysX will move it there on the first step");
            }
        }

        ValidationUtil.Assert(joints > 0,
            $"no articulated joints found across {robots} robot prefab(s) — nothing was checked");

        ValidationUtil.Assert(offenders.Count == 0,
            $"{offenders.Count} joint(s) have an anchor that no longer matches the part's transform, so " +
            "PhysX will teleport those links (and their colliders) away from where they were placed the " +
            "moment physics starts:\n    " + string.Join("\n    ", offenders) +
            "\n  Repair: re-derive the anchor from the part's current position with " +
            "Tools > RoboSim > Repair > Joint Anchors, or rebuild that mechanism. Do NOT fix it by " +
            "moving the part — the part is where you want it; the anchor is the stale copy.");

        return $"Joint Anchors Match The Parts: PASSED ({joints} checks). Every joint on " +
               $"{robots} robot(s) has an anchor consistent with where its part actually sits.";
    }

    // The nearest ancestor that is itself an ArticulationBody — the link this joint connects to.
    private static Transform FindParentBody(ArticulationBody body)
    {
        for (Transform t = body.transform.parent; t != null; t = t.parent)
            if (t.GetComponent<ArticulationBody>() != null) return t;
        return null;
    }

    private static string DescribeJoint(ArticulationBody b)
        => b.jointType == ArticulationJointType.PrismaticJoint ? "prismatic"
            : b.jointType == ArticulationJointType.RevoluteJoint ? "revolute" : "fixed";
}
