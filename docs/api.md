# API 与宿主接入

包 ID 与程序集名称均为 `RZeroF1.Minecraft.Projection`。

SDK 以 `ProjectionSession` 为使用单位。每个 session 持有当前投影文档、客户端资源归档、资源包、模型与纹理缓存、网格、切片索引、相机、D3D11 渲染器及诊断状态。每个视口创建一个 session，关闭视口时释放。

## 初始化与调用顺序

宿主提供 Minecraft 客户端 JAR 路径，并通过薄适配层取得 WinUI 渲染目标的 COM 指针。托管 SDK 不引用 XAML 或 WinUI 控件类型。

1. 在目标所属 UI 线程创建 session 和渲染目标。
2. 调用 `AttachRenderer` 或 `AttachSurfaceRenderer` 连接目标。
3. 调用并等待 `LoadAsync`，再处理输入、绘制、切片与截图。
4. 视口物理像素尺寸或 DPI 变化时调用 `ResizeRenderer`。
5. 卸载时停止帧回调，取消并等待未完成操作，再断开目标、解除绑定并释放 session。

```csharp
using RZeroF1.Minecraft.Projection;

using var session = new ProjectionSession();
var clients = installedClientJars.Select(path => new ProjectionClientSource(path));

session.AttachRenderer(panelIUnknown, pixelWidth, pixelHeight);

await session.LoadAsync(
    projectionPath,
    clients,
    resourcePackPath: optionalResourcePackPath,
    progress: progress,
    cancellationToken: cancellationToken);

session.RenderFrame(ProjectionMovementInput.None);
await session.ApplySliceAsync(
    axis: 1, minimum: 5, maximum: 5, cancellationToken);

byte[] png = await session.CapturePngAsync(cancellationToken);
ProjectionMemorySnapshot diagnostics = session.CaptureDiagnostics();
```

示例中的路径、指针、尺寸、进度对象与取消令牌由宿主提供。应在加载之前连接渲染器；连接本身不会把之前已加载的 CPU 场景自动上传到新目标。

## 线程与并发

连接、尺寸调整、绘制、相机更新、截图、断开和释放均在拥有渲染目标的线程执行。WinUI 接入统一使用 XAML UI 线程。

连接渲染器后，`LoadAsync`、`ApplyResourcePackAsync`、`ReloadAsync` 和 `ApplySliceAsync` 也从该线程调用并等待，保留 UI 同步上下文。SDK 在内部执行后台解析和网格构建，并在异步操作恢复后上传 GPU 资源，宿主无需额外将整个 session 操作放入 `Task.Run`。

同一 session 的加载、重载、资源包替换、切片、清理与释放由宿主串行安排，不能与截图或其他资源修改并发执行。场景修改期间可暂停帧回调；若继续调用 `RenderFrame`，应传入 `rendererBusy: true` 跳过绘制。该参数不会加锁，也不会禁止自由相机状态及其原生相机参数更新。

## 渲染目标与尺寸

`AttachRenderer(nint swapChainPanel, uint width, uint height)` 接收 `SwapChainPanel` 的有效 COM 指针和物理像素尺寸，通过交换链显示画面。

`AttachSurfaceRenderer(nint surfaceImageSource, uint width, uint height)` 接收 `VirtualSurfaceImageSource` 的有效 COM 指针和物理像素尺寸，将画面输出到 XAML 图像表面。宿主应创建不透明图像源，并绑定到 `Image` 或 `ImageBrush`。例如，以下代码位于引用 WinUI 和 C#/WinRT 的宿主中：

```csharp
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;

var surface = new VirtualSurfaceImageSource(
    checked((int)pixelWidth), checked((int)pixelHeight), isOpaque: true);
var brush = new ImageBrush { ImageSource = surface, Stretch = Stretch.Fill };
viewport.Background = brush;

nint surfaceIUnknown = ((IWinRTObject)surface).NativeObject.ThisPtr;
session.AttachSurfaceRenderer(surfaceIUnknown, pixelWidth, pixelHeight);
```

宿主持有 `surface` 和绑定关系直至断开；上述借用指针的有效期依赖图像源对象，宿主不应对该借用指针额外调用 `Release`。如果适配层采用其他方式增加了 COM 引用，则由适配层成对释放自己的引用。SDK 通过接口查询保留并释放其自身引用与设备关联。

图像表面接口不接受普通 `SurfaceImageSource`。不要传入悬空指针或任意整数；SDK 无法验证任意地址的安全性。有效 COM 对象不支持所需接口、设备创建失败或原生操作失败时，通过 HRESULT 映射的托管异常报告；异常类型取决于 HRESULT，不保证统一为 `COMException`。

