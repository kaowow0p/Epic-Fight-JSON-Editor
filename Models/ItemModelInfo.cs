namespace EpicFightJsonGeneratorApp.Models;

public sealed class ItemModelInfo
{
    public ItemModelInfo(
        string itemName,
        string filePath,
        string parent,
        string modId,
        string? guiModelReference,
        string? guiModelFilePath,
        string? guiTextureReference,
        string? guiTextureFilePath,
        byte[]? guiTextureBytes,
        string guiResolveStatus,
        string? guiResolveError)
    {
        ItemName = itemName;
        FilePath = filePath;
        Parent = parent;
        ModId = modId;
        GuiModelReference = guiModelReference;
        GuiModelFilePath = guiModelFilePath;
        GuiTextureReference = guiTextureReference;
        GuiTextureFilePath = guiTextureFilePath;
        GuiTextureBytes = guiTextureBytes;
        GuiResolveStatus = guiResolveStatus;
        GuiResolveError = guiResolveError;
    }

    public string ItemName { get; }

    public string FilePath { get; }

    public string Parent { get; }

    public string ModId { get; }

    public string? GuiModelReference { get; }

    public string? GuiModelFilePath { get; }

    public string? GuiTextureReference { get; }

    public string? GuiTextureFilePath { get; }

    public byte[]? GuiTextureBytes { get; }

    public string GuiResolveStatus { get; }

    public string? GuiResolveError { get; }

    public override string ToString()
    {
        return $"{ItemName} ({ModId})";
    }
}
