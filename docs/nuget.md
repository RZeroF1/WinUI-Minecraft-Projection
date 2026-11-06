# NuGet 包

| 项目 | 值 |
| --- | --- |
| 包 ID | `RZeroF1.Minecraft.Projection` |
| 版本 | `0.1.1` |
| 目标框架 | `net10.0` |
| 原生运行时 | `win-x64` |
| 项目许可证 | `MPL-2.0` |

## 包内容

包中包含以下文件，打包清单以 SDK 的 `.csproj` 为准：

- `lib/net10.0/`：托管 SDK 程序集与 XML API 注释。
- `runtimes/win-x64/native/rzero_projection_d3d11.dll`：原生 D3D11 渲染器。
- `README.md` 与 `README.en.md`：中文和英文说明；NuGet 默认展示中文版本。
- `docs/`：中文 API、架构与包说明。
- `native/projection/README.md`：原生渲染器构建与生命周期说明。
- `LICENSE`、`THIRD-PARTY-NOTICES.md`、`licenses/`：项目许可证、第三方许可证、来源及固定修订信息。

调试符号另行输出为 `.snupkg`。包不包含本地测试、宿主页面、临时诊断文件或构建缓存，不依赖托管 UI 框架运行时。

## 构建与本地消费

使用 Windows x64、.NET 10 SDK 和 Visual Studio C++ Build Tools：

```powershell
dotnet build src/RZeroF1.Minecraft.Projection/RZeroF1.Minecraft.Projection.csproj -c Release -r win-x64
dotnet pack src/RZeroF1.Minecraft.Projection/RZeroF1.Minecraft.Projection.csproj -c Release -r win-x64
```

输出目录为 `artifacts/packages/`。还原过程下载固定版本的 WinUI ABI 头文件包，编译后将原生 DLL 放入 NuGet 原生运行时目录。消费者需要运行在 Windows x64，单纯引用该包不会为宿主安装 WinUI。

已有包消费者项目不引用 SDK 源码项目，可从本地产物还原并验证公开 API、原生 DLL 复制和导出函数加载：

```powershell
dotnet restore tests/PackageConsumer/PackageConsumer.csproj -r win-x64 --configfile tests/PackageConsumer/NuGet.Config -p:NuGetAudit=false
dotnet run --project tests/PackageConsumer/PackageConsumer.csproj -c Release -r win-x64 --no-restore
```

## 0.1.1 版本变更

- 新增 `AttachSurfaceRenderer`，支持将 D3D11 画面经 GPU 复制到 `VirtualSurfaceImageSource`，用于宿主的 XAML 亚克力叠加场景。
- 保留 `AttachRenderer` 的 `SwapChainPanel` 接入，两种输出共用模型、切片、相机和截图。
- 统一颜色纹理作为截图来源；显存估算补充颜色、深度和呈现缓冲区。
- 托管端仅在原生尺寸调整成功后更新尺寸记录。
- 统一中文文档，提供英文 README，并补充线程、对象持有、异常恢复及卸载契约。

从 `0.1.0` 升级后，原有交换链接入可以继续使用。采用新图像表面输出时，宿主需创建并绑定图像源，在 UI 线程按 [API 文档](api.md) 完成连接、刷新、尺寸调整和释放。

版本号与本地打包完成不表示已公开发布。NuGet 推送、GitHub Release 和远端推送分别执行，发布前需核对提交、版本、包内容、原生 DLL 和许可证；构建与自动测试也不替代实际画面及交互检查。
