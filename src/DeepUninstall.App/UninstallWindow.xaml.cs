using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DeepUninstall.App.Services;
using DeepUninstall.App.ViewModels;
using DeepUninstall.Core;
using DeepUninstall.Core.Models;
using DeepUninstall.Core.Removal;
using DeepUninstall.Core.Safety;
using DeepUninstall.Core.Uninstall;
using Wpf.Ui.Controls;

namespace DeepUninstall.App;

/// <summary>Walks one program through: restore point, uninstaller, leftover scan, cleanup.</summary>
public partial class UninstallWindow : FluentWindow
{
    private readonly UninstallSession _session;
    private readonly bool _force;
    private readonly StepItem _restoreStep = new("Restore point");
    private readonly StepItem _uninstallStep = new("Run uninstaller");
    private readonly StepItem _scanStep = new("Scan for leftovers");
    private readonly StepItem _removeStep = new("Remove leftovers");
    private readonly ObservableCollection<LeftoverItem> _leftovers = [];
    private CancellationTokenSource? _waitCts;
    private TaskCompletionSource<bool>? _removeDecision;
    private string? _backupDirectory;
    private bool _running;

    // Collected for the report.
    private string _restoreSummary = "Not created";
    private string _uninstallerSummary = "Not run";
    private IReadOnlyList<Leftover> _found = [];
    private RemovalReport? _removal;

    public UninstallWindow(InstalledApp app, bool force, ImageSource? icon, (int Index, int Count)? batch)
    {
        InitializeComponent();
        _session = new UninstallSession(app);
        _force = force || app.UninstallerMissing || !_session.CanRunUninstaller;

        App.ApplyChrome(this);
        Title = $"Uninstall {app.DisplayName}";
        TitleBar.Title = Title;
        if (batch is null) BatchChip.Visibility = Visibility.Collapsed;
        AppName.Text = app.DisplayName;
        AppDetails.Text = string.Join("  ·  ", new[] { app.Publisher, app.DisplayVersion, app.InstallLocation }.Where(s => !string.IsNullOrWhiteSpace(s)));
        InitialText.Text = app.DisplayName.Length > 0 ? app.DisplayName[..1].ToUpperInvariant() : "?";
        if (icon is not null)
        {
            AppIcon.Source = icon;
            InitialBadge.Visibility = Visibility.Collapsed;
        }
        if (batch is { } b) BatchText.Text = $"Program {b.Index} of {b.Count}";

        StepsList.ItemsSource = new[] { _restoreStep, _uninstallStep, _scanStep, _removeStep };

        var view = CollectionViewSource.GetDefaultView(_leftovers);
        view.SortDescriptions.Add(new SortDescription(nameof(LeftoverItem.GroupOrder), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(LeftoverItem.Confidence), ListSortDirection.Descending));
        view.SortDescriptions.Add(new SortDescription(nameof(LeftoverItem.Location), ListSortDirection.Ascending));
        LeftoversGrid.ItemsSource = view;

        ConfigureOptions(app);
        Closing += OnClosing;
    }

    /// <summary>True if anything on the system was changed.</summary>
    public bool MadeChanges { get; private set; }

    /// <summary>True if the user asked to stop a batch uninstall.</summary>
    public bool StopBatch { get; private set; }

