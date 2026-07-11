using LectureSmith.Models;
using Mscc.GenerativeAI;
using System.Net.Http;
using System.Text;

using System.Text.RegularExpressions;

namespace LectureSmith.Services;

public class GeminiService
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(15)
    };
    private string _apiKey = string.Empty;
    private GenerativeModel? _model;
    private IGenerativeAI? _googleAi;

    /// <summary>
    /// Available text-generation models the user can choose from.
    /// Updated from https://ai.google.dev/gemini-api/docs/models and pricing page.
    /// </summary>
    public static readonly GeminiModelInfo[] AvailableModels =
    [
        new("gemini-3.5-pro",               "Gemini 3.5 Pro (2M Tokens)",           IsFree: false),
        new("gemini-3.5-flash",             "Gemini 3.5 Flash (1M Tokens)",         IsFree: true),
        new("gemini-3.1-pro-preview",       "Gemini 3.1 Pro Preview (1M Tokens)",   IsFree: false),
        new("gemini-3.1-flash-lite-preview","Gemini 3.1 Flash-Lite (1M Tokens)",    IsFree: true),
        new("gemini-2.5-pro",               "Gemini 2.5 Pro (1M Tokens)",           IsFree: false),
        new("gemini-2.5-flash",             "Gemini 2.5 Flash (1M Tokens)",         IsFree: true),
    ];

    /// <summary>
    /// Finds a model info by its ID, or returns the first model as fallback.
    /// </summary>
    public static GeminiModelInfo FindModelById(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return AvailableModels[0];

        return Array.Find(AvailableModels, m => m.Id == modelId) ?? AvailableModels[0];
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public void SetApiKey(string apiKey)
    {
        _apiKey = apiKey;
        _googleAi = new GoogleAI(apiKey);
        _model = null;
    }

    public void SetModel(string modelId, bool thinkingEnabled = false)
    {
        if (_googleAi == null) return;

        var genConfig = new GenerationConfig
        {
            MaxOutputTokens = 65536,
            Temperature = 0.7f,
            // ThinkingBudget = 0 disables thinking so the full 65 536 token
            // budget goes to actual content instead of internal reasoning.
            ThinkingConfig = new ThinkingConfig
            {
                ThinkingBudget = thinkingEnabled ? -1 : 0
            }
        };

        _model = _googleAi.GenerativeModel(
            model: modelId,
            generationConfig: genConfig);
    }

    /// <summary>
    /// Generates text-only content using the Gemini API.
    /// </summary>
    public async Task<string> GenerateAsync(string systemPrompt, string userContent,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureModelConfigured();

        var combinedPrompt = BuildCombinedPrompt(systemPrompt, userContent);
        var response = _model!.GenerateContentStream(combinedPrompt);

        return await StreamResponseAsync(response, progress, ct);
    }

    /// <summary>
    /// Generates content with inline slide images using the Gemini multimodal API.
    /// </summary>
    public async Task<string> GenerateWithImagesAsync(string systemPrompt, string userContent,
        List<string> imagePaths, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureModelConfigured();

        var combinedPrompt = BuildCombinedPrompt(systemPrompt, userContent);
        var request = new GenerateContentRequest(combinedPrompt);

        foreach (var imagePath in imagePaths)
        {
            if (File.Exists(imagePath))
            {
                await request.AddMedia(imagePath);
            }
        }

        var response = _model!.GenerateContentStream(request);

        return await StreamResponseAsync(response, progress, ct);
    }

    /// <summary>
    /// Validates the API key by calling the models REST endpoint (no quota consumed).
    /// </summary>
    public async Task<(bool IsValid, string Error)> ValidateApiKeyAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
                return (false, "Not initialized.");
            
            var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={_apiKey}";
            var response = await _httpClient.GetAsync(url);
            
            if (response.IsSuccessStatusCode)
                return (true, string.Empty);
            
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest || 
                body.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase))
                return (false, "Invalid API key.");
            
            return (false, $"Validation failed ({response.StatusCode}): {body}");
        }
        catch (Exception ex)
        {
            return (false, $"API key validation failed: {ex.Message}");
        }
    }

    // ── Private helpers ───────────────────────────────────────────────

    private void EnsureModelConfigured()
    {
        if (_model == null)
            throw new InvalidOperationException("Model not configured. Set API key and model first.");
    }

    private static string BuildCombinedPrompt(string systemPrompt, string userContent)
        => $"SYSTEM INSTRUCTIONS:\n{systemPrompt}\n\n---\n\nUSER CONTENT:\n{userContent}";

    /// <summary>
    /// Shared streaming logic — consumes an async stream of response chunks,
    /// reports progress, and returns the full accumulated text.
    /// </summary>
    private static async Task<string> StreamResponseAsync(
        IAsyncEnumerable<GenerateContentResponse> response,
        IProgress<string>? progress, CancellationToken ct)
    {
        var fullResponse = new StringBuilder();
        string? lastFinishReason = null;

        try
        {
            await foreach (var chunk in response)
            {
                ct.ThrowIfCancellationRequested();

                // Track the finish reason so we can detect truncation
                if (chunk.Candidates != null)
                {
                    foreach (var candidate in chunk.Candidates)
                    {
                        if (candidate.FinishReason != null)
                            lastFinishReason = candidate.FinishReason.ToString();
                    }
                }

                var text = chunk.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    fullResponse.Append(text);
                    progress?.Report(text);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // If we already got partial content, return it with a warning
            // instead of losing everything
            if (fullResponse.Length > 0)
            {
                fullResponse.AppendLine();
                fullResponse.AppendLine();
                fullResponse.AppendLine($"> [!WARNING] Generation was interrupted: {ex.Message}");
                return fullResponse.ToString();
            }
            throw new InvalidOperationException($"Gemini API error: {ex.Message}", ex);
        }

        // Warn if the model stopped due to token limits
        if (lastFinishReason != null &&
            !lastFinishReason.Equals("Stop", StringComparison.OrdinalIgnoreCase) &&
            !lastFinishReason.Equals("STOP", StringComparison.OrdinalIgnoreCase))
        {
            fullResponse.AppendLine();
            fullResponse.AppendLine();
            fullResponse.AppendLine($"> [!WARNING] AI output was truncated (reason: {lastFinishReason}). The notes above may be incomplete.");
        }

        return fullResponse.ToString();
    }
}
