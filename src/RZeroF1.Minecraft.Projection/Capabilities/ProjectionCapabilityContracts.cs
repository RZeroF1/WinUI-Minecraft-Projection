namespace RZeroF1.Minecraft.Projection;

// Internal P0 contracts. They describe required contributions, not a render plan,
// an NBT parser, a renderer implementation, or a public compatibility promise.
internal enum ProjectionMechanismFamily
{
    ContainerParts, EntityBed, LayeredSurface, HeadAppearance, TextAndBoard, Book,
    PivotParts, MultiFaceMaterial, PoseGraph, EmbeddedItem, EmbeddedEntity, MovedBlock,
    Beam, Portal, WorldParts, DebugOverlay
}

internal enum ProjectionCapabilityResult { Full, StaticLimited, Partial, Unsupported }
internal enum ProjectionContributionState
{
    Resolved, StaticLimited, KnownEmpty, UnknownData, ResourceFailure, DisabledByPolicy, Unsupported
}

internal enum ProjectionContributionKind
{
    BlockModel, Fluid, SpecialModel, SurfacePatterns, Appearance, Text, ItemContent,
    EntityContent, MovedBlock, Beam, PortalSurface, WorldEffect, Animation, DebugOverlay
}

internal enum ProjectionContributionRole { OrdinaryModel, SpecialModel, AdditionalContent, RuntimeEffect }
internal enum ProjectionRenderPath { Unknown, Model, Fluid, Invisible, LegacyNumeric }
internal enum ProjectionVersionStatus { Verified, UnknownVersion, IdentityMismatch }
internal enum ProjectionDeclarationStatus { Known, UnknownVersion, IdentityMismatch, UnknownType, UnknownRenderPath }

[Flags]
internal enum ProjectionCapabilityDependency
{
    None = 0, BlockState = 1, BlockEntityData = 2, World = 4, Time = 8,
    Camera = 16, Appearance = 32, ItemModels = 64, EntityModels = 128,
    Font = 256, Sprite = 512, PartModel = 1024
}

internal sealed record ProjectionContributionRequirement(
    ProjectionContributionKind Kind,
    ProjectionContributionRole Role,
    ProjectionCapabilityDependency Dependencies,
    string? EmptyCondition = null,
    bool CanDisableByPolicy = false);

internal sealed record ProjectionCapabilityDeclaration(
    ProjectionDeclarationStatus Status,
    string VersionId,
    string? BlockEntityTypeId,
    string? Recipe,
    ProjectionMechanismFamily? Family,
    IReadOnlyList<ProjectionContributionRequirement> Requirements);

// Empty/policy results require an explicit explanation from the caller. Missing
// payload is UnknownData, never KnownEmpty. No default observation implies success.
internal sealed record ProjectionContributionObservation(
    ProjectionContributionKind Kind, ProjectionContributionState State, string Evidence);

internal sealed record ProjectionContributionAssessment(
    ProjectionContributionRequirement Requirement, ProjectionContributionState State, string Evidence);

internal sealed record ProjectionCapabilityAssessment(
    ProjectionCapabilityResult Result,
    ProjectionDeclarationStatus DeclarationStatus,
    IReadOnlyList<ProjectionContributionAssessment> Contributions);

internal static class ProjectionCapabilityEvaluator
{
    internal static ProjectionCapabilityAssessment Evaluate(
        ProjectionCapabilityDeclaration declaration,
        IEnumerable<ProjectionContributionObservation> observations)
    {
        if (declaration.Requirements.Any(item => !Enum.IsDefined(item.Kind) || !Enum.IsDefined(item.Role)) ||
            declaration.Requirements.Select(item => item.Kind).Distinct().Count() != declaration.Requirements.Count)
            throw new ArgumentException("Unclassified or repeated contribution requirement.", nameof(declaration));
        var observed = observations.ToDictionary(item => item.Kind);
        if (observed.Keys.Except(declaration.Requirements.Select(item => item.Kind)).Any())
            throw new ArgumentException("Observation is not a declared contribution.", nameof(observations));
        var results = new List<ProjectionContributionAssessment>();
        foreach (var requirement in declaration.Requirements)
        {
            if (!observed.TryGetValue(requirement.Kind, out var observation))
                observation = new(requirement.Kind, ProjectionContributionState.Unsupported, "No implementation observation supplied.");
            if (!Enum.IsDefined(observation.State) || string.IsNullOrWhiteSpace(observation.Evidence))
                throw new ArgumentException("Every observation requires a valid state and evidence.", nameof(observations));
            if (observation.State == ProjectionContributionState.KnownEmpty && requirement.EmptyCondition is null)
                throw new ArgumentException("An unconditional contribution cannot be declared empty.", nameof(observations));
            if (observation.State == ProjectionContributionState.DisabledByPolicy && !requirement.CanDisableByPolicy)
                throw new ArgumentException("This contribution cannot be disabled by policy.", nameof(observations));
            results.Add(new(requirement, observation.State, observation.Evidence));
        }

        var complete = declaration.Status == ProjectionDeclarationStatus.Known && results.Count > 0 &&
            results.All(item => item.State is ProjectionContributionState.Resolved or ProjectionContributionState.StaticLimited or
                ProjectionContributionState.KnownEmpty or ProjectionContributionState.DisabledByPolicy);
        var visible = results.Any(item => item.State is ProjectionContributionState.Resolved or ProjectionContributionState.StaticLimited);
        var result = complete
            ? results.Any(item => item.State == ProjectionContributionState.StaticLimited)
                ? ProjectionCapabilityResult.StaticLimited : ProjectionCapabilityResult.Full
            : visible ? ProjectionCapabilityResult.Partial : ProjectionCapabilityResult.Unsupported;
        return new(result, declaration.Status, results.AsReadOnly());
    }
}
