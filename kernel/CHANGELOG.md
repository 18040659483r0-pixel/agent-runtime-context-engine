# 变更记录（Agent Runtime）

## 0.8.0-dev — **宿主续跑 `--auto-continue`**（2026-09-16 13:4x，主人定）

> 口径：**工具结果落地后，[WB] 要能自己接着说话** —— 否则每个工具轮都得人在终端里再敲一句，那就是「不自持」。
> 依据：`docs/DESIGN-TOOL-FACE.md` §七·5；Stage 1 首跑实测（靠驱动器发「继续」不是长久之计）。**不改协议区**（v7 一个字节未动）。

**内核（不变量：续跑轮不写 UserInput、不接受用户输入、只在尾部追加）**
- `RuntimeContext` 新增 `IsContinuation`（显式标记，默认 `false` ⇒ 老路径一个字节不变）。
- `AgentRuntimeEngine` 新增 **`ContinueAsync()`**；`ChatAsync` 与它共用一个私有 `RunAsync(message, continuation, ct)`。
- `RequestAssembler`：`message` 改为可空；**非续跑轮仍必填**（老契约不松），续跑轮**不追加用户消息**。
- `AppendStreamModule`：续跑轮**不写 `UserInput` 事件** —— 否则会把工具结果当用户输入又记一条（静默污染流）。

**宿主**
- `RuntimeHost` 新增 `ContinueTurnAsync()` · `StreamCount` · **`HasToolOutcomeSince(cursor)`**（auto-continue 的**唯一触发判据**：流里真的多了 `ToolResult`/`ToolDenied`；不看模型自述 —— 被拒也是结果）；一轮尾部处理抽成 `FinishTurn` 供两种轮共用。
- CLI 新增 **`--auto-continue <n>`**（默认 **0 = 关**；不合法报用法错误不静默当 0）：一轮结束后若流里落了工具结果就**不再问人**直接再问一轮，最多 n 次；每次在 stderr 留一行 `[auto-continue]` 痕（**不进 prompt、不改 stdout 口径**）。单发（`--chat` 之外）也生效。
- Stage 1 驱动器 `e2e-shadow-stage1.py` 因此**不再发「继续」**（流里不再有宿主噪声），并加 `--verbose` 采集规模/命中。

**证据**
- `dotnet build` 0 警 0 错；`dotnet test` **365/365**（+`AutoContinueTests` 4 条：续跑不写 UserInput · 续跑拒绝用户输入且正常轮仍必填 · 触发判据只看流（工具结果/被拒）· 续跑轮末条不是用户消息）。
- 真机（Stage 1 第二次跑，见 `docs/REHEARSAL-STAGE1-DOUBLE-RUN.md` §4.2）：prompt 37,936 / **cached 37,760（99.53%）** / uncached 176 / Runtime 开销 32 ms；本轮 0 工具调用 ⇒ 续跑未触发（其真机验证待一个「非动工具不可」的任务）。

## 0.7.1-dev — **must-ask 档：发布 / 不可逆命令**（2026-09-16 13:1x，主人定）

> 口径：**`svn commit` 这类动作从「需批」升到 must-ask** —— 目的是"让主人还有机会在遇到复杂问题时先停手"。
> 依据：`docs/DESIGN-TOOL-FACE.md` §四 / §七·4。**不改协议区**（v7 正文一个字节未动）。

**新增第三档 `ToolRisk.Critical`（中文说法「发布 / 不可逆（must-ask）」）**
- 与 `Mutating` 的差别不是"程度感"，而是**两条可断言的硬约束**：
  ① 审批面**必须**把决定型内容**逐字完整**摆出来（§十·34）——`svn commit -F <文件>` **展开提交信息全文**，不说"见文件"；
  ② 账本按档单独记（`ApprovalEntry.Risk`）⇒ 事后可按档复核"点过几次头"。
- **判据唯一声明处**：`ExecCommandRisk.Classify(command)`（命令级）+ `ToolNames.RiskOf(name)`（工具级）。
  表里只收两类：**发布**（`svn commit` / `git push`）与**不可恢复删除**（`rm` / `rmdir` / `unlink` / `shred` / `dd` / `mkfs`）。
  **默认方向 fail-closed**：判不出（命令空 / 首词不认识 / 复合命令）⇒ **至少需批**，**绝不降为免批**；判据**故意偏严**（某个参数 token 命中即升级）。
- **复合命令取最高档**：`svn status && svn commit` ⇒ must-ask（按 `&&` / `||` / `;` / `|` / 换行切分后取 max）。
- `ITool` 新增 `RiskFor(ToolArgs)`（默认 = `Risk`）—— 让"**同一个工具、不同动作、不同档**"成立；
  `ToolRunner` 的免批判据由 `RiskOf(name)` 改为 **本次动作档**（`risk != ReadOnly`）。
- `ExecMessageFiles.ReferencedIn(command)`：从命令里找出 `-F <文件>` / `--file <文件>` 引用的提交信息文件（**只解析不读**）；
  `ExecTool.Preview` 在 must-ask 档**逐字展开**它（读不到就**明说未展开**，不静默）；
  展开内容经**同一条截断口径**（截断必明说 + 给完整落点），**不是拼接后绕过预算**。
- **不做**（另一个方向、需主人单独点头）：把只读命令（`svn status` / `log` / `info` …）降为免批。
- 测试：**361/361**（+`MustAskTests` 26 条：命令分级正/负例 · 复合取最高档 · 未登记仍 fail-closed ·
  `RiskFor` 同工具不同档 · 审批面展开提交信息全文 · 读不到时明说 · 账本按档记 · 一次一批不长期放行）。
- ⚠️ **安全框架（白名单 / 沙箱 / 围栏）仍未做** —— 仍是"后补"，不是遗漏；本档只是**在审批那一刻把信息给全**。

## 0.7.0-dev — Protocol v7 + 工具面（G1）+ 审批闸门（G2）（2026-09-16）

> 依据：`docs/DESIGN-TOOL-FACE.md`（§二 结论选 **B · 文本协议**；§四 审批闸门；§五 不变量 F1~F5；§七 落地；§八 待裁决）。
> 协议 v7 由主会话定稿并提交（SVN r572）：**正文一个字节未改**，本次只**消费**它（新增的 `[TOOL]` 子句 = 一次回复只允许一次调用 / 必须等结果事件 / 被拒也是结果）。

