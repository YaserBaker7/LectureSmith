namespace LectureSmith.Models;

public enum SlideProcessingMode
{
    TextOnly,
    OcrEnhanced,
    VisionAI
}

public static class SlideProcessingModeExtensions
{
    public static string DisplayName(this SlideProcessingMode mode) => mode switch
    {
        SlideProcessingMode.TextOnly => "Text Only (Fast)",
        SlideProcessingMode.OcrEnhanced => "OCR Enhanced (Local)",
        SlideProcessingMode.VisionAI => "Vision AI (Images)",
        _ => mode.ToString()
    };

    public static string Description(this SlideProcessingMode mode) => mode switch
    {
        SlideProcessingMode.TextOnly => "Extracts text from PDF — fast, minimal tokens",
        SlideProcessingMode.OcrEnhanced => "Runs local Tesseract OCR on slide images — catches screenshots & embedded text",
        SlideProcessingMode.VisionAI => "Sends slide images directly to Gemini — understands diagrams & charts (uses more tokens)",
        _ => string.Empty
    };
}
