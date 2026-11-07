namespace MinecraftRenderer.Schematics;

using System.IO.Compression;
using MinecraftRenderer.Nbt;

internal static class LitematicReader
{
    public static async Task<LitematicSchematic> ReadAsync(
        Stream source,
        LitematicReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new LitematicReadOptions();

        await using var compressed = await CopyWithLimitAsync(source, options.MaxCompressedBytes, cancellationToken);
        if (compressed.Length < 2)
        {
            throw new LitematicFormatException("The litematic file is empty.");
        }

        var header = compressed.GetBuffer();
        if (header[0] != 0x1f || header[1] != 0x8b)
        {
            throw new LitematicFormatException("Litematic files must contain gzip-compressed NBT data.");
        }

        compressed.Position = 0;
        await using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: false);
        await using var decompressed = await CopyWithLimitAsync(gzip, options.MaxDecompressedBytes, cancellationToken);

        NbtDocument document;
        try
        {
            document = NbtParser.ParseUncompressed(decompressed.GetBuffer().AsMemory(0, checked((int)decompressed.Length)), options.NbtLimits ?? new NbtReadLimits(), cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or OverflowException or FormatException)
        {
            throw new LitematicFormatException("The litematic NBT payload could not be parsed.");
        }

        return Read(document, options, cancellationToken);
    }

