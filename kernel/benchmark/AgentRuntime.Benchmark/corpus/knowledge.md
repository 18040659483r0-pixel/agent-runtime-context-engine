# METHODOLOGY · 全平台通用项目方法论（L1/L2 顶层）

> **定位（方案 B）**：本文件 = **知识唯一家 + 顶层常驻入口 + 唯一索引入口**，只承载 **L1 泛用 / L2 中间**两层抽象。
> **L3**（技能正文 / 项目 `PITFALLS.md` / `handoff/` / 项目文档 / `TOOLS.md`）**一律不常驻**，按下方索引下钻，或用 `memory_search` / `memory_get` 定点取。
> **用法**：遇问题**第一反应 = 查本文件 → 按索引下钻**；>10 KB 的文件只读它顶部的索引节。
> **体量闸门**（2026-09-22 微型收尾立，防复发）：§十 **单条 ≤300 B** · **条数 ≤45** · 本文件 **≤25 KB** · **单次收尾净增 ≤600 B** ⇒ `python3 scripts/methodology-guard.py --check`。
> **精简留痕（2026-09-22）**：73,215 B → 本次；**旧 §十 89 条全文 + 原文件全文**在 `handoff/21-方法论条目归档-2026-09-22.md`（寻址 `归档 §十·N` / `归档 §原·<节名>`）。
> 维护：全端可编辑，改后 `BULLETIN.md` 发索引；**顶部只在收尾更新**。助手-MACARM 2026-08-29 初稿。

## 索引（唯一入口）· 要做什么 → 看哪里

> 人工导航；机器索引用 `python3 tools/frozen-build/build-frozen-corpus.py --spec`。**行预算 ≤150 B**（超了说明这行在替正文干活）。

