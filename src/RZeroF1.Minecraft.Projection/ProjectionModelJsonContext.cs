using System.Text.Json.Serialization;

namespace RZeroF1.Minecraft.Projection;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ProjectionBlockModelDefinition))]
internal sealed partial class ProjectionModelJsonContext : JsonSerializerContext;
