using Avalonia.Media.Imaging;
using LectureSmith.Services;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace LectureSmith.ViewModels;

/// <summary>
/// Incrementally converts streaming or completed Markdown into virtualizable block items
/// with bounded, decoded-to-width slide image caching so large decks stay responsive and low-RAM.
/// </summary>
public sealed partial class MarkdownViewerViewModel : ViewModelBase, IDisposable
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .UseAutoLinks()
        .UseTaskLists()
        .Build();

    private const int DecodeTargetWidth = 480;
    private const int MaxCachedBitmaps = 80;

    private readonly Dictionary<string, string> _slidePathByFileName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _slidePathByNumber = [];
    private readonly Dictionary<string, Bitmap> _decodedBitmapCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _bitmapLruOrder = new();

    private string? _baseOutputFolder;
    private string _lastNormalizedMarkdown = string.Empty;

    public ObservableCollection<MarkdownBlockItem> Blocks { get; } = [];

    public void RegisterSlideImagePaths(IEnumerable<string>? imagePaths, string? baseOutputFolder = null)
    {
        ClearImageCache();
        _baseOutputFolder = baseOutputFolder;

        if (imagePaths == null) return;

        foreach (var path in imagePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var fileName = Path.GetFileName(path);
            _slidePathByFileName[fileName] = path;

            var num = PdfExtractorService.GetSlideNumber(path);
            if (num != int.MaxValue)
                _slidePathByNumber[num] = path;
        }
    }

    public void SetBaseOutputFolder(string? outputFilePath)
    {
        var dir = string.IsNullOrWhiteSpace(outputFilePath) ? null : Path.GetDirectoryName(outputFilePath);
        if (string.Equals(dir, _baseOutputFolder, StringComparison.OrdinalIgnoreCase))
            return;

        ClearImageCache();
        _baseOutputFolder = dir;

        if (!string.IsNullOrWhiteSpace(dir))
        {
            var slidesDir = Path.Combine(dir, "slides");
            if (Directory.Exists(slidesDir))
            {
                foreach (var file in Directory.GetFiles(slidesDir, "slide_*.png"))
                {
                    _slidePathByFileName[Path.GetFileName(file)] = file;
                    var num = PdfExtractorService.GetSlideNumber(file);
                    if (num != int.MaxValue)
                        _slidePathByNumber[num] = file;
                }
            }
        }
    }

    public void Clear()
    {
        _lastNormalizedMarkdown = string.Empty;
        Blocks.Clear();
        ClearImageCache();
    }

    public void UpdateMarkdown(string? rawMarkdown)
    {
        if (string.IsNullOrWhiteSpace(rawMarkdown))
        {
            if (Blocks.Count > 0)
                Blocks.Clear();
            _lastNormalizedMarkdown = string.Empty;
            return;
        }

        var normalized = OutputExporterService.NormalizeObsidianImages(rawMarkdown);
        if (string.Equals(normalized, _lastNormalizedMarkdown, StringComparison.Ordinal))
            return;

        _lastNormalizedMarkdown = normalized;

        var doc = Markdown.Parse(normalized, Pipeline);
        var parsedBlocks = new List<MarkdownBlockItem>(doc.Count);

        foreach (var block in doc)
        {
            var item = ConvertBlock(block, normalized);
            if (item != null)
                parsedBlocks.Add(item);
        }

        ApplyIncrementalDiff(parsedBlocks);
    }

    private void ApplyIncrementalDiff(List<MarkdownBlockItem> parsed)
    {
        int commonPrefix = 0;
        int maxCompare = Math.Min(Blocks.Count, parsed.Count);

        // Completed prefix blocks whose signature didn't change are left 100% untouched
        while (commonPrefix < maxCompare &&
               string.Equals(Blocks[commonPrefix].Signature, parsed[commonPrefix].Signature, StringComparison.Ordinal))
        {
            commonPrefix++;
        }

        // Update the first changed block in-place if compatible (e.g., streaming tail paragraph finished + new blocks followed)
        if (commonPrefix < Blocks.Count &&
            commonPrefix < parsed.Count &&
            TryUpdateBlockInPlace(Blocks[commonPrefix], parsed[commonPrefix]))
        {
            commonPrefix++;
        }

        // Trim divergent tail
        for (int i = Blocks.Count - 1; i >= commonPrefix; i--)
            Blocks.RemoveAt(i);

        // Append newly parsed tail blocks
        for (int i = commonPrefix; i < parsed.Count; i++)
            Blocks.Add(parsed[i]);
    }

    private static bool TryUpdateBlockInPlace(MarkdownBlockItem target, MarkdownBlockItem updated)
    {
        switch (target, updated)
        {
            case (ParagraphBlockItem pTarget, ParagraphBlockItem pNew):
                pTarget.Update(pNew.Runs, pNew.Signature);
                return true;
            case (HeadingBlockItem hTarget, HeadingBlockItem hNew):
                hTarget.Update(hNew.Level, hNew.Runs, hNew.Signature);
                return true;
            case (FormulaBlockItem fTarget, FormulaBlockItem fNew):
                fTarget.Update(fNew.Formula, fNew.Signature);
                return true;
            case (CodeBlockItem cTarget, CodeBlockItem cNew):
                cTarget.Update(cNew.Language, cNew.Code, cNew.Signature);
                return true;
            case (QuoteBlockItem qTarget, QuoteBlockItem qNew):
                qTarget.Update(qNew.CalloutTitle, qNew.Paragraphs, qNew.Signature);
                return true;
            case (ListBlockItem lTarget, ListBlockItem lNew):
                lTarget.Update(lNew.Rows, lNew.Signature);
                return true;
            case (TableBlockItem tTarget, TableBlockItem tNew):
                tTarget.Update(tNew.Headers, tNew.Rows, tNew.Signature);
                return true;
            default:
                return false;
        }
    }

    private MarkdownBlockItem? ConvertBlock(Block block, string source)
    {
        var rawSlice = SafeSlice(source, block.Span);

        switch (block)
        {
            case HeadingBlock heading:
                return new HeadingBlockItem(heading.Level, ExtractRuns(heading.Inline), $"H{heading.Level}:{rawSlice}");

            case FencedCodeBlock fenced:
                return new CodeBlockItem(
                    fenced.Info ?? string.Empty,
                    fenced.Lines.ToString().TrimEnd(),
                    $"C:{rawSlice}");

            case CodeBlock code:
                return new CodeBlockItem(
                    string.Empty,
                    code.Lines.ToString().TrimEnd(),
                    $"C:{rawSlice}");

            case QuoteBlock quote:
                return ConvertQuoteBlock(quote, rawSlice);

            case ListBlock list:
                var listRows = new List<MarkdownListRow>();
                CollectListRows(list, depth: 0, listRows);
                return new ListBlockItem(listRows, $"L:{rawSlice}");

            case Table table:
                return ConvertTableBlock(table, rawSlice);

            case ThematicBreakBlock:
                return new DividerBlockItem($"D:{rawSlice}");

            case ParagraphBlock paragraph:
                return ConvertParagraphBlock(paragraph, rawSlice);

            default:
                return null;
        }
    }

    private MarkdownBlockItem? ConvertParagraphBlock(ParagraphBlock paragraph, string rawSlice)
    {
        if (paragraph.Inline == null) return null;

        // Standalone image paragraph -> SlideImageBlockItem
        if (paragraph.Inline.FirstChild is LinkInline { IsImage: true } img && img.NextSibling == null)
        {
            var caption = ExtractPlainText(img);
            if (string.IsNullOrWhiteSpace(caption))
                caption = Path.GetFileNameWithoutExtension(img.Url ?? "Slide");

            var bitmap = ResolveBitmapForRef(img.Url);
            return new SlideImageBlockItem(caption, img.Url ?? string.Empty, bitmap, $"IMG:{rawSlice}:{(bitmap != null ? "1" : "0")}");
        }

        // Standalone display formula $$...$$
        var plain = ExtractPlainText(paragraph.Inline).Trim();
        if (plain.StartsWith("$$", StringComparison.Ordinal) &&
            plain.EndsWith("$$", StringComparison.Ordinal) &&
            plain.Length > 4)
        {
            var formula = MathFormatter.FormatFormula(plain[2..^2], useMarkdownEmphasis: false);
            return new FormulaBlockItem(formula, $"F:{rawSlice}");
        }

        var runs = ExtractRuns(paragraph.Inline);
        if (runs.Count == 0) return null;
        return new ParagraphBlockItem(runs, $"P:{rawSlice}");
    }

    private static QuoteBlockItem ConvertQuoteBlock(QuoteBlock quote, string rawSlice)
    {
        var paragraphs = new List<MarkdownParagraphLine>();
        string calloutTitle = string.Empty;

        foreach (var child in quote)
        {
            if (child is ParagraphBlock p && p.Inline != null)
            {
                var runs = ExtractRuns(p.Inline).ToList();
                if (paragraphs.Count == 0 && runs.Count > 0)
                {
                    var plainHeader = ExtractPlainText(p.Inline);
                    var m = ObsidianCalloutRegex().Match(plainHeader);
                    if (m.Success)
                    {
                        var kind = m.Groups[1].Value.ToLowerInvariant();
                        calloutTitle = kind switch
                        {
                            "tip" => "💡 Tip",
                            "important" => "📌 Important",
                            "warning" => "⚠️ Warning",
                            "note" => "📝 Note",
                            _ => $"💬 {char.ToUpperInvariant(kind[0])}{kind[1..]}"
                        };

                        int toTrim = m.Length;
                        while (toTrim > 0 && runs.Count > 0)
                        {
                            if (runs[0].Text.Length <= toTrim)
                            {
                                toTrim -= runs[0].Text.Length;
                                runs.RemoveAt(0);
                            }
                            else
                            {
                                runs[0] = runs[0] with { Text = runs[0].Text[toTrim..].TrimStart() };
                                toTrim = 0;
                            }
                        }
                    }
                }

                if (runs.Count > 0)
                    paragraphs.Add(new MarkdownParagraphLine(runs));
            }
        }

        return new QuoteBlockItem(calloutTitle, paragraphs, $"Q:{rawSlice}");
    }

    private static void CollectListRows(ListBlock list, int depth, List<MarkdownListRow> rows)
    {
        int order = int.TryParse(list.OrderedStart, out var s) ? s : 1;
        string bullet = depth == 0 ? "•" : "◦";

        foreach (var child in list)
        {
            if (child is not ListItemBlock item) continue;
            string prefix = list.IsOrdered ? $"{order++}." : bullet;

            foreach (var subBlock in item)
            {
                if (subBlock is ParagraphBlock p)
                {
                    rows.Add(new MarkdownListRow(prefix, depth, ExtractRuns(p.Inline)));
                    prefix = " ";
                }
                else if (subBlock is ListBlock nested)
                {
                    CollectListRows(nested, depth + 1, rows);
                }
            }
        }
    }

    private static TableBlockItem ConvertTableBlock(Table table, string rawSlice)
    {
        var tableRows = table.OfType<TableRow>().ToList();
        var headers = new List<string>();
        var bodyRows = new List<IReadOnlyList<string>>();

        foreach (var row in tableRows)
        {
            var cells = row.OfType<TableCell>()
                .Select(c =>
                {
                    var text = string.Join(" ", c.OfType<ParagraphBlock>().Select(p => ExtractPlainText(p.Inline)));
                    return MathFormatter.ReplaceInlineAndDisplayMath(text, useMarkdownEmphasis: false);
                })
                .ToList();

            if (row.IsHeader && headers.Count == 0)
                headers = cells;
            else
                bodyRows.Add(cells);
        }

        return new TableBlockItem(headers, bodyRows, $"T:{rawSlice}");
    }

    private Bitmap? ResolveBitmapForRef(string? imageRef)
    {
        if (string.IsNullOrWhiteSpace(imageRef))
            return null;

        var cleanRef = imageRef.Trim().Replace('\\', '/');
        var fileName = Path.GetFileName(cleanRef);

        if (_decodedBitmapCache.TryGetValue(fileName, out var cached))
            return cached;

        string? resolvedPath = null;

        if (_slidePathByFileName.TryGetValue(fileName, out var byName) && File.Exists(byName))
        {
            resolvedPath = byName;
        }
        else
        {
            var numMatch = SlideNumberRegex().Match(fileName);
            if (numMatch.Success && int.TryParse(numMatch.Groups[1].Value, out int slideNum) &&
                _slidePathByNumber.TryGetValue(slideNum, out var byNum) && File.Exists(byNum))
            {
                resolvedPath = byNum;
            }
            else if (!string.IsNullOrWhiteSpace(_baseOutputFolder))
            {
                var candidate = Path.GetFullPath(Path.Combine(_baseOutputFolder, cleanRef.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(candidate))
                    resolvedPath = candidate;
            }
            else if (File.Exists(cleanRef))
            {
                resolvedPath = cleanRef;
            }
        }

        if (resolvedPath == null)
            return null;

        try
        {
            using var fs = File.OpenRead(resolvedPath);
            var bmp = Bitmap.DecodeToWidth(fs, DecodeTargetWidth);

            // Bounded cache prevents memory growth on very large slide decks
            if (_decodedBitmapCache.Count >= MaxCachedBitmaps && _bitmapLruOrder.Count > 0)
            {
                var oldestKey = _bitmapLruOrder.Dequeue();
                if (_decodedBitmapCache.Remove(oldestKey, out var oldBmp))
                    oldBmp.Dispose();
            }

            _decodedBitmapCache[fileName] = bmp;
            _bitmapLruOrder.Enqueue(fileName);
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void ClearImageCache()
    {
        _slidePathByFileName.Clear();
        _slidePathByNumber.Clear();
        _bitmapLruOrder.Clear();
        foreach (var bmp in _decodedBitmapCache.Values)
            bmp.Dispose();
        _decodedBitmapCache.Clear();
    }

    private static IReadOnlyList<MarkdownInlineRun> ExtractRuns(ContainerInline? container)
    {
        if (container == null) return [];
        var runs = new List<MarkdownInlineRun>();
        AppendInlines(container, runs, bold: false, italic: false, isLink: false);
        return runs;
    }

    private static void AppendInlines(
        ContainerInline? container,
        List<MarkdownInlineRun> runs,
        bool bold,
        bool italic,
        bool isLink)
    {
        if (container == null) return;

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    AppendMathAwareRuns(lit.Content.ToString(), runs, bold, italic, isLink);
                    break;

                case CodeInline code:
                    runs.Add(new MarkdownInlineRun(code.Content, IsCode: true));
                    break;

                case EmphasisInline emp:
                    bool b = bold || emp.DelimiterCount >= 2;
                    bool it = italic || emp.DelimiterCount == 1;
                    AppendInlines(emp, runs, b, it, isLink);
                    break;

                case LinkInline link:
                    AppendInlines(link, runs, bold, italic, isLink: true);
                    break;

                case LineBreakInline:
                    runs.Add(new MarkdownInlineRun("\n"));
                    break;

                case ContainerInline sub:
                    AppendInlines(sub, runs, bold, italic, isLink);
                    break;
            }
        }
    }

    private static void AppendMathAwareRuns(
        string text,
        List<MarkdownInlineRun> runs,
        bool bold,
        bool italic,
        bool isLink)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (!text.Contains('$'))
        {
            runs.Add(new MarkdownInlineRun(text, IsBold: bold, IsItalic: italic, IsLink: isLink));
            return;
        }

        int lastIdx = 0;
        foreach (Match m in MathFormatter.InlineMathRegex().Matches(text))
        {
            if (m.Index > lastIdx)
                runs.Add(new MarkdownInlineRun(text[lastIdx..m.Index], IsBold: bold, IsItalic: italic, IsLink: isLink));

            var formatted = MathFormatter.FormatFormula(m.Groups[1].Value, useMarkdownEmphasis: false);
            runs.Add(new MarkdownInlineRun(formatted, IsBold: bold, IsItalic: true, IsMath: true));
            lastIdx = m.Index + m.Length;
        }

        if (lastIdx < text.Length)
            runs.Add(new MarkdownInlineRun(text[lastIdx..], IsBold: bold, IsItalic: italic, IsLink: isLink));
    }

    private static string ExtractPlainText(ContainerInline? container)
    {
        if (container == null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    sb.Append(lit.Content.ToString());
                    break;
                case CodeInline code:
                    sb.Append(code.Content);
                    break;
                case ContainerInline sub:
                    sb.Append(ExtractPlainText(sub));
                    break;
            }
        }
        return sb.ToString();
    }

    private static string SafeSlice(string source, SourceSpan span)
    {
        if (span.IsEmpty || span.Start < 0 || span.Start >= source.Length)
            return string.Empty;
        int len = Math.Min(span.Length, source.Length - span.Start);
        return source.Substring(span.Start, len);
    }

    public void Dispose()
    {
        ClearImageCache();
    }

    [GeneratedRegex(@"^\[!(\w+)\]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ObsidianCalloutRegex();

    [GeneratedRegex(@"(\d+)")]
    private static partial Regex SlideNumberRegex();
}
