using Docnet.Core;
using Docnet.Core.Models;
using SkiaSharp;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LectureSmith.Services;

public partial class PdfExtractorService : IPdfExtractorService
{
    private static readonly PageDimensions DefaultPageDimensions = new(1080, 1920);
    private static readonly PageDimensions PreviewPageDimensions = new(540, 960);

    /// <summary>
    /// Serializes all native Docnet calls because PDFium is not thread-safe.
    /// </summary>
    private static readonly SemaphoreSlim _docnetLock = new(1, 1);

    public async Task<List<string>> ExtractTextAsync(string pdfPath, int? startPage = null, int? endPage = null, CancellationToken ct = default)
    {
        await _docnetLock.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                var pages = new List<string>();
                var library = DocLib.Instance;
                using var docReader = library.GetDocReader(pdfPath, DefaultPageDimensions);

                int pageCount = docReader.GetPageCount();
                if (pageCount <= 0) return pages;

                int start = Math.Clamp((startPage ?? 1) - 1, 0, pageCount - 1);
                int end = Math.Clamp((endPage ?? pageCount) - 1, 0, pageCount - 1);
                if (end < start)
                    (start, end) = (end, start);

                for (int i = start; i <= end; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var pageReader = docReader.GetPageReader(i);
                    pages.Add(pageReader.GetText() ?? string.Empty);
                }

                return pages;
            }, ct);
        }
        finally
        {
            _docnetLock.Release();
        }
    }

    public Task<List<string>> ExtractSlideImagesAsync(string pdfPath, string outputDir,
        IProgress<(int current, int total)>? progress = null, CancellationToken ct = default)
        => ExtractImagesInternalAsync(pdfPath, outputDir, DefaultPageDimensions, progress, ct);

    public Task<List<string>> ExtractSlidePreviewsAsync(string pdfPath, string outputDir,
        IProgress<(int current, int total)>? progress = null, CancellationToken ct = default)
        => ExtractImagesInternalAsync(pdfPath, outputDir, PreviewPageDimensions, progress, ct);

    public async Task<int> GetPageCountAsync(string pdfPath, CancellationToken ct = default)
    {
        await _docnetLock.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                var library = DocLib.Instance;
                using var docReader = library.GetDocReader(pdfPath, DefaultPageDimensions);
                return docReader.GetPageCount();
            }, ct);
        }
        finally
        {
            _docnetLock.Release();
        }
    }

    private async Task<List<string>> ExtractImagesInternalAsync(string pdfPath, string outputDir,
        PageDimensions dimensions, IProgress<(int current, int total)>? progress, CancellationToken ct = default)
    {
        await _docnetLock.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                Directory.CreateDirectory(outputDir);

                var library = DocLib.Instance;
                int pageCount;
                var savedByPage = new Dictionary<int, string>();
                var failedIndices = new List<int>();

                using (var docReader = library.GetDocReader(pdfPath, dimensions))
                {
                    pageCount = docReader.GetPageCount();

                    for (int i = 0; i < pageCount; i++)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (i > 0 && i % 10 == 0)
                            GC.Collect(1, GCCollectionMode.Optimized);

                        var imagePath = Path.Combine(outputDir, $"slide_{i + 1:D2}.png");
                        if (TryExtractPageWithRetry(docReader, i, imagePath))
                            savedByPage[i] = imagePath;
                        else
                            failedIndices.Add(i);

                        progress?.Report((i + 1, pageCount));
                    }
                }

                if (failedIndices.Count > 0)
                {
                    GC.Collect(2, GCCollectionMode.Forced, true);
                    GC.WaitForPendingFinalizers();

                    var stillMissing = RecoverFailedPages(
                        library, pdfPath, dimensions, outputDir, failedIndices, savedByPage, ct);

                    if (stillMissing.Count > 0)
                    {
                        throw new InvalidOperationException(
                            $"Could not extract slide(s) {string.Join(", ", stillMissing.Select(idx => idx + 1))} from '{Path.GetFileName(pdfPath)}'.");
                    }
                }

                return Enumerable.Range(0, pageCount)
                    .Select(i => savedByPage[i])
                    .ToList();
            }, ct);
        }
        finally
        {
            _docnetLock.Release();
        }
    }

    private static bool TryExtractPageWithRetry(Docnet.Core.Readers.IDocReader docReader, int pageIndex, string imagePath)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var pageReader = docReader.GetPageReader(pageIndex);
                var rawBytes = pageReader.GetImage();
                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();

                if (rawBytes is { Length: > 0 } && width > 0 && height > 0)
                {
                    SaveBgraAsPng(rawBytes, width, height, imagePath);
                    if (File.Exists(imagePath) && new FileInfo(imagePath).Length > 0)
                        return true;
                }
            }
            catch
            {
                if (attempt < 3)
                {
                    GC.Collect(2, GCCollectionMode.Forced, true);
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(100 * attempt);
                }
            }
        }
        return false;
    }

    private static List<int> RecoverFailedPages(
        DocLib library, string pdfPath, PageDimensions primaryDimensions, string outputDir,
        List<int> failedIndices, Dictionary<int, string> savedByPage, CancellationToken ct)
    {
        var remaining = new List<int>();
        using (var freshReader = library.GetDocReader(pdfPath, primaryDimensions))
        {
            foreach (var idx in failedIndices)
            {
                ct.ThrowIfCancellationRequested();
                var imagePath = Path.Combine(outputDir, $"slide_{idx + 1:D2}.png");
                if (TryExtractPageWithRetry(freshReader, idx, imagePath))
                    savedByPage[idx] = imagePath;
                else
                    remaining.Add(idx);
            }
        }

        if (remaining.Count == 0 || primaryDimensions.Equals(PreviewPageDimensions))
            return remaining;

        var stillMissing = new List<int>();
        using (var fallbackReader = library.GetDocReader(pdfPath, PreviewPageDimensions))
        {
            foreach (var idx in remaining)
            {
                ct.ThrowIfCancellationRequested();
                var imagePath = Path.Combine(outputDir, $"slide_{idx + 1:D2}.png");
                if (TryExtractPageWithRetry(fallbackReader, idx, imagePath))
                    savedByPage[idx] = imagePath;
                else
                    stillMissing.Add(idx);
            }
        }
        return stillMissing;
    }

    private static void SaveBgraAsPng(byte[] rawBytes, int width, int height, string outputPath)
    {
        var expectedSize = width * height * 4;
        var copyLength = Math.Min(rawBytes.Length, expectedSize);

        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var pixelsPtr = bitmap.GetPixels();
        Marshal.Copy(rawBytes, 0, pixelsPtr, copyLength);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 85);
        using var stream = File.Create(outputPath);
        data.SaveTo(stream);
    }

    public static int GetSlideNumber(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var match = SlideNumRegex().Match(fileName);
        return match.Success && int.TryParse(match.Value, out var num) ? num : int.MaxValue;
    }

    public static bool IsContinuousSlideSequence(List<string> imagePaths, int expectedTotal)
    {
        if (imagePaths == null || imagePaths.Count != expectedTotal) return false;
        for (int i = 0; i < expectedTotal; i++)
        {
            if (GetSlideNumber(imagePaths[i]) != i + 1)
                return false;
        }
        return true;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex SlideNumRegex();
}
