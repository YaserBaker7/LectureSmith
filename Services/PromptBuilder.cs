using LectureSmith.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace LectureSmith.Services;

/// <summary>
/// Builds structured system, user, continuation, summary, and Q&amp;A prompts for Gemini.
/// </summary>
public static partial class PromptBuilder
{
    public const string FollowUpChatSystemPrompt =
        "You are a helpful academic study assistant. The student has generated lecture notes and wants to ask follow-up questions. " +
        "Answer clearly and concisely, referencing the lecture notes when relevant. " +
        "If the student asks about something not covered in the notes, explicitly mention that and still provide an accurate explanation.";

    public static string BuildSystemPrompt(GenerationSettings settings, int totalSlides)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a university professor generating comprehensive lecture notes for a student.");
        sb.AppendLine();
        sb.AppendLine("CONTEXT:");

        if (!string.IsNullOrWhiteSpace(settings.CourseName))
            sb.AppendLine($"- Course: {settings.CourseName}");

        sb.AppendLine("- Student: University student");
        sb.AppendLine($"- Mode: {settings.Mode.DisplayName()}");
        sb.AppendLine($"- Total Slides in Deck: {totalSlides}");
        sb.AppendLine();

        sb.AppendLine("LANGUAGE:");
        sb.AppendLine(GetLanguageInstruction(settings.Language));
        sb.AppendLine();

        sb.AppendLine(GetModeInstruction(settings.Mode));
        sb.AppendLine();

        AppendSlideProcessingContext(sb, settings.SlideProcessing);

        sb.AppendLine("YOUR TASK:");
        sb.AppendLine($"For EACH slide in the lecture presentation (from Slide 1 through the final Slide {totalSlides}):");
        sb.AppendLine("1. Write a heading with the slide number and title: '## Slide X — Title'");
        sb.AppendLine("2. Include the slide image reference (syntax specified below)");
        sb.AppendLine("3. Provide the explanation based on the selected mode");
        sb.AppendLine("4. If reference book content was provided, integrate relevant book knowledge into your explanation");
        sb.AppendLine($"5. DO NOT skip any slides — cover every single one from Slide 1 to Slide {totalSlides} (unless marked as SKIPPED)");
        sb.AppendLine();

        sb.AppendLine("CRITICAL COMPLETENESS REQUIREMENT:");
        sb.AppendLine($"- There are exactly {totalSlides} slides in this presentation.");
        sb.AppendLine($"- You MUST write an explanation section for EVERY SINGLE SLIDE from Slide 1 all the way to Slide {totalSlides}.");
        sb.AppendLine($"- DO NOT stop early under any circumstances. Reach '## Slide {totalSlides}' before writing the summary table.");
        sb.AppendLine($"- Writing the Key Concepts Summary table before covering all {totalSlides} slides is strictly forbidden.");
        sb.AppendLine();

        AppendFormatInstructions(sb, settings.Format);

        sb.AppendLine();
        sb.AppendLine("EXPLANATION CLARITY & SIMPLIFICATION:");
        sb.AppendLine("- Break down dense academic terminology using intuitive, accessible language.");
        sb.AppendLine("- When introducing formal definitions from slides, follow with a plain-language clarification so the student builds real intuition.");

        sb.AppendLine();
        sb.AppendLine("VISUAL ELEMENTS (Diagrams, Figures, Charts):");
        sb.AppendLine("- Do NOT recreate diagrams using ASCII art or box-drawing characters.");
        sb.AppendLine("- Reference the figure on the slide ('As shown in the diagram on Slide X...') and explain its conceptual meaning, flow, and key takeaways.");

        if (settings.SkippedSlides.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("SKIPPED SLIDES:");
            sb.AppendLine("- Some slides are marked as [SKIPPED].");
            sb.AppendLine("- For SKIPPED slides: include the heading (## Slide X — [Topic]) and write ONLY 'Skipped' underneath.");
            sb.AppendLine("- Still use skipped slide content as background context when explaining other slides.");
        }

        sb.AppendLine();
        sb.AppendLine("START with a title header (# heading) and a brief overview section.");
        sb.AppendLine("END with a Key Concepts Summary table formatted as a valid markdown table:");
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

