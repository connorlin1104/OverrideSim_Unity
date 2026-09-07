using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

// Does every drive wheel stay on the ground, and does it carry anything when it is there?
//
// Asked twice now. The first time ("only the 4 corner wheels spin continuously") it was answered on
// a bare floor, at rest, in a straight line — and the answer was "they are all touching", which was
// true and did not settle it. Connor reopened it on 2026-09-06 against the driven robot:
//
//   "did you ever check whether all 6 wheels are always touching the ground? ... at the begining
//    they all spin fine and all touch im guessing, but maybe something happens during the match and
//    some wheels get lifted slightly, it would explain why the drive train feels so weak because 2
//    of the wheels aren't even powering the movement."
//
// So this probe measures the driven robot, on the FIELD, across a match-like sequence, and reports
// the two halves separately, because they are not the same question:
//
//   TOUCHING is contact, not a gap: a wheel is on the ground for a step if it reported a contact
//   pair with something that is not part of the robot. No floor plane is assumed, which matters on
//   a field where a wheel may be riding a tile seam, a cup or a wall.
//
//   CARRYING is the vertical impulse that contact delivered, in force units. A wheel touching the
//   floor with 1% of its share of the weight powers nothing: traction is mu*N, so N is what makes a
//   wheel a drive wheel. "Touching" and "driving" can differ, and the earlier answer only had the
//   first one.
//
// And the budget line, which is the actual test of Connor's hypothesis: what fraction of the
// robot's weight reaches the ground THROUGH THE DRIVE WHEELS. Weight that goes down a passive arm,
// an intake or the chassis is weight the drivetrain cannot push with, and that is a drivetrain made
// weak by exactly the mechanism he describes.
//
// Nothing asserts here on purpose: this reports. Assertions that come out of it live in
// DrivetrainRigValidation / WheelTyreValidation.
//
// WHAT THIS PROBE ESTABLISHED, 2026-09-07 — read before trying to "fix" a dead wheel.
// Connor: "it does seem to self fix to 2 wheels each side working, but the first minute its 2 from
// one side and later on, the right side one of them would fix while the left side one would break."
// True, reproduced, and NOT the field: on a BARE FLAT FLOOR with nothing else touching the robot and
// closure at exactly 100%, the middle wheel of a rail carried nothing at all — 654V_v2 read
// 231/67/0 down the left rail and 242/60/0 down the right, 654V_v3's right rail 61/221/0.
//
// Three coplanar wheels on a rigid rail is a statically INDETERMINATE contact set: two of them hold
// the rail up, so the equilibrium equations have a whole family of solutions and the solver is free
// to pick any one. It picks the extremes, differently on each rail, and on the field it drifts
// between solutions — which is the trade Connor describes, exactly. A real rigid six-wheel robot has
// the same problem; that is what drop centres and suspension are for. It costs nothing in
// straight-line grip (the weight is still on the wheels) but it does cost grip in a TURN, because
// the tyre's mu is keyed on lateral slip and the rail's end wheels are the ones that slip most.
//
// Contact-level compliance was built and REVERTED — see [[robosim-wheel-tyre-model]] for the two
// traps, in short: SetSeparation gives the right MEAN share but only by hunting in and out of
// contact (which broke the parked-shove hold and then roll-out), and SetMaxImpulse cannot be
// measured with this probe at all, because Physics.ContactEvent reports the impulse PhysX COMPUTED,
// not the one the clamp allowed. Real suspension travel is the honest fix and it is a rig change.
//
// ROBOSIM_PROBE_RIG=field|bare|both (default field)   ROBOSIM_PROBE_ROBOT=<prefab name filter>
public static class WheelGroundContactProbe
{
    // A wheel carrying less than this share of the even split is present but not driving: at 5% of
    // an even share its traction ceiling is 5% of the wheel's design force.
    private const float DeadLoadFraction = 0.05f;

    private struct Phase
    {
        public string name;
        public float throttle, turn;
        public int steps;
    }

