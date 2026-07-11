using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
}