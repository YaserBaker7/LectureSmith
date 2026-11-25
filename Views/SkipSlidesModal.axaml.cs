using Avalonia.Controls;
using Avalonia.Input;
using LectureSmith.ViewModels;

namespace LectureSmith.Views;

public partial class SkipSlidesModal : UserControl
{
    public SkipSlidesModal()
    {
        InitializeComponent();
    }

    private void SlideCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: SkippedSlideItemViewModel slide } &&
            DataContext is SkipSlidesViewModel vm)
        {
            vm.ToggleSlide(slide);
            e.Handled = true;
        }
    }
}
