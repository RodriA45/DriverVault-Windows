using System.Collections.ObjectModel;

namespace DriverVault.Models;

/// <summary>
/// Nodo de vista previa para mostrar exactamente qué subcarpetas se crearán antes de extraer.
/// </summary>
public sealed class FolderPreviewNode
{
    public required string Name { get; init; }
    public string Meta { get; init; } = string.Empty;
    public bool IsFolder { get; init; } = true;
    public ObservableCollection<FolderPreviewNode> Children { get; } = [];
}
