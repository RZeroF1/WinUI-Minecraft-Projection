using System.IO.Compression;
using System.Numerics;
using RZeroF1.Minecraft.Projection;
using K = RZeroF1.Minecraft.Projection.ProjectionContributionKind;
using S = RZeroF1.Minecraft.Projection.ProjectionContributionState;
using V = RZeroF1.Minecraft.Projection.ProjectionCapabilityResult;

internal static class P1PlanRegression
{
    private static int _checks;
    internal static void Run(string root)
    {
        CheckAllRegistrations();
        CheckComposition();
        CheckProductionBaker(root);
        Console.WriteLine($"PASS P1 plans: {_checks} checks; 762 versioned recipes/source identity/composition/empty vs unknown/resource failure/policy/JSON/fluid/slices/generation/budget/cancellation");
    }

    private static void CheckAllRegistrations()
    {
        var registry = ProjectionRenderCapabilityRegistry.Vanilla;
        foreach (var version in registry.Versions.Values)
        foreach (var registration in version.Registrations.Values)
        {
            var source = Document(StateForType(registration.TypeId));
            var input = ProjectionRenderPlanResolver.Describe(source, source.Blocks[0], source.Palette[0], Profile(version.Id, source.DataVersion), default);
            Check(input.Declaration.Recipe == registration.Recipe, "block/type join " + version.Id + "/" + registration.TypeId);
            var plan = ProjectionRenderPlanResolver.Complete(input, new(false, "MissingBlockstate"));
            Check(plan.Assessment.Contributions.Count > 0 &&
                plan.Declaration.Recipe == registration.Recipe && plan.Assessment.Result == V.Unsupported,
                "registration has reviewable, unfinished plan " + version.Id + "/" + registration.TypeId);
            Check(plan.Assessment.Contributions.All(contribution => !string.IsNullOrWhiteSpace(contribution.Evidence)), "all states carry evidence");
        }
    }

