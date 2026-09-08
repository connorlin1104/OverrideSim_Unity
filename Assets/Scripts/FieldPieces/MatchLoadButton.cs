using TMPro;
using UnityEngine;
using UnityEngine.UI;

// The field scene's "Match Load" button, shown only when Automatic Matchloading is OFF in
// Settings — which is the default. Pressing it spawns a match load from the loader whose tape the
// robot is currently on (the tape trigger still tracks which loader the robot is at in manual
// mode); the manual spawn drops from extra height so it can fall into the robot.
//
// The setting is read once in Start: the field scene is reloaded fresh on every entry, so a
// one-shot read is always current (same rationale as ControlsAppearance). The onClick listener
// is added at runtime, so the scene builder never stacks persistent listeners.
// Created by Tools > RoboSim > Scenes > Build Drive Controls.
[RequireComponent(typeof(Button))]
public class MatchLoadButton : MonoBehaviour
{
    // The grey the button wears while no loader can feed it. It is painted here rather than left
    // to the stock Button ColorTint because every on-screen control has its transition set to
    // None (PressFeedback drives the colour instead), so `interactable = false` on its own changed
    // NOTHING on screen: a dead button looked exactly like a live one, and the only way to find
    // out was to press it.
    private static readonly Color UnavailableColor = new Color(0.30f, 0.32f, 0.36f);

    // How far the label fades with the plate. The text has to dim too — on a phone the plate is a
    // few millimetres of colour, and a bright white label on it still reads as "press me".
    private const float UnavailableLabelAlpha = 0.4f;

    private Button button;
    private PressFeedback feedback;
    private Graphic plate;
    private TMP_Text label;

    // The authored look IS the "ready" look, read off the scene rather than named here, so
    // restyling the controls in Build Drive Controls reaches this button too.
    private Color readyColor = Color.white;
    private Color readyLabelColor = Color.white;

    private MatchLoaderController[] loaders;

    void Awake()
    {
        button = GetComponent<Button>();
        button.onClick.AddListener(OnPressed);

        feedback = GetComponent<PressFeedback>();
        plate = button.targetGraphic != null ? button.targetGraphic : GetComponent<Graphic>();
        label = GetComponentInChildren<TMP_Text>(true);
        if (plate != null) readyColor = plate.color;
        if (label != null) readyLabelColor = label.color;
    }

    void Start()
    {
        if (MatchLoadSettings.Automatic)
        {
            gameObject.SetActive(false); // automatic mode: the loaders spawn on their own
            return;
        }
        loaders = FindObjectsByType<MatchLoaderController>(FindObjectsInactive.Exclude);
        if (loaders.Length == 0)
            Debug.LogWarning("MatchLoadButton: no MatchLoaderController in the scene — the button will stay disabled.", this);

        SetReady(false); // start grey rather than flashing blue for the first frame
    }

    void Update()
    {
        // Live-enable only while some loader could actually spawn (robot on its tape, previous
        // piece carried away) so a dead press is visibly impossible. Four loaders — a trivial poll.
        bool any = false;
        if (loaders != null)
            foreach (MatchLoaderController loader in loaders)
                if (loader != null && loader.CanManualSpawn) { any = true; break; }
        SetReady(any);
    }

    // Blue and pressable while a loader is ready, grey and inert otherwise. Written every frame
    // rather than only on the edge because PressFeedback lerps the plate back toward its base
    // colour continuously — handing it a new base is what makes the change stick, and it buys the
    // fade between the two states for free.
    private void SetReady(bool ready)
    {
        button.interactable = ready;

        Color plateColor = ready ? readyColor : UnavailableColor;
        if (feedback != null) feedback.BaseColor = plateColor;
        else if (plate != null) plate.color = plateColor;

        if (label != null)
        {
            Color labelColor = readyLabelColor;
            if (!ready) labelColor.a *= UnavailableLabelAlpha;
            label.color = labelColor;
        }
    }

    private void OnPressed()
    {
        if (loaders == null) return;
        foreach (MatchLoaderController loader in loaders)
            if (loader != null && loader.RequestManualSpawn()) return; // first ready loader wins
    }
}
