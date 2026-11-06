# RZeroF1 Minecraft Projection SDK

[English](README.en.md)

独立的 .NET Minecraft 投影 SDK，提供投影读取、游戏资源加载、模型与网格构建、切片、相机、Direct3D 11 渲染、PNG 截图及资源诊断。宿主通过公开 API 接入，自行提供文件选择、客户端发现和输入事件。

支持 Litematica `.litematic` 与 Sponge `.schem`。托管程序集面向 `net10.0`，不依赖托管 UI 框架；原生渲染器目前仅支持 Windows x64。

## 安装与接入

版本：`0.1.1`。

```xml
<PackageReference Include="RZeroF1.Minecraft.Projection" Version="0.1.1" />
```

每个视口创建一个 `ProjectionSession`。宿主提供 Minecraft 客户端 JAR 路径，通过薄适配层取得目标的 COM 指针，并传入物理像素尺寸。先连接渲染器，再加载投影：

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

示例中的路径、COM 指针、尺寸和取消令牌由宿主提供。渲染相关操作在拥有目标的 UI 线程执行；持续显示由宿主的帧回调调用 `RenderFrame`，SDK 不创建刷新计时器。

## 渲染输出

| 输出接口 | 宿主目标 | 使用场景 |
| --- | --- | --- |
| `AttachRenderer` | `SwapChainPanel` | 通过交换链显示 3D 画面 |
| `AttachSurfaceRenderer` | `VirtualSurfaceImageSource`，绑定到 `Image` 或 `ImageBrush` | 在模型画面上叠加 XAML 亚克力浮层 |

图像表面路径复用模型、切片、相机和 D3D11 绘制流程，每次呈现通过 GPU 复制到 XAML 表面，不进行 CPU 回读或 PNG 编解码。主动截图仍需要回读并编码。亚克力的显示受系统透明效果设置和实际 XAML 层级影响。

尺寸或 DPI 变化时调用 `ResizeRenderer`。卸载时先停止帧回调并等待正在进行的操作结束，再断开渲染器、解除图像源绑定并释放 session。完整调用顺序、线程与所有权契约见 [API 文档](docs/api.md)。

## 构建与打包

使用 Windows x64、.NET 10 SDK 和 Visual Studio C++ Build Tools：

```powershell
dotnet build RZeroF1.Minecraft.Projection.slnx -c Release
dotnet pack src/RZeroF1.Minecraft.Projection/RZeroF1.Minecraft.Projection.csproj -c Release -r win-x64
```

包输出至 `artifacts/packages/`。原生构建使用固定版本 `Microsoft.WindowsAppSDK.WinUI` 包中的 ABI 头文件，不向 SDK 程序集引入 WinUI 运行时依赖。

已有本地验证项目可执行：

```powershell
dotnet run --project tests/RZeroF1.Minecraft.Projection.Tests/RZeroF1.Minecraft.Projection.Tests.csproj -c Release -r win-x64
dotnet restore tests/PackageConsumer/PackageConsumer.csproj -r win-x64 --configfile tests/PackageConsumer/NuGet.Config -p:NuGetAudit=false
dotnet run --project tests/PackageConsumer/PackageConsumer.csproj -c Release -r win-x64 --no-restore
```

编译、自动测试和实际画面检查是不同的验证步骤。原生图像表面测试不替代 WinUI 画面、亚克力及交互的人工检查。

## 文档与许可证

- [API 与宿主接入](docs/api.md)
- [架构与资源所有权](docs/architecture.md)
- [NuGet 包与版本说明](docs/nuget.md)
- [原生渲染器构建说明](native/projection/README.md)

项目代码使用 [Mozilla Public License 2.0](LICENSE)。第三方解析组件保留其 MIT 许可证和来源说明，见 [第三方声明](THIRD-PARTY-NOTICES.md) 及 `licenses/`。
