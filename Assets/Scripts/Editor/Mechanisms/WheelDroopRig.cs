using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// A wheel needs somewhere to go.
//
// WHY THIS EXISTS. Three coplanar wheels on a rigid rail is a statically INDETERMINATE contact set:
// two of them are enough to hold the rail up, so the equilibrium equations have a whole family of
// solutions and the solver may pick any one of them. Measured on a bare flat floor with nothing else
// touching the robot (2026-09-07), it picks the extremes and the middle wheel of a rail carries
// NOTHING — 654V_v2 read 231% / 0% / 67% of an even share down the left rail and 242% / 0% / 60%
// down the right; 654V_v3's right rail read 61% / 221% / 0%. That is Connor's report, driving:
// "it does seem to self fix to 2 wheels each side working ... later on, the right side one of them
// would fix while the left side one would break."
//
// It costs nothing in a straight line — the weight is still on the wheels, and traction is
// mu x (weight on the wheels) however it is split — but it costs a great deal in a TURN, because the
// tyre's mu is keyed on each contact's LATERAL slip and the wheels at the ENDS of a rail are the ones
// that slip most. The rigid solution parks the whole load on exactly the wheels with the least grip.
//
// WHAT THIS IS NOT. Connor, 2026-09-07: "irl with these drivetrains there are not suspensions, there
// may be ever so slight bits it the difference from the screw or axle and the insert that it is in.
// This suspension shouldn't completely change anything, more like just to make sure the wheels
// contact the floor." Exactly so, and this is built to that brief. The travel below is MILLIMETRES —
// the clearance between an axle and its bearing insert, plus the flex in wheels a prefab literally
// names "4 OD Flex Wheel - 45A" and "2 Flex Wheel - 30A". Rubber that soft deflects a millimetre or
// two under a couple of kg on its own. Nothing here is a suspension in the sense a driver would feel;
// it is the give that a real drivetrain has and a rigid body does not.
//
// WHY IT IS A JOINT AND NOT A CONTACT TRICK. Softening the CONTACT was tried twice and reverted both
// times (see the note at the top of WheelGroundContactProbe). Adding the squash to a contact's
// separation gets the mean shares right but only by hunting in and out of contact on ~60% of steps,
// which broke the parked-shove hold and then roll-out; capping the contact impulse cannot even be
// MEASURED, because Physics.ContactEvent reports the impulse PhysX computed rather than the one the
// clamp allowed. To give a wheel a millimetre of give it needs an actual degree of freedom. There is
// nowhere else to put it.
//
// SURGICAL, NOT A RE-RIG. This inserts a link into robots that are already rigged rather than
// rebuilding them from their FBX clusters: the drivetrain rig re-derives wheel positions, sides,
// anchors and masses from geometry, and running it again on a robot Connor has since tuned would
// quietly move things that have nothing to do with droop. Idempotent — a wheel whose parent is
// already a droop link is left alone.
public static class WheelDroopRig
{
    // The runtime needs this name too (RobotMotorController.IgnoreAcrossDroop), and the runtime
    // assembly cannot see this one, so the constant lives there and this is the alias.
    public const string DroopNamePrefix = RobotMotorController.WheelDroopNamePrefix;
    private const string UndoName = "Insert Wheel Droop";

    // Every number lives in DrivetrainTuning, beside the rest of the derived drivetrain, because
    // RobotMotorController.Initialise re-bakes the spring at runtime from the mass it measures then
    // and the two must not drift apart. ROBOSIM_DROOP_SAG_MM overrides the sag for a sweep.
    internal static float SagAtEvenShare
    {
        get
        {
            string mm = System.Environment.GetEnvironmentVariable("ROBOSIM_DROOP_SAG_MM");
            return float.TryParse(mm, out float v) && v > 0f ? v / 100f : DrivetrainTuning.DroopSagAtEvenShare;
        }
    }
    internal static float Travel => SagAtEvenShare * DrivetrainTuning.DroopTravelInShares;
    internal const float DroopMass = DrivetrainTuning.DroopMass;

    // A link with no collider has no inertia for Unity to compute, so it gets one — and it has to be
    // DERIVED from the mass, not picked. A flat 0.01 on every axis was left behind when the mass came
    // down to 0.01 kg, which is a radius of gyration of 100 mm on a 10 g connector: about 15% of a
    // real wheel's rotational inertia, six times over per robot, out of nothing. m*r^2 at 10 mm is
    // what a small metal collar actually has.
    private const float DroopGyrationRadius = 0.1f;   // world units: 10 mm
    private static readonly Vector3 DroopInertia =
        Vector3.one * (DrivetrainTuning.DroopMass * DroopGyrationRadius * DroopGyrationRadius);

