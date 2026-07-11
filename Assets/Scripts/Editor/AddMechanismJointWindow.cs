using System;
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
// Scope: links that ALREADY exist as ArticulationBodies (every URDF link is one, including parts
// that imported as fixed). Splitting a brand-new moving body out of an existing rigid link (mesh
// reparenting) is out of scope — remodel it in Fusion and re-import, or rig it like the drivetrain.
//
// Usage: Tools > RoboSim > Robot > Advanced > Add or Fix Mechanism Joint.
public class AddMechanismJointWindow : EditorWindow
{
    private const string Title = "Add or Fix Mechanism Joint";
    private const float MetersPerUnit = 0.1f; // this project's world: 1 scaled unit = 0.1 m

    private enum AxisPreset { X, Y, Z, Custom }

    [SerializeField] private GameObject childLink;
    [SerializeField] private AddMechanismJoint.JointType jointType = AddMechanismJoint.JointType.Revolute;
    [SerializeField] private AxisPreset axisPreset = AxisPreset.Y;
    [SerializeField] private Vector3 customAxis = Vector3.up;
    [SerializeField] private Vector3 anchor = Vector3.zero;
    [SerializeField] private float lowerLimit = -90f;
    [SerializeField] private float upperLimit = 90f;

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
            EditorGUILayout.HelpBox("Select the link GameObject that should move.", MessageType.Warning);
            return;
        }
        if (childLink.GetComponent<ArticulationBody>() == null)
        {
            EditorGUILayout.HelpBox(
                $"'{childLink.name}' has no ArticulationBody. Pick a URDF link (every imported link " +
                "has one). Creating a brand-new moving body from scratch is not supported here.",
                MessageType.Error);
            return;
        }
        RobotMechanisms registry = childLink.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
        {
            EditorGUILayout.HelpBox(
                "This link is not under a set-up robot (no RobotMechanisms on the root). Run " +
                "Tools > RoboSim > Robot > Set Up Imported Robot first.", MessageType.Error);
            return;
        }

        jointType = (AddMechanismJoint.JointType)EditorGUILayout.EnumPopup("Joint Type", jointType);

        bool showAxis = jointType != AddMechanismJoint.JointType.Fixed;
        bool showLimits = jointType == AddMechanismJoint.JointType.Revolute ||
                          jointType == AddMechanismJoint.JointType.Prismatic;

        if (showAxis)
        {
            axisPreset = (AxisPreset)EditorGUILayout.EnumPopup("Axis (link-local)", axisPreset);
            if (axisPreset == AxisPreset.Custom)
                customAxis = EditorGUILayout.Vector3Field("Custom Axis", customAxis);
            anchor = EditorGUILayout.Vector3Field(new GUIContent("Anchor (link-local)",
                "Pivot/slide origin in the link's local space. 0 = the link origin (the usual case)."),
                anchor);
        }

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
            Vector3 axis = axisPreset switch
            {
                AxisPreset.X => Vector3.right,
                AxisPreset.Y => Vector3.up,
                AxisPreset.Z => Vector3.forward,
                _ => customAxis,
            };

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(Title);
            int group = Undo.GetCurrentGroup();
            AddMechanismJoint.Apply(childLink, jointType, axis, anchor, lowerLimit, upperLimit, useUndo: true);
            Undo.CollapseUndoOperations(group);

            EditorUtility.DisplayDialog(Title,
                $"'{childLink.name}' is now a {jointType} joint" +
                (jointType == AddMechanismJoint.JointType.Fixed ? " (mechanism removed)." : " and is wired as a mechanism.") +
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

    // Configures the link's ArticulationBody as the requested joint (type -> DOF locks -> anchors
    // -> limits, matching the URDF importer's AdjustMovement and the post-processor's anchor
    // re-derivation), then wires (or removes) the mechanism and refreshes the catalog. Throws on
    // any precondition failure. useUndo=false for batch/headless callers.
    public static void Apply(GameObject link, JointType type, Vector3 axis, Vector3 anchor,
        float lowerLimit, float upperLimit, bool useUndo)
    {
        if (link == null) throw new ArgumentNullException(nameof(link));
        ArticulationBody body = link.GetComponent<ArticulationBody>();
        if (body == null)
            throw new InvalidOperationException($"'{link.name}' has no ArticulationBody — pick a URDF link.");

        RobotMechanisms registry = link.GetComponentInParent<RobotMechanisms>();
        if (registry == null)
            throw new InvalidOperationException(
                $"'{link.name}' is not under a set-up robot (no RobotMechanisms). Run " +
                "Set Up Imported Robot first.");
        GameObject root = registry.gameObject;

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
            UrdfPostProcessor.MechKind kind = UrdfPostProcessor.ClassifyMechanism(body);
            RobotMechanisms.Mechanism mech = UrdfPostProcessor.WireMechanism(body, link, kind, useUndo);
            UrdfPostProcessor.RegisterMechanism(registry, mech, useUndo);
        }
        UrdfPostProcessor.RefreshCatalogMechanisms(registry.robotId, root.name, registry);

        EditorUtility.SetDirty(body);
        EditorUtility.SetDirty(registry);
        if (root.scene.IsValid()) EditorSceneManager.MarkSceneDirty(root.scene);
    }

    // Nearest ancestor ArticulationBody — the parent link this joint connects to.
    private static ArticulationBody FindParentBody(ArticulationBody body)
    {
        for (Transform t = body.transform.parent; t != null; t = t.parent)
        {
            ArticulationBody ancestor = t.GetComponent<ArticulationBody>();
            if (ancestor != null) return ancestor;
        }
        return null;
    }
}
