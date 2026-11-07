using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MinecraftRenderer.Nbt;
using RZeroF1.Minecraft.Projection;

internal static class P1DataRegression
{
    private static int _checks;
    internal static async Task RunAsync(string root)
    {
        await CheckSponge(root);
        await CheckLitematic(root);
        CheckAdapters();
        CheckBinaryLimits();
        await CheckLifecycle(root);
        Console.WriteLine($"PASS P1 data: {_checks} checks; Litematic/Sponge v2/v3/offset/negative regions/overlap/payload/invalid inputs/budgets/cancellation/source profiles/visual DTOs");
    }

    private static async Task CheckSponge(string root)
    {
        foreach (var version in new[] { 2, 3 })
        {
            var data = "{Text1:'{\"text\":\"hello\"}',Text2:'\"\"',Text3:'\"\"',Text4:'\"\"',Unknown:{nested:[I;1,2,3]}}";
            var entries = $"[{{Pos:[I;0,0,0],Id:'minecraft:sign',{(version == 3 ? "Data:" + data : data[1..^1])}}}]";
            var path = Path.Combine(root, $"p1-v{version}.schem");
            Write(path, Sponge(version, entries));
            var document = await ProjectionReader.ReadAsync(path);
            var block = document.Blocks.Single();
            Check(block.Position == new ProjectionPosition(-10, 20, -30), "offset once " + version);
            Check(document.BlockEntities.Count == 1 && document.BlockEntities.TryGet(block, out _), "BE extraction " + version);
            document.BlockEntities.TryGet(block, out var entity);
            Check(entity.StoredPosition == new ProjectionPosition(0, 0, 0) && entity.Source.Position == block.Position &&
                entity.DataVersion == 3120 && entity.FormatVersion == version && entity.RawTypeId == "minecraft:sign", "source identity " + version);
            Check(entity.Payload.GetCompound("Unknown")!.GetIntArray("nested")!.SequenceEqual(new[] { 1, 2, 3 }), "unknown fields retained");
            Check(document.EstimatedCpuBytes > entity.EstimatedCpuBytes, "retained bytes counted");
            Check(ProjectionVisualStateAdapter.Adapt(document.Palette[block.StateIndex], entity, document.DataVersion, default)
                .Sign is { Status: ProjectionVisualDataStatus.Known, Value.IsEmpty: false }, "reader -> adapter");
            await ExpectFormat(() => ProjectionReader.ReadAsync(path, new(MaxBlockEntities: 0)), "record budget");
            await ExpectFormat(() => ProjectionReader.ReadAsync(path, new(MaxBlockEntityBytes: 20)), "total bytes budget");
            await ExpectFormat(() => ProjectionReader.ReadAsync(path, new(MaxBlockEntityRecordBytes: 20)), "record bytes budget");
            await ExpectFormat(() => ProjectionReader.ReadAsync(path, new(MaxNbtAllocatedBytes: 20)), "parser allocation budget");
            await ExpectFormat(() => ProjectionReader.ReadAsync(path, new(MaxNbtNodes: 2)), "parser node budget");
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            await ExpectCanceled(() => ProjectionReader.ReadAsync(path, cancellationToken: cancel.Token), "read cancel");

            var bad = version == 3 ? "Data:{}" : "test:1";
            var repeated = $"[{{Pos:[I;0,0,0],Id:'minecraft:sign',{bad}}},{{Pos:[I;0,0,0],Id:'minecraft:banner',{bad}}}," +
                $"{{Pos:[I;1,0,0],Id:'minecraft:sign',{bad}}},{{Pos:[I;2,0,0],{bad}}},{{Id:'minecraft:sign',{bad}}}]";
            Write(path, Sponge(version, repeated, width: 2, blockBytes: [1, 0]));
            document = await ProjectionReader.ReadAsync(path);
            Check(document.BlockEntities.Count == 1 && document.BlockEntities.TryGet(document.Blocks[0], out var winner) &&
                winner.RawTypeId == "minecraft:banner" && winner.RecordIndex == 1, "last duplicate wins, raw ID retained");
            foreach (var code in new[] { "DuplicateBlockEntityLastWins", "OrphanBlockEntity", "OutOfBoundsBlockEntity", "InvalidBlockEntityPosition" })
                Check(document.Warnings.Any(warning => warning.StartsWith(code, StringComparison.Ordinal)), code);
            if (version == 3)
            {
                Write(path, Sponge(version, "[{Pos:[I;0,0,0],Data:{id:'minecraft:sign'}}]"));
                document = await ProjectionReader.ReadAsync(path);
                document.BlockEntities.TryGet(document.Blocks[0], out var missingEnvelopeId);
                Check(missingEnvelopeId.RawTypeId is null && !ProjectionBlockEntityBindings.Bind(document.Palette[document.Blocks[0].StateIndex], missingEnvelopeId, 3120).PayloadTrusted,
                    "v3 Data.id cannot replace required envelope Id");
                Write(path, Sponge(version, "[{Pos:[I;0,0,0],Id:'minecraft:sign',Data:{}},{Pos:[I;0,0,0],Id:'minecraft:sign'}]"));
                document = await ProjectionReader.ReadAsync(path);
                Check(document.BlockEntities.Count == 0 && document.Warnings.Any(warning => warning.StartsWith("MissingBlockEntityData")), "invalid final payload never resurrects earlier data");
            }
            Write(path, Sponge(version, "[]", offset: "[I;2147483647,0,0]", width: 2, blockBytes: [1, 1]));
            await ExpectFormat(() => ProjectionReader.ReadAsync(path), "offset overflow");
            Write(path, Sponge(version, "[]", palette: "{'minecraft:oak_sign':2147483647}"));
            await ExpectFormat(() => ProjectionReader.ReadAsync(path), "sparse palette refuses huge allocation");
            Write(path, Sponge(version, "[]", blockBytes: [255, 255, 255, 255, 127]));
            await ExpectFormat(() => ProjectionReader.ReadAsync(path), "overflow varint");
            Write(path, Sponge(version, "[]", blockBytes: []));
            await ExpectFormat(() => ProjectionReader.ReadAsync(path), "truncated data");
            Write(path, Sponge(version, "[]", offset: "[I;0,0]"));
            await ExpectFormat(() => ProjectionReader.ReadAsync(path), "invalid offset");
            Write(path, Sponge(version, "[{Pos:[I;0,0,0],Data:{}}]"));
            document = await ProjectionReader.ReadAsync(path);
            Check(document.Warnings.Any(warning => warning.StartsWith("MissingBlockEntityId")), "missing ID explicit");
        }
    }

