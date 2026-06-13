using System.IO.Compression;
using System.Text.Json;
using EpicFightJsonGeneratorApp.Models;

namespace EpicFightJsonGeneratorApp.Services;

public sealed class ItemModelScanner
{
    private const int MaxModelParentDepth = 20;
    private const string PrimaryHandheldParent = "minecraft:item/handheld";
    private const string LegacyHandheldParent = "item/handheld";
    private const string TextureFoundStatus = "GUI model found, texture found";
    private const string TextureMissingStatus = "GUI model found, texture missing";
    private const string GuiModelMissingStatus = "GUI model missing";
    private const string NoGuiParentStatus = "no perspectives.gui.parent";

    public async Task<IReadOnlyList<ItemModelInfo>> ScanHandheldItemsAsync(string selectedProjectPath)
    {
        if (IsJarFile(selectedProjectPath))
        {
            return await ScanJarAsync(selectedProjectPath);
        }

        return await ScanResourcesFolderAsync(selectedProjectPath);
    }

    public string ResolveResourcesFolder(string selectedProjectPath)
    {
        if (string.IsNullOrWhiteSpace(selectedProjectPath))
        {
            throw new ArgumentException("Project folder or JAR file is not selected.", nameof(selectedProjectPath));
        }

        if (Directory.Exists(Path.Combine(selectedProjectPath, "assets")))
        {
            return selectedProjectPath;
        }

        string nestedResourcesFolder = Path.Combine(selectedProjectPath, "src", "main", "resources");
        if (Directory.Exists(nestedResourcesFolder))
        {
            return nestedResourcesFolder;
        }

        throw new DirectoryNotFoundException(
            "Resources folder was not found. Select the mod root folder, src/main/resources folder, or a .jar file.");
    }

    private async Task<IReadOnlyList<ItemModelInfo>> ScanResourcesFolderAsync(string selectedProjectPath)
    {
        string resourcesFolder = ResolveResourcesFolder(selectedProjectPath);
        string assetsFolder = Path.Combine(resourcesFolder, "assets");

        if (!Directory.Exists(assetsFolder))
        {
            throw new DirectoryNotFoundException($"Assets folder was not found: {assetsFolder}");
        }

        List<ItemModelInfo> items = new();
        foreach (string modFolder in Directory.EnumerateDirectories(assetsFolder))
        {
            string modId = Path.GetFileName(modFolder);
            string itemModelsFolder = Path.Combine(modFolder, "models", "item");

            if (!Directory.Exists(itemModelsFolder))
            {
                continue;
            }

            IReadOnlyList<ItemModelInfo> modItems = await ScanItemModelsFolderAsync(
                resourcesFolder,
                itemModelsFolder,
                modId);
            items.AddRange(modItems);
        }

        if (items.Count == 0 && !HasAnyItemModelsFolder(assetsFolder))
        {
            throw new DirectoryNotFoundException(
                $"No item model folder was found under: {Path.Combine(assetsFolder, "<modid>", "models", "item")}");
        }

        return SortItems(items);
    }

    private static async Task<IReadOnlyList<ItemModelInfo>> ScanJarAsync(string jarFilePath)
    {
        using ZipArchive archive = ZipFile.OpenRead(jarFilePath);
        List<ItemModelInfo> items = new();

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!TryParseTopLevelItemModelEntry(entry.FullName, out string modId, out string itemName))
            {
                continue;
            }

            await using Stream stream = entry.Open();
            ItemModelData modelData = await ReadItemModelDataAsync(stream);

            if (!IsHandheldParent(modelData.Parent) || IsVariantModel(itemName))
            {
                continue;
            }

            GuiModelResolution guiModel = await ResolveJarGuiModelAsync(
                archive,
                modId,
                itemName,
                modelData.GuiModelReference);

