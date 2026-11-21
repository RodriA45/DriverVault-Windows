using System.Windows;
using DriverVault.Services;

namespace DriverVault.Views;

public partial class ProgressDialog : Window
{
    private readonly CancellationTokenSource _cts;

    public ProgressDialog(CancellationTokenSource cts, int totalCount)
    {
        InitializeComponent();
        _cts = cts;
        ProgressBarExport.Maximum = Math.Max(1, totalCount);
        TxtCounter.Text = $"0 / {totalCount}";
    }

    public void ReportProgress(ExportProgress progress)
    {
        ProgressBarExport.Maximum = Math.Max(1, progress.Total);
        ProgressBarExport.Value = Math.Min(progress.Total, progress.Completed);
        TxtCounter.Text = $"{progress.Completed} / {progress.Total}";
        TxtCurrentDriver.Text = progress.CurrentDriverName;

        if (!string.IsNullOrWhiteSpace(progress.LogLine))
        {
            TxtLog.AppendText(progress.LogLine + Environment.NewLine);
            TxtLog.ScrollToEnd();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        BtnCancel.IsEnabled = false;
        BtnCancel.Content = "Cancelando...";
        TxtCurrentDriver.Text = "Deteniendo después del driver actual...";
        _cts.Cancel();
    }
}
