using System.Text.Json;

namespace RZeroF1.Minecraft.Projection;

internal static class ProjectionStateSelection
{
    public static IEnumerable<ProjectionModelPlacement> Select(
        JsonElement root,
        IReadOnlyDictionary<string, string> properties,
        int seed)
    {
        if (root.TryGetProperty("variants", out var variants) &&
            variants.ValueKind == JsonValueKind.Object)
        {
            foreach (var variant in variants.EnumerateObject())
            {
                if (variant.Name.Length == 0 || MatchesVariant(variant.Name, properties))
                {
                    yield return ReadPlacement(Pick(variant.Value, seed));
                    break;
                }
            }
        }

        if (root.TryGetProperty("multipart", out var multipart) &&
            multipart.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in multipart.EnumerateArray())
            {
                if (!part.TryGetProperty("when", out var when) || MatchesCondition(when, properties))
                {
                    if (part.TryGetProperty("apply", out var apply))
                        yield return ReadPlacement(Pick(apply, seed));
                }
            }
        }
    }

    private static bool MatchesVariant(string key, IReadOnlyDictionary<string, string> properties) =>
        key.Split(',', StringSplitOptions.RemoveEmptyEntries).All(clause =>
        {
            var pair = clause.Split('=', 2);
            return pair.Length == 2 &&
                   properties.TryGetValue(pair[0], out var value) &&
                   pair[1].Split('|').Contains(value, StringComparer.OrdinalIgnoreCase);
        });

    private static bool MatchesCondition(JsonElement condition, IReadOnlyDictionary<string, string> properties)
    {
        if (condition.ValueKind != JsonValueKind.Object)
            return true;
        foreach (var clause in condition.EnumerateObject())
        {
            if (clause.NameEquals("OR"))
            {
                if (clause.Value.ValueKind != JsonValueKind.Array ||
                    !clause.Value.EnumerateArray().Any(item => MatchesCondition(item, properties)))
                    return false;
                continue;
            }
            if (clause.NameEquals("AND"))
            {
                if (clause.Value.ValueKind != JsonValueKind.Array ||
                    !clause.Value.EnumerateArray().All(item => MatchesCondition(item, properties)))
                    return false;
                continue;
            }
            if (!properties.TryGetValue(clause.Name, out var value) ||
                clause.Value.ValueKind != JsonValueKind.String ||
                !clause.Value.GetString()!.Split('|').Contains(value, StringComparer.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static JsonElement Pick(JsonElement value, int seed)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return value;
        var choices = value.EnumerateArray().ToArray();
        if (choices.Length == 0)
            throw new JsonException("模型候选数组为空。");
        var total = choices.Sum(static item =>
            item.TryGetProperty("weight", out var weight) && weight.TryGetInt32(out var parsed)
                ? Math.Max(1, parsed)
                : 1);
        var hash = unchecked((uint)seed * 747796405u + 2891336453u);
        hash = ((hash >> ((int)(hash >> 28) + 4)) ^ hash) * 277803737u;
        hash = (hash >> 22) ^ hash;
        var selected = (int)(hash % (uint)total);
        foreach (var choice in choices)
        {
            selected -= choice.TryGetProperty("weight", out var weight) && weight.TryGetInt32(out var parsed)
                ? Math.Max(1, parsed)
                : 1;
            if (selected < 0)
                return choice;
        }
        return choices[^1];
    }

    private static ProjectionModelPlacement ReadPlacement(JsonElement value) =>
        new(
            ProjectionModelResolver.NormalizeModelId(value.GetProperty("model").GetString() ?? string.Empty),
            value.TryGetProperty("x", out var x) ? x.GetInt32() : 0,
            value.TryGetProperty("y", out var y) ? y.GetInt32() : 0,
            value.TryGetProperty("uvlock", out var uvlock) && uvlock.ValueKind == JsonValueKind.True);
}

internal readonly record struct ProjectionModelPlacement(
    string Model,
    int RotationX,
    int RotationY,
    bool UvLock);
