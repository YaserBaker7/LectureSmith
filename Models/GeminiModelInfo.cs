namespace LectureSmith.Models;

/// <summary>
/// Describes a Gemini model with its API identifier, display name, and pricing tier.
/// </summary>
public record GeminiModelInfo(string Id, string DisplayName, bool IsFree)
{
    public override string ToString() => DisplayName;
}
