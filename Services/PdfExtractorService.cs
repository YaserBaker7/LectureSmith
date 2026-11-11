using Docnet.Core;
using Docnet.Core.Models;
using SkiaSharp;

namespace LectureSmith.Services;

public class PdfExtractorService
{
    private static readonly PageDimensions DefaultPageDimensions = new(1080, 1920);
    private static readonly PageDimensions PreviewPageDimensions = new(540, 960);

    /// <summary>
    /// Serializes all native Docnet calls to prevent concurrent access crashes.
    /// Docnet uses native C libraries that are not thread-safe.
    /// </summary>
    private static readonly SemaphoreSlim _docnetLock = new(1, 1);

    /// <summary>
    /// Extracts text from all pages of a PDF file.
    /// </summary>
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
                int start = (startPage ?? 1) - 1; // Convert to 0-indexed
                int end = Math.Min((endPage ?? pageCount) - 1, pageCount - 1);

                for (int i = start; i <= end; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var pageReader = docReader.GetPageReader(i);
                    var text = pageReader.GetText();
                    pages.Add(text ?? string.Empty);
                }

                return pages;
            }, ct);
        }
        finally
        {
            _docnetLock.Release();
        }
    }

    /// <summary>
    /// Extracts each page of a PDF as a PNG image at full resolution. Returns list of saved image paths.
    /// </summary>
    public async Task<List<string>> ExtractSlideImagesAsync(string pdfPath, string outputDir,
        IProgress<(int current, int total)>? progress = null, CancellationToken ct = default)
    {
        return await ExtractImagesInternalAsync(pdfPath, outputDir, DefaultPageDimensions, progress, ct);
    }

    /// <summary>
    /// Extracts each page of a PDF as a PNG image at preview resolution (half size).
    /// Used for the slide-skip popup to reduce memory usage and speed up loading.
    /// </summary>
    public async Task<List<string>> ExtractSlidePreviewsAsync(string pdfPath, string outputDir,
        IProgress<(int current, int total)>? progress = null, CancellationToken ct = default)
    {
        return await ExtractImagesInternalAsync(pdfPath, outputDir, PreviewPageDimensions, progress, ct);
    }

    /// <summary>
    /// Gets the total page count of a PDF.
    /// </summary>
    public async Task<int> GetPageCountAsync(string pdfPath)
    {
        await _docnetLock.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                var library = DocLib.Instance;
                using var docReader = library.GetDocReader(pdfPath, DefaultPageDimensions);
                return docReader.GetPageCount();
            });
        }
        finally
        {
            _docnetLock.Release();
        }
    }

    /// <summary>
    /// Synchronous page count (thread-safe Docnet access).
    /// </summary>
    public int GetPageCount(string pdfPath)
    {
        _docnetLock.Wait();
        try
        {
            using var library = DocLib.Instance;
            using var docReader = library.GetDocReader(pdfPath, DefaultPageDimensions);
            return docReader.GetPageCount();
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
                        {
                            GC.Collect(1, GCCollectionMode.Optimized);
                        }

                        var imagePath = Path.Combine(outputDir, $"slide_{i + 1:D2}.png");
                        if (TryExtractPageWithRetry(docReader, i, imagePath))
                        {
                            savedByPage[i] = imagePath;
                        }
                        else
                        {
                            failedIndices.Add(i);
                        }

                        progress?.Report((i + 1, pageCount));
                    }
                }

                // Recovery pass for any pages that failed on the primary reader:
                // reopen a fresh DocReader, then fall back to preview dimensions if needed.
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

    /// <summary>
    /// Saves raw BGRA pixel data as a PNG using SkiaSharp (bundled with Avalonia).
    /// </summary>
    private static void SaveBgraAsPng(byte[] rawBytes, int width, int height, string outputPath)
    {
        var expectedSize = width * height * 4;
        var copyLength = Math.Min(rawBytes.Length, expectedSize);

        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        var pixelsPtr = bitmap.GetPixels();
        System.Runtime.InteropServices.Marshal.Copy(rawBytes, 0, pixelsPtr, copyLength);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 85);
        using var stream = File.Create(outputPath);
        data.SaveTo(stream);
    }

    /// <summary>
    /// Parses slide number from filenames like 'slide_01.png', 'slide_100.png' for natural numeric sorting.
    /// </summary>
    public static int GetSlideNumber(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var match = System.Text.RegularExpressions.Regex.Match(fileName, @"\d+");
        return match.Success && int.TryParse(match.Value, out var num) ? num : int.MaxValue;
    }

/// <summary>
    /// Checks whether an extracted slide image collection has an unbroken sequence of slides from 1 to expectedTotal.
    /// </summary>
    public static bool IsContinuousSlideSequence(List<string> imagePaths, int expectedTotal)
    {
        if (imagePaths == null || imagePaths.Count != expectedTotal) return false;
        for (int i = 0; i < expectedTotal; i++)
        {
            var slideNum = GetSlideNumber(imagePaths[i]);
            if (slideNum != i + 1) return false;
        }
        return true;
    }
}