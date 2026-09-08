using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using LectureSmith.Models;
using LectureSmith.ViewModels;

namespace LectureSmith.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Wire up drag-and-drop for slides
        var slidesZone = this.FindControl<Border>("SlidesDropZone");
        if (slidesZone != null)
        {
            slidesZone.AddHandler(DragDrop.DropEvent, SlidesDropZone_Drop);
            slidesZone.AddHandler(DragDrop.DragOverEvent, DragOver);
        }

        // Wire up drag-and-drop for books
        var booksZone = this.FindControl<Border>("BooksDropZone");
        if (booksZone != null)
        {
            booksZone.AddHandler(DragDrop.DropEvent, BooksDropZone_Drop);
            booksZone.AddHandler(DragDrop.DragOverEvent, DragOver);
        }

        // Intercept pointer wheel events on closed ComboBoxes so they don't accidentally cycle items when scrolling the page.
        // When a ComboBox dropdown IS open, allow normal scrolling inside the dropdown list.
        this.AddHandler(InputElement.PointerWheelChangedEvent, (sender, e) =>
        {
            if (e.Source is Avalonia.Visual visual)
            {
                // If scrolling inside an open popup / dropdown list, do not intercept
                if (visual.FindAncestorOfType<Popup>() != null ||
                    visual.FindAncestorOfType<OverlayPopupHost>() != null)
                {
                    return;
                }

                // Check if the source or any ancestor is a ComboBox
                var cb = (visual as ComboBox) ?? visual.FindAncestorOfType<ComboBox>() ?? visual.FindLogicalAncestorOfType<ComboBox>();
                if (cb != null)
                {
                    if (cb.IsDropDownOpen)
                    {
                        // Dropdown is open: let the dropdown menu scroll naturally
                        return;
                    }

                    // Dropdown is closed: prevent ComboBox from cycling items and scroll the parent scroll viewer instead
                    var scroller = visual.FindAncestorOfType<ScrollViewer>() ?? this.FindControl<ScrollViewer>("LeftPanelScroller");
                    if (scroller != null)
                    {
                        var delta = e.Delta.Y;
                        scroller.Offset = new Avalonia.Vector(scroller.Offset.X, scroller.Offset.Y - delta * 50);
                    }
                    e.Handled = true;
                }
            }
        }, RoutingStrategies.Tunnel);

        // Auto-scroll chat to bottom when messages are added
        DataContextChanged += (s, e) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ChatMessages.CollectionChanged += (sender, args) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        var scroller = this.FindControl<ScrollViewer>("ChatScroller");
                        scroller?.ScrollToEnd();
                    });
                };
            }
        };

        // Dispose ViewModel resources (thumbnails, native handles) upon application exit
        Closed += (s, e) => (DataContext as IDisposable)?.Dispose();
    }

    private void DragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        e.DragEffects = e.Data.Contains(Avalonia.Input.DataFormats.Files) || e.Data.Contains("Files")
            ? DragDropEffects.Copy
            : DragDropEffects.None;
#pragma warning restore CS0618
    }

    private void SlidesDropZone_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
#pragma warning disable CS0618
        if (!e.Data.Contains(Avalonia.Input.DataFormats.Files) && !e.Data.Contains("Files")) return;

        var files = e.Data.GetFiles()?.ToList();
#pragma warning restore CS0618

        if (files != null && files.Count > 0)
        {
            var path = files[0].Path.LocalPath;
            vm.HandleSlidesFileDrop(path);
        }
    }

    private void BooksDropZone_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
#pragma warning disable CS0618
        if (!e.Data.Contains(Avalonia.Input.DataFormats.Files) && !e.Data.Contains("Files")) return;

        var files = e.Data.GetFiles()?.ToList();
#pragma warning restore CS0618
        if (files != null)
        {
            foreach (var file in files)
            {
                vm.HandleBookFileDrop(file.Path.LocalPath);
            }
        }
    }

    private async void SlidesDropZone_Tapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Lecture Slides PDF",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PDF Files") { Patterns = ["*.pdf"] }]
        });

        if (files.Count > 0)
        {
            vm.HandleSlidesFileDrop(files[0].Path.LocalPath);
        }
    }

    private async void BooksDropZone_Tapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Reference Book PDFs",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("PDF Files") { Patterns = ["*.pdf"] }]
        });

        foreach (var file in files)
        {
            vm.HandleBookFileDrop(file.Path.LocalPath);
        }
    }

    private async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Output Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            vm.OutputPath = folders[0].Path.LocalPath;
        }
    }

    private void FollowUpInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (DataContext is MainWindowViewModel vm)
            {
                if (vm.SendFollowUpCommand.CanExecute(null))
                {
                    vm.SendFollowUpCommand.Execute(null);
                    e.Handled = true;
                }
            }
        }
    }

    private void SlideCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control control && control.DataContext is SkippedSlideInfo slide && DataContext is MainWindowViewModel vm)
        {
            vm.ToggleSlideSkip(slide);
            e.Handled = true;
        }
    }
}