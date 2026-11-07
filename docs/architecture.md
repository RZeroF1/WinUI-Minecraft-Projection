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
  BlockEntityStore → VisualStateAdapter + 独立源数据/客户端 Profile
  能力登记 → 每个源方块的 RenderPlan 与贡献诊断
  ProjectionResourceSet、方块状态与模型解析、网格构建
  ProjectionPartGraph / Pose / Cuboid / Quad
  ProjectionSpriteResolver + 逻辑材质角色
  P2 静态专用模型生产者
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

资源层使用配套 Native WIC 图像解码入口，在设备创建前验证 Alpha 和动画帧元数据；返回的 BGRA 内存由 Native 分配、托管复制，再通过配对入口立即释放。静态帧按资源缓存预算保存，切包和释放时清空。模型解析先决定可见/占位及保守完整遮挡体，再按世界 Cullface 生成网格；单层切片分别恢复两侧边界。

标准 JSON 面采用背面剔除，保留 Cross 的原始两张平面及正反面定义。退化面不上传，法线保持有限值。透明表面按独立 quad 中心排序，水不写深度、岩浆写深度；相交透明面仍不保证精确排序。纹理生成 mip 链，近景保留 Point 像素外观，远景普通/Blend 材质使用各向异性过滤；Cutout 暂保留首级 Point 覆盖，未实现远景 Alpha 覆盖 mip 策略。

## 资源所有权与释放

文档拥有内部 Block Entity Store，保留有预算的原始 payload 和来源；外观适配输出 SDK 内部 DTO。实际打开的客户端文件哈希选择原版渲染 Profile，源文档 DataVersion 选择独立的数据 schema；未知身份不使用相近端点。显式 Block→BE 类型表关联原版注册，普通 JSON 的存在不取消专用贡献，也不靠方块名称片段推断 renderer。

`ProjectionRenderPlanResolver` 在普通烘焙准备阶段生成需求，并组合原有 JSON/流体生产者的实际观察。计划保留源位置/Region、资源代际、外观 Profile、世界包围盒和各贡献结果。普通模型与专用主体/文字/物品/实体/效果可同时存在；缺少附加内容时保留已完成几何。P1 没有部件图、字体、物品/实体 renderer 或新 shader。现有 SourceBlock 统计、Cullface、切片补面和 Native 上传路径继续复用。

计划不跨文档或外观共享可变缓存；切包后重建，提交场景前检查资源代际。内部计划预算与数据预算分别限制新分配，场景估算计入计划。缓存仍由原 Session/ResourceSet 拥有；同一 Session 的资源操作继续由宿主串行安排，取消并等待后才能 Clear/Dispose。

P2 的特殊几何由不可变数值配方 `ProjectionPartRecipes` 与独立实例姿态组成，经 `ProjectionSpecialMeshBuilder` 输出到现有 `BatchBuilder`。Cuboid/Quad 统一处理逻辑 UV、面法线、镜像绕序、部件可见性、材质和 AABB。箱体单双件、床头尾、潜影盒六向、钟体、共享七部件 BookModel、陶罐四面和雕像四姿态共用此协议。特殊面没有邻面剔除，因此切片边界不重复上传它们；Native 世界平面处理越过源格的书本和雕像。源计数仍来自 `ProjectionDocument`。Double128 使用连续 UV，并将几何分配给原来的两个源格。

`ProjectionSpriteResolver` 不持有 Native 句柄。Atlas 定义按资源层追加 source，纹理仍按既有优先级最后覆盖；解析结果以资源 Generation 归属，Reload 清除依赖。P2 支持 direct、directory、single、unstitch 和 filter 子集，区分 divisor 裁剪单位、模型画布与 PNG/首帧尺寸。色板派生、字体、Item special 和外部皮肤未实现。精确资源端点为 1.19.2/26.2/26.3，箱子另有 1.14.4 Double128 验收；其它端点显式未验证，不按相近版本推断支持。

`ProjectionSession` 持有文档、资源归档、CPU 场景、切片缓存、相机及原生渲染器。连接后的原生渲染器持有目标的独立 COM 引用，拥有 D3D11 设备、上下文、交换链或颜色纹理、深度缓冲区及网格资源。

上传期间会复制顶点和索引数据。网格持有纹理视图的 COM 引用，释放临时解码句柄不会使网格纹理失效。切片 CPU 几何缓存上限为 128 MiB，超过上限时按插入顺序移除较早的缓存项。

宿主卸载时停止帧回调，取消并等待未完成操作，再断开渲染器、解除图像源绑定并释放 session。断开会解除交换链或图像表面的设备关联；session 释放时先销毁原生渲染器，再清理其持有的场景与资源。设备丢失或尺寸调整失败后应停止使用当前渲染目标，断开、重新连接并重新加载场景。

## 诊断估算

`CaptureDiagnostics` 提供进程内存、托管内存、文档、网格、资源缓存、切片缓存和 GPU 资源的诊断值。GPU 估算为网格缓冲区、去重纹理与 `宽 × 高 × 12` 字节的颜色、深度及呈现缓冲区估算之和，不统计合成器内部缓存，也不等同于驱动报告的实际分配量。
