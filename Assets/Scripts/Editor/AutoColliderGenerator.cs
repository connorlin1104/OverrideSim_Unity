using UnityEngine;
using UnityEditor;

public class AutoColliderGenerator : EditorWindow
{
    [MenuItem("Tools/VEX/Auto-Generate Simple Colliders")]
    public static void ShowWindow()
    {
        GetWindow<AutoColliderGenerator>("Collider Generator");
    }

    private void OnGUI()
    {
        GUILayout.Label("Select your imported Robot GameObject in the Hierarchy,", EditorStyles.wordWrappedLabel);
        GUILayout.Label("then click the button below to auto-assign Box Colliders to child meshes.", EditorStyles.wordWrappedLabel);
        
        if (GUILayout.Button("Generate Box Colliders from Meshes"))
        {
            GenerateColliders();
        }
    }

    private static void GenerateColliders()
    {
        GameObject target = Selection.activeGameObject;
        if (target == null)
        {
            Debug.LogError("Please select a GameObject in the Hierarchy first!");
            return;
        }

        MeshFilter[] meshFilters = target.GetComponentsInChildren<MeshFilter>();
        int count = 0;

        foreach (MeshFilter mf in meshFilters)
        {
            // Skip if it already has a collider
            if (mf.GetComponent<Collider>() != null) continue;

            // Add a clean Box Collider instead of a heavy Mesh Collider
            BoxCollider box = mf.gameObject.AddComponent<BoxCollider>();
            
            // Unity automatically fits the BoxCollider to the local bounds of the mesh!
            box.center = mf.sharedMesh.bounds.center;
            box.size = mf.sharedMesh.bounds.size;

            count++;
        }

        Debug.Log($"Successfully auto-generated {count} simplified Box Colliders!");
    }
}