using LectureSmith.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace LectureSmith.Services;

public class NoteGeneratorService
{
    private static readonly Regex PageRangeRegex = new(@"(\d+)\s*[-–]\s*(\d+)", RegexOptions.Compiled);

    private readonly PdfExtractorService _pdfExtractor;
    private readonly GeminiService _gemini;
    private readonly OcrService _ocr;

    public NoteGeneratorService(PdfExtractorService pdfExtractor, GeminiService gemini, OcrService ocr)
    {
        _pdfExtractor = pdfExtractor;
        _gemini = gemini;
        _ocr = ocr;
    }

    /// <summary>
    /// Runs the full note generation pipeline.
    /// </summary>
    public async Task<GenerationResult> GenerateAsync(GenerationSettings settings,
        IProgress<ProgressUpdate>? progress = null, IProgress<string>? liveText = null,
        CancellationToken ct = default)
    {
        var result = new GenerationResult();

        // Step 1: Extract slide images (needed for all modes — OCR, Vision, and export)
        progress?.Report(new ProgressUpdate("Extracting slide images...", 0));
        if (settings.SlidesFile != null)
        {
            var slidesDir = Path.Combine(Path.GetTempPath(), "LectureSmith", Guid.NewGuid().ToString(), "slides");
            result.SlideImagePaths = await _pdfExtractor.ExtractSlideImagesAsync(
                settings.SlidesFile.FilePath, slidesDir,
                new Progress<(int current, int total)>(p =>
                    progress?.Report(new ProgressUpdate($"Extracting slide {p.current} of {p.total}...",
                        (int)(10.0 * p.current / p.total)))));
            ct.ThrowIfCancellationRequested();
        }

        // Step 2: Extract text from slides
        progress?.Report(new ProgressUpdate("Extracting text from slides...", 12));
        var slideTexts = new List<string>();
        if (settings.SlidesFile != null)
        {
            slideTexts = await _pdfExtractor.ExtractTextAsync(settings.SlidesFile.FilePath);
            ct.ThrowIfCancellationRequested();
        }

        // Step 2b: OCR Enhancement — merge OCR text with extracted text
        if (settings.SlideProcessing == SlideProcessingMode.OcrEnhanced && result.SlideImagePaths.Count > 0)
        {
            await RunOcrEnhancementAsync(slideTexts, result.SlideImagePaths, progress, ct);
        }

        // Step 3: Extract text from reference books
        progress?.Report(new ProgressUpdate("Extracting text from reference books...", 20));
        var bookTexts = await ExtractBookTextsAsync(settings.BookFiles, ct);

        // Step 4: Build the AI prompt
        progress?.Report(new ProgressUpdate("Building AI prompt...", 25));
        var systemPrompt = BuildSystemPrompt(settings);
        var userContent = BuildUserContent(slideTexts, bookTexts, settings);

        // Step 4b: Report estimated token count
        var totalChars = slideTexts.Sum(t => t.Length) + bookTexts.Values.SelectMany(v => v).Sum(t => t.Length) + systemPrompt.Length + userContent.Length;
        var estimatedInputTokens = totalChars / 4;  // ~4 chars per token
        progress?.Report(new ProgressUpdate($"Estimated input: ~{estimatedInputTokens:N0} tokens ({slideTexts.Count} slides)", 28));

        // Step 5: Call Gemini API
        result.MarkdownContent = await CallGeminiAsync(settings, systemPrompt, userContent,
            result.SlideImagePaths, progress, liveText, ct);

        progress?.Report(new ProgressUpdate("Notes generated successfully!", 95));
        return result;
    }

    // ── Pipeline step helpers ─────────────────────────────────────────

    private async Task RunOcrEnhancementAsync(List<string> slideTexts, List<string> slideImagePaths,
        IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        progress?.Report(new ProgressUpdate("Running OCR on slide images...", 15));
        var ocrTexts = await _ocr.ExtractTextFromImagesAsync(
            slideImagePaths,
            new Progress<(int current, int total)>(p =>
                progress?.Report(new ProgressUpdate($"OCR scanning slide {p.current} of {p.total}...",
                    15 + (int)(5.0 * p.current / p.total)))));
        ct.ThrowIfCancellationRequested();

        // Merge OCR text with Docnet text per slide
        for (int i = 0; i < slideTexts.Count && i < ocrTexts.Count; i++)
        {
            var docnetText = slideTexts[i].Trim();
            var ocrText = ocrTexts[i].Trim();

            if (!string.IsNullOrWhiteSpace(ocrText) && ocrText != docnetText)
            {
                slideTexts[i] = $"{docnetText}\n\n[OCR-extracted text:]\n{ocrText}";
            }
        }

        // If OCR found more slides than Docnet (unlikely but safe)
        for (int i = slideTexts.Count; i < ocrTexts.Count; i++)
        {
            slideTexts.Add($"[OCR-extracted text:]\n{ocrTexts[i]}");
        }
    }

