using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;

namespace RZeroF1.Minecraft.Projection;

internal sealed record ProjectionFontGlyph(
    string? TextureId, float U0, float V0, float U1, float V1,
    float Width, float Height, float Advance, float Top, bool IsMissing = false);

internal sealed record ProjectionTextStyle(
    string? Color = null,
    bool Bold = false,
    bool Italic = false,
    bool Underlined = false,
    bool Strikethrough = false,
    bool Obfuscated = false,
    string Font = "minecraft:default");

internal sealed record ProjectionTextSpan(string Text, ProjectionTextStyle Style);
internal sealed record ProjectionTextParseResult(IReadOnlyList<ProjectionTextSpan> Spans, bool FullySupported);

internal sealed class ProjectionFontResolver(ProjectionResourceSet resources)
{
    private readonly Dictionary<string, JsonElement[]> _providerCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Font, int CodePoint), ProjectionFontGlyph?> _glyphCache = [];
    private readonly Dictionary<string, ProjectionPng.DecodedImage> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyDictionary<int, byte[]>> _unihexCache = new(StringComparer.OrdinalIgnoreCase);
    private long _generation = -1;

    internal void WarnMissingGlyph(int codePoint) =>
        resources.SpriteWarning($"MissingFontGlyph:U+{codePoint:X}");

    internal ProjectionFontGlyph? GetGlyph(string fontId, int codePoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_generation != resources.Generation)
        {
            _providerCache.Clear();
            _glyphCache.Clear();
            _imageCache.Clear();
            _unihexCache.Clear();
            _generation = resources.Generation;
        }
        var id = ProjectionResourceSet.NormalizeResourceId(string.IsNullOrWhiteSpace(fontId) ? "minecraft:default" : fontId);
        if (_glyphCache.TryGetValue((id, codePoint), out var cached))
            return cached;
        ProjectionFontGlyph? glyph = null;
        foreach (var provider in Providers(id, new HashSet<string>(StringComparer.OrdinalIgnoreCase), cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            glyph = provider.ValueKind == JsonValueKind.Object
                ? provider.GetProperty("type").GetString()?.ToLowerInvariant() switch
                {
                    "space" => Space(provider, codePoint),
                    "bitmap" => Bitmap(provider, codePoint, cancellationToken),
                    "legacy_unicode" => LegacyUnicode(provider, codePoint, cancellationToken),
                    "unihex" => Unihex(provider, codePoint, cancellationToken),
                    _ => null
                }
                : null;
            if (glyph is not null)
                break;
        }
        _glyphCache[(id, codePoint)] = glyph;
        return glyph;
    }

    private IReadOnlyList<JsonElement> Providers(string fontId, HashSet<string> stack, CancellationToken cancellationToken)
    {
        if (_providerCache.TryGetValue(fontId, out var cached))
            return cached;
        if (stack.Count >= 64 || !stack.Add(fontId))
            return Array.Empty<JsonElement>();
        var providers = new List<JsonElement>();
        try
        {
            var layers = resources.ReadResourceLayers(FontPath(fontId, ".json"));
            var optionStack = new List<JsonElement>();
            foreach (var bytes in layers)
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                if (!document.RootElement.TryGetProperty("providers", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var entry in entries.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    var type = entry.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
                    if (type == "reference" && entry.TryGetProperty("id", out var reference))
                    {
                        var referenceId = ProjectionResourceSet.NormalizeResourceId(reference.GetString() ?? "minecraft:default");
                        var nestedProviders = Providers(referenceId, stack, cancellationToken);
                        if (entry.TryGetProperty("filter", out var filter))
                            optionStack.Add(filter.Clone());
                        foreach (var nested in nestedProviders)
                        {
                            if (optionStack.Any(option => !ProviderPassesOptions(nested, option))) continue;
                            providers.Add(nested);
                        }
                        if (entry.TryGetProperty("filter", out _)) optionStack.RemoveAt(optionStack.Count - 1);
                    }
                    else
                        providers.Add(entry.Clone());
                }
            }
            if (providers.Count > 100_000) throw new InvalidDataException("FontProviderBudgetExceeded");
            _providerCache[fontId] = providers.ToArray();
            return _providerCache[fontId];
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            resources.SpriteWarning($"InvalidFontDefinition:{fontId}:{exception.GetType().Name}");
            return Array.Empty<JsonElement>();
        }
        finally { stack.Remove(fontId); }
    }

    private static bool ProviderPassesOptions(JsonElement provider, JsonElement filter)
    {
        if (provider.ValueKind == JsonValueKind.Object &&
            provider.TryGetProperty("filter", out var providerFilter) &&
            providerFilter.ValueKind == JsonValueKind.Object && providerFilter.TryGetProperty("jp", out var jp) &&
            jp.ValueKind is JsonValueKind.True or JsonValueKind.False && jp.GetBoolean() != false)
            return false;
        if (!filter.TryGetProperty("uniform", out var uniformFilter) || uniformFilter.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return true;
        if (!provider.TryGetProperty("type", out var type) || type.GetString() != "bitmap" ||
            !provider.TryGetProperty("chars", out var chars) || chars.ValueKind != JsonValueKind.Array)
            return true;
        var hasUniform = chars.EnumerateArray().Any(row => row.GetString()?.EnumerateRunes().Any(rune => rune.Value is >= 0xE000 and <= 0xF8FF) == true);
        return hasUniform == uniformFilter.GetBoolean();
    }

    private ProjectionFontGlyph? Space(JsonElement provider, int codePoint)
    {
        if (!provider.TryGetProperty("advances", out var advances) || advances.ValueKind != JsonValueKind.Object)
            return null;
        if (!Rune.TryCreate(codePoint, out var rune)) return null;
        var key = rune.ToString();
        if (!advances.TryGetProperty(key, out var value) || !value.TryGetSingle(out var advance))
            return null;
        return new(null, 0, 0, 0, 0, 0, 0, advance, 0);
    }

    private ProjectionFontGlyph? Bitmap(JsonElement provider, int codePoint, CancellationToken cancellationToken)
    {
        if (!provider.TryGetProperty("file", out var file) || !provider.TryGetProperty("chars", out var chars) ||
            chars.ValueKind != JsonValueKind.Array)
            return null;
        var fileId = ProjectionResourceSet.NormalizeResourceId(file.GetString() ?? "minecraft:font/missing.png");
        var rows = chars.EnumerateArray().Select(value => value.GetString() ?? string.Empty).ToArray();
        var row = -1;
        var column = -1;
        for (var y = 0; y < rows.Length && row < 0; y++)
        {
            var columns = rows[y].EnumerateRunes().ToArray();
            for (var x = 0; x < columns.Length; x++)
                if (columns[x].Value == codePoint) { row = y; column = x; break; }
        }
        if (row < 0 || column < 0) return null;
        var image = Image(fileId, cancellationToken);
        if (image is null) return null;
        var columnCount = rows.Length == 0 ? 0 : rows.Max(value => value.EnumerateRunes().Count());
        if (columnCount <= 0 || image.Width % columnCount != 0 || image.Height % rows.Length != 0)
            return null;
        var cellWidth = image.Width / columnCount;
        var cellHeight = image.Height / rows.Length;
        var (left, top, right, bottom) = AlphaBounds(image, column * cellWidth, row * cellHeight, cellWidth, cellHeight);
        if (right <= left || bottom <= top) return new(null, 0, 0, 0, 0, 0, 0, 0, 0);
        var height = provider.TryGetProperty("height", out var heightValue) && heightValue.TryGetSingle(out var suppliedHeight)
            ? suppliedHeight : 8;
        var ascent = provider.TryGetProperty("ascent", out var ascentValue) && ascentValue.TryGetSingle(out var suppliedAscent)
            ? suppliedAscent : height;
        var advance = Advance(provider, (right - left) * height / cellHeight + 1);
        return new(fileId,
            (column * cellWidth + left) / (float)image.Width,
            (row * cellHeight + top) / (float)image.Height,
            (column * cellWidth + right) / (float)image.Width,
            (row * cellHeight + bottom) / (float)image.Height,
            (right - left) * height / cellHeight, (bottom - top) * height / cellHeight, advance,
            (cellHeight - bottom + ascent) * height / cellHeight);
    }

    private ProjectionFontGlyph? LegacyUnicode(JsonElement provider, int codePoint, CancellationToken cancellationToken)
    {
        if ((uint)codePoint > 0xffff || !provider.TryGetProperty("sizes", out var sizeValue) ||
            !provider.TryGetProperty("template", out var templateValue)) return null;
        var sizes = resources.Read(FontPath(sizeValue.GetString() ?? "minecraft:font/glyph_sizes.bin", string.Empty));
        if (sizes is null || sizes.Length <= codePoint) return null;
        var packed = sizes[codePoint];
        var left = packed >> 4;
        var right = packed & 0x0f;
        if (right <= left) return null;
        var page = (codePoint >> 8).ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
        var template = templateValue.GetString() ?? "minecraft:font/unicode_page_%s.png";
        var fileId = ProjectionResourceSet.NormalizeResourceId(template.Replace("%s", page, StringComparison.Ordinal));
        var image = Image(fileId, cancellationToken);
        if (image is null || image.Width < 256 || image.Height < 256) return null;
        var cellX = (codePoint & 15) * 16;
        var cellY = ((codePoint >> 4) & 15) * 16;
        return new(fileId, (cellX + left) / (float)image.Width, cellY / (float)image.Height,
            (cellX + right) / (float)image.Width, (cellY + 16) / (float)image.Height,
            right - left, 16, Advance(provider, right - left + 1), 16);
    }

    private ProjectionFontGlyph? Unihex(JsonElement provider, int codePoint, CancellationToken cancellationToken)
    {
        if (!provider.TryGetProperty("hex_file", out var hexFile)) return null;
        var fileId = ProjectionResourceSet.NormalizeResourceId(hexFile.GetString() ?? "minecraft:font/unifont.zip");
        if (!_unihexCache.TryGetValue(fileId, out var entries))
        {
            var bytes = resources.Read(FontPath(fileId, string.Empty));
            if (bytes is null) return null;
            var found = new Dictionary<int, byte[]>();
            try
            {
                using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read, leaveOpen: false);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.Length > 32 * 1024 * 1024 || entry.FullName.EndsWith('/')) continue;
                    using var input = entry.Open();
                    using var reader = new StreamReader(input, Encoding.ASCII, detectEncodingFromByteOrderMarks: true);
                    while (reader.ReadLine() is { } line)
                    {
                        var separator = line.IndexOf(':');
                        if (separator <= 0 || !int.TryParse(line[..separator], System.Globalization.NumberStyles.HexNumber, null, out var id)) continue;
                        try { found[id] = Convert.FromHexString(line[(separator + 1)..].Trim()); }
                        catch (FormatException) { }
                    }
                }
            }
            catch (InvalidDataException) { return null; }
            entries = found;
            _unihexCache[fileId] = entries;
        }
        if (!entries.TryGetValue(codePoint, out var glyphBytes) || glyphBytes.Length == 0) return null;
        var rowCount = 16;
        var width = glyphBytes.Length * 8 / rowCount;
        if (width is not (8 or 16 or 24 or 32)) return null;
        var rgba = new byte[checked(width * rowCount * 4)];
        for (var y = 0; y < rowCount; y++)
        for (var x = 0; x < width; x++)
        {
            var bit = (glyphBytes[y * ((width + 7) / 8) + x / 8] >> (7 - x % 8)) & 1;
            var offset = (y * width + x) * 4;
            rgba[offset] = rgba[offset + 1] = rgba[offset + 2] = 255;
            rgba[offset + 3] = bit == 0 ? (byte)0 : (byte)255;
        }
        var stableId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fileId)))[..16].ToLowerInvariant();
        var derivedId = $"minecraft:__derived/font/{stableId}-{codePoint:x}";
        var png = ProjectionPng.EncodeBgra(rgba, width, rowCount);
        resources.RegisterDerivedTexture(derivedId, png, true);
        return new(derivedId, 0, 0, 1, 1, width, rowCount, width, rowCount);
    }

    private ProjectionPng.DecodedImage? Image(string fileId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_imageCache.TryGetValue(fileId, out var cached)) return cached;
        var image = resources.LoadTexture(fileId);
        if (image.Failure is not null) return null;
        try
        {
            var decoded = ProjectionPng.Decode(image.Png);
            _imageCache[fileId] = decoded;
            return decoded;
        }
        catch (InvalidDataException) { return null; }
    }

    private static float Advance(JsonElement provider, float fallback)
    {
        if (!provider.TryGetProperty("shift", out var shift) || shift.ValueKind != JsonValueKind.Array || shift.GetArrayLength() != 2 ||
            !shift[0].TryGetSingle(out var x) || !shift[1].TryGetSingle(out var y) || !float.IsFinite(x + y))
            return fallback;
        return MathF.Ceiling(x + fallback);
    }

    private static (int Left, int Top, int Right, int Bottom) AlphaBounds(
        ProjectionPng.DecodedImage image, int x, int y, int width, int height)
    {
        var left = width; var top = height; var right = 0; var bottom = 0;
        for (var row = 0; row < height; row++)
        for (var column = 0; column < width; column++)
        {
            if (image.Rgba[((y + row) * image.Width + x + column) * 4 + 3] == 0) continue;
            left = Math.Min(left, column); top = Math.Min(top, row);
            right = Math.Max(right, column + 1); bottom = Math.Max(bottom, row + 1);
        }
        return (left, top, right, bottom);
    }

    internal static string FontPath(string id, string extension)
    {
        var normalized = ProjectionResourceSet.NormalizeResourceId(id);
        var split = normalized.Split(':', 2);
        var path = split[1];
        if (path.StartsWith("font/", StringComparison.Ordinal))
            return $"assets/{split[0]}/{path}";
        if (!string.IsNullOrEmpty(extension) && !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            path += extension;
        return $"assets/{split[0]}/font/{path}";
    }
}

