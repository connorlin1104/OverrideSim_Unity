using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class FixAllPins : EditorWindow
{
    private struct CustomColliderDef
    {
        public string name;
        public Vector3 position; 
        public Vector3 rotation;
    }

    [MenuItem("Tools/VEX Fixer/Fix All Pins (Any Color)")]
    public static void CleanAndOptimizePins()
    {
        GameObject selectedParent = Selection.activeGameObject;

        if (selectedParent == null)
        {
            EditorUtility.DisplayDialog("Selection Error", "Please select a pin parent folder first.", "OK");
            return;
        }

        Vector3 templateMeshLocalPos = new Vector3(0f, -5.9f, -2.05f); //

        CustomColliderDef[] relativeBottomDefs = new CustomColliderDef[]
        {
            new CustomColliderDef { name = "PinCollider_Wing_0",   position = new Vector3(0.059f, -5.684f, -2.461f) - templateMeshLocalPos, rotation = new Vector3(-10f, 3f, -13.5f) }, //
            new CustomColliderDef { name = "PinCollider_Indent_1", position = new Vector3(-0.119f, -5.776f, -2.465f) - templateMeshLocalPos, rotation = new Vector3(-2.777f, -3.271f, 45.351f) }, //
            new CustomColliderDef { name = "PinCollider_Wing_2",   position = new Vector3(-0.209f, -5.959f, -2.477f) - templateMeshLocalPos, rotation = new Vector3(2.779f, -9.485f, 107.578f) }, //
            new CustomColliderDef { name = "PinCollider_Indent_3", position = new Vector3(-0.044f, -6.072f, -2.466f) - templateMeshLocalPos, rotation = new Vector3(4.7f, -0.8f, -195f) }, //
            new CustomColliderDef { name = "PinCollider_Wing_4",   position = new Vector3(0.15f, -6.055f, -2.465f) - templateMeshLocalPos, rotation = new Vector3(6.345f, 6.637f, -137.179f) }, //
            new CustomColliderDef { name = "PinCollider_Indent_5", position = new Vector3(0.169f, -5.856f, -2.461f) - templateMeshLocalPos, rotation = new Vector3(-1.375f, 4.725f, -73.962f) } //
        };

        Vector3 boxSize = new Vector3(0.2f, 0.01f, 0.8f);
        int fixedCount = 0;

        Undo.RegisterCompleteObjectUndo(selectedParent, "Apply Precise Rotated Pin Colliders");

        foreach (Transform child in selectedParent.transform)
        {
            if (child.name.StartsWith("Pin"))
            {
                Rigidbody rb = child.GetComponent<Rigidbody>();
                if (rb == null) rb = child.gameObject.AddComponent<Rigidbody>();
                rb.isKinematic = false; 
                rb.useGravity = true;

                // Fix for modern Unity versions (CS0618)
                rb.linearDamping = 1.0f;   // Replaces rb.drag
                rb.angularDamping = 5.0f;  // Replaces rb.angularDrag

                Collider[] oldColliders = child.GetComponents<Collider>();
                foreach (var col in oldColliders) Undo.DestroyObjectImmediate(col);

                List<GameObject> oldGeneratedChildren = new List<GameObject>();
                foreach (Transform subChild in child)
                {
                    if (subChild.name.StartsWith("PinCollider_")) oldGeneratedChildren.Add(subChild.gameObject);
                }
                foreach (GameObject oldChild in oldGeneratedChildren) Undo.DestroyObjectImmediate(oldChild);

                Transform meshInstanceChild = null;
                foreach (Transform subChild in child)
                {
                    if (subChild.name.StartsWith("MeshInstance"))
                    {
                        meshInstanceChild = subChild;
                        break; 
                    }
                }

                if (meshInstanceChild == null) continue;

                // FIX: Apply a 90-degree correction step on the local Y-axis rotation matrix to fix the perpendicular offset
                Quaternion correctedMeshRotation = meshInstanceChild.rotation * Quaternion.Euler(90f, 0f, 0f);

                foreach (var def in relativeBottomDefs)
                {
                    Vector3 worldPos = meshInstanceChild.transform.position + (correctedMeshRotation * def.position);
                    Quaternion worldRot = correctedMeshRotation * Quaternion.Euler(def.rotation);

                    CreateColliderChild(child, def.name, child.InverseTransformPoint(worldPos), (Quaternion.Inverse(child.rotation) * worldRot).eulerAngles, boxSize);
                }

                foreach (var def in relativeBottomDefs)
                {
                    Vector3 relativeMirroredPos = new Vector3(def.position.x, def.position.y, -def.position.z);
                    Vector3 worldMirroredPos = meshInstanceChild.transform.position + (correctedMeshRotation * relativeMirroredPos);
                    
                    Vector3 mirroredRotDef = new Vector3(-def.rotation.x, -def.rotation.y, def.rotation.z);
                    Quaternion worldMirroredRot = correctedMeshRotation * Quaternion.Euler(mirroredRotDef);

                    string topHalfName = def.name.Replace("PinCollider_", "PinCollider_Top_");
                    CreateColliderChild(child, topHalfName, child.InverseTransformPoint(worldMirroredPos), (Quaternion.Inverse(child.rotation) * worldMirroredRot).eulerAngles, boxSize);
                }

                fixedCount++;
            }
        }

        EditorUtility.SetDirty(selectedParent);
        EditorUtility.DisplayDialog("Success!", $"Successfully oriented {fixedCount} pins perfectly parallel to their assets!", "Awesome");
    }

    private static void CreateColliderChild(Transform parent, string name, Vector3 localPos, Vector3 localRot, Vector3 size)
    {
        GameObject colHolder = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(colHolder, "Create Precise Wall");
        colHolder.transform.SetParent(parent);
        colHolder.transform.localPosition = localPos;
        colHolder.transform.localRotation = Quaternion.Euler(localRot);
        colHolder.AddComponent<BoxCollider>().size = size;
    }
}