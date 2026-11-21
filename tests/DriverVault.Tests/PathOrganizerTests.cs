using DriverVault.Models;
using DriverVault.Services;
using Xunit;

namespace DriverVault.Tests;

public class PathOrganizerTests
{
    [Fact]
    public void SanitizeSegment_RemovesInvalidCharsAndReservedNames()
    {
        Assert.Equal("NVIDIA_Corporation", PathOrganizer.SanitizeSegment("NVIDIA: Corporation*"));
        Assert.Equal("Fallback", PathOrganizer.SanitizeSegment("CON", "Fallback"));
        Assert.Equal("Fallback", PathOrganizer.SanitizeSegment("   ...  ", "Fallback"));
    }

    [Fact]
    public void ResolveBackupFolderName_AppliesDatePrefixWhenEnabled()
    {
        var date = new DateTime(2026, 11, 20);
        string withPrefix = PathOrganizer.ResolveBackupFolderName("Mi PC Vieja", includeDatePrefix: true, date);
        string withoutPrefix = PathOrganizer.ResolveBackupFolderName("Mi PC Vieja", includeDatePrefix: false, date);

        Assert.Equal("2026-11-20_Mi_PC_Vieja", withPrefix);
        Assert.Equal("Mi_PC_Vieja", withoutPrefix);
    }

    [Fact]
    public void ComputeDriverRelativeFolder_HandlesAllModesAndDeduplicates()
    {
        var driver1 = new DriverEntry
        {
            InfName = "oem10.inf",
            DeviceName = "NVIDIA GeForce RTX 4060",
            DeviceClass = "Display",
            CategoryFolder = "Video_y_Graficos",
            Provider = "NVIDIA"
        };

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string catMfg = PathOrganizer.ComputeDriverRelativeFolder(driver1, FolderOrganizationMode.CategoryAndManufacturer, used);
        string dup = PathOrganizer.ComputeDriverRelativeFolder(driver1, FolderOrganizationMode.CategoryAndManufacturer, used);
        string catOnly = PathOrganizer.ComputeDriverRelativeFolder(driver1, FolderOrganizationMode.CategoryOnly);
        string mfgOnly = PathOrganizer.ComputeDriverRelativeFolder(driver1, FolderOrganizationMode.ManufacturerOnly);
        string flat = PathOrganizer.ComputeDriverRelativeFolder(driver1, FolderOrganizationMode.Flat);

        Assert.Equal(Path.Combine("Video_y_Graficos", "NVIDIA", "NVIDIA_GeForce_RTX_4060_oem10"), catMfg);
        Assert.Equal(Path.Combine("Video_y_Graficos", "NVIDIA", "NVIDIA_GeForce_RTX_4060_oem10_2"), dup);
        Assert.Equal(Path.Combine("Video_y_Graficos", "NVIDIA_GeForce_RTX_4060_oem10"), catOnly);
        Assert.Equal(Path.Combine("NVIDIA", "NVIDIA_GeForce_RTX_4060_oem10"), mfgOnly);
        Assert.Equal("NVIDIA_GeForce_RTX_4060_oem10", flat);
    }

    [Fact]
    public void BuildPreviewTree_IncludesSubfoldersAndExtras()
    {
        var drivers = new List<DriverEntry>
        {
            new()
            {
                InfName = "oem1.inf",
                DeviceName = "Realtek PCIe GbE",
                CategoryFolder = "Red_y_WiFi",
                Provider = "Realtek",
                EstimatedSizeBytes = 2 * 1024 * 1024
            }
        };

        List<FolderPreviewNode> tree = PathOrganizer.BuildPreviewTree(
            "2026-11-20_Mi_PC",
            drivers,
            FolderOrganizationMode.CategoryAndManufacturer,
            includeInstallerScript: true,
            includeInventoryReport: true,
            compressToZip: true);

        Assert.Equal(2, tree.Count);
        Assert.Equal("2026-11-20_Mi_PC\\", tree[0].Name);
        Assert.Equal("2026-11-20_Mi_PC.zip", tree[1].Name);
        Assert.Contains(tree[0].Children, c => c.Name == "Red_y_WiFi\\");
        Assert.Contains(tree[0].Children, c => c.Name == "Reinstalar_Drivers.bat");
        Assert.Contains(tree[0].Children, c => c.Name == "Inventario_Drivers.html");
    }
}