    [MenuItem("Tools/RoboSim/Robot/Mechanisms/Insert Wheel Droop", false, 5)]
    private static void InsertMenu()
    {
        GameObject robot = Selection.activeGameObject;
        if (robot == null || robot.GetComponent<ArticulationBody>() == null)
        {
            EditorUtility.DisplayDialog("Insert Wheel Droop",
                "Select the robot ROOT (the object with the ArticulationBody) first.", "OK");
            return;
        }
        int added = Insert(robot, out string report);
        Debug.Log($"{UndoName}: {report}", robot);
        EditorUtility.DisplayDialog("Insert Wheel Droop", report, "OK");
        if (added > 0) EditorUtility.SetDirty(robot);
    }

    // Batch entry: ROBOSIM_DROOP_ROBOT filters by prefab name, so one robot can be done and measured
    // before the rest follow.
    public static void RunBatchInsert() => ValidationUtil.RunBatch("Insert Wheel Droop", () =>
    {
        string filter = System.Environment.GetEnvironmentVariable("ROBOSIM_DROOP_ROBOT");
        var lines = new System.Text.StringBuilder();
        int robots = 0;
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab.GetComponent<RobotMotorController>() == null) continue;
            if (!string.IsNullOrEmpty(filter) && !prefab.name.Contains(filter)) continue;

            string path = AssetDatabase.GetAssetPath(prefab);
            GameObject instance = PrefabUtility.LoadPrefabContents(path);
            try
            {
                int added = Insert(instance, out string report);
                lines.AppendLine($"  '{prefab.name}': {report}");
                if (added > 0) PrefabUtility.SaveAsPrefabAsset(instance, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(instance); }
            robots++;
        }
        ValidationUtil.Assert(robots > 0,
            $"no robot prefab matched '{filter}' — nothing was changed");
        AssetDatabase.SaveAssets();
        return $"Insert Wheel Droop: PASSED ({robots} robot(s))\n{lines.ToString().TrimEnd()}";
    });

    // Insert a droop link above every drive wheel of `robotRoot`. Returns how many links it TOUCHED
    // — added or re-configured — because the caller uses that to decide whether to save, and a
    // re-configure that is not saved is a silent no-op. It was: changing the spring or the mass and
    // re-running appeared to work and changed nothing on disk, so a sweep measured the same numbers
    // three times over.
    public static int Insert(GameObject robotRoot, out string report)
    {
        report = "no drive wheels found";
        if (robotRoot == null) return 0;
        ArticulationBody root = robotRoot.GetComponent<ArticulationBody>();
        RobotMotorController motor = robotRoot.GetComponentInChildren<RobotMotorController>(true);
        if (root == null || motor == null) return 0;

        var wheels = new List<ArticulationBody>();
        if (motor.leftWheels != null) foreach (ArticulationBody w in motor.leftWheels) if (w != null) wheels.Add(w);
        if (motor.rightWheels != null) foreach (ArticulationBody w in motor.rightWheels) if (w != null) wheels.Add(w);
        if (wheels.Count == 0) return 0;

        // Sized from the robot's own weight, so a heavy robot squashes the same SHARE as a light one.
        // RobotMotorController.Initialise re-bakes these at runtime from the mass it measures then —
        // the same arrangement the wheel drives already use — so this is the edit-mode value, which
        // is what every batch validator and probe actually simulates.
        float mass = DrivetrainTuning.MeasureTotalMass(root);
        DrivetrainTuning.DroopSpring(mass, wheels.Count, Physics.gravity.y,
            out float stiffness, out float damping);

        int added = 0, already = 0;
        foreach (ArticulationBody wheel in wheels)
        {
            Transform parent = wheel.transform.parent;
            ArticulationBody parentBody = parent != null ? parent.GetComponent<ArticulationBody>() : null;
            if (parentBody != null && parentBody.jointType == ArticulationJointType.PrismaticJoint)
            {
                // Already has one: re-configure it rather than skip, so a change to the spring
                // constants reaches robots that were done on an earlier pass. The mass moves back
                // with it — the droop link's mass was taken OUT of the wheel, so changing it has to
                // return the difference or the robot quietly loses weight every time this is run.
                // Idempotent: once the link is at DroopMass there is nothing left to move.
                float back = parentBody.mass - DrivetrainTuning.DroopMass;
                wheel.mass = Mathf.Max(wheel.mass + back, 0.01f);
                parentBody.mass = DrivetrainTuning.DroopMass;
                Configure(parentBody, root.transform, stiffness, damping);
                already++;
                continue;
            }
            if (parent == null) continue;

            string suffix = wheel.name.StartsWith(RobotPartClassifier.WheelLinkNamePrefix)
                ? wheel.name.Substring(RobotPartClassifier.WheelLinkNamePrefix.Length)
                : wheel.name;
            var droop = new GameObject(DroopNamePrefix + suffix);

            // The droop link takes the wheel's transform EXACTLY, and the wheel then sits on it at
            // identity. Copying the transform rather than computing one keeps the wheel's world
            // pose, its world scale (CAD nodes in these imports carry a 1/2.54 inch conversion, so
            // any recomputation is a puzzle) and its axle direction all untouched by construction.
            droop.transform.SetParent(parent, false);
            droop.transform.localPosition = wheel.transform.localPosition;
            droop.transform.localRotation = wheel.transform.localRotation;
            droop.transform.localScale = wheel.transform.localScale;

            wheel.transform.SetParent(droop.transform, false);
            wheel.transform.localPosition = Vector3.zero;
            wheel.transform.localRotation = Quaternion.identity;
            wheel.transform.localScale = Vector3.one;

            Configure(droop.AddComponent<ArticulationBody>(), root.transform, stiffness, damping);

            // The mass comes OUT of the wheel: see DrivetrainTuning.DroopMass.
            wheel.mass = Mathf.Max(wheel.mass - DrivetrainTuning.DroopMass, 0.01f);
            added++;
        }

        report = added > 0
            ? $"{added} droop link(s) added ({Travel * 100f:0.#} mm of travel, " +
              $"{SagAtEvenShare * 100f:0.#} mm under an even share of {mass * Mathf.Abs(Physics.gravity.y) / wheels.Count:0} " +
              $"force){(already > 0 ? $", {already} re-configured" : "")}"
            : $"{already} droop link(s) re-configured ({Travel * 100f:0.#} mm of travel, " +
              $"{SagAtEvenShare * 100f:0.#} mm under an even share, {DrivetrainTuning.DroopMass:0.###} kg each)";
        return added + already;
    }

    // Configure one droop body. `chassis` supplies the UP the wheel droops along — world up would be
    // wrong the moment the robot pitches, and the joint axis is fixed in the chassis anyway.
    public static void Configure(ArticulationBody droop, Transform chassis, float stiffness, float damping)
    {
        droop.jointType = ArticulationJointType.PrismaticJoint;  // BEFORE the drive: a type change resets it
        droop.linearLockX = ArticulationDofLock.LimitedMotion;
        droop.linearLockY = ArticulationDofLock.LockedMotion;
        droop.linearLockZ = ArticulationDofLock.LockedMotion;

        // A prismatic joint slides along the ANCHOR's X (see AddMechanismJointWindow, which sets
        // every other prismatic on these robots the same way), and anchorRotation is expressed in
        // the CHILD's frame — so this is the chassis's up, brought into the droop link's own axes.
        Vector3 upLocal = Quaternion.Inverse(droop.transform.rotation) * chassis.up;
        droop.anchorPosition = Vector3.zero;
        droop.anchorRotation = Quaternion.FromToRotation(Vector3.right, upLocal.normalized);
        droop.matchAnchors = true;   // as the wheel links do; JointAnchorValidation then skips it

        droop.mass = DrivetrainTuning.DroopMass;
        droop.automaticCenterOfMass = false;
        droop.centerOfMass = Vector3.zero;
        droop.automaticInertiaTensor = false;
        droop.inertiaTensor = DroopInertia;
        droop.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        // Zero is the bottom of the travel: the wheel hanging where the CAD put it, which is exactly
        // where it sits today. The ground pushes it UP from there and the spring pushes back, so the
        // chassis settles about SagAtEvenShare lower than it used to and every wheel carries what its
        // position says it should.
        ArticulationDrive d = droop.xDrive;
        d.driveType = ArticulationDriveType.Force;
        d.lowerLimit = 0f;
        d.upperLimit = Travel;
        d.target = 0f;
        d.targetVelocity = 0f;
        d.stiffness = stiffness;
        d.damping = damping;
        d.forceLimit = float.MaxValue;
        droop.xDrive = d;
    }

    // What the droop links actually came out as, per robot: the axis they slide along in WORLD
    // terms, how far off vertical it is, and the spring. An axis that is not vertical does not read
    // as a broken axis when the robot runs — it reads as a robot that falls over.
    public static void RunBatchReport() => ValidationUtil.RunBatch("Wheel Droop Report", () =>
    {
        var lines = new System.Text.StringBuilder();
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            if (prefab.GetComponent<RobotMotorController>() == null) continue;
            var found = new List<ArticulationBody>();
            foreach (ArticulationBody b in prefab.GetComponentsInChildren<ArticulationBody>(true))
                if (IsDroop(b)) found.Add(b);
            if (found.Count == 0) { lines.AppendLine($"  '{prefab.name}': no droop links"); continue; }

            lines.AppendLine($"  '{prefab.name}': {found.Count} droop link(s)");
            foreach (ArticulationBody b in found)
            {
                Vector3 axis = b.transform.TransformDirection(b.anchorRotation * Vector3.right).normalized;
                ArticulationDrive d = b.xDrive;
                lines.AppendLine(
                    $"    {b.name,-16} axis {axis} ({Vector3.Angle(axis, Vector3.up):0.0} deg off world up)  " +
                    $"lock {b.linearLockX}  travel {d.lowerLimit:0.###}..{d.upperLimit:0.###}  " +
                    $"k {d.stiffness:0} c {d.damping:0}  mass {b.mass:0.###}  " +
                    $"lossyScale {b.transform.lossyScale.x:0.###}  matchAnchors {b.matchAnchors}");
            }
        }
        return $"Wheel Droop Report: PASSED\n{lines.ToString().TrimEnd()}";
    });

    // Where each drive wheel's contact patch actually sits, in the robot's own frame. Rigid contacts
    // hide this completely — an indeterminate rail rests on whichever two wheels the solver picks,
    // so a wheel that is millimetres proud reads exactly like one that is not. A spring cannot hide
    // it: the proud wheel compresses further and carries more, in proportion.
    public static void RunBatchGeometry() => ValidationUtil.RunBatch("Wheel Geometry", () =>
    {
        var lines = new System.Text.StringBuilder();
        foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
        {
            RobotMotorController motor = prefab.GetComponent<RobotMotorController>();
            ArticulationBody root = prefab.GetComponent<ArticulationBody>();
            if (motor == null || root == null) continue;

            var wheels = new List<ArticulationBody>();
            if (motor.leftWheels != null) foreach (ArticulationBody w in motor.leftWheels) if (w != null) wheels.Add(w);
            if (motor.rightWheels != null) foreach (ArticulationBody w in motor.rightWheels) if (w != null) wheels.Add(w);
            if (wheels.Count == 0) continue;

            lines.AppendLine($"  '{prefab.name}': bottom of each wheel, in the root's frame (mm)");
            float lowest = float.MaxValue, highest = float.MinValue;
            foreach (ArticulationBody w in wheels)
            {
                SphereCollider s = w.GetComponentInChildren<SphereCollider>(true);
                if (s == null) { lines.AppendLine($"    {w.name,-16} no sphere collider"); continue; }
                float radius = s.radius * Mathf.Abs(s.transform.lossyScale.x);
                Vector3 centre = s.transform.TransformPoint(s.center);
                Vector3 local = root.transform.InverseTransformPoint(centre);
                float bottom = local.y - radius / Mathf.Max(Mathf.Abs(root.transform.lossyScale.y), 1e-6f);
                lowest = Mathf.Min(lowest, bottom); highest = Mathf.Max(highest, bottom);
                lines.AppendLine($"    {w.name,-16} along {local.x * 100f,8:0.0}  across {local.z * 100f,8:0.0}  " +
                                 $"centre {local.y * 100f,7:0.0}  radius {radius * 100f,6:0.0}  bottom {bottom * 100f,7:0.00}");
            }
            lines.AppendLine($"    -> {(highest - lowest) * 100f:0.00} mm between the lowest wheel and the highest");
        }
        return $"Wheel Geometry: PASSED\n{lines.ToString().TrimEnd()}";
    });

    // Is this body one of the droop links? Every tool that hunts for LIFTS looks for exactly what a
    // droop link is — a prismatic with an unlocked axis and real travel — so each of them has to ask.
    public static bool IsDroop(ArticulationBody body)
        => body != null
           && body.jointType == ArticulationJointType.PrismaticJoint
           && body.name.StartsWith(DroopNamePrefix);
}
