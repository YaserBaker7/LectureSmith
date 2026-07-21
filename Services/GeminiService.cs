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
    public static readonly GeminiModelInfo OfflineErrorModel = new("", "✗ Cannot fetch models (Offline or invalid key)", false);
    public static readonly GeminiModelInfo[] AvailableModels = [];

    public static GeminiModelInfo? FindModelById(IEnumerable<GeminiModelInfo> models, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return models.FirstOrDefault();

        return models.FirstOrDefault(m => m.Id == modelId) ?? models.FirstOrDefault();
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
            Temperature = 0.7f
        };

        if (thinkingEnabled)
        {
            genConfig.ThinkingConfig = new ThinkingConfig
            {
                ThinkingBudget = -1
            };
        }

        _model = _googleAi.GenerativeModel(
            model: modelId,
            generationConfig: genConfig);
    }

    /// <summary>
    /// Generates text-only content using the Gemini API with exponential backoff retries.
    /// </summary>
    public async Task<string> GenerateAsync(string systemPrompt, string userContent,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureModelConfigured();

        var combinedPrompt = BuildCombinedPrompt(systemPrompt, userContent);

        return await ExecuteWithRetryAsync(async () =>
        {
            var response = _model!.GenerateContentStream(combinedPrompt);
            return await StreamResponseAsync(response, progress, ct);
        });
    }

    /// <summary>
    /// Generates content with inline slide images using the Gemini multimodal API with exponential backoff retries.
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

        return await ExecuteWithRetryAsync(async () =>
        {
            var response = _model!.GenerateContentStream(request);
            return await StreamResponseAsync(response, progress, ct);
        });
    }

    private static async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, int maxRetries = 3)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt <= maxRetries && (ex.Message.Contains("429") || ex.Message.Contains("Quota") || ex.Message.Contains("503") || ex.Message.Contains("500")))
            {
                await Task.Delay((int)Math.Pow(2, attempt) * 1000);
            }
        }
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

    /// <summary>
    /// Dynamically fetches available Gemini models from Google AI Studio.
    /// Tags models with Free (Flash/Lite) vs Paid (Pro/Preview) indicators.
    /// </summary>
    public async Task<List<GeminiModelInfo>> FetchAvailableModelsAsync()
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) return [.. AvailableModels];

        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models?key={_apiKey}";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return [OfflineErrorModel];

            var json = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            var fetchedList = new List<GeminiModelInfo>();

            if (doc.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var modelElement in modelsArray.EnumerateArray())
                {
                    var name = modelElement.GetProperty("name").GetString() ?? "";
                    var id = name.StartsWith("models/") ? name.Substring("models/".Length) : name;

                    var dispName = modelElement.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? id : id;

                    if (!IsAllowedTextModel(id, dispName, out bool isFree)) continue;

                    // Filter only models that support generateContent
                    if (modelElement.TryGetProperty("supportedGenerationMethods", out var methods))
                    {
                        bool supportsGen = false;
                        foreach (var m in methods.EnumerateArray())
                        {
                            if (m.GetString() == "generateContent") { supportsGen = true; break; }
                        }
                        if (!supportsGen) continue;
                    }

                    fetchedList.Add(new GeminiModelInfo(id, $"{dispName}", isFree));
                }
            }

            return fetchedList.Count > 0 ? fetchedList : [OfflineErrorModel];
        }
        catch
        {
            return [OfflineErrorModel];
        }
    }

    private static bool IsAllowedTextModel(string id, string dispName, out bool isFree)
    {
        var combined = $"{id} {dispName}".ToLowerInvariant();
        isFree = false;

        // Disallow non-text / specialized models (Lyria, TTS, Robotics, Banana, Nano, Computer Use, Deep Research, Agent, Imagen, Audio, Speech)
        if (combined.Contains("lyria") || combined.Contains("tts") || combined.Contains("speech") ||
            combined.Contains("audio") || combined.Contains("imagen") || combined.Contains("banana") ||
            combined.Contains("nano") || combined.Contains("robotics") || combined.Contains("computer") ||
            combined.Contains("deep research") || combined.Contains("deep-research") || combined.Contains("agent") ||
            combined.Contains("omni") || combined.Contains("embedding"))
        {
            return false;
        }

        // Must be a Gemini text model
        if (!combined.Contains("gemini")) return false;

        // Free tier models: Flash / Lite models
        // Paid tier models: Pro / Max / Ultra models
        isFree = combined.Contains("flash") || combined.Contains("lite");
        return true;
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