    // A match, compressed: settle, launch, hold an arc, pivot, back out, arc the other way, stop.
    // Both turn directions on purpose — a robot whose mass is off-centre only drops a wheel one way.
    private static readonly Phase[] Match =
    {
        new Phase { name = "settle",     throttle =  0f, turn =  0f,   steps = 300 },
        new Phase { name = "launch",     throttle =  1f, turn =  0f,   steps = 150 },
        new Phase { name = "arc right",  throttle =  1f, turn =  0.6f, steps = 150 },
        new Phase { name = "pivot",      throttle =  0f, turn =  1f,   steps = 100 },
        new Phase { name = "reverse",    throttle = -1f, turn =  0f,   steps = 150 },
        new Phase { name = "arc left",   throttle =  1f, turn = -0.6f, steps = 150 },
        new Phase { name = "brake",      throttle =  0f, turn =  0f,   steps =  50 },
    };

    // Connor, 2026-09-06: "the drivetrain just breaks after a while. Half the wheels cease
    // touching the ground." A ten-second run cannot tell a fault that CREEPS IN (mechanisms
    // drifting, a joint walking out of pose, the robot slowly climbing something) from one that is
    // TRIGGERED and recovers. ROBOSIM_PROBE_LAPS repeats the match; the timeline below prints how
    // many wheels are carrying load over time, so the shape of the failure is visible.
    private static int Laps()
    {
        string s = Environment.GetEnvironmentVariable("ROBOSIM_PROBE_LAPS");
        return int.TryParse(s, out int n) && n > 0 ? n : 1;
    }

    [MenuItem("Tools/RoboSim/Validate/Probes/Wheel Ground Contact", false, 72)]
    public static void Probe() => ValidationUtil.RunInteractive("Wheel Ground Contact", Run);

    public static void RunBatchValidate() => ValidationUtil.RunBatch("Wheel Ground Contact", Run);

    // ---- contact accounting -------------------------------------------------------------------
    // The callback runs off the main thread: managed lookups and plain arrays only, no Unity API.
    private static readonly Dictionary<EntityId, int> linkByCollider = new Dictionary<EntityId, int>();
    private static Vector3[] stepImpulse = Array.Empty<Vector3>();
    // A wall or a wedged cup resists with a HORIZONTAL normal, so the normal impulse — the only
    // one PhysX reports — does capture it, keyed on the collider doing the pushing.
    private static readonly Dictionary<EntityId, Vector3> stepBlock = new Dictionary<EntityId, Vector3>();
    // (link, what it touched, impulse) for this step. A claw resting on the tiles and a claw
    // holding a cup deliver the SAME vertical load; only the other collider's identity separates
    // them, so the name is the whole point of this list.
    private static readonly List<(int link, EntityId world, Vector3 impulse)> stepPairs =
        new List<(int, EntityId, Vector3)>();
    private static bool[] stepTouching = Array.Empty<bool>();
    private static bool subscribed;

    private static void OnContact(PhysicsScene scene, NativeArray<ContactPairHeader>.ReadOnly headers)
    {
        Array.Clear(stepImpulse, 0, stepImpulse.Length);
        Array.Clear(stepTouching, 0, stepTouching.Length);
        stepBlock.Clear();
        stepPairs.Clear();

        for (int h = 0; h < headers.Length; h++)
        {
            ContactPairHeader header = headers[h];
            for (int p = 0; p < header.pairCount; p++)
            {
                ContactPair pair = header.GetContactPair(p);
                bool aRobot = linkByCollider.TryGetValue(pair.colliderEntityId, out int ia);
                bool bRobot = linkByCollider.TryGetValue(pair.otherColliderEntityId, out int ib);
                // Both -> the robot touching itself, which carries no weight to the ground.
                // Neither -> the field touching the field.
                if (aRobot == bRobot) continue;

                Vector3 j = Vector3.zero;
                for (int i = 0; i < pair.contactCount; i++) j += pair.GetContactPoint(i).impulse;

                // The reported impulse is the one applied to the FIRST collider's body; the second
                // receives its negative. Its vertical part holds the robot up; its horizontal part
                // IS the drivetrain's output — the force the tyres actually put into the ground.
                int link = aRobot ? ia : ib;
                Vector3 onRobot = aRobot ? j : -j;
                stepImpulse[link] += onRobot;
                stepTouching[link] = true;

                EntityId world = aRobot ? pair.otherColliderEntityId : pair.colliderEntityId;
                stepPairs.Add((link, world, onRobot));
                stepBlock.TryGetValue(world, out Vector3 acc);
                stepBlock[world] = acc + onRobot;
            }
        }
    }

