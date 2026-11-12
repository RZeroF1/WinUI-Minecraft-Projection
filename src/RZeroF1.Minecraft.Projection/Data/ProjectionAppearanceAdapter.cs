using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using MinecraftRenderer.Nbt;

namespace RZeroF1.Minecraft.Projection;

internal sealed record ProjectionProfileProperty(string Name, string Value, string? Signature);
internal sealed record ProjectionTexturePatch(string? Texture, string? Cape, string? Elytra, string? Model);
internal sealed record ProjectionHeadAppearance(string? Name, string? Uuid, IReadOnlyList<ProjectionProfileProperty> Properties,
    ProjectionTexturePatch? TexturePatch = null, string? TextureUrl = null);
internal sealed record ProjectionEntityVisual(string Id, ProjectionVisualValue Data);

internal static class ProjectionAppearanceAdapter
{
    internal static ProjectionVisualField<ProjectionHeadAppearance> ReadHead(NbtCompound payload, bool modern, CancellationToken cancellationToken)
    {
        var key = modern ? "profile" : "SkullOwner";
        if (!payload.TryGetValue(key, out var raw))
        {
            if (!modern && payload.TryGetValue("Owner", out var legacyOwner)) raw = legacyOwner;
            else if (!modern) return ProjectionVisualField<ProjectionHeadAppearance>.Unknown("Missing" + key);
            else return ProjectionVisualField<ProjectionHeadAppearance>.Unknown("Missing" + key);
        }
        if (raw is NbtString { Value.Length: > 0 } nameOnly)
            return ProjectionVisualField<ProjectionHeadAppearance>.Known(new(nameOnly.Value, null, []));
        if (raw is NbtString) return ProjectionVisualField<ProjectionHeadAppearance>.Invalid("EmptyHeadProfileName");
        if (raw is not NbtCompound profile) return ProjectionVisualField<ProjectionHeadAppearance>.Invalid("InvalidHeadProfile");
        try
        {
            var name = profile.GetString(modern ? "name" : "Name");
            string? uuid = null;
            if (profile.TryGetValue(modern ? "id" : "Id", out var identity))
            {
                if (identity is NbtString text && Guid.TryParse(text.Value, out var parsed)) uuid = parsed.ToString("D");
                else if (identity is NbtIntArray { Values.Length: 4 } array)
                {
                    Span<byte> bytes = stackalloc byte[16];
                    for (var index = 0; index < 4; index++) BinaryPrimitives.WriteInt32BigEndian(bytes[(index * 4)..], array.Values[index]);
                    uuid = new Guid(bytes, bigEndian: true).ToString("D");
                }
                else throw new InvalidDataException("InvalidHeadUuid");
            }
            var result = new List<ProjectionProfileProperty>();
            void Add(string propertyName, NbtTag tag, string valueKey, string signatureKey)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (tag is not NbtCompound property || property.GetString(valueKey) is not { } value)
                    throw new InvalidDataException("InvalidHeadProperty");
                if (property.ContainsKey(signatureKey) && property.GetString(signatureKey) is null)
                    throw new InvalidDataException("InvalidHeadSignature");
                result.Add(new(propertyName, value, property.GetString(signatureKey)));
            }
            var propertyKey = modern ? "properties" : "Properties";
            if (profile.TryGetValue(propertyKey, out var properties))
            {
                if (modern && properties is NbtList list)
                    foreach (var entry in list)
                    {
                        if (entry is not NbtCompound property || property.GetString("name") is not { Length: > 0 } propertyName)
                            throw new InvalidDataException("InvalidHeadPropertyName");
                        Add(propertyName, entry, "value", "signature");
                    }
                else if (!modern && properties is NbtCompound compound)
                    foreach (var (propertyName, values) in compound)
                    {
                        if (values is not NbtList entries) throw new InvalidDataException("InvalidHeadProperties");
                        foreach (var entry in entries) Add(propertyName, entry, "Value", "Signature");
                    }
                else throw new InvalidDataException("InvalidHeadProperties");
            }
            var patch = modern ? ReadPatch(profile) : null;
            var textureUrl = result.FirstOrDefault(property => property.Name == "textures") is { } textures
                ? ReadTextureUrl(textures.Value)
                : null;
            return name is null && uuid is null && result.Count == 0
                ? patch is null && textureUrl is null
                    ? ProjectionVisualField<ProjectionHeadAppearance>.Unknown("EmptyHeadIdentity")
                    : ProjectionVisualField<ProjectionHeadAppearance>.Known(new(name, uuid, result.AsReadOnly(), patch, textureUrl))
                : ProjectionVisualField<ProjectionHeadAppearance>.Known(new(name, uuid, result.AsReadOnly(), patch, textureUrl));
        }
        catch (InvalidDataException exception) { return ProjectionVisualField<ProjectionHeadAppearance>.Invalid(exception.Message); }
    }

    private static ProjectionTexturePatch? ReadPatch(NbtCompound profile)
    {
        string? Text(string key)
        {
            if (!profile.TryGetValue(key, out var raw)) return null;
            return raw is NbtString value ? value.Value : raw is NbtCompound compound
                ? compound.GetString("id") ?? compound.GetString("path") ?? compound.GetString("asset")
                : null;
        }
        var patch = new ProjectionTexturePatch(Text("texture"), Text("cape"), Text("elytra"), Text("model"));
        return patch.Texture is null && patch.Cape is null && patch.Elytra is null && patch.Model is null ? null : patch;
    }

    private static string? ReadTextureUrl(string encoded)
    {
        try
        {
            var normalized = encoded.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(normalized));
            return document.RootElement.TryGetProperty("textures", out var textures) &&
                textures.TryGetProperty("SKIN", out var skin) && skin.TryGetProperty("url", out var url)
                ? url.GetString()
                : null;
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
    }

    internal static ProjectionVisualField<ProjectionEntityVisual> ReadEntity(NbtCompound payload, CancellationToken cancellationToken)
    {
        if (!payload.TryGetValue("SpawnData", out var raw)) return ProjectionVisualField<ProjectionEntityVisual>.Unknown("MissingSpawnData");
        if (raw is not NbtCompound spawn) return ProjectionVisualField<ProjectionEntityVisual>.Invalid("InvalidSpawnData");
        // The caller already selected a known source profile. Recognize both
        // retained encodings explicitly; never guess an entity when neither exists.
        if (spawn.ContainsKey("entity") && (spawn.GetCompound("entity") is null || spawn.ContainsKey("id")))
            return ProjectionVisualField<ProjectionEntityVisual>.Invalid("AmbiguousOrInvalidSpawnData");
        var entity = spawn.ContainsKey("entity") ? spawn.GetCompound("entity") : spawn;
        if (entity?.GetString("id") is not { } id) return ProjectionVisualField<ProjectionEntityVisual>.Unknown("MissingDisplayEntityId");
        return ProjectionVisualStateAdapter.IsResourceId(id)
            ? ProjectionVisualField<ProjectionEntityVisual>.Known(new(id, ProjectionVisualStateAdapter.Copy(entity, cancellationToken)))
            : ProjectionVisualField<ProjectionEntityVisual>.Invalid("InvalidDisplayEntityId");
    }
}
