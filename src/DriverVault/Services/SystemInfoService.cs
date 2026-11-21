using System.IO;
using System.Management;
using System.Security.Principal;
using DriverVault.Models;

namespace DriverVault.Services;

/// <summary>
/// Obtiene información del equipo actual, permisos de administrador y espacio libre en disco.
/// </summary>
public static class SystemInfoService
{
    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static SystemProfile GetSystemProfile()
    {
        string osCaption = "Windows";
        string osVersion = Environment.OSVersion.Version.ToString();
        string model = "PC";
        string ramFormatted = "-";

        try
        {
            using var osSearcher = new ManagementObjectSearcher("SELECT Caption, Version FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in osSearcher.Get())
            {
                osCaption = obj["Caption"]?.ToString()?.Trim() ?? osCaption;
                osVersion = obj["Version"]?.ToString()?.Trim() ?? osVersion;
                break;
            }
        }
        catch
        {
            // Fallback silencioso para entornos sin WMI disponible
        }

        try
        {
            using var csSearcher = new ManagementObjectSearcher("SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in csSearcher.Get())
            {
                string mfg = obj["Manufacturer"]?.ToString()?.Trim() ?? string.Empty;
                string mdl = obj["Model"]?.ToString()?.Trim() ?? string.Empty;
                model = $"{mfg} {mdl}".Trim();
                if (string.IsNullOrWhiteSpace(model)) model = "PC";

                if (ulong.TryParse(obj["TotalPhysicalMemory"]?.ToString(), out ulong bytes))
                {
                    double gb = bytes / (1024d * 1024d * 1024d);
                    ramFormatted = $"{gb:0.0} GB RAM";
                }
                break;
            }
        }
        catch
        {
            // Fallback silencioso
        }

        return new SystemProfile
        {
            MachineName = Environment.MachineName,
            OsCaption = osCaption,
            OsVersion = osVersion,
            Architecture = Environment.Is64BitOperatingSystem ? "64 bits (x64)" : "32 bits (x86)",
            ManufacturerModel = model,
            TotalRamFormatted = ramFormatted,
            IsAdministrator = IsRunningAsAdministrator()
        };
    }

    public static (bool IsValid, long FreeBytes, string DriveLabel) GetDestinationDriveInfo(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return (false, 0, "-");

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
                return (false, 0, "-");

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return (false, 0, root);

            return (true, drive.AvailableFreeSpace, root);
        }
        catch
        {
            return (false, 0, "-");
        }
    }
}
