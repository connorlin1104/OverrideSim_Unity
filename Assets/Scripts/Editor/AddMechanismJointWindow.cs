using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Author or fix ONE mechanism joint on an already-imported, already-set-up robot — entirely in
// Unity, no Fusion round-trip. The URDF export carries the accurate joint axes/limits, but if a
// part came in as a plain fixed attachment (or with the wrong type/limits), this converts it to a
// working revolute/continuous/prismatic mechanism (or back to fixed), wiring it identically to the
// joints the post-processor wires — so it shows up in the home-screen controller-config UI and is
// mappable to a button. Re-applying to the same link replaces its mechanism, so it doubles as the
// "fix the wrong joint" path.
//
// Scope: both robot kinds. A URDF link already IS an ArticulationBody (including parts that
// imported as fixed) — this retypes it. A mesh/FBX part has no body yet — this SPLITS a new moving
// link off the chassis: it adds the body, so the part's colliders/meshes leave the chassis body and
// become their own link, then joints it. Either way the result is wired identically. It can't build
// a moving body with no geometry — model the part in CAD first.
//
// Usage: Tools > RoboSim > Robot > Advanced > Add or Fix Mechanism Joint.
public class AddMechanismJointWindow : EditorWindow
{
    private const string Title = "Add or Fix Mechanism Joint";
    private const float MetersPerUnit = 0.1f; // this project's world: 1 scaled unit = 0.1 m

    // Auto is last so this enum's stored integer values stay stable for X/Y/Z/Custom across the
    // upgrade (an open window's serialized selection would otherwise shift by one).
    private enum AxisPreset { X, Y, Z, Custom, Auto }

    [SerializeField] private GameObject childLink;
    // Default to a free-spinning axle: the common "make this roller/shaft turn" case, and the one
    // that used to sweep like a lever and jam when it defaulted to a limited Revolute.
    [SerializeField] private AddMechanismJoint.JointType jointType = AddMechanismJoint.JointType.Continuous;
    [SerializeField] private AxisPreset axisPreset = AxisPreset.Auto;
    [SerializeField] private Vector3 customAxis = Vector3.up;
    [SerializeField] private Vector3 anchor = Vector3.zero;
    [SerializeField] private float lowerLimit = -90f;
    [SerializeField] private float upperLimit = 90f;
    [SerializeField] private bool autoAssignButton = true;
    [SerializeField] private List<GameObject> alsoMove = new List<GameObject>();
    [SerializeField] private bool reverseDirection;
    [SerializeField] private bool pneumaticToggle;

    [MenuItem("Tools/RoboSim/Robot/Advanced/Add or Fix Mechanism Joint", false, 4)]
    private static void ShowWindow()
    {
        AddMechanismJointWindow window = GetWindow<AddMechanismJointWindow>(Title);
        window.minSize = new Vector2(420f, 320f);
        window.Show();
    }

    private void OnEnable()
    {
        if (childLink == null) childLink = Selection.activeGameObject;
    }

