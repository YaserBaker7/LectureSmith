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
        NoteLanguage.Auto => "Auto (Match Slides)",
        NoteLanguage.English => "English",
        NoteLanguage.Danish => "Dansk (Danish)",
        _ => lang.ToString()
    };
}
