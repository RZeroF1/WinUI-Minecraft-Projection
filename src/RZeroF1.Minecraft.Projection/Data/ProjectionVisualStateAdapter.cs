using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using MinecraftRenderer.Nbt;

namespace RZeroF1.Minecraft.Projection;

internal enum ProjectionVisualDataStatus { Known, Unknown, Invalid }
internal sealed record ProjectionVisualField<T>(ProjectionVisualDataStatus Status, T? Value, string Evidence)
{
    internal static ProjectionVisualField<T> Known(T value) => new(ProjectionVisualDataStatus.Known, value, "Adapted source data");
    internal static ProjectionVisualField<T> Unknown(string reason) => new(ProjectionVisualDataStatus.Unknown, default, reason);
    internal static ProjectionVisualField<T> Invalid(string reason) => new(ProjectionVisualDataStatus.Invalid, default, reason);
}

// SDK-owned immutable values preserve component/appearance details without exposing
// a parser object to a later item, text or entity implementation.
internal sealed record ProjectionVisualValue(string Kind, string? Scalar,
    IReadOnlyDictionary<string, ProjectionVisualValue>? Fields = null, IReadOnlyList<ProjectionVisualValue>? Elements = null);
internal sealed record ProjectionSignSide(IReadOnlyList<ProjectionVisualValue> Messages,
    IReadOnlyList<ProjectionVisualValue>? FilteredMessages, string Color, bool Glowing, bool IsEmpty);
