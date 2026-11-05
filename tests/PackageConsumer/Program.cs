using System.Runtime.InteropServices;
using System.Numerics;
using RZeroF1.Minecraft.Projection;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
    throw new PlatformNotSupportedException("The package consumer requires Windows x64.");

var nativePath = Path.Combine(AppContext.BaseDirectory, "rzero_projection_d3d11.dll");
Assert(File.Exists(nativePath), "runtime-native DLL is copied beside the package consumer");
var nativeInfo = new FileInfo(nativePath);
Assert(nativeInfo.Length > 0, "runtime-native DLL is non-empty");
var nativeLibrary = NativeLibrary.Load(nativePath);
try
{
    Assert(
        NativeLibrary.TryGetExport(nativeLibrary, "renderer_create", out _) &&
        NativeLibrary.TryGetExport(nativeLibrary, "renderer_mesh", out _) &&
        NativeLibrary.TryGetExport(nativeLibrary, "renderer_pixels", out _),
        "packaged native renderer exports are loadable");
}
finally
{
    NativeLibrary.Free(nativeLibrary);
}
var assembly = typeof(ProjectionSession).Assembly;
var publicTypeNames = assembly.GetExportedTypes().Select(static type => type.FullName).ToHashSet();
Assert(!publicTypeNames.Contains("RZeroF1.Minecraft.Projection.ProjectionResourceSet"), "resource archives stay internal");
Assert(!publicTypeNames.Contains("RZeroF1.Minecraft.Projection.ProjectionSliceIndex"), "slice index stays internal");
Assert(!publicTypeNames.Contains("RZeroF1.Minecraft.Projection.ProjectionNativeRenderer"), "native renderer stays internal");

var camera = new ProjectionCamera();
var document = new ProjectionDocument
{
    Name = "package-consumer",
    SourcePath = "in-memory",
    Format = ProjectionFormat.SpongeSchematic,
    Min = new(-1, 0, -2),
    Max = new(2, 3, 4),
    Blocks = [new(new(0, 1, 1), 0, null)],
    Palette = [new ProjectionState("minecraft:stone")],
    Regions = [],
    Warnings = []
};
camera.Initialize(document);
var anchor = camera.OrbitTarget;
camera.Zoom(1);
camera.SetMode(ProjectionCameraMode.Free);
camera.MoveFree(Vector3.UnitX, .1f);
Assert(camera.OrbitTarget == anchor && camera.FreePosition.X != anchor.X, "public camera API is usable from package reference");

using var session = new ProjectionSession();
Assert(session.Document is null && session.CaptureDiagnostics().DocumentBytes == 0, "public session API constructs from package");
Assert(!session.RenderFrame(), "render frame is harmless before renderer attachment");
Console.WriteLine($"PASS package consumer; native={nativePath}; bytes={nativeInfo.Length}");
