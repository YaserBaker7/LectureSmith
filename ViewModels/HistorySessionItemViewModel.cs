using CommunityToolkit.Mvvm.ComponentModel;
using LectureSmith.Models;

namespace LectureSmith.ViewModels;

/// <summary>
/// Observable presentation wrapper around <see cref="HistorySession"/> so chat counts and file status update live in UI.
/// </summary>
public partial class HistorySessionItemViewModel : ObservableObject
{
    public HistorySession Model { get; }

    public HistorySessionItemViewModel(HistorySession model)
    {
        Model = model;
    }

    public string Id => Model.Id;
    public string FormattedDate => Model.FormattedDate;
    public string DisplayTitle => Model.DisplayTitle;
    public string DisplayCourse => Model.DisplayCourse;
    public string OutputFilePath => Model.OutputFilePath;
    public OutputFormat Format => Model.Format;
    public string MarkdownContent => Model.MarkdownContent;
    public string ChatCountText => Model.ChatCountText;
    public bool FileExists => !string.IsNullOrWhiteSpace(Model.OutputFilePath) && File.Exists(Model.OutputFilePath);

    public void RefreshMetadata()
    {
        OnPropertyChanged(nameof(ChatCountText));
        OnPropertyChanged(nameof(FileExists));
    }
}