    public static LitematicSchematic Read(NbtDocument document, LitematicReadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new LitematicReadOptions();
        var root = document.RootCompound ?? throw new LitematicFormatException("The litematic root must be an NBT compound.");
        var metadata = root.GetCompound("Metadata") ?? throw new LitematicFormatException("The litematic metadata is missing.");
        var regionsTag = root.GetCompound("Regions") ?? throw new LitematicFormatException("The litematic regions are missing.");
        if (regionsTag.Count is < 1 or > 64 || regionsTag.Count > options.MaxRegions)
        {
            throw new LitematicFormatException($"The litematic must contain between 1 and {options.MaxRegions} regions.");
        }

        var warnings = new List<string>();
        void Warn(string message) { if (warnings.Count < 500) warnings.Add(message); }
        var payloadBudget = new NbtPayloadBudget(options.MaxBlockEntities, options.MaxBlockEntityBytes, options.MaxBlockEntityRecordBytes);
        var blocksByPosition = new Dictionary<SchematicPosition, SchematicBlock>();
        var regions = new List<SchematicRegion>(regionsTag.Count);
        long totalVolume = 0;
        foreach (var (regionName, regionTag) in regionsTag)
        {
            if (regionTag is not NbtCompound region)
            {
                throw new LitematicFormatException($"Region '{regionName}' is not an NBT compound.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var position = ReadVector(region, "Position", regionName);
            var signedSize = ReadVector(region, "Size", regionName);
            var sizeX = checked((int)Math.Abs((long)signedSize.X));
            var sizeY = checked((int)Math.Abs((long)signedSize.Y));
            var sizeZ = checked((int)Math.Abs((long)signedSize.Z));
            if (sizeX == 0 || sizeY == 0 || sizeZ == 0)
            {
                throw new LitematicFormatException($"Region '{regionName}' has an empty dimension.");
            }

            var volume = checked((long)sizeX * sizeY * sizeZ);
            totalVolume = checked(totalVolume + volume);
            if (totalVolume > options.MaxVolume)
            {
                throw new LitematicFormatException($"The combined litematic region volume exceeds {options.MaxVolume:N0} blocks.");
            }

            var palette = ReadPalette(region, regionName, cancellationToken);
            var packedStates = region.GetLongArray("BlockStates")
                ?? throw new LitematicFormatException($"Region '{regionName}' is missing its packed BlockStates array.");
            var bitsPerEntry = Math.Max(2, CeilingLog2(palette.Count));
            var requiredBits = checked(volume * bitsPerEntry);
            if (packedStates.LongLength * 64L < requiredBits)
            {
                throw new LitematicFormatException($"Region '{regionName}' has a truncated BlockStates array.");
            }

            var blockEntities = ReadBlockEntities(region, position, signedSize, payloadBudget, Warn, cancellationToken);
            var minimumX = MinimumCoordinate(position.X, signedSize.X);
            var minimumY = MinimumCoordinate(position.Y, signedSize.Y);
            var minimumZ = MinimumCoordinate(position.Z, signedSize.Z);
            for (long index = 0; index < volume; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var paletteIndex = ReadPackedValue(packedStates, index, bitsPerEntry);
                if ((uint)paletteIndex >= (uint)palette.Count)
                {
                    throw new LitematicFormatException($"Region '{regionName}' references palette entry {paletteIndex}, but only {palette.Count} entries exist.");
                }

                var state = palette[paletteIndex];
                var x = (int)(index % sizeX);
                var z = (int)(index / sizeX % sizeZ);
                var y = (int)(index / (sizeX * (long)sizeZ));
                var world = new SchematicPosition(
                    checked(minimumX + x),
                    checked(minimumY + y),
                    checked(minimumZ + z));
                blockEntities.Remove(world, out var blockEntity);
                if (state.IsAir)
                {
                    if (blockEntity.Payload is not null) Warn($"OrphanBlockEntity: {regionName} @ {world}");
                    blocksByPosition.Remove(world);
                }
                else
                    blocksByPosition[world] = new SchematicBlock(world, state, blockEntity.Payload, regionName, blockEntity.Index);
            }

            foreach (var orphan in blockEntities.Keys) Warn($"OrphanBlockEntity: {regionName} @ {orphan}");
            regions.Add(new SchematicRegion(regionName, position, signedSize, palette, volume));
        }

        var blocks = blocksByPosition.Values
            .OrderBy(static block => block.Position.Y)
            .ThenBy(static block => block.Position.Z)
            .ThenBy(static block => block.Position.X)
            .ToArray();
        var regionCorners = regions.SelectMany(static region =>
        {
            var end = new SchematicPosition(
                checked(region.Position.X + (checked((int)Math.Abs((long)region.Size.X)) - 1) * Math.Sign(region.Size.X)),
                checked(region.Position.Y + (checked((int)Math.Abs((long)region.Size.Y)) - 1) * Math.Sign(region.Size.Y)),
                checked(region.Position.Z + (checked((int)Math.Abs((long)region.Size.Z)) - 1) * Math.Sign(region.Size.Z)));
            return new[] { region.Position, end };
        }).ToArray();
        var min = new SchematicPosition(
            regionCorners.Min(static position => position.X),
            regionCorners.Min(static position => position.Y),
            regionCorners.Min(static position => position.Z));
        var max = new SchematicPosition(
            regionCorners.Max(static position => position.X),
            regionCorners.Max(static position => position.Y),
            regionCorners.Max(static position => position.Z));

        return new LitematicSchematic
        {
            Warnings = warnings.AsReadOnly(),
            Name = metadata.GetString("Name") ?? "Untitled schematic",
            Author = metadata.GetString("Author"),
            MinecraftDataVersion = root.GetInt("MinecraftDataVersion"),
            FormatVersion = root.GetInt("Version"),
            Regions = regions,
            Blocks = blocks,
            Min = min,
            Max = max
        };
    }

    internal static int ReadPackedValue(long[] values, long index, int bitsPerEntry)
    {
        var bitIndex = checked(index * bitsPerEntry);
        var startLong = checked((int)(bitIndex >> 6));
        var startOffset = (int)(bitIndex & 63);
        var mask = (1UL << bitsPerEntry) - 1UL;
        var value = (ulong)values[startLong] >> startOffset;
        var endOffset = startOffset + bitsPerEntry;
        if (endOffset > 64)
        {
            value |= (ulong)values[startLong + 1] << (64 - startOffset);
        }

        return checked((int)(value & mask));
    }

    private static IReadOnlyList<SchematicBlockState> ReadPalette(NbtCompound region, string regionName, CancellationToken cancellationToken)
    {
        var paletteTag = region.GetList("BlockStatePalette")
            ?? throw new LitematicFormatException($"Region '{regionName}' is missing its BlockStatePalette.");
        if (paletteTag.Count == 0)
        {
            throw new LitematicFormatException($"Region '{regionName}' has an empty BlockStatePalette.");
        }

        var palette = new List<SchematicBlockState>(paletteTag.Count);
        foreach (var tag in paletteTag)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tag is not NbtCompound entry || string.IsNullOrWhiteSpace(entry.GetString("Name")))
            {
                throw new LitematicFormatException($"Region '{regionName}' has an invalid palette entry.");
            }

            var properties = entry.GetCompound("Properties")?.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value is NbtString value ? value.Value : string.Empty,
                StringComparer.OrdinalIgnoreCase);
            palette.Add(new SchematicBlockState(entry.GetString("Name")!, properties));
        }

