using UnityEngine;
using TMPro;
using UnityEngine.UI;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

/// <summary>
/// Renders the model's JSON reply as an interactive mitigation checklist on the AR panel.
///
/// - BuildChecklistFromJson(): parses { hazard, summary, steps[] }, shows a readable hazard
///   title (e.g. "struck_by" -> "Struck-by") and summary, and creates one checkbox row per step
///   from the step-item prefab. "Verify: ..." is moved onto its own line within each step.
/// - Checking a step strikes it through, fades it, and moves it below the unchecked steps,
///   so the remaining work stays at the top.
/// - ShowScanning() / ShowError() display the "Scanning..." and "Scan failed" states.
/// </summary>
public class ChecklistUI : MonoBehaviour
{
    [Header("UI References")]
    public TextMeshProUGUI hazardTitleText;   // top title
    public TextMeshProUGUI summaryText;       // brief rationale
    public Transform stepsContainer;          // parent with VerticalLayoutGroup
    public GameObject stepItemPrefab;         // prefab: Toggle + TMP_Text

    [System.Serializable]
    public class ChecklistStep
    {
        public int id;
        public string text;
        public bool done;
        public int order;     // original order from JSON
    }

    // Holds both data + UI for each row
    private class StepEntry
    {
        public ChecklistStep data;
        public GameObject go;
        public Toggle toggle;
        public TextMeshProUGUI text;
        public FontStyles baseStyle;
        public Color baseColor;
        public GameObject checkGlyph;   // white tick drawn on the orange fill
    }

    private List<StepEntry> entries = new List<StepEntry>();

    [Header("Error display")]
    [SerializeField] private string errorTitle = "SCAN FAILED";
    [SerializeField] private Color errorTitleColor = new Color(1f, 0.42f, 0.36f);

    [Header("Scanning display")]
    [SerializeField] private string scanningTitle = "Scanning";
    [SerializeField] private string scanningMessage = "Image sent to the AI. Results usually take 3-6 seconds.";
    [SerializeField] private Color scanningTitleColor = new Color(0.45f, 0.75f, 1f);

    // >= 0 while waiting for the AI; used for the elapsed-time counter
    private float scanningSince = -1f;
    private int lastScanSecond = -1;
    private int lastScanDots = -1;

    /// <summary>
    /// Called as soon as the image is sent: clears the old checklist and shows an
    /// animated "Scanning..." title with an elapsed-time counter, so it's clear the
    /// request is in progress.
    /// </summary>
    public void ShowScanning()
    {
        ClearSteps();
        scanningSince = Time.unscaledTime;
        lastScanSecond = -1;
        lastScanDots = -1;
        UpdateScanningText();
    }

    private void Update()
    {
        if (scanningSince >= 0f) UpdateScanningText();
    }

    private void UpdateScanningText()
    {
        float elapsed = Time.unscaledTime - scanningSince;
        int seconds = (int)elapsed;
        int dots = (int)(elapsed / 0.4f) % 4;

        if (dots != lastScanDots && hazardTitleText != null)
        {
            string hex = ColorUtility.ToHtmlStringRGB(scanningTitleColor);
            hazardTitleText.text = $"<color=#{hex}>{scanningTitle}{new string('.', dots)}<alpha=#00>{new string('.', 3 - dots)}</color>";
            lastScanDots = dots;
        }
        if (seconds != lastScanSecond && summaryText != null)
        {
            summaryText.text = $"{scanningMessage}\n<b>Waiting for response: {seconds}s</b>";
            lastScanSecond = seconds;
        }
    }

    private void StopScanning()
    {
        scanningSince = -1f;
    }

    private void ClearSteps()
    {
        foreach (var e in entries)
        {
            if (e.go != null)
                Destroy(e.go);
        }
        entries.Clear();
    }

    /// <summary>
    /// Shows why a scan failed in place of the checklist.
    /// The next successful scan replaces it as normal.
    /// </summary>
    public void ShowError(string reason, string detail)
    {
        StopScanning();
        ClearSteps();

        if (hazardTitleText != null)
        {
            string hex = ColorUtility.ToHtmlStringRGB(errorTitleColor);
            hazardTitleText.text = $"<color=#{hex}>{errorTitle}</color>";
        }
        if (summaryText != null)
            summaryText.text = $"<b>{reason}</b>\n{detail}";
    }

    public void BuildChecklistFromJson(string json)
    {
        // Parse first so a malformed response throws before the UI is touched
        JObject root = JObject.Parse(json);

        StopScanning();

        // Clear old UI
        ClearSteps();

        string hazard = (string)root["hazard"] ?? "";
        string summary = (string)root["summary"] ?? "";

        if (hazardTitleText != null) hazardTitleText.text = PrettyHazardName(hazard);
        if (summaryText != null)    summaryText.text    = summary;

        var stepsArray = (JArray)root["steps"];
        if (stepsArray == null) return;

        int orderCounter = 0;

        foreach (var token in stepsArray)
        {
            var step = new ChecklistStep
            {
                id    = (int)(token["id"]   ?? 0),
                text  = (string)token["text"],
                done  = (bool)(token["done"] ?? false),
                order = orderCounter++
            };

            var go     = Instantiate(stepItemPrefab, stepsContainer);
            var toggle = go.GetComponentInChildren<Toggle>();
            var txt    = go.GetComponentInChildren<TextMeshProUGUI>();

            if (txt != null) txt.text = FormatStepText(step.text);
            if (toggle != null) toggle.isOn = step.done;
            LayoutRow(go, toggle, txt);

            Transform glyph = (toggle != null && toggle.graphic != null)
                ? toggle.graphic.transform.Find("CheckGlyph")
                : null;

            var entry = new StepEntry
            {
                data       = step,
                go         = go,
                toggle     = toggle,
                text       = txt,
                baseStyle  = txt != null ? txt.fontStyle : FontStyles.Normal,
                baseColor  = txt != null ? txt.color     : Color.white,
                checkGlyph = glyph != null ? glyph.gameObject : null
            };

            entries.Add(entry);

            if (toggle != null)
            {
                int capturedId = step.id;
                toggle.onValueChanged.AddListener(value =>
                {
                    OnToggleChanged(capturedId, value);
                });
            }

            UpdateVisual(entry);
        }

        ReorderEntries();

        // Start a new checklist scrolled to the top
        var scroll = stepsContainer != null ? stepsContainer.GetComponentInParent<ScrollRect>(true) : null;
        if (scroll != null)
        {
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = 1f;
        }
    }

