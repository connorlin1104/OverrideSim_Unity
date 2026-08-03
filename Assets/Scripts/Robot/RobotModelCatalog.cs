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
[CreateAssetMenu(menuName = "RoboSim/Robot Model Catalog", fileName = "RobotModelCatalog")]
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

    // The second way an entry can name a robot: an AssetBundle to load it out of, instead of a
    // direct reference to a prefab that had to exist when the app was built.
    //
    // This is what makes a robot deliverable at all. With only a direct reference, every accepted
    // upload is baked into the binary forever for every player — which is what makes a private robot
    // only *hidden* rather than absent, and what puts a hard ceiling on how many robots the game can
    // ever have (measured: ~226 MB of mesh data each, before decimation).
    //
    // WHY sourceGuid IS A STRING AND NOT A GameObject. A serialized reference to an asset is what
    // PULLS THAT ASSET INTO THE BUILD — that is the entire mechanism behind the problem above. So
    // the build tool remembers which prefab to bundle by its GUID, in text, and resolves it through
    // AssetDatabase when it runs. A convenient `public GameObject sourcePrefab;` here would quietly
    // undo the whole point of this class while looking tidier.
    //
    // THE VERSION FIELDS ARE NOT BOOKKEEPING. A bundle serializes the SCRIPTS on the robot prefab,
    // not just its meshes — so changing a serialized field on RobotMotorController silently
    // invalidates every bundle built before the change. `scriptVersion` records which layout this
    // bundle was built against, the app refuses anything that isn't its own, and the refusal is a
    // message rather than a robot that spawns with its tuning quietly zeroed. See RobotBundleFormat.
    [Serializable]
    public class BundleRef
    {
        [Tooltip("Bundle file name, without extension. Empty means this robot isn't delivered as a " +
                 "bundle.")]
        public string id;

        [Tooltip("Which build of that bundle. Part of the path, so an old app keeps asking for the " +
                 "build it was shipped against and never picks up an incompatible newer one.")]
        public string version;

        [Tooltip("The RobotBundleFormat.Version this bundle was built against. A mismatch means the " +
                 "robot scripts changed shape since, and the bundle cannot be trusted to load.")]
        public int scriptVersion;

        [Tooltip("Set when the bundle is served from Storage rather than shipped in StreamingAssets.")]
        public bool remote;

        [Tooltip("Asset GUID of the prefab this bundle is built from. Editor-only bookkeeping.")]
        public string sourceGuid;

        public bool IsSet => !string.IsNullOrWhiteSpace(id);
    }

    [Serializable]
    public class Entry
    {
        public string id;           // stable identifier persisted in PlayerPrefs
        public string displayName;  // what the home screen shows
        // The robot prefab RobotSpawner instantiates into the field scene when this model is
        // selected. Built by the Build Robot Prefabs & Spawner tool; null entries are skipped
        // by the spawner (it falls back to the first entry that has one).
        //
        // A direct reference means the robot is COMPILED INTO THE APP: its geometry ships to every
        // device that installs the build, whether or not that player can see it in the picker. That
        // is the right trade for the handful of robots shipped with the game, and the wrong one for
        // every robot a player sends in — see `bundle` below, which is the other way to fill this in.
        public GameObject prefab;

        [Tooltip("Where to fetch this robot from when it isn't compiled into the app. Leave empty " +
                 "for a built-in robot; the direct prefab reference above wins if both are set.")]
        public BundleRef bundle = new BundleRef();

        public List<MechanismInfo> mechanisms = new List<MechanismInfo>();

        [Tooltip("Public models are listed for everyone. Private models are hidden until someone " +
                 "enters this entry's owner code in Settings.")]
        public Visibility visibility = Visibility.Public;
        [Tooltip("The code(s) that reveal this model when its owner types one in Settings > Team Code. " +
                 "Only meaningful on a Private entry. Separate several with commas — a robot can carry " +
                 "its own one-off code AND its team's code, and holding either one reveals it. " +
                 "Case- and space-insensitive.")]
        public string ownerCode;
        [Tooltip("Optional label for whose robot this is (e.g. a team number). Shown in the picker.")]
        public string ownerLabel;

        [Tooltip("The button layout a fresh install starts with for this robot. Bind the controller " +
                 "how you want it in Configure Controller, then publish it from Tools > RoboSim > " +
                 "Robot > Model Catalog > Make Current Bindings the Default. Empty means the robot " +
                 "ships unbound, which is what every robot did before this existed — the auto-assign " +
                 "that runs when a mechanism is built writes into the EDITOR's PlayerPrefs, and those " +
                 "never reach a build.")]
        public ButtonMap defaultButtonMap = new ButtonMap();

        public bool HasDefaultButtonMap =>
            defaultButtonMap != null && defaultButtonMap.assignments != null
            && defaultButtonMap.assignments.Count > 0;

        // Every code that reveals this entry. Giving several entries one code in common is how a team
        // shares its robots: "654V-TEAM" on five entries means one code opens all five, with no
        // accounts and nothing to verify.
        public List<string> OwnerCodes => RobotOwnerSettings.SplitCodes(ownerCode);

        // A private entry with no code can never be revealed, so it would silently disappear from the
        // picker. That's a misconfiguration rather than a policy, and VisibleModels warns about it.
        public bool IsVisibleOnThisDevice =>
            visibility == Visibility.Public || RobotOwnerSettings.HasAnyCode(ownerCode);
    }

    public List<Entry> models = new List<Entry>();

    // Robots learned about at runtime from the published index (RobotCatalogSync) — ones this build
    // was never shipped with at all.
    //
    // Deliberately NOT part of `models`, and deliberately [NonSerialized]. Adding them to the
    // serialized list would work at runtime and then quietly write itself into the catalog ASSET the
    // next time the editor saved, so a Play-mode session that happened to be online would commit
    // whatever was published that day into the project. This list is rebuilt from the network on
    // every launch, which is also what makes an unpublished robot disappear again.
    [System.NonSerialized] private List<Entry> synced;

    public IEnumerable<Entry> AllModels
    {
        get
        {
            if (models != null)
            {
                foreach (Entry entry in models) yield return entry;
            }
            if (synced == null) yield break;
            foreach (Entry entry in synced) yield return entry;
        }
    }

    // Adds a robot discovered at runtime. Ignores one whose id is already known, so a robot that is
    // both shipped and published stays the shipped copy — the local one is guaranteed loadable and
    // needs no network.
    public bool AddSynced(Entry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.id)) return false;
        foreach (Entry existing in AllModels)
        {
            if (existing != null && existing.id == entry.id) return false;
        }

        synced ??= new List<Entry>();
        synced.Add(entry);
        return true;
    }

    public void ClearSynced() => synced?.Clear();

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
            foreach (Entry entry in AllModels)
            {
                if (entry == null || string.IsNullOrEmpty(entry.id)) continue;
                if (entry.IsVisibleOnThisDevice) { yield return entry; continue; }

                if (entry.visibility == Visibility.Private && entry.OwnerCodes.Count == 0)
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
    //
    // Deliberately NOT bundle-aware: this is the fallback used when the thing the player asked for
    // could not be put on the field, and a fallback that has to go to the network to find out
    // whether it works is not a fallback. FirstVisibleSpawnable is the one to ask when you want
    // "anything at all"; this is the one to ask when you want "anything, right now, guaranteed".
    public Entry FirstVisibleWithPrefab()
    {
        foreach (Entry entry in VisibleModels)
        {
            if (entry.prefab != null) return entry;
        }
        return null;
    }

    // First visible entry that names a robot at all, by either route. What the picker should offer
    // and what the spawner should try before giving up.
    public Entry FirstVisibleSpawnable()
    {
        foreach (Entry entry in VisibleModels)
        {
            if (entry.prefab != null || (entry.bundle != null && entry.bundle.IsSet)) return entry;
        }
        return null;
    }
}
