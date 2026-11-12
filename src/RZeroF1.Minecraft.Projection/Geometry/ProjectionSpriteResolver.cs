using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RZeroF1.Minecraft.Projection;

internal sealed record ProjectionSprite(
    string SpriteId, string TextureId, int LogicalWidth, int LogicalHeight,
    float U0, float V0, float U1, float V1, ProjectionTextureImage Image, string Evidence);
internal readonly record struct ProjectionSpriteRegion(float X, float Y, float Width, float Height);
internal sealed record ProjectionAtlasSource(string TextureId, ProjectionSpriteRegion? Region = null,
    float DivisorX = 0, float DivisorY = 0, string? Failure = null);

internal sealed class ProjectionSpriteResolver(ProjectionResourceSet resources)
{
    private const int MaxSources = 100_000;
    private readonly Dictionary<string, ProjectionAtlasSource> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Atlas, string Sprite), ProjectionAtlasSource> _scopedSources = [];
    private long _generation = -1;
    internal long Generation => _generation;
    internal long EstimatedCpuBytes => _sources.Sum(pair => 128L + pair.Key.Length * 2L + pair.Value.TextureId.Length * 2L);

    internal IReadOnlyList<string> EnumerateSprites(string atlasId)
    {
        if (_generation != resources.Generation) LoadSources(default);
        var atlas = ProjectionResourceSet.NormalizeResourceId(atlasId);
        return _scopedSources.Keys.Where(key => key.Atlas == atlas).Select(key => key.Sprite).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
    }

    internal ProjectionSprite Resolve(string textureId, int logicalWidth, int logicalHeight,
        ProjectionMaterialRole role, CancellationToken cancellationToken)
        => ResolveCore(null, textureId, logicalWidth, logicalHeight, role, cancellationToken);

    internal ProjectionSprite Resolve(string atlasId, string textureId, int logicalWidth, int logicalHeight,
        ProjectionMaterialRole role, CancellationToken cancellationToken)
        => ResolveCore(ProjectionResourceSet.NormalizeResourceId(atlasId), textureId, logicalWidth, logicalHeight, role, cancellationToken);

    private ProjectionSprite ResolveCore(string? atlasId, string textureId, int logicalWidth, int logicalHeight,
        ProjectionMaterialRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (logicalWidth <= 0 || logicalHeight <= 0) throw new ArgumentOutOfRangeException(nameof(logicalWidth));
        if (_generation != resources.Generation) LoadSources(cancellationToken);
        var id = ProjectionResourceSet.NormalizeResourceId(textureId);
        ProjectionAtlasSource? source = null;
        var mapped = atlasId is not null
            ? _scopedSources.TryGetValue((atlasId, id), out source)
            : _sources.TryGetValue(id, out source);
        // Logical material roles use their historical texture address, whereas
        // modern block-entity atlases omit the entity/ prefix from their sprite IDs.
        if (!mapped && id.StartsWith("minecraft:entity/", StringComparison.Ordinal))
        {
            var fallback = "minecraft:" + id["minecraft:entity/".Length..];
            mapped = atlasId is not null
                ? _scopedSources.TryGetValue((atlasId, fallback), out source)
                : _sources.TryGetValue(fallback, out source);
        }
        source ??= new(id);
        var image = resources.LoadTexture(source.TextureId);
        if (source.Failure is { } failure) image = image with { Failure = failure };
        var u0 = 0f; var v0 = 0f; var u1 = 1f; var v1 = 1f;
        if (source.Region is { } region && image.Failure is null)
        {
            var width = BinaryPrimitives.ReadInt32BigEndian(image.Png.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadInt32BigEndian(image.Png.AsSpan(20, 4));
            // Vanilla unstitch coordinates are units of width/divisor_x and height/divisor_y.
            // A zero divisor is reserved for the direct pixel-region test/legacy adapter.
            var dx = source.DivisorX == 0 ? width : source.DivisorX;
            var dy = source.DivisorY == 0 ? height : source.DivisorY;
            u0 = region.X / dx; v0 = region.Y / dy;
            u1 = (region.X + region.Width) / dx; v1 = (region.Y + region.Height) / dy;
            if (!float.IsFinite(u0 + v0 + u1 + v1) || u0 < 0 || v0 < 0 || u1 > 1 || v1 > 1 || u1 <= u0 || v1 <= v0)
                image = image with { Failure = "InvalidSpriteRegion" };
        }
        return new(id, source.TextureId, logicalWidth, logicalHeight, u0, v0, u1, v1, image,
            image.Failure ?? (mapped ? "Audited atlas source; logical UV independent of PNG resolution" : "Direct texture; decoded canonical animation frame"));
    }

    private void LoadSources(CancellationToken ct)
    {
        _sources.Clear();
        _scopedSources.Clear();
        long definitionBytes = 0;
        try
        {
            foreach (var path in resources.EnumeratePaths("assets/").Where(path =>
                path.Contains("/atlases/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                var atlas = AtlasId(path);
                var pathSprites = new HashSet<string>(StringComparer.Ordinal);
                foreach (var bytes in resources.ReadAtlasLayers(path))
                {
                    ct.ThrowIfCancellationRequested();
                    definitionBytes += bytes.Length;
                    if (definitionBytes > 16 * 1024 * 1024) throw new InvalidDataException("AtlasDefinitionBudgetExceeded");
                    try
                    {
                        var before = new Dictionary<string, ProjectionAtlasSource>(_sources, StringComparer.Ordinal);
                        using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 32 });
                        foreach (var source in json.RootElement.GetProperty("sources").EnumerateArray())
                        {
                            ct.ThrowIfCancellationRequested();
                            Apply(source, atlas, ct);
                            if (_sources.Count > MaxSources) throw new InvalidDataException("AtlasSpriteBudgetExceeded");
                        }
                        foreach (var pair in _sources)
                            if (!before.TryGetValue(pair.Key, out var previous) || previous != pair.Value) pathSprites.Add(pair.Key);
                    }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException or RegexMatchTimeoutException)
                    {
                        resources.SpriteWarning("InvalidAtlasDefinition: " + path + ": " + exception.GetType().Name);
                        foreach (var sprite in pathSprites)
                            _sources[sprite] = _sources[sprite] with { Failure = "InvalidAtlasDefinition" };
                    }
                }
            }
            _generation = resources.Generation;
        }
        catch { _sources.Clear(); _scopedSources.Clear(); _generation = -1; throw; }
    }

    private void Apply(JsonElement source, string atlas, CancellationToken ct)
    {
        var type = source.GetProperty("type").GetString()!;
        if (type.StartsWith("minecraft:", StringComparison.Ordinal)) type = type["minecraft:".Length..];
        switch (type)
        {
            case "paletted_permutations":
                // Armor-trim permutation sheets do not participate in the P3
                // banner, pot, sign, or head atlases. Keep those valid Atlas
                // sources quiet while leaving unsupported sprite identities explicit.
                break;
            case "single":
                {
                    var resource = Id(source.GetProperty("resource").GetString()!);
                    var sprite = source.TryGetProperty("sprite", out var value) ? Id(value.GetString()!) : resource;
                    var mapped = new ProjectionAtlasSource(resource);
                    _scopedSources[(atlas, sprite)] = mapped;
                    _sources[sprite] = mapped;
                    break;
                }
            case "unstitch":
                {
                    var resource = Id(source.GetProperty("resource").GetString()!);
                    if (source.TryGetProperty("regions", out var regions))
                    {
                        var dx = source.TryGetProperty("divisor_x", out var x) ? x.GetSingle() : 1;
                        var dy = source.TryGetProperty("divisor_y", out var y) ? y.GetSingle() : 1;
                        foreach (var region in regions.EnumerateArray())
                        {
                            ct.ThrowIfCancellationRequested();
                            var sprite = Id(region.GetProperty("sprite").GetString()!);
                            var mapped = new ProjectionAtlasSource(resource, new(region.GetProperty("x").GetSingle(), region.GetProperty("y").GetSingle(),
                                region.GetProperty("width").GetSingle(), region.GetProperty("height").GetSingle()), dx, dy,
                                dx <= 0 || dy <= 0 || !float.IsFinite(dx + dy) ? "InvalidSpriteDivisor" : null);
                            _scopedSources[(atlas, sprite)] = mapped;
                            _sources[sprite] = mapped;
                        }
                    }
                    else
                    {
                        // Existing pixel-region adapter retained for internal fixture compatibility.
                        var region = source.GetProperty("region");
                        var sprite = Id(source.GetProperty("sprite").GetString()!);
                        var mapped = new ProjectionAtlasSource(resource,
                            new(region[0].GetSingle(), region[1].GetSingle(), region[2].GetSingle(), region[3].GetSingle()));
                        _scopedSources[(atlas, sprite)] = mapped;
                        _sources[sprite] = mapped;
                    }
                    break;
                }
            case "directory":
                {
                    var directory = Id(source.GetProperty("source").GetString()!).Split(':', 2);
                    var prefix = source.GetProperty("prefix").GetString()!;
                    var pathPrefix = $"assets/{directory[0]}/textures/{directory[1]}/";
                    foreach (var path in resources.EnumeratePaths(pathPrefix))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!path.EndsWith(".png", StringComparison.Ordinal)) continue;
                        var relative = path[pathPrefix.Length..^4];
                        var sprite = Id(directory[0] + ":" + prefix.TrimEnd('/') + "/" + relative);
                        var mapped = new ProjectionAtlasSource(directory[0] + ":" + directory[1] + "/" + relative);
                        _scopedSources[(atlas, sprite)] = mapped;
                        _sources[sprite] = mapped;
                        if (_sources.Count > MaxSources) throw new InvalidDataException("AtlasSpriteBudgetExceeded");
                    }
                    break;
                }
            case "filter":
                {
                    var pattern = source.GetProperty("pattern");
                    var ns = pattern.TryGetProperty("namespace", out var n) ? n.GetString()! : ".*";
                    var path = pattern.TryGetProperty("path", out var p) ? p.GetString()! : ".*";
                    var namespaceRegex = new Regex("^(?:" + ns + ")$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                    var pathRegex = new Regex("^(?:" + path + ")$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                    foreach (var key in _sources.Keys.ToArray())
                    {
                        ct.ThrowIfCancellationRequested();
                        var parts = key.Split(':', 2);
                        if (namespaceRegex.IsMatch(parts[0]) && pathRegex.IsMatch(parts[1]))
                        {
                            var mapped = _sources[key] with { Failure = "AtlasSpriteFiltered" };
                            _sources[key] = mapped;
                            if (_scopedSources.ContainsKey((atlas, key))) _scopedSources[(atlas, key)] = mapped;
                        }
                    }
                    break;
                }
            default:
                resources.SpriteWarning("UnsupportedAtlasSource: " + type);
                break;
        }
    }

    private static string Id(string value)
    {
        var id = ProjectionResourceSet.NormalizeResourceId(value);
        if (!ProjectionVisualStateAdapter.IsResourceId(id) || id.Split('/').Any(part => part is "." or ".."))
            throw new JsonException("Invalid atlas resource ID");
        return id;
    }

    private static string AtlasId(string path)
    {
        var parts = path.Split('/');
        return parts.Length >= 4
            ? ProjectionResourceSet.NormalizeResourceId(parts[1] + ":" + parts[^1][..^5])
            : "minecraft:unknown";
    }
}
