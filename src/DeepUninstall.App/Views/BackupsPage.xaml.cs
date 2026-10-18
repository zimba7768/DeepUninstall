using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using DeepUninstall.App.ViewModels;
using DeepUninstall.Core;
using Wpf.Ui.Controls;

namespace DeepUninstall.App.Views;

public partial class BackupsPage : UserControl
{
    public BackupsPage()
    {
        InitializeComponent();
    }

    public void Reload()
    {
        var root = new DirectoryInfo(AppDataPaths.Backups);
        var items = root.Exists
            ? root.GetDirectories().Select(d => new BackupItem(d)).OrderByDescending(b => b.Date).ToList()
            : [];
        BackupsList.ItemsSource = items;
        EmptyPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static BackupItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as BackupItem;

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item && Directory.Exists(item.Directory))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.Directory}\"") { UseShellExecute = true });
    }

    private void ViewReport_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { HasReport: true } item)
            Process.Start(new ProcessStartInfo(item.ReportPath) { UseShellExecute = true });
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;

        var answer = System.Windows.MessageBox.Show(Window.GetWindow(this),
            $"Restore the {item.RegistryFiles} registry backup(s) from the cleanup of \"{item.Program}\" on {item.DateText}?\n\n" +
            "This puts the removed registry keys and values back. Files and folders are not affected; restore those from the Recycle Bin.",
            "Restore registry", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var files = Directory.GetFiles(item.Directory, "*.reg").Order(StringComparer.OrdinalIgnoreCase).ToList();
        var failed = await Task.Run(() => files.Where(f => !Import(f)).Select(Path.GetFileName).ToList());

        ResultBar.Severity = failed.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultBar.Title = failed.Count == 0 ? "Registry restored" : "Restored with problems";
        ResultBar.Message = failed.Count == 0
            ? $"{files.Count} registry backup(s) from \"{item.Program}\" were imported."
            : $"{files.Count - failed.Count} of {files.Count} imported. Could not import: {string.Join(", ", failed)}";
        ResultBar.IsOpen = true;
    }

    private static bool Import(string regFile)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"), $"import \"{regFile}\" /reg:64")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var process = Process.Start(psi);
        if (process is null) return false;
        process.WaitForExit(30_000);
        return process.HasExited && process.ExitCode == 0;
    }
}