    private static string Run()
    {
        string filter = Environment.GetEnvironmentVariable("ROBOSIM_PROBE_ROBOT");
        string rig = Environment.GetEnvironmentVariable("ROBOSIM_PROBE_RIG");
        if (string.IsNullOrEmpty(rig)) rig = "field";

        var lines = new StringBuilder();
        foreach (string where in rig == "both" ? new[] { "bare", "field" } : new[] { rig })
        {
            foreach (GameObject prefab in RoboSimPaths.RobotPrefabs())
            {
                if (prefab == null || prefab.GetComponent<RobotMotorController>() == null) continue;
                if (!string.IsNullOrEmpty(filter) && !prefab.name.Contains(filter)) continue;
                lines.AppendLine(Measure(prefab, where == "field"));
                lines.AppendLine();
            }
        }

        return "Wheel ground contact, driven through a match (contact = a reported pair with "
               + "something that is not the robot; load = the vertical impulse it delivered):\n"
               + lines.ToString().TrimEnd();
    }

    private static string Measure(GameObject prefab, bool onField)
    {
        var sb = new StringBuilder();
        SimulationMode previous = Physics.simulationMode;
        try
        {
            ArticulationBody root = TurnAfterInteractionProbe.SpawnPrepared(prefab, onField,
                out RobotMotorController motor, out float floorY);
            Physics.simulationMode = SimulationMode.Script;

            ArticulationBody[] wheels = RobotPhysicsValidation.FindWheels(root,
                out ArticulationBody[] left, out ArticulationBody[] _);
            var isLeft = new bool[wheels.Length];
            for (int w = 0; w < wheels.Length; w++) isLeft[w] = Array.IndexOf(left, wheels[w]) >= 0;

            // Every link, so weight that leaves the robot somewhere OTHER than a wheel is named.
            ArticulationBody[] links = root.GetComponentsInChildren<ArticulationBody>(true);
            var indexOfLink = new Dictionary<ArticulationBody, int>();
            for (int i = 0; i < links.Length; i++) indexOfLink[links[i]] = i;

            linkByCollider.Clear();
            var robotColliders = new HashSet<Collider>();
            foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger) continue;
                c.providesContacts = true;              // Register does this too; a probe must not assume it
                robotColliders.Add(c);
                ArticulationBody owner = c.GetComponentInParent<ArticulationBody>();
                if (owner != null && indexOfLink.TryGetValue(owner, out int idx))
                    linkByCollider[c.GetEntityId()] = idx;
            }

            // Resolved here because the contact callback runs off the main thread and cannot
            // touch a Unity object. Whole-scene sweep, once per robot; this is a probe.
            var nameByCollider = new Dictionary<EntityId, string>();
            foreach (Collider c in UnityEngine.Object.FindObjectsByType<Collider>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (c == null || robotColliders.Contains(c)) continue;
                nameByCollider[c.GetEntityId()] = c.name;
            }

            stepImpulse = new Vector3[links.Length];
            stepTouching = new bool[links.Length];
            if (!subscribed) { Physics.ContactEvent += OnContact; subscribed = true; }

