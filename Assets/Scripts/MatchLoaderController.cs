using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MatchLoaderController : MonoBehaviour
{
    [Header("Connected Objects")]
    [SerializeField] private Transform movingAssembly; // Drag "MoveableParts" folder here
    [SerializeField] private Transform spawnPoint;     // Drag your empty "SpawnPoint" child here
    [SerializeField] private GameObject elementPrefab; // Drag the matching Red/Blue North/South prefab asset here

    [Header("Toggle Settings")]
    [Tooltip("If checked, the loader stays up as long as an object is inside the tape trigger.")]
    [SerializeField] private bool stayUpWhileOccupied = true;

    [Header("Animation Settings")]
    [SerializeField] private float liftHeight = 2.8f;    // Preserved your custom height!
    [SerializeField] private float liftSpeed = 10f;       // Preserved your custom speed!
    [SerializeField] private float stayUpDuration = 3f;  // Default time it stays up if toggle is OFF

    private Vector3 initialLocalPosition;
    private Vector3 targetLocalPosition;
    private bool isMovingUp = false;
    private bool isMovingDown = false;
    
    // Track how many objects are currently sitting on the tape trigger
    private HashSet<Collider> objectsOnTape = new HashSet<Collider>();

    void Start()
    {
        if (movingAssembly != null)
        {
            initialLocalPosition = movingAssembly.localPosition;
            
            // Lift straight up on the local Z axis
            targetLocalPosition = initialLocalPosition + new Vector3(0, 0, liftHeight);
        }
    }

    // Called when something enters the tape trigger box
    public void OnTapeEntered(Collider other)
    {
        objectsOnTape.Add(other);

        if (!isMovingUp && movingAssembly.localPosition != targetLocalPosition)
        {
            StopAllCoroutines();
            StartCoroutine(MoveToPosition(targetLocalPosition, true));
        }
    }

    // Called when something exits the tape trigger box
    public void OnTapeExited(Collider other)
    {
        objectsOnTape.Remove(other);

        // If the toggle is active, only start moving down when the tape is completely empty
        if (stayUpWhileOccupied)
        {
            if (objectsOnTape.Count == 0 && !isMovingDown)
            {
                StopAllCoroutines();
                StartCoroutine(MoveToPosition(initialLocalPosition, false));
            }
        }
    }

    private void SpawnSingleMatchLoad()
    {
        if (spawnPoint == null || elementPrefab == null) return;

        // Spawns exactly ONE cup/pin unit at the anchor position and rotation
        GameObject spawnedPiece = Instantiate(elementPrefab, spawnPoint.position, spawnPoint.rotation);
        
        // Zero out physics velocity instantly at birth so it drops cleanly
        Rigidbody rb = spawnedPiece.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    private IEnumerator MoveToPosition(Vector3 destination, bool movingUp)
    {
        if (movingUp) { isMovingUp = true; isMovingDown = false; }
        else { isMovingDown = true; isMovingUp = false; }

        // Trigger the single item spawn right as the arm starts lifting
        if (movingUp)
        {
            SpawnSingleMatchLoad();
        }

        while (Vector3.Distance(movingAssembly.localPosition, destination) > 0.01f)
        {
            movingAssembly.localPosition = Vector3.Lerp(movingAssembly.localPosition, destination, Time.deltaTime * liftSpeed);
            yield return null;
        }
        movingAssembly.localPosition = destination;

        isMovingUp = false;
        
        // If we just reached the top and the toggle is OFF, trigger the classic automatic timer countdown
        if (movingUp && !stayUpWhileOccupied)
        {
            yield return new WaitForSeconds(stayUpDuration);
            // Make sure nobody stepped on it manually in the meantime before heading down
            if (objectsOnTape.Count == 0 || !stayUpWhileOccupied)
            {
                StartCoroutine(MoveToPosition(initialLocalPosition, false));
            }
        }
        
        if (!movingUp) isMovingDown = false;
    }
}