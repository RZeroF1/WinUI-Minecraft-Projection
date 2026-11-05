using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace RZeroF1.Minecraft.Projection;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct ProjectionMeshVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Uv;
    public uint Color;
}

internal sealed class ProjectionMaterialBatch
{
    public required string TextureId { get; init; }
    public required int Mode { get; init; }
    public required int ChunkX { get; init; }
    public required int ChunkY { get; init; }
    public required int ChunkZ { get; init; }
    public required ProjectionMeshVertex[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
}

internal sealed class ProjectionBakedScene
{
    public required IReadOnlyList<ProjectionMaterialBatch> Batches { get; init; }
    public required IReadOnlyDictionary<string, byte[]?> TexturePng { get; init; }
    public required IReadOnlyDictionary<string, string> MaterialTextureIds { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required ProjectionPosition Min { get; init; }
    public required ProjectionPosition Max { get; init; }
    public int BlockCount { get; init; }
    public int MaterialCount => TexturePng.Count;
    public int TriangleCount => Batches.Sum(static batch => batch.Indices.Length / 3);
    public long EstimatedCpuBytes =>
        Batches.Sum(static batch =>
            batch.Vertices.LongLength * 36L +
            batch.Indices.LongLength * sizeof(uint)) +
        TexturePng.Values.Sum(static texture => texture?.LongLength ?? 0);
}

internal sealed class ProjectionModelBaker
{
    private readonly ProjectionResourceSet _resources;
    private ProjectionModelResolver _resolver = null!;
    private readonly Dictionary<string, JsonElement> _blockstates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Texture, int Mode, int X, int Y, int Z), BatchBuilder> _batches = new();
    private readonly Dictionary<string, byte[]?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _materialTextureIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];
    private readonly HashSet<ProjectionPosition> _opaqueFullCubes = [];

    public ProjectionModelBaker(ProjectionResourceSet resources)
    {
        _resources = resources;
    }

    public ProjectionBakedScene Build(
        ProjectionDocument document,
        IProgress<ProjectionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _resources.LoadModelDefinitions(cancellationToken);
        _resolver = new ProjectionModelResolver(_resources.Models);
        _blockstates.Clear();
        _batches.Clear();
        _textures.Clear();
        _materialTextureIds.Clear();
        _warnings.Clear();
        _opaqueFullCubes.Clear();

        var center = new Vector3(
            (document.Min.X + document.Max.X + 1) / 2f,
            (document.Min.Y + document.Max.Y + 1) / 2f,
            (document.Min.Z + document.Max.Z + 1) / 2f);
        var blockMap = document.Blocks.ToDictionary(static block => block.Position);
        foreach (var block in document.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsOpaqueFullCube(document.Palette[block.StateIndex].Name))
                _opaqueFullCubes.Add(block.Position);
        }

        for (var index = 0; index < document.Blocks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = document.Blocks[index];
            var state = document.Palette[block.StateIndex];
            AddBlock(document, block, state, center, blockMap);
            if ((index & 0x3ff) == 0)
                progress?.Report(new ProjectionLoadProgress(
                    "mesh",
                    index,
                    document.Blocks.Count,
                    $"生成网格 {index:N0}/{document.Blocks.Count:N0}"));
        }

