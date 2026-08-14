using UnityEditor;
using UnityEngine;

// Shared helpers for the mechanism-authoring tools (the DR4B, pneumatic and claw role builders, and
// the older lift tool if migrated). Kept in one place so the builders can't diverge on how they
// neutralize parts, reparent helpers, measure geometry, or clean button bindings. All support
// useUndo=false for headless/batch callers.
internal static class MechanismBuildUtil
{
    public const string UndoName = "Build Mechanism";

    // Mass-from-geometry on thin bars comes out near-zero; floor a driven joint so it's stable.
    //
    // WAS 1.5, and that number was doing far more than "keep the solver happy". It won on 100% of
    // shipped lift links, so it was not a floor at all — it WAS the mass of every lift in the
    // project. On 654V_v3 that put 4.5 kg of cascade stages on a 14.5 kg robot (31%, against a real
    // cascade's 20-25%) and took the whole robot 28% past the 11.3 kg a VEX V5 is allowed to weigh.
    //
    // What that bought was a raised centre of mass 302 mm up, which is realistic on its own, on a
    // 254 mm track, which is not enough to hold it — so a hard turn rolled the robot over. Forward
    // and reverse were fine, because the wheelbase is longer than the track and the tip thresholds
    // scale with the margin. The complaint that reached me was exactly that shape: "turning tips way
    // too much, going forward is fine."
    //
    // 0.9 is sized against the real part: a cascade stage is two lengths of 2x1 aluminium C-channel
    // plus bearings and hardware, which is 0.6-0.9 kg. It is still a floor — a stage whose geometry
    // measures heavier keeps its own mass — but it is no longer the answer for every lift ever built.
    //
    // This does NOT undo the tipping work. Measured after the change, 654V_v3 with the lift raised
    // still goes over on a slammed reversal; it just stops going over in a turn. See
    // RobotBalanceWindow, which reports both axes, and [robosim-roll-vs-pitch] for why the two
    // cannot be tuned apart with mass alone.
    public const float MinLiftMass = 0.9f;

    public static T AddOrGet<T>(GameObject go, bool useUndo) where T : Component
    {
        T c = go.GetComponent<T>();
        if (c == null) c = useUndo ? Undo.AddComponent<T>(go) : go.AddComponent<T>();
        return c;
    }

    public static void EnsureChildOf(Transform t, Transform parent, bool useUndo)
    {
        if (t == null || parent == null || t.parent == parent || t == parent) return;
        if (useUndo) Undo.SetTransformParent(t, parent, UndoName); // keeps world pose
        else t.SetParent(parent, true);
    }

    public static void DisableColliders(GameObject go, bool useUndo)
    {
        if (go == null) return;
        foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
        {
            if (c == null || !c.enabled) continue;
            if (useUndo) Undo.RecordObject(c, UndoName);
            c.enabled = false;
        }
    }

    // Undo of DisableColliders — used when a build releases a part it had neutralized (deleting a
    // pneumatic/claw, or reassigning a cosmetic cylinder part to something else).
    public static void EnableColliders(GameObject go, bool useUndo)
    {
        if (go == null) return;
        foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
        {
            if (c == null || c.enabled) continue;
            if (useUndo) Undo.RecordObject(c, UndoName);
            c.enabled = true;
        }
    }

    public static void DestroyGo(Transform t, bool useUndo)
    {
        if (t == null) return;
        if (useUndo) Undo.DestroyObjectImmediate(t.gameObject);
        else UnityEngine.Object.DestroyImmediate(t.gameObject);
    }

    public static void RemoveComponents<T>(GameObject go, bool useUndo) where T : Component
    {
        if (go == null) return;
        foreach (T c in go.GetComponents<T>())
        {
            if (c == null) continue;
            if (useUndo) Undo.DestroyObjectImmediate(c);
            else Object.DestroyImmediate(c);
        }
    }

