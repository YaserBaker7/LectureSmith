using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureSmith.Models;
using LectureSmith.Services;
using System.Collections.ObjectModel;
using System.Text;

namespace LectureSmith.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    // Static converters for ComboBox display names
    public static readonly FuncValueConverter<LectureMode, string> LectureModeDisplayConverter =
        new(m => m.DisplayName());
    public static readonly FuncValueConverter<OutputFormat, string> OutputFormatDisplayConverter =
        new(f => f.DisplayName());
    public static readonly FuncValueConverter<SlideProcessingMode, string> SlideProcessingDisplayConverter =
        new(s => s.DisplayName());
    public static readonly FuncValueConverter<NoteLanguage, string> LanguageDisplayConverter =
        new(l => l.DisplayName());
    public static readonly FuncValueConverter<GeminiModelInfo, string> ModelDisplayConverter =
        new(m => m == null ? "" : m.IsFree ? $"🟢 {m.DisplayName}" : $"🔴 {m.DisplayName}");

    private readonly GeminiService _geminiService;
    private readonly PdfExtractorService _pdfExtractorService;
    private readonly NoteGeneratorService _noteGeneratorService;
    private readonly OutputExporterService _outputExporterService;
    private readonly OcrService _ocrService;
    private readonly SettingsService _settingsService;
    private CancellationTokenSource? _cts;

    // === Dropdowns ===
    public ObservableCollection<string> Courses { get; } = [];
    public OutputFormat[] OutputFormats { get; } = Enum.GetValues<OutputFormat>();
    public LectureMode[] LectureModes { get; } = Enum.GetValues<LectureMode>();
    public SlideProcessingMode[] SlideProcessingModes { get; } = Enum.GetValues<SlideProcessingMode>();
    public NoteLanguage[] Languages { get; } = Enum.GetValues<NoteLanguage>();
    public ObservableCollection<GeminiModelInfo> AvailableModels { get; } = [GeminiService.OfflineErrorModel];

    [ObservableProperty] private string _selectedCourse = string.Empty;
    [ObservableProperty] private OutputFormat _selectedFormat = OutputFormat.Obsidian;
    [ObservableProperty] private LectureMode _selectedMode = LectureMode.MissedLecture;
    [ObservableProperty] private GeminiModelInfo? _selectedModel = GeminiService.OfflineErrorModel;
    [ObservableProperty] private SlideProcessingMode _selectedSlideProcessing = SlideProcessingMode.TextOnly;
    [ObservableProperty] private NoteLanguage _selectedLanguage = NoteLanguage.Auto;

    // === Files ===
    [ObservableProperty] private UploadedFile? _slidesFile;
    public ObservableCollection<UploadedFile> BookFiles { get; } = [];

    // === Settings ===
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private bool _isApiKeyValid;
    [ObservableProperty] private string _apiKeyStatus = "Not configured";
    [ObservableProperty] private bool _enableThinking;

    // === Extra notes ===
    [ObservableProperty] private string _extraNotes = string.Empty;
    [ObservableProperty] private string _outputPath = string.Empty;
    [ObservableProperty] private string _cleanupStatus = string.Empty;

    // === Progress ===
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private int _progressPercent;
    [ObservableProperty] private string _progressMessage = "Ready";
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isStatusError;
    [ObservableProperty] private bool _showSettings;

    // === Token estimate & live preview ===
    [ObservableProperty] private string _estimatedTokenInfo = string.Empty;
    [ObservableProperty] private string _livePreview = string.Empty;
    [ObservableProperty] private string _weeklyTokensDisplay = "0";
    [ObservableProperty] private string _monthlyTokensDisplay = "0";

    // === Follow-up Q&A session ===
    [ObservableProperty] private bool _isFollowUpSessionActive;
    [ObservableProperty] private string _followUpInput = string.Empty;
    [ObservableProperty] private bool _isSendingFollowUp;
    public ObservableCollection<ChatMessage> ChatMessages { get; } = [];

    // === Slide skipping ===
    [ObservableProperty] private bool _showSkipSlidesPopup;
    [ObservableProperty] private string _skippedSlidesDisplayText = string.Empty;
    public ObservableCollection<SkippedSlideInfo> SlidesThumbnails { get; } = [];
    private readonly HashSet<int> _skippedSlideNumbers = [];

    public bool HasLivePreview => !string.IsNullOrEmpty(LivePreview);

    public bool CanGenerate => SlidesFile != null && IsApiKeyValid && !IsGenerating && !string.IsNullOrWhiteSpace(OutputPath);

    public MainWindowViewModel()
    {
        _geminiService = new GeminiService();
        _pdfExtractorService = new PdfExtractorService();
        _ocrService = new OcrService();
        _noteGeneratorService = new NoteGeneratorService(_pdfExtractorService, _geminiService, _ocrService);
        _outputExporterService = new OutputExporterService();
        _settingsService = new SettingsService();

        TempFileCleanupService.RunBackgroundCleanup();
        LoadSettings();
    }

    private void LoadSettings()
    {
        _settingsService.Load();
        ApiKey = _settingsService.Settings.ApiKey;
        OutputPath = _settingsService.Settings.LastOutputPath;

        var savedModel = GeminiService.FindModelById(AvailableModels, _settingsService.Settings.PreferredModelId);
        SelectedModel = savedModel;
        UpdateTokenStatsDisplay();

        // Load Course History
        Courses.Clear();
        if (_settingsService.Settings.CourseHistory != null && _settingsService.Settings.CourseHistory.Count > 0)
        {
            foreach (var course in _settingsService.Settings.CourseHistory)
            {
                Courses.Add(course);
            }
        }

        SelectedCourse = Courses.FirstOrDefault() ?? string.Empty;

        // Try environment variables if saved key is empty
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") 
                         ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
            {
                ApiKey = envKey;
                ApiKeyStatus = "Loaded from environment variable (validating...)";
            }
        }

        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            _geminiService.SetApiKey(ApiKey);
            if (SelectedModel != null && !string.IsNullOrEmpty(SelectedModel.Id))
            {
                _geminiService.SetModel(SelectedModel.Id, EnableThinking);
            }
            
            // Asynchronously validate the key in the background to avoid blocking the UI thread
            _ = ValidateLoadedKeyAsync();
        }
    }

    private void UpdateTokenStatsDisplay()
    {
        var w = _settingsService.Settings.GetTokensThisWeek();
        var m = _settingsService.Settings.GetTokensThisMonth();
        WeeklyTokensDisplay = w >= 1000 ? $"{w / 1000.0:F1}k" : w.ToString();
        MonthlyTokensDisplay = m >= 1000 ? $"{m / 1000.0:F1}k" : m.ToString();
    }

    private async Task ValidateLoadedKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) return;

        try
        {
            ApiKeyStatus = "Validating API key...";
            var (valid, error) = await _geminiService.ValidateApiKeyAsync();
            if (valid)
            {
                IsApiKeyValid = true;
                ApiKeyStatus = string.IsNullOrEmpty(error) ? "✓ API key is valid!" : $"✓ {error}";
                _ = RefreshAvailableModelsAsync();
            }
            else
            {
                IsApiKeyValid = false;
                ApiKeyStatus = $"✗ API key validation failed: {error}";
            }
        }
        catch (Exception ex)
        {
            IsApiKeyValid = false;
            ApiKeyStatus = $"✗ Connection error: {ex.Message}";
        }
    }

    private async Task RefreshAvailableModelsAsync()
    {
        try
        {
            var fetched = await _geminiService.FetchAvailableModelsAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var currentId = SelectedModel?.Id ?? _settingsService.Settings.PreferredModelId;
                AvailableModels.Clear();
                if (fetched != null && fetched.Count > 0 && !(fetched.Count == 1 && string.IsNullOrEmpty(fetched[0].Id)))
                {
                    foreach (var m in fetched)
                    {
                        AvailableModels.Add(m);
                    }
                    var match = AvailableModels.FirstOrDefault(m => m.Id == currentId);
                    SelectedModel = match ?? AvailableModels.FirstOrDefault();
                }
                else
                {
                    AvailableModels.Add(GeminiService.OfflineErrorModel);
                    SelectedModel = GeminiService.OfflineErrorModel;
                }
            });
        }
        catch
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                AvailableModels.Clear();
                AvailableModels.Add(GeminiService.OfflineErrorModel);
                SelectedModel = GeminiService.OfflineErrorModel;
            });
        }
    }

    partial void OnSelectedModelChanged(GeminiModelInfo? value)
    {
        if (_geminiService.IsConfigured && value != null && !string.IsNullOrEmpty(value.Id))
        {
            _geminiService.SetModel(value.Id, EnableThinking);
        }
        if (value != null && !string.IsNullOrEmpty(value.Id))
        {
            _settingsService.Settings.PreferredModelId = value.Id;
            _settingsService.Save();
        }
        OnPropertyChanged(nameof(CanGenerate));
    }

    partial void OnEnableThinkingChanged(bool value)
    {
        if (_geminiService.IsConfigured && SelectedModel != null)
            _geminiService.SetModel(SelectedModel.Id, value);
    }

    partial void OnSlidesFileChanged(UploadedFile? value)
    {
        OnPropertyChanged(nameof(CanGenerate));
        _ = EstimateTokensAsync();
    }

    partial void OnLivePreviewChanged(string value)
    {
        OnPropertyChanged(nameof(HasLivePreview));
    }

    // === Follow-up Q&A Commands ===

    [RelayCommand]
    private void StartFollowUpSession()
    {
        if (string.IsNullOrEmpty(LivePreview) || !_geminiService.IsConfigured) return;

        var systemPrompt = "You are a helpful study assistant. The student has just generated lecture notes using an AI tool. " +
                           "They want to ask follow-up questions about the lecture content. " +
                           "Answer concisely and clearly, referencing the notes when relevant. " +
                           "If the student asks about something not covered in the notes, say so and provide what you can.";

        _geminiService.StartChatSession(systemPrompt, LivePreview);
        IsFollowUpSessionActive = true;
        ChatMessages.Clear();
        ChatMessages.Add(new ChatMessage("I've reviewed your lecture notes. Ask me anything about the material!", false));
    }

    [RelayCommand]
    private void EndFollowUpSession()
    {
        _geminiService.EndChatSession();
        IsFollowUpSessionActive = false;
        ChatMessages.Clear();
        FollowUpInput = string.Empty;
    }

    [RelayCommand]
    private async Task SendFollowUp()
    {
        if (string.IsNullOrWhiteSpace(FollowUpInput) || IsSendingFollowUp || !_geminiService.HasActiveChat) return;

        var userMessage = FollowUpInput.Trim();
        FollowUpInput = string.Empty;
        ChatMessages.Add(new ChatMessage(userMessage, true));
        IsSendingFollowUp = true;

        try
        {
            var responseBuilder = new StringBuilder();
            var aiMessage = new ChatMessage("", false);
            ChatMessages.Add(aiMessage);

            var streamProgress = new Progress<string>(chunk =>
            {
                responseBuilder.Append(chunk);
            });

            var response = await _geminiService.SendChatMessageAsync(userMessage, streamProgress);

            // Replace the placeholder message with the full response
            var index = ChatMessages.IndexOf(aiMessage);
            if (index >= 0)
            {
                ChatMessages[index] = new ChatMessage(response, false);
            }
        }
        catch (Exception ex)
        {
            ChatMessages.Add(new ChatMessage($"Error: {ex.Message}", false));
        }
        finally
        {
            IsSendingFollowUp = false;
        }
    }

    // === Slide Skipping Commands ===

    /// <summary>
    /// Directory containing extracted slide images (cached for reuse by Vision AI).
    /// </summary>
    private string? _cachedSlideImagesDir;
    private List<string>? _cachedSlideImagePaths;

    [RelayCommand]
    private async Task OpenSkipSlidesPopup()
    {
        if (SlidesFile == null) return;

        try
        {
            StatusMessage = "Loading slide images...";
            IsStatusError = false;

            // Use a stable cache directory keyed to the PDF filename so images persist
            // and can be reused directly when Vision AI is selected during generation
            var pdfHash = Path.GetFileNameWithoutExtension(SlidesFile.FilePath).GetHashCode().ToString("X8");
            var cacheDir = Path.Combine(Path.GetTempPath(), "LectureSmith", $"slides_{pdfHash}");

            List<string> imagePaths;

            // Reuse cached slide images if they exist on disk
            if (Directory.Exists(cacheDir))
            {
                var existing = Directory.GetFiles(cacheDir, "slide_*.png").OrderBy(f => f).ToList();
                if (existing.Count > 0)
                {
                    imagePaths = existing;
                }
                else
                {
                    imagePaths = await _pdfExtractorService.ExtractSlideImagesAsync(SlidesFile.FilePath, cacheDir);
                }
            }
            else
            {
                // Extract full slide photos to the cache directory
                imagePaths = await _pdfExtractorService.ExtractSlideImagesAsync(SlidesFile.FilePath, cacheDir);
            }

            _cachedSlideImagesDir = cacheDir;
            _cachedSlideImagePaths = imagePaths;

            if (imagePaths.Count == 0)
            {
                StatusMessage = "✗ Could not extract any slide images from this PDF.";
                IsStatusError = true;
                return;
            }

            // Populate the popup on the UI thread
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                SlidesThumbnails.Clear();
                for (int i = 0; i < imagePaths.Count; i++)
                {
                    var info = new SkippedSlideInfo(i + 1, imagePaths[i])
                    {
                        IsSkipped = _skippedSlideNumbers.Contains(i + 1)
                    };
                    SlidesThumbnails.Add(info);
                }

                ShowSkipSlidesPopup = true;
                StatusMessage = string.Empty;
            });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StatusMessage = $"✗ Failed to load slide images: {ex.Message}";
                IsStatusError = true;
            });
        }
    }

    [RelayCommand]
    public void ToggleSlideSkip(SkippedSlideInfo slide)
    {
        slide.IsSkipped = !slide.IsSkipped;
        if (slide.IsSkipped)
            _skippedSlideNumbers.Add(slide.SlideNumber);
        else
            _skippedSlideNumbers.Remove(slide.SlideNumber);

        UpdateSkippedSlidesDisplay();
    }

    [RelayCommand]
    private void CloseSkipSlidesPopup()
    {
        ShowSkipSlidesPopup = false;
        UpdateSkippedSlidesDisplay();
    }

    private void UpdateSkippedSlidesDisplay()
    {
        if (_skippedSlideNumbers.Count == 0)
        {
            SkippedSlidesDisplayText = string.Empty;
        }
        else
        {
            var sorted = _skippedSlideNumbers.OrderBy(n => n).ToList();
            SkippedSlidesDisplayText = $"Slides {string.Join(", ", sorted)} will be skipped";
        }
    }

    partial void OnIsApiKeyValidChanged(bool value) => OnPropertyChanged(nameof(CanGenerate));
    partial void OnIsGeneratingChanged(bool value) => OnPropertyChanged(nameof(CanGenerate));
    partial void OnOutputPathChanged(string value) => OnPropertyChanged(nameof(CanGenerate));
    // === Token estimation ===

    private async Task EstimateTokensAsync()
    {
        if (SlidesFile == null)
        {
            EstimatedTokenInfo = string.Empty;
            return;
        }

        try
        {
            EstimatedTokenInfo = "Estimating tokens...";
            var texts = await _pdfExtractorService.ExtractTextAsync(SlidesFile.FilePath);
            var totalChars = texts.Sum(t => t.Length);
            var estimatedTokens = totalChars / 4 + 1500; // ~4 chars/token + system prompt overhead
            EstimatedTokenInfo = $"📊 ~{estimatedTokens:N0} input tokens  •  {texts.Count} slides";
        }
        catch
        {
            EstimatedTokenInfo = string.Empty;
        }
    }

    // === Commands ===

    [RelayCommand]
    private async Task SaveApiKey()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            ApiKeyStatus = "✗ Please enter an API key";
            IsApiKeyValid = false;
            return;
        }

        ApiKeyStatus = "Validating...";
        _geminiService.SetApiKey(ApiKey);
        if (SelectedModel != null && !string.IsNullOrEmpty(SelectedModel.Id))
        {
            _geminiService.SetModel(SelectedModel.Id, EnableThinking);
        }

        var (valid, error) = await _geminiService.ValidateApiKeyAsync();
        if (valid)
        {
            IsApiKeyValid = true;
            ApiKeyStatus = string.IsNullOrEmpty(error) ? "✓ API key is valid!" : $"✓ {error}";
            _settingsService.Settings.ApiKey = ApiKey;
            _settingsService.Save();
        }
        else
        {
            IsApiKeyValid = false;
            ApiKeyStatus = $"✗ Validation failed: {error}";
        }
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        ShowSettings = !ShowSettings;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        ShowSettings = true;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        ShowSettings = false;
    }

    [RelayCommand]
    private void RemoveSlides()
    {
        SlidesFile = null;
    }

    [RelayCommand]
    private void RemoveBook(UploadedFile book)
    {
        BookFiles.Remove(book);
    }

    public void HandleSlidesFileDrop(string filePath)
    {
        if (filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            SlidesFile = new UploadedFile(filePath, FileType.Slides);
            StatusMessage = string.Empty;
            IsStatusError = false;
        }
        else
        {
            StatusMessage = "✗ Only PDF files are supported for slides.";
            IsStatusError = true;
        }
    }

    public void HandleBookFileDrop(string filePath)
    {
        if (filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            BookFiles.Add(new UploadedFile(filePath, FileType.Book));
            StatusMessage = string.Empty;
            IsStatusError = false;
        }
        else
        {
            StatusMessage = "✗ Only PDF files are supported for reference books.";
            IsStatusError = true;
        }
    }

    [RelayCommand]
    private async Task Generate()
    {
        if (SlidesFile == null || !IsApiKeyValid) return;

        // Save SelectedCourse to history if it is a new non-empty course name
        var currentCourse = SelectedCourse?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(currentCourse))
        {
            if (!Courses.Contains(currentCourse))
            {
                Courses.Add(currentCourse);
            }
            if (_settingsService.Settings.CourseHistory == null)
            {
                _settingsService.Settings.CourseHistory = new();
            }
            if (!_settingsService.Settings.CourseHistory.Contains(currentCourse))
            {
                _settingsService.Settings.CourseHistory.Add(currentCourse);
            }
        }

        IsGenerating = true;
        ProgressPercent = 0;
        ProgressMessage = "Starting...";
        StatusMessage = string.Empty;
        IsStatusError = false;
        _cts = new CancellationTokenSource();
        GenerationResult? result = null;

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
                SkippedSlides = [.. _skippedSlideNumbers]
            };

            // Save user preferences
            _settingsService.Settings.LastOutputPath = OutputPath;
            _settingsService.Save();

            // Reset live preview
            LivePreview = string.Empty;
            var previewBuilder = new StringBuilder();

            var progress = new Progress<ProgressUpdate>(update =>
            {
                ProgressPercent = Math.Min(update.Percentage, 100);
                ProgressMessage = update.Message;
            });

            var liveTextProgress = new Progress<string>(chunk =>
            {
                previewBuilder.Append(chunk);
                LivePreview = previewBuilder.ToString();
            });

            result = await _noteGeneratorService.GenerateAsync(settings, progress, liveTextProgress, _cts.Token);

            ProgressMessage = "Exporting notes...";
            ProgressPercent = 95;

            var outputFile = await _outputExporterService.ExportAsync(result, settings);

            // Record token usage
            var outputTokens = (result?.MarkdownContent?.Length ?? 0) / 4;
            var inputTokens = 1000;
            if (!string.IsNullOrEmpty(EstimatedTokenInfo))
            {
                var match = System.Text.RegularExpressions.Regex.Match(EstimatedTokenInfo, @"\d+[\d,]*");
                if (match.Success && int.TryParse(match.Value.Replace(",", ""), out var parsedIt))
                    inputTokens = parsedIt;
            }
            if (_settingsService.Settings.TokenUsageHistory == null)
                _settingsService.Settings.TokenUsageHistory = new();
            _settingsService.Settings.TokenUsageHistory.Add(new TokenRecord(DateTime.Now, inputTokens, outputTokens));
            _settingsService.Save();
            UpdateTokenStatsDisplay();

            ProgressPercent = 100;
            ProgressMessage = "Done!";
            StatusMessage = $"✓ Notes saved to: {outputFile}";
            IsStatusError = false;
        }
        catch (OperationCanceledException ex)
        {
            IsStatusError = true;
            if (_cts != null && _cts.IsCancellationRequested)
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
        catch (Exception ex)
        {
            StatusMessage = $"✗ Error: {ex.Message}";
            ProgressMessage = "Error occurred";
            IsStatusError = true;
        }
        finally
        {
            // Automatic self-cleanup of current request's images
            if (result?.SlideImagePaths?.FirstOrDefault() is string firstImage)
            {
                var sessionDir = Path.GetDirectoryName(firstImage);
                if (sessionDir != null && Directory.Exists(sessionDir))
                {
                    try { Directory.Delete(sessionDir, true); } catch { /* ignore locked files */ }
                }
            }

            IsGenerating = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private async Task CleanupCacheAsync()
    {
        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "LectureSmith");
            if (!Directory.Exists(tempDir))
            {
                CleanupStatus = "Cache is already empty.";
                return;
            }

            long totalBytes = 0;
            int fileCount = 0;
            var dirInfo = new DirectoryInfo(tempDir);

            foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                totalBytes += file.Length;
                fileCount++;
            }

            Directory.Delete(tempDir, true);
            
            var mb = totalBytes / 1024.0 / 1024.0;
            CleanupStatus = $"✓ Cleaned {fileCount} files ({mb:F1} MB).";
        }
        catch (Exception ex)
        {
            CleanupStatus = $"Cleanup failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelGeneration()
    {
        _cts?.Cancel();
    }
}
