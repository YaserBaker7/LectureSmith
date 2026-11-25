using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LectureSmith.Models;
using LectureSmith.Services;
using System.Collections.ObjectModel;

namespace LectureSmith.ViewModels;

public partial class SkipSlidesViewModel : ViewModelBase, IDisposable
{
    private readonly ISlideCacheService _slideCache;
    private readonly HashSet<int> _skippedSlideNumbers = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _skippedSlidesDisplayText = string.Empty;

    public ObservableCollection<SkippedSlideItemViewModel> Thumbnails { get; } = [];

    public IReadOnlySet<int> SkippedNumbers => _skippedSlideNumbers;

    public SkipSlidesViewModel(ISlideCacheService slideCache)
    {
        _slideCache = slideCache;
    }

    public async Task OpenAsync(string pdfPath, Action<string, bool> setStatus)
    {
        try
        {
            IsLoading = true;
            setStatus("Loading slide previews...", false);

            var previewPaths = await _slideCache.GetOrExtractSlidesAsync(pdfPath, previewQuality: true);
            if (previewPaths.Count == 0)
            {
                setStatus("✗ Could not extract any slide previews from this PDF.", true);
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ClearThumbnails();
                for (int i = 0; i < previewPaths.Count; i++)
                {
                    Thumbnails.Add(new SkippedSlideItemViewModel(i + 1, previewPaths[i])
                    {
                        IsSkipped = _skippedSlideNumbers.Contains(i + 1)
                    });
                }

                IsOpen = true;
                setStatus(string.Empty, false);
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() =>
                setStatus($"✗ Failed to load slide previews: {ex.Message}", true));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public void ToggleSlide(SkippedSlideItemViewModel? slide)
    {
        if (slide == null) return;
        slide.IsSkipped = !slide.IsSkipped;
        if (slide.IsSkipped)
            _skippedSlideNumbers.Add(slide.SlideNumber);
        else
            _skippedSlideNumbers.Remove(slide.SlideNumber);

        UpdateDisplayText();
    }

    [RelayCommand]
    private void Close()
    {
        IsOpen = false;
        UpdateDisplayText();
        // Release decoded thumbnail bitmaps immediately once the dialog closes
        ClearThumbnails();
    }

    public void Reset()
    {
        _skippedSlideNumbers.Clear();
        IsOpen = false;
        UpdateDisplayText();
        ClearThumbnails();
    }

    private void UpdateDisplayText()
    {
        if (_skippedSlideNumbers.Count == 0)
        {
            SkippedSlidesDisplayText = string.Empty;
        }
        else
        {
            var sorted = _skippedSlideNumbers.OrderBy(n => n).ToList();
            SkippedSlidesDisplayText = $"Skipping {sorted.Count} slide{(sorted.Count == 1 ? "" : "s")}: {string.Join(", ", sorted)}";
        }
    }

    private void ClearThumbnails()
    {
        foreach (var item in Thumbnails)
            item.Dispose();
        Thumbnails.Clear();
    }

    public void Dispose()
    {
        ClearThumbnails();
        GC.SuppressFinalize(this);
    }
}
