using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace RZeroF1.Minecraft.Projection;

/// <summary>Grouped rendering diagnostics. Positions contains up to 16 source-coordinate samples.</summary>
public sealed record ProjectionRenderDiagnostic(
    string SourcePath, string ClientJarPath, string BlockId, string Blockstate, string? Region,
    string Reason, string? Model, string? Texture, long Count, IReadOnlyList<ProjectionPosition> Positions);

internal sealed partial class ProjectionModelBaker
{
    private static bool IsInvisible(ProjectionState state) => state.IsAir ||
        state.Name is "minecraft:barrier" or "minecraft:light" or "minecraft:structure_void";

    private bool IsPreviewFiltered(ProjectionState state) => _renderOptions.HideIsolatedGlassPanes &&
        state.Name.StartsWith("minecraft:", StringComparison.Ordinal) &&
        (state.Name == "minecraft:glass_pane" || state.Name.EndsWith("_stained_glass_pane", StringComparison.Ordinal)) &&
        new[] { "north", "east", "south", "west" }.All(direction =>
            state.Properties.TryGetValue(direction, out var connection) && connection == "false");

    private void PrepareBlocks(ProjectionDocument document, CancellationToken cancellationToken)
    {
        _resolved.Clear();
        _diagnostics.Clear();
        _opaqueFullCubes.Clear();
        _blendFace = 0;
        _plans.Clear();
        _specialModels.Clear();
        _specialTemplates.Clear();
        _specialTemplateBytes = 0;
        _specialOutputBytes = 0;
        _planBytes = 0;
        if (document.Blocks.Count > MaxRenderPlanBytes / 256)
            throw new InvalidDataException("RenderPlanBudgetExceeded");
        _profile = new(_resources.SelectClient(cancellationToken), ProjectionVisualDataProfile.Select(document.DataVersion), _resources.Generation);
        foreach (var block in document.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = document.Palette[block.StateIndex];
            var planInput = ProjectionRenderPlanResolver.Describe(document, block, state, _profile, cancellationToken);
            var specialKey = state.Key + "|" + (planInput.Profile.Client.Version?.Id ?? planInput.Profile.Client.RequestedVersion) + "|" + planInput.Profile.Source.DataVersion + "|" +
                planInput.Visual.BedColor.Status + ":" + planInput.Visual.BedColor.Value + "|" +
                string.Join(";", planInput.Visual.PotSideDetails.Value?.Select(side => $"{side.Side}:{side.ItemId}:{side.PatternId}:{side.PatternAssetId}:{side.IsDefault}") ?? []) +
                SpecialAppearanceKey(planInput);
            if (!_specialTemplates.TryGetValue(specialKey, out var special))
            {
                special = ProjectionSpecialModelProducer.TryBuild(state, planInput, _resources, cancellationToken);
                var bytes = special.Primitives.Sum(primitive => primitive.Vertices.LongLength * 36 + primitive.Indices.LongLength * 4);
                if (_specialTemplateBytes + bytes <= 16L * 1024 * 1024 && _specialTemplates.Count < 8192)
                {
                    _specialTemplates[specialKey] = special;
                    _specialTemplateBytes += bytes;
                }
            }
            if (special.HasGeometry) _specialModels[block.Position] = special;
            var parts = new List<(ProjectionModelPlacement, ProjectionModelInstance)>();
            _resolved[block.Position] = [];
            if (IsInvisible(state))
            {
                Diagnose(block, state, "IntentionalInvisible");
                CompletePlan(planInput, new(false, null, IsInvisible: true, SpecialContributions: special.Contributions));
                continue;
            }
            if (IsPreviewFiltered(state))
            {
                Diagnose(block, state, "PreviewFilteredIsolatedGlassPane");
                CompletePlan(planInput, new(false, null, IsPreviewFiltered: true, SpecialContributions: special.Contributions));
                continue;
            }
            if (state.Name is "minecraft:water" or "minecraft:lava")
            {
                string? fluidFailure = null;
                foreach (var suffix in new[] { "_still", "_flow" })
                {
                    var texture = state.Name.Replace("minecraft:", "minecraft:block/", StringComparison.Ordinal) + suffix;
                    fluidFailure ??= _resources.LoadTexture(texture).Failure;
                }
                CompletePlan(planInput, new(true, fluidFailure, IsFluid: true, SpecialContributions: special.Contributions));
                continue;
            }
            string? failure = null, failedModel = null, failedTexture = null;
            try
            {
                var json = ReadBlockstate(state.Name);
                var selected = json is { ValueKind: JsonValueKind.Object } root
                    ? ProjectionStateSelection.Select(root, state.Properties, StableSeed(block.Position)).ToArray() : [];
                if (selected.Length == 0) failure = json is null ? _blockstateFailures.GetValueOrDefault(state.Name, "MissingBlockstate") : "NoMatchingVariant";
                foreach (var placement in selected)
                {
                    failedModel = placement.Model;
                    var model = _resolver.Resolve(placement.Model);
                    if (model.Elements.Count == 0)
                    {
                        failure = planInput.MayHaveSpecialBody ? "UnsupportedRenderer" : "EmptyModelUnknownIntent";
                        break;
                    }
                    if (!model.Elements.Any(element => element.Faces.Keys.Any(direction =>
                    {
                        var corners = CreateCorners(element.From / 16, element.To / 16);
                        TransformCorners(corners, element.Rotation, Matrix4x4.Identity);
                        var indices = ProjectionModelGeometry.FaceVertexIndices[direction];
                        var area = Vector3.Cross(corners[indices[1]] - corners[indices[0]], corners[indices[2]] - corners[indices[0]]).LengthSquared();
                        return float.IsFinite(area) && area >= 1e-12f;
                    })))
                    {
                        failure = "InvalidModelGeometry";
                        break;
                    }
                    foreach (var element in model.Elements)
                    foreach (var face in element.Faces.Values)
                    {
                        var texture = ResolveTexture(model, face.Texture);
                        var image = _resources.LoadTexture(texture);
                        if (texture == "__missing__" || image.Failure is not null)
                        {
                            failure = image.Failure ?? "UnresolvedTextureReference";
                            failedTexture = texture;
                            break;
                        }
                    }
                    if (failure is not null) break;
                    parts.Add((placement, model));
                }
            }
            catch (KeyNotFoundException) { failure = "MissingModelOrParent"; }
            catch (JsonException) { failure = "InvalidBlockstate"; }
            catch (InvalidOperationException) { failure = "InvalidModel"; }
            if (failure is not null)
            {
                if (failure == "MissingBlockstate" && planInput.MayHaveSpecialBody &&
                    !planInput.Declaration.Requirements.Any(requirement => requirement.Kind == ProjectionContributionKind.BlockModel))
                    failure = "UnsupportedRenderer";
                if (!special.HasGeometry || failure is not ("UnsupportedRenderer" or "EmptyModelUnknownIntent" or "MissingBlockstate"))
                    Diagnose(block, state, failure, failedModel, failedTexture);
                CompletePlan(planInput, new(false, failure, Bounds: WorldBounds(special.Bounds, block.Position), SpecialContributions: special.Contributions));
                var expectedAbsent = failure is "UnsupportedRenderer" or "EmptyModelUnknownIntent" or "MissingBlockstate";
                _resolved[block.Position] = special.HasGeometry && expectedAbsent ? [] : [(new("__missing__", 0, 0, false), MissingModel())];
                continue;
            }
            _resolved[block.Position] = parts.ToArray();
            var boundsMin = new Vector3(float.PositiveInfinity);
            var boundsMax = new Vector3(float.NegativeInfinity);
            foreach (var (placement, model) in parts)
            foreach (var element in model.Elements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rotation = Matrix4x4.CreateRotationX(-placement.RotationX * MathF.PI / 180f) *
                    Matrix4x4.CreateRotationY(-placement.RotationY * MathF.PI / 180f);
                var corners = CreateCorners(element.From / 16f, element.To / 16f);
                TransformCorners(corners, element.Rotation, rotation);
                foreach (var corner in corners)
                {
                    var world = corner + new Vector3(block.Position.X, block.Position.Y, block.Position.Z);
                    boundsMin = Vector3.Min(boundsMin, world); boundsMax = Vector3.Max(boundsMax, world);
                }
            }
            var ordinaryBounds = new ProjectionContributionBounds(boundsMin, boundsMax);
            var specialWorldBounds = WorldBounds(special.Bounds, block.Position);
            var combinedBounds = special.HasGeometry
                ? new ProjectionContributionBounds(Vector3.Min(ordinaryBounds.Minimum, specialWorldBounds.Minimum),
                    Vector3.Max(ordinaryBounds.Maximum, specialWorldBounds.Maximum))
                : ordinaryBounds;
            CompletePlan(planInput, new(parts.Count > 0, null, Bounds: combinedBounds, SpecialContributions: special.Contributions));
            if (parts.Count == 1 && IsFullCube(parts[0].Item2) && parts[0].Item2.Elements[0].Faces.Values.All(face =>
                RenderMode(state.Name, ResolveTexture(parts[0].Item2, face.Texture)) == 0 &&
                !_resources.LoadTexture(ResolveTexture(parts[0].Item2, face.Texture)).HasTransparency))
                _opaqueFullCubes.Add(block.Position);
            if (state.Properties.TryGetValue("waterlogged", out var waterlogged) && waterlogged == "true")
                Diagnose(block, state, "WaterloggedFluidUnsupported");
        }
    }