        return new ProjectionBakedScene
        {
            Batches = _batches.Values
                .Select(static batch => batch.ToResult())
                .Where(static batch => batch.Indices.Length > 0)
                .ToArray(),
            TexturePng = new Dictionary<string, byte[]?>(_textures, StringComparer.OrdinalIgnoreCase),
            MaterialTextureIds = new Dictionary<string, string>(_materialTextureIds, StringComparer.OrdinalIgnoreCase),
            Warnings = _warnings.Concat(_resources.Warnings).Distinct(StringComparer.Ordinal).Take(500).ToArray(),
            Min = document.Min,
            Max = document.Max,
            BlockCount = document.Blocks.Count
        };
    }

    public ProjectionBakedScene BuildSliceBoundary(
        ProjectionDocument document,
        ProjectionSliceIndex index,
        int axis,
        int minimum,
        int maximum,
        CancellationToken cancellationToken = default)
    {
        if (axis is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(axis));

        _resources.LoadModelDefinitions(cancellationToken);
        _resolver = new ProjectionModelResolver(_resources.Models);
        _blockstates.Clear();
        _batches.Clear();
        _textures.Clear();
        _materialTextureIds.Clear();
        _warnings.Clear();
        var center = new Vector3(
            (document.Min.X + document.Max.X + 1) / 2f,
            (document.Min.Y + document.Max.Y + 1) / 2f,
            (document.Min.Z + document.Max.Z + 1) / 2f);
        var blockMap = index.BlockMap;

        var lowerDirection = BoundaryDirection(axis, lower: true);
        var upperDirection = BoundaryDirection(axis, lower: false);
        AddBoundary(
            document,
            index,
            blockMap,
            center,
            axis,
            minimum,
            lowerDirection,
            cancellationToken);
        if (maximum != minimum)
        {
            AddBoundary(
                document,
                index,
                blockMap,
                center,
                axis,
                maximum,
                upperDirection,
                cancellationToken);
        }

        return new ProjectionBakedScene
        {
            Batches = _batches.Values
                .Select(static batch => batch.ToResult())
                .Where(static batch => batch.Indices.Length > 0)
                .ToArray(),
            TexturePng = new Dictionary<string, byte[]?>(_textures, StringComparer.OrdinalIgnoreCase),
            MaterialTextureIds = new Dictionary<string, string>(_materialTextureIds, StringComparer.OrdinalIgnoreCase),
            Warnings = _warnings.Concat(_resources.Warnings).Distinct(StringComparer.Ordinal).Take(500).ToArray(),
            Min = document.Min,
            Max = document.Max,
            BlockCount = 0
        };
    }

    private void AddBoundary(
        ProjectionDocument document,
        ProjectionSliceIndex index,
        IReadOnlyDictionary<ProjectionPosition, ProjectionBlock> blockMap,
        Vector3 center,
        int axis,
        int coordinate,
        ProjectionFaceDirection direction,
        CancellationToken cancellationToken)
    {
        foreach (var block in index.BlocksAt(axis, coordinate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var neighbor = Neighbor(block.Position, direction);
            if (!index.IsOpaqueFullCube(neighbor) || !blockMap.ContainsKey(neighbor))
                continue;

            AddBlock(
                document,
                block,
                document.Palette[block.StateIndex],
                center,
                blockMap,
                direction);
        }
    }

    private void AddBlock(
        ProjectionDocument document,
        ProjectionBlock block,
        ProjectionState state,
        Vector3 center,
        IReadOnlyDictionary<ProjectionPosition, ProjectionBlock> blockMap,
        ProjectionFaceDirection? onlyDirection = null)
    {
        var selected = new List<ProjectionModelPlacement>();
        var stateJson = ReadBlockstate(state.Name);
        if (stateJson is { } json)
        {
            try
            {
                selected.AddRange(ProjectionStateSelection.Select(
                    json,
                    state.Properties,
                    StableSeed(block.Position)));
            }
            catch (Exception exception)
            {
                AddWarning($"{state.Key}: blockstate 选择失败：{exception.Message}");
            }
        }

        var blockName = state.Name;
        if (blockName is "minecraft:water" or "minecraft:lava")
        {
            AddFluid(block, state, center, blockName);
            return;
        }

        if (selected.Count == 0)
        {
            AddWarning($"{state.Key}: 缺少可用 blockstate，使用占位模型。");
            selected.Add(new ProjectionModelPlacement("__missing__", 0, 0, false));
        }

        foreach (var placement in selected)
        {
            ProjectionModelInstance model;
            try
            {
                model = placement.Model == "__missing__"
                    ? MissingModel()
                    : _resolver.Resolve(placement.Model);
            }
            catch (Exception exception)
            {
                AddWarning($"{state.Key}: {exception.Message}，使用占位模型。");
                model = MissingModel();
            }

            if (model.Elements.Count == 0)
            {
                AddWarning($"{state.Key}: 模型由原版专用 renderer 提供几何，当前引擎暂不支持该特殊渲染器。");
                continue;
            }

            var stateRotation =
                Matrix4x4.CreateRotationX(placement.RotationX * MathF.PI / 180f) *
                Matrix4x4.CreateRotationY(-placement.RotationY * MathF.PI / 180f);
            var fullCube = IsFullCube(model);
            foreach (var element in model.Elements)
            {
                var corners = CreateCorners(element.From / 16f, element.To / 16f);
                TransformCorners(corners, element.Rotation, stateRotation);
                foreach (var (direction, face) in element.Faces)
                {
                    if (onlyDirection is not null && direction != onlyDirection)
                        continue;
                    if (onlyDirection is null && fullCube && IsInternalFace(block.Position, direction, blockMap))
                        continue;

                    var positions = ProjectionModelGeometry.FaceVertexIndices[direction]
                        .Select(index => corners[index] + new Vector3(block.Position.X, block.Position.Y, block.Position.Z) - center)
                        .ToArray();
                    var normal = Vector3.Normalize(Vector3.Cross(positions[1] - positions[0], positions[2] - positions[0]));
                    var textureId = ResolveTexture(model, face.Texture);
                    _materialTextureIds.TryAdd(state.Key, textureId);
                    var mode = RenderMode(state.Name, textureId);
                    var uv = ProjectionModelGeometry.CreateUvMap(
                        face.Uv ?? ProjectionModelGeometry.DefaultFaceUv(element.From, element.To, direction),
                        face.Rotation);
                    if (placement.UvLock)
                        uv = LockUvToWorld(positions, uv, normal);
                    var tint = face.TintIndex is not null ? Tint(state.Name) : Vector3.One;
                    if (state.Name is "minecraft:water" or "minecraft:lava")
                        tint = Tint(state.Name);
                    var shade = element.Shade
                        ? ShadeFactor(element.ShadeDirectionOverride, normal)
                        : 1f;
                    AddFace(
                        textureId,
                        mode,
                        block.Position,
                        positions,
                        normal,
                        uv,
                        tint * shade);
                }
            }
        }
    }

    private void AddFluid(ProjectionBlock block, ProjectionState state, Vector3 center, string blockName)
    {
        var level = state.Properties.TryGetValue("level", out var rawLevel) &&
                    int.TryParse(rawLevel, out var parsed)
            ? parsed
            : 0;
        var height = level >= 8 ? 1f : Math.Clamp((8 - level) / 9f, .1f, 1f);
        var model = CubeModel(blockName == "minecraft:water"
            ? "minecraft:block/water_still"
            : "minecraft:block/lava_still", height);
        _materialTextureIds.TryAdd(state.Key, blockName == "minecraft:water"
            ? "minecraft:block/water_still"
            : "minecraft:block/lava_still");
        var fake = block with { };
        foreach (var element in model.Elements)
        {
            var corners = CreateCorners(element.From / 16f, element.To / 16f);
            foreach (var (direction, face) in element.Faces)
            {
                var positions = ProjectionModelGeometry.FaceVertexIndices[direction]
                    .Select(index => corners[index] + new Vector3(fake.Position.X, fake.Position.Y, fake.Position.Z) - center)
                    .ToArray();
                var normal = Vector3.Normalize(Vector3.Cross(positions[1] - positions[0], positions[2] - positions[0]));
                var uv = ProjectionModelGeometry.CreateUvMap(
                    face.Uv ?? ProjectionModelGeometry.DefaultFaceUv(element.From, element.To, direction),
                    face.Rotation);
                AddFace(face.Texture, 2, fake.Position, positions, normal, uv, Tint(blockName));
            }
        }
    }

    private void AddFace(
        string textureId,
        int mode,
        ProjectionPosition blockPosition,
        IReadOnlyList<Vector3> positions,
        Vector3 normal,
        IReadOnlyList<Vector2> uv,
        Vector3 color)
    {
        EnsureTexture(textureId);
        var chunk = (
            FloorDiv(blockPosition.X, 16),
            FloorDiv(blockPosition.Y, 16),
            FloorDiv(blockPosition.Z, 16));
        var key = (textureId, mode, chunk.Item1, chunk.Item2, chunk.Item3);
        if (!_batches.TryGetValue(key, out var batch))
        {
            batch = new BatchBuilder(textureId, mode, chunk.Item1, chunk.Item2, chunk.Item3);
            _batches[key] = batch;
        }

        var packed = PackColor(color);
        var first = (uint)batch.Vertices.Count;
        for (var index = 0; index < 4; index++)
        {
            batch.Vertices.Add(new ProjectionMeshVertex
            {
                Position = positions[index],
                Normal = normal,
                Uv = uv[index],
                Color = packed
            });
        }
        batch.Indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
    }

    private JsonElement? ReadBlockstate(string blockName)
    {
        if (_blockstates.TryGetValue(blockName, out var cached))
            return cached;
        var bytes = _resources.Read(ProjectionResourceSet.ResourcePath(blockName, "blockstates", ".json"));
        if (bytes is null)
        {
            _blockstates[blockName] = default;
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement.Clone();
            _blockstates[blockName] = root;
            return root;
        }
        catch (JsonException exception)
        {
            AddWarning($"{blockName}: blockstate JSON 无法解析：{exception.Message}");
            _blockstates[blockName] = default;
            return null;
        }
    }

    private string ResolveTexture(ProjectionModelInstance model, string texture)
    {
        var current = texture.Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; depth < 16; depth++)
        {
            var key = current.StartsWith('#') ? current[1..] : current;
            if (!model.Textures.TryGetValue(key, out var mapped))
                break;
            if (!seen.Add(key))
                break;
            current = mapped.Trim();
        }
        if (current.StartsWith('#'))
        {
            AddWarning($"{model.Name}: 无法解析纹理引用 {texture}。");
            return "__missing__";
        }
        if (current.Equals("__missing__", StringComparison.OrdinalIgnoreCase))
            return "__missing__";
        return ProjectionResourceSet.NormalizeResourceId(current);
    }

    private void EnsureTexture(string textureId)
    {
        if (_textures.ContainsKey(textureId))
            return;
        _textures[textureId] = textureId == "__missing__"
            ? null
            : _resources.Read(ProjectionResourceSet.ResourcePath(textureId, "textures", ".png"));
        if (_textures[textureId] is null && textureId != "__missing__")
            AddWarning($"缺少纹理 {textureId}。");
    }

    private int RenderMode(string blockName, string textureId)
    {
        if (textureId == "__missing__")
            return 0;
        if (_resources.ForceTranslucent.Contains(textureId) ||
            blockName.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
            blockName.Contains("ice", StringComparison.OrdinalIgnoreCase) ||
            blockName.Contains("water", StringComparison.OrdinalIgnoreCase) ||
            blockName.Contains("lava", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (blockName.Contains("leaves", StringComparison.OrdinalIgnoreCase) ||
            blockName.Contains("flower", StringComparison.OrdinalIgnoreCase) ||
            blockName.Contains("grass", StringComparison.OrdinalIgnoreCase) ||
            blockName.Contains("fern", StringComparison.OrdinalIgnoreCase) ||
            blockName.Contains("vine", StringComparison.OrdinalIgnoreCase) ||
            blockName.Contains("crop", StringComparison.OrdinalIgnoreCase) ||
            blockName.Contains("wheat", StringComparison.OrdinalIgnoreCase) ||
            blockName.Contains("carrot", StringComparison.OrdinalIgnoreCase) ||
            blockName.Contains("potato", StringComparison.OrdinalIgnoreCase) ||
            blockName.Contains("beetroot", StringComparison.OrdinalIgnoreCase) ||
            blockName.Contains("berry", StringComparison.OrdinalIgnoreCase))
            return 1;
        return 0;
    }

    private bool IsInternalFace(
        ProjectionPosition position,
        ProjectionFaceDirection direction,
        IReadOnlyDictionary<ProjectionPosition, ProjectionBlock> blockMap)
    {
        var neighbor = direction switch
        {
            ProjectionFaceDirection.North => position with { Z = position.Z - 1 },
            ProjectionFaceDirection.South => position with { Z = position.Z + 1 },
            ProjectionFaceDirection.East => position with { X = position.X + 1 },
            ProjectionFaceDirection.West => position with { X = position.X - 1 },
            ProjectionFaceDirection.Up => position with { Y = position.Y + 1 },
            _ => position with { Y = position.Y - 1 }
        };
        return _opaqueFullCubes.Contains(neighbor) && blockMap.ContainsKey(neighbor);
    }

    private static ProjectionPosition Neighbor(
        ProjectionPosition position,
        ProjectionFaceDirection direction) =>
        direction switch
        {
            ProjectionFaceDirection.North => position with { Z = position.Z - 1 },
            ProjectionFaceDirection.South => position with { Z = position.Z + 1 },
            ProjectionFaceDirection.East => position with { X = position.X + 1 },
            ProjectionFaceDirection.West => position with { X = position.X - 1 },
            ProjectionFaceDirection.Up => position with { Y = position.Y + 1 },
            _ => position with { Y = position.Y - 1 }
        };

    private static ProjectionFaceDirection BoundaryDirection(int axis, bool lower) =>
        axis switch
        {
            0 => lower ? ProjectionFaceDirection.West : ProjectionFaceDirection.East,
            1 => lower ? ProjectionFaceDirection.Down : ProjectionFaceDirection.Up,
            _ => lower ? ProjectionFaceDirection.North : ProjectionFaceDirection.South
        };

    private void AddWarning(string warning)
    {
        if (_warnings.Count < 500)
            _warnings.Add(warning);
    }

    private static Vector3[] CreateCorners(Vector3 from, Vector3 to) =>
    [
        new(from.X, from.Y, from.Z),
        new(to.X, from.Y, from.Z),
        new(to.X, to.Y, from.Z),
        new(from.X, to.Y, from.Z),
        new(from.X, from.Y, to.Z),
        new(to.X, from.Y, to.Z),
        new(to.X, to.Y, to.Z),
        new(from.X, to.Y, to.Z)
    ];

    private static void TransformCorners(
        Vector3[] corners,
        ProjectionElementRotation? elementRotation,
        Matrix4x4 stateRotation)
    {
        for (var index = 0; index < corners.Length; index++)
        {
            var point = corners[index];
            if (elementRotation is not null)
            {
                var axis = elementRotation.Axis switch
                {
                    "x" => Vector3.UnitX,
                    "z" => Vector3.UnitZ,
                    _ => Vector3.UnitY
                };
                var angle = elementRotation.AngleDegrees * MathF.PI / 180f;
                var local = point - elementRotation.Origin / 16f;
                if (elementRotation.Rescale)
                {
                    var scale = 1 / MathF.Cos(angle);
                    local *= axis == Vector3.UnitX
                        ? new Vector3(1, scale, scale)
                        : axis == Vector3.UnitY
                            ? new Vector3(scale, 1, scale)
                            : new Vector3(scale, scale, 1);
                }
                point = Vector3.Transform(local, Matrix4x4.CreateFromAxisAngle(axis, angle))
                    + elementRotation.Origin / 16f;
            }
            corners[index] = Vector3.Transform(point - new Vector3(.5f), stateRotation) + new Vector3(.5f);
        }
    }

    private static ProjectionModelInstance MissingModel() => CubeModel("__missing__", 1f);

    private static ProjectionModelInstance CubeModel(string texture, float height) =>
        new()
        {
            Name = "generated",
            Textures = new Dictionary<string, string>(),
            Elements =
            [
                new ProjectionModelElement
                {
                    From = Vector3.Zero,
                    To = new Vector3(16, 16 * height, 16),
                    Faces = Enum.GetValues<ProjectionFaceDirection>()
                        .ToDictionary(
                            static direction => direction,
                            direction => new ProjectionModelFace(texture, null, 0, null, direction.ToString())),
                    Shade = true
                }
            ]
        };

    private static bool IsFullCube(ProjectionModelInstance model) =>
        model.Elements.Count == 1 &&
        model.Elements[0].Rotation is null &&
        model.Elements[0].From == Vector3.Zero &&
        model.Elements[0].To == new Vector3(16, 16, 16) &&
        model.Elements[0].Faces.Count == 6;

    internal static bool IsOpaqueFullCube(string stateName)
    {
        var lower = stateName.ToLowerInvariant();
        return !lower.Contains("air") &&
               !lower.Contains("water") &&
               !lower.Contains("lava") &&
               !lower.Contains("glass") &&
               !lower.Contains("leaves") &&
               !lower.Contains("slab") &&
               !lower.Contains("stairs") &&
               !lower.Contains("fence") &&
               !lower.Contains("wall") &&
               !lower.Contains("door") &&
               !lower.Contains("pane") &&
               !lower.Contains("torch") &&
               !lower.Contains("flower") &&
               !lower.Contains("grass") &&
               !lower.Contains("fern") &&
               !lower.Contains("vine") &&
               !lower.Contains("chain");
    }

    private static Vector3 Tint(string stateName) =>
        stateName.Contains("grass", StringComparison.OrdinalIgnoreCase)
            ? new Vector3(.57f, .74f, .35f)
            : stateName.Contains("fern", StringComparison.OrdinalIgnoreCase) ||
              stateName.Contains("vine", StringComparison.OrdinalIgnoreCase) ||
              stateName.Contains("leaves", StringComparison.OrdinalIgnoreCase)
                ? new Vector3(.47f, .67f, .18f)
                : stateName.Contains("water", StringComparison.OrdinalIgnoreCase)
                    ? new Vector3(.247f, .463f, .894f)
                    : Vector3.One;

    private static Vector2[] LockUvToWorld(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector2> source,
        Vector3 normal)
    {
        var projected = positions
            .Select(position =>
                MathF.Abs(normal.Y) > .9f
                    ? new Vector2(position.X, normal.Y > 0 ? position.Z : -position.Z)
                    : MathF.Abs(normal.X) > .9f
                        ? new Vector2(normal.X > 0 ? -position.Z : position.Z, -position.Y)
                        : new Vector2(normal.Z > 0 ? position.X : -position.X, -position.Y))
            .ToArray();
        var minU = projected.Min(static point => point.X);
        var maxU = projected.Max(static point => point.X);
        var minV = projected.Min(static point => point.Y);
        var maxV = projected.Max(static point => point.Y);
        return projected
            .Select(point =>
            {
                var right = MathF.Abs(point.X - maxU) < MathF.Abs(point.X - minU);
                var top = MathF.Abs(point.Y - maxV) < MathF.Abs(point.Y - minV);
                var sourceIndex = (right, top) switch
                {
                    (false, false) => 0,
                    (false, true) => 1,
                    (true, true) => 2,
                    _ => 3
                };
                return source[sourceIndex];
            })
            .ToArray();
    }

    private static float ShadeFactor(string? directionOverride, Vector3 normal)
    {
        var shadeNormal = directionOverride?.ToLowerInvariant() switch
        {
            "up" => Vector3.UnitY,
            "down" => -Vector3.UnitY,
            "north" => -Vector3.UnitZ,
            "south" => Vector3.UnitZ,
            "west" => -Vector3.UnitX,
            "east" => Vector3.UnitX,
            _ => normal
        };
        return MathF.Abs(shadeNormal.Y) > .9f
            ? shadeNormal.Y > 0 ? 1f : .5f
            : MathF.Abs(shadeNormal.X) > .9f ? .8f : .6f;
    }

    private static uint PackColor(Vector3 color) =>
        (uint)(Math.Clamp(color.X, 0, 1) * 255) |
        ((uint)(Math.Clamp(color.Y, 0, 1) * 255) << 8) |
        ((uint)(Math.Clamp(color.Z, 0, 1) * 255) << 16) |
        0xff000000;

    private static int StableSeed(ProjectionPosition position) =>
        unchecked(position.X * 73428767 ^ position.Y * 912931 ^ position.Z * 19349663);

    private static int FloorDiv(int value, int divisor) =>
        (int)Math.Floor(value / (double)divisor);

    private sealed class BatchBuilder(string textureId, int mode, int chunkX, int chunkY, int chunkZ)
    {
        public string TextureId { get; } = textureId;
        public int Mode { get; } = mode;
        public int ChunkX { get; } = chunkX;
        public int ChunkY { get; } = chunkY;
        public int ChunkZ { get; } = chunkZ;
        public List<ProjectionMeshVertex> Vertices { get; } = [];
        public List<uint> Indices { get; } = [];

        public ProjectionMaterialBatch ToResult() => new()
        {
            TextureId = TextureId,
            Mode = Mode,
            ChunkX = ChunkX,
            ChunkY = ChunkY,
            ChunkZ = ChunkZ,
            Vertices = Vertices.ToArray(),
            Indices = Indices.ToArray()
        };
    }
}