    private void ConfigureOptions(InstalledApp app)
    {
        var silentSupported = !_force && _session.SupportsSilent;
        SilentCheck.IsEnabled = silentSupported;
        SilentHint.Text = _force
            ? "Not used: the program's uninstaller will be skipped."
            : silentSupported
                ? "Runs the program's uninstaller without showing its windows."
                : "This program's uninstaller does not offer a silent mode.";

        if (_force)
        {
            ForceWarning.Title = app.UninstallerMissing ? "Uninstaller missing" : "Force removal";
            ForceWarning.Message = app.UninstallerMissing
                ? "This program's uninstaller is missing, so it can't uninstall itself. DeepUninstall will skip it and remove what is left directly."
                : "Force removal skips the program's own uninstaller and deletes what it finds directly. Use it only when the normal uninstall is broken.";
            ForceWarning.IsOpen = true;
            StartButton.Content = "Force remove";
            StartButton.Appearance = ControlAppearance.Danger;
            _uninstallStep.State = StepState.Skipped;
            _uninstallStep.Detail = "Skipped (force removal)";
        }

        WhatHappensText.Text = _force
            ? "1. A restore point is created (if selected).\n2. DeepUninstall scans for the program's files, folders, registry entries, services, scheduled tasks, shortcuts and shell extensions.\n3. You review the list and choose what to remove."
            : "1. A restore point is created (if selected).\n2. The program's own uninstaller runs. DeepUninstall waits for it, including any helper processes it starts.\n3. DeepUninstall scans for leftover files, folders, registry entries, services, scheduled tasks, shortcuts and shell extensions.\n4. You review the list and choose what to remove.";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true;

        StartButton.Visibility = Visibility.Collapsed;
        OptionsPanel.Visibility = Visibility.Collapsed;
        LogExpander.Visibility = Visibility.Visible;
        LogExpander.IsExpanded = true;
        CloseButton.IsEnabled = false;

        try
        {
            await RunAsync();
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            ShowDone(false, "Something went wrong", ex.Message);
        }
        finally
        {
            _running = false;
            CloseButton.IsEnabled = true;
            CloseButton.Content = "Close";
            StopWaitingButton.Visibility = Visibility.Collapsed;
        }
    }