| 要做什么 / 触发词 | 主技能 | 坑集 / 设计文档（L3，按需 read） |
|---|---|---|
| 不确定看哪个 → 先路由 | `tools/task-router/`（Win）· `memory_search`（Mac） | — |
| **日常找知识（skills 未装）** | `knowledge/knowledge.md`（L1 法则 + L2 问题地图） | 按 id 取 L3：`skill-repo/<skill>/L3.jsonl`（第 N 行 = 第 N 条） |
| **坑集 / 交接逐条定位** | `tools/skill-repo/l3-unify.py` | `projects/AgentRuntime/docs/DESIGN-L3-UNIFY.md` |
| 铁则 / 流程 / 启动 / 收尾 | `AGENTS.md`（唯一家） | — |
| 环境 / 主机 / 平台事实（谁注入什么） | — | `TOOLS.md` |
| 跨端共享 / 公告板 / SVN 三端协作 | `svn-workflow` | `BULLETIN.md` · `handoff/03` |
| 编辑文件安全（原子回滚 / 锚点 / LF / `write`=覆盖） | `agent-file-editing` | 归档 §十·67 |
| 有界面程序 DEBUG · UIA 真机截图 | `windows-uia-automation` · `screenshot-uia` | `NodeMesh/deploy/15` |
| 日志式排障（FLAG 埋点） | `log-debug` | — |
| C#/.NET WPF 桌面 | `csharp-wpf-development` | `handoff/07` |
| macOS SwiftUI App / 套壳 App | `swiftui-desktop-app` · `webview-app` | `SequenceRunner/mac/PITFALLS.md` |
| PowerShell 5.1 脚本 | `powershell-51-scripting` | `handoff/02` · `handoff/07` |
| 图标 / 上架素材 / 中文图 | `icon-forge` · `taobao-listing-assets` · `macos-chinese-image-text` | — |
| 图片压缩 / 本地 OCR | `image-compress` · `image-ocr` | MEMORY 铁则#1 |
| 授权 / 发码 / 激活 / 打包 | `license-delivery` · `license-keygen` · `dual-track-release` | `handoff/04` · AGENTS 铁则 5 |
| Windows 部署（自启 / 防火墙 / publish） | `windows-deployment` | `sales/<product>/deliverables/` |
| 增量归档 / 媒体扫描配对 / 语音存档 | `incremental-archive` · `media-scan-pairing` · `voice-archive` | — |
| **上下文成本 / 消融 / 头部精简 / 遵从率** | `llm-context-benchmark` · `context-index-maintenance` | 归档 §十·6/7/22 |
| **知识库精简 / 冻结点搬家** | `knowledge-slim-plan` · `artifact-relocation-check` | `handoff/15` |
| **一轮值不值这个钱（能效）** | `agent-turn-cost-triage` | 归档 §十·75~82 |
| **闸门「应有而缺失」/ 豁免落点** | `expected-set-gates` | 归档 §十·9/30/46/58 |
| TUI 上色 / 人读呈现 / 单栏版式 | `tui-presentation-layer` · `tui-frame-budget` | 归档 §十·34/57/60/73 · `projects/AgentRuntime/docs/DESIGN-PRESENTATION.md` |
| 意外退出 / 进程自己没了 | `unexpected-exit-triage` | 归档 §十·68 |
| 崩溃后启动失败 / 找不到文件 | `launch-failure-triage` | — |
| 两副本同步（运行时 vs 版本库） | `workspace-copy-sync` | 归档 §十·19/20/32/33 |
| 发布切片 / 公开变体 / 去人名 | `public-slice-publish` | `docs/PUBLISH-v4.0-RUNBOOK.md` · `tools/pre-publish-scan.sh` |
| 架构论文立证（GitHub） | `architecture-paper-publishing` | `analysis/` 论文稿 |
| 文档 ↔ 生成物同步 | `generated-view-sync` | `handoff/19` |
| 常驻层改动的缓存代价 | `prompt-header-change` | 归档 §十·7/79 |
| **协议区（不可变契约）** | — | `projects/AgentRuntime/docs/{REPORT,DESIGN}-PROTOCOL-ZONE.md` · 归档 §十·21/66 |
| **[WB] 宿主架构 / 生命周期卡 / 求解语言** | — | `docs/DESIGN-LIFECYCLE-UX.md` · `DESIGN-SOLVE-LANGUAGE.md` · 归档 §十·62~65 |
| **[WB] 安全网关 / 审批 / 预授权** | — | `docs/DESIGN-SECURITY-GATEWAY.md` · `DESIGN-APPROVAL-V12.md` · 归档 §十·37/42~45 |
| **[WB] 门序 G7/G8 · 宿主切换** | — | `docs/G7-DAILY-CHECKLIST.md` · `REPORT-HOST-SWITCH-PREMORTEM.md` |
| **[WB] 项目坑集（700+ 行）** | — | `projects/AgentRuntime/docs/PITFALLS.md` |
| 模型速度 / 费用 | `model-speed-benchmark` | — |
| 跨端消息 / 指令协作 | — | `handoff/13`（NodeMesh 协议） |
| **他端记忆（WIN端 · Windows 端）** | 索引 → `agents/WIN端/MEMORY-INDEX.md`（**第二区域 · append-only；不进本端冻结区**） | `agents/WIN端/MEMORY.md`（按节读） |
| **本次精简前的旧条目全文** | — | `handoff/21-方法论条目归档-2026-09-22.md` |

**仓库结构**：`software-company/{ workspace（跨端共享）, projects, sales, analysis, deploy }`。`svn update` 后**首要检查区** = `workspace/BULLETIN.md`（只读顶部 `🧭 索引` 节）。

## 〇、总纲：一个项目从 0 到交付的 7 阶段

```
立项 → 设计 → 实现 → DEBUG → 测试验收 → 交付部署 → 运维迭代
```

- 三条全局铁则：**数据安全可恢复** / **改动留痕进 SVN** / **验收必须有证据**。
- 角色分工（用户定）：**助手 = 需求/分析/营销/素材**，**WIN端 = Windows 实现**；助手不写实现代码，只写「要什么 / 为什么 / 验收标准」。

## 〇·五、Tool First 铁则：先找工具再思考（最高优先级）

- 固定顺序：① skill？② 本文件（L1/L2）？③ 小工具/分析器？④ 才进模型深推；**让模型只看精简后的几十行**。细则与「能力前置分发」→ **`AGENTS.md` 铁则 2**（唯一家）。

## 一、立项（需求阶段）

1. **方向验证先行**：查 `analysis/方向验证记录-*.md` 的验证模式。
2. **立项文档** `analysis/<产品>.md`：定位 / 目标客户 / 技术栈 / 验收标准。
3. **跑通全流程优先**：第一单用低价屠夫款跑通「产品→上架→发货→授权」，再谈利润。
- **需求三件套铁则**：**要什么 / 为什么 / 验收标准**（可测量、可截图证明）。
- 默认技术选型：Windows 桌面 = **C#/.NET + WPF**；服务端 = **Razor Pages + SQLite**；跨平台壳 = **SwiftUI/WPF + WebView2**；工具类 = **类库 + xunit**。