    private void OnGUI()
    {
        EditorGUILayout.HelpBox(
            "Turns one already-imported link into a controllable mechanism (or fixes/removes one). " +
            "The robot must already be set up (Set Up Imported Robot). Pick the moving link, the " +
            "joint type, and the axis in the LINK's local frame.", MessageType.Info);

        childLink = (GameObject)EditorGUILayout.ObjectField("Child Link", childLink, typeof(GameObject), true);
        if (childLink == null)
        {
            EditorGUILayout.HelpBox("Select the link (URDF) or part (mesh/FBX) that should move.", MessageType.Warning);
            return;
        }
        RobotMechanisms registry = childLink.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
        {
            EditorGUILayout.HelpBox(
                "This is not under a set-up robot (no RobotMechanisms on the root). Run " +
                "Tools > RoboSim > Robot > Set Up Imported Robot first.", MessageType.Error);
            return;
        }
        // A mesh/FBX part has no ArticulationBody yet — Apply splits it off the chassis into a new
        // moving link. That needs a rigged chassis (ArticulationBody) above it.
        bool willSplitNewLink = childLink.GetComponent<ArticulationBody>() == null;
        if (willSplitNewLink)
        {
            if (childLink.GetComponentInParent<ArticulationBody>() == null)
            {
                EditorGUILayout.HelpBox(
                    $"'{childLink.name}' has no ArticulationBody and no rigged chassis above it. Run " +
                    "Set Up Imported Robot first, then pick the part that should move.", MessageType.Error);
                return;
            }
            EditorGUILayout.HelpBox(
                $"'{childLink.name}' isn't a moving link yet — Apply will split it off the chassis as a new " +
                "mechanism (its meshes and colliders leave the chassis body). Pick the node that moves as a " +
                "unit, and set the Anchor to the hinge/slide axis location.", MessageType.Info);
        }

        jointType = (AddMechanismJoint.JointType)EditorGUILayout.EnumPopup("Joint Type", jointType);

        bool showAxis = jointType != AddMechanismJoint.JointType.Fixed;
        bool showLimits = jointType == AddMechanismJoint.JointType.Revolute ||
                          jointType == AddMechanismJoint.JointType.Prismatic;

        if (showAxis)
        {
            axisPreset = (AxisPreset)EditorGUILayout.EnumPopup(new GUIContent("Axis (link-local)",
                "Auto guesses the hinge/slide axis and anchor from the part's geometry — good for a " +
                "mesh/FBX part. X/Y/Z/Custom set it by hand (use these to fix a URDF link's axis)."),
                axisPreset);
            if (axisPreset == AxisPreset.Auto)
            {
                EditorGUILayout.HelpBox("Axis + anchor will be inferred from the part's geometry. " +
                    "It's a best guess — check the result and switch to X/Y/Z/Custom if it's off.",
                    MessageType.None);
            }
            else
            {
                if (axisPreset == AxisPreset.Custom)
                    customAxis = EditorGUILayout.Vector3Field("Custom Axis", customAxis);
                anchor = EditorGUILayout.Vector3Field(new GUIContent("Anchor (link-local)",
                    "Pivot/slide origin in the link's local space. 0 = the link origin (the usual case)."),
                    anchor);
            }
        }

        if (jointType != AddMechanismJoint.JointType.Fixed)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(new GUIContent("Parts That Move Together",
                "Extra parts to fold into this ONE moving link so the whole axle co-rotates (sprockets, " +
                "rollers, the shaft). Leave the MOTOR out — anything not listed stays welded to the chassis " +
                "and stays still. Only plain, un-rigged parts can be added."), EditorStyles.miniBoldLabel);
            for (int i = 0; i < alsoMove.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                alsoMove[i] = (GameObject)EditorGUILayout.ObjectField(alsoMove[i], typeof(GameObject), true);
                if (GUILayout.Button("X", GUILayout.Width(24))) { alsoMove.RemoveAt(i); i--; }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("Add Part", GUILayout.Width(100))) alsoMove.Add(null);

            if (jointType == AddMechanismJoint.JointType.Revolute)
                pneumaticToggle = EditorGUILayout.Toggle(new GUIContent("Piston Toggle",
                    "Drive this hinge like a binary pneumatic: each button press snaps it between the Lower " +
                    "and Upper limit, instead of a hold-to-run motor. Use for a piston-driven pivot/flipper."),
                    pneumaticToggle);

            reverseDirection = EditorGUILayout.Toggle(new GUIContent("Reverse Direction",
                "Flip the drive sense if the mechanism runs backward for 'forward' input (motor) or starts " +
                "at the wrong end (piston)."), reverseDirection);
        }

        if (jointType != AddMechanismJoint.JointType.Fixed)
            autoAssignButton = EditorGUILayout.Toggle(new GUIContent("Auto-Assign Button",
                "After applying, map this mechanism to the next free controller button (motor = " +
                "forward/reverse pair, pneumatic = toggle) so it's drivable without opening Configure Controller."),
                autoAssignButton);

