namespace LectureSmith.Models;

public enum OutputFormat
{
    Obsidian,
    HTML,
    PDF
}

public static class OutputFormatExtensions
{
    public static string DisplayName(this OutputFormat format) => format switch
    {
        OutputFormat.Obsidian => "Obsidian Notes (.md)",
        OutputFormat.HTML => "HTML File (.html)",
        OutputFormat.PDF => "PDF Document (.pdf)",
        _ => format.ToString()
    };

    public static string FileExtension(this OutputFormat format) => format switch
    {
        OutputFormat.Obsidian => ".md",
        OutputFormat.HTML => ".html",
        OutputFormat.PDF => ".pdf",
        _ => ".txt"
    };
}
