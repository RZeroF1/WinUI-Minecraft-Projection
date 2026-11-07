# NuGet 包

| 项目 | 值 |
| --- | --- |
| 包 ID | `RZeroF1.Minecraft.Projection` |
| 版本 | `0.1.1` |
| 目标框架 | `net10.0` |
| 原生运行时 | `win-x64` |
| 项目许可证 | `MPL-2.0` |

## 包内容

包中只包含明确列出的公开文件，打包清单以 SDK 的 `.csproj` 为准，不递归收集文档或许可证目录：

- `lib/net10.0/`：托管 SDK 程序集与 XML API 注释。
- `runtimes/win-x64/native/rzero_projection_d3d11.dll`：原生 D3D11 渲染器。
- `README.md` 与 `README.en.md`：中文和英文说明；NuGet 默认展示中文版本。
- `docs/api.md`、`docs/architecture.md`、`docs/nuget.md`：中文公开文档。
- `native/projection/README.md`：原生渲染器构建与生命周期说明。
- `LICENSE`、`THIRD-PARTY-NOTICES.md`、`licenses/`：项目许可证、第三方许可证、来源及固定修订信息。

调试符号另行输出为 `.snupkg`。包不包含本地测试、宿主页面、临时诊断文件或构建缓存，不依赖托管 UI 框架运行时。

`docs/internal/`、新增内部目录、调查截图、ZIP、投影样本和探针均不在白名单中，不依赖 Git 忽略规则。每次打包后执行 `.github/scripts/Validate-Packages.ps1`，同时检查 `.nupkg` 和 `.snupkg` 的完整条目集合，拒绝未知内容并核对 Native DLL、API XML、许可证和来源文件。新增公开文档需要同步更新打包清单和检查器，不能扩回目录通配。

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

## 当前源码的 P1 架构变更

内部保留 Litematic/Sponge v2/v3 Block Entity 数据，分离源版本适配与实际客户端 Profile，并接入组合渲染计划和贡献诊断。普通模型和流体继续复用原实现；已有底座但附加内容不足时明确报告 Partial。此阶段没有新增专用部件几何、公开类型或 Native ABI，详见 [数据与诊断契约](api.md)。版本仍为本地开发声明，不能据此推断这些变更已经发布。

## 当前源码的 P2 架构变更

内部增加共享 Cuboid/Quad/ModelPartGraph/Pose、逻辑 Sprite/材质角色和首批静态专用模型生产者。现有公开 API、Native ABI、Windows x64 限制和包白名单保持不变；动态专用效果及 P3/P4 能力未因本地 P2 包而宣称完整支持。

## 0.1.1 版本变更

- 新增 `AttachSurfaceRenderer`，支持将 D3D11 画面经 GPU 复制到 `VirtualSurfaceImageSource`，用于宿主的 XAML 亚克力叠加场景。
- 保留 `AttachRenderer` 的 `SwapChainPanel` 接入，两种输出共用模型、切片、相机和截图。
- 统一颜色纹理作为截图来源；显存估算补充颜色、深度和呈现缓冲区。
- 托管端仅在原生尺寸调整成功后更新尺寸记录。
- 统一中文文档，提供英文 README，并补充线程、对象持有、异常恢复及卸载契约。

从 `0.1.0` 升级后，原有交换链接入可以继续使用。采用新图像表面输出时，宿主需创建并绑定图像源，在 UI 线程按 [API 文档](api.md) 完成连接、刷新、尺寸调整和释放。

版本号与本地打包完成不表示已公开发布。NuGet 推送、GitHub Release 和远端推送分别执行，发布前需核对提交、版本、包内容、原生 DLL 和许可证；构建与自动测试也不替代实际画面及交互检查。
