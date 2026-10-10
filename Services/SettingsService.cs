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
    /// Safely wipes internal application cache and configuration (AppData settings, LocalAppData models, Temp caches).
    /// If selfUninstall is true:
    ///   - If installed via setup installer, triggers the official Windows uninstaller (unins000.exe).
    ///   - If running standalone, safely cleans internal application folders and removes the standalone executable.
    /// User documents and saved lecture notes are NEVER touched.
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
            var appDir = Path.GetDirectoryName(exePath);
            var innoUninstaller = !string.IsNullOrEmpty(appDir) ? Path.Combine(appDir, "unins000.exe") : null;

            // If installed via the official setup wizard, launch the standard Windows uninstaller
            if (!string.IsNullOrEmpty(innoUninstaller) && File.Exists(innoUninstaller))
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = innoUninstaller,
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(psi);
                Environment.Exit(0);
                return;
            }

            // Standalone / portable fallback: safe clean-up of internal LectureSmith folders & standalone exe
            var safeAppData = (Directory.Exists(appData) && appData.EndsWith("LectureSmith", StringComparison.OrdinalIgnoreCase))
                ? $"rd /s /q \"{appData}\" & " : "";
            var safeLocalAppData = (Directory.Exists(localAppData) && localAppData.EndsWith("LectureSmith", StringComparison.OrdinalIgnoreCase))
                ? $"rd /s /q \"{localAppData}\" & " : "";
            var safeTempDir = (Directory.Exists(tempDir) && tempDir.EndsWith("LectureSmith", StringComparison.OrdinalIgnoreCase))
                ? $"rd /s /q \"{tempDir}\" & " : "";

            var psiStandalone = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 1 /nobreak > NUL & {safeAppData}{safeLocalAppData}{safeTempDir}del /f /q \"{exePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            System.Diagnostics.Process.Start(psiStandalone);
            Environment.Exit(0);
            return;
        }

        // Wipe / Reset data mode: strictly delete only internal LectureSmith directories
        try { if (Directory.Exists(appData) && appData.EndsWith("LectureSmith", StringComparison.OrdinalIgnoreCase)) Directory.Delete(appData, true); } catch { }
        try { if (Directory.Exists(localAppData) && localAppData.EndsWith("LectureSmith", StringComparison.OrdinalIgnoreCase)) Directory.Delete(localAppData, true); } catch { }
        try { if (Directory.Exists(tempDir) && tempDir.EndsWith("LectureSmith", StringComparison.OrdinalIgnoreCase)) Directory.Delete(tempDir, true); } catch { }
    }
}
