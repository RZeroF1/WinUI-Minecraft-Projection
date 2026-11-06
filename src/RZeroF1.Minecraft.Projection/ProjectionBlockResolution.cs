using System.Numerics;
using System.Text.Json;

namespace RZeroF1.Minecraft.Projection;

/// <summary>Grouped rendering diagnostics. Positions contains up to 16 source-coordinate samples.</summary>
public sealed record ProjectionRenderDiagnostic(
    string SourcePath, string ClientJarPath, string BlockId, string Blockstate, string? Region,
    string Reason, string? Model, string? Texture, long Count, IReadOnlyList<ProjectionPosition> Positions);

internal sealed partial class ProjectionModelBaker
{
    private static bool IsInvisible(ProjectionState state) => state.IsAir ||
        state.Name is "minecraft:barrier" or "minecraft:light" or "minecraft:structure_void";

    private bool IsPreviewFiltered(ProjectionState state) => _renderOptions.HideIsolatedGlassPanes &&
        state.Name.StartsWith("minecraft:", StringComparison.Ordinal) &&
        (state.Name == "minecraft:glass_pane" || state.Name.EndsWith("_stained_glass_pane", StringComparison.Ordinal)) &&
        new[] { "north", "east", "south", "west" }.All(direction =>
            state.Properties.TryGetValue(direction, out var connection) && connection == "false");

    private void PrepareBlocks(ProjectionDocument document, CancellationToken cancellationToken)
    {
        _resolved.Clear();
        _diagnostics.Clear();
        _opaqueFullCubes.Clear();
        _blendFace = 0;
        foreach (var block in document.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = document.Palette[block.StateIndex];
            var parts = new List<(ProjectionModelPlacement, ProjectionModelInstance)>();
            _resolved[block.Position] = [];
            if (IsInvisible(state))
            {
                Diagnose(block, state, "IntentionalInvisible");
                continue;
            }
            if (IsPreviewFiltered(state))
            {
                Diagnose(block, state, "PreviewFilteredIsolatedGlassPane");
                continue;
            }
            if (state.Name is "minecraft:water" or "minecraft:lava") continue;
            string? failure = null, failedModel = null, failedTexture = null;
            try
            {
                var json = ReadBlockstate(state.Name);
                var selected = json is { ValueKind: JsonValueKind.Object } root
                    ? ProjectionStateSelection.Select(root, state.Properties, StableSeed(block.Position)).ToArray() : [];
                if (selected.Length == 0) failure = json is null ? _blockstateFailures.GetValueOrDefault(state.Name, "MissingBlockstate") : "NoMatchingVariant";
                foreach (var placement in selected)
                {
                    failedModel = placement.Model;
                    var model = _resolver.Resolve(placement.Model);
                    if (model.Elements.Count == 0)
                    {
                        var id = state.Name.Split(':', 2)[1];
                        failure = state.Name.StartsWith("minecraft:", StringComparison.Ordinal) && (id.Contains("chest", StringComparison.Ordinal) || id.Contains("shulker_box", StringComparison.Ordinal) ||
                            id.EndsWith("_head", StringComparison.Ordinal) || id.EndsWith("_skull", StringComparison.Ordinal) ||
                            id.Contains("banner", StringComparison.Ordinal) || id.EndsWith("_bed", StringComparison.Ordinal) ||
                            id.Contains("sign", StringComparison.Ordinal) || id.Contains("golem_statue", StringComparison.Ordinal) ||
                            id is "conduit" or "decorated_pot" or "moving_piston")
                            ? "UnsupportedRenderer" : "EmptyModelUnknownIntent";
                        break;
                    }
                    if (!model.Elements.Any(element => element.Faces.Keys.Any(direction =>
                    {
                        var corners = CreateCorners(element.From / 16, element.To / 16);
                        TransformCorners(corners, element.Rotation, Matrix4x4.Identity);
                        var indices = ProjectionModelGeometry.FaceVertexIndices[direction];
                        var area = Vector3.Cross(corners[indices[1]] - corners[indices[0]], corners[indices[2]] - corners[indices[0]]).LengthSquared();
                        return float.IsFinite(area) && area >= 1e-12f;
                    })))
                    {
                        failure = "InvalidModelGeometry";
                        break;
                    }
                    foreach (var element in model.Elements)
                    foreach (var face in element.Faces.Values)
                    {
                        var texture = ResolveTexture(model, face.Texture);
                        var image = _resources.LoadTexture(texture);
                        if (texture == "__missing__" || image.Failure is not null)
                        {
                            failure = image.Failure ?? "UnresolvedTextureReference";
                            failedTexture = texture;
                            break;
                        }
                    }
                    if (failure is not null) break;
                    parts.Add((placement, model));
                }
            }
            catch (KeyNotFoundException) { failure = "MissingModelOrParent"; }
            catch (JsonException) { failure = "InvalidBlockstate"; }
            catch (InvalidOperationException) { failure = "InvalidModel"; }
            if (failure is not null)
            {
                Diagnose(block, state, failure, failedModel, failedTexture);
                _resolved[block.Position] = [(new("__missing__", 0, 0, false), MissingModel())];
                continue;
            }
            _resolved[block.Position] = parts.ToArray();
            if (parts.Count == 1 && IsFullCube(parts[0].Item2) && parts[0].Item2.Elements[0].Faces.Values.All(face =>
                RenderMode(state.Name, ResolveTexture(parts[0].Item2, face.Texture)) == 0 &&
                !_resources.LoadTexture(ResolveTexture(parts[0].Item2, face.Texture)).HasTransparency))
                _opaqueFullCubes.Add(block.Position);
            if (state.Properties.TryGetValue("waterlogged", out var waterlogged) && waterlogged == "true")
                Diagnose(block, state, "WaterloggedFluidUnsupported");
        }
    }

