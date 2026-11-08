using System.Collections.Frozen;
using System.Text.Json;

namespace RZeroF1.Minecraft.Projection;

internal sealed record ProjectionRenderVersionProfile(
    string Routing, string Resources, string Boards, string Bed, string Chest, string Data, string Material);

internal sealed record ProjectionRendererRegistration(
    string TypeId, string? Provider, string Recipe, ProjectionMechanismFamily Family);

internal sealed record ProjectionVanillaRenderVersion(
    string Id,
    string ClientSha256,
    ProjectionRenderVersionProfile Profile,
    FrozenDictionary<string, ProjectionRendererRegistration> Registrations,
    FrozenSet<string>? BlockEntityTypes,
    FrozenSet<string>? ResourceCodecs,
    string RegistrationEvidenceSha256,
    string? CodecEvidenceSha256,
    string EvidenceKind);

internal sealed record ProjectionVersionSelection(
    ProjectionVersionStatus Status, string RequestedVersion, ProjectionVanillaRenderVersion? Version);

/// <summary>
/// Immutable, exact-identity capability census. Has no file/network/GPU side effects.
/// P0 callers supply the canonical type/render path; no block-name or resource-name guessing.
/// P1 must join source data separately from this resource/client identity.
/// </summary>
internal sealed class ProjectionRenderCapabilityRegistry
{
    private static readonly Lazy<ProjectionRenderCapabilityRegistry> Instance = new(Load);
    internal static ProjectionRenderCapabilityRegistry Vanilla => Instance.Value;
    internal FrozenDictionary<string, ProjectionVanillaRenderVersion> Versions { get; }
    internal FrozenDictionary<string, ProjectionMechanismFamily> Providers { get; }
    internal string EvidenceId { get; }

    private ProjectionRenderCapabilityRegistry(
        string evidenceId,
        FrozenDictionary<string, ProjectionVanillaRenderVersion> versions,
        FrozenDictionary<string, ProjectionMechanismFamily> providers)
    {
        EvidenceId = evidenceId;
        Versions = versions;
        Providers = providers;
    }

    internal ProjectionVersionSelection SelectVersion(string versionId, string clientSha256)
    {
        if (!Versions.TryGetValue(versionId, out var version))
            return new(ProjectionVersionStatus.UnknownVersion, versionId, null);
        if (!string.Equals(version.ClientSha256, clientSha256, StringComparison.OrdinalIgnoreCase))
            return new(ProjectionVersionStatus.IdentityMismatch, versionId, null);
        return new(ProjectionVersionStatus.Verified, versionId, version);
    }

    internal ProjectionCapabilityDeclaration Declare(
        ProjectionVersionSelection selection, string? blockEntityTypeId, ProjectionRenderPath renderPath)
    {
        ProjectionContributionRequirement[] Ordinary() => renderPath switch
        {
            ProjectionRenderPath.Model => [new(ProjectionContributionKind.BlockModel, ProjectionContributionRole.OrdinaryModel, ProjectionCapabilityDependency.BlockState)],
            ProjectionRenderPath.Fluid => [new(ProjectionContributionKind.Fluid, ProjectionContributionRole.OrdinaryModel, ProjectionCapabilityDependency.BlockState | ProjectionCapabilityDependency.World)],
            ProjectionRenderPath.Invisible => [new(ProjectionContributionKind.BlockModel, ProjectionContributionRole.OrdinaryModel, ProjectionCapabilityDependency.None, "IntentionalInvisible")],
            _ => []
        };
        ProjectionCapabilityDeclaration Unclassified(ProjectionDeclarationStatus status) => new(
            status, selection.RequestedVersion, blockEntityTypeId, null, null, Array.AsReadOnly(Ordinary()));

        if (selection.Status != ProjectionVersionStatus.Verified || selection.Version is not { } version)
            return Unclassified(selection.Status == ProjectionVersionStatus.IdentityMismatch
                ? ProjectionDeclarationStatus.IdentityMismatch : ProjectionDeclarationStatus.UnknownVersion);
        if (blockEntityTypeId is not null)
        {
            if (TryGetRegistration(version, blockEntityTypeId, out var registration))
                return new(ProjectionDeclarationStatus.Known, version.Id, blockEntityTypeId, registration.Recipe,
                    registration.Family, ProjectionCapabilityRecipes.Requirements(registration.Recipe,
                        version.Profile.Boards == "BlockModel", version.Profile.Bed == "ColorNbt"));
            if (version.BlockEntityTypes is null || !version.BlockEntityTypes.Contains(blockEntityTypeId))
                return Unclassified(ProjectionDeclarationStatus.UnknownType);
            // A class-key renderer may be inherited by another declared BE type.
            // Unjoined ClassKeyNeedsJoin rows are not evidence of "no BER".
            if (version.Profile.Routing is "Class" or "LegacyClass")
                return Unclassified(ProjectionDeclarationStatus.UnknownRenderPath);
        }
        if (renderPath is ProjectionRenderPath.Unknown or ProjectionRenderPath.LegacyNumeric || !Enum.IsDefined(renderPath))
            return Unclassified(ProjectionDeclarationStatus.UnknownRenderPath);
        if (version.Profile.Resources == "TerrainAtlas" && renderPath == ProjectionRenderPath.Model)
            return Unclassified(ProjectionDeclarationStatus.UnknownRenderPath);
        return Unclassified(ProjectionDeclarationStatus.Known);
    }

