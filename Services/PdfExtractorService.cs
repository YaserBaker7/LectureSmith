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
                using var library = DocLib.Instance;
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
                using var library = DocLib.Instance;
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
                var imagePaths = new List<string>();
                Directory.CreateDirectory(outputDir);

                using var library = DocLib.Instance;
                using var docReader = library.GetDocReader(pdfPath, dimensions);
                int pageCount = docReader.GetPageCount();

                for (int i = 0; i < pageCount; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    // Periodically collect to avoid Large Object Heap exhaustion
                    if (i > 0 && i % 10 == 0)
                    {
                        GC.Collect(1, GCCollectionMode.Optimized);
                    }

                    bool pageSaved = false;
                    for (int attempt = 1; attempt <= 3 && !pageSaved; attempt++)
                    {
                        try
                        {
                            using var pageReader = docReader.GetPageReader(i);
                            var rawBytes = pageReader.GetImage();
                            var width = pageReader.GetPageWidth();
                            var height = pageReader.GetPageHeight();

                            if (rawBytes is { Length: > 0 } && width > 0 && height > 0)
                            {
                                var imagePath = Path.Combine(outputDir, $"slide_{i + 1:D2}.png");
                                SaveBgraAsPng(rawBytes, width, height, imagePath);
                                imagePaths.Add(imagePath);
                                pageSaved = true;
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

                    progress?.Report((i + 1, pageCount));
                }

                return imagePaths;
            }, ct);
        }
        finally
        {
            _docnetLock.Release();
        }
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