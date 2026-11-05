# Architecture

```text
Host application
  file dialogs, client discovery, keyboard and pointer events
  thin SwapChainPanel COM-handle adapter
       │
       ▼
RZeroF1.Minecraft.Projection
  ProjectionSession: lifetime, loading, scene, slice cache, capture, diagnostics
  ProjectionReader + bounded NBT parsers
  ProjectionResourceSet + blockstate/model resolver + mesh baker
  ProjectionCamera + ProjectionSliceIndex
       │
       ▼
rzero_projection_d3d11.dll
  SwapChainPanel composition swapchain, GPU resources, WIC decode and readback
```

The managed SDK targets `net10.0` and has no UI framework dependency. Native code receives the panel COM identity and pixel dimensions through the public session boundary. The host supplies client JAR paths and maps input events to the session API.

All projection format parsing, block and model resolution, texture access, mesh generation, slice-boundary generation and cache, camera calculations, render scheduling operations, screenshot encoding, resource ownership and engine diagnostics live in the SDK.

The native build pins `Microsoft.WindowsAppSDK.WinUI` 2.3.9 only as a package download to access `ISwapChainPanelNative` declarations. It does not reference WinUI targets from the managed SDK project. WIC decodes textures to RGBA before upload. PNG screenshots use the managed SDK's BGRA encoder.

The D3D11 bridge owns its swap chain, device, context and mesh resources. Mesh upload copies vertex and index buffers. Texture shader-resource views are held by meshes after transient decoder handles are released. `ProjectionSession` destroys the renderer before clearing its scene when disposed.
