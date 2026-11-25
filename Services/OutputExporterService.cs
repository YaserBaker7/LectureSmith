using LectureSmith.Models;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PdfContainer = QuestPDF.Infrastructure.IContainer;

namespace LectureSmith.Services;

public partial class OutputExporterService : IOutputExporterService
{
    static OutputExporterService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public async Task<string> ExportAsync(GenerationResult result, GenerationSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(settings.OutputPath);

        var rawCourse = string.IsNullOrWhiteSpace(settings.CourseName)
            ? Path.GetFileNameWithoutExtension(settings.SlidesFile?.FileName)
            : settings.CourseName;
        var safeCourse = AppPaths.SanitizeFileName(rawCourse, "Lecture");
        var baseName = $"{safeCourse} - Notes";

        if (settings.Format == OutputFormat.Obsidian)
        {
            var slidesOutputDir = Path.Combine(settings.OutputPath, "slides");
            Directory.CreateDirectory(slidesOutputDir);

            foreach (var imgPath in result.SlideImagePaths)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(imgPath))
                    throw new FileNotFoundException($"Missing extracted slide image '{imgPath}'.", imgPath);

                var destPath = Path.Combine(slidesOutputDir, Path.GetFileName(imgPath));
                await CopySlideWithRetryAsync(imgPath, destPath, ct);
            }

