using System.IO;
using System.Text;
using System.Windows;
using DeepUninstall.Core.Removal;
using Microsoft.Win32;

namespace DeepUninstall.App.Services;

/// <summary>Saves cleanup reports as HTML or text.</summary>
internal static class ReportExporter
{
    public const string ReportFileName = "report.html";

    /// <summary>Keeps a copy next to the backups so the Backups page can show it later.</summary>
    public static void SaveToBackupFolder(ReportContext context)
    {
        if (context.Removal is null) return;
        try
        {
            File.WriteAllText(Path.Combine(context.Removal.BackupDirectory, ReportFileName), ReportWriter.ToHtml(context), Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The report is a convenience; the cleanup itself already succeeded.
        }
    }

    /// <summary>Asks where to save the report and writes it.</summary>
    /// <returns>The saved file, or null if the user cancelled.</returns>
    public static string? SaveAs(Window owner, ReportContext context)
    {
        var subject = context.Subject ?? context.Title;
        var safe = string.Concat(subject.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        var dialog = new SaveFileDialog
        {
            Title = "Save report",
            FileName = $"DeepUninstall report - {safe} - {context.GeneratedAt:yyyy-MM-dd HHmm}",
            Filter = "Web page (*.html)|*.html|Text file (*.txt)|*.txt",
            DefaultExt = ".html",
            AddExtension = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(owner) != true) return null;

        var asText = Path.GetExtension(dialog.FileName).Equals(".txt", StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(dialog.FileName, asText ? ReportWriter.ToText(context) : ReportWriter.ToHtml(context), Encoding.UTF8);
        return dialog.FileName;
    }

    public static void Open(string path) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
}
