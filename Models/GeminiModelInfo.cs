namespace LectureSmith.Models;

/// <summary>
/// Describes a text-generation Gemini model with its API identifier, tier, token limits, and capabilities.
/// </summary>
public record GeminiModelInfo(
    string Id,
    string DisplayName,
    bool IsFree,
    int? OutputTokenLimit = null,
    int? InputTokenLimit = null,
    bool SupportsThinking = false,
    bool IsDeprecated = false,
    string? PricingSummary = null)
{
    public string TierLabel => string.IsNullOrEmpty(Id)
        ? "Offline"
        : IsFree
            ? "Free Tier"
            : "Paid Tier";

    public string DropdownDisplay => string.IsNullOrEmpty(Id)
        ? DisplayName
        : $"{(IsFree ? "🟢" : "🟣")} {DisplayName}{(IsDeprecated ? " (Deprecated)" : "")}  •  {TierLabel}";

    public override string ToString() => DisplayName;
}