**工具面（G1）**
- Core 新增 `AgentRuntime.Core.Tooling`：`ToolNames`/`ToolRisk`（封闭集 + 分级）、`ToolReport`（`[TOOL] name {json}` 解析）、
  `ToolArgs`（JSON-in-text 的受控解析 + 摘要）、`ToolLimits`（**只做上下文保护**，不是安全边界）、
  `ToolPaths`（**路径真身**：解析 ~ / 相对 / `..` / 符号链接）、`ToolBase` + `read`/`list`/`write`/`edit`、
  `ITool.Preview` + `ApprovalFace`/`LineDiff`（**知情审批面**）、`IEventSink`（结果必须落成事件）、`InMemoryEventSink`。
- 解析判据：**一次回复只允许一次调用**（多于一次 ⇒ 拒绝，不取第一个）；JSON 只认**大括号配平**段（转义/字符串内的括号不算）；
  块头必须**行首**（与既有块同族）。
- 结果按既有的 **V3 只追加流**记成事件：`ToolResult`（既有 KIND）/ **新增 `ToolDenied`**；行号即地址，可重放（F2）。
- Modules 新增 `ToolModule`（`tool`）：**零注入**（不改 prompt 一个字节），必须有事件落点（没挂 `append-stream` ⇒ 装配期报错）。

**改定（2026-09-16 03:00，主人定；由主会话复核后落地）**
- **砍掉 `exec` / 子进程**：论文里没有这一项 ⇒ `ExecTool`、命令白名单、超时设置**全部移除**；工具集收窄为
  `read` / `list` / `write` / `edit`。模型若点 `exec` 这类未登记名字 ⇒ 归**对外类 must-ask** ⇒ 默认拒绝（fail-closed）。
- **取消路径围栏**：目标就是"能改本机任何文件"，安全性**全部压在审批质量上**（`DESIGN-TOOL-FACE` §四·二 S1~S6）。
  `ToolSandbox`（含 `Root` / "未配置 ⇒ 一律拒"）删除，换成 `ToolLimits`（只留上下文保护上限）+ `ToolPaths`（真身）。
- **审批面做到"知情"（S2/S3）**：真身绝对路径 + 存在性 + 现有行数/字节；**新文件给全文 / 已存在给 diff（带 +行/−行 精确计数）**
  / `edit` 给 `oldText → newText` 块；**覆盖已存在文件显著标注**；超预算则截断**明说**并给完整内容落点。
  审批面**只由 Runtime 从结构化参数渲染**，模型正文进不来（否则模型能伪造提示骗人点头）。
- **CLI 审批（G2 落地）**：新增 `ConsoleApprovalGate`（审批面进 stderr、答复读 stdin，只认 `y`/`yes`）；
  **stdin / stderr 任一不是终端 ⇒ 不问人、直接拒**（fail-closed）并说一句人话；EOF ⇒ `Unknown` + 审批者记 `non-interactive`（**绝不谎称有人点头**）。
- **端到端验证**（PTY 真机驱动，可复算）：`src/AgentRuntime.Cli/e2e-tool-approval.py` —— 点头 ⇒ 文件确实被写入且审批面含真身+全文；
  拒绝 ⇒ 文件**确实没被创建**。
- 测试：**333/333**（+ CLI 审批 12 条：非交互 / EOF / y-yes-yes变体 / 拒绝 / 知情面真身与 diff / 一次一批不缓存）。
- **TUI 审批（G2 的 TUI 落地）**：新增 `TuiApprovalGate` —— 审批面进**右栏详细块**（Runtime 渲染）、键 `y/n`；
  非交互 ⇒ 拒；取不到键 ⇒ `Unknown` + actor=`non-interactive`。TUI 的按键循环在轮次期间是阻塞的 ⇒ **闸门自己取键**。
- **右栏折行开关**：`SplitFrame.DetailWrap` —— **只在待审批时**把详细块由「截断」改为「折行」，
  因为审批的「知情」要求人看得到**完整**真身路径（PITFALLS #46）。
- **TUI 端到端**：`src/AgentRuntime.Tui/e2e-tool-approval.py`（PTY）—— 右栏出现审批面（含真身 `/private/tmp/…`）；
  `y` ⇒ 文件真被写入；`n` ⇒ 确实没被创建。**PASS**
- 测试：**333/333**（+ CLI 审批 12 条 + TUI 审批 5 条）；`verify-demo.sh` 仍绿（无待审批 ⇒ 帧逐字节不变）。
- 坑集：**#46**（审批面被截断 ⇒ 待决定内容折行永胜截断）· **#47**（E2E 夹具三连坑：按键切分 / 提示吃 stdin / 回显缺块头）。
- **exec 回归（2026-09-16 12:58 主人改定）**：新增 `ExecTool`（`{"command":"…","timeoutSeconds":?}`）——
  理由：**[WB] 要自持**（自己跑 `svn` 与 `tools/frozen-build/build-frozen-corpus.py`）；
  **安全框架后补**（命令白名单 / 沙箱 / 目录围栏都**还没做**，是"后补"不是遗漏）。
  本版只守三条（都是"别把会话搞死"而非安全）：超时（默认 60s、硬上限 600s）、输出上限（**超限明说**）、走审批闸门。
  语义：**非 0 退出是"结果"**（跑了但失败），不是拒绝；审批面给**完整命令原文 + 工作目录 + 超时**（S2）。
- **自持闭环已实测**（`src/AgentRuntime.Cli/e2e-exec-selfsustain.py`，PTY）：[WB] 自己跑 `svn info` ⇒ exit 0；
  自己跑语料重建闸门 `build-frozen-corpus.py --check` ⇒ exit 0 并回传校验输出。
- 测试 333 → **335**（exec 在册 + 真跑得动 + 审批面原文 三条新断言）。

**审批闸门（G2）**
- `IApprovalGate` + 四个实现：默认 `NonInteractiveApprovalGate`（非交互 / 无 TTY ⇒ **拒绝**）、`UnknownApprovalGate`（判不出 ⇒ Unknown ⇒ 当拒绝）、
  `ScriptedApprovalGate`（测试）、`OneShotApprovalGate`（**一次一批**：摘要取走即失效，不产生长期放行）。