    private async Task<Dictionary<string, List<string>>> ExtractBookTextsAsync(
        List<UploadedFile> bookFiles, CancellationToken ct)
    {
        var bookTexts = new Dictionary<string, List<string>>();

        foreach (var book in bookFiles)
        {
            ParsePageRange(book.ChapterInfo, out var startPage, out var endPage);
            var pages = await _pdfExtractor.ExtractTextAsync(book.FilePath, startPage, endPage);
            bookTexts[book.FileName] = pages;
            ct.ThrowIfCancellationRequested();
        }

        return bookTexts;
    }

    private async Task<string> CallGeminiAsync(GenerationSettings settings, string systemPrompt,
        string userContent, List<string> slideImagePaths,
        IProgress<ProgressUpdate>? progress, IProgress<string>? liveText, CancellationToken ct)
    {
        progress?.Report(new ProgressUpdate("Generating notes with AI (this may take a few minutes)...", 30));

        var contentBuilder = new StringBuilder();
        var streamProgress = new Progress<string>(chunk =>
        {
            contentBuilder.Append(chunk);
            var length = contentBuilder.Length;
            progress?.Report(new ProgressUpdate("AI is writing notes...",
                30 + (int)(60.0 * length / Math.Max(1, length + 1000))));
            liveText?.Report(chunk);
        });

        if (settings.SlideProcessing == SlideProcessingMode.VisionAI && slideImagePaths.Count > 0)
        {
            progress?.Report(new ProgressUpdate("Sending slide images to AI (Vision mode — this uses more tokens)...", 30));
            await _gemini.GenerateWithImagesAsync(systemPrompt, userContent, slideImagePaths, streamProgress, ct);
        }
        else
        {
            await _gemini.GenerateAsync(systemPrompt, userContent, streamProgress, ct);
        }

        var result = contentBuilder.ToString();
        // Strip 3 or more consecutive empty lines to prevent markdown editor crashes
        result = Regex.Replace(result, @"(\r?\n){3,}", "\n\n");
        return result;
    }

    // ── Prompt building ───────────────────────────────────────────────

    private static string BuildSystemPrompt(GenerationSettings settings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a university professor generating comprehensive lecture notes for a student.");
        sb.AppendLine();
        sb.AppendLine("CONTEXT:");

        if (!string.IsNullOrWhiteSpace(settings.CourseName))
        {
            sb.AppendLine($"- Course: {settings.CourseName}");
        }

        sb.AppendLine("- Student: University student");
        sb.AppendLine($"- Mode: {settings.Mode.DisplayName()}");
        sb.AppendLine();

        // Language instruction
        sb.AppendLine("LANGUAGE:");
        sb.AppendLine(settings.Language.AiInstruction());
        sb.AppendLine();

        sb.AppendLine(settings.Mode.AiInstruction());
        sb.AppendLine();

        AppendSlideProcessingContext(sb, settings.SlideProcessing);

        sb.AppendLine("YOUR TASK:");
        sb.AppendLine("For EACH slide in the lecture presentation:");
        sb.AppendLine("1. Write a heading with the slide number and title: '## Slide X — Title'");
        sb.AppendLine("2. Include the slide image reference (I will tell you the syntax below)");
        sb.AppendLine("3. Provide the explanation based on the mode (detailed for missed, concise for attended)");
        sb.AppendLine("4. If reference book content was provided, integrate relevant book knowledge into your explanation");
        sb.AppendLine("5. DO NOT skip any slides — cover every single one");
        sb.AppendLine();

        AppendFormatInstructions(sb, settings.Format);

        sb.AppendLine();
        sb.AppendLine("START with a title header (# heading) and a brief overview section.");
        sb.AppendLine("END with a Key Concepts Summary table.");
        sb.AppendLine("IMPORTANT: The summary table MUST be strictly formatted as a valid markdown table with a proper header and divider row like this:");
        sb.AppendLine("| Concept | Description |");
        sb.AppendLine("|---|---|");
        sb.AppendLine("| [Name] | [Details] |");
        sb.AppendLine("Do NOT leave out the divider row (|---|---|) and do NOT put empty lines between table rows.");

        if (!string.IsNullOrWhiteSpace(settings.ExtraNotes))
        {
            sb.AppendLine();
            sb.AppendLine("ADDITIONAL INSTRUCTIONS FROM STUDENT:");
            sb.AppendLine(settings.ExtraNotes);
        }

        return sb.ToString();
    }

