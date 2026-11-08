using UnityEngine;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Models;
using System.Collections.Generic;
using PassthroughCameraSamples;
using TMPro;
using Newtonsoft.Json.Linq;
using Utilities.WebRequestRest;

/// <summary>
/// Core of the hazard scanner: captures a passthrough frame and asks GPT-4o for hazards.
///
/// Flow (started by ScanButtonHandler calling AnalyzeEnvironment()):
///   1. TakePicture() copies the current Quest passthrough camera frame
///      (WebCamTextureManager, from Meta's Passthrough Camera API) into a Texture2D.
///   2. SubmitImage() sends the image plus a system prompt to OpenAI GPT-4o.
///      The prompt is precision-first: it restricts the model to visually supported
///      hazards in the OSHA Focus Four taxonomy (falls, struck-by, caught-in/between,
///      electrocution, other) and asks for a strict JSON object with a hazard,
///      summary, and 3-6 "Verify:"-style mitigation steps.
///   3. The JSON reply is cleaned and passed to ChecklistUI to build the checklist.
///   4. Success or failure is reported back to ScanButtonHandler. DescribeFailure()
///      maps API/network errors (401, 429, no internet, etc.) to short user-facing messages.
///
/// The API key is read from an OpenAIConfiguration asset (Assets/Resources/OpenAIConfiguration),
/// which is not committed to the repository.
/// </summary>
public class PassthroughCameraDescription : MonoBehaviour
{
    public WebCamTextureManager webcamManager;
    public OpenAIConfiguration configuration;

    [Header("UI Outputs")]
    public TextMeshProUGUI rawDebugText;
    public ChecklistUI checklistUI;
    
    [Header("Button Integration")]
    public ScanButtonHandler scanButtonHandler;

    private Texture2D picture;
    private bool isAnalyzing = false;

    void Start()
    {
        // Don't auto-submit - wait for button press
        // SubmitImage();
    }

    // NOTE: The old Update() listened for OVRInput.Button.One, which with hand
    // tracking is the index-finger + thumb pinch. It was removed so the poke
    // button (ScanButtonHandler) is the only way to start a scan.

    /// <summary>
    /// Called by ScanButtonHandler when the button is pressed
    /// </summary>
    public void AnalyzeEnvironment()
    {
        if (isAnalyzing)
        {
            Debug.Log("[PassthroughCamera] Already analyzing, ignoring request");
            return;
        }
        
        Debug.Log("[PassthroughCamera] AnalyzeEnvironment called");
        if (!TakePicture())
        {
            // Don't silently reuse an old photo if the camera isn't delivering frames
            NotifyScanFailed("Camera not ready",
                "The passthrough camera isn't providing images yet. Make sure camera permission was granted, wait a few seconds, and try again.");
            return;
        }
        SubmitImage();
    }

