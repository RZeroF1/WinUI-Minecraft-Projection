using System.Numerics;

namespace RZeroF1.Minecraft.Projection;

internal enum ProjectionMaterialRole { Opaque, Cutout, Blend }

internal readonly record struct ProjectionMaterialRequest(
    string TextureId, ProjectionMaterialRole Role, int LogicalWidth, int LogicalHeight,
    string Evidence, float SpriteU0 = 0, float SpriteV0 = 0, float SpriteU1 = 1, float SpriteV1 = 1);

internal readonly record struct ProjectionPose(
    Vector3 Pivot, Vector3 RotationRadians, Vector3 Translation, Vector3 Scale, bool MirrorX, bool MirrorY, bool MirrorZ)
{
    internal static ProjectionPose Identity => new(Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.One, false, false, false);

    internal Matrix4x4 Matrix
    {
        get
        {
            var mirror = Matrix4x4.CreateScale(
                MirrorX ? -Scale.X : Scale.X,
                MirrorY ? -Scale.Y : Scale.Y,
                MirrorZ ? -Scale.Z : Scale.Z);
            var rotation = Matrix4x4.CreateRotationX(RotationRadians.X) *
                Matrix4x4.CreateRotationY(RotationRadians.Y) * Matrix4x4.CreateRotationZ(RotationRadians.Z);
            return Matrix4x4.CreateTranslation(-Pivot) * mirror * rotation *
                Matrix4x4.CreateTranslation(Pivot + Translation);
        }
    }
}

internal sealed record ProjectionPartNode(
    string Name,
    ProjectionPose Pose,
    IReadOnlyList<ProjectionCuboid> Cuboids,
    IReadOnlyList<ProjectionQuad> Quads,
    IReadOnlyList<ProjectionPartNode> Children,
    bool Visible = true);

internal sealed record ProjectionPartGraph(string Name, ProjectionPartNode Root)
{
    internal IEnumerable<(ProjectionPartNode Node, Matrix4x4 Matrix)> Walk(ProjectionPose instancePose)
    {
        var root = instancePose.Matrix;
        return WalkNode(Root, root);

        static IEnumerable<(ProjectionPartNode Node, Matrix4x4 Matrix)> WalkNode(ProjectionPartNode node, Matrix4x4 parent)
        {
            if (!node.Visible) yield break;
            var matrix = node.Pose.Matrix * parent;
            yield return (node, matrix);
            foreach (var child in node.Children)
                foreach (var result in WalkNode(child, matrix)) yield return result;
        }
    }
}

internal sealed record ProjectionCuboid(
    Vector3 From, Vector3 To, ProjectionMaterialRequest Material,
    Vector4? Uv = null, bool Shade = true, bool CullFaces = false,
    Vector3? UvSize = null, bool Mirror = false, IReadOnlySet<ProjectionFaceDirection>? Faces = null);

internal sealed record ProjectionQuad(
    IReadOnlyList<Vector3> Positions, ProjectionMaterialRequest Material,
    IReadOnlyList<Vector2> Uv, bool DoubleSided = false);

internal sealed record ProjectionSpecialPrimitive(
    ProjectionMaterialRequest Material, ProjectionMeshVertex[] Vertices, uint[] Indices,
    ProjectionContributionBounds Bounds);

internal sealed class ProjectionSpecialMeshBuilder(Matrix4x4 transform)
{
    private sealed class MaterialGeometry
    {
        internal readonly List<ProjectionMeshVertex> Vertices = [];
        internal readonly List<uint> Indices = [];
    }

    private readonly Dictionary<ProjectionMaterialRequest, MaterialGeometry> _materials = [];

    internal void AddQuad(IReadOnlyList<Vector3> positions, ProjectionMaterialRequest material, Vector4 logicalUv)
    {
        AddQuadLogical(positions, material,
        [
            new(logicalUv.X, logicalUv.Y),
            new(logicalUv.Z, logicalUv.Y),
            new(logicalUv.Z, logicalUv.W),
            new(logicalUv.X, logicalUv.W)
        ]);
    }

