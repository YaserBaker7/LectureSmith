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
}