    public async void SubmitImage()
    {
        if (picture == null)
        {
            TakePicture();
            if (picture == null)
            {
                Debug.LogError("[PassthroughCamera] Failed to capture image");
                NotifyScanFailed("Camera not ready",
                    "The passthrough camera isn't providing images yet. Make sure camera permission was granted, wait a few seconds, and try again.");
                return;
            }
        }

        if (configuration == null)
        {
            NotifyScanFailed("No API configuration",
                "No OpenAIConfiguration is assigned on the Passthrough Camera Description object. Assign Assets/Resources/OpenAIConfiguration.");
            return;
        }

        isAnalyzing = true;
        Debug.Log("[PassthroughCamera] Starting API call...");

        // Show the "Scanning..." state on the panel right away
        if (checklistUI != null) checklistUI.ShowScanning();

        try
        {
            var api = new OpenAIClient(configuration);

            var messages = new List<Message>();

            Message systemMessage = new Message(
                Role.System,
                "You are a construction safety assistant assigned to analyze images from construction sites. " +
            "For each submitted image, identify hazards that are visually supported and provide mitigation steps aligned with OSHA best practices.\n\n" +

            "PRIMARY HAZARD TAXONOMY (use one of these in your hazard field if applicable):\n" +
            "- falls\n- struck_by\n- caught_in_between\n- electrocution\n- other\n\n" +

            "CORE RULES (strictly visual evidence only):\n" +
            "- Use ONLY what is visible in the image. Do NOT introduce unobserved objects, people, equipment type, brands, or actions.\n" +
            "- Avoid 'rule violation' wording. Describe risk-relevant observations (e.g., 'open edge visible,' 'guardrail system not clearly visible from this view,' 'exposed moving components visible').\n" +
            "- Do NOT state exact measurements/counts unless clearly verifiable from the image.\n" +
            "- Do NOT name specific tools/vehicles/machines unless unmistakable. If unsure, describe the visible component:\n" +
            "  - Say 'powered hand tool creating sparks' (not 'cut-off saw').\n" +
            "  - Say 'industrial vehicle with forks' only if forks are clearly visible; otherwise 'material-handling equipment.'\n" +
            "  - Say 'exposed belt/pulley' or 'exposed rotating component area' (not 'conveyor' unless unmistakable).\n\n" +

            "EVIDENCE GATE (precision-first, HARD):\n" +
            "- Only include a hazard if there is a concrete visual cue that a reviewer could literally circle in the image.\n" +
            "- If you cannot point to a concrete visible cue, OMIT the hazard (do not 'keep it with low confidence').\n" +
            "- Confidence is about visibility/clarity of the cue, not about how dangerous the situation might be.\n\n" +

            "CONFIDENCE LEVELS (include in summary or notes):\n" +
            "- high: hazard cue is unmistakable and clearly visible.\n" +
            "- medium: cue is visible but partially unclear/occluded/low resolution.\n" +
            "- low: cue is weak/partial. Use low only when the cue still exists visually; do NOT add extra specifics.\n\n" +

            "ANTI-HALLUCINATION (common FP traps):\n" +
            "- Absence claims: Do NOT claim 'no guardrails,' 'no fall protection,' 'not grounded,' 'no GFCI,' etc. unless the view is close enough to clearly confirm absence. Otherwise use 'not clearly visible from this view.'\n" +
            "- Roof/edge language: Only say 'roof edge/open edge' if the edge boundary is clearly visible.\n" +
            "- Motion: Only say a part is 'moving/rotating' if motion is visible or unmistakably in-use.\n" +
            "- PPE: Do not add PPE-related hazards unless PPE (or lack of it) is clearly visible and central to the hazard exposure in the image.\n\n" +

            "FALLS-SPECIFIC CONSTRAINTS:\n" +
            "- Mention 'open edge/unprotected edge/opening' only when the boundary is clearly visible.\n" +
            "- Do NOT mention 'stepping on/off,' 'transition,' or 'accessing' unless the stepping action (foot/leg + landing edge) is clearly visible.\n" +
            "- Only state 'guardrail missing' if the platform edge and expected rail area are visible and clearly absent; otherwise 'guardrail system not clearly visible from this view.'\n\n" +

            "CAUGHT-IN-BETWEEN CONSTRAINTS:\n" +
            "- Describe pinch/crush points as observed physical situations without assuming movement.\n" +
            "- Avoid 'trapped' unless clearly shown; prefer 'positioned beneath/near' and describe the visible support/spacing condition.\n\n" +

            "STRUCK-BY CONSTRAINTS:\n" +
            "- Only label struck-by if a visible impact source exists (suspended load, overhead materials, swing radius hazard clearly implied by equipment position, materials protruding into a path, etc.).\n" +
            "- Do not mention people/worker actions unless people are clearly visible.\n\n" +

            "RECALL SAFEGUARD:\n" +
            "- After listing hazards, do a quick second scan for OTHER distinct hazards, but apply the SAME Evidence Gate.\n" +
            "- Add only hazards that are equally well-supported and clearly distinct from those already listed.\n" +
            "- Prefer at most 1–3 hazards. Add a 4th only if the cue is unmistakable and distinct.\n\n" +

            "MITIGATION REQUIREMENTS:\n" +
            "- 3–6 actionable checklist items per hazard.\n" +
            "- Each item starts with an imperative verb and includes 'Verify: <physical/observable check>.'\n" +
            "- Prefer engineering controls, then administrative controls (max 1 item), then PPE.\n" +
            "- Keep verification physical/observable whenever possible.\n\n" +

            "OUTPUT FORMAT (respond ONLY with this JSON object, no markdown or extra text):\n" +
            "{\n" +
            "  \"hazard\": \"string - taxonomy category (falls/struck_by/caught_in_between/electrocution/other) or 'No immediate hazards detected'\",\n" +
            "  \"summary\": \"string - 1–2 sentence description of the visible hazard cue(s) and confidence level\",\n" +
            "  \"steps\": [\n" +
            "    { \"id\": number, \"text\": \"string - mitigation action with Verify: <check>\", \"done\": false }\n" +
            "  ],\n" +
            "  \"notes\": [\"optional extra notes: additional observations, confidence clarifications, or cues that did not meet Evidence Gate\"]\n" +
            "}\n\n" +

            "RULES:\n" +
            "- If NO hazards meet the Evidence Gate, set hazard to 'Watch-outs (no confirmed hazards)' and provide 3-7 potential areas to monitor or general safety watch-outs visible in the scene in steps (e.g., 'Monitor stacked materials for stability. Verify: check for leaning or shifting.', 'Ensure walkways remain clear. Verify: no obstructions in visible paths.').\n" +
            "- NEVER return an empty steps array. Always provide actionable items.\n" +
            "- Do NOT include markdown, bullet points, explanations, or any text outside the JSON."
            );

            var imageContents = new List<Content>();

            string textContent =
                 "Analyze this construction site image for hazards. Apply the Evidence Gate strictly: " +
            "only include hazards with concrete visual cues a reviewer could circle. " +
            "Focus on OSHA Focus Four categories (falls, struck-by, caught-in-between, electrocution) and other visible hazards. " +
            "If no hazards meet the Evidence Gate, provide potential watch-outs and areas to monitor based on what IS visible in the scene.";

            imageContents.Add(textContent);
            imageContents.Add(picture);

            Message imageMessage = new Message(Role.User, imageContents);

            messages.Add(systemMessage);
            messages.Add(imageMessage);

            var chatRequest = new ChatRequest(messages, model: Model.GPT4o);
            var result = await api.ChatEndpoint.GetCompletionAsync(chatRequest);

            if (result == null || result.FirstChoice == null)
                throw new System.InvalidOperationException("The AI returned an empty response.");

            string json = result.FirstChoice.ToString();

            Debug.Log("[PassthroughCamera] AI Response:\n" + json);

            // Clean up JSON if it has markdown code blocks
            json = CleanJsonResponse(json);

            // Optional: show raw text for debugging
            if (rawDebugText != null)
            {
                rawDebugText.text = PrettyPrintFromJson(json);
            }

            // Build interactive checklist
            if (checklistUI != null)
            {
                Debug.Log("[PassthroughCamera] Calling BuildChecklistFromJson...");
                checklistUI.BuildChecklistFromJson(json);
            }
            else
            {
                Debug.LogError("[PassthroughCamera] ChecklistUI is not assigned!");
            }

            // Notify button that scan is complete
            NotifyScanComplete();
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[PassthroughCamera] Error: " + ex.Message);
            Debug.LogError("[PassthroughCamera] Stack trace: " + ex.StackTrace);
            
            if (rawDebugText != null)
            {
                rawDebugText.text = "Error: " + ex.Message;
            }
            
            DescribeFailure(ex, out string reason, out string detail);
            NotifyScanFailed(reason, detail);
        }
        finally
        {
            isAnalyzing = false;
        }
    }

