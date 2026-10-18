using System.Windows;
using System.Windows.Threading;

namespace DeepUninstall.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nPlease report this at the project's GitHub issues page.",
            "DeepUninstall", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