    internal void AddQuadLogical(IReadOnlyList<Vector3> positions, ProjectionMaterialRequest material,
        IReadOnlyList<Vector2> logicalUv, bool shade = false, bool doubleSided = false)
    {
        if (positions.Count != 4 || logicalUv.Count != 4)
            throw new ArgumentException("A projection quad requires four positions and four UV coordinates.");
        var points = positions.Select(position => Vector3.Transform(position, transform)).ToArray();
        var cross = Vector3.Cross(points[1] - points[0], points[2] - points[0]);
        if (!float.IsFinite(cross.LengthSquared()) || cross.LengthSquared() < 1e-12f) return;
        var mirrored = transform.GetDeterminant() < 0;
        var normal = Vector3.Normalize(mirrored ? -cross : cross);
        if (!_materials.TryGetValue(material, out var geometry))
            _materials.Add(material, geometry = new());
        var first = (uint)geometry.Vertices.Count;
        var uSpan = material.SpriteU1 - material.SpriteU0;
        var vSpan = material.SpriteV1 - material.SpriteV0;
        for (var index = 0; index < 4; index++)
        {
            var uv = logicalUv[index];
            geometry.Vertices.Add(new()
            {
                Position = points[index],
                Normal = normal,
                Uv = new(material.SpriteU0 + uv.X / material.LogicalWidth * uSpan,
                    material.SpriteV0 + uv.Y / material.LogicalHeight * vSpan),
                Color = shade ? Shade(normal) : 0xffffffff
            });
        }
        if (mirrored)
            geometry.Indices.AddRange([first, first + 2, first + 1, first, first + 3, first + 2]);
        else
            geometry.Indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        if (doubleSided)
        {
            var back = (uint)geometry.Vertices.Count;
            for (var index = 0; index < 4; index++)
            {
                var vertex = geometry.Vertices[(int)first + index];
                vertex.Normal = -vertex.Normal;
                geometry.Vertices.Add(vertex);
            }
            geometry.Indices.AddRange(mirrored
                ? [back, back + 1, back + 2, back, back + 2, back + 3]
                : [back, back + 2, back + 1, back, back + 3, back + 2]);
        }
    }

    internal IReadOnlyList<ProjectionSpecialPrimitive> Build() => _materials.Select(pair =>
        new ProjectionSpecialPrimitive(pair.Key, pair.Value.Vertices.ToArray(), pair.Value.Indices.ToArray(),
            ProjectionGeometryBuilder.Bounds(pair.Value.Vertices))).ToArray();

    internal static IReadOnlyList<ProjectionSpecialPrimitive> Bake(ProjectionPartGraph graph,
        ProjectionPose instancePose, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, ProjectionMaterialRequest>? materials = null,
        IReadOnlyDictionary<string, ProjectionPose>? poses = null, Matrix4x4? instanceMatrix = null)
    {
        var output = new ProjectionSpecialMeshBuilder(Matrix4x4.Identity);
        Walk(graph.Root, instanceMatrix ?? instancePose.Matrix);
        return output.Build();

        void Walk(ProjectionPartNode node, Matrix4x4 parent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!node.Visible) return;
            var pose = poses is not null && poses.TryGetValue(node.Name, out var overridePose) ? overridePose : node.Pose;
            var matrix = pose.Matrix * parent;
            var part = new ProjectionSpecialMeshBuilder(matrix);
            foreach (var cuboid in node.Cuboids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                part.AddCuboid(cuboid with { Material = Material(cuboid.Material) });
            }
            foreach (var quad in node.Quads)
                part.AddQuadLogical(quad.Positions, Material(quad.Material), quad.Uv, doubleSided: quad.DoubleSided);
            foreach (var primitive in part.Build()) output.Append(primitive);
            foreach (var child in node.Children) Walk(child, matrix);
        }
        ProjectionMaterialRequest Material(ProjectionMaterialRequest material) =>
            materials is not null && materials.TryGetValue(material.TextureId, out var replacement) ? replacement : material;
    }

    private void AddCuboid(ProjectionCuboid cuboid)
    {
        var origin = cuboid.Uv is { } uv ? new Vector2(uv.X, uv.Y) : Vector2.Zero;
        AddCuboid(cuboid.From, cuboid.To, cuboid.Material, origin, cuboid.UvSize, cuboid.Mirror, cuboid.Faces, cuboid.Shade);
    }

    internal void AddCuboid(Vector3 from, Vector3 to, ProjectionMaterialRequest material, Vector2 uvOrigin,
        Vector3? uvSize = null, bool mirror = false, IReadOnlySet<ProjectionFaceDirection>? faces = null, bool shade = false)
    {
        if (mirror) (from.X, to.X) = (to.X, from.X);
        var p = new[]
        {
            new Vector3(from.X, from.Y, from.Z), new Vector3(to.X, from.Y, from.Z),
            new Vector3(to.X, to.Y, from.Z), new Vector3(from.X, to.Y, from.Z),
            new Vector3(from.X, from.Y, to.Z), new Vector3(to.X, from.Y, to.Z),
            new Vector3(to.X, to.Y, to.Z), new Vector3(from.X, to.Y, to.Z)
        };
        var size = uvSize ?? Vector3.Abs(to - from) * 16;
        var width = size.X;
        var height = size.Y;
        var depth = size.Z;
        var u = uvOrigin.X;
        var v = uvOrigin.Y;
        Face(ProjectionFaceDirection.Down, [5, 4, 0, 1], new(u + depth, v, u + depth + width, v + depth));
        Face(ProjectionFaceDirection.Up, [2, 3, 7, 6], new(u + depth + width, v + depth, u + depth + width + width, v));
        Face(ProjectionFaceDirection.West, [0, 4, 7, 3], new(u, v + depth, u + depth, v + depth + height));
        Face(ProjectionFaceDirection.North, [1, 0, 3, 2], new(u + depth, v + depth, u + depth + width, v + depth + height));
        Face(ProjectionFaceDirection.East, [5, 1, 2, 6], new(u + depth + width, v + depth, u + depth + width + depth, v + depth + height));
        Face(ProjectionFaceDirection.South, [4, 5, 6, 7], new(u + depth + width + depth, v + depth, u + depth + width + depth + width, v + depth + height));

        void Face(ProjectionFaceDirection direction, int[] indices, Vector4 rect)
        {
            if (faces is not null && !faces.Contains(direction)) return;
            Vector2[] uv = [new(rect.Z, rect.Y), new(rect.X, rect.Y), new(rect.X, rect.W), new(rect.Z, rect.W)];
            if (mirror) { Array.Reverse(indices); Array.Reverse(uv); }
            AddQuadLogical(indices.Select(index => p[index]).ToArray(), material, uv, shade);
        }
    }

    private static uint Shade(Vector3 normal)
    {
        var factor = normal.Y > .9f ? 1f : normal.Y < -.9f ? .5f : MathF.Abs(normal.X) > .9f ? .8f : .6f;
        var channel = (uint)(factor * 255);
        return 0xff000000 | channel | channel << 8 | channel << 16;
    }

    private void Append(ProjectionSpecialPrimitive primitive)
    {
        if (!_materials.TryGetValue(primitive.Material, out var geometry))
            _materials.Add(primitive.Material, geometry = new());
        var first = (uint)geometry.Vertices.Count;
        geometry.Vertices.AddRange(primitive.Vertices);
        geometry.Indices.AddRange(primitive.Indices.Select(index => index + first));
    }
}

