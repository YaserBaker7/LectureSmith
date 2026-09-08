using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using LectureSmith.ViewModels;
using LectureSmith.Views;
using LectureSmith.Services;

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
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            try
            {
                var settings = new SettingsService();
                settings.Load();
                var isDark = string.IsNullOrEmpty(settings.Settings.ThemePreference) ||
                             settings.Settings.ThemePreference.Equals("Dark", StringComparison.OrdinalIgnoreCase);
                RequestedThemeVariant = isDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            }
            catch
            {
                RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            }

            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            var splash = new SplashWindow();
            splash.Show();

            _ = LaunchWorkspaceAsync(desktop, splash);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task LaunchWorkspaceAsync(
        IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
    {
        await Task.Delay(150);

        MainWindow? mainWindow = null;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            mainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel()
            };
        });

        await Task.Delay(500);

        if (mainWindow != null)
        {
            desktop.MainWindow = mainWindow;
            mainWindow.WindowState = Avalonia.Controls.WindowState.Maximized;
            mainWindow.Show();
            splash.Close();
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
        }
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}