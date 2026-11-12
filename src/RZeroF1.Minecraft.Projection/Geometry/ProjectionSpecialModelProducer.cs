using System.Numerics;
using System.Collections.Frozen;

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
        var mechanism = ProjectionSpecialModelCompatibility.Select(input.Profile.Client, input.Declaration.Recipe) ??
            ProjectionP3SpecialModelCompatibility.Select(input.Profile.Client, input.Declaration.Recipe);
        if (mechanism is null)
            return ProjectionSpecialModelResult.Unsupported("SpecialModelMechanismNotAudited");
        if (input.Declaration.Recipe is "ChestRenderer" or "ShulkerBoxRenderer" or "BedRenderer" or
            "LecternRenderer" or "DecoratedPotRenderer" or "CopperGolemStatueBlockRenderer")
        {
            var facing = state.Properties.GetValueOrDefault("facing");
            var valid = facing is "north" or "south" or "east" or "west" ||
                input.Declaration.Recipe == "ShulkerBoxRenderer" && facing is "up" or "down";
            if (!valid) return DataUnknown("MissingOrInvalidFacing");
        }
        if (input.Declaration.Recipe == "BannerRenderer")
        {
            var valid = state.Name.EndsWith("_wall_banner", StringComparison.Ordinal)
                ? state.Properties.GetValueOrDefault("facing") is "north" or "south" or "east" or "west"
                : TryRotation(state.Properties.GetValueOrDefault("rotation"), out _);
            if (!valid) return DataUnknown("MissingOrInvalidBannerOrientation");
        }
        if (input.Declaration.Recipe is "SignRenderer" or "StandingSignRenderer" or "HangingSignRenderer")
        {
            var valid = state.Name.Contains("wall", StringComparison.Ordinal)
                ? state.Properties.GetValueOrDefault("facing") is "north" or "south" or "east" or "west"
                : input.Declaration.Recipe == "HangingSignRenderer" && input.Profile.Source.DataVersion == 3337
                    ? true
                    : TryRotation(state.Properties.GetValueOrDefault("rotation"), out _);
            if (!valid) return DataUnknown("MissingOrInvalidSignOrientation");
        }
        if (input.Declaration.Recipe == "SkullBlockRenderer")
        {
            var valid = state.Name.Contains("wall", StringComparison.Ordinal)
                ? state.Properties.GetValueOrDefault("facing") is "north" or "south" or "east" or "west"
                : TryRotation(state.Properties.GetValueOrDefault("rotation"), out _);
            if (!valid) return DataUnknown("MissingOrInvalidSkullOrientation");
        }
        var result = input.Declaration.Recipe switch
        {
            "ChestRenderer" => Chest(state, mechanism, resources, cancellationToken),
            "ShulkerBoxRenderer" => Shulker(state, resources, cancellationToken),
            "BedRenderer" => Bed(state, input, mechanism, resources, cancellationToken),
            "BellRenderer" => Bell(resources, cancellationToken),
            "EnchantTableRenderer" => Book(state, input, mechanism, resources, cancellationToken),
            "LecternRenderer" when state.Properties.GetValueOrDefault("has_book") == "true" => Book(state, input, mechanism, resources, cancellationToken),
            "LecternRenderer" when state.Properties.GetValueOrDefault("has_book") == "false" => EmptyBook(),
            "LecternRenderer" => DataUnknown("MissingHasBookState"),
            "DecoratedPotRenderer" => Pot(state, input, resources, cancellationToken),
            "BannerRenderer" => Banner(state, input, resources, cancellationToken),
            "SignRenderer" or "StandingSignRenderer" or "HangingSignRenderer" => Sign(state, input, resources, cancellationToken),
            "SkullBlockRenderer" => Skull(state, input, resources, cancellationToken),
            "CopperGolemStatueBlockRenderer" => Statue(state, input, mechanism, resources, cancellationToken),
            _ => ProjectionSpecialModelResult.Unsupported("NoP2SpecialModelRecipe")
        };
        return result with
        {
            Contributions = result.Contributions.ToFrozenDictionary(pair => pair.Key,
                pair => pair.Value.State is ProjectionContributionState.Resolved or ProjectionContributionState.StaticLimited or ProjectionContributionState.KnownEmpty
                    ? pair.Value with { Evidence = "Mechanism=" + mechanism.Id + "; " + pair.Value.Evidence } : pair.Value)
        };
    }

    private static ProjectionSpecialModelResult Chest(ProjectionState state, ProjectionSpecialModelProfile mechanism,
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
        var legacy = mechanism.Geometry == ProjectionSpecialGeometry.ChestDouble128;
        var modern = mechanism.Geometry == ProjectionSpecialGeometry.ChestSplitHiddenSeam;
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
        ProjectionSpecialModelProfile mechanism,
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
        var visibleFaces = mechanism.Geometry == ProjectionSpecialGeometry.EntityBedVisibleFaces;
        var graph = (part, visibleFaces) switch
        {
            ("head", true) => ProjectionPartRecipes.BedHeadVisibleFaces,
            ("foot", true) => ProjectionPartRecipes.BedFootVisibleFaces,
            ("head", false) => ProjectionPartRecipes.BedHead,
            ("foot", false) => ProjectionPartRecipes.BedFoot,
            _ => null
        };
        if (graph is null) return DataUnknown("InvalidBedPart");
        return Result(Bake(graph, material.Value, root, ct),
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
        ProjectionSpecialModelProfile mechanism,
        ProjectionResourceSet resources, CancellationToken ct)
    {
        var modern = mechanism.Resources == ProjectionSpecialResources.BookEnchantment;
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
        var graph = mechanism.Geometry == ProjectionSpecialGeometry.BookPlanar ? ProjectionPartRecipes.BookPlanar : ProjectionPartRecipes.Book;
        return Result(ProjectionSpecialMeshBuilder.Bake(graph, ProjectionPose.Identity, ct,
            new Dictionary<string, ProjectionMaterialRequest> { ["base"] = material.Value }, poses, root),
            "Shared seven-part BookModel; canonical spread/page poses; runtime animation unavailable", true);
    }

    private static ProjectionSpecialModelResult Pot(ProjectionState state, ProjectionRenderPlanInput input,
        ProjectionResourceSet resources, CancellationToken ct)
    {
        var baseMaterial = Texture(resources, "minecraft:entity/decorated_pot/decorated_pot_base", 32, 32, ct);
        var sides = Texture(resources, "minecraft:entity/decorated_pot/decorated_pot_side", 16, 16, ct);
        if (baseMaterial.Failure is not null) return Failure(baseMaterial.Failure);
        if (sides.Failure is not null) return Failure(sides.Failure);
        var root = Matrix4x4.CreateTranslation(-.5f, -.5f, -.5f) *
            Matrix4x4.CreateRotationY(MathF.PI - YRotation(state.Properties.GetValueOrDefault("facing", "north"))) *
            Matrix4x4.CreateTranslation(.5f, .5f, .5f);
        var parts = Bake(ProjectionPartRecipes.PotBase, baseMaterial.Value, root, ct).ToList();
        var sideDetails = input.Visual.PotSideDetails;
        if (sideDetails.Status != ProjectionVisualDataStatus.Known)
        {
            parts.AddRange(ProjectionSpecialMeshBuilder.Bake(ProjectionPartRecipes.PotSides, ProjectionPose.Identity, ct,
                new Dictionary<string, ProjectionMaterialRequest>
                {
                    ["front"] = sides.Value with { Evidence = "Pot front fallback" },
                    ["back"] = sides.Value with { Evidence = "Pot back fallback" },
                    ["left"] = sides.Value with { Evidence = "Pot left fallback" },
                    ["right"] = sides.Value with { Evidence = "Pot right fallback" }
                }, instanceMatrix: root));
            return WithContribution(Result(parts, "Audited pot base; side decorations unavailable", true),
                ProjectionContributionKind.SurfacePatterns, ProjectionContributionState.UnknownData, sideDetails.Evidence);
        }

        var materials = new Dictionary<string, ProjectionMaterialRequest>(StringComparer.Ordinal);
        var visible = new HashSet<string>(sideDetails.Value!.Select(side => side.Side), StringComparer.Ordinal);
        var patternState = ProjectionContributionState.Resolved;
        var evidence = new List<string>();
        foreach (var side in sideDetails.Value!)
        {
            ct.ThrowIfCancellationRequested();
            if (side.IsDefault)
            {
                materials[side.Side] = sides.Value with { Evidence = "Pot default brick side" };
                continue;
            }
            if (side.PatternId is null && side.PatternAssetId is null)
            {
                materials[side.Side] = sides.Value with { Evidence = "Pot side fallback: unknown item pattern" };
                if (patternState == ProjectionContributionState.Resolved)
                    patternState = ProjectionContributionState.UnknownData;
                evidence.Add(side.Side + ":unknown-item-pattern");
                continue;
            }
            var pattern = side.PatternAssetId is { } assetId
                ? assetId.Insert(assetId.IndexOf(':') + 1, "entity/decorated_pot/")
                : resources.ResolvePotPatternTexture(side.PatternId!);
            if (pattern is null)
            {
                if (patternState == ProjectionContributionState.Resolved)
                    patternState = ProjectionContributionState.UnknownData;
                evidence.Add(side.Side + ":unknown-pattern=" + (side.PatternId ?? side.PatternAssetId));
                materials[side.Side] = sides.Value with { Evidence = "Pot side fallback: pattern definition unavailable" };
                continue;
            }
            var sprite = resources.Sprites.Resolve("minecraft:decorated_pot", pattern, 16, 16,
                ProjectionMaterialRole.Cutout, ct);
            if (sprite.Image.Failure is not null)
            {
                patternState = ProjectionContributionState.ResourceFailure;
                evidence.Add(side.Side + ":" + sprite.Image.Failure);
                materials[side.Side] = sides.Value with { Evidence = sprite.Image.Failure };
                continue;
            }
            materials[side.Side] = new(sprite.TextureId, ProjectionMaterialRole.Opaque, 16, 16,
                "Pot pattern " + (side.PatternId ?? side.PatternAssetId), sprite.U0, sprite.V0, sprite.U1, sprite.V1);
            visible.Add(side.Side);
            evidence.Add(side.Side + ":" + (side.PatternId ?? side.PatternAssetId));
        }
        foreach (var side in sideDetails.Value)
            materials.TryAdd(side.Side, sides.Value with { Evidence = "Pot default brick or unresolved-side fallback" });
        parts.AddRange(ProjectionSpecialMeshBuilder.Bake(ProjectionPartRecipes.PotSides, ProjectionPose.Identity, ct,
            materials, instanceMatrix: root, visibleParts: visible));
        var contributionState = patternState == ProjectionContributionState.Resolved && sideDetails.Value.All(side => side.IsDefault)
            ? ProjectionContributionState.KnownEmpty : patternState;
        var result = Result(parts, "Pot body with four independently selected side sprites; wobble omitted", false,
            state: ProjectionContributionState.StaticLimited);
        return WithContribution(result, ProjectionContributionKind.SurfacePatterns, contributionState,
            evidence.Count == 0 ? "Four brick default sides" : string.Join(";", evidence));
    }

    private static ProjectionSpecialModelResult Banner(ProjectionState state, ProjectionRenderPlanInput input,
        ProjectionResourceSet resources, CancellationToken ct)
    {
        var visual = input.Visual.Patterns;
        if (visual.Status != ProjectionVisualDataStatus.Known)
            return WithContribution(ProjectionSpecialModelResult.Unsupported(visual.Evidence),
                ProjectionContributionKind.SurfacePatterns, ProjectionContributionState.UnknownData, visual.Evidence);
        var image = ProjectionSurfaceComposer.ComposeBanner(resources, visual.Value!, ct);
        if (image.Failure is not null)
            return WithContribution(Failure(image.Failure), ProjectionContributionKind.SurfacePatterns,
                ProjectionContributionState.ResourceFailure, image.Failure);
        var wall = state.Name.EndsWith("_wall_banner", StringComparison.Ordinal);
        var yaw = wall ? YRotation(state.Properties.GetValueOrDefault("facing", "north")) :
            BannerRotation(state.Properties.GetValueOrDefault("rotation"));
        if (yaw is null) return DataUnknown("MissingOrInvalidBannerRotation");
        var scale = Matrix4x4.CreateScale(2f / 3f, -2f / 3f, -2f / 3f);
        var root = wall
            ? scale * Matrix4x4.CreateTranslation(0, -.3125f, -.4375f) *
                Matrix4x4.CreateRotationY(-yaw.Value) * Matrix4x4.CreateTranslation(.5f, -.16666667f, .5f)
            : scale * Matrix4x4.CreateRotationY(-yaw.Value) * Matrix4x4.CreateTranslation(.5f, .5f, .5f);
        var graph = wall ? ProjectionPartRecipes.BannerWall : ProjectionPartRecipes.BannerStanding;
        var poles = TextureFirst(resources, ["minecraft:entity/banner_base", "minecraft:entity/banner/banner_base"], 64, 64, ct);
        if (poles.Failure is not null) return Failure(poles.Failure);
        var parts = ProjectionSpecialMeshBuilder.Bake(graph, ProjectionPose.Identity, ct,
            new Dictionary<string, ProjectionMaterialRequest> { ["base"] = poles.Value }, instanceMatrix: root).ToList();
        var bannerMaterial = new ProjectionMaterialRequest(image.TextureId, ProjectionMaterialRole.Cutout, 64, 64,
            "Ordered banner base and pattern layers composed by source-over");
        var surfaceGraph = wall ? ProjectionPartRecipes.BannerWallSurface : ProjectionPartRecipes.BannerStandingSurface;
        parts.AddRange(ProjectionSpecialMeshBuilder.Bake(surfaceGraph, ProjectionPose.Identity, ct,
            new Dictionary<string, ProjectionMaterialRequest> { ["surface"] = bannerMaterial }, instanceMatrix: root));
        var result = Result(parts, "Static vanilla banner pole and two-sided 42x41 patterned flag", false);
        return WithContribution(result, ProjectionContributionKind.SurfacePatterns,
            visual.Value!.Layers.Count == 0 ? ProjectionContributionState.KnownEmpty : ProjectionContributionState.Resolved,
            $"Base={visual.Value.BaseColor}; layers={visual.Value.Layers.Count}; derived={image.TextureId}");
    }

    private static ProjectionSpecialModelResult Sign(ProjectionState state, ProjectionRenderPlanInput input,
        ProjectionResourceSet resources, CancellationToken ct)
    {
        var visual = input.Visual.Sign;
        if (visual.Status != ProjectionVisualDataStatus.Known)
            return WithContribution(ProjectionSpecialModelResult.Unsupported(visual.Evidence), ProjectionContributionKind.Text,
                ProjectionContributionState.UnknownData, visual.Evidence);
        var hanging = state.Name.Contains("hanging_sign", StringComparison.Ordinal);
        var yaw = SignYaw(state, hanging, input.Profile.Source.DataVersion);
        if (yaw is null) return DataUnknown("MissingOrInvalidSignRotation");
        var modernBoard = input.Profile.Client.Version?.Profile.Boards == "BlockModel";
        var parts = new List<ProjectionSpecialPrimitive>();
        if (!modernBoard)
        {
            var board = TextureFirst(resources,
                hanging ? ["minecraft:entity/signs/hanging/" + SignWood(state.Name)] : ["minecraft:entity/signs/" + SignWood(state.Name)],
                64, 32, ct);
            if (board.Failure is not null) return Failure(board.Failure);
            parts.AddRange(Bake(hanging ? ProjectionPartRecipes.HangingSignBoard : ProjectionPartRecipes.StandingSignBoard,
                board.Value, SignModelTransform(yaw.Value), ct));
        }
        // Modern hanging-sign block models already own the board, crossbar and
        // chains. The special producer contributes text only for that route.
        if (state.Name.Contains("wall", StringComparison.Ordinal))
        {
            var wallRotation = state.Properties.GetValueOrDefault("rotation") switch
            {
                "north" => 0f,
                "east" => MathF.PI / 2,
                "south" => MathF.PI,
                "west" => -MathF.PI / 2,
                _ => float.NaN
            };
            if (!float.IsFinite(wallRotation)) return DataUnknown("MissingOrInvalidWallSignRotation");
            yaw = wallRotation;
        }
        var font = new ProjectionFontResolver(resources);
        var faces = visual.Value!.Back is null ? new[] { (visual.Value.Front, false) } :
            new[] { (visual.Value.Front, false), (visual.Value.Back, true) };
        var textResolved = true;
        foreach (var (side, back) in faces)
        {
            if (side is null) continue;
            var selected = side.FilteredMessages ?? side.Messages;
            var messages = selected.Select(value => ProjectionTextComponentReader.Read(value, resources, ct)).ToArray();
            if (hanging && messages.Any(message => !message.FullySupported))
                resources.SpriteWarning("HangingSignTextParseUnsupported:" + string.Join(",", messages.Select(message =>
                    message.FullySupported + "/" + string.Join(";", message.Spans.Select(span => span.Text)))));
            if (messages.Any(message => !message.FullySupported)) textResolved = false;
            var maxWidth = hanging ? 60f : 90f;
            var lineHeight = hanging ? 9f : 10f;
            var scale = hanging ? .0140625f : 1f / 96f;
            var baseColor = SignColor(side.Color, side.Glowing);
            for (var lineIndex = 0; lineIndex < Math.Min(4, messages.Length); lineIndex++)
            {
                var glyphs = LayoutText(messages[lineIndex].Spans, font, maxWidth, ct, ref textResolved);
                var cursor = -glyphs.Sum(glyph => glyph.Advance) / 2;
                // World Y increases upward. Sign line 1 is the upper line, so
                // keep the source order while placing lower indices higher.
                var y = (lineIndex - 1.5f) * lineHeight;
                foreach (var glyph in glyphs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (glyph.Glyph.TextureId is { } textureId && glyph.Glyph.U1 > glyph.Glyph.U0 && glyph.Glyph.V1 > glyph.Glyph.V0)
                    {
                        var texture = resources.LoadTexture(textureId);
                        if (texture.Failure is not null)
                        {
                            if (hanging) resources.SpriteWarning("HangingSignGlyphTextureFailure:" + textureId + ":" + texture.Failure);
                            textResolved = false; cursor += glyph.Advance; continue;
                        }
                        var material = new ProjectionMaterialRequest(textureId,
                            texture.HasTransparency ? ProjectionMaterialRole.Blend : ProjectionMaterialRole.Cutout,
                            1, 1, "Font provider glyph");
                        var glyphRoot = SignTextTransform(hanging, back, yaw.Value);
                        var x0 = cursor * scale; var x1 = (cursor + glyph.Glyph.Width) * scale;
                        (x0, x1) = (-x1, -x0);
                        var glyphTop = y - glyph.Glyph.Top;
                        var y0 = glyphTop * scale; var y1 = (glyphTop + glyph.Glyph.Height) * scale;
                        var z = .0001f;
                        var matrix = glyphRoot * Matrix4x4.CreateTranslation(0, hanging ? -.3125f : 0,
                            hanging ? .073f : .04666667f);
                        var builder = new ProjectionSpecialMeshBuilder(matrix);
                        var screenUv = new Vector2[]
                        {
                            new(back ? glyph.Glyph.U1 : glyph.Glyph.U0, glyph.Glyph.V0),
                            new(back ? glyph.Glyph.U0 : glyph.Glyph.U1, glyph.Glyph.V0),
                            new(back ? glyph.Glyph.U0 : glyph.Glyph.U1, glyph.Glyph.V1),
                            new(back ? glyph.Glyph.U1 : glyph.Glyph.U0, glyph.Glyph.V1)
                        };
                        builder.AddQuadLogical([new(x0, y0, z), new(x1, y0, z), new(x1, y1, z), new(x0, y1, z)],
                            material, screenUv, doubleSided: true, color: baseColor);
                        parts.AddRange(builder.Build());
                    }
                    cursor += glyph.Advance;
                }
            }
        }
        var result = Result(parts, hanging ? "Hanging sign board with static front/back glyph geometry" : "Standing/wall sign board with static front/back glyph geometry", false);
        return WithContribution(result, ProjectionContributionKind.Text,
            textResolved ? (visual.Value.IsEmpty ? ProjectionContributionState.KnownEmpty : ProjectionContributionState.Resolved) : ProjectionContributionState.Unsupported,
            "Font provider layout; selected filtered text; static color/glow; bidi, TTF and outline unavailable");
    }

    private static ProjectionSpecialModelResult Skull(ProjectionState state, ProjectionRenderPlanInput input,
        ProjectionResourceSet resources, CancellationToken ct)
    {
        var wall = state.Name.Contains("wall", StringComparison.Ordinal);
        var type = state.Name.Replace("minecraft:", "", StringComparison.Ordinal).Replace("_wall", "", StringComparison.Ordinal)
            .Replace("_skull", "", StringComparison.Ordinal).Replace("_head", "", StringComparison.Ordinal);
        var appearance = input.Visual.Appearance;
        if (appearance.Status != ProjectionVisualDataStatus.Known && type == "player")
            return WithContribution(ProjectionSpecialModelResult.Unsupported(appearance.Evidence), ProjectionContributionKind.Appearance,
                ProjectionContributionState.UnknownData, appearance.Evidence);
        var textureId = type switch
        {
            "skeleton" => "minecraft:entity/skeleton/skeleton",
            "wither_skeleton" => "minecraft:entity/skeleton/wither_skeleton",
            "zombie" => "minecraft:entity/zombie/zombie",
            "creeper" => "minecraft:entity/creeper/creeper",
            "dragon" => "minecraft:entity/enderdragon/dragon",
            "piglin" => "minecraft:entity/piglin/piglin",
            "player" => null,
            _ => null
        };
        var materials = new List<ProjectionMaterialRequest>();
        if (textureId is not null)
        {
            // Logical model canvas is independent of the resource pack's pixel resolution.
            var width = type == "dragon" ? 256 : 64;
            var height = type == "dragon" ? 256 : type is "skeleton" or "wither_skeleton" or "creeper" ? 32 : 64;
            var texture = Texture(resources, textureId, width, height, ct);
            if (texture.Failure is not null) return Failure(texture.Failure);
            materials.Add(texture.Value);
        }
        else if ((appearance.Value?.TexturePatch?.Texture ??
            (input.Profile.Source.DataVersion == 3120 ? "minecraft:entity/steve" : null)) is { } texturePatch)
        {
            var texture = Texture(resources, texturePatch, 64, 64, ct);
            if (texture.Failure is not null) return WithContribution(Failure(texture.Failure), ProjectionContributionKind.Appearance,
                ProjectionContributionState.ResourceFailure, texture.Failure);
            materials.Add(texture.Value with { Role = ProjectionMaterialRole.Blend });
        }
        else return WithContribution(ProjectionSpecialModelResult.Unsupported("PlayerSkinExternalRequired"),
            ProjectionContributionKind.Appearance, ProjectionContributionState.UnknownData, "PlayerSkinExternalRequired");
        var graph = type switch
        {
            "dragon" => ProjectionPartRecipes.DragonSkull,
            "piglin" => ProjectionPartRecipes.PiglinSkull,
            "player" or "zombie" => ProjectionPartRecipes.HumanoidSkull,
            _ => ProjectionPartRecipes.MobSkull
        };
        var yaw = wall ? ProjectionSpecialModelProducer.YRotation(state.Properties.GetValueOrDefault("facing", "north")) :
            SkullRotation(state.Properties.GetValueOrDefault("rotation"));
        if (yaw is null) return DataUnknown("MissingOrInvalidSkullRotation");
        var signFlip = Matrix4x4.CreateScale(-1, -1, 1);
        var root = wall
            ? signFlip * Matrix4x4.CreateRotationY(MathF.PI - yaw.Value) * Matrix4x4.CreateTranslation(
                .5f - FacingStep(state.Properties.GetValueOrDefault("facing", "north")).X * .25f,
                .25f,
                .5f - FacingStep(state.Properties.GetValueOrDefault("facing", "north")).Z * .25f)
            : signFlip * Matrix4x4.CreateRotationY(-yaw.Value) * Matrix4x4.CreateTranslation(.5f, 0, .5f);
        var parts = ProjectionSpecialMeshBuilder.Bake(graph, ProjectionPose.Identity, ct,
            new Dictionary<string, ProjectionMaterialRequest> { ["base"] = materials[0] }, instanceMatrix: root);
        var result = Result(parts, type == "player" ? "Player skin texture patch; head geometry static" :
            type + " static skull head; canonical animation position=0", type is "dragon" or "piglin");
        if (type != "player")
            result = WithContribution(result, ProjectionContributionKind.Appearance, ProjectionContributionState.KnownEmpty,
                "Fixed vanilla head texture; profile appearance is not required");
        return WithContribution(result, ProjectionContributionKind.Appearance, type == "player" ? ProjectionContributionState.StaticLimited : ProjectionContributionState.KnownEmpty,
            type == "player" ? "Explicit local texture patch used; profile URL bytes require external resolver" : "Vanilla fixed texture and head model; pose static");
    }

    private sealed record LaidGlyph(ProjectionFontGlyph Glyph, float Advance);

    private static List<LaidGlyph> LayoutText(IReadOnlyList<ProjectionTextSpan> spans, ProjectionFontResolver font,
        float maxWidth, CancellationToken ct, ref bool complete)
    {
        var glyphs = new List<LaidGlyph>();
        var width = 0f;
        foreach (var span in spans)
        foreach (var rune in span.Text.EnumerateRunes())
        {
            ct.ThrowIfCancellationRequested();
            var glyph = font.GetGlyph(span.Style.Font, rune.Value, ct) ?? font.GetGlyph("minecraft:default", rune.Value, ct);
            if (glyph is null)
            {
                complete = false;
                font.WarnMissingGlyph(rune.Value);
                glyphs.Add(new(new(null, 0, 0, 0, 0, 0, 0, 4, 0, true), 4));
                continue;
            }
            var advance = glyph.Advance + (span.Style.Bold ? 1 : 0);
            if (width + advance > maxWidth) return glyphs;
            glyphs.Add(new(glyph, advance));
            width += advance;
        }
        return glyphs;
    }

    private static Matrix4x4 SignTextTransform(bool hanging, bool back, float yaw)
    {
        var orientation = back ? Matrix4x4.CreateRotationY(MathF.PI) : Matrix4x4.Identity;
        var modelYaw = Matrix4x4.CreateRotationY(-yaw);
        var origin = Matrix4x4.CreateTranslation(.5f, hanging ? .9375f : .5f, .5f);
        return Matrix4x4.CreateScale(1, -1, -1) * orientation * modelYaw * origin;
    }

    private static Matrix4x4 SignModelTransform(float yaw) =>
        Matrix4x4.CreateScale(2f / 3f, -2f / 3f, -2f / 3f) *
        Matrix4x4.CreateRotationY(yaw) *
        Matrix4x4.CreateTranslation(.5f, .5f, .5f);

    private static float? SignYaw(ProjectionState state, bool hanging, int? sourceDataVersion)
    {
        if (state.Name.Contains("wall", StringComparison.Ordinal))
            return state.Properties.GetValueOrDefault("facing") is { } facing && facing is "north" or "south" or "east" or "west"
                ? ProjectionSpecialModelProducer.YRotation(facing) : null;
        if (hanging)
        {
            var rotation = state.Properties.GetValueOrDefault("rotation");
            if (rotation is null && sourceDataVersion == 3337) return 0;
            return byte.TryParse(rotation, out var turn) && turn < 16 ? turn * MathF.Tau / 16 : null;
        }
        var text = state.Properties.GetValueOrDefault("rotation");
        return byte.TryParse(text, out var value) && value < 16 ? value * MathF.Tau / 16 : null;
    }

    private static float? SkullRotation(string? text) => byte.TryParse(text, out var value) && value < 16
        ? value * MathF.Tau / 16 : null;

    private static Vector3 FacingStep(string facing) => facing switch
    { "north" => -Vector3.UnitZ, "south" => Vector3.UnitZ, "west" => -Vector3.UnitX, "east" => Vector3.UnitX, _ => Vector3.Zero };

    private static float? BannerRotation(string? text) => byte.TryParse(text, out var value) && value < 16
        ? value * MathF.Tau / 16 : null;

    private static bool TryRotation(string? text, out byte value) => byte.TryParse(text, out value) && value < 16;

    private static string SignTexture(string blockId)
    {
        var name = blockId.Replace("minecraft:", "", StringComparison.Ordinal).Replace("_wall_hanging_sign", "", StringComparison.Ordinal)
            .Replace("_hanging_sign", "", StringComparison.Ordinal).Replace("_wall_sign", "", StringComparison.Ordinal)
            .Replace("_sign", "", StringComparison.Ordinal);
        return "minecraft:entity/signs/" + name;
    }

    private static string SignWood(string blockId)
    {
        var name = blockId.Replace("minecraft:", "", StringComparison.Ordinal);
        foreach (var suffix in new[] { "_wall_hanging_sign", "_hanging_sign", "_wall_sign", "_sign" })
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return name[..^suffix.Length];
        return name;
    }

    private static uint SignColor(string dye, bool glowing)
    {
        ReadOnlySpan<uint> colors = [0xfff9fffe, 0xfff9801d, 0xffc74ebd, 0xff3ab3da, 0xfffed83d, 0xff80c71f,
            0xfff38baa, 0xff474f52, 0xff9d9d97, 0xff169c9c, 0xff8932b8, 0xff3c44aa, 0xff835432, 0xff5e7c16,
            0xffb02e26, 0xff1d1d21];
        var names = new[] { "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray",
            "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black" };
        var index = Array.IndexOf(names, dye);
        if (index < 0) return 0xff000000;
        var argb = !glowing ? ScaleSignTextColor(colors[index], .4f) :
            index == 15 ? 0xff6d6d70u : colors[index];
        return (argb & 0xff00ff00) | ((argb & 0xff) << 16) | ((argb >> 16) & 0xff);
    }

    private static uint ScaleSignTextColor(uint argb, float factor)
    {
        var red = (uint)(((argb >> 16) & 0xff) * factor);
        var green = (uint)(((argb >> 8) & 0xff) * factor);
        var blue = (uint)((argb & 0xff) * factor);
        return 0xff000000 | red << 16 | green << 8 | blue;
    }

    private static ProjectionSpecialModelResult WithContribution(ProjectionSpecialModelResult result,
        ProjectionContributionKind kind, ProjectionContributionState state, string evidence)
    {
        var contributions = result.Contributions.ToDictionary(pair => pair.Key, pair => pair.Value);
        contributions[kind] = new(state, evidence);
        return result with { Contributions = contributions.ToFrozenDictionary() };
    }

    private static ProjectionSpecialModelResult Statue(ProjectionState state, ProjectionRenderPlanInput input,
        ProjectionSpecialModelProfile mechanism,
        ProjectionResourceSet resources, CancellationToken ct)
    {
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
        var texturePrefix = mechanism.Resources == ProjectionSpecialResources.CopperGolem64Legacy
            ? oxidation == "unaffected" ? "copper_golem" : oxidation + "_copper_golem"
            : "copper_golem" + suffix;
        var material = Texture(resources, "minecraft:entity/copper_golem/" + texturePrefix, 64, 64, ct);
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

    private static (ProjectionMaterialRequest Value, string? Failure) TextureFirst(ProjectionResourceSet resources,
        IReadOnlyList<string> ids, int width, int height, CancellationToken ct)
    {
        foreach (var id in ids)
        {
            var result = Texture(resources, id, width, height, ct);
            if (result.Failure is null) return result;
        }
        return Texture(resources, ids[0], width, height, ct);
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