    // Strip any prior mechanism/follower wiring so a part becomes an inert transform a follower can
    // pose: JointCoupler, LiftCarriage, LinkageBarFollower, both DR4B followers, both cosmetic
    // cylinder followers, and the ArticulationBody itself (a transform follower can't move an AB
    // link — PhysX owns it). Two followers on one part would fight over its transform, so a part
    // being reassigned to a new role must arrive clean. Safe when none are present.
    public static void NeutralizeToPlainTransform(GameObject go, bool useUndo)
    {
        if (go == null) return;
        RemoveComponents<JointCoupler>(go, useUndo);
        RemoveComponents<LiftCarriage>(go, useUndo);
        RemoveComponents<LinkageBarFollower>(go, useUndo);
        RemoveComponents<Dr4bMoveFollower>(go, useUndo);
        RemoveComponents<PivotRotateFollower>(go, useUndo);
        RemoveComponents<PneumaticCylinderFollower>(go, useUndo);
        RemoveComponents<PneumaticSlideFollower>(go, useUndo);
        ArticulationBody body = go.GetComponent<ArticulationBody>();
        if (body != null)
        {
            if (useUndo) Undo.DestroyObjectImmediate(body);
            else Object.DestroyImmediate(body);
        }
    }

    // Drop every button binding a mechanism currently holds so re-assigning gives one clean pair
    // instead of stacking another each run (map.assignments is a public flat list keyed by id).
    //
    // Deliberately leaves the mechanism's CONTROL STYLE alone: this runs on every rebuild, and the
    // style is the player's choice, not build output — AssignButtons re-assigns to match it. Styles
    // for mechanisms that are genuinely gone are pruned by ControllerConfigScreen when it opens.
    public static void ClearMechanismBindings(string robotId, string mechanismId)
    {
        ButtonMap map = ControllerMapSettings.Load(robotId);
        if (map == null || map.assignments == null) return;
        int n = map.assignments.RemoveAll(a => a != null && a.mechanismId == mechanismId);
        if (n > 0) ControllerMapSettings.Save(robotId, map);
    }

    // Re-derive a joint's PARENT-side anchor from the live (already-scaled) transforms, with
    // matchAnchors off. PhysX stores the joint frame on BOTH sides, so this is needed whenever the
    // link's own anchor is set or the link CHANGES PARENT — a stale parent anchor snaps the link back
    // to where its old parent held it on the first Simulate(). Shared by
    // AddMechanismJoint.ConfigureJointLink (fresh anchor) and the cascade builder (which reparents an
    // already-jointed arm under a lift stage). No-op for a root or a link with no body above it.
    public static void RederiveParentAnchors(ArticulationBody body)
    {
        // NEVER gate this on ArticulationBody.isRoot. That property reflects NATIVE articulation
        // state, and it is true for every body in a hierarchy whose articulation has not been built
        // — which is the case for a prefab opened with LoadPrefabContents, and can be the case in
        // Prefab Mode. Gated on it, this method silently did nothing at all, and the joint kept
        // whatever parent anchor it was born with: 654V_v3's CascadeMotor still carries the
        // untouched default (0,0,0), which puts 1.5 kg 311 mm from where the part is drawn the
        // instant physics starts. A silent no-op in the one place that writes the anchor is the
        // worst possible failure, because everything downstream looks correct in the editor.
        //
        // The hierarchy walk below is the correct root test and was always here: no ancestor body
        // means no joint to derive, and the method returns on its own.
        if (body == null) return;
        body.matchAnchors = false;   // cleared whether or not a parent is found, as the inline code did
        ArticulationBody parent = null;
        for (Transform p = body.transform.parent; p != null && parent == null; p = p.parent)
            parent = p.GetComponent<ArticulationBody>();
        if (parent == null) return;

        Transform pt = parent.transform, ct = body.transform;
        body.parentAnchorPosition = pt.InverseTransformPoint(ct.TransformPoint(body.anchorPosition));
        body.parentAnchorRotation = Quaternion.Inverse(pt.rotation) * (ct.rotation * body.anchorRotation);
    }

    // --- Geometry ---------------------------------------------------------------------------------
    // Bounds are RENDERER-based (the visual), not transform origins: CAD parts routinely have their
    // pivot far off the mesh, which is what made early rigs place pivots and cylinder ends in midair.

