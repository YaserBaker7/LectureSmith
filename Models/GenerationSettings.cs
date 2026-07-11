namespace LectureSmith.Models;

public class GenerationSettings
{
    public Course Course { get; set; }
    public string CustomCourseName { get; set; } = string.Empty;
    public OutputFormat Format { get; set; }
    public LectureMode Mode { get; set; }
    public NoteLanguage Language { get; set; } = NoteLanguage.Auto;
    public string ExtraNotes { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public bool GenerateAudio { get; set; } = false;
    public string SelectedModelId { get; set; } = "gemini-2.5-flash";
    public SlideProcessingMode SlideProcessing { get; set; } = SlideProcessingMode.TextOnly;
    public UploadedFile? SlidesFile { get; set; }
    public List<UploadedFile> BookFiles { get; set; } = [];

    /// <summary>
    /// Returns the effective course name (custom name or enum display name).
    /// </summary>
    public string EffectiveCourseName =>
        Course == Course.Custom && !string.IsNullOrWhiteSpace(CustomCourseName)
            ? CustomCourseName
            : Course.DisplayName();
}
