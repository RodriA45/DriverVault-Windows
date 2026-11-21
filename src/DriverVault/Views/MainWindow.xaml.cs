using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DriverVault.Models;
using DriverVault.Services;
using Microsoft.Win32;

namespace DriverVault.Views;

public partial class MainWindow : Window
{
    private readonly DriverDiscoveryService _discoveryService = new();
    private readonly DriverExportService _exportService = new();

    private List<DriverEntry> _allDrivers = [];
    private List<DriverEntry> _visibleDrivers = [];
    private HashSet<string> _supersededInfs = new(StringComparer.OrdinalIgnoreCase);
    private SystemProfile _systemProfile = new();
    private bool _isInitializing = true;

    public MainWindow()
    {
        InitializeComponent();
        AdaptToCurrentScreenWorkArea();

        TxtBackupName.Text = $"Drivers_{PathOrganizer.SanitizeSegment(Environment.MachineName, "MiPC", 32)}";
        TxtDestinationRoot.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Backups_Drivers");

        _isInitializing = false;
        Loaded += MainWindow_Loaded;
    }

    private void AdaptToCurrentScreenWorkArea()
    {
        Rect workArea = SystemParameters.WorkArea;
        if (workArea.Width <= 0 || workArea.Height <= 0) return;

        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;
        MinWidth = Math.Min(MinWidth, Math.Max(640, workArea.Width * 0.90));
        MinHeight = Math.Min(MinHeight, Math.Max(460, workArea.Height * 0.85));
        Width = Math.Min(Width, workArea.Width * 0.94);
        Height = Math.Min(Height, workArea.Height * 0.92);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadSystemAndDriversAsync();

        if (!string.IsNullOrWhiteSpace(App.CaptureDarkPath) && !string.IsNullOrWhiteSpace(App.CaptureLightPath))
        {
            await CaptureThemesAndExitAsync(App.CaptureDarkPath, App.CaptureLightPath);
        }
    }

    private async Task CaptureThemesAndExitAsync(string darkPath, string lightPath)
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(300);
        SaveVisualPng(darkPath);

        App.ApplyTheme(false);
        BtnToggleTheme.Content = "Modo Oscuro";
        TxtStatusBanner.Text = "Tema cambiado a Blanco y Negro (Claro).";
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(250);
        SaveVisualPng(lightPath);