- 分级：`read` / `list` 免批（**不打扰人**，闸门根本不被调用）；`write` / `edit` / `exec` 需批；未登记的工具名归**对外类（must-ask）**⇒ 默认拒绝。
- `ApprovalLedger`：只追加、可落 JSONL，**不进 prompt**（F5 逐字节断言）；**模型不得自批**（`Approved` 的 actor 非 `human` ⇒ 抛错）。
- 参数体检在**审批之前**：不该请人批准一个连参数都写错了的动作。
- `exec` 的沙箱边界**只**做四件事（首词白名单 / 工作目录限定 / 超时 / 输出上限），**白名单为空或未配根 ⇒ 一律拒绝**；边界本身列为**待主人裁决**（不自造）。

**测试**
- 新增 3 个测试类 **35** 条：`ToolFaceTests`（F1 不改已有段字节 / F2 结果是事件·行号即地址·可重放 / F3 `/ablate tool` 关得掉但协议区摘不掉·且关了真的不兑现 /
  F4 fail-closed（Unknown / 默认非交互 / 一次一批没点头 / 模型自批不算）+ **正例控制组「批准后确实执行」** / F5 账本不进 prompt 逐字节不变 / 装配闸门 / 块头清单 / 名单一致）、
  `ToolReportTests`（解析 8 条 + 沙箱越界 + 各工具边界，负例有牙）、`ApprovalGateTests`（一次一批 / 模型不得自批 / 账本只追加）。
- `ProtocolZoneTests` 的块头清单断言按 v7 更新（`ReportBlockHeaders` 4 → **5**，补 `[TOOL]`）；`ProtocolText` 正文与预算**一字未动**。
- **320/320 全绿**（基线 285 + 35）；`dotnet build` **0 错 0 警**。

**顺手修**：`RuntimeHost.BuildModules` 的 `effective` 漏拷 `skill` 段（PITFALLS #14 同类静默丢配置）⇒ 补上；并把工具面的沙箱/闸门/账本透传给 `ModuleRegistry.Create`。

**未做（本轮）**：CLI / TUI 的审批交互 UI（待裁决 Q2）；`--tool-*` 命令行开关（待裁决 Q4）；真机 API 验收（由主人做）。

## 0.6.0-dev — Protocol v5（协议区扩容：L1/L2/L3 + 铁则 + memory-index + 各区契约，2026-09-16）

> 依据：`docs/DESIGN-SKILL-LAYERS.md` §十（主人 2026-09-15 23:4x 定「协议区扩容」）；协议区规格：`docs/REPORT-PROTOCOL-ZONE.md` §三/§四/§七 Q8i。
> 协议区仍是「只写怎么对话，不写怎么做人」；协议变更 = 改头部 = 缓存整体归零 ⇒ **一次做完**（v4 → v5）。

**协议 v5（R1-P）**
- `ProtocolText.Version` `"4"` → **`"5"`**：第 1 条读序改按层（`rules (constraints, they win) -> knowledge (L1 law + L2 problem map, detail by id) -> memory (an index, not history) -> events -> [TAIL] -> [DRAFT] -> [FOCUS]`）；第 2 条改「**按 id 索取、不得整份读**」（`Read by id, never in bulk`）；第 3 条补 KIND 封闭集 + 历史不可变（原第 2/3 条合并）。
- **`[SKILL] S-xxx-007` 自报段不写**：Runtime 侧「按 id 装载 L3」尚未实现，写进去 = 协议承诺了运行时不兑现的动作 ⇒ 静默失败；第 2 条「按 id 索取、不得整份读」已覆盖语义，待 skill 装载落地后随内核版本升 v6。
- **预算重定**：`DeclaredMaxLines` 10 → **8**、`DeclaredMaxTokens` / `MaxTokens` 240 → **280**（正文**一个字节不删**；设计预估 20 行 / 640 token，实际落地 **6 行 / 1029 字符 / 258 token**）。段前缀与段上限（`FocusPrefix` / `TailPrefix` / `TailMaxLines=12` / `DraftPrefix` / `DraftMaxLines=16` / `ReportBlockHeaders`）**一个字节未动**。
- 版本号仍**只进 `FrozenManifest` 账本，不进 prompt**。

**测试**
- `ProtocolZoneTests.Gate_ProtocolBudget_IsWithinLimits_AndRankZero` 按新实测值断言（行数=6 · 字符=1029 · token=258 · Rank 0），P1~P4 四条闸门（来源只能是代码 / 收尾不可改 / 用户不可摘 / 预算+Rank0）仍全绿；`TuiInvariantTests` 区栈版本标签同步 v4 → v5。
- **基线 261 全绿**（D1 只重述闸门，不增删测试）；`dotnet build` **0 错 0 警**。

**TUI（P4 / P5 面板补齐，`docs/DESIGN-V4.4-TUI.md` §三）**
- **P4 焦点 · 悬空标签告警**：新增 `FocusService.Verify`（口径与 `CurrentTailService.Verify` / `DraftService.Verify` 一致）；显式焦点/分叉后引用流里不存在的标签 ⇒ `FocusPanel` 报警不静默。
- **P5 快照 · 账本补「模型」字段**：`SnapshotPanel.Describe` 增 `[快照] 模型`（换模型后一眼对账）。
- **P5 快照/恢复 · `/fork` 入口**：`PanelRouter` + `ResumeSupport.ForkStream` —— 只分叉不续写、原流只读保留、目标已存在拒绝覆盖；续写仍走 `/resume`。
- **P6 消融 · 可见反馈**：`AblationService.Apply` 改「**本会话已摘**」；右栏状态块与 Δ 在 `/ablate` 后当场刷新（`StateChangingCommands` 驱动）。
- **HelpLines / 文档**：`/fork` 进面板清单；`docs/TUI-DEMO-SAMPLE.md` 按键表 + §三/§六/§七/§八 样例帧随 `demo.sh` 重出（协议 v5：1029 字节 / `efda91f2ed1e`，逐字节一致）。
- **测试**：新增 **7** 条（`Focus_Verify_悬空标签` / `P4_焦点面板` / `P5_快照模型` / `P5_fork` ×2 / `消融本会话已摘` / `Split_ablate_右栏反馈`），**268/268 全绿**（基线 261）。

