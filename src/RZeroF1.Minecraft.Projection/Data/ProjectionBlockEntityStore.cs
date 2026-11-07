using System.Collections.Frozen;
using MinecraftRenderer.Nbt;

namespace RZeroF1.Minecraft.Projection;

internal readonly record struct ProjectionSourceBlockKey(ProjectionPosition Position, string? Region);

internal sealed record ProjectionBlockEntityData(
    ProjectionSourceBlockKey Source, ProjectionPosition StoredPosition, ProjectionFormat Format,
    int? FormatVersion, int? DataVersion, int RecordIndex, string? RawTypeId, NbtCompound Payload,
    long EstimatedCpuBytes);

// Owned by the document. No resource/Native handles or independently mutable cache.
// Dropping a session's document releases its ownership; a caller retaining the public
// document deliberately retains its data too, as with Blocks and Palette.
internal sealed class ProjectionBlockEntityStore
{
    internal static ProjectionBlockEntityStore Empty { get; } = new([]);
    private readonly FrozenDictionary<ProjectionSourceBlockKey, ProjectionBlockEntityData> _entries;

    internal ProjectionBlockEntityStore(IEnumerable<ProjectionBlockEntityData> entries)
    {
        _entries = entries.ToFrozenDictionary(entry => entry.Source);
        EstimatedCpuBytes = _entries.Count * 96L + _entries.Values.Sum(entry => entry.EstimatedCpuBytes);
    }

    internal int Count => _entries.Count;
    internal long EstimatedCpuBytes { get; }
    internal bool TryGet(ProjectionBlock block, out ProjectionBlockEntityData data) =>
        _entries.TryGetValue(new(block.Position, block.Region), out data!);

    internal static ProjectionBlockEntityData Create(ProjectionBlock block, NbtCompound payload,
        ProjectionPosition storedPosition, ProjectionFormat format, int? formatVersion, int? dataVersion,
        int recordIndex, CancellationToken cancellationToken, string? rawTypeId = null, bool usePayloadTypeId = true) =>
        new(new(block.Position, block.Region), storedPosition, format, formatVersion, dataVersion, recordIndex,
            rawTypeId ?? (usePayloadTypeId ? payload.GetString("id") ?? payload.GetString("Id") : null), payload,
            128 + (block.Region?.Length ?? 0) * 2L + (rawTypeId?.Length ?? 0) * 2L + NbtPayloadBudget.Measure(payload, cancellationToken));
}