    private static bool TryGetRegistration(ProjectionVanillaRenderVersion version, string typeId,
        out ProjectionRendererRegistration registration)
    {
        if (version.Registrations.TryGetValue(typeId, out registration!)) return true;
        // Audited class inheritance: TrappedChestBlockEntity extends ChestBlockEntity;
        // the 1.14.4 dispatcher walks superclasses. This is an inherited lookup,
        // not an extra registration and not a rule for other class-key endpoints.
        if (version.Id == "1.14.4" && typeId == "minecraft:trapped_chest" &&
            version.BlockEntityTypes?.Contains(typeId) == true &&
            version.Registrations.TryGetValue("minecraft:chest", out registration!)) return true;
        // These exact LegacyClass endpoints register one ChestRenderer for normal
        // and trapped chest block states, but their old type census is unavailable.
        // Admit only the numerically audited Double128 endpoints.
        if (typeId == "minecraft:trapped_chest" && version.Id is
            "1.11" or "1.11.2" or "1.12" or "1.12.2" or "1.13" or "1.13.2" or "1.14" &&
            version.Registrations.TryGetValue("minecraft:chest", out registration!)) return true;
        return false;
    }

    private static ProjectionRenderCapabilityRegistry Load()
    {
        using var stream = typeof(ProjectionRenderCapabilityRegistry).Assembly.GetManifestResourceStream(
            "RZeroF1.Minecraft.Projection.vanilla-capabilities.json")
            ?? throw new InvalidDataException("Missing vanilla capability specification.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1)
            throw new InvalidDataException("Unknown capability specification schema.");
        var providers = root.GetProperty("providers").EnumerateObject().ToFrozenDictionary(
            item => item.Name, item => Enum.Parse<ProjectionMechanismFamily>(item.Value.GetString()!), StringComparer.Ordinal);
        if (providers.Values.Any(family => !Enum.IsDefined(family)))
            throw new InvalidDataException("Unclassified capability family.");
        foreach (var provider in providers.Keys)
            _ = ProjectionCapabilityRecipes.Requirements(provider, modelBoards: false);
        var versions = new Dictionary<string, ProjectionVanillaRenderVersion>(StringComparer.Ordinal);
        foreach (var item in root.GetProperty("versions").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var p = item.GetProperty("profile");
            var profile = new ProjectionRenderVersionProfile(
                Text(p, "routing"), Text(p, "resources"), Text(p, "boards"), Text(p, "bed"), Text(p, "chest"), Text(p, "data"), Text(p, "material"));
            ValidateProfile(profile);
            var registrations = new Dictionary<string, ProjectionRendererRegistration>(StringComparer.Ordinal);
            foreach (var row in root.GetProperty("registrations").GetProperty(Text(item, "registrations")).EnumerateArray())
            {
                if (row.GetArrayLength() != 3)
                    throw new InvalidDataException("Invalid capability registration.");
                var type = row[0].GetString()!;
                var provider = row[1].GetString();
                var recipe = row[2].GetString()!;
                if (!providers.TryGetValue(recipe, out var family) || provider is not null && provider != recipe)
                    throw new InvalidDataException("Unclassified capability registration: " + type);
                registrations.Add(type, new(type, provider, recipe, family));
            }
            if (registrations.Count != item.GetProperty("registrationCount").GetInt32())
                throw new InvalidDataException("Incomplete capability registration set: " + id);
            FrozenSet<string>? ReadSet(string table)
            {
                var key = item.GetProperty(table).GetString();
                if (key is null) return null;
                var entries = root.GetProperty(table).GetProperty(key).EnumerateArray().Select(value => value.GetString()!).ToArray();
                var set = entries.ToFrozenSet(StringComparer.Ordinal);
                if (entries.Length != set.Count)
                    throw new InvalidDataException("Duplicate capability declaration: " + table);
                return set;
            }
            var types = ReadSet("types");
            if (types is not null && registrations.Keys.Except(types).Any())
                throw new InvalidDataException("Renderer type missing from type census: " + id);
            foreach (var digest in new[] { Text(item, "clientSha256"), Text(item, "registrationEvidenceSha256") })
                if (digest.Length != 64 || digest.Any(c => !char.IsAsciiHexDigit(c)))
                    throw new InvalidDataException("Invalid capability identity digest: " + id);
            versions.Add(id, new(id, Text(item, "clientSha256"), profile, registrations.ToFrozenDictionary(StringComparer.Ordinal),
                types, ReadSet("codecs"), Text(item, "registrationEvidenceSha256"), item.GetProperty("codecEvidenceSha256").GetString(), Text(item, "evidence")));
        }
        return new(Text(root, "evidenceId"), versions.ToFrozenDictionary(StringComparer.Ordinal), providers);
    }

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new InvalidDataException("Missing capability field: " + name);

    private static void ValidateProfile(ProjectionRenderVersionProfile profile)
    {
        if (profile.Routing is not ("LegacyClass" or "Class" or "Type" or "Bootstrap") ||
            profile.Resources is not ("TerrainAtlas" or "LegacyNames" or "BlockJson") ||
            profile.Boards is not ("BlockModel" or "PartModel") ||
            profile.Bed is not ("BlockModel" or "ColorNbt" or "ColorId") ||
            profile.Chest is not ("Split64" or "Double128" or "Legacy") ||
            profile.Data != "SourceDataVersionRequired" || profile.Material != "AuditedRequirementsOnly")
            throw new InvalidDataException("Unknown capability profile axis; explicit classification required.");
    }
}
