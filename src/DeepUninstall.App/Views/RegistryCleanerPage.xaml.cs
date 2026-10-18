using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using DeepUninstall.App.Services;
using DeepUninstall.App.ViewModels;
using DeepUninstall.Core.Models;
using DeepUninstall.Core.RegistryCleaning;
using DeepUninstall.Core.Removal;
using Wpf.Ui.Controls;

namespace DeepUninstall.App.Views;

public partial class RegistryCleanerPage : UserControl
{
    private readonly ObservableCollection<RegistryIssueItem> _issues = [];
    private readonly List<CategoryChoice> _choices = RegistryCleaner.Categories.Select(c => new CategoryChoice(c)).ToList();
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _cts;

    // The current scan, for the report.
    private string _scanName = "";
    private IReadOnlyList<RegistryIssueCategory> _categories = [];
    private IReadOnlyList<RegistryIssue> _found = [];
    private RemovalReport? _removal;

    public RegistryCleanerPage()
    {
        InitializeComponent();
        ChoicesList.ItemsSource = _choices;

        var view = CollectionViewSource.GetDefaultView(_issues);
        view.SortDescriptions.Add(new SortDescription(nameof(RegistryIssueItem.CategoryOrder), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(RegistryIssueItem.Location), ListSortDirection.Ascending));
        ResultsGrid.ItemsSource = view;

        string Checks(ScanLevel level) => $"{RegistryCleaner.CategoriesFor(level).Count} checks";
        QuickChecksText.Text = $"{Checks(ScanLevel.Safe)}  ·  no review needed";
        SafeChecksText.Text = Checks(ScanLevel.Safe);
        ModerateChecksText.Text = Checks(ScanLevel.Moderate);
        AdvancedChecksText.Text = Checks(ScanLevel.Advanced);
    }

