using System;
using System.Collections.Generic;
using UnityEngine;

// Authoring record for one claw built by Tools > RoboSim > Robot > Mechanisms > Build Claw (roles).
// Lives on the claw's driven link (the flip link, or the first clamp half when the claw doesn't flip)
// and travels with the robot prefab, so the builder window can list every claw on the robot and
// re-open one for editing — rebuild updates it in place. Same idea as PneumaticRig.
//
// Pure data. PneumaticActuator drives the two real joints, JointCoupler mirrors the extra clamp
// halves, PneumaticSlideFollower poses the cosmetic cylinders and ClawGrab does the holding.
//
// A claw has two independent pneumatic functions, each its own button mechanism:
//   FLIP  — the whole assembly rotates (typically 180°) so a stack picked up upside down lands the
//           right way up. Everything below is a child of this link, so it all flips together.
//   CLAMP — the jaws close. Usually two mirrored halves; extra halves follow the first through a
//           JointCoupler in Position mode rather than getting a button of their own.
// Either half can be left empty: clamp-only claws are common, and so are plain flip-out trays.
public class ClawRig : MonoBehaviour
{
    // Which way a hinge turns.
    //
    // Why not the part's own X/Y/Z: a joint axis is expressed in the LINK's local frame, and an
    // imported CAD part's local frame is arbitrary — "Y" is only "up" by luck. So the options here
    // are resolved either against the ROBOT (so they mean the same thing on every model) or against
    // the SCENE, whose axes are the coloured arrows on the move gizmo. The scene options exist
    // because they're the only ones you can check by looking: "it should turn about the blue one" is
    // a thing you can see, whereas which way a given claw is mounted is not something this tool can
    // reliably infer — Auto has guessed wrong before.
    //
    // Explicit values: these are serialized as integers into every built ClawRig, so members may be
    // APPENDED but never renumbered or reordered.
    public enum HingeAxis
    {
        [Tooltip("Best guess: a jaw closes inward, a flip rolls over. Check it against the preview.")]
        Auto = 0,
        [InspectorName("Robot up/down pins (jaws close inward)")]
        [Tooltip("Vertical pins: the jaws swing inward and meet in front. Most clamps.")]
        JawsCloseInward = 1,
        [InspectorName("Robot left/right pins (pitches over)")]
        [Tooltip("Left-right pins: the claw pitches over like a wrist.")]
        TurnsOverForward = 2,
        [InspectorName("Robot front/back pins (rolls over)")]
        [Tooltip("Front-back pins: the claw rolls over sideways.")]
        RollsSideways = 3,
        [InspectorName("Pivot marker's own X")]
        [Tooltip("Rotate the marker in the Scene view to aim it. For geometry none of the above fits.")]
        PivotMarkerX = 4,
        [InspectorName("Custom (type a vector)")]
        [Tooltip("Type the axis by hand, in the part's local space.")]
        Custom = 5,
        [InspectorName("Scene X — the RED arrow")]
        [Tooltip("Turns about the scene's X axis, whichever way the robot happens to be facing.")]
        WorldX = 6,
        [InspectorName("Scene Y — the GREEN arrow")]
        [Tooltip("Turns about the scene's Y axis (straight up).")]
        WorldY = 7,
        [InspectorName("Scene Z — the BLUE arrow")]
        [Tooltip("Turns about the scene's Z axis, whichever way the robot happens to be facing.")]
        WorldZ = 8,
        [InspectorName("Match the arm (recommended)")]
        [Tooltip("Level-keeper only: turn about the SAME line the rotating arm turns about, so the " +
                 "counter-rotation exactly cancels the arm's. Read straight off the arm's own joint.")]
        MatchArm = 9,
        [InspectorName("The Pivot axle's own shaft (level-keeper)")]
        [Tooltip("Level-keeper only: turn about the shaft dropped in the 'Pivot axle' slot — its long " +
                 "axis, read straight off the mesh. Use only when that axle isn't mounted parallel to the " +
                 "arm; otherwise Match the arm is the exact-cancel choice.")]
        FromAxle = 10,
    }

    // Which way round the CAD was drawn. A joint's rest pose is wherever the modeller left the part,
    // and the travel goes AWAY from it — so a claw modelled shut opens when the piston fires, and one
    // modelled open shuts. Everything that depends on "are the jaws closed right now" (the grab, the
    // start state) flips with it, which is why this is one setting rather than several toggles the
    // user has to keep consistent by hand.
    public enum JawRest
    {
        [Tooltip("The CAD has the jaws apart. Firing the piston closes them.")]
        ModelledOpen,
        [Tooltip("The CAD has the jaws shut. Firing the piston opens them.")]
        ModelledClosed,
    }

