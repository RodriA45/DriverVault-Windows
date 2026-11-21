using System.Diagnostics;
using System.IO;
using System.Windows;
using DriverVault.Models;

namespace DriverVault.Views;

public partial class SummaryDialog : Window
{
    private readonly ExtractionSummary _summary;

    public SummaryDialog(ExtractionSummary summary)
    {
        InitializeComponent();
        _summary = summary;

        if (summary.WasCancelled)
        {
            TxtTag.Text = "EXTRACCIÓN CANCELADA";
            TxtHeadline.Text = "Se detuvo el proceso (se conservó lo exportado hasta el momento)";
        }
        else if (summary.FailedCount > 0 && summary.SucceededCount == 0)
        {
            TxtTag.Text = "ATENCIÓN";
            TxtHeadline.Text = "No se pudieron exportar los drivers seleccionados";
        }

        string displayPath = !string.IsNullOrWhiteSpace(summary.ZipFilePath)
            ? summary.ZipFilePath
            : summary.OutputFolderPath;

        TxtOutputPath.Text = $"Guardado en: {displayPath}";
        TxtSuccessCount.Text = summary.SucceededCount.ToString();
        TxtIssueCount.Text = (summary.SkippedCount + summary.FailedCount).ToString();
        TxtTotalSize.Text = DriverEntry.FormatBytes(summary.TotalBytesWritten);
        TxtElapsed.Text = $"{summary.Elapsed.TotalSeconds:0.0}s";

        BtnOpenReport.Visibility = !string.IsNullOrWhiteSpace(summary.ReportHtmlPath) && File.Exists(summary.ReportHtmlPath)
            ? Visibility.Visible
            : Visibility.Collapsed;

        ListResults.ItemsSource = summary.Items
            .OrderByDescending(i => i.Outcome != DriverExportOutcome.Succeeded)
            .Select(i => new ResultRow(
                StatusLabel: i.Outcome switch
                {
                    DriverExportOutcome.Succeeded => "OK",
                    DriverExportOutcome.Skipped => "OMITIDO",
                    _ => "ERROR"
                },
                DriverName: $"{i.Driver.PrimaryDisplayName} ({i.Driver.InfName})",
                FolderLine: i.TargetRelativeFolder,
                Message: i.Message))
            .ToList();
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string target = Directory.Exists(_summary.OutputFolderPath)
            ? _summary.OutputFolderPath
            : Path.GetDirectoryName(_summary.ZipFilePath) ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        }
    }

    private void BtnOpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_summary.ReportHtmlPath) && File.Exists(_summary.ReportHtmlPath))
        {
            Process.Start(new ProcessStartInfo(_summary.ReportHtmlPath) { UseShellExecute = true });
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed record ResultRow(string StatusLabel, string DriverName, string FolderLine, string Message);
}
