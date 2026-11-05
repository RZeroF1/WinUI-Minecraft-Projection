using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
namespace RZeroF1.Minecraft.Projection;

internal sealed class ProjectionNativeRenderer : IDisposable
{
    private nint _renderer;
    private uint _width;
    private uint _height;
    private bool _disposed;
    private readonly Dictionary<string, DecodedTexture> _decodedTextures = new(StringComparer.OrdinalIgnoreCase);

    internal bool IsCreated => _renderer != 0;

    internal uint FeatureLevel { get; private set; }

    internal ulong GpuBytesEstimate =>
        _renderer == 0 ? 0 : Native.Memory(_renderer);

    internal Task UploadAsync(
        ProjectionBakedScene scene,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_renderer == 0)
            return Task.CompletedTask;
        Check(Native.Clear(_renderer));
        ReleaseDecodedTextures();
        return UploadBatchesAsync(scene.Batches, scene.TexturePng, slice: false, cancellationToken);
    }

    internal Task UploadSliceAsync(
        ProjectionBakedScene scene,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_renderer == 0)
            return Task.CompletedTask;
        Check(Native.ClearSlice(_renderer));
        return UploadBatchesAsync(scene.Batches, scene.TexturePng, slice: true, cancellationToken);
    }

    internal void ClearSlice()
    {
        if (_renderer != 0)
            Check(Native.ClearSlice(_renderer));
    }

    internal void SetClip(int axis, float minimum, float maximum)
    {
        if (_renderer != 0)
            Check(Native.Clip(_renderer, axis, minimum, maximum));
    }

    internal void SetCamera(Vector3 eye, Vector3 target)
    {
        if (_renderer != 0)
            Check(Native.Camera(_renderer, eye.X, eye.Y, eye.Z, target.X, target.Y, target.Z));
    }

    internal void Clear()
    {
        if (_renderer != 0)
        {
            Check(Native.Clear(_renderer));
            ReleaseDecodedTextures();
        }
    }

    internal void Draw()
    {
        if (_renderer != 0)
            Check(Native.Draw(_renderer));
    }

    internal Task<byte[]> CapturePngAsync(CancellationToken cancellationToken = default)
    {
        if (_renderer == 0)
            throw new InvalidOperationException("D3D11 viewport has not been created.");
        cancellationToken.ThrowIfCancellationRequested();
        var pixels = new byte[checked((int)(_width * _height * 4))];
        Check(Native.Pixels(_renderer, pixels, (uint)pixels.Length));
        return Task.FromResult(ProjectionPng.EncodeBgra(pixels, checked((int)_width), checked((int)_height)));
    }

    internal void Attach(nint swapChainPanel, uint width, uint height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_renderer != 0)
            return;

        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        Check(Native.Create(swapChainPanel, _width, _height, out _renderer));
        FeatureLevel = Native.Feature(_renderer);
    }

    internal void Resize(uint width, uint height)
    {
        if (_renderer == 0)
            return;
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _width && height == _height)
            return;
        _width = width;
        _height = height;
        Check(Native.Resize(_renderer, _width, _height));
    }

    internal void Detach()
    {
        if (_renderer == 0)
            return;
        ReleaseDecodedTextures();
        Native.Destroy(_renderer);
        _renderer = 0;
        FeatureLevel = 0;
        _width = 0;
        _height = 0;
    }

    private void ReleaseDecodedTextures()
    {
        foreach (var texture in _decodedTextures.Values)
            Native.DestroyTexture(texture.Handle);
        _decodedTextures.Clear();
    }

    internal void ReleaseDecodedTextureCache() => ReleaseDecodedTextures();

    private Task UploadBatchesAsync(
        IReadOnlyList<ProjectionMaterialBatch> batches,
        IReadOnlyDictionary<string, byte[]?> textures,
        bool slice,
        CancellationToken cancellationToken)
    {
        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_decodedTextures.TryGetValue(batch.TextureId, out var texture))
            {
                textures.TryGetValue(batch.TextureId, out var png);
                texture = Native.DecodeTexture(_renderer, png);
                _decodedTextures.Add(batch.TextureId, texture);
            }
            unsafe
            {
                fixed (ProjectionMeshVertex* vertices = batch.Vertices)
                fixed (uint* indices = batch.Indices)
                {
                    var mode = batch.Mode == 0 && texture.HasTransparency
                        ? 1
                        : batch.Mode;
                    Check(Native.Mesh(
                        _renderer,
                        vertices,
                        (uint)batch.Vertices.Length,
                        indices,
                        (uint)batch.Indices.Length,
                        texture.Handle,
                        mode,
                        slice ? 1 : 0));
                }
            }
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Detach();
    }

    private static void Check(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
    }

    private static class Native
    {
        [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_texture_create",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern int CreateTexture(nint renderer, byte[]? png, uint size, out nint texture, out int hasTransparency);

        [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_texture_destroy",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern void DestroyTexture(nint texture);

        public static DecodedTexture DecodeTexture(nint renderer, byte[]? png)
        {
            var result = CreateTexture(renderer, png, (uint)(png?.Length ?? 0), out var texture, out var hasTransparency);
            Check(result);
            return new DecodedTexture(texture, hasTransparency != 0);
        }

        [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_create",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern int Create(nint panel, uint width, uint height, out nint renderer);

        [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_destroy",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern void Destroy(nint renderer);

        [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_resize",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern int Resize(nint renderer, uint width, uint height);

        [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_clear",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern int Clear(nint renderer);

        [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_clear_slice",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern int ClearSlice(nint renderer);

        [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_clip",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern int Clip(nint renderer, int axis, float minimum, float maximum);

        [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_mesh",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe int Mesh(
            nint renderer,
            ProjectionMeshVertex* vertices,
            uint vertexCount,
            uint* indices,
            uint indexCount,
            nint texture,
            int mode,
            int slice);

        [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_camera",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern int Camera(
            nint renderer,
            float x, float y, float z,
            float targetX, float targetY, float targetZ);

        [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_draw",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern int Draw(nint renderer);

        [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_feature",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Feature(nint renderer);

        [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_memory",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern ulong Memory(nint renderer);

        [DllImport("rzero_projection_d3d11", EntryPoint = "renderer_pixels",
            CallingConvention = CallingConvention.Cdecl)]
        public static extern int Pixels(nint renderer, [Out] byte[] pixels, uint size);
    }

    private readonly record struct DecodedTexture(nint Handle, bool HasTransparency);
}
