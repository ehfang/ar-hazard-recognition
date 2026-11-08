using UnityEngine;

/// <summary>
/// Previously toggled the whole canvas open/closed with a middle-finger + thumb pinch.
/// That gesture has been removed: the panel is now collapsed/expanded with the
/// on-panel Minimize button and the status slip (see PanelMinimizer).
/// This component now only makes sure the canvas is visible at startup.
/// (Class renamed from CanvasToggleA to ContentToggler so it matches its file name;
/// existing scene references are kept because the script GUID is unchanged.)
/// </summary>
public class ContentToggler : MonoBehaviour
{
    public Camera sceneCamera;   // kept for reference; no longer used
    public GameObject canvas;    // canvas root

    void Awake()
    {
        if (canvas) canvas.SetActive(true);
    }
}
