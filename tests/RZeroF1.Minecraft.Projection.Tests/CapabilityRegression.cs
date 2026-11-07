using System.IO.Compression;
using RZeroF1.Minecraft.Projection;
using K = RZeroF1.Minecraft.Projection.ProjectionContributionKind;
using S = RZeroF1.Minecraft.Projection.ProjectionContributionState;
using V = RZeroF1.Minecraft.Projection.ProjectionCapabilityResult;

internal static class CapabilityRegression
{
    private static int _checks;
    private static readonly ProjectionRenderCapabilityRegistry Registry = ProjectionRenderCapabilityRegistry.Vanilla;

    internal static void Run(string root)
    {
        Check(Registry.Versions.Count == 43, "43 audited client identities");
        Check(Registry.Providers.Count == 28 && Registry.Providers.Values.Distinct().Count() == 16, "28 named providers / 16 families");
        Check(Registry.Versions.Values.Sum(v => v.Registrations.Count) == 762, "762 independent registration bindings");
        Check(Registry.Versions.Values.Sum(v => v.BlockEntityTypes?.Count ?? 0) == 1049, "1049 type declarations");
        foreach (var version in Registry.Versions.Values)
        {
            var selection = Select(version.Id);
            Check(selection.Status == ProjectionVersionStatus.Verified, "verified " + version.Id);
            foreach (var row in version.Registrations.Values)
            {
                var declaration = Registry.Declare(selection, row.TypeId, ProjectionRenderPath.Unknown);
                Check(declaration.Status == ProjectionDeclarationStatus.Known && declaration.Family == row.Family,
                    "classified " + version.Id + "/" + row.TypeId);
                Check(ProjectionCapabilityEvaluator.Evaluate(declaration, []).Result == V.Unsupported, "registration never implies implementation");
                Check(ProjectionCapabilityEvaluator.Evaluate(declaration,
                    declaration.Requirements.Select(r => Obs(r.Kind, S.Resolved))).Result == V.Full,
                    "all declared contributions are required");
            }
            var report = ProjectionCapabilityCoverage.Check(selection, version.Registrations.Values, version.BlockEntityTypes, version.ResourceCodecs);
            Check(report.Issues.Count == 0, "locked census accepted " + version.Id);
            Check(report.Passed == (version.BlockEntityTypes is not null && version.ResourceCodecs is not null), "unaudited channels cannot pass");
            var removed = ProjectionCapabilityCoverage.Check(selection, version.Registrations.Values.Skip(1));
            Check(removed.Issues.Any(i => i.Code == "MissingRegistration"), "deleted registration caught " + version.Id);
            var changed = version.Registrations.Values.First() with { Provider = "FutureProvider", Recipe = "FutureRecipe" };
            var changedRows = version.Registrations.Values.Select(r => r.TypeId == changed.TypeId ? changed : r);
            Check(ProjectionCapabilityCoverage.Check(selection, changedRows).Issues.Any(i => i.Code == "RegistrationMismatch"), "same-count mutation caught");
            var added = changed with { TypeId = "minecraft:future_type" };
            Check(ProjectionCapabilityCoverage.Check(selection, version.Registrations.Values.Append(added)).Issues.Any(i => i.Code == "UnclassifiedRegistration"), "unclassified registration caught");
        }
        CheckVersionBoundaries();
        CheckCompositionAndStates();
        CheckCoverageFailures();
        CheckSyntheticBaking(root);
        CheckAssetIsolation();
        Console.WriteLine($"PASS P0 capabilities: {_checks} checks; 43 clients/762 bindings/28 providers/16 families; states/composition/coverage mutations/synthetic baking");
    }

