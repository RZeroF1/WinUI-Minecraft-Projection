using System.Collections.Frozen;
using System.Text.Json;

namespace RZeroF1.Minecraft.Projection;

internal enum ProjectionSpecialGeometry
{
    ChestDouble128, ChestSplit, ChestSplitHiddenSeam, ShulkerShell,
    EntityBed, EntityBedVisibleFaces, BellBody, BookPlanar, BookThin, PotBase, StatuePoses
}

internal enum ProjectionSpecialResources
{
    ChestDouble128, ChestSplit64, Shulker64, BedColorId, Bell32,
    BookLegacy, BookEnchantment, PotBase32Side16, CopperGolem64Legacy, CopperGolem64Modern
}

internal sealed record ProjectionSpecialModelProfile(
    string Id, ProjectionSpecialGeometry Geometry, ProjectionSpecialResources Resources,
    ProjectionMaterialRole Material, string EvidenceSha256);

// Identity is still selected by the actual client SHA256. These memberships
// describe audited static mechanisms, independently of the coarse P0 profiles.
// They are neither a nearest-version fallback nor a game/host certification.
internal static class ProjectionSpecialModelCompatibility
{
    private static readonly FrozenDictionary<(string Version, string Recipe), ProjectionSpecialModelProfile> Memberships = Load();

    internal static ProjectionSpecialModelProfile? Select(ProjectionVersionSelection client, string? recipe) =>
        client.Status == ProjectionVersionStatus.Verified && client.Version is { } version && recipe is not null
            ? Memberships.GetValueOrDefault((version.Id, recipe)) : null;

    private static FrozenDictionary<(string Version, string Recipe), ProjectionSpecialModelProfile> Load()
    {
        using var stream = typeof(ProjectionSpecialModelCompatibility).Assembly.GetManifestResourceStream(
            "RZeroF1.Minecraft.Projection.p2-compatibility.json")
            ?? throw new InvalidDataException("Missing special model compatibility specification.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 ||
            string.IsNullOrWhiteSpace(root.GetProperty("evidenceId").GetString()))
            throw new InvalidDataException("Unknown special model compatibility specification.");
        var registry = ProjectionRenderCapabilityRegistry.Vanilla;
        var memberships = new Dictionary<(string Version, string Recipe), ProjectionSpecialModelProfile>();
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in root.GetProperty("groups").EnumerateArray())
        {
            var id = group.GetProperty("id").GetString()!;
            var digest = group.GetProperty("evidenceSha256").GetString()!;
            if (string.IsNullOrWhiteSpace(id) || !groupIds.Add(id) || digest.Length != 64 ||
                digest.Any(character => !char.IsAsciiHexDigit(character)) ||
                group.GetProperty("material").GetString() != "StaticCutout" ||
                !Enum.TryParse<ProjectionSpecialGeometry>(group.GetProperty("geometry").GetString(), out var geometry) ||
                !Enum.IsDefined(geometry) ||
                !Enum.TryParse<ProjectionSpecialResources>(group.GetProperty("resources").GetString(), out var resources) ||
                !Enum.IsDefined(resources))
                throw new InvalidDataException("Unclassified special model mechanism: " + id);
            var profile = new ProjectionSpecialModelProfile(id, geometry, resources, ProjectionMaterialRole.Cutout, digest);
            var recipes = group.GetProperty("recipes").EnumerateArray().Select(value => value.GetString()!).ToArray();
            var versions = group.GetProperty("versions").EnumerateArray().Select(value => value.GetString()!).ToArray();
            if (recipes.Length == 0 || versions.Length == 0)
                throw new InvalidDataException("Empty special model compatibility group: " + id);
            foreach (var versionId in versions)
            {
                if (!registry.Versions.TryGetValue(versionId, out var version))
                    throw new InvalidDataException("Unaudited client in special model group: " + versionId);
                foreach (var recipe in recipes)
                {
                    if (!MatchesRecipe(profile, recipe) ||
                        !version.Registrations.Values.Any(registration => registration.Recipe == recipe) ||
                        !memberships.TryAdd((versionId, recipe), profile))
                        throw new InvalidDataException("Invalid or duplicate special model membership: " + versionId + "/" + recipe);
                }
            }
        }
        return memberships.ToFrozenDictionary();
    }

    private static bool MatchesRecipe(ProjectionSpecialModelProfile profile, string recipe) =>
        (recipe, profile.Geometry, profile.Resources) switch
        {
            ("ChestRenderer", ProjectionSpecialGeometry.ChestDouble128, ProjectionSpecialResources.ChestDouble128) => true,
            ("ChestRenderer", ProjectionSpecialGeometry.ChestSplit or ProjectionSpecialGeometry.ChestSplitHiddenSeam,
                ProjectionSpecialResources.ChestSplit64) => true,
            ("ShulkerBoxRenderer", ProjectionSpecialGeometry.ShulkerShell, ProjectionSpecialResources.Shulker64) => true,
            ("BedRenderer", ProjectionSpecialGeometry.EntityBed or ProjectionSpecialGeometry.EntityBedVisibleFaces,
                ProjectionSpecialResources.BedColorId) => true,
            ("BellRenderer", ProjectionSpecialGeometry.BellBody, ProjectionSpecialResources.Bell32) => true,
            ("EnchantTableRenderer" or "LecternRenderer", ProjectionSpecialGeometry.BookPlanar or ProjectionSpecialGeometry.BookThin,
                ProjectionSpecialResources.BookLegacy or ProjectionSpecialResources.BookEnchantment) => true,
            ("DecoratedPotRenderer", ProjectionSpecialGeometry.PotBase, ProjectionSpecialResources.PotBase32Side16) => true,
            ("CopperGolemStatueBlockRenderer", ProjectionSpecialGeometry.StatuePoses,
                ProjectionSpecialResources.CopperGolem64Legacy or ProjectionSpecialResources.CopperGolem64Modern) => true,
            _ => false
        };
}