**文档**：`docs/REPORT-PROTOCOL-ZONE.md`（§三 v5 正文 + §四 P4 + §七 Q8i）、`docs/DESIGN-PROTOCOL-ZONE.md`（§四 预算表 / §五 首版内容 / §八 对照表）、`docs/DESIGN-SKILL-LAYERS.md` §十（标注已落地，诚实写明「设计预估 20 行 / 640 token，实际落地 6 行 / ≤280 token」）、`docs/CHANGELOG.md`（本条）。

**未做（本轮）**：不提交 SVN（由主人做）；不跑真机 API（真机验收由主人做）。

## 0.5.0-dev — V4.3 Dynamic Draft（动态草稿区 R5 + 次序重排，2026-09-15）

> 论文 §4.2（非确定性例外）/ §5.1 第 **7** 项：把「讨论中的构想 / 未定稿需求 / 未验证假设」单独隔离到**唯一允许大删大改**的区域（R5）。
> 规格书：`docs/DESIGN-V4.3-DRAFT-REGION.md`（定稿：主人 2026-09-15 23:2x 点头 Q5~Q9 五条）；协议区规格：`docs/REPORT-PROTOCOL-ZONE.md` §三/§四/§七 Q8h。
> 本次把「**次序重排**（R3 居末）与 R5 同一次做完」（Q9：同一处闸门，分开做等于两次改头部）。

**协议 v4（R1-P，改头部 ⇒ 缓存整体归零）**
- `ProtocolText.Version` `"3"` → **`"4"`**：第 1 条**读序**改为 `frozen rules/knowledge/memory (stable) -> events (E###, append-only) -> [TAIL] (working state) -> [DRAFT] (open, unsettled items) -> [FOCUS] (ids to attend)`；第 4 条自报条补 `[DRAFT]` 块（格式/上限与 `[TAIL]` 并列，触发同为「任务/待办/草稿变化时」）。
- 新增与协议同处声明的段常量：`DraftPrefix` / `DraftMaxLines = 16` / `DraftMaxChars = 3000` / `FocusPrefix` / `ReportBlockHeaders`（块头清单唯一声明处）。
- **预算放宽 Q5**：`DeclaredMaxLines` 8 → **10**、`DeclaredMaxTokens` / `MaxTokens` 200 → **240**（正文**一个字节不删**；实测 **6 行 / 849 字符 / 213 token**）；`--protocol-show` 新增 `[DRAFT]` 上限行；`docs/REPORT-PROTOCOL-ZONE.md` §三 逐字文本升 v4、§四 P4、§七 新增 **Q8h**。

**区域序重述（R3 居末）**
- `DeterminismGate`：规范序改为 **R2 → R4 → R5 → R3**（`DynamicSequence` / `DynamicSequenceText` 为**唯一声明处**）；
  **记号（区编号 R2/R3/R4/R5）与位置（规范序下标）分离**（`DynamicRank` / `DynamicPosition`）—— 旧版用一个数字兼做两者，R3 居末后表达不出来；
  新增 **「R3 之后不得有任何段」** 判据（由位置单调 + R3 位置最大共同守住），违序仍**报错不纠正**；
  旧名 `EnsureFocusIsLastDynamicRegion` 保留为薄包装（现在**名副其实**）；Help 文本 / 注释里的旧序 `R2 → R3 → R4 → R5` 全部同步。

**R5 实现（以 R4 为模板逐一对齐）**
- Core：`Core/Draft/` —— `DraftState`（不可变，`DraftSources`） / `DraftOptions`（上限默认取协议区） / `DraftService`（纯函数 `Normalize` / `TryParseReport` / `ExtractTags` / `Snapshot` / `Render` / `IsOverLimit` / `DescribeOverLimit` / `Verify` / `Diff`） / `DraftEntry` / `DraftStore`（**唯一真相源**：原子**整块**写；**坏文件报错**，口径与 `focus.json`「可重建⇒降级」相反） / `IDraftRegionModule`。
- Modules：`DynamicDraftModule`（只贡献 `[DRAFT]` 段；空草稿**零注入**；观察回复末尾自报 ⇒ **整块覆盖**存储区；超限 ⇒ 报告并沿用上一版，不截断不卡轮）；**不允许局部编辑入口**（无 `Remove`/`Move`/`Patch`/public setter —— 「删/改/重排」只能靠整块覆盖）。
- 配置：`RuntimeConfiguration` 新增 `dynamicDraft`（**只留 `storePath`**；出现 `maxLines`/`maxChars`/`reportHint` ⇒ 显式拒绝）+ `KnownModules` 加 `dynamic-draft`；`ModuleRegistry` 注册。
- 快照/恢复：`RuntimeSnapshot.DynamicDraft`（`Capture(..., draft)`）；`SnapshotService.VerifyDraft`（复用 `VerifyFocus`/`VerifyTail` 口径）；`--resume` 报告悬空标签 + 照读草稿。
- CLI：`--draft-show`（只读：全文/来源/轮次/余量/存储路径/写入口）/ `--draft "文本"`（人工覆盖）/ `--draft-clear` / `--draft-report on|off` + help 文本。
- **解析分节（必要修正）**：一个回复可同时带 `[TAIL]` 与 `[DRAFT]` ⇒ R4 / R5 的 `TryParseReport` 均改为「**遇到下一个块头即停**」（块头清单 = `ProtocolText.ReportBlockHeaders`；否则先解析的那块会吞掉后一块的正文 —— 见 PITFALLS #30）；对只带一块的历史回复逐字节无影响。

**测试**
- 新增 `DraftServiceTests` / `DraftStoreTests` / `DraftStreamTests` / `DraftResumeTests` / `DraftGateTests`（规格 I1~I20 每条钉一个测试名 + 存储区/配置/分节符硬性质）；
  V4.2 的旧次序测试（原 `Gate_DynamicRegionOrder_IsEnforced` 旧断言、`Gate_FocusBeforeTail_IsEnforced`）按新规范序**作废并重述**到 `DraftGateTests`。
- **221/221 全绿**（基线 199，新增 24，作废 2）；`dotnet build` **0 错 0 警**。

**未做（本轮）**：不提交 SVN（由主人做）；不跑真机 API（真机验收由主人做）。

