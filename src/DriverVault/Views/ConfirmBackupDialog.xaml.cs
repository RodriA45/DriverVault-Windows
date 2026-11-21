using System.IO;
using System.Windows;
using DriverVault.Models;
using DriverVault.Services;

namespace DriverVault.Views;

public partial class ConfirmBackupDialog : Window
{
    public ExistingFolderPolicy SelectedFolderPolicy =>
        RbMergeOverwrite.IsChecked == true
            ? ExistingFolderPolicy.MergeOrOverwrite
            : ExistingFolderPolicy.CreateUniqueWithTimestamp;

    public ConfirmBackupDialog(
        string finalOutputPath,
        string orgModeLabel,
        int driverCount,
        int subfolderCount,
        long estimatedBytes,
        long freeBytes,
        bool isAdmin,
        bool folderAlreadyExists,
        string extrasDescription)
    {
        InitializeComponent();
        MaxHeight = Math.Max(420, SystemParameters.WorkArea.Height * 0.90);
        MaxWidth = Math.Max(500, SystemParameters.WorkArea.Width * 0.94);

        TxtSubfolderNotice.Text =
            $"Se creará la carpeta \"{Path.GetFileName(finalOutputPath)}\" y dentro de ella se generarán aproximadamente {subfolderCount} subcarpetas organizadas por {orgModeLabel}. Cada driver quedará aislado en su propia subcarpeta para evitar conflictos.";

        TxtDestinationPath.Text = finalOutputPath;
        TxtDriverCount.Text = $"{driverCount} paquetes de controladores";
        TxtOrgMode.Text = orgModeLabel;
        TxtSpaceCheck.Text = $"{DriverEntry.FormatBytes(estimatedBytes)} estimados · {DriverEntry.FormatBytes(freeBytes)} disponibles";
        TxtAdminStatus.Text = isAdmin
            ? "Ejecutando como Administrador (acceso completo)"
            : "Usuario estándar (exporta paquetes OEM; algunos drivers protegidos pueden requerir Admin)";
        TxtExtras.Text = string.IsNullOrWhiteSpace(extrasDescription) ? "Ninguno" : extrasDescription;

        if (folderAlreadyExists)
        {
            ExistingFolderPanel.Visibility = Visibility.Visible;
        }

        if (freeBytes > 0 && estimatedBytes > 0 && freeBytes < estimatedBytes + (50L * 1024 * 1024))
        {
            LowSpacePanel.Visibility = Visibility.Visible;
            TxtLowSpaceWarning.Text =
                $"Aviso de espacio: El disco destino tiene {DriverEntry.FormatBytes(freeBytes)} libres y el respaldo estima {DriverEntry.FormatBytes(estimatedBytes)}. Liberá espacio o elegí otro disco antes de continuar.";
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
