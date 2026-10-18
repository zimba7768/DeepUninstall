using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeepUninstall.Core;

namespace DeepUninstall.App.Views;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = $"Version {MainWindow.AppVersion}";
    }

    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private void GitHub_Click(object sender, RoutedEventArgs e) => Open(MainWindow.ProjectUrl);

    private void Issues_Click(object sender, RoutedEventArgs e) => Open(MainWindow.ProjectUrl + "/issues/new/choose");

    private void Backups_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppDataPaths.Backups);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppDataPaths.Backups}\"") { UseShellExecute = true });
    }
}