## 0.4.0-dev — V4.2 Current Tail（当前尾部 R4，2026-09-15）

> 论文 §15 / §5.1 第 5 项：把「现在在干什么」的**工作台白板**单独立一个家（R4），持久化在 Runtime 本地。
> 规格书：`docs/DESIGN-V4.2-CURRENT-TAIL.md`（v3 定稿）；协议区规格：`docs/REPORT-PROTOCOL-ZONE.md`。
> 三阶段分别入库：**Phase A = r530**、**Phase B = r531**、**Phase C = r533**（本文一并对齐口径）。

**Phase A（r530）· 协议区 R1-P**
- 新增 `FrozenZone.Protocol`（Rank 0，排在 Rules 之前）；`Core/Protocol/ProtocolText`（英文协议文本 v2 **唯一声明处**：`[FOCUS]` / `[TAIL]` 格式与上限）+ `Modules/ProtocolModule`（内容只来自常量，类型上无别的入口）。
- 四条闸门：P1 来源只能是代码（`RuntimeConfiguration` 无协议区路径/开关；`--domains` 不作用于协议区）、P2 收尾不可改（`CloseoutService` 晋升白名单显式排除）、P3 用户不可摘（组合根强制装配 + 结果层 `EnsureProtocolPresent`）、P4 预算闸门。
- 迁移：配置项 `focus.reportHint` 删除（残留该键 ⇒ **显式拒绝**，不静默忽略）；`docs/DESIGN-OO.md` 冻结区由三区升为四区；`PITFALLS.md` #25。测试 **169 → 176**。

**Phase B（r531）· 模式重定义**
- `--bare` 新基线 = 协议区 + 本轮用户消息（「裸聊 = 与 V0 逐字节等价」旧口径作废）；新增 `--vacuum`（Core.Configuration.VacuumMode，一条 system 都没有，P3 闸门的唯一天然例外、**不是用户可选档位**）；新增 `--protocol-show` 与 `--api-key-file <path>`（只传路径，密钥不进 argv / 日志 / 配置）。
- 帮助文本把第三道闸门表述改为 `R2 → R3 → R4 → R5`；配置新增 `currentTail` 段（本 Phase 只落配置面）。测试 **176 → 179**。

**Phase C（r533）· R4 接线 + 不变式 I1~I18**
- Core：`Core/Tail/`（`CurrentTailState` 不可变 / `CurrentTailOptions`（上限默认取协议区）/ `CurrentTailService` 纯函数 / `CurrentTailStore` **唯一真相源**（原子写；**坏文件报错**，与 `focus.json` 的「可重建 ⇒ 降级」**口径相反**））+ `ITailRegionModule`。
- Modules：`CurrentTailModule`（只贡献 `[TAIL]` 段；空空板**零注入**；观察回复末尾自报 ⇒ **覆盖**存储区；超限 ⇒ 报告并沿用上一版，不截断不卡轮）。
- 闸门：`DeterminismGate.EnsureDynamicRegionOrder`（动态区规范序 **R2 → R3 → R4 → R5**，等价于「焦点之后只允许 R4/R5」）—— V4.1 的「焦点必须居末」由此**放宽**（旧名 `EnsureFocusIsLastDynamicRegion` 保留为薄包装直接转调）；违序仍**报错不纠正**。
- 配置：`KnownModules` 增 `current-tail`；`currentTail` 只留 `storePath`（出现 `maxLines`/`maxChars`/`reportHint` ⇒ 显式拒绝）。
- 快照/恢复：`RuntimeSnapshot.CurrentTail`（往返一致；旧快照缺字段 ⇒ 空默认；`SchemaVersion` **不 +1**）；`Capture(..., tail)`；`SnapshotService.VerifyTail`（复用 `VerifyFocus` 口径）；`--resume` 报告悬空标签。
- CLI：`--tail-show`（只读：白板全文 + 来源 + 轮次 + 上限余量 + 存储路径）/ `--tail "文本"`（人工纠偏）/ `--tail-clear` / `--tail-report on|off`。
- 测试：新增 **20** 条（I1~I18 每条钉一个测试名 + 2 条存储区硬性质），**199/199 全绿**（基线 179）；`dotnet build` **0 错 0 警**。
- 协议区预算（**主人 2026-09-15 22:09 按 A 定**）：**预算 ≤8 行 / ≤200 token**（行数不变、token 上限 160 → 200；**协议文本本身不动**）—— `ProtocolText.DeclaredMaxTokens` / `MaxTokens` = 200（P4 闸门：行数 ≤8 · token ≤200 · 必须 Rank 0 不变），逐字正文实测 **6 行 / 723 字符 / 181 token = 在预算内**（不再计为偏离）；`--protocol-show` 预算行、`DESIGN-OO.md` 协议区附录与 README 模块表同步。规格正文（REPORT / DESIGN-PROTOCOL-ZONE）的预算行由主人更新，本会话未动。
- 真机对照：新增尺子 `benchmark/tools/v42-tail-ab.py`（四组 × 4 轮 = 16 次调用，新种子冷启动，组间盐隔离）：**R4 每轮变 ⇒ 稳定前缀命中不降**（无 R4 组 cached 2560；每轮改白板组 2432~2688，只丢尾部自身 1~2 个 64 块）；冷启动每组 turn 1 恰 `cached=128`（协议区头 2 块）⇒ 协议区稳定带来的长期命中成立；正确率 4/4 × 4 组。真机发现：模型未按协议自报 `[TAIL]`（B 组 4/4 轮白板恒空，按 I16 沿用上一版）。