    // One batch of parts that swings together as a jaw. Most claws use two — the left and right half.
    [Serializable]
    public class ClampSection
    {
        [Tooltip("The parts of this jaw half. The first is the driven link; the rest are welded into it.")]
        public List<GameObject> parts = new List<GameObject>();
        [Tooltip("How far this half swings when the cylinder fires, in degrees.")]
        public float closeAngleDeg = 35f;
        [Tooltip("The POINT this half hinges about — drag it onto the edge of the plastic. Which way it " +
                 "turns is the axis setting below. Left empty, the build creates one to drag.")]
        public Transform pivot;
        public HingeAxis axisPreset = HingeAxis.Auto;
        public Vector3 customAxis = Vector3.right;
        [Tooltip("Swing the opposite way to the first half — what makes two jaws close on each other " +
                 "instead of both sweeping the same direction.")]
        public bool mirror;
        [Tooltip("Set at build: the link this half's joint ended up on, so a rebuild can find and clean it.")]
        public GameObject builtLink;
    }

    [Tooltip("Shown on the Configure Controller screen, suffixed with Flip / Clamp.")]
    public string displayName = "Claw";

    // A claw hung off a rotating arm (a turret that swings it front-to-back) tumbles with the arm: an
    // articulation parent turns its whole subtree, so a 180 swing lands the claw upside down. The fix
    // is a link BETWEEN the arm and the claw that counter-rotates the arm exactly — arm +θ, this -θ,
    // claw net 0 — so the claw rides the arc but keeps one orientation. It's a real revolute joint
    // slaved to the arm through a JointCoupler in Position mode at ratio -1 (the DR4B tray's trick, and
    // the mirrored jaw's), NOT an actuator: nothing on a button, it just tracks the arm. Optional and
    // independent of flip/clamp; the flip link (or the jaws, on a flipless claw) become its children so
    // the whole claw rides it.
    [Header("Rides a rotating arm (stays level)")]
    [Tooltip("The claw's MOUNT — the bracket the claw hangs off at the end of the arm. It travels with " +
             "the arm but counter-rotates so the claw stays level. Empty = the claw is bolted straight " +
             "to the arm and turns with it. The flip/jaws are reparented under this, so don't list them.")]
    public List<GameObject> levelParts = new List<GameObject>();
    [Tooltip("The rotating arm this mount rides — the part you turned into a spinning joint with Build " +
             "Chain. Build that FIRST; the mount reads its axis and slaves to its motion.")]
    public GameObject armDriver;
    [Tooltip("The AXLE the claw pivots on at the end of the arm — the shaft the mount turns about. Its " +
             "centre becomes the rotation point, so the claw turns THERE and stays put at the arm's end " +
             "instead of swinging on a huge arc about the robot's middle (the reported bug). Empty = the " +
             "pivot falls back to the claw's own centre, seeded from the jaws.")]
    public GameObject levelAxle;
    [Tooltip("The POINT the mount counter-rotates about — usually where it bolts to the end of the arm. " +
             "Only the axis (below) has to match the arm; the point can sit anywhere along that line. " +
             "Left empty, the Pivot axle (or the claw's centre) seeds it.")]
    public Transform levelPivot;
    public HingeAxis levelAxisPreset = HingeAxis.MatchArm;
    public Vector3 levelCustomAxis = Vector3.right;
    [Tooltip("Mount angle : arm angle. -1 keeps the claw dead level (equal and opposite). Between -1 " +
             "and 0 lets it lean part way with the arm; positive would DOUBLE the tumble — don't.")]
    public float levelRatio = -1f;
    [Tooltip("How far each way the mount is allowed to counter-rotate, in degrees. Must cover the arm's " +
             "full swing or the claw stops staying level past that point. 180 suits a front-to-back arm.")]
    public float levelSweepDeg = 180f;
    public float levelStiffness = 20000f;
    public float levelDamping = 500f;
    [Tooltip("Once the arm swings past the midpoint of its travel, snap the claw an extra 180 so it faces " +
             "the OTHER way on the back half of the arc — on top of the leveling. Off = the claw keeps " +
             "one orientation the whole way.")]
    public bool levelFlipPastMidpoint;
    [Tooltip("How far the midpoint flip turns the claw, in degrees.")]
    public float levelFlipDegrees = 180f;
    [Tooltip("Where in the arm's swing the flip fires, as a fraction from rest to the far end. 0.5 = halfway.")]
    [Range(0.05f, 0.95f)] public float levelFlipFraction = 0.5f;
    [Tooltip("Seconds the flip takes once it fires — the 'quick' part.")]
    public float levelFlipSeconds = 0.3f;
    [Tooltip("The part that YAWS the claw to face the other way at the midpoint — a wrist between the " +
             "mount and the claw. With this set, the midpoint flip turns about the robot's UP axis (a " +
             "yaw: claw stays level, just faces the opposite way) instead of tipping the whole claw over " +
             "on the leveling joint. Empty = the flip tips the claw over (a pitch) on the mount.")]
    public List<GameObject> yawWristParts = new List<GameObject>();
    [Tooltip("The POINT the wrist yaws about — usually the middle of the claw, so it spins in place. " +
             "Empty = the build drops a marker on the claw's centre.")]
    public Transform yawWristPivot;
    [Tooltip("Set at build: the link the counter-rotation joint ended up on.")]
    public GameObject builtLevelLink;
    [Tooltip("Set at build: the link the yaw-wrist joint ended up on.")]
    public GameObject builtYawWristLink;

