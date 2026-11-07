using System.Numerics;
using RZeroF1.Minecraft.Projection;

internal static class P2GeometryRegression
{
    private static int _checks;
    internal static void Run(string root)
    {
        var repo = Directory.GetCurrentDirectory();
        var real = new[]
        {
            ("1.19.2", Path.Combine(repo, "artifacts/render-investigation-2026-11-06/1.19.2-client.jar"), 3120, new[] { "chest[facing=north,type=single,waterlogged=false]", "trapped_chest[facing=east,type=single,waterlogged=false]", "ender_chest[facing=south,type=single,waterlogged=false]", "red_shulker_box[facing=up]", "red_bed[facing=west,part=head,occupied=false]", "bell[facing=north,attachment=floor,powered=false]", "enchanting_table", "lectern[facing=north,has_book=true,powered=false]" }),
            ("26.2", Path.Combine(repo, "artifacts/research-archive/2026-11-04/inputs/minecraft-client-baseline/26.2-client.jar"), 4903, new[] { "chest[facing=north,type=single,waterlogged=false]", "trapped_chest[facing=east,type=single,waterlogged=false]", "ender_chest[facing=south,type=single,waterlogged=false]", "red_shulker_box[facing=up]", "bell[facing=north,attachment=floor,powered=false]", "enchanting_table", "lectern[facing=north,has_book=true,powered=false]", "decorated_pot[facing=east,cracked=false,waterlogged=false]", "copper_golem_statue[facing=south,pose=standing,oxidation_level=unaffected]" }),
            ("26.3", Path.Combine(repo, "artifacts/research-archive/2026-11-04/inputs/minecraft-client-baseline/26.3-client.jar"), 5023, new[] { "chest[facing=west,type=single,waterlogged=false]", "red_shulker_box[facing=north]", "bell[facing=south,attachment=floor,powered=false]", "decorated_pot[facing=north,cracked=false,waterlogged=false]", "copper_golem_statue[facing=east,pose=standing,oxidation_level=unaffected]" })
        };
        foreach (var (version, jar, dataVersion, states) in real)
        {
            if (!File.Exists(jar)) continue;
            using var resources = new ProjectionResourceSet(jar);
            var document = Document(states, dataVersion);
            var scene = new ProjectionModelBaker(resources).Build(document);
            Check(scene.RenderPlans.Count == states.Length, version + " one plan per source");
            Check(scene.RenderPlans.All(plan => plan.DocumentIdentity == document.Identity), version + " document identity");
            Check(scene.Batches.Count > 0 && scene.TriangleCount > states.Length * 6, version + " actual special geometry");
            var sourceRelative = scene.Batches.SelectMany(batch => batch.Vertices).Select(vertex => vertex.Position).ToArray();
            var farthest = sourceRelative.OrderByDescending(vertex => vertex.LengthSquared()).FirstOrDefault();
            Check(sourceRelative.All(vertex => float.IsFinite(vertex.LengthSquared()) && MathF.Abs(vertex.X) <= states.Length + 2 && MathF.Abs(vertex.Y) <= states.Length + 2 && MathF.Abs(vertex.Z) <= states.Length + 2), version + $" facing transforms remain bounded around the source document; farthest={farthest}");
            Check(scene.RenderPlans.All(plan => plan.Assessment.Contributions.Any(contribution => contribution.Requirement.Kind == ProjectionContributionKind.SpecialModel)), version + " special requirement present");
            Check(scene.RenderPlans.All(plan => plan.Assessment.Contributions.Any(contribution => contribution.Requirement.Kind == ProjectionContributionKind.SpecialModel && contribution.State is ProjectionContributionState.Resolved or ProjectionContributionState.StaticLimited)), version + " special contribution completed: " +
                string.Join("; ", scene.RenderPlans.Select(plan => plan.Source + ":" + string.Join(",", plan.Assessment.Contributions.Select(item => item.Requirement.Kind + "=" + item.State + "/" + item.Evidence)))));
            Check(!scene.RenderPlans.Any(plan => plan.UsesPlaceholder), version + " no special placeholder");
            Check(scene.Diagnostics.All(diagnostic => diagnostic.Reason is not "UnsupportedRenderer" and not "EmptyModelUnknownIntent"), version + " special failure diagnostic absent");
            var p2Vertices = scene.Batches.SelectMany(batch => batch.Vertices).ToArray();
            var uvMin = p2Vertices.Aggregate(new Vector2(float.PositiveInfinity), (min, vertex) => Vector2.Min(min, vertex.Uv));
            var uvMax = p2Vertices.Aggregate(new Vector2(float.NegativeInfinity), (max, vertex) => Vector2.Max(max, vertex.Uv));
            var badBatch = scene.Batches.FirstOrDefault(batch => batch.Vertices.Any(vertex => vertex.Uv.X < 0 || vertex.Uv.X > 1 || vertex.Uv.Y < 0 || vertex.Uv.Y > 1));
            Check(p2Vertices.All(vertex => float.IsFinite(vertex.Position.LengthSquared()) && float.IsFinite(vertex.Normal.LengthSquared()) && float.IsFinite(vertex.Uv.LengthSquared()) && vertex.Uv is { X: >= 0 and <= 1, Y: >= 0 and <= 1 }), version + $" finite geometry and logical UV [{uvMin},{uvMax}] texture={badBatch?.TextureId}");
            Check(scene.Batches.SelectMany(batch => batch.Vertices).All(vertex => MathF.Abs(vertex.Normal.Length() - 1) < .001f), version + " normalized normals");
            Check(scene.RenderPlans.All(plan => plan.Assessment.Result is ProjectionCapabilityResult.StaticLimited or ProjectionCapabilityResult.Partial or ProjectionCapabilityResult.Full), version + " explicit static/partial result");
            Check(scene.TexturePng.Keys.Any(id => id.Contains("chest", StringComparison.OrdinalIgnoreCase) || id.Contains("shulker", StringComparison.OrdinalIgnoreCase) || id.Contains("bell", StringComparison.OrdinalIgnoreCase) || id.Contains("bed", StringComparison.OrdinalIgnoreCase) || id.Contains("pot", StringComparison.OrdinalIgnoreCase) || id.Contains("golem", StringComparison.OrdinalIgnoreCase) || id.Contains("book", StringComparison.OrdinalIgnoreCase)), version + " role texture IDs");
            var slice = new ProjectionModelBaker(resources).BuildSliceBoundary(document, new(document), 0, document.Min.X, document.Max.X);
            Check(slice.RenderPlans.Count == states.Length && !slice.Batches.Any(batch => batch.TextureId.StartsWith("minecraft:entity/", StringComparison.Ordinal)), version + " slice never duplicates uncullled special geometry");
        }
        CheckSpriteAndReload(repo);
        CheckGeometryPrimitives();
        CheckAuditedFamilies(repo);
        Console.WriteLine($"PASS P2 geometry: {_checks} checks; profiles/chest/shulker/bed/bell/book/pot/statue/UV/normals/slices");
    }