            ct.ThrowIfCancellationRequested();
            return await ExportObsidianAsync(result, settings, baseName, ct);
        }

        // HTML & PDF embed slide images directly; stage them in %TEMP% so OutputPath stays clean
        var stagingDir = Path.Combine(AppPaths.TempDir, $"export_staging_{Guid.NewGuid():N}");
        var stagingSlidesDir = Path.Combine(stagingDir, "slides");

        try
        {
            Directory.CreateDirectory(stagingSlidesDir);
            foreach (var imgPath in result.SlideImagePaths)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(imgPath))
                {
                    var destPath = Path.Combine(stagingSlidesDir, Path.GetFileName(imgPath));
                    File.Copy(imgPath, destPath, overwrite: true);
                }
            }

            ct.ThrowIfCancellationRequested();
            return settings.Format switch
            {
                OutputFormat.HTML => await ExportHtmlAsync(result, settings, baseName, stagingDir, ct),
                OutputFormat.PDF => await ExportPdfAsync(result, settings, baseName, stagingDir, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(settings.Format))
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, recursive: true);
            }
            catch { }
        }
    }

    private static string GetUniqueFilePath(string directory, string baseName, string extension)
    {
        var candidate = Path.Combine(directory, $"{baseName}{extension}");
        if (!File.Exists(candidate)) return candidate;

        int counter = 1;
        while (File.Exists(Path.Combine(directory, $"{baseName} ({counter}){extension}")))
            counter++;

        return Path.Combine(directory, $"{baseName} ({counter}){extension}");
    }

    private static async Task CopySlideWithRetryAsync(string srcPath, string destPath, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                File.Copy(srcPath, destPath, overwrite: true);
                if (File.Exists(destPath) && new FileInfo(destPath).Length > 0)
                    return;
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(150 * attempt, ct);
            }
        }

        throw new IOException($"Failed to copy slide image to '{destPath}'.");
    }

    private static async Task<string> ExportObsidianAsync(
        GenerationResult result, GenerationSettings settings, string baseName, CancellationToken ct)
    {
        var outputFile = GetUniqueFilePath(settings.OutputPath, baseName, ".md");
        await File.WriteAllTextAsync(outputFile, result.MarkdownContent, Encoding.UTF8, ct);
        return outputFile;
    }

    private static async Task<string> ExportHtmlAsync(
        GenerationResult result, GenerationSettings settings, string baseName, string imageBasePath, CancellationToken ct)
    {
        var outputFile = GetUniqueFilePath(settings.OutputPath, baseName, ".html");
        var htmlBody = await RenderMarkdownToSelfContainedHtmlBodyAsync(result.MarkdownContent, imageBasePath, ct);
        var fullHtml = BuildHtmlDocument(baseName, htmlBody, printOnlyLayout: false);

        await File.WriteAllTextAsync(outputFile, fullHtml, Encoding.UTF8, ct);
        return outputFile;
    }

    private static async Task<string> ExportPdfAsync(
        GenerationResult result, GenerationSettings settings, string baseName, string imageBasePath, CancellationToken ct)
    {
        var outputFile = GetUniqueFilePath(settings.OutputPath, baseName, ".pdf");

        var browserPath = FindHeadlessBrowserPath();
        if (!string.IsNullOrEmpty(browserPath))
        {
            var tempHtml = Path.Combine(Path.GetTempPath(), $"LectureSmith_PDF_{Guid.NewGuid():N}.html");
            var tempUserData = Path.Combine(Path.GetTempPath(), $"LectureSmith_Browser_{Guid.NewGuid():N}");
            try
            {
                var htmlBody = await RenderMarkdownToSelfContainedHtmlBodyAsync(result.MarkdownContent, imageBasePath, ct);
                var printableHtml = BuildHtmlDocument(baseName, htmlBody, printOnlyLayout: true);
                await File.WriteAllTextAsync(tempHtml, printableHtml, Encoding.UTF8, ct);

                Directory.CreateDirectory(tempUserData);

                var psi = new ProcessStartInfo
                {
                    FileName = browserPath,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("--headless=new");
                psi.ArgumentList.Add("--disable-gpu");
                psi.ArgumentList.Add("--no-first-run");
                psi.ArgumentList.Add("--no-default-browser-check");
                psi.ArgumentList.Add("--no-pdf-header-footer");
                psi.ArgumentList.Add("--run-all-compositor-stages-before-draw");
                psi.ArgumentList.Add("--virtual-time-budget=5000");
                psi.ArgumentList.Add($"--user-data-dir={tempUserData}");
                psi.ArgumentList.Add($"--print-to-pdf={outputFile}");
                psi.ArgumentList.Add(new Uri(tempHtml).AbsoluteUri);

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
                    try
                    {
                        await proc.WaitForExitAsync(timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                        throw;
                    }
                    catch (OperationCanceledException)
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                    }

                    if (File.Exists(outputFile) && new FileInfo(outputFile).Length > 0)
                        return outputFile;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OutputExporterService] Headless PDF generation failed: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(tempHtml)) File.Delete(tempHtml); } catch { }
                try { if (Directory.Exists(tempUserData)) Directory.Delete(tempUserData, recursive: true); } catch { }
            }
        }

        // Fallback: AST-driven QuestPDF export (completely offline, no browser required)
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var normalizedMd = NormalizeObsidianImages(result.MarkdownContent);
            var doc = Markdown.Parse(normalizedMd, CreateMarkdownPipeline());

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(45);
                    page.DefaultTextStyle(x => x.FontSize(10.5f).FontFamily("Segoe UI").LineHeight(1.35f));

                    page.Header().PaddingBottom(8).Text(baseName)
                        .FontSize(17).Bold().FontColor(Colors.Purple.Darken2);

                    page.Content().PaddingVertical(6).Column(col =>
                    {
                        col.Spacing(4);
                        RenderBlocks(col, doc, imageBasePath);
                    });

                    page.Footer().AlignCenter().Text(text =>
                    {
                        text.Span("Generated by LectureSmith • ");
                        text.CurrentPageNumber();
                        text.Span(" / ");
                        text.TotalPages();
                    });
                });
            }).GeneratePdf(outputFile);
        }, ct);

        return outputFile;
    }

    private static async Task<string> RenderMarkdownToSelfContainedHtmlBodyAsync(
        string markdown, string imageBasePath, CancellationToken ct)
    {
        var normalized = NormalizeObsidianImages(markdown);
        var pipeline = CreateMarkdownPipeline();
        var htmlBody = Markdown.ToHtml(normalized, pipeline);
        return await EmbedImagesAsBase64Async(htmlBody, imageBasePath, ct);
    }

    /// <summary>
    /// Converts Obsidian ![[slides/slide_XX.png]] syntax into standard markdown images so Markdig &amp; PDF renderers embed them.
    /// </summary>
    internal static string NormalizeObsidianImages(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return string.Empty;
        return ObsidianImageRegex().Replace(markdown, m =>
        {
            var path = m.Groups[1].Value.Trim();
            return $"![Slide]({path})";
        });
    }

    private static MarkdownPipeline CreateMarkdownPipeline()
        => new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseGridTables()
            .UseAutoLinks()
            .UseTaskLists()
            .Build();

    private static string? FindHeadlessBrowserPath()
    {
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Edge\Application\msedge.exe"),
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string BuildHtmlDocument(string title, string bodyContent, bool printOnlyLayout)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        var screenStyles = printOnlyLayout
            ? """
                body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Inter", sans-serif; color: #1e293b; background: #ffffff; line-height: 1.65; font-size: 14px; }
                h1 { color: #5b21b6; font-size: 22px; font-weight: 700; margin: 22px 0 10px; border-bottom: 2px solid #e2e8f0; padding-bottom: 6px; page-break-after: avoid; break-after: avoid; }
                h2 { color: #6d28d9; font-size: 18px; font-weight: 700; margin: 20px 0 8px; border-bottom: 1px solid #f1f5f9; padding-bottom: 4px; page-break-after: avoid; break-after: avoid; }
                h3 { color: #7c3aed; font-size: 15px; font-weight: 600; margin: 16px 0 6px; page-break-after: avoid; break-after: avoid; }
                h4, h5, h6 { color: #475569; font-size: 13.5px; font-weight: 600; margin: 14px 0 4px; page-break-after: avoid; break-after: avoid; }
                strong { color: #0f172a; }
                blockquote { border-left: 4px solid #8b5cf6; background: #f8faff; padding: 10px 16px; margin: 12px 0; border-radius: 0 6px 6px 0; page-break-inside: avoid; break-inside: avoid; }
                code { background: #f1f5f9; color: #6d28d9; padding: 2px 5px; border-radius: 4px; font-size: 0.9em; font-family: 'Cascadia Code', Consolas, monospace; }
                pre { background: #f8fafc; border: 1px solid #e2e8f0; border-radius: 6px; padding: 12px; margin: 12px 0; overflow-x: auto; page-break-inside: avoid; break-inside: avoid; }
                pre code { background: none; color: #1e293b; padding: 0; }
                th { background: #f1f5f9; color: #4338ca; font-weight: 600; }
                th, td { border: 1px solid #cbd5e1; padding: 8px 12px; text-align: left; font-size: 13px; }
                tr:nth-child(even) { background: #f8fafc; }
                img { display: block; max-width: 95%; margin: 16px auto; border-radius: 6px; border: 1px solid #cbd5e1; box-shadow: 0 2px 8px rgba(0,0,0,0.06); page-break-inside: avoid; break-inside: avoid; }
                hr { border: none; border-top: 1px solid #e2e8f0; margin: 20px 0; }
                mjx-container, .mjx-chtml { color: #1e293b !important; }
              """
            : """
                :root { --bg: #0f121e; --surface: #151a2c; --card: #1a2138; --text: #e2e8f0; --accent: #7c3aed; --accent2: #a78bfa; --border: #283250; }
                body { font-family: 'Segoe UI', Inter, -apple-system, sans-serif; background: var(--bg); color: var(--text); line-height: 1.7; padding: 2rem; max-width: 920px; margin: 0 auto; }
                h1 { color: var(--accent2); font-size: 2rem; margin: 2rem 0 1rem; border-bottom: 2px solid var(--accent); padding-bottom: 0.5rem; }
                h2 { color: var(--accent2); font-size: 1.5rem; margin: 2rem 0 0.8rem; }
                h3 { color: #c4b5fd; font-size: 1.2rem; margin: 1.5rem 0 0.5rem; }
                img { max-width: 100%; border-radius: 8px; border: 1px solid var(--border); margin: 1rem 0; box-shadow: 0 4px 12px rgba(0,0,0,0.3); }
                blockquote { border-left: 4px solid var(--accent); padding: 0.8rem 1.2rem; margin: 1rem 0; background: var(--card); border-radius: 0 8px 8px 0; }
                code { background: var(--card); padding: 2px 6px; border-radius: 4px; font-size: 0.9em; font-family: 'Cascadia Code', Consolas, monospace; }
                pre { background: var(--surface); padding: 1rem; border-radius: 8px; overflow-x: auto; border: 1px solid var(--border); margin: 1rem 0; }
                pre code { background: none; padding: 0; }
                th, td { border: 1px solid var(--border); padding: 0.6rem 1rem; text-align: left; }
                th { background: var(--card); color: var(--accent2); }
                tr:nth-child(even) { background: rgba(255,255,255,0.02); }
                hr { border: none; border-top: 1px solid var(--border); margin: 2rem 0; }
                strong { color: #c4b5fd; }
                .mjx-chtml { color: #c4b5fd !important; }
                @media print {
                    body { background: #ffffff !important; color: #1e293b !important; padding: 0 !important; max-width: 100% !important; }
                    h1 { color: #5b21b6 !important; border-bottom-color: #e2e8f0 !important; }
                    h2 { color: #6d28d9 !important; }
                    h3 { color: #7c3aed !important; }
                    strong { color: #0f172a !important; }
                    blockquote { background: #f8faff !important; border-left-color: #8b5cf6 !important; color: #1e293b !important; }
                    code { background: #f1f5f9 !important; color: #6d28d9 !important; }
                    pre { background: #f8fafc !important; border-color: #e2e8f0 !important; }
                    pre code { color: #1e293b !important; }
                    table, th, td { border-color: #cbd5e1 !important; }
                    th { background: #f1f5f9 !important; color: #4338ca !important; }
                    .mjx-chtml { color: #1e293b !important; }
                }
              """;

        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>{{safeTitle}}</title>
            <script>
                window.MathJax = {
                    tex: {
                        inlineMath: [['$', '$'], ['\\(', '\\)']],
                        displayMath: [['$$', '$$'], ['\\[', '\\]']],
                        processEscapes: true
                    },
                    options: {
                        skipHtmlTags: ['script', 'noscript', 'style', 'textarea', 'pre', 'code']
                    }
                };
            </script>
            <script id="MathJax-script" async src="https://cdn.jsdelivr.net/npm/mathjax@3/es5/tex-chtml.js"></script>
            <style>
                @page { size: A4; margin: 18mm 16mm; }
                * { margin: 0; padding: 0; box-sizing: border-box; }
                p { margin: 0.7rem 0; }
                ul, ol { margin: 0.6rem 0; padding-left: 1.5rem; }
                li { margin: 0.25rem 0; }
                table { width: 100%; border-collapse: collapse; margin: 1rem 0; page-break-inside: avoid; break-inside: avoid; }
                {{screenStyles}}
            </style>
        </head>
        <body>
        {{bodyContent}}
        </body>
        </html>
        """;
    }

    // ── AST-based QuestPDF fallback rendering ─────────────────────────

    private static void RenderBlocks(ColumnDescriptor col, ContainerBlock container, string imageBasePath)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var (size, color, topPad) = heading.Level switch
                    {
                        1 => (17f, Colors.Purple.Darken2, 12f),
                        2 => (14f, Colors.Purple.Medium, 10f),
                        3 => (12f, Colors.Purple.Lighten1, 7f),
                        _ => (11f, Colors.Grey.Darken2, 5f)
                    };
                    col.Item().PaddingTop(topPad)
                        .DefaultTextStyle(x => x.FontSize(size).Bold().FontColor(color))
                        .Text(t => RenderInlines(t, heading.Inline));
                    break;

                case FencedCodeBlock or CodeBlock:
                    var codeBlock = (CodeBlock)block;
                    var codeText = codeBlock.Lines.ToString().TrimEnd();
                    col.Item().PaddingVertical(4).Background(Colors.Grey.Lighten4)
                        .Border(1).BorderColor(Colors.Grey.Lighten2)
                        .Padding(8)
                        .Text(codeText).FontFamily("Consolas").FontSize(9.5f).FontColor(Colors.Grey.Darken3);
                    break;

                case QuoteBlock quote:
                    col.Item().PaddingLeft(10).PaddingVertical(3)
                        .BorderLeft(3).BorderColor(Colors.Purple.Medium)
                        .Background(Colors.Grey.Lighten5)
                        .Padding(8)
                        .Column(qCol => RenderBlocks(qCol, quote, imageBasePath));
                    break;

                case ListBlock list:
                    RenderListBlock(col, list, imageBasePath, depth: 0);
                    break;

                case Table table:
                    RenderTableBlock(col, table);
                    break;

                case ThematicBreakBlock:
                    col.Item().PaddingVertical(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                    break;

                case ParagraphBlock paragraph:
                    RenderParagraphBlock(col, paragraph, imageBasePath);
                    break;
            }
        }
    }

    private static void RenderParagraphBlock(ColumnDescriptor col, ParagraphBlock paragraph, string imageBasePath)
    {
        if (paragraph.Inline == null) return;

        // Check for standalone image paragraph
        if (paragraph.Inline.FirstChild is LinkInline { IsImage: true } img && img.NextSibling == null)
        {
            TryRenderImage(col, img.Url, imageBasePath);
            return;
        }

        // Check for standalone $$...$$ display math block
        var rawText = ExtractPlainInlineText(paragraph.Inline).Trim();
        if (rawText.StartsWith("$$", StringComparison.Ordinal) &&
            rawText.EndsWith("$$", StringComparison.Ordinal) &&
            rawText.Length > 4)
        {
            var formatted = MathFormatter.FormatFormula(rawText[2..^2], useMarkdownEmphasis: false);
            col.Item().PaddingVertical(4).Background(Colors.Grey.Lighten4)
                .BorderLeft(3).BorderColor(Colors.Purple.Medium)
                .Padding(8)
                .Text(t =>
                {
                    t.Span("Formula: ").Bold().FontColor(Colors.Purple.Darken2);
                    t.Span(formatted).FontColor(Colors.Grey.Darken3);
                });
            return;
        }

        col.Item().PaddingVertical(2).Text(t => RenderInlines(t, paragraph.Inline, imageBasePath, col));
    }

    private static void RenderListBlock(ColumnDescriptor col, ListBlock list, string imageBasePath, int depth)
    {
        int order = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        string bulletChar = depth == 0 ? "•" : "◦";

        foreach (var child in list)
        {
            if (child is not ListItemBlock item) continue;

            string prefix = list.IsOrdered ? $"{order++}." : bulletChar;
            float leftPad = 14f * (depth + 1);

            foreach (var itemBlock in item)
            {
                if (itemBlock is ParagraphBlock p)
                {
                    col.Item().PaddingLeft(leftPad).Row(row =>
                    {
                        row.ConstantItem(18).Text(prefix).Bold();
                        row.RelativeItem().Text(t => RenderInlines(t, p.Inline, imageBasePath, col));
                    });
                    prefix = " "; // Only prefix the first paragraph in a multi-paragraph list item
                }
                else if (itemBlock is ListBlock nested)
                {
                    RenderListBlock(col, nested, imageBasePath, depth + 1);
                }
                else
                {
                    col.Item().PaddingLeft(leftPad).Column(sub => RenderBlocks(sub, new ContainerItemAdapter(itemBlock), imageBasePath));
                }
            }
        }
    }

    private sealed class ContainerItemAdapter : ContainerBlock
    {
        public ContainerItemAdapter(Block single) : base(null)
        {
            Add(single);
        }
    }

    private static void RenderTableBlock(ColumnDescriptor col, Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        if (rows.Count == 0) return;

        int colCount = rows.Max(r => r.Count);
        if (colCount <= 0) return;

        col.Item().PaddingVertical(6).Table(t =>
        {
            t.ColumnsDefinition(cols =>
            {
                for (int c = 0; c < colCount; c++)
                    cols.RelativeColumn();
            });

            foreach (var row in rows)
            {
                var cells = row.OfType<TableCell>().ToList();
                for (int c = 0; c < colCount; c++)
                {
                    PdfContainer cellContainer = t.Cell()
                        .Border(0.75f).BorderColor(Colors.Grey.Lighten1)
                        .Background(row.IsHeader ? Colors.Purple.Lighten5 : Colors.White)
                        .Padding(6);

                    if (c < cells.Count)
                    {
                        var cell = cells[c];
                        var cellText = string.Join(" ", cell.OfType<ParagraphBlock>()
                            .Select(p => ExtractPlainInlineText(p.Inline)));
                        var formatted = MathFormatter.ReplaceInlineAndDisplayMath(cellText, useMarkdownEmphasis: false);

                        if (row.IsHeader)
                            cellContainer.Text(formatted).Bold().FontSize(9.5f).FontColor(Colors.Purple.Darken2);
                        else
                            cellContainer.Text(formatted).FontSize(9.5f);
                    }
                    else
                    {
                        cellContainer.Text(string.Empty);
                    }
                }
            }
        });
    }

    private static void TryRenderImage(ColumnDescriptor col, string? relativeUrl, string imageBasePath)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl)) return;

        var cleanRel = relativeUrl.Trim().Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(imageBasePath, cleanRel));
        if (File.Exists(fullPath))
            col.Item().PaddingVertical(5).Image(fullPath).FitWidth();
    }

    private static void RenderInlines(
        TextDescriptor text,
        ContainerInline? container,
        string? imageBasePath = null,
        ColumnDescriptor? parentCol = null,
        bool bold = false,
        bool italic = false)
    {
        if (container == null) return;

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    var content = MathFormatter.ReplaceInlineAndDisplayMath(lit.Content.ToString(), useMarkdownEmphasis: false);
                    var span = text.Span(content);
                    if (bold) span.Bold();
                    if (italic) span.Italic();
                    break;

                case CodeInline code:
                    text.Span(code.Content)
                        .FontFamily("Consolas")
                        .FontSize(9.5f)
                        .BackgroundColor(Colors.Grey.Lighten4);
                    break;

                case EmphasisInline emp:
                    bool isBold = bold || emp.DelimiterCount >= 2;
                    bool isItalic = italic || emp.DelimiterCount == 1;
                    RenderInlines(text, emp, imageBasePath, parentCol, isBold, isItalic);
                    break;

                case LinkInline link:
                    if (link.IsImage && parentCol != null && imageBasePath != null)
                        TryRenderImage(parentCol, link.Url, imageBasePath);
                    else
                        RenderInlines(text, link, imageBasePath, parentCol, bold, italic);
                    break;

                case LineBreakInline:
                    text.Span("\n");
                    break;

                case ContainerInline sub:
                    RenderInlines(text, sub, imageBasePath, parentCol, bold, italic);
                    break;
            }
        }
    }

    private static string ExtractPlainInlineText(ContainerInline? container)
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
                    sb.Append(ExtractPlainInlineText(sub));
                    break;
            }
        }
        return sb.ToString();
    }

    private static async Task<string> EmbedImagesAsBase64Async(string html, string basePath, CancellationToken ct)
    {
        var matches = ImageSrcAttrRegex().Matches(html);
        if (matches.Count == 0) return html;

        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in matches)
        {
            ct.ThrowIfCancellationRequested();
            var originalSrc = match.Groups[2].Value;
            if (cache.ContainsKey(originalSrc)) continue;

            var fullPath = Path.Combine(basePath, originalSrc);
            if (File.Exists(fullPath))
            {
                var bytes = await File.ReadAllBytesAsync(fullPath, ct);
                var base64 = Convert.ToBase64String(bytes);
                var ext = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
                var mimeType = ext switch
                {
                    "jpg" or "jpeg" => "image/jpeg",
                    "png" => "image/png",
                    "gif" => "image/gif",
                    "svg" => "image/svg+xml",
                    _ => "image/png"
                };
                cache[originalSrc] = $"data:{mimeType};base64,{base64}";
            }
        }

        if (cache.Count == 0) return html;

        return ImageSrcAttrRegex().Replace(html, m =>
        {
            var src = m.Groups[2].Value;
            return cache.TryGetValue(src, out var dataUri)
                ? $"{m.Groups[1].Value}{dataUri}{m.Groups[3].Value}"
                : m.Value;
        });
    }

    [GeneratedRegex(@"!\[\[([^\]]+)\]\]")]
    private static partial Regex ObsidianImageRegex();

    [GeneratedRegex(@"(<img\s+[^>]*?src="")([^""]+)("")", RegexOptions.IgnoreCase)]
    private static partial Regex ImageSrcAttrRegex();
}
