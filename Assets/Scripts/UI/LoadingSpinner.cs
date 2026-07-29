using UnityEngine;

// Spins a UI element continuously while its GameObject is active — a simple "busy" indicator for
// the loading overlay. Uses unscaled time so it keeps turning even if the game is paused.
public class LoadingSpinner : MonoBehaviour
{
    [Tooltip("Rotation speed in degrees per second (negative spins clockwise).")]
    public float degreesPerSecond = -220f;

    void Update()
    {
        transform.Rotate(0f, 0f, degreesPerSecond * Time.unscaledDeltaTime);
    }
}
