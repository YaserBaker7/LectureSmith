using LectureSmith.Models;
using System.Text.RegularExpressions;

namespace LectureSmith.Services;

public partial class NoteGeneratorService : INoteGeneratorService
{
    private readonly IPdfExtractorService _pdfExtractor;
    private readonly ISlideCacheService _slideCache;
    private readonly IGeminiService _gemini;
    private readonly IOcrService _ocr;

    public NoteGeneratorService(
        IPdfExtractorService pdfExtractor,
        ISlideCacheService slideCache,
        IGeminiService gemini,
        IOcrService ocr)
    {
        _pdfExtractor = pdfExtractor;
        _slideCache = slideCache;
        _gemini = gemini;
        _ocr = ocr;
    }

    public async Task<GenerationResult> GenerateAsync(
        GenerationSettings settings,
        IProgress<ProgressUpdate>? progress = null,
        IProgress<string>? liveText = null,
        Action<string>? onResetPreview = null,
        Action<IReadOnlyList<string>>? onSlideImagesReady = null,
        CancellationToken ct = default)
    {
        var result = new GenerationResult();
        int totalInputTokens = 0;
        int totalOutputTokens = 0;

        // Step 1: Extract or reuse cached slide images (needed for OCR, Vision, and live preview)
        progress?.Report(new ProgressUpdate("Preparing slides...", 0));
        if (settings.SlidesFile != null)
        {
            result.SlideImagePaths = await _slideCache.GetOrExtractSlidesAsync(
                settings.SlidesFile.FilePath,
                previewQuality: false,
                new Progress<(int current, int total)>(p =>
                    progress?.Report(new ProgressUpdate(
                        $"Slide {p.current} / {p.total}",
                        (int)(10.0 * p.current / Math.Max(1, p.total))))),
                ct);

            ct.ThrowIfCancellationRequested();
            onSlideImagesReady?.Invoke(result.SlideImagePaths);
        }

        // Step 2: Extract text from slides
        progress?.Report(new ProgressUpdate("Extracting text from slides...", 12));
        var slideTexts = new List<string>();
        if (settings.SlidesFile != null)
        {
            slideTexts = await _pdfExtractor.ExtractTextAsync(settings.SlidesFile.FilePath, ct: ct);
            ct.ThrowIfCancellationRequested();
        }

        // Step 2b: OCR Enhancement — ensure tessdata is present (auto-download if needed) and merge OCR output
        if (settings.SlideProcessing == SlideProcessingMode.OcrEnhanced && result.SlideImagePaths.Count > 0)
        {
            await RunOcrEnhancementAsync(slideTexts, result.SlideImagePaths, progress, ct);
        }

        // Step 3: Extract text from reference books (with true page offsets)
        progress?.Report(new ProgressUpdate("Extracting text from reference books...", 20));
        var books = await ExtractBookTextsAsync(settings.BookFiles, ct);

        // Step 4: Build AI prompts
        progress?.Report(new ProgressUpdate("Building AI prompt...", 25));
        var systemPrompt = PromptBuilder.BuildSystemPrompt(settings, slideTexts.Count);
        var userContent = PromptBuilder.BuildUserContent(slideTexts, books, settings, startSlideNumber: 1);

        var estInputChars = systemPrompt.Length + userContent.Length;
        progress?.Report(new ProgressUpdate($"Estimated input: ~{estInputChars / 4:N0} tokens ({slideTexts.Count} slides)", 28));

        // Step 5: Initial Gemini generation pass
        var (initialText, initialUsage) = await CallGeminiAsync(
            settings, systemPrompt, userContent, result.SlideImagePaths, progress, liveText, ct);
        result.MarkdownContent = initialText;
        totalInputTokens += ResolveTokenCount(initialUsage.InputTokens, estInputChars);
        totalOutputTokens += ResolveTokenCount(initialUsage.OutputTokens, initialText.Length);

        // Step 6: Completeness check & seamless resume if AI stopped early (interruption, final slide cutoff, or token limit)
        if (slideTexts.Count > 0)
        {
            int lastCoveredSlide = FindHighestCoveredSlide(result.MarkdownContent, slideTexts.Count);
            bool finalSlideLooksTruncated = lastCoveredSlide >= slideTexts.Count && LooksTruncated(result.MarkdownContent);
            int continuationAttempt = 0;
            int stuckRetries = 0;
            const int maxContinuations = 5;

            while ((lastCoveredSlide < slideTexts.Count || finalSlideLooksTruncated) &&
                   continuationAttempt < maxContinuations)
            {
                ct.ThrowIfCancellationRequested();
                continuationAttempt++;

                int startSlide = finalSlideLooksTruncated
                    ? slideTexts.Count
                    : Math.Max(1, lastCoveredSlide);

                result.MarkdownContent = StripSlideAndBeyond(result.MarkdownContent, startSlide);

                var previewPrefix = string.IsNullOrWhiteSpace(result.MarkdownContent)
                    ? string.Empty
                    : result.MarkdownContent.TrimEnd() + "\n\n";
                onResetPreview?.Invoke(previewPrefix);

                progress?.Report(new ProgressUpdate(
                    $"Resuming slide {startSlide} / {slideTexts.Count}",
                    30 + (int)(60.0 * (startSlide - 1) / slideTexts.Count)));

                var continuationUserContent = PromptBuilder.BuildUserContent(slideTexts, books, settings, startSlide);
                var previousNotesContext = string.IsNullOrWhiteSpace(result.MarkdownContent)
                    ? string.Empty
                    : $"=== PREVIOUSLY GENERATED NOTES (Up to Slide {startSlide - 1}) ===\n" +
                      $"{result.MarkdownContent}\n\n---\n\n";

                var continuationPrompt =
                    $"{previousNotesContext}" +
                    $"You are continuing the lecture notes from where you left off. " +
                    $"The student already has notes through Slide {startSlide - 1}. " +
                    $"There are {slideTexts.Count} slides total in this deck. " +
                    $"Now CONTINUE generating notes starting directly from Slide {startSlide} through Slide {slideTexts.Count}. " +
                    $"Maintain the exact same tone, depth, and formatting. " +
                    $"Start immediately with '## Slide {startSlide} — [Title]'. Do NOT repeat earlier slides or write a new intro. " +
                    $"Write the Key Concepts Summary table ONLY AFTER Slide {slideTexts.Count}.\n\n" +
                    continuationUserContent;

                var remainingImages = result.SlideImagePaths.Count >= slideTexts.Count
                    ? result.SlideImagePaths.Skip(startSlide - 1).ToList()
                    : result.SlideImagePaths;

                try
                {
                    var (contText, contUsage) = await CallGeminiAsync(
                        settings, systemPrompt, continuationPrompt, remainingImages, progress, liveText, ct);

                    totalInputTokens += ResolveTokenCount(contUsage.InputTokens, systemPrompt.Length + continuationPrompt.Length);
                    totalOutputTokens += ResolveTokenCount(contUsage.OutputTokens, contText.Length);

                    result.MarkdownContent = string.IsNullOrWhiteSpace(result.MarkdownContent)
                        ? contText.TrimStart()
                        : result.MarkdownContent.TrimEnd() + "\n\n" + contText.TrimStart();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception) when (continuationAttempt < maxContinuations)
                {
                    await Task.Delay(1500 * continuationAttempt, ct);
                    continue;
                }

                int newHighest = FindHighestCoveredSlide(result.MarkdownContent, slideTexts.Count);
                finalSlideLooksTruncated = newHighest >= slideTexts.Count && LooksTruncated(result.MarkdownContent);

                if (newHighest <= lastCoveredSlide && newHighest > 0 && !finalSlideLooksTruncated)
                {
                    stuckRetries++;
                    if (stuckRetries >= 2)
                        break;
                }
                else
                {
                    stuckRetries = 0;
                    lastCoveredSlide = newHighest;
                }
            }

            // Ensure summary table exists once all slides are covered
            if (lastCoveredSlide >= slideTexts.Count &&
                !result.MarkdownContent.Contains("| Concept |", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var summaryPrompt =
                        $"The lecture notes covering all {slideTexts.Count} slides are complete. " +
                        $"Write ONLY the final Key Concepts Summary table for this lecture. " +
                        $"Do not repeat any slide notes. Start directly with '## Key Concepts Summary' followed by the markdown table.\n\n" +
                        result.MarkdownContent;

                    var (summaryText, summaryUsage) = await CallGeminiAsync(
                        settings, systemPrompt, summaryPrompt, [], progress, liveText, ct);

                    totalInputTokens += ResolveTokenCount(summaryUsage.InputTokens, systemPrompt.Length + summaryPrompt.Length);
                    totalOutputTokens += ResolveTokenCount(summaryUsage.OutputTokens, summaryText.Length);

                    result.MarkdownContent = result.MarkdownContent.TrimEnd() + "\n\n" + summaryText.TrimStart();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Non-critical: summary auto-append is optional
                }
            }
        }

        result.MarkdownContent = ExcessiveNewlinesRegex().Replace(result.MarkdownContent, "\n\n").Trim();
        result.InputTokens = totalInputTokens;
        result.OutputTokens = totalOutputTokens;

        progress?.Report(new ProgressUpdate("Notes generated successfully!", 95));
        return result;
    }

