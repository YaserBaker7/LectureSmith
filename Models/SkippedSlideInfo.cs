using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LectureSmith.Models;

/// <summary>
/// Represents a slide in the skip-slides popup with its image and toggle state.
/// </summary>
public partial class SkippedSlideInfo : ObservableObject, IDisposable
{
    public int SlideNumber { get; }
    public string ImagePath { get; }
    public Bitmap? SlideImage { get; }

    [ObservableProperty]
    private bool _isSkipped;

    public SkippedSlideInfo(int slideNumber, string imagePath)
    {
        SlideNumber = slideNumber;
        ImagePath = imagePath;
        try
        {
            SlideImage = new Bitmap(imagePath);
        }
        catch
        {
            SlideImage = null;
        }
    }

    public void Dispose()
    {
        SlideImage?.Dispose();
        GC.SuppressFinalize(this);
    }
}
