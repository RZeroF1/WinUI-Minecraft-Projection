namespace RZeroF1.Minecraft.Projection;

/// <summary>Immutable display policy for a projection session. Source blocks and material counts are retained.</summary>
public sealed record ProjectionRenderOptions
{
    /// <summary>
    /// Hides vanilla glass panes whose north/east/south/west connections are all explicitly false.
    /// This preview filter also hides intentionally placed unconnected panes; default is true.
    /// Set false to retain their source geometry.
    /// </summary>
    public bool HideIsolatedGlassPanes { get; init; } = true;
}