        return palette;
    }

    private static Dictionary<SchematicPosition, (NbtCompound Payload, int Index)> ReadBlockEntities(
        NbtCompound region, SchematicPosition origin, SchematicPosition signedSize,
        NbtPayloadBudget budget, Action<string> warn, CancellationToken cancellationToken)
    {
        var result = new Dictionary<SchematicPosition, (NbtCompound Payload, int Index)>();
        var key = region.ContainsKey("TileEntities") ? "TileEntities" : "BlockEntities";
        if (!region.TryGetValue(key, out var raw)) return result;
        if (raw is not NbtList entities) { warn("InvalidBlockEntityList"); return result; }
        for (var recordIndex = 0; recordIndex < entities.Count; recordIndex++)
        {
            var tag = entities[recordIndex];
            budget.Include(tag, cancellationToken);
            if (tag is not NbtCompound entity) { warn("InvalidBlockEntityRecord"); continue; }
            var pos = entity.GetIntArray("Pos");
            int x, y, z;
            if (entity.ContainsKey("Pos"))
            {
                if (pos is not { Length: 3 }) { warn("InvalidBlockEntityPosition"); continue; }
                (x, y, z) = (pos[0], pos[1], pos[2]);
            }
            else if (entity.GetInt("x") is { } ex && entity.GetInt("y") is { } ey && entity.GetInt("z") is { } ez)
                (x, y, z) = (ex, ey, ez);
            else { warn("MissingBlockEntityPosition"); continue; }
            // Litematic storage coordinates start at the minimum corner, also for
            // negative selections. Never guess that an invalid local is a world coordinate.
            if (x < 0 || x >= Math.Abs((long)signedSize.X) || y < 0 || y >= Math.Abs((long)signedSize.Y) ||
                z < 0 || z >= Math.Abs((long)signedSize.Z))
            { warn("OutOfBoundsBlockEntity"); continue; }
            var world = new SchematicPosition(checked(MinimumCoordinate(origin.X, signedSize.X) + x),
                checked(MinimumCoordinate(origin.Y, signedSize.Y) + y), checked(MinimumCoordinate(origin.Z, signedSize.Z) + z));
            if (result.ContainsKey(world)) warn($"DuplicateBlockEntityLastWins: {world}");
            result[world] = (entity, recordIndex);
        }
        return result;
    }

    private static SchematicPosition ReadVector(NbtCompound owner, string key, string regionName)
    {
        var value = owner.GetCompound(key) ?? throw new LitematicFormatException($"Region '{regionName}' is missing {key}.");
        return new SchematicPosition(
            value.GetInt("x") ?? throw new LitematicFormatException($"Region '{regionName}' has an invalid {key}.x value."),
            value.GetInt("y") ?? throw new LitematicFormatException($"Region '{regionName}' has an invalid {key}.y value."),
            value.GetInt("z") ?? throw new LitematicFormatException($"Region '{regionName}' has an invalid {key}.z value."));
    }

    private static int CeilingLog2(int value)
    {
        var bits = 0;
        var remaining = value - 1;
        while (remaining > 0)
        {
            remaining >>= 1;
            bits++;
        }
        return bits;
    }

    private static async Task<MemoryStream> CopyWithLimitAsync(Stream source, long maxBytes, CancellationToken cancellationToken)
    {
        var destination = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (destination.Length + read > maxBytes)
                {
                    throw new LitematicFormatException($"The litematic data exceeds the {maxBytes:N0}-byte safety limit.");
                }
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            destination.Position = 0;
            return destination;
        }
        catch { await destination.DisposeAsync(); throw; }
    }

    private static int MinimumCoordinate(int origin, int signedSize) =>
        checked(origin + (signedSize < 0 ? signedSize + 1 : 0));
}