    private async Task RunAsync()
    {
        var log = new Progress<string>(Log);

        // 1. Restore point
        if (RestorePointCheck.IsChecked == true)
        {
            _restoreStep.State = StepState.Running;
            _restoreStep.Detail = "Creating...";
            FooterText.Text = "Creating a System Restore point...";
            var rp = await RestorePoint.CreateAsync($"DeepUninstall: {_session.App.DisplayName}");
            Log(rp.Message);
            _restoreStep.State = rp.Created ? StepState.Done : StepState.Warning;
            _restoreStep.Detail = rp.Created ? "Created" : rp.Message;
            _restoreSummary = rp.Message;
        }
        else
        {
            _restoreStep.State = StepState.Skipped;
            _restoreStep.Detail = "Skipped";
            _restoreSummary = "Skipped";
        }

        if (_force) _uninstallerSummary = "Skipped (force removal)";

        // 2. The program's own uninstaller
        if (!_force)
        {
            _uninstallStep.State = StepState.Running;
            _uninstallStep.Detail = "Waiting for the uninstaller...";
            FooterText.Text = "Complete the program's uninstaller if it shows a window.";
            StopWaitingButton.Visibility = Visibility.Visible;

            _waitCts = new CancellationTokenSource();
            var result = await _session.RunUninstallerAsync(SilentCheck.IsChecked == true, log, _waitCts.Token);
            StopWaitingButton.Visibility = Visibility.Collapsed;
            MadeChanges = true;

            var stillRegistered = _session.IsStillRegistered();
            _uninstallerSummary = result.Message + (stillRegistered ? " The program was still registered as installed afterwards." : "");
            _uninstallStep.State = result.LooksSuccessful && !stillRegistered ? StepState.Done : StepState.Warning;
            _uninstallStep.Detail = result.Outcome switch
            {
                UninstallOutcome.SucceededRebootRequired => "Finished, restart required",
                UninstallOutcome.WaitAbandoned => "Stopped waiting",
                _ when stillRegistered => "Program is still installed",
                _ => "Finished",
            };

            if (stillRegistered)
            {
                var answer = MessageBox.Show(this,
                    "The program is still registered as installed. Its uninstaller may have been cancelled, may have failed, or may still be running.\n\n" +
                    "Scanning now can flag files the program still uses. Scan for leftovers anyway?",
                    "Program still installed", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    _scanStep.State = StepState.Skipped;
                    _removeStep.State = StepState.Skipped;
                    ShowDone(false, "The uninstall did not finish", "Nothing else was changed. You can try again, or use Force remove if the uninstaller is broken.");
                    return;
                }
            }
        }

        // 3. Scan
        _scanStep.State = StepState.Running;
        _scanStep.Detail = "Scanning...";
        FooterText.Text = "Scanning for leftovers...";
        var leftovers = await _session.ScanLeftoversAsync(log);
        _found = leftovers;
        _scanStep.State = StepState.Done;
        _scanStep.Detail = $"{leftovers.Count} item(s) found";

        if (leftovers.Count == 0)
        {
            _removeStep.State = StepState.Skipped;
            ShowDone(true, $"{_session.App.DisplayName} was removed cleanly", "No leftovers were found.");
            return;
        }

        foreach (var item in leftovers) _leftovers.Add(new LeftoverItem(item));
        foreach (var item in _leftovers) item.PropertyChanged += (_, _) => UpdateRemoveButton();
        LeftoversTitle.Text = $"{leftovers.Count} leftover item(s) found";
        LeftoversPanel.Visibility = Visibility.Visible;
        LogExpander.IsExpanded = false;
        FooterText.Text = "Choose what to remove.";
        UpdateRemoveButton();

        // 4. Wait for the user's choice, then remove.
        List<Leftover> selected;
        bool closeProcesses;
        while (true)
        {
            CloseButton.IsEnabled = true;
            SkipButton.Visibility = Visibility.Visible;
            RemoveButton.Visibility = Visibility.Visible;
            _removeDecision = new TaskCompletionSource<bool>();
            var remove = await _removeDecision.Task;
            CloseButton.IsEnabled = false;
            SkipButton.Visibility = Visibility.Collapsed;
            RemoveButton.Visibility = Visibility.Collapsed;

            if (!remove)
            {
                _removeStep.State = StepState.Skipped;
                ShowDone(true, "Cleanup skipped", "No leftovers were removed.");
                return;
            }

            selected = _leftovers.Where(i => i.IsSelected).Select(i => i.Leftover).ToList();
            var answer = ConfirmRunningProcesses(selected);
            if (answer is null) continue; // Back to the list.
            closeProcesses = answer.Value;
            break;
        }

        _removeStep.State = StepState.Running;
        _removeStep.Detail = $"Removing {selected.Count} item(s)...";
        FooterText.Text = "Removing leftovers...";
        LeftoversPanel.Visibility = Visibility.Hidden;
        LogExpander.IsExpanded = true;

        var report = await _session.RemoveAsync(selected, new RemovalOptions
        {
            UseRecycleBin = RecycleCheck.IsChecked == true,
            CloseRunningProcesses = closeProcesses,
        }, log);
        MadeChanges = true;
        _backupDirectory = report.BackupDirectory;
        _removal = report;
        ReportExporter.SaveToBackupFolder(BuildReport());

        _removeStep.State = report.Failed == 0 ? StepState.Done : StepState.Warning;
        _removeStep.Detail = $"{report.Removed} removed" + (report.Failed > 0 ? $", {report.Failed} failed" : "");

        var text = $"{report.Removed} of {selected.Count} item(s) removed.";
        if (report.Failed > 0) text += $" {report.Failed} could not be removed; see Details.";
        if (report.RebootRequired) text += " Some files are in use and will be removed when you restart Windows.";
        text += " Registry backups were saved in case you need to restore anything.";
        ShowDone(report.Failed == 0, report.Failed == 0 ? $"{_session.App.DisplayName} is gone" : "Cleanup finished with problems", text);
        OpenBackupsButton.Visibility = Visibility.Visible;
    }

