using Avalonia;
using LectureSmith.Services;

namespace LectureSmith;

sealed class Program
{
    private static string GetCrashLogPath()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            return AppPaths.CrashLogFile;
        }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "LectureSmith_crash.log");
        }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                File.AppendAllText(GetCrashLogPath(),
                    $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED EXCEPTION (IsTerminating={e.IsTerminating}):\n{ex}\n");
            }
            catch { }
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            try
            {
                File.AppendAllText(GetCrashLogPath(),
                    $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNOBSERVED TASK EXCEPTION:\n{e.Exception}\n");
            }
            catch { }
            e.SetObserved();
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            try
            {
                File.AppendAllText(GetCrashLogPath(),
                    $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] MAIN CATCH:\n{ex}\n");
            }
            catch { }
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
