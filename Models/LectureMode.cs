namespace LectureSmith.Models;

public enum LectureMode
{
    MissedLecture,
    AttendedLecture
}

public static class LectureModeExtensions
{
    public static string DisplayName(this LectureMode mode) => mode switch
    {
        LectureMode.MissedLecture => "Missed Lecture (Detailed)",
        LectureMode.AttendedLecture => "Attended Lecture (Concise)",
        _ => mode.ToString()
    };
}
