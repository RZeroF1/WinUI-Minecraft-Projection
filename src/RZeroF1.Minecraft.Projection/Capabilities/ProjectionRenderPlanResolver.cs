using System.Numerics;
using System.Collections.Frozen;
using K = RZeroF1.Minecraft.Projection.ProjectionContributionKind;
using S = RZeroF1.Minecraft.Projection.ProjectionContributionState;
using R = RZeroF1.Minecraft.Projection.ProjectionContributionRole;

namespace RZeroF1.Minecraft.Projection;

internal readonly record struct ProjectionContributionBounds(Vector3 Minimum, Vector3 Maximum);
internal sealed record ProjectionRenderPlan(
    ProjectionSourceBlockKey Source, Guid DocumentIdentity, long ResourceGeneration, ProjectionVisualDataProfile SourceProfile,
    ProjectionContributionBounds Bounds, ProjectionCapabilityDeclaration Declaration,
    ProjectionCapabilityAssessment Assessment, bool UsesPlaceholder, string? DataIssue)
{
    internal long EstimatedCpuBytes => 256L + (Source.Region?.Length ?? 0) * 2L +
        Assessment.Contributions.Sum(contribution => 128L + contribution.Evidence.Length * 2L);
}

internal sealed record ProjectionRenderPlanInput(ProjectionBlock Block, ProjectionState State,
    Guid DocumentIdentity, ProjectionVersionProfile Profile, ProjectionCapabilityDeclaration Declaration,
    ProjectionVisualState Visual, string? DataIssue, bool MayHaveSpecialBody);

internal sealed record ProjectionOrdinaryResult(bool HasGeometry, string? Failure, bool IsFluid = false,
    bool IsInvisible = false, bool IsPreviewFiltered = false, ProjectionContributionBounds? Bounds = null,
    IReadOnlyDictionary<ProjectionContributionKind, ProjectionSpecialExecution>? SpecialContributions = null);

// P1 composes requirements and actual observations. It owns neither resources nor
// geometry. Future producers supply observations only after successful execution.
internal static class ProjectionRenderPlanResolver
{
    private static readonly FrozenSet<string> SpecialBodyTypes = ProjectionRenderCapabilityRegistry.Vanilla.Versions.Values
        .SelectMany(version => version.Registrations.Values.Where(registration => ProjectionCapabilityRecipes
            .Requirements(registration.Recipe, version.Profile.Boards == "BlockModel").Any(requirement => requirement.Role == R.SpecialModel)))
        .Select(registration => registration.TypeId).ToFrozenSet(StringComparer.Ordinal);

    internal static ProjectionRenderPlanInput Describe(ProjectionDocument document, ProjectionBlock block,
        ProjectionState state, ProjectionVersionProfile profile, CancellationToken cancellationToken)
    {
        document.BlockEntities.TryGet(block, out var payload);
        var binding = ProjectionBlockEntityBindings.Bind(state, payload, document.DataVersion);
        var type = ProjectionBlockEntityBindings.TypeForBlock(state.Name) ?? binding.TypeId;
        if (state.Name is "minecraft:player_head" or "minecraft:player_wall_head" &&
            profile.Client.Version?.Id == "1.19.2" && profile.Client.Version.BlockEntityTypes?.Contains("minecraft:skull") == true)
            type = "minecraft:skull";
        if (type == "minecraft:trapped_chest" && profile.Client.Version?.Profile.Routing == "LegacyClass" &&
            ProjectionSpecialModelCompatibility.Select(profile.Client, "ChestRenderer") is not null)
            type = "minecraft:chest";
        // Bed BE routing itself disappeared at the audited BlockModel endpoints.
        // This decision belongs to the resource profile, independently of source NBT.
        if (type == "minecraft:bed" && profile.Client.Version?.Profile.Bed == "BlockModel") type = null;
        if (state.Name == "minecraft:suspicious_sand" && profile.Client.Version?.Registrations.ContainsKey("minecraft:suspicious_sand") == true)
            type = "minecraft:suspicious_sand";
        var path = state.Name is "minecraft:water" or "minecraft:lava" ? ProjectionRenderPath.Fluid :
            IsInvisible(state) ? ProjectionRenderPath.Invisible : ProjectionRenderPath.Model;
        var declaration = ProjectionRenderCapabilityRegistry.Vanilla.Declare(profile.Client, type, path);
        if (binding.Issue is not null || !state.Name.StartsWith("minecraft:", StringComparison.Ordinal) && type is null)
            declaration = declaration with { Status = ProjectionDeclarationStatus.UnknownType };
        var visual = ProjectionVisualStateAdapter.Adapt(state, binding.PayloadTrusted ? payload : null,
            document.DataVersion, cancellationToken);
        var specialBody = declaration.Requirements.Any(requirement => requirement.Role == R.SpecialModel) ||
            declaration.Status != ProjectionDeclarationStatus.Known && type is not null && SpecialBodyTypes.Contains(type);
        return new(block, state, document.Identity, profile, declaration, visual, binding.Issue, specialBody);
    }

