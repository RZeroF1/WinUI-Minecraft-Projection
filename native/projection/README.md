# Minecraft Projection D3D11 原生渲染器

此目录提供项目自有的 Direct3D 11 桥接层，负责交换链或 XAML 图像表面输出、纹理解码、网格上传、绘制与像素回读。GPU 资源由渲染器持有，直到 `renderer_destroy`。

Minecraft 资源查找、方块状态选择、模型与网格构建、缓存策略和引擎诊断逻辑保留在托管 SDK。C ABI 仅在上传调用期间借用调用方缓冲区；原生端将数据复制为自身持有的 D3D11 资源。

## 输出方式

- `SwapChainPanel`：创建合成交换链，通过 `ISwapChainPanelNative` 关联并呈现。
- `VirtualSurfaceImageSource`：通过 `IVirtualSurfaceImageSourceNative` 关联设备，先绘制到离屏纹理，再调用 `BeginDraw`、GPU 复制及 `EndDraw` 写入 XAML 表面。

两种方式共用 `renderer_create` 和后续 C ABI。图像表面路径每次复制完整视口，使用 XAML 返回的目标纹理子资源与偏移；日常呈现不进行 CPU 回读。主动截图重新绘制当前场景，再回读颜色纹理，供托管端编码 PNG。

## 构建

仅支持 Windows x64。通常由 SDK 项目的 MSBuild 目标调用 `build.cmd`，无需单独构建。先安装 .NET 10 SDK 和 Visual Studio C++ Build Tools，再在仓库根目录执行：

```powershell
dotnet build src/RZeroF1.Minecraft.Projection/RZeroF1.Minecraft.Projection.csproj -c Release -r win-x64
```

SDK 固定下载 `Microsoft.WindowsAppSDK.WinUI` `2.3.9` 以获取 ABI 头文件。`build.cmd` 接受架构、输出目录和 WinUI include 目录三个参数；目前应使用 `x64`。原生渲染器链接 Windows 提供的 D3D11、DXGI、着色器编译、WIC 与 COM 库，输出 `rzero_projection_d3d11.dll`。

## 生命周期

创建、绘制、调整尺寸、回读和销毁均由宿主在拥有渲染目标的线程调用。WinUI 图像表面路径要求 XAML UI 线程。断开时解除目标关联，清理上下文状态并释放 COM 与 GPU 资源。设备丢失或尺寸调整失败后应销毁渲染器、重新创建并重新上传场景。

接口、线程与宿主释放顺序见 [API 文档](../../docs/api.md)，整体所有权见 [架构文档](../../docs/architecture.md)。
