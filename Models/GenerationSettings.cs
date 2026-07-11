namespace LectureSmith.Models;

public class GenerationSettings
{
    public string CourseName { get; set; } = string.Empty;
    public OutputFormat Format { get; set; }
    public LectureMode Mode { get; set; }
    public NoteLanguage Language { get; set; } = NoteLanguage.Auto;
    public string ExtraNotes { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string SelectedModelId { get; set; } = "gemini-2.5-flash";
    public SlideProcessingMode SlideProcessing { get; set; } = SlideProcessingMode.TextOnly;
    public UploadedFile? SlidesFile { get; set; }
    public List<UploadedFile> BookFiles { get; set; } = [];

    /// <summary>
    /// Returns the effective course name.
    /// </summary>
    public string EffectiveCourseName => CourseName;
}
