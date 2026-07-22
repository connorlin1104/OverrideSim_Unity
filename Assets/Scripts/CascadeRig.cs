using System;
using System.Collections.Generic;
using UnityEngine;

// Authoring record for the cascade lift built by Tools > RoboSim > Robot > Mechanisms >
// Build Cascade Lift (roles). Lives on the robot root and travels with the prefab, so the builder
// window can re-open the lift for editing and rebuild it in place — the same job ClawRig does for a
// claw. Pure data: CascadeLift drives the stages, MotorActuator drives the hidden driver.
//
// A cascade is a stack of C-channel bars, each sliding out of the one below it. The user fills one
// block per bar:
//   parts    — everything bolted to that bar: motors, sprockets, pulleys, string mounts, brackets.
//   channels — the bar's C-channel(s), usually two grouped under one empty. This bucket is what the
//              build reads the slide DIRECTION and the maximum travel from, which is why it's
//              separate: on a slightly angled cascade each bar leans its own way, and only the
//              channel says which way that is.
// The channel bolted to the robot is deliberately NOT listed — every bar in this list slides.
[DisallowMultipleComponent]
public class CascadeRig : MonoBehaviour
{
    // One sliding bar of the cascade.
    [Serializable]
    public class Bar
    {
        [Tooltip("Everything that rides this bar EXCEPT its C-channels — motors, sprockets, pulleys, " +
                 "string mounts, brackets. All of it welds into one link with the channels.")]
        public List<GameObject> parts = new List<GameObject>();
        [Tooltip("This bar's C-channel(s) — group both under one empty. The build reads the slide " +
                 "direction and the maximum travel off the longest channel here.")]
        public List<GameObject> channels = new List<GameObject>();
        [Tooltip("Override how far this bar slides, in world units (1 unit = 0.1 m). 0 = work it out " +
                 "from the channel length.")]
        public float travelOverride;

        [Tooltip("Set at build: the link this bar's joint ended up on.")]
        public GameObject builtLink;
        [Tooltip("Set at build: the travel actually used, so the window can show it back.")]
        public float builtTravel;
    }

    // Where a part sat before the build pulled it into a stage, so Delete can put the CAD hierarchy
    // back instead of stranding every bar inside a CascadeStage empty.
    [Serializable]
    public class Moved
    {
        public GameObject part;
        public Transform originalParent;
    }

    [Tooltip("Shown on the Configure Controller screen.")]
    public string displayName = "Cascade";

    [Tooltip("The bars, bottom first. Bar 1 slides on the robot, bar 2 slides on bar 1, and so on.")]
    public List<Bar> bars = new List<Bar>();

    [Tooltip("Everything that rides the lift without being part of it — the claw arm and its claw. " +
             "These are reparented onto the top bar, so their own joints keep working while the lift " +
             "carries them.")]
    public List<GameObject> ridesAlong = new List<GameObject>();
    [Tooltip("Which bar the riders mount on. -1 = the top one (the normal case).")]
    public int attachToBarIndex = -1;

    [Header("Motion")]
    [Tooltip("OFF = every bar extends together. ON = the bars run out one at a time, in order.")]
    public bool oneAtATime;
    [Tooltip("One-at-a-time only: ON = top bar first, OFF = bottom bar first.")]
    public bool topFirst;

    [Header("Travel")]
    [Tooltip("How many holes of the channel stay overlapped at full extension — the bar can't come " +
             "all the way out or it would fall off. 2 is the usual build.")]
    public float overlapHoles = 2f;
    [Tooltip("Distance between two holes, in world units. VEX C-channel is 0.5 in = 0.0127 m, and " +
             "this project's world is 10x (1 unit = 0.1 m), so 0.127.")]
    public float holePitch = 0.127f;
    [Tooltip("Slide the bars the other way along their channels — for CAD drawn already extended, or " +
             "a cascade that reaches downward.")]
    public bool reverseDirection;

    [Header("Feel")]
    [Tooltip("Seconds to raise the lift fully while holding the button.")]
    public float raiseSeconds = 2f;
    public float stageStiffness = 20000f;
    public float stageDamping = 500f;
    [Tooltip("Force limit per bar. Gravity here is 10x, so a claw on a 3-stage lift sags on a small one.")]
    public float stageForceLimit = 5000f;
    public bool autoAssignButtons = true;

    [Header("Set at build")]
    [Tooltip("The hidden revolute joint the buttons drive.")]
    public GameObject builtDriver;
    [Tooltip("Every part the build reparented, with where it came from, so Delete can undo it.")]
    public List<Moved> moved = new List<Moved>();
}
