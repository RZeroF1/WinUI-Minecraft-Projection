using System.Diagnostics;
using System.Numerics;

namespace RZeroF1.Minecraft.Projection;

public sealed record ProjectionMemorySnapshot(
    long PrivateBytes,
    long WorkingSet,
    long ManagedBytes,
    long DocumentBytes,
    long MeshBytes,
    ulong GpuBytes,
    long ResourceBytes,
    long CachedSliceBytes);

public sealed record ClientResourceInfo(
    string Path,
    string Label,
    int? DataVersion,
    string DataVersionLabel,
    string Sha256);

[Flags]
public enum ProjectionMovementInput
{
    None = 0,
    Forward = 1,
    Backward = 2,
    Right = 4,
    Left = 8,
    Up = 16,
    Down = 32,
    Fast = 64,
    Precise = 128
}

public sealed class ProjectionSession : IDisposable
{
    private const long MaxSliceCacheBytes = 128L * 1024 * 1024;
    private readonly Dictionary<SliceBoundaryKey, ProjectionBakedScene> _sliceCache = [];
    private readonly SemaphoreSlim _clientDiscoveryGate = new(1, 1);
    private readonly ProjectionNativeRenderer _renderer = new();
    private ClientSourceStamp[]? _clientSourceStamps;
    private IReadOnlyList<ProjectionClientResource> _discoveredClients = [];
    private ProjectionResourceSet? _resources;
    private int _disposed;
    private int _cachedSliceAxis = -1;
    private int _cachedSliceMinimum;
    private int _cachedSliceMaximum;
    private long _lastFrameTimestamp;

    public ProjectionDocument? Document { get; private set; }
    internal ProjectionBakedScene? Scene { get; private set; }
    internal ProjectionSliceIndex? SliceIndex { get; private set; }
    public ClientResourceInfo? SelectedClient { get; private set; }
    public string? SourcePath => Document?.SourcePath;
    public ProjectionCamera Camera { get; } = new();
    public bool IsRendererCreated => _renderer.IsCreated;
    internal uint FeatureLevel => _renderer.FeatureLevel;
    internal ulong GpuBytesEstimate => _renderer.GpuBytesEstimate;
    public long CachedSliceBytes { get; private set; }
    internal IReadOnlyList<ProjectionMaterialGroup> FullMaterialGroups { get; private set; } = [];
    internal IReadOnlyList<ProjectionMaterialGroup> SliceMaterialGroups { get; private set; } = [];
    public IReadOnlyDictionary<string, byte[]> MaterialIcons { get; private set; } =
        new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> Warnings =>
        (Document?.Warnings ?? []).Concat(Scene?.Warnings ?? []).Concat(_resources?.Warnings ?? [])
            .Distinct(StringComparer.Ordinal).Take(500).ToArray();

    internal ProjectionResourceSet? Resources => _resources;

    public void AttachRenderer(nint swapChainPanel, uint width, uint height)
    {
        ThrowIfDisposed();
        _renderer.Attach(swapChainPanel, width, height);
    }

    public void ResizeRenderer(uint width, uint height) => _renderer.Resize(width, height);

    /// <summary>
    /// 通过 COM 标识连接不透明的 WinUI VirtualSurfaceImageSource。
    /// 在 XAML UI 线程调用，图像源需保留至 DetachRenderer。
    /// ResizeRenderer 同时调整图像源和 GPU 目标尺寸；RenderFrame
    /// 绘制时刷新完整表面，供 XAML 亚克力采样 3D 画面。
    /// </summary>
    public void AttachSurfaceRenderer(nint surfaceImageSource, uint width, uint height)
    {
        ThrowIfDisposed();
        _renderer.Attach(surfaceImageSource, width, height);
    }

    public void DetachRenderer() => _renderer.Detach();

    public async Task<byte[]> CapturePngAsync(CancellationToken cancellationToken = default) =>
        await _renderer.CapturePngAsync(cancellationToken);