    public static string BuildUserContent(
        IReadOnlyList<string> slideTexts,
        IReadOnlyList<(string FileName, string ChapterInfo, int StartPage, List<string> Pages)> books,
        GenerationSettings settings,
        int startSlideNumber = 1)
    {
        var sb = new StringBuilder();
        var isContinuation = startSlideNumber > 1;

        sb.AppendLine(isContinuation
            ? $"=== REMAINING LECTURE SLIDES (Slides {startSlideNumber} to {slideTexts.Count}) ==="
            : "=== LECTURE SLIDES ===");
        sb.AppendLine($"Total slides: {slideTexts.Count}");
        sb.AppendLine();

        for (int i = Math.Max(0, startSlideNumber - 1); i < slideTexts.Count; i++)
        {
            var slideNum = i + 1;
            var text = slideTexts[i].Trim();

            if (settings.SkippedSlides.Contains(slideNum))
            {
                sb.AppendLine($"--- SLIDE {slideNum} [SKIPPED] ---");
                sb.AppendLine(text);
                sb.AppendLine("[NOTE: This slide is SKIPPED — include heading but write only 'Skipped' in the output]");
            }
            else
            {
                sb.AppendLine($"--- SLIDE {slideNum} ---");
                sb.AppendLine(string.IsNullOrWhiteSpace(text)
                    ? "[Visual slide: contains diagram, illustration, or graphic without machine-readable text — provide a concise overview heading for this slide]"
                    : text);
            }
            sb.AppendLine();
        }

        if (books.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== REFERENCE BOOK CONTENT ===");
            foreach (var (fileName, chapterInfo, startPage, pages) in books)
            {
                sb.AppendLine($"\n--- BOOK: {fileName} ---");
                if (!string.IsNullOrWhiteSpace(chapterInfo))
                    sb.AppendLine($"Chapter/Section: {chapterInfo}");
                sb.AppendLine();

                for (int i = 0; i < pages.Count; i++)
                {
                    sb.AppendLine($"[Page {startPage + i}]");
                    sb.AppendLine(pages[i].Trim());
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Extracts start/end page numbers from strings like "Ch 1-2, p305-340", "pages 12–45", or "305-340".
    /// Prioritizes explicit page prefixes ('p', 'pp', 'page', 'pages') before bare ranges so chapter numbers aren't misread.
    /// </summary>
    public static void ParsePageRange(string? chapterInfo, out int? startPage, out int? endPage)
    {
        startPage = null;
        endPage = null;
        if (string.IsNullOrWhiteSpace(chapterInfo)) return;

        var explicitMatch = ExplicitPageRangeRegex().Match(chapterInfo);
        var match = explicitMatch.Success ? explicitMatch : BareRangeRegex().Match(chapterInfo);

        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out int s) && s > 0) startPage = s;
            if (int.TryParse(match.Groups[2].Value, out int e) && e > 0) endPage = e;
        }
    }

    private static string GetModeInstruction(LectureMode mode) => mode switch
    {
        LectureMode.MissedLecture =>
            "The student MISSED this lecture entirely. Provide very detailed, professor-style explanations for every single slide. " +
            "Think: 'What would the professor have said for this slide?' Include context, examples, and connections to the textbook. " +
            "The student should feel as if they attended the lecture after reading your notes.",
        LectureMode.AttendedLecture =>
            "The student ATTENDED this lecture but wants organized notes for review. Provide concise but comprehensive notes " +
            "focusing on key points, definitions, and important concepts. Keep explanations shorter and more to-the-point.",
        _ => string.Empty
    };

    private static string GetLanguageInstruction(NoteLanguage lang) => lang switch
    {
        NoteLanguage.Auto =>
            "Write your notes in the SAME language as the lecture slides. If the slides are in Danish, write in Danish. If in English, write in English.",
        NoteLanguage.English =>
            "Write ALL notes in English, regardless of the language of the input slides.",
        NoteLanguage.Danish =>
            "Skriv ALLE noter på dansk, uanset sproget i input-slides. Write ALL notes in Danish. Keep slide headings as '## Slide X — [Dansk Titel]'.",
        _ => string.Empty
    };

    private static void AppendSlideProcessingContext(StringBuilder sb, SlideProcessingMode mode)
    {
        switch (mode)
        {
            case SlideProcessingMode.VisionAI:
                sb.AppendLine("SLIDE IMAGES:");
                sb.AppendLine("- Actual slide images are attached for inspection.");
                sb.AppendLine("- Use them to understand diagrams, illustrations, code screenshots, and visual content.");
                sb.AppendLine();
                break;
            case SlideProcessingMode.OcrEnhanced:
                sb.AppendLine("SLIDE TEXT:");
                sb.AppendLine("- Slide text combines PDF extraction with local OCR output.");
                sb.AppendLine("- OCR text may contain minor recognition artifacts — interpret using surrounding context.");
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
                sb.AppendLine("- For math formulas, use $...$ for inline math and $$...$$ for display equations");
                break;
            case OutputFormat.HTML:
                sb.AppendLine("OUTPUT FORMAT: Clean Markdown (converted to self-contained HTML)");
                sb.AppendLine("- Use standard markdown image syntax: ![Slide X](slides/slide_XX.png)");
                sb.AppendLine("- Use standard markdown headers, bold, italic, lists, tables, and code blocks");
                sb.AppendLine("- For math formulas, use $...$ for inline math and $$...$$ for display equations");
                break;
            case OutputFormat.PDF:
                sb.AppendLine("OUTPUT FORMAT: Clean Markdown (rendered to printable PDF)");
                sb.AppendLine("- Use standard markdown image syntax: ![Slide X](slides/slide_XX.png)");
                sb.AppendLine("- Use markdown headers (# ## ###), lists, blockquotes, tables, and code blocks");
                sb.AppendLine("- For math formulas, use $...$ for inline math and $$...$$ for display equations");
                break;
        }
    }

    [GeneratedRegex(@"(?:pages?|pp?\.?)\s*(\d+)\s*[-–]\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExplicitPageRangeRegex();

    [GeneratedRegex(@"(\d+)\s*[-–]\s*(\d+)")]
    private static partial Regex BareRangeRegex();
}