    private static void CheckComposition()
    {
        var sign = Plan("oak_sign", "26.2", null);
        Check(sign.Assessment.Result == V.Partial && State(sign, K.BlockModel) == S.Resolved && State(sign, K.Text) == S.UnknownData,
            "modern sign keeps base with unknown text");
        Check(sign.SourceProfile.DataVersion == 3120 && sign.Declaration.VersionId == "26.2", "source/client axes independent");
        Check(Plan("oak_sign", "1.21.11", null, geometry: false, failure: "UnsupportedRenderer").Assessment.Result == V.Unsupported, "old sign needs special board");
        var text = "{Text1:'\"hello\"',Text2:'\"\"',Text3:'\"\"',Text4:'\"\"'}";
        Check(State(Plan("oak_sign", "26.2", text), K.Text) == S.Unsupported, "known nonempty text has no producer");
        var emptyText = "{Text1:'\"\"',Text2:'\"\"',Text3:'\"\"',Text4:'\"\"'}";
        var emptySign = Plan("oak_sign", "26.2", emptyText);
        Check(emptySign.Assessment.Result == V.Full && State(emptySign, K.Text) == S.KnownEmpty, "explicit adapted empty sign");
        Check(State(Plan("oak_sign", "26.2", emptyText, dataVersion: 999999), K.Text) == S.UnknownData, "future source cannot prove empty");
        var emptyCampfire = Plan("campfire", "1.21.11", "{Items:[]}");
        Check(emptyCampfire.Assessment.Result == V.Full && State(emptyCampfire, K.ItemContent) == S.KnownEmpty, "known empty displayed items");
        Check(State(Plan("campfire", "1.21.11", "{}"), K.ItemContent) == S.UnknownData, "missing items never empty");
        Check(State(Plan("campfire", "1.21.11", "{Items:[{Slot:0b,id:'minecraft:apple',Count:1b}]}"), K.ItemContent) == S.Unsupported, "nonempty item view unsupported");
        Check(State(Plan("campfire", "1.21.11", "{Items:1}"), K.ItemContent) == S.UnknownData, "wrong data type unknown visual data");
        var closedLectern = Plan("lectern[has_book=false]", "1.21.11", null);
        Check(closedLectern.Assessment.Result == V.Full && State(closedLectern, K.SpecialModel) == S.KnownEmpty && State(closedLectern, K.Animation) == S.KnownEmpty, "has_book false predicates");
        Check(Plan("lectern[has_book=true]", "1.21.11", null).Assessment.Result == V.Partial, "book true requires book producer");
        Check(State(Plan("lectern", "1.21.11", null), K.SpecialModel) == S.UnknownData, "missing has_book not false");
        var debug = Plan("structure_block", "1.21.11", null);
        Check(debug.Assessment.Result == V.Full && State(debug, K.DebugOverlay) == S.DisabledByPolicy, "debug disabled by policy");
        var chest = Plan("chest", "1.21.11", null);
        Check(chest.Assessment.Result == V.Partial && State(chest, K.BlockModel) == S.Resolved && State(chest, K.SpecialModel) == S.Unsupported,
            "extra JSON cannot erase dedicated body");
        Check(!chest.UsesPlaceholder, "partial base never hidden by a cube");
        var missingChest = Plan("chest", "1.21.11", null, geometry: false, failure: "MissingBlockstate");
        Check(missingChest.UsesPlaceholder && missingChest.Assessment.Result == V.Unsupported &&
            missingChest.Assessment.Contributions.All(contribution => contribution.State != S.ResourceFailure), "dedicated body absence remains unsupported, not fake JSON failure");
        var corruptChest = Plan("chest", "1.21.11", null, geometry: false, failure: "TextureDecodeFailure");
        Check(corruptChest.UsesPlaceholder && State(corruptChest, K.BlockModel) == S.ResourceFailure, "bad override not hidden by dedicated fallback");
        var corruptBase = Plan("campfire", "1.21.11", "{Items:[]}", geometry: false, failure: "MissingTexture");
        Check(State(corruptBase, K.BlockModel) == S.ResourceFailure && State(corruptBase, K.ItemContent) == S.KnownEmpty && corruptBase.Assessment.Result == V.Unsupported,
            "known empty cannot mask resource failure");
        foreach (var type in new[] { "furnace", "hopper", "barrel", "crafter", "chiseled_bookshelf", "creaking_heart" })
            Check(Plan(type, "1.21.11", null).Assessment.Result == V.Full, "known no BER " + type);
        var ordinaryBed = Plan("red_bed", "26.2", null);
        Check(ordinaryBed.Assessment.Contributions.Single().Requirement.Kind == K.BlockModel && ordinaryBed.Assessment.Result == V.Full &&
            ordinaryBed.Declaration.BlockEntityTypeId is null, "modern bed ordinary path, no removed BE type");
        Check(Plan("red_bed", "26.3", "{color:14}", dataVersion: 1343).Assessment.Result == V.Full,
            "old source bed data cannot restore a removed target BER");
        Check(Plan("bed", "1.11", null, dataVersion: 1343).Assessment.Result == V.Full, "pre-entity bed uses its explicit BlockModel profile");
        Check(State(Plan("bed", "1.12.2", null, geometry: false, failure: "UnsupportedRenderer", dataVersion: 1343), K.SpecialModel) == S.UnknownData,
            "old bed requires source color");
        Check(State(Plan("bed", "1.12.2", "{color:14}", geometry: false, failure: "UnsupportedRenderer", dataVersion: 1343), K.SpecialModel) == S.Unsupported,
            "source color is adapted but geometry not implemented");
        Check(State(Plan("red_banner", "1.21.11", "{Patterns:[]}", geometry: false, failure: "UnsupportedRenderer"), K.SurfacePatterns) == S.KnownEmpty,
            "empty pattern list separate from missing banner geometry");
        Check(State(Plan("skeleton_skull", "1.21.11", null, geometry: false, failure: "UnsupportedRenderer"), K.Appearance) == S.KnownEmpty, "nonplayer head needs no skin");
        Check(State(Plan("player_head", "1.21.11", null, geometry: false, failure: "UnsupportedRenderer"), K.Appearance) == S.UnknownData, "missing player appearance unknown");
        var waterlogged = Plan("oak_sign[waterlogged=true]", "26.2", emptyText);
        Check(waterlogged.Assessment.Result == V.Partial && State(waterlogged, K.Fluid) == S.Unsupported, "waterlogged model remains with separate fluid contribution");
        var fluid = Plan("water[level=0]", "1.21.11", null, isFluid: true);
        Check(fluid.Assessment.Result == V.StaticLimited && State(fluid, K.Fluid) == S.StaticLimited, "existing fluid limits explicit");
        Check(State(Plan("water[level=0]", "1.21.11", null, isFluid: true, failure: "MissingTexture"), K.Fluid) == S.ResourceFailure, "fluid resource failure");
        Check(State(Plan("beacon", "1.21.11", null), K.Beam) == S.UnknownData, "external world not invented as inactive");
        var unknown = Document("campfire");
        var unknownInput = ProjectionRenderPlanResolver.Describe(unknown, unknown.Blocks[0], unknown.Palette[0],
            new(new(ProjectionVersionStatus.UnknownVersion, "future", null), ProjectionVisualDataProfile.Select(3120), 77), default);
        Check(ProjectionRenderPlanResolver.Complete(unknownInput, new(true, null)).Assessment.Result == V.Partial, "unknown resource profile never Full");
        var mismatch = Plan("campfire", "1.21.11", "{Items:[]}", rawType: "minecraft:chest");
        Check(mismatch.DataIssue == "BlockEntityTypeMismatch" && State(mismatch, K.ItemContent) == S.UnknownData && mismatch.Assessment.Result == V.Partial,
            "mismatched payload cannot prove emptiness");
        var missingId = Plan("campfire", "1.21.11", "{Items:[]}", rawType: "");
        Check(missingId.DataIssue == "MissingBlockEntityId" && State(missingId, K.ItemContent) == S.UnknownData, "missing ID never trusted");
        var litematic = Document("campfire");
        var litematicPayload = ProjectionBlockEntityStore.Create(litematic.Blocks[0], P1DataRegression.C("{Items:[]}"), new(0, 0, 0),
            ProjectionFormat.Litematic, 6, 3120, 0, default);
        litematic = CopyWithStore(litematic, new([litematicPayload]));
        var idless = ProjectionRenderPlanResolver.Complete(ProjectionRenderPlanResolver.Describe(litematic, litematic.Blocks[0], litematic.Palette[0], Profile("1.21.11", 3120), default), new(true, null));
        Check(idless.Assessment.Result == V.Full && idless.DataIssue is null && State(idless, K.ItemContent) == S.KnownEmpty,
            "Litematic ID-less payload uses exact winning block type");
        Check(Plan("campfire", "1.21.11", "{Items:[]}", dataVersion: 1343, rawType: "Campfire").DataIssue == "BlockEntityTypeMismatch", "unknown legacy ID not case guessed");
        Check(Plan("oak_sign", "26.2", emptyText, dataVersion: 1343, rawType: "Sign").DataIssue is null, "explicit legacy type alias");
        Check(ProjectionBlockEntityBindings.TypeForBlock("example:oak_sign") is null && ProjectionBlockEntityBindings.TypeForBlock("minecraft:sign_like_custom_block") is null,
            "no suffix/contains routing");
        foreach (var block in new[] { "poplar_sign", "poplar_hanging_sign", "poplar_shelf" })
            Check(ProjectionBlockEntityBindings.TypeForBlock("minecraft:" + block) is not null, "explicit audited poplar variant " + block);
        Check(Plan("example:unknown", "26.3", "{Items:[]}", rawType: "minecraft:campfire").DataIssue == "UnknownBlockEntityAssociation",
            "unknown association is distinct from contradictory types");
    }

