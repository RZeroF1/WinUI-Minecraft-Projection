# Minecraft Projection D3D11 renderer

This is the project-owned Direct3D11 bridge used by the Projection SDK. It creates a composition swap chain for `SwapChainPanel`, accepts blittable vertex/index/texture packets from the managed mesh layer, and owns all GPU resources until `renderer_destroy`.

The renderer is intentionally independent from Minecraft model semantics: resource lookup, blockstate selection, mesh generation, diagnostics, and cache generations remain in the managed SDK. The C ABI uses caller-owned input buffers only during each upload call; the native side copies the data into immutable D3D11 resources.
