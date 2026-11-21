using System.IO;
using System.IO.Compression;
using DriverVault.Models;
using DriverVault.Services;
using Xunit;

namespace DriverVault.Tests;

public class DriverExportIntegrationTests
{
    [Fact]
    public async Task ExportDriversAsync_RealWindowsDrivers_ExportsSubfoldersScriptReportAndZip()
    {
        var discovery = new DriverDiscoveryService();
        List<DriverEntry> discovered = await discovery.DiscoverDriversAsync();

        Assert.NotEmpty(discovered);

        // Elegir hasta 2 paquetes OEM reales chicos (< 5 MB) para prueba rápida y real con pnputil
        List<DriverEntry> sampleDrivers = discovered
            .Where(d => d.IsOemPackage && d.EstimatedSizeBytes > 0 && d.EstimatedSizeBytes < 5 * 1024 * 1024)
            .Take(2)
            .ToList();

        Assert.NotEmpty(sampleDrivers);

        string tempRoot = Path.Combine(Path.GetTempPath(), $"DriverVault_E2E_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var options = new BackupOptions
            {
                BackupName = "Prueba_E2E",
                IncludeDatePrefix = true,
                DestinationRoot = tempRoot,
                OrganizationMode = FolderOrganizationMode.CategoryAndManufacturer,
                GenerateInstallerScript = true,
                GenerateInventoryReport = true,
                CompressToZip = true,
                KeepZipOnly = false,
                FolderPolicy = ExistingFolderPolicy.CreateUniqueWithTimestamp
            };

            SystemProfile profile = SystemInfoService.GetSystemProfile();
            var exportService = new DriverExportService();

            ExtractionSummary summary = await exportService.ExportDriversAsync(
                sampleDrivers,
                options,
                profile);

            Assert.False(summary.WasCancelled);
            Assert.Equal(sampleDrivers.Count, summary.SucceededCount);
            Assert.Equal(0, summary.FailedCount);
            Assert.True(summary.TotalBytesWritten > 0);
            Assert.True(Directory.Exists(summary.OutputFolderPath));
            Assert.NotNull(summary.InstallerBatPath);
            Assert.True(File.Exists(summary.InstallerBatPath));
            Assert.NotNull(summary.ReportHtmlPath);
            Assert.True(File.Exists(summary.ReportHtmlPath));
            Assert.NotNull(summary.ReportTxtPath);
            Assert.True(File.Exists(summary.ReportTxtPath));
            Assert.NotNull(summary.ZipFilePath);
            Assert.True(File.Exists(summary.ZipFilePath));

            // Verificar que cada driver tenga su subcarpeta creada y archivos .inf reales dentro
            foreach (DriverExportItemResult item in summary.Items)
            {
                string driverDir = Path.Combine(summary.OutputFolderPath, item.TargetRelativeFolder);
                Assert.True(Directory.Exists(driverDir), $"No existe la subcarpeta: {driverDir}");
                string[] infFiles = Directory.GetFiles(driverDir, "*.inf", SearchOption.TopDirectoryOnly);
                Assert.NotEmpty(infFiles);
            }

            // Verificar que el ZIP es válido y contiene el script + archivos
            using ZipArchive archive = ZipFile.OpenRead(summary.ZipFilePath);
            Assert.Contains(archive.Entries, e => e.FullName.EndsWith("Reinstalar_Drivers.bat", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(archive.Entries, e => e.FullName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