    private static void CheckProductionBaker(string root)
    {
        var jar = Path.Combine(root, "p1-plan.jar");
        var model = """{"textures":{"all":"block/solid"},"elements":[{"from":[0,0,0],"to":[16,20,16],"faces":{"north":{"texture":"#all","cullface":"north"},"south":{"texture":"#all","cullface":"south"},"up":{"texture":"#all","cullface":"up"}}}]}""";
        using (var archive = ZipFile.Open(jar, ZipArchiveMode.Create))
        {
            Put(archive, "assets/minecraft/models/block/base.json", model);
            foreach (var name in new[] { "stone", "campfire", "chest", "oak_sign", "lectern" })
                Put(archive, "assets/minecraft/blockstates/" + name + ".json", """{"variants":{"":{"model":"block/base"}}}""");
            Put(archive, "assets/minecraft/models/block/empty.json", "{\"elements\":[]}");
            Put(archive, "assets/minecraft/blockstates/shulker_box.json", """{"variants":{"":{"model":"block/empty"}}}""");
            using var stream = archive.CreateEntry("assets/minecraft/textures/block/solid.png").Open();
            stream.Write(ProjectionPng.EncodeBgra([255, 255, 255, 255], 1, 1));
        }
        using var resources = new ProjectionResourceSet(jar);
        var document = Document("chest", "campfire", "oak_sign", "stone");
        var baker = new ProjectionModelBaker(resources);
        var scene = baker.Build(document);
        Check(scene.RenderPlans.Count == 4 && scene.RenderPlans.Select(plan => plan.Source).Distinct().Count() == 4 && scene.BlockCount == 4, "one plan/count per source");
        Check(scene.RenderPlans.All(plan => plan.DocumentIdentity == document.Identity) && Document("chest").Identity != document.Identity, "source identity includes document generation");
        Check(scene.TriangleCount == 24 && scene.RenderPlans.All(plan => !plan.UsesPlaceholder), "ordinary bases preserved, no overlays");
        Check(scene.RenderPlans.All(plan => plan.Bounds.Maximum.Y == 1.25f), "actual JSON world bounds");
        Check(scene.RenderPlans.All(plan => plan.Assessment.Result == V.Partial), "synthetic resource identity remains unknown");
        Check(ProjectionStatistics.GroupMaterials(document).Sum(group => group.Count) == 4 && new ProjectionSliceIndex(document).VisibleBlockCount(0, 0, 0) == 1,
            "materials/slice source counts independent of contributions");
        var boundary = baker.BuildSliceBoundary(document, new(document), 0, 0, 0);
        Check(boundary.BlockCount == 0 && boundary.RenderPlans.Count == 4 && boundary.RenderPlans[0].Source == scene.RenderPlans[0].Source, "slice uses complete document identity");
        var unsupported = baker.Build(Document("shulker_box"));
        Check(unsupported.TriangleCount == 12 && unsupported.RenderPlans.Single().UsesPlaceholder && unsupported.Diagnostics.Any(diagnostic => diagnostic.Reason == "UnsupportedRenderer"),
            "special body still black purple placeholder");
        var filtered = baker.Build(Document("glass_pane[north=false,east=false,south=false,west=false]"));
        Check(filtered.TriangleCount == 0 && !filtered.RenderPlans.Single().UsesPlaceholder && State(filtered.RenderPlans.Single(), K.BlockModel) == S.Unsupported &&
            filtered.Diagnostics.Single().Reason == "PreviewFilteredIsolatedGlassPane", "preview filter does not supply fake implementation evidence");
        try { new ProjectionModelBaker(resources) { MaxRenderPlanBytes = 1 }.Build(document); throw new Exception("budget accepted"); }
        catch (InvalidDataException) { _checks++; }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { baker.Build(document, cancellationToken: cancel.Token); throw new Exception("cancel accepted"); }
        catch (OperationCanceledException) { _checks++; }
        var originalGeneration = resources.Generation;
        var pack = Path.Combine(root, "p1-bad-plan-pack.zip");
        using (var archive = ZipFile.Open(pack, ZipArchiveMode.Create)) Put(archive, "assets/minecraft/models/block/base.json", "broken");
        resources.AddResourcePack(pack);
        scene = baker.Build(document);
        Check(resources.Generation != originalGeneration && scene.RenderPlans.All(plan => plan.ResourceGeneration == resources.Generation && plan.UsesPlaceholder), "reload invalidates actual model and plan observations");
        Check(scene.RenderPlans.All(plan => State(plan, K.BlockModel) == S.ResourceFailure), "broken override cannot fall back to old plan success");
        var fluidScene = baker.Build(Document("water[level=0]"));
        Check(State(fluidScene.RenderPlans.Single(), K.Fluid) == S.ResourceFailure && fluidScene.TriangleCount > 0, "missing fluid resource retains fallback and accurate state");
        Check(scene.EstimatedCpuBytes >= scene.RenderPlans.Sum(plan => plan.EstimatedCpuBytes), "plan memory counted");
        // Same palette, distinct source appearance: no state-only visual/plan cache.
        var a = Document("campfire", "campfire");
        var data = ProjectionBlockEntityStore.Create(a.Blocks[0], P1DataRegression.C("{Items:[]}"), new(0, 0, 0), ProjectionFormat.Litematic, 6, 3120, 0, default, "minecraft:campfire");
        a = CopyWithStore(a, new([data]));
        var first = ProjectionRenderPlanResolver.Describe(a, a.Blocks[0], a.Palette[0], Profile("1.21.11", 3120), default);
        var second = ProjectionRenderPlanResolver.Describe(a, a.Blocks[1], a.Palette[1], Profile("1.21.11", 3120), default);
        Check(State(ProjectionRenderPlanResolver.Complete(first, new(true, null)), K.ItemContent) == S.KnownEmpty &&
            State(ProjectionRenderPlanResolver.Complete(second, new(true, null)), K.ItemContent) == S.UnknownData, "appearance belongs to each source, not palette");
    }