    private static async Task CheckLitematic(string root)
    {
        var path = Path.Combine(root, "p1-regions.litematic");
        var negative = Region("{x:5,y:-2,z:8}", "{x:-2,y:-1,z:-1}", 5,
            "[{id:'minecraft:sign',x:0,y:0,z:0,Text1:'\"A\"',unknown:'kept'},{id:'minecraft:sign',x:1,y:0,z:0}]");
        Write(path, Litematic("{first:" + negative + "}"));
        var document = await ProjectionReader.ReadAsync(path);
        Check(document.Blocks.Count == 2 && document.Min == new ProjectionPosition(4, -2, 8), "negative region minimum");
        Check(document.BlockEntities.Count == 2 && document.BlockEntities.TryGet(document.Blocks[0], out var first) &&
            first.StoredPosition.X == 0 && first.Source.Region == "first" && first.Payload.GetString("unknown") == "kept", "negative region local BE retained");
        var overwrite = Region("{x:4,y:-2,z:8}", "{x:1,y:1,z:1}", 1, "[]");
        Write(path, Litematic("{first:" + negative + ",second:" + overwrite + "}"));
        document = await ProjectionReader.ReadAsync(path);
        var block = document.Blocks.Single(value => value.Position.X == 4);
        Check(block.Region == "second" && !document.BlockEntities.TryGet(block, out _) && document.BlockEntities.Count == 1,
            "winner without BE cannot inherit loser");
        overwrite = Region("{x:4,y:-2,z:8}", "{x:1,y:1,z:1}", 0, "[]");
        Write(path, Litematic("{first:" + negative + ",second:" + overwrite + "}"));
        document = await ProjectionReader.ReadAsync(path);
        Check(document.Blocks.Count == 1 && document.BlockEntities.Count == 1 && document.Blocks[0].Position.X == 5, "last air removes losing source and payload");
        var entities = "[{id:'minecraft:sign',x:0,y:0,z:0,value:1},{id:'minecraft:sign',x:0,y:0,z:0,value:2}," +
            "{id:'minecraft:sign',x:1,y:0,z:0},{id:'minecraft:sign',x:9,y:0,z:0},{id:'minecraft:sign'}]";
        Write(path, Litematic("{one:" + Region("{x:0,y:0,z:0}", "{x:2,y:1,z:1}", 1, entities) + "}"));
        document = await ProjectionReader.ReadAsync(path);
        Check(document.BlockEntities.Count == 1 && document.BlockEntities.TryGet(document.Blocks[0], out var winner) &&
            winner.Payload.GetInt("value") == 2 && winner.RecordIndex == 1, "Litematic duplicate winner identity");
        foreach (var code in new[] { "DuplicateBlockEntityLastWins", "OrphanBlockEntity", "OutOfBoundsBlockEntity", "MissingBlockEntityPosition" })
            Check(document.Warnings.Any(warning => warning.StartsWith(code, StringComparison.Ordinal)), "Litematic " + code);
        await ExpectFormat(() => ProjectionReader.ReadAsync(path, new(MaxBlockEntities: 2)), "discarded records count toward budget");
        Write(path, Litematic("{one:" + Region("{x:2147483647,y:0,z:0}", "{x:2,y:1,z:1}", 5, "[]") + "}"));
        await ExpectFormat(() => ProjectionReader.ReadAsync(path), "region overflow");
        Write(path, Litematic("{one:" + Region("{x:0,y:0,z:0}", "{x:-2147483648,y:1,z:1}", 0, "[]") + "}"));
        await ExpectFormat(() => ProjectionReader.ReadAsync(path), "int min signed dimension");
    }

