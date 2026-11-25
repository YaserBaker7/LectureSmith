using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureSmith.Models;
using LectureSmith.Services;
using System.Collections.ObjectModel;
using System.Text;

namespace LectureSmith.ViewModels;

public partial class WorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly IGeminiService _geminiService;
    private readonly IPdfExtractorService _pdfExtractor;
    private readonly INoteGeneratorService _noteGenerator;
    private readonly IOutputExporterService _outputExporter;
    private readonly ISettingsService _settingsService;
    private readonly IHistoryService _historyService;
    private readonly IClipboardService _clipboard;
    private readonly IFileLauncherService _launcher;

    private readonly object _previewLock = new();
    private readonly StringBuilder _previewBuffer = new();
    private readonly DispatcherTimer _previewFlushTimer;
    private bool _previewDirty;

    private CancellationTokenSource? _generationCts;
    private CancellationTokenSource? _estimateCts;
    private string? _activeHistorySessionId;

    // === Dropdown collections ===
    public ObservableCollection<string> Courses { get; } = [];
    public OutputFormat[] OutputFormats { get; } = Enum.GetValues<OutputFormat>();
    public LectureMode[] LectureModes { get; } = Enum.GetValues<LectureMode>();
    public SlideProcessingMode[] SlideProcessingModes { get; } = Enum.GetValues<SlideProcessingMode>();
    public NoteLanguage[] Languages { get; } = Enum.GetValues<NoteLanguage>();
    public ObservableCollection<GeminiModelInfo> AvailableModels { get; } = [GeminiModelCatalogService.OfflineErrorModel];

    // === Parameters ===
    [ObservableProperty] private string _selectedCourse = string.Empty;
    [ObservableProperty] private OutputFormat _selectedFormat = OutputFormat.Obsidian;
    [ObservableProperty] private LectureMode _selectedMode = LectureMode.MissedLecture;
    [ObservableProperty] private GeminiModelInfo? _selectedModel = GeminiModelCatalogService.OfflineErrorModel;
    [ObservableProperty] private SlideProcessingMode _selectedSlideProcessing = SlideProcessingMode.TextOnly;
    [ObservableProperty] private NoteLanguage _selectedLanguage = NoteLanguage.Auto;
    [ObservableProperty] private bool _enableThinking;
    [ObservableProperty] private string _extraNotes = string.Empty;
    [ObservableProperty] private string _outputPath = string.Empty;

    // === Uploaded materials ===
    [ObservableProperty] private UploadedFile? _slidesFile;
    public ObservableCollection<UploadedFile> BookFiles { get; } = [];

    // === Status & Progress ===
    [ObservableProperty] private bool _isApiKeyValid;
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private string _progressMessage = "Ready";
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isStatusError;
    [ObservableProperty] private string _estimatedTokenInfo = string.Empty;
    [ObservableProperty] private string _livePreview = string.Empty;
    [ObservableProperty] private string _lastOutputFilePath = string.Empty;
    [ObservableProperty] private bool _isRenderedPreview = true;

    // === Follow-up Q&A ===
    [ObservableProperty] private bool _isFollowUpSessionActive;

    public MarkdownViewerViewModel MarkdownViewer { get; } = new();
    public ChatPanelViewModel Chat { get; }
    public SkipSlidesViewModel SkipSlides { get; }

    public bool HasLivePreview => !string.IsNullOrEmpty(LivePreview);
    public bool HasLastOutputFile => !string.IsNullOrWhiteSpace(LastOutputFilePath) && File.Exists(LastOutputFilePath);

    public bool CanGenerate =>
        SlidesFile != null &&
        IsApiKeyValid &&
        !IsGenerating &&
        SelectedModel != null &&
        !string.IsNullOrWhiteSpace(SelectedModel.Id) &&
        !string.IsNullOrWhiteSpace(OutputPath);

    public string GenerateBlockedReason
    {
        get
        {
            if (IsGenerating) return "Generating lecture notes...";
            if (!IsApiKeyValid) return "Configure a valid Gemini API key in Settings";
            if (SelectedModel == null || string.IsNullOrWhiteSpace(SelectedModel.Id)) return "Waiting for an available text model";
            if (SlidesFile == null) return "Upload a lecture slides PDF";
            if (string.IsNullOrWhiteSpace(OutputPath)) return "Choose an output folder";
            return string.Empty;
        }
    }

    public string SelectedSlideProcessingDescription => SelectedSlideProcessing.Description();
    public string ActiveModelDisplay => SelectedModel?.DisplayName ?? "No Model";
    public string ActiveModelSubtext => SelectedModel == null || string.IsNullOrEmpty(SelectedModel.Id)
        ? "Offline / Key Required"
        : SelectedModel.PricingSummary ?? (SelectedModel.IsFree ? "Free Tier" : "Paid Tier");

    public event Action? ModelOrStatsChanged;
    public event Action<HistorySession>? SessionRecorded;
    public event Action<string, ChatMessage, ChatMessage>? SessionChatUpdated;

    public WorkspaceViewModel(
        IGeminiService geminiService,
        IPdfExtractorService pdfExtractor,
        ISlideCacheService slideCache,
        INoteGeneratorService noteGenerator,
        IOutputExporterService outputExporter,
        ISettingsService settingsService,
        IHistoryService historyService,
        IClipboardService clipboard,
        IFileLauncherService launcher)
    {
        _geminiService = geminiService;
        _pdfExtractor = pdfExtractor;
        _noteGenerator = noteGenerator;
        _outputExporter = outputExporter;
        _settingsService = settingsService;
        _historyService = historyService;
        _clipboard = clipboard;
        _launcher = launcher;

        SkipSlides = new SkipSlidesViewModel(slideCache);
        Chat = new ChatPanelViewModel(_geminiService)
        {
            Subtitle = "AI assistant retains complete context from generated notes"
        };
        Chat.ExchangeCompleted += OnChatExchangeCompleted;

        _previewFlushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _previewFlushTimer.Tick += (_, _) => FlushPreviewBuffer();
    }

    public void LoadFromSettings()
    {
        OutputPath = _settingsService.Settings.LastOutputPath;

        Courses.Clear();
        foreach (var course in _settingsService.Settings.CourseHistory)
        {
            if (!string.IsNullOrWhiteSpace(course))
                Courses.Add(course);
        }
        SelectedCourse = Courses.FirstOrDefault() ?? string.Empty;
    }

    public void ApplyAvailableModels(List<GeminiModelInfo> models)
    {
        var currentId = !string.IsNullOrWhiteSpace(SelectedModel?.Id)
            ? SelectedModel!.Id
            : _settingsService.Settings.PreferredModelId;

        AvailableModels.Clear();
        if (models.Count > 0 && !(models.Count == 1 && string.IsNullOrEmpty(models[0].Id)))
        {
            foreach (var m in models)
                AvailableModels.Add(m);

            SelectedModel = AvailableModels.FirstOrDefault(m => m.Id == currentId)
                            ?? AvailableModels.FirstOrDefault();
        }
        else
        {
            AvailableModels.Add(GeminiModelCatalogService.OfflineErrorModel);
            SelectedModel = GeminiModelCatalogService.OfflineErrorModel;
        }
    }

    public void LoadResumedSession(HistorySession session)
    {
        LivePreview = session.MarkdownContent;
        LastOutputFilePath = session.OutputFilePath;
        OnPropertyChanged(nameof(HasLastOutputFile));

        MarkdownViewer.SetBaseOutputFolder(session.OutputFilePath);
        MarkdownViewer.UpdateMarkdown(session.MarkdownContent);

        if (!string.IsNullOrWhiteSpace(session.CourseName))
            SelectedCourse = session.CourseName;

        _activeHistorySessionId = session.Id;
        Chat.StartOrResume(
            session.MarkdownContent,
            session.ChatHistory,
            $"I've reviewed your lecture notes for '{session.DisplayTitle}'. Ask me anything about this lecture!");
        IsFollowUpSessionActive = true;
    }

    public void ResetAll()
    {
        Courses.Clear();
        SelectedCourse = string.Empty;
        OutputPath = string.Empty;
        SlidesFile = null;
        BookFiles.Clear();
        SkipSlides.Reset();
        LivePreview = string.Empty;
        MarkdownViewer.Clear();
        LastOutputFilePath = string.Empty;
        EstimatedTokenInfo = string.Empty;
        StatusMessage = string.Empty;
        IsStatusError = false;
        ProgressPercent = 0;
        ProgressMessage = "Ready";
        _activeHistorySessionId = null;
        IsFollowUpSessionActive = false;
        Chat.Clear();
        AvailableModels.Clear();
        AvailableModels.Add(GeminiModelCatalogService.OfflineErrorModel);
        SelectedModel = GeminiModelCatalogService.OfflineErrorModel;
        OnPropertyChanged(nameof(HasLastOutputFile));
    }

    partial void OnSelectedModelChanged(GeminiModelInfo? value)
    {
        if (IsStatusError)
        {
            StatusMessage = string.Empty;
            IsStatusError = false;
        }

        Chat.InvalidateHandle();

        if (!string.IsNullOrWhiteSpace(value?.Id) && !string.IsNullOrWhiteSpace(_settingsService.GetApiKey()))
        {
            _geminiService.SetModel(value.Id, EnableThinking, value.OutputTokenLimit, value.SupportsThinking);
            _settingsService.Settings.PreferredModelId = value.Id;
            _settingsService.Save();
        }

        OnPropertyChanged(nameof(ActiveModelDisplay));
        OnPropertyChanged(nameof(ActiveModelSubtext));
        NotifyCanGenerateChanged();
        ModelOrStatsChanged?.Invoke();
    }

    partial void OnEnableThinkingChanged(bool value)
    {
        if (SelectedModel != null && !string.IsNullOrEmpty(SelectedModel.Id))
            _geminiService.SetModel(SelectedModel.Id, value, SelectedModel.OutputTokenLimit, SelectedModel.SupportsThinking);
    }

    partial void OnSelectedSlideProcessingChanged(SlideProcessingMode value)
    {
        OnPropertyChanged(nameof(SelectedSlideProcessingDescription));
        _ = EstimateTokensAsync();
    }

    partial void OnSlidesFileChanged(UploadedFile? value)
    {
        SkipSlides.Reset();
        NotifyCanGenerateChanged();
        _ = EstimateTokensAsync();
    }

    partial void OnIsApiKeyValidChanged(bool value) => NotifyCanGenerateChanged();
    partial void OnIsGeneratingChanged(bool value) => NotifyCanGenerateChanged();
    partial void OnOutputPathChanged(string value) => NotifyCanGenerateChanged();

    partial void OnLivePreviewChanged(string value)
    {
        OnPropertyChanged(nameof(HasLivePreview));
    }

    private void NotifyCanGenerateChanged()
    {
        OnPropertyChanged(nameof(CanGenerate));
        OnPropertyChanged(nameof(GenerateBlockedReason));
    }

    partial void OnIsRenderedPreviewChanged(bool value)
    {
        if (value)
            MarkdownViewer.UpdateMarkdown(LivePreview);
    }

    [RelayCommand]
    private void SetPreviewMode(string mode)
    {
        IsRenderedPreview = !string.Equals(mode, "Raw", StringComparison.OrdinalIgnoreCase);
    }

    private void FlushPreviewBuffer()
    {
        string? snapshot = null;
        lock (_previewLock)
        {
            if (!_previewDirty) return;
            _previewDirty = false;
            snapshot = _previewBuffer.ToString();
        }

        if (snapshot != null)
        {
            LivePreview = snapshot;
            if (IsRenderedPreview)
                MarkdownViewer.UpdateMarkdown(snapshot);
        }
    }

    private async Task EstimateTokensAsync()
    {
        _estimateCts?.Cancel();
        _estimateCts?.Dispose();
        _estimateCts = new CancellationTokenSource();
        var ct = _estimateCts.Token;

        var currentSlides = SlidesFile;
        var currentBooks = BookFiles.ToList();
        var currentMode = SelectedSlideProcessing;

        if (currentSlides == null)
        {
            EstimatedTokenInfo = string.Empty;
            return;
        }

        try
        {
            EstimatedTokenInfo = "Estimating tokens...";
            var slideTexts = await _pdfExtractor.ExtractTextAsync(currentSlides.FilePath, ct: ct);
            if (ct.IsCancellationRequested || SlidesFile != currentSlides) return;

            long totalChars = slideTexts.Sum(t => (long)t.Length);
            foreach (var book in currentBooks)
            {
                ct.ThrowIfCancellationRequested();
                PromptBuilder.ParsePageRange(book.ChapterInfo, out var startPage, out var endPage);
                var pages = await _pdfExtractor.ExtractTextAsync(book.FilePath, startPage, endPage, ct);
                totalChars += pages.Sum(p => (long)p.Length);
            }

            if (ct.IsCancellationRequested || SlidesFile != currentSlides) return;

            long estimatedTokens = totalChars / 4 + 1500;
            if (currentMode == SlideProcessingMode.VisionAI)
                estimatedTokens += slideTexts.Count * 1000L;

            var visionTag = currentMode == SlideProcessingMode.VisionAI ? " (incl. Vision)" : "";
            EstimatedTokenInfo = $"~{estimatedTokens:N0} input tokens{visionTag}  •  {slideTexts.Count} slides";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (SlidesFile == currentSlides)
                EstimatedTokenInfo = string.Empty;
        }
    }

    // === File Drop & Removal ===

    public void HandleSlidesFileDrop(string filePath)
    {
        if (File.Exists(filePath) && filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            SlidesFile = new UploadedFile(filePath, FileType.Slides);
            StatusMessage = string.Empty;
            IsStatusError = false;
        }
        else
        {
            StatusMessage = "✗ Please choose a valid PDF file for slides.";
            IsStatusError = true;
        }
    }

    public void HandleBookFileDrop(string filePath)
    {
        if (File.Exists(filePath) && filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            BookFiles.Add(new UploadedFile(filePath, FileType.Book));
            StatusMessage = string.Empty;
            IsStatusError = false;
            _ = EstimateTokensAsync();
        }
        else
        {
            StatusMessage = "✗ Only valid PDF files are supported for reference books.";
            IsStatusError = true;
        }
    }

    [RelayCommand]
    private void RemoveSlides()
    {
        SlidesFile = null;
        SkipSlides.Reset();
    }

    [RelayCommand]
    private void RemoveBook(UploadedFile book)
    {
        BookFiles.Remove(book);
        _ = EstimateTokensAsync();
    }

    [RelayCommand]
    private void RemoveCourse(string course)
    {
        if (string.IsNullOrEmpty(course)) return;
        Courses.Remove(course);
        _settingsService.Settings.CourseHistory.Remove(course);
        _settingsService.Save();

        if (SelectedCourse == course)
            SelectedCourse = Courses.FirstOrDefault() ?? string.Empty;
    }

    // === Skip Slides ===

    [RelayCommand]
    private async Task OpenSkipSlidesPopupAsync()
    {
        if (SlidesFile == null) return;
        await SkipSlides.OpenAsync(SlidesFile.FilePath, (msg, isErr) =>
        {
            StatusMessage = msg;
            IsStatusError = isErr;
        });
    }

    // === Generation ===

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (!CanGenerate) return;

        var currentCourse = SelectedCourse?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(currentCourse))
        {
            if (!Courses.Contains(currentCourse))
                Courses.Add(currentCourse);
            if (!_settingsService.Settings.CourseHistory.Contains(currentCourse))
                _settingsService.Settings.CourseHistory.Add(currentCourse);
        }

        IsGenerating = true;
        ProgressPercent = 0;
        ProgressMessage = "Starting...";
        StatusMessage = string.Empty;
        IsStatusError = false;
        LastOutputFilePath = string.Empty;
        OnPropertyChanged(nameof(HasLastOutputFile));

        _generationCts = new CancellationTokenSource();

        lock (_previewLock)
        {
            _previewBuffer.Clear();
            _previewDirty = false;
        }
        LivePreview = string.Empty;
        MarkdownViewer.Clear();
        _previewFlushTimer.Start();

        try
        {
            var settings = new GenerationSettings
            {
                CourseName = currentCourse,
                Format = SelectedFormat,
                Mode = SelectedMode,
                Language = SelectedLanguage,
                SlideProcessing = SelectedSlideProcessing,
                ExtraNotes = ExtraNotes,
                OutputPath = OutputPath,
                SelectedModelId = SelectedModel?.Id ?? "",
                SlidesFile = SlidesFile,
                BookFiles = [.. BookFiles],
                SkippedSlides = [.. SkipSlides.SkippedNumbers]
            };

            _settingsService.Settings.LastOutputPath = OutputPath;
            _settingsService.Save();

            if (SelectedModel != null && !string.IsNullOrEmpty(SelectedModel.Id))
                _geminiService.SetModel(SelectedModel.Id, EnableThinking, SelectedModel.OutputTokenLimit, SelectedModel.SupportsThinking);

            var progress = new Progress<ProgressUpdate>(update =>
            {
                ProgressPercent = Math.Min(update.Percentage, 100);
                ProgressMessage = update.Message;
            });

            var liveTextProgress = new Progress<string>(chunk =>
            {
                lock (_previewLock)
                {
                    _previewBuffer.Append(chunk);
                    _previewDirty = true;
                }
            });

            Action<string> resetPreview = text =>
            {
                lock (_previewLock)
                {
                    _previewBuffer.Clear();
                    _previewBuffer.Append(text);
                    _previewDirty = false;
                }
                Dispatcher.UIThread.Post(() =>
                {
                    LivePreview = text;
                    if (IsRenderedPreview)
                        MarkdownViewer.UpdateMarkdown(text);
                });
            };

            Action<IReadOnlyList<string>> onSlidesReady = paths =>
            {
                Dispatcher.UIThread.Post(() => MarkdownViewer.RegisterSlideImagePaths(paths));
            };

            var result = await _noteGenerator.GenerateAsync(
                settings, progress, liveTextProgress, resetPreview, onSlidesReady, _generationCts.Token);

            _previewFlushTimer.Stop();
            if (!string.IsNullOrEmpty(result.MarkdownContent))
            {
                LivePreview = result.MarkdownContent;
                if (IsRenderedPreview)
                    MarkdownViewer.UpdateMarkdown(result.MarkdownContent);
            }

            ProgressMessage = "Exporting notes...";
            ProgressPercent = 96;

            var outputFile = await _outputExporter.ExportAsync(result, settings, _generationCts.Token);
            LastOutputFilePath = outputFile;
            OnPropertyChanged(nameof(HasLastOutputFile));

            var lectureTitle = ExtractLectureTitle(result.MarkdownContent)
                               ?? (!string.IsNullOrWhiteSpace(settings.SlidesFile?.FileName)
                                   ? Path.GetFileNameWithoutExtension(settings.SlidesFile.FileName)
                                   : "Lecture Notes");

            var historySession = new HistorySession
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.Now,
                CourseName = settings.CourseName,
                LectureTitle = lectureTitle,
                SlidesFileName = settings.SlidesFile?.FileName ?? string.Empty,
                OutputFilePath = outputFile,
                Format = settings.Format,
                Mode = settings.Mode,
                MarkdownContent = result.MarkdownContent,
                ChatHistory = []
            };
            _historyService.SaveSession(historySession);
            _activeHistorySessionId = historySession.Id;
            SessionRecorded?.Invoke(historySession);

            // Record real API token counts returned by NoteGeneratorService
            _settingsService.Settings.TokenUsageHistory.Add(
                new TokenRecord(DateTime.Now, result.InputTokens, result.OutputTokens));
            _settingsService.Save();
            ModelOrStatsChanged?.Invoke();

            ProgressPercent = 100;
            ProgressMessage = "Ready";
            StatusMessage = $"✓ Notes saved to: {outputFile}";
            IsStatusError = false;
        }
        catch (OperationCanceledException ex)
        {
            IsStatusError = true;
            if (_generationCts != null && _generationCts.IsCancellationRequested)
            {
                StatusMessage = "Generation cancelled.";
                ProgressMessage = "Cancelled";
            }
            else
            {
                StatusMessage = $"✗ API Timeout Error: {ex.Message}";
                ProgressMessage = "API Timeout";
            }
        }
        catch (Exception ex) when (_generationCts != null && _generationCts.IsCancellationRequested)
        {
            _ = ex;
            IsStatusError = true;
            StatusMessage = "Generation cancelled.";
            ProgressMessage = "Cancelled";
        }
        catch (Exception ex)
        {
            IsStatusError = true;
            if (ex.Message.Contains("503", StringComparison.Ordinal) ||
                ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = "✗ Selected model is experiencing temporary high demand (503). Try switching to Gemini 3.8 Flash or Gemini 4 Hydrogen.";
                ProgressMessage = "Model Busy (503)";
            }
            else
            {
                StatusMessage = $"✗ Error: {ex.Message}";
                ProgressMessage = "Error";
            }
        }
        finally
        {
            _previewFlushTimer.Stop();
            IsGenerating = false;
            _generationCts?.Dispose();
            _generationCts = null;
        }
    }

    [RelayCommand]
    private void CancelGeneration()
    {
        _generationCts?.Cancel();
        ProgressMessage = "Cancelling...";
    }

    // === Quick Note Actions & Follow-up ===

    [RelayCommand]
    private async Task CopyLiveNotesAsync()
    {
        if (!string.IsNullOrEmpty(LivePreview))
            await _clipboard.SetTextAsync(LivePreview);
    }

    [RelayCommand]
    private void OpenLastOutputFile()
    {
        if (HasLastOutputFile)
            _launcher.OpenFile(LastOutputFilePath);
    }

    [RelayCommand]
    private void OpenLastOutputFolder()
    {
        if (!string.IsNullOrWhiteSpace(LastOutputFilePath))
            _launcher.OpenFolderAndSelect(LastOutputFilePath);
    }

    [RelayCommand]
    private void StartFollowUpSession()
    {
        if (string.IsNullOrEmpty(LivePreview) || !_geminiService.IsConfigured)
        {
            if (!_geminiService.IsConfigured)
            {
                StatusMessage = "✗ Configure a valid API key and model in Settings to start chat.";
                IsStatusError = true;
            }
            return;
        }

        Chat.StartOrResume(LivePreview);
        IsFollowUpSessionActive = true;
    }

    [RelayCommand]
    private void EndFollowUpSession()
    {
        IsFollowUpSessionActive = false;
        Chat.Clear();
    }

    private void OnChatExchangeCompleted(ChatMessage userMsg, ChatMessage aiMsg)
    {
        if (!string.IsNullOrEmpty(_activeHistorySessionId))
            SessionChatUpdated?.Invoke(_activeHistorySessionId, userMsg, aiMsg);
    }

    private static string? ExtractLectureTitle(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(markdown, @"(?m)^\s*#\s+(.+)$");
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value)
            ? match.Groups[1].Value.Trim()
            : null;
    }

    public void Dispose()
    {
        _previewFlushTimer.Stop();
        _estimateCts?.Cancel();
        _estimateCts?.Dispose();
        _generationCts?.Dispose();
        SkipSlides.Dispose();
        MarkdownViewer.Dispose();
        GC.SuppressFinalize(this);
    }
}
