using LectureSmith.Models;

namespace LectureSmith.Services;

public record TokenUsage(int InputTokens, int OutputTokens);

public interface ISettingsService
{
    AppSettings Settings { get; }
    string GetApiKey();
    void SetApiKey(string apiKey);
    void Load();
    void Save();
    void Reset();
}

public interface IHistoryService
{
    List<HistorySession> LoadHistory();
    void SaveSession(HistorySession session);
    void DeleteSession(string sessionId);
    void ClearHistory();
}

public interface IGeminiModelCatalogService
{
    Task<(bool IsKeyValid, string ValidationMessage, List<GeminiModelInfo> Models)> ValidateAndFetchModelsAsync(
        string apiKey, CancellationToken ct = default);
}

public interface IGeminiChatHandle
{
    Task<string> SendMessageAsync(string message, IProgress<string>? progress = null, CancellationToken ct = default);
}

public interface IGeminiService
{
    bool IsConfigured { get; }
    void SetApiKey(string apiKey);
    void SetModel(string modelId, bool thinkingEnabled = false, int? outputTokenLimit = null, bool? supportsThinking = null);
    Task<(string Text, TokenUsage Usage)> GenerateAsync(string systemPrompt, string userContent,
        IProgress<string>? progress = null, CancellationToken ct = default);
    Task<(string Text, TokenUsage Usage)> GenerateWithImagesAsync(string systemPrompt, string userContent,
        List<string> imagePaths, IProgress<string>? progress = null, CancellationToken ct = default);
    IGeminiChatHandle CreateChatSession(string systemPrompt, string initialContext,
        IEnumerable<ChatMessage>? previousMessages = null);
}

public interface IPdfExtractorService
{
    Task<List<string>> ExtractTextAsync(string pdfPath, int? startPage = null, int? endPage = null, CancellationToken ct = default);
    Task<List<string>> ExtractSlideImagesAsync(string pdfPath, string outputDir,
        IProgress<(int current, int total)>? progress = null, CancellationToken ct = default);
    Task<List<string>> ExtractSlidePreviewsAsync(string pdfPath, string outputDir,
        IProgress<(int current, int total)>? progress = null, CancellationToken ct = default);
    Task<int> GetPageCountAsync(string pdfPath, CancellationToken ct = default);
}

public interface ISlideCacheService
{
    Task<List<string>> GetOrExtractSlidesAsync(string pdfPath, bool previewQuality,
        IProgress<(int current, int total)>? progress = null, CancellationToken ct = default);
}

public interface IOcrService : IDisposable
{
    Task EnsureTrainedDataAsync(IProgress<string>? status = null, CancellationToken ct = default);
    Task<List<string>> ExtractTextFromImagesAsync(List<string> imagePaths,
        IProgress<(int current, int total)>? progress = null, CancellationToken ct = default);
}

public interface INoteGeneratorService
{
    Task<GenerationResult> GenerateAsync(GenerationSettings settings,
        IProgress<ProgressUpdate>? progress = null, IProgress<string>? liveText = null,
        Action<string>? onResetPreview = null, Action<IReadOnlyList<string>>? onSlideImagesReady = null,
        CancellationToken ct = default);
}

public interface IOutputExporterService
{
    Task<string> ExportAsync(GenerationResult result, GenerationSettings settings, CancellationToken ct = default);
}

public interface IClipboardService
{
    Task SetTextAsync(string text);
}

public interface IFileLauncherService
{
    void OpenFile(string filePath);
    void OpenFolderAndSelect(string filePath);
}
