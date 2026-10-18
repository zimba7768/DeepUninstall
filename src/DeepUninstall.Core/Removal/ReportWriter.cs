using System.Globalization;
using System.Net;
using System.Text;
using DeepUninstall.Core.Models;

namespace DeepUninstall.Core.Removal;

/// <summary>What a report is about.</summary>
public sealed record ReportContext
{
    /// <summary>e.g. "Uninstall report" or "Registry cleanup report".</summary>
    public required string Title { get; init; }

    /// <summary>e.g. the program name.</summary>
    public string? Subject { get; init; }

    /// <summary>Label/value pairs shown in the summary (publisher, version, uninstaller result...).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Facts { get; init; } = [];

    public RemovalReport? Removal { get; init; }

    /// <summary>Items that were found but deliberately left in place.</summary>
    public IReadOnlyList<Leftover> Kept { get; init; } = [];

    /// <summary>The detailed activity log.</summary>
    public string? Log { get; init; }

    public DateTime GeneratedAt { get; init; } = DateTime.Now;
}

/// <summary>Turns a cleanup into a readable report, as plain text or a self-contained HTML page.</summary>
public static class ReportWriter
{
    private static string Version =>
        typeof(ReportWriter).Assembly.GetName().Version?.ToString(3) ?? "";

    public static string KindName(Leftover item) => item.Kind switch
    {
        LeftoverKind.Directory => "Folder",
        LeftoverKind.File => item.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? "Shortcut" : "File",
        LeftoverKind.RegistryKey => "Registry key",
        LeftoverKind.RegistryValue => "Registry value",
        LeftoverKind.Service => "Service",
        LeftoverKind.ScheduledTask => "Scheduled task",
        LeftoverKind.FirewallRule => "Firewall rule",
        _ => item.Kind.ToString(),
    };

    public static string StatusName(RemovalStatus status) => status switch
    {
        RemovalStatus.Removed => "Removed",
        RemovalStatus.ScheduledForReboot => "Removed at restart",
        RemovalStatus.AlreadyGone => "Already gone",
        RemovalStatus.Blocked => "Blocked by safety rules",
        RemovalStatus.Failed => "Failed",
        _ => status.ToString(),
    };

    public static string ToText(ReportContext ctx)
    {
        var sb = new StringBuilder();
        var heading = string.IsNullOrEmpty(ctx.Subject) ? ctx.Title : $"{ctx.Title}: {ctx.Subject}";
        sb.AppendLine(heading);
        sb.AppendLine(new string('=', heading.Length));
        sb.AppendLine($"Created {ctx.GeneratedAt.ToString("F", CultureInfo.CurrentCulture)} by DeepUninstall {Version}");
        sb.AppendLine();

        if (ctx.Facts.Count > 0)
        {
            var width = ctx.Facts.Max(f => f.Key.Length) + 2;
            foreach (var (label, value) in ctx.Facts)
                sb.AppendLine($"{(label + ":").PadRight(width)} {value}");
            sb.AppendLine();
        }

        if (ctx.Removal is { } removal)
        {
            sb.AppendLine(SummaryLine(removal, ctx.Kept.Count));
            sb.AppendLine();
            sb.AppendLine("REMOVED ITEMS");
            sb.AppendLine("-------------");
            foreach (var entry in removal.Entries)
            {
                sb.AppendLine($"[{StatusName(entry.Status)}] {KindName(entry.Item)}: {entry.Item.DisplayPath}");
                sb.AppendLine($"    Why: {entry.Item.Reason} ({entry.Item.Confidence} confidence)");
                if (!string.IsNullOrEmpty(entry.Message)) sb.AppendLine($"    Note: {entry.Message}");
            }
            foreach (var line in removal.CleanedUpParents) sb.AppendLine(line);
            sb.AppendLine();
        }

        if (ctx.Kept.Count > 0)
        {
            sb.AppendLine("FOUND BUT KEPT");
            sb.AppendLine("--------------");
            foreach (var item in ctx.Kept)
                sb.AppendLine($"{KindName(item)}: {item.DisplayPath}  ({item.Confidence} confidence: {item.Reason})");
            sb.AppendLine();
        }

        if (ctx.Removal is { } r)
        {
            sb.AppendLine("BACKUPS");
            sb.AppendLine("-------");
            sb.AppendLine(r.BackupDirectory);
            sb.AppendLine("Registry entries can be restored from DeepUninstall's Backups page or by double-clicking the .reg files there.");
            sb.AppendLine("Files and folders were moved to the Recycle Bin unless you chose permanent deletion.");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(ctx.Log))
        {
            sb.AppendLine("ACTIVITY LOG");
            sb.AppendLine("------------");
            sb.AppendLine(ctx.Log.TrimEnd());
        }

        return sb.ToString();
    }

