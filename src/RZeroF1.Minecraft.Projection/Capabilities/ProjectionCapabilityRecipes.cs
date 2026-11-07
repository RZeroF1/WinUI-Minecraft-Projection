using D = RZeroF1.Minecraft.Projection.ProjectionCapabilityDependency;
using K = RZeroF1.Minecraft.Projection.ProjectionContributionKind;
using R = RZeroF1.Minecraft.Projection.ProjectionContributionRole;

namespace RZeroF1.Minecraft.Projection;

internal static class ProjectionCapabilityRecipes
{
    internal static IReadOnlyList<ProjectionContributionRequirement> Requirements(string recipe, bool modelBoards, bool bedColorNbt = false)
    {
        ProjectionContributionRequirement Base() => new(K.BlockModel, R.OrdinaryModel, D.BlockState);
        ProjectionContributionRequirement Parts(string? empty = null) => new(K.SpecialModel, R.SpecialModel, D.BlockState | D.PartModel | D.Sprite, empty);
        ProjectionContributionRequirement Animate(string? empty = null) => new(K.Animation, R.RuntimeEffect, D.Time | D.World, empty);
        ProjectionContributionRequirement Content(K kind, D dependencies, string empty) => new(kind, R.AdditionalContent, dependencies, empty);
        ProjectionContributionRequirement[] requirements = recipe switch
        {
            "ChestRenderer" => [Parts() with { Dependencies = D.BlockState | D.World | D.PartModel | D.Sprite }, Animate()],
            "ShulkerBoxRenderer" => [Parts(), Animate()],
            "BedRenderer" => [Parts() with { Dependencies = D.BlockState | D.PartModel | D.Sprite | (bedColorNbt ? D.BlockEntityData : D.None) }],
            "BannerRenderer" => [Parts(), Content(K.SurfacePatterns, D.BlockEntityData | D.Sprite, "NoPatterns"), Animate()],
            "SkullBlockRenderer" => [Parts(), Content(K.Appearance, D.BlockEntityData | D.Appearance, "NonPlayerHead"), Animate("NonAnimatedHead")],
            "SignRenderer" or "StandingSignRenderer" or "HangingSignRenderer" =>
                [modelBoards ? Base() : Parts(), Content(K.Text, D.BlockEntityData | D.Font, "EmptyText")],
            "EnchantTableRenderer" => [Base(), Parts(), Animate()],
            "LecternRenderer" => [Base(), Parts("HasBookFalse"), Animate("HasBookFalse")],
            "BellRenderer" => [Base(), Parts(), Animate()],
            "DecoratedPotRenderer" => [Parts(), Content(K.SurfacePatterns, D.BlockEntityData | D.Sprite, "UndecoratedSides"), Animate()],
            "CopperGolemStatueBlockRenderer" => [Parts()],
            "CampfireRenderer" or "ShelfRenderer" or "SuspiciousSandRenderer" or "BrushableBlockRenderer" =>
                [Base(), Content(K.ItemContent, D.BlockEntityData | D.ItemModels, "NoDisplayedItems")],
            "VaultRenderer" => [Base(), Content(K.ItemContent, D.BlockEntityData | D.ItemModels, "NoDisplayedItems"), Animate("NoDisplayedItems")],
            "SpawnerRenderer" => [Base(), Content(K.EntityContent, D.BlockEntityData | D.EntityModels, "NoDisplayEntity"), Animate("NoDisplayEntity")],
            "TrialSpawnerRenderer" => [Base(), Content(K.EntityContent, D.BlockEntityData | D.EntityModels | D.Time, "NoDisplayEntity"),
                Animate("NoDisplayEntity"), new(K.WorldEffect, R.RuntimeEffect, D.World | D.Time, "InactiveWorldEffect")],
            "PistonHeadRenderer" => [new(K.MovedBlock, R.SpecialModel, D.BlockEntityData | D.BlockState, "NoMovedBlock"), Animate()],
            "BeaconRenderer" => [Base(), new(K.Beam, R.RuntimeEffect, D.World | D.Time | D.Sprite, "InactiveBeam")],
            "TheEndPortalRenderer" => [new(K.PortalSurface, R.SpecialModel, D.World | D.Sprite), Animate()],
            "TheEndGatewayRenderer" => [new(K.PortalSurface, R.SpecialModel, D.World | D.Sprite),
                new(K.Beam, R.RuntimeEffect, D.World | D.Time | D.Sprite, "InactiveBeam"), Animate()],
            "ConduitRenderer" => [Parts(), new(K.WorldEffect, R.RuntimeEffect, D.World | D.Camera | D.Sprite, "InactiveConduit"), Animate()],
            "StructureBlockRenderer" or "BlockEntityWithBoundingBoxRenderer" or "TestInstanceRenderer" =>
                [Base(), new(K.DebugOverlay, R.RuntimeEffect, D.BlockEntityData | D.World, "HiddenBounds", CanDisableByPolicy: true)],
            _ => throw new ArgumentException("Unclassified renderer recipe: " + recipe, nameof(recipe))
        };
        return Array.AsReadOnly(requirements);
    }
}