两个连接方法复用同一内部入口，原生端识别 COM 接口类型。已连接时再次连接不会替换目标，也不会应用新尺寸；切换输出前先 `DetachRenderer`。session 释放后调用连接方法会抛出 `ObjectDisposedException`。

调用 `ResizeRenderer(pixelWidth, pixelHeight)` 同时调整 GPU 目标及虚拟图像表面。尺寸为零时规范为 1 像素；任一边超过原生 D3D11 纹理尺寸上限时失败。物理像素尺寸通常由 `ActualWidth / ActualHeight × XamlRoot.RasterizationScale` 计算，宿主需要同时处理尺寸和缩放变化。未连接时尺寸调整不执行操作。

尺寸调整失败不保证保留原渲染目标，宿主应停止绘制、断开并重建。托管尺寸记录只在原生调整成功后更新。

图像表面路径每次实际绘制都刷新完整表面，当前实现不要求宿主注册虚拟图像更新回调。每次呈现仅在 GPU 内复制，不进行 CPU 回读或 PNG 编解码，也不创建独立刷新计时器。

该输出用于让 XAML 亚克力采样模型背景。卡片前方的实色遮挡、画刷配置、系统透明效果关闭或高对比设置都会影响实际显示，仍需在真实宿主中检查。

## 客户端资源

`ProjectionClientSource` 描述一个由宿主提供的客户端：

```csharp
var source = new ProjectionClientSource(
    Path: @"C:\Minecraft\versions\1.21.4\1.21.4.jar",
    Label: "1.21.4");
```

`Label` 可省略。SDK 会读取客户端归档中的 `version.json`，必要时使用归档文件名作为标签。

`DiscoverClientsAsync` 检查候选归档并返回 `ClientResourceInfo`，包含标准化路径、标签、`DataVersion`、显示版本标签与 SHA-256。当路径、标签、文件长度和修改时间均不变时复用发现结果：

```csharp
IReadOnlyList<ClientResourceInfo> clients =
    await session.DiscoverClientsAsync(clientSources, cancellationToken);
```

发现步骤可省略，`LoadAsync` 会执行相同检查。投影带有 `DataVersion` 时优先选择首个版本完全匹配的可读客户端；没有完全匹配时选择首个可读候选。没有可读客户端时加载失败。SDK 不负责下载或分发 Minecraft 客户端资源。

## 加载与重载

`LoadAsync` 接受 `.litematic` 和 `.schem` 路径，以及客户端、可选资源包、进度和取消令牌：

```csharp
await session.LoadAsync(
    projectionPath,
    clients,
    resourcePackPath: null,
    progress: new Progress<ProjectionLoadProgress>(value =>
        Console.WriteLine($"{value.Stage}: {value.Completed}/{value.Total} {value.Message}")),
    cancellationToken: cancellationToken);
```

加载成功后，`Document` 保存投影，`SelectedClient` 标识使用的客户端，`Camera` 按文档边界初始化，`GetMaterialGroups()` 返回全场景材料统计。`MaterialIcons` 按方块状态键提供可用的 PNG 图标，`Warnings` 汇总有数量上限的解析、资源和模型警告。

`ProjectionDocument` 提供 `Format`、`DataVersion`、`Min`、`Max`、`Width`、`Height`、`Length`、`Blocks`、`Palette`、`Regions` 和 `EstimatedCpuBytes`。方块记录包含位置、调色板状态索引和可选区域名称。

`LoadAsync` 开始时先清理当前场景；失败或取消不保证保留原投影。`ReloadAsync` 使用当前投影路径及已选客户端重新加载，不自动保留此前应用的资源包，必要时由宿主重新应用。

`Clear` 释放当前文档、资源归档、CPU 网格、图标与切片缓存，并清空原生场景；已连接的渲染器保留，可继续加载下一个投影。

## 资源包与材料

`ApplyResourcePackAsync` 使用当前客户端与指定资源包重新构建场景，资源包优先于客户端资源。当前实现替换此前的资源包配置，不累积多个外部资源包层：

```csharp
await session.ApplyResourcePackAsync(
    resourcePackPath, progress, cancellationToken);
```

操作成功后更新模型、纹理、材料及切片索引，清空切片缓存并移除当前裁剪；当前文档及相机对象继续由 session 持有。

```csharp
string name = session.GetDisplayName("minecraft:stone");
var allMaterials = session.GetMaterialGroups();
var sliceMaterials = session.GetMaterialGroups(axis: 1, minimum: 5, maximum: 5);
```

