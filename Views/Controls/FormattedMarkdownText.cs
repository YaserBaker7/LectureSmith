using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using LectureSmith.ViewModels;

namespace LectureSmith.Views.Controls;

/// <summary>
/// Lightweight TextBlock renderer for inline Markdown runs (bold, italic, inline code, inline math, links).
/// Rebuilds Run elements only when Runs changes or theme changes.
/// </summary>
public class FormattedMarkdownText : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<MarkdownInlineRun>?> RunsProperty =
        AvaloniaProperty.Register<FormattedMarkdownText, IReadOnlyList<MarkdownInlineRun>?>(nameof(Runs));

    private static readonly FontFamily MonoFont = new("Cascadia Code, Consolas, Courier New, monospace");

    public IReadOnlyList<MarkdownInlineRun>? Runs
    {
        get => GetValue(RunsProperty);
        set => SetValue(RunsProperty, value);
    }

    static FormattedMarkdownText()
    {
        RunsProperty.Changed.AddClassHandler<FormattedMarkdownText>((ctl, _) => ctl.RebuildInlines());
    }

    public FormattedMarkdownText()
    {
        TextWrapping = TextWrapping.Wrap;
        ActualThemeVariantChanged += (_, _) => RebuildInlines();
    }

    private void RebuildInlines()
    {
        Inlines?.Clear();
        if (Inlines == null) return;

        var runs = Runs;
        if (runs == null || runs.Count == 0) return;

        var theme = ActualThemeVariant;
        var accentBrush = ResolveBrush("AccentSubtleText", theme);
        var codeBgBrush = ResolveBrush("CardBackgroundSecondary", theme);

        foreach (var item in runs)
        {
            var run = new Run(item.Text);

            if (item.IsBold)
                run.FontWeight = FontWeight.Bold;
            if (item.IsItalic)
                run.FontStyle = FontStyle.Italic;

            if (item.IsCode)
            {
                run.FontFamily = MonoFont;
                if (accentBrush != null) run.Foreground = accentBrush;
                if (codeBgBrush != null) run.Background = codeBgBrush;
            }
            else if (item.IsMath || item.IsLink)
            {
                if (accentBrush != null) run.Foreground = accentBrush;
            }

            Inlines.Add(run);
        }
    }

    private static IBrush? ResolveBrush(string key, Avalonia.Styling.ThemeVariant theme)
    {
        if (Application.Current?.TryGetResource(key, theme, out var val) == true && val is IBrush brush)
            return brush;
        return null;
    }
}
