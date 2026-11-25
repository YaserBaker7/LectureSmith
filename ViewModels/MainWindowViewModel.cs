using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureSmith.Models;
using LectureSmith.Services;

namespace LectureSmith.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IHistoryService _historyService;
    private readonly IGeminiService _geminiService;
    private readonly IGeminiModelCatalogService _catalogService;
    private readonly IOcrService _ocrService;

    private CancellationTokenSource? _validationCts;

    public WorkspaceViewModel Workspace { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private string _currentNav = "Workspace";
    [ObservableProperty] private string _weeklyTokensDisplay = "0";
    [ObservableProperty] private string _monthlyTokensDisplay = "0";

    public bool IsWorkspaceActive => CurrentNav == "Workspace";
    public bool IsHistoryActive => CurrentNav == "History";
    public bool IsSettingsActive => CurrentNav == "Settings";

    public MainWindowViewModel() : this(CreateDefaultSettings())
    {
    }

    public MainWindowViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _historyService = new HistoryService();
        _geminiService = new GeminiService();
        _catalogService = new GeminiModelCatalogService();
        _ocrService = new OcrService();

        var pdfExtractor = new PdfExtractorService();
        var slideCache = new SlideCacheService(pdfExtractor);
        var noteGenerator = new NoteGeneratorService(pdfExtractor, slideCache, _geminiService, _ocrService);
        var outputExporter = new OutputExporterService();
        var clipboard = new AvaloniaClipboardService();
        var launcher = new WindowsFileLauncherService();

        Workspace = new WorkspaceViewModel(
            _geminiService, pdfExtractor, slideCache, noteGenerator, outputExporter,
            _settingsService, _historyService, clipboard, launcher);

        History = new HistoryViewModel(_historyService, _geminiService, clipboard, launcher);
        Settings = new SettingsViewModel(_settingsService, _historyService, () => Workspace.IsGenerating);

        WireEvents();

        TempFileCleanupService.RunBackgroundCleanup();
        InitializeFromSettings();
        History.LoadSessions();
    }

    private static SettingsService CreateDefaultSettings()
    {
        var s = new SettingsService();
        s.Load();
        return s;
    }

    private void WireEvents()
    {
        Workspace.ModelOrStatsChanged += () =>
        {
            UpdateTokenStatsDisplay();
            History.InvalidateChatHandle();
        };

        Workspace.SessionRecorded += _ => History.LoadSessions();

        Workspace.SessionChatUpdated += (sessionId, userMsg, aiMsg) =>
        {
            var item = History.FindById(sessionId);
            if (item != null)
            {
                item.Model.ChatHistory.Add(userMsg);
                item.Model.ChatHistory.Add(aiMsg);
                _historyService.SaveSession(item.Model);
                item.RefreshMetadata();
            }
        };

        History.ResumeInWorkspaceRequested += session =>
        {
            Workspace.LoadResumedSession(session);
            CurrentNav = "Workspace";
        };

        Settings.ValidateAndSaveKeyRequested += async key =>
        {
            await ValidateAndLoadModelsAsync(key, persistOnSuccess: true);
        };

        Settings.ApiKeyEditedInvalidated += () =>
        {
            _validationCts?.Cancel();
            Workspace.IsApiKeyValid = false;
        };

        Settings.ThemeChangedRequested += isDark =>
        {
            ApplyTheme(isDark);
            _settingsService.Settings.ThemePreference = isDark ? "Dark" : "Light";
            _settingsService.Save();
        };

        Settings.DataWiped += () =>
        {
            _validationCts?.Cancel();
            _geminiService.SetApiKey(string.Empty);
            Workspace.IsApiKeyValid = false;
            Workspace.ResetAll();
            History.ClearAll();
            UpdateTokenStatsDisplay();
        };
    }

    private void InitializeFromSettings()
    {
        Workspace.LoadFromSettings();
        UpdateTokenStatsDisplay();

        var themePref = _settingsService.Settings.ThemePreference;
        var isDark = string.IsNullOrEmpty(themePref) || themePref.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        Settings.IsDarkMode = isDark;
        ApplyTheme(isDark);

        var key = _settingsService.GetApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            var envKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                         ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
                key = envKey.Trim();
        }

        Settings.IsBusyValidating = true;
        Settings.ApiKey = key;
        Settings.IsBusyValidating = false;

        if (!string.IsNullOrWhiteSpace(key))
        {
            _geminiService.SetApiKey(key);
            _ = ValidateAndLoadModelsAsync(key, persistOnSuccess: false);
        }
    }

    private async Task ValidateAndLoadModelsAsync(string apiKey, bool persistOnSuccess)
    {
        _validationCts?.Cancel();
        _validationCts?.Dispose();
        _validationCts = new CancellationTokenSource();
        var ct = _validationCts.Token;

        try
        {
            Settings.IsBusyValidating = true;
            Settings.ApiKeyStatus = "Validating key & loading models...";
            _geminiService.SetApiKey(apiKey);

            var (isValid, message, models) = await _catalogService.ValidateAndFetchModelsAsync(apiKey, ct);
            if (ct.IsCancellationRequested) return;

            if (isValid)
            {
                Settings.IsApiKeyValid = true;
                Workspace.IsApiKeyValid = true;
                Settings.ApiKeyStatus = $"✓ API key valid ({models.Count(m => !string.IsNullOrEmpty(m.Id))} text models)";
                Workspace.ApplyAvailableModels(models);

                if (persistOnSuccess)
                {
                    _settingsService.SetApiKey(apiKey);
                    _settingsService.Save();
                }
            }
            else
            {
                Settings.IsApiKeyValid = false;
                Workspace.IsApiKeyValid = false;
                Settings.ApiKeyStatus = $"✗ {message}";
                Workspace.ApplyAvailableModels([GeminiModelCatalogService.OfflineErrorModel]);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Settings.IsApiKeyValid = false;
                Workspace.IsApiKeyValid = false;
                Settings.ApiKeyStatus = $"✗ Connection error: {ex.Message}";
            }
        }
        finally
        {
            Settings.IsBusyValidating = false;
        }
    }

    private void UpdateTokenStatsDisplay()
    {
        var w = _settingsService.Settings.GetTokensThisWeek();
        var m = _settingsService.Settings.GetTokensThisMonth();
        WeeklyTokensDisplay = w >= 1000 ? $"{w / 1000.0:F1}k" : w.ToString();
        MonthlyTokensDisplay = m >= 1000 ? $"{m / 1000.0:F1}k" : m.ToString();
    }

    partial void OnCurrentNavChanged(string value)
    {
        OnPropertyChanged(nameof(IsWorkspaceActive));
        OnPropertyChanged(nameof(IsHistoryActive));
        OnPropertyChanged(nameof(IsSettingsActive));
    }

    [RelayCommand]
    private void NavigateToWorkspace() => CurrentNav = "Workspace";

    [RelayCommand]
    private void NavigateToHistory()
    {
        History.LoadSessions();
        CurrentNav = "History";
    }

    [RelayCommand]
    private void NavigateToSettings() => CurrentNav = "Settings";

    public static void ApplyTheme(bool isDark)
    {
        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant =
                isDark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    public void Dispose()
    {
        _validationCts?.Cancel();
        _validationCts?.Dispose();
        Workspace.Dispose();
        History.MarkdownViewer.Dispose();
        _ocrService.Dispose();
        GC.SuppressFinalize(this);
    }
}
