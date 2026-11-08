using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// The pokeable "SCAN FOR HAZARDS" button that starts a scan.
///
/// - Hover / press / release events from the Interaction SDK change the button's color;
///   a completed poke calls TriggerScan().
/// - TriggerScan() calls the configured method (default "AnalyzeEnvironment") on the
///   analysis component (PassthroughCameraDescription) via SendMessage.
/// - Tracks scan status (Idle / Scanning / Ready / Failed) and raises StatusChanged so the
///   minimized status slip (PanelMinimizer) can show it.
/// - After a scan completes or fails, a short cooldown prevents accidental repeat scans.
/// </summary>
public class ScanButtonHandler : MonoBehaviour
{
    /// <summary>Overall scan state, shown on the minimized status slip.</summary>
    public enum ScanStatus { Idle, Scanning, Ready, Failed }

    /// <summary>Raised whenever the scan status changes (used by PanelMinimizer).</summary>
    public event System.Action<ScanStatus> StatusChanged;

    /// <summary>Current scan status. Ready = a checklist has been generated.</summary>
    public ScanStatus Status { get; private set; } = ScanStatus.Idle;

    /// <summary>Short reason for the last failure (e.g. "No API key"), shown on the status slip.</summary>
    public string LastFailureReason { get; private set; } = "";

    [Header("References - Assign your ChatGPT/OpenAI Manager here")]
    [Tooltip("Drag your script that handles OpenAI/ChatGPT API calls here")]
    [SerializeField] private MonoBehaviour _chatGPTManager;
    
    [Header("Method Name Configuration")]
    [Tooltip("The name of the method to call on your ChatGPT manager to trigger analysis")]
    [SerializeField] private string _analyzeMethodName = "AnalyzeEnvironment";
    
    [Header("UI References")]
    [SerializeField] private Image _buttonImage;
    [SerializeField] private TextMeshProUGUI _buttonText;

    [Header("Colors")]
    [SerializeField] private Color _readyColor = new Color(1f, 0.42f, 0.21f);    // Orange
    [SerializeField] private Color _hoverColor = new Color(1f, 0.55f, 0.35f);    // Light orange  
    [SerializeField] private Color _pressedColor = new Color(0.8f, 0.33f, 0.17f); // Dark orange
    [SerializeField] private Color _scanningColor = new Color(0.2f, 0.6f, 1f);   // Blue

    [Header("Button Text")]
    [SerializeField] private string _readyText = "SCAN FOR HAZARDS";
    [SerializeField] private string _scanningText = "SCANNING...";
    [SerializeField] private string _cooldownText = "PLEASE WAIT...";
    [SerializeField] private string _failedText = "SCAN FAILED";

    [Header("Failure")]
    [SerializeField] private Color _failedColor = new Color(0.85f, 0.25f, 0.2f);  // Red

    [Header("Settings")]
    [SerializeField] private float _cooldownTime = 3f;

    private bool _isScanning = false;
    private bool _isCoolingDown = false;
    private Coroutine _scanAnimation;

    private void Awake()
    {
        // Auto-find UI components if not assigned
        if (_buttonImage == null)
            _buttonImage = GetComponent<Image>();
        if (_buttonText == null)
            _buttonText = GetComponentInChildren<TextMeshProUGUI>();
            
        // Set initial state
        if (_buttonImage != null)
            _buttonImage.color = _readyColor;
        if (_buttonText != null)
            _buttonText.text = _readyText;
    }

    /// <summary>
    /// Called by Unity Event Wrapper - When Hover
    /// Finger approaches the button
    /// </summary>
    public void OnHoverEnter()
    {
        if (_isScanning || _isCoolingDown) return;
        
        Debug.Log("[ScanButton] Hover Enter - finger approaching");
        if (_buttonImage != null)
            _buttonImage.color = _hoverColor;
    }

    /// <summary>
    /// Called by Unity Event Wrapper - When Unhover
    /// Finger moves away from button
    /// </summary>
    public void OnHoverExit()
    {
        if (_isScanning || _isCoolingDown) return;
        
        Debug.Log("[ScanButton] Hover Exit - finger moved away");
        if (_buttonImage != null)
            _buttonImage.color = _readyColor;
    }

    /// <summary>
    /// Called by Unity Event Wrapper - When Select
    /// Finger presses/pokes the button
    /// </summary>
    public void OnPress()
    {
        if (_isScanning || _isCoolingDown) return;
        
        Debug.Log("[ScanButton] Button Pressed");
        if (_buttonImage != null)
            _buttonImage.color = _pressedColor;
    }

    /// <summary>
    /// Called by Unity Event Wrapper - When Unselect
    /// Finger releases the button - THIS TRIGGERS THE SCAN
    /// </summary>
    public void OnRelease()
    {
        if (_isScanning || _isCoolingDown) 
        {
            Debug.Log("[ScanButton] Release ignored - scan in progress or cooling down");
            return;
        }
        
        Debug.Log("[ScanButton] Button Released - TRIGGERING SCAN");
        TriggerScan();
    }

