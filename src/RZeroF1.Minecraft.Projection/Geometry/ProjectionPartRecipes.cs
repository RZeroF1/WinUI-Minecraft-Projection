using System.Numerics;

namespace RZeroF1.Minecraft.Projection;

// Numeric part dimensions, texture canvases and pivots audited at the selected client endpoints.
// Immutable templates contain logical material slots; resources and instance poses are bound at bake time.
internal static class ProjectionPartRecipes
{
    private static ProjectionPartGraph VisibleBedFaces(ProjectionPartGraph graph, bool head)
    {
        ProjectionPartNode Map(ProjectionPartNode node) => node with
        {
            Cuboids = node.Cuboids.Select(cube => cube with
            {
                Faces = Enum.GetValues<ProjectionFaceDirection>().Where(face =>
                    face != (node.Name == "main" && head ? ProjectionFaceDirection.Up : ProjectionFaceDirection.Down)).ToHashSet()
            }).ToArray(),
            Children = node.Children.Select(Map).ToArray()
        };
        return new(graph.Name + "VisibleFaces", Map(graph.Root));
    }

    internal static readonly ProjectionPartGraph ChestSingle = new("ChestSingle", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("bottom", new(0, 0, 0), new(0, 0, 0),
            [C(new(1, 0, 1), new(14, 10, 14), new(0, 19), "base", 64, 64, 0, false)],
            []),
P("lid", new(0, 9, 1), new(0, 0, 0),
            [C(new(1, 0, 0), new(14, 5, 14), new(0, 0), "base", 64, 64, 0, false)],
            []),
P("lock", new(0, 8, 0), new(0, 0, 0),
            [C(new(7, -1, 15), new(2, 4, 1), new(0, 0), "base", 64, 64, 0, false)],
            [])]));
    internal static readonly ProjectionPartGraph ChestRight = new("ChestRight", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("bottom", new(0, 0, 0), new(0, 0, 0),
            [C(new(1, 0, 1), new(15, 10, 14), new(0, 19), "base", 64, 64, 0, false)],
            []),
P("lid", new(0, 9, 1), new(0, 0, 0),
            [C(new(1, 0, 0), new(15, 5, 14), new(0, 0), "base", 64, 64, 0, false)],
            []),
P("lock", new(0, 8, 0), new(0, 0, 0),
            [C(new(15, -1, 15), new(1, 4, 1), new(0, 0), "base", 64, 64, 0, false)],
            [])]));
    internal static readonly ProjectionPartGraph ChestLeft = new("ChestLeft", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("bottom", new(0, 0, 0), new(0, 0, 0),
            [C(new(0, 0, 1), new(15, 10, 14), new(0, 19), "base", 64, 64, 0, false)],
            []),
P("lid", new(0, 9, 1), new(0, 0, 0),
            [C(new(0, 0, 0), new(15, 5, 14), new(0, 0), "base", 64, 64, 0, false)],
            []),
P("lock", new(0, 8, 0), new(0, 0, 0),
            [C(new(0, -1, 15), new(1, 4, 1), new(0, 0), "base", 64, 64, 0, false)],
            [])]));
    internal static readonly ProjectionPartGraph ChestSingleModern = new("ChestSingleModern", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("bottom", new(0, 0, 0), new(0, 0, 0),
            [C(new(1, 0, 1), new(14, 10, 14), new(0, 19), "base", 64, 64, 0, false)],
            []),
P("lid", new(0, 9, 1), new(0, 0, 0),
            [C(new(1, 0, 0), new(14, 5, 14), new(0, 0), "base", 64, 64, 0, false)],
            []),
P("lock", new(0, 9, 1), new(0, 0, 0),
            [C(new(7, -2, 14), new(2, 4, 1), new(0, 0), "base", 64, 64, 0, false)],
            [])]));
    internal static readonly ProjectionPartGraph ChestRightModern = new("ChestRightModern", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("bottom", new(0, 0, 0), new(0, 0, 0),
            [C(new(1, 0, 1), new(15, 10, 14), new(0, 19), "base", 64, 64, 0, false, [ProjectionFaceDirection.Down, ProjectionFaceDirection.Up, ProjectionFaceDirection.West, ProjectionFaceDirection.North, ProjectionFaceDirection.South])],
            []),
P("lid", new(0, 9, 1), new(0, 0, 0),
            [C(new(1, 0, 0), new(15, 5, 14), new(0, 0), "base", 64, 64, 0, false, [ProjectionFaceDirection.Down, ProjectionFaceDirection.Up, ProjectionFaceDirection.West, ProjectionFaceDirection.North, ProjectionFaceDirection.South])],
            []),
P("lock", new(0, 9, 1), new(0, 0, 0),
            [C(new(15, -2, 14), new(1, 4, 1), new(0, 0), "base", 64, 64, 0, false, [ProjectionFaceDirection.Down, ProjectionFaceDirection.Up, ProjectionFaceDirection.West, ProjectionFaceDirection.North, ProjectionFaceDirection.South])],
            [])]));
    internal static readonly ProjectionPartGraph ChestLeftModern = new("ChestLeftModern", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("bottom", new(0, 0, 0), new(0, 0, 0),
            [C(new(0, 0, 1), new(15, 10, 14), new(0, 19), "base", 64, 64, 0, false, [ProjectionFaceDirection.Down, ProjectionFaceDirection.Up, ProjectionFaceDirection.North, ProjectionFaceDirection.East, ProjectionFaceDirection.South])],
            []),
P("lid", new(0, 9, 1), new(0, 0, 0),
            [C(new(0, 0, 0), new(15, 5, 14), new(0, 0), "base", 64, 64, 0, false, [ProjectionFaceDirection.Down, ProjectionFaceDirection.Up, ProjectionFaceDirection.North, ProjectionFaceDirection.East, ProjectionFaceDirection.South])],
            []),
P("lock", new(0, 9, 1), new(0, 0, 0),
            [C(new(0, -2, 14), new(1, 4, 1), new(0, 0), "base", 64, 64, 0, false, [ProjectionFaceDirection.Down, ProjectionFaceDirection.Up, ProjectionFaceDirection.North, ProjectionFaceDirection.East, ProjectionFaceDirection.South])],
            [])]));
    internal static readonly ProjectionPartGraph BedHead = new("BedHead", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("main", new(0, 0, 0), new(0, 0, 0),
            [C(new(0, 0, 0), new(16, 16, 6), new(0, 0), "base", 64, 64, 0, false)],
            []),
P("left_leg", new(0, 0, 0), new(1.570796f, 0, 1.570796f),
            [C(new(0, 6, 0), new(3, 3, 3), new(50, 6), "base", 64, 64, 0, false)],
            []),
P("right_leg", new(0, 0, 0), new(1.570796f, 0, 3.141593f),
            [C(new(-16, 6, 0), new(3, 3, 3), new(50, 18), "base", 64, 64, 0, false)],
            [])]));
    internal static readonly ProjectionPartGraph BedFoot = new("BedFoot", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("main", new(0, 0, 0), new(0, 0, 0),
            [C(new(0, 0, 0), new(16, 16, 6), new(0, 22), "base", 64, 64, 0, false)],
            []),
P("left_leg", new(0, 0, 0), new(1.570796f, 0, 0),
            [C(new(0, 6, -16), new(3, 3, 3), new(50, 0), "base", 64, 64, 0, false)],
            []),
P("right_leg", new(0, 0, 0), new(1.570796f, 0, 4.712389f),
            [C(new(-16, 6, -16), new(3, 3, 3), new(50, 12), "base", 64, 64, 0, false)],
            [])]));
    internal static readonly ProjectionPartGraph Book = new("Book", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("left_lid", new(0, 0, -1), new(0, 0, 0),
            [C(new(-6, -5, -0.005f), new(6, 10, 0.005f), new(0, 0), "base", 64, 32, 0, false)],
            []),
P("right_lid", new(0, 0, 1), new(0, 0, 0),
            [C(new(0, -5, -0.005f), new(6, 10, 0.005f), new(16, 0), "base", 64, 32, 0, false)],
            []),
P("seam", new(0, 0, 0), new(0, 1.570796f, 0),
            [C(new(-1, -5, 0), new(2, 10, 0.005f), new(12, 0), "base", 64, 32, 0, false)],
            []),
P("left_pages", new(0, 0, 0), new(0, 0, 0),
            [C(new(0, -4, -0.99f), new(5, 8, 1), new(0, 10), "base", 64, 32, 0, false)],
            []),
P("right_pages", new(0, 0, 0), new(0, 0, 0),
            [C(new(0, -4, -0.01f), new(5, 8, 1), new(12, 10), "base", 64, 32, 0, false)],
            []),
P("flip_page1", new(0, 0, 0), new(0, 0, 0),
            [C(new(0, -4, 0), new(5, 8, 0.005f), new(24, 10), "base", 64, 32, 0, false)],
            []),
P("flip_page2", new(0, 0, 0), new(0, 0, 0),
            [C(new(0, -4, 0), new(5, 8, 0.005f), new(24, 10), "base", 64, 32, 0, false)],
            [])]));
    internal static readonly ProjectionPartGraph Shulker = new("Shulker", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("lid", new(0, 24, 0), new(0, 0, 0),
            [C(new(-8, -16, -8), new(16, 12, 16), new(0, 0), "base", 64, 64, 0, false)],
            []),
P("base", new(0, 24, 0), new(0, 0, 0),
            [C(new(-8, -8, -8), new(16, 8, 16), new(0, 28), "base", 64, 64, 0, false)],
            [])]));
    internal static readonly ProjectionPartGraph Bell = new("Bell", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("bell_body", new(8, 12, 8), new(0, 0, 0),
            [C(new(-3, -6, -3), new(6, 7, 6), new(0, 0), "base", 32, 32, 0, false)],
            [P("bell_base", new(-8, -12, -8), new(0, 0, 0),
                [C(new(4, 4, 4), new(8, 2, 8), new(0, 13), "base", 32, 32, 0, false)],
                [])])]));
    internal static readonly ProjectionPartGraph PotBase = new("PotBase", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("neck", new(0, 37, 16), new(3.141593f, 0, 0),
            [C(new(4, 17, 4), new(8, 3, 8), new(0, 0), "base", 32, 32, -0.1f, false),
C(new(5, 20, 5), new(6, 1, 6), new(0, 5), "base", 32, 32, 0.2f, false)],
            []),
P("top", new(1, 16, 1), new(0, 0, 0),
            [C(new(0, 0, 0), new(14, 0, 14), new(-14, 13), "base", 32, 32, 0, false)],
            []),
P("bottom", new(1, 0, 1), new(0, 0, 0),
            [C(new(0, 0, 0), new(14, 0, 14), new(-14, 13), "base", 32, 32, 0, false)],
            [])]));
    internal static readonly ProjectionPartGraph BannerStanding = BannerStructure("BannerStanding", true);
    internal static readonly ProjectionPartGraph BannerWall = BannerStructure("BannerWall", false);
    // BannerRenderer moves the flag pivot to y=-32 before drawing it.
    // Keep the cloth and support in the same model coordinate system.
    internal static readonly ProjectionPartGraph BannerStandingSurface = BannerSurface("BannerStandingSurface", -10, -32, -2, 20, 40);
    internal static readonly ProjectionPartGraph BannerWallSurface = BannerSurface("BannerWallSurface", -10, -32, -2, 20, 40);
    internal static readonly ProjectionPartGraph StandingSignBoard = new("StandingSignBoard", P("root", Vector3.Zero, Vector3.Zero,
        [],
        [P("board", Vector3.Zero, Vector3.Zero,
            [C(new(-12, -14, -1), new(24, 12, 2), new(0, 0), "base", 64, 32)], []),
         P("stick", Vector3.Zero, Vector3.Zero,
            [C(new(-1, -2, -1), new(2, 14, 2), new(0, 14), "base", 64, 32)], [])]));
    internal static readonly ProjectionPartGraph HangingSignBoard = new("HangingSignBoard", P("root", Vector3.Zero, Vector3.Zero,
        [C(new(-12, 5, -1), new(24, 9, 2), new(0, 2), "base", 64, 64),
         C(new(-12, -8, -1), new(2, 13, 2), new(0, 0), "base", 64, 64),
         C(new(10, -8, -1), new(2, 13, 2), new(4, 0), "base", 64, 64)], []));
    internal static readonly ProjectionPartGraph ModernHangingSignChains = HangingChains();
    internal static readonly ProjectionPartGraph HumanoidSkull = Skull("HumanoidSkull", 64, 64, true, false);
    internal static readonly ProjectionPartGraph MobSkull = Skull("MobSkull", 64, 32, false, true);
    internal static readonly ProjectionPartGraph DragonSkull = Dragon();
    internal static readonly ProjectionPartGraph PiglinSkull = Piglin();
    internal static readonly ProjectionPartGraph PotSides = new("PotSides", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("back", new(15, 16, 1), new(0, 0, 3.141593f),
            [C(new(0, 0, 0), new(14, 16, 0), new(1, 0), "back", 16, 16, 0, false, [ProjectionFaceDirection.North])],
            []),
P("left", new(1, 16, 1), new(0, -1.570796f, 3.141593f),
            [C(new(0, 0, 0), new(14, 16, 0), new(1, 0), "left", 16, 16, 0, false, [ProjectionFaceDirection.North])],
            []),
P("right", new(15, 16, 15), new(0, 1.570796f, 3.141593f),
            [C(new(0, 0, 0), new(14, 16, 0), new(1, 0), "right", 16, 16, 0, false, [ProjectionFaceDirection.North])],
            []),
P("front", new(1, 16, 15), new(3.141593f, 0, 0),
            [C(new(0, 0, 0), new(14, 16, 0), new(1, 0), "front", 16, 16, 0, false, [ProjectionFaceDirection.North])],
            [])]));
    internal static readonly ProjectionPartGraph StatueStanding = new("StatueStanding", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("body", new(0, -5, 0), new(0, 0, 0),
            [C(new(-4, -6, -3), new(8, 6, 6), new(0, 15), "base", 64, 64, 0, false)],
            [P("head", new(0, -6, 0), new(0, 0, 0),
                [C(new(-4, -5, -5), new(8, 5, 10), new(0, 0), "base", 64, 64, 0.015f, false),
C(new(-1, -2, -6), new(2, 3, 2), new(56, 0), "base", 64, 64, 0, false),
C(new(-1, -9, -1), new(2, 4, 2), new(37, 8), "base", 64, 64, -0.015f, false),
C(new(-2, -13, -2), new(4, 4, 4), new(37, 0), "base", 64, 64, -0.015f, false)],
                []),
P("right_arm", new(-4, -6, 0), new(0, 0, 0),
                [C(new(-3, -1, -2), new(3, 10, 4), new(36, 16), "base", 64, 64, 0, false)],
                []),
P("left_arm", new(4, -6, 0), new(0, 0, 0),
                [C(new(0, -1, -2), new(3, 10, 4), new(50, 16), "base", 64, 64, 0, false)],
                [])]),
P("right_leg", new(0, -5, 0), new(0, 0, 0),
            [C(new(-4, 0, -2), new(4, 5, 4), new(0, 27), "base", 64, 64, 0, false)],
            []),
P("left_leg", new(0, -5, 0), new(0, 0, 0),
            [C(new(0, 0, -2), new(4, 5, 4), new(16, 27), "base", 64, 64, 0, false)],
            [])]));
    internal static readonly ProjectionPartGraph StatueRunning = new("StatueRunning", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("body", new(-1.064f, -5, 0), new(0, 0, 0),
            [],
            [P("body_r1", new(1.1f, 0.1f, 0.7f), new(0.1204f, -0.0064f, -0.0779f),
                [C(new(-4.02f, -6.116f, -3.5f), new(8, 6, 6), new(0, 15), "base", 64, 64, 0, false)],
                []),
P("head", new(0.7f, -5.6f, -1.8f), new(0, 0, 0),
                [C(new(-4, -5.1f, -5), new(8, 5, 10), new(0, 0), "base", 64, 64, 0, false),
C(new(-1.02f, -2.1f, -6), new(2, 3, 2), new(56, 0), "base", 64, 64, 0, false),
C(new(-1.02f, -9.1f, -1), new(2, 4, 2), new(37, 8), "base", 64, 64, -0.015f, false),
C(new(-2, -13.1f, -2), new(4, 4, 4), new(37, 0), "base", 64, 64, -0.015f, false)],
                []),
P("right_arm", new(-4, -6, 0), new(0, 0, 0),
                [],
                [P("right_arm_r1", new(0.7f, -0.248f, -1.62f), new(1.0036f, 0, 0),
                    [C(new(-3.052f, -1.11f, -2.036f), new(3, 10, 4), new(36, 16), "base", 64, 64, 0, false)],
                    [])]),
P("left_arm", new(4, -6, 0), new(0, 0, 0),
                [],
                [P("left_arm_r1", new(0.732f, 0, 0), new(-0.8715f, -0.0535f, -0.0449f),
                    [C(new(0.032f, -1.1f, -2), new(3, 10, 4), new(50, 16), "base", 64, 64, 0, false)],
                    [])])]),
P("right_leg", new(-3.064f, -5, 0), new(0, 0, 0),
            [],
            [P("right_leg_r1", new(1.048f, 0, -0.9f), new(-0.8727f, 0, 0),
                [C(new(-1.856f, -0.1f, -1.09f), new(4, 5, 4), new(0, 27), "base", 64, 64, 0, false)],
                [])]),
P("left_leg", new(0.936f, -5, 0), new(0, 0, 0),
            [],
            [P("left_leg_r1", new(1, 0, 0), new(0.7854f, 0, 0),
                [C(new(-2.088f, -0.1f, -2), new(4, 5, 4), new(16, 27), "base", 64, 64, 0, false)],
                [])])]));
    internal static readonly ProjectionPartGraph StatueSitting = new("StatueSitting", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("body", new(0, -3, 2.325f), new(0, 0, 0),
            [C(new(-3, -4, -4.525f), new(6, 1, 6), new(3, 19), "base", 64, 64, 0, false),
C(new(-4, -3, -3.525f), new(8, 6, 6), new(0, 15), "base", 64, 64, 0, false)],
            [P("body_r1", new(0, -1, -4.325f), new(0, 0, -3.1416f),
                [C(new(-4, -3, -2.2f), new(8, 6, 3), new(3, 18), "base", 64, 64, 0, false)],
                []),
P("head", new(0, -6, -0.2f), new(0, 0, 0),
                [C(new(-1, -7, -3.3f), new(2, 4, 2), new(37, 8), "base", 64, 64, -0.015f, false),
C(new(-2, -11, -4.3f), new(4, 4, 4), new(37, 0), "base", 64, 64, -0.015f, false),
C(new(-4, -3, -7.325f), new(8, 5, 10), new(0, 0), "base", 64, 64, 0, false),
C(new(-1, 0, -8.325f), new(2, 3, 2), new(56, 0), "base", 64, 64, 0, false)],
                []),
P("right_arm", new(-4, -5.6f, -1.8f), new(0.4363f, 0, 0),
                [],
                [P("right_arm_r1", new(0, 0.0893f, 0.1198f), new(-1.0472f, 0, 0),
                    [C(new(-3.075f, -0.9733f, -1.9966f), new(3, 10, 4), new(36, 16), "base", 64, 64, 0, false)],
                    [])]),
P("left_arm", new(4, -5.6f, -1.7f), new(0.4363f, 0, 0),
                [],
                [P("left_arm_r1", new(0, -0.0015f, -0.0808f), new(-1.0472f, 0, 0),
                    [C(new(0.075f, -1.0443f, -1.8997f), new(3, 10, 4), new(50, 16), "base", 64, 64, 0, false)],
                    [])])]),
P("right_leg", new(-2.1f, -2.1f, -2.075f), new(0, 0, 0),
            [],
            [P("right_leg_r1", new(0.05f, -1.9f, 1.075f), new(-1.5708f, 0, 0),
                [C(new(-2, 0.975f, 0), new(4, 5, 4), new(0, 27), "base", 64, 64, 0, false)],
                [])]),
P("left_leg", new(2, -2, -2.075f), new(0, 0, 0),
            [],
            [P("left_leg_r1", new(0.05f, -2, 1.075f), new(-1.5708f, 0, 0),
                [C(new(-2, 0.975f, 0), new(4, 5, 4), new(16, 27), "base", 64, 64, 0, false)],
                [])])]));
    internal static readonly ProjectionPartGraph StatueStar = new("StatueStar", P("root", new(0, 0, 0), new(0, 0, 0),
        [],
        [P("body", new(0, -5, 0), new(0, 0, 0),
            [C(new(-4, -6, -3), new(8, 6, 6), new(0, 15), "base", 64, 64, 0, false)],
            [P("head", new(0, -6, 0), new(0, 0, 0),
                [C(new(-4, -5, -5), new(8, 5, 10), new(0, 0), "base", 64, 64, 0, false),
C(new(-1, -2, -6), new(2, 3, 2), new(56, 0), "base", 64, 64, 0, false),
C(new(-1, -9, -1), new(2, 4, 2), new(37, 8), "base", 64, 64, -0.015f, false),
C(new(-2, -13, -2), new(4, 4, 4), new(37, 0), "base", 64, 64, -0.015f, false)],
                []),
P("right_arm", new(-4, -6, 0), new(0, 0, 0),
                [],
                [P("right_arm_r1", new(1, 1, 0), new(0, 0, 1.9199f),
                    [C(new(-1.5f, -5, -2), new(3, 10, 4), new(36, 16), "base", 64, 64, 0, false)],
                    []),
P("rightItem", new(-1, 7.4f, -1), new(0, 0, 0),
                    [],
                    [])]),
P("left_arm", new(4, -6, 0), new(0, 0, 0),
                [],
                [P("left_arm_r1", new(-1, 1, 0), new(0, 0, -1.9199f),
                    [C(new(-1.5f, -5, -2), new(3, 10, 4), new(50, 16), "base", 64, 64, 0, false)],
                    [])])]),
P("right_leg", new(-3, -5, 0), new(0, 0, 0),
            [],
            [P("right_leg_r1", new(0.35f, 2, 0.01f), new(0, 0, 0.2618f),
                [C(new(-2, -2.5f, -2), new(4, 5, 4), new(0, 27), "base", 64, 64, 0, false)],
                [])]),
P("left_leg", new(1, -5, 0), new(0, 0, 0),
            [],
            [P("left_leg_r1", new(1.65f, 2, 0), new(0, 0, -0.2618f),
                [C(new(-2, -2.5f, -2), new(4, 5, 4), new(16, 27), "base", 64, 64, 0, false)],
                [])])]));
    internal static readonly ProjectionPartGraph BedHeadVisibleFaces = VisibleBedFaces(BedHead, true);
    internal static readonly ProjectionPartGraph BedFootVisibleFaces = VisibleBedFaces(BedFoot, false);
    internal static readonly ProjectionPartGraph BookPlanar = PlanarBook();

    private static ProjectionPartGraph BannerSurface(string name, float x, float y, float z, float width, float height)
    {
        var surface = ProjectionGeometryBuilder.Part("surface", ProjectionPose.Identity,
            [new ProjectionCuboid(
                new Vector3(x, y, z) / 16,
                new Vector3(x + width, y + height, z + 1) / 16,
                new ProjectionMaterialRequest("surface", ProjectionMaterialRole.Cutout, 64, 64,
                    "Vanilla 64x64 BannerModel 20x40x1 cube atlas layout"),
                Vector4.Zero, Shade: false, UvSize: new(width, height, 1))]);
        return ProjectionGeometryBuilder.FromRoot(name, new("root", ProjectionPose.Identity, [], [], [surface]));
    }

    private static ProjectionPartGraph BannerStructure(string name, bool standing)
    {
        var nodes = new List<ProjectionPartNode>();
        if (standing)
            nodes.Add(ProjectionGeometryBuilder.Part("pole", ProjectionPose.Identity,
                [C(new(-1, -30, -1), new(2, 42, 2), new(44, 0), "base", 64, 64)]));
        nodes.Add(ProjectionGeometryBuilder.Part("bar", ProjectionPose.Identity,
            [C(new(-10, -32, -1), new(20, 2, 2), new(0, 42), "base", 64, 64)]));
        return ProjectionGeometryBuilder.Graph(name, [.. nodes]);
    }

    private static ProjectionPartGraph HangingChains()
    {
        ProjectionPartNode Chain(string name, Vector3 origin, float zRotation) =>
            ProjectionGeometryBuilder.Part(name, new(origin, new(0, 0, zRotation), Vector3.Zero, Vector3.One, false, false, false),
                [new ProjectionCuboid(new Vector3(-1.5f, 0, 0) / 16, new Vector3(1.5f, 6, 0) / 16,
                    new("base", ProjectionMaterialRole.Cutout, 16, 16, "Audited hanging-sign chain"),
                    new(0, 0, 0, 0), Shade: false, UvSize: new(3, 6, 0))]);
        var root = new ProjectionPartNode("root", ProjectionPose.Identity, [], [], [
            Chain("chainL1", new Vector3(-5, 26, 0) / 16, -.785398f), Chain("chainL2", new Vector3(-5, 26, 0) / 16, .785398f),
            Chain("chainR1", new Vector3(5, 26, 0) / 16, -.785398f), Chain("chainR2", new Vector3(5, 26, 0) / 16, .785398f),
            ProjectionGeometryBuilder.Part("vChains", ProjectionPose.Identity,
                [new ProjectionCuboid(new Vector3(-6, 20, 0) / 16, new Vector3(6, 26, 0) / 16,
                    new("base", ProjectionMaterialRole.Cutout, 16, 16, "Audited hanging-sign V chain"),
                    new(14, 6, 0, 0), Shade: false, UvSize: new(12, 6, 0))])]);
        return ProjectionGeometryBuilder.FromRoot("ModernHangingSignChains", root);
    }

    private static ProjectionPartGraph Skull(string name, int width, int height, bool hat, bool mob)
    {
        var headFrom = mob ? new Vector3(-4, -8, -4) / 16 : new Vector3(-4, -8, -4) / 16;
        var headTo = new Vector3(4, 0, 4) / 16;
        var head = ProjectionGeometryBuilder.Part("head", ProjectionPose.Identity,
            [SkullBox(headFrom, headTo, new ProjectionMaterialRequest("base", ProjectionMaterialRole.Cutout, width, height, "Audited static skull head"), Vector2.Zero)]);
        var parts = new List<ProjectionPartNode> { head };
        if (hat)
        {
            var hatMaterial = new ProjectionMaterialRequest("base", ProjectionMaterialRole.Blend, width, height, "Humanoid outer head layer");
            var hatPart = new ProjectionPartNode("hat", ProjectionPose.Identity,
                [SkullBox(headFrom - new Vector3(.25f / 16), headTo + new Vector3(.25f / 16), hatMaterial, new(32, 0))], [], []);
            parts.Add(hatPart);
        }
        return ProjectionGeometryBuilder.Graph(name, [.. parts]);
    }

    private static ProjectionCuboid SkullBox(Vector3 from, Vector3 to, ProjectionMaterialRequest material, Vector2 uvOrigin) =>
        // CubeDeformation changes geometry only; the skin still contains 8x8 faces.
        new(from, to, material, new(uvOrigin.X, uvOrigin.Y, 0, 0), Shade: false, UvSize: new(8));

    private static ProjectionPartGraph Dragon()
    {
        var skull = P("skull", Vector3.Zero, Vector3.Zero,
            [C(new(-8, -8, -10), new(16, 16, 16), new(112, 30), "base", 256, 256)], []);
        var upperLip = P("upperLip", Vector3.Zero, Vector3.Zero,
            [C(new(-6, -1, -24), new(12, 5, 16), new(176, 44), "base", 256, 256)], []);
        var scales = P("scales", Vector3.Zero, Vector3.Zero,
            [C(new(-5, -12, -4), new(2, 4, 6), new(0, 0), "base", 256, 256, mirror: true),
             C(new(3, -12, -4), new(2, 4, 6), new(0, 0), "base", 256, 256)], []);
        var nostrils = P("nostrils", Vector3.Zero, Vector3.Zero,
            [C(new(-5, -3, -22), new(2, 2, 4), new(112, 0), "base", 256, 256, mirror: true),
             C(new(3, -3, -22), new(2, 2, 4), new(112, 0), "base", 256, 256)], []);
        var jaw = P("jaw", new(0, 4, -8), new(.2f, 0, 0),
            [C(new(-6, 0, -16), new(12, 4, 16), new(176, 65), "base", 256, 256)], []);
        // PartPose.scaled also scales the offset; this matches the older renderer's
        // translate(0, -0.3743749857, 0), scale(0.75) sequence.
        var head = new ProjectionPartNode("head", new(Vector3.Zero, Vector3.Zero, new Vector3(0, -7.9866662f * .75f, 0) / 16,
            new(.75f), false, false, false), [], [], [skull, upperLip, scales, nostrils, jaw]);
        return ProjectionGeometryBuilder.FromRoot("DragonSkull", new("root", ProjectionPose.Identity, [], [], [head]));
    }

    private static ProjectionPartGraph Piglin()
    {
        var head = ProjectionGeometryBuilder.Part("head", ProjectionPose.Identity,
            [C(new(-5, -8, -4), new(10, 8, 8), new(0, 0), "base", 64, 64),
             C(new(-2, -4, -5), new(4, 4, 1), new(31, 1), "base", 64, 64),
             C(new(2, -2, -5), new(1, 2, 1), new(2, 4), "base", 64, 64),
             C(new(-3, -2, -5), new(1, 2, 1), new(2, 0), "base", 64, 64)]);
        // setupAnim at animation position zero: +/- (cos(0) + 2.5) * 0.2.
        var leftEar = P("rightEar", new(-4.5f, -6, 0), new(0, 0, .7f),
            [C(new(-1, 0, -2), new(1, 5, 4), new(39, 6), "base", 64, 64)], []);
        var rightEar = P("leftEar", new(4.5f, -6, 0), new(0, 0, -.7f),
            [C(new(0, 0, -2), new(1, 5, 4), new(51, 6), "base", 64, 64)], []);
        return ProjectionGeometryBuilder.Graph("PiglinSkull", [head, leftEar, rightEar]);
    }

    private static ProjectionPartGraph PlanarBook()
    {
        ProjectionPartNode Map(ProjectionPartNode node) => node with
        {
            Cuboids = node.Cuboids.Select(cube => node.Name is "left_lid" or "right_lid" or "seam" or "flip_page1" or "flip_page2"
                ? cube with { From = cube.From with { Z = 0 }, To = cube.To with { Z = 0 },
                    UvSize = cube.UvSize!.Value with { Z = 0 } } : cube).ToArray(),
            Children = node.Children.Select(Map).ToArray()
        };
        return new("BookPlanar", Map(Book.Root));
    }

    private static ProjectionPartNode P(string name, Vector3 offset, Vector3 rotation,
        ProjectionCuboid[] cubes, ProjectionPartNode[] children) =>
        new(name, new(Vector3.Zero, rotation, offset / 16, Vector3.One, false, false, false), cubes, [], children);

    private static ProjectionCuboid C(Vector3 from, Vector3 size, Vector2 uv, string slot, int width, int height,
        float inflation = 0, bool mirror = false, ProjectionFaceDirection[]? faces = null) =>
        new((from - new Vector3(inflation)) / 16, (from + size + new Vector3(inflation)) / 16,
            new(slot, ProjectionMaterialRole.Cutout, width, height, "Audited logical part canvas"),
            new(uv.X, uv.Y, 0, 0), UvSize: size, Mirror: mirror, Faces: faces?.ToHashSet());
}
