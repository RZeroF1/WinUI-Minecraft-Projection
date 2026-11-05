using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using MinecraftRenderer.Nbt;
using RZeroF1.Minecraft.Projection;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

var root = Path.Combine(Path.GetTempPath(), "rzero-projection-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var model = new ProjectionBlockModelDefinition
    {
        Elements =
        [
            new ProjectionElementDefinition
            {
                From = [0, 0, 0],
                To = [16, 16, 16],
                Faces = new Dictionary<string, ProjectionFaceDefinition>
                {
                    ["north"] = new() { Texture = "#all" },
                    ["south"] = new() { Texture = "#all" },
                    ["east"] = new() { Texture = "#all" },
                    ["west"] = new() { Texture = "#all" },
                    ["up"] = new() { Texture = "#all" },
                    ["down"] = new() { Texture = "#all" }
                }
            }
        ],
        Textures = new Dictionary<string, string> { ["all"] = "minecraft:block/stone" }
    };
    var modelJson = JsonSerializer.SerializeToUtf8Bytes(model, ProjectionModelJsonContext.Default.ProjectionBlockModelDefinition);
    var whiteTexture = CreatePng(0xffffffff);
    var translucentTexture = CreatePng(0x80ff00ff);
    var jarPath = Path.Combine(root, "client.jar");
    using (var archive = ZipFile.Open(jarPath, ZipArchiveMode.Create))
    {
        Write(archive, "version.json", "{\"id\":\"test-client\",\"world_version\":3700}");
        Write(archive, "assets/minecraft/blockstates/stone.json", "{\"variants\":{\"\":{\"model\":\"minecraft:block/stone\"}}}");
        Write(archive, "assets/minecraft/models/block/stone.json", Encoding.UTF8.GetString(modelJson));
        WriteBytes(archive, "assets/minecraft/textures/block/stone.png", whiteTexture);
        Write(archive, "assets/minecraft/lang/en_us.json", "{\"block.minecraft.stone\":\"Stone\"}");
    }

    using var session = new ProjectionSession();
    var clients = await session.DiscoverClientsAsync([new ProjectionClientSource(jarPath)]);
    Assert(
        clients is [{ DataVersion: 3700, Label: "test-client" }],
        "client JAR metadata");

    var projectionPath = Path.Combine(root, "two-stone.schem");
    WriteSpongeSchematic(projectionPath);
    await session.LoadAsync(
        projectionPath,
        [new ProjectionClientSource(jarPath)],
        cancellationToken: CancellationToken.None);
    Assert(session.Document is { Format: ProjectionFormat.SpongeSchematic, DataVersion: 3700, NonAirCount: 2 }, "Sponge projection read and client match");
    Assert(session.Scene is { TriangleCount: 20, MaterialCount: 1 }, "model baking and opaque internal face culling");
    Assert(session.GetDisplayName("minecraft:stone") == "Stone", "localized block display name");
    Assert(session.GetMaterialGroups().Single().Count == 2, "full material grouping");
    Assert(session.GetMaterialGroups(0, 0, 0).Single().Count == 1, "slice material grouping");
    Assert(session.VisibleBlockCount(0, 0, 0) == 1, "slice block count");
    await session.ApplySliceAsync(0, 1, 1);
    Assert(session.CachedSliceBytes > 0, "slice-boundary cache population");
    var sliceBytes = session.CachedSliceBytes;
    await session.ApplySliceAsync(0, 1, 1);
    Assert(session.CachedSliceBytes == sliceBytes, "slice-boundary cache reuse");

    var camera = session.Camera;
    var start = camera.Eye;
    camera.Zoom(1);
    Assert(camera.Distance < 9, "orbit camera zoom");
    camera.SetMode(ProjectionCameraMode.Free);
    var freePosition = camera.FreePosition;
    camera.MoveFree(Vector3.UnitZ, .5f);
    Assert(camera.FreePosition != freePosition, "free camera movement");
    camera.Rotate(24, -12);
    var packPath = Path.Combine(root, "override.zip");
    using (var archive = ZipFile.Open(packPath, ZipArchiveMode.Create))
    {
        WriteBytes(archive, "assets/minecraft/textures/block/stone.png", translucentTexture);
        Write(archive, "assets/minecraft/lang/en_us.json", "{\"block.minecraft.stone\":\"Override Stone\"}");
    }
    await session.ApplyResourcePackAsync(packPath);
    Assert(session.GetDisplayName("minecraft:stone") == "Override Stone", "resource-pack localization override");
    Assert(
        session.Scene!.TexturePng.Values.Single()!.SequenceEqual(translucentTexture),
        "resource-pack texture override");
    Assert(session.CaptureDiagnostics().CachedSliceBytes == 0, "resource-pack slice invalidation");
    camera.Reset(session.Document!);
    Assert(camera.Mode == ProjectionCameraMode.Free && camera.Distance > 0 && start != Vector3.Zero, "camera reset preserves mode and bounds");

    Assert(session.Document is not null, "session remains owned until disposal");
    session.Clear();
    Assert(session.Document is null && session.CachedSliceBytes == 0, "session clear releases scene and slice cache");
    var clearedDiagnostics = session.CaptureDiagnostics();
    Assert(
        clearedDiagnostics.DocumentBytes == 0 &&
        clearedDiagnostics.MeshBytes == 0 &&
        clearedDiagnostics.ResourceBytes == 0,
        "session clear releases owned resource diagnostics");

    var litematicPath = Path.Combine(root, "one-stone.litematic");
    WriteLitematic(litematicPath);
    using var litematicSession = new ProjectionSession();
    await litematicSession.LoadAsync(
        litematicPath,
        [new ProjectionClientSource(jarPath)],
        cancellationToken: CancellationToken.None);
    Assert(
        litematicSession.Document is
        {
            Format: ProjectionFormat.Litematic,
            DataVersion: 3700,
            Name: "one-stone-litematic",
            NonAirCount: 1,
            Regions.Count: 1
        },
        "Litematic projection read and client match");

    var cameraModel = new ProjectionDocument
    {
        Name = "camera",
        SourcePath = projectionPath,
        Format = ProjectionFormat.SpongeSchematic,
        Min = new(0, 0, 0),
        Max = new(3, 7, 11),
        Blocks = [new(new(0, 0, 0), 0, null)],
        Palette = [new ProjectionState("minecraft:stone")],
        Regions = [],
        Warnings = []
    };
    var resetCamera = new ProjectionCamera();
    resetCamera.Initialize(cameraModel);
    var orbitCenter = resetCamera.OrbitTarget;
    resetCamera.SetMode(ProjectionCameraMode.Free);
    resetCamera.MoveFree(Vector3.UnitX, .25f);
    resetCamera.Reset(cameraModel);
    Assert(resetCamera.Mode == ProjectionCameraMode.Free && resetCamera.OrbitTarget == orbitCenter, "camera bounds and reset mode");

    var rawPng = ProjectionPng.EncodeBgra(
        [0, 0, 255, 255, 0, 255, 0, 255],
        2,
        1);
    Assert(rawPng.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG signature");
    Assert(
        DecodeSingleRowPng(rawPng) is [0, 255, 0, 0, 255, 0, 255, 0, 255],
        "PNG dimensions, zlib scanline, and BGRA-to-RGBA channel order");
    Console.WriteLine("PASS litematic/schem read, resource-pack texture/localization, materials, slices, camera, PNG encoding and disposal");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Write(ZipArchive archive, string path, string text) =>
    WriteBytes(archive, path, Encoding.UTF8.GetBytes(text));

static void WriteBytes(ZipArchive archive, string path, byte[] bytes)
{
    using var stream = archive.CreateEntry(path).Open();
    stream.Write(bytes);
}

static byte[] CreatePng(uint argb) =>
    ProjectionPng.EncodeBgra(
        [(byte)argb, (byte)(argb >> 8), (byte)(argb >> 16), (byte)(argb >> 24)],
        1,
        1);

static byte[] DecodeSingleRowPng(byte[] png)
{
    Assert(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG signature");
    var cursor = 8;
    var width = 0;
    var height = 0;
    using var compressed = new MemoryStream();
    var sawEnd = false;
    while (cursor < png.Length)
    {
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(cursor, 4)));
        cursor += 4;
        var type = png.AsSpan(cursor, 4);
        cursor += 4;
        var data = png.AsSpan(cursor, length);
        cursor += length + 4;
        if (type.SequenceEqual("IHDR"u8))
        {
            width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data));
            height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[4..]));
            Assert(data[8] == 8 && data[9] == 6, "PNG uses 8-bit RGBA pixels");
        }
        else if (type.SequenceEqual("IDAT"u8))
        {
            compressed.Write(data);
        }
        else if (type.SequenceEqual("IEND"u8))
        {
            sawEnd = true;
            Assert(cursor == png.Length, "PNG IEND is the final chunk");
            break;
        }
    }

    Assert(width == 2 && height == 1 && sawEnd, "PNG header and end chunk");
    compressed.Position = 0;
    using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
    using var scanlines = new MemoryStream();
    zlib.CopyTo(scanlines);
    return scanlines.ToArray();
}