    private static void CheckVersionBoundaries()
    {
        Check(Declare("1.12.2", "bed").Requirements.Any(r => r.Kind == K.SpecialModel), "NBT color entity bed");
        Check(Declare("1.12.2", "bed").Requirements.Single().Dependencies.HasFlag(ProjectionCapabilityDependency.BlockEntityData) &&
            !Declare("1.19.2", "bed").Requirements.Single().Dependencies.HasFlag(ProjectionCapabilityDependency.BlockEntityData), "bed color source is versioned");
        Check(Declare("26.1", "bed").Requirements.Any(r => r.Kind == K.SpecialModel), "26.1 entity bed");
        Check(!Registry.Versions["26.2"].Registrations.ContainsKey("minecraft:bed"), "26.2 removes bed BER");
        Check(Declare("26.2", null, ProjectionRenderPath.Model).Requirements.Single().Kind == K.BlockModel, "26.2 ordinary bed path");
        Check(Declare("1.19.2", "sign").Requirements.Any(r => r.Kind == K.SpecialModel), "old sign board parts");
        Check(Declare("26.2", "sign").Requirements.Select(r => r.Kind).SequenceEqual([K.BlockModel, K.Text]), "modern sign still needs text");
        Check(Registry.Versions["1.14.4"].Profile.Chest == "Double128" && Registry.Versions["1.15"].Profile.Chest == "Split64", "chest UV profile boundary");
        Check(Declare("1.14.4", "trapped_chest", ProjectionRenderPath.Model).Status == ProjectionDeclarationStatus.UnknownRenderPath,
            "unjoined class inheritance cannot become no-BER Full");
        Check(Declare("a1.1.0", null, ProjectionRenderPath.LegacyNumeric).Status == ProjectionDeclarationStatus.UnknownRenderPath, "legacy numeric handlers unaudited");
        Check(Declare("a1.1.0", null, ProjectionRenderPath.Model).Status == ProjectionDeclarationStatus.UnknownRenderPath, "terrain atlas cannot be declared ordinary JSON");
        var unknown = Registry.SelectVersion("26.999", Registry.Versions["26.3"].ClientSha256);
        var unknownDeclaration = Registry.Declare(unknown, null, ProjectionRenderPath.Model);
        Check(unknown.Status == ProjectionVersionStatus.UnknownVersion &&
            ProjectionCapabilityEvaluator.Evaluate(unknownDeclaration, [Obs(K.BlockModel, S.Resolved)]).Result == V.Partial, "unknown version never Full despite JSON");
        var mismatch = Registry.SelectVersion("26.3", new string('0', 64));
        Check(mismatch.Status == ProjectionVersionStatus.IdentityMismatch &&
            Registry.Declare(mismatch, "minecraft:sign", ProjectionRenderPath.Model).Status == ProjectionDeclarationStatus.IdentityMismatch, "version label is not verified identity");
        Check(Registry.SelectVersion("26.3", Registry.Versions["26.3"].ClientSha256.ToUpperInvariant()).Status == ProjectionVersionStatus.Verified, "hex digest casing");
        Check(Declare("1.21.11", "future_entity", ProjectionRenderPath.Model).Status == ProjectionDeclarationStatus.UnknownType, "new type explicit unknown");
        Check(Declare("1.21.11", "example:chest", ProjectionRenderPath.Model).Status == ProjectionDeclarationStatus.UnknownType, "mod chest is not vanilla chest");
        Check(Declare("1.21.11", "chest_imitation", ProjectionRenderPath.Model).Status == ProjectionDeclarationStatus.UnknownType, "no substring guessing");
        foreach (var type in new[] { "furnace", "hopper", "barrel", "crafter", "chiseled_bookshelf", "creaking_heart" })
        {
            var declaration = Declare("1.21.11", type, ProjectionRenderPath.Model);
            Check(declaration.Status == ProjectionDeclarationStatus.Known && declaration.Family is null &&
                ProjectionCapabilityEvaluator.Evaluate(declaration, [Obs(K.BlockModel, S.Resolved)]).Result == V.Full, "verified no-BER " + type);
        }
        Parallel.For(0, 128, i =>
        {
            if (Declare("26.2", "sign").Requirements.Count != 2) throw new InvalidOperationException("Immutable registry concurrency.");
        });
        Check(true, "concurrent immutable queries");
    }