    /// <summary>
    /// Cleans up JSON response that might have markdown code blocks
    /// </summary>
    private string CleanJsonResponse(string response)
    {
        string cleaned = response.Trim();
        
        // Remove markdown code blocks if present
        if (cleaned.StartsWith("```json"))
        {
            cleaned = cleaned.Substring(7);
        }
        else if (cleaned.StartsWith("```"))
        {
            cleaned = cleaned.Substring(3);
        }
        
        if (cleaned.EndsWith("```"))
        {
            cleaned = cleaned.Substring(0, cleaned.Length - 3);
        }
        
        return cleaned.Trim();
    }

    private string PrettyPrintFromJson(string json)
    {
        try
        {
            var root = JObject.Parse(json);

            string hazard = (string)root["hazard"] ?? "";
            string summary = (string)root["summary"] ?? "";

            var s = $"{hazard}\n{summary}\n\nChecklist:\n";

            var steps = (JArray)root["steps"];
            if (steps != null)
            {
                foreach (var step in steps)
                {
                    string txt = (string)step["text"];
                    s += $"- {txt}\n";
                }
            }

            var notes = (JArray)root["notes"];
            if (notes != null && notes.Count > 0)
            {
                s += "\nNotes:\n";
                foreach (var n in notes)
                {
                    s += $"- {n}\n";
                }
            }

            return s;
        }
        catch (System.Exception ex)
        {
            Debug.LogError("[PassthroughCamera] JSON parse error: " + ex.Message);
            return json;
        }
    }

    /// <summary>Captures the current passthrough frame. Returns false if the camera isn't ready.</summary>
    public bool TakePicture()
    {
        if (webcamManager == null || webcamManager.WebCamTexture == null)
        {
            Debug.LogError("[PassthroughCamera] WebCamTextureManager not ready");
            return false;
        }
        
        var camTex = webcamManager.WebCamTexture;
        int width = camTex.width;
        int height = camTex.height;

        if (width <= 0 || height <= 0)
        {
            Debug.LogError("[PassthroughCamera] Invalid camera dimensions");
            return false;
        }

        if (picture == null || picture.width != width || picture.height != height)
        {
            picture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        }

        var pixels = new Color32[width * height];
        camTex.GetPixels32(pixels);

        picture.SetPixels32(pixels);
        picture.Apply(false);
        
        Debug.Log($"[PassthroughCamera] Picture captured: {width}x{height}");
        return true;
    }
    
