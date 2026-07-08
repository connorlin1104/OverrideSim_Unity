using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MatchLoaderController : MonoBehaviour
{
    [Header("Connected Objects")]
    [SerializeField] private Transform movingAssembly; // Drag "MoveableParts" folder here
    [SerializeField] private Transform spawnPoint;     // Drag your empty "SpawnPoint" child here
    [SerializeField] private GameObject elementPrefab; // Drag the matching Red/Blue North/South prefab asset here

    [Header("Trigger Settings")]
    [Tooltip("Only colliders whose Rigidbody has this tag drive the loader. Set your Robot object's tag to match so game pieces can't trigger it.")]
    [SerializeField] private string robotTag = "Player";
    [Tooltip("A spawned piece counts as 'taken' once its body moves this far from the spawn point, which allows the next spawn.")]
    [SerializeField] private float clearRadius = 1.5f;

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

    // The robot has many child colliders, so track which of them are inside the tape and
    // treat "robot present" as count > 0. This de-dupes the flicker of colliders entering
    // and leaving so we only react to the robot fully arriving / fully leaving.
    private readonly HashSet<Collider> robotColliders = new HashSet<Collider>();

    // Latch: consumed when we spawn, re-armed only when the robot fully leaves the tape.
    // This enforces "the robot must leave the trigger to get another one."
    private bool readyToSpawn = true;

    // The one live piece this loader has spawned (enforces the strict 1-item limit).
    private GameObject currentItem;

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
        if (!IsRobot(other)) return; // ignore game pieces and anything that isn't the robot

        bool wasEmpty = robotColliders.Count == 0;
        robotColliders.Add(other);

        // Spawn at most one piece per robot visit, and only once the previous piece has
        // been carried out of the loader.
        if (readyToSpawn && !LoaderOccupied())
        {
            SpawnSingleMatchLoad();
            readyToSpawn = false;
        }

        // Raise the arm when the robot first arrives.
        if (wasEmpty && !isMovingUp && movingAssembly.localPosition != targetLocalPosition)
        {
            StopAllCoroutines();
            StartCoroutine(MoveToPosition(targetLocalPosition, true));
        }
    }

    // Called when something exits the tape trigger box
    public void OnTapeExited(Collider other)
    {
        if (!IsRobot(other)) return;

        robotColliders.Remove(other);
        if (robotColliders.Count > 0) return; // part of the robot is still on the tape

        // Robot has fully left the trigger: re-arm so the next visit can spawn again.
        readyToSpawn = true;

        // If the toggle is active, drop the arm now that the tape is empty.
        if (stayUpWhileOccupied && !isMovingDown)
        {
            StopAllCoroutines();
            StartCoroutine(MoveToPosition(initialLocalPosition, false));
        }
    }

    // A collider belongs to the robot if its owning Rigidbody carries the robot tag.
    private bool IsRobot(Collider other)
    {
        Rigidbody body = other.attachedRigidbody;
        return body != null && body.CompareTag(robotTag);
    }

    // True while our spawned piece is still sitting in the loader (not yet carried away).
    private bool LoaderOccupied()
    {
        if (currentItem == null || spawnPoint == null) return false; // destroyed / never spawned

        // The prefab root has no Rigidbody; measure the piece from its physical child body.
        Rigidbody itemBody = currentItem.GetComponentInChildren<Rigidbody>();
        Vector3 pos = itemBody != null ? itemBody.position : currentItem.transform.position;
        return Vector3.Distance(pos, spawnPoint.position) <= clearRadius;
    }

    private void SpawnSingleMatchLoad()
    {
        if (spawnPoint == null || elementPrefab == null) return;

        // Spawns exactly ONE cup/pin unit at the anchor position and rotation
        currentItem = Instantiate(elementPrefab, spawnPoint.position, spawnPoint.rotation);

        // Zero out physics velocity instantly at birth so it drops cleanly. The prefab root
        // has no Rigidbody, so reset the child bodies (cup + pin) instead.
        foreach (Rigidbody rb in currentItem.GetComponentsInChildren<Rigidbody>())
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    private IEnumerator MoveToPosition(Vector3 destination, bool movingUp)
    {
        if (movingUp) { isMovingUp = true; isMovingDown = false; }
        else { isMovingDown = true; isMovingUp = false; }

        while (Vector3.Distance(movingAssembly.localPosition, destination) > 0.01f)
        {
            movingAssembly.localPosition = Vector3.Lerp(movingAssembly.localPosition, destination, Time.deltaTime * liftSpeed);
            yield return null;
        }
        movingAssembly.localPosition = destination;

        isMovingUp = false;

        // If we just reached the top and the toggle is OFF, run the classic timed countdown.
        if (movingUp && !stayUpWhileOccupied)
        {
            yield return new WaitForSeconds(stayUpDuration);
            // Make sure the robot didn't drive back on in the meantime before heading down.
            if (robotColliders.Count == 0)
            {
                StartCoroutine(MoveToPosition(initialLocalPosition, false));
            }
        }

        if (!movingUp) isMovingDown = false;
    }
}
