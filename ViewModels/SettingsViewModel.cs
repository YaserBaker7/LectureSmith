using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureSmith.Services;

namespace LectureSmith.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly IHistoryService _historyService;
    private readonly Func<bool> _isGeneratingProvider;

    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private bool _showApiKey;
    [ObservableProperty] private bool _isApiKeyValid;
    [ObservableProperty] private string _apiKeyStatus = "Not configured";
    [ObservableProperty] private bool _isDarkMode = true;
    [ObservableProperty] private bool _showWipeConfirmation;
    [ObservableProperty] private bool _showUninstallConfirmation;
    [ObservableProperty] private string _wipeStatusMessage = string.Empty;
    [ObservableProperty] private string _cleanupStatus = string.Empty;
    [ObservableProperty] private bool _isBusyValidating;

    public char PasswordChar => ShowApiKey ? '\0' : '•';

    public event Func<string, Task>? ValidateAndSaveKeyRequested;
    public event Action? ApiKeyEditedInvalidated;
    public event Action<bool>? ThemeChangedRequested;
    public event Action? DataWiped;

    public SettingsViewModel(
        ISettingsService settingsService,
        IHistoryService historyService,
        Func<bool> isGeneratingProvider)
    {
        _settingsService = settingsService;
        _historyService = historyService;
        _isGeneratingProvider = isGeneratingProvider;
    }

    partial void OnShowApiKeyChanged(bool value) => OnPropertyChanged(nameof(PasswordChar));

    partial void OnApiKeyChanged(string value)
    {
        if (IsBusyValidating) return;

        var saved = _settingsService.GetApiKey();
        if (!string.Equals(value?.Trim(), saved, StringComparison.Ordinal))
        {
            if (IsApiKeyValid)
            {
                IsApiKeyValid = false;
                ApiKeyStatus = "Unsaved changes — click Save & Validate";
                ApiKeyEditedInvalidated?.Invoke();
            }
        }
    }

    [RelayCommand]
    private void ToggleRevealApiKey() => ShowApiKey = !ShowApiKey;

    [RelayCommand]
    private async Task SaveApiKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            ApiKeyStatus = "✗ Please enter an API key";
            IsApiKeyValid = false;
            return;
        }

        if (ValidateAndSaveKeyRequested != null)
            await ValidateAndSaveKeyRequested.Invoke(ApiKey.Trim());
    }

    [RelayCommand]
    private void SetTheme(string mode)
    {
        bool dark = string.Equals(mode, "Dark", StringComparison.OrdinalIgnoreCase);
        if (IsDarkMode == dark) return;
        IsDarkMode = dark;
        ThemeChangedRequested?.Invoke(dark);
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkMode = !IsDarkMode;
        ThemeChangedRequested?.Invoke(IsDarkMode);
    }

    [RelayCommand]
    private async Task CleanupCacheAsync()
    {
        if (_isGeneratingProvider())
        {
            CleanupStatus = "Wait for active note generation to finish before clearing cache.";
            return;
        }

        try
        {
            var resultMsg = await Task.Run(() =>
            {
                if (!Directory.Exists(AppPaths.TempDir))
                    return "Cache is already empty.";

                long totalBytes = 0;
                int fileCount = 0;
                var dirInfo = new DirectoryInfo(AppPaths.TempDir);
                foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    totalBytes += file.Length;
                    fileCount++;
                }

                Directory.Delete(AppPaths.TempDir, recursive: true);
                var mb = totalBytes / 1024.0 / 1024.0;
                return $"✓ Cleaned {fileCount} files ({mb:F1} MB).";
            });

            CleanupStatus = resultMsg;
        }
        catch (Exception ex)
        {
            CleanupStatus = $"Cleanup failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RequestWipeData()
    {
        ShowWipeConfirmation = true;
        ShowUninstallConfirmation = false;
        WipeStatusMessage = string.Empty;
    }

    [RelayCommand]
    private void CancelWipeData() => ShowWipeConfirmation = false;

    [RelayCommand]
    private void ConfirmWipeData()
    {
        SettingsService.WipeAllData(selfUninstall: false);
        _settingsService.Reset();
        _historyService.ClearHistory();

        ShowApiKey = false;
        IsBusyValidating = true;
        ApiKey = string.Empty;
        IsBusyValidating = false;
        IsApiKeyValid = false;
        ApiKeyStatus = "Not configured";
        CleanupStatus = string.Empty;
        ShowWipeConfirmation = false;
        WipeStatusMessage = "✓ All settings, encrypted keys, history, and caches have been reset.";

        DataWiped?.Invoke();
    }

    [RelayCommand]
    private void RequestUninstall()
    {
        ShowUninstallConfirmation = true;
        ShowWipeConfirmation = false;
        WipeStatusMessage = string.Empty;
    }

    [RelayCommand]
    private void CancelUninstall() => ShowUninstallConfirmation = false;

    [RelayCommand]
    private void ConfirmUninstall() => SettingsService.WipeAllData(selfUninstall: true);
}