**协议文本 v3 + 自报遵从率实测（主人 2026-09-15 22:22 按 A + C 定）**
- `ProtocolText.Version` = **"3"**：第 4/5 条由「可选自报」改为**条件强制**（`when the task or todos change, add a [TAIL] block`）；实测 **6 行 / 742 字符 / 186 token**（仍在 ≤8 行 / ≤200 token 预算内），P4 与 PITFALLS #25 的钉住数字随之更新（181 → 186）。按「协议变更 = 改头部 = 缓存归零」，本次属**内核版本级**变更。
- 尺子增强：`--model <id>`（换模型 = 换变量，组间仍隔离）+ 每条记录新增 `tail_reported` 字段 + 汇总新增 **J4 · 自报遵从率**（从此「模型配不配合」是可量的数字，不靠感觉）。
- **实测（各 16 次调用）**：`deepseek-flash` 与 `deepseek-v4-pro` 的 B 组 **自报率均 0/4** —— 与 v2（optional）**同值**。根因不是模型能力：本夹具里**任务/待办根本没变**（按第 5 条省略**合规**），且探针题写「只回代号」与「末尾再加一段」**指令冲突** ⇒ **这个夹具区分不了「不遵守」与「正确省略」**，得重的不是协议字眼而是**量法**（已入 PITFALLS #29）。
- 缓存侧**再次成立**：R4 每轮变时 D 组 cached ≥ A 组（flash `[128,2432,2432,2560]` vs `[0,2560,2560,2560]`；pro `[256,256,2432,2432]` vs `[0,0,2432,2432]`），正确率仍 4/4。原始数据：`benchmark/runs/20260915-2231-v42-tail-v3-{flash,pro}/`。
- **自报遵从率（量法修正后的肯定结论，2026-09-15 23:0x）**：上面那 0/4 是**量法问题**（夹具里任务从不变 ⇒ 省略合规；探针题「只回代号」与自报段抢指令）。另建量表 `benchmark/tools/v42-tail-compliance.py`（**任务每轮真的变化** + 探针不抢指令）后，**纯协议驱动组自报率 = 4/4 = 100%**（显式要求组同），白板内容逐轮累积（当前任务/待办/遗留/暂停/已完成）—— 同一模型、同一协议，**差异全部来自夹具设计**（见 PITFALLS #29）。原始数据：`benchmark/runs/20260915-2255-v42-tail-compliance-flash/`。
- 新尺子：`benchmark/tools/v42-tail-compliance.py`（遵从率量表：`--model` 可换模型；输出 `records.jsonl` + `summary.md`，含回答原文/是否自报/存储区白板/token）。

## 0.3.0-dev — V4.1 Semantic Focus（语义焦点，2026-09-15）

> 论文 §4.5。**要解决的是注意力稀释，不是「每个 turn 省 token」**：一行 band，换「长程任务不必被迫收尾重拉」。
> 规格书：`docs/DESIGN-V4.1-SEMANTIC-FOCUS.md`（定稿 v5，逐条实现，不自行改设计）。

**Core/Stream（Tag = 身份）**
- `SessionEvent` 新增 **`Tag`**（`E001`；**身份**）；`Seq` 退回**位置**（只进 JSONL 账本，**不渲染**）；`Render()` 行首改为 Tag（`E001 [MEMORY] …`）⇒ 标签可直接被模型自报引用。
- `EventTag`（新增）：标签格式的**唯一声明处**（`Format` / `TryParseNumber` / `Compare` / `MaxNumber`；数字语义排序，非字符串）。
- `SessionAppendStream`：新身份入口 `Append`（`tag = max(既有)+1`，空流从 1 起 ⇒ 重放可算出同样标签）+ 重放/分叉专用 `AppendPreservingTag`；`ValidateInvariants` 新增「标签 session 内唯一」。
- `SessionStreamStore`（JSONL）新增 `tag` 字段；**旧文件缺该字段 ⇒ `tag = "E"+seq`**（等价迁移，不炸旧数据）。
- `SessionEventKind` 新增 **`FocusReport`**（模型自报，§四）。

**Core/Focus（新增，全纯函数）**
- `FocusState`（不可变 record：Tags + 账本；**无 setter / Remove / Insert**）、`FocusReport`、`FocusWeights`、`FocusOptions`（K=16 / **minWeight=0.95** / 半衰期=40 / band 一行封顶）、`FocusService`（`Normalize` / `Weigh` / `Resolve` / `Diff` / `Render` / `Snapshot` / `Reconcile`，**不读挂钟时间**）、`FocusCache`（`focus.json`，**只加速**）、`IFocusRegionModule`（R3 标记）。

**第三道闸门（Frozen/DeterminismGate）**
- 新增 `EnsureFocusIsLastDynamicRegion`：焦点必须在**全部动态区之后**（含 `append-stream`）；违反 ⇒ **报错，不纠正**。

**Modules**
- `FocusModule`（新增）：**不实现** `IFrozenZoneModule`；只贡献一行 band；**空焦点零注入**；不覆写 `ObserveAsync`（观察无副作用）。
- `AppendStreamModule`：`ObserveAsync` 解析回复末尾自报 ⇒ 作为 `FocusReport` 事件 **append** 进流（仅当挂了 focus：消融铁则的接线点）；解析不到 ⇒ 不报错。

**Snapshot / Cli**
- `RuntimeSnapshot.Focus` 预留位**启用**（存**事件 Tag**；`Capture` 带上当前焦点）；旧快照缺该字段走空默认，**SchemaVersion 不 +1**；新增 `SnapshotService.VerifyFocus`（悬空标签必报）；`Fork` 改为**保 Tag、重排 Seq**。
- CLI 新增 `--focus-show` / `--focus E###,…` / `--focus-clear` / `--focus-policy report|explicit`；配置新增 `focus` 段（§六 默认值）；组合根每轮写 `focus.json` 缓存并在 `--resume` 时校验悬空标签。

