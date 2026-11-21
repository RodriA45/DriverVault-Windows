using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DriverVault.Models;

/// <summary>
/// Representa un controlador (driver) detectado en el sistema.
/// </summary>
public sealed class DriverEntry : INotifyPropertyChanged
{
    private bool _isSelected;

    public required string InfName { get; init; }
    public string OriginalInfName { get; init; } = string.Empty;
    public required string DeviceName { get; init; }
    public string DeviceClass { get; init; } = "Other";
    public string CategoryFolder { get; init; } = "Otros";
    public string Provider { get; init; } = "Desconocido";
    public string Manufacturer { get; init; } = "Desconocido";
    public string Version { get; init; } = "-";
    public string DriverDate { get; init; } = "-";
    public DateTime? ParsedDriverDate { get; init; }
    public bool IsSigned { get; init; }
    public string Signer { get; init; } = string.Empty;
    public bool IsThirdParty { get; init; }
    public bool IsActiveDevice { get; init; }
    public bool IsOemPackage { get; init; }
    public string DriverStorePath { get; init; } = string.Empty;
    public long EstimatedSizeBytes { get; init; }
    public string HardwareId { get; init; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string SizeFormatted => FormatBytes(EstimatedSizeBytes);

    public string OriginBadge => IsThirdParty ? "TERCERO" : "MICROSOFT";

    public string ActivityBadge => IsActiveDevice ? "EN USO" : "ALMACÉN";

    public string SignatureBadge => IsSigned ? "FIRMADO" : "SIN FIRMAR";

    public string PrimaryDisplayName =>
        string.IsNullOrWhiteSpace(DeviceName) ? OriginalInfName : DeviceName;

    public string SubtitleLine =>
        $"{Provider} · {CategoryFolder} · v{Version} · {DriverDate} · {InfName}";

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "< 0.1 MB";
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit <= 1 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
