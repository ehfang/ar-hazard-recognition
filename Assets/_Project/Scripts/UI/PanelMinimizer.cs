using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Collapses the checklist panel into a small status slip and expands it again.
///
/// Wiring (already set up in "camera API test" scene):
///   - MinimizeButton (top-right of ChecklistPanel) -> InteractableUnityEventWrapper.WhenUnselect -> Minimize()
///   - StatusSlip                                    -> InteractableUnityEventWrapper.WhenUnselect -> Expand()
///
/// The slip shows the current scan status from ScanButtonHandler
/// (no scan yet / scanning / checklist ready / scan failed).
/// </summary>
public class PanelMinimizer : MonoBehaviour
{
    [Header("Objects shown only when the panel is expanded")]
    [Tooltip("ChecklistPanel and ScanButton. The Minimize button is a child of ChecklistPanel, so it hides with it.")]
    [SerializeField] private GameObject[] _fullPanelObjects;

    [Header("Minimized status slip")]
    [SerializeField] private GameObject _statusSlip;
    [SerializeField] private Image _slipBackground;
    [SerializeField] private TextMeshProUGUI _slipText;

    [Header("Scan status source")]
    [SerializeField] private ScanButtonHandler _scanButton;

    [Header("Behaviour")]
    [SerializeField] private bool _startMinimized = false;
    [Tooltip("Ignore presses for this long after switching, so one poke can't minimize and expand at once.")]
    [SerializeField] private float _debounceSeconds = 0.5f;

    [Header("Slip text")]
    [SerializeField] private string _idleText = "No scan yet";
    [SerializeField] private string _scanningText = "Scanning for hazards...";
    [SerializeField] private string _readyText = "Checklist ready";
    [SerializeField] private string _failedText = "Scan failed";
    [SerializeField] private string _tapHint = "Tap to open";

    [Header("Slip colors")]
    [SerializeField] private Color _idleColor = new Color(0.30f, 0.30f, 0.30f, 0.92f);     // Grey
    [SerializeField] private Color _scanningColor = new Color(0.20f, 0.60f, 1.00f, 0.95f); // Blue (matches scan button)
    [SerializeField] private Color _readyColor = new Color(0.18f, 0.65f, 0.33f, 0.95f);    // Green
    [SerializeField] private Color _failedColor = new Color(0.85f, 0.25f, 0.20f, 0.95f);   // Red
    [SerializeField] private Color _slipTextColor = Color.white;

    private bool _isMinimized;
    private float _lastSwitchTime = -10f;

    public bool IsMinimized => _isMinimized;

    private void Awake()
    {
        if (_statusSlip != null)
        {
            if (_slipBackground == null) _slipBackground = _statusSlip.GetComponent<Image>();
            if (_slipText == null) _slipText = _statusSlip.GetComponentInChildren<TextMeshProUGUI>(true);
        }

        if (_slipText != null)
        {
            // One fixed size for every state; longer messages wrap to a second line
            // instead of shrinking, so the slip text never changes size.
            _slipText.enableAutoSizing = false;
        }
    }

    private void OnEnable()
    {
        if (_scanButton != null) _scanButton.StatusChanged += OnStatusChanged;
    }

    private void OnDisable()
    {
        if (_scanButton != null) _scanButton.StatusChanged -= OnStatusChanged;
    }

    private void Start()
    {
        ApplyState(_startMinimized);
        RefreshSlip();
    }

    /// <summary>Hide the full panel and show the status slip.</summary>
    public void Minimize()
    {
        if (_isMinimized || IsDebouncing()) return;
        Debug.Log("[PanelMinimizer] Minimize");
        ApplyState(true);
    }

    /// <summary>Show the full panel and hide the status slip.</summary>
    public void Expand()
    {
        if (!_isMinimized || IsDebouncing()) return;
        Debug.Log("[PanelMinimizer] Expand");
        ApplyState(false);
    }

    public void Toggle()
    {
        if (_isMinimized) Expand(); else Minimize();
    }

    private bool IsDebouncing() => Time.unscaledTime - _lastSwitchTime < _debounceSeconds;

    private void ApplyState(bool minimized)
    {
        _isMinimized = minimized;
        _lastSwitchTime = Time.unscaledTime;

        if (_fullPanelObjects != null)
        {
            foreach (var go in _fullPanelObjects)
                if (go != null) go.SetActive(!minimized);
        }

        if (_statusSlip != null) _statusSlip.SetActive(minimized);

        RefreshSlip();
    }

    private void OnStatusChanged(ScanButtonHandler.ScanStatus status) => RefreshSlip();

    private int _lastDots = -1;

    /// <summary>Animates the dots on the slip while a scan is in progress.</summary>
    private void Update()
    {
        if (!_isMinimized || _scanButton == null || _scanButton.Status != ScanButtonHandler.ScanStatus.Scanning)
            return;

        int dots = (int)(Time.unscaledTime / 0.4f) % 4;
        if (dots != _lastDots)
        {
            _lastDots = dots;
            RefreshSlip();
        }
    }

    private string ScanningLabel()
    {
        int dots = Mathf.Max(0, _lastDots);
        return _scanningText.TrimEnd('.', ' ') + new string('.', dots) + "<alpha=#00>" + new string('.', 3 - dots) + "<alpha=#FF>";
    }

    private void RefreshSlip()
    {
        var status = _scanButton != null ? _scanButton.Status : ScanButtonHandler.ScanStatus.Idle;

        string label;
        Color color;
        switch (status)
        {
            case ScanButtonHandler.ScanStatus.Scanning: label = ScanningLabel(); color = _scanningColor; break;
            case ScanButtonHandler.ScanStatus.Ready:    label = _readyText;    color = _readyColor;    break;
            case ScanButtonHandler.ScanStatus.Failed:
                label = string.IsNullOrEmpty(_scanButton.LastFailureReason)
                    ? _failedText
                    : $"{_failedText}: {_scanButton.LastFailureReason}";
                color = _failedColor;
                break;
            default:                                    label = _idleText;     color = _idleColor;     break;
        }

        if (_slipBackground != null) _slipBackground.color = color;
        if (_slipText != null)
        {
            _slipText.color = _slipTextColor;
            _slipText.text = string.IsNullOrEmpty(_tapHint)
                ? $"<b>{label}</b>"
                : $"<b>{label}</b>   <alpha=#B3>{_tapHint}";
        }
    }
}