    private static string StateForType(string type) => type switch
    {
        "minecraft:sign" => "oak_sign", "minecraft:hanging_sign" => "oak_hanging_sign", "minecraft:banner" => "red_banner",
        "minecraft:skull" => "player_head", "minecraft:bed" => "red_bed", "minecraft:mob_spawner" => "spawner",
        "minecraft:piston" => "moving_piston", "minecraft:brushable_block" or "minecraft:suspicious_sand" => "suspicious_sand",
        "minecraft:shelf" => "oak_shelf", _ => type
    };
    private static ProjectionVersionProfile Profile(string version, int? sourceVersion)
    {
        var registry = ProjectionRenderCapabilityRegistry.Vanilla;
        return new(registry.SelectVersion(version, registry.Versions[version].ClientSha256), ProjectionVisualDataProfile.Select(sourceVersion), 77);
    }
    private static ProjectionRenderPlan Plan(string state, string version, string? payload, bool geometry = true, string? failure = null,
        int dataVersion = 3120, string? rawType = null, bool isFluid = false)
    {
        var document = Document(state, dataVersion: dataVersion);
        if (payload is not null)
        {
            var type = rawType ?? ProjectionBlockEntityBindings.TypeForBlock(document.Palette[0].Name);
            var entry = ProjectionBlockEntityStore.Create(document.Blocks[0], P1DataRegression.C(payload), new(0, 0, 0),
                rawType == "" ? ProjectionFormat.SpongeSchematic : ProjectionFormat.Litematic, 6, dataVersion, 0, default, type);
            document = CopyWithStore(document, new([entry]));
        }
        var input = ProjectionRenderPlanResolver.Describe(document, document.Blocks[0], document.Palette[0], Profile(version, dataVersion), default);
        return ProjectionRenderPlanResolver.Complete(input, new(geometry, failure, IsFluid: isFluid));
    }
    private static ProjectionDocument CopyWithStore(ProjectionDocument source, ProjectionBlockEntityStore store) => new()
    {
        Name = source.Name, SourcePath = source.SourcePath, Format = source.Format, DataVersion = source.DataVersion, Min = source.Min, Max = source.Max,
        Blocks = source.Blocks, Palette = source.Palette, Regions = [], Warnings = [], BlockEntities = store
    };
    private static ProjectionDocument Document(string state, int dataVersion = 3120) => Document([state], dataVersion);
    private static ProjectionDocument Document(params string[] states) => Document(states, 3120);
    private static ProjectionDocument Document(string[] states, int dataVersion) => new()
    {
        Name = "P1 plan fixture", SourcePath = "synthetic", Format = ProjectionFormat.Litematic, DataVersion = dataVersion,
        Min = new(0, 0, 0), Max = new(states.Length * 2, 0, 0), Palette = states.Select(ProjectionState.Parse).ToArray(),
        Blocks = states.Select((_, index) => new ProjectionBlock(new(index * 2, 0, 0), index, "fixture")).ToArray(), Regions = [], Warnings = []
    };
    private static S State(ProjectionRenderPlan plan, K kind) => plan.Assessment.Contributions.Single(contribution => contribution.Requirement.Kind == kind).State;
    private static void Put(ZipArchive archive, string path, string text) { using var writer = new StreamWriter(archive.CreateEntry(path).Open()); writer.Write(text); }
    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new Exception("P1 plans: " + message); }
}
