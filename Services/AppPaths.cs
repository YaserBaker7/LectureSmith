using System.Security.Cryptography;
using System.Text;

namespace LectureSmith.Services;

/// <summary>
/// Centralizes application directories, file paths, safe filename helpers, and atomic persistence utilities.
/// </summary>
public static class AppPaths
{
    public static string AppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LectureSmith");

    public static string LocalAppDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LectureSmith");

    public static string TempDir { get; } = Path.Combine(Path.GetTempPath(), "LectureSmith");

    public static string SettingsFile => Path.Combine(AppDataDir, "settings.json");
    public static string HistoryFile => Path.Combine(AppDataDir, "history.json");
    public static string LogsDir => Path.Combine(AppDataDir, "logs");
    public static string CrashLogFile => Path.Combine(LogsDir, "crash.log");
    public static string TessDataDir => Path.Combine(LocalAppDataDir, "tessdata");

    /// <summary>
    /// Strips invalid path/filename characters and ensures a non-empty, safe base filename.
    /// </summary>
    public static string SanitizeFileName(string? name, string fallback = "Lecture")
    {
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            sb.Append(Array.IndexOf(invalidChars, ch) >= 0 ? '_' : ch);
        }

        var sanitized = sb.ToString().Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    /// <summary>
    /// Computes a deterministic 16-hex-character key for a file based on canonical path, size, and timestamp.
    /// Avoids cross-process randomization of string.GetHashCode() and same-filename collisions across folders.
    /// </summary>
    public static string ComputeFileCacheKey(string filePath, string variant = "full")
    {
        var fullPath = Path.GetFullPath(filePath);
        long size = 0;
        long lastWriteTicks = 0;
        try
        {
            var info = new FileInfo(fullPath);
            if (info.Exists)
            {
                size = info.Length;
                lastWriteTicks = info.LastWriteTimeUtc.Ticks;
            }
        }
        catch
        {
            // Fall back to path-only hash if metadata cannot be read
        }

        var payload = $"{fullPath.ToUpperInvariant()}|{size}|{lastWriteTicks}|{variant}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes.AsSpan(0, 8));
    }

    /// <summary>
    /// Atomically writes UTF-8 text to a target file via a sibling temporary file to prevent truncation on crash.
    /// </summary>
    public static void WriteAllTextAtomic(string targetPath, string content)
    {
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content, Encoding.UTF8);
            File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup of temp file
            }
        }
    }

    /// <summary>
    /// Preserves a corrupted config/data file alongside the original instead of silently losing it.
    /// </summary>
    public static void BackupCorruptFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            var backupPath = $"{filePath}.corrupt.{DateTime.Now:yyyyMMddHHmmss}.bak";
            File.Copy(filePath, backupPath, overwrite: true);
        }
        catch
        {
            // Non-fatal
        }
    }
}
