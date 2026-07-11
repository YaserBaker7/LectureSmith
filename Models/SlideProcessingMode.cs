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
        SlideProcessingMode.TextOnly => "Text Only (default)",
        SlideProcessingMode.OcrEnhanced => "OCR Enhanced (lightweight)",
        SlideProcessingMode.VisionAI => "Vision AI (send images to AI)",
        _ => mode.ToString()
    };

    public static string Description(this SlideProcessingMode mode) => mode switch
    {
        SlideProcessingMode.TextOnly => "Extracts text from PDF — fast, no extra tokens",
        SlideProcessingMode.OcrEnhanced => "Runs OCR on slide images for better text extraction — catches code screenshots & embedded text",
        SlideProcessingMode.VisionAI => "Sends slide images directly to the AI — understands diagrams & illustrations (uses more tokens)",
        _ => string.Empty
    };
}