internal sealed record ProjectionSpecialExecution(
    ProjectionContributionState State, string Evidence);

internal sealed record ProjectionSpecialModelResult(
    bool HasGeometry,
    IReadOnlyList<ProjectionSpecialPrimitive> Primitives,
    IReadOnlyDictionary<ProjectionContributionKind, ProjectionSpecialExecution> Contributions,
    ProjectionContributionBounds Bounds,
    string? Failure = null)
{
    internal static ProjectionSpecialModelResult Unsupported(string evidence) => new(
        false, [], new Dictionary<ProjectionContributionKind, ProjectionSpecialExecution>
        {
            [ProjectionContributionKind.SpecialModel] = new(ProjectionContributionState.Unsupported, evidence)
        }, new(Vector3.Zero, Vector3.One));
}

internal static class ProjectionGeometryBuilder
{
    internal static ProjectionPartGraph Graph(string name, params ProjectionPartNode[] children) =>
        new(name, new("root", ProjectionPose.Identity, [], [], children));

    internal static ProjectionPartNode Part(string name, ProjectionPose pose, IEnumerable<ProjectionCuboid> cuboids,
        IEnumerable<ProjectionPartNode>? children = null, bool visible = true) =>
        new(name, pose, cuboids.ToArray(), [], children?.ToArray() ?? [], visible);

    internal static ProjectionCuboid Cuboid(Vector3 from, Vector3 to, ProjectionMaterialRequest material,
        Vector4? uv = null, bool shade = true, bool cullFaces = false) =>
        new(from, to, material, uv, shade, cullFaces);

    internal static ProjectionMaterialRequest Material(string textureId, ProjectionMaterialRole role,
        int logicalWidth, int logicalHeight, string evidence,
        float spriteU0 = 0, float spriteV0 = 0, float spriteU1 = 1, float spriteV1 = 1) =>
        new(ProjectionResourceSet.NormalizeResourceId(textureId), role, logicalWidth, logicalHeight, evidence,
            spriteU0, spriteV0, spriteU1, spriteV1);

    internal static ProjectionContributionBounds Bounds(IEnumerable<ProjectionMeshVertex> vertices)
    {
        var values = vertices.ToArray();
        if (values.Length == 0) return new(Vector3.Zero, Vector3.Zero);
        var min = values[0].Position;
        var max = min;
        foreach (var vertex in values.Skip(1))
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }
        return new(min, max);
    }

    internal static ProjectionPartGraph FromRoot(string name, ProjectionPartNode root) => new(name, root);
}
