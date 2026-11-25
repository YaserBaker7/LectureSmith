using LectureSmith.Models;
using System.Diagnostics;
using System.Text.Json;

namespace LectureSmith.Services;

public class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly ISecretProtector _protector;

    public AppSettings Settings { get; private set; } = new();

    public SettingsService(ISecretProtector? protector = null)
    {
        _protector = protector ?? new DpapiSecretProtector();
    }

    public string GetApiKey()
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(Settings.EncryptedApiKey))
            {
                var decrypted = _protector.Unprotect(Settings.EncryptedApiKey);
                if (!string.IsNullOrWhiteSpace(decrypted))
                    return decrypted;
            }

            return Settings.ApiKey ?? string.Empty;
        }
    }

    public void SetApiKey(string apiKey)
    {
        lock (_lock)
        {
            var trimmed = apiKey?.Trim() ?? string.Empty;
            Settings.EncryptedApiKey = string.IsNullOrEmpty(trimmed) ? string.Empty : _protector.Protect(trimmed);
            Settings.ApiKey = null; // Never persist plaintext API keys
        }
    }

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(AppPaths.SettingsFile))
                    return;

                var json = File.ReadAllText(AppPaths.SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                loaded.CourseHistory ??= [];
                loaded.TokenUsageHistory ??= [];

                Settings = loaded;

                // One-time transparent migration from plaintext ApiKey -> DPAPI EncryptedApiKey
                if (!string.IsNullOrWhiteSpace(Settings.ApiKey) && string.IsNullOrWhiteSpace(Settings.EncryptedApiKey))
                {
                    Settings.EncryptedApiKey = _protector.Protect(Settings.ApiKey.Trim());
                    Settings.ApiKey = null;
                    PersistUnsafe();
                }
            }
            catch
            {
                AppPaths.BackupCorruptFile(AppPaths.SettingsFile);
                Settings = new AppSettings();
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                PersistUnsafe();
            }
            catch
            {
                // Settings writes should not crash active workflows
            }
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            Settings = new AppSettings();
            try
            {
                PersistUnsafe();
            }
            catch
            {
                // Non-fatal
            }
        }
    }

    private void PersistUnsafe()
    {
        Settings.CourseHistory ??= [];
        Settings.TokenUsageHistory ??= [];

        // Keep at most 1,000 most recent token telemetry records
        if (Settings.TokenUsageHistory.Count > 1000)
        {
            Settings.TokenUsageHistory = Settings.TokenUsageHistory
                .OrderByDescending(r => r.Timestamp)
                .Take(1000)
                .OrderBy(r => r.Timestamp)
                .ToList();
        }

        // Ensure plaintext legacy property is never re-emitted once encrypted
        if (!string.IsNullOrWhiteSpace(Settings.EncryptedApiKey))
            Settings.ApiKey = null;

        var json = JsonSerializer.Serialize(Settings, JsonOptions);
        AppPaths.WriteAllTextAtomic(AppPaths.SettingsFile, json);
    }

    /// <summary>
    /// Safely wipes internal application cache and configuration (AppData, LocalAppData, Temp caches).
    /// User documents and saved lecture notes are NEVER touched.
    /// </summary>
    public static void WipeAllData(bool selfUninstall = false)
    {
        var appData = AppPaths.AppDataDir;
        var localAppData = AppPaths.LocalAppDataDir;
        var tempDir = AppPaths.TempDir;
        var desktopCrashLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "LectureSmith_crash.log");

        try { if (File.Exists(desktopCrashLog)) File.Delete(desktopCrashLog); } catch { }

        if (selfUninstall && !string.IsNullOrEmpty(Environment.ProcessPath))
        {
            var exePath = Environment.ProcessPath;
            var appDir = Path.GetDirectoryName(exePath);
            var innoUninstaller = !string.IsNullOrEmpty(appDir) ? Path.Combine(appDir, "unins000.exe") : null;

            if (!string.IsNullOrEmpty(innoUninstaller) && File.Exists(innoUninstaller))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = innoUninstaller,
                    UseShellExecute = true
                });
                Environment.Exit(0);
                return;
            }

            // Standalone portable fallback: delete internal directories and standalone exe after exit
            var cmds = new List<string> { "timeout /t 1 /nobreak > NUL" };
            foreach (var dir in new[] { appData, localAppData, tempDir })
            {
                if (Directory.Exists(dir) && dir.EndsWith("LectureSmith", StringComparison.OrdinalIgnoreCase))
                    cmds.Add($"rd /s /q \"{dir}\"");
            }
            cmds.Add($"del /f /q \"{exePath}\"");

            var psiStandalone = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            psiStandalone.ArgumentList.Add("/c");
            psiStandalone.ArgumentList.Add(string.Join(" & ", cmds));
            Process.Start(psiStandalone);
            Environment.Exit(0);
            return;
        }

        foreach (var dir in new[] { appData, localAppData, tempDir })
        {
            try
            {
                if (Directory.Exists(dir) && dir.EndsWith("LectureSmith", StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(dir, true);
            }
            catch { }
        }
    }
}
