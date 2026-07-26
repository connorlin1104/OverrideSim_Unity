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

    // Who can see a model. Public is 0 on purpose: entries serialized before this field existed
    // deserialize to 0, so every robot already in the catalog stays visible with no migration.
    public enum Visibility
    {
        Public = 0,
        Private = 1,
    }

    [Serializable]
    public class Entry
    {
        public string id;           // stable identifier persisted in PlayerPrefs
        public string displayName;  // what the home screen shows
        // The robot prefab RobotSpawner instantiates into the field scene when this model is
        // selected. Built by the Build Robot Prefabs & Spawner tool; null entries are skipped
        // by the spawner (it falls back to the first entry that has one).
        public GameObject prefab;
        public List<MechanismInfo> mechanisms = new List<MechanismInfo>();

        [Tooltip("Public models are listed for everyone. Private models are hidden until someone " +
                 "enters this entry's owner code in Settings.")]
        public Visibility visibility = Visibility.Public;
        [Tooltip("The code that reveals this model when its owner types it in Settings > Team Code. " +
                 "Only meaningful on a Private entry. Case- and space-insensitive.")]
        public string ownerCode;
        [Tooltip("Optional label for whose robot this is (e.g. a team number). Shown in the picker.")]
        public string ownerLabel;

        // A private entry with no code can never be revealed, so it would silently disappear from the
        // picker. That's a misconfiguration rather than a policy, and VisibleModels warns about it.
        public bool IsVisibleOnThisDevice =>
            visibility == Visibility.Public || RobotOwnerSettings.HasCode(ownerCode);
    }

    public List<Entry> models = new List<Entry>();

    // PlayerPrefs key for the selected model id (public so loaders can read it directly).
    public const string SelectedModelPrefKey = "SelectedRobotModelId";

    // The models this device is allowed to see: everything public, plus any private model whose
    // owner code has been entered in Settings.
    //
    // EVERY reader must go through this rather than `models` directly. A private robot is only
    // actually hidden if the picker, the spawner, the controller-config screen and the selection
    // fallbacks all agree — otherwise a stale PlayerPrefs selection or a "first entry" fallback
    // quietly puts it back on the field.
    public IEnumerable<Entry> VisibleModels
    {
        get
        {
            if (models == null) yield break;
            foreach (Entry entry in models)
            {
                if (entry == null || string.IsNullOrEmpty(entry.id)) continue;
                if (entry.IsVisibleOnThisDevice) { yield return entry; continue; }

                if (entry.visibility == Visibility.Private && string.IsNullOrWhiteSpace(entry.ownerCode))
                {
                    Debug.LogWarning($"RobotModelCatalog: '{entry.displayName}' is Private but has no " +
                                     "owner code, so nothing can ever reveal it. Give it a code, or " +
                                     "set it back to Public.", this);
                }
            }
        }
    }

    // The currently selected model id. Reads fall back to the first VISIBLE catalog entry when the
    // pref is unset, names an id no longer in the catalog (e.g. after an entry is removed), or names
    // a private model this device hasn't unlocked — so callers always get a usable id, and never one
    // the player isn't allowed to see.
    public string SelectedModelId
    {
        get
        {
            string saved = PlayerPrefs.GetString(SelectedModelPrefKey, string.Empty);
            string firstVisible = null;
            foreach (Entry entry in VisibleModels)
            {
                if (!string.IsNullOrEmpty(saved) && entry.id == saved) return saved;
                if (firstVisible == null) firstVisible = entry.id;
            }
            return firstVisible;
        }
        set
        {
            PlayerPrefs.SetString(SelectedModelPrefKey, value);
            PlayerPrefs.Save(); // flush immediately so a crash/force-quit doesn't lose the choice
        }
    }

    // The Entry for the current selection (mirrors SelectedModelId's fallback), or null if there is
    // nothing visible to select. RobotSpawner reads this to know which prefab to place on the field.
    public Entry SelectedModel
    {
        get
        {
            string id = SelectedModelId;
            if (id == null) return null;
            foreach (Entry entry in VisibleModels)
            {
                if (entry.id == id) return entry;
            }
            return null;
        }
    }

    // First visible entry that actually has a prefab — the spawner's last resort when the selection
    // has no prefab built yet. Visible-only, so it can't surface a private robot.
    public Entry FirstVisibleWithPrefab()
    {
        foreach (Entry entry in VisibleModels)
        {
            if (entry.prefab != null) return entry;
        }
        return null;
    }
}
