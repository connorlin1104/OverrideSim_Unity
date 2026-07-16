using UnityEditor;
using UnityEngine;

// Shared helpers for the mechanism-authoring tools (the DR4B role builder, and the older lift tool if
// migrated). Kept in one place so the builders can't diverge on how they neutralize parts, reparent
// helpers, or clean button bindings. All support useUndo=false for headless/batch callers.
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
    // pose: JointCoupler, LiftCarriage, LinkageBarFollower, both DR4B followers, and the ArticulationBody
    // itself (a transform follower can't move an AB link — PhysX owns it). Safe when none are present.
    public static void NeutralizeToPlainTransform(GameObject go, bool useUndo)
    {
        if (go == null) return;
        RemoveComponents<JointCoupler>(go, useUndo);
        RemoveComponents<LiftCarriage>(go, useUndo);
        RemoveComponents<LinkageBarFollower>(go, useUndo);
        RemoveComponents<Dr4bMoveFollower>(go, useUndo);
        RemoveComponents<PivotRotateFollower>(go, useUndo);
        ArticulationBody body = go.GetComponent<ArticulationBody>();
        if (body != null)
        {
            if (useUndo) Undo.DestroyObjectImmediate(body);
            else Object.DestroyImmediate(body);
        }
    }

    // Drop every button binding a mechanism currently holds so re-assigning gives one clean pair
    // instead of stacking another each run (map.assignments is a public flat list keyed by id).
    public static void ClearMechanismBindings(string robotId, string mechanismId)
    {
        ButtonMap map = ControllerMapSettings.Load(robotId);
        if (map == null || map.assignments == null) return;
        int n = map.assignments.RemoveAll(a => a != null && a.mechanismId == mechanismId);
        if (n > 0) ControllerMapSettings.Save(robotId, map);
    }
}
