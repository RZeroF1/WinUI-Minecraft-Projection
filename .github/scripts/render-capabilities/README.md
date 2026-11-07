# 渲染能力规格维护

`build_spec.py` 将独立验证过的注册台账转成 SDK 的精简事实规格。输入目录作为参数传递，不将本机路径写入 SDK。

```powershell
python .github/scripts/render-capabilities/build_spec.py '<内部研究根目录>' --check
```

检查覆盖全部 43 个客户端、762 条注册、28 个具名 Provider/16 个机制家族及版本化类型/已审计 codec。它同时检查生成结果是否逐字一致，因此不会只比较数量。没有官方映射的记录只导出规范配方键和结构发现证据，Provider 留空；1.14.4 的 Class 键通过明确对照表联接，不能泛化成 Block ID 字符串转换。

有意更新时移除 `--check`，审查生成的 JSON、Profile、来源摘要与配方变化，运行普通测试和相关基线比较。版本列表是封闭端点集，出现新版本必须先明确分类。工具使用 Python 标准库，无网络或 JAR/字节码读取，不包含原版实现。

普通测试不依赖内部研究目录：

```powershell
dotnet run --project tests/RZeroF1.Minecraft.Projection.Tests/RZeroF1.Minecraft.Projection.Tests.csproj -c Release
```

契约见 `docs/render-capabilities.md`。原版 JAR、映射、资源、日志、内部资料不进入 SDK 或包内容，现有包白名单继续保持。覆盖校验是证据台账门槛，不是画面正确率。
