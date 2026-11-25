using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LectureSmith.ViewModels;

public partial class SkippedSlideItemViewModel : ObservableObject, IDisposable
{
    public int SlideNumber { get; }
    public string ImagePath { get; }
    public Bitmap? SlideImage { get; private set; }

    [ObservableProperty]
    private bool _isSkipped;

    public SkippedSlideItemViewModel(int slideNumber, string imagePath)
    {
        SlideNumber = slideNumber;
        ImagePath = imagePath;
        try
        {
            using var fs = File.OpenRead(imagePath);
            SlideImage = Bitmap.DecodeToWidth(fs, 440);
        }
        catch
        {
            SlideImage = null;
        }
    }

    public void Dispose()
    {
        SlideImage?.Dispose();
        SlideImage = null;
        GC.SuppressFinalize(this);
    }
}