            float mass = 0f;
            foreach (ArticulationBody b in links) mass += b.mass;
            float g = Mathf.Abs(Physics.gravity.y);
            float weight = mass * g;
            float evenShare = weight / Mathf.Max(wheels.Length, 1);
            float dt = ValidationUtil.StepSeconds;

            // A component whose whole effect is a contact that never happens is invisible until
            // something climbs, so print the wiring: 0 registered means it never ran.
            NonSupportingLinkModel.CollectDiagnostics = true;
            NonSupportingLinkModel.ResetCounters();
            sb.AppendLine($"    [non-supporting links: {NonSupportingLinkModel.RegisteredColliderCount} " +
                          $"colliders registered{(NonSupportingLinkModel.Disabled ? ", DISABLED by env" : "")}]");
            sb.AppendLine($"'{prefab.name}' on the {(onField ? "field" : "bare floor")}: " +
                          $"{left.Length} left / {wheels.Length - left.Length} right wheels, " +
                          $"{mass:0.0} kg, weight {weight:0} ({evenShare:0} per wheel if shared evenly)");

            // Per wheel, over the whole run.
            var touchSteps = new int[wheels.Length];
            var deadSteps = new int[wheels.Length];
            var loadSum = new float[wheels.Length];
            var worstAirGap = new float[wheels.Length];
            var airRun = new int[wheels.Length];
            var worstAirRun = new int[wheels.Length];
            var otherSupportSum = new float[links.Length];
            int sampled = 0;
            float wheelSupportSum = 0f, otherTotalSum = 0f;
            var blockSum = new Dictionary<EntityId, float>();   // horizontal reaction, by pusher
            var carriedBy = new Dictionary<int, Dictionary<EntityId, float>>();  // link -> what held it up

            // push  = the NET forward force the wheels put into the ground, in g. This is the
            //         drivetrain's actual output, and the number "the drive train feels weak" is
            //         a claim about.
            // grip  = the TRACTION CEILING, sum over wheels of mu_i * N_i, over the robot's weight.
            //         This is how hard the robot is ALLOWED to push, as a fraction of its own
            //         weight: 0.80 is a fully gripping robot, 0.40 is one that can only push half
            //         as hard as it could in a straight line.
            //
            // NOT measured per wheel, and it cannot be: ContactPairPoint.impulse is the NORMAL
            // impulse only — PhysX never reports friction per contact point — so a per-wheel
            // forward force read off the contacts is identically zero on a flat floor. An earlier
            // revision of this probe printed exactly that (+0.00 in every phase). accel comes off
            // the chassis, where it is real; grip is derived from mu and N, which are both honest.
            // mu    = what the tyre model left the contact at. It is ONE isotropic coefficient per
            //         contact, so lateral slip lowers the forward grip too; that coupling is
            //         exactly what this column is here to expose.
            sb.AppendLine("    phase        travel  speed  down  thru wheels  accel(g)  grip(g)  held(g)   mu   " +
                          "per wheel: load as % of an even share");
            int laps = Laps();
            var timeline = new List<string>();
            int timelineEvery = Mathf.Max(1, Mathf.RoundToInt(0.5f / dt));