    public static bool TryBounds(GameObject go, out Bounds b)
    {
        b = default;
        if (go == null) return false;
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return false;
        b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return true;
    }

    public static bool TryBoundsCenter(GameObject go, out Vector3 center)
    {
        center = Vector3.zero;
        if (!TryBounds(go, out Bounds b)) return false;
        center = b.center;
        return true;
    }

    public static Vector3 BoundsCenterOrOrigin(GameObject go)
    {
        if (go == null) return Vector3.zero;
        return TryBoundsCenter(go, out Vector3 c) ? c : go.transform.position;
    }

    // The point on go's mesh bounds nearest `point` (falls back to its transform origin). Used to seat
    // helper empties — cylinder mounts, claw hinge pivots — on the actual geometry rather than in space.
    public static Vector3 ClosestOnBounds(GameObject go, Vector3 point)
        => TryBounds(go, out Bounds b) ? b.ClosestPoint(point) : (go != null ? go.transform.position : point);

    // The far end of `go` ALONG THE AXIS from `from` through the mesh's visual center — a point on the
    // part's centerline, not an axis-aligned bounding-box corner. Keeping cylinder ends on the
    // centerline makes a rod slide straight out of its body instead of skewing toward a corner.
    public static Vector3 AxisFarPoint(GameObject go, Vector3 from)
    {
        if (!TryBounds(go, out Bounds b)) return go != null ? go.transform.position : from;
        Vector3 dir = b.center - from;
        if (dir.sqrMagnitude < 1e-8f) return b.center;
        dir.Normalize();
        // Distance from the center to the AABB surface in the direction `dir` (support width of the box).
        float ext = Mathf.Abs(dir.x) * b.extents.x + Mathf.Abs(dir.y) * b.extents.y + Mathf.Abs(dir.z) * b.extents.z;
        return b.center + dir * ext;
    }

    // The robot's lateral (left<->right) axis in `frame`'s local space, read from the drivetrain
    // wheels — the hinge axis a doinker, DR4B arm or claw flip turns about, correct whatever the CAD
    // orientation. False if the robot has no rigged drivetrain to read.
    public static bool TryDrivetrainLateralLocal(GameObject frame, out Vector3 axisLocal)
    {
        axisLocal = Vector3.right;
        if (frame == null) return false;
        RobotMechanisms reg = frame.GetComponentInParent<RobotMechanisms>();
        RobotMotorController mc = reg != null ? reg.GetComponentInChildren<RobotMotorController>(true) : null;
        if (mc == null) return false;
        Vector3 lat = Centroid(mc.rightWheels) - Centroid(mc.leftWheels);
        if (lat.sqrMagnitude < 1e-6f) return false;
        axisLocal = frame.transform.InverseTransformDirection(lat.normalized).normalized;
        return axisLocal.sqrMagnitude > 1e-8f;
    }

    // The mean world position of a set of links — the drivetrain's left/right wheel groups, a
    // chain's sprockets. Was a private copy in four editor files under two parameter names.
    // "The user clicked a part — which robot is that?" Walks up through the registry, then the motor
    // controller, then to the topmost ArticulationBody, and finally gives up and treats the selection
    // itself as the robot (nothing rigged yet is a legitimate state for the collider tools).
    //
    // Was byte-identical in StripRobotRig and GeneratePartColliders, the latter carrying a comment
    // saying it mirrored the former so the two tools would resolve the same root. Now they do by
    // construction rather than by vigilance.
    public static GameObject ResolveRobotRoot(GameObject sel)
    {
        if (sel == null) return null;
        RobotMechanisms reg = sel.GetComponentInParent<RobotMechanisms>();
        if (reg != null) return reg.gameObject;
        RobotMotorController motor = sel.GetComponentInParent<RobotMotorController>();
        if (motor != null) return motor.gameObject;
        ArticulationBody ab = sel.GetComponentInParent<ArticulationBody>();
        if (ab != null)
        {
            Transform top = ab.transform;
            for (Transform t = top.parent; t != null; t = t.parent)
                if (t.GetComponent<ArticulationBody>() != null) top = t;
            return top.gameObject;
        }
        return sel; // nothing rigged yet — treat the selection as the robot
    }

