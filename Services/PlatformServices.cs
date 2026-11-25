using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System.Diagnostics;

namespace LectureSmith.Services;

public sealed class AvaloniaClipboardService : IClipboardService
{
    public async Task SetTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
        }
    }
}

public sealed class WindowsFileLauncherService : IFileLauncherService
{
    public void OpenFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            });
        }
        catch { }
    }

    public void OpenFolderAndSelect(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            if (File.Exists(filePath))
            {
                var psi = new ProcessStartInfo { FileName = "explorer.exe" };
                psi.ArgumentList.Add($"/select,{filePath}");
                Process.Start(psi);
            }
            else
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dir,
                        UseShellExecute = true
                    });
                }
            }
        }
        catch { }
    }
}
