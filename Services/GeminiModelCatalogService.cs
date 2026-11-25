using LectureSmith.Models;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LectureSmith.Services;

/// <summary>
/// Discovers available Gemini models from Google's REST API and enriches them with live
/// pricing/tier/capability data scraped from Google AI Studio's official markdown documentation.
/// Strictly filters to text-generation LLMs suitable for lecture note synthesis and Q&A.
/// </summary>
public partial class GeminiModelCatalogService : IGeminiModelCatalogService
{
    private const string ModelsApiUrl = "https://generativelanguage.googleapis.com/v1beta/models";
    private const string PricingDocUrl = "https://ai.google.dev/gemini-api/docs/pricing.md.txt";
    private const string ModelsDocUrl = "https://ai.google.dev/gemini-api/docs/models.md.txt";

    private static readonly TimeSpan DocsCacheTtl = TimeSpan.FromHours(6);

    public static readonly GeminiModelInfo OfflineErrorModel =
        new("", "✗ Cannot fetch models (Offline or invalid key)", false);

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _docsLock = new(1, 1);
    private Dictionary<string, ScrapedModelMetadata>? _cachedDocMetadata;
    private DateTime _cacheTimestampUtc;

    public sealed record ScrapedModelMetadata(
        bool? IsFree,
        bool IsDeprecated,
        bool IsShutDown,
        bool IsNonTextSpecialized,
        string? PricingSummary);

    public GeminiModelCatalogService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<(bool IsKeyValid, string ValidationMessage, List<GeminiModelInfo> Models)> ValidateAndFetchModelsAsync(
        string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (false, "API key is not configured.", [OfflineErrorModel]);

        try
        {
            // Fetch live REST model list (paginated) and official docs metadata concurrently
            var docsTask = GetScrapedDocsMetadataAsync(ct);
            var (isValid, validationError, apiModels) = await FetchRestModelsAsync(apiKey.Trim(), ct);

            if (!isValid)
                return (false, validationError, [OfflineErrorModel]);

            var docMeta = await docsTask;
            var filtered = new List<GeminiModelInfo>();

            foreach (var raw in apiModels)
            {
                docMeta.TryGetValue(raw.Id, out var meta);
                var classified = ClassifyTextGenerationModel(raw, meta);
                if (classified != null)
                    filtered.Add(classified);
            }

            if (filtered.Count == 0)
                return (true, "API key is valid, but no compatible text models were returned.", [OfflineErrorModel]);

            var sorted = SortModels(filtered);
            return (true, string.Empty, sorted);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Connection error: {ex.Message}", [OfflineErrorModel]);
        }
    }

    private sealed record RawApiModel(
        string Id,
        string DisplayName,
        IReadOnlyList<string> SupportedMethods,
        int? InputTokenLimit,
        int? OutputTokenLimit,
        bool? ThinkingSupported,
        string Description);

