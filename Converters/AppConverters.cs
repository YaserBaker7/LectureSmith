using Avalonia.Data.Converters;
using LectureSmith.Models;

namespace LectureSmith.Converters;

public static class AppConverters
{
    public static readonly FuncValueConverter<LectureMode, string> LectureModeDisplay =
        new(m => m.DisplayName());

    public static readonly FuncValueConverter<OutputFormat, string> OutputFormatDisplay =
        new(f => f.DisplayName());

    public static readonly FuncValueConverter<SlideProcessingMode, string> SlideProcessingDisplay =
        new(s => s.DisplayName());

    public static readonly FuncValueConverter<NoteLanguage, string> LanguageDisplay =
        new(l => l.DisplayName());

    public static readonly FuncValueConverter<GeminiModelInfo?, string> ModelDisplay =
        new(m => m == null ? string.Empty : m.DropdownDisplay);
}
