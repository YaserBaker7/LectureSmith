namespace LectureSmith.Models;

/// <summary>
/// Persisted application settings (API key, output path, model preference).
/// </summary>
public class AppSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string LastOutputPath { get; set; } = string.Empty;
    public bool GenerateAudio { get; set; } = false;
    public string PreferredModelId { get; set; } = "gemini-2.5-flash";
}
