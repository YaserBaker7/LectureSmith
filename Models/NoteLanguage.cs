namespace LectureSmith.Models;

public enum NoteLanguage
{
    Auto,
    English,
    Danish
}

public static class NoteLanguageExtensions
{
    public static string DisplayName(this NoteLanguage lang) => lang switch
    {
        NoteLanguage.Auto => "Auto (match slide language)",
        NoteLanguage.English => "English",
        NoteLanguage.Danish => "Dansk (Danish)",
        _ => lang.ToString()
    };

    public static string AiInstruction(this NoteLanguage lang) => lang switch
    {
        NoteLanguage.Auto =>
            "Write your notes in the SAME language as the lecture slides. If the slides are in Danish, write in Danish. If in English, write in English.",
        NoteLanguage.English =>
            "Write ALL notes in English, regardless of the language of the input slides.",
        NoteLanguage.Danish =>
            "Skriv ALLE noter på dansk, uanset sproget i input-slides. Write ALL notes in Danish.",
        _ => ""
    };
}
