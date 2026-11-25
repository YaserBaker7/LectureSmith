namespace LectureSmith.Models;

/// <summary>
/// Contains the output and token telemetry of the note generation pipeline.
/// </summary>
public class GenerationResult
{
    public string MarkdownContent { get; set; } = string.Empty;
    public List<string> SlideImagePaths { get; set; } = [];
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}