## 二、设计（方案阶段）

1. 多方案对比时**用户面命名避免歧义**（内部码 A/B/C，用户面 ALPHA/BETA/SIGMA）。
2. **数据安全设计先行**：删除/移动必须可恢复（回收站 + 撤销日志）；目标已存在绝不覆盖（冲突三选一）。
3. **资源安全设计**：内存敏感操作设硬上限；超大文件不做自动批量，给清单交人工。
4. **授权方案复用** `SoftwareLicense` 通用库；免费限量 `MaxFreeFiles/MaxFreeBytes` 常量化可配。
- **铁则**：设计评审必问「**数据找得回来吗**」；版本号/序列号校验位权重与模数互质、测试垃圾码含禁用字符；架构取舍给**结论 + 理由**（用户是架构师，别写论文）。

## 三、实现（编码阶段）

1. **编码统一 UTF-8**；外部对接才转码且显式标注。2. 新增文件**立即 `svn add`**（漏入库 ⇒ 别端编译不过）。3. 改核心文件后跑**命令行全 sln 编译**验证（VS Error List 可能是 Ghost 假错）。4. 每替换/每步编辑后**立即 read 核对**。

**跨平台铁则（不可越过）**
- **UI 线程永不阻塞**：耗时/阻塞操作（解码/压缩/加密/IO/网络）一律走**异步通道**；结果回 UI 用 Dispatcher/主队列；后台任务可取消 + 低优先级 QoS；响应 < 100ms。
- **后台线程绝不碰 UI 控件**：后台入口先取 UI 值为局部变量；回 UI 用 `Dispatcher.BeginInvoke`；Log 用线程安全方法。
- **UI 联动重建单入口**：同步/写值方法绝不触发重建；仅用户输入触发一次；改前查环（否则 StackOverflow）。
- **每步视图必须有 `OnEnter()` 回显快照**（只靠 IsVisibleChanged 触发的视图会永久卡初始态）。
- **序列化兼容**：旧字段不能删（XmlSerializer 遇未知属性抛）；格式重写别丢行前缀。
- **多步骤任务持久化**（长耗时/可能中断者必做）：命名任务 + JSON 快照 + 阶段状态机（含 version/阶段/设置/进度）、每步自动保存 + 关闭兜底、断点续做、恢复前校验源文件、保存失败必须提示，**绝不静默丢数据**。

**主题 / UI 标准**：必须支持**深色/浅色**（跟随系统 + 三态切换 + 记忆，**含弹窗/悬浮层/新窗口**；验收切一遍深色）；**颜色一律变量化**；高频 UI 抽两端共用组件；固定尺寸先测内容高度。

**平台细节各回其家**（本文件只留上面那些跨平台铁则）：C#/WPF → `csharp-wpf-development`｜macOS SwiftUI → `swiftui-desktop-app` / `webview-app`（libproc / `DOTNET_ROOT` / CGWindow 权限等**全在 skill 内**）｜PowerShell 5.1 → `powershell-51-scripting`｜Web 三步（`node --check` → 加调试字段 → node 模拟验证）。

## 四、DEBUG（排障阶段）

**标准流程（有界面程序）**
```
UIA 确认现状（读窗口/控件/像素，绝不先猜代码） → 分析实现逻辑 → 修改 →
命令行重编译（权威） → UIA 再验证 → 关 debug 实例
```

- **日志先行**：先加日志再复现（发送类问题先查服务端是否收到），别反复猜；crash 日志落盘。
- **编译以命令行 MSBuild 为权威**；VS Error List 大量报错多为 Ghost/IntelliSense 假错；改 csproj 后报错 ⇒ 关 VS、删 `.vs` + bin/obj、全 restore。
- **exe 被锁定 MSB3026/3027**：编译失败 ≠ 编译失败（csc 已成功），先关运行实例。
- **无读图能力**：界面核查用 UIA + 像素采样/OCR，不用 image 模型；调试标识法 = 标题栏直接显示判定字段。
- 细节 → skill `windows-uia-automation`。**macOS 不做 UIA**（要验证走后台 CLI / PTY；要看屏请用户自己看）。