    internal static ProjectionRenderPlan Complete(ProjectionRenderPlanInput input, ProjectionOrdinaryResult ordinary)
    {
        var requirements = input.Declaration.Requirements.ToList();
        var observations = new List<ProjectionContributionObservation>();
        var expectedNoModel = !requirements.Any(requirement => requirement.Kind == K.BlockModel) && input.MayHaveSpecialBody;
        var expectedAbsent = expectedNoModel && ordinary.Failure is "MissingBlockstate" or "UnsupportedRenderer" or "EmptyModelUnknownIntent";
        if ((ordinary.HasGeometry || ordinary.Failure is not null && !expectedAbsent) && !ordinary.IsFluid &&
            !requirements.Any(requirement => requirement.Kind == K.BlockModel))
            requirements.Insert(0, new(K.BlockModel, R.OrdinaryModel, ProjectionCapabilityDependency.BlockState));
        if (input.State.Properties.GetValueOrDefault("waterlogged") == "true" && !requirements.Any(requirement => requirement.Kind == K.Fluid))
            requirements.Add(new(K.Fluid, R.AdditionalContent, ProjectionCapabilityDependency.BlockState | ProjectionCapabilityDependency.World));

        foreach (var requirement in requirements)
        {
            var (status, evidence) = GetSpecialObservation(ordinary, requirement) ?? requirement.Kind switch
            {
                K.BlockModel when ordinary.IsInvisible => (S.KnownEmpty, "IntentionalInvisible: confirmed vanilla block"),
                K.BlockModel when ordinary.IsPreviewFiltered => (S.Unsupported, "PreviewFilteredIsolatedGlassPane: ordinary producer skipped by preview filter"),
                K.BlockModel when ordinary.Failure is not null => (ordinary.Failure is "UnsupportedRenderer" or "EmptyModelUnknownIntent" ? S.Unsupported : S.ResourceFailure, ordinary.Failure),
                K.BlockModel when ordinary.HasGeometry => (S.Resolved, "Existing JSON producer validated geometry and textures"),
                K.Fluid when ordinary.IsFluid && ordinary.Failure is not null => (S.ResourceFailure, ordinary.Failure),
                K.Fluid when ordinary.IsFluid => (S.StaticLimited, "Existing static level/adjacency fluid producer; flow/time/world simulation limited"),
                K.Fluid => (S.Unsupported, "WaterloggedFluidUnsupported"),
                K.DebugOverlay when requirement.CanDisableByPolicy => (S.DisabledByPolicy, "Projection preview disables debug overlays"),
                _ => ObserveAdditional(input, requirement)
            };
            observations.Add(new(requirement.Kind, status, evidence));
        }
        var declaration = input.Declaration with { Requirements = requirements.AsReadOnly() };
        var assessment = ProjectionCapabilityEvaluator.Evaluate(declaration, observations);
        var position = input.Block.Position;
        var specialVisible = ordinary.SpecialContributions is not null &&
            ordinary.SpecialContributions.TryGetValue(K.SpecialModel, out var body) &&
            body.State is ProjectionContributionState.Resolved or ProjectionContributionState.StaticLimited;
        var brokenOrdinary = ordinary.Failure is not null && !expectedAbsent;
        return new(new(position, input.Block.Region), input.DocumentIdentity, input.Profile.ResourceGeneration, input.Profile.Source,
            ordinary.Bounds ?? new(new(position.X, position.Y, position.Z), new((float)position.X + 1, (float)position.Y + 1, (float)position.Z + 1)),
            declaration, assessment, !ordinary.HasGeometry && !ordinary.IsFluid && !ordinary.IsInvisible && !ordinary.IsPreviewFiltered && (!specialVisible || brokenOrdinary),
            input.DataIssue);
    }

    private static (S State, string Evidence)? GetSpecialObservation(
        ProjectionOrdinaryResult ordinary, ProjectionContributionRequirement requirement)
    {
        return ordinary.SpecialContributions is not null && ordinary.SpecialContributions.TryGetValue(requirement.Kind, out var execution)
            ? (execution.State, execution.Evidence)
            : null;
    }

