using System.Collections.ObjectModel;
using System.Numerics;

namespace RZeroF1.Minecraft.Projection;

public enum ProjectionFormat
{
    Litematic,
    SpongeSchematic
}

public readonly record struct ProjectionPosition(int X, int Y, int Z)
{
    public int GetAxis(int axis) => axis switch
    {
        0 => X,
        1 => Y,
        _ => Z
    };
}

public sealed class ProjectionState
{
    public ProjectionState(string name, IReadOnlyDictionary<string, string>? properties = null)
    {
        Name = NormalizeName(name);
        Properties = properties is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase);
        Key = Properties.Count == 0
            ? Name
            : $"{Name}[{string.Join(',', Properties.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static p => $"{p.Key}={p.Value}"))}]";
    }

    public string Name { get; }

    public IReadOnlyDictionary<string, string> Properties { get; }

    public string Key { get; }

    public bool IsAir => Name is "minecraft:air" or "minecraft:cave_air" or "minecraft:void_air";

    public static ProjectionState Parse(string state)
    {
        var open = state.IndexOf('[', StringComparison.Ordinal);
        if (open < 0 || !state.EndsWith(']'))
            return new ProjectionState(state);

        var name = state[..open];
        var properties = state[(open + 1)..^1]
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Split('=', 2))
            .Where(static pair => pair.Length == 2)
            .ToDictionary(static pair => pair[0], static pair => pair[1], StringComparer.OrdinalIgnoreCase);
        return new ProjectionState(name, properties);
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Contains(':', StringComparison.Ordinal)
            ? trimmed.ToLowerInvariant()
            : "minecraft:" + trimmed.ToLowerInvariant();
    }
}

public readonly record struct ProjectionBlock(ProjectionPosition Position, int StateIndex, string? Region);

public sealed record ProjectionRegionInfo(
    string Name,
    ProjectionPosition Position,
    ProjectionPosition Size,
    long Volume,
    int PaletteSize);

public sealed class ProjectionDocument
{
    internal Guid Identity { get; } = Guid.NewGuid();
    internal ProjectionBlockEntityStore BlockEntities { get; init; } = ProjectionBlockEntityStore.Empty;
    public required string Name { get; init; }
    public required string SourcePath { get; init; }
    public required ProjectionFormat Format { get; init; }
    public int? DataVersion { get; init; }
    public string? Author { get; init; }
    public required ProjectionPosition Min { get; init; }
    public required ProjectionPosition Max { get; init; }
    public required IReadOnlyList<ProjectionBlock> Blocks { get; init; }
    public required IReadOnlyList<ProjectionState> Palette { get; init; }
    public required IReadOnlyList<ProjectionRegionInfo> Regions { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    public int Width => Max.X - Min.X + 1;
    public int Height => Max.Y - Min.Y + 1;
    public int Length => Max.Z - Min.Z + 1;
    public long Volume => checked((long)Width * Height * Length);
    public int NonAirCount => Blocks.Count;

    public long EstimatedCpuBytes
    {
        get
        {
            var blockBytes = Blocks.Count * 32L;
            var paletteBytes = Palette.Sum(state =>
                64L +
                state.Name.Length * 2L +
                state.Key.Length * 2L +
                state.Properties.Sum(pair => (pair.Key.Length + pair.Value.Length) * 2L + 32L));
            var regionBytes = Regions.Count * 128L;
            return blockBytes + paletteBytes + regionBytes + BlockEntities.EstimatedCpuBytes;
        }
    }
}

internal sealed record ProjectionLoadOptions(
    long MaxCompressedBytes = 256 * 1024 * 1024,
    long MaxDecompressedBytes = 512 * 1024 * 1024,
    long MaxVolume = 50_000_000,
    int MaxRegions = 64,
    int MaxBlockEntities = 250_000,
    long MaxBlockEntityBytes = 64L * 1024 * 1024,
    long MaxBlockEntityRecordBytes = 1024 * 1024,
    int MaxNbtDepth = 64,
    int MaxNbtNodes = 4_000_000,
    long MaxNbtAllocatedBytes = 512L * 1024 * 1024);

public sealed record ProjectionLoadProgress(
    string Stage,
    long Completed,
    long Total,
    string Message);

internal sealed record ProjectionMaterialCount(string StateKey, int Count);

public sealed record ProjectionMaterialVariant(
    string StateKey,
    string BlockName,
    IReadOnlyDictionary<string, string> Properties,
    int Count);

public sealed record ProjectionMaterialGroup(
    string BlockName,
    int Count,
    IReadOnlyList<ProjectionMaterialVariant> Variants);

internal static class ProjectionStatistics
{
    public static IReadOnlyList<ProjectionMaterialGroup> GroupMaterials(
        ProjectionDocument document,
        int axis = -1,
        int min = int.MinValue,
        int max = int.MaxValue)
    {
        var counts = new Dictionary<int, int>();
        foreach (var block in document.Blocks)
        {
            if (axis >= 0)
            {
                var coordinate = block.Position.GetAxis(axis);
                if (coordinate < min || coordinate > max)
                    continue;
            }

            counts.TryGetValue(block.StateIndex, out var count);
            counts[block.StateIndex] = count + 1;
        }

        return counts
            .GroupBy(pair => document.Palette[pair.Key].Name, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var variants = group
                    .Select(pair =>
                    {
                        var state = document.Palette[pair.Key];
                        return new ProjectionMaterialVariant(
                            state.Key,
                            state.Name,
                            state.Properties,
                            pair.Value);
                    })
                    .OrderByDescending(static item => item.Count)
                    .ThenBy(static item => item.StateKey, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new ProjectionMaterialGroup(
                    group.Key,
                    variants.Sum(static item => item.Count),
                    variants);
            })
            .OrderByDescending(static item => item.Count)
            .ThenBy(static item => item.BlockName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<ProjectionMaterialCount> CountMaterials(
        ProjectionDocument document,
        int axis = -1,
        int min = int.MinValue,
        int max = int.MaxValue)
    {
        var counts = new Dictionary<int, int>();
        foreach (var block in document.Blocks)
        {
            if (axis >= 0)
            {
                var coordinate = block.Position.GetAxis(axis);
                if (coordinate < min || coordinate > max)
                    continue;
            }

            counts.TryGetValue(block.StateIndex, out var count);
            counts[block.StateIndex] = count + 1;
        }

        return counts
            .Select(pair => new ProjectionMaterialCount(document.Palette[pair.Key].Key, pair.Value))
            .OrderByDescending(static item => item.Count)
            .ThenBy(static item => item.StateKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal sealed class ProjectionMaterialRow
{
    public ProjectionMaterialRow(string stateKey, int count)
    {
        StateKey = stateKey;
        Count = count;
    }

    public string StateKey { get; }
    public int Count { get; }
}
