using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class FixRollers : EditorWindow
{
    [MenuItem("Tools/RoboSim/Field/Rig Rollers (Hinge Joints)", false, 3)]
    public static void ShowWindow()
    {
        GetWindow<FixRollers>("Roller Fixer");
    }

    private void OnGUI()
    {
        GUILayout.Label("VEX Dynamic Roller Rigging Tool", EditorStyles.boldLabel);
        GUILayout.Label("Select individual Rollers or the entire parent folder.", EditorStyles.miniLabel);
        GUILayout.Space(10);

        if (GUILayout.Button("Assemble Selected Rollers", GUILayout.Height(40)))
        {
            ProcessSelection();
        }
    }

    private static void ProcessSelection()
    {
        GameObject selection = Selection.activeGameObject;

        if (selection == null)
        {
            EditorUtility.DisplayDialog("Selection Error", "Please select a Roller object or folder first.", "OK");
            return;
        }

        // ===================================================================
        //   MANUAL CONTROLS: ADJUST INDEPENDENT ROLLER DIMENSIONS
        // ===================================================================
        float rollerRadius = 0.15f;     // Extrusion radius from center axle
        float rollerLength = 6f;        // Horizontal length along field parallel alignment
        float panelWidth = 0.5f;        // Face thickness span for each of the 3 panels
        float wallThickness = 0.04f;    // Thickness of the individual box panels

        // If your 3-sided mesh faces ever need a universal slight rotational nudge, 
        // you can change this offset value here. Default is clean zero.
        Vector3 assemblyRotationOffset = new Vector3(0f, 0f, 0f); 
        // ===================================================================

        List<GameObject> targetsToProcess = new List<GameObject>();
        
        // Smart Selection Handler: Checks if a root folder is highlighted
        bool isFolder = false;
        foreach (Transform child in selection.transform)
        {
            if (child.name.StartsWith("RollerFrame") || child.name.Contains("Roller")) 
            { 
                isFolder = true; 
                break; 
            }
        }

        if (isFolder && !selection.name.StartsWith("RollerNorth") && !selection.name.StartsWith("RollerSouth") && !selection.name.StartsWith("RollerEast") && !selection.name.StartsWith("RollerWest"))
        {
            foreach (Transform child in selection.transform) targetsToProcess.Add(child.gameObject);
        }
        else
        {
            // If a nested child panel or component was clicked, climb out to find the true directional root
            GameObject trueTarget = selection;
            while (trueTarget.transform.parent != null && !trueTarget.name.StartsWith("RollerNorth") && !trueTarget.name.StartsWith("RollerSouth") && !trueTarget.name.StartsWith("RollerEast") && !trueTarget.name.StartsWith("RollerWest"))
            {
                trueTarget = trueTarget.transform.parent.gameObject;
            }
            targetsToProcess.Add(trueTarget);
        }

        int processedCount = 0;

        foreach (GameObject rollerParent in targetsToProcess)
        {
            Undo.RegisterCompleteObjectUndo(rollerParent, "Rig Roller Physics");

            Transform faceTarget = null;
            foreach (Transform child in rollerParent.transform)
            {
                if (child.name.StartsWith("RollerFace")) faceTarget = child; //
            }

            if (faceTarget == null) continue;

            // 1. Clear old runtime components smoothly
            RollerSnap oldSnapScript = faceTarget.GetComponent<RollerSnap>();
            if (oldSnapScript != null) Undo.DestroyObjectImmediate(oldSnapScript);

            // 2. Strip messy joints and rigidbodies to prevent physics binding
            Rigidbody[] existingBodies = rollerParent.GetComponentsInChildren<Rigidbody>(true);
            foreach (Rigidbody rb in existingBodies) Undo.DestroyObjectImmediate(rb);

            Joint[] existingJoints = rollerParent.GetComponentsInChildren<Joint>(true);
            foreach (Joint j in existingJoints) Undo.DestroyObjectImmediate(j);

            // 3. CLEANUP BUG FIX: Find and vaporize any old master pivot containers to completely avoid duplicates
            Transform oldPivot = faceTarget.Find("Roller_Collider_Pivot");
            if (oldPivot != null) Undo.DestroyObjectImmediate(oldPivot.gameObject);

            // Also clean up loose old-style nested face panels if they exist
            List<GameObject> loosePanels = new List<GameObject>();
            foreach (Transform child in faceTarget)
            {
                if (child.name.StartsWith("RollerFace_Panel_")) loosePanels.Add(child.gameObject);
            }
            foreach (GameObject lp in loosePanels) Undo.DestroyObjectImmediate(lp);

            // 4. Strip any leftover stray colliders
            Collider[] existingColliders = rollerParent.GetComponentsInChildren<Collider>(true);
            foreach (Collider c in existingColliders) Undo.DestroyObjectImmediate(c);

            // 5. Track down visual MeshInstance baseline to extract precise local coordinates
            Transform meshInstanceChild = null;
            foreach (Transform subChild in faceTarget)
            {
                if (subChild.name.StartsWith("MeshInstance"))
                {
                    meshInstanceChild = subChild;
                    break; 
                }
            }

            Vector3 targetPivotLocalPos = (meshInstanceChild != null) ? meshInstanceChild.localPosition : Vector3.zero;
            Quaternion targetPivotLocalRot = (meshInstanceChild != null) ? meshInstanceChild.localRotation : Quaternion.identity;

            // ===================================================================
            // CONFIGURATION: GENERATE SYNCHRONIZED REF PIVOT CONTAINER
            // ===================================================================
            GameObject colliderPivot = new GameObject("Roller_Collider_Pivot");
            Undo.RegisterCreatedObjectUndo(colliderPivot, "Create Roller Collider Pivot");
            colliderPivot.transform.SetParent(faceTarget);
            
            // Automatically adapts your green wireframe boxes perfectly to match the rotated meshes
            colliderPivot.transform.localPosition = targetPivotLocalPos;
            colliderPivot.transform.localRotation = targetPivotLocalRot * Quaternion.Euler(assemblyRotationOffset);

            // ===================================================================
            // STEP 1: GENERATE DYNAMIC 3-SIDED COLLIDER PANELS
            // ===================================================================
            int sides = 3;

            for (int i = 0; i < sides; i++)
            {
                GameObject triFace = new GameObject($"RollerFace_Panel_{i}");
                Undo.RegisterCreatedObjectUndo(triFace, "Create Roller Face Panel");
                
                triFace.transform.SetParent(colliderPivot.transform);
                triFace.transform.localPosition = Vector3.zero;
                triFace.transform.localRotation = Quaternion.Euler(i * (360f / sides), 0f, 0f);

                triFace.transform.Translate(0f, rollerRadius, 0f, Space.Self);

                BoxCollider faceBox = triFace.AddComponent<BoxCollider>();
                faceBox.size = new Vector3(rollerLength, wallThickness, panelWidth);
            }

            // ===================================================================
            // STEP 2: RIGIDBODY & CRITICAL JOINT FIX
            // ===================================================================
            Rigidbody spinnerRb = faceTarget.gameObject.AddComponent<Rigidbody>();
            spinnerRb.isKinematic = false; 
            spinnerRb.useGravity = false;  
            spinnerRb.angularDamping = 0.5f;  

            HingeJoint hinge = faceTarget.gameObject.AddComponent<HingeJoint>();
            
            // Lock anchor precisely down the local middle center line of the mesh
            hinge.anchor = targetPivotLocalPos;
            
            // Forces a pristine, perfectly un-skewed local spin vector parallel to the axle
            hinge.axis = new Vector3(1f, 0f, 0f); 

            // ===================================================================
            // STEP 3: ATTACH ROTATIONAL DETENT SNAP SCRIPT
            // ===================================================================
            faceTarget.gameObject.AddComponent<RollerSnap>();

            EditorUtility.SetDirty(rollerParent);
            processedCount++;
        }

        EditorUtility.DisplayDialog("Success!", $"Perfect structure and clean local physics set up across {processedCount} Rollers!", "Awesome");
    }
}
