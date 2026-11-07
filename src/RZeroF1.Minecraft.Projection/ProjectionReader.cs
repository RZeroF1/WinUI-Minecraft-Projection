using System.IO.Compression;
using MinecraftRenderer.Nbt;
using MinecraftRenderer.Schematics;

namespace RZeroF1.Minecraft.Projection;

internal static class ProjectionReader
{
    public static async Task<ProjectionDocument> ReadAsync(
        string path,
        ProjectionLoadOptions? options = null,
        IProgress<ProjectionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new ProjectionLoadOptions();
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".litematic", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".schem", StringComparison.OrdinalIgnoreCase))
            throw new ProjectionFormatException("目前支持 .litematic 和 .schem 文件。");

        progress?.Report(new ProjectionLoadProgress("read", 0, 1, $"读取 {Path.GetFileName(path)}"));
        cancellationToken.ThrowIfCancellationRequested();
        ProjectionDocument result;
        try
        {
            result = extension.Equals(".litematic", StringComparison.OrdinalIgnoreCase)
                ? await ReadLitematicAsync(path, options, progress, cancellationToken)
                : await ReadSpongeAsync(path, options, progress, cancellationToken);
        }
        catch (Exception exception) when (exception is LitematicFormatException or InvalidDataException or OverflowException or EndOfStreamException)
        { throw new ProjectionFormatException(exception.Message); }
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Blocks.Count == 0) throw new ProjectionFormatException("投影只包含空气方块。");
        if ((long)result.Max.X - result.Min.X + 1 > int.MaxValue || (long)result.Max.Y - result.Min.Y + 1 > int.MaxValue ||
            (long)result.Max.Z - result.Min.Z + 1 > int.MaxValue)
            throw new ProjectionFormatException("投影边界超出坐标范围。");
        progress?.Report(new ProjectionLoadProgress("complete", result.Blocks.Count, result.Blocks.Count, "投影解析完成"));
        return result;
    }

    private static async Task<ProjectionDocument> ReadLitematicAsync(
        string path,
        ProjectionLoadOptions options,
        IProgress<ProjectionLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var schematic = await LitematicReader.ReadAsync(
            stream,
            new LitematicReadOptions(
                options.MaxCompressedBytes,
                options.MaxDecompressedBytes,
                options.MaxRegions,
                options.MaxVolume, options.MaxBlockEntities, options.MaxBlockEntityBytes,
                options.MaxBlockEntityRecordBytes, NbtLimits(options)),
            cancellationToken);

        var palette = new List<ProjectionState>();
        var paletteMap = new Dictionary<string, int>(StringComparer.Ordinal);
        var blocks = new List<ProjectionBlock>(schematic.Blocks.Count);
        var blockEntities = new List<ProjectionBlockEntityData>();
        for (var index = 0; index < schematic.Blocks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = schematic.Blocks[index];
            var state = new ProjectionState(source.State.Name, source.State.Properties);
            if (!paletteMap.TryGetValue(state.Key, out var stateIndex))
            {
                stateIndex = palette.Count;
                paletteMap.Add(state.Key, stateIndex);
                palette.Add(state);
            }

            blocks.Add(new ProjectionBlock(
                new ProjectionPosition(source.Position.X, source.Position.Y, source.Position.Z),
                stateIndex,
                source.Region));
            if (source.BlockEntity is { } payload)
            {
                var stored = payload.GetIntArray("Pos") is { Length: 3 } pos
                    ? new ProjectionPosition(pos[0], pos[1], pos[2])
                    : new ProjectionPosition(payload.GetInt("x")!.Value, payload.GetInt("y")!.Value, payload.GetInt("z")!.Value);
                blockEntities.Add(ProjectionBlockEntityStore.Create(blocks[^1], payload, stored,
                    ProjectionFormat.Litematic, schematic.FormatVersion, schematic.MinecraftDataVersion, source.BlockEntityRecordIndex, cancellationToken));
            }
            if ((index & 0x3fff) == 0)
                progress?.Report(new ProjectionLoadProgress("blocks", index, schematic.Blocks.Count, "整理方块状态"));
        }

        var regions = schematic.Regions
            .Select(region => new ProjectionRegionInfo(
                region.Name,
                new ProjectionPosition(region.Position.X, region.Position.Y, region.Position.Z),
                new ProjectionPosition(region.Size.X, region.Size.Y, region.Size.Z),
                region.Volume,
                region.Palette.Count))
            .ToArray();

        return new ProjectionDocument
        {
            Name = schematic.Name,
            SourcePath = path,
            Format = ProjectionFormat.Litematic,
            DataVersion = schematic.MinecraftDataVersion,
            Author = schematic.Author,
            Min = new ProjectionPosition(schematic.Min.X, schematic.Min.Y, schematic.Min.Z),
            Max = new ProjectionPosition(schematic.Max.X, schematic.Max.Y, schematic.Max.Z),
            Blocks = blocks,
            Palette = palette,
            Regions = regions,
            Warnings = schematic.Warnings,
            BlockEntities = new ProjectionBlockEntityStore(blockEntities)
        };
    }

    private static async Task<ProjectionDocument> ReadSpongeAsync(
        string path,
        ProjectionLoadOptions options,
        IProgress<ProjectionLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        await using var compressed = await CopyWithLimitAsync(file, options.MaxCompressedBytes, cancellationToken);
        await using var decompressed = await DecompressAsync(compressed, options.MaxDecompressedBytes, cancellationToken);

        NbtDocument document;
        try
        {
            document = NbtParser.ParseUncompressed(decompressed.GetBuffer().AsMemory(0, checked((int)decompressed.Length)), NbtLimits(options), cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or OverflowException or FormatException)
        {
            throw new ProjectionFormatException($"Sponge .schem 的 NBT 无法解析：{exception.Message}");
        }

        return ReadSpongeDocument(document, path, options, progress, cancellationToken);
    }

    private static ProjectionDocument ReadSpongeDocument(
        NbtDocument document,
        string path,
        ProjectionLoadOptions options,
        IProgress<ProjectionLoadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = document.RootCompound
            ?? throw new ProjectionFormatException("Sponge .schem 根标签不是复合标签。");
        var schematic = root.GetCompound("Schematic") ?? root;

        var warnings = new List<string>();
        var formatVersion = schematic.GetInt("Version");
        if (schematic.ContainsKey("Version") && formatVersion is not (2 or 3))
            throw new ProjectionFormatException("Sponge .schem 仅支持 Version 2/3。");
        if (formatVersion is null) warnings.Add("MissingSpongeVersion: 按方块容器识别 v2/v3，Block Entity 仍需完整字段。");
        var isV3 = formatVersion == 3 || formatVersion is null && schematic.ContainsKey("Blocks");
        var blockOwner = isV3 ? schematic.GetCompound("Blocks")
            ?? throw new ProjectionFormatException("Sponge v3 缺少 Blocks。") : schematic;
        var width = ReadInt(schematic, "Width");
        var height = ReadInt(schematic, "Height");
        var length = ReadInt(schematic, "Length");
        if (width <= 0 || height <= 0 || length <= 0)
            throw new ProjectionFormatException("Sponge .schem 的尺寸无效。");
        var volume = checked((long)width * height * length);
        if (volume > options.MaxVolume)
            throw new ProjectionFormatException($"Sponge .schem 体积 {volume:N0} 超过当前上限 {options.MaxVolume:N0}。");

        var offset = ReadVector(schematic.GetIntArray("Offset"));
        if (schematic.ContainsKey("Offset") && schematic.GetIntArray("Offset") is not { Length: 3 })
            throw new ProjectionFormatException("Sponge Offset 必须是三个整数。");
        _ = checked(offset.X + width - 1); _ = checked(offset.Y + height - 1); _ = checked(offset.Z + length - 1);
        var blockData = blockOwner.GetByteArray(isV3 ? "Data" : "BlockData")
            ?? throw new ProjectionFormatException("Sponge .schem 缺少 BlockData。");
        var paletteTag = blockOwner.GetCompound("Palette")
            ?? throw new ProjectionFormatException("Sponge .schem 缺少 Palette。");

        // Dense indices are required. Do not allocate from an untrusted maximum index.
        var paletteByIndex = new ProjectionState?[paletteTag.Count];
        foreach (var (stateName, tag) in paletteTag)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tag is not NbtInt index || index.Value < 0 || index.Value >= paletteByIndex.Length || paletteByIndex[index.Value] is not null)
                throw new ProjectionFormatException($"Sponge .schem 的 palette 索引无效：{stateName}。");
            paletteByIndex[index.Value] = ProjectionState.Parse(stateName);
        }

        if (paletteByIndex.Any(static state => state is null))
            throw new ProjectionFormatException("Sponge .schem 的 Palette 存在未定义的索引。");

        var palette = paletteByIndex.Select(static state => state!).ToArray();
        var blocks = new List<ProjectionBlock>((int)Math.Min(volume, 1_000_000));
        var cursor = 0;
        for (long linear = 0; linear < volume; linear++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var paletteIndex = ReadVarInt(blockData, ref cursor);
            if ((uint)paletteIndex >= (uint)palette.Length)
                throw new ProjectionFormatException($"Sponge .schem 方块数据引用了不存在的 palette 索引 {paletteIndex}。");
            var state = palette[paletteIndex];
            if (!state.IsAir)
            {
                var x = (int)(linear % width);
                var z = (int)(linear / width % length);
                var y = (int)(linear / (width * (long)length));
                blocks.Add(new ProjectionBlock(
                    new ProjectionPosition(checked(offset.X + x), checked(offset.Y + y), checked(offset.Z + z)),
                    paletteIndex,
                    null));
            }

            if ((linear & 0x3fff) == 0)
                progress?.Report(new ProjectionLoadProgress("blocks", linear, volume, "解码 Sponge 方块数据"));
        }

        if (blocks.Count == 0)
            throw new ProjectionFormatException("投影只包含空气方块。");

        var min = new ProjectionPosition(
            blocks.Min(static block => block.Position.X),
            blocks.Min(static block => block.Position.Y),
            blocks.Min(static block => block.Position.Z));
        var max = new ProjectionPosition(
            blocks.Max(static block => block.Position.X),
            blocks.Max(static block => block.Position.Y),
            blocks.Max(static block => block.Position.Z));

        var dataVersion = schematic.GetInt("DataVersion") ?? root.GetInt("DataVersion");
        var entities = ReadSpongeBlockEntities(blockOwner, isV3, blocks, offset, width, height, length,
            formatVersion, dataVersion, options, warnings, cancellationToken);
        if (cursor < blockData.Length)
            warnings.Add("BlockData 在声明体积之后仍有额外字节，已忽略。");

        return new ProjectionDocument
        {
            Name = schematic.GetCompound("Metadata")?.GetString("Name")
                ?? Path.GetFileNameWithoutExtension(path),
            SourcePath = path,
            Format = ProjectionFormat.SpongeSchematic,
            DataVersion = dataVersion,
            BlockEntities = entities,
            Min = min,
            Max = max,
            Blocks = blocks,
            Palette = palette,
            Regions =
            [
                new ProjectionRegionInfo(
                    "Schematic",
                    offset,
                    new ProjectionPosition(width, height, length),
                    volume,
                    palette.Length)
            ],
            Warnings = warnings
        };
    }

    private static NbtReadLimits NbtLimits(ProjectionLoadOptions options) =>
        new(options.MaxNbtDepth, options.MaxNbtNodes, options.MaxNbtAllocatedBytes);

    private static ProjectionBlockEntityStore ReadSpongeBlockEntities(NbtCompound owner, bool isV3,
        List<ProjectionBlock> blocks, ProjectionPosition offset, int width, int height, int length,
        int? formatVersion, int? dataVersion, ProjectionLoadOptions options, List<string> warnings,
        CancellationToken cancellationToken)
    {
        void Warn(string message) { if (warnings.Count < 500) warnings.Add(message); }
        if (!owner.TryGetValue("BlockEntities", out var raw)) return ProjectionBlockEntityStore.Empty;
        if (raw is not NbtList list) { Warn("InvalidBlockEntityList"); return ProjectionBlockEntityStore.Empty; }
        var budget = new NbtPayloadBudget(options.MaxBlockEntities, options.MaxBlockEntityBytes, options.MaxBlockEntityRecordBytes);
        var sources = blocks.ToDictionary(block => block.Position);
        var entities = new Dictionary<ProjectionPosition, ProjectionBlockEntityData>();
        for (var index = 0; index < list.Count; index++)
        {
            var tag = list[index];
            budget.Include(tag, cancellationToken);
            if (tag is not NbtCompound entry) { Warn("InvalidBlockEntityRecord"); continue; }
            if (entry.GetIntArray("Pos") is not { Length: 3 } pos) { Warn("InvalidBlockEntityPosition"); continue; }
            if (pos[0] < 0 || pos[0] >= width || pos[1] < 0 || pos[1] >= height || pos[2] < 0 || pos[2] >= length)
            { Warn("OutOfBoundsBlockEntity"); continue; }
            var world = new ProjectionPosition(checked(offset.X + pos[0]), checked(offset.Y + pos[1]), checked(offset.Z + pos[2]));
            if (!sources.TryGetValue(world, out var block)) { Warn($"OrphanBlockEntity: {world}"); continue; }
            if (entities.ContainsKey(world)) Warn($"DuplicateBlockEntityLastWins: {world}");
            // A malformed final record must not resurrect an earlier payload.
            entities.Remove(world);
            var payload = isV3 ? entry.GetCompound("Data") : entry;
            if (payload is null) { Warn($"MissingBlockEntityData: {world}"); continue; }
            var id = entry.GetString("Id");
            if (string.IsNullOrWhiteSpace(id)) Warn($"MissingBlockEntityId: {world}");
            entities[world] = ProjectionBlockEntityStore.Create(block, payload, new(pos[0], pos[1], pos[2]),
                ProjectionFormat.SpongeSchematic, formatVersion, dataVersion, index, cancellationToken, id, usePayloadTypeId: false);
        }
        return new ProjectionBlockEntityStore(entities.Values);
    }

    private static int ReadInt(NbtCompound owner, string name) =>
        owner.GetInt(name)
        ?? (owner.TryGetValue(name, out var tag) && tag is NbtShort shortValue ? shortValue.Value : 0);

    private static ProjectionPosition ReadVector(int[]? values) =>
        values is { Length: >= 3 }
            ? new ProjectionPosition(values[0], values[1], values[2])
            : new ProjectionPosition(0, 0, 0);

    private static int ReadVarInt(byte[] bytes, ref int cursor)
    {
        var value = 0;
        var shift = 0;
        while (true)
        {
            if ((uint)cursor >= (uint)bytes.Length || shift > 28)
                throw new ProjectionFormatException("Sponge .schem 的 BlockData 包含截断或过长的 VarInt。");
            var current = bytes[cursor++];
            if (shift == 28 && (current & 0xf8) != 0)
                throw new ProjectionFormatException("Sponge BlockData VarInt 超出有效索引范围。");
            value |= (current & 0x7f) << shift;
            if ((current & 0x80) == 0)
                return value;
            shift += 7;
        }
    }

    private static async Task<MemoryStream> CopyWithLimitAsync(
        Stream source,
        long maximum,
        CancellationToken cancellationToken)
    {
        var destination = new MemoryStream();
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                    break;
                if (destination.Length + read > maximum)
                    throw new ProjectionFormatException($"压缩文件超过 {maximum:N0} 字节上限。");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            destination.Position = 0;
            return destination;
        }
        catch { await destination.DisposeAsync(); throw; }
    }

    private static async Task<MemoryStream> DecompressAsync(
        MemoryStream compressed,
        long maximum,
        CancellationToken cancellationToken)
    {
        if (compressed.Length < 2)
            throw new ProjectionFormatException("Sponge .schem 文件为空。");

        compressed.Position = 0;
        Stream input = compressed;
        if (compressed.GetBuffer()[0] == 0x1f && compressed.GetBuffer()[1] == 0x8b)
            input = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        else if (compressed.GetBuffer()[0] == 0x78)
            input = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: true);

        await using (input)
        {
            return await CopyWithLimitAsync(input, maximum, cancellationToken);
        }
    }
}

public sealed class ProjectionFormatException(string message) : IOException(message);
