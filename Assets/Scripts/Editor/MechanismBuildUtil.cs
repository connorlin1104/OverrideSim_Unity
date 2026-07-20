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
    public const float MinLiftMass = 1.5f;

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

    private static Vector3 Centroid(ArticulationBody[] arr)
    {
        if (arr == null) return Vector3.zero;
        Vector3 s = Vector3.zero; int n = 0;
        foreach (ArticulationBody a in arr) if (a != null) { s += a.transform.position; n++; }
        return n > 0 ? s / n : Vector3.zero;
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