**测试 / 实验**
- 新增 **20 个**用例（I1~I17 逐条 + 默认值/标签格式/协议解析 3 条），**168/168 全绿**（现有 148 全绿 + 新增 20）；`dotnet build` **0 错 0 警**。
- 新增 A/B 实验尺子 `benchmark/tools/v41-focus-ab.py`（221 事件夹具、A/B 流**逐字节相同**、先写判据）。真机 A/B 已做（2026-09-15 19:03，`deepseek-flash`，阈值 0.95）：**A 6/6 = B 6/6** ⇒ 准确率**无可复现提升**，按 §十 判据落「**架构成立、收益未证**」（夹具天花板效应，待长流/更强干扰复验）；band 增量 +45 token/轮、cached 命中率持平、Release 每轮开销 < 0.2ms。数据：`benchmark/runs/20260915-1915-v41-focus-ab-real-t095/NOTES.md`。
- **收益验证移交社区（主人 2026-09-15 20:39 定）**：本机要测出 A/B 差异需要 **≥500k token 上下文**，成本高 ⇒ **不定「降级为账本-only」**，而是保留**判据 + 尺子 + 夹具**、公开邀请社区复跑并回填四项指标；方案与数据入 **`docs/EXPERIMENT-V4.1-FOCUS.md`**（版本化），见规格书 §十二。
- **默认阈值下调（主人 2026-09-15 决定）**：`minWeight` 1.0 → **0.95**（单次自报从「只撑 Δ=0」变「撑 3 个 turn」）；尺子同步改 0.95，模型 id 改用提供商清单里的 `deepseek-flash`；新增测试 `Focus_SingleReport_SurvivesThreeTurns`，合计 **169/169 全绿**。
- 离线取证：① 221 条夹具上焦点落在当前任务的两条事实（`[FOCUS] E208 E216`），三个已结束任务的标签因衰减全部退场；② Release 暖进程每轮开销 **< 0.2ms**（流放大 10× 至 2161 条，增量不变）；③ mock provider 下 A/B 各 6 轮管线跑通。

## 0.2.0-dev — V2 冻结区（进行中，逐 Phase 汇报）

> 目标：把 Cache Boundary 之上的**稳定前缀**从「一段手填字符串」升级为**结构化的冻结区**。
> 分 7 个 Phase 推进（0 数据模型 → 1 三模块骨架 → 2 指纹与闸门 → 3a/3b 语料 → 4 收尾水位线 → 5 实测扩展）。

**Phase 6 — V3 Append Stream（2026-09-14，纯离线 + 真机复验）**
- 新增 `AgentRuntime.Core.Stream`：`SessionEventKind`（封闭标签集）、`SessionEvent`（get-only record；`Render()` → `001 [KIND] 正文`）、`SessionAppendStream`（只追加：序号由流分配、`Since(cursor)` 增量读、`ValidateInvariants()` 守卫）、`SessionStreamStore`（JSONL 只追加持久化；重放期校验序号连续）。
- 新增模块 `append-stream`：流按序贡献为消息（用户/助手保持角色，其余 system）；`ObserveAsync` 把本轮 user/agent 追加进流；`AppendDocument()` = **L3 逐条加载**。与 `session` **互斥**（配置校验拦）。
- CLI 新增离线命令（不需要密钥）：`--append <file> [--kind] [--source]`、`--stream <path>`、`--stream-show [--stream-tail n]`。
- **可执行不变量**：序号 1..N 严格连续；坏文件/坏流抛错（不静默带病）；反射测试断言「没有删除/插入/重排入口」「事件无可写属性」。
- 真机实测：`--append` 两本技能 → prompt **24,569 → 29,504**；第二轮（从文件重放）`cached 29,312 / 29,542 = 99.2%`、uncached 230；Runtime 开销 6.0–6.3ms（含流渲染）。
- 测试：**133/133**（新增 15 个），0 警告。

**Phase 0 — 冻结区数据模型 + 稳定序列化 + 守卫测试（2026-09-14，纯离线）**
- 新增 `AgentRuntime.Core.Frozen`：`FrozenZone`（Knowledge/Rules/MemoryIndex）、`FrozenLayer`（Global/Expert/Project）、`KnowledgeDomain` + **17 个预设专业领域**（含 `misc` 杂项兜底）、`FrozenSection`（最小可版本单元）、`FrozenSnapshot`（规范排序 + 校验 + 稳定渲染 + 字节指纹）。
- **两条不变量入测试**：① 规范顺序唯一（Knowledge → Rules → MemoryIndex；层内 Global → Expert[域按 id 排序] → Project）；② 字节稳定（内容不变 ⇒ `PromptText` / `Id` 与输入顺序、换行风格无关）。
- **版本号不进 prompt**（版本是账本，不是内容）；换行统一归一化为 LF（跨端同内容 ⇒ 同字节）。
- 测试：新增 14 个守卫用例，全解决方案 **68/68 通过**（零网络）。

**Phase 1b — 层级修正 + 面向对象设计说明（2026-09-14，纯离线）**
- **层级**：`Rules` 为**绝对顶层**，`Knowledge` 与 `MemoryIndex` **并列**在其下；新增 `FrozenZoneTier` + `FrozenZoneTopology`（层级与序列化次序的唯一声明处）。规范序列化次序 = Rules → Knowledge → MemoryIndex。
- **OO 表达**：新增 `ContentZoneModuleBase`（并列内容层基类）；`RulesModule` 直接继承 `FrozenZoneModuleBase`，`KnowledgeModule`/`MemoryIndexModule` 继承 `ContentZoneModuleBase` —— **继承树即层级声明**。
- 新增 `docs/DESIGN-OO.md`（类图 + 层级 + 不变式表 + 扩展点），并约定**所有交付/部署必须随附**。
- 测试：83/83 通过（新增层级 / OO 层次用例）。

**Phase 1 — 三区模块骨架 + 域过滤 + 清单命令（2026-09-14，纯离线）**
- 新增模块 `rules` / `knowledge` / `memory-index`，均继承 `FrozenZoneModuleBase`（OO 硬区分；删整个 DLL 仍可裸聊）。
- Core 新增接缝：`IFrozenContentSource`（内容来源，Phase 3 换文件实现）、`FrozenSlot`、`FrozenContent`、`FrozenSelection`（域/项目选择）。
- 配置新增 `frozen.domains` / `frozen.project`；CLI 新增 `--domains` / `--list-domains` / `--list-misc`（后两者不需要密钥）。
- 领域过滤：空选择 = 全部加载；只选某域 = 其余域不进 prompt。
- 测试：81/81 通过（新增 13 个；含「去掉知识模块后规则模块贡献逐字不变」的消融不变量）。

**Phase 2 — 确定性闸门 + 全局指纹 + 版本账本（2026-09-14，纯离线）**
- `IFrozenZoneModule`（冻结区标记接口；不实现即算动态区）+ `DeterminismGate`（冻结区整体前置 + 区内部规范序；同一区重复/次序颠倒 → 报错）。
- `FrozenPrefix.Assemble`：全局指纹（各区段汇成一个整体快照）；组装点（`ModuleRegistry`）已接入闸门。
- `FrozenManifest`：版本账本 = 段→版本 + 整段指纹；JSON **逐字节确定**、可往返、可 `Diff`（新增/删除/改版），无时间戳。
- 测试：83 → 96（+13）；0 警告 0 错误。