    /// <summary>
    /// Puts the "Verify: ..." part of a step on its own line (same checklist item).
    /// </summary>
    private static string FormatStepText(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return System.Text.RegularExpressions.Regex.Replace(
            text, @"\s*\b(Verify:)", "\n$1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).TrimStart('\n');
    }

    /// <summary>Turns the model's taxonomy keys (e.g. "struck_by") into readable titles.</summary>
    private static string PrettyHazardName(string hazard)
    {
        if (string.IsNullOrWhiteSpace(hazard)) return "Hazard check";
        switch (hazard.Trim().ToLowerInvariant())
        {
            case "falls":             return "Falls";
            case "struck_by":         return "Struck-by";
            case "caught_in_between": return "Caught-in/between";
            case "electrocution":     return "Electrocution";
            case "other":             return "Other hazard";
            default:
                string s = hazard.Replace('_', ' ').Trim();
                return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }

    [Header("Row layout")]
    [SerializeField] private float checkboxSize = 52f;
    [SerializeField] private float checkboxTopOffset = 4f;   // lines the box up with the first text line
    [SerializeField] private float textIndent = 80f;          // space from row edge to the text
    [SerializeField] private float rowPadding = 8f;

    /// <summary>
    /// Places the checkbox and text of one row explicitly and sets the row's height
    /// from the wrapped text, so rows never overlap and the checkbox is never clipped.
    /// (Doesn't rely on the row prefab's own layout settings.)
    /// </summary>
    private void LayoutRow(GameObject row, Toggle toggle, TextMeshProUGUI txt)
    {
        var rowLayout = row.GetComponent<HorizontalLayoutGroup>();
        if (rowLayout != null) rowLayout.enabled = false;

        // Checkbox: fixed square, top-left of the row
        RectTransform box = (toggle != null && toggle.targetGraphic != null) ? toggle.targetGraphic.rectTransform : null;
        if (box != null)
        {
            box.anchorMin = new Vector2(0f, 1f);
            box.anchorMax = new Vector2(0f, 1f);
            box.pivot = new Vector2(0f, 1f);
            box.sizeDelta = new Vector2(checkboxSize, checkboxSize);
            box.anchoredPosition = new Vector2(0f, -checkboxTopOffset);
        }

        float textHeight = 0f;
        if (txt != null)
        {
            // Text: fills the row to the right of the checkbox
            RectTransform tr = txt.rectTransform;
            tr.anchorMin = new Vector2(0f, 0f);
            tr.anchorMax = new Vector2(1f, 1f);
            tr.pivot = new Vector2(0.5f, 0.5f);
            tr.offsetMin = new Vector2(textIndent, 0f);
            tr.offsetMax = new Vector2(0f, 0f);

            float width = (stepsContainer is RectTransform rt ? rt.rect.width : 0f) - textIndent;
            if (width <= 0f) width = 1200f;
            textHeight = txt.GetPreferredValues(txt.text, width, 0f).y;
        }

        var le = row.GetComponent<LayoutElement>();
        if (le == null) le = row.AddComponent<LayoutElement>();
        float height = Mathf.Max(checkboxSize + checkboxTopOffset, textHeight) + rowPadding;
        le.minHeight = height;
        le.preferredHeight = height;
        le.flexibleHeight = 0f;
    }

    private void OnToggleChanged(int id, bool newValue)
    {
        var entry = entries.Find(e => e.data.id == id);
        if (entry == null) return;

        entry.data.done = newValue;

        // Update strike-through / fade
        UpdateVisual(entry);

        // Move completed items to the bottom
        ReorderEntries();
    }

    private void UpdateVisual(StepEntry entry)
    {
        // The Toggle only fades the orange fill, so show/hide the tick ourselves
        if (entry.checkGlyph != null)
            entry.checkGlyph.SetActive(entry.data.done);

        if (entry.text == null) return;

        if (entry.data.done)
        {
            // Add strikethrough and fade a bit
            entry.text.fontStyle = entry.baseStyle | FontStyles.Strikethrough;

            var c = entry.baseColor;
            c.a *= 0.5f;
            entry.text.color = c;
        }
        else
        {
            // Restore original style and full opacity
            entry.text.fontStyle = entry.baseStyle & ~FontStyles.Strikethrough;
            entry.text.color     = entry.baseColor;
        }
    }

    private void ReorderEntries()
    {
        // Incomplete first, then completed (keeping original order inside each group)
        var ordered = entries
            .OrderBy(e => e.data.done ? 1 : 0)
            .ThenBy(e => e.data.order)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].go != null)
                ordered[i].go.transform.SetSiblingIndex(i);
        }
    }
}