    public async Task LoadAsync(
        string projectionPath,
        IEnumerable<ProjectionClientSource> clients,
        string? resourcePackPath = null,
        IProgress<ProjectionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(projectionPath);
        ArgumentNullException.ThrowIfNull(clients);
        Clear();
        var document = await Task.Run(
            () => ProjectionReader.ReadAsync(
                projectionPath,
                progress: progress,
                cancellationToken: cancellationToken),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = await DiscoverClientResourcesAsync(clients, cancellationToken);
        var client = ChooseClient(candidates, document.DataVersion)
            ?? throw new InvalidOperationException("No readable Minecraft client JAR was supplied.");
        var resources = new ProjectionResourceSet(client.Path);
        try
        {
            if (!string.IsNullOrWhiteSpace(resourcePackPath))
                resources.AddResourcePack(resourcePackPath);
            var index = await Task.Run(() => new ProjectionSliceIndex(document), cancellationToken);
            var scene = await Task.Run(
                () => new ProjectionModelBaker(resources).Build(document, progress, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_renderer.IsCreated)
            {
                await _renderer.UploadAsync(scene, cancellationToken);
                _renderer.SetClip(-1, 0, 0);
            }
            _resources = resources;
            resources = null!;
            SelectedClient = ToClientResourceInfo(client);
            Document = document;
            SliceIndex = index;
            Scene = scene;
            FullMaterialGroups = index.GroupMaterials();
            SliceMaterialGroups = [];
            MaterialIcons = BuildMaterialIcons(scene);
            Camera.Initialize(document);
            UpdateCamera();
            progress?.Report(new ProjectionLoadProgress("complete", document.Blocks.Count, document.Blocks.Count, "投影与网格载入完成"));
        }
        finally
        {
            resources?.Dispose();
        }
    }

    public async Task<IReadOnlyList<ClientResourceInfo>> DiscoverClientsAsync(
        IEnumerable<ProjectionClientSource> clients,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ThrowIfDisposed();
        var resources = await DiscoverClientResourcesAsync(clients, cancellationToken);
        return resources
            .Select(static item => new ClientResourceInfo(item.Path, item.Label, item.DataVersion, item.DataVersionLabel, item.Sha256))
            .ToArray();
    }

    public async Task ApplyResourcePackAsync(
        string path,
        IProgress<ProjectionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var document = Document ?? throw new InvalidOperationException("No projection is loaded.");
        var current = _resources ?? throw new InvalidOperationException("Minecraft resources are not loaded.");
        var replacement = new ProjectionResourceSet(current.ClientJarPath);
        try
        {
            replacement.AddResourcePack(path);
            var scene = await Task.Run(
                () => new ProjectionModelBaker(replacement).Build(document, progress, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var index = await Task.Run(() => new ProjectionSliceIndex(document), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_renderer.IsCreated)
            {
                _renderer.Clear();
                _renderer.ReleaseDecodedTextureCache();
                await _renderer.UploadAsync(scene, cancellationToken);
                await _renderer.UploadSliceAsync(CreateEmptyScene(), cancellationToken);
                _renderer.SetClip(-1, 0, 0);
            }
            _renderer.ReleaseDecodedTextureCache();
            _resources = replacement;
            replacement = null!;
            _sliceCache.Clear();
            CachedSliceBytes = 0;
            Scene = scene;
            SliceMaterialGroups = [];
            MaterialIcons = BuildMaterialIcons(scene);
            SliceIndex = index;
            FullMaterialGroups = index.GroupMaterials();
            _cachedSliceAxis = -1;
        }
        finally
        {
            replacement?.Dispose();
        }
        current.Dispose();
    }

    public async Task ReloadAsync(
        IProgress<ProjectionLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var document = Document ?? throw new InvalidOperationException("No projection is loaded.");
        var client = SelectedClient ?? throw new InvalidOperationException("No Minecraft client is selected.");
        await LoadAsync(
            document.SourcePath,
            [new ProjectionClientSource(client.Path, client.Label)],
            progress: progress,
            cancellationToken: cancellationToken);
    }

    public int VisibleBlockCount(int axis = -1, int minimum = int.MinValue, int maximum = int.MaxValue) =>
        SliceIndex?.VisibleBlockCount(axis, minimum, maximum) ?? 0;

    public IReadOnlyList<ProjectionMaterialGroup> GetMaterialGroups(
        int axis = -1,
        int minimum = int.MinValue,
        int maximum = int.MaxValue)
    {
        if (SliceIndex is null)
            return [];
        if (axis < 0)
            return FullMaterialGroups;
        if (axis == _cachedSliceAxis && minimum == _cachedSliceMinimum && maximum == _cachedSliceMaximum)
            return SliceMaterialGroups;
        _cachedSliceAxis = axis;
        _cachedSliceMinimum = minimum;
        _cachedSliceMaximum = maximum;
        return SliceMaterialGroups = SliceIndex.GroupMaterials(axis, minimum, maximum);
    }

    public string GetDisplayName(string blockName) =>
        _resources?.GetDisplayName(blockName) ?? blockName;

    public async Task ApplySliceAsync(
        int axis,
        int minimum,
        int maximum,
        CancellationToken cancellationToken = default,
        bool rebuildBoundary = true)
    {
        ThrowIfDisposed();
        if (Document is null || Scene is null || _resources is null)
            return;
        if (axis < 0)
        {
            if (_renderer.IsCreated)
            {
                _renderer.ClearSlice();
                _renderer.SetClip(-1, 0, 0);
            }
            return;
        }
        if (axis > 2)
            throw new ArgumentOutOfRangeException(nameof(axis));

        var center = axis switch
        {
            0 => (Document.Min.X + Document.Max.X + 1) / 2f,
            1 => (Document.Min.Y + Document.Max.Y + 1) / 2f,
            _ => (Document.Min.Z + Document.Max.Z + 1) / 2f
        };
        if (_renderer.IsCreated)
        {
            _renderer.SetClip(axis, minimum - center, maximum + 1 - center);
        }
        if (!rebuildBoundary)
            return;

        var key = new SliceBoundaryKey(axis, minimum, maximum);
        if (!_sliceCache.TryGetValue(key, out var boundary))
        {
            var document = Document;
            var index = SliceIndex!;
            var resources = _resources;
            boundary = await Task.Run(
                () => new ProjectionModelBaker(resources).BuildSliceBoundary(
                    document, index, axis, minimum, maximum, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = boundary.EstimatedCpuBytes;
            if (bytes <= MaxSliceCacheBytes)
            {
                while (_sliceCache.Count > 0 && CachedSliceBytes + bytes > MaxSliceCacheBytes)
                {
                    var oldest = _sliceCache.First();
                    CachedSliceBytes -= oldest.Value.EstimatedCpuBytes;
                    _sliceCache.Remove(oldest.Key);
                }
                _sliceCache[key] = boundary;
                CachedSliceBytes += bytes;
            }
        }
        if (_renderer.IsCreated)
            await _renderer.UploadSliceAsync(boundary, cancellationToken);
    }

    public ProjectionMemorySnapshot CaptureDiagnostics()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ProjectionMemorySnapshot(
            process.PrivateMemorySize64,
            process.WorkingSet64,
            GC.GetTotalMemory(false),
            Document?.EstimatedCpuBytes ?? 0,
            Scene?.EstimatedCpuBytes ?? 0,
            GpuBytesEstimate,
            _resources?.CachedBytes ?? 0,
            CachedSliceBytes);
    }

    public void Clear()
    {
        if (_disposed != 0)
            return;
        if (_renderer.IsCreated)
            _renderer.Clear();
        ClearOwnedScene();
    }

    public void UpdateCamera() => _renderer.SetCamera(Camera.Eye, Camera.Target);

    public void RotateCamera(float deltaX, float deltaY)
    {
        Camera.Rotate(deltaX, deltaY);
        UpdateCamera();
    }

    public void PanCamera(float deltaX, float deltaY, float viewportHeight)
    {
        Camera.Pan(deltaX, deltaY, viewportHeight);
        UpdateCamera();
    }

    public void ZoomCamera(float wheelNotches)
    {
        Camera.Zoom(wheelNotches);
        UpdateCamera();
    }

    public void ResetCamera()
    {
        var document = Document ?? throw new InvalidOperationException("No projection is loaded.");
        Camera.Reset(document);
        UpdateCamera();
    }

    public void FocusCamera()
    {
        Camera.Focus();
        UpdateCamera();
    }

    public void SetCameraMode(ProjectionCameraMode mode)
    {
        Camera.SetMode(mode);
        UpdateCamera();
    }

    public bool RenderFrame(
        ProjectionMovementInput input = ProjectionMovementInput.None,
        float speedScale = 1,
        bool rendererBusy = false)
    {
        ThrowIfDisposed();
        if (!_renderer.IsCreated)
            return false;
        var now = Stopwatch.GetTimestamp();
        var elapsed = _lastFrameTimestamp == 0
            ? 1d / 60d
            : Math.Clamp((now - _lastFrameTimestamp) / (double)Stopwatch.Frequency, .001d, .05d);
        _lastFrameTimestamp = now;
        if (Camera.Mode == ProjectionCameraMode.Free)
        {
            var movement = Vector3.Zero;
            if (input.HasFlag(ProjectionMovementInput.Forward)) movement += Camera.FreeForward;
            if (input.HasFlag(ProjectionMovementInput.Backward)) movement -= Camera.FreeForward;
            if (input.HasFlag(ProjectionMovementInput.Right)) movement += Camera.FreeRight;
            if (input.HasFlag(ProjectionMovementInput.Left)) movement -= Camera.FreeRight;
            if (input.HasFlag(ProjectionMovementInput.Up)) movement += Vector3.UnitY;
            if (input.HasFlag(ProjectionMovementInput.Down)) movement -= Vector3.UnitY;
            Camera.MoveFree(
                movement,
                (float)elapsed,
                speedScale,
                input.HasFlag(ProjectionMovementInput.Fast),
                input.HasFlag(ProjectionMovementInput.Precise));
            UpdateCamera();
        }
        if (rendererBusy)
            return false;
        _renderer.Draw();
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _renderer.Dispose();
        ClearOwnedScene();
    }

    private void ClearOwnedScene()
    {
        _resources?.Dispose();
        _resources = null;
        Document = null;
        Scene = null;
        SliceIndex = null;
        SelectedClient = null;
        FullMaterialGroups = [];
        SliceMaterialGroups = [];
        MaterialIcons = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        _sliceCache.Clear();
        CachedSliceBytes = 0;
        _cachedSliceAxis = -1;
        _lastFrameTimestamp = 0;
    }

    private static IReadOnlyDictionary<string, byte[]> BuildMaterialIcons(ProjectionBakedScene scene)
    {
        var icons = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (stateKey, textureId) in scene.MaterialTextureIds)
        {
            if (scene.TexturePng.TryGetValue(textureId, out var png) && png is { Length: > 0 })
                icons.TryAdd(stateKey, png);
        }
        return icons;
    }

    private static ProjectionBakedScene CreateEmptyScene() => new()
    {
        Batches = [],
        TexturePng = new Dictionary<string, byte[]?>(),
        MaterialTextureIds = new Dictionary<string, string>(),
        Warnings = [],
        Min = default,
        Max = default
    };

    private static ProjectionClientResource? ChooseClient(
        IReadOnlyList<ProjectionClientResource> clients,
        int? dataVersion)
    {
        if (dataVersion is int value)
        {
            var exact = clients.FirstOrDefault(client => client.DataVersion == value);
            if (exact is not null)
                return exact;
        }
        return clients.FirstOrDefault();
    }

    private static ClientResourceInfo ToClientResourceInfo(ProjectionClientResource client) =>
        new(client.Path, client.Label, client.DataVersion, client.DataVersionLabel, client.Sha256);

    private async Task<IReadOnlyList<ProjectionClientResource>> DiscoverClientResourcesAsync(
        IEnumerable<ProjectionClientSource> sources,
        CancellationToken cancellationToken)
    {
        var clientSources = sources.ToArray();
        var stamps = clientSources.Select(ClientSourceStamp.Create).ToArray();
        await _clientDiscoveryGate.WaitAsync(cancellationToken);
        try
        {
            if (_clientSourceStamps is not null && _clientSourceStamps.SequenceEqual(stamps))
                return _discoveredClients;

            var discovered = await Task.Run(
                () => ProjectionResourceDiscovery.ReadCandidatesAsync(clientSources, cancellationToken),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _clientSourceStamps = stamps;
            _discoveredClients = discovered;
            return _discoveredClients;
        }
        finally
        {
            _clientDiscoveryGate.Release();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private readonly record struct ClientSourceStamp(
        string Path,
        string? Label,
        long Length,
        long LastWriteTimeUtcTicks)
    {
        public static ClientSourceStamp Create(ProjectionClientSource source)
        {
            try
            {
                var path = System.IO.Path.GetFullPath(source.Path);
                var file = new FileInfo(path);
                return new ClientSourceStamp(
                    path,
                    source.Label,
                    file.Exists ? file.Length : -1,
                    file.Exists ? file.LastWriteTimeUtc.Ticks : 0);
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                System.Security.SecurityException)
            {
                return new ClientSourceStamp(source.Path, source.Label, -1, 0);
            }
        }
    }

    private readonly record struct SliceBoundaryKey(int Axis, int Minimum, int Maximum);
}