        Application.Current.Shutdown();
    }

    private void SaveVisualPng(string outputPath)
    {
        if (Content is not FrameworkElement rootVisual) return;

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int width = Math.Max(1, (int)Math.Ceiling(rootVisual.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(1, (int)Math.Ceiling(rootVisual.ActualHeight * dpi.DpiScaleY));

        var rtb = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, rootVisual.ActualWidth, rootVisual.ActualHeight));
            dc.DrawRectangle(new VisualBrush(rootVisual), null, new Rect(0, 0, rootVisual.ActualWidth, rootVisual.ActualHeight));
        }
        rtb.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using FileStream fs = File.Create(outputPath);
        encoder.Save(fs);
    }

    private async Task LoadSystemAndDriversAsync()
    {
        BtnRescan.IsEnabled = false;
        BtnExtract.IsEnabled = false;
        TxtStatusBanner.Text = "Escaneando controladores instalados y el almacén DriverStore... esto toma unos segundos.";

        _systemProfile = await Task.Run(SystemInfoService.GetSystemProfile);
        TxtSystemInfoPill.Text = $"{_systemProfile.MachineName} · {_systemProfile.Architecture} · {_systemProfile.TotalRamFormatted}";

        if (_systemProfile.IsAdministrator)
        {
            TxtAdminBadge.Text = "Administrador activo";
            BtnElevateAdmin.Visibility = Visibility.Collapsed;
        }
        else
        {
            TxtAdminBadge.Text = "Usuario estándar";
            BtnElevateAdmin.Visibility = Visibility.Visible;
        }

        try
        {
            _allDrivers = await _discoveryService.DiscoverDriversAsync();
            _supersededInfs = DriverDiscoveryService.IdentifySupersededInfNames(_allDrivers);

            // Deseleccionar versiones viejas duplicadas por defecto
            foreach (DriverEntry driver in _allDrivers)
            {
                if (_supersededInfs.Contains(driver.InfName))
                {
                    driver.IsSelected = false;
                }
            }

            PopulateCategoryDropdown();
            ApplyFiltersAndRefresh();
            UpdateDestinationAndPreview();

            int thirdPartyCount = _allDrivers.Count(d => d.IsThirdParty);
            TxtStatusBanner.Text = _systemProfile.IsAdministrator
                ? $"Escaneo completo: {_allDrivers.Count} drivers detectados ({thirdPartyCount} de terceros). Elegí dónde guardar y revisá la vista previa de subcarpetas."
                : $"Escaneo completo: {_allDrivers.Count} drivers detectados ({thirdPartyCount} de terceros). Podés exportar normalmente; si algún driver protegido falla, usá 'Ejecutar como Admin'.";
        }
        catch (Exception ex)
        {
            TxtStatusBanner.Text = $"Error al escanear drivers: {ex.Message}";
        }
        finally
        {
            BtnRescan.IsEnabled = true;
            UpdateSelectionSummary();
        }
    }

    private void PopulateCategoryDropdown()
    {
        string? prevSelected = CmbCategory.SelectedItem as string;
        var categories = _allDrivers
            .Select(d => d.CategoryFolder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        categories.Insert(0, "Todas las categorías");
        CmbCategory.ItemsSource = categories;
        CmbCategory.SelectedIndex = prevSelected is not null && categories.Contains(prevSelected)
            ? categories.IndexOf(prevSelected)
            : 0;
    }

    private void FiltersChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        TxtSearchPlaceholder.Visibility = string.IsNullOrEmpty(TxtSearch.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyFiltersAndRefresh();
    }

    private void FiltersChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        TxtSearchPlaceholder.Visibility = string.IsNullOrEmpty(TxtSearch.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyFiltersAndRefresh();
    }

    private void FiltersChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        ApplyFiltersAndRefresh();
    }

    private void ApplyFiltersAndRefresh()
    {
        string query = TxtSearch.Text?.Trim() ?? string.Empty;
        string selectedCategory = CmbCategory.SelectedItem as string ?? "Todas las categorías";
        bool hideDuplicates = ChkHideDuplicates.IsChecked == true;

        IEnumerable<DriverEntry> filtered = _allDrivers;

        if (PillThirdParty.IsChecked == true)
            filtered = filtered.Where(d => d.IsThirdParty);
        else if (PillAllOem.IsChecked == true)
            filtered = filtered.Where(d => d.IsOemPackage);
        else if (PillActiveOnly.IsChecked == true)
            filtered = filtered.Where(d => d.IsActiveDevice);
        else if (PillUnsigned.IsChecked == true)
            filtered = filtered.Where(d => !d.IsSigned);

        if (hideDuplicates)
            filtered = filtered.Where(d => !_supersededInfs.Contains(d.InfName));

        if (!selectedCategory.Equals("Todas las categorías", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(d => d.CategoryFolder.Equals(selectedCategory, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(d =>
                d.PrimaryDisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                d.Provider.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                d.CategoryFolder.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                d.InfName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                d.OriginalInfName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        _visibleDrivers = filtered.ToList();
        ListDrivers.ItemsSource = _visibleDrivers;
        TxtVisibleCount.Text = $"{_visibleDrivers.Count} drivers mostrados (de {_allDrivers.Count} totales)";

        UpdateSelectionSummary();
    }

    private void ListDrivers_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListDrivers.SelectedItem is DriverEntry selected)
        {
            selected.IsSelected = !selected.IsSelected;
            ListDrivers.SelectedItem = null;
            UpdateSelectionSummary();
            UpdateDestinationAndPreview();
        }
    }

    private void DriverCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateSelectionSummary();
        UpdateDestinationAndPreview();
    }

    private void BtnSelectVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (DriverEntry d in _visibleDrivers) d.IsSelected = true;
        UpdateSelectionSummary();
        UpdateDestinationAndPreview();
        TxtStatusBanner.Text = $"Se seleccionaron los {_visibleDrivers.Count} drivers visibles.";
    }

    private void BtnDeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (DriverEntry d in _allDrivers) d.IsSelected = false;
        UpdateSelectionSummary();
        UpdateDestinationAndPreview();
        TxtStatusBanner.Text = "Se deseleccionaron todos los drivers.";
    }

    private void BtnSelectActive_Click(object sender, RoutedEventArgs e)
    {
        foreach (DriverEntry d in _visibleDrivers) d.IsSelected = d.IsActiveDevice;
        UpdateSelectionSummary();
        UpdateDestinationAndPreview();
        TxtStatusBanner.Text = "Seleccionados solo los drivers actualmente en uso en este equipo.";
    }

    private void UpdateSelectionSummary()
    {
        List<DriverEntry> selected = GetSelectedDrivers();
        long bytes = selected.Sum(d => d.EstimatedSizeBytes);
        TxtSelectionSummary.Text = $"{selected.Count} seleccionados · {DriverEntry.FormatBytes(bytes)}";
        BtnExtract.Content = selected.Count > 0
            ? $"Extraer {selected.Count} Drivers ({DriverEntry.FormatBytes(bytes)})"
            : "Extraer Drivers Seleccionados";
        BtnExtract.IsEnabled = selected.Count > 0;
    }

    private List<DriverEntry> GetSelectedDrivers() =>
        _allDrivers.Where(d => d.IsSelected).ToList();

    private void BackupConfigChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        ChkKeepZipOnly.IsEnabled = ChkCompressZip.IsChecked == true;
        if (ChkCompressZip.IsChecked != true) ChkKeepZipOnly.IsChecked = false;
        UpdateDestinationAndPreview();
    }

    private void BackupConfigChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateDestinationAndPreview();
    }

    private void BackupConfigChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateDestinationAndPreview();
    }

    private FolderOrganizationMode GetSelectedOrganizationMode()
    {
        if (CmbOrganizationMode.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<FolderOrganizationMode>(item.Tag?.ToString(), out var mode))
        {
            return mode;
        }
        return FolderOrganizationMode.CategoryAndManufacturer;
    }

    private string GetOrganizationModeLabel(FolderOrganizationMode mode) => mode switch
    {
        FolderOrganizationMode.CategoryAndManufacturer => "Categoría / Fabricante",
        FolderOrganizationMode.CategoryOnly => "Categoría",
        FolderOrganizationMode.ManufacturerOnly => "Fabricante",
        FolderOrganizationMode.Flat => "Carpeta única por driver",
        _ => "Categoría / Fabricante"
    };

    private void UpdateDestinationAndPreview()
    {
        string backupFolderName = PathOrganizer.ResolveBackupFolderName(
            TxtBackupName.Text,
            ChkDatePrefix.IsChecked == true);

        string destRoot = TxtDestinationRoot.Text.Trim();
        string fullPreviewPath = string.IsNullOrWhiteSpace(destRoot)
            ? backupFolderName
            : Path.Combine(destRoot, backupFolderName);

        (bool driveValid, long freeBytes, string driveLabel) = SystemInfoService.GetDestinationDriveInfo(destRoot);
        TxtDiskSpace.Text = driveValid
            ? $"Libre en {driveLabel}: {DriverEntry.FormatBytes(freeBytes)}"
            : "Ruta no verificada";

        bool alreadyExists = !string.IsNullOrWhiteSpace(destRoot) && Directory.Exists(fullPreviewPath);
        FolderOrganizationMode orgMode = GetSelectedOrganizationMode();

        List<DriverEntry> selected = GetSelectedDrivers();
        List<FolderPreviewNode> tree = PathOrganizer.BuildPreviewTree(
            backupFolderName,
            selected,
            orgMode,
            ChkGenerateBat.IsChecked == true,
            ChkGenerateInventory.IsChecked == true,
            ChkCompressZip.IsChecked == true);

        TreeFolderPreview.ItemsSource = tree;
        int leafSubfolders = selected.Count;
        TxtSubfolderPreviewCount.Text = $"{leafSubfolders} subcarpetas de drivers";

        if (alreadyExists)
        {
            TxtPathPreviewNotice.Text =
                $"AVISO: Ya existe \"{backupFolderName}\" en ese destino. Al confirmar podrás elegir crear una carpeta nueva con la hora o combinar archivos. Se crearán {leafSubfolders} subcarpetas ({GetOrganizationModeLabel(orgMode)}).";
        }
        else
        {
            TxtPathPreviewNotice.Text =
                $"Se creará \"{fullPreviewPath}\" y adentro se organizarán automáticamente {leafSubfolders} subcarpetas por {GetOrganizationModeLabel(orgMode)}.";
        }
    }

    private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Elegí la carpeta donde guardar tu respaldo de drivers",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(TxtDestinationRoot.Text) && Directory.Exists(TxtDestinationRoot.Text))
        {
            dialog.InitialDirectory = TxtDestinationRoot.Text;
        }

        if (dialog.ShowDialog(this) == true)
        {
            TxtDestinationRoot.Text = dialog.FolderName;
            TxtStatusBanner.Text = $"Destino elegido: {dialog.FolderName}. Revisá la vista previa de subcarpetas antes de extraer.";
        }
    }

    private void BtnDesktopPreset_Click(object sender, RoutedEventArgs e)
    {
        TxtDestinationRoot.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Backups_Drivers");
        TxtStatusBanner.Text = "Carpeta destino configurada en el Escritorio.";
    }

    private void BtnDocumentsPreset_Click(object sender, RoutedEventArgs e)
    {
        TxtDestinationRoot.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Backups_Drivers");
        TxtStatusBanner.Text = "Carpeta destino configurada en Documentos.";
    }

    private async void BtnExtract_Click(object sender, RoutedEventArgs e)
    {
        List<DriverEntry> selected = GetSelectedDrivers();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Seleccioná al menos un driver de la lista para poder extraerlo.", "DriverVault · Aviso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string destRoot = TxtDestinationRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(destRoot))
        {
            TxtStatusBanner.Text = "Aviso: Primero elegí una carpeta donde guardar los drivers.";
            BtnBrowseFolder_Click(sender, e);
            destRoot = TxtDestinationRoot.Text.Trim();
            if (string.IsNullOrWhiteSpace(destRoot)) return;
        }

        string folderName = PathOrganizer.ResolveBackupFolderName(
            TxtBackupName.Text,
            ChkDatePrefix.IsChecked == true);
        string fullPreviewPath = Path.Combine(destRoot, folderName);
        bool exists = Directory.Exists(fullPreviewPath);
        long estimatedBytes = selected.Sum(d => d.EstimatedSizeBytes);
        (_, long freeBytes, _) = SystemInfoService.GetDestinationDriveInfo(destRoot);
        FolderOrganizationMode orgMode = GetSelectedOrganizationMode();

        var extras = new List<string>();
        if (ChkGenerateBat.IsChecked == true) extras.Add("Reinstalar_Drivers.bat");
        if (ChkGenerateInventory.IsChecked == true) extras.Add("Inventario_Drivers.html y .txt");
        if (ChkCompressZip.IsChecked == true) extras.Add(ChkKeepZipOnly.IsChecked == true ? "Archivo .zip (solo ZIP)" : "Archivo .zip");

        var confirmDialog = new ConfirmBackupDialog(
            finalOutputPath: fullPreviewPath,
            orgModeLabel: GetOrganizationModeLabel(orgMode),
            driverCount: selected.Count,
            subfolderCount: selected.Count,
            estimatedBytes: estimatedBytes,
            freeBytes: freeBytes,
            isAdmin: _systemProfile.IsAdministrator,
            folderAlreadyExists: exists,
            extrasDescription: string.Join(" · ", extras))
        {
            Owner = this
        };

        if (confirmDialog.ShowDialog() != true)
        {
            TxtStatusBanner.Text = "Extracción cancelada antes de empezar. No se creó ninguna carpeta.";
            return;
        }

        var options = new BackupOptions
        {
            BackupName = TxtBackupName.Text,
            IncludeDatePrefix = ChkDatePrefix.IsChecked == true,
            DestinationRoot = destRoot,
            OrganizationMode = orgMode,
            GenerateInstallerScript = ChkGenerateBat.IsChecked == true,
            GenerateInventoryReport = ChkGenerateInventory.IsChecked == true,
            CompressToZip = ChkCompressZip.IsChecked == true,
            KeepZipOnly = ChkKeepZipOnly.IsChecked == true,
            FolderPolicy = confirmDialog.SelectedFolderPolicy
        };

        using var cts = new CancellationTokenSource();
        var progressDialog = new ProgressDialog(cts, selected.Count) { Owner = this };
        var progressReporter = new Progress<ExportProgress>(p => progressDialog.ReportProgress(p));

        IsEnabled = false;
        progressDialog.Show();

        ExtractionSummary summary;
        try
        {
            summary = await _exportService.ExportDriversAsync(
                selected,
                options,
                _systemProfile,
                progressReporter,
                cts.Token);
        }
        finally
        {
            progressDialog.Close();
            IsEnabled = true;
        }

        TxtStatusBanner.Text = summary.WasCancelled
            ? $"Proceso detenido: se exportaron {summary.SucceededCount} drivers en {summary.OutputFolderPath}."
            : $"Respaldo terminado: {summary.SucceededCount} drivers guardados en {summary.OutputFolderPath}.";

        var summaryDialog = new SummaryDialog(summary) { Owner = this };
        summaryDialog.ShowDialog();
        UpdateDestinationAndPreview();
    }

    private void BtnToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        bool switchToDark = !App.IsDarkTheme;
        App.ApplyTheme(switchToDark);
        BtnToggleTheme.Content = switchToDark ? "Modo Claro" : "Modo Oscuro";
        TxtStatusBanner.Text = switchToDark
            ? "Tema cambiado a Blanco y Negro (Oscuro)."
            : "Tema cambiado a Blanco y Negro (Claro).";
    }

    private async void BtnRescan_Click(object sender, RoutedEventArgs e)
    {
        await LoadSystemAndDriversAsync();
    }

    private void BtnElevateAdmin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas"
            });
            Application.Current.Shutdown();
        }
        catch
        {
            TxtStatusBanner.Text = "No se concedieron permisos de Administrador. La app sigue funcionando en modo estándar.";
        }
    }
}
