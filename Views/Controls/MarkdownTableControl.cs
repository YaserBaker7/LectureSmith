using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace LectureSmith.Views.Controls;

/// <summary>
/// Renders Markdown tables cleanly with dynamic columns, header highlight, and alternating row shading.
/// </summary>
public class MarkdownTableControl : Border
{
    public static readonly StyledProperty<IReadOnlyList<string>?> HeadersProperty =
        AvaloniaProperty.Register<MarkdownTableControl, IReadOnlyList<string>?>(nameof(Headers));

    public static readonly StyledProperty<IReadOnlyList<IReadOnlyList<string>>?> RowsProperty =
        AvaloniaProperty.Register<MarkdownTableControl, IReadOnlyList<IReadOnlyList<string>>?>(nameof(Rows));

    public IReadOnlyList<string>? Headers
    {
        get => GetValue(HeadersProperty);
        set => SetValue(HeadersProperty, value);
    }

    public IReadOnlyList<IReadOnlyList<string>>? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    static MarkdownTableControl()
    {
        HeadersProperty.Changed.AddClassHandler<MarkdownTableControl>((ctl, _) => ctl.RebuildTable());
        RowsProperty.Changed.AddClassHandler<MarkdownTableControl>((ctl, _) => ctl.RebuildTable());
    }

    public MarkdownTableControl()
    {
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        ClipToBounds = true;
        Margin = new Thickness(0, 6);
        ActualThemeVariantChanged += (_, _) => RebuildTable();
    }

    private void RebuildTable()
    {
        var headers = Headers ?? [];
        var rows = Rows ?? [];
        int colCount = Math.Max(headers.Count, rows.Count > 0 ? rows.Max(r => r.Count) : 0);

        if (colCount == 0)
        {
            Child = null;
            return;
        }

        var theme = ActualThemeVariant;
        var borderBrush = ResolveBrush("CardBorderBrush", theme);
        var headerBg = ResolveBrush("CardBackgroundSecondary", theme);
        var headerFg = ResolveBrush("AccentSubtleText", theme);
        var textFg = ResolveBrush("PreviewText", theme);
        var altRowBg = ResolveBrush("CardBackground", theme);

        BorderBrush = borderBrush;

        var grid = new Grid();
        for (int c = 0; c < colCount; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        int totalRows = (headers.Count > 0 ? 1 : 0) + rows.Count;
        for (int r = 0; r < totalRows; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        int currentRow = 0;
        if (headers.Count > 0)
        {
            for (int c = 0; c < colCount; c++)
            {
                var cell = CreateCell(
                    c < headers.Count ? headers[c] : string.Empty,
                    isHeader: true,
                    bg: headerBg,
                    fg: headerFg,
                    border: borderBrush);
                Grid.SetRow(cell, currentRow);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
            currentRow++;
        }

        for (int r = 0; r < rows.Count; r++)
        {
            var rowCells = rows[r];
            var rowBg = r % 2 == 1 ? altRowBg : null;

            for (int c = 0; c < colCount; c++)
            {
                var cell = CreateCell(
                    c < rowCells.Count ? rowCells[c] : string.Empty,
                    isHeader: false,
                    bg: rowBg,
                    fg: textFg,
                    border: borderBrush);
                Grid.SetRow(cell, currentRow);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
            currentRow++;
        }

        Child = grid;
    }

    private static Border CreateCell(string text, bool isHeader, IBrush? bg, IBrush? fg, IBrush? border)
    {
        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            LineHeight = 18,
            FontWeight = isHeader ? FontWeight.Bold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (fg != null) tb.Foreground = fg;

        var cell = new Border
        {
            Padding = new Thickness(10, 7),
            BorderThickness = new Thickness(0, 0, 1, 1),
            Child = tb
        };
        if (bg != null) cell.Background = bg;
        if (border != null) cell.BorderBrush = border;
        return cell;
    }

    private static IBrush? ResolveBrush(string key, Avalonia.Styling.ThemeVariant theme)
    {
        if (Application.Current?.TryGetResource(key, theme, out var val) == true && val is IBrush brush)
            return brush;
        return null;
    }
}
