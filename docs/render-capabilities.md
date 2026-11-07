# 版本化渲染能力规格与组合计划（P0/P1）

这份源码规格描述原版需要哪些渲染贡献，并提供覆盖检查与能力评估。登记某个 renderer 不代表 SDK 已实现它。P1 已将数据适配、精确类型关联和组合计划接入现有 ModelBaker，并通过现有公开诊断报告贡献缺失；没有增加专用几何或更改 Native ABI。

所有类型均为 `internal`，位于 `src/RZeroF1.Minecraft.Projection/Capabilities/`。没有新增公开 API 或 Native ABI。

## 身份、规格与来源

`ProjectionRenderCapabilityRegistry.Vanilla` 惰性加载唯一嵌入资源 `RZeroF1.Minecraft.Projection.vanilla-capabilities.json`，产出不可变的 FrozenDictionary/FrozenSet 及只读贡献列表。加载后并发查询不访问磁盘、网络或 GPU，也不持有 ZIP/COM 句柄。

规格包含 43 个确切客户端身份、762 条版本化 BER 注册、28 个具名 Provider 对应的 16 个机制家族及 1049 条版本化 Block Entity 类型声明。相同注册、类型和 codec 集合按内容哈希去重。仅保存标识、事实关系、Profile 和证据摘要；不含原版实现、映射、字节码、纹理、客户端路径或实验文件。

`sourceDigests` 锁定输入台账，`registrationEvidenceSha256` 锁定各版本完整原始注册记录，`codecEvidenceSha256` 锁定已审计端点的共享系统记录。`EvidenceId` 为 `vanilla-render-census-2026-11-07`。它们表达来源追溯，不是 GPU 验证等级。早期结构发现的 `Provider=null`；`Recipe` 是 SDK 规范配方键，不能反推成那个旧版 Java 类的名称。

版本选择必须同时匹配**确切版本 ID 和已验证客户端 SHA256**。文件夹名、DataVersion、资源包声明或相近版本不能替代客户端身份。`SelectVersion` 返回 Verified、UnknownVersion 或 IdentityMismatch；未识别版本不自动继承最近的 Profile。

Profile 分为 routing、resources、boards、bed、chest、data、material 轴。封闭端点集明确选择 Class/Type/Bootstrap、旧资源名称、床颜色来源、箱子 UV 布局等。`SourceDataVersionRequired` 表示数据适配仍必须依据源文件版本，`AuditedRequirementsOnly` 不意味着实现了原版全部材质。完整 atlas/font/item schema 支持不由这个粗粒度 Profile 自动推断。

## 查询契约

```csharp
var registry = ProjectionRenderCapabilityRegistry.Vanilla;
var version = registry.SelectVersion(clientVersionId, verifiedClientSha256);
var required = registry.Declare(version, canonicalBlockEntityTypeId, ordinaryRenderPath);
var assessment = ProjectionCapabilityEvaluator.Evaluate(required, actualObservations);
```

`Declare` 的类型参数是规范、带命名空间的 **Block Entity 类型 ID**，不是 Block ID：例如 `minecraft:skull`、`minecraft:piston`。禁止把 `player_head`、`moving_piston` 或 Java `Blocks` 字段通过字符串替换当作类型 ID。普通非 BE 方块传 `null`；P1 根据显式 Block→BE 类型关系与源状态确定这一点，不能为了得到 Full 将未知关联丢成 `null`。

`ordinaryRenderPath` 是调用方已确认的原版普通路径，不是“找到一个非空 JSON”。已注册类型以登记中的组合需求为准；箱子不因覆盖包添加普通盒子模型而免除专用主体。无 BER 类型只有在相应端点类型台账中已知、且不存在未联接的 Class 继承分派时，才能走普通路径。未知类型、未知路径、早期数字 handler 以及未联接的 Class 路由明确返回非 Known。

`ProjectionCapabilityDeclaration` 保留版本、类型、配方、家族、声明状态及贡献清单。贡献具有 Kind、Role、Dependencies、可为空的具体条件、是否允许策略关闭。它是需求规格，不包含网格、上传任务或 NBT 解析逻辑。

## 贡献与结果

| 贡献角色 | 典型内容 |
|---|---|
| OrdinaryModel | 普通 Block Model、Fluid |
| SpecialModel | 部件主体、移动方块、门户表面 |
| AdditionalContent | 文字、物品、实体、皮肤、图案 |
| RuntimeEffect | 动画、世界效果、光束、调试边界 |

容器/潜影盒、实体床、旗帜、头部、牌、书本、钟、陶罐、铜雕像、嵌入物品、嵌入实体、移动方块、光束、门户、潮涌核心和调试框分别属于 16 个家族。多个 Provider 可使用同一家族；有共同底层几何也不意味着形状相同。

`ProjectionCapabilityEvaluator` 仅评估调用方提供的实际观察，不运行 renderer，不读取数据。每项观察必须有明确状态和非空证据说明；未知/重复贡献、非法状态、无条件主体被标为空或不允许关闭的贡献被关闭会抛出 ArgumentException。没有观察的贡献默认 Unsupported。

