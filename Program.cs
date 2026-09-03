using Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LectureSmith;

sealed class Program
{
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "LectureSmith_crash.log");

    [STAThread]
    public static void Main(string[] args)
    {
        // Catch ALL unhandled exceptions including native crashes
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            File.AppendAllText(CrashLogPath,
                $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED EXCEPTION (IsTerminating={e.IsTerminating}):\n{ex}\n");
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            File.AppendAllText(CrashLogPath,
                $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNOBSERVED TASK EXCEPTION:\n{e.Exception}\n");
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            File.AppendAllText(CrashLogPath,
                $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] MAIN CATCH:\n{ex}\n");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
