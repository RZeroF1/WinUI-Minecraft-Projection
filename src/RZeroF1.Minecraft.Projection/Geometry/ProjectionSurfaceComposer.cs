using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace RZeroF1.Minecraft.Projection;

internal sealed record ProjectionComposedSurface(string TextureId, string? Failure);

internal static class ProjectionSurfaceComposer
{
    private static readonly string[] DyeNames = ["white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray",
        "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black"];
    private static readonly uint[] DyeRgb = [0xf9fffe, 0xf9801d, 0xc74ebd, 0x3ab3da, 0xfed83d, 0x80c71f, 0xf38baa, 0x474f52,
        0x9d9d97, 0x169c9c, 0x8932b8, 0x3c44aa, 0x835432, 0x5e7c16, 0xb02e26, 0x1d1d21];

    internal static ProjectionComposedSurface ComposeBanner(ProjectionResourceSet resources,
        ProjectionBannerVisual visual, CancellationToken cancellationToken)
    {
        if (visual.Layers.Count > 16) return new("", "BannerLayerBudgetExceeded");
        if (!TryDye(visual.BaseColor, out var baseColor)) return new("", "UnknownBannerBaseColor");
        var baseId = resources.ReadTexture("minecraft:entity/banner_base") is not null
            ? "minecraft:entity/banner_base" : "minecraft:entity/banner/banner_base";
        var baseTexture = resources.LoadTexture(baseId);
        if (baseTexture.Failure is not null) return new("", baseTexture.Failure);
        ProjectionPng.DecodedImage baseImage;
        try { baseImage = ProjectionPng.DecodeCanonicalPng(baseTexture.Png); }
        catch (InvalidDataException exception) { return new("", exception.Message); }
        if (baseImage.Width != 64 || baseImage.Height != 64)
            return new("", "UnsupportedBannerCanvas");
        var pixels = baseImage.Rgba.ToArray();
        // The 20x40x1 vanilla flag cube occupies the top 42x41 area of the
        // atlas; the pole and crossbar are below that region.
        for (var y = 1; y <= 41; y++)
        for (var x = 1; x <= 42; x++)
        {
            var offset = (y * baseImage.Width + x) * 4;
            pixels[offset] = (byte)(baseImage.Rgba[offset] * ((baseColor >> 16) & 255) / 255);
            pixels[offset + 1] = (byte)(baseImage.Rgba[offset + 1] * ((baseColor >> 8) & 255) / 255);
            pixels[offset + 2] = (byte)(baseImage.Rgba[offset + 2] * (baseColor & 255) / 255);
        }
        string? unsupportedLayer = null;
        foreach (var layer in visual.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryDye(layer.Color, out var tint)) { unsupportedLayer = "UnknownBannerDye:" + layer.Color; continue; }
            var patternId = resources.ResolveLegacyBannerPattern(layer.Pattern) ?? layer.Pattern;
            var textureId = resources.ResolveBannerPatternTexture(patternId, layer.Color);
            if (textureId is null) { unsupportedLayer ??= "UnknownBannerPattern:" + layer.Pattern; continue; }
            var source = resources.LoadTexture(textureId);
            if (source.Failure is not null) { unsupportedLayer ??= source.Failure; continue; }
            ProjectionPng.DecodedImage overlay;
            try { overlay = ProjectionPng.DecodeCanonicalPng(source.Png); }
            catch (InvalidDataException exception) { unsupportedLayer ??= exception.Message; continue; }
            if (overlay.Width != baseImage.Width || overlay.Height != baseImage.Height)
            { unsupportedLayer ??= "BannerPatternCanvasMismatch:" + textureId; continue; }
            CompositeCloth(pixels, overlay.Rgba, baseImage.Width, tint, cancellationToken);
        }
        var key = new StringBuilder("banner-v1|").Append(resources.Generation).Append('|').Append(baseImage.Width).Append('x').Append(baseImage.Height)
            .Append('|').Append(visual.BaseColor);
        foreach (var layer in visual.Layers) key.Append('|').Append(layer.Pattern).Append(':').Append(layer.Color);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.ToString())))[..24].ToLowerInvariant();
        var id = $"minecraft:__derived/banner/{resources.Generation}-{digest}";
        var png = ProjectionPng.EncodeRgba(pixels, baseImage.Width, baseImage.Height);
        resources.RegisterDerivedTexture(id, png, ProjectionPng.Decode(png).HasTransparency);
        return new(id, unsupportedLayer);
    }

    internal static byte[]? DecodeHexGlyph(ReadOnlySpan<byte> packed, int width, int height)
    {
        if (width is < 1 or > 64 || height is < 1 or > 64 || packed.Length != (width * height + 7) / 8)
            return null;
        var rgba = new byte[checked(width * height * 4)];
        for (var pixel = 0; pixel < width * height; pixel++)
        {
            var set = (packed[pixel >> 3] & (1 << (7 - (pixel & 7)))) != 0;
            var offset = pixel * 4;
            rgba[offset] = rgba[offset + 1] = rgba[offset + 2] = 255;
            rgba[offset + 3] = set ? (byte)255 : (byte)0;
        }
        return rgba;
    }

    private static bool TryDye(string name, out uint rgb)
    {
        var index = Array.IndexOf(DyeNames, name);
        if (index < 0) { rgb = 0; return false; }
        rgb = DyeRgb[index];
        return true;
    }

    private static void CompositeCloth(Span<byte> destination, ReadOnlySpan<byte> source, int width, uint tint, CancellationToken ct)
    {
        var red = (tint >> 16) & 255; var green = (tint >> 8) & 255; var blue = tint & 255;
        for (var y = 1; y <= 41; y++)
        for (var x = 1; x <= 42; x++)
        {
            var offset = (y * width + x) * 4;
            if ((offset & 0x3fff) == 0) ct.ThrowIfCancellationRequested();
            var sourceAlpha = source[offset + 3];
            if (sourceAlpha == 0) continue;
            var alpha = sourceAlpha + destination[offset + 3] * (255 - sourceAlpha) / 255;
            if (alpha == 0) continue;
            var sr = source[offset] * red / 255; var sg = source[offset + 1] * green / 255; var sb = source[offset + 2] * blue / 255;
            var da = destination[offset + 3];
            destination[offset] = (byte)((sr * sourceAlpha + destination[offset] * da * (255 - sourceAlpha) / 255) / alpha);
            destination[offset + 1] = (byte)((sg * sourceAlpha + destination[offset + 1] * da * (255 - sourceAlpha) / 255) / alpha);
            destination[offset + 2] = (byte)((sb * sourceAlpha + destination[offset + 2] * da * (255 - sourceAlpha) / 255) / alpha);
            destination[offset + 3] = (byte)alpha;
        }
    }
}
