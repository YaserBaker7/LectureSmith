using System.Text.Json.Serialization;

namespace LectureSmith.Models;

/// <summary>
/// Persisted application settings (encrypted API key, output path, model preference, course history, token telemetry).
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Legacy plaintext key field retained solely for one-time migration to <see cref="EncryptedApiKey"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; set; }

    /// <summary>
    /// Base64-encoded DPAPI-encrypted Gemini API key.
    /// </summary>
    public string EncryptedApiKey { get; set; } = string.Empty;

    public string LastOutputPath { get; set; } = string.Empty;
    public string PreferredModelId { get; set; } = "gemini-4-hydrogen";
    public string ThemePreference { get; set; } = "Dark";
    public List<string> CourseHistory { get; set; } = [];
    public List<TokenRecord> TokenUsageHistory { get; set; } = [];

    public int GetTokensThisWeek()
    {
        var now = DateTime.Now;
        var daysSinceMonday = ((int)now.DayOfWeek + 6) % 7;
        var startOfWeek = now.Date.AddDays(-daysSinceMonday);
        int total = 0;
        foreach (var rec in TokenUsageHistory)
        {
            if (rec.Timestamp >= startOfWeek)
                total += rec.InputTokens + rec.OutputTokens;
        }
        return total;
    }

    public int GetTokensThisMonth()
    {
        var now = DateTime.Now;
        var startOfMonth = new DateTime(now.Year, now.Month, 1);
        int total = 0;
        foreach (var rec in TokenUsageHistory)
        {
            if (rec.Timestamp >= startOfMonth)
                total += rec.InputTokens + rec.OutputTokens;
        }
        return total;
    }
}

public record TokenRecord(DateTime Timestamp, int InputTokens, int OutputTokens);