            for (int lap = 0; lap < laps; lap++)
            foreach (Phase phase in Match)
            {
                int phaseTouch = 0, phaseSteps = 0;
                float phaseWheelSupport = 0f, phaseOther = 0f, phaseSpeed = 0f;
                float phaseAccel = 0f, phaseGrip = 0f, phaseMu = 0f, phaseHeld = 0f;
                Vector3 phaseStart = root.transform.position;
                float prevFwd = Vector3.Dot(root.linearVelocity, motor.DriveForwardWorld);
                var phaseLoad = new float[wheels.Length];

                for (int s = 0; s < phase.steps; s++)
                {
                    TipOverValidation.StepDriven(motor, phase.throttle, phase.turn, 1);
                    sampled++; phaseSteps++;
                    phaseSpeed += new Vector3(root.linearVelocity.x, 0f, root.linearVelocity.z).magnitude;
                    Vector3 fwd = motor.DriveForwardWorld;

                    // The drivetrain's actual output, measured where it is real: the chassis.
                    float nowFwd = Vector3.Dot(root.linearVelocity, fwd);
                    phaseAccel += (nowFwd - prevFwd) / dt / g;
                    prevFwd = nowFwd;

                    float wheelSupport = 0f;
                    for (int w = 0; w < wheels.Length; w++)
                    {
                        int idx = indexOfLink[wheels[w]];
                        Vector3 force = stepImpulse[idx] / dt;
                        float load = force.y;
                        wheelSupport += load;
                        loadSum[w] += load; phaseLoad[w] += load;
                        float mu = WheelTyreModel.PeekLastMu(wheels[w]);
                        phaseGrip += mu * load;   // what this contact patch is allowed to push with
                        phaseMu += mu;
                        if (stepTouching[idx]) { touchSteps[w]++; phaseTouch++; airRun[w] = 0; }
                        else
                        {
                            airRun[w]++;
                            if (airRun[w] > worstAirRun[w]) worstAirRun[w] = airRun[w];
                            worstAirGap[w] = Mathf.Max(worstAirGap[w], GapBelow(wheels[w], robotColliders));
                        }
                        if (load < evenShare * DeadLoadFraction) deadSteps[w]++;
                    }

                    float other = 0f;
                    for (int i = 0; i < links.Length; i++)
                    {
                        if (Array.IndexOf(wheels, links[i]) >= 0) continue;
                        float load = stepImpulse[i].y / dt;
                        other += load;
                        otherSupportSum[i] += load;
                    }

                    foreach ((int link, EntityId world, Vector3 impulse) in stepPairs)
                    {
                        float up = impulse.y / dt;
                        if (up <= 0f) continue;
                        if (!carriedBy.TryGetValue(link, out Dictionary<EntityId, float> byWorld))
                            carriedBy[link] = byWorld = new Dictionary<EntityId, float>();
                        byWorld.TryGetValue(world, out float prior);
                        byWorld[world] = prior + up;
                    }

                    if (sampled % timelineEvery == 0)
                    {
                        // Connor, 2026-09-07: "it does seem to self fix to 2 wheels each side
                        // working ... later on, the right side one of them would fix while the left
                        // side one would break." A COUNT cannot show a TRADE — 4/6 reads the same
                        // whichever two are dead. The mask can: one character per wheel in rail
                        // order, '#' carrying load, '.' below the dead threshold.
                        int loaded = 0;
                        var maskL = new StringBuilder();
                        var maskR = new StringBuilder();
                        for (int w = 0; w < wheels.Length; w++)
                        {
                            bool live = stepImpulse[indexOfLink[wheels[w]]].y / dt
                                        >= evenShare * DeadLoadFraction;
                            if (live) loaded++;
                            (isLeft[w] ? maskL : maskR).Append(live ? '#' : '.');
                        }
                        timeline.Add($"{sampled * dt,5:0.0}s {phase.name,-10} " +
                                     $"loaded {loaded}/{wheels.Length}  " +
                                     $"L {maskL} R {maskR}  " +
                                     $"tilt {Vector3.Angle(root.transform.up, Vector3.up),4:0.0} deg  " +
                                     $"height {root.transform.position.y,6:0.00}");
                    }

                    // What the WORLD is holding the robot back with. A robot stalled against a
                    // wall reads a large held; a robot that has simply run out of grip reads ~0.
                    float held = 0f;
                    foreach (KeyValuePair<EntityId, Vector3> kv in stepBlock)
                    {
                        Vector3 f = kv.Value / dt;
                        float h = new Vector3(f.x, 0f, f.z).magnitude;
                        held += h;
                        blockSum.TryGetValue(kv.Key, out float prior);
                        blockSum[kv.Key] = prior + h;
                    }
                    phaseHeld += held;

                    wheelSupportSum += wheelSupport; otherTotalSum += other;
                    phaseWheelSupport += wheelSupport; phaseOther += other;
                }

                var row = new StringBuilder($"    {phase.name,-11} " +
                    $"{Vector3.Distance(phaseStart, root.transform.position),6:0.0} " +
                    $"{phaseSpeed / phaseSteps,6:0.0} " +
                    $"{(float)phaseTouch / (phaseSteps * wheels.Length),5:0%} " +
                    $"{phaseWheelSupport / Mathf.Max(phaseWheelSupport + phaseOther, 1e-3f),9:0%}    " +
                    $"{phaseAccel / phaseSteps,8:+0.00;-0.00} {phaseGrip / phaseSteps / weight,8:0.00} " +
                    $"{phaseHeld / phaseSteps / weight,8:0.00} " +
                    $"{phaseMu / (phaseSteps * wheels.Length),5:0.00}  ");
                for (int w = 0; w < wheels.Length; w++)
                    row.Append($"{phaseLoad[w] / phaseSteps / evenShare,5:0%} ");
                if (lap == 0) sb.AppendLine(row.ToString());
            }
            if (laps > 1)
            {
                sb.AppendLine($"    ({laps} laps; the phase table above is lap 1 only)");
                var legendL = new StringBuilder();
                var legendR = new StringBuilder();
                for (int w = 0; w < wheels.Length; w++)
                    (isLeft[w] ? legendL : legendR).Append(Short(wheels[w].name) + " ");
                sb.AppendLine("    timeline — wheels carrying load, every 0.5 s:");
                sb.AppendLine($"      mask order:  L {legendL}  R {legendR}");
                foreach (string t in timeline) sb.AppendLine("      " + t);
            }

