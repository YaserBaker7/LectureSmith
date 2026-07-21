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

    // Inline markdown patterns for PDF rendering
    private static readonly Regex InlineBoldRegex =
        new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
    private static readonly Regex InlineItalicRegex =
        new(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex InlineCodeRegex =
        new(@"`([^`]+)`", RegexOptions.Compiled);

    /// <summary>
    /// Exports generated notes to the specified format.
    /// </summary>
    public async Task<string> ExportAsync(GenerationResult result, GenerationSettings settings)
    {
        Directory.CreateDirectory(settings.OutputPath);

        // Copy slide images to output
        var slidesOutputDir = Path.Combine(settings.OutputPath, "slides");
        Directory.CreateDirectory(slidesOutputDir);
        foreach (var imgPath in result.SlideImagePaths)
        {
            var destPath = Path.Combine(slidesOutputDir, Path.GetFileName(imgPath));
            File.Copy(imgPath, destPath, overwrite: true);
        }

        var baseName = $"{settings.EffectiveCourseName} - Notes";

        return settings.Format switch
        {
            OutputFormat.Obsidian => await ExportObsidianAsync(result, settings, baseName),
            OutputFormat.HTML => await ExportHtmlAsync(result, settings, baseName),
            OutputFormat.PDF => await ExportPdfAsync(result, settings, baseName),
            _ => throw new ArgumentOutOfRangeException(nameof(settings.Format))
        };
    }

    private static async Task<string> ExportObsidianAsync(GenerationResult result, GenerationSettings settings, string baseName)
    {
        var outputFile = Path.Combine(settings.OutputPath, $"{baseName}.md");
        await File.WriteAllTextAsync(outputFile, result.MarkdownContent, Encoding.UTF8);
        return outputFile;
    }

    private async Task<string> ExportHtmlAsync(GenerationResult result, GenerationSettings settings, string baseName)
    {
        var outputFile = Path.Combine(settings.OutputPath, $"{baseName}.html");

        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
        var htmlBody = Markdig.Markdown.ToHtml(result.MarkdownContent, pipeline);

        // Convert image paths to base64 for self-contained HTML
        htmlBody = await EmbedImagesAsBase64Async(htmlBody, settings.OutputPath);

        var fullHtml = $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>{{baseName}}</title>
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

    private static async Task<string> ExportPdfAsync(GenerationResult result, GenerationSettings settings, string baseName)
    {
        var outputFile = Path.Combine(settings.OutputPath, $"{baseName}.pdf");

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
                        RenderMarkdownLines(col, result.MarkdownContent, settings.OutputPath);
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

    /// <summary>
    /// Parses inline markdown (bold, italic, code) and renders with proper QuestPDF styling.
    /// This prevents raw **asterisks** from appearing in the PDF output.
    /// </summary>
    private static void RenderInlineMarkdown(TextDescriptor text, string content)
    {
        // Tokenize: find all bold, italic, and code spans
        // Process order: bold first (**), then code (`), then italic (*)
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
                default:
                    text.Span(segment.Text);
                    break;
            }
        }
    }

    private enum InlineStyle { Normal, Bold, Italic, Code }

    /// <summary>
    /// Parses a string for **bold**, *italic*, and `code` segments.
    /// </summary>
    private static void ParseInlineSegments(string input, List<(string Text, InlineStyle Style)> segments)
    {
        // Combined regex to match bold, code, or italic in order of precedence
        var combinedRegex = new Regex(@"\*\*(.+?)\*\*|`([^`]+)`|(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", RegexOptions.Compiled);

        var lastIndex = 0;
        foreach (Match match in combinedRegex.Matches(input))
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
                // *italic*
                segments.Add((match.Groups[3].Value, InlineStyle.Italic));
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
    /// </summary>
    private static async Task<string> EmbedImagesAsBase64Async(string html, string basePath)
    {
        var matches = ImageSrcRegex.Matches(html);

        foreach (Match match in matches)
        {
            var originalSrc = match.Groups[1].Value;
            var fullPath = Path.Combine(basePath, originalSrc);

            if (File.Exists(fullPath))
            {
                var bytes = await File.ReadAllBytesAsync(fullPath);
                var base64 = Convert.ToBase64String(bytes);
                var ext = Path.GetExtension(fullPath).TrimStart('.').ToLower();
                var mimeType = ext switch
                {
                    "jpg" or "jpeg" => "image/jpeg",
                    "png" => "image/png",
                    "gif" => "image/gif",
                    "svg" => "image/svg+xml",
                    _ => "image/png"
                };
                var dataUri = $"data:{mimeType};base64,{base64}";
                html = html.Replace(originalSrc, dataUri);
            }
        }

        return html;
    }
}
