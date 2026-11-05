using System.Numerics;
using System.Text.Json.Serialization;

namespace RZeroF1.Minecraft.Projection;

internal sealed class ProjectionBlockModelDefinition
{
    [JsonPropertyName("parent")]
    public string? Parent { get; init; }

    [JsonPropertyName("textures")]
    public Dictionary<string, string>? Textures { get; init; }

    [JsonPropertyName("elements")]
    public List<ProjectionElementDefinition>? Elements { get; init; }
}

internal sealed class ProjectionElementDefinition
{
    [JsonPropertyName("from")]
    public float[]? From { get; init; }

    [JsonPropertyName("to")]
    public float[]? To { get; init; }

    [JsonPropertyName("rotation")]
    public ProjectionElementRotationDefinition? Rotation { get; init; }

    [JsonPropertyName("faces")]
    public Dictionary<string, ProjectionFaceDefinition>? Faces { get; init; }

    [JsonPropertyName("shade")]
    public bool? Shade { get; init; }

    [JsonPropertyName("shade_direction_override")]
    public string? ShadeDirectionOverride { get; init; }
}

internal sealed class ProjectionElementRotationDefinition
{
    [JsonPropertyName("angle")]
    public float Angle { get; init; }

    [JsonPropertyName("axis")]
    public string Axis { get; init; } = "y";

    [JsonPropertyName("origin")]
    public float[]? Origin { get; init; }

    [JsonPropertyName("rescale")]
    public bool? Rescale { get; init; }
}

internal sealed class ProjectionFaceDefinition
{
    [JsonPropertyName("uv")]
    public float[]? Uv { get; init; }

    [JsonPropertyName("texture")]
    public string Texture { get; init; } = string.Empty;

    [JsonPropertyName("rotation")]
    public int? Rotation { get; init; }

    [JsonPropertyName("tintindex")]
    public int? TintIndex { get; init; }

    [JsonPropertyName("cullface")]
    public string? CullFace { get; init; }
}

internal sealed class ProjectionModelInstance
{
    public required string Name { get; init; }
    public required IReadOnlyDictionary<string, string> Textures { get; init; }
    public required IReadOnlyList<ProjectionModelElement> Elements { get; init; }
}

internal sealed class ProjectionModelElement
{
    public required Vector3 From { get; init; }
    public required Vector3 To { get; init; }
    public ProjectionElementRotation? Rotation { get; init; }
    public required IReadOnlyDictionary<ProjectionFaceDirection, ProjectionModelFace> Faces { get; init; }
    public bool Shade { get; init; } = true;
    public string? ShadeDirectionOverride { get; init; }
}

internal sealed record ProjectionElementRotation(float AngleDegrees, Vector3 Origin, string Axis, bool Rescale);

internal sealed record ProjectionModelFace(
    string Texture,
    Vector4? Uv,
    int Rotation,
    int? TintIndex,
    string? CullFace);

internal enum ProjectionFaceDirection
{
    North,
    South,
    East,
    West,
    Up,
    Down
}

internal static class ProjectionModelGeometry
{
    public static readonly IReadOnlyDictionary<ProjectionFaceDirection, int[]> FaceVertexIndices =
        new Dictionary<ProjectionFaceDirection, int[]>
        {
            [ProjectionFaceDirection.Down] = [4, 0, 1, 5],
            [ProjectionFaceDirection.Up] = [3, 7, 6, 2],
            [ProjectionFaceDirection.North] = [2, 1, 0, 3],
            [ProjectionFaceDirection.South] = [7, 4, 5, 6],
            [ProjectionFaceDirection.West] = [3, 0, 4, 7],
            [ProjectionFaceDirection.East] = [6, 5, 1, 2]
        };

    public static Vector4 DefaultFaceUv(Vector3 from, Vector3 to, ProjectionFaceDirection direction) =>
        direction switch
        {
            ProjectionFaceDirection.Down => new Vector4(from.X, 16f - to.Z, to.X, 16f - from.Z),
            ProjectionFaceDirection.Up => new Vector4(from.X, from.Z, to.X, to.Z),
            ProjectionFaceDirection.North => new Vector4(16f - to.X, 16f - to.Y, 16f - from.X, 16f - from.Y),
            ProjectionFaceDirection.South => new Vector4(from.X, 16f - to.Y, to.X, 16f - from.Y),
            ProjectionFaceDirection.West => new Vector4(from.Z, 16f - to.Y, to.Z, 16f - from.Y),
            ProjectionFaceDirection.East => new Vector4(16f - to.Z, 16f - to.Y, 16f - from.Z, 16f - from.Y),
            _ => new Vector4(0, 0, 16, 16)
        };

    public static Vector2[] CreateUvMap(Vector4 uv, int rotationDegrees)
    {
        var normalized = ((rotationDegrees % 360) + 360) % 360;
        var quadrant = normalized switch
        {
            90 => 1,
            180 => 2,
            270 => 3,
            _ => 0
        };
        var result = new Vector2[4];
        for (var i = 0; i < 4; i++)
        {
            var shifted = (i + quadrant) % 4;
            var u = (shifted is 0 or 1 ? uv.X : uv.Z) / 16f;
            var v = (shifted is 0 or 3 ? uv.Y : uv.W) / 16f;
            result[i] = new Vector2(u, v);
        }
        return result;
    }

    public static bool TryParseFaceDirection(string key, out ProjectionFaceDirection direction) =>
        Enum.TryParse(key, true, out direction);
}