            sb.AppendLine($"    non-supporting contacts suppressed: {NonSupportingLinkModel.SuppressedContacts}");
            sb.AppendLine($"    {NonSupportingLinkModel.ConeHistogram()}");
            sb.AppendLine("    per wheel over the whole run:");
            for (int w = 0; w < wheels.Length; w++)
            {
                string air = worstAirRun[w] == 0 ? "never left the ground"
                    // 100, not 1000: the world is 10 units to the METRE (gravity 98, and an 11 kg
                    // robot weighs 1077), so a unit is 100 mm. This read 10x high until 2026-09-07.
                    : $"off the ground for up to {worstAirRun[w] * dt:0.00} s (gap {worstAirGap[w] * 100f:0} mm)";
                sb.AppendLine($"      {Short(wheels[w].name),-6} {(isLeft[w] ? 'L' : 'R')}  " +
                              $"touching {(float)touchSteps[w] / sampled,4:0%} of steps  " +
                              $"mean load {loadSum[w] / sampled / evenShare,5:0%} of even  " +
                              $"under {DeadLoadFraction:0%} for {(float)deadSteps[w] / sampled,4:0%} of steps  {air}");
            }

            // Where the weight goes when it does not go through a wheel.
            var carriers = new List<(string Key, float Value, int Index)>();
            for (int i = 0; i < links.Length; i++)
            {
                if (Array.IndexOf(wheels, links[i]) >= 0 || otherSupportSum[i] / sampled < evenShare * 0.01f) continue;
                carriers.Add((links[i].name, otherSupportSum[i] / sampled, i));
            }
            carriers.Sort((x, y) => y.Value.CompareTo(x.Value));

