using System.Collections;
using UnityEngine;

/// <summary>
/// Makes the checklist canvas world-locked instead of head-locked, with a "lazy follow".
///
/// - On start, places the panel in front of the user (slightly below eye height) and
///   detaches it from the head so it stays put in the room.
/// - The panel can be moved closer/farther by pinching the grab bar under it
///   (Grabbable + HandGrabInteractable on the "GrabHandle" object). Wherever the user
///   leaves it becomes its preferred distance and height.
/// - Lazy follow: the panel stays still while the user looks around, but if they walk
///   too far away, or it stays out of view for a few seconds, it glides back in front
///   of them at their preferred distance and height. It never moves while being grabbed.
/// - The panel always turns to face the user.
/// - Recentering the view (hold the Meta button) snaps it back in front of the user.
/// </summary>
public class PanelPlacement : MonoBehaviour
{
    [Tooltip("The user's head (CenterEyeAnchor). Falls back to Camera.main.")]
    [SerializeField] private Transform _head;

    [Header("Starting position")]
    [Tooltip("How far in front of the user the panel appears, in meters.")]
    [SerializeField] private float _spawnDistance = 0.6f;
    [Tooltip("Vertical offset from eye height, in meters (negative = below eyes).")]
    [SerializeField] private float _spawnHeightOffset = -0.12f;
    [Tooltip("Sideways offset, in meters (positive = to the user's right).")]
    [SerializeField] private float _spawnSideOffset = 0f;

    [Header("Facing")]
    [SerializeField] private bool _faceUser = true;
    [Tooltip("How quickly the panel turns to face the user.")]
    [SerializeField] private float _turnSpeed = 10f;

    [Header("Distance limits (meters)")]
    [Tooltip("The panel can't be pulled closer than this.")]
    [SerializeField] private float _minDistance = 0.3f;
    [Tooltip("When following, the panel returns at the user's chosen distance, clamped to this range.")]
    [SerializeField] private Vector2 _followDistanceRange = new Vector2(0.4f, 1.0f);

    [Header("Lazy follow")]
    [Tooltip("Start following when the user is farther than this from the panel (horizontal meters).")]
    [SerializeField] private float _followWhenFartherThan = 1.5f;
    [Tooltip("Start following when the panel is more than this many degrees from where the user is facing...")]
    [SerializeField] private float _outOfViewAngle = 75f;
    [Tooltip("...for at least this many seconds.")]
    [SerializeField] private float _outOfViewSeconds = 3f;
    [Tooltip("Roughly how long the glide back takes, in seconds.")]
    [SerializeField] private float _followSmoothTime = 0.35f;
    [Tooltip("Don't follow for this long after the user has moved the panel.")]
    [SerializeField] private float _grabCooldownSeconds = 1f;

    private bool _placed;
    private bool _following;
    private float _outOfViewTimer;
    private Vector3 _followVelocity;

    // Where the user likes the panel, relative to their head
    private float _preferredDistance;
    private float _preferredHeightOffset;

    // Used to detect when something else (the grab) moved the panel
    private Vector3 _lastPositionSetByUs;
    private float _lastExternalMoveTime = -10f;
    private bool _wasMovedExternally;

    private void Start()
    {
        if (_head == null && Camera.main != null) _head = Camera.main.transform;
        _preferredDistance = _spawnDistance;
        _preferredHeightOffset = _spawnHeightOffset;

        StartCoroutine(PlaceWhenTracking());

        if (OVRManager.display != null)
            OVRManager.display.RecenteredPose += PlaceInFront;
    }

    private void OnDestroy()
    {
        if (OVRManager.display != null)
            OVRManager.display.RecenteredPose -= PlaceInFront;
    }

    /// <summary>
    /// The headset pose isn't valid for the first few frames, so wait until it is
    /// (or a short timeout) before placing the panel.
    /// </summary>
    private IEnumerator PlaceWhenTracking()
    {
        const float timeout = 2f;
        float start = Time.unscaledTime;
        while (_head != null && _head.position == Vector3.zero && Time.unscaledTime - start < timeout)
            yield return null;

        // One extra frame so the tracking pose has been applied
        yield return null;

        // Detach from the head so the panel stays where it is put
        transform.SetParent(null, true);
        PlaceInFront();
        _placed = true;
    }

