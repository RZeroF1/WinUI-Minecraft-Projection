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
internal sealed record ProjectionVisualState(
    ProjectionVisualDataProfile Profile,
    ProjectionVisualField<ProjectionSignVisual> Sign,
    ProjectionVisualField<IReadOnlyList<ProjectionItemVisual>> Items,
    ProjectionVisualField<IReadOnlyList<ProjectionPatternVisual>> Patterns,
    ProjectionVisualField<ProjectionHeadAppearance> Appearance,
    ProjectionVisualField<ProjectionEntityVisual> DisplayEntity,
    ProjectionVisualField<IReadOnlyList<string>> PotSides,
    ProjectionVisualField<string> BedColor);

internal static class ProjectionVisualStateAdapter
{
    private static readonly string[] Dyes = ["white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray",
        "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black"];

    internal static ProjectionVisualState Adapt(ProjectionState state, ProjectionBlockEntityData? data,
        int? sourceDataVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = ProjectionVisualDataProfile.Select(sourceDataVersion);
        var payload = data?.Payload;
        var reason = payload is null ? "MissingBlockEntityData" : "UnknownSourceDataProfile";
        ProjectionVisualField<T> Missing<T>() => ProjectionVisualField<T>.Unknown(reason);
        var sign = Missing<ProjectionSignVisual>();
        var items = Missing<IReadOnlyList<ProjectionItemVisual>>();
        var patterns = Missing<IReadOnlyList<ProjectionPatternVisual>>();
        var appearance = Missing<ProjectionHeadAppearance>();
        var entity = Missing<ProjectionEntityVisual>();
        var sides = Missing<IReadOnlyList<string>>();
        var bed = Missing<string>();
        if (payload is not null && profile.Schema != ProjectionVisualDataSchema.Unknown)
        {
            // Each field adapts independently: an invalid unrelated field cannot turn
            // known contents into empty, or poison ordinary block geometry.
            sign = ReadSign(payload, profile.Sign, cancellationToken);
            items = ReadItems(payload, profile.Items, state, cancellationToken);
            patterns = ReadPatterns(payload, profile, cancellationToken);
            var modern = profile.Head is ProjectionHeadDataSchema.ResolvableProfile or ProjectionHeadDataSchema.ProfileWithSkinPatch;
            appearance = ProjectionAppearanceAdapter.ReadHead(payload, modern, cancellationToken);
            if (modern && payload.TryGetValue("profile", out var rawProfile) &&
                (rawProfile is NbtString || profile.Head == ProjectionHeadDataSchema.ProfileWithSkinPatch &&
                    rawProfile is NbtCompound head && new[] { "texture", "cape", "elytra", "model" }.Any(head.ContainsKey)))
                appearance = ProjectionVisualField<ProjectionHeadAppearance>.Unknown("HeadProfileEncodingNotImplemented");
            entity = ProjectionAppearanceAdapter.ReadEntity(payload, cancellationToken);
            var potKey = profile.Pot == ProjectionPotDataSchema.Shards ? "shards" : "sherds";
            if (profile.Pot == ProjectionPotDataSchema.NamedItemTemplates)
                sides = ProjectionVisualField<IReadOnlyList<string>>.Unknown("NamedPotItemTemplatesNotImplemented");
            else if ((profile.Pot is ProjectionPotDataSchema.Shards or ProjectionPotDataSchema.Sherds) && payload.GetList(potKey) is { } sherds)
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
        return new(profile, sign, items, patterns, appearance, entity, sides, bed);
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
                var color = owner.GetString(legacy ? "Color" : "color") ?? "black";
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

    private static ProjectionVisualField<IReadOnlyList<ProjectionPatternVisual>> ReadPatterns(NbtCompound payload,
        ProjectionVisualDataProfile profile, CancellationToken cancellationToken)
    {
        var modern = profile.Banner == ProjectionBannerDataSchema.RegistryLayers;
        var key = modern ? "patterns" : "Patterns";
        if (!payload.TryGetValue(key, out var raw)) return ProjectionVisualField<IReadOnlyList<ProjectionPatternVisual>>.Unknown("MissingBannerPatterns");
        if (raw is not NbtList list) return ProjectionVisualField<IReadOnlyList<ProjectionPatternVisual>>.Invalid("InvalidBannerPatterns");
        var result = new List<ProjectionPatternVisual>();
        foreach (var value in list)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value is not NbtCompound entry) return ProjectionVisualField<IReadOnlyList<ProjectionPatternVisual>>.Invalid("InvalidBannerPattern");
            if (modern && entry.GetCompound("pattern") is not null)
                return ProjectionVisualField<IReadOnlyList<ProjectionPatternVisual>>.Unknown("InlineBannerPatternNotImplemented");
            var pattern = entry.GetString(modern ? "pattern" : "Pattern");
            var color = modern ? entry.GetString("color") : Integer(entry, "Color") is >= 0 and < 16 and var dye ? Dyes[profile.Banner == ProjectionBannerDataSchema.ShortCodeDamageColor ? 15 - dye : dye] : null;
            if (string.IsNullOrEmpty(pattern) || color is null || !Dyes.Contains(color))
                return ProjectionVisualField<IReadOnlyList<ProjectionPatternVisual>>.Invalid("InvalidBannerPattern");
            result.Add(new(pattern, color));
        }
        return ProjectionVisualField<IReadOnlyList<ProjectionPatternVisual>>.Known(result.AsReadOnly());
    }

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
