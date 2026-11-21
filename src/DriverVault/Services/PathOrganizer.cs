using System.IO;
using System.Text;
using DriverVault.Models;

namespace DriverVault.Services;

/// <summary>
/// Organiza rutas limpias, seguras y predecibles para subcarpetas de drivers en Windows.
/// </summary>
public static class PathOrganizer
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly Dictionary<string, string> CategoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Display"] = "Video_y_Graficos",
        ["Net"] = "Red_y_WiFi",
        ["NetTrans"] = "Red_y_WiFi",
        ["NetService"] = "Red_y_WiFi",
        ["NetClient"] = "Red_y_WiFi",
        ["MEDIA"] = "Audio_y_Multimedia",
        ["AudioEndpoint"] = "Audio_y_Multimedia",
        ["AUDIOPROCESSINGOBJECT"] = "Audio_y_Multimedia",
        ["Bluetooth"] = "Bluetooth",
        ["USB"] = "USB_y_Controladores",
        ["USBDevice"] = "USB_y_Controladores",
        ["SCSIAdapter"] = "Almacenamiento",
        ["HDC"] = "Almacenamiento",
        ["DiskDrive"] = "Almacenamiento",
        ["NVMe"] = "Almacenamiento",
        ["HIDClass"] = "Perifericos_HID",
        ["Keyboard"] = "Teclado_y_Mouse",
        ["Mouse"] = "Teclado_y_Mouse",
        ["Printer"] = "Impresoras_y_Escaner",
        ["PrintQueue"] = "Impresoras_y_Escaner",
        ["Image"] = "Impresoras_y_Escaner",
        ["Camera"] = "Camaras",
        ["Monitor"] = "Monitores",
        ["Biometric"] = "Biometria_y_Huella",
        ["System"] = "Chipset_y_Sistema",
        ["Processor"] = "Chipset_y_Sistema",
        ["Firmware"] = "Firmware_y_BIOS",
        ["Battery"] = "Energia_y_Bateria",
        ["Ports"] = "Puertos_COM_y_LPT",
        ["Extension"] = "Extensiones_de_Driver",
        ["SoftwareComponent"] = "Componentes_de_Software",
        ["SecurityDevices"] = "Seguridad_TPM",
        ["SmartCardReader"] = "Seguridad_TPM",
        ["Sensor"] = "Sensores"
    };

    public static string MapCategoryFolder(string? deviceClass)
    {
        if (string.IsNullOrWhiteSpace(deviceClass))
            return "Otros_Dispositivos";

        if (CategoryMap.TryGetValue(deviceClass.Trim(), out string? friendly))
            return friendly;

        string sanitized = SanitizeSegment(deviceClass, "Otros_Dispositivos");
        return sanitized.Equals("Other", StringComparison.OrdinalIgnoreCase)
            ? "Otros_Dispositivos"
            : sanitized;
    }

    public static string SanitizeSegment(string? input, string fallback = "Sin_Nombre", int maxLength = 52)
    {
        if (string.IsNullOrWhiteSpace(input))
            return fallback;

        char[] invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);

        foreach (char ch in input.Trim())
        {
            if (invalidChars.Contains(ch) || char.IsControl(ch))
            {
                sb.Append('_');
            }
            else if (ch == ' ')
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(ch);
            }
        }

        string cleaned = sb.ToString().Trim('_', '.', ' ');
        while (cleaned.Contains("__", StringComparison.Ordinal))
        {
            cleaned = cleaned.Replace("__", "_", StringComparison.Ordinal);
        }

        if (cleaned.Length > maxLength)
        {
            cleaned = cleaned[..maxLength].TrimEnd('_', '.');
        }

        if (string.IsNullOrWhiteSpace(cleaned) || ReservedWindowsNames.Contains(cleaned))
            return fallback;

        return cleaned;
    }

    public static string ResolveBackupFolderName(string? customName, bool includeDatePrefix, DateTime? timestamp = null)
    {
        string baseName = SanitizeSegment(customName, $"Drivers_{Environment.MachineName}", 64);
        if (!includeDatePrefix)
            return baseName;

        string date = (timestamp ?? DateTime.Now).ToString("yyyy-MM-dd");
        return baseName.StartsWith(date, StringComparison.Ordinal)
            ? baseName
            : $"{date}_{baseName}";
    }

    public static string ResolveUniqueOutputDirectory(string destinationRoot, string backupFolderName, ExistingFolderPolicy policy)
    {
        string basePath = Path.Combine(destinationRoot, backupFolderName);
        if (policy == ExistingFolderPolicy.MergeOrOverwrite || !Directory.Exists(basePath))
            return basePath;

        string suffix = DateTime.Now.ToString("HHmmss");
        string candidate = Path.Combine(destinationRoot, $"{backupFolderName}_{suffix}");
        int counter = 2;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(destinationRoot, $"{backupFolderName}_{suffix}_{counter}");
            counter++;
        }
        return candidate;
    }

    public static string ComputeDriverRelativeFolder(
        DriverEntry driver,
        FolderOrganizationMode mode,
        ISet<string>? usedFolders = null)
    {
        string category = SanitizeSegment(
            string.IsNullOrWhiteSpace(driver.CategoryFolder) ? MapCategoryFolder(driver.DeviceClass) : driver.CategoryFolder,
            "Otros_Dispositivos",
            36);

        string provider = SanitizeSegment(
            string.IsNullOrWhiteSpace(driver.Provider) ? driver.Manufacturer : driver.Provider,
            "Fabricante_Desconocido",
            34);

        string infStem = SanitizeSegment(Path.GetFileNameWithoutExtension(driver.InfName), "driver", 20);
        string deviceStem = SanitizeSegment(driver.PrimaryDisplayName, infStem, 40);
        string packageFolder = $"{deviceStem}_{infStem}";

        string relativePath = mode switch
        {
            FolderOrganizationMode.CategoryAndManufacturer => Path.Combine(category, provider, packageFolder),
            FolderOrganizationMode.CategoryOnly => Path.Combine(category, packageFolder),
            FolderOrganizationMode.ManufacturerOnly => Path.Combine(provider, packageFolder),
            FolderOrganizationMode.Flat => packageFolder,
            _ => Path.Combine(category, provider, packageFolder)
        };

        if (usedFolders is not null)
        {
            string unique = relativePath;
            int index = 2;
            while (!usedFolders.Add(unique))
            {
                unique = $"{relativePath}_{index}";
                index++;
            }
            return unique;
        }

        return relativePath;
    }

    public static List<FolderPreviewNode> BuildPreviewTree(
        string rootFolderName,
        IReadOnlyList<DriverEntry> selectedDrivers,
        FolderOrganizationMode mode,
        bool includeInstallerScript,
        bool includeInventoryReport,
        bool compressToZip)
    {
        var rootNode = new FolderPreviewNode
        {
            Name = $"{rootFolderName}\\",
            Meta = $"{selectedDrivers.Count} drivers",
            IsFolder = true
        };

        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DriverEntry driver in selectedDrivers)
        {
            string relative = ComputeDriverRelativeFolder(driver, mode, usedFolders);
            string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            FolderPreviewNode current = rootNode;
            for (int i = 0; i < segments.Length; i++)
            {
                bool isLeafPackage = i == segments.Length - 1;
                string segName = $"{segments[i]}\\";

                FolderPreviewNode? existing = current.Children.FirstOrDefault(c =>
                    c.IsFolder && c.Name.Equals(segName, StringComparison.OrdinalIgnoreCase));

                if (existing is null)
                {
                    existing = new FolderPreviewNode
                    {
                        Name = segName,
                        Meta = isLeafPackage ? $"{driver.InfName} · {driver.SizeFormatted}" : string.Empty,
                        IsFolder = true
                    };
                    current.Children.Add(existing);
                }

                current = existing;
            }
        }

        // Ordenar alfabéticamente y actualizar conteo de subcarpetas
        SortAndAnnotateFolders(rootNode);

        if (includeInstallerScript)
        {
            rootNode.Children.Add(new FolderPreviewNode
            {
                Name = "Reinstalar_Drivers.bat",
                Meta = "Instalador automático (Admin)",
                IsFolder = false
            });
        }

        if (includeInventoryReport)
        {
            rootNode.Children.Add(new FolderPreviewNode
            {
                Name = "Inventario_Drivers.html",
                Meta = "Reporte visual B&N",
                IsFolder = false
            });
            rootNode.Children.Add(new FolderPreviewNode
            {
                Name = "Inventario_Drivers.txt",
                Meta = "Lista de texto plano",
                IsFolder = false
            });
        }

        var result = new List<FolderPreviewNode> { rootNode };
        if (compressToZip)
        {
            result.Add(new FolderPreviewNode
            {
                Name = $"{rootFolderName}.zip",
                Meta = "Archivo comprimido listo para guardar/subir",
                IsFolder = false
            });
        }

        return result;
    }

    private static void SortAndAnnotateFolders(FolderPreviewNode node)
    {
        var sorted = node.Children
            .OrderByDescending(c => c.IsFolder)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        node.Children.Clear();
        foreach (var child in sorted)
        {
            if (child.IsFolder && child.Children.Count > 0)
            {
                SortAndAnnotateFolders(child);
            }
            node.Children.Add(child);
        }
    }
}