| 状态 | 语义 |
|---|---|
| Resolved | 此次输入下该贡献确已完整完成；不能用原版注册或文件存在作依据 |
| StaticLimited | 明确只实现规范静态姿态/某个子集，证据需说明限制 |
| KnownEmpty | 已知输入确实满足需求声明的空条件；缺失 NBT 不算空 |
| UnknownData | 确认贡献需要输入，但输入未知或未适配 |
| ResourceFailure | 真实资源读取/解码/解析失败，保留原因，不降级成 Unsupported |
| DisabledByPolicy | 需求允许关闭，调用方显式采用关闭策略；目前仅调试 overlay |
| Unsupported | 此贡献尚无实现/没有实现观察 |

| 总体结果 | 判定 |
|---|---|
| Full | 声明 Known，全部贡献满足，且没有静态限制 |
| StaticLimited | 全部贡献满足，但至少一项明确静态受限 |
| Partial | 有实际完成/静态受限的贡献，同时仍有未满足需求或声明未知 |
| Unsupported | 没有实际完成的贡献，且需求未全部满足 |

KnownEmpty 和 DisabledByPolicy 可以满足其需求，但不作为“已经绘制几何”的证据。Full 始终相对于**所声明的贡献和输入**；不代表游戏仿真、所有资源包、所有动画、GPU 或实际宿主已验收。特殊主体不能由普通底座替代。取消、OOM、设备异常不属于以上业务结果，执行层必须继续传播。

例如营火底座成功、物品未知为 Partial；明确空槽可满足 ItemContent。牌底座成功、文字未知仍为 Partial。讲台 has_book=false 可以满足书本及其动画的空条件。调试框显式关闭可以正常完成。已经实现箱体而只有固定闭合姿态应标 StaticLimited，不能填 Resolved 冒充动画等价。

## 覆盖门槛

`ProjectionCapabilityCoverage.Check` 比较**独立观察的完整注册快照**，逐键检查注册、Provider、配方、家族，同时可比较 Block Entity 类型和资源 codec 集合。结果中的 Issues 区分 Missing、Duplicate、Unclassified、Mismatch、Unaudited；同数量但换了一个注册也不能通过。

`UncheckedChannels` 明确列出未提供的类型/codec 通道。`Passed` 只有 Issues 与 UncheckedChannels 都为空才为 true。没有某端点的 codec 审计时，`null` 表达未知，不能拿空集合冒充“原版没有 codec”。覆盖通过只证明台账吻合，不证明 renderer 已实现。P0 没有在运行时扫描用户 JAR 字节码；新增客户端的独立发现仍是维护规格时的受控输入。

未知 Profile 轴、schema、家族或配方加载失败，禁止默认选择一个普通模型。新版本即使所有普通 JSON 可读，也必须先完成独立注册/资源分类、更新证据和测试。

## 更新与验证

在仓库根目录执行普通合成测试，无需 Minecraft JAR 或内部报告：

```powershell
dotnet build RZeroF1.Minecraft.Projection.slnx -c Release
dotnet run --project tests/RZeroF1.Minecraft.Projection.Tests/RZeroF1.Minecraft.Projection.Tests.csproj -c Release
```

`CapabilityRegression` 与既有渲染/解析/资源/生命周期回归共用本地测试入口。测试同时覆盖登记、组合、状态、无 BER 对照组、未知机制、注册删除/替换/重复、codec 未分类和实际合成 ZIP 烘焙。测试依照仓库约定保留在本地；不为内部语料添加远端 CI 依赖。

维护工具 `.github/scripts/render-capabilities/build_spec.py` 只接收内部研究台账，导出上述精简事实规格。`--check` 只读检查，任何漂移失败；默认生成仅用于完成审查后的有意更新。增加端点必须显式调整封闭版本集、Profile、分类与验收，不能自动扩张兼容范围。工具不下载、不解包、不复制原版源码或资源。

P1 的 `ProjectionBlockEntityBindings` 枚举原版类型关系，`ProjectionVisualStateAdapter` 依据独立源版本适配外观，`ProjectionRenderPlanResolver` 组合每个源方块的需求和实际观察。旧的空模型名称片段启发式已由类型/登记关系替代。普通 JSON 与流体继续由原生产者生成几何；注册家族的部件、文字、物品/实体和程序化效果仍可明确为未实现或数据未知。

计划在内部保存全部贡献，公开诊断使用 `Capability.<Result>.<Kind>.<State>` 聚合缺失/限制。`KnownEmpty` 只来自明确适配的空列表、空文字或 `has_book=false` 等条件；缺字段、身份冲突或未知源版本不能作为空。已有底座不因缺特殊贡献而隐藏；整块无主体仍占位，损坏资源继续报告真实错误。材料/切片按源方块计数，不能求和贡献诊断 Count。

每次资源集合/覆盖变更生成新代际，计划随场景重建，不以 Palette state 缓存实例外观。输入、计划的预算和内存估算已接入既有取消、串行调度和释放契约。完整 source/diagnostic 契约见 `docs/api.md`。`P1DataRegression`、`P1PlanRegression` 与既有普通回归共用本地入口，内部资源和测试不进入公开包。