    public static string ToHtml(ReportContext ctx)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var heading = string.IsNullOrEmpty(ctx.Subject) ? ctx.Title : ctx.Subject;
        var sb = new StringBuilder();

        sb.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            """);
        sb.Append($"<title>{E(ctx.Title)}{(ctx.Subject is null ? "" : " - " + E(ctx.Subject))}</title>");
        sb.Append("""
            <style>
            :root { --bg:#f6f7f9; --card:#fff; --text:#16191f; --muted:#5f6673; --line:#e3e6eb; --accent:#2458d6;
                    --ok:#1e7f4f; --okbg:#e6f4ec; --warn:#9a5b00; --warnbg:#fff3df; --bad:#b42318; --badbg:#fdecea; }
            @media (prefers-color-scheme: dark) {
              :root { --bg:#1b1c1f; --card:#25272b; --text:#eceef1; --muted:#a3a9b3; --line:#363940; --accent:#7aa2ff;
                      --ok:#6fd39c; --okbg:#1f3a2b; --warn:#f2c56b; --warnbg:#3d3220; --bad:#ff8a80; --badbg:#3f2224; }
            }
            * { box-sizing: border-box; }
            body { margin:0; background:var(--bg); color:var(--text); font:14px/1.5 "Segoe UI Variable Text","Segoe UI",system-ui,sans-serif; }
            main { max-width:1000px; margin:0 auto; padding:32px 20px 48px; }
            .eyebrow { color:var(--accent); font-weight:600; font-size:13px; text-transform:uppercase; letter-spacing:.06em; }
            h1 { margin:4px 0 4px; font-size:28px; font-weight:600; }
            h2 { font-size:17px; margin:32px 0 10px; }
            .muted { color:var(--muted); }
            .card { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:16px 20px; }
            .facts { display:grid; grid-template-columns:max-content 1fr; gap:6px 24px; margin:0; }
            .facts dt { color:var(--muted); } .facts dd { margin:0; overflow-wrap:anywhere; }
            .stats { display:flex; flex-wrap:wrap; gap:12px; margin-top:20px; }
            .stat { flex:1 1 140px; background:var(--card); border:1px solid var(--line); border-radius:10px; padding:12px 16px; }
            .stat b { display:block; font-size:24px; font-weight:600; }
            table { width:100%; border-collapse:collapse; background:var(--card); border:1px solid var(--line); border-radius:10px; overflow:hidden; }
            th, td { text-align:left; padding:9px 12px; border-bottom:1px solid var(--line); vertical-align:top; }
            th { font-size:12px; color:var(--muted); font-weight:600; }
            tr:last-child td { border-bottom:0; }
            td.path { font-family:"Cascadia Mono",Consolas,monospace; font-size:12.5px; overflow-wrap:anywhere; }
            .chip { display:inline-block; padding:1px 8px; border-radius:6px; font-size:12px; white-space:nowrap; }
            .ok { color:var(--ok); background:var(--okbg); } .warn { color:var(--warn); background:var(--warnbg); } .bad { color:var(--bad); background:var(--badbg); }
            pre { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:14px; overflow:auto; font:12px/1.5 "Cascadia Mono",Consolas,monospace; white-space:pre-wrap; }
            footer { margin-top:36px; color:var(--muted); font-size:12px; }
            </style>
            </head>
            <body><main>
            """);

        sb.Append($"<div class=\"eyebrow\">{E(ctx.Title)}</div><h1>{E(heading)}</h1>");
        sb.Append($"<div class=\"muted\">Created {E(ctx.GeneratedAt.ToString("F", CultureInfo.CurrentCulture))} by DeepUninstall {E(Version)}</div>");

        if (ctx.Facts.Count > 0)
        {
            sb.Append("<h2>Summary</h2><div class=\"card\"><dl class=\"facts\">");
            foreach (var (label, value) in ctx.Facts) sb.Append($"<dt>{E(label)}</dt><dd>{E(value)}</dd>");
            sb.Append("</dl></div>");
        }

        if (ctx.Removal is { } removal)
        {
            var scheduled = removal.Entries.Count(e => e.Status == RemovalStatus.ScheduledForReboot);
            sb.Append("<div class=\"stats\">");
            sb.Append($"<div class=\"stat\"><b>{removal.Removed}</b><span class=\"muted\">Removed</span></div>");
            if (scheduled > 0) sb.Append($"<div class=\"stat\"><b>{scheduled}</b><span class=\"muted\">Removed at next restart</span></div>");
            sb.Append($"<div class=\"stat\"><b>{removal.Failed}</b><span class=\"muted\">Could not be removed</span></div>");
            sb.Append($"<div class=\"stat\"><b>{ctx.Kept.Count}</b><span class=\"muted\">Found but kept</span></div>");
            sb.Append("</div>");

            sb.Append("<h2>Removed items</h2>");
            if (removal.Entries.Count == 0)
            {
                sb.Append("<p class=\"muted\">Nothing was removed.</p>");
            }
            else
            {
                sb.Append("<table><thead><tr><th>Result</th><th>Type</th><th>Location</th><th>Why it was removed</th></tr></thead><tbody>");
                foreach (var entry in removal.Entries)
                {
                    var css = entry.Status switch
                    {
                        RemovalStatus.Removed or RemovalStatus.AlreadyGone => "ok",
                        RemovalStatus.ScheduledForReboot => "warn",
                        _ => "bad",
                    };
                    var note = string.IsNullOrEmpty(entry.Message) ? "" : $"<br><span class=\"muted\">{E(entry.Message)}</span>";
                    sb.Append($"<tr><td><span class=\"chip {css}\">{E(StatusName(entry.Status))}</span></td><td>{E(KindName(entry.Item))}</td>")
                      .Append($"<td class=\"path\">{E(entry.Item.DisplayPath)}</td><td>{E(entry.Item.Reason)} <span class=\"muted\">({entry.Item.Confidence})</span>{note}</td></tr>");
                }
                foreach (var line in removal.CleanedUpParents)
                    sb.Append($"<tr><td><span class=\"chip ok\">Removed</span></td><td>Empty parent</td><td class=\"path\" colspan=\"2\">{E(line.Replace("[Removed] ", ""))}</td></tr>");
                sb.Append("</tbody></table>");
            }
        }

        if (ctx.Kept.Count > 0)
        {
            sb.Append("<h2>Found but kept</h2><table><thead><tr><th>Type</th><th>Location</th><th>Confidence</th><th>Reason</th></tr></thead><tbody>");
            foreach (var item in ctx.Kept)
                sb.Append($"<tr><td>{E(KindName(item))}</td><td class=\"path\">{E(item.DisplayPath)}</td><td>{item.Confidence}</td><td>{E(item.Reason)}</td></tr>");
            sb.Append("</tbody></table>");
        }

        if (ctx.Removal is { } r)
        {
            sb.Append("<h2>Backups</h2><div class=\"card\">");
            sb.Append($"<div class=\"path\" style=\"font-family:Cascadia Mono,Consolas,monospace;overflow-wrap:anywhere\">{E(r.BackupDirectory)}</div>");
            sb.Append("<p class=\"muted\" style=\"margin:8px 0 0\">Registry entries can be restored from DeepUninstall's Backups page, or by double-clicking the .reg files in this folder. Files and folders were moved to the Recycle Bin unless permanent deletion was chosen.</p></div>");
        }

        if (!string.IsNullOrWhiteSpace(ctx.Log))
            sb.Append($"<h2>Activity log</h2><pre>{E(ctx.Log.TrimEnd())}</pre>");

        sb.Append("<footer>DeepUninstall is free and open source: https://github.com/zimba7768/DeepUninstall</footer>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    private static string SummaryLine(RemovalReport removal, int kept)
    {
        var scheduled = removal.Entries.Count(e => e.Status == RemovalStatus.ScheduledForReboot);
        var parts = new List<string> { $"{removal.Removed} removed" };
        if (scheduled > 0) parts.Add($"{scheduled} removed at next restart");
        parts.Add($"{removal.Failed} could not be removed");
        parts.Add($"{kept} found but kept");
        return string.Join(", ", parts) + ".";
    }
}
