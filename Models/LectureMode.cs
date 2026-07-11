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
        LectureMode.MissedLecture => "Missed Lecture (detailed explanations)",
        LectureMode.AttendedLecture => "Attended Lecture (concise notes)",
        _ => mode.ToString()
    };

    public static string AiInstruction(this LectureMode mode) => mode switch
    {
        LectureMode.MissedLecture =>
            "The student MISSED this lecture entirely. Provide very detailed, professor-style explanations for every single slide. " +
            "Think: 'What would the professor have said for this slide?' Include context, examples, and connections to the textbook. " +
            "The student should feel as if they attended the lecture after reading your notes.",
        LectureMode.AttendedLecture =>
            "The student ATTENDED this lecture but wants organized notes for review. Provide concise but comprehensive notes " +
            "focusing on key points, definitions, and important concepts. Keep explanations shorter and more to-the-point.",
        _ => ""
    };
}
