using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// NonSupportingLink does its whole job by NOT happening — a contact that never carries weight — so
// every way it can fail is silent. The robot climbs its intake again and nothing anywhere says so.
// This is the guard, and it checks the ways it can be wired up to do nothing:
//
//   1. On a link with no colliders. Register() walks GetComponentsInChildren<Collider>; on a bare
//      transform it registers zero and returns before it ever subscribes.
//   2. On a link whose colliders are all triggers. Triggers are skipped by design (they generate no
//      contacts to modify), so a component on a trigger-only link is a component doing nothing.
//   3. On a WHEEL. The rule kills exactly the contact a wheel exists to make. This one would not be
//      silent — it would delete the drivetrain — but it is the worst way to hold the component and
//      costs one line to rule out.
//
// It deliberately does NOT assert which links carry it: that is a per-robot design decision, and a
// validator that pins the list would have to be edited every time a robot gains a mechanism.
public static class NonSupportingLinkValidation
{
    [MenuItem("Tools/RoboSim/Validate/Non-Supporting Links", false, 47)]
    public static void Validate() => ValidationUtil.RunInteractive("Non-Supporting Links", Run);

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Non-Supporting Links", Run);

    private static string Run()
    {
        var failures = new List<string>();
        int checks = 0, components = 0;

        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab == null) continue;

            ArticulationBody root = prefab.GetComponentInChildren<ArticulationBody>(true);
            var wheelSet = new HashSet<ArticulationBody>();
            if (root != null)
                foreach (ArticulationBody w in RobotPhysicsValidation.FindWheels(root, out _, out _))
                    wheelSet.Add(w);

            foreach (NonSupportingLink link in prefab.GetComponentsInChildren<NonSupportingLink>(true))
            {
                components++;

                int solid = 0;
                foreach (Collider c in link.GetComponentsInChildren<Collider>(true))
                    if (c != null && !c.isTrigger) solid++;

                checks++;
                if (solid == 0)
                    failures.Add($"{prefab.name}/{link.name}: no non-trigger colliders, so it " +
                                 "registers nothing and mutes nothing — it is on the wrong link");

                checks++;
                ArticulationBody owner = link.GetComponentInParent<ArticulationBody>();
                if (owner != null && wheelSet.Contains(owner))
                    failures.Add($"{prefab.name}/{link.name}: this is a DRIVE WHEEL. The rule kills " +
                                 "the resting contact a wheel exists to make; the robot would sink " +
                                 "through the floor on that corner");
            }
        }

        if (failures.Count > 0)
            return $"Non-Supporting Links: FAILED ({failures.Count} of {checks} checks)\n    " +
                   string.Join("\n    ", failures);

        return $"Non-Supporting Links: PASSED ({checks} checks). {components} link(s) across the " +
               "robots may push field objects but can never stand on them.";
    }
}