    private static (S State, string Evidence) ObserveAdditional(ProjectionRenderPlanInput input, ProjectionContributionRequirement requirement)
    {
        var visual = input.Visual;
        if (requirement.EmptyCondition == "HasBookFalse")
        {
            if (input.State.Properties.GetValueOrDefault("has_book") == "false") return (S.KnownEmpty, "has_book=false");
            if (input.State.Properties.GetValueOrDefault("has_book") != "true") return (S.UnknownData, "MissingHasBookState");
        }
        if (requirement.EmptyCondition == "NonPlayerHead" && input.State.Name is
            "minecraft:skeleton_skull" or "minecraft:skeleton_wall_skull" or "minecraft:wither_skeleton_skull" or "minecraft:wither_skeleton_wall_skull" or
            "minecraft:zombie_head" or "minecraft:zombie_wall_head" or "minecraft:creeper_head" or "minecraft:creeper_wall_head" or
            "minecraft:dragon_head" or "minecraft:dragon_wall_head" or "minecraft:piglin_head" or "minecraft:piglin_wall_head")
            return (S.KnownEmpty, "NonPlayerHead: explicit vanilla head variant");
        if (requirement.EmptyCondition == "NonAnimatedHead" && input.State.Name is
            "minecraft:skeleton_skull" or "minecraft:skeleton_wall_skull" or "minecraft:wither_skeleton_skull" or "minecraft:wither_skeleton_wall_skull" or
            "minecraft:zombie_head" or "minecraft:zombie_wall_head" or "minecraft:creeper_head" or "minecraft:creeper_wall_head" or "minecraft:player_head" or "minecraft:player_wall_head")
            return (S.KnownEmpty, "NonAnimatedHead: explicit vanilla head variant");
        if (requirement.Kind == K.Text)
            return visual.Sign.Status != ProjectionVisualDataStatus.Known ? (S.UnknownData, visual.Sign.Evidence) :
                visual.Sign.Value!.IsEmpty ? (S.KnownEmpty, "Adapted front/back messages explicitly empty") : (S.Unsupported, "Text geometry not produced or provider coverage incomplete");
        if (requirement.Kind == K.ItemContent || requirement.EmptyCondition == "NoDisplayedItems")
            return visual.Items.Status != ProjectionVisualDataStatus.Known ? (S.UnknownData, visual.Items.Evidence) :
                visual.Items.Value!.Count == 0 ? (S.KnownEmpty, "Adapted displayed item list explicitly empty") : (S.Unsupported, "Item view producer not implemented");
        if (requirement.Kind == K.SurfacePatterns)
        {
            if (requirement.EmptyCondition == "UndecoratedSides")
                return visual.PotSides.Status != ProjectionVisualDataStatus.Known ? (S.UnknownData, visual.PotSides.Evidence) :
                    visual.PotSides.Value!.All(side => side == "minecraft:brick") ? (S.KnownEmpty, "Four adapted pot sides explicitly brick") : (S.Unsupported, "Decorated surface producer not implemented");
            return visual.Patterns.Status != ProjectionVisualDataStatus.Known ? (S.UnknownData, visual.Patterns.Evidence) :
                visual.Patterns.Value!.Layers.Count == 0 ? (S.KnownEmpty, "Adapted pattern list explicitly empty") : (S.Unsupported, "Layered surface geometry not produced or a pattern is unresolved");
        }
        if (requirement.Kind == K.Appearance)
        {
            if (requirement.EmptyCondition == "NonPlayerHead" && input.State.Name is
                "minecraft:skeleton_skull" or "minecraft:skeleton_wall_skull" or "minecraft:wither_skeleton_skull" or "minecraft:wither_skeleton_wall_skull" or
                "minecraft:zombie_head" or "minecraft:zombie_wall_head" or "minecraft:creeper_head" or "minecraft:creeper_wall_head" or
                "minecraft:dragon_head" or "minecraft:dragon_wall_head" or "minecraft:piglin_head" or "minecraft:piglin_wall_head")
                return (S.KnownEmpty, "Non-player head does not use profile appearance");
            if (visual.Appearance.Status != ProjectionVisualDataStatus.Known) return (S.UnknownData, visual.Appearance.Evidence);
            return (S.UnknownData, "Appearance geometry did not produce a local, verified skin texture");
        }
        if (requirement.Kind == K.EntityContent && visual.DisplayEntity.Status != ProjectionVisualDataStatus.Known)
            return (S.UnknownData, visual.DisplayEntity.Evidence);
        if (requirement.Kind == K.SpecialModel && input.Profile.Client.Version?.Profile.Bed == "ColorNbt" && input.Declaration.BlockEntityTypeId == "minecraft:bed" &&
            visual.BedColor.Status != ProjectionVisualDataStatus.Known) return (S.UnknownData, visual.BedColor.Evidence);
        if (requirement.Kind is K.Beam or K.WorldEffect) return (S.UnknownData, "Projection external world context unavailable");
        if (requirement.Kind == K.MovedBlock) return (S.UnknownData, "Moved block visual adapter/producer not implemented");
        return (S.Unsupported, "Contribution producer not implemented in P1");
    }

    private static bool IsInvisible(ProjectionState state) => state.IsAir || state.Name is "minecraft:barrier" or "minecraft:light" or "minecraft:structure_void";
}
