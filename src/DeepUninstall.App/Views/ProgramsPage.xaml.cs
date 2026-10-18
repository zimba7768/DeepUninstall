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

namespace DeepUninstall.App.Views;

public partial class ProgramsPage : UserControl
{
    private readonly ObservableCollection<AppItem> _apps = [];
    private readonly ICollectionView _view;
    private CancellationTokenSource? _iconLoading;

    public ProgramsPage()
    {
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(_apps);
        _view.Filter = o => o is AppItem item && item.Matches(SearchBox.Text.Trim());
        ApplySort();
        AppsList.ItemsSource = _view;
        Loaded += async (_, _) =>
        {
            if (_apps.Count == 0) await LoadAppsAsync();
        };
    }

    private IReadOnlyList<AppItem> SelectedApps => AppsList.SelectedItems.OfType<AppItem>().ToList();

    public async Task LoadAppsAsync()
    {
        LoadingRing.Visibility = Visibility.Visible;
        EmptyText.Visibility = Visibility.Collapsed;
        _iconLoading?.Cancel();

        var apps = await Task.Run(() => new InstalledAppScanner().Scan());
        _apps.Clear();
        foreach (var app in apps) _apps.Add(new AppItem(app));

        LoadingRing.Visibility = Visibility.Collapsed;
        UpdateStats();
        UpdateDetails();
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

    private void UpdateStats()
    {
        var visible = _view.Cast<object>().Count();
        var totalBytes = _apps.Sum(a => a.SizeBytes);
        var broken = _apps.Count(a => a.IsBroken);
        var parts = new List<string> { $"{_apps.Count} programs installed" };
        if (totalBytes > 0) parts.Add($"{Format.Size(totalBytes)} reported in use");
        if (broken > 0) parts.Add(broken == 1 ? "1 broken entry" : $"{broken} broken entries");
        if (visible != _apps.Count) parts.Add($"{visible} shown");
        StatsText.Text = string.Join("  ·  ", parts);
        EmptyText.Visibility = visible == 0 && _apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDetails()
    {
        var selected = SelectedApps;
        NoSelectionPanel.Visibility = selected.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SinglePanel.Visibility = selected.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
        MultiPanel.Visibility = selected.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        if (selected.Count == 1)
        {
            var item = selected[0];
            SinglePanel.DataContext = item;
            SizeValue.Text = item.SizeText.Length > 0 ? item.SizeText : "Not reported";
            DateValue.Text = item.InstallDateText.Length > 0 ? item.InstallDateText : "Not reported";
            LocationValue.Text = item.HasLocation ? item.Location : "Not recorded by the installer";
            OpenFolderButton.Visibility = item.HasLocation && Directory.Exists(item.Location) ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (selected.Count > 1)
        {
            MultiTitle.Text = $"{selected.Count} programs selected";
            MultiList.ItemsSource = selected;
            UninstallManyButton.Content = $"Uninstall {selected.Count} programs";
        }
    }

    private void ApplySort()
    {
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            switch (SortBox?.SelectedIndex ?? 0)
            {
                case 1:
                    _view.SortDescriptions.Add(new SortDescription(nameof(AppItem.SizeBytes), ListSortDirection.Descending));
                    break;
                case 2:
                    _view.SortDescriptions.Add(new SortDescription(nameof(AppItem.InstallDate), ListSortDirection.Descending));
                    break;
                case 3:
                    _view.SortDescriptions.Add(new SortDescription(nameof(AppItem.Publisher), ListSortDirection.Ascending));
                    break;
            }
            _view.SortDescriptions.Add(new SortDescription(nameof(AppItem.Name), ListSortDirection.Ascending));
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _view.Refresh();
        UpdateStats();
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_view is not null) ApplySort();
    }

    private void AppsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetails();

    private void AppsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(AppsList, source) is ListViewItem)
            Uninstall_Click(sender, e);
    }

    private void AppsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) Uninstall_Click(sender, e);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAppsAsync();

    private async void Uninstall_Click(object sender, RoutedEventArgs e) => await RunWizardAsync(SelectedApps, force: false);

    private async void Force_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedApps;
        if (selected.Count == 1) await RunWizardAsync(selected, force: true);
    }

    private async Task RunWizardAsync(IReadOnlyList<AppItem> items, bool force)
    {
        if (items.Count == 0) return;
        var owner = Window.GetWindow(this);

        if (items.Count > 1)
        {
            var names = string.Join("\n", items.Take(12).Select(i => "  •  " + i.Name)) + (items.Count > 12 ? $"\n  …and {items.Count - 12} more" : "");
            var answer = System.Windows.MessageBox.Show(owner, $"Uninstall these {items.Count} programs one after another?\n\n{names}",
                "DeepUninstall", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }

        var changed = false;
        for (var i = 0; i < items.Count; i++)
        {
            var wizard = new UninstallWindow(items[i].App, force, items[i].Icon, items.Count > 1 ? (i + 1, items.Count) : null) { Owner = owner };
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
            System.Windows.MessageBox.Show(Window.GetWindow(this), "This program did not record an install folder.", "DeepUninstall",
                MessageBoxButton.OK, MessageBoxImage.Information);
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
}
