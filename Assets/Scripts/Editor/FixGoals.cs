using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class FixGoals : EditorWindow
{
    [MenuItem("Tools/RoboSim/Field/Rebuild Goal Colliders", false, 2)]
    public static void ShowWindow()
    {
        GetWindow<FixGoals>("Goal Fixer");
    }

    private void OnGUI()
    {
        GUILayout.Label("VEX Goal Collider Generator & Clean-up", EditorStyles.boldLabel);
        GUILayout.Label("Select an individual goal or an entire parent folder.", EditorStyles.miniLabel);
        GUILayout.Space(10);

        // Alliance parameters modified to generate both the upper sloped walls AND the lower short flat rim
        if (GUILayout.Button("Spawn Alliance Goal Setup (Short)", GUILayout.Height(40)))
        {
            ProcessSelection(
                assemblyRotation: new Vector3(-90f, 0f, 0f), 
                outerCardinalRadius: 0.45f, outerDiagonalRadius: 0.6f, 
                outerHeight: 0.75f, outerZShift: 0.45f, cardWidth: 0.5f, diagWidth: 0.3f,
                outerDiagTilt: -18.0f, // Upper diagonal tilt
                innerRadius: 0.25f, innerHeight: 0.83f, innerZShift: 0.405f,
                goalType: "Alliance",
                
                // NEW: Parameters dedicated strictly to your second, flat lower ring base
                hasLowerBase: true,
                lowerCardinalRadius: 0.705f, lowerDiagonalRadius: 0.785f,
                lowerHeight: 0.11f, lowerZShift: 0.04f, lowerCardWidth: 0.8f, lowerDiagWidth: 0.4f
            ); 
        }

        GUILayout.Space(5);

        // YOUR PERFECTLY TUNED EXACT NUMBERS!
        if (GUILayout.Button("Spawn Neutral Goal Setup (Standard)", GUILayout.Height(40)))
        {
            ProcessSelection(
                assemblyRotation: new Vector3(-90f, 0f, 0f), 
                outerCardinalRadius: 0.705f, outerDiagonalRadius: 0.785f, 
                outerHeight: 0.7f, outerZShift: -0.28f, cardWidth: 0.8f, diagWidth: 0.4f,
                outerDiagTilt: 0f, 
                innerRadius: 0.25f, innerHeight: 0.83f, innerZShift: 0.405f,
                goalType: "Neutral",
                
                // Unused for Neutral
                hasLowerBase: false, 0f, 0f, 0f, 0f, 0f, 0f
            );
        }

        GUILayout.Space(5);

        // Central goals scaled up significantly across all dimensions
        if (GUILayout.Button("Spawn Central Goal Setup (Large)", GUILayout.Height(40)))
        {
            ProcessSelection(
                assemblyRotation: new Vector3(-90f, 0f, 0f), 
                outerCardinalRadius: 0.705f, outerDiagonalRadius: 0.785f, 
                outerHeight: 1.4f, outerZShift: -0.7f, cardWidth: .8f, diagWidth: 0.4f,
                outerDiagTilt: 0f, 
                innerRadius: 0.25f, innerHeight: 0.83f, innerZShift: 0.405f,
                goalType: "Central",
                
                // Unused for Central
                hasLowerBase: false, 0f, 0f, 0f, 0f, 0f, 0f
            );
        }
    }

    private static void ProcessSelection(
        Vector3 assemblyRotation,
        float outerCardinalRadius, float outerDiagonalRadius,
        float outerHeight, float outerZShift, float cardWidth, float diagWidth,
        float outerDiagTilt,
        float innerRadius, float innerHeight, float innerZShift,
        string goalType,
        
        // Lower base parameters passed here to preserve structure signatures
        bool hasLowerBase,
        float lowerCardinalRadius, float lowerDiagonalRadius,
        float lowerHeight, float lowerZShift, float lowerCardWidth, float lowerDiagWidth)
    {
        GameObject selection = Selection.activeGameObject;

        if (selection == null)
        {
            EditorUtility.DisplayDialog("Selection Error", "Please select a Goal object or folder first.", "OK");
            return;
        }

        List<GameObject> targetsToProcess = new List<GameObject>();
        
        bool isFolderFolder = false;
        foreach (Transform child in selection.transform)
        {
            foreach (Transform sub in child)
            {
                if (sub.name.StartsWith("MeshInstance") || sub.name.StartsWith("GoalCenter")) { isFolderFolder = true; break; }
            }
            if (isFolderFolder) break;
        }

        if (isFolderFolder)
        {
            foreach (Transform child in selection.transform) targetsToProcess.Add(child.gameObject);
        }
        else
        {
            targetsToProcess.Add(selection);
        }

        int processedCount = 0;

        foreach (GameObject targetGoal in targetsToProcess)
        {
            Undo.RegisterCompleteObjectUndo(targetGoal, $"Clean and Generate {goalType} Goal");

            MeshCollider[] existingMeshColliders = targetGoal.GetComponentsInChildren<MeshCollider>(true);
            foreach (MeshCollider mc in existingMeshColliders)
            {
                Undo.DestroyObjectImmediate(mc);
            }

            List<GameObject> oldGeneratedObjects = new List<GameObject>();
            foreach (Transform child in targetGoal.transform)
            {
                if (child.name.StartsWith("GoalWall_") || child.name.StartsWith("GoalFloor")) 
                    oldGeneratedObjects.Add(child.gameObject);
            }
            foreach (GameObject old in oldGeneratedObjects) Undo.DestroyObjectImmediate(old);

            Transform meshInstanceChild = null;
            foreach (Transform subChild in targetGoal.transform)
            {
                if (subChild.name.StartsWith("GoalCenter") || subChild.name.StartsWith("MeshInstance"))
                {
                    meshInstanceChild = subChild;
                    break; 
                }
            }

            float wallThickness = 0.01f;
            float innerTaperAngle = 6.5f; 

            Vector3 basePosition = (meshInstanceChild != null) ? meshInstanceChild.position : targetGoal.transform.position;
            Quaternion rawRotation = (meshInstanceChild != null) ? meshInstanceChild.rotation : targetGoal.transform.rotation;
            Quaternion correctedOrientation = rawRotation * Quaternion.Euler(assemblyRotation);
            
            Vector3 upAxis = correctedOrientation * Vector3.forward; 

            Vector3 outerAssemblyCenter = basePosition + (upAxis * outerZShift);
            Vector3 innerAssemblyCenter = basePosition + (upAxis * innerZShift);

            // ===================================================================
            // STEP 2A: MAIN/UPPER OUTER BUMPER WALL GENERATION
            // ===================================================================
            int outerSides = 8;
            for (int i = 0; i < outerSides; i++)
            {
                GameObject wall = new GameObject($"GoalWall_Outer_Octagon_{i}");
                Undo.RegisterCreatedObjectUndo(wall, "Create Outer Goal Wall");
                wall.transform.SetParent(targetGoal.transform);
                
                wall.transform.position = outerAssemblyCenter;
                wall.transform.rotation = correctedOrientation * Quaternion.Euler(0f, 0f, i * (360f / outerSides));

                bool isCardinal = (i % 2 == 0);
                float currentRadius = isCardinal ? outerCardinalRadius : outerDiagonalRadius;
                
                wall.transform.Translate(0f, currentRadius, 0f, Space.Self);

                if (!isCardinal && outerDiagTilt != 0f)
                {
                    wall.transform.Rotate(-outerDiagTilt, 0f, 0f, Space.Self);
                }

                BoxCollider box = wall.AddComponent<BoxCollider>();
                float currentWidth = isCardinal ? cardWidth : diagWidth;

                box.size = new Vector3(currentWidth, wallThickness, outerHeight);
            }

            // ===================================================================
            // STEP 2B: LOWER BASE RIM GENERATION (8 PIECES - UNTILTED)
            // ===================================================================
            if (hasLowerBase)
            {
                Vector3 lowerAssemblyCenter = basePosition + (upAxis * lowerZShift);
                
                for (int i = 0; i < outerSides; i++)
                {
                    GameObject wall = new GameObject($"GoalWall_Lower_Base_Octagon_{i}");
                    Undo.RegisterCreatedObjectUndo(wall, "Create Lower Base Goal Wall");
                    wall.transform.SetParent(targetGoal.transform);
                    
                    wall.transform.position = lowerAssemblyCenter;
                    wall.transform.rotation = correctedOrientation * Quaternion.Euler(0f, 0f, i * (360f / outerSides));

                    bool isCardinal = (i % 2 == 0);
                    float currentRadius = isCardinal ? lowerCardinalRadius : lowerDiagonalRadius;
                    
                    wall.transform.Translate(0f, currentRadius, 0f, Space.Self);
                    
                    // No tilt applied here—stays perfectly flat-vertical like the neutral goals!

                    BoxCollider box = wall.AddComponent<BoxCollider>();
                    float currentWidth = isCardinal ? lowerCardWidth : lowerDiagWidth;

                    box.size = new Vector3(currentWidth, wallThickness, lowerHeight);
                }
            }

            // ===================================================================
            // STEP 3: INNER POCKET GENERATION
            // ===================================================================
            int innerSides = 6;
            for (int i = 0; i < innerSides; i++)
            {
                GameObject wall = new GameObject($"GoalWall_Inner_Pocket_{i}");
                Undo.RegisterCreatedObjectUndo(wall, "Create Inner Goal Wall");
                wall.transform.SetParent(targetGoal.transform);

                wall.transform.position = innerAssemblyCenter;
                wall.transform.rotation = correctedOrientation * Quaternion.Euler(0f, 0f, i * (360f / innerSides));

                wall.transform.Translate(0f, innerRadius, 0f, Space.Self);
                wall.transform.Rotate(-innerTaperAngle, 0f, 180f, Space.Self);

                BoxCollider box = wall.AddComponent<BoxCollider>();
                float innerPanelWidth = (i % 2 == 0) ? 0.38f : 0.30f; 
                box.size = new Vector3(innerPanelWidth, wallThickness, innerHeight);
            }

            // ===================================================================
            // STEP 4: GOAL FLOOR BASE
            // ===================================================================
            GameObject floor = new GameObject("GoalFloor_Base");
            Undo.RegisterCreatedObjectUndo(floor, "Create Goal Floor");
            floor.transform.SetParent(targetGoal.transform);
            
            floor.transform.position = innerAssemblyCenter - (upAxis * (innerHeight * 0.5f));
            floor.transform.rotation = correctedOrientation;

            BoxCollider floorBox = floor.AddComponent<BoxCollider>();
            floorBox.size = new Vector3(innerRadius * 2f, innerRadius * 2f, 0.02f);

            EditorUtility.SetDirty(targetGoal);
            processedCount++;
        }

        EditorUtility.DisplayDialog("Success!", $"Processed {processedCount} target objects. Cleaned old mesh colliders and generated optimized shapes!", "Awesome");
    }
}