    private ReportContext BuildReport()
    {
        var app = _session.App;
        var facts = new List<KeyValuePair<string, string>>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) facts.Add(new(label, value));
        }

        Add("Program", app.DisplayName);
        Add("Publisher", app.Publisher);
        Add("Version", app.DisplayVersion);
        Add("Install folder", app.InstallLocation);
        Add("Installed for", app.IsPerUser ? "Current user" : "All users");
        Add("Method", _force ? "Force removal (the program's uninstaller was skipped)"
            : SilentCheck.IsChecked == true ? "Program's own uninstaller, silent" : "Program's own uninstaller");
        Add("Uninstaller result", _uninstallerSummary);
        Add("Restore point", _restoreSummary);
        Add("Leftovers found", _found.Count.ToString(System.Globalization.CultureInfo.CurrentCulture));
        if (_removal is null && _found.Count > 0) Add("Cleanup", "Skipped: nothing was removed");

        var removed = _removal?.Entries.Select(e => e.Item.Identity).ToHashSet() ?? [];
        return new ReportContext
        {
            Title = "Uninstall report",
            Subject = app.DisplayName,
            Facts = facts,
            Removal = _removal,
            Kept = _found.Where(f => !removed.Contains(f.Identity)).ToList(),
            Log = LogBox.Text,
        };
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        var path = ReportExporter.SaveAs(this, BuildReport());
        if (path is null) return;
        var answer = MessageBox.Show(this, $"The report was saved to:\n{path}\n\nOpen it now?", "Report saved",
            MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Yes) ReportExporter.Open(path);
    }

    /// <returns>true to close running processes, false to leave them, null to go back.</returns>
    private bool? ConfirmRunningProcesses(IReadOnlyList<Leftover> selected)
    {
        var running = ProcessFinder.FindIn(selected.Where(l => l.Kind == LeftoverKind.Directory).Select(l => l.Path));
        if (running.Count == 0) return true;

        var names = string.Join("\n", running.Select(p => $"  •  {p.Name} ({p.ExecutablePath})").Take(10));
        var answer = MessageBox.Show(this,
            $"These programs are running from folders that will be removed:\n\n{names}\n\nClose them now? Unsaved work in them will be lost.\n\n" +
            "Yes: close them and continue.\nNo: continue without closing them (locked files are removed at the next restart).\nCancel: go back.",
            "Programs are running", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        return answer switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };
    }

    private void ShowDone(bool success, string title, string text)
    {
        OptionsPanel.Visibility = Visibility.Collapsed;
        LeftoversPanel.Visibility = Visibility.Hidden;
        DonePanel.Visibility = Visibility.Visible;
        LogExpander.IsExpanded = false;
        DoneGlyph.Symbol = success ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Warning24;
        DoneGlyph.SetResourceReference(ForegroundProperty, success ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        DoneTitle.Text = title;
        DoneText.Text = text;
        FooterText.Text = "";
    }

    private void UpdateRemoveButton()
    {
        var count = _leftovers.Count(i => i.IsSelected);
        RemoveButton.Content = count == 0 ? "Remove selected" : $"Remove {count} selected";
        RemoveButton.IsEnabled = count > 0;
    }

    private void Log(string message)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void SetAll(Func<LeftoverItem, bool> selector)
    {
        foreach (var item in _leftovers) item.IsSelected = selector(item);
    }

    private void SelectRecommended_Click(object sender, RoutedEventArgs e) => SetAll(i => i.Leftover.IsRecommended);
    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAll(_ => true);
    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAll(_ => false);

    private void LeftoversGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space) return;
        var rows = LeftoversGrid.SelectedItems.OfType<LeftoverItem>().ToList();
        if (rows.Count == 0) return;
        var value = !rows.All(r => r.IsSelected);
        foreach (var row in rows) row.IsSelected = value;
        e.Handled = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = _leftovers.Where(i => i.IsSelected).ToList();
        var risky = selected.Count(i => i.Confidence != Confidence.High);
        if (risky > 0)
        {
            var answer = MessageBox.Show(this,
                $"{risky} of the selected items are not high-confidence matches and could belong to other software.\n\nRemove them anyway?",
                "Confirm removal", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }
        _removeDecision?.TrySetResult(true);
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => _removeDecision?.TrySetResult(false);

    private void StopWaiting_Click(object sender, RoutedEventArgs e)
    {
        _waitCts?.Cancel();
        StopWaitingButton.IsEnabled = false;
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        if (_backupDirectory is not null && Directory.Exists(_backupDirectory))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_backupDirectory}\"") { UseShellExecute = true });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_running && _removeDecision is { Task.IsCompleted: false })
        {
            // Closing while choosing leftovers counts as "skip".
            _removeDecision.TrySetResult(false);
            return;
        }

        if (_running)
        {
            e.Cancel = true;
            return;
        }

        // Cancelling before starting stops a batch uninstall.
        if (OptionsPanel.Visibility == Visibility.Visible) StopBatch = true;
    }
}