    private static void CheckSpriteAndReload(string repo)
    {
        var jar = Path.Combine(repo, "artifacts/render-investigation-2026-11-06/1.19.2-client.jar");
        if (!File.Exists(jar)) return;
        using var resources = new ProjectionResourceSet(jar);
        var pack = Path.Combine(Path.GetTempPath(), "p2-sprite-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var archive = System.IO.Compression.ZipFile.Open(pack, System.IO.Compression.ZipArchiveMode.Create))
            {
                Write(archive, "assets/minecraft/atlases/blocks.json", "{\"sources\":[{\"type\":\"single\",\"resource\":\"minecraft:entity/test_128\",\"sprite\":\"minecraft:test_sprite\"}]}");
                Write(archive, "assets/minecraft/atlases/unstitch.json", """{"sources":[{"type":"minecraft:unstitch","resource":"minecraft:entity/test_128","divisor_x":4,"divisor_y":2,"regions":[{"sprite":"minecraft:test_crop","x":1,"y":0,"width":1,"height":1},{"sprite":"minecraft:entity/chest/normal","x":1,"y":0,"width":1,"height":1}]}]}""");
                using var png = archive.CreateEntry("assets/minecraft/textures/entity/test_128.png").Open();
                png.Write(ProjectionPng.EncodeBgra(new byte[128 * 64 * 4], 128, 64));
            }
            resources.AddResourcePack(pack);
            var resolver = new ProjectionSpriteResolver(resources);
            var sprite = resolver.Resolve("minecraft:test_sprite", 128, 64, ProjectionMaterialRole.Cutout, default);
            Check(sprite.TextureId == "minecraft:entity/test_128" && sprite.LogicalWidth == 128 && sprite.LogicalHeight == 64 && sprite.Image.Failure is null, "atlas single source and rectangular high-resolution sprite");
            var crop = resolver.Resolve("minecraft:test_crop", 32, 32, ProjectionMaterialRole.Cutout, default);
            Check(MathF.Abs(crop.U0 - .25f) < .0001f && MathF.Abs(crop.U1 - .5f) < .0001f && MathF.Abs(crop.V1 - .5f) < .0001f, "atlas unstitch sprite crop preserves logical UV rectangle");
            var croppedMaterial = ProjectionGeometryBuilder.Material(crop.TextureId, ProjectionMaterialRole.Cutout, 32, 32, "crop test", crop.U0, crop.V0, crop.U1, crop.V1);
            var cropMesh = new ProjectionSpecialMeshBuilder(Matrix4x4.Identity);
            cropMesh.AddQuad([Vector3.Zero, Vector3.UnitX, Vector3.One, Vector3.UnitY], croppedMaterial, new(0, 0, 32, 32));
            var cropUvs = cropMesh.Build().Single().Vertices.Select(vertex => vertex.Uv).ToArray();
            Check(MathF.Abs(cropUvs.Min(uv => uv.X) - crop.U0) < .0001f && MathF.Abs(cropUvs.Max(uv => uv.X) - crop.U1) < .0001f && MathF.Abs(cropUvs.Max(uv => uv.Y) - crop.V1) < .0001f, "logical UV maps through an atlas crop exactly once");
            var croppedScene = new ProjectionModelBaker(resources).Build(Document(["chest[facing=north,type=single,waterlogged=false]"], 3120));
            var croppedVertices = croppedScene.Batches.Where(batch => batch.TextureId == "minecraft:entity/test_128").SelectMany(batch => batch.Vertices).ToArray();
            Check(croppedVertices.Length > 0 && croppedVertices.All(vertex => vertex.Uv.X is >= .25f and <= .5f && vertex.Uv.Y is >= 0f and <= .5f), "special model vertices apply atlas crop to logical UVs");
            Check(resources.Generation > 0, "sprite resource generation");
            var generation = resources.Generation;
            var broken = Path.Combine(Path.GetTempPath(), "p2-broken-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var archive = System.IO.Compression.ZipFile.Open(broken, System.IO.Compression.ZipArchiveMode.Create))
                using (var stream = archive.CreateEntry("assets/minecraft/textures/entity/chest/normal.png").Open()) stream.Write([1, 2, 3, 4]);
                using var brokenResources = new ProjectionResourceSet(jar);
                brokenResources.AddResourcePack(broken);
                var image = brokenResources.LoadTexture("minecraft:entity/chest/normal");
                Check(brokenResources.Generation != generation && image.Failure == "TextureDecodeFailure", "resource reload invalidates sprite cache and keeps corrupt override failure");
                var corruptScene = new ProjectionModelBaker(brokenResources).Build(Document(["chest[facing=north,type=single,waterlogged=false]"], 3120));
                Check(corruptScene.RenderPlans.Single().UsesPlaceholder && corruptScene.RenderPlans.Single().Assessment.Contributions.Any(contribution => contribution.Requirement.Kind == ProjectionContributionKind.SpecialModel && contribution.State == ProjectionContributionState.ResourceFailure), "corrupt special texture keeps structured resource failure and placeholder");
            }
            finally { resources.Dispose(); File.Delete(broken); }
        }
        finally { resources.Dispose(); File.Delete(pack); }

        using var chestResources = new ProjectionResourceSet(jar);
        var doubleDocument = Document(["chest[facing=north,type=left,waterlogged=false]", "chest[facing=north,type=right,waterlogged=false]"], 3120);
        var doubleScene = new ProjectionModelBaker(chestResources).Build(doubleDocument);
        var x = doubleScene.Batches.SelectMany(batch => batch.Vertices).Select(vertex => vertex.Position.X).ToArray();
        var z = doubleScene.Batches.SelectMany(batch => batch.Vertices).Select(vertex => vertex.Position.Z).ToArray();
        Check(MathF.Max(x.Max() - x.Min(), z.Max() - z.Min()) > 1.5f && doubleScene.RenderPlans.All(plan => !plan.UsesPlaceholder), "double chest halves share a continuous world seam");
    }

