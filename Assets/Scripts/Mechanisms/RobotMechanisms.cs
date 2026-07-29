using System;
using System.Collections.Generic;
using UnityEngine;

// Registry of a robot's controllable mechanisms (everything except the drivetrain, which
// stays hardwired to the joysticks). Lives on the robot root; filled by the URDF
// post-processor from the robot's non-wheel joints: revolute/continuous -> MotorActuator,
// prismatic -> PneumaticActuator.
//
// The same mechanism list (id/displayName/type only) is mirrored onto the robot's
// RobotModelCatalog entry so the home-screen controller-config UI can offer the mechanisms
// without loading the field scene; ids are the join key between that UI's saved ButtonMap
// and the live actuator components here (resolved by ButtonRouter at scene load).
public class RobotMechanisms : MonoBehaviour
{
    public const string TypeMotor = "motor";
    public const string TypePneumatic = "pneumatic";

    [Serializable]
    public class Mechanism
    {
        public string id;             // stable slug of the URDF link name (catalog Slugify rules)
        public string displayName;    // what the config UI shows, e.g. "Arm"
        public string type;           // TypeMotor or TypePneumatic
        public MotorActuator motor;         // set when type == TypeMotor
        public PneumaticActuator pneumatic; // set when type == TypePneumatic
    }

    [Tooltip("Catalog slug of this robot; keys the persisted ButtonMap_<robotId> mapping.")]
    public string robotId;

    public List<Mechanism> mechanisms = new List<Mechanism>();

    public Mechanism Find(string id)
    {
        if (string.IsNullOrEmpty(id) || mechanisms == null) return null;
        foreach (Mechanism mechanism in mechanisms)
        {
            if (mechanism != null && mechanism.id == id) return mechanism;
        }
        return null;
    }
}