static void WriteSpongeSchematic(string path)
{
    using var file = File.Create(path);
    using var gzip = new GZipStream(file, CompressionLevel.Optimal);
    using var writer = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: true);
    writer.Write((byte)NbtTagType.Compound);
    WriteString(writer, "");
    writer.Write((byte)NbtTagType.Compound);
    WriteString(writer, "Schematic");
    writer.Write((byte)NbtTagType.Short); WriteString(writer, "Width"); WriteInt16(writer, 2);
    writer.Write((byte)NbtTagType.Short); WriteString(writer, "Height"); WriteInt16(writer, 1);
    writer.Write((byte)NbtTagType.Short); WriteString(writer, "Length"); WriteInt16(writer, 1);
    writer.Write((byte)NbtTagType.Int); WriteString(writer, "DataVersion"); WriteInt32(writer, 3700);
    writer.Write((byte)NbtTagType.Compound); WriteString(writer, "Palette");
    writer.Write((byte)NbtTagType.Int); WriteString(writer, "minecraft:air"); WriteInt32(writer, 0);
    writer.Write((byte)NbtTagType.Int); WriteString(writer, "minecraft:stone"); WriteInt32(writer, 1);
    writer.Write((byte)NbtTagType.End);
    writer.Write((byte)NbtTagType.ByteArray); WriteString(writer, "BlockData"); WriteInt32(writer, 2); writer.Write(new byte[] { 1, 1 });
    writer.Write((byte)NbtTagType.IntArray); WriteString(writer, "Offset"); WriteInt32(writer, 3); foreach (var value in new[] { 0, 0, 0 }) WriteInt32(writer, value);
    writer.Write((byte)NbtTagType.End);
    writer.Write((byte)NbtTagType.Compound); WriteString(writer, "Metadata");
    writer.Write((byte)NbtTagType.String); WriteString(writer, "Name"); WriteString(writer, "two-stone");
    writer.Write((byte)NbtTagType.End);
    writer.Write((byte)NbtTagType.End);
}