        if (showLimits)
        {
            if (jointType == AddMechanismJoint.JointType.Revolute)
            {
                EditorGUILayout.LabelField("Limits (degrees)", EditorStyles.miniBoldLabel);
                lowerLimit = EditorGUILayout.FloatField("Lower", lowerLimit);
                upperLimit = EditorGUILayout.FloatField("Upper", upperLimit);
            }
            else // Prismatic
            {
                EditorGUILayout.LabelField("Stroke (scaled units, 1 unit = 0.1 m)", EditorStyles.miniBoldLabel);
                lowerLimit = EditorGUILayout.FloatField("Lower", lowerLimit);
                upperLimit = EditorGUILayout.FloatField("Upper", upperLimit);
                EditorGUILayout.LabelField(" ",
                    $"= {lowerLimit * MetersPerUnit:0.###} .. {upperLimit * MetersPerUnit:0.###} m");
            }
        }
        else if (jointType == AddMechanismJoint.JointType.Continuous)
        {
            EditorGUILayout.HelpBox("Continuous spins freely — no limits.", MessageType.None);
        }
        else if (jointType == AddMechanismJoint.JointType.Fixed)
        {
            EditorGUILayout.HelpBox(
                "Fixed welds the link to its parent and REMOVES any mechanism it had.", MessageType.None);
        }

        EditorGUILayout.Space();
        if (!GUILayout.Button(jointType == AddMechanismJoint.JointType.Fixed ? "Apply (make fixed)" : "Apply Joint",
            GUILayout.Height(30))) return;

        try
        {
            Vector3 axis;
            Vector3 effectiveAnchor = anchor;
            if (axisPreset == AxisPreset.Auto && jointType != AddMechanismJoint.JointType.Fixed)
            {
                MechanismAutoDetect.TryInferAxisAnchor(childLink, jointType, out axis, out effectiveAnchor);
            }
            else
            {
                axis = axisPreset switch
                {
                    AxisPreset.X => Vector3.right,
                    AxisPreset.Y => Vector3.up,
                    AxisPreset.Z => Vector3.forward,
                    AxisPreset.Custom => customAxis,
                    _ => Vector3.up, // Auto + Fixed: axis unused
                };
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Title);
            int group = Undo.GetCurrentGroup();

            bool asToggle = jointType == AddMechanismJoint.JointType.Revolute && pneumaticToggle;
            var options = new AddMechanismJoint.Options
            {
                alsoMove = alsoMove.Count > 0 ? alsoMove.ToArray() : null,
                reverseDirection = reverseDirection,
                actuation = asToggle ? AddMechanismJoint.Actuation.Toggle : AddMechanismJoint.Actuation.Auto,
            };
            AddMechanismJoint.Apply(childLink, jointType, axis, effectiveAnchor, lowerLimit, upperLimit, options, useUndo: true);

            // Map it to a free button so it's drivable immediately (skipped for Fixed, which removed
            // the mechanism). A piston-toggle hinge maps like a pneumatic (one toggle button), so pass
            // Prismatic for the button style. Non-fatal: a full map just means the user maps it later.
            string buttonNote = "";
            if (autoAssignButton && jointType != AddMechanismJoint.JointType.Fixed)
            {
                RobotMechanisms reg = childLink.GetComponentInParent<RobotMechanisms>();
                AddMechanismJoint.JointType buttonType = asToggle ? AddMechanismJoint.JointType.Prismatic : jointType;
                if (reg != null)
                    buttonNote = "\nButton: " + MechanismAutoDetect.AssignButtons(
                        reg.robotId, UrdfPostProcessor.Slugify(childLink.name), buttonType);
            }
            Undo.CollapseUndoOperations(group);

            EditorUtility.DisplayDialog(Title,
                $"'{childLink.name}' is now a {jointType} joint" +
                (jointType == AddMechanismJoint.JointType.Fixed ? " (mechanism removed)." : " and is wired as a mechanism.") +
                buttonNote +
                "\n\nSave the scene, then Robot > Validate Robot Physics to test it.", "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog(Title, e.Message, "OK");
            Debug.LogException(e, childLink);
        }
    }
}

// The joint-authoring core, split out so the headless validator can drive it without the window.
public static class AddMechanismJoint
{
    public enum JointType { Revolute, Continuous, Prismatic, Fixed }

    // How a mechanism is actuated from its button(s), independent of the joint's DOF type. Auto
    // classifies by joint type (revolute/continuous -> hold-to-run motor, prismatic -> pneumatic
    // toggle); Toggle forces a binary snap between the limits (a piston-driven pivot/flipper);
    // HoldToRun forces a velocity motor.
    public enum Actuation { Auto, HoldToRun, Toggle }

