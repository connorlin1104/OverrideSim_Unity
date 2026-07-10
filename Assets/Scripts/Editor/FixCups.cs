using UnityEngine; 
using UnityEditor; 
using System.Collections.Generic; 

public class FixCups : EditorWindow 
{ 
    [MenuItem("Tools/RoboSim/Game Pieces/Rebuild Cup Colliders", false, 1)]
    public static void CleanAndOptimizeCups() 
    { 
        GameObject selectedParent = Selection.activeGameObject; 

        if (selectedParent == null) 
        { 
            EditorUtility.DisplayDialog("Selection Error", "Please select the 'Cups' parent object first.", "OK"); 
            return; 
        } 

        // =================================================================== 
        // MANUAL CONTROLS: ADJUST HEIGHT, GAP, AND MASTER ROTATION 
        // =================================================================== 
        
        // 1. MASTER ASSEMBLY ROTATION 
        // This rotates the ENTIRE collider system together around the cup's pivot point. 
        // Change these values to find which axis snaps it to the cup length! 
        // Try configurations like: (90f, 0f, 0f), (0f, 90f, 0f), or (0f, 0f, 90f) 
        Vector3 assemblyRotationOffset = new Vector3(90f, 0f, 0f); 

        // 2. OVERALL HEIGHT SHIFT (Moves the whole assembly along its corrected axis) 
        float globalVerticalShift = -0.45f; 

        // 3. EXTRA BOTTOM GAP (Pushes the bottom row further down away from the top) 
        float extraBottomSeparation = 0.80f; 

        // 4. COLLIDER BOX DIMENSIONS 
        float cupHeight = 0.8f; 
        float wallThickness = 0.01f; 

        // 5. RADIAL FIT 
        float outerRadius = 0.25f; 
        float innerRadius = 0.25f; 
        float taperAngle = 6.5f; 

        // =================================================================== 

        int fixedCount = 0; 
        Undo.RegisterCompleteObjectUndo(selectedParent, "Fix Cups Spacing and Orientation"); 

        foreach (Transform child in selectedParent.transform) 
        { 
            Collider[] oldColliders = child.GetComponents<Collider>(); 
            foreach (var col in oldColliders) Undo.DestroyObjectImmediate(col); 

            List<GameObject> oldGeneratedChildren = new List<GameObject>(); 
            foreach (Transform subChild in child) 
            { 
                if (subChild.name.StartsWith("CupWall_")) oldGeneratedChildren.Add(subChild.gameObject); 
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

            Vector3 meshBaseCenter = meshInstanceChild.position; 
            
            // FIX: Calculate a master corrected orientation using your manual offset variable 
            Quaternion correctedOrientation = meshInstanceChild.rotation * Quaternion.Euler(assemblyRotationOffset); 

            // Calculate direction vectors using the corrected unit matrix 
            Vector3 worldUpDirection = correctedOrientation * Vector3.forward; 

            // Apply placement offsets along the synchronized axis 
            Vector3 worldTopCenter = meshBaseCenter + (worldUpDirection * globalVerticalShift); 
            Vector3 worldBottomCenter = worldTopCenter - (worldUpDirection * extraBottomSeparation); 

            int sides = 6; 

            // 1. GENERATE CUP TOP 
            for (int i = 0; i < sides; i++) 
            { 
                bool isWing = (i % 2 == 0); 
                string sideType = isWing ? "Outer" : "Inner"; 
                
                GameObject colHolder = new GameObject($"CupWall_Top_{sideType}_{i}"); 
                Undo.RegisterCreatedObjectUndo(colHolder, "Create Cup Top Wall"); 
                
                colHolder.transform.SetParent(child); 
                colHolder.transform.position = worldTopCenter; 
                colHolder.transform.rotation = correctedOrientation * Quaternion.Euler(0f, 0f, i * (360f / sides)); 

                float currentRadius = isWing ? outerRadius : innerRadius; 
                float currentWidth = isWing ? 0.38f : 0.30f; 

                colHolder.transform.Translate(0f, currentRadius, 0f, Space.Self); 
                colHolder.transform.Rotate(-taperAngle, 0f, 180f, Space.Self); 

                BoxCollider box = colHolder.AddComponent<BoxCollider>(); 
                box.size = new Vector3(currentWidth, wallThickness, cupHeight); 
            } 

            // 2. GENERATE CUP BOTTOM 
            for (int i = 0; i < sides; i++) 
            { 
                bool isWing = (i % 2 == 0); 
                string sideType = isWing ? "Outer" : "Inner"; 
                
                GameObject colHolder = new GameObject($"CupWall_Bottom_{sideType}_{i}"); 
                Undo.RegisterCreatedObjectUndo(colHolder, "Create Cup Bottom Wall"); 
                
                colHolder.transform.SetParent(child); 
                colHolder.transform.position = worldBottomCenter; 
                colHolder.transform.rotation = correctedOrientation * Quaternion.Euler(0f, 0f, i * (360f / sides)); 

                float currentRadius = isWing ? outerRadius : innerRadius; 
                float currentWidth = isWing ? 0.38f : 0.30f; 

                colHolder.transform.Translate(0f, currentRadius, 0f, Space.Self); 
                colHolder.transform.Rotate(taperAngle, 0f, 180f, Space.Self); 

                BoxCollider box = colHolder.AddComponent<BoxCollider>(); 
                box.size = new Vector3(currentWidth, wallThickness, cupHeight); 
            } 

            fixedCount++; 
        } 

        EditorUtility.SetDirty(selectedParent); 
        EditorUtility.DisplayDialog("Success!", $"Successfully processed {fixedCount} cups!", "Awesome"); 
    } 
}