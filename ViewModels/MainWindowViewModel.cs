using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureSmith.Models;
using LectureSmith.Services;
using System.Collections.ObjectModel;
using System.Text;

namespace LectureSmith.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
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
    private readonly HistoryService _historyService;
    private CancellationTokenSource? _cts;
    private string? _activeWorkspaceSessionId;

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
    [ObservableProperty] private bool _showWipeConfirmation;
    [ObservableProperty] private bool _showUninstallConfirmation;
    [ObservableProperty] private string _wipeStatusMessage = string.Empty;

    // === Navigation & Theme ===
    [ObservableProperty] private bool _isDarkMode = true;
    [ObservableProperty] private string _currentNav = "Workspace";

    public bool IsWorkspaceActive => CurrentNav == "Workspace";
    public bool IsHistoryActive => CurrentNav == "History";
    public bool IsSettingsActive => CurrentNav == "Settings";

    partial void OnCurrentNavChanged(string value)
    {
        ShowSettings = value == "Settings";
        OnPropertyChanged(nameof(IsWorkspaceActive));
        OnPropertyChanged(nameof(IsHistoryActive));
        OnPropertyChanged(nameof(IsSettingsActive));
    }

    partial void OnShowSettingsChanged(bool value)
    {
        CurrentNav = value ? "Settings" : "Workspace";
    }

    public string ActiveModelDisplay => SelectedModel?.DisplayName ?? "Gemini Flash";
    public string ActiveModelSubtext => SelectedModel?.IsFree == true ? "🟢 Free Tier (1M Context)" : "🔴 Custom Model";

    // === History Studio ===
    public ObservableCollection<HistorySession> HistorySessions { get; } = [];
    public ObservableCollection<HistorySession> FilteredHistorySessions { get; } = [];
    [ObservableProperty] private HistorySession? _selectedHistorySession;
    [ObservableProperty] private string _historySearchQuery = string.Empty;
    [ObservableProperty] private string _historyFollowUpInput = string.Empty;
    [ObservableProperty] private bool _isSendingHistoryFollowUp;
    [ObservableProperty] private string _historySessionTab = "Notes";
    public ObservableCollection<ChatMessage> HistoryChatMessages { get; } = [];
    private string? _currentActiveHistoryChatSessionId;

    public bool HasHistorySessions => FilteredHistorySessions.Count > 0;
    public bool HasSelectedHistorySession => SelectedHistorySession != null;
    public bool IsHistoryNotesTabActive => HistorySessionTab == "Notes";
    public bool IsHistoryChatTabActive => HistorySessionTab == "Chat";

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
        _historyService = new HistoryService();

        TempFileCleanupService.RunBackgroundCleanup();
        LoadSettings();
        LoadHistorySessions();
    }

    private void LoadSettings()
    {
        _settingsService.Load();
        ApiKey = _settingsService.Settings.ApiKey;
        OutputPath = _settingsService.Settings.LastOutputPath;

        var savedModel = GeminiService.FindModelById(AvailableModels, _settingsService.Settings.PreferredModelId);
        SelectedModel = savedModel;
        UpdateTokenStatsDisplay();

        // Theme preference
        var themePref = _settingsService.Settings.ThemePreference;
        IsDarkMode = string.IsNullOrEmpty(themePref) || themePref.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        ApplyTheme(IsDarkMode);

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
        OnPropertyChanged(nameof(ActiveModelDisplay));
        OnPropertyChanged(nameof(ActiveModelSubtext));
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
            var completedAiMsg = new ChatMessage(response, false);
            if (index >= 0)
            {
                ChatMessages[index] = completedAiMsg;
            }

            // Sync with history session if one is active
            if (!string.IsNullOrEmpty(_activeWorkspaceSessionId))
            {
                var s = HistorySessions.FirstOrDefault(h => h.Id == _activeWorkspaceSessionId);
                if (s != null)
                {
                    s.ChatHistory.Add(new ChatMessage(userMessage, true));
                    s.ChatHistory.Add(completedAiMsg);
                    _historyService.SaveSession(s);
                }
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
                var existing = Directory.GetFiles(cacheDir, "slide_*.png").OrderBy(PdfExtractorService.GetSlideNumber).ToList();
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

            if (imagePaths.Count == 0)
            {
                StatusMessage = "✗ Could not extract any slide images from this PDF.";
                IsStatusError = true;
                return;
            }

            // Populate the popup on the UI thread
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var item in SlidesThumbnails)
                {
                    item.Dispose();
                }
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
        var currentFile = SlidesFile;
        if (currentFile == null)
        {
            EstimatedTokenInfo = string.Empty;
            return;
        }

        try
        {
            EstimatedTokenInfo = "Estimating tokens...";
            var texts = await _pdfExtractorService.ExtractTextAsync(currentFile.FilePath);
            if (SlidesFile != currentFile) return; // Discard stale estimate if file changed
            var totalChars = texts.Sum(t => t.Length);
            var estimatedTokens = totalChars / 4 + 1500; // ~4 chars/token + system prompt overhead
            EstimatedTokenInfo = $"📊 ~{estimatedTokens:N0} input tokens  •  {texts.Count} slides";
        }
        catch
        {
            if (SlidesFile == currentFile)
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
    private void NavigateToWorkspace()
    {
        CurrentNav = "Workspace";
    }

    [RelayCommand]
    private void NavigateToHistory()
    {
        LoadHistorySessions();
        CurrentNav = "History";
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        CurrentNav = "Settings";
    }

    // === History Studio Commands & Methods ===

    public void LoadHistorySessions()
    {
        var loaded = _historyService.LoadHistory();
        HistorySessions.Clear();
        foreach (var s in loaded)
        {
            HistorySessions.Add(s);
        }
        ApplyHistoryFilter();
    }

    private void ApplyHistoryFilter()
    {
        FilteredHistorySessions.Clear();
        var q = HistorySearchQuery?.Trim().ToLowerInvariant() ?? "";
        foreach (var s in HistorySessions)
        {
            if (string.IsNullOrEmpty(q) ||
                s.DisplayTitle.ToLowerInvariant().Contains(q) ||
                s.DisplayCourse.ToLowerInvariant().Contains(q) ||
                s.SlidesFileName.ToLowerInvariant().Contains(q))
            {
                FilteredHistorySessions.Add(s);
            }
        }
        OnPropertyChanged(nameof(HasHistorySessions));
        if (SelectedHistorySession == null || !FilteredHistorySessions.Contains(SelectedHistorySession))
        {
            SelectedHistorySession = FilteredHistorySessions.FirstOrDefault();
        }
    }

    partial void OnHistorySearchQueryChanged(string value)
    {
        ApplyHistoryFilter();
    }

    partial void OnSelectedHistorySessionChanged(HistorySession? value)
    {
        OnPropertyChanged(nameof(HasSelectedHistorySession));
        HistoryChatMessages.Clear();
        _currentActiveHistoryChatSessionId = null;

        if (value != null)
        {
            if (value.ChatHistory != null)
            {
                foreach (var msg in value.ChatHistory)
                {
                    HistoryChatMessages.Add(msg);
                }
            }
            if (HistoryChatMessages.Count == 0)
            {
                HistoryChatMessages.Add(new ChatMessage($"I've reviewed your lecture notes for '{value.DisplayTitle}'. Ask me anything about this lecture!", false));
            }
        }
    }

    [RelayCommand]
    private void SelectHistorySession(HistorySession? session)
    {
        if (session != null)
        {
            SelectedHistorySession = session;
        }
    }

    [RelayCommand]
    private void DeleteHistorySession(HistorySession? session)
    {
        var target = session ?? SelectedHistorySession;
        if (target == null) return;

        _historyService.DeleteSession(target.Id);
        HistorySessions.Remove(target);
        FilteredHistorySessions.Remove(target);
        OnPropertyChanged(nameof(HasHistorySessions));

        if (SelectedHistorySession == target)
        {
            SelectedHistorySession = FilteredHistorySessions.FirstOrDefault();
        }
    }

    [RelayCommand]
    private void SwitchHistoryTab(string tabName)
    {
        HistorySessionTab = tabName;
        OnPropertyChanged(nameof(IsHistoryNotesTabActive));
        OnPropertyChanged(nameof(IsHistoryChatTabActive));
    }

    [RelayCommand]
    private void OpenSessionFile(HistorySession? session)
    {
        var target = session ?? SelectedHistorySession;
        if (target != null && !string.IsNullOrWhiteSpace(target.OutputFilePath) && File.Exists(target.OutputFilePath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = target.OutputFilePath,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    [RelayCommand]
    private void OpenSessionFolder(HistorySession? session)
    {
        var target = session ?? SelectedHistorySession;
        if (target != null && !string.IsNullOrWhiteSpace(target.OutputFilePath))
        {
            try
            {
                if (File.Exists(target.OutputFilePath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{target.OutputFilePath}\"");
                }
                else
                {
                    var dir = Path.GetDirectoryName(target.OutputFilePath);
                    if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = dir,
                            UseShellExecute = true
                        });
                    }
                }
            }
            catch { }
        }
    }

    [RelayCommand]
    private async Task CopyHistoryNotesAsync()
    {
        if (SelectedHistorySession == null || string.IsNullOrEmpty(SelectedHistorySession.MarkdownContent)) return;
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard != null)
        {
            await desktop.MainWindow.Clipboard.SetTextAsync(SelectedHistorySession.MarkdownContent);
        }
    }

    [RelayCommand]
    private void ResumeSessionInWorkspace(HistorySession? session)
    {
        var target = session ?? SelectedHistorySession;
        if (target == null) return;

        LivePreview = target.MarkdownContent;
        if (!string.IsNullOrWhiteSpace(target.CourseName))
        {
            SelectedCourse = target.CourseName;
        }

        ChatMessages.Clear();
        if (target.ChatHistory != null)
        {
            foreach (var msg in target.ChatHistory)
            {
                ChatMessages.Add(msg);
            }
        }
        if (ChatMessages.Count == 0)
        {
            ChatMessages.Add(new ChatMessage($"I've reviewed your lecture notes for '{target.DisplayTitle}'. Ask me anything about this lecture!", false));
        }

        var systemPrompt = "You are a helpful study assistant. The student has just generated lecture notes using an AI tool. " +
                           "They want to ask follow-up questions about the lecture content. " +
                           "Answer concisely and clearly, referencing the notes when relevant. " +
                           "If the student asks about something not covered in the notes, say so and provide what you can.";

        _geminiService.StartChatSession(systemPrompt, target.MarkdownContent, target.ChatHistory);
        IsFollowUpSessionActive = true;
        _activeWorkspaceSessionId = target.Id;

        NavigateToWorkspace();
    }

    [RelayCommand]
    private async Task SendHistoryFollowUp()
    {
        if (string.IsNullOrWhiteSpace(HistoryFollowUpInput) || IsSendingHistoryFollowUp || SelectedHistorySession == null || !_geminiService.IsConfigured) return;

        var userMessage = HistoryFollowUpInput.Trim();
        HistoryFollowUpInput = string.Empty;
        var userChatMsg = new ChatMessage(userMessage, true);
        HistoryChatMessages.Add(userChatMsg);
        SelectedHistorySession.ChatHistory.Add(userChatMsg);
        IsSendingHistoryFollowUp = true;

        try
        {
            if (_currentActiveHistoryChatSessionId != SelectedHistorySession.Id || !_geminiService.HasActiveChat)
            {
                var systemPrompt = "You are a helpful study assistant. The student has just generated lecture notes using an AI tool. " +
                                   "They want to ask follow-up questions about the lecture content. " +
                                   "Answer concisely and clearly, referencing the notes when relevant. " +
                                   "If the student asks about something not covered in the notes, say so and provide what you can.";

                _geminiService.StartChatSession(systemPrompt, SelectedHistorySession.MarkdownContent, SelectedHistorySession.ChatHistory);
                _currentActiveHistoryChatSessionId = SelectedHistorySession.Id;
            }

            var responseBuilder = new StringBuilder();
            var aiMessage = new ChatMessage("", false);
            HistoryChatMessages.Add(aiMessage);

            var streamProgress = new Progress<string>(chunk =>
            {
                responseBuilder.Append(chunk);
            });

            var response = await _geminiService.SendChatMessageAsync(userMessage, streamProgress);

            var index = HistoryChatMessages.IndexOf(aiMessage);
            var completedAiMsg = new ChatMessage(response, false);
            if (index >= 0)
            {
                HistoryChatMessages[index] = completedAiMsg;
            }
            SelectedHistorySession.ChatHistory.Add(completedAiMsg);
            _historyService.SaveSession(SelectedHistorySession);
            OnPropertyChanged(nameof(SelectedHistorySession));
        }
        catch (Exception ex)
        {
            HistoryChatMessages.Add(new ChatMessage($"Error: {ex.Message}", false));
        }
        finally
        {
            IsSendingHistoryFollowUp = false;
        }
    }

    [RelayCommand]
    public void ToggleTheme()
    {
        IsDarkMode = !IsDarkMode;
        ApplyTheme(IsDarkMode);
        _settingsService.Settings.ThemePreference = IsDarkMode ? "Dark" : "Light";
        _settingsService.Save();
    }

    public static void ApplyTheme(bool isDark)
    {
        if (Avalonia.Application.Current != null)
        {
            Avalonia.Application.Current.RequestedThemeVariant =
                isDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
        }
    }

    [RelayCommand]
    private void RemoveCourse(string course)
    {
        if (string.IsNullOrEmpty(course)) return;
        Courses.Remove(course);
        if (_settingsService.Settings.CourseHistory != null)
        {
            _settingsService.Settings.CourseHistory.Remove(course);
            _settingsService.Save();
        }
        if (SelectedCourse == course)
        {
            SelectedCourse = Courses.FirstOrDefault() ?? string.Empty;
        }
    }

    [RelayCommand]
    private void RemoveSlides()
    {
        SlidesFile = null;
        _skippedSlideNumbers.Clear();
        UpdateSkippedSlidesDisplay();
        foreach (var item in SlidesThumbnails)
        {
            item.Dispose();
        }
        SlidesThumbnails.Clear();
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

            Action<string> resetPreview = text =>
            {
                previewBuilder.Clear();
                previewBuilder.Append(text);
                LivePreview = text;
            };

            result = await _noteGeneratorService.GenerateAsync(settings, progress, liveTextProgress, resetPreview, _cts.Token);

            if (!string.IsNullOrEmpty(result?.MarkdownContent))
            {
                LivePreview = result.MarkdownContent;
            }

            ProgressMessage = "Exporting notes...";
            ProgressPercent = 95;

            var outputFile = await _outputExporterService.ExportAsync(result!, settings, _cts.Token);

            // Record history session
            var lectureTitle = ExtractLectureTitle(result?.MarkdownContent) ??
                               (!string.IsNullOrWhiteSpace(settings.SlidesFile?.FileName) ? Path.GetFileNameWithoutExtension(settings.SlidesFile.FileName) : "Lecture Notes");
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
                MarkdownContent = result?.MarkdownContent ?? string.Empty,
                ChatHistory = []
            };
            _historyService.SaveSession(historySession);
            _activeWorkspaceSessionId = historySession.Id;
            LoadHistorySessions();

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
            if (ex.Message.Contains("503") || ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = "✗ Model is currently experiencing temporary high demand (503). Try switching to another model like Gemini 2.5 Flash in Settings.";
                ProgressMessage = "Model Busy (503)";
            }
            else
            {
                StatusMessage = $"✗ Error: {ex.Message}";
                ProgressMessage = "Error occurred";
            }
            IsStatusError = true;
        }
        finally
        {
            IsGenerating = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static string? ExtractLectureTitle(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(markdown, @"(?m)^\s*#\s+(.+)$");
        if (match.Success)
        {
            var title = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(title)) return title;
        }
        return null;
    }

    [RelayCommand]
    private void RequestWipeData()
    {
        ShowWipeConfirmation = true;
        ShowUninstallConfirmation = false;
        WipeStatusMessage = string.Empty;
    }

    [RelayCommand]
    private void CancelWipeData()
    {
        ShowWipeConfirmation = false;
    }

    [RelayCommand]
    private void ConfirmWipeData()
    {
        SettingsService.WipeAllData(selfUninstall: false);
        _settingsService.Reset();
        _historyService.ClearHistory();

        ApiKey = string.Empty;
        IsApiKeyValid = false;
        ApiKeyStatus = "Not configured";
        OutputPath = string.Empty;
        Courses.Clear();
        SelectedCourse = string.Empty;
        ChatMessages.Clear();
        LivePreview = string.Empty;
        EstimatedTokenInfo = string.Empty;
        HistorySessions.Clear();
        FilteredHistorySessions.Clear();
        SelectedHistorySession = null;
        HistoryChatMessages.Clear();
        ShowWipeConfirmation = false;
        WipeStatusMessage = "✓ All settings, history, and cached data have been completely wiped.";
        UpdateTokenStatsDisplay();
    }

    [RelayCommand]
    private void RequestUninstall()
    {
        ShowUninstallConfirmation = true;
        ShowWipeConfirmation = false;
        WipeStatusMessage = string.Empty;
    }

    [RelayCommand]
    private void CancelUninstall()
    {
        ShowUninstallConfirmation = false;
    }

    [RelayCommand]
    private void ConfirmUninstall()
    {
        SettingsService.WipeAllData(selfUninstall: true);
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
        ProgressMessage = "Cancelling...";
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _ocrService.Dispose();
        foreach (var thumbnail in SlidesThumbnails)
        {
            thumbnail.Dispose();
        }
        SlidesThumbnails.Clear();
        GC.SuppressFinalize(this);
    }
}