    /// <summary>
    /// Turns an exception from the OpenAI call into a short reason (for the button/slip)
    /// and a one or two sentence explanation (for the panel).
    /// </summary>
    private static void DescribeFailure(System.Exception ex, out string reason, out string detail)
    {
        // Unwrap AggregateException / inner exceptions to find the most useful one
        System.Exception root = ex;
        if (root is System.AggregateException agg && agg.InnerExceptions.Count > 0)
            root = agg.InnerExceptions[0];

        RestException rest = null;
        System.Security.Authentication.AuthenticationException auth = null;
        for (var e = root; e != null; e = e.InnerException)
        {
            if (rest == null && e is RestException r) rest = r;
            if (auth == null && e is System.Security.Authentication.AuthenticationException a) auth = a;
        }

        // Missing / empty API key: the OpenAI client refuses to start
        if (auth != null)
        {
            reason = "No API key";
            detail = "No OpenAI API key was found. Add your key to Assets/Resources/OpenAIConfiguration and rebuild.";
            return;
        }

        if (rest != null && rest.Response != null)
        {
            long code = rest.Response.Code;
            string body = rest.Response.Body ?? "";
            string apiMessage = ExtractApiErrorMessage(body);

            switch (code)
            {
                case 401:
                    reason = "API key rejected";
                    detail = "OpenAI rejected the API key (401). Check that the key in OpenAIConfiguration is correct and hasn't been revoked.";
                    return;
                case 403:
                    reason = "Access denied";
                    detail = "OpenAI refused the request (403). This key or project may not have access to GPT-4o." + Suffix(apiMessage);
                    return;
                case 404:
                    reason = "Model not found";
                    detail = "OpenAI couldn't find the requested model (404). This key may not have access to GPT-4o." + Suffix(apiMessage);
                    return;
                case 429:
                    if (body.Contains("insufficient_quota"))
                    {
                        reason = "Out of API credit";
                        detail = "The OpenAI account has run out of credit or hit its spending limit (429). Add billing credit on platform.openai.com.";
                    }
                    else
                    {
                        reason = "Rate limited";
                        detail = "Too many requests to OpenAI in a short time (429). Wait a few seconds and try again.";
                    }
                    return;
                case 400:
                    reason = "Request rejected";
                    detail = "OpenAI couldn't process the request (400)." + Suffix(apiMessage);
                    return;
                case 0:
                    string err = (rest.Response.Error ?? "").ToLowerInvariant();
                    if (err.Contains("timeout") || err.Contains("timed out"))
                    {
                        reason = "Request timed out";
                        detail = "OpenAI didn't respond in time. Check the Wi-Fi connection and try again.";
                    }
                    else
                    {
                        reason = "No internet";
                        detail = "Couldn't reach OpenAI. Check that the headset is connected to Wi-Fi and try again.";
                    }
                    return;
                default:
                    if (code >= 500)
                    {
                        reason = "OpenAI server error";
                        detail = $"OpenAI had a problem on its end ({code}). Wait a moment and try again.";
                    }
                    else
                    {
                        reason = $"API error {code}";
                        detail = $"OpenAI returned an error ({code})." + Suffix(apiMessage);
                    }
                    return;
            }
        }

        if (root is System.OperationCanceledException || root is System.TimeoutException)
        {
            reason = "Request timed out";
            detail = "OpenAI didn't respond in time. Check the Wi-Fi connection and try again.";
            return;
        }

        // Newtonsoft parse errors: the AI answered, but not with the expected JSON
        if (root.GetType().Namespace != null && root.GetType().Namespace.StartsWith("Newtonsoft.Json")
            || root is System.InvalidCastException)
        {
            reason = "Unreadable AI response";
            detail = "The AI replied, but not in the expected checklist format. Try scanning again.";
            return;
        }

        reason = "Unexpected error";
        detail = root.Message;
    }

    /// <summary>Pulls error.message out of an OpenAI error body, if present.</summary>
    private static string ExtractApiErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            var msg = (string)JObject.Parse(body)["error"]?["message"];
            if (string.IsNullOrWhiteSpace(msg)) return "";
            return msg.Length > 200 ? msg.Substring(0, 200) + "..." : msg;
        }
        catch
        {
            return "";
        }
    }

    private static string Suffix(string apiMessage) =>
        string.IsNullOrEmpty(apiMessage) ? "" : $" Details: {apiMessage}";

    private void NotifyScanComplete()
    {
        if (scanButtonHandler != null)
        {
            scanButtonHandler.OnScanComplete();
        }
    }
    
    private void NotifyScanFailed(string reason, string detail)
    {
        Debug.LogError($"[PassthroughCamera] Scan failed: {reason} - {detail}");

        if (checklistUI != null)
        {
            checklistUI.ShowError(reason, detail);
        }

        if (scanButtonHandler != null)
        {
            scanButtonHandler.OnScanFailed(reason);
        }
    }
}