    private static void CheckAdapters()
    {
        var block = new ProjectionBlock(new(1, 2, 3), 0, "source");
        ProjectionVisualState Adapt(string payload, int? version, string state = "oak_sign") => ProjectionVisualStateAdapter.Adapt(
            ProjectionState.Parse(state), ProjectionBlockEntityStore.Create(block, C(payload), new(0, 0, 0),
                ProjectionFormat.Litematic, 6, version, 0, default), version, default);
        var legacy = "{Text1:'\"\"',Text2:'{\"text\":\"\"}',Text3:'\"\"',Text4:'\"\"',Color:'red',GlowingText:1b}";
        Check(Adapt(legacy, 3120).Sign is { Status: ProjectionVisualDataStatus.Known, Value.IsEmpty: true, Value.Front.Glowing: true }, "legacy sign empty/color/glow");
        Check(Adapt("{Text1:'\"\"'}", 3120).Sign.Status != ProjectionVisualDataStatus.Known, "missing text is unknown, never empty");
        Check(Adapt(legacy, 99999).Sign.Status == ProjectionVisualDataStatus.Unknown, "future version not inferred");
        Check(Adapt(legacy, null).Sign.Status == ProjectionVisualDataStatus.Unknown, "missing version explicit");
        var side = "{messages:['\"\"','\"\"','\"\"','\"\"'],color:'blue',has_glowing_text:0b}";
        foreach (var version in new[] { 3463, 3839, 3953, 4189 })
            Check(Adapt("{front_text:" + side + ",back_text:" + side + "}", version).Sign.Value!.IsEmpty, "two-sided sign " + version);
        var structured = "{messages:[{text:''},{text:''},{text:''},{text:''}],color:'black'}";
        Check(Adapt("{front_text:" + structured + ",back_text:" + structured + "}", 5023).Sign.Value!.IsEmpty, "structured NBT text");
        Check(Adapt("{Text1:'bad',Text2:'\"\"',Text3:'\"\"',Text4:'\"\"'}", 3120).Sign.Status == ProjectionVisualDataStatus.Invalid, "bad JSON is invalid");
        Check(Adapt("{Text1:'\"nonempty\"',Text2:'bad',Text3:'\"\"',Text4:'\"\"'}", 3120).Sign.Status == ProjectionVisualDataStatus.Invalid, "validate every text line after nonempty line");
        Check(Adapt("{Text1:'\"\"',Text2:'\"\"',Text3:'\"\"',Text4:'\"\"',FilteredText1:'\"filtered\"'}", 3120).Sign.Value!.Front.FilteredMessages?.Count == 4, "legacy filtered text normalized");
        Check(Adapt("{Items:[]}", 3120, "campfire").Items.Value!.Count == 0, "explicit empty legacy items");
        Check(Adapt("{}", 3120, "campfire").Items.Status == ProjectionVisualDataStatus.Unknown, "absent items unknown");
        var oldItem = Adapt("{Items:[{Slot:1b,id:'minecraft:apple',Count:2b,tag:{CustomModelData:7}}]}", 3120, "campfire").Items.Value!.Single();
        Check(oldItem.Slot == 1 && oldItem.Count == 2 && oldItem.Components!.Fields!.ContainsKey("CustomModelData"), "legacy item tag normalized");
        var newItem = Adapt("{Items:[{Slot:2b,id:'minecraft:apple',count:3,components:{'minecraft:custom_model_data':{floats:[1.0f]}}}]}", 5023, "campfire").Items.Value!.Single();
        Check(newItem.Count == 3 && newItem.Components!.Fields!.ContainsKey("minecraft:custom_model_data"), "modern components normalized");
        Check(Adapt("{Items:[{Slot:4b,id:'minecraft:apple',Count:1b}]}", 3120, "campfire").Items.Status == ProjectionVisualDataStatus.Invalid, "invalid item slot");
        Check(Adapt("{Patterns:[{Pattern:'bs',Color:14}]}", 3120, "red_banner").Patterns.Value!.Single() is { Pattern: "bs", Color: "red" }, "legacy ordered banner patterns");
        Check(Adapt("{Patterns:[{Pattern:'bs',Color:1}]}", 1343, "red_banner").Patterns.Value!.Single().Color == "red", "pre-flattening banner dye damage order");
        Check(Adapt("{patterns:[{pattern:'minecraft:stripe_bottom',color:'red'}]}", 5023, "red_banner").Patterns.Value!.Single().Pattern == "minecraft:stripe_bottom", "modern pattern IDs");
        var oldHead = Adapt("{SkullOwner:{Name:'player',Properties:{textures:[{Value:'skin',Signature:'signed'}]}}}", 3120, "player_head").Appearance.Value!;
        var newHead = Adapt("{profile:{name:'player',properties:[{name:'textures',value:'skin',signature:'signed'}]}}", 5023, "player_head").Appearance.Value!;
        Check(oldHead.Name == "player" && oldHead.Properties.Single() is { Name: "textures", Value: "skin", Signature: "signed" }, "legacy profile normalized");
        Check(newHead.Name == oldHead.Name && newHead.Properties.SequenceEqual(oldHead.Properties), "new/old profiles share one DTO shape");
        Check(Adapt("{profile:{id:[I;0,0,0,1]}}", 5023, "player_head").Appearance.Value!.Uuid == "00000000-0000-0000-0000-000000000001", "big-endian Java profile UUID normalized");
        Check(Adapt("{profile:{id:123}}", 5023, "player_head").Appearance.Status == ProjectionVisualDataStatus.Invalid, "malformed profile ID");
        Check(Adapt("{color:8}", 1343, "bed").BedColor.Value == "light_gray", "1.12 BE bed color");
        Check(Adapt("{}", 3120, "red_bed").BedColor.Value == "red", "flattened source bed color");
        Check(Adapt("{color:999}", 1343, "bed").BedColor.Status == ProjectionVisualDataStatus.Invalid, "invalid bed color");
        Check(Adapt("{sherds:['minecraft:brick','minecraft:brick','minecraft:brick','minecraft:brick']}", 5023, "decorated_pot").PotSides.Value!.Count == 4, "pot sides ordered");
        Check(Adapt("{SpawnData:{entity:{id:'minecraft:pig'}}}", 3120, "spawner").DisplayEntity.Value!.Id == "minecraft:pig", "modern entity display DTO");
        Check(Adapt("{SpawnData:{id:'minecraft:pig'}}", 1343, "spawner").DisplayEntity.Value!.Id == "minecraft:pig", "old SpawnData normalized to same DTO");
        Check(Adapt("{SpawnData:{}}", 3120, "spawner").DisplayEntity.Status == ProjectionVisualDataStatus.Unknown, "empty SpawnData is unknown, not known empty");
        foreach (var version in ProjectionRenderCapabilityRegistry.Vanilla.Versions.Values)
            Check(ProjectionVersionProfile.SelectClient(version.ClientSha256).Version?.Id == version.Id, "actual hash -> canonical client " + version.Id);
        Check(ProjectionVersionProfile.SelectClient(new string('0', 64)).Status == ProjectionVersionStatus.UnknownVersion, "unknown client identity");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { ProjectionVisualStateAdapter.Adapt(new("campfire"), null, 3120, cancel.Token); throw new Exception("adapter cancel missing"); }
        catch (OperationCanceledException) { _checks++; }
    }

