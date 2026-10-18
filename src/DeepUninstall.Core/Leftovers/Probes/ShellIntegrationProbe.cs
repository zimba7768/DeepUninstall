using DeepUninstall.Core.Models;
using Microsoft.Win32;

namespace DeepUninstall.Core.Leftovers.Probes;

/// <summary>
/// COM classes, context-menu handlers, icon overlays, file types, "Open with" entries and
/// default-program registrations that point into the install folder. These are what leave dead
/// entries in Explorer's right-click menu after a normal uninstall.
/// </summary>
internal static class ShellIntegrationProbe
{
    private static readonly string[] ServerSubKeys = ["InprocServer32", "LocalServer32", "InprocHandler32"];

    private static readonly HashSet<string> NonProgIdClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "*", "CLSID", "Interface", "TypeLib", "AppID", "Installer", "Applications", "SystemFileAssociations", "MIME",
        "Local Settings", "WOW6432Node", "Media Type", "MediaFoundation", "PROTOCOLS", "DirectShow",
        "Component Categories", "Record", "Directory", "Folder", "Drive", "AllFilesystemObjects", "DesktopBackground",
    };

    private static readonly string[] ShellExContainers = ["*", "Directory", @"Directory\Background", "Folder", "Drive", "AllFilesystemObjects", "DesktopBackground"];
    private static readonly string[] ShellExHandlerTypes = ["ContextMenuHandlers", "DragDropHandlers", "PropertySheetHandlers", "CopyHookHandlers", "ColumnHandlers"];

    public static void Run(ScanContext ctx)
    {
        // Everything here needs hard evidence: a path into the install folder.
        if (ctx.TrustedDirectories.Count == 0) return;

        var clsids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in RegistryHelpers.SoftwareViews())
            ScanClsids(ctx, new RegistryLocation(hive, view, @"SOFTWARE\Classes\CLSID"), clsids);

        // HKLM\SOFTWARE\Classes is shared between the 32 and 64-bit views apart from CLSID, so one pass is enough.
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Default), (RegistryHive.LocalMachine, RegistryView.Registry64) })
        {
            ctx.Token.ThrowIfCancellationRequested();
            var classes = new RegistryLocation(hive, view, @"SOFTWARE\Classes");
            var progIds = ScanProgIds(ctx, classes, clsids);
            ScanApplications(ctx, classes.Child("Applications"));
            ScanShellExHandlers(ctx, classes, clsids);
            ScanFileExtensions(ctx, classes, progIds, clsids);
        }

        if (clsids.Count > 0)
        {
            ScanApprovedExtensions(ctx, clsids);
            ScanIconOverlays(ctx, clsids);
        }
        ScanClients(ctx);
        ScanRegisteredApplications(ctx);
    }

    private static void ScanClsids(ScanContext ctx, RegistryLocation root, HashSet<string> found)
    {
        foreach (var (clsid, key) in RegistryHelpers.EnumerateSubKeys(root))
        {
            foreach (var server in ServerSubKeys)
            {
                if (!ctx.ReferencesInstallFolder(RegistryHelpers.GetString(key, server))) continue;
                found.Add(clsid);
                ctx.AddKey(root.Child(clsid), Confidence.High, "COM component registered by the program");
                break;
            }
        }
    }

    private static HashSet<string> ScanProgIds(ScanContext ctx, RegistryLocation classes, HashSet<string> clsids)
    {
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, key) in RegistryHelpers.EnumerateSubKeys(classes))
        {
            if (name.StartsWith('.') || NonProgIdClasses.Contains(name)) continue;

            var hit = ReferencesViaShellVerbs(ctx, key)
                      || ctx.ReferencesInstallFolder(RegistryHelpers.GetString(key, "DefaultIcon"))
                      || (RegistryHelpers.GetString(key, "CLSID") is { } clsid && clsids.Contains(clsid));
            if (!hit) continue;

            // A program can hijack a shared type such as "txtfile". Only remove the type if it
            // is also named after the program.
            if (!ctx.Matcher.NameContainsProductOrPublisher(name)) continue;

            ctx.AddKey(classes.Child(name), Confidence.High, "File type or protocol registered by the program");
            removed.Add(name);
        }
        return removed;
    }

    private static void ScanApplications(ScanContext ctx, RegistryLocation applications)
    {
        foreach (var (name, key) in RegistryHelpers.EnumerateSubKeys(applications))
        {
            if (ReferencesViaShellVerbs(ctx, key))
                ctx.AddKey(applications.Child(name), Confidence.High, "\"Open with\" registration for the program");
        }
    }

    private static void ScanShellExHandlers(ScanContext ctx, RegistryLocation classes, HashSet<string> clsids)
    {
        if (clsids.Count == 0) return;
        foreach (var container in ShellExContainers)
        {
            foreach (var type in ShellExHandlerTypes)
            {
                var root = classes.Child(container).Child("shellex").Child(type);
                foreach (var (name, key) in RegistryHelpers.EnumerateSubKeys(root))
                {
                    var value = RegistryHelpers.GetString(key, null);
                    if (clsids.Contains(name) || (value is not null && clsids.Contains(value)))
                        ctx.AddKey(root.Child(name), Confidence.High, "Explorer shell extension (e.g. right-click menu entry)");
                }
            }
        }
    }

    private static void ScanFileExtensions(ScanContext ctx, RegistryLocation classes, HashSet<string> progIds, HashSet<string> clsids)
    {
        if (progIds.Count == 0 && clsids.Count == 0) return;
        foreach (var (ext, key) in RegistryHelpers.EnumerateSubKeys(classes))
        {
            if (!ext.StartsWith('.')) continue;
            var extLocation = classes.Child(ext);

            if (progIds.Count > 0)
            {
                using var openWith = RegistryHelpers.OpenSubKey(key, "OpenWithProgids");
                if (openWith is not null)
                {
                    foreach (var value in RegistryHelpers.ValueNames(openWith).Where(progIds.Contains))
                        ctx.AddValue(extLocation.Child("OpenWithProgids"), value, Confidence.High, "\"Open with\" entry for a removed file type");
                }
            }

            if (clsids.Count > 0)
            {
                using var shellEx = RegistryHelpers.OpenSubKey(key, "shellex");
                if (shellEx is null) continue;
                foreach (var handler in RegistryHelpers.SubKeyNames(shellEx))
                {
                    var value = RegistryHelpers.GetString(shellEx, handler);
                    if (value is not null && clsids.Contains(value))
                        ctx.AddKey(extLocation.Child("shellex").Child(handler), Confidence.High, "Preview, thumbnail or property handler");
                }
            }
        }
    }

    private static void ScanApprovedExtensions(ScanContext ctx, HashSet<string> clsids)
    {
        foreach (var (hive, view) in RegistryHelpers.SoftwareViews())
        {
            var location = new RegistryLocation(hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved");
            using var key = location.Open();
            if (key is null) continue;
            foreach (var value in RegistryHelpers.ValueNames(key).Where(clsids.Contains))
                ctx.AddValue(location, value, Confidence.High, "Approval entry for a removed shell extension");
        }
    }

    private static void ScanIconOverlays(ScanContext ctx, HashSet<string> clsids)
    {
        foreach (var (hive, view) in RegistryHelpers.SoftwareViews())
        {
            var root = new RegistryLocation(hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers");
            foreach (var (name, key) in RegistryHelpers.EnumerateSubKeys(root))
            {
                var value = RegistryHelpers.GetString(key, null);
                if (value is not null && clsids.Contains(value))
                    ctx.AddKey(root.Child(name), Confidence.High, "Explorer icon overlay handler");
            }
        }
    }

    private static void ScanClients(ScanContext ctx)
    {
        foreach (var (hive, view) in RegistryHelpers.SoftwareViews())
        {
            var clients = new RegistryLocation(hive, view, @"SOFTWARE\Clients");
            foreach (var (type, typeKey) in RegistryHelpers.EnumerateSubKeys(clients))
            {
                foreach (var name in RegistryHelpers.SubKeyNames(typeKey))
                {
                    using var client = RegistryHelpers.OpenSubKey(typeKey, name);
                    if (client is null) continue;
                    if (ReferencesViaShellVerbs(ctx, client)
                        || ctx.ReferencesInstallFolder(RegistryHelpers.GetString(client, "DefaultIcon"))
                        || ctx.ReferencesInstallFolder(RegistryHelpers.GetString(client, "InstallInfo", "ReinstallCommand")))
                    {
                        ctx.AddKey(clients.Child(type).Child(name), Confidence.High, "Default-programs registration (browser, mail...)");
                    }
                }
            }
        }
    }

    private static void ScanRegisteredApplications(ScanContext ctx)
    {
        foreach (var (hive, view) in RegistryHelpers.SoftwareViews())
        {
            var location = new RegistryLocation(hive, view, @"SOFTWARE\RegisteredApplications");
            using var key = location.Open();
            if (key is null) continue;
            foreach (var value in RegistryHelpers.ValueNames(key))
            {
                var target = RegistryHelpers.GetString(key, null, value)?.Trim('\\');
                if (!string.IsNullOrEmpty(target) && ctx.Results.CoversKey(hive, target))
                    ctx.AddValue(location, value, Confidence.High, "Default-apps entry for removed program settings");
            }
        }
    }

    private static bool ReferencesViaShellVerbs(ScanContext ctx, RegistryKey key)
    {
        using var shell = RegistryHelpers.OpenSubKey(key, "shell");
        if (shell is null) return false;
        foreach (var verb in RegistryHelpers.SubKeyNames(shell))
        {
            if (ctx.ReferencesInstallFolder(RegistryHelpers.GetString(shell, $@"{verb}\command")))
                return true;
        }
        return false;
    }
}
