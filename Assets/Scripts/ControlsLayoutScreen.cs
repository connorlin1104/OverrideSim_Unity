using UnityEngine;

// The home screen's "Edit Control Layout" sub-screen: a scaled preview of the field controls
// with one draggable proxy per control group. Dragging a proxy saves that control's position
// offset (ControlsLayoutSettings); the field scene's ControlsAppearance applies the offsets when
// it loads. Reset returns every control to its authored position.
//
// Same open/close pattern as ControllerConfigScreen — HomeScreenController shows/hides it. Built
// and fully wired (panel, six proxies, Reset/Back) by the Build Home Screen tool.
public class ControlsLayoutScreen : MonoBehaviour
{
    [SerializeField] private GameObject panel;
    [SerializeField] private DraggableControlProxy[] proxies;

    // Called by HomeScreenController when the player opens the layout editor.
    public void Open()
    {
        RefreshProxies();
        if (panel != null) panel.SetActive(true);
    }

    public void Close()
    {
        if (panel != null) panel.SetActive(false);
    }

    // Wired to the Reset button by the Build Home Screen tool.
    public void OnResetPressed()
    {
        ControlsLayoutSettings.Reset();
        RefreshProxies();
    }

    private void RefreshProxies()
    {
        if (proxies == null) return;
        foreach (DraggableControlProxy proxy in proxies)
        {
            if (proxy != null) proxy.ApplySavedOffset();
        }
    }
}
