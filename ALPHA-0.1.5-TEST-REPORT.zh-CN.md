# Alpha 0.1.5 · 测试与发布报告（v9.0）

> English: [`ALPHA-0.1.5-TEST-REPORT.md`](ALPHA-0.1.5-TEST-REPORT.md) · 日本語: [`ALPHA-0.1.5-TEST-REPORT.ja.md`](ALPHA-0.1.5-TEST-REPORT.ja.md) · 发布说明: [`ALPHA-0.1.5-RELEASE.zh-CN.md`](ALPHA-0.1.5-RELEASE.zh-CN.md)

> 全部读数在**切片目录内**取得（独立于私有构建目录；全新下载解压目录复验同读数）。

| 项 | 读数 |
|---|---|
| 编译（`AgentRuntime.slnx -c Release`） | **0 错误** |
| 测试（`tests/AgentRuntime.Tests`） | **1347 / 1349 通过** |
| 未通过的 2 条 | `PitfallsDerivedTests.公开变体_与python派生物逐字节一致` · `坑集索引_与python派生物逐字节一致` —— **内部专属**：需要内部 `docs/PITFALLS.md` 的派生物，公开切片不含该文件 |
| TUI2 自检（`AgentRuntime.Tui2 --selftest`） | **PASS（全部断言通过）** |
| 发布扫描（`wb-cli scan <slice>`） | **硬红线 0**，可发布（软提示仅称谓/人名） |
| 协议区 | v102（25 行 / 5,234 token ≤ 5,600） |
| 切片规模 | **635 文件 / tar 1.6 MB**（无 bin/obj） |

## 复现（任何人拿到切片都能跑）

```bash
cd kernel
dotnet build AgentRuntime.slnx -c Release
dotnet test tests/AgentRuntime.Tests -c Release
```

## 本版测试变化

**新增**
- `TuiStringsTests`：三语键集一一对应 + **英文档不含汉字** + 启动播报走语言包（切中文必须回中文）
- `TopBandLanguageTests`：真宿主渲染顶栏，英文档出现汉字即红；空台面必须是语言包的空值文案
- `KnowledgeSinkTests`：**不合规范的节点标记必须报错不许静默跳过**（含规范形正例）
- `TuiInvariantTests`：**预组装请求_原对象发出_一轮只组装一次**（`Assert.Same` 引用相等）

**随 TUI1 退役删除（12 个文件 + 若干帧级用例）**
- `TuiSplitTests` / `TuiInvariantTests` 的帧级用例 / `TuiExitVoiceTests` / `BracketedPasteTests` /
  `ExitPolicyTests` / `InputCommandRuleTests` / `LifecycleCardViewTests` / `SelfReportDisplayTests` /
  `DecisionReportBindingTests` / `DecisionReportFlowTests` / `ContinuationCapTests` /
  `LifecycleCloseoutTests` / `ThinkingLineRoleTests` / `ToolVisibilityTests`

> 完整版测试数（内部构建）：**1349 / 1349**；切片少 2 条内部专属。
