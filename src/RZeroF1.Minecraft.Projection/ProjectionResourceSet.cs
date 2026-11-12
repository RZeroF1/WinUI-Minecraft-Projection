using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RZeroF1.Minecraft.Projection;

internal sealed class ProjectionResourceSet : IDisposable
{
    private const long MaxResourceBytes = 16 * 1024 * 1024;
    private const long MaxCachedBytes = 128 * 1024 * 1024;
    private const long MaxDerivedTextureBytes = 32 * 1024 * 1024;
    private readonly List<ZipArchive> _layers = [];
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _assetObjectCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _assetIndexHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _assetObjectHashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _potPatternTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _bannerPatternIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _localizedNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];
    private readonly HashSet<string> _forceTranslucent = new(StringComparer.OrdinalIgnoreCase);
    private long _cachedBytes;
    private long _derivedTextureBytes;
    private long _assetObjectBytes;
    private string? _assetIndexId;
    private long _bannerPatternGeneration = -1;
    private readonly string? _assetStoreRoot;
    private bool _disposed;
    private static long _nextGeneration;
    private readonly FileStream _clientStream;
    private ProjectionVersionSelection? _clientSelection;
    internal long Generation { get; private set; } = Interlocked.Increment(ref _nextGeneration);

    internal ProjectionVersionSelection SelectClient(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (_clientSelection is not null) return _clientSelection;
        var position = _clientStream.Position;
        try
        {
            _clientStream.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = _clientStream.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, count);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return _clientSelection = ProjectionVersionProfile.SelectClient(Convert.ToHexString(hash.GetHashAndReset()));
        }
        finally { _clientStream.Position = position; }
    }
    private bool _modelsLoaded;
    private readonly Dictionary<string, ProjectionTextureImage> _images = new(StringComparer.OrdinalIgnoreCase);
    private bool _assetIndexLoaded;
    private ProjectionSpriteResolver? _sprites;
    internal ProjectionSpriteResolver Sprites => _sprites ??= new(this);

    internal int RegisteredBannerPatternCount => _bannerPatternIds.Count;

    internal ProjectionResourceSet(string clientJarPath, string? assetIndexId = null, string? assetStoreRoot = null)
    {
        ClientJarPath = Path.GetFullPath(clientJarPath);
        _assetIndexId = string.IsNullOrWhiteSpace(assetIndexId) ? null : assetIndexId;
        _assetStoreRoot = string.IsNullOrWhiteSpace(assetStoreRoot) ? null : Path.GetFullPath(assetStoreRoot);
        _clientStream = File.OpenRead(ClientJarPath);
        try { _layers.Add(new ZipArchive(_clientStream, ZipArchiveMode.Read, leaveOpen: false)); }
        catch { _clientStream.Dispose(); throw; }
    }

    internal string ClientJarPath { get; }
    internal long CachedBytes => _cachedBytes + _derivedTextureBytes + _assetObjectBytes + (_sprites?.EstimatedCpuBytes ?? 0);
    internal IReadOnlyList<string> Warnings => _warnings;
    internal IReadOnlySet<string> ForceTranslucent => _forceTranslucent;
    internal IReadOnlyDictionary<string, ProjectionBlockModelDefinition> Models { get; private set; } =
        new Dictionary<string, ProjectionBlockModelDefinition>(StringComparer.OrdinalIgnoreCase);

    internal void AddResourcePack(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!File.Exists(path))
            throw new FileNotFoundException("资源包不存在。", path);
        _layers.Add(ZipFile.OpenRead(path));
        Generation = Interlocked.Increment(ref _nextGeneration);
        _cache.Clear();
        _images.Clear();
        _derivedTextureBytes = 0;
        _assetObjectCache.Clear();
        _assetIndexHashes.Clear();
        _assetObjectBytes = 0;
        _assetObjectHashes.Clear();
        _assetIndexId = ClientAssetIndexId();
        _potPatternTextures.Clear();
        _bannerPatternIds.Clear();
        _bannerPatternGeneration = -1;
        _assetIndexLoaded = false;
        _sprites = null;
        _localizedNames.Clear();
        _cachedBytes = 0;
        _modelsLoaded = false;
        Models = new Dictionary<string, ProjectionBlockModelDefinition>(StringComparer.OrdinalIgnoreCase);
        _forceTranslucent.Clear();
        _warnings.Clear();
    }

    internal byte[]? Read(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidatePath(path);
        if (_cache.TryGetValue(path, out var cached))
            return cached;

        for (var index = _layers.Count - 1; index >= 0; index--)
        {
            var entry = _layers[index].GetEntry(path);
            if (entry is null)
                continue;
            if (entry.Length > MaxResourceBytes)
                throw new InvalidDataException($"资源 {path} 超过 {MaxResourceBytes:N0} 字节上限。");

            using var input = entry.Open();
            using var output = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            input.CopyTo(output);
            var bytes = output.ToArray();
            if (_cachedBytes + bytes.Length <= MaxCachedBytes)
            {
                _cache[path] = bytes;
                _cachedBytes += bytes.Length;
            }
            return bytes;
        }

        return ReadExternalAssetObject(path);
    }

    internal void RegisterDerivedTexture(string textureId, byte[] png, bool hasTransparency)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(png);
        var id = NormalizeResourceId(textureId);
        if (id == "__missing__")
            throw new ArgumentException("A derived texture cannot use the missing-texture ID.", nameof(textureId));
        if (png.Length == 0 || png.Length > MaxResourceBytes)
            throw new InvalidDataException("Derived texture exceeds the resource budget.");
        if (_images.TryGetValue(id, out var previous))
            _derivedTextureBytes -= previous.Png.LongLength;
        if (_derivedTextureBytes + png.LongLength > MaxDerivedTextureBytes)
            throw new InvalidDataException("DerivedTextureBudgetExceeded");
        _images[id] = new ProjectionTextureImage(png.ToArray(), hasTransparency);
        _derivedTextureBytes += png.LongLength;
    }

    internal string? ResolvePotPatternTexture(string patternId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = NormalizeResourceId(patternId);
        if (_potPatternTextures.TryGetValue(id, out var cached)) return cached;
        var split = id.Split(':', 2);
        var bytes = Read($"data/{split[0]}/decorated_pot_pattern/{split[1]}.json");
        string? texture = null;
        if (bytes is not null)
        {
            try
            {
                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                if (json.RootElement.TryGetProperty("asset_id", out var asset) && asset.ValueKind == JsonValueKind.String)
                {
                    var assetParts = NormalizeResourceId(asset.GetString()!).Split(':', 2);
                    texture = assetParts[0] + ":entity/decorated_pot/" + assetParts[1];
                }
            }
            catch (JsonException exception) { SpriteWarning($"InvalidPotPatternDefinition:{id}:{exception.GetType().Name}"); }
        }
        if (texture is null)
        {
            var name = split[1].Split('/')[^1];
            if (name.EndsWith("_pottery_pattern", StringComparison.Ordinal))
                name = name[..^"_pottery_pattern".Length];
            else if (name.StartsWith("pottery_pattern_", StringComparison.Ordinal))
                name = name["pottery_pattern_".Length..];
            foreach (var spriteName in new[] { name + "_pottery_pattern", "pottery_pattern_" + name })
            {
                var candidate = $"{split[0]}:entity/decorated_pot/{spriteName}";
                var sprite = Sprites.Resolve("minecraft:decorated_pot", candidate, 16, 16, ProjectionMaterialRole.Cutout, default);
                if (sprite.Image.Failure is not null) continue;
                texture = sprite.SpriteId;
                break;
            }
        }
        else
        {
            var sprite = Sprites.Resolve("minecraft:decorated_pot", texture, 16, 16, ProjectionMaterialRole.Cutout, default);
            texture = sprite.Image.Failure is null ? sprite.SpriteId : null;
        }
        _potPatternTextures[id] = texture;
        return texture;
    }

    internal string? ResolveBannerPatternTexture(string patternId, string color)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LoadBannerPatternIds();
        var id = NormalizeResourceId(patternId);
        var split = id.Split(':', 2);
        var path = split[1];
        if (path.StartsWith("banner/", StringComparison.Ordinal)) path = path["banner/".Length..];
        var modernSprite = split[0] + ":entity/banner/" + path;
        var legacySprite = modernSprite;
        if (legacySprite.EndsWith("_banner_pattern", StringComparison.Ordinal))
            legacySprite = legacySprite[..^"_banner_pattern".Length];
        var definition = Read($"data/{split[0]}/banner_pattern/{split[1]}.json");
        if (definition is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(definition, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.TryGetProperty("asset_id", out var asset) && asset.ValueKind == JsonValueKind.String)
                {
                    var assetParts = NormalizeResourceId(asset.GetString()!).Split(':', 2);
                    modernSprite = assetParts[0] + ":entity/banner/" + assetParts[1];
                }
            }
            catch (JsonException exception) { SpriteWarning($"InvalidBannerPatternDefinition:{id}:{exception.GetType().Name}"); return null; }
        }
        var legacyColorSprite = legacySprite + "_" + color;
        if (_bannerPatternIds.Contains(legacyColorSprite)) return legacyColorSprite;
        if (_bannerPatternIds.Contains(modernSprite)) return modernSprite;
        if (_bannerPatternIds.Contains(legacySprite)) return legacySprite;
        return null;
    }

    internal string? ResolveLegacyBannerPattern(string patternCode)
    {
        var id = patternCode switch
        {
            "bs" => "stripe_bottom", "ts" => "stripe_top", "ls" => "stripe_left", "rs" => "stripe_right",
            "cs" => "stripe_center", "ms" => "stripe_middle", "drs" => "stripe_downright", "dls" => "stripe_downleft",
            "ss" => "small_stripes", "cr" => "cross", "sc" => "straight_cross", "ld" => "diagonal_left",
            "rd" => "diagonal_right", "lud" => "diagonal_up_left", "rud" => "diagonal_up_right",
            "vh" => "half_vertical", "vhr" => "half_vertical_right", "hh" => "half_horizontal",
            "hhb" => "half_horizontal_bottom", "bl" => "square_bottom_left", "br" => "square_bottom_right",
            "tl" => "square_top_left", "tr" => "square_top_right", "bts" => "triangles_bottom",
            "tts" => "triangles_top", "bri" => "bricks", "mc" => "circle", "mr" => "rhombus",
            "cbo" => "border", "bo" => "curly_border", "gra" => "gradient", "gru" => "gradient_up",
            "cre" => "creeper", "sku" => "skull", "flo" => "flower", "moj" => "mojang",
            "glb" => "globe", "pig" => "piglin", "flw" => "flow", "gus" => "guster", _ => null
        };
        return id;
    }

    private void LoadBannerPatternIds()
    {
        if (_bannerPatternGeneration == Generation) return;
        _bannerPatternIds.Clear();
        if (_sprites is not null)
            foreach (var sprite in _sprites.EnumerateSprites("minecraft:banner_patterns"))
                _bannerPatternIds.Add(sprite);
        foreach (var path in EnumeratePaths("assets/"))
        {
            if (!path.Contains("/textures/entity/banner/", StringComparison.Ordinal) || !path.EndsWith(".png", StringComparison.Ordinal))
                continue;
            var parts = path.Split('/');
            if (parts.Length != 6) continue;
            var file = parts[^1][..^4];
            _bannerPatternIds.Add(NormalizeResourceId($"{parts[1]}:entity/banner/{file}"));
        }
        _bannerPatternGeneration = Generation;
    }

    // Atlas definitions append their sources from low to high resource layers.
    // Texture/model reads retain the existing last-layer-wins behavior.
    internal IEnumerable<byte[]> ReadAtlasLayers(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidatePath(path);
        foreach (var layer in _layers)
        {
            var entry = layer.GetEntry(path);
            if (entry is null) continue;
            if (entry.Length > MaxResourceBytes) throw new InvalidDataException("AtlasResourceBudgetExceeded");
            using var input = entry.Open();
            using var output = new MemoryStream((int)entry.Length);
            input.CopyTo(output);
            yield return output.ToArray();
        }
    }

    internal void SpriteWarning(string message)
    {
        if (_warnings.Count < 500) _warnings.Add(message);
    }

    internal void LoadModelDefinitions(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_modelsLoaded)
            return;

        var definitions = new Dictionary<string, ProjectionBlockModelDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumeratePaths("assets/"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!path.Contains("/models/block/", StringComparison.Ordinal) ||
                !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            var bytes = Read(path);
            if (bytes is null)
                continue;
            try
            {
                var node = JsonNode.Parse(bytes)?.AsObject()
                    ?? throw new JsonException("模型 JSON 根节点不是对象。");
                if (node["textures"] is JsonObject textures)
                {
                    foreach (var (key, value) in textures.ToArray())
                    {
                        if (value is not JsonObject texture)
                            continue;
                        var sprite = texture["sprite"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(sprite))
                            throw new JsonException("纹理对象缺少 sprite。");
                        if (texture["force_translucent"]?.GetValue<bool>() == true)
                            _forceTranslucent.Add(NormalizeResourceId(sprite));
                        textures[key] = sprite;
                    }
                }

                var definition = JsonSerializer.Deserialize(
                    node.ToJsonString(),
                    ProjectionModelJsonContext.Default.ProjectionBlockModelDefinition);
                if (definition is null)
                    continue;
                var parts = path.Split('/', 4);
                if (parts.Length == 4)
                    definitions[$"{parts[1]}:{parts[3][..^5]}"] = definition;
            }
            catch (JsonException exception)
            {
                _warnings.Add($"{path}: {exception.Message}");
            }
        }

        Models = definitions;
        LoadLocalizedNames();
        _modelsLoaded = true;
    }

    internal string GetDisplayName(string blockName)
    {
        LoadLocalizedNames();
        var normalized = NormalizeResourceId(blockName);
        var split = normalized.Split(':', 2);
        var key = $"block.{split[0]}.{split[1]}";
        if (_localizedNames.TryGetValue(key, out var localized) && !string.IsNullOrWhiteSpace(localized))
            return localized;
        if (ProjectionFallbackNames.TryGet(normalized, out var fallbackName))
            return fallbackName;

        var fallback = split[1].Replace('_', ' ');
        return fallback.Length == 0
            ? normalized
            : char.ToUpperInvariant(fallback[0]) + fallback[1..];
    }

    internal string? Translate(string key)
    {
        LoadLocalizedNames();
        return _localizedNames.GetValueOrDefault(key);
    }

    internal IReadOnlyList<byte[]> ReadResourceLayers(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidatePath(path);
        var result = new List<byte[]>();
        for (var index = 0; index < _layers.Count; index++)
        {
            var layer = _layers[index];
            var entry = layer.GetEntry(path);
            if (entry is not null)
            {
                if (entry.Length > MaxResourceBytes)
                    throw new InvalidDataException($"Resource stack entry {path} exceeds the resource budget.");
                using var input = entry.Open();
                using var output = new MemoryStream((int)entry.Length);
                input.CopyTo(output);
                result.Add(output.ToArray());
            }
            // Newer clients expose part of the resource tree through the
            // official asset object index. Insert those verified resources
            // immediately above the bundled client JAR, below user packs.
            if (index == 0 && ReadExternalAssetObject(path) is { } external)
                result.Add(external);
        }
        return result;
    }

    internal ProjectionTextureImage LoadTexture(string textureId)
    {
        if (textureId == "__missing__") return new(ProjectionTextureLoader.MissingPng, false);
        if (_images.TryGetValue(textureId, out var image)) return image;
        var path = ResourcePath(textureId, "textures", ".png");
        image = ProjectionTextureLoader.Load(Read(path), Read(path + ".mcmeta"));
        if (_cachedBytes + image.Png.Length <= MaxCachedBytes)
        {
            _images[textureId] = image;
            _cachedBytes += image.Png.Length;
        }
        return image;
    }

    internal byte[]? ReadTexture(string textureId)
    {
        return textureId.Equals("__missing__", StringComparison.OrdinalIgnoreCase)
            ? null
            : Read(ResourcePath(textureId, "textures", ".png"));
    }

    private byte[]? ReadExternalAssetObject(string path)
    {
        if (!path.StartsWith("assets/", StringComparison.Ordinal))
            return null;
        EnsureAssetIndex();
        var assetPath = path["assets/".Length..];
        if (!_assetObjectHashes.TryGetValue(assetPath, out var hash))
            return null;
        if (_assetObjectCache.TryGetValue(hash, out var cached))
            return cached;
        var root = DiscoverAssetObjectRoot();
        if (root is null)
            return null;
        var objectPath = Path.Combine(root, hash[..2], hash);
        if (!File.Exists(objectPath))
            return null;
        var file = new FileInfo(objectPath);
        if (file.Length <= 0 || file.Length > MaxResourceBytes)
            throw new InvalidDataException($"Asset object {path} exceeds the resource budget.");
        var bytes = File.ReadAllBytes(objectPath);
        if (!Convert.ToHexString(SHA1.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase))
        {
            _warnings.Add($"AssetObjectHashMismatch:{assetPath}");
            return null;
        }
        if (_assetObjectCache.Values.Sum(value => (long)value.Length) + bytes.LongLength > MaxCachedBytes)
            throw new InvalidDataException("AssetObjectCacheBudgetExceeded");
        _assetObjectCache[hash] = bytes;
        _assetObjectBytes += bytes.LongLength;
        return bytes;
    }

    private void EnsureAssetIndex()
    {
        if (_assetIndexLoaded && _assetIndexId == ClientAssetIndexId())
            return;
        _assetObjectHashes.Clear();
        _assetObjectCache.Clear();
        _assetObjectBytes = 0;
        _assetIndexId = _assetIndexId ?? ClientAssetIndexId();
        _assetIndexLoaded = true;
        var expectedIndex = _assetIndexId ?? ClientAssetIndexId();
        if (expectedIndex is null)
        {
            _warnings.Add("AssetIndexIdentityUnavailable");
            return;
        }
        foreach (var directory in DiscoverAssetIndexRoots())
        {
            if (!Directory.Exists(directory)) continue;
            var file = Path.Combine(directory, expectedIndex + ".json");
            if (!File.Exists(file)) continue;
            try
            {
                var indexSha = _assetIndexHashes.GetValueOrDefault(expectedIndex);
                if (indexSha is not null && !Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(file))).Equals(indexSha, StringComparison.OrdinalIgnoreCase))
                {
                    _warnings.Add($"AssetIndexHashMismatch:{expectedIndex}");
                    continue;
                }
                using var document = JsonDocument.Parse(File.ReadAllBytes(file), new JsonDocumentOptions { MaxDepth = 32 });
                if (!document.RootElement.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Object)
                    continue;
            foreach (var item in objects.EnumerateObject())
                {
                    if (item.Value.TryGetProperty("hash", out var hash) && hash.ValueKind == JsonValueKind.String &&
                        hash.GetString() is { Length: 40 } value && value.All(char.IsAsciiHexDigit))
                        _assetObjectHashes[item.Name] = value.ToLowerInvariant();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                _warnings.Add($"AssetIndexReadFailure:{file}:{exception.GetType().Name}");
            }
        }
        if (expectedIndex is not null) _assetIndexId = expectedIndex;
    }

    private string? ClientAssetIndexId()
    {
        try
        {
            var entry = _layers[0].GetEntry("version.json");
            if (entry is not null)
            {
                using var input = entry.Open();
                using var json = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 16 });
                if (FindProperty(json.RootElement, "assetIndex") is { ValueKind: JsonValueKind.Object } index &&
                    FindProperty(index, "id") is { ValueKind: JsonValueKind.String } id)
                {
                    if (FindProperty(index, "sha1") is { ValueKind: JsonValueKind.String } sha && sha.GetString() is { Length: 40 } hash)
                        _assetIndexHashes[id.GetString()!] = hash;
                    return id.GetString();
                }
            }
            using var metadata = File.OpenRead(Path.ChangeExtension(ClientJarPath, ".json"));
            using var version = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 16 });
            if (FindProperty(version.RootElement, "assetIndex") is { ValueKind: JsonValueKind.Object } asset &&
                FindProperty(asset, "id") is { ValueKind: JsonValueKind.String } assetId)
            {
                if (FindProperty(asset, "sha1") is { ValueKind: JsonValueKind.String } assetSha && assetSha.GetString() is { Length: 40 } assetHash)
                    _assetIndexHashes[assetId.GetString()!] = assetHash;
                return assetId.GetString();
            }
            return FindProperty(version.RootElement, "assets") is { ValueKind: JsonValueKind.String } assets
                ? assets.GetString()
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _warnings.Add("AssetIndexMetadataFailure:" + exception.GetType().Name);
            return null;
        }
    }

    private static JsonElement? FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        return null;
    }

    private IEnumerable<string> DiscoverAssetIndexRoots()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_assetStoreRoot is not null)
            result.Add(Path.Combine(_assetStoreRoot, "indexes"));
        var jarDirectory = Path.GetDirectoryName(ClientJarPath);
        if (jarDirectory is not null)
        {
            result.Add(Path.GetFullPath(Path.Combine(jarDirectory, "..", "..", "assets", "indexes")));
            result.Add(Path.GetFullPath(Path.Combine(jarDirectory, "..", "..", "..", "assets", "indexes")));
        }
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roaming))
            result.Add(Path.Combine(roaming, ".minecraft", "assets", "indexes"));
        return result;
    }

    private string? DiscoverAssetObjectRoot()
    {
        var jarDirectory = Path.GetDirectoryName(ClientJarPath);
        var candidates = new List<string>();
        if (_assetStoreRoot is not null)
            candidates.Add(Path.Combine(_assetStoreRoot, "objects"));
        if (jarDirectory is not null)
        {
            candidates.Add(Path.GetFullPath(Path.Combine(jarDirectory, "..", "..", "assets", "objects")));
            candidates.Add(Path.GetFullPath(Path.Combine(jarDirectory, "..", "..", "..", "assets", "objects")));
        }
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var candidate in candidates.Append(Path.Combine(roaming, ".minecraft", "assets", "objects")))
            if (Directory.Exists(candidate)) return candidate;
        return null;
    }

    internal IEnumerable<string> EnumeratePaths(string prefix)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var layer in _layers)
        foreach (var entry in layer.Entries)
        {
            if (entry.FullName.StartsWith(prefix, StringComparison.Ordinal) &&
                !entry.FullName.EndsWith('/') &&
                seen.Add(entry.FullName))
                yield return entry.FullName;
        }
    }

    internal static string ResourcePath(string resourceId, string folder, string extension)
    {
        var normalized = NormalizeResourceId(resourceId);
        var split = normalized.Split(':', 2);
        var path = split[1];
        if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            path = path[..^extension.Length];
        return $"assets/{split[0]}/{folder}/{path}{extension}";
    }

    internal static string NormalizeResourceId(string resourceId)
    {
        var value = resourceId.Trim();
        if (value.StartsWith('#'))
            value = value[1..];
        if (value.Equals("__missing__", StringComparison.OrdinalIgnoreCase))
            return "__missing__";
        return value.Contains(':', StringComparison.Ordinal)
            ? value.ToLowerInvariant()
            : "minecraft:" + value.ToLowerInvariant();
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.StartsWith('/') ||
            path.Contains('\\') ||
            path.Split('/').Any(static part => part is "." or ".."))
            throw new ArgumentException("资源路径无效。", nameof(path));
    }

    private static class ProjectionFallbackNames
    {
        private static readonly IReadOnlyDictionary<string, string> Names =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["minecraft:air"] = "空气",
                ["minecraft:anvil"] = "铁砧",
                ["minecraft:black_wool"] = "黑色羊毛",
                ["minecraft:bookshelf"] = "书架",
                ["minecraft:chest"] = "箱子",
                ["minecraft:coal_block"] = "煤炭块",
                ["minecraft:cobblestone"] = "圆石",
                ["minecraft:cobblestone_slab"] = "圆石台阶",
                ["minecraft:cobblestone_stairs"] = "圆石楼梯",
                ["minecraft:cyan_wool"] = "青色羊毛",
                ["minecraft:dirt"] = "泥土",
                ["minecraft:dirt_path"] = "土径",
                ["minecraft:farmland"] = "耕地",
                ["minecraft:furnace"] = "熔炉",
                ["minecraft:glass"] = "玻璃",
                ["minecraft:grass_block"] = "草方块",
                ["minecraft:iron_block"] = "铁块",
                ["minecraft:lever"] = "拉杆",
                ["minecraft:polished_andesite"] = "磨制安山岩",
                ["minecraft:polished_andesite_stairs"] = "磨制安山岩楼梯",
                ["minecraft:polished_andesite_slab"] = "磨制安山岩台阶",
                ["minecraft:gray_wool"] = "灰色羊毛",
                ["minecraft:smooth_stone"] = "平滑石头",
                ["minecraft:cyan_terracotta"] = "青色陶瓦",
                ["minecraft:cobblestone_wall"] = "圆石墙",
                ["minecraft:stone_brick_slab"] = "石砖台阶",
                ["minecraft:stone_brick_wall"] = "石砖墙",
                ["minecraft:light_gray_wool"] = "淡灰色羊毛",
                ["minecraft:smooth_stone_slab"] = "平滑石台阶",
                ["minecraft:stone_brick_stairs"] = "石砖楼梯",
                ["minecraft:sea_lantern"] = "海晶灯",
                ["minecraft:clay"] = "黏土",
                ["minecraft:iron_bars"] = "铁栏杆",
                ["minecraft:emerald_block"] = "绿宝石块",
                ["minecraft:stone_button"] = "石按钮",
                ["minecraft:nether_brick_slab"] = "下界砖台阶",
                ["minecraft:yellow_terracotta"] = "黄色陶瓦",
                ["minecraft:black_terracotta"] = "黑色陶瓦",
                ["minecraft:black_stained_glass_pane"] = "黑色染色玻璃板",
                ["minecraft:quartz_slab"] = "石英台阶",
                ["minecraft:light_blue_stained_glass"] = "淡蓝色染色玻璃",
                ["minecraft:quartz_block"] = "石英块",
                ["minecraft:gold_block"] = "金块",
                ["minecraft:oak_wall_sign"] = "橡木墙告示牌",
                ["minecraft:oak_stairs"] = "橡木楼梯",
                ["minecraft:oak_log"] = "橡木原木",
                ["minecraft:spruce_planks"] = "云杉木板",
                ["minecraft:spruce_trapdoor"] = "云杉活板门",
                ["minecraft:spruce_stairs"] = "云杉楼梯",
                ["minecraft:stripped_spruce_wood"] = "去皮云杉木",
                ["minecraft:stripped_oak_log"] = "去皮橡木原木",
                ["minecraft:black_stained_glass"] = "黑色染色玻璃",
                ["minecraft:glass_pane"] = "玻璃板",
                ["minecraft:black_carpet"] = "黑色地毯",
                ["minecraft:nether_brick_stairs"] = "下界砖楼梯",
                ["minecraft:light_gray_wall_banner"] = "淡灰色墙旗帜",
                ["minecraft:blue_terracotta"] = "蓝色陶瓦",
                ["minecraft:oak_button"] = "橡木按钮",
                ["minecraft:cobbled_deepslate"] = "深板岩圆石",
                ["minecraft:cobbled_deepslate_slab"] = "深板岩圆石台阶",
                ["minecraft:cobbled_deepslate_stairs"] = "深板岩圆石楼梯",
                ["minecraft:hay_block"] = "干草块",
                ["minecraft:torch"] = "火把",
                ["minecraft:water"] = "水",
                ["minecraft:stone_slab"] = "石台阶",
                ["minecraft:stone_bricks"] = "石砖",
                ["minecraft:birch_leaves"] = "白桦树叶",
                ["minecraft:pale_oak_leaves"] = "苍白橡木树叶",
                ["minecraft:cave_vines"] = "洞穴藤蔓",
                ["minecraft:sugar_cane"] = "甘蔗",
                ["minecraft:chain"] = "锁链",
                ["minecraft:moss_carpet"] = "苔藓地毯",
                ["minecraft:scaffolding"] = "脚手架",
                ["minecraft:stripped_pale_oak_wood"] = "去皮苍白橡木",
                ["minecraft:pale_oak_fence_gate"] = "苍白橡木栅栏门",
                ["minecraft:polished_diorite_slab"] = "磨制闪长岩台阶",
                ["minecraft:dark_oak_hanging_sign"] = "深色橡木悬挂告示牌",
                ["minecraft:dark_oak_planks"] = "深色橡木木板",
                ["minecraft:dark_oak_slab"] = "深色橡木台阶",
                ["minecraft:pearlescent_froglight"] = "珠光蛙明灯",
                ["minecraft:tall_grass"] = "高草",
                ["minecraft:azalea"] = "杜鹃花",
                ["minecraft:composter"] = "堆肥桶",
                ["minecraft:oak_door"] = "橡木门",
                ["minecraft:oak_fence"] = "橡木栅栏",
                ["minecraft:oak_fence_gate"] = "橡木栅栏门",
                ["minecraft:oak_planks"] = "橡木木板",
                ["minecraft:pale_oak_fence"] = "苍白橡木栅栏",
                ["minecraft:pale_oak_planks"] = "苍白橡木木板",
                ["minecraft:pale_oak_log"] = "苍白橡木原木",
                ["minecraft:pale_oak_slab"] = "苍白橡木台阶",
                ["minecraft:pale_oak_stairs"] = "苍白橡木楼梯",
                ["minecraft:pale_oak_trapdoor"] = "苍白橡木活板门",
                ["minecraft:pale_oak_wood"] = "苍白橡木",
                ["minecraft:short_grass"] = "短草",
                ["minecraft:cave_vines_plant"] = "洞穴藤蔓植株",
                ["minecraft:ladder"] = "梯子",
                ["minecraft:moss_block"] = "苔藓块",
                ["minecraft:dark_oak_log"] = "深色橡木原木",
                ["minecraft:dark_oak_leaves"] = "深色橡树树叶",
                ["minecraft:dark_oak_stairs"] = "深色橡木楼梯",
                ["minecraft:dark_oak_trapdoor"] = "深色橡木活板门",
                ["minecraft:oak_leaves"] = "橡树树叶",
                ["minecraft:diorite"] = "闪长岩",
                ["minecraft:diorite_slab"] = "闪长岩台阶",
                ["minecraft:diorite_stairs"] = "闪长岩楼梯",
                ["minecraft:diorite_wall"] = "闪长岩墙",
                ["minecraft:polished_diorite"] = "磨制闪长岩",
                ["minecraft:snow"] = "雪",
                ["minecraft:magenta_stained_glass"] = "品红色染色玻璃",
                ["minecraft:magenta_stained_glass_pane"] = "品红色染色玻璃板",
                ["minecraft:purple_stained_glass"] = "紫色染色玻璃",
                ["minecraft:purple_stained_glass_pane"] = "紫色染色玻璃板",
                ["minecraft:pink_stained_glass"] = "粉红色染色玻璃",
                ["minecraft:shulker_box"] = "潜影盒",
                ["minecraft:red_bed"] = "红色床",
                ["minecraft:redstone_lamp"] = "红石灯",
                ["minecraft:soul_soil"] = "灵魂土",
                ["minecraft:spruce_stairs"] = "云杉楼梯",
                ["minecraft:stone"] = "石头",
                ["minecraft:wall_torch"] = "墙上的火把",
                ["minecraft:wheat"] = "小麦"
            };

        public static bool TryGet(string name, out string value) => Names.TryGetValue(name, out value!);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var layer in _layers)
            layer.Dispose();
        _layers.Clear();
        _cache.Clear();
        _images.Clear();
        _derivedTextureBytes = 0;
        _assetObjectCache.Clear();
        _assetObjectBytes = 0;
        _assetObjectHashes.Clear();
        _assetIndexId = null;
        _assetIndexLoaded = false;
        _potPatternTextures.Clear();
        _sprites = null;
        _localizedNames.Clear();
        _cachedBytes = 0;
    }

    private void LoadLocalizedNames()
    {
        if (_localizedNames.Count > 0)
            return;

        foreach (var locale in new[] { "zh_cn", "en_us" })
        {
            var bytes = Read($"assets/minecraft/lang/{locale}.json");
            if (bytes is null)
                continue;
            try
            {
                var root = JsonNode.Parse(bytes)?.AsObject();
                if (root is null)
                    continue;
                foreach (var (key, value) in root)
                {
                    if (value is JsonValue jsonValue &&
                        jsonValue.TryGetValue<string>(out var text) &&
                        !string.IsNullOrWhiteSpace(text) &&
                        !_localizedNames.ContainsKey(key))
                        _localizedNames[key] = text;
                }
            }
            catch (JsonException exception)
            {
                _warnings.Add($"assets/minecraft/lang/{locale}.json: {exception.Message}");
            }
        }
    }
}