    private static void CheckCompositionAndStates()
    {
        var campfire = Declare("1.19.2", "campfire");
        Check(Evaluate(campfire, Obs(K.BlockModel, S.Resolved)).Result == V.Partial, "base JSON does not provide item content");
        var empty = Evaluate(campfire, Obs(K.BlockModel, S.Resolved), Obs(K.ItemContent, S.KnownEmpty));
        Check(empty.Result == V.Full && empty.Contributions.Last().State == S.KnownEmpty, "known empty slots valid without item renderer");
        var unknown = Evaluate(campfire, Obs(K.BlockModel, S.Resolved), Obs(K.ItemContent, S.UnknownData));
        Check(unknown.Result == V.Partial && unknown.Contributions.Last().State == S.UnknownData, "absent payload is unknown, not empty");
        var broken = Evaluate(campfire, Obs(K.BlockModel, S.Resolved), Obs(K.ItemContent, S.ResourceFailure));
        Check(broken.Result == V.Partial && broken.Contributions.Last().State == S.ResourceFailure, "resource failure retained");
        Check(Evaluate(campfire, Obs(K.BlockModel, S.ResourceFailure), Obs(K.ItemContent, S.Unsupported)).Result == V.Unsupported, "no rendered contribution is unsupported");

        var debug = Declare("1.21.11", "structure_block");
        Check(Evaluate(debug, Obs(K.BlockModel, S.Resolved), Obs(K.DebugOverlay, S.DisabledByPolicy)).Result == V.Full, "explicitly disabled debug overlay is valid");
        Check(Evaluate(debug, Obs(K.BlockModel, S.Resolved)).Result == V.Partial, "missing debug observation not implicitly disabled");
        var lectern = Declare("1.19.2", "lectern");
        Check(Evaluate(lectern, Obs(K.BlockModel, S.Resolved), Obs(K.SpecialModel, S.KnownEmpty), Obs(K.Animation, S.KnownEmpty)).Result == V.Full, "has_book=false excludes book and animation");
        var chest = Declare("1.19.2", "chest");
        Check(Evaluate(chest, Obs(K.SpecialModel, S.Resolved), Obs(K.Animation, S.StaticLimited)).Result == V.StaticLimited, "canonical static pose is limited");
        Check(Evaluate(chest, Obs(K.SpecialModel, S.StaticLimited)).Result == V.Partial, "static body does not hide missing animation requirement");
        CheckThrows(() => Evaluate(chest, Obs(K.SpecialModel, S.KnownEmpty)), "required body cannot be known-empty");
        CheckThrows(() => Evaluate(chest, Obs(K.SpecialModel, S.DisabledByPolicy)), "policy cannot suppress required body");
        CheckThrows(() => Evaluate(chest, Obs(K.BlockModel, S.Resolved)), "unrequested base cannot replace special body");
        CheckThrows(() => Evaluate(chest, Obs(K.SpecialModel, S.Resolved), Obs(K.SpecialModel, S.ResourceFailure)), "duplicate observations rejected");
        CheckThrows(() => Evaluate(chest, new ProjectionContributionObservation(K.SpecialModel, S.Resolved, "")), "evidence required");
        CheckThrows(() => Evaluate(chest, new ProjectionContributionObservation(K.SpecialModel, (S)99, "invalid enum")), "invalid state rejected");
        CheckThrows(() => Evaluate(chest with { Requirements = [new((K)99, ProjectionContributionRole.SpecialModel, ProjectionCapabilityDependency.None)] }), "unclassified contribution rejected");
        var invisible = Declare("1.19.2", null, ProjectionRenderPath.Invisible);
        Check(Evaluate(invisible, Obs(K.BlockModel, S.KnownEmpty)).Result == V.Full, "intentional invisible path");
        var fluid = Declare("1.19.2", null, ProjectionRenderPath.Fluid);
        Check(Evaluate(fluid, Obs(K.Fluid, S.StaticLimited)).Result == V.StaticLimited, "existing fluid subset does not imply full world simulation");
    }

    private static void CheckCoverageFailures()
    {
        var v = Registry.Versions["1.21.11"];
        var s = Select(v.Id);
        Check(ProjectionCapabilityCoverage.Check(s, v.Registrations.Values).UncheckedChannels.Count == 2, "unchecked channels visible");
        Check(ProjectionCapabilityCoverage.Check(s, v.Registrations.Values.Append(v.Registrations.Values.First())).Issues.Any(i => i.Code == "DuplicateRegistration"), "duplicate registrations");
        Check(ProjectionCapabilityCoverage.Check(s, v.Registrations.Values, v.BlockEntityTypes!.Append("minecraft:future_machine"), v.ResourceCodecs).Issues.Any(i => i.Code == "UnclassifiedBlockEntityType"), "new no-BER type not silently accepted");
        Check(ProjectionCapabilityCoverage.Check(s, v.Registrations.Values, v.BlockEntityTypes, v.ResourceCodecs!.Append("ItemModels:future_codec")).Issues.Any(i => i.Code == "UnclassifiedResourceCodec"), "unknown codec explicit");
        var old = Registry.Versions["1.12.2"];
        Check(ProjectionCapabilityCoverage.Check(Select(old.Id), old.Registrations.Values, [], []).Issues.Any(i => i.Code == "UnauditedResourceCodec"), "missing codec audit not an empty known set");
        Check(!ProjectionCapabilityCoverage.Check(Registry.SelectVersion("future", ""), []).Passed, "unknown census cannot pass");
        var unknownFamily = v.Registrations.Values.First() with { Family = (ProjectionMechanismFamily)99 };
        Check(ProjectionCapabilityCoverage.Check(s, [unknownFamily]).Issues.Any(i => i.Code == "UnclassifiedMechanism"), "unknown mechanism explicit");
    }

