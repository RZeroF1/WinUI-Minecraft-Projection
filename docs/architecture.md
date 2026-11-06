# 架构

```text
宿主应用
  文件选择、客户端发现、键盘与指针事件、帧回调
  SwapChainPanel / VirtualSurfaceImageSource COM 指针薄适配层
       │
       ▼
RZeroF1.Minecraft.Projection
  ProjectionSession：生命周期、加载、场景、切片缓存、截图、诊断
  ProjectionReader 与受限 NBT 解析器
  ProjectionResourceSet、方块状态与模型解析、网格构建
  ProjectionCamera 与 ProjectionSliceIndex
       │
       ▼
rzero_projection_d3d11.dll
  交换链 / XAML 图像表面输出、GPU 资源、WIC 解码与截图回读
```

## SDK 与宿主边界

托管 SDK 面向 `net10.0`，不引用托管 UI 框架。宿主提供 Minecraft 客户端 JAR 路径，通过公开 session 接口传入渲染目标的 COM 指针和物理像素尺寸，并将输入事件映射到相机与渲染 API。

投影解析、资源与模型解析、纹理访问、网格构建、切片边界生成及缓存、相机计算、截图编码、引擎资源所有权和诊断均由 SDK 负责。页面、工作台、XAML 布局、文件对话框和刷新计时器由宿主负责。SDK 不保存宿主业务状态。

## 两种渲染输出

`AttachRenderer` 连接 `SwapChainPanel`。原生渲染器创建合成交换链，以其后台缓冲区作为颜色目标，绘制后调用 `Present`。

`AttachSurfaceRenderer` 连接宿主创建的 `VirtualSurfaceImageSource`。原生渲染器关联 D3D11 设备并创建离屏颜色纹理；绘制后通过 `BeginDraw`、`CopySubresourceRegion` 和 `EndDraw` 将整帧复制到 XAML 图像表面。复制使用返回的纹理子资源和偏移，每次成功的 `BeginDraw` 都配对调用 `EndDraw`。该路径不创建交换链，也不进行逐帧 CPU 回读。

两种接口复用同一内部创建入口，原生端通过 COM 接口查询识别目标类型，共用模型、网格、切片、着色器和相机。公开接口不暴露内部渲染器句柄，也没有新增原生导出函数或修改现有 P/Invoke 签名。

图像源绑定到宿主的 `Image` 或 `ImageBrush` 后参与 XAML 显示，为原生亚克力浮层提供画面背景。最终效果还取决于 XAML 层级、画刷配置与系统透明效果设置。

## 构建依赖与平台

原生构建固定下载 `Microsoft.WindowsAppSDK.WinUI` `2.3.9`，仅使用其 ABI 头文件中的 `ISwapChainPanelNative`、`IVirtualSurfaceImageSourceNative` 等声明。托管 SDK 不引用 WinUI 构建目标，也不向消费者传递 WinUI 运行时依赖。原生 DLL 目前仅支持 `win-x64`。

WIC 将纹理解码为 RGBA 后上传到 GPU。截图从渲染器颜色纹理复制到可读暂存纹理，回读为 BGRA，再由托管 PNG 编码器生成图片；截图不包含宿主叠加的 XAML 卡片。

## 资源所有权与释放

`ProjectionSession` 持有文档、资源归档、CPU 场景、切片缓存、相机及原生渲染器。连接后的原生渲染器持有目标的独立 COM 引用，拥有 D3D11 设备、上下文、交换链或颜色纹理、深度缓冲区及网格资源。

上传期间会复制顶点和索引数据。网格持有纹理视图的 COM 引用，释放临时解码句柄不会使网格纹理失效。切片 CPU 几何缓存上限为 128 MiB，超过上限时按插入顺序移除较早的缓存项。

宿主卸载时停止帧回调，取消并等待未完成操作，再断开渲染器、解除图像源绑定并释放 session。断开会解除交换链或图像表面的设备关联；session 释放时先销毁原生渲染器，再清理其持有的场景与资源。设备丢失或尺寸调整失败后应停止使用当前渲染目标，断开、重新连接并重新加载场景。

## 诊断估算

`CaptureDiagnostics` 提供进程内存、托管内存、文档、网格、资源缓存、切片缓存和 GPU 资源的诊断值。GPU 估算为网格缓冲区、去重纹理与 `宽 × 高 × 12` 字节的颜色、深度及呈现缓冲区估算之和，不统计合成器内部缓存，也不等同于驱动报告的实际分配量。
