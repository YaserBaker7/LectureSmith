namespace LectureSmith.Models;

/// <summary>
/// Contains the output of the note generation pipeline.
/// </summary>
public class GenerationResult
{
    public string MarkdownContent { get; set; } = string.Empty;
    public List<string> SlideImagePaths { get; set; } = [];
}