    private async Task<(bool IsValid, string Error, List<RawApiModel> Models)> FetchRestModelsAsync(
        string apiKey, CancellationToken ct)
    {
        var results = new List<RawApiModel>();
        string? pageToken = null;

        do
        {
            var url = string.IsNullOrEmpty(pageToken)
                ? $"{ModelsApiUrl}?pageSize=100"
                : $"{ModelsApiUrl}?pageSize=100&pageToken={Uri.EscapeDataString(pageToken)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);

            using var response = await _httpClient.SendAsync(req, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
                    body.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, "Invalid API key.", []);
                }

                return (false, $"Validation failed ({(int)response.StatusCode} {response.StatusCode}).", []);
            }

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var el in modelsArray.EnumerateArray())
                {
                    var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var id = name.StartsWith("models/", StringComparison.Ordinal) ? name["models/".Length..] : name;
                    if (string.IsNullOrWhiteSpace(id)) continue;

                    var dispName = el.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? id : id;
                    var desc = el.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

                    var methods = new List<string>();
                    if (el.TryGetProperty("supportedGenerationMethods", out var mArr))
                    {
                        foreach (var m in mArr.EnumerateArray())
                        {
                            var ms = m.GetString();
                            if (!string.IsNullOrEmpty(ms)) methods.Add(ms);
                        }
                    }

                    int? inputLimit = el.TryGetProperty("inputTokenLimit", out var itl) && itl.TryGetInt32(out var itlVal) ? itlVal : null;
                    int? outputLimit = el.TryGetProperty("outputTokenLimit", out var otl) && otl.TryGetInt32(out var otlVal) ? otlVal : null;
                    bool? thinking = el.TryGetProperty("thinking", out var th) && th.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? th.GetBoolean()
                        : null;

                    results.Add(new RawApiModel(id, dispName, methods, inputLimit, outputLimit, thinking, desc));
                }
            }

            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var next)
                ? next.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return (true, string.Empty, results);
    }

    private async Task<Dictionary<string, ScrapedModelMetadata>> GetScrapedDocsMetadataAsync(CancellationToken ct)
    {
        await _docsLock.WaitAsync(ct);
        try
        {
            if (_cachedDocMetadata != null && DateTime.UtcNow - _cacheTimestampUtc < DocsCacheTtl)
                return _cachedDocMetadata;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));

            string? pricingMd = null;
            string? modelsMd = null;

            try
            {
                var pricingTask = _httpClient.GetStringAsync(PricingDocUrl, timeoutCts.Token);
                var modelsTask = _httpClient.GetStringAsync(ModelsDocUrl, timeoutCts.Token);
                await Task.WhenAll(
                    pricingTask.ContinueWith(t => pricingMd = t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default),
                    modelsTask.ContinueWith(t => modelsMd = t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default));
            }
            catch
            {
                // Graceful fallback to heuristic classification if docs site is unreachable
            }

            var merged = MergeDocMetadata(pricingMd, modelsMd);
            if (merged.Count > 0)
            {
                _cachedDocMetadata = merged;
                _cacheTimestampUtc = DateTime.UtcNow;
            }

            return merged;
        }
        finally
        {
            _docsLock.Release();
        }
    }

    internal static Dictionary<string, ScrapedModelMetadata> MergeDocMetadata(string? pricingMd, string? modelsMd)
    {
        var result = new Dictionary<string, ScrapedModelMetadata>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(pricingMd))
        {
            foreach (var (id, meta) in ParsePricingMarkdown(pricingMd))
                result[id] = meta;
        }

        if (!string.IsNullOrWhiteSpace(modelsMd))
        {
            foreach (var (id, mMeta) in ParseModelsMarkdown(modelsMd))
            {
                if (result.TryGetValue(id, out var existing))
                {
                    result[id] = existing with
                    {
                        // Trust pricing.md's dedicated section for deprecation unless models.md explicitly marks it shut down
                        IsDeprecated = existing.IsDeprecated || mMeta.IsShutDown,
                        IsShutDown = existing.IsShutDown || mMeta.IsShutDown,
                        IsNonTextSpecialized = existing.IsNonTextSpecialized || mMeta.IsNonTextSpecialized
                    };
                }
                else
                {
                    result[id] = mMeta;
                }
            }
        }

        return result;
    }

    internal static Dictionary<string, ScrapedModelMetadata> ParsePricingMarkdown(string markdown)
    {
        var map = new Dictionary<string, ScrapedModelMetadata>(StringComparer.OrdinalIgnoreCase);
        var sections = SectionSplitRegex().Split(markdown);

        // Skip preamble before first "## "
        for (int i = 1; i < sections.Length; i++)
        {
            var section = sections[i];
            var headingEnd = section.IndexOf('\n');
            var heading = headingEnd > 0 ? section[..headingEnd] : section;

            // Isolate header block before "{% hr" or "{% callout" so replacement model IDs inside deprecation callouts aren't scraped
            var headerSliceEnd = section.Length;
            var hrIdx = section.IndexOf("{% hr", StringComparison.Ordinal);
            if (hrIdx > 0) headerSliceEnd = Math.Min(headerSliceEnd, hrIdx);
            var calloutIdx = section.IndexOf("{% callout", StringComparison.Ordinal);
            if (calloutIdx > 0) headerSliceEnd = Math.Min(headerSliceEnd, calloutIdx);

            var headerSlice = section[..headerSliceEnd];
            var modelIds = ModelCodeLinkRegex().Matches(headerSlice)
                .Select(m => m.Groups[1].Value.Trim())
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (modelIds.Count == 0) continue;

            var isDeprecated = section.Contains("[deprecated]", StringComparison.OrdinalIgnoreCase) ||
                               section.Contains("is deprecated", StringComparison.OrdinalIgnoreCase);
            var isShutDown = section.Contains("(Shut down)", StringComparison.OrdinalIgnoreCase);
            var isNonText = IsNonTextTitleOrId(heading) || modelIds.Any(IsNonTextTitleOrId);

            bool? isFree = DetermineFreeTierFromPricingSection(section);
            var priceSummary = ExtractPaidPriceSummary(section, isFree);

            var meta = new ScrapedModelMetadata(isFree, isDeprecated, isShutDown, isNonText, priceSummary);
            foreach (var id in modelIds)
                map[id] = meta;
        }

        return map;
    }

    internal static Dictionary<string, ScrapedModelMetadata> ParseModelsMarkdown(string markdown)
    {
        var map = new Dictionary<string, ScrapedModelMetadata>(StringComparer.OrdinalIgnoreCase);
        var sections = SectionSplitRegex().Split(markdown);

        for (int i = 1; i < sections.Length; i++)
        {
            var section = sections[i];
            var headingEnd = section.IndexOf('\n');
            var heading = headingEnd > 0 ? section[..headingEnd].Trim() : string.Empty;

            // Skip documentation/prose sections that only cite model IDs as examples
            if (heading.StartsWith("Model version", StringComparison.OrdinalIgnoreCase) ||
                heading.StartsWith("Model deprecation", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var inPreviousModels = heading.StartsWith("Previous models", StringComparison.OrdinalIgnoreCase);
            var isSpecializedSection = heading.StartsWith("Specialized task models", StringComparison.OrdinalIgnoreCase);

            // Parse table rows / bullet lists cleanly
            var blocks = section.Split("---");
            foreach (var block in blocks)
            {
                var ids = BacktickModelIdRegex().Matches(block)
                    .Select(m => m.Groups[1].Value.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (ids.Count == 0) continue;

                var blockShutDown = block.Contains("(Shut down)", StringComparison.OrdinalIgnoreCase);
                var blockHasDepText = inPreviousModels || block.Contains("is [deprecated]", StringComparison.OrdinalIgnoreCase);

                foreach (var id in ids)
                {
                    var nonText = isSpecializedSection || IsNonTextTitleOrId(id) ||
                                  block.Contains("text-to-speech", StringComparison.OrdinalIgnoreCase) ||
                                  block.Contains("speech-to-text", StringComparison.OrdinalIgnoreCase) ||
                                  block.Contains("image generation", StringComparison.OrdinalIgnoreCase) ||
                                  block.Contains("embedding", StringComparison.OrdinalIgnoreCase) ||
                                  block.Contains("Live API", StringComparison.OrdinalIgnoreCase) ||
                                  (block.Contains("video", StringComparison.OrdinalIgnoreCase) && block.Contains("Omni", StringComparison.OrdinalIgnoreCase));

                    // Only treat an entry as deprecated if it's in Previous models or appears in its own section (not a replacement target)
                    bool idDeprecated = inPreviousModels ||
                                        (blockHasDepText && heading.Contains(id.Replace("-", " "), StringComparison.OrdinalIgnoreCase));

                    if (map.TryGetValue(id, out var existing))
                    {
                        map[id] = existing with
                        {
                            IsDeprecated = existing.IsDeprecated || idDeprecated,
                            IsShutDown = existing.IsShutDown || blockShutDown,
                            IsNonTextSpecialized = existing.IsNonTextSpecialized || nonText
                        };
                    }
                    else
                    {
                        map[id] = new ScrapedModelMetadata(null, idDeprecated, blockShutDown, nonText, null);
                    }
                }
            }
        }

        return map;
    }

    private static bool? DetermineFreeTierFromPricingSection(string section)
    {
        if (section.Contains("is not available on the Free tier", StringComparison.OrdinalIgnoreCase) ||
            section.Contains("does not support the Free tier", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Inspect Standard tab's Input price row: `* Input price` followed by `* Free of charge` or `* Not available`
        var inputPriceMatch = InputPriceFreeCellRegex().Match(section);
        if (inputPriceMatch.Success)
        {
            var cell = inputPriceMatch.Groups[1].Value.Trim();
            if (cell.Contains("Free of charge", StringComparison.OrdinalIgnoreCase))
                return true;
            if (cell.Contains("Not available", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (section.Contains("Free of charge", StringComparison.OrdinalIgnoreCase))
            return true;

        return null;
    }

    private static string? ExtractPaidPriceSummary(string section, bool? isFree)
    {
        var inputMatch = InputPricePaidCellRegex().Match(section);
        var outputMatch = OutputPricePaidCellRegex().Match(section);

        if (inputMatch.Success && outputMatch.Success)
        {
            var inPrice = inputMatch.Groups[1].Value;
            var outPrice = outputMatch.Groups[1].Value;
            return isFree == true
                ? $"Free Tier  •  Paid {inPrice}/{outPrice} per 1M"
                : $"Paid Only  •  {inPrice}/{outPrice} per 1M";
        }

        return isFree == true ? "Free Tier Available" : isFree == false ? "Paid Tier Only" : null;
    }

    private static GeminiModelInfo? ClassifyTextGenerationModel(RawApiModel raw, ScrapedModelMetadata? docMeta)
    {
        if (docMeta?.IsShutDown == true)
            return null;

        // Must support standard text/multimodal content generation
        if (raw.SupportedMethods.Count > 0 &&
            !raw.SupportedMethods.Contains("generateContent", StringComparer.Ordinal))
        {
            return null;
        }

        // Exclude non-Gemini open-weight / legacy non-chat families (e.g., gemma, text-bison, aqa)
        if (!raw.Id.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
            return null;

        // Exclude specialized non-text-generation models (TTS, Image, Video, Live Audio, Transcribe, Embeddings, Robotics, etc.)
        if (docMeta?.IsNonTextSpecialized == true ||
            IsNonTextTitleOrId(raw.Id) ||
            IsNonTextTitleOrId(raw.DisplayName) ||
            IsNonTextDescription(raw.Description))
        {
            return null;
        }

        // Exclude internal custom-tools / experimental duplicate variants that clutter standard note generation
        if (raw.Id.Contains("customtools", StringComparison.OrdinalIgnoreCase))
            return null;

        bool isFree = docMeta?.IsFree ?? FallbackIsFreeTier(raw.Id, raw.DisplayName);
        bool isDeprecated = docMeta?.IsDeprecated ?? IsLikelyDeprecated(raw.Id, raw.Description);

        // Clean display name (avoid trailing redundant descriptors)
        var cleanDisplay = raw.DisplayName.Trim();
        if (string.IsNullOrWhiteSpace(cleanDisplay))
            cleanDisplay = raw.Id;

        bool supportsThinking = raw.ThinkingSupported ?? SupportsThinkingHeuristic(raw.Id);

        var summary = docMeta?.PricingSummary ??
                      (isFree ? "Free Tier Available" : "Paid Tier Only");

        return new GeminiModelInfo(
            Id: raw.Id,
            DisplayName: cleanDisplay,
            IsFree: isFree,
            OutputTokenLimit: raw.OutputTokenLimit,
            InputTokenLimit: raw.InputTokenLimit,
            SupportsThinking: supportsThinking,
            IsDeprecated: isDeprecated,
            PricingSummary: summary);
    }

    private static bool IsNonTextTitleOrId(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("tts") ||
               lower.Contains("speech") ||
               lower.Contains("transcribe") ||
               lower.Contains("native-audio") ||
               lower.Contains("native audio") ||
               lower.Contains("-audio") ||
               lower.Contains("-live") ||
               lower.Contains(" live") ||
               lower.Contains("image") ||
               lower.Contains("imagen") ||
               lower.Contains("banana") ||
               lower.Contains("nano") ||
               lower.Contains("omni") ||
               lower.Contains("genie") ||
               lower.Contains("veo") ||
               lower.Contains("lyria") ||
               lower.Contains("embedding") ||
               lower.Contains("robotics") ||
               lower.Contains("computer-use") ||
               lower.Contains("computer use") ||
               lower.Contains("deep-research") ||
               lower.Contains("deep research") ||
               lower.Contains("decision") ||
               lower.Contains("aqa") ||
               lower.Contains("bisect");
    }

    private static bool IsNonTextDescription(string description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        var lower = description.ToLowerInvariant();
        return lower.Contains("text-to-speech") ||
               lower.Contains("speech-to-text") ||
               lower.Contains("image generation") ||
               lower.Contains("video generation") ||
               lower.Contains("generate music") ||
               lower.Contains("embedding");
    }

    internal static bool FallbackIsFreeTier(string modelId, string displayName)
    {
        var combined = $"{modelId} {displayName}".ToLowerInvariant();

        // Gemini 4: only Hydrogen is on the Free tier; Carbon/Argon/Neon are Paid-only
        if (combined.Contains("gemini-4") || combined.Contains("gemini 4"))
            return combined.Contains("hydrogen");

        // Gemini 2.5/3.x: Pro/Ultra/Max are Paid-only; Flash/Flash-Lite are Free-tier
        if (combined.Contains("pro") || combined.Contains("ultra") || combined.Contains("max") ||
            combined.Contains("carbon") || combined.Contains("argon") || combined.Contains("neon"))
        {
            return false;
        }

        return combined.Contains("flash") || combined.Contains("lite") || combined.Contains("hydrogen");
    }

    private static bool IsLikelyDeprecated(string modelId, string description)
    {
        var lower = modelId.ToLowerInvariant();
        return lower.Contains("gemini-1.0") ||
               lower.Contains("gemini-1.5") ||
               lower.Contains("gemini-2.0") ||
               lower.Contains("gemini-2.5") ||
               description.Contains("deprecated", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SupportsThinkingHeuristic(string modelId)
    {
        var lower = modelId.ToLowerInvariant();
        if (lower.Contains("1.0") || lower.Contains("1.5"))
            return false;
        return lower.Contains("gemini-4") ||
               lower.Contains("gemini-3") ||
               lower.Contains("gemini-2.5") ||
               lower.Contains("thinking");
    }

    private static List<GeminiModelInfo> SortModels(List<GeminiModelInfo> models)
    {
        return models
            .OrderBy(m => m.IsDeprecated ? 1 : 0)
            .ThenBy(m => m.IsFree ? 0 : 1)
            .ThenByDescending(m => ParseGenerationRank(m.Id))
            .ThenBy(m => GetVariantOrder(m.Id))
            .ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static double ParseGenerationRank(string id)
    {
        var m = GenerationNumberRegex().Match(id);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var ver))
            return ver;
        return 0;
    }

    private static int GetVariantOrder(string id)
    {
        var lower = id.ToLowerInvariant();
        if (lower.Contains("latest")) return 4;
        if (lower.Contains("preview") || lower.Contains("exp")) return 3;
        if (lower.Contains("lite")) return 2;
        if (lower.Contains("hydrogen") || lower.Contains("flash")) return 0;
        if (lower.Contains("carbon")) return 0;
        if (lower.Contains("neon")) return 1;
        if (lower.Contains("argon")) return 2;
        return 1;
    }

    [GeneratedRegex(@"(?m)^##\s+")]
    private static partial Regex SectionSplitRegex();

    [GeneratedRegex(@"\*\[`([a-z0-9\-\.]+)`\]\(/docs/models/")]
    private static partial Regex ModelCodeLinkRegex();

    [GeneratedRegex(@"`((?:gemini|gemma|lyria|genie)[a-z0-9\-\.]*)`")]
    private static partial Regex BacktickModelIdRegex();

    [GeneratedRegex(@"\*\s*Input price\s*\r?\n\s*\*\s*([^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InputPriceFreeCellRegex();

    [GeneratedRegex(@"\*\s*Input price\s*\r?\n\s*\*\s*[^\r\n]+\r?\n\s*\*\s*(\$[0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex InputPricePaidCellRegex();

    [GeneratedRegex(@"\*\s*Output price[^\r\n]*\r?\n\s*\*\s*[^\r\n]+\r?\n\s*\*\s*(\$[0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex OutputPricePaidCellRegex();

    [GeneratedRegex(@"gemini-(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex GenerationNumberRegex();
}