            items.Add(new ItemModelInfo(
                itemName,
                $"{jarFilePath}::{entry.FullName}",
                modelData.Parent!,
                modId,
                guiModel.ModelReference,
                guiModel.ModelFilePath,
                guiModel.TextureReference,
                null,
                guiModel.TextureBytes,
                guiModel.Status,
                guiModel.Error));
        }

        if (items.Count == 0 && !HasAnyJarItemModelsFolder(archive))
        {
            throw new DirectoryNotFoundException("No assets/<modid>/models/item folder was found inside the selected JAR file.");
        }

        return SortItems(items);
    }

    private static async Task<IReadOnlyList<ItemModelInfo>> ScanItemModelsFolderAsync(
        string resourcesFolder,
        string itemModelsFolder,
        string modId)
    {
        List<ItemModelInfo> result = new();

        foreach (string jsonFilePath in Directory.EnumerateFiles(itemModelsFolder, "*.json", SearchOption.TopDirectoryOnly))
        {
            string itemName = Path.GetFileNameWithoutExtension(jsonFilePath);
            await using FileStream stream = File.OpenRead(jsonFilePath);
            ItemModelData modelData = await ReadItemModelDataAsync(stream);

            if (!IsHandheldParent(modelData.Parent) || IsVariantModel(itemName))
            {
                continue;
            }

            GuiModelResolution guiModel = await ResolveFileSystemGuiModelAsync(
                resourcesFolder,
                itemModelsFolder,
                modId,
                itemName,
                modelData.GuiModelReference);

            result.Add(new ItemModelInfo(
                itemName,
                jsonFilePath,
                modelData.Parent!,
                modId,
                guiModel.ModelReference,
                guiModel.ModelFilePath,
                guiModel.TextureReference,
                guiModel.TextureFilePath,
                null,
                guiModel.Status,
                guiModel.Error));
        }

        return result;
    }

    private static async Task<GuiModelResolution> ResolveFileSystemGuiModelAsync(
        string resourcesFolder,
        string itemModelsFolder,
        string defaultNamespace,
        string itemName,
        string? guiModelReference)
    {
        bool hasExplicitGuiParent = !string.IsNullOrWhiteSpace(guiModelReference);
        string? resolvedModelReference = hasExplicitGuiParent ? guiModelReference!.Trim() : null;
        string? modelFilePath = null;
        string modelNamespace = defaultNamespace;

        if (hasExplicitGuiParent)
        {
            ResourceLocation modelLocation = ParseResourceLocation(resolvedModelReference!, defaultNamespace);
            modelNamespace = modelLocation.Namespace;
            modelFilePath = FindModelFile(resourcesFolder, modelLocation);
        }
        else
        {
            (resolvedModelReference, modelFilePath, modelNamespace) = FindFallbackGuiModel(
                resourcesFolder,
                itemModelsFolder,
                defaultNamespace,
                itemName);
        }

        if (string.IsNullOrWhiteSpace(modelFilePath) || !File.Exists(modelFilePath))
        {
            string status = hasExplicitGuiParent ? GuiModelMissingStatus : NoGuiParentStatus;
            string error = hasExplicitGuiParent ? "GUI model not found" : "No GUI parent in item model";
            return new GuiModelResolution(resolvedModelReference, modelFilePath, null, null, null, status, error);
        }

        try
        {
            TextureResolution texture = await ResolveFileSystemTextureFromModelChainAsync(
                resourcesFolder,
                modelFilePath,
                modelNamespace);

            if (string.IsNullOrWhiteSpace(texture.Reference))
            {
                return new GuiModelResolution(
                    resolvedModelReference,
                    modelFilePath,
                    null,
                    null,
                    null,
                    TextureMissingStatus,
                    "Texture not found");
            }

            string? textureFilePath = ResolveTextureFilePath(resourcesFolder, texture.Reference, texture.Namespace);
            string status = textureFilePath is null ? TextureMissingStatus : TextureFoundStatus;
            string? error = textureFilePath is null ? "Texture not found" : null;

            return new GuiModelResolution(
                resolvedModelReference,
                modelFilePath,
                texture.Reference,
                textureFilePath,
                null,
                status,
                error);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new GuiModelResolution(
                resolvedModelReference,
                modelFilePath,
                null,
                null,
                null,
                TextureMissingStatus,
                ex.Message);
        }
    }

    private static async Task<GuiModelResolution> ResolveJarGuiModelAsync(
        ZipArchive archive,
        string defaultNamespace,
        string itemName,
        string? guiModelReference)
    {
        bool hasExplicitGuiParent = !string.IsNullOrWhiteSpace(guiModelReference);
        string? resolvedModelReference = hasExplicitGuiParent ? guiModelReference!.Trim() : null;
        string? modelEntryPath = null;
        string modelNamespace = defaultNamespace;

        if (hasExplicitGuiParent)
        {
            ResourceLocation modelLocation = ParseResourceLocation(resolvedModelReference!, defaultNamespace);
            modelNamespace = modelLocation.Namespace;
            modelEntryPath = FindJarModelEntryPath(archive, modelLocation);
        }
        else
        {
            (resolvedModelReference, modelEntryPath, modelNamespace) = FindFallbackJarGuiModel(
                archive,
                defaultNamespace,
                itemName);
        }

        ZipArchiveEntry? modelEntry = string.IsNullOrWhiteSpace(modelEntryPath)
            ? null
            : GetEntryIgnoreCase(archive, modelEntryPath);

        if (modelEntry is null)
        {
            string status = hasExplicitGuiParent ? GuiModelMissingStatus : NoGuiParentStatus;
            string error = hasExplicitGuiParent ? "GUI model not found" : "No GUI parent in item model";
            return new GuiModelResolution(resolvedModelReference, modelEntryPath, null, null, null, status, error);
        }

        try
        {
            TextureResolution texture = await ResolveJarTextureFromModelChainAsync(
                archive,
                modelEntry.FullName,
                modelNamespace);

            if (string.IsNullOrWhiteSpace(texture.Reference))
            {
                return new GuiModelResolution(
                    resolvedModelReference,
                    modelEntry.FullName,
                    null,
                    null,
                    null,
                    TextureMissingStatus,
                    "Texture not found");
            }

            byte[]? textureBytes = ResolveJarTextureBytes(archive, texture.Reference, texture.Namespace);
            string status = textureBytes is null ? TextureMissingStatus : TextureFoundStatus;
            string? error = textureBytes is null ? "Texture not found" : null;

            return new GuiModelResolution(
                resolvedModelReference,
                modelEntry.FullName,
                texture.Reference,
                null,
                textureBytes,
                status,
                error);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new GuiModelResolution(
                resolvedModelReference,
                modelEntry.FullName,
                null,
                null,
                null,
                TextureMissingStatus,
                ex.Message);
        }
    }

    private static async Task<TextureResolution> ResolveFileSystemTextureFromModelChainAsync(
        string resourcesFolder,
        string startModelFilePath,
        string startNamespace)
    {
        List<ModelChainEntry> chain = new();
        HashSet<string> visitedModels = new(StringComparer.OrdinalIgnoreCase);
        string? currentModelFilePath = startModelFilePath;
        string currentNamespace = startNamespace;

        for (int depth = 0; depth < MaxModelParentDepth && !string.IsNullOrWhiteSpace(currentModelFilePath); depth++)
        {
            string visitedKey = Path.GetFullPath(currentModelFilePath);
            if (!visitedModels.Add(visitedKey) || !File.Exists(currentModelFilePath))
            {
                break;
            }

            await using FileStream stream = File.OpenRead(currentModelFilePath);
            ItemModelData modelData = await ReadItemModelDataAsync(stream);
            chain.Add(new ModelChainEntry(modelData, currentNamespace));

            if (string.IsNullOrWhiteSpace(modelData.Parent) || IsTerminalParent(modelData.Parent))
            {
                break;
            }

            ResourceLocation parentLocation = ParseResourceLocation(modelData.Parent, currentNamespace);
            string? parentFilePath = FindModelFile(resourcesFolder, parentLocation);
            currentNamespace = InferNamespaceFromModelPath(resourcesFolder, parentFilePath) ?? parentLocation.Namespace;
            currentModelFilePath = parentFilePath;
        }

        return ResolveTextureReferenceFromChain(chain);
    }

    private static async Task<TextureResolution> ResolveJarTextureFromModelChainAsync(
        ZipArchive archive,
        string startModelEntryPath,
        string startNamespace)
    {
        List<ModelChainEntry> chain = new();
        HashSet<string> visitedModels = new(StringComparer.OrdinalIgnoreCase);
        string? currentModelEntryPath = startModelEntryPath;
        string currentNamespace = startNamespace;

        for (int depth = 0; depth < MaxModelParentDepth && !string.IsNullOrWhiteSpace(currentModelEntryPath); depth++)
        {
            ZipArchiveEntry? entry = GetEntryIgnoreCase(archive, currentModelEntryPath);
            if (entry is null || !visitedModels.Add(entry.FullName))
            {
                break;
            }

            await using Stream stream = entry.Open();
            ItemModelData modelData = await ReadItemModelDataAsync(stream);
            chain.Add(new ModelChainEntry(modelData, currentNamespace));

            if (string.IsNullOrWhiteSpace(modelData.Parent) || IsTerminalParent(modelData.Parent))
            {
                break;
            }

            ResourceLocation parentLocation = ParseResourceLocation(modelData.Parent, currentNamespace);
            string? parentEntryPath = FindJarModelEntryPath(archive, parentLocation);
            currentNamespace = InferNamespaceFromModelEntryPath(parentEntryPath) ?? parentLocation.Namespace;
            currentModelEntryPath = parentEntryPath;
        }

        return ResolveTextureReferenceFromChain(chain);
    }

    private static TextureResolution ResolveTextureReferenceFromChain(IReadOnlyList<ModelChainEntry> chain)
    {
        if (chain.Count == 0)
        {
            return new TextureResolution(null, string.Empty);
        }

        Dictionary<string, TextureValue> mergedTextures = new(StringComparer.OrdinalIgnoreCase);
        for (int index = chain.Count - 1; index >= 0; index--)
        {
            foreach ((string key, string value) in chain[index].Data.Textures)
            {
                mergedTextures[key] = new TextureValue(value, chain[index].Namespace);
            }
        }

        TextureValue? textureValue = ReadPreferredTextureValue(mergedTextures);
        return ResolveTextureVariable(textureValue, mergedTextures);
    }

    private static TextureValue? ReadPreferredTextureValue(IReadOnlyDictionary<string, TextureValue> textures)
    {
        foreach (string key in new[] { "layer0", "0", "particle" })
        {
            if (textures.TryGetValue(key, out TextureValue? value) && !string.IsNullOrWhiteSpace(value.Reference))
            {
                return value;
            }
        }

        foreach (TextureValue value in textures.Values)
        {
            if (!string.IsNullOrWhiteSpace(value.Reference))
            {
                return value;
            }
        }

        return null;
    }

    private static TextureResolution ResolveTextureVariable(
        TextureValue? textureValue,
        IReadOnlyDictionary<string, TextureValue> textures)
    {
        TextureValue? resolvedValue = textureValue;
        HashSet<string> visitedKeys = new(StringComparer.OrdinalIgnoreCase);

        for (int depth = 0; depth < MaxModelParentDepth; depth++)
        {
            if (resolvedValue is null || string.IsNullOrWhiteSpace(resolvedValue.Reference))
            {
                return new TextureResolution(null, string.Empty);
            }

            if (!resolvedValue.Reference.StartsWith('#'))
            {
                return new TextureResolution(resolvedValue.Reference, resolvedValue.Namespace);
            }

            string key = resolvedValue.Reference[1..];
            if (!visitedKeys.Add(key) || !textures.TryGetValue(key, out resolvedValue))
            {
                return new TextureResolution(null, string.Empty);
            }
        }

        return new TextureResolution(null, string.Empty);
    }

    private static (string? ModelReference, string? ModelFilePath, string ModelNamespace) FindFallbackGuiModel(
        string resourcesFolder,
        string itemModelsFolder,
        string defaultNamespace,
        string itemName)
    {
        foreach (string candidateName in GetGuiModelCandidates(itemName))
        {
            string candidatePath = Path.Combine(itemModelsFolder, $"{candidateName}.json");
            if (File.Exists(candidatePath))
            {
                return (candidateName, candidatePath, defaultNamespace);
            }

            string? fallbackPath = FindModelFileByFileName(resourcesFolder, $"{candidateName}.json");
            if (!string.IsNullOrWhiteSpace(fallbackPath))
            {
                return (candidateName, fallbackPath, InferNamespaceFromModelPath(resourcesFolder, fallbackPath) ?? defaultNamespace);
            }
        }

        return (null, null, defaultNamespace);
    }

    private static (string? ModelReference, string? ModelEntryPath, string ModelNamespace) FindFallbackJarGuiModel(
        ZipArchive archive,
        string modId,
        string itemName)
    {
        foreach (string candidateName in GetGuiModelCandidates(itemName))
        {
            string candidateEntryPath = $"assets/{modId}/models/item/{candidateName}.json";
            ZipArchiveEntry? candidateEntry = GetEntryIgnoreCase(archive, candidateEntryPath);
            if (candidateEntry is not null)
            {
                return (candidateName, candidateEntry.FullName, modId);
            }

            ZipArchiveEntry? fallbackEntry = FindJarModelEntryByFileName(archive, $"{candidateName}.json");
            if (fallbackEntry is not null)
            {
                return (candidateName, fallbackEntry.FullName, InferNamespaceFromModelEntryPath(fallbackEntry.FullName) ?? modId);
            }
        }

        return (null, null, modId);
    }

    private static IEnumerable<string> GetGuiModelCandidates(string itemName)
    {
        yield return $"{itemName}_gui";
        yield return $"{itemName}gui";
        yield return itemName;
        yield return $"{itemName}_2d";
        yield return $"{itemName}_2d_gui";
        yield return $"{itemName}_inventory";
        yield return $"{itemName}_icon";
    }

    private static async Task<ItemModelData> ReadItemModelDataAsync(Stream jsonStream)
    {
        using JsonDocument document = await JsonDocument.ParseAsync(jsonStream);
        JsonElement root = document.RootElement;

        string? parent = ReadStringProperty(root, "parent");
        string? guiModelReference = null;

        if (root.TryGetProperty("perspectives", out JsonElement perspectivesElement)
            && perspectivesElement.ValueKind == JsonValueKind.Object
            && perspectivesElement.TryGetProperty("gui", out JsonElement guiElement)
            && guiElement.ValueKind == JsonValueKind.Object)
        {
            guiModelReference = ReadStringProperty(guiElement, "parent");
        }

        Dictionary<string, string> textures = new(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("textures", out JsonElement texturesElement)
            && texturesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty textureProperty in texturesElement.EnumerateObject())
            {
                if (textureProperty.Value.ValueKind == JsonValueKind.String)
                {
                    string? value = textureProperty.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        textures[textureProperty.Name] = value;
                    }
                }
            }
        }

        return new ItemModelData(parent, guiModelReference, textures);
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement propertyElement)
            && propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }

    private static bool IsHandheldParent(string? parent)
    {
        return string.Equals(parent, PrimaryHandheldParent, StringComparison.OrdinalIgnoreCase)
            || string.Equals(parent, LegacyHandheldParent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTerminalParent(string parent)
    {
        return string.Equals(parent, "minecraft:item/generated", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parent, "item/generated", StringComparison.OrdinalIgnoreCase)
            || string.Equals(parent, PrimaryHandheldParent, StringComparison.OrdinalIgnoreCase)
            || string.Equals(parent, LegacyHandheldParent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVariantModel(string itemName)
    {
        return itemName.EndsWith("_gui", StringComparison.OrdinalIgnoreCase)
            || itemName.EndsWith("gui", StringComparison.OrdinalIgnoreCase)
            || itemName.EndsWith("_3d", StringComparison.OrdinalIgnoreCase)
            || itemName.EndsWith("3d", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveTextureFilePath(
        string resourcesFolder,
        string? textureReference,
        string defaultNamespace)
    {
        if (string.IsNullOrWhiteSpace(textureReference))
        {
            return null;
        }

        ResourceLocation textureLocation = ParseResourceLocation(textureReference, defaultNamespace);
        string texturePath = Path.Combine(
            resourcesFolder,
            "assets",
            textureLocation.Namespace,
            "textures",
            ToPlatformPath(textureLocation.Path) + ".png");

        if (File.Exists(texturePath))
        {
            return texturePath;
        }

        return FindTextureFileByFileName(resourcesFolder, $"{Path.GetFileName(textureLocation.Path)}.png");
    }

    private static byte[]? ResolveJarTextureBytes(
        ZipArchive archive,
        string? textureReference,
        string defaultNamespace)
    {
        if (string.IsNullOrWhiteSpace(textureReference))
        {
            return null;
        }

        ResourceLocation textureLocation = ParseResourceLocation(textureReference, defaultNamespace);
        string textureEntryPath = $"assets/{textureLocation.Namespace}/textures/{textureLocation.Path}.png";
        ZipArchiveEntry? textureEntry = GetEntryIgnoreCase(archive, textureEntryPath)
            ?? FindJarTextureEntryByFileName(archive, $"{Path.GetFileName(textureLocation.Path)}.png");

        if (textureEntry is null)
        {
            return null;
        }

        using MemoryStream memoryStream = new();
        using Stream entryStream = textureEntry.Open();
        entryStream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    private static string? FindModelFile(string resourcesFolder, ResourceLocation modelLocation)
    {
        string modelFilePath = ResolveModelFilePath(resourcesFolder, modelLocation);
        if (File.Exists(modelFilePath))
        {
            return modelFilePath;
        }

        return FindModelFileByFileName(resourcesFolder, $"{Path.GetFileName(modelLocation.Path)}.json");
    }

    private static string ResolveModelFilePath(string resourcesFolder, ResourceLocation modelLocation)
    {
        return Path.Combine(
            resourcesFolder,
            "assets",
            modelLocation.Namespace,
            "models",
            ToPlatformPath(modelLocation.Path) + ".json");
    }

    private static string? FindModelFileByFileName(string resourcesFolder, string fileName)
    {
        string assetsFolder = Path.Combine(resourcesFolder, "assets");
        if (!Directory.Exists(assetsFolder))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(assetsFolder, "*.json", SearchOption.AllDirectories)
            .FirstOrDefault(path =>
                path.Contains($"{Path.DirectorySeparatorChar}models{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindTextureFileByFileName(string resourcesFolder, string fileName)
    {
        string assetsFolder = Path.Combine(resourcesFolder, "assets");
        if (!Directory.Exists(assetsFolder))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(assetsFolder, "*.png", SearchOption.AllDirectories)
            .FirstOrDefault(path =>
                path.Contains($"{Path.DirectorySeparatorChar}textures{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindJarModelEntryPath(ZipArchive archive, ResourceLocation modelLocation)
    {
        string modelEntryPath = $"assets/{modelLocation.Namespace}/models/{modelLocation.Path}.json";
        ZipArchiveEntry? modelEntry = GetEntryIgnoreCase(archive, modelEntryPath);
        return modelEntry?.FullName
            ?? FindJarModelEntryByFileName(archive, $"{Path.GetFileName(modelLocation.Path)}.json")?.FullName;
    }

    private static ZipArchiveEntry? FindJarModelEntryByFileName(ZipArchive archive, string fileName)
    {
        return archive.Entries.FirstOrDefault(entry =>
            entry.FullName.Contains("/models/", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(entry.FullName), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static ZipArchiveEntry? FindJarTextureEntryByFileName(ZipArchive archive, string fileName)
    {
        return archive.Entries.FirstOrDefault(entry =>
            entry.FullName.Contains("/textures/", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(entry.FullName), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static ZipArchiveEntry? GetEntryIgnoreCase(ZipArchive archive, string entryPath)
    {
        return archive.GetEntry(entryPath)
            ?? archive.Entries.FirstOrDefault(entry => string.Equals(entry.FullName, entryPath, StringComparison.OrdinalIgnoreCase));
    }

    private static ResourceLocation ParseResourceLocation(string reference, string defaultNamespace)
    {
        string trimmedReference = reference.Trim();
        int separatorIndex = trimmedReference.IndexOf(':', StringComparison.Ordinal);

        if (separatorIndex < 0)
        {
            return new ResourceLocation(defaultNamespace, trimmedReference);
        }

        string namespaceName = trimmedReference[..separatorIndex];
        string resourcePath = trimmedReference[(separatorIndex + 1)..];

        return new ResourceLocation(namespaceName, resourcePath);
    }

    private static string? InferNamespaceFromModelPath(string resourcesFolder, string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return null;
        }

        string assetsFolder = Path.Combine(resourcesFolder, "assets") + Path.DirectorySeparatorChar;
        string fullAssetsFolder = Path.GetFullPath(assetsFolder);
        string fullModelPath = Path.GetFullPath(modelPath);

        if (!fullModelPath.StartsWith(fullAssetsFolder, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string relativePath = fullModelPath[fullAssetsFolder.Length..];
        return relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    private static string? InferNamespaceFromModelEntryPath(string? entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath))
        {
            return null;
        }

        string[] parts = entryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && string.Equals(parts[0], "assets", StringComparison.OrdinalIgnoreCase)
            ? parts[1]
            : null;
    }

    private static string ToPlatformPath(string resourcePath)
    {
        return resourcePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
    }

    private static bool IsJarFile(string path)
    {
        return File.Exists(path)
            && string.Equals(Path.GetExtension(path), ".jar", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseTopLevelItemModelEntry(string entryPath, out string modId, out string itemName)
    {
        modId = string.Empty;
        itemName = string.Empty;

        string[] parts = entryPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5
            || !string.Equals(parts[0], "assets", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[2], "models", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parts[3], "item", StringComparison.OrdinalIgnoreCase)
            || !parts[4].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        modId = parts[1];
        itemName = Path.GetFileNameWithoutExtension(parts[4]);
        return true;
    }

    private static bool HasAnyItemModelsFolder(string assetsFolder)
    {
        return Directory.EnumerateDirectories(assetsFolder)
            .Any(modFolder => Directory.Exists(Path.Combine(modFolder, "models", "item")));
    }

    private static bool HasAnyJarItemModelsFolder(ZipArchive archive)
    {
        return archive.Entries.Any(entry =>
            entry.FullName.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
            && entry.FullName.Contains("/models/item/", StringComparison.OrdinalIgnoreCase)
            && entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<ItemModelInfo> SortItems(IEnumerable<ItemModelInfo> items)
    {
        return items
            .OrderBy(item => item.ModId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ItemName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed record ItemModelData(
        string? Parent,
        string? GuiModelReference,
        IReadOnlyDictionary<string, string> Textures);

    private sealed record ModelChainEntry(ItemModelData Data, string Namespace);

    private sealed record TextureValue(string Reference, string Namespace);

    private sealed record GuiModelResolution(
        string? ModelReference,
        string? ModelFilePath,
        string? TextureReference,
        string? TextureFilePath,
        byte[]? TextureBytes,
        string Status,
        string? Error);

    private sealed record TextureResolution(string? Reference, string Namespace);

    private readonly record struct ResourceLocation(string Namespace, string Path);
}
