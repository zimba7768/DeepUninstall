using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DeepUninstall.App.Services;
using DeepUninstall.App.ViewModels;
using DeepUninstall.Core.Discovery;
using DeepUninstall.Core.Uninstall;

namespace DeepUninstall.App;

public partial class MainWindow : Window
{
    public const string ProjectUrl = "https://github.com/zimba7768/DeepUninstall";

    private readonly ObservableCollection<AppItem> _apps = [];
    private readonly ICollectionView _view;
    private CancellationTokenSource? _iconLoading;

    public MainWindow()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_apps);
        _view.Filter = o => o is AppItem item && item.Matches(SearchBox.Text.Trim());
        _view.SortDescriptions.Add(new SortDescription(nameof(AppItem.Name), ListSortDirection.Ascending));
        AppsGrid.ItemsSource = _view;
        Loaded += async (_, _) => await LoadAppsAsync();
    }

    private IReadOnlyList<AppItem> SelectedApps => AppsGrid.SelectedItems.OfType<AppItem>().ToList();

    private async Task LoadAppsAsync()
    {
        LoadingText.Visibility = Visibility.Visible;
        StatusText.Text = "";
        _iconLoading?.Cancel();

        var apps = await Task.Run(() => new InstalledAppScanner().Scan());
        _apps.Clear();
        foreach (var app in apps) _apps.Add(new AppItem(app));

        LoadingText.Visibility = Visibility.Collapsed;
        UpdateStatus();
        _ = LoadIconsAsync();
    }

    private async Task LoadIconsAsync()
    {
        var cts = new CancellationTokenSource();
        _iconLoading = cts;
        var items = _apps.ToList();
        await Task.Run(() =>
        {
            foreach (var item in items)
            {
                if (cts.IsCancellationRequested) return;
                var icon = IconLoader.Load(item.App);
                if (icon is not null) Dispatcher.BeginInvoke(() => item.Icon = icon);
            }
        });
    }

    private void UpdateStatus()
    {
        var visible = _view.Cast<object>().Count();
        var selected = AppsGrid.SelectedItems.Count;
        StatusText.Text = $"{visible} of {_apps.Count} programs" + (selected > 1 ? $"  ·  {selected} selected" : "");

        UninstallButton.IsEnabled = selected > 0;
        ForceButton.IsEnabled = selected == 1;
        UninstallButtonText.Text = selected > 1 ? $"Uninstall {selected}" : "Uninstall";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        UpdateStatus();
    }

    private void AppsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStatus();

    private void AppsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(AppsGrid, source) is DataGridRow)
            Uninstall_Click(sender, e);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAppsAsync();

    private async void Uninstall_Click(object sender, RoutedEventArgs e) => await RunWizardAsync(SelectedApps, force: false);

    private async void Force_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedApps;
        if (selected.Count != 1) return;
        await RunWizardAsync(selected, force: true);
    }

    private async Task RunWizardAsync(IReadOnlyList<AppItem> items, bool force)
    {
        if (items.Count == 0) return;

        if (items.Count > 1)
        {
            var names = string.Join("\n", items.Take(12).Select(i => "  •  " + i.Name)) + (items.Count > 12 ? $"\n  …and {items.Count - 12} more" : "");
            var answer = MessageBox.Show(this, $"Uninstall these {items.Count} programs one after another?\n\n{names}",
                "DeepUninstall", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }

        var changed = false;
        for (var i = 0; i < items.Count; i++)
        {
            var wizard = new UninstallWindow(items[i].App, force, items[i].Icon, items.Count > 1 ? (i + 1, items.Count) : null) { Owner = this };
            wizard.ShowDialog();
            changed |= wizard.MadeChanges;
            if (wizard.StopBatch) break;
        }

        if (changed) await LoadAppsAsync();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var app = SelectedApps.FirstOrDefault()?.App;
        var folder = app?.InstallLocation;
        if (string.IsNullOrEmpty(folder))
        {
            var exe = CommandLineParser.Parse(app?.UninstallString)?.FileName;
            folder = exe is null ? null : Path.GetDirectoryName(exe);
        }

        if (folder is not null && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        else
            MessageBox.Show(this, "This program did not record an install folder.", "DeepUninstall", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CopyName_Click(object sender, RoutedEventArgs e)
    {
        var app = SelectedApps.FirstOrDefault();
        if (app is not null) Clipboard.SetText(app.Name);
    }

    private void CopyCommand_Click(object sender, RoutedEventArgs e)
    {
        var app = SelectedApps.FirstOrDefault()?.App;
        var command = app is null ? null : UninstallCommandBuilder.Build(app, silent: false)?.ToCommandLine();
        if (command is not null) Clipboard.SetText(command);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        var answer = MessageBox.Show(this,
            $"DeepUninstall {version}\n\nA free, open-source uninstaller that removes programs completely and safely.\n" +
            "Released under the MIT License.\n\nOpen the project page on GitHub?",
            "About DeepUninstall", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true });
    }
}
