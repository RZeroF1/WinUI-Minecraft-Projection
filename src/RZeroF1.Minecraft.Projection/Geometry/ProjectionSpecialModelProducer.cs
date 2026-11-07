using System.Numerics;

namespace RZeroF1.Minecraft.Projection;

// Local primitives only. Immutable numeric recipes own neither source identities nor resources.
internal static class ProjectionSpecialModelProducer
{
    internal static ProjectionSpecialModelResult TryBuild(ProjectionState state, ProjectionRenderPlanInput input,
        ProjectionResourceSet resources, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Declaration.Status != ProjectionDeclarationStatus.Known)
            return ProjectionSpecialModelResult.Unsupported("UnknownSpecialModelProfile");
        var client = input.Profile.Client.Version!.Id;
        if (client is not ("1.19.2" or "26.2" or "26.3") &&
            !(client == "1.14.4" && input.Declaration.Recipe == "ChestRenderer"))
            return ProjectionSpecialModelResult.Unsupported("P2GeometryEndpointNotVerified");
        if (input.Declaration.Recipe is "ChestRenderer" or "ShulkerBoxRenderer" or "BedRenderer" or
            "LecternRenderer" or "DecoratedPotRenderer" or "CopperGolemStatueBlockRenderer")
        {
            var facing = state.Properties.GetValueOrDefault("facing");
            var valid = facing is "north" or "south" or "east" or "west" ||
                input.Declaration.Recipe == "ShulkerBoxRenderer" && facing is "up" or "down";
            if (!valid) return DataUnknown("MissingOrInvalidFacing");
        }
        return input.Declaration.Recipe switch
        {
            "ChestRenderer" => Chest(state, input, resources, cancellationToken),
            "ShulkerBoxRenderer" => Shulker(state, resources, cancellationToken),
            "BedRenderer" => Bed(state, input, resources, cancellationToken),
            "BellRenderer" => Bell(resources, cancellationToken),
            "EnchantTableRenderer" => Book(state, input, resources, cancellationToken),
            "LecternRenderer" when state.Properties.GetValueOrDefault("has_book") == "true" => Book(state, input, resources, cancellationToken),
            "LecternRenderer" when state.Properties.GetValueOrDefault("has_book") == "false" => EmptyBook(),
            "LecternRenderer" => DataUnknown("MissingHasBookState"),
            "DecoratedPotRenderer" => Pot(state, resources, cancellationToken),
            "CopperGolemStatueBlockRenderer" => Statue(state, input, resources, cancellationToken),
            _ => ProjectionSpecialModelResult.Unsupported("NoP2SpecialModelRecipe")
        };
    }

    private static ProjectionSpecialModelResult Chest(ProjectionState state, ProjectionRenderPlanInput input,
        ProjectionResourceSet resources, CancellationToken ct)
    {
        var family = state.Name switch
        {
            "minecraft:chest" => "normal",
            "minecraft:trapped_chest" => "trapped",
            "minecraft:ender_chest" => "ender",
            _ => null
        };
        if (family is null) return ProjectionSpecialModelResult.Unsupported("ChestMaterialVariantNotInP2");
        var type = family == "ender" ? "single" : state.Properties.GetValueOrDefault("type");
        if (type is not ("single" or "left" or "right")) return DataUnknown("InvalidChestType");
        var layout = input.Profile.Client.Version!.Profile.Chest;
        if (layout == "Legacy") return ProjectionSpecialModelResult.Unsupported("LegacyChestGeometryProfile");
        var legacy = layout == "Double128";
        var modern = input.Profile.Client.Version.Id is "26.1" or "26.1.2" or "26.2" or "26.3" or "26.4-snapshot-6";
        var textureSuffix = type == "single" ? "" : legacy ? "_double" : "_" + type;
        var width = legacy && type != "single" ? 128 : 64;
        var material = Texture(resources, $"minecraft:entity/chest/{family}{textureSuffix}", width, 64, ct);
        if (material.Failure is not null) return Failure(material.Failure);
        var graph = legacy ? LegacyChest(type != "single") : (type, modern) switch
        {
            ("left", true) => ProjectionPartRecipes.ChestLeftModern,
            ("right", true) => ProjectionPartRecipes.ChestRightModern,
            ("single", true) => ProjectionPartRecipes.ChestSingleModern,
            ("left", false) => ProjectionPartRecipes.ChestLeft,
            ("right", false) => ProjectionPartRecipes.ChestRight,
            _ => ProjectionPartRecipes.ChestSingle
        };
        var facing = state.Properties.GetValueOrDefault("facing", "north");
        if (legacy)
        {
            var root = Matrix4x4.CreateScale(1, -1, -1) * Matrix4x4.CreateTranslation(type == "left" ? -1 : 0, 1, 1);
            var parts = Bake(graph, material.Value, root, ct);
            if (type != "single") parts = ProjectionGeometryClip.BetweenX(parts, 0, 1, ct);
            parts = ProjectionGeometryClip.Transform(parts, Facing(facing), ct);
            return Result(parts, "Double128 closed chest; source half clipping preserves continuous UV", true);
        }
        return Result(Bake(graph, material.Value, Facing(facing), ct),
            "Split64 closed chest body/lid/lock; canonical lid progress=0", true);
    }

    private static readonly ProjectionPartGraph LegacySingle = MakeLegacyChest(false);
    private static readonly ProjectionPartGraph LegacyDouble = MakeLegacyChest(true);
    private static ProjectionPartGraph LegacyChest(bool large) => large ? LegacyDouble : LegacySingle;
    private static ProjectionPartGraph MakeLegacyChest(bool large)
    {
        var width = large ? 30 : 14;
        var material = new ProjectionMaterialRequest("base", ProjectionMaterialRole.Cutout, large ? 128 : 64, 64, "Double128 canvas");
        ProjectionCuboid Cube(Vector3 from, Vector3 size, int v) =>
            new(from / 16, (from + size) / 16, material, new(0, v, 0, 0), UvSize: size);
        return ProjectionGeometryBuilder.Graph(large ? "LegacyDoubleChest" : "LegacySingleChest",
            ProjectionGeometryBuilder.Part("bottom", Pose(new(1, 6, 1)), [Cube(Vector3.Zero, new(width, 10, 14), 19)]),
            ProjectionGeometryBuilder.Part("lid", Pose(new(1, 7, 15)), [Cube(new(0, -5, -14), new(width, 5, 14), 0)]),
            ProjectionGeometryBuilder.Part("lock", Pose(new(large ? 16 : 8, 7, 15)), [Cube(new(-1, -2, -15), new(2, 4, 1), 0)]));
    }

    private static ProjectionSpecialModelResult Shulker(ProjectionState state, ProjectionResourceSet resources, CancellationToken ct)
    {
        var color = state.Name["minecraft:".Length..].Replace("_shulker_box", "", StringComparison.Ordinal);
        var id = state.Name == "minecraft:shulker_box" ? "shulker" : "shulker_" + color;
        var material = Texture(resources, "minecraft:entity/shulker/" + id, 64, 64, ct);
        if (material.Failure is not null) return Failure(material.Failure);
        var root = Matrix4x4.CreateTranslation(0, -1, 0) * Matrix4x4.CreateScale(1, -1, -1) *
            SurfaceFacing(state.Properties.GetValueOrDefault("facing", "up")) *
            Matrix4x4.CreateScale(.9995f) * Matrix4x4.CreateTranslation(.5f, .5f, .5f);
        var parts = Bake(ProjectionPartRecipes.Shulker, material.Value, root, ct);
        return Result(parts,
            "Audited two-shell shulker; six attachments; closed canonical progress=0", true);
    }

    private static ProjectionSpecialModelResult Bed(ProjectionState state, ProjectionRenderPlanInput input,
        ProjectionResourceSet resources, CancellationToken ct)
    {
        if (input.Profile.Client.Version!.Profile.Bed == "BlockModel")
            return ProjectionSpecialModelResult.Unsupported("BedUsesOrdinaryBlockModel");
        if (input.Visual.BedColor.Status != ProjectionVisualDataStatus.Known)
            return DataUnknown(input.Visual.BedColor.Evidence);
        var color = input.Visual.BedColor.Value!;
        if (input.Profile.Client.Version.Profile.Bed == "ColorNbt" && color == "light_gray") color = "silver";
        var part = state.Properties.GetValueOrDefault("part");
        if (part is not ("head" or "foot")) return DataUnknown("MissingBedPart");
        var material = Texture(resources, "minecraft:entity/bed/" + color, 64, 64, ct);
        if (material.Failure is not null) return Failure(material.Failure);
        var root = Matrix4x4.CreateTranslation(-.5f, -.5f, -.5f) *
            Matrix4x4.CreateRotationZ(MathF.PI + YRotation(state.Properties.GetValueOrDefault("facing", "north"))) *
            Matrix4x4.CreateTranslation(.5f, .5f, .5f) * Matrix4x4.CreateRotationX(MathF.PI / 2) *
            Matrix4x4.CreateTranslation(0, 9 / 16f, 0);
        return Result(Bake(part == "head" ? ProjectionPartRecipes.BedHead : ProjectionPartRecipes.BedFoot, material.Value, root, ct),
            "Audited entity bed half, mattress and two rotated legs; source color DTO independent of resource profile",
            animation: false, state: ProjectionContributionState.Resolved);
    }

    private static ProjectionSpecialModelResult Bell(ProjectionResourceSet resources, CancellationToken ct)
    {
        var material = Texture(resources, "minecraft:entity/bell/bell_body", 32, 32, ct);
        if (material.Failure is not null) return Failure(material.Failure);
        return Result(Bake(ProjectionPartRecipes.Bell, material.Value, Matrix4x4.Identity, ct),
            "Audited bell_body and child bell_base; swing angles=0; JSON support independent", true);
    }

    private static ProjectionSpecialModelResult Book(ProjectionState state, ProjectionRenderPlanInput input,
        ProjectionResourceSet resources, CancellationToken ct)
    {
        var modern = input.Profile.Client.Version!.Id.StartsWith("26.", StringComparison.Ordinal);
        var material = Texture(resources, modern ? "minecraft:entity/enchantment/enchanting_table_book" : "minecraft:entity/enchanting_table_book", 64, 32, ct);
        if (material.Failure is not null) return Failure(material.Failure);
        // Canonical state: time=0, spread input=1, pageFlip1=.1, pageFlip2=.9.
        const float spread = 1.25f;
        var shift = MathF.Sin(spread);
        var poses = new Dictionary<string, ProjectionPose>
        {
            ["left_lid"] = Pose(new(0, 0, -1), new(0, MathF.PI + spread, 0)),
            ["right_lid"] = Pose(new(0, 0, 1), new(0, -spread, 0)),
            ["left_pages"] = Pose(new(shift, 0, 0), new(0, spread, 0)),
            ["right_pages"] = Pose(new(shift, 0, 0), new(0, -spread, 0)),
            ["flip_page1"] = Pose(new(shift, 0, 0), new(0, spread * .8f, 0)),
            ["flip_page2"] = Pose(new(shift, 0, 0), new(0, -spread * .8f, 0))
        };
        var lectern = input.Declaration.Recipe == "LecternRenderer";
        var angle = lectern ? -YRotation(Clockwise(state.Properties.GetValueOrDefault("facing", "north"))) : 0;
        var root = lectern
            ? Matrix4x4.CreateTranslation(0, -.125f, 0) * Matrix4x4.CreateRotationZ(67.5f * MathF.PI / 180) *
                Matrix4x4.CreateRotationY(angle) * Matrix4x4.CreateTranslation(.5f, 1.0625f, .5f)
            : Matrix4x4.CreateRotationZ(80 * MathF.PI / 180) * Matrix4x4.CreateTranslation(.5f, .85f, .5f);
        return Result(ProjectionSpecialMeshBuilder.Bake(ProjectionPartRecipes.Book, ProjectionPose.Identity, ct,
            new Dictionary<string, ProjectionMaterialRequest> { ["base"] = material.Value }, poses, root),
            "Shared seven-part BookModel; canonical spread/page poses; runtime animation unavailable", true);
    }

    private static ProjectionSpecialModelResult Pot(ProjectionState state, ProjectionResourceSet resources, CancellationToken ct)
    {
        var baseMaterial = Texture(resources, "minecraft:entity/decorated_pot/decorated_pot_base", 32, 32, ct);
        var sides = Texture(resources, "minecraft:entity/decorated_pot/decorated_pot_side", 16, 16, ct);
        if (baseMaterial.Failure is not null) return Failure(baseMaterial.Failure);
        if (sides.Failure is not null) return Failure(sides.Failure);
        var root = Matrix4x4.CreateTranslation(-.5f, -.5f, -.5f) *
            Matrix4x4.CreateRotationY(MathF.PI - YRotation(state.Properties.GetValueOrDefault("facing", "north"))) *
            Matrix4x4.CreateTranslation(.5f, .5f, .5f);
        var parts = Bake(ProjectionPartRecipes.PotBase, baseMaterial.Value, root, ct).ToList();
        parts.AddRange(ProjectionSpecialMeshBuilder.Bake(ProjectionPartRecipes.PotSides, ProjectionPose.Identity, ct,
            new Dictionary<string, ProjectionMaterialRequest>
            {
                ["front"] = sides.Value with { Evidence = "Pot front" },
                ["back"] = sides.Value with { Evidence = "Pot back" },
                ["left"] = sides.Value with { Evidence = "Pot left" },
                ["right"] = sides.Value with { Evidence = "Pot right" }
            }, instanceMatrix: root));
        // P1 observes surface data: explicit brick=KnownEmpty; missing=UnknownData; patterns=P3.
        return Result(parts, "Audited pot neck, top/bottom and four independent side materials; no wobble", true,
            state: ProjectionContributionState.Resolved);
    }

    private static ProjectionSpecialModelResult Statue(ProjectionState state, ProjectionRenderPlanInput input,
        ProjectionResourceSet resources, CancellationToken ct)
    {
        if (input.Profile.Client.Version!.Id is not ("26.1" or "26.1.2" or "26.2" or "26.3" or "26.4-snapshot-6"))
            return ProjectionSpecialModelResult.Unsupported("StatueGeometryProfileNotVerified");
        var pose = state.Properties.GetValueOrDefault("pose");
        var graph = pose switch
        {
            "standing" => ProjectionPartRecipes.StatueStanding,
            "running" => ProjectionPartRecipes.StatueRunning,
            "sitting" => ProjectionPartRecipes.StatueSitting,
            "star" => ProjectionPartRecipes.StatueStar,
            _ => null
        };
        if (graph is null) return DataUnknown("MissingOrUnknownStatuePose");
        var oxidation = state.Properties.GetValueOrDefault("oxidation_level");
        oxidation ??= state.Name.Contains("oxidized", StringComparison.Ordinal) ? "oxidized" :
            state.Name.Contains("weathered", StringComparison.Ordinal) ? "weathered" :
            state.Name.Contains("exposed", StringComparison.Ordinal) ? "exposed" : "unaffected";
        var suffix = oxidation switch { "unaffected" => "", "exposed" => "_exposed", "weathered" => "_weathered", "oxidized" => "_oxidized", _ => null };
        if (suffix is null) return DataUnknown("UnknownStatueOxidation");
        var material = Texture(resources, "minecraft:entity/copper_golem/copper_golem" + suffix, 64, 64, ct);
        if (material.Failure is not null) return Failure(material.Failure);
        var root = Matrix4x4.CreateRotationZ(MathF.PI) *
            Matrix4x4.CreateRotationY(-YRotation(Opposite(state.Properties.GetValueOrDefault("facing", "north")))) *
            Matrix4x4.CreateTranslation(.5f, 0, .5f);
        return Result(Bake(graph, material.Value, root, ct), "Audited static copper statue " + pose + " graph",
            animation: false, state: ProjectionContributionState.Resolved);
    }

    private static IReadOnlyList<ProjectionSpecialPrimitive> Bake(ProjectionPartGraph graph, ProjectionMaterialRequest material,
        Matrix4x4 root, CancellationToken ct) =>
        ProjectionSpecialMeshBuilder.Bake(graph, ProjectionPose.Identity, ct,
            new Dictionary<string, ProjectionMaterialRequest> { ["base"] = material }, instanceMatrix: root);

    private static ProjectionSpecialModelResult Result(IReadOnlyList<ProjectionSpecialPrimitive> parts, string evidence,
        bool animation, ProjectionContributionState state = ProjectionContributionState.StaticLimited)
    {
        if (parts.Count == 0) return ProjectionSpecialModelResult.Unsupported("SpecialModelProducedNoGeometry");
        var observations = new Dictionary<ProjectionContributionKind, ProjectionSpecialExecution>
        { [ProjectionContributionKind.SpecialModel] = new(state, evidence) };
        if (animation) observations[ProjectionContributionKind.Animation] = new(ProjectionContributionState.StaticLimited, "Documented canonical static pose; runtime animation unavailable");
        return new(true, parts, observations, new(
            parts.Select(part => part.Bounds.Minimum).Aggregate(Vector3.Min),
            parts.Select(part => part.Bounds.Maximum).Aggregate(Vector3.Max)));
    }

    private static ProjectionSpecialModelResult EmptyBook() => new(false, [],
        new Dictionary<ProjectionContributionKind, ProjectionSpecialExecution>
        {
            [ProjectionContributionKind.SpecialModel] = new(ProjectionContributionState.KnownEmpty, "has_book=false"),
            [ProjectionContributionKind.Animation] = new(ProjectionContributionState.KnownEmpty, "has_book=false")
        }, new(Vector3.Zero, Vector3.One));
    private static ProjectionSpecialModelResult DataUnknown(string reason) => new(false, [],
        new Dictionary<ProjectionContributionKind, ProjectionSpecialExecution>
        { [ProjectionContributionKind.SpecialModel] = new(ProjectionContributionState.UnknownData, reason) }, new(Vector3.Zero, Vector3.One));
    private static ProjectionSpecialModelResult Failure(string reason) => new(false, [],
        new Dictionary<ProjectionContributionKind, ProjectionSpecialExecution>
        { [ProjectionContributionKind.SpecialModel] = new(ProjectionContributionState.ResourceFailure, reason) }, new(Vector3.Zero, Vector3.One), reason);

    private static (ProjectionMaterialRequest Value, string? Failure) Texture(ProjectionResourceSet resources, string id,
        int width, int height, CancellationToken ct)
    {
        var sprite = resources.Sprites.Resolve(id, width, height, ProjectionMaterialRole.Cutout, ct);
        return (new(sprite.TextureId, ProjectionMaterialRole.Cutout, width, height, sprite.Evidence,
            sprite.U0, sprite.V0, sprite.U1, sprite.V1), sprite.Image.Failure);
    }

    internal static ProjectionPose Pose(Vector3 offset, Vector3 rotation = default) =>
        new(Vector3.Zero, rotation, offset / 16, Vector3.One, false, false, false);
    internal static Matrix4x4 Facing(string facing) =>
        Matrix4x4.CreateTranslation(-.5f, 0, -.5f) * Matrix4x4.CreateRotationY(-YRotation(facing)) * Matrix4x4.CreateTranslation(.5f, 0, .5f);
    internal static float YRotation(string facing) => facing switch
    { "south" => 0, "west" => MathF.PI / 2, "north" => MathF.PI, "east" => 3 * MathF.PI / 2, _ => 0 };
    private static string Clockwise(string facing) => facing switch { "north" => "east", "east" => "south", "south" => "west", _ => "north" };
    private static string Opposite(string facing) => facing switch { "north" => "south", "east" => "west", "south" => "north", _ => "east" };
    internal static Matrix4x4 SurfaceFacing(string facing) => facing switch
    {
        "down" => Matrix4x4.CreateRotationX(MathF.PI),
        "north" => Matrix4x4.CreateRotationX(-MathF.PI / 2),
        "south" => Matrix4x4.CreateRotationX(MathF.PI / 2),
        "west" => Matrix4x4.CreateRotationZ(MathF.PI / 2),
        "east" => Matrix4x4.CreateRotationZ(-MathF.PI / 2),
        _ => Matrix4x4.Identity
    };
}
