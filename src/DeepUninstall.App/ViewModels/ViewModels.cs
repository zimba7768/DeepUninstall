using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DeepUninstall.Core.Models;

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

/// <summary>A row in the installed programs list.</summary>
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

    public string Details
    {
        get
        {
            var parts = new List<string> { App.IsPerUser ? "Current user" : "All users" };
            if (App.Is32Bit) parts.Add("32-bit");
            if (App.InstallerType != InstallerType.Unknown) parts.Add(Format.Installer(App.InstallerType));
            if (IsBroken) parts.Add("Uninstaller missing");
            return string.Join(" · ", parts);
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

    public string Group => Leftover.Kind switch
    {
        LeftoverKind.Directory or LeftoverKind.File => "Files and folders",
        LeftoverKind.RegistryKey or LeftoverKind.RegistryValue => "Registry",
        LeftoverKind.Service => "Services",
        LeftoverKind.ScheduledTask => "Scheduled tasks",
        LeftoverKind.FirewallRule => "Firewall rules",
        _ => "Other",
    };

    public string KindText => Leftover.Kind switch
    {
        LeftoverKind.Directory => "Folder",
        LeftoverKind.File => Leftover.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? "Shortcut" : "File",
        LeftoverKind.RegistryKey => "Key",
        LeftoverKind.RegistryValue => "Value",
        LeftoverKind.Service => "Service",
        LeftoverKind.ScheduledTask => "Task",
        LeftoverKind.FirewallRule => "Firewall",
        _ => "",
    };

    public string Glyph => Leftover.Kind switch
    {
        LeftoverKind.Directory => "",
        LeftoverKind.File => "",
        LeftoverKind.RegistryKey or LeftoverKind.RegistryValue => "",
        LeftoverKind.Service => "",
        LeftoverKind.ScheduledTask => "",
        LeftoverKind.FirewallRule => "",
        _ => "",
    };
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
            if (Set(ref _state, value)) Raise(nameof(Glyph));
        }
    }

    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    public string Glyph => State switch
    {
        StepState.Running => "",
        StepState.Done => "",
        StepState.Warning => "",
        StepState.Failed => "",
        StepState.Skipped => "",
        _ => "",
    };
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