    private static void CheckBinaryLimits()
    {
        foreach (var snbt in new[] { "{list:[1,2,3]}", "{nested:{nested:{nested:1}}}" })
        {
            var bytes = Bytes(C(snbt));
            try { NbtParser.ParseUncompressed(bytes, new(MaxDepth: 2, MaxNodes: 2), default); throw new Exception("limit missing"); }
            catch (InvalidDataException) { _checks++; }
        }
        // A ten-byte input advertises Int32.MaxValue list entries. Must reject before allocation.
        byte[] bomb = [10, 0, 0, 9, 0, 1, 120, 10, 127, 255, 255, 255, 0];
        try { NbtParser.ParseUncompressed(bomb, new(), default); throw new Exception("list bomb accepted"); }
        catch (InvalidDataException) { _checks++; }
        var duplicate = Bytes(C("{a:1,b:2}"));
        duplicate[duplicate.AsSpan().IndexOf(new byte[] { 0, 1, (byte)'b' }) + 2] = (byte)'a';
        try { NbtParser.ParseUncompressed(duplicate, new(), default); throw new Exception("duplicate accepted"); }
        catch (InvalidDataException) { _checks++; }
    }

    private static async Task CheckLifecycle(string root)
    {
        var path = Path.Combine(root, "p1-lifecycle.schem");
        Write(path, Sponge(3, "[{Pos:[I;0,0,0],Id:'minecraft:sign',Data:{front_text:{messages:['\"x\"','\"\"','\"\"','\"\"']}}}]"));
        var jar = Path.Combine(root, "p1-lifecycle-client.jar");
        File.Copy(Path.Combine(root, "client.jar"), jar);
        var pack = Path.Combine(root, "p1-empty-pack.zip");
        using (ZipFile.Open(pack, ZipArchiveMode.Create)) { }
        var session = new ProjectionSession();
        await session.LoadAsync(path, [new(jar, "26.3")]);
        var original = session.Document!;
        var generation = session.Resources!.Generation;
        Check(original.BlockEntities.Count == 1 && session.CaptureDiagnostics().DocumentBytes == original.EstimatedCpuBytes, "session owns and counts payload");
        Check(session.Resources.SelectClient(default).Status == ProjectionVersionStatus.UnknownVersion, "display label cannot spoof profile");
        await session.ApplyResourcePackAsync(pack);
        Check(ReferenceEquals(session.Document, original) && session.Resources!.Generation != generation &&
            session.Document.BlockEntities.Count == 1, "resource generation replaces caches, retains source data");
        await session.ReloadAsync();
        Check(!ReferenceEquals(session.Document, original) && session.Document!.BlockEntities.Count == 1, "reload recreates source store");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await session.LoadAsync(path, [new(jar)], cancellationToken: cancel.Token); throw new Exception("load cancel missing"); }
        catch (OperationCanceledException) { _checks++; }
        Check(session.Document is null && session.CaptureDiagnostics().DocumentBytes == 0 && session.Resources is null, "cancel does not publish data");
        using var duringRead = new CancellationTokenSource();
        await ExpectCanceled(() => ProjectionReader.ReadAsync(path, progress: new ImmediateProgress(value =>
        { if (value.Stage == "blocks") duringRead.Cancel(); }), cancellationToken: duringRead.Token), "cancel during block decode");
        await session.LoadAsync(path, [new(jar)]);
        session.Clear();
        Check(session.Document is null && session.RenderDiagnostics.Count == 0 && session.CaptureDiagnostics().DocumentBytes == 0, "clear releases session data ownership");
        await session.LoadAsync(path, [new(jar)]);
        session.Dispose(); session.Dispose();
        Check(session.Document is null && session.Resources is null && session.CaptureDiagnostics().DocumentBytes == 0, "dispose releases source and resources");
        Check(original.BlockEntities.Count == 1, "retained document remains immutable and readable after session disposal");
        using var exclusive = File.Open(jar, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check(exclusive.Length > 0, "resource handles released");
    }

