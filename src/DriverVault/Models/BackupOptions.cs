namespace DriverVault.Models;

public enum ExistingFolderPolicy
{
    CreateUniqueWithTimestamp,
    MergeOrOverwrite
}

/// <summary>
/// Opciones elegidas por el usuario para generar el respaldo de drivers.
/// </summary>
public sealed class BackupOptions
{
    public required string BackupName { get; init; }
    public bool IncludeDatePrefix { get; init; } = true;
    public required string DestinationRoot { get; init; }
    public FolderOrganizationMode OrganizationMode { get; init; } = FolderOrganizationMode.CategoryAndManufacturer;
    public bool GenerateInstallerScript { get; init; } = true;
    public bool GenerateInventoryReport { get; init; } = true;
    public bool CompressToZip { get; init; } = false;
    public bool KeepZipOnly { get; init; } = false;
    public ExistingFolderPolicy FolderPolicy { get; init; } = ExistingFolderPolicy.CreateUniqueWithTimestamp;
}