    /// <summary>
    /// Main method to trigger the environment scan via ChatGPT
    /// </summary>
    public void TriggerScan()
    {
        if (_isScanning || _isCoolingDown)
        {
            Debug.Log("[ScanButton] Scan blocked - already in progress or cooling down");
            return;
        }

        _isScanning = true;
        SetStatus(ScanStatus.Scanning);
        
        // Update UI to scanning state
        if (_buttonImage != null)
            _buttonImage.color = _scanningColor;
        if (_buttonText != null)
            _buttonText.text = _scanningText;
        StartScanAnimation();

        Debug.Log("[ScanButton] ===== HAZARD SCAN STARTED =====");

        // Call the ChatGPT/OpenAI manager
        if (_chatGPTManager != null)
        {
            Debug.Log($"[ScanButton] Calling {_analyzeMethodName} on {_chatGPTManager.GetType().Name}");
            _chatGPTManager.SendMessage(_analyzeMethodName, SendMessageOptions.DontRequireReceiver);
        }
        else
        {
            Debug.LogWarning("[ScanButton] ChatGPT Manager not assigned! Running in test mode...");
            // Test mode - auto complete after 3 seconds
            Invoke(nameof(OnScanComplete), 3f);
        }
    }

    /// <summary>
    /// Call this method from your ChatGPT manager when the API response is received
    /// </summary>
    public void OnScanComplete()
    {
        Debug.Log("[ScanButton] ===== SCAN COMPLETE =====");
        _isScanning = false;
        StopScanAnimation();
        LastFailureReason = "";
        SetStatus(ScanStatus.Ready);
        StartCooldown(_cooldownText);
    }

    /// <summary>
    /// Call this method from your ChatGPT manager if the API call fails
    /// </summary>
    public void OnScanFailed()
    {
        OnScanFailed("");
    }

    /// <summary>
    /// Call this with a short, user-facing reason (e.g. "No API key", "No internet").
    /// </summary>
    public void OnScanFailed(string reason)
    {
        Debug.LogError($"[ScanButton] ===== SCAN FAILED ===== {reason}");
        _isScanning = false;
        StopScanAnimation();
        LastFailureReason = reason ?? "";
        SetStatus(ScanStatus.Failed);

        // Show the failure on the button during the cooldown
        if (_buttonImage != null)
            _buttonImage.color = _failedColor;
        StartCooldown(_failedText);
    }

    private void SetStatus(ScanStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    /// <summary>
    /// Starts the cooldown. If the button is hidden (panel minimized) a coroutine
    /// can't run, so skip straight back to the ready state instead.
    /// </summary>
    private void StartCooldown(string cooldownLabel)
    {
        if (isActiveAndEnabled)
            StartCoroutine(CooldownRoutine(cooldownLabel));
        else
            ResetToReady();
    }

    /// <summary>
    /// If the panel is minimized during a cooldown, Unity stops the coroutine,
    /// so reset here to avoid the button getting stuck on "PLEASE WAIT...".
    /// </summary>
    private void OnDisable()
    {
        _scanAnimation = null;   // Unity stops coroutines on disable
        if (_isCoolingDown)
        {
            StopAllCoroutines();
            ResetToReady();
        }
    }

    /// <summary>
    /// When the panel is expanded again, make sure the button shows the right state.
    /// </summary>
    private void OnEnable()
    {
        if (_isScanning)
            StartScanAnimation();   // panel re-opened mid-scan: keep showing progress
        else if (!_isCoolingDown)
            ResetToReady();
    }

    private void StartScanAnimation()
    {
        StopScanAnimation();
        if (isActiveAndEnabled)
            _scanAnimation = StartCoroutine(ScanAnimationRoutine());
    }

    private void StopScanAnimation()
    {
        if (_scanAnimation != null)
        {
            StopCoroutine(_scanAnimation);
            _scanAnimation = null;
        }
    }

    /// <summary>
    /// While waiting for the AI: animated dots ("SCANNING", "SCANNING.", ...) and a
    /// gentle pulse on the button so it's obvious the request is in progress.
    /// </summary>
    private System.Collections.IEnumerator ScanAnimationRoutine()
    {
        string baseText = _scanningText.TrimEnd('.', ' ');
        Color bright = Color.Lerp(_scanningColor, Color.white, 0.35f);
        float start = Time.unscaledTime;
        int lastDots = -1;

        while (_isScanning)
        {
            float t = Time.unscaledTime - start;

            int dots = (int)(t / 0.4f) % 4;
            if (dots != lastDots && _buttonText != null)
            {
                // Hidden dots keep the text from shifting as dots appear
                _buttonText.text = baseText + new string('.', dots) + "<alpha=#00>" + new string('.', 3 - dots);
                lastDots = dots;
            }

            if (_buttonImage != null)
                _buttonImage.color = Color.Lerp(_scanningColor, bright, 0.5f + 0.5f * Mathf.Sin(t * 4f));

            yield return null;
        }
        _scanAnimation = null;
    }

    private void ResetToReady()
    {
        _isCoolingDown = false;
        if (_buttonImage != null)
            _buttonImage.color = _readyColor;
        if (_buttonText != null)
            _buttonText.text = _readyText;
    }

    /// <summary>
    /// Cooldown period to prevent accidental double-triggers
    /// </summary>
    private System.Collections.IEnumerator CooldownRoutine(string cooldownLabel)
    {
        _isCoolingDown = true;
        
        if (_buttonText != null)
            _buttonText.text = cooldownLabel;
        
        yield return new WaitForSeconds(_cooldownTime);
        
        _isCoolingDown = false;
        
        // Reset to ready state
        if (_buttonImage != null)
            _buttonImage.color = _readyColor;
        if (_buttonText != null)
            _buttonText.text = _readyText;
            
        Debug.Log("[ScanButton] Ready for next scan");
    }

    /// <summary>
    /// Public method to check if currently scanning (useful for other scripts)
    /// </summary>
    public bool IsScanning => _isScanning;
    
    /// <summary>
    /// Public method to check if in cooldown (useful for other scripts)
    /// </summary>
    public bool IsCoolingDown => _isCoolingDown;
}