    private static void CheckSyntheticBaking(string root)
    {
        var jar = Path.Combine(root, "p0-synthetic.jar");
        using (var zip = ZipFile.Open(jar, ZipArchiveMode.Create))
        {
            void Write(string path, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }
            Write("assets/minecraft/models/block/p0.json", """
                {"textures":{"all":"minecraft:block/p0"},"elements":[{"from":[0,0,0],"to":[16,16,16],"faces":{
                "north":{"texture":"#all"},"south":{"texture":"#all"},"east":{"texture":"#all"},
                "west":{"texture":"#all"},"up":{"texture":"#all"},"down":{"texture":"#all"}}}]}
                """);
            foreach (var block in new[] { "campfire", "enchanting_table", "oak_sign", "furnace" })
                Write($"assets/minecraft/blockstates/{block}.json", """{"variants":{"":{"model":"minecraft:block/p0"}}}""");
            using var texture = zip.CreateEntry("assets/minecraft/textures/block/p0.png").Open();
            texture.Write(ProjectionPng.EncodeBgra([255, 255, 255, 255], 1, 1));
        }
        using (var resources = new ProjectionResourceSet(jar))
        {
            var baker = new ProjectionModelBaker(resources);
            foreach (var (block, type, version, expected) in new[] {
                ("campfire", "campfire", "1.19.2", V.Partial), ("enchanting_table", "enchanting_table", "1.19.2", V.Partial),
                ("oak_sign", "sign", "26.2", V.Partial), ("furnace", "furnace", "1.21.11", V.Full) })
            {
                var doc = new ProjectionDocument { Name = "P0", SourcePath = "synthetic", Format = ProjectionFormat.SpongeSchematic,
                    Min = new(0, 0, 0), Max = new(0, 0, 0), Palette = [new(block)], Blocks = [new(new(0, 0, 0), 0, null)], Regions = [], Warnings = [] };
                var scene = baker.Build(doc);
                Check(scene.TriangleCount == 12 && !scene.Diagnostics.Any(diagnostic => !diagnostic.Reason.StartsWith("Capability.", StringComparison.Ordinal)), "preserved synthetic base geometry " + block);
                var declaration = Declare(version, type, ProjectionRenderPath.Model);
                Check(Evaluate(declaration, Obs(K.BlockModel, S.Resolved)).Result == expected, "actual JSON cannot mask extra contribution " + block);
                Check(ProjectionStatistics.GroupMaterials(doc).Single().Count == 1 && scene.BlockCount == 1, "source identity unchanged " + block);
            }
        }
        using var exclusive = File.Open(jar, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Check(exclusive.Length > 0, "registry holds no resource handle; baker resource disposal still closes ZIP");
    }

    private static ProjectionVersionSelection Select(string version) => Registry.SelectVersion(version, Registry.Versions[version].ClientSha256);
    private static void CheckAssetIsolation()
    {
        using var stream = typeof(ProjectionDocument).Assembly.GetManifestResourceStream("RZeroF1.Minecraft.Projection.vanilla-capabilities.json")!;
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        Check(System.Text.Encoding.UTF8.GetByteCount(text) < 96 * 1024, "compact bounded factual spec");
        foreach (var marker in new[] { ".linx", "bytecodeOffset", "rawName", "descriptor", "DO NOT SUBMIT", "E:\\", "F:\\", "C:\\" })
            Check(!text.Contains(marker, StringComparison.Ordinal), "no private/raw payload in SDK spec: " + marker);
    }
    private static ProjectionCapabilityDeclaration Declare(string version, string? type, ProjectionRenderPath path = ProjectionRenderPath.Unknown) =>
        Registry.Declare(Select(version), type is null || type.Contains(':') ? type : "minecraft:" + type, path);
    private static ProjectionContributionObservation Obs(K kind, S state) => new(kind, state, "Controlled synthetic observation: " + state);
    private static ProjectionCapabilityAssessment Evaluate(ProjectionCapabilityDeclaration declaration, params ProjectionContributionObservation[] observations) =>
        ProjectionCapabilityEvaluator.Evaluate(declaration, observations);
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException("P0: " + message);
    }
    private static void CheckThrows(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { Check(true, message); return; }
        throw new InvalidOperationException("P0: Expected rejection: " + message);
    }
}
