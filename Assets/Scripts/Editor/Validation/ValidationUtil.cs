using System;
using UnityEditor;
using UnityEngine;

// The shared harness and assertion helpers for the editor validators. Every validator follows the
// same contract: a private Run() that returns a one-line PASSED summary or throws
// InvalidOperationException with a human-readable why — and these wrappers turn that into either
// a dialog (menu item) or a nonzero editor exit (-executeMethod batch run).
//
// Each validator keeps its OWN public RunBatchValidate() entry point delegating here, because CI
// invokes them by class+method name.
internal static class ValidationUtil
{
    // Interactive menu wrapper: the summary — or the failure — lands in a dialog, and failures
    // also go to the console with a stack.
    public static void RunInteractive(string title, Func<string> run)
    {
        try
        {
            EditorUtility.DisplayDialog(title, run(), "OK");
        }
        catch (Exception e)
        {
            EditorUtility.DisplayDialog(title, "FAILED\n\n" + e.Message, "OK");
            Debug.LogException(e);
        }
    }

    // Batch wrapper for -executeMethod: log the summary and exit 0, or log the failure and exit 1
    // so a headless run fails loudly instead of "passing" by not crashing.
    public static void RunBatch(string title, Func<string> run)
    {
        try
        {
            Debug.Log(run());
        }
        catch (Exception e)
        {
            Debug.LogError(title + " FAILED: " + e.Message);
            EditorApplication.Exit(1);
            return;
        }
        EditorApplication.Exit(0);
    }

    public static void Assert(bool condition, string why)
    {
        if (!condition) throw new InvalidOperationException(why);
    }

    // Approximate equality with the actual/expected baked into the message. The NaN test is not
    // paranoia: NaN compares false against everything, so without it a NaN sails through the
    // tolerance check and "passes".
    public static void Near(float actual, float expected, float tolerance, string why)
    {
        if (float.IsNaN(actual) || Mathf.Abs(actual - expected) > tolerance)
            throw new InvalidOperationException($"{why} — expected {expected}, got {actual}");
    }

    public static void AssertThrows(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            return; // rejected, as it should be
        }
        throw new InvalidOperationException($"'{what}' was accepted, but it should have been rejected");
    }

    // Finite AND non-negative — the shape every physics-tuning number must have before it goes
    // into PhysX (a NaN forceLimit takes the whole articulation down).
    public static void Finite(float value, string what)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
            throw new InvalidOperationException($"{what} must be finite and non-negative, got {value}");
    }

    // Test-fixture cube. The two overloads are deliberately NOT unified: the position-only form
    // leaves the box's rotation inherited from its parent (what the cascade/claw fixtures rely
    // on), while the rotation form pins the world rotation explicitly.
    public static GameObject MakeBox(Transform parent, string name, Vector3 position, Vector3 size)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = position;
        go.transform.localScale = size;
        return go;
    }

    public static GameObject MakeBox(Transform parent, string name, Vector3 position, Quaternion rotation,
        Vector3 size)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(position, rotation);
        go.transform.localScale = size;
        return go;
    }

    // A big static floor for edit-mode physics fixtures, so a simulated robot has something to
    // land on. No renderer — validation scenes are never looked at.
    public static void CreateGroundPlane()
    {
        GameObject ground = new GameObject("Ground");
        BoxCollider box = ground.AddComponent<BoxCollider>();
        box.size = new Vector3(200f, 1f, 200f);
        ground.transform.position = new Vector3(0f, -0.5f, 0f);
    }
}
