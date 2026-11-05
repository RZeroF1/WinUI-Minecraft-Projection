using System.Numerics;

namespace RZeroF1.Minecraft.Projection;

internal sealed class ProjectionModelResolver
{
    private readonly IReadOnlyDictionary<string, ProjectionBlockModelDefinition> _definitions;
    private readonly Dictionary<string, ProjectionModelInstance> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ProjectionModelResolver(IReadOnlyDictionary<string, ProjectionBlockModelDefinition> definitions)
    {
        _definitions = definitions;
    }

    public ProjectionModelInstance Resolve(string name)
    {
        var normalized = NormalizeModelId(name);
        if (_cache.TryGetValue(normalized, out var cached))
            return cached;
        var result = ResolveInternal(normalized, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _cache[normalized] = result;
        return result;
    }

    private ProjectionModelInstance ResolveInternal(string name, HashSet<string> stack)
    {
        if (!stack.Add(name))
            throw new InvalidOperationException($"模型继承环：{name}");
        if (!_definitions.TryGetValue(name, out var definition))
            throw new KeyNotFoundException($"找不到模型 {name}");

        var textures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var elements = new List<ProjectionModelElement>();
        if (!string.IsNullOrWhiteSpace(definition.Parent))
        {
            var parent = ResolveInternal(NormalizeModelId(definition.Parent!), stack);
            foreach (var pair in parent.Textures)
                textures[pair.Key] = pair.Value;
            elements.AddRange(parent.Elements.Select(CloneElement));
        }

        if (definition.Textures is not null)
        {
            foreach (var pair in definition.Textures)
                textures[pair.Key] = pair.Value;
        }

        if (definition.Elements is not null)
        {
            elements.Clear();
            elements.AddRange(definition.Elements
                .Select(ConvertElement)
                .Where(static element => element is not null)
                .Select(static element => element!));
        }

        stack.Remove(name);
        return new ProjectionModelInstance
        {
            Name = name,
            Textures = textures,
            Elements = elements
        };
    }

    private static ProjectionModelElement? ConvertElement(ProjectionElementDefinition definition)
    {
        if (definition.From is not { Length: 3 } from || definition.To is not { Length: 3 } to)
            return null;

        ProjectionElementRotation? rotation = null;
        if (definition.Rotation is { } source && source.Origin is { Length: 3 } origin)
        {
            rotation = new ProjectionElementRotation(
                source.Angle,
                new Vector3(origin[0], origin[1], origin[2]),
                source.Axis.ToLowerInvariant(),
                source.Rescale ?? false);
        }

        var faces = new Dictionary<ProjectionFaceDirection, ProjectionModelFace>();
        if (definition.Faces is not null)
        {
            foreach (var (key, face) in definition.Faces)
            {
                if (!ProjectionModelGeometry.TryParseFaceDirection(key, out var direction))
                    continue;
                Vector4? uv = face.Uv is { Length: 4 }
                    ? new Vector4(face.Uv[0], face.Uv[1], face.Uv[2], face.Uv[3])
                    : null;
                faces[direction] = new ProjectionModelFace(
                    face.Texture,
                    uv,
                    face.Rotation ?? 0,
                    face.TintIndex,
                    face.CullFace);
            }
        }

        return new ProjectionModelElement
        {
            From = new Vector3(from[0], from[1], from[2]),
            To = new Vector3(to[0], to[1], to[2]),
            Rotation = rotation,
            Faces = faces,
            Shade = definition.Shade ?? true,
            ShadeDirectionOverride = definition.ShadeDirectionOverride
        };
    }

    private static ProjectionModelElement CloneElement(ProjectionModelElement source) =>
        new()
        {
            From = source.From,
            To = source.To,
            Rotation = source.Rotation is null
                ? null
                : new ProjectionElementRotation(
                    source.Rotation.AngleDegrees,
                    source.Rotation.Origin,
                    source.Rotation.Axis,
                    source.Rotation.Rescale),
            Faces = source.Faces.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value with { }),
            Shade = source.Shade,
            ShadeDirectionOverride = source.ShadeDirectionOverride
        };

    public static string NormalizeModelId(string name)
    {
        var value = name.Trim();
        if (!value.Contains(':', StringComparison.Ordinal))
            value = "minecraft:" + value;
        return value.ToLowerInvariant();
    }
}
