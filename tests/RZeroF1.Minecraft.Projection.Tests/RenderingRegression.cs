using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using RZeroF1.Minecraft.Projection;

internal static class RenderingRegression
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        var jar = Path.Combine(root, "rendering.jar");
        using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
        {
            void Text(string path, string value) { using var writer = new StreamWriter(zip.CreateEntry("assets/minecraft/" + path).Open()); writer.Write(value); }
            void Png(string name, byte[] pixels, int w, int h) { using var stream = zip.CreateEntry("assets/minecraft/textures/block/" + name + ".png").Open(); stream.Write(ProjectionPng.EncodeBgra(pixels, w, h)); }
            Text("models/block/cube.json", """
                {"textures":{"all":"block/solid"},"elements":[{"from":[0,0,0],"to":[16,16,16],"faces":{
                "north":{"texture":"#all","cullface":"north"},"south":{"texture":"#all","cullface":"south"},
                "east":{"texture":"#all","cullface":"east"},"west":{"texture":"#all","cullface":"west"},
                "up":{"texture":"#all","cullface":"up"},"down":{"texture":"#all","cullface":"down"}}}]}
                """);
            Text("blockstates/stone.json", """{"variants":{"":{"model":"block/cube"}}}""");
            foreach (var pane in new[] { "glass_pane", "blue_stained_glass_pane" })
                Text("blockstates/" + pane + ".json", """{"variants":{"":{"model":"block/cube"}}}""");
            Text("models/block/empty.json", """{"elements":[]}""");
            Text("blockstates/chest.json", """{"variants":{"":{"model":"block/empty"}}}""");
            Text("blockstates/custom_empty.json", """{"variants":{"":{"model":"block/empty"}}}""");
            Text("models/block/bad.json", """{"parent":"block/cube","textures":{"all":"block/corrupt"}}""");
            Text("textures/block/corrupt.png", "not a PNG");
            Text("blockstates/bad.json", """{"variants":{"":{"model":"block/bad"}}}""");
            var faces = new[] { "north", "south", "east", "west", "up", "down" }.ToDictionary(d => d, d => new { texture = "#all" });
            Text("models/block/no_cull.json", System.Text.Json.JsonSerializer.Serialize(new { textures = new { all = "block/solid" }, elements = new[] { new { from = new[] {0,0,0}, to = new[] {16,16,16}, faces } } }));
            Text("blockstates/no_cull.json", """{"variants":{"":{"model":"block/no_cull"}}}""");
            Text("models/block/small.json", System.Text.Json.JsonSerializer.Serialize(new { textures = new { all = "block/solid" }, elements = new[] { new { from = new[] {7,0,7}, to = new[] {9,6,9}, faces } } }));
            foreach (var name in new[] { "lever", "wheat", "candle", "farmland" })
                Text("blockstates/" + name + ".json", """{"variants":{"":{"model":"block/small"}}}""");
            Text("blockstates/missing_parent.json", """{"variants":{"":{"model":"block/missing_parent"}}}""");
            Text("models/block/missing_parent.json", """{"parent":"block/not_installed"}""");
            Png("solid", [255,255,255,255], 1, 1);
            Png("water_still", [100,60,30,180], 1, 1); Png("water_flow", [100,60,30,180], 1, 1);
            Png("lava_still", [0,80,255,255], 1, 1); Png("lava_flow", [0,80,255,255], 1, 1);
            // Source has two colored frames; first listed frame is green, not atlas index zero.
            byte[] frames = [0,0,255,255, 0,0,255,255, 0,0,255,255, 0,0,255,255,
                0,255,0,255, 0,255,0,255, 0,255,0,255, 0,255,0,255];
            Png("animated", frames, 2, 4); Png("rectangle", frames, 2, 4);
            Text("textures/block/animated.png.mcmeta", """{"animation":{"frames":[{"index":1,"time":3},0]}}""");
            Text("textures/block/rectangle.png.mcmeta", """{"texture":{"blur":false}}""");
            for (var x = 0; x < 4; x++)
            for (var y = 0; y < 4; y++)
                Text($"blockstates/rot{x}{y}.json", System.Text.Json.JsonSerializer.Serialize(new { variants = new Dictionary<string, object> { [""] = new { model = "block/one_face", x = x * 90, y = y * 90 } } }));
            Text("models/block/one_face.json", """{"textures":{"all":"block/solid"},"elements":[{"from":[0,0,0],"to":[16,16,16],"faces":{"north":{"texture":"#all","cullface":"north"}}}]}""");
            Text("models/block/uv_rectangle.json", System.Text.Json.JsonSerializer.Serialize(new {
                textures = new { all = "block/solid" },
                elements = new[] { new { from = new[] {2,3,5}, to = new[] {13,11,14}, faces } }
            }));
            Text("models/block/uv_custom.json", """{"textures":{"all":"block/solid"},"elements":[{"from":[2,3,5],"to":[13,11,14],"faces":{"up":{"texture":"#all","uv":[3,6,13,15],"rotation":90}}}]}""");
            Text("blockstates/uv_custom.json", """{"variants":{"":{"model":"block/uv_custom","uvlock":true,"y":90}}}""");
            Text("blockstates/uv_custom_identity.json", """{"variants":{"":{"model":"block/uv_custom","uvlock":true}}}""");
            for (var x = 0; x < 4; x++)
            for (var y = 0; y < 4; y++)
            foreach (var locked in new[] { false, true })
                Text($"blockstates/uv{x}{y}{locked}.json", System.Text.Json.JsonSerializer.Serialize(new {
                    variants = new Dictionary<string, object> { [""] = new { model = "block/uv_rectangle", x = x * 90, y = y * 90, uvlock = locked } }
                }));
        }
        using var resources = new ProjectionResourceSet(jar);
        var frame = resources.LoadTexture("minecraft:block/animated");
        Check(BinaryPrimitives.ReadInt32BigEndian(frame.Png.AsSpan(20)) == 2, "mcmeta selects frame height");
        Check(frame.Png.SequenceEqual(ProjectionPng.EncodeBgra([0,255,0,255,0,255,0,255,0,255,0,255,0,255,0,255], 2, 2)), "first listed frame, including object frame index");
        Check(BinaryPrimitives.ReadInt32BigEndian(resources.LoadTexture("minecraft:block/rectangle").Png.AsSpan(20)) == 4, "non-animation rectangular PNG is not cropped");
        var baker = new ProjectionModelBaker(resources);
        var stone = Doc(("stone", 0, 0, 0));
        var missing = baker.Build(Doc(("absent", 0, 0, 0), ("absent", 2, 0, 0)));
        Check(missing.TriangleCount == 24 && missing.Diagnostics.Single(diagnostic => !diagnostic.Reason.StartsWith("Capability.", StringComparison.Ordinal)).Count == 2 && missing.Diagnostics.Single(diagnostic => !diagnostic.Reason.StartsWith("Capability.", StringComparison.Ordinal)).Positions.Count == 2, "one placeholder per failed block; grouped diagnostics retain counts/coordinates");
        Check(missing.TexturePng.Values.All(p => p is { Length: > 0 }), "fallback always supplies valid PNG bytes");
        foreach (var state in new[] { "air", "cave_air", "void_air", "barrier", "light", "structure_void" })
            Check(baker.Build(Doc((state, 0, 0, 0))).TriangleCount == 0, "intentional invisibility: " + state);
        Check(baker.Build(Doc(("chest", 0, 0, 0))).Diagnostics.Single(diagnostic => !diagnostic.Reason.StartsWith("Capability.", StringComparison.Ordinal)).Reason == "UnsupportedRenderer", "unsupported entity classified separately");
        Check(baker.Build(Doc(("custom_empty", 0, 0, 0))).Diagnostics.Single(diagnostic => !diagnostic.Reason.StartsWith("Capability.", StringComparison.Ordinal)).Reason == "EmptyModelUnknownIntent", "unknown empty model not mislabeled invisible/supported");
        var corrupt = baker.Build(Doc(("bad", 0, 0, 0)));
        Check(corrupt.TriangleCount == 12 && corrupt.Diagnostics.Single(diagnostic => !diagnostic.Reason.StartsWith("Capability.", StringComparison.Ordinal)).Reason == "TextureDecodeFailure", "corrupt image becomes diagnostic cube");
        var hidden = baker.Build(Doc(("stone", 0, 0, 0), ("chest", 1, 0, 0)));
        Check(hidden.TriangleCount == 22, "fallback is not a trusted occluder");
        Check(baker.Build(Doc(("missing_parent",0,0,0))).Diagnostics.Single(diagnostic => !diagnostic.Reason.StartsWith("Capability.", StringComparison.Ordinal)).Reason == "MissingModelOrParent", "missing parent classified");
        Check(baker.Build(Doc(("no_cull",0,0,0),("stone",1,0,0))).TriangleCount == 22, "full geometry without Cullface keeps own internal face");
        foreach (var small in new[] {"lever","wheat","candle","farmland"})
            Check(baker.Build(Doc(("stone",0,0,0),(small,0,1,0))).TriangleCount == 24, "partial geometry cannot erase floor: " + small);
        var pair = Doc(("stone", 0, 0, 0), ("stone", 1, 0, 0));
        Check(baker.Build(pair).TriangleCount == 20, "solid internal faces still culled");
        Check(baker.BuildSliceBoundary(pair, new(pair), 0, 0, 0).TriangleCount == 2, "slice restores only removed border face");
        // Independent expected cardinal table for rotating the source north face.
        int[,] expected = { {0,1,2,3}, {4,4,4,4}, {2,3,0,1}, {5,5,5,5} };
        (int X,int Y,int Z)[] offsets = [(0,0,-1),(1,0,0),(0,0,1),(-1,0,0),(0,-1,0),(0,1,0)];
        for (var x = 0; x < 4; x++)
        for (var y = 0; y < 4; y++)
        {
            var n = offsets[expected[x,y]];
            var doc = Doc(($"rot{x}{y}",0,0,0),("stone",n.X,n.Y,n.Z));
            Check(baker.Build(doc).TriangleCount == 12, $"world Cullface X={x * 90},Y={y * 90}");
        }
        var water = baker.Build(Doc(("water[level=0]",0,0,0),("water[level=7]",1,0,0)));
        Check(water.TriangleCount == 20 && water.Batches.All(b=>b.Mode == 2), "water adjacency removes shared internal faces");
        var lava = baker.Build(Doc(("lava[level=0]",0,0,0)));
        Check(lava.Batches.All(b=>b.Mode == 0), "lava writes opaque depth");
        var stacked = Doc(("water[level=8]",0,0,0),("water[level=0]",0,1,0));
        Check(baker.Build(stacked).TriangleCount == 20, "falling and stacked water remove internal horizontal faces");
        Check(baker.BuildSliceBoundary(stacked,new(stacked),1,0,0).TriangleCount == 2, "fluid slice restores top once");
        Check(water.Batches.SelectMany(b=>b.Vertices).All(v=>float.IsFinite(v.Normal.LengthSquared())), "finite fluid normals");
        Check(new ProjectionRenderOptions().HideIsolatedGlassPanes, "isolated pane preview filter defaults to enabled");
        var faithfulBaker = new ProjectionModelBaker(resources, new ProjectionRenderOptions { HideIsolatedGlassPanes = false });
        var previewBaker = new ProjectionModelBaker(resources);
        foreach (var pane in new[] { "glass_pane", "blue_stained_glass_pane" })
        {
            var isolated = Doc(($"{pane}[north=false,east=false,south=false,west=false]",0,0,0));
            Check(faithfulBaker.Build(isolated).TriangleCount == 12, "explicit false retains isolated panes");
            var filtered = previewBaker.Build(isolated);
            Check(filtered.TriangleCount == 0 && filtered.Diagnostics.Single(diagnostic => !diagnostic.Reason.StartsWith("Capability.", StringComparison.Ordinal)).Reason == "PreviewFilteredIsolatedGlassPane", "default filter reports hidden geometry");
            Check(isolated.Blocks.Count == 1 && new ProjectionSliceIndex(isolated).GroupMaterials().Single().Count == 1, "filter preserves source/material counts");
            var connected = Doc(($"{pane}[north=false,east=true,south=false,west=false]",0,0,0));
            Check(previewBaker.Build(connected).TriangleCount == 12, "connected pane retained");
            Check(previewBaker.Build(Doc((pane,0,0,0))).TriangleCount == 12, "missing connection properties not guessed");
            var border = Doc(("stone",0,0,0),($"{pane}[north=false,east=false,south=false,west=false]",1,0,0));
            Check(previewBaker.BuildSliceBoundary(border,new(border),0,1,1).TriangleCount == 0, "slice cannot reintroduce filtered pane");
        }
        Check(previewBaker.Build(Doc(("example:glass_pane[north=false,east=false,south=false,west=false]",0,0,0))).TriangleCount == 12, "mod namespace is not filtered");
        // Independent world-coordinate oracle: an uncropped model face has one UV unit per
        // block regardless of the quarter turns, its dimensions, position or face direction.
        static Vector2 WorldUv(ProjectionMeshVertex vertex)
        {
            var p = vertex.Position + new Vector3(.5f);
            var n = vertex.Normal;
            if (n.Y > .9f) return new(p.X,p.Z);
            if (n.Y < -.9f) return new(p.X,1-p.Z);
            if (n.Z < -.9f) return new(1-p.X,1-p.Y);
            if (n.Z > .9f) return new(p.X,1-p.Y);
            if (n.X < -.9f) return new(p.Z,1-p.Y);
            return new(1-p.Z,1-p.Y);
        }
        for (var x = 0; x < 4; x++)
        for (var y = 0; y < 4; y++)
        {
            var locked = baker.Build(Doc(($"uv{x}{y}True",0,0,0)));
            Check(locked.Batches.SelectMany(b=>b.Vertices).All(v=>Vector2.Distance(v.Uv,WorldUv(v))<1e-5f),
                $"uvlock preserves rectangle scale/crop on six faces X={x*90},Y={y*90}");
            var unlocked = baker.Build(Doc(($"uv{x}{y}False",0,0,0)));
            var originalUv = baker.Build(Doc(("uv00False",0,0,0))).Batches.SelectMany(b=>b.Vertices).Select(v=>v.Uv);
            Check(unlocked.Batches.SelectMany(b=>b.Vertices).Select(v=>v.Uv).SequenceEqual(originalUv), "uvlock=false follows original model UVs");
        }
        Vector2[] customExpected = [new(1/16f,3/16f),new(1/16f,13/16f),new(10/16f,13/16f),new(10/16f,3/16f)];
        var custom = baker.Build(Doc(("uv_custom",0,0,0))).Batches.Single().Vertices.Select(v=>v.Uv);
        Check(custom.SequenceEqual(customExpected), "explicit cropped UV and face rotation retained through uvlock");
        Vector2[] identityExpected = [new(3/16f,15/16f),new(13/16f,15/16f),new(13/16f,6/16f),new(3/16f,6/16f)];
        Check(baker.Build(Doc(("uv_custom_identity",0,0,0))).Batches.Single().Vertices.Select(v=>v.Uv).SequenceEqual(identityExpected), "identity uvlock leaves face rotation and crop unchanged");
        Console.WriteLine("PASS UV lock: rectangular crops/six faces/16 rotations/explicit face rotation/unlocked geometry");
        Console.WriteLine("PASS isolated-pane preview: enabled default/explicit disable/connected/incomplete properties/materials/slice/namespace");
        Console.WriteLine("PASS rendering: fallback/invisibility/corrupt PNG/mcmeta/16 rotations/occlusion/slices/water/lava");
    }

    private static ProjectionDocument Doc(params (string State,int X,int Y,int Z)[] source) => new()
    {
        Name="regression",SourcePath="in-memory",Format=ProjectionFormat.SpongeSchematic,
        Min=new(source.Min(b=>b.X),source.Min(b=>b.Y),source.Min(b=>b.Z)),
        Max=new(source.Max(b=>b.X),source.Max(b=>b.Y),source.Max(b=>b.Z)),
        Palette=source.Select(b=>ProjectionState.Parse(b.State)).ToArray(),
        Blocks=source.Select((b,i)=>new ProjectionBlock(new(b.X,b.Y,b.Z),i,null)).ToArray(),Regions=[],Warnings=[]
    };
}