    private static void Write(System.IO.Compression.ZipArchive archive, string path, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(text);
    }

    private static void CheckGeometryPrimitives()
    {
        var graph = ProjectionGeometryBuilder.Graph("hierarchy", ProjectionGeometryBuilder.Part("parent", new(new(.5f, .5f, .5f), new(0, 0, MathF.PI / 2), Vector3.Zero, Vector3.One, false, false, false), [ProjectionGeometryBuilder.Cuboid(Vector3.Zero, Vector3.One, ProjectionGeometryBuilder.Material("minecraft:stone", ProjectionMaterialRole.Opaque, 16, 16, "test"))], [ProjectionGeometryBuilder.Part("child", new(Vector3.Zero, Vector3.Zero, new(.5f, 0, 0), Vector3.One, true, false, false), [])]));
        var walked = graph.Walk(ProjectionPose.Identity).ToArray();
        Check(walked.Length == 3 && walked.Any(item => item.Node.Name == "child"), "hierarchical ModelPartGraph walk");
        var pose = new ProjectionPose(new(.5f, .5f, .5f), new(0, MathF.PI / 2, 0), Vector3.Zero, Vector3.One, true, false, false);
        var point = Vector3.Transform(new(1, 0, 0), pose.Matrix);
        var pivot = Vector3.Transform(new(.5f, .5f, .5f), pose.Matrix);
        Check(float.IsFinite(point.LengthSquared()) && float.IsFinite(pivot.LengthSquared()) && point != new Vector3(1, 0, 0), "pivot rotation and mirror transform");
        var rectangle = new ProjectionMaterialRequest("minecraft:test", ProjectionMaterialRole.Cutout, 128, 64, "logical rectangle");
        Check(rectangle.LogicalWidth == 128 && rectangle.LogicalHeight == 64 && rectangle.Role == ProjectionMaterialRole.Cutout, "logical material canvas is independent of PNG size");

        var materialA = ProjectionGeometryBuilder.Material("minecraft:a", ProjectionMaterialRole.Cutout, 64, 64, "first material");
        var materialB = ProjectionGeometryBuilder.Material("minecraft:b", ProjectionMaterialRole.Cutout, 64, 64, "second material");
        var visible = ProjectionGeometryBuilder.Part("visible", ProjectionPose.Identity,
            [ProjectionGeometryBuilder.Cuboid(Vector3.Zero, Vector3.One, materialA, new(0, 0, 16, 16)),
             ProjectionGeometryBuilder.Cuboid(new(1, 0, 0), new(2, 1, 1), materialB, new(16, 0, 32, 16))]);
        var hiddenChild = ProjectionGeometryBuilder.Part("hidden-child", ProjectionPose.Identity,
            [ProjectionGeometryBuilder.Cuboid(new(2, 0, 0), new(3, 1, 1), materialA)], visible: true);
        var hiddenParent = ProjectionGeometryBuilder.Part("hidden-parent", ProjectionPose.Identity, [], [hiddenChild], visible: false);
        var multiMaterialGraph = ProjectionGeometryBuilder.Graph("multi-material", visible, hiddenParent);
        var primitives = ProjectionSpecialMeshBuilder.Bake(multiMaterialGraph, ProjectionPose.Identity, default);
        Check(primitives.Count == 2 && primitives.Select(primitive => primitive.Material.TextureId).ToHashSet().SetEquals(["minecraft:a", "minecraft:b"]), "shared mesh builder groups independent materials and prunes invisible part subtrees");
        Check(primitives.All(primitive => primitive.Indices.Length == 36 && primitive.Vertices.All(vertex => float.IsFinite(vertex.Normal.LengthSquared()))), "shared cuboid baker emits six valid independently indexed faces");
        var cuboid = primitives.Single(primitive => primitive.Material.TextureId == "minecraft:a");
        Check(cuboid.Vertices.Select(vertex => vertex.Uv).Distinct().Count() > 4 && cuboid.Vertices.All(vertex => vertex.Uv is { X: >= 0 and <= 1, Y: >= 0 and <= 1 }), "cuboid faces use bounded, distinct logical UV regions");
        var topUvs = cuboid.Vertices.Where(vertex => vertex.Normal.Y > .99f).Select(vertex => vertex.Uv).ToArray();
        var bottomUvs = cuboid.Vertices.Where(vertex => vertex.Normal.Y < -.99f).Select(vertex => vertex.Uv).ToArray();
        Check(topUvs.Length == 4 && bottomUvs.Length == 4 && MathF.Abs(topUvs.Min(uv => uv.X) - .5f) < .001f && MathF.Abs(topUvs.Max(uv => uv.X) - .75f) < .001f && MathF.Abs(bottomUvs.Min(uv => uv.X) - .25f) < .001f && MathF.Abs(bottomUvs.Max(uv => uv.X) - .5f) < .001f, "standard cuboid top and bottom use audited UV net panels");
        foreach (var (facing, expected) in new[]
        {
            ("up", Vector3.UnitY), ("down", -Vector3.UnitY), ("north", -Vector3.UnitZ),
            ("south", Vector3.UnitZ), ("east", Vector3.UnitX), ("west", -Vector3.UnitX)
        })
        {
            var actual = Vector3.TransformNormal(Vector3.UnitY, ProjectionSpecialModelProducer.SurfaceFacing(facing));
            Check(Vector3.Distance(actual, expected) < .001f, "shulker " + facing + " attachment normal");
        }
        Check(Vector3.TransformNormal(Vector3.UnitZ, ProjectionSpecialModelProducer.Facing("east")).X > .99f &&
              Vector3.TransformNormal(Vector3.UnitZ, ProjectionSpecialModelProducer.Facing("west")).X < -.99f,
            "horizontal facing follows east/west block axes");
        var mirrored = ProjectionSpecialMeshBuilder.Bake(ProjectionGeometryBuilder.Graph("mirrored", visible),
            ProjectionPose.Identity with { MirrorX = true, Scale = new(2, 1, .5f) }, default);
        foreach (var primitive in mirrored)
        {
            var center = (primitive.Bounds.Minimum + primitive.Bounds.Maximum) / 2;
            Check(primitive.Vertices.All(vertex => Vector3.Dot(vertex.Normal, vertex.Position - center) > .001f),
                "mirrored nonuniform instance keeps outward unit normals");
            for (var index = 0; index < primitive.Indices.Length; index += 3)
            {
                var a = primitive.Vertices[primitive.Indices[index]];
                var b = primitive.Vertices[primitive.Indices[index + 1]];
                var c = primitive.Vertices[primitive.Indices[index + 2]];
                Check(Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), a.Normal) > 0,
                    "mirrored instance keeps front winding consistent with normal");
            }
        }
    }

    private static void CheckAuditedFamilies(string repo)
    {
        var jar = Path.Combine(repo, "artifacts/render-investigation-2026-11-06/1.19.2-client.jar");
        if (!File.Exists(jar)) return;
        using var resources = new ProjectionResourceSet(jar);
        var baker = new ProjectionModelBaker(resources);
        foreach (var facing in new[] { "up", "down", "north", "south", "east", "west" })
        {
            var scene = baker.Build(Document([$"red_shulker_box[facing={facing}]"], 3120));
            var bounds = scene.RenderPlans.Single().Bounds;
            Check(Vector3.Distance(bounds.Minimum, new(.00025f)) < .002f && Vector3.Distance(bounds.Maximum, new(.99975f)) < .002f,
                "closed shulker stays inside its source cube: " + facing);
        }
        foreach (var facing in new[] { "north", "south", "east", "west" })
        foreach (var color in new[] { "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black" })
        foreach (var part in new[] { "head", "foot" })
        {
            var document = Document([$"{color}_bed[facing={facing},part={part},occupied=false]"], 3120);
            var scene = baker.Build(document);
            var bounds = scene.RenderPlans.Single().Bounds;
            Check(!scene.RenderPlans.Single().UsesPlaceholder && MathF.Abs(bounds.Minimum.Y) < .001f &&
                  MathF.Abs(bounds.Maximum.Y - 9 / 16f) < .001f && scene.TriangleCount == 36 &&
                  scene.BlockCount == 1, color + "/" + facing + "/" + part + " exact half-bed bounds, three cuboids and one owner");
        }
        var mixed = baker.Build(Document(["enchanting_table", "lectern[facing=north,has_book=true,powered=false]", "bell[facing=north,attachment=floor,powered=false]"], 3120));
        Check(mixed.Batches.Any(batch => batch.TextureId.StartsWith("minecraft:block/", StringComparison.Ordinal)) &&
              mixed.Batches.Any(batch => batch.TextureId.Contains("book", StringComparison.Ordinal)) &&
              mixed.Batches.Any(batch => batch.TextureId.Contains("bell_body", StringComparison.Ordinal)) && mixed.BlockCount == 3,
              "ordinary bases and audited book/bell contributions coexist without repeated source counts");
        Check(mixed.RenderPlans[1].Bounds.Maximum.Y > 1, "lectern book has a world AABB extending above its owner block");
        var empty = baker.Build(Document(["lectern[facing=north,has_book=false,powered=false]"], 3120));
        Check(empty.RenderPlans.Single().Assessment.Contributions.Single(item => item.Requirement.Kind == ProjectionContributionKind.SpecialModel).State == ProjectionContributionState.KnownEmpty &&
              !empty.Batches.Any(batch => batch.TextureId.Contains("book", StringComparison.Ordinal)), "explicit empty lectern suppresses only book");
        var budgetDocument = Document(["chest[facing=north,type=single,waterlogged=false]"], 3120);
        var invalidState = baker.Build(Document(["chest[facing=up,type=single]"], 3120));
        Check(invalidState.RenderPlans.Single().UsesPlaceholder && invalidState.RenderPlans.Single().Assessment.Contributions.Any(item =>
            item.Requirement.Kind == ProjectionContributionKind.SpecialModel && item.State == ProjectionContributionState.UnknownData),
            "invalid special facing remains unknown rather than choosing a default pose");
        try { _ = new ProjectionModelBaker(resources) { MaxSpecialGeometryBytes = 1 }.Build(budgetDocument); Check(false, "special geometry budget"); }
        catch (InvalidDataException exception) { Check(exception.Message == "SpecialGeometryBudgetExceeded", "special geometry budget prevents output expansion"); }
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { _ = baker.Build(budgetDocument, cancellationToken: canceled.Token); Check(false, "special cancellation"); }
            catch (OperationCanceledException) { Check(true, "special cancellation preserves cancellation contract"); }
        }
        Check(baker.Build(budgetDocument).RenderPlans.Single().ResourceGeneration == resources.Generation, "baker recovers after cancellation using current generation");

        var modernJar = Path.Combine(repo, "artifacts/research-archive/2026-11-04/inputs/minecraft-client-baseline/26.2-client.jar");
        if (!File.Exists(modernJar)) return;
        using var modern = new ProjectionResourceSet(modernJar);
        var modernBaker = new ProjectionModelBaker(modern);
        var hashes = new HashSet<string>();
        foreach (var pose in new[] { "standing", "sitting", "running", "star" })
        foreach (var oxidation in new[] { "unaffected", "exposed", "weathered", "oxidized" })
        {
            var scene = modernBaker.Build(Document([$"copper_golem_statue[facing=north,pose={pose},oxidation_level={oxidation},waterlogged=false]"], 4903));
            Check(!scene.RenderPlans.Single().UsesPlaceholder && scene.Batches.All(batch => batch.Indices.All(index => index < batch.Vertices.Length)), "statue audited pose/material " + pose + "/" + oxidation);
            if (oxidation == "unaffected")
                hashes.Add(string.Join(";", scene.Batches.SelectMany(batch => batch.Vertices).Select(vertex => vertex.Position.ToString())));
        }
        Check(hashes.Count == 4, "four statue poses have distinct actual geometry");
        var unknown = modernBaker.Build(Document(["copper_golem_statue[facing=north]"], 4903));
        Check(unknown.RenderPlans.Single().UsesPlaceholder && unknown.RenderPlans.Single().Assessment.Contributions.Any(item =>
            item.Requirement.Kind == ProjectionContributionKind.SpecialModel && item.State == ProjectionContributionState.UnknownData),
            "missing statue pose stays unknown and uses placeholder");
        var pot = modernBaker.Build(Document(["decorated_pot[facing=north,waterlogged=false,cracked=false]"], 4903));
        Check(pot.TexturePng.Keys.Count(texture => texture.Contains("decorated_pot", StringComparison.Ordinal)) == 2 &&
              pot.RenderPlans.Single().Assessment.Result == ProjectionCapabilityResult.Partial,
            "pot base and side materials are distinct; missing side data remains Partial");
        var atlasPack = Path.Combine(Path.GetTempPath(), "p2-invalid-atlas-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var archive = System.IO.Compression.ZipFile.Open(atlasPack, System.IO.Compression.ZipArchiveMode.Create))
                Write(archive, "assets/minecraft/atlases/chests.json", "{ bad atlas override");
            modern.AddResourcePack(atlasPack);
            var brokenAtlas = modernBaker.Build(Document(["chest[facing=north,type=single,waterlogged=false]"], 4903));
            Check(brokenAtlas.RenderPlans.Single().UsesPlaceholder && brokenAtlas.RenderPlans.Single().Assessment.Contributions.Any(item =>
                item.Requirement.Kind == ProjectionContributionKind.SpecialModel && item.State == ProjectionContributionState.ResourceFailure &&
                item.Evidence == "InvalidAtlasDefinition"), "damaged high-priority atlas never silently uses base mapping");
        }
        finally { modern.Dispose(); File.Delete(atlasPack); }

        var oldJar = @"E:\PCL2\.linx\versions\1.14.4\1.14.4.jar";
        if (File.Exists(oldJar))
        {
            using var old = new ProjectionResourceSet(oldJar);
            var oldScene = new ProjectionModelBaker(old).Build(Document(["chest[facing=north,type=left,waterlogged=false]", "chest[facing=north,type=right,waterlogged=false]"], 1976));
            Check(!oldScene.RenderPlans.Any(plan => plan.UsesPlaceholder) && oldScene.TexturePng.ContainsKey("minecraft:entity/chest/normal_double") && oldScene.BlockCount == 2,
                "Double128 halves use one continuous texture while retaining two sources");
        }
    }

    private static ProjectionDocument Document(string[] states, int dataVersion) => new()
    {
        Name = "P2 special fixture", SourcePath = "synthetic", Format = ProjectionFormat.Litematic, DataVersion = dataVersion,
        Min = new(0, 0, 0), Max = new(states.Length - 1, 0, 0), Palette = states.Select(ProjectionState.Parse).ToArray(),
        Blocks = states.Select((_, index) => new ProjectionBlock(new(index, 0, 0), index, "p2")).ToArray(), Regions = [], Warnings = []
    };

    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new Exception("P2 geometry: " + message); }
}
