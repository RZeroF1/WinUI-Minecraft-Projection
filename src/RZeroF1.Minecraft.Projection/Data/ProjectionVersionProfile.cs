using System.Collections.Frozen;

namespace RZeroF1.Minecraft.Projection;

internal enum ProjectionVisualDataSchema { Unknown, LegacyNbt, TwoSidedText, Components, StructuredText }
internal enum ProjectionSignDataSchema { Unknown, SingleJson, TwoSidedJson, TwoSidedNbt }
internal enum ProjectionItemDataSchema { Unknown, Tag, Components }
internal enum ProjectionBannerDataSchema { Unknown, ShortCodeDamageColor, ShortCodeDyeColor, RegistryLayers }
internal enum ProjectionPotDataSchema { Unknown, Unavailable, Shards, Sherds, NamedItemTemplates }
internal enum ProjectionBedDataSchema { Unknown, ColorNbt, ColorBlockId }
internal enum ProjectionHeadDataSchema { Unknown, SkullOwner, ResolvableProfile, ProfileWithSkinPatch }

internal sealed record ProjectionVisualDataProfile(int? DataVersion, ProjectionVisualDataSchema Schema)
{
    // The aggregate label is retained for existing internal consumers. Individual
    // adapters use their own encoding axis: a text change cannot select a pot codec.
    internal ProjectionSignDataSchema Sign => Schema switch
    {
        ProjectionVisualDataSchema.LegacyNbt => ProjectionSignDataSchema.SingleJson,
        ProjectionVisualDataSchema.TwoSidedText or ProjectionVisualDataSchema.Components => ProjectionSignDataSchema.TwoSidedJson,
        ProjectionVisualDataSchema.StructuredText => ProjectionSignDataSchema.TwoSidedNbt,
        _ => ProjectionSignDataSchema.Unknown
    };
    internal ProjectionItemDataSchema Items => Schema switch
    {
        ProjectionVisualDataSchema.LegacyNbt or ProjectionVisualDataSchema.TwoSidedText => ProjectionItemDataSchema.Tag,
        ProjectionVisualDataSchema.Components or ProjectionVisualDataSchema.StructuredText => ProjectionItemDataSchema.Components,
        _ => ProjectionItemDataSchema.Unknown
    };
    internal ProjectionBannerDataSchema Banner => Schema == ProjectionVisualDataSchema.Unknown
        ? ProjectionBannerDataSchema.Unknown : DataVersion switch
        {
            1139 or 1343 => ProjectionBannerDataSchema.ShortCodeDamageColor,
            3839 or 3953 or 3955 or 4082 or 4189 or 4325 or 4671 or 4786 or 4790 or 4903 or 5023 or 5126 => ProjectionBannerDataSchema.RegistryLayers,
            _ => ProjectionBannerDataSchema.ShortCodeDyeColor
        };
    internal ProjectionPotDataSchema Pot => Schema == ProjectionVisualDataSchema.Unknown
        ? ProjectionPotDataSchema.Unknown : DataVersion switch
        {
            3337 => ProjectionPotDataSchema.Shards,
            3463 or 3839 or 3953 or 3955 or 4082 or 4189 or 4325 or 4671 or 4786 or 4790 or 4903 => ProjectionPotDataSchema.Sherds,
            5023 or 5126 => ProjectionPotDataSchema.NamedItemTemplates,
            _ => ProjectionPotDataSchema.Unavailable
        };
    internal ProjectionBedDataSchema Bed => Schema == ProjectionVisualDataSchema.Unknown
        ? ProjectionBedDataSchema.Unknown : DataVersion is 1139 or 1343 ? ProjectionBedDataSchema.ColorNbt : ProjectionBedDataSchema.ColorBlockId;
    internal ProjectionHeadDataSchema Head => Schema == ProjectionVisualDataSchema.Unknown
        ? ProjectionHeadDataSchema.Unknown : DataVersion switch
        {
            4671 or 4786 or 4790 or 4903 or 5023 or 5126 => ProjectionHeadDataSchema.ProfileWithSkinPatch,
            3839 or 3953 or 3955 or 4082 or 4189 or 4325 => ProjectionHeadDataSchema.ResolvableProfile,
            _ => ProjectionHeadDataSchema.SkullOwner
        };

    // Explicit source endpoints, independently of the selected client. Intermediate
    // snapshots are not silently assigned a schema. This is not a data fixer.
    internal static ProjectionVisualDataProfile Select(int? dataVersion) => new(dataVersion, dataVersion switch
    {
        1139 or 1343 or 1519 or 1631 or 1952 or 1976 or 2225 or 2230 or 2566 or 2567 or 2586 or
        2724 or 2730 or 2860 or 2975 or 3105 or 3120 or 3337 => ProjectionVisualDataSchema.LegacyNbt,
        3463 => ProjectionVisualDataSchema.TwoSidedText,
        3839 or 3953 or 3955 or 4082 or 4189 => ProjectionVisualDataSchema.Components,
        4325 or 4671 or 4786 or 4790 or 4903 or 5023 or 5126 => ProjectionVisualDataSchema.StructuredText,
        _ => ProjectionVisualDataSchema.Unknown
    });
}

internal sealed record ProjectionVersionProfile(
    ProjectionVersionSelection Client, ProjectionVisualDataProfile Source, long ResourceGeneration)
{
    private static readonly FrozenDictionary<string, string> ClientIds = ProjectionRenderCapabilityRegistry.Vanilla
        .Versions.Values.ToFrozenDictionary(version => version.ClientSha256, version => version.Id, StringComparer.OrdinalIgnoreCase);

    internal static ProjectionVersionSelection SelectClient(string actualSha256) =>
        ClientIds.TryGetValue(actualSha256, out var id)
            ? ProjectionRenderCapabilityRegistry.Vanilla.SelectVersion(id, actualSha256)
            : new(ProjectionVersionStatus.UnknownVersion, "UnknownClientIdentity", null);
}
