using LectureSmith.Models;
using Mscc.GenerativeAI;
using System.Net.Http;
using System.Text;

namespace LectureSmith.Services;

public class GeminiService : IGeminiService
{
    private string _apiKey = string.Empty;
    private string _modelId = string.Empty;
    private bool _thinkingEnabled;
    private int? _outputTokenLimit;
    private bool? _supportsThinking;
    private GenerativeModel? _model;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey) && _model != null;

    public void SetApiKey(string apiKey)
    {
        _apiKey = apiKey?.Trim() ?? string.Empty;
        _model = null;
    }

    public void SetModel(string modelId, bool thinkingEnabled = false, int? outputTokenLimit = null, bool? supportsThinking = null)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(modelId))
            return;

        _modelId = modelId;
        _thinkingEnabled = thinkingEnabled;
        _outputTokenLimit = outputTokenLimit;
        _supportsThinking = supportsThinking;
        _model = CreateConfiguredModel(systemInstruction: null);
    }

    private GenerativeModel CreateConfiguredModel(string? systemInstruction)
    {
        var googleAi = new GoogleAI(_apiKey);
        var lowerId = _modelId.ToLowerInvariant();

        int? maxTokens = _outputTokenLimit;
        if (!maxTokens.HasValue)
        {
            maxTokens = (lowerId.Contains("1.5") || lowerId.Contains("1.0")) ? 8192 : 65536;
        }
        else if (maxTokens.Value > 65536)
        {
            // Keep synchronous single-pass output capped at 65,536 tokens
            maxTokens = 65536;
        }

        var genConfig = new GenerationConfig
        {
            MaxOutputTokens = maxTokens,
            Temperature = 0.7f
        };

        bool thinkingCapable = _supportsThinking ??
            (lowerId.Contains("gemini-4") || lowerId.Contains("gemini-3") ||
             lowerId.Contains("gemini-2.5") || lowerId.Contains("gemini-2.0") ||
             lowerId.Contains("thinking"));

        if (_thinkingEnabled && thinkingCapable)
        {
            genConfig.ThinkingConfig = new ThinkingConfig
            {
                ThinkingBudget = -1
            };
        }

        var sysContent = string.IsNullOrWhiteSpace(systemInstruction)
            ? null
            : new Content(systemInstruction);

        var model = googleAi.GenerativeModel(
            model: _modelId,
            generationConfig: genConfig,
            systemInstruction: sysContent);

        // BaseModel shares a static HttpClient; Timeout can only be set before the first HTTP request.
        try
        {
            if (model.Timeout != TimeSpan.FromMinutes(15))
                model.Timeout = TimeSpan.FromMinutes(15);
        }
        catch (InvalidOperationException)
        {
        }

        return model;
    }

    public async Task<(string Text, TokenUsage Usage)> GenerateAsync(
        string systemPrompt, string userContent,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureModelConfigured();

        return await ExecuteWithRetryAsync(async () =>
        {
            var model = CreateConfiguredModel(systemPrompt);
            var response = model.GenerateContentStream(userContent, cancellationToken: ct);
            return await StreamResponseAsync(response, progress, allowPartialOnError: true, ct);
        }, 3, ct);
    }

    public async Task<(string Text, TokenUsage Usage)> GenerateWithImagesAsync(
        string systemPrompt, string userContent, List<string> imagePaths,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureModelConfigured();

        var request = new GenerateContentRequest(userContent);
        foreach (var imagePath in imagePaths)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(imagePath))
            {
                await request.AddMedia(imagePath);
            }
        }

        return await ExecuteWithRetryAsync(async () =>
        {
            var model = CreateConfiguredModel(systemPrompt);
            var response = model.GenerateContentStream(request, cancellationToken: ct);
            return await StreamResponseAsync(response, progress, allowPartialOnError: true, ct);
        }, 3, ct);
    }

    /// <summary>
    /// Creates an isolated chat handle primed with the lecture notes and optional prior chat messages.
    /// Workspace and History chats each hold their own handle so they never overwrite one another.
    /// </summary>
    public IGeminiChatHandle CreateChatSession(
        string systemPrompt, string initialContext, IEnumerable<LectureSmith.Models.ChatMessage>? previousMessages = null)
    {
        EnsureModelConfigured();

        var model = CreateConfiguredModel(systemPrompt);
        var historyList = new List<ContentResponse>
        {
            new($"Here are the lecture notes to reference:\n\n{initialContext}", "user"),
            new("I've reviewed the lecture notes. I'm ready to answer any follow-up questions you have about this material. What would you like to know?", "model")
        };

        if (previousMessages != null)
        {
            foreach (var msg in previousMessages)
            {
                if (!msg.IsUser && msg.Content.StartsWith("I've reviewed", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrWhiteSpace(msg.Content))
                    continue;

                historyList.Add(new ContentResponse(msg.Content, msg.IsUser ? "user" : "model"));
            }
        }

        var chat = model.StartChat(history: historyList);
        return new GeminiChatHandle(chat);
    }

    private sealed class GeminiChatHandle : IGeminiChatHandle
    {
        private readonly ChatSession _chatSession;

        public GeminiChatHandle(ChatSession chatSession)
        {
            _chatSession = chatSession;
        }

        public async Task<string> SendMessageAsync(
            string message, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            return await ExecuteWithRetryAsync(async () =>
            {
                int historyCountBefore = _chatSession.History?.Count ?? 0;
                try
                {
                    var response = _chatSession.SendMessageStream(message, cancellationToken: ct);
                    var (text, _) = await StreamResponseAsync(response, progress, allowPartialOnError: false, ct);
                    return text;
                }
                catch
                {
                    // Roll back any uncompleted turn appended by the SDK before retrying or propagating
                    if (_chatSession.History != null && _chatSession.History.Count > historyCountBefore)
                    {
                        _chatSession.History.RemoveRange(historyCountBefore, _chatSession.History.Count - historyCountBefore);
                    }
                    throw;
                }
            }, 3, ct);
        }
    }

    private static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> action, int maxRetries = 3, CancellationToken ct = default)
    {
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await action();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("Generation was cancelled.", ex, ct);
            }
            catch (Exception ex) when (attempt <= maxRetries && IsTransientError(ex))
            {
                await Task.Delay((int)Math.Pow(2, attempt) * 1000, ct);
            }
        }
    }

    private static bool IsTransientError(Exception ex)
    {
        return ex.Message.Contains("429", StringComparison.Ordinal) ||
               ex.Message.Contains("Quota", StringComparison.OrdinalIgnoreCase) ||
               ex.Message.Contains("503", StringComparison.Ordinal) ||
               ex.Message.Contains("500", StringComparison.Ordinal) ||
               ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase) ||
               ex is HttpRequestException || ex.InnerException is HttpRequestException ||
               ex is TimeoutException || ex.InnerException is TimeoutException ||
               ex is TaskCanceledException || ex.InnerException is TaskCanceledException ||
               ex is IOException || ex.InnerException is IOException;
    }

    private void EnsureModelConfigured()
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _model == null || string.IsNullOrWhiteSpace(_modelId))
            throw new InvalidOperationException("Model not configured. Please enter a valid API key and select a model in Settings.");
    }

    private static async Task<(string Text, TokenUsage Usage)> StreamResponseAsync(
        IAsyncEnumerable<GenerateContentResponse> response,
        IProgress<string>? progress,
        bool allowPartialOnError,
        CancellationToken ct)
    {
        var fullResponse = new StringBuilder();
        int promptTokens = 0;
        int outputTokens = 0;

        try
        {
            await foreach (var chunk in response.WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();

                if (chunk.UsageMetadata != null)
                {
                    if (chunk.UsageMetadata.PromptTokenCount > 0)
                        promptTokens = chunk.UsageMetadata.PromptTokenCount;

                    var candidateAndThoughts = chunk.UsageMetadata.CandidatesTokenCount + chunk.UsageMetadata.ThoughtsTokenCount;
                    if (candidateAndThoughts > 0)
                        outputTokens = candidateAndThoughts;
                }

                var text = chunk.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    fullResponse.Append(text);
                    progress?.Report(text);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException("Generation was cancelled.", ex, ct);

            if (allowPartialOnError && fullResponse.Length > 0)
            {
                return (fullResponse.ToString(), new TokenUsage(promptTokens, outputTokens));
            }

            throw new InvalidOperationException($"Gemini API error: {ex.Message}", ex);
        }

        return (fullResponse.ToString(), new TokenUsage(promptTokens, outputTokens));
    }
}
