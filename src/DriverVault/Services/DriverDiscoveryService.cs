using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using DriverVault.Models;

namespace DriverVault.Services;

/// <summary>
/// Descubre los drivers instalados en Windows combinando WMI (dispositivos activos)
/// con el almacén DriverStore / C:\Windows\INF (paquetes OEM).
/// </summary>
public sealed class DriverDiscoveryService
{
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupGetInfDriverStoreLocationW(
        string fileName,
        IntPtr alternatePlatformInfo,
        string? localeName,
        StringBuilder returnBuffer,
        int returnBufferSize,
        out int requiredSize);

    public async Task<List<DriverEntry>> DiscoverDriversAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => DiscoverDriversCore(cancellationToken), cancellationToken);
    }

    private static List<DriverEntry> DiscoverDriversCore(CancellationToken cancellationToken)
    {
        var wmiByInf = new Dictionary<string, List<WmiDriverRow>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceName, FriendlyName, Description, DeviceClass, DriverProviderName, DriverVersion, DriverDate, InfName, Manufacturer, IsSigned, Signer, HardWareID " +
                "FROM Win32_PnPSignedDriver");

            foreach (ManagementObject obj in searcher.Get())
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? infName = obj["InfName"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(infName)) continue;

                string deviceName = FirstNonEmpty(
                    obj["FriendlyName"]?.ToString(),
                    obj["DeviceName"]?.ToString(),
                    obj["Description"]?.ToString(),
                    infName);

                var row = new WmiDriverRow(
                    InfName: infName,
                    DeviceName: deviceName,
                    DeviceClass: obj["DeviceClass"]?.ToString()?.Trim() ?? "Other",
                    Provider: obj["DriverProviderName"]?.ToString()?.Trim() ?? string.Empty,
                    Manufacturer: obj["Manufacturer"]?.ToString()?.Trim() ?? string.Empty,
                    Version: obj["DriverVersion"]?.ToString()?.Trim() ?? "-",
                    RawDriverDate: obj["DriverDate"]?.ToString()?.Trim() ?? string.Empty,
                    IsSigned: obj["IsSigned"] is true,
                    Signer: obj["Signer"]?.ToString()?.Trim() ?? string.Empty,
                    HardwareId: obj["HardWareID"]?.ToString()?.Trim() ?? string.Empty);

                if (!wmiByInf.TryGetValue(infName, out var list))
                {
                    list = [];
                    wmiByInf[infName] = list;
                }
                list.Add(row);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Si WMI falla en algún equipo puntual, continuamos con los .inf en C:\Windows\INF
        }

        string windowsInfDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        var oemFiles = Directory.Exists(windowsInfDir)
            ? Directory.EnumerateFiles(windowsInfDir, "oem*.inf", SearchOption.TopDirectoryOnly)
            : [];

        var results = new List<DriverEntry>();
        var processedInfs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Procesar todos los paquetes oem*.inf presentes en el sistema
        foreach (string fullInfPath in oemFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string infFileName = Path.GetFileName(fullInfPath);
            processedInfs.Add(infFileName);

            InfMetadata infMeta = InfParser.ParseFile(fullInfPath);
            string storePath = TryGetDriverStoreLocation(fullInfPath);
            string originalInf = !string.IsNullOrWhiteSpace(storePath)
                ? FindOriginalInfName(storePath, infFileName)
                : infFileName;
            long folderSize = CalculateDirectorySize(storePath);

            bool hasActive = wmiByInf.TryGetValue(infFileName, out var wmiRows) && wmiRows.Count > 0;
            WmiDriverRow? primary = hasActive ? wmiRows![0] : null;

            string deviceName = primary is not null
                ? (wmiRows!.Count > 1 ? $"{primary.DeviceName} (+{wmiRows.Count - 1} disp.)" : primary.DeviceName)
                : Path.GetFileNameWithoutExtension(originalInf);

            string provider = FirstNonEmpty(primary?.Provider, infMeta.Provider, primary?.Manufacturer, "Desconocido");
            string manufacturer = FirstNonEmpty(primary?.Manufacturer, provider, "Desconocido");
            string deviceClass = FirstNonEmpty(primary?.DeviceClass, infMeta.DeviceClass, "Other");
            string version = FirstNonEmpty(primary?.Version, infMeta.Version, "-");
            (string dateText, DateTime? parsedDate) = ResolveDate(primary?.RawDriverDate, infMeta.DriverDate, infMeta.ParsedDriverDate);
            bool isMicrosoft = IsMicrosoftProvider(provider, manufacturer);

            results.Add(new DriverEntry
            {
                InfName = infFileName,
                OriginalInfName = originalInf,
                DeviceName = deviceName,
                DeviceClass = deviceClass,
                CategoryFolder = PathOrganizer.MapCategoryFolder(deviceClass),
                Provider = provider,
                Manufacturer = manufacturer,
                Version = version,
                DriverDate = dateText,
                ParsedDriverDate = parsedDate,
                IsSigned = primary?.IsSigned ?? !string.IsNullOrWhiteSpace(infMeta.CatalogFile),
                Signer = primary?.Signer ?? string.Empty,
                IsThirdParty = !isMicrosoft,
                IsActiveDevice = hasActive,
                IsOemPackage = true,
                DriverStorePath = storePath,
                EstimatedSizeBytes = folderSize,
                HardwareId = primary?.HardwareId ?? string.Empty,
                IsSelected = !isMicrosoft
            });
        }

        // 2. Agregar drivers nativos/inbox activos por si el usuario activa "Incluir integrados Windows"
        foreach ((string infName, List<WmiDriverRow> rows) in wmiByInf)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processedInfs.Contains(infName)) continue;

            WmiDriverRow primary = rows[0];
            string fullInfPath = Path.Combine(windowsInfDir, infName);
            string storePath = File.Exists(fullInfPath) ? TryGetDriverStoreLocation(fullInfPath) : string.Empty;
            long folderSize = CalculateDirectorySize(storePath);

            string provider = FirstNonEmpty(primary.Provider, primary.Manufacturer, "Microsoft");
            string manufacturer = FirstNonEmpty(primary.Manufacturer, provider, "Microsoft");
            (string dateText, DateTime? parsedDate) = ResolveDate(primary.RawDriverDate, "-", null);

            results.Add(new DriverEntry
            {
                InfName = infName,
                OriginalInfName = infName,
                DeviceName = rows.Count > 1 ? $"{primary.DeviceName} (+{rows.Count - 1} disp.)" : primary.DeviceName,
                DeviceClass = primary.DeviceClass,
                CategoryFolder = PathOrganizer.MapCategoryFolder(primary.DeviceClass),
                Provider = provider,
                Manufacturer = manufacturer,
                Version = string.IsNullOrWhiteSpace(primary.Version) ? "-" : primary.Version,
                DriverDate = dateText,
                ParsedDriverDate = parsedDate,
                IsSigned = primary.IsSigned,
                Signer = primary.Signer,
                IsThirdParty = false,
                IsActiveDevice = true,
                IsOemPackage = false,
                DriverStorePath = storePath,
                EstimatedSizeBytes = folderSize,
                HardwareId = primary.HardwareId,
                IsSelected = false
            });
        }

        return results
            .OrderByDescending(d => d.IsThirdParty)
            .ThenByDescending(d => d.IsActiveDevice)
            .ThenBy(d => d.CategoryFolder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.PrimaryDisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Mantiene solo la versión más reciente/activa cuando hay paquetes duplicados en DriverStore.
    /// </summary>
    public static HashSet<string> IdentifySupersededInfNames(IReadOnlyList<DriverEntry> drivers)
    {
        var superseded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var groups = drivers
            .Where(d => d.IsOemPackage && !string.IsNullOrWhiteSpace(d.OriginalInfName))
            .GroupBy(d => $"{d.OriginalInfName.ToLowerInvariant()}|{d.Provider.ToLowerInvariant()}");

        foreach (var group in groups)
        {
            if (group.Count() <= 1) continue;

            var ordered = group
                .OrderByDescending(d => d.IsActiveDevice)
                .ThenByDescending(d => d.ParsedDriverDate ?? DateTime.MinValue)
                .ThenByDescending(d => TryParseVersion(d.Version))
                .ToList();

            for (int i = 1; i < ordered.Count; i++)
            {
                superseded.Add(ordered[i].InfName);
            }
        }

        return superseded;
    }

    private static Version TryParseVersion(string raw)
    {
        return Version.TryParse(raw, out Version? v) ? v : new Version(0, 0);
    }

    private static string TryGetDriverStoreLocation(string fullInfPath)
    {
        try
        {
            var sb = new StringBuilder(1024);
            if (SetupGetInfDriverStoreLocationW(fullInfPath, IntPtr.Zero, null, sb, sb.Capacity, out _))
            {
                string resolvedInf = sb.ToString();
                string? dir = Path.GetDirectoryName(resolvedInf);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                    return dir;
            }
        }
        catch
        {
            // Fallback silencioso
        }

        return string.Empty;
    }

    private static string FindOriginalInfName(string storePath, string fallback)
    {
        try
        {
            string? firstInf = Directory.EnumerateFiles(storePath, "*.inf", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(firstInf) ? fallback : firstInf;
        }
        catch
        {
            return fallback;
        }
    }

    private static long CalculateDirectorySize(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return 0;

        try
        {
            return new DirectoryInfo(directoryPath)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsMicrosoftProvider(string provider, string manufacturer)
    {
        return provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
            || manufacturer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
    }

    private static (string Text, DateTime? Parsed) ResolveDate(string? wmiRawDate, string infDate, DateTime? infParsed)
    {
        if (!string.IsNullOrWhiteSpace(wmiRawDate))
        {
            try
            {
                DateTime dt = ManagementDateTimeConverter.ToDateTime(wmiRawDate);
                return (dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dt);
            }
            catch
            {
                // Ignorar si no es formato DMTF válido
            }
        }

        return (infDate, infParsed);
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (string? c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c))
                return c.Trim();
        }
        return string.Empty;
    }

    private sealed record WmiDriverRow(
        string InfName,
        string DeviceName,
        string DeviceClass,
        string Provider,
        string Manufacturer,
        string Version,
        string RawDriverDate,
        bool IsSigned,
        string Signer,
        string HardwareId);
}
