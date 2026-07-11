using Docnet.Core;
using Docnet.Core.Models;
using SkiaSharp;

namespace LectureSmith.Services;

public class PdfExtractorService
{
    private static readonly PageDimensions DefaultPageDimensions = new(1080, 1920);

    /// <summary>
    /// Extracts text from all pages of a PDF file.
    /// </summary>
    public async Task<List<string>> ExtractTextAsync(string pdfPath, int? startPage = null, int? endPage = null)
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

    /// <summary>
    /// Extracts each page of a PDF as a PNG image. Returns list of saved image paths.
    /// </summary>
    public async Task<List<string>> ExtractSlideImagesAsync(string pdfPath, string outputDir,
        IProgress<(int current, int total)>? progress = null)
    {
        return await Task.Run(() =>
        {
            var imagePaths = new List<string>();
            Directory.CreateDirectory(outputDir);

            using var library = DocLib.Instance;
            using var docReader = library.GetDocReader(pdfPath, DefaultPageDimensions);
            int pageCount = docReader.GetPageCount();

            for (int i = 0; i < pageCount; i++)
            {
                using var pageReader = docReader.GetPageReader(i);
                var rawBytes = pageReader.GetImage();
                var width = pageReader.GetPageWidth();
                var height = pageReader.GetPageHeight();

                if (rawBytes is { Length: > 0 })
                {
                    var imagePath = Path.Combine(outputDir, $"slide_{i + 1:D2}.png");
                    SaveBgraAsPng(rawBytes, width, height, imagePath);
                    imagePaths.Add(imagePath);
                }

                progress?.Report((i + 1, pageCount));
            }

            return imagePaths;
        });
    }

    /// <summary>
    /// Gets the total page count of a PDF.
    /// </summary>
    public int GetPageCount(string pdfPath)
    {
        using var library = DocLib.Instance;
        using var docReader = library.GetDocReader(pdfPath, DefaultPageDimensions);
        return docReader.GetPageCount();
    }

    /// <summary>
    /// Saves raw BGRA pixel data as a PNG using SkiaSharp (bundled with Avalonia).
    /// </summary>
    private static void SaveBgraAsPng(byte[] rawBytes, int width, int height, string outputPath)
    {
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        var pixelsPtr = bitmap.GetPixels();
        System.Runtime.InteropServices.Marshal.Copy(rawBytes, 0, pixelsPtr, Math.Min(rawBytes.Length, width * height * 4));

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var stream = File.OpenWrite(outputPath);
        data.SaveTo(stream);
    }
}
