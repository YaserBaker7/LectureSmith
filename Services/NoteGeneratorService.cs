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
            var pdfHash = Path.GetFileNameWithoutExtension(settings.SlidesFile.FilePath).GetHashCode().ToString("X8");
            var slidesDir = Path.Combine(Path.GetTempPath(), "LectureSmith", $"slides_{pdfHash}");

            if (Directory.Exists(slidesDir))
            {
                var existing = Directory.GetFiles(slidesDir, "slide_*.png").OrderBy(PdfExtractorService.GetSlideNumber).ToList();
                if (existing.Count > 0)
                {
                    result.SlideImagePaths = existing;
                    progress?.Report(new ProgressUpdate("Reusing cached slide images...", 10));
                }
            }

            if (result.SlideImagePaths.Count == 0)
            {
                result.SlideImagePaths = await _pdfExtractor.ExtractSlideImagesAsync(
                    settings.SlidesFile.FilePath, slidesDir,
                    new Progress<(int current, int total)>(p =>
                        progress?.Report(new ProgressUpdate($"Extracting slide {p.current} of {p.total}...",
                            (int)(10.0 * p.current / p.total)))), ct);
            }
            ct.ThrowIfCancellationRequested();
        }

        // Step 2: Extract text from slides
        progress?.Report(new ProgressUpdate("Extracting text from slides...", 12));
        var slideTexts = new List<string>();
        if (settings.SlidesFile != null)
        {
            slideTexts = await _pdfExtractor.ExtractTextAsync(settings.SlidesFile.FilePath, ct: ct);
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
        var systemPrompt = BuildSystemPrompt(settings, slideTexts.Count);
        var userContent = BuildUserContent(slideTexts, bookTexts, settings);

        // Step 4b: Report estimated token count
        var totalChars = slideTexts.Sum(t => t.Length) + bookTexts.Values.SelectMany(v => v).Sum(t => t.Length) + systemPrompt.Length + userContent.Length;
        var estimatedInputTokens = totalChars / 4;  // ~4 chars per token
        progress?.Report(new ProgressUpdate($"Estimated input: ~{estimatedInputTokens:N0} tokens ({slideTexts.Count} slides)", 28));

        // Step 5: Call Gemini API
        result.MarkdownContent = await CallGeminiAsync(settings, systemPrompt, userContent,
            result.SlideImagePaths, progress, liveText, ct);

        // Step 6: Verify completeness and auto-continue if AI stopped early
        if (slideTexts.Count > 0)
        {
            int lastCoveredSlide = FindHighestCoveredSlide(result.MarkdownContent);
            int continuationAttempt = 0;
            const int maxContinuations = 4;

            while (lastCoveredSlide < slideTexts.Count && continuationAttempt < maxContinuations)
            {
                ct.ThrowIfCancellationRequested();
                continuationAttempt++;
                int startSlide = lastCoveredSlide + 1;

                progress?.Report(new ProgressUpdate(
                    $"AI covered up to Slide {lastCoveredSlide} of {slideTexts.Count}. Auto-continuing for remaining slides...",
                    30 + (int)(60.0 * lastCoveredSlide / slideTexts.Count)));

                // Strip premature summary table so it can be re-appended at the very end
                result.MarkdownContent = StripPrematureSummaryTable(result.MarkdownContent);

                // Build continuation user content containing only remaining slides
                var continuationUserContent = BuildContinuationUserContent(slideTexts, bookTexts, settings, startSlide);
                var continuationPrompt = $"You previously generated lecture notes up to Slide {lastCoveredSlide}. " +
                                         $"There are {slideTexts.Count} slides total in this lecture. " +
                                         $"Now CONTINUE generating notes starting from Slide {startSlide} through Slide {slideTexts.Count}. " +
                                         $"Follow the exact same format and style as before. " +
                                         $"Start directly with '## Slide {startSlide} — [Title]'. Do NOT repeat previous slides or write an introduction. " +
                                         $"Only write the Key Concepts Summary table AFTER Slide {slideTexts.Count}.";

                var remainingImages = result.SlideImagePaths.Count >= slideTexts.Count
                    ? result.SlideImagePaths.Skip(startSlide - 1).ToList()
                    : result.SlideImagePaths;

                liveText?.Report($"\n\n---\n*Continuing notes from Slide {startSlide}...*\n\n");

                var continuationMarkdown = await CallGeminiAsync(settings, systemPrompt,
                    $"{continuationPrompt}\n\n{continuationUserContent}",
                    remainingImages, progress, liveText, ct);

                result.MarkdownContent = result.MarkdownContent.TrimEnd() + "\n\n" + continuationMarkdown.TrimStart();

                int newHighest = FindHighestCoveredSlide(result.MarkdownContent);
                if (newHighest <= lastCoveredSlide)
                {
                    break;
                }
                lastCoveredSlide = newHighest;
            }
        }

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
                    15 + (int)(5.0 * p.current / p.total)))), ct);
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
            var pages = await _pdfExtractor.ExtractTextAsync(book.FilePath, startPage, endPage, ct);
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

    private static string BuildSystemPrompt(GenerationSettings settings, int totalSlides)
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
        sb.AppendLine($"- Total Slides in Deck: {totalSlides}");
        sb.AppendLine();

        // Language instruction
        sb.AppendLine("LANGUAGE:");
        sb.AppendLine(settings.Language.AiInstruction());
        sb.AppendLine();

        sb.AppendLine(settings.Mode.AiInstruction());
        sb.AppendLine();

        AppendSlideProcessingContext(sb, settings.SlideProcessing);

        sb.AppendLine("YOUR TASK:");
        sb.AppendLine($"For EACH slide in the lecture presentation (from Slide 1 through the final Slide {totalSlides}):");
        sb.AppendLine("1. Write a heading with the slide number and title: '## Slide X — Title'");
        sb.AppendLine("2. Include the slide image reference (I will tell you the syntax below)");
        sb.AppendLine("3. Provide the explanation based on the mode (detailed for missed, concise for attended)");
        sb.AppendLine("4. If reference book content was provided, integrate relevant book knowledge into your explanation");
        sb.AppendLine($"5. DO NOT skip any slides — cover every single one from Slide 1 to Slide {totalSlides} (unless marked as SKIPPED)");
        sb.AppendLine();

        sb.AppendLine("CRITICAL COMPLETENESS REQUIREMENT:");
        sb.AppendLine($"- There are exactly {totalSlides} slides in this presentation.");
        sb.AppendLine($"- You MUST write an explanation section for EVERY SINGLE SLIDE from Slide 1 all the way to Slide {totalSlides}.");
        sb.AppendLine($"- DO NOT stop early under any circumstances. You must reach '## Slide {totalSlides}' before writing the summary table.");
        sb.AppendLine($"- The final slide section in your notes MUST be Slide {totalSlides}.");
        sb.AppendLine($"- Writing the Key Concepts Summary table before covering all {totalSlides} slides is strictly forbidden.");
        sb.AppendLine();

        AppendFormatInstructions(sb, settings.Format);

        sb.AppendLine();
        sb.AppendLine("VISUAL ELEMENTS (Diagrams, Figures, Charts):");
        sb.AppendLine("- Do NOT recreate or reproduce diagrams using ASCII art, box-drawing characters, or terminal-style text graphics");
        sb.AppendLine("- The student has the slides open alongside these notes and can already see all figures");
        sb.AppendLine("- Instead, REFERENCE the figure on the slide: 'As shown in the diagram on Slide X...'");
        sb.AppendLine("- Focus on explaining WHAT the diagram shows conceptually and WHY it matters");
        sb.AppendLine("- Explain the relationships, flows, and key takeaways from the visual — not its visual structure");
        sb.AppendLine("- Only describe specific data points, labels, or values if they are important for understanding");

        // Slide skipping instructions
        if (settings.SkippedSlides.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("SKIPPED SLIDES:");
            sb.AppendLine("- Some slides are marked as SKIPPED by the student");
            sb.AppendLine("- For SKIPPED slides: include the slide heading (## Slide X — [Topic]) but write ONLY 'Skipped' underneath");
            sb.AppendLine("- Do NOT provide any explanation for skipped slides");
            sb.AppendLine("- Still use skipped slide content for context when explaining other slides");
        }

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
                sb.AppendLine("- For mathematical formulas, use standard LaTeX syntax: $...$ for inline math and $$...$$ for display equations");
                break;
            case OutputFormat.HTML:
                sb.AppendLine("OUTPUT FORMAT: Clean Markdown (will be converted to HTML)");
                sb.AppendLine("- Use standard markdown image syntax: ![Slide X](slides/slide_XX.png)");
                sb.AppendLine("- Use standard markdown formatting (headers, bold, italic, lists, tables)");
                sb.AppendLine("- For mathematical formulas, use standard LaTeX syntax: $...$ for inline math and $$...$$ for display equations");
                break;
            case OutputFormat.PDF:
                sb.AppendLine("OUTPUT FORMAT: Clean Markdown for PDF rendering");
                sb.AppendLine("- Use standard markdown image syntax for slide references: ![Slide X](slides/slide_XX.png)");
                sb.AppendLine("- Use markdown headers (# ## ###) for section titles");
                sb.AppendLine("- Use bullet points (- or •) for lists");
                sb.AppendLine("- Use > for blockquotes");
                sb.AppendLine("- Use **bold**, *italic*, and `code` formatting where appropriate");
                sb.AppendLine("- For mathematical formulas, use standard LaTeX syntax: $...$ for inline math and $$...$$ for display equations");
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
            var slideNum = i + 1;
            if (settings.SkippedSlides.Contains(slideNum))
            {
                sb.AppendLine($"--- SLIDE {slideNum} [SKIPPED] ---");
                sb.AppendLine(slideTexts[i].Trim());
                sb.AppendLine("[NOTE: This slide is SKIPPED — include heading but write only 'Skipped' in the output]");
            }
            else
            {
                sb.AppendLine($"--- SLIDE {slideNum} ---");
                sb.AppendLine(slideTexts[i].Trim());
            }
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

    /// <summary>
    /// Finds the highest slide number that was covered in the generated markdown.
    /// Prioritizes section headings like '## Slide 31' to avoid false matches in body prose.
    /// </summary>
    private static int FindHighestCoveredSlide(string markdown)
    {
        // 1. Look for explicit line-start headings (e.g. "## Slide 31", "### Slide 31", "# Slide 31")
        var headerMatches = Regex.Matches(markdown, @"(?m)^\s*#{1,3}\s*Slide\s+(\d+)", RegexOptions.IgnoreCase);
        int max = 0;
        foreach (Match m in headerMatches)
        {
            if (int.TryParse(m.Groups[1].Value, out int num) && num > max && num <= 500)
            {
                max = num;
            }
        }

        if (max > 0) return max;

        // 2. Fallback: match inline bold slide headers like "**Slide 31**" or "## Slide 31" anywhere
        var fallbackMatches = Regex.Matches(markdown, @"(?:#{1,3}\s*Slide|\*\*Slide)\s*(\d+)", RegexOptions.IgnoreCase);
        foreach (Match m in fallbackMatches)
        {
            if (int.TryParse(m.Groups[1].Value, out int num) && num > max && num <= 500)
            {
                max = num;
            }
        }

        return max;
    }

    /// <summary>
    /// Strips any premature summary table generated when the model stopped early,
    /// so the continuation slides can be cleanly appended before the final summary.
    /// </summary>
    private static string StripPrematureSummaryTable(string markdown)
    {
        var tableMarker = "| Concept |";
        var lastTable = markdown.LastIndexOf(tableMarker, StringComparison.OrdinalIgnoreCase);
        if (lastTable > 0 && lastTable > markdown.Length - 4000)
        {
            var headerMarkers = new[] { "## Key Concepts", "# Key Concepts", "## Summary", "# Summary" };
            foreach (var marker in headerMarkers)
            {
                var hIdx = markdown.LastIndexOf(marker, lastTable, StringComparison.OrdinalIgnoreCase);
                if (hIdx > 0 && hIdx > markdown.Length - 5000)
                {
                    return markdown.Substring(0, hIdx).TrimEnd();
                }
            }
            return markdown.Substring(0, lastTable).TrimEnd();
        }
        return markdown.TrimEnd();
    }

    /// <summary>
    /// Builds user content containing only the remaining slides for continuation.
    /// </summary>
    private static string BuildContinuationUserContent(List<string> slideTexts,
        Dictionary<string, List<string>> bookTexts, GenerationSettings settings, int startSlideNumber)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== REMAINING LECTURE SLIDES (Slides {startSlideNumber} to {slideTexts.Count}) ===");
        sb.AppendLine($"Total slides in deck: {slideTexts.Count}");
        sb.AppendLine();

        for (int i = startSlideNumber - 1; i < slideTexts.Count; i++)
        {
            var slideNum = i + 1;
            if (settings.SkippedSlides.Contains(slideNum))
            {
                sb.AppendLine($"--- SLIDE {slideNum} [SKIPPED] ---");
                sb.AppendLine(slideTexts[i].Trim());
                sb.AppendLine("[NOTE: This slide is SKIPPED — include heading but write only 'Skipped' in the output]");
            }
            else
            {
                sb.AppendLine($"--- SLIDE {slideNum} ---");
                sb.AppendLine(slideTexts[i].Trim());
            }
            sb.AppendLine();
        }

        if (bookTexts.Count > 0)
        {
            sb.AppendLine("=== REFERENCE BOOK CONTENT (for context) ===");
            foreach (var (fileName, pages) in bookTexts)
            {
                sb.AppendLine($"\n--- BOOK: {fileName} ---");
                for (int i = 0; i < pages.Count; i++)
                {
                    sb.AppendLine($"[Page {i + 1}] {pages[i].Trim()}");
                }
            }
        }

        return sb.ToString();
    }
}
