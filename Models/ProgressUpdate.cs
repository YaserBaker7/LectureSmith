namespace LectureSmith.Models;

/// <summary>
/// Progress report emitted during note generation.
/// </summary>
public record ProgressUpdate(string Message, int Percentage);