    private static string SpecialAppearanceKey(ProjectionRenderPlanInput input)
    {
        // The template cache is per document/resource generation, but two
        // instances of the same state may carry different visible BE contents.
        object? value = input.Declaration.Recipe switch
        {
            // An enumerable DTO serializes as its layers; retain the base color
            // and field status explicitly in the appearance cache identity.
            "BannerRenderer" => new
            {
                input.Visual.Patterns.Status,
                input.Visual.Patterns.Evidence,
                BaseColor = input.Visual.Patterns.Value?.BaseColor,
                Layers = input.Visual.Patterns.Value?.Layers
            },
            "SignRenderer" or "StandingSignRenderer" or "HangingSignRenderer" => input.Visual.Sign,
            "SkullBlockRenderer" => input.Visual.Appearance,
            _ => null
        };
        return value is null ? "" : "|" + Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(value, value.GetType())));
    }

    private static ProjectionContributionBounds WorldBounds(ProjectionContributionBounds bounds, ProjectionPosition position) =>
        new(bounds.Minimum + new Vector3(position.X, position.Y, position.Z),
            bounds.Maximum + new Vector3(position.X, position.Y, position.Z));

    private void CompletePlan(ProjectionRenderPlanInput input, ProjectionOrdinaryResult ordinary)
    {
        var plan = ProjectionRenderPlanResolver.Complete(input, ordinary);
        _planBytes = checked(_planBytes + plan.EstimatedCpuBytes);
        if (_planBytes > MaxRenderPlanBytes) throw new InvalidDataException("RenderPlanBudgetExceeded");
        _plans.Add(plan);
        if (plan.DataIssue is { } dataIssue) Diagnose(input.Block, input.State, dataIssue);
        if (ordinary.IsInvisible || ordinary.IsPreviewFiltered) return;
        if (plan.Declaration.Status != ProjectionDeclarationStatus.Known)
            Diagnose(input.Block, input.State, $"Capability.{plan.Assessment.Result}.{plan.Declaration.Status}");
        foreach (var contribution in plan.Assessment.Contributions)
            if (contribution.Requirement.Kind != ProjectionContributionKind.BlockModel &&
                !(contribution.Requirement.Kind == ProjectionContributionKind.SurfacePatterns &&
                    contribution.State is ProjectionContributionState.Resolved or ProjectionContributionState.KnownEmpty) && contribution.State is
                ProjectionContributionState.Unsupported or ProjectionContributionState.UnknownData or ProjectionContributionState.ResourceFailure or ProjectionContributionState.StaticLimited)
                Diagnose(input.Block, input.State, $"Capability.{plan.Assessment.Result}.{contribution.Requirement.Kind}.{contribution.State}");
    }

    private static ProjectionFaceDirection RotateDirection(ProjectionFaceDirection direction, Matrix4x4 rotation)
    {
        var vector = direction switch
        {
            ProjectionFaceDirection.North => -Vector3.UnitZ, ProjectionFaceDirection.South => Vector3.UnitZ,
            ProjectionFaceDirection.East => Vector3.UnitX, ProjectionFaceDirection.West => -Vector3.UnitX,
            ProjectionFaceDirection.Up => Vector3.UnitY, _ => -Vector3.UnitY
        };
        vector = Vector3.TransformNormal(vector, rotation);
        return MathF.Abs(vector.Y) > .9f ? vector.Y > 0 ? ProjectionFaceDirection.Up : ProjectionFaceDirection.Down :
            MathF.Abs(vector.X) > .9f ? vector.X > 0 ? ProjectionFaceDirection.East : ProjectionFaceDirection.West :
            vector.Z > 0 ? ProjectionFaceDirection.South : ProjectionFaceDirection.North;
    }

    private void Diagnose(ProjectionBlock block, ProjectionState state, string code, string? model = null, string? texture = null)
    {
        var key = (state.Key, code, model, texture, block.Region);
        if (!_diagnostics.TryGetValue(key, out var diagnostic))
        {
            diagnostic = new DiagnosticBuilder(state, block.Region, code, model, texture);
            _diagnostics.Add(key, diagnostic);
            if (code is not "IntentionalInvisible" and not "PreviewFilteredIsolatedGlassPane")
                AddWarning($"{code}: {state.Key} @ ({block.Position.X},{block.Position.Y},{block.Position.Z}); model={model}; texture={texture}");
        }
        diagnostic.Count++;
        if (diagnostic.Positions.Count < 16) diagnostic.Positions.Add(block.Position);
    }

    private sealed class DiagnosticBuilder(ProjectionState state, string? region, string code, string? model, string? texture)
    {
        public long Count;
        public List<ProjectionPosition> Positions { get; } = [];
        public ProjectionRenderDiagnostic ToResult(string path, string jar) =>
            new(path, jar, state.Name, state.Key, region, code, model, texture, Count, Array.AsReadOnly(Positions.ToArray()));
    }
}
