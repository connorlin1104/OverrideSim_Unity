using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// THE REPAIR FOR WHAT JointAnchorValidation FINDS.
//
// A joint's parent-side anchor is a cached copy of where the part was when the joint was made. When
// it disagrees with the part's actual transform, PhysX does not warn — at joint position zero it
// forces the two frames together, which means it MOVES THE LINK, and its colliders with it, away
// from the pose you see in the editor. Re-deriving the anchor from the part's current transform is
// the whole fix: the part is right, the anchor is the stale copy.
//
// This deliberately edits the PREFAB ASSET rather than a scene instance, because that is where the
// bad value lives and where every future instantiation reads it from.
public static class RepairJointAnchorsTool
{
    // Same tolerances as the validator, for the obvious reason: a repair that leaves offenders the
    // check still reports, or that rewrites anchors the check is happy with, is worse than nothing.
    private const float PositionToleranceUnits = 0.001f;   // 0.1 mm at 1 unit = 100 mm
    private const float RotationToleranceDeg = 0.1f;

    [MenuItem("Tools/RoboSim/Robot/Advanced/Repair Joint Anchors", false, 10)]
    private static void RunInteractive()
    {
        string report = Run();
        Debug.Log(report);
        EditorUtility.DisplayDialog("Repair Joint Anchors", report, "OK");
    }

    public static void RunBatchRepair()
    {
        Debug.Log(Run());
        EditorApplication.Exit(0);
    }

    public static string Run()
    {
        var repaired = new List<string>();
        int robots = 0, joints = 0;

        foreach (string path in RoboSimPaths.RobotPrefabPaths())
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (contents.GetComponent<ArticulationBody>() == null) continue;
                robots++;
                bool dirty = false;

                foreach (ArticulationBody body in contents.GetComponentsInChildren<ArticulationBody>(true))
                {
                    if (body == null || body.matchAnchors) continue;

                    // Hierarchy, never isRoot — see MechanismBuildUtil.RederiveParentAnchors for why
                    // that property is a trap on an uninstantiated prefab.
                    Transform child = body.transform;
                    Transform parent = FindParentBody(body);
                    if (parent == null) continue;
                    joints++;

                    Vector3 expectedPos =
                        parent.InverseTransformPoint(child.TransformPoint(body.anchorPosition));
                    Quaternion expectedRot =
                        Quaternion.Inverse(parent.rotation) * (child.rotation * body.anchorRotation);

                    float posOff = Vector3.Distance(expectedPos, body.parentAnchorPosition);
                    float rotOff = Quaternion.Angle(expectedRot, body.parentAnchorRotation);
                    if (posOff <= PositionToleranceUnits && rotOff <= RotationToleranceDeg) continue;

                    MechanismBuildUtil.RederiveParentAnchors(body);
                    dirty = true;
                    repaired.Add($"{contents.name}/{body.name}: anchor moved back " +
                                 $"{posOff * 100f:0.0} mm and {rotOff:0.0} deg onto the part " +
                                 $"({body.mass:0.000} kg that would otherwise have teleported there)");
                }

                if (dirty) PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }

        AssetDatabase.SaveAssets();

        if (repaired.Count == 0)
            return $"Repair Joint Anchors: nothing to do — all {joints} joint(s) across {robots} " +
                   "robot(s) already have anchors that match their parts.";

        return $"Repair Joint Anchors: fixed {repaired.Count} of {joints} joint(s) across {robots} " +
               "robot(s):\n    " + string.Join("\n    ", repaired) +
               "\n  These links will now start where the editor draws them. Anything measured off " +
               "the robot's mass distribution changes as a result — re-run Mass & Balance, and " +
               "ApplyDriveTuningTool if the drive force limit was derived from it.";
    }

    private static Transform FindParentBody(ArticulationBody body)
    {
        for (Transform t = body.transform.parent; t != null; t = t.parent)
            if (t.GetComponent<ArticulationBody>() != null) return t;
        return null;
    }
}
