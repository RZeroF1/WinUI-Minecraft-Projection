# RZeroF1 Minecraft Projection SDK

[简体中文](README.md)

An independent .NET SDK for reading Minecraft projections, loading game resources, building models and meshes, slicing scenes, controlling cameras, rendering with Direct3D 11, capturing PNG images, and reporting resource diagnostics. Hosts use the public API and provide file selection, client discovery, and input events.

Supports Litematica `.litematic` and Sponge `.schem`. The managed assembly targets `net10.0` without a managed UI framework dependency. The native renderer currently supports Windows x64 only.

Visible blocks that fail to render use a black/magenta placeholder. `ProjectionSession.RenderDiagnostics` reports grouped reasons, states and source-coordinate samples; intentionally invisible vanilla blocks remain invisible. Animation metadata selects a static first frame; world-space cull faces, cross plants and basic neighboring fluids are supported. Complex block entity renderers, waterlogged fluid geometry, complete fluid animation and advanced lighting remain limited. Packages use an explicit public file list, validated for both NuGet and symbols archives.

Vanilla panes with all four horizontal connections explicitly false are hidden by default to remove floating end caps in test scenes. Use `new ProjectionSession(new ProjectionRenderOptions { HideIsolatedGlassPanes = false })` to retain their source geometry. Connected panes, source blocks and material counts are unchanged.

## Installation and integration

Version: `0.1.1`.

```xml
<PackageReference Include="RZeroF1.Minecraft.Projection" Version="0.1.1" />
```

Create one `ProjectionSession` per viewport. Supply Minecraft client JAR paths, obtain the target's COM pointer through a thin host adapter, and pass physical pixel dimensions. Attach the renderer before loading a projection:

```csharp
using RZeroF1.Minecraft.Projection;

using var session = new ProjectionSession();
session.AttachRenderer(panelIUnknown, pixelWidth, pixelHeight);
await session.LoadAsync(
    projectionPath,
    clientJarPaths.Select(path => new ProjectionClientSource(path)),
    cancellationToken: cancellationToken);

await session.ApplySliceAsync(axis: 1, minimum: 5, maximum: 5, cancellationToken);
session.RenderFrame(ProjectionMovementInput.None);
byte[] screenshot = await session.CapturePngAsync(cancellationToken);
ProjectionMemorySnapshot diagnostics = session.CaptureDiagnostics();
```

The host supplies the paths, COM pointer, dimensions, and cancellation token used above. Run rendering operations on the target's owning UI thread. For continuous display, call `RenderFrame` from a host frame callback; the SDK does not create a refresh timer.

## Rendering outputs

| API | Host target | Use case |
| --- | --- | --- |
| `AttachRenderer` | `SwapChainPanel` | Display 3D content through a swap chain |
| `AttachSurfaceRenderer` | `VirtualSurfaceImageSource` bound to an `Image` or `ImageBrush` | Overlay XAML Acrylic on the rendered model |

The image-surface path shares the model, slice, camera, and D3D11 drawing pipeline. Each presentation copies the frame to the XAML surface on the GPU, without CPU readback or PNG encoding. Explicit screenshots still require readback and encoding. Acrylic appearance depends on system transparency settings and the actual XAML layering.

Call `ResizeRenderer` when the viewport size or DPI changes. On unload, stop frame callbacks and await pending operations before detaching the renderer, clearing the image binding, and disposing the session. See the [API documentation, in Chinese](docs/api.md) for call ordering, threading, and ownership contracts.

## Building and packaging

Use Windows x64, the .NET 10 SDK, and Visual Studio C++ Build Tools:

```powershell
dotnet build RZeroF1.Minecraft.Projection.slnx -c Release
dotnet pack src/RZeroF1.Minecraft.Projection/RZeroF1.Minecraft.Projection.csproj -c Release -r win-x64
```

Packages are written to `artifacts/packages/`. The native build uses ABI headers from a pinned `Microsoft.WindowsAppSDK.WinUI` package without adding a WinUI runtime dependency to the managed SDK assembly.

Run the existing local validation projects with:

```powershell
dotnet run --project tests/RZeroF1.Minecraft.Projection.Tests/RZeroF1.Minecraft.Projection.Tests.csproj -c Release -r win-x64
dotnet restore tests/PackageConsumer/PackageConsumer.csproj -r win-x64 --configfile tests/PackageConsumer/NuGet.Config -p:NuGetAudit=false
dotnet run --project tests/PackageConsumer/PackageConsumer.csproj -c Release -r win-x64 --no-restore
```

Compilation, automated tests, and visual inspection are separate checks. Native image-surface tests do not replace manual verification of WinUI rendering, Acrylic, or interaction.

## Documentation and licenses

The detailed documentation is in Chinese:

- [API and host integration](docs/api.md)
- [Architecture and resource ownership](docs/architecture.md)
- [NuGet package and version notes](docs/nuget.md)
- [Native renderer build instructions](native/projection/README.md)

Project code is licensed under the [Mozilla Public License 2.0](LICENSE). Third-party parser components retain their MIT licenses and source attributions in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and `licenses/`.
