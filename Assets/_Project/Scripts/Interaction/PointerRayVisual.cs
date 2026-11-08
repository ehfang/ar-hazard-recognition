using System.Collections.Generic;
using Oculus.Interaction;
using UnityEngine;

/// <summary>
/// Draws a soft, semi-transparent ray from each hand/controller so people can see
/// where they're pointing for distance clicks.
///
/// It follows the Interaction SDK's own RayInteractors (from the OVRInteractionComprehensive
/// rig), so the line always matches what the ray will actually click:
///   - Pointing at nothing: a short ray that fades out.
///   - Pointing at the panel/buttons: the ray reaches the panel and gets brighter.
///   - Pinching / pulling the trigger on something: the ray turns orange.
///   - Hand right up against the panel (poking): the ray is hidden.
///
/// Note: not currently attached to anything in the main scene; kept for re-use.
/// </summary>
public class PointerRayVisual : MonoBehaviour
{
    [Header("Look")]
    [SerializeField] private float _startWidth = 0.004f;
    [SerializeField] private float _endWidth = 0.0015f;
    [SerializeField] private Color _idleColor = new Color(1f, 1f, 1f, 0.12f);
    [SerializeField] private Color _hoverColor = new Color(1f, 1f, 1f, 0.3f);
    [SerializeField] private Color _selectColor = new Color(1f, 0.6f, 0.25f, 0.5f);

    [Header("Length")]
    [Tooltip("Length of the ray when it isn't pointing at anything, in meters.")]
    [SerializeField] private float _idleLength = 0.5f;
    [Tooltip("Hide the ray when the hit point is closer than this (e.g. while poking).")]
    [SerializeField] private float _minVisibleDistance = 0.12f;

    private class Line
    {
        public RayInteractor interactor;
        public LineRenderer renderer;
    }

    private readonly List<Line> _lines = new List<Line>();

    /// <summary>
    /// Adds itself to the scene automatically on startup, so no scene setup is needed.
    /// (If one is already placed in the scene, that one is used instead.)
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoCreate()
    {
        if (FindAnyObjectByType<PointerRayVisual>() == null)
            new GameObject("PointerRayVisual").AddComponent<PointerRayVisual>();
    }
    private Material _material;
    private float _nextScanTime;

    private void Start()
    {
        // Sprites/Default is unlit, transparent, uses vertex colors, and is always
        // included in builds, so it works for a fading line without a custom material.
        _material = new Material(Shader.Find("Sprites/Default"));
        ScanForInteractors();
    }

    private void OnDestroy()
    {
        foreach (var l in _lines)
            if (l.renderer != null) Destroy(l.renderer.gameObject);
        if (_material != null) Destroy(_material);
    }

    /// <summary>Find the rig's ray interactors (hands and controllers).</summary>
    private void ScanForInteractors()
    {
        _nextScanTime = Time.unscaledTime + 2f;
        var found = FindObjectsByType<RayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var interactor in found)
        {
            if (_lines.Exists(l => l.interactor == interactor)) continue;

            var go = new GameObject($"PointerRay ({interactor.name})");
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.numCapVertices = 4;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.sharedMaterial = _material;
            lr.startWidth = _startWidth;
            lr.endWidth = _endWidth;
            lr.enabled = false;

            _lines.Add(new Line { interactor = interactor, renderer = lr });
        }
    }

    private void LateUpdate()
    {
        if (Time.unscaledTime >= _nextScanTime) ScanForInteractors();

        foreach (var line in _lines)
        {
            var lr = line.renderer;
            var ray = line.interactor;
            if (lr == null) continue;

            if (ray == null || !ray.isActiveAndEnabled || ray.State == InteractorState.Disabled)
            {
                lr.enabled = false;
                continue;
            }

            Vector3 origin = ray.Origin;
            Vector3 end;
            Color color;

            bool hasHit = ray.CollisionInfo.HasValue &&
                          (ray.State == InteractorState.Hover || ray.State == InteractorState.Select);

            if (hasHit)
            {
                end = ray.End;
                if ((end - origin).magnitude < _minVisibleDistance)
                {
                    lr.enabled = false;   // hand is basically touching the panel: poking, not pointing
                    continue;
                }
                color = ray.State == InteractorState.Select ? _selectColor : _hoverColor;
            }
            else
            {
                Vector3 dir = ray.Ray.direction.sqrMagnitude > 0f ? ray.Ray.direction : ray.Rotation * Vector3.forward;
                end = origin + dir.normalized * _idleLength;
                color = _idleColor;
            }

            lr.enabled = true;
            lr.SetPosition(0, origin);
            lr.SetPosition(1, end);

            // Solid near the hand, fading toward the end (fully transparent if not hitting anything)
            Color endColor = color;
            endColor.a = hasHit ? color.a * 0.6f : 0f;
            lr.startColor = color;
            lr.endColor = endColor;
        }
    }
}