    private async Task RunOcrEnhancementAsync(
        List<string> slideTexts,
        List<string> slideImagePaths,
        IProgress<ProgressUpdate>? progress,
        CancellationToken ct)
    {
        progress?.Report(new ProgressUpdate("Checking Tesseract OCR language data...", 13));
        await _ocr.EnsureTrainedDataAsync(
            new Progress<string>(msg => progress?.Report(new ProgressUpdate(msg, 14))),
            ct);

        progress?.Report(new ProgressUpdate("OCR scanning...", 15));
        var ocrTexts = await _ocr.ExtractTextFromImagesAsync(
            slideImagePaths,
            new Progress<(int current, int total)>(p =>
                progress?.Report(new ProgressUpdate(
                    $"OCR slide {p.current} / {p.total}",
                    15 + (int)(5.0 * p.current / Math.Max(1, p.total))))),
            ct);
        ct.ThrowIfCancellationRequested();

        for (int i = 0; i < slideTexts.Count && i < ocrTexts.Count; i++)
        {
            var docnetText = slideTexts[i].Trim();
            var ocrText = ocrTexts[i].Trim();

            if (!string.IsNullOrWhiteSpace(ocrText) && ocrText != docnetText)
            {
                slideTexts[i] = string.IsNullOrWhiteSpace(docnetText)
                    ? $"[OCR-extracted text:]\n{ocrText}"
                    : $"{docnetText}\n\n[OCR-extracted text:]\n{ocrText}";
            }
        }

        for (int i = slideTexts.Count; i < ocrTexts.Count; i++)
            slideTexts.Add($"[OCR-extracted text:]\n{ocrTexts[i]}");
    }

