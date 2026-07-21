using System.Collections.Generic;

namespace LectureSmith.Models;

/// <summary>
/// Persisted application settings (API key, output path, model preference, course history).
/// </summary>
public class AppSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string LastOutputPath { get; set; } = string.Empty;
    public string PreferredModelId { get; set; } = "gemini-2.5-flash";
    public List<string> CourseHistory { get; set; } = new();
    public List<TokenRecord> TokenUsageHistory { get; set; } = new();

    public int GetTokensThisWeek()
    {
        var startOfWeek = DateTime.Now.Date.AddDays(-(int)DateTime.Now.DayOfWeek);
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
        var startOfMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
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