            float total = Mathf.Max(wheelSupportSum + otherTotalSum, 1e-3f);
            // HONESTY CHECK. Everything holding the robot up must add to its weight. If this reads
            // far above 100% the reported impulses are ones PhysX COMPUTED but did not apply, and
            // every "carried by" number below is inflated — which is exactly what contact
            // modification does to a probe that measures support from contact impulses.
            sb.AppendLine($"    reported support / weight: {total / sampled / weight:0%} " +
                          "(100% = the accounting is closed; well above = impulses were clamped away)");
            sb.AppendLine($"    -> the drive wheels carried {wheelSupportSum / total:0%} of the support the " +
                          $"robot got from the world; {otherTotalSum / total:0%} went through something else.");
            var blockers = new List<KeyValuePair<string, float>>();
            foreach (KeyValuePair<EntityId, float> kv in blockSum)
            {
                float mean = kv.Value / sampled;
                if (mean < weight * 0.02f) continue;
                blockers.Add(new KeyValuePair<string, float>(
                    nameByCollider.TryGetValue(kv.Key, out string n) ? n : "?", mean));
            }
            blockers.Sort((x, y) => y.Value.CompareTo(x.Value));

            if (carriers.Count == 0) sb.Append("       Nothing but the wheels ever took the robot's weight.");
            else
            {
                sb.AppendLine("       Also load-bearing:");
                for (int i = 0; i < carriers.Count && i < 5; i++)
                {
                    sb.Append($"         {carriers[i].Key} {carriers[i].Value / evenShare:0%} of an even " +
                              "share, resting on ");
                    if (!carriedBy.TryGetValue(carriers[i].Index, out Dictionary<EntityId, float> byWorld))
                    { sb.AppendLine("?"); continue; }
                    var on = new List<KeyValuePair<string, float>>();
                    foreach (KeyValuePair<EntityId, float> kv in byWorld)
                        on.Add(new KeyValuePair<string, float>(
                            nameByCollider.TryGetValue(kv.Key, out string n) ? n : "?", kv.Value));
                    on.Sort((x, y) => y.Value.CompareTo(x.Value));
                    for (int k = 0; k < on.Count && k < 3; k++)
                        sb.Append($"{on[k].Key} ({on[k].Value / Mathf.Max(carriers[i].Value * sampled, 1e-3f):0%}) ");
                    sb.AppendLine();
                }
            }
            sb.AppendLine();
            if (blockers.Count == 0)
                sb.Append("       Nothing in the world ever pushed back on it horizontally.");
            else
            {
                sb.Append("       Held back by: ");
                for (int i = 0; i < blockers.Count && i < 5; i++)
                    sb.Append($"{blockers[i].Key} at {blockers[i].Value / weight:0.00} g; ");
            }
            return sb.ToString().TrimEnd();
        }
        finally
        {
            Physics.simulationMode = previous;
            linkByCollider.Clear();
        }
    }

    // How far below this wheel the nearest thing that is NOT the robot sits. Only asked when the
    // wheel reported no contact, so a raycast is the honest measure and no floor plane is assumed.
    private static float GapBelow(ArticulationBody wheel, HashSet<Collider> own)
    {
        if (wheel == null) return 0f;
        float lowest = float.PositiveInfinity;
        Vector3 at = wheel.transform.position;
        foreach (SphereCollider sphere in wheel.GetComponentsInChildren<SphereCollider>(true))
        {
            Vector3 centre = sphere.transform.TransformPoint(sphere.center);
            Vector3 lossy = sphere.transform.lossyScale;
            float scale = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Max(Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));
            if (centre.y - sphere.radius * scale < lowest)
            {
                lowest = centre.y - sphere.radius * scale;
                at = new Vector3(centre.x, lowest, centre.z);
            }
        }
        if (float.IsPositiveInfinity(lowest)) return 0f;

        var hits = new RaycastHit[16];
        int n = Physics.RaycastNonAlloc(at + Vector3.up * 0.01f, Vector3.down, hits, 1f);
        float best = float.PositiveInfinity;
        for (int i = 0; i < n; i++)
            if (!own.Contains(hits[i].collider)) best = Mathf.Min(best, hits[i].distance - 0.01f);
        return float.IsPositiveInfinity(best) ? 1f : Mathf.Max(best, 0f);
    }

    private static string Short(string name)
    {
        int slash = name.LastIndexOf('/');
        return slash >= 0 ? name.Substring(slash + 1) : name;
    }
}