    // Optional authoring extras for Apply. All default to "single picked part, forward direction,
    // auto actuation", so the legacy 7-arg overload keeps working unchanged.
    public struct Options
    {
        public GameObject[] alsoMove;   // plain parts to fold into the one driven link
        public bool reverseDirection;   // flip motor sense / swap pneumatic endpoints
        public Actuation actuation;
    }

    private const float WorldScaleFactor = 10f;  // this project's world: 1 scaled unit = 0.1 m
    private const float DefaultLinkMass = 1f;     // fallback mass for a split link with no closed mesh
    private const float MinSplitMass = 1e-3f;     // below this the geometry mass is treated as absent

    // Configures the link's ArticulationBody as the requested joint (type -> DOF locks -> anchors
    // -> limits, matching the URDF importer's AdjustMovement and the post-processor's anchor
    // re-derivation), then wires (or removes) the mechanism and refreshes the catalog. When the link
    // is a plain mesh part with no body, first splits a new link off the chassis (adds the body + a
    // geometry-derived mass). Throws on any precondition failure. useUndo=false for batch/headless callers.
    public static void Apply(GameObject link, JointType type, Vector3 axis, Vector3 anchor,
        float lowerLimit, float upperLimit, bool useUndo)
        => Apply(link, type, axis, anchor, lowerLimit, upperLimit, default, useUndo);