    private async Task<List<(string FileName, string ChapterInfo, int StartPage, List<string> Pages)>> ExtractBookTextsAsync(
        List<UploadedFile> bookFiles, CancellationToken ct)
    {
        var books = new List<(string, string, int, List<string>)>();
        foreach (var book in bookFiles)
        {
            PromptBuilder.ParsePageRange(book.ChapterInfo, out var startPage, out var endPage);
            var pages = await _pdfExtractor.ExtractTextAsync(book.FilePath, startPage, endPage, ct);
            int actualStart = startPage.HasValue && endPage.HasValue
                ? Math.Min(startPage.Value, endPage.Value)
                : (startPage ?? 1);
            books.Add((book.FileName, book.ChapterInfo, Math.Max(1, actualStart), pages));
            ct.ThrowIfCancellationRequested();
        }
        return books;
    }

    private async Task<(string Text, TokenUsage Usage)> CallGeminiAsync(
        GenerationSettings settings,
        string systemPrompt,
        string userContent,
        List<string> slideImagePaths,
        IProgress<ProgressUpdate>? progress,
        IProgress<string>? liveText,
        CancellationToken ct)
    {
        progress?.Report(new ProgressUpdate("Contacting Gemini...", 30));

        int streamedChars = 0;
        var streamProgress = new Progress<string>(chunk =>
        {
            streamedChars += chunk.Length;
            progress?.Report(new ProgressUpdate(
                "Writing notes...",
                30 + (int)(60.0 * streamedChars / Math.Max(1, streamedChars + 1200))));
            liveText?.Report(chunk);
        });

        if (settings.SlideProcessing == SlideProcessingMode.VisionAI && slideImagePaths.Count > 0)
        {
            progress?.Report(new ProgressUpdate($"Sending {slideImagePaths.Count} images...", 30));
            return await _gemini.GenerateWithImagesAsync(systemPrompt, userContent, slideImagePaths, streamProgress, ct);
        }

        return await _gemini.GenerateAsync(systemPrompt, userContent, streamProgress, ct);
    }