internal static class ProjectionTextComponentReader
{
    internal static ProjectionTextParseResult Read(ProjectionVisualValue value, ProjectionResourceSet resources,
        CancellationToken cancellationToken)
    {
        var spans = new List<ProjectionTextSpan>();
        var supported = true;
        Append(value, new(), spans, resources, cancellationToken, ref supported, 0);
        return new(spans.AsReadOnly(), supported);
    }

    private static void Append(ProjectionVisualValue value, ProjectionTextStyle style, List<ProjectionTextSpan> spans,
        ProjectionResourceSet resources, CancellationToken cancellationToken, ref bool supported, int depth)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > 64) { supported = false; return; }
        if (value.Kind == "List" && value.Elements is not null)
        {
            foreach (var item in value.Elements) Append(item, style, spans, resources, cancellationToken, ref supported, depth + 1);
            return;
        }
        if (value.Kind == "String")
        {
            var scalar = value.Scalar ?? string.Empty;
            if (scalar.Length > 0 && (scalar[0] == '{' || scalar[0] == '[' || scalar[0] == '"'))
            {
                try
                {
                    using var json = JsonDocument.Parse(scalar);
                    AppendJson(json.RootElement, style, spans, resources, cancellationToken, ref supported, depth + 1);
                    return;
                }
                catch (JsonException) { supported = false; }
            }
            if (scalar.Length > 0) spans.Add(new(scalar, style));
            return;
        }
        if (value.Kind != "Object" || value.Fields is null)
        {
            supported = false;
            return;
        }
        var nextStyle = ApplyStyle(value.Fields, style, ref supported);
        if (value.Fields.TryGetValue("text", out var text))
            AppendTextValue(text, nextStyle, spans);
        else if (value.Fields.TryGetValue("translate", out var translate))
        {
            var key = translate.Scalar ?? string.Empty;
            var translated = resources.Translate(key) ?? key;
            if (resources.Translate(key) is null) supported = false;
            if (value.Fields.TryGetValue("with", out var arguments) && arguments.Elements is not null)
            {
                var values = new string[arguments.Elements.Count];
                for (var index = 0; index < arguments.Elements.Count; index++)
                    values[index] = Flatten(arguments.Elements[index], resources, cancellationToken, ref supported);
                for (var index = 0; index < values.Length; index++) translated = translated.Replace("{" + index + "}", values[index], StringComparison.Ordinal);
            }
            spans.Add(new(translated, nextStyle));
        }
        else
        {
            foreach (var key in new[] { "score", "selector", "nbt", "keybind" })
                if (value.Fields.ContainsKey(key)) { supported = false; spans.Add(new("?", nextStyle)); break; }
        }
        if (value.Fields.TryGetValue("extra", out var extra) && extra.Elements is not null)
            foreach (var item in extra.Elements) Append(item, nextStyle, spans, resources, cancellationToken, ref supported, depth + 1);
    }

    private static string Flatten(ProjectionVisualValue value, ProjectionResourceSet resources,
        CancellationToken cancellationToken, ref bool supported)
    {
        var spans = Read(value, resources, cancellationToken);
        supported &= spans.FullySupported;
        return string.Concat(spans.Spans.Select(span => span.Text));
    }

    private static void AppendTextValue(ProjectionVisualValue value, ProjectionTextStyle style, List<ProjectionTextSpan> spans)
    {
        if (value.Kind == "String" && !string.IsNullOrEmpty(value.Scalar)) spans.Add(new(value.Scalar, style));
        else if (value.Kind == "List" && value.Elements is not null)
            foreach (var item in value.Elements) AppendTextValue(item, style, spans);
    }

    private static ProjectionTextStyle ApplyStyle(IReadOnlyDictionary<string, ProjectionVisualValue> fields,
        ProjectionTextStyle style, ref bool supported)
    {
        string? Text(string key) => fields.TryGetValue(key, out var value) ? value.Scalar : null;
        bool Bool(string key, bool fallback)
        {
            if (!fields.TryGetValue(key, out var value)) return fallback;
            if (value.Kind is "Byte" or "Short" or "Int") return value.Scalar == "1";
            return value.Scalar?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? fallback;
        }
        return style with
        {
            Color = Text("color") ?? style.Color,
            Bold = Bool("bold", style.Bold),
            Italic = Bool("italic", style.Italic),
            Underlined = Bool("underlined", style.Underlined),
            Strikethrough = Bool("strikethrough", style.Strikethrough),
            Obfuscated = Bool("obfuscated", style.Obfuscated),
            Font = Text("font") is { Length: > 0 } font ? ProjectionResourceSet.NormalizeResourceId(font) : style.Font
        };
    }

    private static void AppendJson(JsonElement value, ProjectionTextStyle style, List<ProjectionTextSpan> spans,
        ProjectionResourceSet resources, CancellationToken cancellationToken, ref bool supported, int depth)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            spans.Add(new(value.GetString() ?? string.Empty, style));
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) AppendJson(item, style, spans, resources, cancellationToken, ref supported, depth + 1);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) { supported = false; return; }
        var fields = new Dictionary<string, ProjectionVisualValue>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject())
            fields[item.Name] = JsonValue(item.Value);
        Append(new("Object", null, fields), style, spans, resources, cancellationToken, ref supported, depth + 1);
    }

    private static ProjectionVisualValue JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => new("String", value.GetString()),
        JsonValueKind.Number => new("Double", value.GetRawText()),
        JsonValueKind.True => new("Byte", "1"),
        JsonValueKind.False => new("Byte", "0"),
        JsonValueKind.Array => new("List", null, Elements: value.EnumerateArray().Select(JsonValue).ToArray()),
        JsonValueKind.Object => new("Object", null, value.EnumerateObject().ToDictionary(item => item.Name, item => JsonValue(item.Value), StringComparer.Ordinal)),
        _ => new("String", string.Empty)
    };
}