    // Full overload: adds part-grouping (the driven link co-rotates a whole axle while the unlisted
    // motor housing stays welded), a reverse-direction flip, and an actuation override (drive a
    // revolute as a pneumatic toggle — a piston-driven pivot). See Options.
    public static void Apply(GameObject link, JointType type, Vector3 axis, Vector3 anchor,
        float lowerLimit, float upperLimit, Options options, bool useUndo)
    {
        if (link == null) throw new ArgumentNullException(nameof(link));

        RobotMechanisms registry = link.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
            throw new InvalidOperationException(
                $"'{link.name}' is not under a set-up robot (no RobotMechanisms). Run " +
                "Set Up Imported Robot first.");
        GameObject root = registry.gameObject;

        // Fold any "parts that move together" into this link BEFORE it gets a body, so the driven
        // link is the whole axle (its mass covers them) while the unlisted motor housing stays welded
        // to the chassis. Only meaningful for a moving joint.
        bool mergedExtras = type != JointType.Fixed && MergeIntoLink(link, options.alsoMove, useUndo);

        // A URDF link already carries an ArticulationBody; a plain FBX part does not. When it
        // doesn't, split a new moving link off its rigid parent: adding the body moves this part's
        // colliders/meshes out of the chassis body into their own link, jointed to the nearest
        // ancestor body below. Needs a rigged chassis above it, and there's no mechanism to remove
        // yet, so Fixed is meaningless here.
        ArticulationBody body = link.GetComponent<ArticulationBody>();
        if (body == null)
        {
            if (FindParentBodyOf(link.transform) == null)
                throw new InvalidOperationException(
                    $"'{link.name}' has no ArticulationBody and no rigged chassis above it. Run Set Up " +
                    "Imported Robot first, then pick the part that should move.");
            if (type == JointType.Fixed)
                throw new InvalidOperationException(
                    $"'{link.name}' isn't a moving link yet, so there's nothing to make Fixed. Pick " +
                    "Revolute, Continuous, or Prismatic to split it off as a mechanism.");

            // Size the new link's mass from its geometry (part name -> density) before it gets a body.
            float density = RobotPartClassifier.TryGetDensity(link.name, out float d)
                ? d : RobotPartClassifier.DefaultDensity;
            float massKg = RobotMassFromGeometry.MassForLinkNode(link, root.transform, WorldScaleFactor, density);

            body = useUndo ? Undo.AddComponent<ArticulationBody>(link) : link.AddComponent<ArticulationBody>();
            body.mass = massKg > MinSplitMass ? massKg : DefaultLinkMass;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.angularDamping = 0.05f;
            body.ResetCenterOfMass();
            body.ResetInertiaTensor(); // from the colliders that just became this link's

            // The split-off geometry must keep the robot's tag so the match loaders still see it.
            if (useUndo) Undo.RecordObject(link, "Add or Fix Mechanism Joint");
            link.tag = root.tag;
        }
        else if (mergedExtras)
        {
            // Extra geometry just joined an existing link — refresh its mass distribution.
            if (useUndo) Undo.RecordObject(body, "Add or Fix Mechanism Joint");
            body.ResetCenterOfMass();
            body.ResetInertiaTensor();
        }

        // Drivetrain wheels belong to the joysticks via RobotMotorController, not the buttons.
        RobotMotorController motor = root.GetComponent<RobotMotorController>();
        if (motor != null && ((motor.leftWheels != null && Array.IndexOf(motor.leftWheels, body) >= 0) ||
                              (motor.rightWheels != null && Array.IndexOf(motor.rightWheels, body) >= 0)))
            throw new InvalidOperationException(
                $"'{link.name}' is a drivetrain wheel wired to the joysticks — it can't be a button mechanism.");

        if (type != JointType.Fixed)
            axis = axis.sqrMagnitude < 1e-8f ? Vector3.right : axis.normalized;

        if (useUndo) Undo.RecordObject(body, "Add or Fix Mechanism Joint");

        // Type first (changing it resets the drives), then the DOF locks + anchor rotation exactly
        // as the importer's AdjustMovement would set them for a joint of this type.
        switch (type)
        {
            case JointType.Revolute:
            case JointType.Continuous:
                body.jointType = ArticulationJointType.RevoluteJoint;
                body.linearLockX = ArticulationDofLock.LockedMotion;
                body.linearLockY = ArticulationDofLock.LockedMotion;
                body.linearLockZ = ArticulationDofLock.LockedMotion;
                body.twistLock = type == JointType.Continuous
                    ? ArticulationDofLock.FreeMotion : ArticulationDofLock.LimitedMotion;
                body.anchorRotation = Quaternion.FromToRotation(Vector3.right, -axis);
                break;
            case JointType.Prismatic:
                body.jointType = ArticulationJointType.PrismaticJoint;
                body.linearLockX = ArticulationDofLock.LimitedMotion;
                body.linearLockY = ArticulationDofLock.LockedMotion;
                body.linearLockZ = ArticulationDofLock.LockedMotion;
                body.anchorRotation = Quaternion.FromToRotation(Vector3.right, axis);
                break;
            case JointType.Fixed:
                body.jointType = ArticulationJointType.FixedJoint;
                break;
        }

        body.anchorPosition = anchor;

        // Re-derive the parent-side anchor from the actual (already-scaled) transforms with
        // matchAnchors off — the same fix the post-processor's scale bake uses, or PhysX snaps the
        // link back on the first Simulate().
        body.matchAnchors = false;
        ArticulationBody parentBody = FindParentBody(body);
        if (parentBody != null)
        {
            Transform p = parentBody.transform, c = body.transform;
            body.parentAnchorPosition = p.InverseTransformPoint(c.TransformPoint(body.anchorPosition));
            body.parentAnchorRotation = Quaternion.Inverse(p.rotation) * (c.rotation * body.anchorRotation);
        }

        // Travel limits (degrees for revolute, scaled units for prismatic; set BEFORE WireMechanism
        // because the pneumatic reads these as its endpoints).
        if (type == JointType.Revolute || type == JointType.Prismatic)
        {
            ArticulationDrive drive = body.xDrive;
            drive.lowerLimit = Mathf.Min(lowerLimit, upperLimit);
            drive.upperLimit = Mathf.Max(lowerLimit, upperLimit);
            body.xDrive = drive;
        }

        string id = UrdfPostProcessor.Slugify(link.name);
        if (type == JointType.Fixed)
        {
            UrdfPostProcessor.RemoveMechanism(registry, id, useUndo);
        }
        else
        {
            UrdfPostProcessor.MechKind kind = ResolveKind(body, options.actuation);
            RobotMechanisms.Mechanism mech = UrdfPostProcessor.WireMechanism(body, link, kind, useUndo);
            ApplyDirection(mech, body, options.reverseDirection, useUndo);
            UrdfPostProcessor.RegisterMechanism(registry, mech, useUndo);
        }
        UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, root.name, registry);