    [Header("Flip")]
    [Tooltip("What the claw hangs off — its mount, or the group the claw sits in. The jaws are NOT " +
             "listed here: they're reparented under this link at build. Empty = this claw doesn't flip.")]
    public List<GameObject> flippingParts = new List<GameObject>();
    [Tooltip("How far the claw rotates when the flip cylinder fires. 180 turns a stack upside down.")]
    public float flipAngleDeg = 180f;
    [Tooltip("The POINT the claw pivots about when it flips. Which way it turns is the axis setting below.")]
    public Transform flipPivot;
    public HingeAxis flipAxisPreset = HingeAxis.Auto;
    public Vector3 flipCustomAxis = Vector3.right;
    [Tooltip("Seconds the flip takes end to end. A pneumatic snaps, and half a turn that snaps is over " +
             "before the eye catches it — on a claw that's roughly symmetric it reads as nothing " +
             "happening at all. 0 restores the instant snap.")]
    public float flipTravelSeconds = 0.35f;
    [Tooltip("Start the match already flipped.")]
    public bool flipStartExtended;
    [Tooltip("Position spring gain of the flip joint. Lower it if a 180° flip slams too hard.")]
    public float flipStiffness = 20000f;
    [Tooltip("Velocity damping of the flip joint. Raise it to slow the flip down.")]
    public float flipDamping = 500f;
    [Tooltip("Set at build: the link the flip joint ended up on.")]
    public GameObject flipLink;

    [Header("Flip cylinder (cosmetic)")]
    public GameObject flipCylinderBody;
    public GameObject flipCylinderRod;
    [Tooltip("Cylinder stroke in millimetres — the real VEX classes are 20 / 50 / 90.")]
    public float flipStrokeMm = 90f;
    [Tooltip("How much of the travel the BODY takes instead of the rod. 0 = barrel bolted down, " +
             "0.5 = balanced (the cylinder's midpoint holds still), 1 = the barrel does all the moving.")]
    [Range(0f, 1f)] public float flipRecoil = 0.5f;
    [Tooltip("Flip which way the cylinder slides, if it extends when it should retract.")]
    public bool flipCylinderReverse;

    [Header("Clamp")]
    public List<ClampSection> clampSections = new List<ClampSection>();
    [Tooltip("Whether the CAD draws the jaws open or shut. Sets which end of the travel counts as " +
             "closed, which is what the grab and the start state key off.")]
    public JawRest clampModelled = JawRest.ModelledOpen;
    [Tooltip("Degrees to shift the jaws' RESTING pose by, on the same axis and in the same sense as " +
             "the swing angle. A CAD drawn a touch too open closes tighter at -10. Mirrored halves " +
             "get the mirrored trim, so both jaws move in together.")]
    public float clampTrimDeg;
    [Tooltip("Begin the match with the jaws shut (whichever end of the travel that is).")]
    public bool clampStartClosed;
    public float clampStiffness = 20000f;
    public float clampDamping = 500f;

    [Header("Clamp cylinder (cosmetic)")]
    public GameObject clampCylinderBody;
    public GameObject clampCylinderRod;
    public float clampStrokeMm = 50f;
    [Range(0f, 1f)] public float clampRecoil = 0.5f;
    public bool clampCylinderReverse;

    [Header("Grab")]
    [Tooltip("Let the closed jaws actually hold a game piece. Off = the jaws are solid but can't retain " +
             "anything (pieces are minimum-friction, so squeezing alone never grips).")]
    public bool enableGrab = true;
    [Tooltip("Grab when the cylinder RETRACTS instead of extends — for jaws that are sprung open.")]
    public bool grabWhenRetracted;
    [Tooltip("A held piece stops colliding with everything until it's dropped, so a grab that caught " +
             "it at an awkward angle can't wedge it through the CAD or shove the robot. Off = it only " +
             "ignores the claw itself and stays solid to the field.")]
    public bool grabPassThrough = true;
    [Tooltip("Stand each grabbed piece along the hold point's up, measured per piece from its mesh — " +
             "so a pin lying flat and one standing upright are carried the same way round.")]
    public bool grabAutoUpright = true;
    [Tooltip("Set at build: the trigger zone between the jaws.")]
    public GameObject clawMouth;
    [Tooltip("Set at build: where a grabbed piece is held. A child of the flip link, so it rides the flip.")]
    public Transform holdPoint;

    public bool autoAssignButtons = true;
}
