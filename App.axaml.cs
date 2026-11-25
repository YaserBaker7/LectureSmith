using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using LectureSmith.Services;
using LectureSmith.ViewModels;
using LectureSmith.Views;

namespace LectureSmith;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            var settingsService = new SettingsService();
            settingsService.Load();

            var isDark = string.IsNullOrEmpty(settingsService.Settings.ThemePreference) ||
                         settingsService.Settings.ThemePreference.Equals("Dark", StringComparison.OrdinalIgnoreCase);
            RequestedThemeVariant = isDark ? ThemeVariant.Dark : ThemeVariant.Light;

            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var splash = new SplashWindow();
            splash.Show();

            _ = LaunchWorkspaceAsync(desktop, splash, settingsService);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task LaunchWorkspaceAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash,
        ISettingsService settingsService)
    {
        try
        {
            await Task.Yield();

            MainWindow? mainWindow = null;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                mainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(settingsService)
                };
            });

            if (mainWindow != null)
            {
                desktop.MainWindow = mainWindow;
                mainWindow.WindowState = WindowState.Maximized;
                mainWindow.Show();
                splash.Close();
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            }
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                File.AppendAllText(AppPaths.CrashLogFile,
                    $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] STARTUP FAILURE:\n{ex}\n");
            }
            catch { }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                splash.Close();
                desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
                desktop.Shutdown(1);
            });
        }
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
            BindingPlugins.DataValidators.Remove(plugin);
    }
}