    private void Show(FrameworkElement panel)
    {
        foreach (var p in new FrameworkElement[] { HomePanel, ManagedPanel, WorkingPanel, ResultsPanel, DonePanel })
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void QuickClean_Click(object sender, RoutedEventArgs e) =>
        await ScanAsync("Quick Clean", RegistryCleaner.CategoriesFor(ScanLevel.Safe), cleanAutomatically: true);

    private async void Level_Click(object sender, RoutedEventArgs e)
    {
        var level = Enum.Parse<ScanLevel>((string)((FrameworkElement)sender).Tag);
        await ScanAsync($"{level} Scan", RegistryCleaner.CategoriesFor(level), cleanAutomatically: false);
    }

    private void Managed_Click(object sender, RoutedEventArgs e) => Show(ManagedPanel);

    private async void ScanSelected_Click(object sender, RoutedEventArgs e)
    {
        var categories = _choices.Where(c => c.IsChecked).Select(c => c.Info.Category).ToList();
        if (categories.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "Choose at least one check.", "Managed Scan", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await ScanAsync("Managed Scan", categories, cleanAutomatically: false);
    }

    private async Task ScanAsync(string name, IReadOnlyList<RegistryIssueCategory> categories, bool cleanAutomatically)
    {
        _scanName = name;
        _categories = categories;
        _removal = null;
        _log.Clear();
        _issues.Clear();
        Log($"{name}: {categories.Count} check(s)");

        WorkingTitle.Text = $"{name}: scanning the registry…";
        WorkingStatus.Text = "";
        CancelButton.Visibility = Visibility.Visible;
        Show(WorkingPanel);

        _cts = new CancellationTokenSource();
        var progress = OnUiThread(m =>
        {
            Log(m);
            if (!m.StartsWith(' ')) WorkingStatus.Text = m;
        });

        try
        {
            var token = _cts.Token;
            _found = await Task.Run(() => new RegistryCleaner().Scan(categories, progress, token), token);
        }
        catch (OperationCanceledException)
        {
            Show(HomePanel);
            return;
        }
        Log($"{_found.Count} issue(s) found.");

        if (_found.Count == 0)
        {
            ShowDone(true, "No problems found", $"{name} checked {categories.Count} kind(s) of registry entries and found nothing to clean.", cleaned: false);
            return;
        }

        if (cleanAutomatically)
        {
            var safe = _found.Where(i => i.Item.Confidence == Confidence.High).Select(i => i.Item).ToList();
            await CleanAsync(safe);
            return;
        }

        foreach (var issue in _found)
        {
            var item = new RegistryIssueItem(issue);
            item.PropertyChanged += (_, _) => UpdateSelection();
            _issues.Add(item);
        }
        ResultsTitle.Text = _found.Count == 1 ? "1 issue found" : $"{_found.Count} issues found";
        UpdateSelection();
        Show(ResultsPanel);
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var selected = _issues.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        var risky = selected.Count(i => i.Confidence != Confidence.High);
        if (risky > 0)
        {
            var answer = MessageBox.Show(Window.GetWindow(this),
                $"{risky} of the selected entries come from Advanced checks and are less certain.\n\nThey are backed up first. Remove them anyway?",
                "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        await CleanAsync(selected.Select(i => i.Leftover).ToList());
    }

    private async Task CleanAsync(IReadOnlyList<Leftover> items)
    {
        WorkingTitle.Text = $"Cleaning {items.Count} entr{(items.Count == 1 ? "y" : "ies")}…";
        WorkingStatus.Text = "Backing up and removing";
        CancelButton.Visibility = Visibility.Collapsed;
        Show(WorkingPanel);

        var progress = OnUiThread(Log);
        var label = $"Registry cleanup ({_scanName})";
        _removal = await Task.Run(() => new LeftoverRemover().Remove(items,
            new RemovalOptions { Label = label, CloseRunningProcesses = false, UseRecycleBin = true }, progress));
        ReportExporter.SaveToBackupFolder(BuildReport());

        var text = $"{_removal.Removed} of {items.Count} registry entr{(items.Count == 1 ? "y was" : "ies were")} removed.";
        if (_removal.Failed > 0) text += $" {_removal.Failed} could not be removed.";
        var kept = _found.Count - items.Count;
        if (kept > 0) text += $" {kept} entr{(kept == 1 ? "y was" : "ies were")} left in place.";
        text += " Everything removed was backed up and can be restored from the Backups page.";
        ShowDone(_removal.Failed == 0, _removal.Failed == 0 ? "Registry cleaned" : "Cleaned with problems", text, cleaned: true);
    }

    private void ShowDone(bool success, string title, string text, bool cleaned)
    {
        DoneIcon.Symbol = success ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Warning24;
        DoneIcon.SetResourceReference(ForegroundProperty, success ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        DoneTitle.Text = title;
        DoneText.Text = text;
        OpenBackupsButton.Visibility = cleaned ? Visibility.Visible : Visibility.Collapsed;
        SaveReportButton.Visibility = cleaned || _found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Show(DonePanel);
    }

    private ReportContext BuildReport()
    {
        var removed = _removal?.Entries.Select(e => e.Item.Identity).ToHashSet() ?? [];
        return new ReportContext
        {
            Title = "Registry cleanup report",
            Subject = _scanName,
            Facts =
            [
                new("Scan", _scanName),
                new("Checks", string.Join(", ", _categories.Select(c => RegistryCleaner.Info(c).Name))),
                new("Entries found", _found.Count.ToString(CultureInfo.CurrentCulture)),
            ],
            Removal = _removal,
            Kept = _found.Select(i => i.Item).Where(i => !removed.Contains(i.Identity)).ToList(),
            Log = _log.ToString(),
        };
    }

    private void UpdateSelection()
    {
        var count = _issues.Count(i => i.IsSelected);
        CleanButton.Content = count == 0 ? "Clean selected" : $"Clean {count} selected";
        CleanButton.IsEnabled = count > 0;
        SelectionText.Text = $"{count} of {_issues.Count} selected";
    }

/// <summary>Progress reports arrive from worker threads; handle them on the UI thread.</summary>
    private IProgress<string> OnUiThread(Action<string> handler) =>
        new Progress<string>(m => Dispatcher.BeginInvoke(() => handler(m)));

    private void Log(string message) => _log.AppendLine($"{DateTime.Now:HH:mm:ss}  {message.Trim()}");

    private void SetAll(Func<RegistryIssueItem, bool> selector)
    {
        foreach (var item in _issues) item.IsSelected = selector(item);
    }

    private void SelectRecommended_Click(object sender, RoutedEventArgs e) => SetAll(i => i.Leftover.IsRecommended);
    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(_ => true);
    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAll(_ => false);

    private void ResultsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        var rows = ResultsGrid.SelectedItems.OfType<RegistryIssueItem>().ToList();
        if (rows.Count == 0) return;
        var value = !rows.All(r => r.IsSelected);
        foreach (var row in rows) row.IsSelected = value;
        e.Handled = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Back_Click(object sender, RoutedEventArgs e) => Show(HomePanel);

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var path = ReportExporter.SaveAs(owner, BuildReport());
        if (path is null) return;
        var answer = MessageBox.Show(owner, $"The report was saved to:\n{path}\n\nOpen it now?", "Report saved",
            MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Yes) ReportExporter.Open(path);
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        if (_removal is not null && Directory.Exists(_removal.BackupDirectory))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_removal.BackupDirectory}\"") { UseShellExecute = true });
    }
}