    private static ProjectionFaceDirection RotateDirection(ProjectionFaceDirection direction, Matrix4x4 rotation)
    {
        var vector = direction switch
        {
            ProjectionFaceDirection.North => -Vector3.UnitZ, ProjectionFaceDirection.South => Vector3.UnitZ,
            ProjectionFaceDirection.East => Vector3.UnitX, ProjectionFaceDirection.West => -Vector3.UnitX,
            ProjectionFaceDirection.Up => Vector3.UnitY, _ => -Vector3.UnitY
        };
        vector = Vector3.TransformNormal(vector, rotation);
        return MathF.Abs(vector.Y) > .9f ? vector.Y > 0 ? ProjectionFaceDirection.Up : ProjectionFaceDirection.Down :
            MathF.Abs(vector.X) > .9f ? vector.X > 0 ? ProjectionFaceDirection.East : ProjectionFaceDirection.West :
            vector.Z > 0 ? ProjectionFaceDirection.South : ProjectionFaceDirection.North;
    }

    private void Diagnose(ProjectionBlock block, ProjectionState state, string code, string? model = null, string? texture = null)
    {
        var key = (state.Key, code, model, texture, block.Region);
        if (!_diagnostics.TryGetValue(key, out var diagnostic))
        {
            diagnostic = new DiagnosticBuilder(state, block.Region, code, model, texture);
            _diagnostics.Add(key, diagnostic);
            if (code is not "IntentionalInvisible" and not "PreviewFilteredIsolatedGlassPane")
                AddWarning($"{code}: {state.Key} @ ({block.Position.X},{block.Position.Y},{block.Position.Z}); model={model}; texture={texture}");
        }
        diagnostic.Count++;
        if (diagnostic.Positions.Count < 16) diagnostic.Positions.Add(block.Position);
    }

    private sealed class DiagnosticBuilder(ProjectionState state, string? region, string code, string? model, string? texture)
    {
        public long Count;
        public List<ProjectionPosition> Positions { get; } = [];
        public ProjectionRenderDiagnostic ToResult(string path, string jar) =>
            new(path, jar, state.Name, state.Key, region, code, model, texture, Count, Array.AsReadOnly(Positions.ToArray()));
    }
}
