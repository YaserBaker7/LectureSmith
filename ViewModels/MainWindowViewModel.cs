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
    public GeminiModelInfo[] AvailableModels { get; } = GeminiService.AvailableModels;

    [ObservableProperty] private string _selectedCourse = string.Empty;
    [ObservableProperty] private OutputFormat _selectedFormat = OutputFormat.Obsidian;
    [ObservableProperty] private LectureMode _selectedMode = LectureMode.MissedLecture;
    [ObservableProperty] private GeminiModelInfo _selectedModel = GeminiService.AvailableModels[1]; // gemini-3.5-flash
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

        LoadSettings();
    }

    private void LoadSettings()
    {
        _settingsService.Load();
        ApiKey = _settingsService.Settings.ApiKey;
        OutputPath = _settingsService.Settings.LastOutputPath;

        var savedModel = GeminiService.FindModelById(_settingsService.Settings.PreferredModelId);
        SelectedModel = savedModel;

        // Load Course History
        Courses.Clear();
        if (_settingsService.Settings.CourseHistory != null && _settingsService.Settings.CourseHistory.Count > 0)
        {
            foreach (var course in _settingsService.Settings.CourseHistory)
            {
                Courses.Add(course);
            }
        }
        else
        {
            // Pre-populate with some default suggestions on first load
            Courses.Add("Fundamentals of Software Engineering");
            Courses.Add("Advanced OOP");
            Courses.Add("Data Management");
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
            _geminiService.SetModel(SelectedModel.Id, EnableThinking);
            
            // Asynchronously validate the key in the background to avoid blocking the UI thread
            _ = ValidateLoadedKeyAsync();
        }
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

    partial void OnSelectedModelChanged(GeminiModelInfo value)
    {
        if (_geminiService.IsConfigured && value != null)
        {
            _geminiService.SetModel(value.Id, EnableThinking);
        }
        if (value != null)
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
        _geminiService.SetModel(SelectedModel.Id, EnableThinking);

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
                SelectedModelId = SelectedModel.Id,
                SlidesFile = SlidesFile,
                BookFiles = [.. BookFiles]
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