    private static int ResolveTokenCount(int apiTokens, int charCountFallback)
        => apiTokens > 0 ? apiTokens : Math.Max(0, charCountFallback / 4);

    internal static int FindHighestCoveredSlide(string markdown, int totalSlides = 500)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return 0;

        var cleanMarkdown = StripPrematureSummaryTable(markdown);

        var matches = SlideHeadingRegex().Matches(cleanMarkdown);
        if (matches.Count == 0)
            matches = BoldSlideRegex().Matches(cleanMarkdown);

        if (matches.Count == 0) return 0;

        int maxAllowed = Math.Max(1, totalSlides);
        int highest = 0;

        // Walk backward: pick the latest slide heading whose number is within deck bounds
        // and not wildly ahead of sequential coverage.
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            if (int.TryParse(matches[i].Groups[1].Value, out int num) &&
                num > 0 &&
                num <= maxAllowed &&
                num <= matches.Count + 12)
            {
                return num;
            }
        }

        foreach (Match m in matches)
        {
            if (int.TryParse(m.Groups[1].Value, out int num) && num > highest && num <= maxAllowed)
                highest = num;
        }

        return highest;
    }

    internal static bool LooksTruncated(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return false;
        if (markdown.Contains("| Concept |", StringComparison.OrdinalIgnoreCase)) return false;

        var trimmed = markdown.TrimEnd();
        if (trimmed.Length == 0) return false;

        char lastChar = trimmed[^1];
        // A complete note section normally ends with punctuation, code fence, table pipe, or bracket
        return lastChar is not ('.' or '!' or '?' or ':' or ')' or ']' or '|' or '`' or '*' or '"' or '\'');
    }

    public static string StripSlideAndBeyond(string markdown, int slideNumber)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;

        markdown = StripPrematureSummaryTable(markdown);
        markdown = WarningLineRegex().Replace(markdown, "").TrimEnd();

        var headingMatch = Regex.Match(
            markdown,
            $@"(?m)^\s*#{{1,3}}\s*(?:Slide|Slides|Dias)\s+{slideNumber}\b",
            RegexOptions.IgnoreCase);
        if (headingMatch.Success)
            return markdown[..headingMatch.Index].TrimEnd();

        var boldMatch = Regex.Match(
            markdown,
            $@"(?:#{{1,3}}\s*(?:Slide|Slides|Dias)|\*\*(?:Slide|Slides|Dias))\s+{slideNumber}\b",
            RegexOptions.IgnoreCase);
        if (boldMatch.Success)
            return markdown[..boldMatch.Index].TrimEnd();

        return markdown.TrimEnd();
    }

    private static string StripPrematureSummaryTable(string markdown)
    {
        const string tableMarker = "| Concept |";
        var lastTable = markdown.LastIndexOf(tableMarker, StringComparison.OrdinalIgnoreCase);
        if (lastTable > 0 && lastTable > markdown.Length - 4000)
        {
            var headerMarkers = new[] { "## Key Concepts", "# Key Concepts", "## Summary", "# Summary" };
            foreach (var marker in headerMarkers)
            {
                var hIdx = markdown.LastIndexOf(marker, lastTable, StringComparison.OrdinalIgnoreCase);
                if (hIdx > 0 && hIdx > markdown.Length - 5000)
                    return markdown[..hIdx].TrimEnd();
            }
            return markdown[..lastTable].TrimEnd();
        }
        return markdown.TrimEnd();
    }

    [GeneratedRegex(@"(\r?\n){3,}")]
    private static partial Regex ExcessiveNewlinesRegex();

    [GeneratedRegex(@"(?m)^\s*#{1,3}\s*(?:Slide|Slides|Dias)\s+(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SlideHeadingRegex();

    [GeneratedRegex(@"(?:\*\*(?:Slide|Slides|Dias))\s+(\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BoldSlideRegex();

    [GeneratedRegex(@"(?m)^\s*>\s*\[!WARNING\]\s*Generation was interrupted:.*$", RegexOptions.IgnoreCase)]
    private static partial Regex WarningLineRegex();
}
