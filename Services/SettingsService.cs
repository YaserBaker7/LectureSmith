using LectureSmith.Models;
using System.Text.Json;

namespace LectureSmith.Services;

public class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LectureSmith");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            // Prune token history to keep at most 1,000 recent records
            if (Settings.TokenUsageHistory.Count > 1000)
            {
                Settings.TokenUsageHistory = Settings.TokenUsageHistory
                    .OrderByDescending(r => r.Timestamp)
                    .Take(1000)
                    .OrderBy(r => r.Timestamp)
                    .ToList();
            }

            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Silently fail — settings are not critical
        }
    }

    /// <summary>
    /// Resets in-memory settings to defaults and saves.
    /// </summary>
    public void Reset()
    {
        Settings = new AppSettings();
        Save();
    }

    /// <summary>
    /// Completely wipes all application data (AppData settings, LocalAppData models, Temp caches, crash logs).
    /// If selfUninstall is true, spawns a detached helper to delete the executable itself after exit.
    /// </summary>
    public static void WipeAllData(bool selfUninstall = false)
    {
        var appData = SettingsDir;
        var localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LectureSmith");
        var tempDir = Path.Combine(Path.GetTempPath(), "LectureSmith");
        var desktopCrashLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "LectureSmith_crash.log");

        try { if (File.Exists(desktopCrashLog)) File.Delete(desktopCrashLog); } catch { }

        if (selfUninstall && !string.IsNullOrEmpty(Environment.ProcessPath))
        {
            var exePath = Environment.ProcessPath;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 1 /nobreak > NUL & rd /s /q \"{appData}\" & rd /s /q \"{localAppData}\" & rd /s /q \"{tempDir}\" & del /f /q \"{exePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            System.Diagnostics.Process.Start(psi);
            Environment.Exit(0);
            return;
        }

        try { if (Directory.Exists(appData)) Directory.Delete(appData, true); } catch { }
        try { if (Directory.Exists(localAppData)) Directory.Delete(localAppData, true); } catch { }
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
    }
}
