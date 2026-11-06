using System.Runtime.InteropServices;
using System.Text.Json;

namespace RZeroF1.Minecraft.Projection;

internal sealed record ProjectionTextureImage(byte[] Png, bool HasTransparency, string? Failure = null);

internal static class ProjectionTextureLoader
{
    internal static readonly byte[] MissingPng = CreateMissing();

    internal static ProjectionTextureImage Load(byte[]? png, byte[]? metadata)
    {
        if (png is null)
            return new(MissingPng, false, "MissingTexture");
        var result = Decode(png, (uint)png.Length, out var pixels, out var width, out var height);
        if (width > 16384 || height > 16384 || (long)width * height > 16777216)
            throw new InvalidDataException("Decoded texture exceeds the 16M pixel budget.");
        if (result < 0)
        {
            if (result == unchecked((int)0x8007000e))
                Marshal.ThrowExceptionForHR(result);
            return new(MissingPng, false, "TextureDecodeFailure");
        }
        try
        {
            var bytes = new byte[checked((int)(width * height * 4))];
            Marshal.Copy(pixels, bytes, 0, bytes.Length);
            var frameWidth = (int)width;
            var frameHeight = (int)height;
            var frameIndex = 0;
            if (metadata is not null)
            {
                using var json = JsonDocument.Parse(metadata);
                if (json.RootElement.TryGetProperty("animation", out var animation))
                {
                    var hasWidth = animation.TryGetProperty("width", out var w);
                    var hasHeight = animation.TryGetProperty("height", out var h);
                    frameWidth = hasWidth ? w.GetInt32() : hasHeight ? (int)width : (int)Math.Min(width, height);
                    frameHeight = hasHeight ? h.GetInt32() : hasWidth ? (int)height : (int)Math.Min(width, height);
                    if (animation.TryGetProperty("frames", out var frames) && frames.GetArrayLength() > 0)
                    {
                        var first = frames[0];
                        frameIndex = first.ValueKind == JsonValueKind.Number ? first.GetInt32() : first.GetProperty("index").GetInt32();
                    }
                }
            }
            if (frameWidth <= 0 || frameHeight <= 0 || width % frameWidth != 0 || height % frameHeight != 0 ||
                frameIndex < 0 || frameIndex >= (long)(width / frameWidth) * (height / frameHeight))
                return new(MissingPng, false, "InvalidAnimationMetadata");
            var frame = new byte[checked(frameWidth * frameHeight * 4)];
            var left = frameIndex % ((int)width / frameWidth) * frameWidth;
            var top = frameIndex / ((int)width / frameWidth) * frameHeight;
            for (var y = 0; y < frameHeight; y++)
                bytes.AsSpan(((top + y) * (int)width + left) * 4, frameWidth * 4).CopyTo(frame.AsSpan(y * frameWidth * 4));
            var transparent = false;
            for (var i = 3; i < frame.Length; i += 4)
                transparent |= frame[i] != 255;
            return new(ProjectionPng.EncodeBgra(frame, frameWidth, frameHeight), transparent);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            return new(MissingPng, false, "InvalidAnimationMetadata");
        }
        finally
        {
            Free(pixels);
        }
    }

    private static byte[] CreateMissing()
    {
        var pixels = new byte[16 * 16 * 4];
        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
        {
            var i = (y * 16 + x) * 4;
            pixels[i] = pixels[i + 2] = ((x / 4 + y / 4) & 1) == 0 ? (byte)12 : (byte)230;
            pixels[i + 3] = 255;
        }
        return ProjectionPng.EncodeBgra(pixels, 16, 16);
    }

    [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_image_decode", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Decode(byte[] png, uint size, out nint pixels, out uint width, out uint height);

    [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_image_free", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Free(nint pixels);
}
