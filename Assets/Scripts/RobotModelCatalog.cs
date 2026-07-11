using System;
using System.Collections.Generic;
using UnityEngine;

// Catalog of the robot models the player can choose from on the home screen.
//
// Each entry pairs a stable string id (safe to persist across renames of the display text)
// with the human-readable name shown in the UI. The current selection is stored in
// PlayerPrefs — not on the asset — so picking a model never dirties the project and the
// choice survives app restarts per device.
//
// Usage: create via Assets > Create > VEX > Robot Model Catalog (the Build Home Scene tool
// creates Assets/Settings/RobotModelCatalog.asset automatically), then assign it to the
// HomeScreenController in the home scene.
[CreateAssetMenu(menuName = "VEX/Robot Model Catalog", fileName = "RobotModelCatalog")]
public class RobotModelCatalog : ScriptableObject
{
    // Mirror of a RobotMechanisms.Mechanism (id/displayName/type only, no component refs) so
    // the home-screen controller-config UI can list a robot's mechanisms without loading the
    // field scene. Written by the URDF post-processor alongside the scene-side registry.
    [Serializable]
    public class MechanismInfo
    {
        public string id;
        public string displayName;
        public string type; // RobotMechanisms.TypeMotor or RobotMechanisms.TypePneumatic
    }

    [Serializable]
    public class Entry
    {
        public string id;           // stable identifier persisted in PlayerPrefs
        public string displayName;  // what the home screen shows
        public List<MechanismInfo> mechanisms = new List<MechanismInfo>();
    }

    public List<Entry> models = new List<Entry>();

    // PlayerPrefs key for the selected model id (public so loaders can read it directly).
    public const string SelectedModelPrefKey = "SelectedRobotModelId";

    // The currently selected model id. Reads fall back to the first catalog entry when the
    // pref is unset or names an id no longer in the catalog (e.g. after an entry is removed),
    // so callers always get a usable id as long as the catalog is non-empty.
    public string SelectedModelId
    {
        get
        {
            string saved = PlayerPrefs.GetString(SelectedModelPrefKey, string.Empty);
            if (!string.IsNullOrEmpty(saved) && models != null)
            {
                foreach (Entry entry in models)
                {
                    if (entry != null && entry.id == saved) return saved;
                }
            }
            return (models != null && models.Count > 0 && models[0] != null) ? models[0].id : null;
        }
        set
        {
            PlayerPrefs.SetString(SelectedModelPrefKey, value);
            PlayerPrefs.Save(); // flush immediately so a crash/force-quit doesn't lose the choice
        }
    }
}
