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

    /// <summary>
    /// Generates chunked audio from text bypassing the 10k TPM free tier limit.
    /// </summary>
    public async Task<string> GenerateSpeechAsync(string text, string outputPath, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        EnsureModelConfigured();

        // Target ~5000 characters per chunk to prevent 60-second gateway drops.
        var textChunks = ChunkText(text, 5000);
        var wavBuffers = new List<byte[]>();

        for (int i = 0; i < textChunks.Count; i++)
        {
            if (i > 0)
            {
                progress?.Report($"API Quota limit hit. Waiting 60s for timer to reset (Chunk {i + 1} of {textChunks.Count})...");
                try { await Task.Delay(TimeSpan.FromSeconds(61), ct); } catch (TaskCanceledException) { }
            }
            else
            {
                progress?.Report($"Generating audio segment {i + 1} of {textChunks.Count}...");
            }

            ct.ThrowIfCancellationRequested();

            var chunkBase64 = await CallTtsApiCoreAsync(textChunks[i], ct);
            wavBuffers.Add(Convert.FromBase64String(chunkBase64));
        }

        progress?.Report("Stitching audio files together...");
        var finalWav = CombinePcmIntoWav(wavBuffers);
        await File.WriteAllBytesAsync(outputPath, finalWav, ct);

        return outputPath;
    }

    // ── Private helpers ───────────────────────────────────────────────

    private async Task<string> CallTtsApiCoreAsync(string text, CancellationToken ct)
    {
        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text } } } },
            generationConfig = new { responseModalities = new[] { "AUDIO" } }
        };

        var jsonPayload = System.Text.Json.JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash-preview-tts:generateContent?key={_apiKey}";
        var response = await _httpClient.PostAsync(url, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new Exception($"TTS request failed ({response.StatusCode}): {err}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var jsonDoc = System.Text.Json.JsonDocument.Parse(responseJson);

        try
        {
            var b64 = jsonDoc.RootElement.GetProperty("candidates")[0]
                        .GetProperty("content").GetProperty("parts")[0]
                        .GetProperty("inlineData").GetProperty("data").GetString();
            if (b64 == null) throw new Exception("Audio data is null.");
            return b64;
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to parse TTS response: " + ex.Message);
        }
    }

    private static List<string> ChunkText(string text, int maxTokenLength)
    {
        var chunks = new List<string>();
        // Quick split by period or newline for natural sentences
        var sentences = Regex.Split(text, @"(?<=[.!?\n])\s+");
        var currentChunk = new StringBuilder();

        foreach (var sentence in sentences)
        {
            if (currentChunk.Length + sentence.Length > maxTokenLength && currentChunk.Length > 0)
            {
                chunks.Add(currentChunk.ToString().Trim());
                currentChunk.Clear();
            }
            currentChunk.Append(sentence).Append(' ');
        }
        if (currentChunk.Length > 0) chunks.Add(currentChunk.ToString().Trim());
        return chunks;
    }

    private static byte[] CombinePcmIntoWav(List<byte[]> pcmChunks, int sampleRate = 24000, short channels = 1, short bitsPerSample = 16)
    {
        var totalDataSize = pcmChunks.Sum(c => c.Length);
        var finalBytes = new byte[44 + totalDataSize];
        
        // 0-3: "RIFF"
        finalBytes[0] = (byte)'R'; finalBytes[1] = (byte)'I'; finalBytes[2] = (byte)'F'; finalBytes[3] = (byte)'F';
        
        // 4-7: FileSize = 36 + totalDataSize
        var fileSize = 36 + totalDataSize;
        Buffer.BlockCopy(BitConverter.GetBytes(fileSize), 0, finalBytes, 4, 4);
        
        // 8-11: "WAVE"
        finalBytes[8] = (byte)'W'; finalBytes[9] = (byte)'A'; finalBytes[10] = (byte)'V'; finalBytes[11] = (byte)'E';
        
        // 12-15: "fmt "
        finalBytes[12] = (byte)'f'; finalBytes[13] = (byte)'m'; finalBytes[14] = (byte)'t'; finalBytes[15] = (byte)' ';
        
        // 16-19: Subchunk1Size = 16 (for PCM)
        Buffer.BlockCopy(BitConverter.GetBytes(16), 0, finalBytes, 16, 4);
        
        // 20-21: AudioFormat = 1 (PCM)
        Buffer.BlockCopy(BitConverter.GetBytes((short)1), 0, finalBytes, 20, 2);
        
        // 22-23: NumChannels
        Buffer.BlockCopy(BitConverter.GetBytes(channels), 0, finalBytes, 22, 2);
        
        // 24-27: SampleRate
        Buffer.BlockCopy(BitConverter.GetBytes(sampleRate), 0, finalBytes, 24, 4);
        
        // 28-31: ByteRate = SampleRate * channels * bytesPerSample
        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        Buffer.BlockCopy(BitConverter.GetBytes(byteRate), 0, finalBytes, 28, 4);
        
        // 32-33: BlockAlign = channels * bytesPerSample
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        Buffer.BlockCopy(BitConverter.GetBytes(blockAlign), 0, finalBytes, 32, 2);
        
        // 34-35: BitsPerSample
        Buffer.BlockCopy(BitConverter.GetBytes(bitsPerSample), 0, finalBytes, 34, 2);
        
        // 36-39: "data"
        finalBytes[36] = (byte)'d'; finalBytes[37] = (byte)'a'; finalBytes[38] = (byte)'t'; finalBytes[39] = (byte)'a';
        
        // 40-43: Subchunk2Size (data size)
        Buffer.BlockCopy(BitConverter.GetBytes(totalDataSize), 0, finalBytes, 40, 4);
        
        // Append raw PCM data
        int offset = 44;
        foreach (var chunk in pcmChunks)
        {
            Buffer.BlockCopy(chunk, 0, finalBytes, offset, chunk.Length);
            offset += chunk.Length;
        }
        
        return finalBytes;
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
