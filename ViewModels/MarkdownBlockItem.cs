using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LectureSmith.ViewModels;

public sealed record MarkdownInlineRun(
    string Text,
    bool IsBold = false,
    bool IsItalic = false,
    bool IsCode = false,
    bool IsMath = false,
    bool IsLink = false);

public sealed record MarkdownParagraphLine(IReadOnlyList<MarkdownInlineRun> Runs);

public sealed record MarkdownListRow(
    string Prefix,
    int IndentLevel,
    IReadOnlyList<MarkdownInlineRun> Runs)
{
    public Thickness IndentMargin => new(8 + IndentLevel * 18, 2, 0, 2);
}

public abstract partial class MarkdownBlockItem : ObservableObject
{
    public string Signature { get; protected set; } = string.Empty;
}

public sealed partial class HeadingBlockItem : MarkdownBlockItem
{
    [ObservableProperty] private int _level;
    [ObservableProperty] private IReadOnlyList<MarkdownInlineRun> _runs = [];

    public bool IsH1 => Level == 1;
    public bool IsH2 => Level == 2;
    public bool IsH3 => Level == 3;
    public bool IsH4Plus => Level >= 4;

    public double FontSize => Level switch
    {
        1 => 20,
        2 => 16.5,
        3 => 14,
        _ => 12.5
    };

    public HeadingBlockItem(int level, IReadOnlyList<MarkdownInlineRun> runs, string signature)
    {
        _level = level;
        _runs = runs;
        Signature = signature;
    }

    partial void OnLevelChanged(int value)
    {
        OnPropertyChanged(nameof(IsH1));
        OnPropertyChanged(nameof(IsH2));
        OnPropertyChanged(nameof(IsH3));
        OnPropertyChanged(nameof(IsH4Plus));
        OnPropertyChanged(nameof(FontSize));
    }

    public void Update(int level, IReadOnlyList<MarkdownInlineRun> runs, string signature)
    {
        Level = level;
        Runs = runs;
        Signature = signature;
    }
}

public sealed partial class ParagraphBlockItem : MarkdownBlockItem
{
    [ObservableProperty] private IReadOnlyList<MarkdownInlineRun> _runs = [];

    public ParagraphBlockItem(IReadOnlyList<MarkdownInlineRun> runs, string signature)
    {
        _runs = runs;
        Signature = signature;
    }

    public void Update(IReadOnlyList<MarkdownInlineRun> runs, string signature)
    {
        Runs = runs;
        Signature = signature;
    }
}

public sealed partial class FormulaBlockItem : MarkdownBlockItem
{
    [ObservableProperty] private string _formula = string.Empty;

    public FormulaBlockItem(string formula, string signature)
    {
        _formula = formula;
        Signature = signature;
    }

    public void Update(string formula, string signature)
    {
        Formula = formula;
        Signature = signature;
    }
}

public sealed partial class CodeBlockItem : MarkdownBlockItem
{
    [ObservableProperty] private string _language = string.Empty;
    [ObservableProperty] private string _code = string.Empty;

    public bool HasLanguage => !string.IsNullOrWhiteSpace(Language);

    public CodeBlockItem(string language, string code, string signature)
    {
        _language = language;
        _code = code;
        Signature = signature;
    }

    partial void OnLanguageChanged(string value) => OnPropertyChanged(nameof(HasLanguage));

    public void Update(string language, string code, string signature)
    {
        Language = language;
        Code = code;
        Signature = signature;
    }
}

public sealed partial class QuoteBlockItem : MarkdownBlockItem
{
    [ObservableProperty] private string _calloutTitle = string.Empty;
    [ObservableProperty] private IReadOnlyList<MarkdownParagraphLine> _paragraphs = [];

    public bool HasCalloutTitle => !string.IsNullOrWhiteSpace(CalloutTitle);

    public QuoteBlockItem(string calloutTitle, IReadOnlyList<MarkdownParagraphLine> paragraphs, string signature)
    {
        _calloutTitle = calloutTitle;
        _paragraphs = paragraphs;
        Signature = signature;
    }

    partial void OnCalloutTitleChanged(string value) => OnPropertyChanged(nameof(HasCalloutTitle));

    public void Update(string calloutTitle, IReadOnlyList<MarkdownParagraphLine> paragraphs, string signature)
    {
        CalloutTitle = calloutTitle;
        Paragraphs = paragraphs;
        Signature = signature;
    }
}

public sealed partial class ListBlockItem : MarkdownBlockItem
{
    [ObservableProperty] private IReadOnlyList<MarkdownListRow> _rows = [];

    public ListBlockItem(IReadOnlyList<MarkdownListRow> rows, string signature)
    {
        _rows = rows;
        Signature = signature;
    }

    public void Update(IReadOnlyList<MarkdownListRow> rows, string signature)
    {
        Rows = rows;
        Signature = signature;
    }
}

public sealed partial class TableBlockItem : MarkdownBlockItem
{
    [ObservableProperty] private IReadOnlyList<string> _headers = [];
    [ObservableProperty] private IReadOnlyList<IReadOnlyList<string>> _rows = [];

    public TableBlockItem(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows, string signature)
    {
        _headers = headers;
        _rows = rows;
        Signature = signature;
    }

    public void Update(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows, string signature)
    {
        Headers = headers;
        Rows = rows;
        Signature = signature;
    }
}

public sealed partial class SlideImageBlockItem : MarkdownBlockItem
{
    [ObservableProperty] private string _caption = string.Empty;
    [ObservableProperty] private string _imageRef = string.Empty;
    [ObservableProperty] private Bitmap? _image;

    public bool HasImage => Image != null;

    public SlideImageBlockItem(string caption, string imageRef, Bitmap? image, string signature)
    {
        _caption = caption;
        _imageRef = imageRef;
        _image = image;
        Signature = signature;
    }

    partial void OnImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasImage));
}

public sealed class DividerBlockItem : MarkdownBlockItem
{
    public DividerBlockItem(string signature)
    {
        Signature = signature;
    }
}