## 五、测试 / 验收

- **证据等级**：`真机截图实证 > 官方后台 > 转述`；交互态（下拉/弹窗）模拟器做不出 ⇒ **真机截图**；截图验收要**像素级检测**主色占比（防黑屏/白屏废图）。
- **标准动作**：① 标配冒烟/E2E 脚本；② 测试副作用控制（用无害目标）；③ 跨端链路按验收判定表逐项核对。
- **铁则**：说「完成了」**必须有证据**（测试输出/截图/日志）；改完必须验证；部署后先刷新验证加载新版本。

## 六、交付 / 部署

- **Windows 部署** → skill `windows-deployment`（自启 / 防火墙 / publish / 进程占用）；出包前关运行中的程序与锁文件的 VS。
- **交付流程**：① 交付物入库 `sales/<product>/deliverables/`；② 售卖包传网盘（固定地址 + 提取码:<略> 发码 skill `license-delivery`（生成占用→话术→标记已发→验收，防重复）；④ 上架素材 `taobao-listing-assets`；⑤ 图标 `icon-forge`。
- **交付必附「面向对象设计说明」**（强制）：**类图 + 层级声明 + 不变式表（每条对应一个测试名）+ 扩展点** —— 目标是**让读者不看源码即可判断设计是否正确**（范例 `projects/AgentRuntime/docs/DESIGN-OO.md`）。
- **售后话术**：退款用自信承诺「不能满足你的需求，可退」。

## 七、运维 / 迭代

- **周更版本兜底**（授权 v1 定位：防君子 + 提高转卖门槛，不防专业破解）；版本号变更 ⇒ 新序列号批次；免费限量/价格走配置不改代码。
- 迭代必做：新坑**立即回写项目 `PITFALLS.md`** + 通用经验进 skill。

## 八、协作与同步（跨端，每天用）

- **SVN**（skill `svn-workflow`）：命令带证书参数 · **提交后必须 `svn update` 核对版本号** · 新增文件务必入库 · 中文路径用 ASCII 名入库 · 提交信息一律 `-F` **且显式列出路径** · 同一时间只在一台机器改同一文件。
- **公告板 `BULLETIN.md`**：`svn update` 后的**首要检查区**（只读顶部 `🧭 索引` 节）；命中自己的条目 → 确认 → 执行 → 标 ✅。
- **知识分层**：memory（素材）→ handoff（交接）→ skill（说明书）→ **本文件（L1/L2 常驻）**；**L3 细节 → 项目 `PITFALLS.md` / 设计文档（不装载）**。
- **入库两问判据**：① **含不含凭据？**（含 ⇒ **任何仓库都不行**）② **能不能给一个陌生工程师看？**（不能 ⇒ **GitHub 绝不上**）。不含凭据且属公司知识资产 ⇒ SVN 内网照常入库；**私有语料只进 SVN、绝不进 GitHub**；凭据只走环境变量/本地文件；运行产物（`bin/ obj/ runs/ dist/`）都不入库。**发布前必跑** `projects/AgentRuntime/tools/pre-publish-scan.sh <暂存目录>`。
- **NodeMesh 指令协议** → `handoff/13`；铁则——**测试指令必须由其它端发出，严禁自测**。
- **Skill 同步**：全端一套，新增/修改 → push SVN + 公告板 `📢 [Skill]`；**修改权无「本轮用过」限制**（见 归档 §十·24 · AGENTS 铁则 4）。

## 九、速查表

> 已并入本文**顶部「索引（唯一入口）」**（2026-09-14 收尾重构）。

## 十、通用工程铁则（法则骨架，45 条）

> **只留法则本体**：判据全文 / 实测数值 / 平台 L3 在 `归档 §十·N` 或下游家；**新条先过体量闸门**。
> **1 产物隔离与生命周期**：为运行服务的中间产物放系统缓存/临时区，不进用户可见区；**自产目录必须进自身扫描排除名单**（否则「产物↔源」同名互覆 ⇒ 静默错数据）。→ 归档 §十·1
> **2 产物与异步两纪律**：稳定暂停 + 原子产物（临时文件 / 逐项取消 / 原子改名，续做不重清）；异步结果写共享缓存**必须在写入点校验上下文代数**。→ 归档 §十·2/3