    public static Vector3 Centroid(ArticulationBody[] arr)
    {
        if (arr == null) return Vector3.zero;
        Vector3 s = Vector3.zero; int n = 0;
        foreach (ArticulationBody a in arr) if (a != null) { s += a.transform.position; n++; }
        return n > 0 ? s / n : Vector3.zero;
    }

    // --- Shared builder plumbing ------------------------------------------------------------------

    // The registry a builder should target: the first candidate part that sits under a
    // RobotMechanisms, else whatever the current selection sits under. Each builder passes its own
    // assigned parts as candidates, so a half-filled form still resolves the right robot.
    public static RobotMechanisms RegistryFromAny(System.Collections.Generic.IEnumerable<GameObject> candidates)
    {
        if (candidates != null)
        {
            foreach (GameObject go in candidates)
            {
                if (go == null) continue;
                RobotMechanisms r = go.GetComponentInParent<RobotMechanisms>();
                if (r != null) return r;
            }
        }
        return Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponentInParent<RobotMechanisms>()
            : null;
    }

    // First descendant of root (inclusive) with this exact name — how the builders find the helper
    // empties (pivots, hold points, carriages) they created on a previous run.
    public static Transform FindChild(Transform root, string name)
    {
        if (root == null) return null;
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    // The builders' shared part-list UI: a labelled column of GameObject fields with per-row
    // remove and a trailing Add.
    public static void DrawGoList(string label, string tip, System.Collections.Generic.List<GameObject> list)
    {
        EditorGUILayout.LabelField(new GUIContent(label, tip), EditorStyles.miniBoldLabel);
        for (int i = 0; i < list.Count; i++)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                list[i] = (GameObject)EditorGUILayout.ObjectField(list[i], typeof(GameObject), true);
                if (GUILayout.Button("X", GUILayout.Width(24))) { list.RemoveAt(i); i--; }
            }
        }
        if (GUILayout.Button("Add", GUILayout.Width(70))) list.Add(null);
    }

    // Non-zero guard for scale division: a ~0 lossyScale component would blow a size up to
    // infinity, so treat it as 1.
    public static float Nz(float v) => Mathf.Abs(v) < 1e-4f ? 1f : v;

    // --- The robot's existing mechanisms ----------------------------------------------------------
    //
    // Both the joint tool and the starting-pose tool open on the same question — "which of the
    // things already on this robot am I changing?" — and neither could answer it: you had to know
    // the part's name, find it in a 1260-node hierarchy, and drag it in. This is that list.

    // The robot a mechanism window should work on, from a form field, else the selection, else the
    // one robot that is open. The last fallback is what makes the roster appear the moment the
    // window opens in Prefab Mode, with nothing filled in and nothing selected.
    public static RobotMechanisms ResolveRobot(GameObject hint)
    {
        if (hint != null)
        {
            RobotMechanisms fromHint = hint.GetComponentInParent<RobotMechanisms>();
            if (fromHint != null) return fromHint;
        }
        if (Selection.activeGameObject != null)
        {
            RobotMechanisms fromSelection = Selection.activeGameObject.GetComponentInParent<RobotMechanisms>();
            if (fromSelection != null) return fromSelection;
        }
        UnityEditor.SceneManagement.PrefabStage stage =
            UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null && stage.prefabContentsRoot != null)
            return stage.prefabContentsRoot.GetComponentInChildren<RobotMechanisms>(true);
        return null;
    }

    // The link a registry record drives, whichever kind of actuator it uses.
    public static GameObject MechanismLink(RobotMechanisms.Mechanism m)
    {
        if (m == null) return null;
        if (m.motor != null) return m.motor.gameObject;
        return m.pneumatic != null ? m.pneumatic.gameObject : null;
    }

    // What the joint can do and WHERE THE CAD SITS IN IT. The second half is the whole reason this
    // string exists: a mechanism's rest pose is always joint angle 0, so which end of the travel the
    // part was modelled at is just where 0 falls between the limits — and that is exactly the fact
    // you need when a part was modelled extended and has to start folded up to be in size.
    public static string DescribeJointTravel(ArticulationBody body)
    {
        if (body == null) return "no joint";
        ArticulationDrive d = body.xDrive;
        switch (body.jointType)
        {
            case ArticulationJointType.RevoluteJoint:
                if (body.twistLock == ArticulationDofLock.FreeMotion) return "spins freely";
                return $"hinge {d.lowerLimit:0.#}° … {d.upperLimit:0.#}° — {DescribeRestWithin(d)}";
            case ArticulationJointType.PrismaticJoint:
                return $"slides {d.lowerLimit:0.##} … {d.upperLimit:0.##} u — {DescribeRestWithin(d)}";
            case ArticulationJointType.FixedJoint:
                return "welded (no mechanism)";
            default:
                return body.jointType.ToString();
        }
    }

    // Where rest (joint 0) falls in a limited joint's window, in words.
    private static string DescribeRestWithin(ArticulationDrive d)
    {
        float span = d.upperLimit - d.lowerLimit;
        if (span <= 1e-4f) return "no travel at all";
        float fraction = Mathf.Clamp01((0f - d.lowerLimit) / span);
        if (fraction <= 0.02f) return "starts at the BOTTOM";
        if (fraction >= 0.98f) return "starts at the TOP";
        return $"starts {fraction:P0} up";
    }

    // One row per mechanism on the robot: its name, what its joint does, where it currently starts,
    // and a button that loads it into the calling window. Returns the link whose button was pressed,
    // or null. `current` is highlighted so it's obvious what the form is showing.
    public static GameObject DrawMechanismRoster(RobotMechanisms registry, GameObject current, string pickLabel)
    {
        if (registry == null) return null;

        int count = registry.mechanisms != null ? registry.mechanisms.Count : 0;
        EditorGUILayout.LabelField(new GUIContent($"On '{registry.gameObject.name}' ({count})",
            "Everything already set up as a mechanism on this robot. Pick one to work on it instead " +
            "of hunting for the part in the hierarchy."), EditorStyles.miniBoldLabel);

        if (count == 0)
        {
            EditorGUILayout.HelpBox("This robot has no mechanisms yet.", MessageType.None);
            return null;
        }

        GameObject picked = null;
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            foreach (RobotMechanisms.Mechanism m in registry.mechanisms)
            {
                GameObject link = MechanismLink(m);
                if (m == null) continue;

                using (new EditorGUILayout.HorizontalScope())
                {
                    // A record whose actuator went missing is worth SHOWING rather than hiding —
                    // that is a broken mechanism, and the config UI will still offer it a button.
                    if (link == null)
                    {
                        EditorGUILayout.LabelField($"{m.displayName} — actuator missing", EditorStyles.miniLabel);
                        continue;
                    }
                    if (GUILayout.Button(pickLabel, GUILayout.Width(52))) picked = link;

                    bool isCurrent = link == current;
                    string label = $"{m.displayName}  ·  {DescribeJointTravel(link.GetComponent<ArticulationBody>())}";
                    EditorGUILayout.LabelField(isCurrent ? $"▸ {label}" : label,
                        isCurrent ? EditorStyles.boldLabel : EditorStyles.miniLabel);
                }
            }
        }
        return picked;
    }

    // Never strip the drivetrain or another registered mechanism's body out from under it — the guard
    // every builder's delete path needs before destroying an ArticulationBody.
    public static bool IsProtected(ArticulationBody body, RobotMechanisms registry)
    {
        if (body == null || registry == null) return false;
        RobotMotorController mc = registry.GetComponent<RobotMotorController>();
        if (mc != null)
        {
            if (mc.leftWheels != null && System.Array.IndexOf(mc.leftWheels, body) >= 0) return true;
            if (mc.rightWheels != null && System.Array.IndexOf(mc.rightWheels, body) >= 0) return true;
        }
        foreach (RobotMechanisms.Mechanism m in registry.mechanisms)
        {
            if (m == null) continue;
            if (m.motor != null && m.motor.gameObject == body.gameObject) return true;
            if (m.pneumatic != null && m.pneumatic.gameObject == body.gameObject) return true;
        }
        return false;
    }
}
