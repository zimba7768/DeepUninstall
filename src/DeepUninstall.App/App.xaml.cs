using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace DeepUninstall.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        ApplicationThemeManager.Apply(SystemUsesDarkTheme() ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, true);
        base.OnStartup(e);
    }

    /// <summary>Follows Settings > Personalization > Colors > "Choose your app mode".</summary>
    internal static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }

    /// <summary>Applies Mica and keeps the window in sync with the Windows light/dark setting.</summary>
    internal static void ApplyChrome(Window window) =>
        window.Loaded += (_, _) => SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, updateAccents: true);

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        System.Windows.MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nPlease report this at the project's GitHub issues page.",
            "DeepUninstall", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
