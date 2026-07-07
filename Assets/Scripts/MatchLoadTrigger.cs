using UnityEngine;

public class MatchLoadTrigger : MonoBehaviour
{
    [Header("Link to Main Match Loader")]
    [SerializeField] private MatchLoaderController mainController;

    private void OnTriggerEnter(Collider other)
    {
        if (mainController != null)
        {
            mainController.OnTapeEntered(other);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (mainController != null)
        {
            mainController.OnTapeExited(other);
        }
    }
}