    private static void AppendSlideProcessingContext(StringBuilder sb, SlideProcessingMode mode)
    {
        switch (mode)
        {
            case SlideProcessingMode.VisionAI:
                sb.AppendLine("SLIDE IMAGES:");
                sb.AppendLine("- I have attached the actual slide images for you to look at.");
                sb.AppendLine("- Use the images to understand diagrams, illustrations, code screenshots, and any visual content.");
                sb.AppendLine("- The extracted text may miss visual elements — rely on the images for full context.");
                sb.AppendLine();
                break;
            case SlideProcessingMode.OcrEnhanced:
                sb.AppendLine("SLIDE TEXT:");
                sb.AppendLine("- The slide text includes both regular extracted text and OCR-scanned text.");
                sb.AppendLine("- OCR text may contain minor errors — use context to interpret it correctly.");
                sb.AppendLine();
                break;
        }
    }

    private static void AppendFormatInstructions(StringBuilder sb, OutputFormat format)
    {
        switch (format)
        {
            case OutputFormat.Obsidian:
                sb.AppendLine("OUTPUT FORMAT: Obsidian-compatible Markdown");
                sb.AppendLine("- Use Obsidian image syntax: ![[slides/slide_XX.png]]");
                sb.AppendLine("- Use standard markdown headers, lists, tables, blockquotes");
                sb.AppendLine("- Use callouts like '> [!tip]' for tips and '> [!important]' for key points");
                break;
            case OutputFormat.HTML:
                sb.AppendLine("OUTPUT FORMAT: Clean Markdown (will be converted to HTML)");
                sb.AppendLine("- Use standard markdown image syntax: ![Slide X](slides/slide_XX.png)");
                sb.AppendLine("- Use standard markdown formatting");
                break;
            case OutputFormat.PDF:
                sb.AppendLine("OUTPUT FORMAT: Plain text for PDF rendering");
                sb.AppendLine("- Use standard markdown image syntax for slide references: ![Slide X](slides/slide_XX.png)");
                sb.AppendLine("- Use markdown headers (# ## ###) for section titles — these WILL be rendered correctly");
                sb.AppendLine("- Use bullet points (- or •) for lists");
                sb.AppendLine("- Use > for blockquotes");
                sb.AppendLine("- Do NOT use **bold**, *italic*, or `code` inline markup — the PDF renderer does not support inline markdown and it will show raw asterisks in the output");
                sb.AppendLine("- Instead of bold, just write the text normally — do not wrap words in asterisks");
                sb.AppendLine("- Write clearly and legibly without relying on bold or italic emphasis");
                break;
        }
    }

    private static string BuildUserContent(List<string> slideTexts, Dictionary<string, List<string>> bookTexts,
        GenerationSettings settings)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== LECTURE SLIDES ===");
        sb.AppendLine($"Total slides: {slideTexts.Count}");
        sb.AppendLine();
        for (int i = 0; i < slideTexts.Count; i++)
        {
            sb.AppendLine($"--- SLIDE {i + 1} ---");
            sb.AppendLine(slideTexts[i].Trim());
            sb.AppendLine();
        }

        if (bookTexts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== REFERENCE BOOK CONTENT ===");
            foreach (var (fileName, pages) in bookTexts)
            {
                var bookFile = settings.BookFiles.FirstOrDefault(b => b.FileName == fileName);
                sb.AppendLine($"\n--- BOOK: {fileName} ---");
                if (bookFile != null && !string.IsNullOrWhiteSpace(bookFile.ChapterInfo))
                {
                    sb.AppendLine($"Chapter/Section: {bookFile.ChapterInfo}");
                }
                sb.AppendLine();
                for (int i = 0; i < pages.Count; i++)
                {
                    sb.AppendLine($"[Page {i + 1}]");
                    sb.AppendLine(pages[i].Trim());
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    // ── Utilities ─────────────────────────────────────────────────────

    /// <summary>
    /// Parses a user-provided chapter info string to extract page numbers.
    /// Supports formats like "Chapter 10, pages 305-340", "p305-340", "305-340"
    /// </summary>
    private static void ParsePageRange(string? chapterInfo, out int? startPage, out int? endPage)
    {
        startPage = null;
        endPage = null;

        if (string.IsNullOrWhiteSpace(chapterInfo)) return;

        var match = PageRangeRegex.Match(chapterInfo);
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out int s)) startPage = s;
            if (int.TryParse(match.Groups[2].Value, out int e)) endPage = e;
        }
    }
}