internal static class ProjectionResourceDiscovery
{
    internal static Task<IReadOnlyList<ProjectionClientResource>> ReadCandidatesAsync(
        IEnumerable<ProjectionClientSource> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var result = new List<ProjectionClientResource>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var resource = ProjectionClientResource.Read(candidate.Path, candidate.Label);
                if (resource is not null)
                    result.Add(resource);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _ = exception;
            }
        }

        return Task.FromResult<IReadOnlyList<ProjectionClientResource>>(result
            .GroupBy(static item => item.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray());
    }
}

public sealed record ProjectionClientSource(string Path, string? Label = null);

internal sealed class ProjectionClientResource
{
    private ProjectionClientResource(string path, string label, int? dataVersion, string sha256)
    {
        Path = path;
        Label = label;
        DataVersion = dataVersion;
        Sha256 = sha256;
    }

    internal string Path { get; }
    internal string Label { get; }
    internal int? DataVersion { get; }
    internal string Sha256 { get; }
    internal string DataVersionLabel => DataVersion is int value ? $"DataVersion {value}" : "DataVersion 未知";

    internal static ProjectionClientResource? Read(string path, string? labelOverride = null)
    {
        try
        {
            path = System.IO.Path.GetFullPath(path);
            using var archive = ZipFile.OpenRead(path);
            var version = archive.GetEntry("version.json");
            int? dataVersion = null;
            string label = System.IO.Path.GetFileNameWithoutExtension(path);
            if (version is not null)
            {
                using var stream = version.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var json = JsonDocument.Parse(reader.ReadToEnd());
                if (json.RootElement.TryGetProperty("id", out var id))
                    label = id.GetString() ?? label;
                if (json.RootElement.TryGetProperty("world_version", out var worldVersion) &&
                    worldVersion.TryGetInt32(out var parsed))
                    dataVersion = parsed;
            }
            return new ProjectionClientResource(path, labelOverride ?? label, dataVersion, ComputeSha256(path));
        }
        catch
        {
            return null;
        }
    }

    public override string ToString() => $"{Label} · {DataVersionLabel}";

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }
}
