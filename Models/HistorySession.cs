using System.Text.Json.Serialization;

namespace LectureSmith.Models;

/// <summary>
/// Represents a saved lecture note generation session, including notes and follow-up chat history.
/// </summary>
public class HistorySession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string CourseName { get; set; } = string.Empty;
    public string LectureTitle { get; set; } = string.Empty;
    public string SlidesFileName { get; set; } = string.Empty;
    public string OutputFilePath { get; set; } = string.Empty;
    public OutputFormat Format { get; set; } = OutputFormat.Obsidian;
    public LectureMode Mode { get; set; } = LectureMode.MissedLecture;
    public string MarkdownContent { get; set; } = string.Empty;
    public List<ChatMessage> ChatHistory { get; set; } = [];

    [JsonIgnore]
    public string FormattedDate => Timestamp.ToString("MMM d, yyyy • h:mm tt");

    [JsonIgnore]
    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LectureTitle))
                return LectureTitle;
            if (!string.IsNullOrWhiteSpace(SlidesFileName))
                return Path.GetFileNameWithoutExtension(SlidesFileName);
            return "Lecture Notes";
        }
    }

    [JsonIgnore]
    public string DisplayCourse => string.IsNullOrWhiteSpace(CourseName) ? "General" : CourseName;

    [JsonIgnore]
    public string ChatCountText => ChatHistory.Count > 0
        ? $"💬 {ChatHistory.Count} message{(ChatHistory.Count == 1 ? "" : "s")}"
        : "No chat";

    [JsonIgnore]
    public bool FileExists => !string.IsNullOrWhiteSpace(OutputFilePath) && File.Exists(OutputFilePath);
}
