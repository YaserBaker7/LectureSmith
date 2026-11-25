using LectureSmith.Services;
using Xunit;

namespace LectureSmith.Tests;

public class AppPathsAndExporterTests
{
    [Fact]
    public void SanitizeFileName_HandlesInvalidCharactersAndBlankFallback()
    {
        Assert.Equal("CS101_ Intro _ Basics", AppPaths.SanitizeFileName("CS101: Intro / Basics"));
        Assert.Equal("Lecture", AppPaths.SanitizeFileName("   ", "Lecture"));
    }

    [Fact]
    public void ComputeFileCacheKey_IsDeterministicAndDistinguishesDifferentPaths()
    {
        var key1 = AppPaths.ComputeFileCacheKey(@"C:\CourseA\Lecture1.pdf", "full");
        var key1Again = AppPaths.ComputeFileCacheKey(@"C:\CourseA\Lecture1.pdf", "full");
        var key2 = AppPaths.ComputeFileCacheKey(@"C:\CourseB\Lecture1.pdf", "full");
        var keyPreview = AppPaths.ComputeFileCacheKey(@"C:\CourseA\Lecture1.pdf", "preview");

        Assert.Equal(key1, key1Again);
        Assert.NotEqual(key1, key2);
        Assert.NotEqual(key1, keyPreview);
    }

    [Fact]
    public void DpapiSecretProtector_RoundTripsSecret()
    {
        var protector = new DpapiSecretProtector();
        var secret = "AIzaSy-test-key-12345";
        var protectedText = protector.Protect(secret);

        Assert.NotEqual(secret, protectedText);
        Assert.Equal(secret, protector.Unprotect(protectedText));
    }

    [Fact]
    public void NormalizeObsidianImages_ConvertsWikiLinksToMarkdownImages()
    {
        var input = "See below:\n![[slides/slide_01.png]]\nDone.";
        var normalized = OutputExporterService.NormalizeObsidianImages(input);
        Assert.Contains("![Slide](slides/slide_01.png)", normalized);
    }

    [Fact]
    public void ResolveTesseractNativeFolders_FindsSingleFileExtractedFolder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ls_ocr_test_{Guid.NewGuid():N}");
        var bundleDir = Path.Combine(tempRoot, "bundle");
        var archDir = Path.Combine(bundleDir, "x64");
        Directory.CreateDirectory(archDir);
        File.WriteAllBytes(Path.Combine(archDir, "tesseract50.dll"), [0]);

        try
        {
            var fakeDesktop = Path.Combine(tempRoot, "Desktop");
            var searchDirs = $"{fakeDesktop};{bundleDir}";
            var (parent, native) = OcrService.ResolveTesseractNativeFolders(fakeDesktop, searchDirs, "x64");

            Assert.Equal(bundleDir, parent);
            Assert.Equal(archDir, native);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
