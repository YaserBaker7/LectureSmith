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
    public async Task<List<string>> ExtractTextAsync(string pdfPath, int? startPage = null, int? endPage = null)
    {
        await _docnetLock.WaitAsync();
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
                    using var pageReader = docReader.GetPageReader(i);
                    var text = pageReader.GetText();
                    pages.Add(text ?? string.Empty);
                }

                return pages;
            });
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
        IProgress<(int current, int total)>? progress = null)
    {
        return await ExtractImagesInternalAsync(pdfPath, outputDir, DefaultPageDimensions, progress);
    }

    /// <summary>
    /// Extracts each page of a PDF as a PNG image at preview resolution (half size).
    /// Used for the slide-skip popup to reduce memory usage and speed up loading.
    /// </summary>
    public async Task<List<string>> ExtractSlidePreviewsAsync(string pdfPath, string outputDir,
        IProgress<(int current, int total)>? progress = null)
    {
        return await ExtractImagesInternalAsync(pdfPath, outputDir, PreviewPageDimensions, progress);
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
        PageDimensions dimensions, IProgress<(int current, int total)>? progress)
    {
        await _docnetLock.WaitAsync();
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
                        }
                    }
                    catch
                    {
                        // Skip pages that fail to render — don't crash the whole extraction
                    }

                    progress?.Report((i + 1, pageCount));
                }

                return imagePaths;
            });
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
}