internal sealed record ProjectionSignVisual(ProjectionSignSide Front, ProjectionSignSide? Back)
{
    internal bool IsEmpty => Front.IsEmpty && (Back?.IsEmpty ?? true);
}
internal sealed record ProjectionItemVisual(int Slot, string Id, int Count, ProjectionVisualValue? Components);
internal sealed record ProjectionPatternVisual(string Pattern, string Color);
// Preserve ordered-list consumption while carrying the separately adapted base color.
internal sealed record ProjectionBannerVisual(string BaseColor, IReadOnlyList<ProjectionPatternVisual> Layers)
    : IReadOnlyList<ProjectionPatternVisual>
{
    public int Count => Layers.Count;
    public ProjectionPatternVisual this[int index] => Layers[index];
    public IEnumerator<ProjectionPatternVisual> GetEnumerator() => Layers.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
internal sealed record ProjectionPotSideVisual(string Side, string? ItemId, string? PatternId,
    ProjectionVisualValue? Components, bool IsDefault, string? PatternAssetId = null);
internal sealed record ProjectionVisualState(
    ProjectionVisualDataProfile Profile,
    ProjectionVisualField<ProjectionSignVisual> Sign,
    ProjectionVisualField<IReadOnlyList<ProjectionItemVisual>> Items,
    ProjectionVisualField<ProjectionBannerVisual> Patterns,
    ProjectionVisualField<ProjectionHeadAppearance> Appearance,
    ProjectionVisualField<ProjectionEntityVisual> DisplayEntity,
    ProjectionVisualField<IReadOnlyList<string>> PotSides,
    ProjectionVisualField<IReadOnlyList<ProjectionPotSideVisual>> PotSideDetails,
    ProjectionVisualField<string> BedColor);

internal static class ProjectionVisualStateAdapter
{
    private static readonly string[] Dyes = ["white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray",
        "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black"];
    private static readonly IReadOnlyDictionary<string, string> LegacySignColorAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["dark_blue"] = "blue",
            ["dark_green"] = "green",
            ["dark_aqua"] = "cyan",
            ["dark_red"] = "red",
            ["dark_purple"] = "purple",
            ["gold"] = "orange",
            ["dark_gray"] = "gray",
            ["aqua"] = "cyan",
            ["light_purple"] = "magenta"
        };

    internal static ProjectionVisualState Adapt(ProjectionState state, ProjectionBlockEntityData? data,
        int? sourceDataVersion, CancellationToken cancellationToken, string? targetVersionId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = ProjectionVisualDataProfile.Select(sourceDataVersion);
        var payload = data?.Payload;
        var reason = payload is null ? "MissingBlockEntityData" : "UnknownSourceDataProfile";
        ProjectionVisualField<T> Missing<T>() => ProjectionVisualField<T>.Unknown(reason);
        var sign = Missing<ProjectionSignVisual>();
        var items = Missing<IReadOnlyList<ProjectionItemVisual>>();
        var patterns = Missing<ProjectionBannerVisual>();
        var appearance = Missing<ProjectionHeadAppearance>();
        var entity = Missing<ProjectionEntityVisual>();
        var sides = Missing<IReadOnlyList<string>>();
        var sideDetails = Missing<IReadOnlyList<ProjectionPotSideVisual>>();
        var bed = Missing<string>();
        if (payload is not null && profile.Schema != ProjectionVisualDataSchema.Unknown)
        {
            // Each field adapts independently: an invalid unrelated field cannot turn
            // known contents into empty, or poison ordinary block geometry.
            sign = ReadSign(payload, profile.Sign, cancellationToken);
            items = ReadItems(payload, profile.Items, state, cancellationToken);
            if (state.Name.Contains("banner", StringComparison.Ordinal))
                patterns = ReadPatterns(payload, profile, state.Name, cancellationToken);
            var modern = profile.Head is ProjectionHeadDataSchema.ResolvableProfile or ProjectionHeadDataSchema.ProfileWithSkinPatch;
            appearance = ProjectionAppearanceAdapter.ReadHead(payload, modern, cancellationToken);
            if (!modern && profile.Head == ProjectionHeadDataSchema.SkullOwner && payload.ContainsKey("Owner"))
                appearance = ProjectionAppearanceAdapter.ReadHead(payload, false, cancellationToken);
            entity = ProjectionAppearanceAdapter.ReadEntity(payload, cancellationToken);
            var targetNamedPot = targetVersionId is "26.3" or "26.4-snapshot-6";
            var potSchema = profile.Pot == ProjectionPotDataSchema.Unavailable && profile.DataVersion == 3120
                ? ProjectionPotDataSchema.Unavailable : profile.Pot;
            if (targetNamedPot && payload.ContainsKey("sherds") && potSchema != ProjectionPotDataSchema.NamedItemTemplates)
            {
                (sides, sideDetails) = ReadNamedPotSides(payload, cancellationToken);
            }
            else if (profile.Pot == ProjectionPotDataSchema.NamedItemTemplates)
            {
                (sides, sideDetails) = ReadNamedPotSides(payload, cancellationToken);
            }
            else if ((profile.Pot is ProjectionPotDataSchema.Shards or ProjectionPotDataSchema.Sherds) &&
                payload.GetList(profile.Pot == ProjectionPotDataSchema.Shards ? "shards" : "sherds") is { } sherds)
            {
                var values = sherds.OfType<NbtString>().Select(value => value.Value).ToArray();
                if (sherds.Count > 4 || values.Length != sherds.Count || values.Any(value => !IsResourceId(value)))
                    sides = ProjectionVisualField<IReadOnlyList<string>>.Invalid("InvalidPotSides");
                else
                {
                    // Vanilla's list codec accepts 0..4 entries. PotDecorations
                    // reads the four fixed positions and uses an empty Optional
                    // past the list length; the renderer then selects the default
                    // decorated-pot side (minecraft:brick).
                    var padded = values.Concat(Enumerable.Repeat("minecraft:brick", 4 - values.Length)).ToArray();
                    sides = ProjectionVisualField<IReadOnlyList<string>>.Known(Array.AsReadOnly(padded));
                    var adaptedSides = PadPotSides(values.Select((value, index) => new ProjectionPotSideVisual(
                        PotSideName(index), value, profile.DataVersion == 3337 ? LegacyShardPattern(value) : PatternFromItem(value), null,
                        value == "minecraft:brick" || value == "minecraft:brick_sherd" || value == "minecraft:brick_shard")).ToArray());
                    if (targetNamedPot)
                        adaptedSides = adaptedSides.Select(side => side with { PatternId = side.IsDefault ? null : side.PatternId }).ToArray();
                    sideDetails = ProjectionVisualField<IReadOnlyList<ProjectionPotSideVisual>>.Known(Array.AsReadOnly(adaptedSides));
                }
            }
            if (profile.Bed == ProjectionBedDataSchema.ColorNbt && payload.GetInt("color") is { } color)
                bed = color is >= 0 and < 16 ? ProjectionVisualField<string>.Known(Dyes[color])
                    : ProjectionVisualField<string>.Invalid("InvalidBedColor");
        }
        // Flattened bed color is a source block-state property, independent of BE.
        if (profile.Bed == ProjectionBedDataSchema.ColorBlockId)
            foreach (var color in Dyes)
                if (state.Name == "minecraft:" + color + "_bed") bed = ProjectionVisualField<string>.Known(color);
        if (profile.Bed == ProjectionBedDataSchema.ColorNbt && payload is not null && !payload.ContainsKey("color"))
            bed = ProjectionVisualField<string>.Invalid("MissingBedColor");
        if (profile.Bed == ProjectionBedDataSchema.ColorNbt && payload is null)
            bed = ProjectionVisualField<string>.Unknown(reason);
        return new(profile, sign, items, patterns, appearance, entity, sides, sideDetails, bed);
    }

    private static (ProjectionVisualField<IReadOnlyList<string>> Sides,
        ProjectionVisualField<IReadOnlyList<ProjectionPotSideVisual>> Details) ReadNamedPotSides(
        NbtCompound payload, CancellationToken cancellationToken)
    {
        if (!payload.TryGetValue("sherds", out var raw))
            return (ProjectionVisualField<IReadOnlyList<string>>.Unknown("MissingNamedPotSides"),
                ProjectionVisualField<IReadOnlyList<ProjectionPotSideVisual>>.Unknown("MissingNamedPotSides"));
        if (raw is not NbtCompound compound)
            return (ProjectionVisualField<IReadOnlyList<string>>.Unknown("InvalidNamedPotSides"),
                ProjectionVisualField<IReadOnlyList<ProjectionPotSideVisual>>.Invalid("InvalidNamedPotSides"));
        var result = new List<ProjectionPotSideVisual>(4);
        foreach (var (side, index) in new[] { ("back", 0), ("left", 1), ("right", 2), ("front", 3) })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!compound.TryGetValue(side, out var value))
            {
                result.Add(new(side, null, null, null, true));
                continue;
            }
            if (value is NbtString text && IsResourceId(text.Value))
            {
                var isDefault = text.Value is "minecraft:brick" or "minecraft:brick_sherd" or "minecraft:brick_shard";
                result.Add(new(side, text.Value, PatternFromItem(text.Value), null, isDefault));
                continue;
            }
            if (value is not NbtCompound item)
                return (ProjectionVisualField<IReadOnlyList<string>>.Invalid("InvalidNamedPotSide"),
                    ProjectionVisualField<IReadOnlyList<ProjectionPotSideVisual>>.Invalid("InvalidNamedPotSide"));
            var itemId = item.GetString("id") ?? item.GetString("item");
            if (itemId is null || !IsResourceId(itemId))
                return (ProjectionVisualField<IReadOnlyList<string>>.Invalid("InvalidNamedPotItemId"),
                    ProjectionVisualField<IReadOnlyList<ProjectionPotSideVisual>>.Invalid("InvalidNamedPotItemId"));
            item.TryGetValue("components", out var componentTag);
            var components = componentTag is null ? null : Copy(componentTag, cancellationToken);
            var pattern = ComponentPattern(components);
            var patternId = pattern.Present ? pattern.Id : PatternFromItem(itemId);
            var defaultItem = pattern.Removed || !pattern.Present && itemId is
                "minecraft:brick" or "minecraft:brick_sherd" or "minecraft:brick_shard";
            result.Add(new(side, itemId, patternId, components, defaultItem, pattern.AssetId));
        }
        var details = Array.AsReadOnly(result.ToArray());
        var ids = Array.AsReadOnly(result.Select(item => item.IsDefault ? "minecraft:brick" :
            item.ItemId ?? item.PatternId ?? item.PatternAssetId ?? "minecraft:brick").ToArray());
        return (ProjectionVisualField<IReadOnlyList<string>>.Known(ids),
            ProjectionVisualField<IReadOnlyList<ProjectionPotSideVisual>>.Known(details));
    }

    private static ProjectionPotSideVisual[] PadPotSides(ProjectionPotSideVisual[] values)
    {
        if (values.Length > 4) throw new InvalidDataException("InvalidPotSides");
        return values.Concat(Enumerable.Range(values.Length, 4 - values.Length)
            .Select(index => new ProjectionPotSideVisual(PotSideName(index), null, null, null, true))).ToArray();
    }

    private static string PotSideName(int index) => index switch
    {
        0 => "back", 1 => "left", 2 => "right", _ => "front"
    };

    private static string? PatternFromItem(string? itemId)
    {
        // Vanilla item defaults are a finite table. A custom item's name is not
        // evidence that it provides a pottery pattern.
        if (itemId is null || !itemId.StartsWith("minecraft:", StringComparison.Ordinal)) return null;
        var name = itemId["minecraft:".Length..];
        if (!name.EndsWith("_pottery_sherd", StringComparison.Ordinal)) return null;
        var pattern = name[..^"_pottery_sherd".Length];
        if (pattern is "angler" or "archer" or "arms_up" or "blade" or "brewer" or "burn" or "danger" or
            "explorer" or "flow" or "friend" or "guster" or "heart" or "heartbreak" or "howl" or "miner" or
            "mourner" or "plenty" or "prize" or "scrape" or "sheaf" or "shelter" or "skull" or "snort")
            return "minecraft:" + pattern;
        return null;
    }

    private static string? LegacyShardPattern(string itemId) => itemId switch
    {
        "minecraft:archer_pottery_shard" => "minecraft:pottery_pattern_archer",
        "minecraft:arms_up_pottery_shard" => "minecraft:pottery_pattern_arms_up",
        "minecraft:prize_pottery_shard" => "minecraft:pottery_pattern_prize",
        "minecraft:skull_pottery_shard" => "minecraft:pottery_pattern_skull",
        _ => null
    };

    private static (bool Present, bool Removed, string? Id, string? AssetId) ComponentPattern(ProjectionVisualValue? components)
    {
        if (components?.Fields is not { } fields) return default;
        // DataComponentPatch uses !component_id for an explicit removal.
        if (fields.ContainsKey("!minecraft:provides_pottery_pattern") || fields.ContainsKey("!provides_pottery_pattern"))
            return (true, true, null, null);
        if (!fields.TryGetValue("minecraft:provides_pottery_pattern", out var value) &&
            !fields.TryGetValue("provides_pottery_pattern", out value))
            return default;
        if (value.Kind == "String" && value.Scalar is { } scalar && IsResourceId(scalar))
            return (true, false, scalar, null);
        if (value.Fields?.GetValueOrDefault("asset_id")?.Scalar is { } asset && IsResourceId(asset))
        {
            // Inline Holder<DecoratedPotPattern> carries an asset_id, not a
            // registry ID. Keep the distinction through Sprite resolution.
            return (true, false, null, asset);
        }
        // An invalid explicit override must not silently resurrect item defaults.
        return (true, false, null, null);
    }

    private static ProjectionVisualField<ProjectionSignVisual> ReadSign(NbtCompound payload, ProjectionSignDataSchema schema,
        CancellationToken cancellationToken)
    {
        try
        {
            ProjectionSignSide Side(NbtCompound owner, bool legacy)
            {
                var messages = legacy
                    ? Enumerable.Range(1, 4).Select(index => owner.TryGetValue("Text" + index, out var value) ? value : null).ToArray()
                    : owner.GetList("messages")?.ToArray();
                if (messages is not { Length: 4 } || messages.Any(value => value is null)) throw new InvalidDataException("MissingSignMessages");
                bool Empty(NbtTag value)
                {
                    if (schema == ProjectionSignDataSchema.TwoSidedNbt)
                    {
                        if (value is not (NbtString or NbtCompound or NbtList)) throw new InvalidDataException("InvalidSignTextEncoding");
                        return value is NbtString { Value.Length: 0 } || value is NbtCompound compound &&
                            compound.Count == 1 && compound.GetString("text") == "";
                    }
                    if (value is not NbtString text) throw new InvalidDataException("InvalidSignTextEncoding");
                    using var json = JsonDocument.Parse(text.Value);
                    var root = json.RootElement;
                    if (root.ValueKind == JsonValueKind.String) return root.GetString() == "";
                    if (root.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) throw new InvalidDataException("InvalidSignComponent");
                    return root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 1 &&
                        root.TryGetProperty("text", out var plain) && plain.ValueKind == JsonValueKind.String && plain.GetString() == "";
                }
                var colorKey = legacy ? "Color" : "color";
                var color = owner.TryGetValue(colorKey, out var rawColor)
                    ? rawColor switch
                    {
                        NbtString text => text.Value,
                        NbtByte number when legacy && number.Value is >= 0 and < 16 => Dyes[number.Value],
                        NbtShort number when legacy && number.Value is >= 0 and < 16 => Dyes[number.Value],
                        NbtInt number when legacy && number.Value is >= 0 and < 16 => Dyes[number.Value],
                        _ => null
                    }
                    : "black";
                if (color is null) throw new InvalidDataException("InvalidSignColor");
                if ((schema is ProjectionSignDataSchema.SingleJson or ProjectionSignDataSchema.TwoSidedJson) &&
                    LegacySignColorAliases.TryGetValue(color, out var alias))
                    color = alias;
                if (!Dyes.Contains(color)) throw new InvalidDataException("InvalidSignColor");
                var glowing = ReadBool(owner, legacy ? "GlowingText" : "has_glowing_text");
                var empty = true;
                foreach (var message in messages) empty &= Empty(message!);
                var filtered = owner.GetList("filtered_messages")?.ToArray();
                if (owner.ContainsKey("filtered_messages") && filtered is null) throw new InvalidDataException("InvalidFilteredMessages");
                if (legacy && Enumerable.Range(1, 4).Any(index => owner.ContainsKey("FilteredText" + index)))
                    filtered = Enumerable.Range(1, 4).Select(index => owner.TryGetValue("FilteredText" + index, out var value) ? value : messages[index - 1]!).ToArray();
                if (filtered is not null && filtered.Length != 4) throw new InvalidDataException("InvalidFilteredMessages");
                if (filtered is not null) foreach (var message in filtered) _ = Empty(message);
                return new(Array.AsReadOnly(messages.Select(value => Copy(value!, cancellationToken)).ToArray()),
                    filtered is null ? null : Array.AsReadOnly(filtered.Select(value => Copy(value, cancellationToken)).ToArray()), color, glowing, empty);
            }
            if (schema == ProjectionSignDataSchema.SingleJson)
                return ProjectionVisualField<ProjectionSignVisual>.Known(new(Side(payload, true), null));
            if (payload.GetCompound("front_text") is not { } front || payload.GetCompound("back_text") is not { } back)
                return ProjectionVisualField<ProjectionSignVisual>.Unknown("MissingSignSides");
            return ProjectionVisualField<ProjectionSignVisual>.Known(new(Side(front, false), Side(back, false)));
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        { return ProjectionVisualField<ProjectionSignVisual>.Invalid(exception.Message); }
    }

    private static ProjectionVisualField<IReadOnlyList<ProjectionItemVisual>> ReadItems(NbtCompound payload,
        ProjectionItemDataSchema schema, ProjectionState state, CancellationToken cancellationToken)
    {
        var modern = schema == ProjectionItemDataSchema.Components;
        var key = payload.ContainsKey("Items") ? "Items" : "items";
        if (!payload.TryGetValue(key, out var raw)) return ProjectionVisualField<IReadOnlyList<ProjectionItemVisual>>.Unknown("MissingDisplayedItems");
        if (raw is not NbtList list) return ProjectionVisualField<IReadOnlyList<ProjectionItemVisual>>.Invalid("InvalidDisplayedItems");
        var items = new List<ProjectionItemVisual>();
        var slots = new HashSet<int>();
        foreach (var value in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value is not NbtCompound item || item.GetString("id") is not { } id || !IsResourceId(id))
                return ProjectionVisualField<IReadOnlyList<ProjectionItemVisual>>.Invalid("InvalidItemId");
            var slot = Integer(item, "Slot") ?? Integer(item, "slot");
            var maxSlots = state.Name is "minecraft:campfire" or "minecraft:soul_campfire" ? 4 : 3;
            var count = Integer(item, modern ? "count" : "Count") ?? (modern ? 1 : -1);
            if (slot is null || slot < 0 || slot >= maxSlots || !slots.Add(slot.Value) || count < 0 || count > 99)
                return ProjectionVisualField<IReadOnlyList<ProjectionItemVisual>>.Invalid("InvalidItemSlotOrCount");
            if (id == "minecraft:air" || count == 0) continue;
            item.TryGetValue(modern ? "components" : "tag", out var components);
            items.Add(new(slot.Value, id, count, components is null ? null : Copy(components, cancellationToken)));
        }
        return ProjectionVisualField<IReadOnlyList<ProjectionItemVisual>>.Known(items.AsReadOnly());
    }

    private static ProjectionVisualField<ProjectionBannerVisual> ReadPatterns(NbtCompound payload,
        ProjectionVisualDataProfile profile, string stateName, CancellationToken cancellationToken)
    {
        var modern = profile.Banner == ProjectionBannerDataSchema.RegistryLayers;
        var key = modern ? "patterns" : "Patterns";
        if (!payload.TryGetValue(key, out var raw)) return ProjectionVisualField<ProjectionBannerVisual>.Unknown("MissingBannerPatterns");
        if (raw is not NbtList list) return ProjectionVisualField<ProjectionBannerVisual>.Invalid("InvalidBannerPatterns");
        var result = new List<ProjectionPatternVisual>();
        foreach (var value in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= 16) return ProjectionVisualField<ProjectionBannerVisual>.Invalid("TooManyBannerPatterns");
            if (value is not NbtCompound entry) return ProjectionVisualField<ProjectionBannerVisual>.Invalid("InvalidBannerPattern");
            if (modern && entry.GetCompound("pattern") is { } inline)
            {
                if (inline.GetString("asset_id") is not { } assetId || !IsResourceId(assetId))
                    return ProjectionVisualField<ProjectionBannerVisual>.Unknown("InlineBannerPatternWithoutAssetId");
                var inlineDye = entry.TryGetValue("color", out var inlineColor) ? ReadBannerColor(inlineColor) : null;
                if (inlineDye is null || !Dyes.Contains(inlineDye))
                    return ProjectionVisualField<ProjectionBannerVisual>.Invalid("InvalidBannerPatternColor");
                result.Add(new(assetId, inlineDye));
                continue;
            }
            var pattern = entry.GetString(modern ? "pattern" : "Pattern");
            var color = modern
                ? entry.TryGetValue("color", out var colorValue) ? ReadBannerColor(colorValue) : null
                : Integer(entry, "Color") is >= 0 and < 16 and var dye ? Dyes[profile.Banner == ProjectionBannerDataSchema.ShortCodeDamageColor ? 15 - dye : dye] : null;
            if (string.IsNullOrEmpty(pattern) || color is null || !Dyes.Contains(color))
                return ProjectionVisualField<ProjectionBannerVisual>.Invalid("InvalidBannerPattern");
            result.Add(new(pattern, color));
        }
        var baseColor = stateName.Split(':', 2)[1].Split('_', 2)[0];
        return Dyes.Contains(baseColor)
            ? ProjectionVisualField<ProjectionBannerVisual>.Known(new(baseColor, result.AsReadOnly()))
            : ProjectionVisualField<ProjectionBannerVisual>.Invalid("InvalidBannerBaseColor");
    }

    private static string? ReadBannerColor(NbtTag value) => value switch
    {
        NbtString text when Dyes.Contains(text.Value) => text.Value,
        NbtInt number when number.Value is >= 0 and < 16 => Dyes[number.Value],
        NbtByte number when number.Value is >= 0 and < 16 => Dyes[number.Value],
        _ => null
    };

    private static int? Integer(NbtCompound owner, string key) => owner.TryGetValue(key, out var value) ? value switch
    { NbtInt number => number.Value, NbtByte number => number.Value, NbtShort number => number.Value, _ => null } : null;
    private static bool ReadBool(NbtCompound owner, string key) => !owner.ContainsKey(key) ? false : Integer(owner, key) switch
    { 0 => false, 1 => true, _ => throw new InvalidDataException("InvalidBoolean:" + key) };
    internal static bool IsResourceId(string value)
    {
        var colon = value.IndexOf(':');
        return colon > 0 && colon < value.Length - 1 && value.IndexOf(':', colon + 1) < 0 &&
            value[..colon].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '.' or '-') &&
            value[(colon + 1)..].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '/' or '.' or '-');
    }

    internal static ProjectionVisualValue Copy(NbtTag value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return value switch
        {
            NbtCompound compound => new("Object", null, compound.ToFrozenDictionary(pair => pair.Key, pair => Copy(pair.Value, cancellationToken), StringComparer.Ordinal)),
            NbtList list => new("List", null, Elements: Array.AsReadOnly(list.Select(child => Copy(child, cancellationToken)).ToArray())),
            NbtString text => new("String", text.Value),
            NbtByte number => new("Byte", number.Value.ToString(CultureInfo.InvariantCulture)),
            NbtShort number => new("Short", number.Value.ToString(CultureInfo.InvariantCulture)),
            NbtInt number => new("Int", number.Value.ToString(CultureInfo.InvariantCulture)),
            NbtLong number => new("Long", number.Value.ToString(CultureInfo.InvariantCulture)),
            NbtFloat number => new("Float", number.Value.ToString("R", CultureInfo.InvariantCulture)),
            NbtDouble number => new("Double", number.Value.ToString("R", CultureInfo.InvariantCulture)),
            NbtByteArray array => new("ByteArray", Convert.ToBase64String(array.Values)),
            NbtIntArray array => new("IntArray", string.Join(',', array.Values)),
            NbtLongArray array => new("LongArray", string.Join(',', array.Values)),
            _ => throw new InvalidDataException("UnknownVisualValue")
        };
    }
}