        EditorUtility.SetDirty(body);
        EditorUtility.SetDirty(registry);
        if (root.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.scene);
    }

    // Nearest ancestor ArticulationBody — the parent link this joint connects to.
    private static ArticulationBody FindParentBody(ArticulationBody body) => FindParentBodyOf(body.transform);

    // Nearest ArticulationBody strictly above a transform. Used before the split link has its own
    // body, to confirm there's a rigged chassis to joint it to.
    private static ArticulationBody FindParentBodyOf(Transform t)
    {
        for (Transform p = t.parent; p != null; p = p.parent)
        {
            ArticulationBody ancestor = p.GetComponent<ArticulationBody>();
            if (ancestor != null) return ancestor;
        }
        return null;
    }

    // Reparents each extra part under the driven link so the whole axle co-rotates as one body.
    // Only plain (un-rigged) parts can join — a part that's already its own link can't be absorbed.
    // Reparenting keeps world position, so the geometry doesn't jump. Returns true if anything moved.
    private static bool MergeIntoLink(GameObject link, GameObject[] alsoMove, bool useUndo)
    {
        if (alsoMove == null) return false;

        // Validate EVERY entry before moving anything, so a bad one can't leave the hierarchy
        // half-reparented. Skip entries already inside the link (idempotent re-apply) before the
        // rigged-link check, or a nested link already part of this driven subtree would false-trip it.
        var toMove = new List<Transform>();
        foreach (GameObject part in alsoMove)
        {
            if (part == null || part == link) continue;
            if (part.transform.IsChildOf(link.transform)) continue; // already part of the link
            if (part.GetComponentInChildren<ArticulationBody>(true) != null)
                throw new InvalidOperationException(
                    $"'{part.name}' already contains a rigged link, so it can't be merged into " +
                    $"'{link.name}'. Add only plain, un-rigged parts to a driven link.");
            if (link.transform.IsChildOf(part.transform))
                throw new InvalidOperationException(
                    $"'{part.name}' is above the driven link '{link.name}' in the hierarchy. Pick the " +
                    "axle/output as the moving link, then add the parts attached to it — not the reverse.");
            toMove.Add(part.transform);
        }

        foreach (Transform t in toMove)
        {
            if (useUndo) Undo.SetTransformParent(t, link.transform, "Add or Fix Mechanism Joint");
            else t.SetParent(link.transform, worldPositionStays: true);
        }
        return toMove.Count > 0;
    }

    // The actuator kind for a driven joint: Toggle drives it as a binary pneumatic (snap between the
    // limits — a piston-driven pivot), HoldToRun as a velocity motor, Auto by the joint's DOF type.
    private static UrdfPostProcessor.MechKind ResolveKind(ArticulationBody body, Actuation actuation)
    {
        switch (actuation)
        {
            case Actuation.Toggle:    return UrdfPostProcessor.MechKind.Pneumatic;
            case Actuation.HoldToRun: return UrdfPostProcessor.MechKind.Motor;
            default:                  return UrdfPostProcessor.ClassifyMechanism(body);
        }
    }

    // Applies the reverse-direction flip to the freshly-wired actuator: motors invert their input
    // sense; pistons swap their two endpoints (and re-seat the rest target) so they start at the
    // other end.
    private static void ApplyDirection(RobotMechanisms.Mechanism mech, ArticulationBody body, bool reverse, bool useUndo)
    {
        if (mech == null) return;
        if (mech.motor != null)
        {
            if (useUndo) Undo.RecordObject(mech.motor, "Add or Fix Mechanism Joint");
            mech.motor.invert = reverse;
        }
        else if (mech.pneumatic != null && reverse)
        {
            PneumaticActuator p = mech.pneumatic;
            if (useUndo) Undo.RecordObject(p, "Add or Fix Mechanism Joint");
            (p.retractedTarget, p.extendedTarget) = (p.extendedTarget, p.retractedTarget);
            ArticulationDrive d = body.xDrive;
            d.target = p.startExtended ? p.extendedTarget : p.retractedTarget;
            body.xDrive = d;
        }
    }
}
