using LectureSmith.Models;
using LectureSmith.Services;
using Xunit;

namespace LectureSmith.Tests;

public class PromptBuilderAndNoteGeneratorTests
{
    [Theory]
    [InlineData("Ch 1-2, p305-340", 305, 340)]
    [InlineData("Chapter 10, pages 12–45", 12, 45)]
    [InlineData("305-340", 305, 340)]
    [InlineData("", null, null)]
    public void ParsePageRange_ExtractsPageRangeWithoutMisreadingChapters(string input, int? expectedStart, int? expectedEnd)
    {
        PromptBuilder.ParsePageRange(input, out var start, out var end);
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    [Fact]
    public void FindHighestCoveredSlide_SupportsLocalizedHeadingsAndIgnoresPrematureSummary()
    {
        var md = """
            # Forelæsning
            ## Slide 1 — Intro
            Tekst
            ## Dias 2 — Kernebegreb
            Mere tekst
            ## Key Concepts Summary
            | Concept | Description |
            |---|---|
            | X | Y |
            """;

        Assert.Equal(2, NoteGeneratorService.FindHighestCoveredSlide(md, totalSlides: 5));
        Assert.True(NoteGeneratorService.LooksTruncated("## Slide 2 — Title\nThis sentence stops abruptly without"));
        Assert.False(NoteGeneratorService.LooksTruncated("## Slide 2 — Title\nThis sentence is complete."));
    }

    [Fact]
    public async Task GenerateAsync_RunsOcrEnsureTrainedData_ContinuesTruncatedFinalSlide_AndTracksUsage()
    {
        var pdf = new FakePdfExtractor(["Slide 1 text", "Slide 2 text"]);
        var cache = new FakeSlideCache(["s1.png", "s2.png"]);
        var ocr = new FakeOcrService();
        var gemini = new FakeGeminiService();

        var service = new NoteGeneratorService(pdf, cache, gemini, ocr);
        var settings = new GenerationSettings
        {
            CourseName = "Algorithms",
            SlideProcessing = SlideProcessingMode.OcrEnhanced,
            SlidesFile = new UploadedFile("C:\\tmp\\deck.pdf", FileType.Slides),
            OutputPath = "C:\\tmp\\out"
        };

        var result = await service.GenerateAsync(settings);

        Assert.True(ocr.EnsureTrainedDataCalled);
        Assert.True(ocr.ExtractCalled);
        Assert.Contains("Slide 2", result.MarkdownContent);
        Assert.Contains("| Concept |", result.MarkdownContent);
        Assert.Equal(300, result.InputTokens); // 200 initial + 100 continuation
        Assert.Equal(150, result.OutputTokens); // 80 initial + 70 continuation
    }

    private sealed class FakePdfExtractor(List<string> pages) : IPdfExtractorService
    {
        public Task<List<string>> ExtractTextAsync(string pdfPath, int? startPage = null, int? endPage = null, CancellationToken ct = default)
            => Task.FromResult(pages.ToList());
        public Task<List<string>> ExtractSlideImagesAsync(string pdfPath, string outputDir, IProgress<(int current, int total)>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
        public Task<List<string>> ExtractSlidePreviewsAsync(string pdfPath, string outputDir, IProgress<(int current, int total)>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new List<string>());
        public Task<int> GetPageCountAsync(string pdfPath, CancellationToken ct = default)
            => Task.FromResult(pages.Count);
    }

    private sealed class FakeSlideCache(List<string> paths) : ISlideCacheService
    {
        public Task<List<string>> GetOrExtractSlidesAsync(string pdfPath, bool previewQuality, IProgress<(int current, int total)>? progress = null, CancellationToken ct = default)
            => Task.FromResult(paths);
    }

    private sealed class FakeOcrService : IOcrService
    {
        public bool EnsureTrainedDataCalled { get; private set; }
        public bool ExtractCalled { get; private set; }

        public Task EnsureTrainedDataAsync(IProgress<string>? status = null, CancellationToken ct = default)
        {
            EnsureTrainedDataCalled = true;
            return Task.CompletedTask;
        }

        public Task<List<string>> ExtractTextFromImagesAsync(List<string> imagePaths, IProgress<(int current, int total)>? progress = null, CancellationToken ct = default)
        {
            ExtractCalled = true;
            return Task.FromResult(imagePaths.Select(_ => "OCR extra").ToList());
        }

        public void Dispose() { }
    }

    private sealed class FakeGeminiService : IGeminiService
    {
        private int _callCount;
        public bool IsConfigured => true;
        public void SetApiKey(string apiKey) { }
        public void SetModel(string modelId, bool thinkingEnabled = false, int? outputTokenLimit = null, bool? supportsThinking = null) { }

        public Task<(string Text, TokenUsage Usage)> GenerateAsync(string systemPrompt, string userContent, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            _callCount++;
            if (_callCount == 1)
            {
                return Task.FromResult((
                    "# Lecture\n\n## Slide 1 — Intro\nComplete slide one.\n\n## Slide 2 — Final\nCut off mid",
                    new TokenUsage(200, 80)));
            }

            return Task.FromResult((
                "## Slide 2 — Final\nComplete slide two.\n\n## Key Concepts Summary\n| Concept | Description |\n|---|---|\n| Big-O | Complexity |",
                new TokenUsage(100, 70)));
        }

        public Task<(string Text, TokenUsage Usage)> GenerateWithImagesAsync(string systemPrompt, string userContent, List<string> imagePaths, IProgress<string>? progress = null, CancellationToken ct = default)
            => GenerateAsync(systemPrompt, userContent, progress, ct);

        public IGeminiChatHandle CreateChatSession(string systemPrompt, string initialContext, IEnumerable<ChatMessage>? previousMessages = null)
            => throw new NotImplementedException();
    }
}
