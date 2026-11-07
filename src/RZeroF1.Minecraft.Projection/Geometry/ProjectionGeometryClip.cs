using System.Numerics;

namespace RZeroF1.Minecraft.Projection;

internal static class ProjectionGeometryClip
{
    internal static IReadOnlyList<ProjectionSpecialPrimitive> Transform(IReadOnlyList<ProjectionSpecialPrimitive> source,
        Matrix4x4 matrix, CancellationToken cancellationToken)
    {
        var result = new List<ProjectionSpecialPrimitive>();
        foreach (var primitive in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vertices = primitive.Vertices.Select(vertex =>
            {
                vertex.Position = Vector3.Transform(vertex.Position, matrix);
                vertex.Normal = Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, matrix));
                return vertex;
            }).ToArray();
            result.Add(new(primitive.Material, vertices, primitive.Indices, ProjectionGeometryBuilder.Bounds(vertices)));
        }
        return result;
    }

    // The legacy double chest is one UV net. Each source owns its original half,
    // including the matching portion of the latch; clipping never adds a false cap.
    internal static IReadOnlyList<ProjectionSpecialPrimitive> BetweenX(IReadOnlyList<ProjectionSpecialPrimitive> source,
        float minimum, float maximum, CancellationToken cancellationToken)
    {
        var result = new List<ProjectionSpecialPrimitive>();
        foreach (var primitive in source)
        {
            var vertices = new List<ProjectionMeshVertex>();
            var indices = new List<uint>();
            for (var index = 0; index < primitive.Indices.Length; index += 3)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var polygon = new List<ProjectionMeshVertex>
                {
                    primitive.Vertices[primitive.Indices[index]],
                    primitive.Vertices[primitive.Indices[index + 1]],
                    primitive.Vertices[primitive.Indices[index + 2]]
                };
                polygon = Clip(polygon, minimum, lower: true);
                polygon = Clip(polygon, maximum, lower: false);
                if (polygon.Count < 3) continue;
                for (var corner = 1; corner < polygon.Count - 1; corner++)
                {
                    if (Vector3.Cross(polygon[corner].Position - polygon[0].Position,
                        polygon[corner + 1].Position - polygon[0].Position).LengthSquared() < 1e-12f) continue;
                    var first = (uint)vertices.Count;
                    vertices.AddRange([polygon[0], polygon[corner], polygon[corner + 1]]);
                    indices.AddRange([first, first + 1, first + 2]);
                }
            }
            if (indices.Count != 0)
                result.Add(new(primitive.Material, vertices.ToArray(), indices.ToArray(), ProjectionGeometryBuilder.Bounds(vertices)));
        }
        return result;
    }

    private static List<ProjectionMeshVertex> Clip(List<ProjectionMeshVertex> polygon, float coordinate, bool lower)
    {
        var result = new List<ProjectionMeshVertex>();
        for (var index = 0; index < polygon.Count; index++)
        {
            var a = polygon[index];
            var b = polygon[(index + 1) % polygon.Count];
            var insideA = lower ? a.Position.X >= coordinate : a.Position.X <= coordinate;
            var insideB = lower ? b.Position.X >= coordinate : b.Position.X <= coordinate;
            if (insideA) result.Add(a);
            if (insideA == insideB) continue;
            var t = (coordinate - a.Position.X) / (b.Position.X - a.Position.X);
            result.Add(new()
            {
                Position = Vector3.Lerp(a.Position, b.Position, t),
                Normal = a.Normal,
                Uv = Vector2.Lerp(a.Uv, b.Uv, t),
                Color = a.Color
            });
        }
        return result;
    }
}
