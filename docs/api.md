# API

The package ID and assembly name are `RZeroF1.Minecraft.Projection`.

The SDK is a session-oriented API. A `ProjectionSession` owns the current projection document, client resource archives, resource-pack layers, model and texture caches, baked meshes, slice index, camera, D3D11 renderer, screenshot capture path, and diagnostics. Create one session per viewport and dispose it when that viewport closes.

## Typical host flow

The host supplies client JAR paths and, when rendering to WinUI 3, obtains the `IUnknown` identity of its `SwapChainPanel` in a thin adapter. The SDK itself does not reference XAML or WinUI managed controls.

```csharp
using RZeroF1.Minecraft.Projection;

using var session = new ProjectionSession();

var clients = installedClientJars.Select(path => new ProjectionClientSource(path));

session.AttachRenderer(
    swapChainPanelIUnknown,
    pixelWidth,
    pixelHeight);

await session.LoadAsync(
    projectionPath,
    clients,
    resourcePackPath: optionalResourcePackPath,
    progress: progress,
    cancellationToken: cancellationToken);

session.RenderFrame(ProjectionMovementInput.None);

await session.ApplySliceAsync(
    axis: 1,
    minimum: 5,
    maximum: 5,
    cancellationToken);

byte[] png = await session.CapturePngAsync(cancellationToken);
ProjectionMemorySnapshot diagnostics = session.CaptureDiagnostics();
```

`AttachRenderer`, `ResizeRenderer`, `DetachRenderer`, `UpdateCamera`, `RenderFrame`, and screenshot capture must run on the UI or render thread that owns the composition surface. File reading, client metadata inspection, projection parsing, resource loading, model resolution, and mesh baking are performed by asynchronous SDK operations and do not require the host to create worker threads.

## Client resources

`ProjectionClientSource` is an input descriptor:

```csharp
var source = new ProjectionClientSource(
    Path: @"C:\Minecraft\versions\1.21.4\1.21.4.jar",
    Label: "1.21.4");
```

The label is optional. If it is absent, the SDK reads the client archive's `version.json` and derives a label from the archive file name when necessary.

`DiscoverClientsAsync` validates the supplied archives and returns `ClientResourceInfo` values containing the normalized path, label, `DataVersion`, display label, and SHA-256. Discovery results are cached while the source path, label, file length, and last-write timestamp remain unchanged.

```csharp
IReadOnlyList<ClientResourceInfo> readableClients = await session.DiscoverClientsAsync(clients, cancellationToken);
```

Calling `DiscoverClientsAsync` is optional. `LoadAsync` performs the same validation internally. When a projection has a `DataVersion`, the SDK selects the first readable client with an exact matching value. If no exact match exists, it selects the first readable client supplied by the host. Loading fails when no readable client archive is available.

## Loading a projection

`LoadAsync` accepts `.litematic` and `.schem` files:

```csharp
await session.LoadAsync(
    projectionPath,
    clients,
    resourcePackPath: null,
    progress: new Progress<ProjectionLoadProgress>(value => Console.WriteLine($"{value.Stage}: {value.Completed}/{value.Total} {value.Message}")),
    cancellationToken);
```

After a successful load, `Document` contains the parsed projection and block palette, `SelectedClient` identifies the client archive used for the load, `Camera` is initialized from the document bounds, `FullMaterialGroups` contains the full-scene material summary, `MaterialIcons` contains PNG bytes keyed by block-state key when an icon is available, and `Warnings` contains bounded parser, resource, and model warnings.

`ProjectionDocument` exposes `Format`, `DataVersion`, `Min`, `Max`, `Width`, `Height`, `Length`, `Blocks`, `Palette`, `Regions`, and `EstimatedCpuBytes`. Blocks store a position, palette state index, and optional region name.

`ReloadAsync` rebuilds the current document from its selected client. It is useful after changing local resource files. `Clear` releases the document, resource archives, CPU meshes, material icons, and slice cache while leaving an attached renderer available for the next load.

## Resource packs and materials

`ApplyResourcePackAsync` adds the supplied resource pack as the highest priority resource layer and rebuilds the current scene:

```csharp
await session.ApplyResourcePackAsync(resourcePackPath, progress, cancellationToken);
```

The operation invalidates the material and slice caches, rebuilds the model and texture data, and replaces the renderer's base scene when a renderer is attached. The current projection document and camera object remain owned by the session.

Use `GetDisplayName` for a localized or fallback block name:

```csharp
string name = session.GetDisplayName("minecraft:stone");
```

Use `GetMaterialGroups` for full-scene or sliced material summaries:

```csharp
var allMaterials = session.GetMaterialGroups();
var visibleMaterials = session.GetMaterialGroups(axis: 1, minimum: 5, maximum: 5);
```

Material groups contain a block name, total count, and state variants with properties and counts. The returned collections should be treated as read-only snapshots.

## Slicing

The axis values are:

| Axis | Coordinate |
| ---: | --- |
| `0` | X |
| `1` | Y |
| `2` | Z |

Apply a slice with inclusive minimum and maximum coordinates:

```csharp
await session.ApplySliceAsync(
    axis: 1,
    minimum: 10,
    maximum: 12,
    cancellationToken);
```

The SDK clips the base scene and builds the newly exposed boundary geometry. Up to 128 MiB of boundary CPU geometry is retained in an LRU cache. Repeating the same range can reuse the cached boundary.

Pass `axis: -1` to remove the slice:

```csharp
await session.ApplySliceAsync(
    axis: -1,
    minimum: int.MinValue,
    maximum: int.MaxValue,
    cancellationToken,
    rebuildBoundary: false);
```

Set `rebuildBoundary` to `false` when only the clipping range should change and the existing boundary mesh should not be rebuilt.

`VisibleBlockCount` and `GetMaterialGroups` use the same inclusive range:

```csharp
int count = session.VisibleBlockCount(axis: 1, minimum: 10, maximum: 12);
```

## Camera

`ProjectionCamera` stores camera state and model-bound information. Its `Eye` and `Target` vectors describe the current view. The session wrappers update the Native renderer after changing the camera:

```csharp
session.RotateCamera(deltaX, deltaY);
session.PanCamera(deltaX, deltaY, viewportHeight);
session.ZoomCamera(wheelNotches);
session.FocusCamera();
session.ResetCamera();
session.SetCameraMode(ProjectionCameraMode.Free);
```

In orbit mode, pointer rotation changes the orbit angles, right-button movement pans the target, and wheel input changes the orbit distance. In free mode, `RenderFrame` applies movement input using the elapsed time measured by the session:

```csharp
var input = ProjectionMovementInput.Forward | ProjectionMovementInput.Right;
session.RenderFrame(input, speedScale: 1, rendererBusy: false);
```

`Fast` multiplies movement speed and `Precise` reduces it. Set `rendererBusy: true` while an asynchronous scene replacement is still modifying renderer resources; the session updates free-camera state but skips the draw call for that frame.

## Rendering and resizing

Attach a renderer once the host has a valid composition surface:

```csharp
session.AttachRenderer(panelIUnknown, pixelWidth, pixelHeight);
```

Call `ResizeRenderer` when physical pixel dimensions change, including a composition-scale change:

```csharp
session.ResizeRenderer(pixelWidth, pixelHeight);
```

`RenderFrame` draws the current base and slice meshes. It returns `true` when it performed a draw and `false` when no renderer is attached or `rendererBusy` was set.

Call `DetachRenderer` before the host destroys the composition surface. The Native renderer keeps its own COM reference to the panel and releases it when detached or disposed.

Only Windows x64 is supported. Other Runtime Identifiers fail the SDK build explicitly.

## Screenshots and diagnostics

`CapturePngAsync` returns an encoded PNG of the latest rendered frame. A renderer must be attached, and the call should be made on the renderer's thread:

```csharp
byte[] png = await session.CapturePngAsync(cancellationToken);
await File.WriteAllBytesAsync("projection.png", png, cancellationToken);
```

`CaptureDiagnostics` returns process memory, document memory, CPU mesh and texture estimates, resource-cache bytes, slice-cache bytes, and Native GPU resource estimates:

```csharp
ProjectionMemorySnapshot snapshot = session.CaptureDiagnostics();
Console.WriteLine($"GPU estimate: {snapshot.GpuBytes} bytes");
```

These values are diagnostic estimates. `GpuBytes` is calculated from the Native mesh and texture resources currently retained by the renderer; it is not a driver-reported allocation total.

## Errors and cancellation

All asynchronous load, resource-pack, slice, discovery, and capture methods accept a `CancellationToken` where work can block or allocate. Cancellation throws `OperationCanceledException`; callers should discard partially completed UI updates and keep the session available for the next operation.

Invalid or unsupported projection data throws `ProjectionFormatException` or an underlying `IOException`. Missing client resources, missing renderer attachment, invalid slice axes, and calls made after disposal throw `InvalidOperationException`, `ArgumentException`, or `ObjectDisposedException` as appropriate.

`ProjectionSession` is `IDisposable` and should normally be used with `using` or equivalent host lifetime management:

```csharp
using var session = new ProjectionSession();
// attach, load, render, capture
```