`GetDisplayName` 返回资源中的显示名，缺少名称时回退到原方块名。材料分组包含名称、数量和带属性的状态变体；宿主应将返回集合视为只读结果，不修改内部对象。

## 切片

| axis | 坐标轴 |
| ---: | --- |
| `0` | X |
| `1` | Y |
| `2` | Z |
| `-1` | 移除切片 |

范围包含两端，`minimum` 与 `maximum` 使用投影坐标：

```csharp
await session.ApplySliceAsync(
    axis: 1, minimum: 10, maximum: 12, cancellationToken);
int count = session.VisibleBlockCount(axis: 1, minimum: 10, maximum: 12);
```

SDK 裁剪基础场景并生成新暴露的边界几何。CPU 边界缓存上限为 128 MiB，相同范围可复用缓存；超出上限时按插入顺序移除较早的缓存项，命中不会更新淘汰顺序。

传入 `axis: -1` 移除切片；设置 `rebuildBoundary: false` 时只更新裁剪范围，不重新生成或上传边界网格：

```csharp
await session.ApplySliceAsync(
    axis: -1,
    minimum: int.MinValue,
    maximum: int.MaxValue,
    cancellationToken,
    rebuildBoundary: false);
```

没有已加载场景时，`ApplySliceAsync` 直接返回；已加载场景时大于 2 的轴值抛出 `ArgumentOutOfRangeException`。`VisibleBlockCount` 与 `GetMaterialGroups` 使用相同的包含两端范围。

## 相机与逐帧绘制

`ProjectionCamera` 保存相机及模型边界，`Eye` 与 `Target` 描述当前视角。session 包装方法在修改相机后同步原生参数：

```csharp
session.RotateCamera(deltaX, deltaY);
session.PanCamera(deltaX, deltaY, viewportHeight);
session.ZoomCamera(wheelNotches);
session.FocusCamera();
session.ResetCamera();
session.SetCameraMode(ProjectionCameraMode.Free);
```

环绕模式下，旋转改变环绕角度，平移改变目标，滚轮改变距离。自由模式下，`RenderFrame` 使用 session 测量的帧间隔应用移动：

```csharp
var input = ProjectionMovementInput.Forward | ProjectionMovementInput.Right;
bool drawn = session.RenderFrame(input, speedScale: 1, rendererBusy: false);
```

`Fast` 加快移动，`Precise` 降低速度。`RenderFrame` 实际绘制时返回 `true`，未连接或 `rendererBusy` 为 `true` 时返回 `false`。宿主负责调用频率，SDK 不提供帧调度器。

## 截图与诊断

默认隐藏测试模型中的孤立玻璃板端帽。需要显示其原始几何时，可在创建 session 时关闭预览过滤：

```csharp
using var session = new ProjectionSession(new ProjectionRenderOptions
{
    HideIsolatedGlassPanes = false
});
```

该选项默认 `true`，仅隐藏原版普通/染色玻璃板中 north/east/south/west 四个连接属性全部显式为 `false` 的几何；也会隐藏用户有意摆放的此类玻璃板，不自动判断哪些是辅助支撑。设为 `false` 可恢复这些几何。连接中的玻璃板、缺少连接属性的状态、Mod namespace 均保留。投影文档、坐标、材料数量不变；主视图、切包和切片使用同一不可变策略。更换策略需新建 session，并按原生命周期释放旧 session。

过滤项在 `RenderDiagnostics` 中记为 `PreviewFilteredIsolatedGlassPane`，包含次数和源坐标样本；它是展示过滤，不是解析错误或正常不可见分类。花排上方的玻璃端帽因此可以从预览中移除，不需要改动花朵模型、Alpha 或源文件。

`RenderDiagnostics` 返回最近一次成功加载或切包的分组渲染诊断快照，未加载或 `Clear` 后为空。与其他 session 状态一样，由宿主在所属线程读取，不与加载、切包和释放并发访问；应先等待操作完成。已取得的快照不持有 Native 资源，可以在 session 释放后保留。

`ProjectionRenderDiagnostic` 包含 `SourcePath`、`ClientJarPath`、`BlockId`、完整 `Blockstate`、`Region`、`Reason`、`Model`、`Texture`、`Count` 和最多 16 个原始源坐标 `Positions`。`Count` 不受 `Warnings` 的 500 条上限截断；退化面的 Count 为面事件数，其余为对应状态/原因的方块发生次数。路径可能涉及隐私，宿主导出时应按需处理。

