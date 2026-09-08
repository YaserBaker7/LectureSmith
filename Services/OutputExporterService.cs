using LectureSmith.Models;
using Markdig;
using System.Text;
using System.Text.RegularExpressions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace LectureSmith.Services;

public class OutputExporterService
{
    private static readonly Regex ImageSrcRegex =
        new(@"<img\s+[^>]*src=""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SlideImageRegex =
        new(@"(?:\!\[\[|\!\[.*?\]\()([^\])\s]+)", RegexOptions.Compiled);

    // Inline markdown regex for QuestPDF segment parsing (bold, code, math, italic)
    private static readonly Regex CombinedInlineRegex =
        new(@"\*\*(.+?)\*\*|`([^`]+)`|(?<!\$)\$(?!\$)(.+?)(?<!\$)\$(?!\$)|(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", RegexOptions.Compiled);

    private static string GetUniqueFilePath(string directory, string baseName, string extension)
    {
        var candidate = Path.Combine(directory, $"{baseName}{extension}");
        if (!File.Exists(candidate)) return candidate;
        
        int counter = 1;
        while (File.Exists(Path.Combine(directory, $"{baseName} ({counter}){extension}")))
        {
            counter++;
        }
        return Path.Combine(directory, $"{baseName} ({counter}){extension}");
    }

    /// <summary>
    /// Exports generated notes to the specified format.
    /// </summary>
    public async Task<string> ExportAsync(GenerationResult result, GenerationSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(settings.OutputPath);
        var baseName = $"{settings.EffectiveCourseName} - Notes";

        // For Obsidian: markdown requires the external slides folder to reside on disk alongside the .md file.
        if (settings.Format == OutputFormat.Obsidian)
        {
            var slidesOutputDir = Path.Combine(settings.OutputPath, "slides");
            Directory.CreateDirectory(slidesOutputDir);
            foreach (var imgPath in result.SlideImagePaths)
            {
                if (File.Exists(imgPath))
                {
                    var destPath = Path.Combine(slidesOutputDir, Path.GetFileName(imgPath));
                    File.Copy(imgPath, destPath, overwrite: true);
                }
            }

            ct.ThrowIfCancellationRequested();
            return await ExportObsidianAsync(result, settings, baseName);
        }

        // For HTML (base64 embedded) and PDF (binary embedded), images are self-contained.
        // Stage images inside a temporary staging folder in %TEMP% so they NEVER appear or get deleted in settings.OutputPath.
        var stagingDir = Path.Combine(Path.GetTempPath(), "LectureSmith", $"export_staging_{Guid.NewGuid():N}");
        var stagingSlidesDir = Path.Combine(stagingDir, "slides");

        try
        {
            Directory.CreateDirectory(stagingSlidesDir);
            foreach (var imgPath in result.SlideImagePaths)
            {
                if (File.Exists(imgPath))
                {
                    var destPath = Path.Combine(stagingSlidesDir, Path.GetFileName(imgPath));
                    File.Copy(imgPath, destPath, overwrite: true);
                }
            }

            ct.ThrowIfCancellationRequested();
            return settings.Format switch
            {
                OutputFormat.HTML => await ExportHtmlAsync(result, settings, baseName, stagingDir),
                OutputFormat.PDF => await ExportPdfAsync(result, settings, baseName, stagingDir),
                _ => throw new ArgumentOutOfRangeException(nameof(settings.Format))
            };
        }
        finally
        {
            if (Directory.Exists(stagingDir))
            {
                try
                {
                    Directory.Delete(stagingDir, recursive: true);
                }
                catch
                {
                    // Ignore transient lock delays
                }
            }
        }
    }

    private static async Task<string> ExportObsidianAsync(GenerationResult result, GenerationSettings settings, string baseName)
    {
        var outputFile = GetUniqueFilePath(settings.OutputPath, baseName, ".md");
        await File.WriteAllTextAsync(outputFile, result.MarkdownContent, Encoding.UTF8);
        return outputFile;
    }

    private async Task<string> ExportHtmlAsync(GenerationResult result, GenerationSettings settings, string baseName, string imageBasePath)
    {
        var outputFile = GetUniqueFilePath(settings.OutputPath, baseName, ".html");

        var pipeline = CreateMarkdownPipeline();
        var htmlBody = Markdig.Markdown.ToHtml(result.MarkdownContent, pipeline);

        // Convert image paths to base64 for self-contained HTML using staging directory
        htmlBody = await EmbedImagesAsBase64Async(htmlBody, imageBasePath);

        var fullHtml = $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>{{baseName}}</title>
            <!-- MathJax configuration and library for rendering LaTeX math formulas -->
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
                :root { --bg: #1a1a2e; --surface: #16213e; --card: #1e2d4a; --text: #e0e0e0; --accent: #7c3aed; --accent2: #a78bfa; --border: #2d3a5a; }
                * { margin: 0; padding: 0; box-sizing: border-box; }
                body { font-family: 'Segoe UI', Inter, -apple-system, sans-serif; background: var(--bg); color: var(--text); line-height: 1.7; padding: 2rem; max-width: 900px; margin: 0 auto; }
                h1 { color: var(--accent2); font-size: 2rem; margin: 2rem 0 1rem; border-bottom: 2px solid var(--accent); padding-bottom: 0.5rem; }
                h2 { color: var(--accent2); font-size: 1.5rem; margin: 2rem 0 0.8rem; }
                h3 { color: #c4b5fd; font-size: 1.2rem; margin: 1.5rem 0 0.5rem; }
                p { margin: 0.8rem 0; }
                img { max-width: 100%; border-radius: 8px; border: 1px solid var(--border); margin: 1rem 0; box-shadow: 0 4px 12px rgba(0,0,0,0.3); }
                blockquote { border-left: 4px solid var(--accent); padding: 0.8rem 1.2rem; margin: 1rem 0; background: var(--card); border-radius: 0 8px 8px 0; }
                code { background: var(--card); padding: 2px 6px; border-radius: 4px; font-size: 0.9em; }
                pre { background: var(--surface); padding: 1rem; border-radius: 8px; overflow-x: auto; border: 1px solid var(--border); margin: 1rem 0; }
                pre code { background: none; padding: 0; }
                table { width: 100%; border-collapse: collapse; margin: 1rem 0; }
                th, td { border: 1px solid var(--border); padding: 0.6rem 1rem; text-align: left; }
                th { background: var(--card); color: var(--accent2); }
                tr:nth-child(even) { background: rgba(255,255,255,0.02); }
                ul, ol { margin: 0.5rem 0; padding-left: 1.5rem; }
                li { margin: 0.3rem 0; }
                hr { border: none; border-top: 1px solid var(--border); margin: 2rem 0; }
                strong { color: #c4b5fd; }
                .mjx-chtml { color: #c4b5fd !important; }

                @media print {
                    body {
                        background: #ffffff !important;
                        color: #1e293b !important;
                        padding: 0 !important;
                        max-width: 100% !important;
                    }
                    h1 { color: #5b21b6 !important; border-bottom-color: #e2e8f0 !important; }
                    h2 { color: #6d28d9 !important; border-bottom-color: #f1f5f9 !important; }
                    h3 { color: #7c3aed !important; }
                    strong { color: #0f172a !important; }
                    blockquote { background: #f8faff !important; border-left-color: #8b5cf6 !important; color: #1e293b !important; }
                    code { background: #f1f5f9 !important; color: #6d28d9 !important; }
                    pre { background: #f8fafc !important; border-color: #e2e8f0 !important; }
                    pre code { color: #1e293b !important; }
                    table, th, td { border-color: #cbd5e1 !important; }
                    th { background: #f1f5f9 !important; color: #4338ca !important; }
                    tr:nth-child(even) { background: #f8fafc !important; }
                    img { box-shadow: 0 2px 8px rgba(0,0,0,0.06) !important; border-color: #cbd5e1 !important; }
                    .mjx-chtml { color: #1e293b !important; }
                }
            </style>
        </head>
        <body>
        {{htmlBody}}
        </body>
        </html>
        """;

        await File.WriteAllTextAsync(outputFile, fullHtml, Encoding.UTF8);
        return outputFile;
    }

    private async Task<string> ExportPdfAsync(GenerationResult result, GenerationSettings settings, string baseName, string imageBasePath)
    {
        var outputFile = GetUniqueFilePath(settings.OutputPath, baseName, ".pdf");

        // Try high-fidelity HTML-to-PDF export via headless browser (Edge / Chrome)
        var browserPath = FindHeadlessBrowserPath();
        if (!string.IsNullOrEmpty(browserPath))
        {
            var tempHtml = Path.Combine(Path.GetTempPath(), $"LectureSmith_PDF_{Guid.NewGuid():N}.html");
            var tempUserData = Path.Combine(Path.GetTempPath(), $"LectureSmith_Edge_{Guid.NewGuid():N}");
            try
            {
                var pipeline = CreateMarkdownPipeline();
                var htmlBody = Markdig.Markdown.ToHtml(result.MarkdownContent, pipeline);
                htmlBody = await EmbedImagesAsBase64Async(htmlBody, imageBasePath);

                var printableHtml = BuildPrintableHtml(baseName, htmlBody);
                await File.WriteAllTextAsync(tempHtml, printableHtml, Encoding.UTF8);

                Directory.CreateDirectory(tempUserData);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = browserPath,
                    Arguments = $"--headless=new --disable-gpu --no-first-run --no-default-browser-check --no-pdf-header-footer --run-all-compositor-stages-before-draw --virtual-time-budget=5000 --user-data-dir=\"{tempUserData}\" --print-to-pdf=\"{outputFile}\" \"{tempHtml}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                        await proc.WaitForExitAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                    }

                    if (File.Exists(outputFile) && new FileInfo(outputFile).Length > 0)
                    {
                        return outputFile;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[OutputExporterService] Headless PDF generation failed: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(tempHtml)) File.Delete(tempHtml); } catch { }
                try { if (Directory.Exists(tempUserData)) Directory.Delete(tempUserData, recursive: true); } catch { }
            }
        }

        // Fallback: QuestPDF generation
        await Task.Run(() =>
        {
            QuestPDF.Settings.License = LicenseType.Community;

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(50);
                    page.DefaultTextStyle(x => x.FontSize(11).FontFamily("Segoe UI"));

                    page.Header().Text(baseName)
                        .FontSize(18).Bold().FontColor(Colors.Purple.Darken2);

                    page.Content().PaddingVertical(10).Column(col =>
                    {
                        RenderMarkdownLines(col, result.MarkdownContent, imageBasePath);
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
        });

        return outputFile;
    }

    private static MarkdownPipeline CreateMarkdownPipeline()
    {
        return new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseGridTables()
            .UseAutoLinks()
            .UseTaskLists()
            .Build();
    }

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

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        return null;
    }

    private static string BuildPrintableHtml(string title, string bodyContent)
    {
        return $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>{{title}}</title>
            <!-- MathJax 3 Configuration for LaTeX Math Typesetting -->
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
            <script id="MathJax-script" src="https://cdn.jsdelivr.net/npm/mathjax@3/es5/tex-chtml.js"></script>
            <style>
                @page {
                    size: A4;
                    margin: 18mm 16mm;
                }
                * {
                    box-sizing: border-box;
                    margin: 0;
                    padding: 0;
                }
                body {
                    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Inter", Helvetica, Arial, sans-serif;
                    color: #1e293b;
                    background: #ffffff;
                    line-height: 1.65;
                    font-size: 14px;
                    padding: 0;
                    max-width: 100%;
                }
                h1 {
                    color: #5b21b6;
                    font-size: 22px;
                    font-weight: 700;
                    margin: 22px 0 10px;
                    border-bottom: 2px solid #e2e8f0;
                    padding-bottom: 6px;
                    page-break-after: avoid;
                    break-after: avoid;
                }
                h2 {
                    color: #6d28d9;
                    font-size: 18px;
                    font-weight: 700;
                    margin: 20px 0 8px;
                    border-bottom: 1px solid #f1f5f9;
                    padding-bottom: 4px;
                    page-break-after: avoid;
                    break-after: avoid;
                }
                h3 {
                    color: #7c3aed;
                    font-size: 15px;
                    font-weight: 600;
                    margin: 16px 0 6px;
                    page-break-after: avoid;
                    break-after: avoid;
                }
                h4, h5, h6 {
                    color: #475569;
                    font-size: 13.5px;
                    font-weight: 600;
                    margin: 14px 0 4px;
                    page-break-after: avoid;
                    break-after: avoid;
                }
                p {
                    margin: 8px 0;
                }
                ul, ol {
                    margin: 8px 0;
                    padding-left: 22px;
                }
                li {
                    margin: 3px 0;
                }
                strong {
                    color: #0f172a;
                }
                blockquote {
                    border-left: 4px solid #8b5cf6;
                    background: #f8faff;
                    padding: 10px 16px;
                    margin: 12px 0;
                    border-radius: 0 6px 6px 0;
                    page-break-inside: avoid;
                    break-inside: avoid;
                }
                code {
                    background: #f1f5f9;
                    color: #6d28d9;
                    padding: 2px 5px;
                    border-radius: 4px;
                    font-size: 0.9em;
                    font-family: 'Cascadia Code', 'Fira Code', Consolas, monospace;
                }
                pre {
                    background: #f8fafc;
                    border: 1px solid #e2e8f0;
                    border-radius: 6px;
                    padding: 12px;
                    margin: 12px 0;
                    overflow-x: auto;
                    page-break-inside: avoid;
                    break-inside: avoid;
                }
                pre code {
                    background: none;
                    color: #1e293b;
                    padding: 0;
                }
                table {
                    width: 100%;
                    border-collapse: collapse;
                    margin: 14px 0;
                    page-break-inside: avoid;
                    break-inside: avoid;
                }
                th, td {
                    border: 1px solid #cbd5e1;
                    padding: 8px 12px;
                    text-align: left;
                    font-size: 13px;
                }
                th {
                    background: #f1f5f9;
                    color: #4338ca;
                    font-weight: 600;
                }
                tr:nth-child(even) {
                    background: #f8fafc;
                }
                img {
                    display: block;
                    max-width: 95%;
                    margin: 16px auto;
                    border-radius: 6px;
                    border: 1px solid #cbd5e1;
                    box-shadow: 0 2px 8px rgba(0, 0, 0, 0.06);
                    page-break-inside: avoid;
                    break-inside: avoid;
                }
                hr {
                    border: none;
                    border-top: 1px solid #e2e8f0;
                    margin: 20px 0;
                }
                mjx-container {
                    color: #1e293b !important;
                }
            </style>
        </head>
        <body>
        {{bodyContent}}
        </body>
        </html>
        """;
    }

    /// <summary>
    /// Renders markdown lines into QuestPDF column components.
    /// Supports headers, images, blockquotes, lists, horizontal rules, tables,
    /// and inline bold/italic/code formatting.
    /// </summary>
    private static void RenderMarkdownLines(ColumnDescriptor col, string markdownContent, string outputPath)
    {
        var lines = markdownContent.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r');

            if (trimmed.StartsWith("# "))
            {
                col.Item().PaddingTop(15)
                    .DefaultTextStyle(x => x.FontSize(18).Bold().FontColor(Colors.Purple.Darken2))
                    .Text(text => RenderInlineMarkdown(text, trimmed[2..]));
            }
            else if (trimmed.StartsWith("## "))
            {
                col.Item().PaddingTop(12)
                    .DefaultTextStyle(x => x.FontSize(15).Bold().FontColor(Colors.Purple.Medium))
                    .Text(text => RenderInlineMarkdown(text, trimmed[3..]));
            }
            else if (trimmed.StartsWith("### "))
            {
                col.Item().PaddingTop(8)
                    .DefaultTextStyle(x => x.FontSize(13).Bold().FontColor(Colors.Purple.Lighten2))
                    .Text(text => RenderInlineMarkdown(text, trimmed[4..]));
            }
            else if (trimmed.StartsWith("#### "))
            {
                col.Item().PaddingTop(6)
                    .DefaultTextStyle(x => x.FontSize(11.5f).Bold().FontColor(Colors.Purple.Lighten1))
                    .Text(text => RenderInlineMarkdown(text, trimmed[5..]));
            }
            else if (trimmed.StartsWith("##### "))
            {
                col.Item().PaddingTop(4)
                    .DefaultTextStyle(x => x.FontSize(11).Bold().FontColor(Colors.Grey.Darken2))
                    .Text(text => RenderInlineMarkdown(text, trimmed[6..]));
            }
            else if (trimmed.StartsWith("![") || trimmed.StartsWith("![["))
            {
                var imgMatch = SlideImageRegex.Match(trimmed);
                if (imgMatch.Success)
                {
                    var imgFullPath = Path.Combine(outputPath, imgMatch.Groups[1].Value);
                    if (File.Exists(imgFullPath))
                    {
                        col.Item().PaddingVertical(5).Image(imgFullPath).FitWidth();
                    }
                }
            }
            else if (trimmed.Trim().StartsWith("$$") && trimmed.Trim().EndsWith("$$") && trimmed.Trim().Length > 2)
            {
                // Display math formula block (handles both root and indented lines)
                var mathContent = trimmed.Trim().Trim('$', ' ');
                var formatted = MathFormatter.FormatFormula(mathContent, useMarkdownEmphasis: false);
                col.Item().PaddingVertical(4).Background(Colors.Grey.Lighten4)
                    .BorderLeft(3).BorderColor(Colors.Purple.Medium)
                    .Padding(8)
                    .Text(text =>
                    {
                        text.Span("📐 Formula: ").Bold().FontColor(Colors.Purple.Darken2);
                        text.Span(formatted).FontColor(Colors.Grey.Darken3);
                    });
            }
            else if (trimmed.StartsWith('|'))
            {
                // Table rows — render as monospace
                col.Item().DefaultTextStyle(x => x.FontSize(10).FontFamily("Consolas"))
                    .Text(text => RenderInlineMarkdown(text, trimmed));
            }
            else if (trimmed.StartsWith("> "))
            {
                col.Item().PaddingLeft(15).PaddingVertical(2)
                    .BorderLeft(3).BorderColor(Colors.Purple.Medium)
                    .PaddingLeft(8)
                    .DefaultTextStyle(x => x.FontSize(11).Italic())
                    .Text(text => RenderInlineMarkdown(text, trimmed[2..]));
            }
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("• "))
            {
                var bulletContent = trimmed.StartsWith("• ") ? trimmed[2..] : trimmed[2..];
                col.Item().PaddingLeft(15).Text(text =>
                {
                    text.Span("• ").Bold();
                    RenderInlineMarkdown(text, bulletContent);
                });
            }
            else if (trimmed.StartsWith("  - ") || trimmed.StartsWith("  * ") || trimmed.StartsWith("   - ") || trimmed.StartsWith("   * "))
            {
                // Indented sub-bullets
                var content = trimmed.TrimStart()[2..];
                col.Item().PaddingLeft(30).Text(text =>
                {
                    text.Span("◦ ");
                    RenderInlineMarkdown(text, content);
                });
            }
            else if (trimmed.StartsWith("---"))
            {
                col.Item().PaddingVertical(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
            }
            else if (!string.IsNullOrWhiteSpace(trimmed))
            {
                col.Item().PaddingVertical(2).Text(text =>
                    RenderInlineMarkdown(text, trimmed));
            }
        }
    }

    private static readonly Regex PreDisplayMathRegex =
        new(@"(?s)\$\$(.+?)\$\$", RegexOptions.Compiled);
    private static readonly Regex PreInlineMathRegex =
        new(@"(?<!\$)\$(?!\$)(.+?)(?<!\$)\$(?!\$)", RegexOptions.Compiled);

    /// <summary>
    /// Parses inline markdown (bold, italic, code, math) and renders with proper QuestPDF styling.
    /// This prevents raw **asterisks** or LaTeX syntax from appearing in the PDF output.
    /// </summary>
    private static void RenderInlineMarkdown(TextDescriptor text, string content)
    {
        // First convert any display ($$...$$) or inline ($...$) math into formatted typography so math
        // inside bold (e.g. **Population ($N$):**) or bullets is always cleanly rendered
        content = PreDisplayMathRegex.Replace(content, m => MathFormatter.FormatFormula(m.Groups[1].Value, useMarkdownEmphasis: false));
        content = PreInlineMathRegex.Replace(content, m => MathFormatter.FormatFormula(m.Groups[1].Value, useMarkdownEmphasis: false));

        var segments = new List<(string Text, InlineStyle Style)>();
        ParseInlineSegments(content, segments);

        foreach (var segment in segments)
        {
            switch (segment.Style)
            {
                case InlineStyle.Bold:
                    text.Span(segment.Text).Bold();
                    break;
                case InlineStyle.Italic:
                    text.Span(segment.Text).Italic();
                    break;
                case InlineStyle.Code:
                    text.Span(segment.Text).FontFamily("Consolas").FontSize(10)
                        .BackgroundColor(Colors.Grey.Lighten4);
                    break;
                case InlineStyle.Math:
                    text.Span(segment.Text).Italic().FontColor(Colors.Purple.Darken2);
                    break;
                default:
                    text.Span(segment.Text);
                    break;
            }
        }
    }

    private enum InlineStyle { Normal, Bold, Italic, Code, Math }

    /// <summary>
    /// Parses a string for **bold**, *italic*, `code`, and $math$ segments.
    /// </summary>
    private static void ParseInlineSegments(string input, List<(string Text, InlineStyle Style)> segments)
    {
        var lastIndex = 0;
        foreach (Match match in CombinedInlineRegex.Matches(input))
        {
            // Add any text before this match
            if (match.Index > lastIndex)
            {
                segments.Add((input[lastIndex..match.Index], InlineStyle.Normal));
            }

            if (match.Groups[1].Success)
            {
                // **bold**
                segments.Add((match.Groups[1].Value, InlineStyle.Bold));
            }
            else if (match.Groups[2].Success)
            {
                // `code`
                segments.Add((match.Groups[2].Value, InlineStyle.Code));
            }
            else if (match.Groups[3].Success)
            {
                // $math$
                var formattedMath = MathFormatter.FormatFormula(match.Groups[3].Value, useMarkdownEmphasis: false);
                segments.Add((formattedMath, InlineStyle.Math));
            }
            else if (match.Groups[4].Success)
            {
                // *italic*
                segments.Add((match.Groups[4].Value, InlineStyle.Italic));
            }

            lastIndex = match.Index + match.Length;
        }

        // Add remaining text
        if (lastIndex < input.Length)
        {
            segments.Add((input[lastIndex..], InlineStyle.Normal));
        }

        // If nothing was parsed, add the whole string
        if (segments.Count == 0)
        {
            segments.Add((input, InlineStyle.Normal));
        }
    }

    /// <summary>
    /// Converts image src references in HTML to base64 data URIs for self-contained HTML.
    /// Uses single-pass regex replacement with caching to prevent LOH re-allocations.
    /// </summary>
    private static async Task<string> EmbedImagesAsBase64Async(string html, string basePath)
    {
        var matches = ImageSrcRegex.Matches(html);
        if (matches.Count == 0) return html;

        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in matches)
        {
            var originalSrc = match.Groups[1].Value;
            if (cache.ContainsKey(originalSrc)) continue;

            var fullPath = Path.Combine(basePath, originalSrc);
            if (File.Exists(fullPath))
            {
                var bytes = await File.ReadAllBytesAsync(fullPath);
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

        return ImageSrcRegex.Replace(html, m =>
        {
            var src = m.Groups[1].Value;
            return cache.TryGetValue(src, out var dataUri)
                ? m.Value.Replace(src, dataUri)
                : m.Value;
        });
    }
}