    /// <summary>Moves the panel to its starting spot in front of the user.</summary>
    public void PlaceInFront()
    {
        if (_head == null) return;

        _preferredDistance = _spawnDistance;
        _preferredHeightOffset = _spawnHeightOffset;

        Vector3 forward = FlatForward();
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        transform.position = _head.position
                             + forward * _spawnDistance
                             + Vector3.up * _spawnHeightOffset
                             + right * _spawnSideOffset;
        transform.rotation = FacingRotation();

        _following = false;
        _outOfViewTimer = 0f;
        _lastPositionSetByUs = transform.position;
    }

    private void LateUpdate()
    {
        if (!_placed || _head == null) return;

        TrackExternalMoves();

        bool recentlyGrabbed = Time.unscaledTime - _lastExternalMoveTime < _grabCooldownSeconds;

        if (recentlyGrabbed)
        {
            // The user is placing the panel: don't fight them
            _following = false;
            _outOfViewTimer = 0f;
        }
        else
        {
            UpdateFollow();
        }

        // Keep the panel from being pulled into the user's face
        Vector3 offset = transform.position - _head.position;
        if (offset.magnitude < _minDistance && offset.sqrMagnitude > 0.0001f)
            transform.position = _head.position + offset.normalized * _minDistance;

        if (_faceUser)
        {
            float t = 1f - Mathf.Exp(-_turnSpeed * Time.deltaTime);
            transform.rotation = Quaternion.Slerp(transform.rotation, FacingRotation(), t);
        }

        _lastPositionSetByUs = transform.position;
    }

    /// <summary>
    /// If the panel moved since our last frame and we didn't move it, the user is
    /// grabbing it. When they stop, remember where they put it.
    /// </summary>
    private void TrackExternalMoves()
    {
        bool movedExternally = (transform.position - _lastPositionSetByUs).sqrMagnitude > 1e-8f;

        if (movedExternally)
        {
            _lastExternalMoveTime = Time.unscaledTime;
            _wasMovedExternally = true;
        }
        else if (_wasMovedExternally)
        {
            // User just let go: their placement becomes the new preferred distance/height
            _wasMovedExternally = false;
            Vector3 offset = transform.position - _head.position;
            Vector3 flat = Vector3.ProjectOnPlane(offset, Vector3.up);
            _preferredDistance = Mathf.Clamp(flat.magnitude, _followDistanceRange.x, _followDistanceRange.y);
            _preferredHeightOffset = Mathf.Clamp(offset.y, -0.5f, 0.3f);
        }
    }

    private void UpdateFollow()
    {
        Vector3 toPanel = transform.position - _head.position;
        Vector3 flatToPanel = Vector3.ProjectOnPlane(toPanel, Vector3.up);
        float flatDistance = flatToPanel.magnitude;
        float angle = flatDistance > 0.001f ? Vector3.Angle(FlatForward(), flatToPanel) : 0f;

        if (!_following)
        {
            if (angle > _outOfViewAngle) _outOfViewTimer += Time.deltaTime;
            else _outOfViewTimer = 0f;

            if (flatDistance > _followWhenFartherThan || _outOfViewTimer > _outOfViewSeconds)
            {
                _following = true;
                _followVelocity = Vector3.zero;
            }
        }

        if (_following)
        {
            Vector3 target = _head.position
                             + FlatForward() * _preferredDistance
                             + Vector3.up * _preferredHeightOffset;

            transform.position = Vector3.SmoothDamp(transform.position, target, ref _followVelocity, _followSmoothTime);

            if ((transform.position - target).sqrMagnitude < 0.02f * 0.02f)
            {
                _following = false;
                _outOfViewTimer = 0f;
            }
        }
    }

    private Vector3 FlatForward()
    {
        Vector3 forward = Vector3.ProjectOnPlane(_head.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.001f) forward = _head.forward;
        return forward.normalized;
    }

    /// <summary>World-space canvases face the user when their forward points away from the head.</summary>
    private Quaternion FacingRotation()
    {
        Vector3 away = transform.position - _head.position;
        if (away.sqrMagnitude < 0.0001f) away = _head.forward;
        return Quaternion.LookRotation(away.normalized, Vector3.up);
    }
}