`Reason` 区分 `IntentionalInvisible`、`UnsupportedRenderer`、`EmptyModelUnknownIntent`、`MissingBlockstate`、`NoMatchingVariant`、`InvalidBlockstate`、`MissingModelOrParent`、`InvalidModel`、`InvalidModelGeometry`、`UnresolvedTextureReference`、`MissingTexture`、`TextureDecodeFailure`、`InvalidAnimationMetadata`、`DegenerateFace` 和 `WaterloggedFluidUnsupported`。普通渲染失败输出黑紫单位立方体；空气、屏障、光源方块和结构空位保持不可见。占位不表示专用 renderer 已实现，也不作为邻面剔除的可信遮挡体。

纹理在 Windows x64 的 WIC 路径校验，并按 `.png.mcmeta` 动画尺寸和首个帧序索引静态取帧；无动画元数据的非正方形纹理保留尺寸。单张解码纹理上限为 16M 像素、任一边 16384；超限抛出 `InvalidDataException`，内存不足仍传播，不伪装成缺失材质。加载和烘焙也需要配套 Native DLL，但不要求先连接视口。内部成对初始化/释放线程 COM，支持后台加载；旧 Native DLL 不可与新增图像解码入口的托管程序集混用。

基础流体处理邻接、层级高度和面剔除，水走 Alpha Blend、岩浆走不透明深度。目前没有完整原版流体模拟、含水流体几何、动画时钟、复杂 Block Entity Renderer、生物群系光照或高级阴影。含水方块的普通几何保留，并单独输出能力诊断。

`CapturePngAsync` 在原生端重新绘制当前场景但不呈现，随后回读颜色纹理并编码为 PNG。需要已连接渲染器，并从其所属线程调用：

```csharp
byte[] png = await session.CapturePngAsync(cancellationToken);
await File.WriteAllBytesAsync("projection.png", png, cancellationToken);
```

截图只包含渲染器画面，不包含宿主叠加的 XAML 控件。虽然接口返回 `Task`，回读与编码当前在调用期间完成，不应视为后台任务；取消令牌在截图开始前检查，不支持中断已开始的 GPU 回读或编码。

`CaptureDiagnostics` 返回进程私有内存、工作集、托管内存、文档与网格估算、资源缓存、切片缓存和原生 GPU 资源估算：

```csharp
ProjectionMemorySnapshot snapshot = session.CaptureDiagnostics();
Console.WriteLine($"GPU 估算：{snapshot.GpuBytes} 字节");
```

`GpuBytes` 为网格缓冲区、去重纹理和 `宽 × 高 × 12` 字节的颜色、深度及呈现缓冲区估算之和。不包含合成器内部缓存，也不等同于驱动报告的分配总量。

## 异常、取消与恢复

加载、客户端发现、资源包替换、切片及截图接口接受 `CancellationToken`。支持检查取消的位置可能抛出 `OperationCanceledException`；宿主需要等待操作结束后再清理或发起下一次修改。

加载及 GPU 上传不提供事务回滚。取消或异常后，不能假定旧文档、网格或切片状态完整；应清理并重新加载。设备丢失、尺寸调整或绘制失败时，停止帧回调，断开并重建渲染器，然后重新加载场景。没有自动设备恢复流程。

无效投影可能抛出 `ProjectionFormatException`、`IOException` 或相关解析异常。没有可读客户端、未连接就截图或未加载就替换资源包等状态错误抛出 `InvalidOperationException`；参数错误按接口抛出 `ArgumentException` 及其派生类型。原生错误通过 HRESULT 映射为托管异常。

已释放后，加载、发现、资源包替换、切片、连接与 `RenderFrame` 等带生命周期检查的入口会抛出 `ObjectDisposedException`。并非所有读取或清理方法都执行该检查；宿主仍不得在释放后继续使用 session。

## 所有权与卸载

宿主拥有 UI 对象、帧回调和输入事件订阅；SDK 拥有场景、归档、缓存及其原生资源。卸载建议按以下顺序执行：

1. 停止帧回调和新的输入、加载请求。
2. 取消并等待未完成的异步操作，避免与释放并发。
3. 在目标线程调用 `DetachRenderer`。
4. 清空 `Image.Source` 或 `ImageBrush.ImageSource`，解除宿主事件订阅。
5. 调用 `Dispose`；重复释放不会重复销毁资源。

`DetachRenderer` 释放原生渲染器及其 COM 引用，但不清理 CPU 文档；重新连接不会自动重新上传文档，仍需重新加载。`Dispose` 先销毁渲染器，再释放场景和资源归档。

仅支持 Windows x64。其他 Runtime Identifier 会使 SDK 构建显式失败。