    private sealed class ImmediateProgress(Action<ProjectionLoadProgress> action) : IProgress<ProjectionLoadProgress>
    {
        public void Report(ProjectionLoadProgress value) => action(value);
    }

    internal static NbtCompound C(string snbt) => NbtParser.ParseSnbt(snbt).RootCompound!;
    internal static NbtCompound Sponge(int version, string entities, int width = 1, byte[]? blockBytes = null,
        string offset = "[I;-10,20,-30]", string palette = "{'minecraft:air':0,'minecraft:oak_sign':1}", int dataVersion = 3120)
    {
        var bytes = blockBytes ?? [1];
        var data = "[B;" + string.Join(',', bytes.Select(value => unchecked((sbyte)value) + "b")) + "]";
        var content = "Palette:" + palette + "," + (version == 3 ? "Data:" : "BlockData:") + data + ",BlockEntities:" + entities;
        return C("{Schematic:{Version:" + version + ",DataVersion:" + dataVersion + ",Width:" + width + "s,Height:1s,Length:1s,Offset:" + offset + "," +
            (version == 3 ? "Blocks:{" + content + "}" : content) + "}}");
    }
    private static string Region(string origin, string size, long packed, string entities) =>
        "{Position:" + origin + ",Size:" + size + ",BlockStatePalette:[{Name:'minecraft:air'},{Name:'minecraft:oak_sign'}],BlockStates:[L;" + packed + "L],TileEntities:" + entities + "}";
    private static NbtCompound Litematic(string regions) => C("{Version:6,MinecraftDataVersion:3120,Metadata:{Name:'P1 synthetic'},Regions:" + regions + "}");
    internal static void Write(string path, NbtCompound nbt)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        gzip.Write(Bytes(nbt));
    }
    private static byte[] Bytes(NbtTag root)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write((byte)root.Type); Text(""); Payload(root);
        return output.ToArray();
        void I32(int value) => writer.Write(BinaryPrimitives.ReverseEndianness(value));
        void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write(BinaryPrimitives.ReverseEndianness((ushort)bytes.Length)); writer.Write(bytes); }
        void Payload(NbtTag tag)
        {
            switch (tag)
            {
                case NbtCompound compound:
                    foreach (var (key, value) in compound) { writer.Write((byte)value.Type); Text(key); Payload(value); }
                    writer.Write((byte)0); break;
                case NbtList list: writer.Write((byte)list.ElementType); I32(list.Count); foreach (var value in list) Payload(value); break;
                case NbtString value: Text(value.Value); break;
                case NbtByte value: writer.Write(value.Value); break;
                case NbtShort value: writer.Write(BinaryPrimitives.ReverseEndianness(value.Value)); break;
                case NbtInt value: I32(value.Value); break;
                case NbtLong value: writer.Write(BinaryPrimitives.ReverseEndianness(value.Value)); break;
                case NbtByteArray value: I32(value.Values.Length); writer.Write(value.Values); break;
                case NbtIntArray value: I32(value.Values.Length); foreach (var number in value.Values) I32(number); break;
                case NbtLongArray value: I32(value.Values.Length); foreach (var number in value.Values) writer.Write(BinaryPrimitives.ReverseEndianness(number)); break;
                default: throw new NotSupportedException(tag.Type.ToString());
            }
        }
    }
    internal static void Check(bool condition, string message) { _checks++; if (!condition) throw new Exception("P1 data: " + message); }
    private static async Task ExpectFormat(Func<Task<ProjectionDocument>> action, string message)
    { try { await action(); throw new Exception("Expected format failure: " + message); } catch (ProjectionFormatException) { _checks++; } }
    private static async Task ExpectCanceled(Func<Task<ProjectionDocument>> action, string message)
    { try { await action(); throw new Exception("Expected cancel: " + message); } catch (OperationCanceledException) { _checks++; } }
}