static void WriteLitematic(string path)
{
    using var file = File.Create(path);
    using var gzip = new GZipStream(file, CompressionLevel.Optimal);
    using var writer = new BinaryWriter(gzip, Encoding.UTF8, leaveOpen: true);

    writer.Write((byte)NbtTagType.Compound);
    WriteString(writer, "");
    WriteCompound(writer, "Metadata", metadata =>
    {
        metadata.Write((byte)NbtTagType.String); WriteString(metadata, "Name"); WriteString(metadata, "one-stone-litematic");
        metadata.Write((byte)NbtTagType.String); WriteString(metadata, "Author"); WriteString(metadata, "tests");
        metadata.Write((byte)NbtTagType.End);
    });
    writer.Write((byte)NbtTagType.Int); WriteString(writer, "MinecraftDataVersion"); WriteInt32(writer, 3700);
    writer.Write((byte)NbtTagType.Int); WriteString(writer, "Version"); WriteInt32(writer, 6);
    WriteCompound(writer, "Regions", regions =>
    {
        WriteCompound(regions, "Region", region =>
        {
            WriteCompound(region, "Position", position =>
            {
                position.Write((byte)NbtTagType.Int); WriteString(position, "x"); WriteInt32(position, 0);
                position.Write((byte)NbtTagType.Int); WriteString(position, "y"); WriteInt32(position, 0);
                position.Write((byte)NbtTagType.Int); WriteString(position, "z"); WriteInt32(position, 0);
                position.Write((byte)NbtTagType.End);
            });
            WriteCompound(region, "Size", size =>
            {
                size.Write((byte)NbtTagType.Int); WriteString(size, "x"); WriteInt32(size, 2);
                size.Write((byte)NbtTagType.Int); WriteString(size, "y"); WriteInt32(size, 1);
                size.Write((byte)NbtTagType.Int); WriteString(size, "z"); WriteInt32(size, 1);
                size.Write((byte)NbtTagType.End);
            });
            region.Write((byte)NbtTagType.List); WriteString(region, "BlockStatePalette");
            region.Write((byte)NbtTagType.Compound); WriteInt32(region, 2);
            WritePaletteEntry(region, "minecraft:air");
            WritePaletteEntry(region, "minecraft:stone");
            region.Write((byte)NbtTagType.LongArray); WriteString(region, "BlockStates");
            WriteInt32(region, 1); WriteInt64(region, 4);
            region.Write((byte)NbtTagType.End);
        });
        regions.Write((byte)NbtTagType.End);
    });
    writer.Write((byte)NbtTagType.End);
}

static void WriteCompound(BinaryWriter writer, string name, Action<BinaryWriter> contents)
{
    writer.Write((byte)NbtTagType.Compound);
    WriteString(writer, name);
    contents(writer);
}

static void WritePaletteEntry(BinaryWriter writer, string name)
{
    writer.Write((byte)NbtTagType.String);
    WriteString(writer, "Name");
    WriteString(writer, name);
    writer.Write((byte)NbtTagType.End);
}

static void WriteString(BinaryWriter writer, string value)
{
    var bytes = Encoding.UTF8.GetBytes(value);
    WriteInt16(writer, checked((short)bytes.Length));
    writer.Write(bytes);
}

static void WriteInt16(BinaryWriter writer, short value) =>
    writer.Write(BinaryPrimitives.ReverseEndianness(value));

static void WriteInt32(BinaryWriter writer, int value) =>
    writer.Write(BinaryPrimitives.ReverseEndianness(value));

static void WriteInt64(BinaryWriter writer, long value) =>
    writer.Write(BinaryPrimitives.ReverseEndianness(value));
