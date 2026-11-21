namespace DriverVault.Models;

public sealed class SystemProfile
{
    public string MachineName { get; init; } = Environment.MachineName;
    public string OsCaption { get; init; } = "Windows";
    public string OsVersion { get; init; } = Environment.OSVersion.Version.ToString();
    public string Architecture { get; init; } = Environment.Is64BitOperatingSystem ? "x64" : "x86";
    public string ManufacturerModel { get; init; } = "PC";
    public string TotalRamFormatted { get; init; } = "-";
    public bool IsAdministrator { get; init; }
}
