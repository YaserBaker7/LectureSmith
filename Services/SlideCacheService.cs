namespace LectureSmith.Services;

/// <summary>
/// Manages deterministic, collision-free slide image caching for both preview thumbnails and full-resolution exports.
/// </summary>
public class SlideCacheService : ISlideCacheService
{
    private readonly IPdfExtractorService _pdfExtractor;

    public SlideCacheService(IPdfExtractorService pdfExtractor)
    {
        _pdfExtractor = pdfExtractor;
    }

    public async Task<List<string>> GetOrExtractSlidesAsync(
        string pdfPath,
        bool previewQuality,
        IProgress<(int current, int total)>? progress = null,
        CancellationToken ct = default)
    {
        var variant = previewQuality ? "preview" : "full";
        var cacheKey = AppPaths.ComputeFileCacheKey(pdfPath, variant);
        var cacheDir = Path.Combine(AppPaths.TempDir, $"{variant}_{cacheKey}");

        var expectedCount = await _pdfExtractor.GetPageCountAsync(pdfPath, ct);
        if (expectedCount <= 0) return [];

        if (Directory.Exists(cacheDir))
        {
            var existing = Directory.GetFiles(cacheDir, "slide_*.png")
                .OrderBy(PdfExtractorService.GetSlideNumber)
                .ToList();

            if (existing.Count == expectedCount &&
                PdfExtractorService.IsContinuousSlideSequence(existing, expectedCount))
            {
                return existing;
            }

            try
            {
                Directory.Delete(cacheDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup before re-extracting
            }
        }

        return previewQuality
            ? await _pdfExtractor.ExtractSlidePreviewsAsync(pdfPath, cacheDir, progress, ct)
            : await _pdfExtractor.ExtractSlideImagesAsync(pdfPath, cacheDir, progress, ct);
    }
}