**Phase 3a — 磁盘语料来源 + 产品最小引导语料（2026-09-14，纯离线）**
- 新增 `FileFrozenContentSource`：区/层/领域 → `frozen/` 文件映射；**版本从文件头取**（`<!-- frozen: version=N -->`，不进 prompt），无头则用正文哈希兜底；缺失文件 = 不贡献（切域省 token 的基础）；**非法标识拒绝**（防目录穿越）。
- 配置新增 `frozen.root`（相对路径按**配置文件所在目录**解析）；配置指向不存在目录 → **报错**（不静默少加载）。
- 产品语料 `frozen/`（**干净、随源码发布**）：只含「如何引导用户使用本软件」的最少必要内容。
- 测试：96 → 105；mock provider 端到端实证（3 条消息 = 铁则 + 知识 + 用户）。

**Phase 3b — 可公开的实测语料（2026-09-14，纯离线）**
- 新增 `benchmark/tools/build-public-corpus.py`：从真实资料按字符预算切片 + **去隐私**（人名/本机路径/内网地址/账号/口令/令牌/电话/网盘码/金额 → 占位符）+ **构建后自检必须 0 命中**。
- 产出 `benchmark/AgentRuntime.Benchmark/corpus/{rules,knowledge,memory}.md`（17990 字符 ≈ 9569 token，10k 档）——**可公开**，他人可复现。
- `benchmark.config.json` 默认指向仓库内 corpus/（私有全量素材仍可临时切换）；`--plan` 实叏 L2 = 18058 字符。

**Phase 4 — 收尾 / 水位线最小版（2026-09-14，纯离线）**
- 新增 `CloseoutWatermark`（Runtime 层，Session 之外：账本 + 游标 + 时刻）与 `CloseoutService`（Inspect / Perform / PendingMisc）。
- 新增 `FrozenJson`（账本类文件统一 JSON 口径）。
- CLI：`--closeout`（校验前缀 → 上报 misc → 推进水位线）；正常启动检测「未收尾」并提示（交互终端下可直接收尾）；`--list-misc` 改为读配置的冻结语料。
- 默认 `config.json` 启用三区模块（此前只有 `session` → 前缀为空）。
- **本阶段尚不做**：会话游标（待 V3 Append Stream）与「自动收敛知识库新版本」—— 收尾只记账 + 推进水位，不改写语料。
- 测试：105 → 114（+9）；0 警告 0 错误。

**Phase 5 — T07 新增 + 真实实测（2026-09-14）**
- 新增场景 **T07 冻结版本变更**：同一档位上 `v1→v1` / `v1→追加尾部` / `v1→改头部`，各 2 轮；**每变体独立 salt + 首轮冷启动自检**。
- 语料切换到仓库内**可公开** corpus/（真实资料去隐私切片）。
- **实测（61 次真实调用 / $0.021011 / 61s，`runs/20260914-211355-v2-frozen-final/`）**：
  - **T06**：L1 稳定追加稳定后命中 896 / **省 61%**；L2 命中 **9600 / 省 78.2%**；动态前置两档均 **0 命中 / 省 0%**；经 Runtime 与手工直调**一致**。
  - **T07（最有价值的新结果）**：改头部 → 命中 **0**、省 0%（成本 $0.001368 vs $0.000297 ≈ **4.6×**）；追加尾部 → 命中 **9600**、省 **78.3%**（保留）。⇒ **改哪里比改多少更重要**，支撑「追加式演进」。
  - **T04**：裸聊多轮**任务失败** ❌；+session 全过 ✅（Session 买到的是能力）。
  - **T05**：`system-rules` 每调用固定 +24 prompt token；`session` 买到能力。
  - **全套**：prompt 225,632 · 命中 104,576（46.3%）· 实际 $0.021011 vs 全未缓存 $0.032723（**省 35.8%**）· Runtime 开销 **≤1.8ms**。
- **修正自己的实验缺陷**：T07 首版三变体**共用 salt** → 变体间互相预热（首轮即命中 9472）→ 改为**每变体独立 salt** 并加冷启动自检告警。

## 0.1.0 — 2026-09-14（内核封版：V0 裸聊 + V1 模块管线）

**内核**
- V0 最小闭环：一句话进 → 组装最小 Request → OpenAI 兼容 API → 原样返回。
- V1 模块管线：`IRuntimeModule` 契约 + 配置驱动开关；**裸聊模式永远可用**（去掉全部模块 = 等价 V0）。
- 模块 `session`（多轮会话，内存态）、`system-rules`（顶端铁则/人格注入）。
- `AgentRuntime.Modules` 独立程序集：删除该 DLL，Runtime 仍可裸聊运行。
- 计时口径：`RuntimeTiming.RuntimeOverheadMs = TotalMs − ProviderCallMs`（单一定义处）。

**工程**
- 技术基线：C# 14 / .NET 10 LTS / `net10.0` / Console CLI / `System.Net.Http` / `System.Text.Json`。
- 测试：xUnit **v3**（3.2.2），54 个用例，**全离线零网络**。
- 交付：`run.sh` 启动器（`--bare` / `--modules` / `--chat`）。

**度量（尺子）**
- `benchmark/AgentRuntime.Benchmark`：独立实验系统（**Runtime 永不引用 Benchmark**）。
- 场景：T01 基线 / T02 重复请求 / T04 会话维度 / T05 模块消融 / **T06 冻结上下文阶梯**。
- 语料：真实素材（铁则 → 知识 → 记忆）按字符预算切片，**token 校准**到 1k / 10k 档。
- 实验纪律：变体隔离（salt 置**冻结前缀最前**，杜绝互相预热缓存）；原始记录落 `records.jsonl`，报告可复算。

**首轮实测结论（DeepSeek V4 Flash，2026-09-14）**
- 10k 冻结上下文：稳定前缀 + 只追加 → 输入 token 命中 **98.4%**，单轮成本**省 78.7%**（$0.000299 vs $0.001417）。
- 反例（动态内容前置）：5 轮**全部 0 命中**，成本 4.7 倍。
- Runtime 自身开销：**最大 1.3ms / 平均 0.1ms**。
