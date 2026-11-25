using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using LectureSmith.ViewModels;

namespace LectureSmith.Views;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView()
    {
        InitializeComponent();

        var slidesZone = this.FindControl<Border>("SlidesDropZone");
        if (slidesZone != null)
        {
            slidesZone.AddHandler(DragDrop.DropEvent, SlidesDropZone_Drop);
            slidesZone.AddHandler(DragDrop.DragOverEvent, DragOver);
        }

        var booksZone = this.FindControl<Border>("BooksDropZone");
        if (booksZone != null)
        {
            booksZone.AddHandler(DragDrop.DropEvent, BooksDropZone_Drop);
            booksZone.AddHandler(DragDrop.DragOverEvent, DragOver);
        }

        // Prevent mouse-wheel over closed ComboBoxes from accidentally changing dropdown values while scrolling
        AddHandler(InputElement.PointerWheelChangedEvent, (sender, e) =>
        {
            if (e.Source is Visual visual)
            {
                if (visual.FindAncestorOfType<Popup>() != null ||
                    visual.FindAncestorOfType<OverlayPopupHost>() != null)
                {
                    return;
                }

                var cb = (visual as ComboBox) ?? visual.FindAncestorOfType<ComboBox>() ?? visual.FindLogicalAncestorOfType<ComboBox>();
                if (cb != null && !cb.IsDropDownOpen)
                {
                    var scroller = visual.FindAncestorOfType<ScrollViewer>() ?? this.FindControl<ScrollViewer>("LeftPanelScroller");
                    if (scroller != null)
                        scroller.Offset = new Vector(scroller.Offset.X, scroller.Offset.Y - e.Delta.Y * 50);

                    e.Handled = true;
                }
            }
        }, RoutingStrategies.Tunnel);
    }

    private static void DragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        e.DragEffects = e.Data.Contains(DataFormats.Files) || e.Data.Contains("Files")
            ? DragDropEffects.Copy
            : DragDropEffects.None;
#pragma warning restore CS0618
    }

    private void SlidesDropZone_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm) return;
#pragma warning disable CS0618
        if (!e.Data.Contains(DataFormats.Files) && !e.Data.Contains("Files")) return;
        var files = e.Data.GetFiles()?.ToList();
#pragma warning restore CS0618

        var localPath = files?.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(localPath))
            vm.HandleSlidesFileDrop(localPath);
    }

    private void BooksDropZone_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not WorkspaceViewModel vm) return;
#pragma warning disable CS0618
        if (!e.Data.Contains(DataFormats.Files) && !e.Data.Contains("Files")) return;
        var files = e.Data.GetFiles()?.ToList();
#pragma warning restore CS0618

        if (files == null) return;
        foreach (var file in files)
        {
            var localPath = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(localPath))
                vm.HandleBookFileDrop(localPath);
        }
    }

    private async void SlidesDropZone_Tapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (DataContext is not WorkspaceViewModel vm) return;
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Lecture Slides PDF",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("PDF Files") { Patterns = ["*.pdf"] }]
            });

            var localPath = files.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(localPath))
                vm.HandleSlidesFileDrop(localPath);
        }
        catch { }
    }

    private async void BooksDropZone_Tapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (DataContext is not WorkspaceViewModel vm) return;
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Reference Book PDFs",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("PDF Files") { Patterns = ["*.pdf"] }]
            });

            foreach (var file in files)
            {
                var localPath = file.TryGetLocalPath();
                if (!string.IsNullOrEmpty(localPath))
                    vm.HandleBookFileDrop(localPath);
            }
        }
        catch { }
    }

    private async void BrowseOutput_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not WorkspaceViewModel vm) return;
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage == null) return;

            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select Output Folder",
                AllowMultiple = false
            });

            var localPath = folders.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrEmpty(localPath))
                vm.OutputPath = localPath;
        }
        catch { }
    }
}
