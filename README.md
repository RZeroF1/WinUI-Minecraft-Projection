# RZeroF1 Minecraft Projection SDK

Independent .NET SDK for reading Minecraft projection files, resolving game resources, building block models and meshes, computing slice boundaries, controlling a camera, rendering through Direct3D 11, capturing PNG images, and reporting resource and memory diagnostics.

Supported formats: Litematica `.litematic` and Sponge `.schem`. The native renderer currently supports Windows x64. The SDK has no UI framework dependency.

## License

Project code is released under the Mozilla Public License 2.0. Third-party parser components retain their MIT licenses and source attributions in `licenses/` and `THIRD-PARTY-NOTICES.md`.

## Build

Use .NET 10 and Visual Studio C++ build tools on Windows x64.

```powershell
dotnet build RZeroF1.Minecraft.Projection.slnx -c Release
dotnet pack src/RZeroF1.Minecraft.Projection/RZeroF1.Minecraft.Projection.csproj -c Release
dotnet run --project tests/RZeroF1.Minecraft.Projection.Tests/RZeroF1.Minecraft.Projection.Tests.csproj -c Release -r win-x64
dotnet run --project tests/PackageConsumer/PackageConsumer.csproj -c Release -r win-x64
```

Packages are written to `artifacts/packages`. The native build gets the WinUI ABI header from the pinned `Microsoft.WindowsAppSDK.WinUI` NuGet package and does not add a WinUI dependency to the SDK assembly.

## Consume

Reference `RZeroF1.Minecraft.Projection` and create a `ProjectionSession`. The host discovers Minecraft client JAR paths and supplies those paths to the SDK. A UI host obtains its `SwapChainPanel` COM handle in a small adapter and passes the handle and pixel dimensions to `AttachRenderer`.

```csharp
using RZeroF1.Minecraft.Projection;

using var session = new ProjectionSession();
session.AttachRenderer(panelIUnknown, pixelWidth, pixelHeight);
await session.LoadAsync(
    projectionPath,
    clients.Select(path => new ProjectionClientSource(path)),
    cancellationToken: cancellationToken);

await session.ApplySliceAsync(axis: 1, minimum: 5, maximum: 5, cancellationToken);
session.RenderFrame(ProjectionMovementInput.None);
byte[] screenshot = await session.CapturePngAsync(cancellationToken);
ProjectionMemorySnapshot diagnostics = session.CaptureDiagnostics();
```

See `docs/api.md` and `docs/architecture.md` for ownership and behavior contracts. Review `LICENSE` and `THIRD-PARTY-NOTICES.md` before any distribution.
