using System.Collections.Frozen;

namespace RZeroF1.Minecraft.Projection;

internal sealed record ProjectionBlockEntityBinding(string? TypeId, bool PayloadTrusted, string? Issue);

// Explicit vanilla block -> BE type join. Expansions enumerate known vanilla
// variants; resource names, suffix matches and the presence of JSON never route BER.
internal static class ProjectionBlockEntityBindings
{
    private static readonly FrozenDictionary<string, string> Blocks = Build();
    private static readonly FrozenDictionary<string, string> LegacyIds = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Chest"] = "chest", ["EnderChest"] = "ender_chest", ["Trap"] = "dispenser", ["Dropper"] = "dropper",
        ["Furnace"] = "furnace", ["Sign"] = "sign", ["MobSpawner"] = "mob_spawner", ["Music"] = "noteblock",
        ["Piston"] = "piston", ["Cauldron"] = "brewing_stand", ["EnchantTable"] = "enchanting_table",
        ["Airportal"] = "end_portal", ["Control"] = "command_block", ["Beacon"] = "beacon", ["Skull"] = "skull",
        ["DLDetector"] = "daylight_detector", ["Hopper"] = "hopper", ["Comparator"] = "comparator",
        ["Banner"] = "banner", ["Structure"] = "structure_block", ["EndGateway"] = "end_gateway", ["Bed"] = "bed"
    }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static string? TypeForBlock(string blockId) => Blocks.GetValueOrDefault(blockId);

    internal static ProjectionBlockEntityBinding Bind(ProjectionState state, ProjectionBlockEntityData? data, int? sourceDataVersion)
    {
        var expected = TypeForBlock(state.Name);
        if (sourceDataVersion == 3337 && state.Name == "minecraft:suspicious_sand") expected = "minecraft:suspicious_sand";
        if (data is null) return new(expected, false, null);
        var rawId = data.RawTypeId;
        if (string.IsNullOrWhiteSpace(rawId))
            return data.Format == ProjectionFormat.Litematic && expected is not null
                ? new(expected, true, null) // Litematic may omit the redundant ID; the winning source block supplies its type.
                : new(expected, false, "MissingBlockEntityId");
        var type = sourceDataVersion is 1139 or 1343 && LegacyIds.TryGetValue(rawId, out var legacy)
            ? "minecraft:" + legacy : rawId;
        // Old trapped chests used the chest type; the target resource version still
        // selects its own renderer type. Never reinterpret mismatched payload fields.
        var oldTrapped = sourceDataVersion is 1139 or 1343 or 1519 or 1631 && expected == "minecraft:trapped_chest" && type == "minecraft:chest";
        if (expected is null) return new(type, false, "UnknownBlockEntityAssociation");
        if (type != expected && !oldTrapped)
            return new(expected, false, "BlockEntityTypeMismatch");
        return new(expected, true, null);
    }

    private static FrozenDictionary<string, string> Build()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string block, string type) => result.Add("minecraft:" + block, "minecraft:" + type);
        foreach (var type in new[] { "chest", "trapped_chest", "ender_chest", "beacon", "bell", "conduit", "decorated_pot",
            "enchanting_table", "end_gateway", "end_portal", "lectern", "trial_spawner", "vault", "potent_sulfur",
            "barrel", "beehive", "blast_furnace", "brewing_stand", "chiseled_bookshelf", "command_block", "comparator",
            "crafter", "creaking_heart", "daylight_detector", "dispenser", "dropper", "furnace", "hopper", "jigsaw", "jukebox",
            "sculk_catalyst", "sculk_sensor", "sculk_shrieker", "calibrated_sculk_sensor", "smoker", "structure_block", "test_block", "test_instance_block" }) Add(type, type);
        Add("bee_nest", "beehive"); Add("lit_furnace", "furnace"); Add("daylight_detector_inverted", "daylight_detector");
        Add("chain_command_block", "command_block"); Add("repeating_command_block", "command_block");
        Add("unpowered_comparator", "comparator"); Add("powered_comparator", "comparator");
        Add("spawner", "mob_spawner"); Add("mob_spawner", "mob_spawner"); Add("moving_piston", "piston"); Add("piston_extension", "piston");
        Add("campfire", "campfire"); Add("soul_campfire", "campfire");
        Add("suspicious_sand", "brushable_block"); Add("suspicious_gravel", "brushable_block");
        Add("bed", "bed"); Add("banner", "banner"); Add("wall_banner", "banner"); Add("skull", "skull");
        Add("standing_sign", "sign"); Add("wall_sign", "sign"); Add("sign", "sign");
        foreach (var wood in new[] { "oak", "spruce", "birch", "jungle", "acacia", "dark_oak", "mangrove", "cherry", "bamboo", "crimson", "warped", "pale_oak", "poplar" })
        {
            Add(wood + "_sign", "sign"); Add(wood + "_wall_sign", "sign");
            Add(wood + "_hanging_sign", "hanging_sign"); Add(wood + "_wall_hanging_sign", "hanging_sign");
            Add(wood + "_shelf", "shelf");
        }
        foreach (var color in new[] { "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black" })
        {
            Add(color + "_bed", "bed"); Add(color + "_banner", "banner"); Add(color + "_wall_banner", "banner");
            Add(color + "_shulker_box", "shulker_box");
        }
        Add("shulker_box", "shulker_box"); Add("silver_shulker_box", "shulker_box");
        foreach (var head in new[] { "skeleton_skull", "skeleton_wall_skull", "wither_skeleton_skull", "wither_skeleton_wall_skull",
            "zombie_head", "zombie_wall_head", "player_head", "player_wall_head", "creeper_head", "creeper_wall_head", "dragon_head", "dragon_wall_head", "piglin_head", "piglin_wall_head" }) Add(head, "skull");
        foreach (var oxidation in new[] { "", "exposed_", "weathered_", "oxidized_" })
        foreach (var wax in new[] { "", "waxed_" })
        {
            Add(wax + oxidation + "copper_chest", "chest");
            Add(wax + oxidation + "copper_golem_statue", "copper_golem_statue");
        }
        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
