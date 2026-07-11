using CommunityToolkit.Mvvm.ComponentModel;

namespace LectureSmith.Models;

public partial class UploadedFile : ObservableObject
{
    public string FileName { get; }
    public string FilePath { get; }
    public FileType FileType { get; }

    [ObservableProperty]
    private string _chapterInfo = string.Empty;

    public UploadedFile(string filePath, FileType fileType)
    {
        FilePath = filePath;
        FileName = System.IO.Path.GetFileName(filePath);
        FileType = fileType;
    }
}

public enum FileType
{
    Slides,
    Book
}
