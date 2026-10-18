using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DeepUninstall.Core.Models;
using Wpf.Ui.Controls;

namespace DeepUninstall.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A program in the installed programs list.</summary>
public sealed class AppItem(InstalledApp app) : ObservableObject
{
    private ImageSource? _icon;

    public InstalledApp App { get; } = app;
    public string Name => App.DisplayName;
    public string Publisher => App.Publisher ?? "";
    public string Version => App.DisplayVersion ?? "";
    public long SizeBytes => App.EstimatedSizeBytes ?? 0;
    public string SizeText => Format.Size(App.EstimatedSizeBytes);
    public DateTime InstallDate => App.InstallDate ?? DateTime.MinValue;
    public string InstallDateText => App.InstallDate?.ToString("d", CultureInfo.CurrentCulture) ?? "";
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1].ToUpperInvariant();
    public bool IsBroken => App.UninstallerMissing;
    public string Location => App.InstallLocation ?? "";
    public bool HasLocation => !string.IsNullOrWhiteSpace(App.InstallLocation);

    /// <summary>"Publisher · Version" line under the name.</summary>
    public string Subtitle => string.Join("  ·  ", new[] { Publisher, Version }.Where(s => s.Length > 0));

    /// <summary>Short facts shown as tags in the details panel.</summary>
    public IReadOnlyList<string> Tags
    {
        get
        {
            var tags = new List<string> { App.IsPerUser ? "Current user" : "All users" };
            if (App.Is32Bit) tags.Add("32-bit");
            var installer = Format.Installer(App.InstallerType);
            if (installer.Length > 0) tags.Add(installer);
            return tags;
        }
    }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (Set(ref _icon, value)) Raise(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon is not null;

    public bool Matches(string filter) =>
        filter.Length == 0
        || Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || Publisher.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
}

/// <summary>A row in the leftovers list.</summary>
public sealed class LeftoverItem(Leftover leftover) : ObservableObject
{
    private bool _isSelected = leftover.IsRecommended;

    public Leftover Leftover { get; } = leftover;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public string Location => Leftover.DisplayPath;
    public string Reason => Leftover.Reason;
    public string ConfidenceText => Leftover.Confidence.ToString();
    public Confidence Confidence => Leftover.Confidence;

    /// <summary>Sort key that keeps related items together.</summary>
    public int GroupOrder => Leftover.Kind switch
    {
        LeftoverKind.Directory or LeftoverKind.File => 0,
        LeftoverKind.RegistryKey or LeftoverKind.RegistryValue => 1,
        _ => 2,
    };

    public string KindText => Leftover.Kind switch
    {
        LeftoverKind.Directory => "Folder",
        LeftoverKind.File => IsShortcut ? "Shortcut" : "File",
        LeftoverKind.RegistryKey => "Registry key",
        LeftoverKind.RegistryValue => "Registry value",
        LeftoverKind.Service => "Service",
        LeftoverKind.ScheduledTask => "Scheduled task",
        LeftoverKind.FirewallRule => "Firewall rule",
        _ => "",
    };

    public SymbolRegular Symbol => Leftover.Kind switch
    {
        LeftoverKind.Directory => SymbolRegular.Folder24,
        LeftoverKind.File => IsShortcut ? SymbolRegular.Link24 : SymbolRegular.Document24,
        LeftoverKind.RegistryKey or LeftoverKind.RegistryValue => SymbolRegular.Key24,
        LeftoverKind.Service => SymbolRegular.Settings24,
        LeftoverKind.ScheduledTask => SymbolRegular.CalendarClock24,
        LeftoverKind.FirewallRule => SymbolRegular.Shield24,
        _ => SymbolRegular.Document24,
    };

    private bool IsShortcut => Leftover.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
}

public enum StepState
{
    Pending,
    Running,
    Done,
    Warning,
    Failed,
    Skipped,
}

/// <summary>One step in the uninstall wizard's progress list.</summary>
public sealed class StepItem(string title) : ObservableObject
{
    private StepState _state;
    private string _detail = "";

    public string Title { get; } = title;

    public StepState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(Symbol));
            Raise(nameof(IsRunning));
        }
    }

    public bool IsRunning => State == StepState.Running;

    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    public SymbolRegular Symbol => State switch
    {
        StepState.Done => SymbolRegular.CheckmarkCircle24,
        StepState.Warning => SymbolRegular.Warning24,
        StepState.Failed => SymbolRegular.DismissCircle24,
        StepState.Skipped => SymbolRegular.SubtractCircle24,
        _ => SymbolRegular.Circle24,
    };
}

/// <summary>One cleanup's backup folder.</summary>
public sealed class BackupItem
{
    public BackupItem(DirectoryInfo dir)
    {
        Directory = dir.FullName;
        var name = dir.Name;
        if (name.Length > 16 && DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            Date = date;
            Program = name[16..];
        }
        else
        {
            Date = dir.CreationTime;
            Program = name;
        }

        RegistryFiles = dir.GetFiles("*.reg").Length;
        var log = Path.Combine(dir.FullName, "removal-log.txt");
        RemovedItems = File.Exists(log) ? File.ReadLines(log).Count(l => l.StartsWith("[Removed]", StringComparison.Ordinal)) : 0;
    }

    public string Directory { get; }
    public string Program { get; }
    public DateTime Date { get; }
    public string DateText => Date.ToString("g", CultureInfo.CurrentCulture);
    public int RegistryFiles { get; }
    public int RemovedItems { get; }
    public bool CanRestoreRegistry => RegistryFiles > 0;

    public string Summary =>
        $"{RemovedItems} item(s) removed  ·  {RegistryFiles} registry backup(s)";
}

internal static class Format
{
    public static string Size(long? bytes) => bytes switch
    {
        null or 0 => "",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public static string Installer(InstallerType type) => type switch
    {
        InstallerType.Msi => "Windows Installer",
        InstallerType.InnoSetup => "Inno Setup",
        InstallerType.Nsis => "NSIS",
        InstallerType.InstallShield => "InstallShield",
        InstallerType.WixBurn => "WiX bundle",
        InstallerType.Squirrel => "Squirrel",
        _ => "",
    };
}
