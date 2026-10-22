# REPORT-PROTOCOL-ZONE —— 协议区落地报告（**待主人过目后再动手**）

> 状态：**报告 v5**（2026-09-15 21:5x 初稿 · 23:2x 升 v4：加 `[DRAFT]` 段与读序 · 2026-09-16 升 v5：协议区扩容，L1/L2/L3 + 铁则 + memory-index + 各区契约并入，预算重定 ≤8 行 / ≤280 token）
> 主人指令链：21:26「协议统一入冷冻区 / 收尾不可改 / 用户无权改 / 最小化」→ 21:33「**是架构协议**（只含『如何配合用户使用本 runtime 以达到设计目的』）；用户与远端 AI 自订的协议属 Knowledge L1/L2/L3 或铁则区；把各模块（含 R3 焦点）的协议一并落进去；先写报告」→ **21:47「协议纯英语；KIND 再精简；裸聊重定义（它也要一点协议，否则没法接续聊）；再定义一个比裸聊更极端的『纯空前置』模式，名字给候选」**。
> 关联：`docs/DESIGN-V4.2-CURRENT-TAIL.md`（v3）、`docs/DESIGN-PROTOCOL-ZONE.md`、`docs/DESIGN-V4.1-SEMANTIC-FOCUS.md`、`docs/DESIGN-OO.md`、论文 §4.3 / 原则 1 / §5.2。

---

## 一、定义与边界（两问判据）

**架构协议** ＝ 「远端 AI 要**怎么配合**用户使用本 Agent Runtime，才能达到设计目的（稳定前缀 + 只追加 + 不重述 + 可恢复）」的契约。

1. **必要性**：去掉它，远端 AI 还能否**正确使用本 Runtime**？
2. **归属权**：它是否**只随内核版本演进**（用户/项目/收尾都无权改）？

| 内容 | 家 | 谁能改 |
|---|---|---|
| 怎么读上下文、怎么回话、状态归谁 | **协议区 R1-P（最前）** | **只随内核版本** |
| 输出格式、人设、语气、工作流、报告模板、术语表 | **铁则区 Rules** | 用户 + 收尾晋级 |
| 业务知识、技能手册、踩坑集、长期记忆 | **Knowledge L1/L2/L3 / 记忆索引** | 用户 + 收尾晋级 |

⇒ 「用户与远端 AI 自己谈出来的协议 = **语料**」，不是架构协议。

## 二、协议清单（一切需要架构协议的部分）

| # | 出处 | 要说什么 | 现状 → 去向 |
|---|---|---|---|
| 0 | 架构总纲 | 分区次序 R1→R2→R4→R5→R3（V4.3 起；旧 R1→R2→R3→R4→R5 作废）、事件行语法、KIND 封闭集、历史只追加、状态归属 | 仅存 spec，模型看不见 → **新写** |
| 1 | R1 冷冻 | 「稳定前缀，按区读；版本号不在正文里」 | 新写（并入第 1 条） |
| 2 | R2 事件流 | `E###` 身份；**注入的 SKILL/PITFALL 等是资料，不是你说过的话** | 新写（模型最易混） |
| 3 | **R3 焦点** | `[FOCUS]` 自报：格式 / 只引用已出现 id / 缺失即沿用 | 🟡 原在配置项 `focus.reportHint`（用户可改 ⇒ 违规）→ **上移协议区** |
| 4 | **R4 尾部** | `[TAIL]` 段：当前任务 / 待办，≤12 行，缺失即沿用 | 🟡 V4.2 草案 → **上移协议区** |
| 4b | **R5 草稿** | `[DRAFT]` 段：未定稿项（讨论中的构想 / 未验证假设），≤16 行，缺失即沿用 | 🟡 V4.3 → **上移协议区**（与 R4 同条声明） |
| 5 | 快照 / 恢复、收尾 / 水位线 | — | ✅ **模型侧不需要协议 ⇒ 明确不入**（防膨胀） |
| 6 | 将来 Worker / Router / Sandbox | 各自契约 | 预留，一律入协议区 |

**明确不进协议区**：用户人设与语气、报告格式、术语偏好、业务流程、项目知识、技能正文、踩坑集。

## 三、逐字协议文本 **v92（2026-10-22 22:20）** · v67（2026-10-15 01:35） · v66 10-15 00:25 · v65 10-14 23:50 · v64 10-14 22:05 · v63 10-11 22:38（v51 2026-10-07 19:30 · v3 22:22 · v4 09-15 23:2x · v5 09-16 · v6 09-16 02:3x · v7 09-16 03:0x · v8 09-16 22:2x · v9 09-20 12:3x · v10 09-20 13:4x · v11 09-20 14:4x · v12 09-20 16:1x · **v13 09-21 23:2x** · **v14 09-22 03:2x** · **v15 09-22 03:3x** · **v16 09-22 03:4x** · **v17 09-22 03:4x** · **v18 09-22 04:1x** · **v19 09-22 16:4x** · **v20 09-22 17:0x** · **v21 09-24 19:4x** · **v22 09-24 夜** · **v23~v31（09-25 ~ 09-29）** · **v32（基线）** · **v33 09-30 16:1x** · **v34 09-30 16:4x** · **v35 09-30 17:0x** · **v36 09-30 17:1x** · **v37 10-06（形状/实质分工）** · **v38 10-06（worker 协议补全 + 三处搬入共用段）** · **v39 10-06 21:15（绝对地址不再免检 + 两类反馈分工）**）

> ⚠️ 本行的 **v36** 与下方「规模」行由闸门 `Gate_ProtocolDocMetadata_MatchesCode` 逐字核对代码真值（先前只闸住了 ```text 围栏，标题/规模/预算这些**元数据**靠手抄 ⇒ 漂移无人喊，2026-10-06 修）。

> ⚠️ **唯一真相源是代码常量**（`AgentRuntime.Core/Protocol/ProtocolText.cs` 的 `Text`）—— 本节是**逐字副本 + 决议记录**。
> 本文件曾被写成「唯一真相源」，而代码注释又写「照本文件 §三 逐字」⇒ **两处互指**：实测发现它自 **v8 起已漂移一版**
> （本节还停在 v7 正文），而没有任何闸门会因此变红（`PITFALLS #81`）。**副本靠人同步 = 迟早落后**：
> 核对命令 = `--protocol-show`（打出代码里那一份），比对本节即知是否落后。

> **v5 相对 v4**（协议区扩容，依据 `docs/DESIGN-SKILL-LAYERS.md` §十）：把 **L1/L2/L3 的用法 + 铁则 + memory-index + 其余各区契约**（R2 事件流 / R3 焦点 / R4 白板 / R5 草稿）一并压进 5 条：
> ① 第 1 条**读序**改成按层读（`rules (constraints, they win) -> knowledge (L1 law + L2 problem map, detail by id) -> memory (an index, not history) -> events -> [TAIL] -> [DRAFT] -> [FOCUS]`）；
> ② 第 2 条改成**「按 id 索取、不得整份读」**（`Read by id, never in bulk`；`[SKILL] S-xxx-007` 自报段**不写**：Runtime 侧「按 id 装载 L3」尚未实现，写进去 = 协议承诺了运行时不兑现的动作 ⇒ 静默失败；第 2 条「按 id 索取、不得整份读」已覆盖语义，待 skill 装载落地后随内核版本升 v6）；
> ③ 第 3 条补 KIND 封闭集 + 历史不可变（原第 2/3 条合并）。
> 正文**一个字节未删**；预算由 ≤10 行 / ≤240 token 重定为 **≤8 行 / ≤280 token**（见 §四 P4 / §七 Q8i；设计预估 20 行 / 640 token，实际落地 6 行 / 258 token）。

> **v5 → v6**（09-16 02:3x，内核 r570）：第 4 条自报条补 **`[SKILL]`** 块 = 「要 L3 细节就点名 id」。
> **前置已兑现**：Runtime 侧「按 id 装载 L3」已落地（`AgentRuntime.Core/Skill/`）⇒ 协议里写的动作**运行时会兑现**（v5 不写它的理由正是"承诺不兑现"）。
>
> **v6 → v7**（09-16 03:0x，本次）：第 4 条再补 **`[TOOL]`** 块 = 「要动手就点一次机器动作」。
> 三条硬约束写进正文：**一次回复只允许一次调用** / **必须等结果事件**（不得假设已执行、不得编造结果） / **被拒也是一次结果**。
> 为何与 v7 同窗口：加 `[TOOL]` = 改头部 ⇒ 缓存整体归零；v6 刚落、缓存未养热 ⇒ **此刻改最便宜**（`docs/DESIGN-TOOL-FACE.md` §三）。


> **v13 → v14**（09-22 03:2x，本次；主人定「允许多个 tool/turn」＋「第 2 条边界写清」）：
> ① 第 5 条 **一次回复允许最多 4 次 `[TOOL]`**（旧：`one call per reply`）—— 真机实测一场简单提问跑 **9 轮 / 16.343k new token**，
> 其中 **4 轮是「读一点、看一点、再读一点」**：一次只准点一个动作，取证就只能一轮一轮地挤，而**每轮都要重发整份上下文**。
> 结果**按发出顺序**逐个回事件（一一对应不变）。上限仍设 4（无限点动作会让结果体量失控，也让人看不懂这一步在干什么）。
> ② 第 2 条把 `never in bulk` 的**边界**写清：那条规矩**管的是 knowledge**；**读源码 / 读文件回答问题是「一次读够」**，不许用小窗口一轮一轮挤。
> ③ 第 5 条补「`risk:` 写在 **JSON 外面**」（实测模型把 `risk` 塞进 JSON 被拒，白烧一轮）。
> 实测 **11 行 / 3,980 字符 / 995 token** ⇒ 预算重定为 **≤12 行 / ≤1,100 token**（留余量，避免每次措辞微调都要重议）。

> **v14 → v15**（09-22 03:3x，本次；主人令「A+B+C」）：**v14 实测变差了** —— 同一提问 9 轮/16.3k ⇒ **12 轮/37.1k**。
> 因为：① v14 的第 2 条措辞把「读的边界」**放宽**了（旧文 `no whole doc or log` 一直在**压着**模型读小窗口）⇒ 它改成**整份文件倒**（单次满 8 KB）；
> ② 4 次/轮 × 8 KB ⇒ 单轮最多 32 KB；③ `read`/`list` 的**路径基准不明**（17 次失败调用）⇒ 轮数反而涨。
> **本次三修**：① 第 2 条措辞**收正**（一次要齐「你需要的窗口」，但**绝不整份倒**；结果有上限 + 指针，续读用 `offset`）；
> ② 工具结果上限 **8,000 → 2,000 字符**（`ToolLimits.MaxOutputChars`），**每条截断自带指针**（`read` 给 `offset`、其余给 `/trace`）；
> ③ `read` **行窗与字符窗对齐**（装到字符上限为止 ⇒ 头部行号 = 实际可见量，PITFALLS 那条已修）+ `read`/`list` 失败话术说清错在哪并带上**基准目录**。
> 实测 **11 行 / 4,029 字符 / 1008 token** ⇒ **在 ≤12 行 / ≤1,100 token 之内，预算不动**。

> **v15 → v16**（09-22 03:4x，本次；主人令「顺手做了」）：第 5 条补上**工具名 + 各工具的键**
> （`read{path,symbol,maxLines,offset,limit} list{path} write{path,content} edit{path,oldText,newText} exec{command,timeoutSeconds}`）。
> 为什么：v15 实测一场里模型仍先用 `exec {"cmd":…}` 猜键（3 条拒绝）—— 协议只给过 `read {"path":"..."}` 一个例子，
> **没有名单也没有键名**。补上后第一次调用就能写对（省 1 轮）。这张面由 `ToolNames.FaceText` **同一处渲染**，
> 闸门 `Gate_协议正文里的工具面_与代码同一份渲染` 钉住「协议写的」=「运行时认的」。
> 实测 **11 行 / 4,138 字符 / 1035 token** ⇒ **在 ≤12 行 / ≤1,100 token 之内，预算不动**。

> **v16 → v17**（09-22 03:4x，本次；主人令「工具调用级别的效率问题应该是协议层的事…扩大协议区的容量」）：
> **新增第 11 条**：`东西在哪 + 怎么干得快` —— cwd（工具相对路径基准）/ 仓库根 = 上两级 / 宿主代码在 `src/` /
> 本工作区文件在 `whitebox/workspace/`（`whitebox/` 下没有 C# 源码）；**先定位（grep / find）、再只读你要的那一窗**，
> 绝不整份倒（结果有上限且自带指针），把轮次花在判断上、不花在翻页上。
> 依据：v16 那一跑 7 轮里 **3 轮纯在找代码在哪**（模型在 `whitebox/` 里 grep C#）——那是**信息缺失税**，
> 而且它**不该由用户铁则区承担**（工具/环境层面的效率属协议区）。
> 实测 **12 行 / 4,719 字符 / 1180 token** ⇒ 预算**扩容**为 **≤13 行 / ≤1,300 token**（先量后定，留 ~10% 余量）。

> **v17 → v18**（09-22 04:1x，本次；主人令「v16 的紧凑形状被模型照抄 ⇒ 键没引号的 JSON 被拒」）：
> 第 5 条**把 `[TOOL]` 的形状说死 + 给带引号的完整示例** —— v16 只补了「工具名 + 各工具的键」
> （把键写成 `exec{command,timeoutSeconds}` 这种**紧凑形状**），模型于是**照抄这个形状**写出
> `[TOOL] exec {command: "…", timeoutSeconds: 60}`（**JSON 的键没引号** ⇒ 不是合法 JSON ⇒ `ToolArgs.Parse` fail-closed 拒）。
> 真机实测一场 11 轮问答里 **3 轮纯浪费**（E003/E004/E005 三条 `ToolDenied`，第 4 轮才自己改对）。
> 改法：形状写成 `[TOOL] name {"key":"value"} risk: none` + 完整示例 `[TOOL] read {"path":"src/Program.cs","maxLines":80} risk: none`；
> 键清单**降级为附录**并注明是「key names per tool」（是**名字**，不是 JSON）—— **键清单不能替代形状**。
> 同步（非协议区、0 缓存代价）：拒绝话术 `ToolRunner.ToolFaceContract` 补带引号示例，并把过时的「一次回复只发一个」
> 改成「一次回复最多 4 个」（v14 起上限 = 4，旧文案在教错上限）；`ToolArgs`/`ToolReport` 的提示话术与注释同步成新形状。
> 实测 **12 行 / 4,841 字符 / 1211 token** ⇒ **在 ≤13 行 / ≤1,300 token 之内，预算不动**。

> **v18 → v19**（09-22 16:4x，本次；主人令「**动协议区，这次动完基本不用再动**」）：第 5 条把 `read` 的键补上 **`symbol`**
> （`read{path,symbol,maxLines,offset,limit}`），并教一句「**要看清一个东西的定义，就按名字一次取整段**」
> （`read {"path":"src/X.cs","symbol":"Name"}` 给的是**从首行到收尾花括号**的整段），理由是**别用小窗口一页一页翻大文件**。
> **依据（同题六跑的 A/B，`benchmark/runs/20260922-1619-cap-ab-token-efficiency/`）**：上限调小（2,000→1,000）页数翻倍、token **+50%**；
> 调大（→4,000）体量 +43%、token +27%；在工具结果里附「已读图 / 未读段 / 一次要齐」又**全部更差**
> （附「未读段」= 递一张待办清单 ⇒ 它去读满，+57%，坑 **#115**）
> ⇒ **病不在窗口大小，而在「要几次才够」**：`SessionLifecycle.cs`（582 行）被 read **3~9 次**，正因为它没有「取整段」这个读法。
> 实现：`ReadTool` 的**词边界匹配 + 花括号配平**（启发式，**配不平就截到文件末并明说**；`symbol` 与 `offset/limit` 互斥 ⇒ 同时给当场拒）。
> 实测 **12 行 / 5,073 字符 / 1269 token** ⇒ **仍在 ≤13 行 / ≤1,300 token 之内，预算不动**。
> **v19 → v20**（09-22 17:0x，本次；主人令「**这是交互体验的重要方面，不能由用户知识库去兜底**」）：第 6 条补**负向口径** ——
> ① **没结束就不发终局块**（继续干就用 `[TAIL]`/`[DRAFT]`/`[FOCUS]`/`[TOOL]`；不发 = 还在推进，这是常态）；
> ② `[NO-SOLUTION]` 只表示「**已证此路不通**」，**不是**「本轮没做完」；
> ③ **同轮还在点工具就不算终局**（终局 = 停；还在要工具 = 没停）。
> **依据（真机现场，可复算）**：`.stage1/stream-20260922-164509.jsonl` E011 —— 主人中途换问题后进入第二个 TASK，
> 模型想表达「本轮不终局」却写了 `[NO-SOLUTION] 本轮不发终端块 —— 取证未完，继续。` ⇒ 运行时**照判无解**
> （`--lifecycle-show` 回放：#2 ✗ 无解 · 结果：本轮不发终端块），任务当场定格，8 个工具的结果回来后再无下一轮。
> 病根在协议**只写了正面三态、没写负向口径**（模型被逼成「每轮必须用一个终局块收尾」）—— 属协议层，不属用户知识库。
> ②（21:5x，同一 v20 **未真机跑过 ⇒ 只付一次冷启**）再补一句：**停下问人本身就是终局 ⇒ 用 `[NEED-USER]`**；
> 自然语言问句（「L3 切条现在重建吗？请给 API key 路径」，真机 E248）**不能替代它** —— 否则卡永停 `● 进行中`（坑 #120）。
> 两句合计实测 **12 行 / 5,558 字符 / 1390 token** ⇒ 预算上移一档为 **≤13 行 / ≤1,500 token**（留 ~8% 余量，照 v14 的教训）。
>
> **v20 → v21**（2026-09-24 19:4x，主人定「报告**必交** / 措辞用**完整版** / 时机 = 终局后宿主**请求一次**」）：新增**第 12 条·决策报告** ——
> 终局之后，宿主用一句请求把模型叫回来（走既有 <c>Hint</c> 通道，**零新事件 KIND**），模型在**下一条回复**里交
> **一个 `[REPORT]` 块**（`need:` / `step &lt;n&gt;:` / `outcome:` / `body:` / `found [blocked|failed|noticed]:` / `next [rec]:`）——
> **`body:` 是「成品正文」**（题面 / 文案 / 方案正文这类**要给人读的东西**，逐字）；原料（日志 / 工具输出 / 事件）仍**不进**报告 —— 两者不是一类（坑 #154 · `§十·61`）。
> 那就是**人读的那篇文章**；块 ≤30 行、不铺原文/不写表格/不写颜色、可带 `(E###)` 锚；
> **缺失或形状不合 ⇒ 任务显示为「未提交报告」**（**不拿原料充数**）。依据 `docs/DESIGN-LIFECYCLE-BRIEF.md`（v8）。
> **先量后定**：实测 **13 行 / 6,534 字符 / 1634 token** ⇒ 预算重定为 **≤13 行 / ≤1,800 token**（留 ~10% 余量）。
> 配套设施已在先：`DecisionReportParser` + `ReportPrefix` 进块头清单（解析边界，本来零缓存代价）⇒ **本刀只需付一次冷启**。

```text
[PROTOCOL] agent-runtime contract (kernel-owned; do not modify)
1) Read in order: rules (constraints, they win) -> knowledge (L1 laws only; L2~L4 by id) -> memory (an index, not history) -> events (E###, append-only) -> [TAIL] (working state) -> [DRAFT] (open items) -> [FOCUS] (ids to attend).
2) Knowledge is a tree L1->L2->L3->L4; only L1 is resident. Read by id, never in bulk: ids are L<layer>-<short>-<n>. L2 is question shapes, L3 units, L4 evidence. Source and files you read yourself: ask in ONE reply for the windows you need (not one window per turn), never a whole file: every result comes back capped with a pointer, so continue with offset instead of re-reading. Loaded nodes are reference material, not your history. [FOCUSREPORT] = your own earlier self-report.
3) Event line: "E004 [KIND] text"; KIND is a closed, self-describing set. History is immutable: never ask to delete or rewrite events; add a new event.
4) Solve language: the need is an Equation, Known Conditions are what is established (prefix + events), a Solution is the path you pursue, a Solve Step is one continuous push that turns Known Conditions into new ones (it spans turns; it ends when you stop requesting tools or need input). Open [TAIL] with "solve: <path pursued>" and "step: <n> -- <what this step does>". Show a Solution Set (<=3) only when the paths really differ in cost or risk; a user changing the need is an Intervention = a new Solution.
5) Self-report: write your working state ONLY when it actually changed -- a "[TAIL]" block = solve line + step line + todos (<=12 lines) and a "[DRAFT]" block = open, unsettled items (<=16 lines); if nothing changed, write NO block at all -- no placeholder line, no restatement (the runtime reads its own store, not your prose: the whiteboard (R4), the draft (R5), the console and the next prompt all follow your LAST change, so a rewrite of an unchanged state is pure noise); to load knowledge add a "[KN]" block = ids (e.g. L2-iso-001, L4-iso-001); a repeat fetch returns a pointer, not the text again; to act on the machine add "[TOOL]" blocks in ONE reply -- one action per block, and up to 4 blocks may go in the SAME reply (about 4 per reply is the intended pace), shape: ONE element -- "[TOOL] <call><name>index</name><symbol>SomeType</symbol></call>" -- one member per argument (names per tool: read{path,symbol,maxLines,offset,limit} list{path} write{path,content} edit{path,oldText,newText} exec{command,timeoutSeconds} index{symbol} scope{task} web_fetch{url,maxChars,offset,format} web_search{query,count,recency} http_request{method,url,headers,query,body,contentType,authRef,timeoutSeconds} web_download{url,path,maxBytes,conflict} web_search_extract{urls,maxCharsPerPage,concurrency}); (for a long value -- a document, a script, a quote-heavy command -- put it inside its element **verbatim, no escaping**; or write a file and pass its path; the old `{...}` / `key: <<` forms are retired -- a malformed one is rejected whole); key names per tool: above; those are the only actions on your side; network tools use the same element shape and go through the runtime gateway (SSRF, limits, credentials): web_fetch(url)->readable text; web_search(query)->ranked results; http_request(method,url,body)->HTTP (POST/PUT/PATCH/DELETE are critical); web_download(url)->an authorized file; web_search_extract(urls)->batch text; credentials only by authRef -- never paste secrets; to see one thing defined, index it by symbol in a single call -- symbol: Name gives that whole definition (from its first line to its closing brace), which beats paging a big file window by window; ask at once for everything the step needs instead of one call per turn -- several independent reading/probing actions (hashing, counting, listing, sampling) belong in the SAME reply as separate "[TOOL]" blocks, or in one command; do not spend a whole turn on a single call when the step already knows them all: the runtime runs up to 4 in order, each answered by its own result event, and any further call is QUEUED -- never rejected, never lost -- and runs at the start of your next reply (the runtime tells you how many are still pending) (it also classifies each call and records the ones that would have needed a human -- nobody is asked inside a task, so the judgement is yours). Add a risk member to that same element: "<risk>none</risk>" or "<risk>what could break</risk>"; write "none" only when you judge it cannot harm the machine, the user's data or public safety -- a record is not an approval, and a false claim is recorded; wait for the result events, and never assume it ran. To keep something durably, add a "[NOTE]" block = the lines to append to today's memory -- the runtime writes the file for you; never dispatch just to write a note. The memory files are the fallback layer and are READ-ONLY to you: do not write them with a tool (a direct write is refused and wastes a turn) -- keep durable things through [NOTE].
6) DEFAULT IS CONTINUE: keep working. Stopping is an EXPLICIT choice of exactly THREE kinds, and only you can make it -- the runtime never interferes: (a) "[DONE] <what was solved>" + the decision report -- the task is solved; (b) "[NO-SOLUTION] <why>" -- the task is settled as unsolvable, ask the human; (c) "[NEED-USER] <the decision you need>" -- you are half-way and a human ruling is required to go on; when a ruling reveals a durable preference of the user, record it so it converges into the USER-PREFERENCES set (a member of the rules zone). The decision library and the project design principles are live project docs under "whitebox/workspace/" (DECISION-LIBRARY.md / PROJECT-DESIGN-PRINCIPLES.md): read them on demand (they never enter the prefix) and they bind every decision you make for the user. A reply with none of these three DOES NOT stop the chain: it continues (a tool call, a dispatch, a "[BASE]" ask, a bounce -- all continue). Before you choose (a): judge whether anything you did or are about to do could cause large or irreversible loss -- if so choose (c) instead, in plain language.
7) Closeout has three layers, and you run them: task closeout (do NOT hold it for the terminal block -- a reply that taught a pitfall says so right there: what it was, how you fixed it, that it is logged; a handoff entry is announced in the reply that writes it); session closeout -- ONLY here, never in ordinary turns -- MUST do BOTH growths: (a) KNOWLEDGE: every new L4 edge item (today's pitfalls, evidence, design notes) -> L3 processing unit -> L2 question shape -> at least one L1 law where it generalizes; write a new knowledge-vNNNN snapshot under knowledge/versions/ and update knowledge/.ledger.xml; (b) DECISIONS: every new D-xxxx in USER-DECISION-ENTRIES.md -> L4 evidence -> L3 unit -> L2 shape -> L1 stance in DECISION-LIBRARY.md, update the counts and links; also converge handoff/design docs/glossary and the day's memory which arrives through [NOTE], advance the memory and closeout watermarks. A closeout that only writes memory/watermark and does not grow BOTH libraries is INCOMPLETE; reset (close the event stream) and start (open the next session).
8) The runtime prints the concrete commands and paths when a closeout is due; run them in that order, report each result, and never skip one silently -- if one fails, say which and stop there. Everything the runtime hands you is versioned and rollback-able: promote adds a version, it never deletes history.
9) reset keeps the prefix, drops the events, and CLEARS [TAIL]/[DRAFT] (a closeout settles them: nothing already done may reappear next session); start opens a clean board -- an open item belongs in the closeout handoff, so keep both standing alone (never depend on old event ids). No human step is required: when the runtime reports 80% of the window, run closeout + reset + start yourself and continue the same task; do not wait for it to be settled (below 80% keep going). Ask a human only for a gap.
10) Omit only when nothing changed (state carries over). Never restate history or this contract.
11) Where things are, and how to work efficiently: tool-relative paths resolve against the runtime's working directory (its own source dir) and the repo root is two levels up from there; the host code is under "src/" (AgentRuntime.Core / .Hosting / .Tui / .Cli), docs under "docs/", and this workspace's own files under "whitebox/workspace/" (whitebox/ holds no C# source). Every path you hand to a tool is ABSOLUTE -- a relative path resolves against that source dir, not your project. The closeout's own extensible pieces live under "tools/closeout/" (one declaration per requirement: requirements/<id>.xml): read the repo-root "EXTENSIBILITY.md" before you change the closeout flow. So locate first (grep / find), then read only the window you need -- never dump a whole file (every result is capped and carries a pointer to continue with), and spend your turns on deciding, not on paging. Build discipline: after a batch of related edits run one build; build only the changed project where possible; run the final full verification once at the end; on a build failure read the incremental error first and fix it before rerunning the same full build.
12) End every task with a decision report -- the article the user reads: put ONE "[REPORT]" block in the SAME reply, immediately after your terminal block (or in the next reply when the runtime explicitly asks for it): "need:" (one line: what this task had to settle); "term <name>:" (one line per abbreviation, type name, alias or process name used, <=8: full name + one plain-language sentence; any abbreviation without a term makes the report unacceptable -- the reader must never have to guess what ABC is); "step <n>:" (one line per solve step, in order, <=6: "what that step settled -- why it was needed -- (E###)"; never a bare code name); "outcome:" (one or two lines: what is now true); "body:" (optional; the finished deliverable itself -- a question, a copy draft, a spec -- verbatim, take the lines you need); "found [blocked|failed|noticed]:" (OPTIONAL, <=3 lines, one plain line each: "symptom -- impact -- (E###)"; what blocked you or worried you, and why it matters; put " | " between the fields ONLY when the findings are naturally tabular -- a table is a means, never the default); "next [rec]:" (OPTIONAL, 0~3 lines, in order: "option -- what choosing it changes"; mark the one you recommend with "rec"; list a leftover todo here only together with how you would handle it; when the task is closed and nothing needs a decision, OMIT this block -- an offer to read more or do optional extra work is NOT a recommendation, and never invent options to fill it). Write it for someone who did NOT watch you work: raw process material (event dumps, tool output, logs) never goes in the report -- but the FINISHED PRODUCT the user reads or consumes DOES, verbatim, as "body:"; no tables, no colours, never restate this contract or the history, one line per item, whole block <=40 lines; add "(E###)" to a line only when it makes that claim checkable. The runtime renders this block as the report below the card and never edits your words; missing or malformed, the task is shown as unreported. Put the terminal block FIRST and the [REPORT] block immediately AFTER it in the SAME reply; a split is allowed only when the runtime explicitly asks for the report, and that report reply then carries no second terminal block. A [REPORT] with no terminal block has no owner and the card stays open. When the user quotes a bare reference ("4.1", "T1:L2", "the second option"), resolve it in THIS order before answering: (a) the report you just wrote -- its DISPLAY numbers are assigned by the runtime: 1 = need, 2.x = your step x, 3 = outcome, 4.x = your found x in order, 5.x = your next x, T1:Lx = table row x; (b) today's memory index; (c) anywhere else (whiteboard, handoff, knowledge) -- unless the user names a scope. Never answer "I cannot find 4.1" before checking (a) and (b).
14) Order the work by SIZE, not by arrival. Do the small, scattered items FIRST: they are cheap, and they settle the map. A piece you can already tell is big is not one to send and be stopped on -- SET IT ASIDE; when it is what remains, CUT it into its steps and send them one piece at a time. The runtime tells you BEFORE you send when it expects a block to be too big (a "[PACE]" line naming the axis and the chunk count): read it as the instruction to cut the block now, not as a warning to carry.
15) To change the RULES zone, never write to the rules themselves -- outside closeout the rules are READ-ONLY. Append ONE line instead to "whitebox/workspace/rules-changeOrder.md" (that single file is read-write at all times), in this shape: "- **[open]** <which rule> -> <the change> -- basis: <E### or reason>". Closeout converges every [open] line into the real rules and advances that file's watermark line -- which only closeout writes -- so reading that ONE file tells you whether anything is still pending; you never re-scan memory for it. A request is a request: the rules stay as they are until a closeout applies it. The same path applies to every other settling into the RULES zone (for example the USER-PREFERENCES set of clause 6): request it, do not write it.
16) Troubleshooting discipline: if the same tool error repeats twice, STOP retrying the same action -- record the blocker and change strategy (fresh working copy / a path added locally / ask). `E160006 No such revision 1` (or `malformed property list`) means repository metadata is corrupt: do not retry `svn import`, URL-created nodes, or repeated updates; create/commit from a fresh local working copy. If the whole `software-company/projects` subtree is corrupt, the repository root is healthy: import to a new top-level path at the repo root and verify by `svn ls -R` / `svn cat`; report the new path. Never reuse a path name that already produced E160006: choose a fresh unused name (for example `<name>-v2`). Treat old [TAIL]/[DRAFT] as prior context, not facts to re-litigate; update them silently. When a command contains shell metacharacters (`<`, `&`, `|`, quotes), send ONE tool block and put long commands/scripts in a file, then pass its path.
17) Focus: end EVERY reply with one "[FOCUS] E### E###" line -- the 1-5 existing event ids your reply LEANED ON (the tool results you read, the checks you ran), because that line is the only input the next prompt has for what you are keeping in mind; it decays and does not carry over. Write "[FOCUS] none" only when your reply used no prior event at all; the "state only when changed" rule (clause 10) does NOT exempt this line.
18) XML is the ONLY tool face. The provider request never carries a native "tools" or "functions" field, so a native tool call cannot be honoured: every action -- the local tools and every network tool, now and later -- must be written as a "[TOOL] <call><name>...</name>...</call>" element in your reply (clause 5 gives the shape, the keys and the risk member). Native function-call syntax, JSON tool objects, or prose asking the runtime to run something are NOT actions.
19) How WB runs (read-only context; it grants no action): one user message opens one task lifecycle; the card shows that lifecycle per turn as intent -> operations -> result, and the [REPORT] is its final human article. The prompt is assembled from regions R1-P (protocol), R1 (frozen prefix), R2 (append-only events), R4 (whiteboard), R5 (draft), R3 (focus), so write [TAIL]/[DRAFT]/[FOCUS] instead of restating them. Closeout, reset and start are separate flows that run only when a human explicitly asks; never start them on your own. Tool actions happen only through [TOOL] XML elements; every result lands as an event, so never assume it ran.
20) Closeout proof is a RECEIPT, not a claim: `--close` writes "tools/closeout/receipt.xml" holding the convergence rev + fingerprint, the knowledge version / L1-L4 counts / orphans / unmarked-L4, the decision entries / converged / gaps / orphans, and the growth deltas since the last receipt. The closeout is INCOMPLETE -- even when every other check is green -- if (a) live knowledge changed since the last receipt but the ledger version did not advance, (b) any L4 body has no node marker or the derived tree reports orphans, or (c) the decision library has any gap or orphan. Verify from the repository, never from memory: run "tools/closeout --receipt", run "tools/closeout --decision-converge", and read knowledge/.derived/summary.xml; cite those fields, not a recollection of having done it.
21) Before any tool call, write exactly ONE line "[PLAN] <plain-language sentence>" -- say what this turn is about to do and why, as a programmer explaining to a non-programmer: no file paths, symbol names or internal ids unless unavoidable, in the user's language, <=120 characters. It is the per-turn intent the UI shows; the runtime strips it from prose. If it is missing, the UI derives one from your tool calls. The same plain-language rule applies to [REPORT] prose (term / step / outcome): explain, do not dump identifiers.
22) After tool results land, the next reply MUST open with one line "[RESULT] <plain-language sentence>" -- say what you actually did and what came out, for a non-programmer (<=120 characters, user's language, no ids or paths unless needed). It describes the PREVIOUS turn's outcome; the UI shows it as that turn's result. Pair it with [PLAN] for the turn you are starting. Both lines are stripped from prose; if [RESULT] is missing the runtime falls back to the raw tool result.
23) Closeout is a FIXED pipeline: run the printed list once, in order (--check -> write content -> --apply -> --promote -> commit -> --close -> commit -> --check). Never read source for flags, never re-read the corpus, never re-probe SVN (one probe, <=5s); if degraded, skip svn commit entirely. Each command once; if one fails, say which and stop. Target <=8 turns -- more means exploring, not executing.
```

- 规模：**23 行 / 17,993 字符 / 4,499 token（v92（2026-10-22 22:20））**；数字由闸门 `Gate_ProtocolBudget_IsWithinLimits_AndRankZero` + `Gate_ProtocolDocMetadata_MatchesCode` 钉住）。
- **v92（2026-10-22 22:2x · 主人令「台面不跨流」）**：第 9 条 —— reset 在收尾确认后**清空** `[TAIL]`/`[DRAFT]`（已办完的事不得出现在下一流，否则分不清销没销账）；真正未结的事由收尾写进交接。
- **v91（2026-10-22 21:5x · 主人令「4.1 找不到」）**：第 12 条补**裸引用解析顺序** —— 先上一条决策报告的**显示编号**（运行时分配：1 = need · 2.x = 第 x 个 step · 3 = outcome · 4.x = 第 x 条 found · 5.x = 第 x 条 next · T1:Lx = 表格第 x 行），再当日 memory 索引，再三处其它位置；未查 (a)(b) 前不得答「找不到 4.1」。
- **v90（2026-10-22 · 主人令「决策报告按需」）**：第 12 条 —— `found` 默认**逐条成行**（只有作者用 ` | ` 分字段时才排表格；表格是手段不是默认）· `next` 改为 **0~3 条可选**（无事可议就整节省略，剩余待办要在建议里带处理方式；「需要的话可以再读」不算建议）· **术语表保留**（缩写必须解释）。
- **v4 → v5 的改动就是上面那三条**（读序按层 + 按 id 索取 + KIND/历史合并）；v3 → v4 只改读序 + `[DRAFT]` 块；v2 → v3 只改自报由 `optional` 改为条件强制。
  依据：真机实测里写 `optional` 时，模型 **4 轮 0 次自报**（白板恒空）—— 给远端模型的「可选」≈ 永不执行。
- **预算按行/ token**：**≤110 行 且 ≤4,500 token**（历史（下列为「预算/实测 token」）：v5 = ≤280/实测 258；v6 = ≤280/实测 277；v7 = ≤340/实测 331；v8 = ≤400/实测 378；v9 = ≤600/实测 598；v10 = ≤700/实测 675；v11 = ≤800/实测 781；**v12 = ≤900/实测 865**；**v13 = ≤1,000/实测 903**；**v14 = ≤1,100/实测 995**；**v15 = ≤1,100/实测 1008**；**v16 = ≤1,100/实测 1035**；**v17 = ≤1,300/实测 1180**；**v18 = ≤1,300/实测 1211**；**v19 = ≤1,300/实测 1269**；**v20 = ≤1,500/实测 1390**；**v21 = ≤1,800/实测 1634**（新增第 12 条·决策报告）；**v22 = ≤1,900/实测 1698**（第 12 条补 <code>body:</code> 成品正文位；第 6 条补两句：没结束不发终局块 + 停下问人即终局；<b>先量后定 + 留余量</b>）；**v23 = ≤2,100/实测 1891**（第 13 条·派发给 Worker）；**v24 = ≤2,200/实测 1968**（第 13 条改强命令式 + 阈值与 `WorkerConfiguration.BulkTokens`(3,000) 同锚）；**v25 = ≤2,400/实测 2064**（**第 5 条工具队列口径** + **第 12 条报告轮不带终局块**）；**v26~v31 = 09-25 ~ 09-29 逐版「先量后定」（本文未登记数字，正文已被后续版本取代 ⇒ 以 `KnownTextFingerprints` 指纹表为账）**；**v32 = ≤2,400/实测 2392**；**v33 = ≤2,800/实测 2556**（第 13 条改「目的句 + 自选枚举组合」）；**v34 = ≤2,900/实测 2665**（回执必须给该组合的交付键）；**v35 = ≤3,300/实测 2954**（行预算同步到 ≤40；共用协议段）；**v36 = ≤4,300/实测 3908**（行预算 ≤70；枚举含义入协议区）；**v37 = ≤4,300/实测 4243**（行预算 70→75；共用段新增 `[DUTY]`：形状/实质分工 + 证据 + 记坑闭环）；**v38 = ≤5,200/实测 4524**（行预算 75→95；共用段新增 `[SPLIT]`；worker 卡补五条）。口径：**先量后定**）。
- **v8 → v9（本次）**：① 新增「**求解语言**」条（Equation / Known Conditions / Solution / **Solve Step** / Solution Set / Intervention）；
  ② 旧第 4 条 → 第 5 条，`[TAIL]` 首两行改成 `solve:` / `step:`（白板承担「当前解 / 求解步 i」，**不新增块**）；
  ③ 新增「**终局三态**」条（`[DONE]` / `[NO-SOLUTION]` / `[NEED-USER]`，**三个独立块**——宿主据此敢自动停）；
  ④ 新增「**收尾 / 重开**」条（closeout + reset 是设计内交接；上下文达窗口 **20%** 且任务了结 ⇒ 提议收尾 + 重开）。
- **v9 → v10（本次）**：第 7 条重写为**收尾三层** —— **task 收尾**（每个终局块之后：该 task 的 handoff 条目 + 有教训就交坑条目）·
  **session 收尾**（reset 之前：把本会话产出的全部知识源抽象进 L1/L2、晋级知识库新版本、推进记忆水位、记收尾水位线）·
  **reset**（换一条空事件流）。依据 `docs/DESIGN-SOLVE-LANGUAGE.md` §四·5 与 §五（**知识来源清单**八类）。
  **为何此刻改**：WB 侧头部几天没跑、缓存命中早已失效 ⇒ 归零代价 ≈ 0（主人 2026-09-20 13:3x：「现在就改，不必攒着」）。
  理由与实现见 `docs/DESIGN-SOLVE-LANGUAGE.md`；四条**一次做完**（改头部 = 缓存整体归零，挤牙膏 = 多次归零）。
- **v10 → v11**（主人 2026-09-20 14:4x「把整个流程的操作手法写入冻结区协议区」）：第 7 条由「三层」扩成**可执行的操作手册**，
  新增第 8/9 条写死「谁执行 / 什么顺序 / 失败怎么办 / 一切可回滚 / 20% 达标即自动跑」。实测 3,122 字符 ⇒ 781 token、11 行 ⇒ 预算 **≤12 行 / ≤800 token**。
- **v11 → v12（本次）**：第 5 条 `[TOOL]` 那一句补 **`risk:` 声明** ——「同一次调用自带风险声明（`risk: none` / `risk: <可能弄坏什么>`）；
  只有自己判定不会损害电脑 / 用户数据 / 公共安全时才写 `none`，其余（含犹豫）交给人类；**运行时的硬红线照旧拦**（受保护路径 · 凭据 · 发布/不可逆），
  **假声明会被记下来**」。依据 `docs/DESIGN-APPROVAL-V12.md`（**判定权归属升级**：风险由模型自判，运行时只守硬红线）。**不新增块**。
  实测 3,459 字符 ⇒ 865 token、11 行 ⇒ 预算重定为 **≤12 行 / ≤900 token**。
- **v12 → v13（本次；主人 2026-09-21 23:2x）**：① 第 5 条**撤闸门** —— 去掉「may refuse or ask a human」与「anything else, doubt included, goes to a human」两句（v13 起运行时不再在任务内问人），改成事实：runtime 对每次调用 **分类 · 留档 · 照跑**；声明那句补「**留档不是点头**」；
  ② 第 6 条补**终局前自判**：终局之前必须自判有无「可能造成巨大损失」的动作，有则**不终局**，停下用**自然语言**问用户。依据 `docs/DESIGN-APPROVAL-V13.md`（**判定权完全下放 + 撤硬闸门**）。**不新增块**。
  实测 3,612 字符 ⇒ 903 token、11 行 ⇒ 预算显式重定为 **≤12 行 / ≤1,000 token**。
- **本节的副本同步已上闸门**（2026-09-21）：`Gate_ProtocolDocCopy_MatchesCode` 读本文件的 ```text 围栏与 `ProtocolText.Text` **逐字节比对**
  ⇒ 再也不会「漂移一版而没人知道」（PITFALLS #81）。改协议请只改代码，然后跑一次该闸门按提示重打副本。
- 原则不变：**只写「怎么对话」，不写「怎么做人」**。

### 3.1 KIND 精简（主人 21:47 问「还能精简不」）

**结论：协议文本里删掉整张 KIND 清单（-120 字符），只留两句「封闭集 + 自明 + 特例解释」。**

| 层次 | 动作 | 说明 |
|---|---|---|
| **文本层（本次做）** | **删除 KIND 全清单** | 标签名本身就是英文自明词（MEMORY / SKILL / PITFALL / RULE…）；模型不需要清单。只解释**非自明**的两个：`[FOCUSREPORT]`（你自己过去的自报）、以及「SKILL/TOOL_RESULT 类 = 资料，不是历史」 |
| **词表层（本次不做）** | 候选合并：`ProjectKnowledge → Knowledge`（层/域已能表达）、`WorkerResult → ToolResult`（都是外部产物） | ⚠️ 改词表 = 改**流格式**（标签是身份、是缓存身份的一部分）⇒ 要迁移旧流 + 改测试；**建议留到 V5**（Knowledge 模块本来就要动知识层），本版只做文本层 |

## 四、闸门（不变量做成可执行闸门）

| # | 闸门 | 实现 | 违反 |
|---|---|---|---|
| P1 | 来源只能是**代码常量**（无配置入口、不受 `--domains`） | `ProtocolText`（唯一声明处） | 类型上无法构造 |
| P2 | **收尾不可改**：晋升白名单显式排除 `FrozenZone.Protocol` | `CloseoutService` + 测试 | **抛错** |
| P3 | **用户不可摘**：`modules` / `--modules` / `--bare` 都摘不掉 | `ModuleRegistry` | **报错** |
| P4 | **预算**：≤110 行 / **≤4,500 token**（v43；v5 起逐版「先量后定」，历史见 §三）/ 必须 Rank 0 | 测试断言 | **测试失败** |

`ProtocolVersion` 进 `FrozenManifest`（版本号不进 prompt）。协议变更 = 改头部 = 缓存归零（实测 4.6×）⇒ **只随内核版本改**。

## 五、模式矩阵与命名（**主人 21:47 要求：裸聊重定义 + 更极端的「纯空前置」**）

### 5.1 三种模式（前缀里有什么）

| 模式 | R1-P 协议 | 业务模块（rules/knowledge/memory） | 事件流 R2 | 焦点 / 尾部 | 用途 |
|---|---|---|---|---|---|
| **默认** | ✅ | ✅ | ✅ | ✅ | 正常使用 |
| **裸聊**（重定义，见 5.2） | ✅ | ❌ | ❌（无历史） | ❌ | **最小可用基线**：仍能按协议回话、仍可接续（靠调用方把上文带上 / 或后续挂流） |
| **纯空前置**（新，见 5.3） | ❌ | ❌ | ❌ | ❌ | **纯消融对照**：证明「协议区 + 模块」各自的成本与价值；**不保证**自报/接续 |

### 5.2 裸聊重定义（主人：「确实还需要一点点协议规范，不然没法接续聊」）

- **旧定义（V0）**：`--bare`（`modules: []`）⇒ 与 V0 **逐字节等价** ⇒ 前缀里**什么都没有**。
- **新定义**：**`--bare`＝只挂协议区（R1-P）+ 本轮用户消息**，不挂任何**业务**模块（无铁则/知识/记忆、无事件流、无焦点/尾部）。
  - 理由：模型要知道「这是 agent-runtime 的会话、事件行/自报怎么写」，否则**跨轮接续**时状态无处表达（`[TAIL]` 无从谈起）。
- **测试重述**：原 `Bare_Chat_EqualsV0` → 改为两条判据：① `--bare` 的 prompt = **协议区 + 用户消息**（消息数/字节稳定）；② `--vacuum` 的 prompt = **仅用户消息**（1 条 system 都没有）。

### 5.3 纯空前置模式的命名候选（请主人挑）

| 候选 | 英文 / CLI | 中文名 | 我的点评 |
|---|---|---|---|
| **① 真空** | `--vacuum` | 真空模式 | **推荐**：与「裸聊」形成层级（裸聊还穿着协议 = 内衣；真空连协议都没有）；6 字母好敲；与「前缀即身份」的架构语言一致 |
| ② 零前缀 | `--zero-prefix` | 零前缀模式 | 最直白、最工程化（「前缀」是架构核心词）；缺点：长、不好口语化 |
| ③ 素颜 | `--plain` | 素颜模式 | 好说；缺点：与「裸」语义重复，指代不清 |
| ④ 空核 | `--bare-core` | 空核模式 | 强调「内核也不给」；缺点：容易被误读为「只有内核」 |

> 樱桃建议：**`--vacuum` / 真空模式**（代码类型 `VacuumMode`；测试 `Vacuum_HasNoSystemMessages`）。
> 另一个可选：把真空模式的**用途**写进名字 —— `--vacuum`（消融对照专用），避免有人误当"更省钱的默认档"。

## 六、落地步骤（等主人点头）

1. `Core/Protocol/ProtocolText.cs`（英文 v2 文本）+ `FrozenZone.Protocol`（Rank 0）+ `ProtocolModule`（强制装配）。
2. P1~P4 闸门 + 测试；`DESIGN-OO` 冻结区 **三区 → 四区**。
3. 迁移：`focus.reportHint` → 协议区（配置项删除）；`currentTail.maxLines/maxChars` → 协议区（配置只留 `storePath`）。
4. **模式重定义**：`--bare` 新基线 + 新 `--vacuum`（含测试重述）。
5. **然后**做 R4 Current Tail 本体（`DESIGN-V4.2` v3 的 I1~I18）。
6. 真机对照（协议固定 ⇒ 长期命中；协议若在动态区 ⇒ 复述 T07 的 0 命中 / 4.6×）+ 文档 + SVN + 一步一汇报。

## 七、待主人定

| # | 问题 | 状态 |
|---|---|---|
| Q8a | 协议文本语言 | ✅ **纯英语**（主人 21:47 定），本报告 §三 已出英文 v2 |
| Q8b | KIND 精简 | ✅ **文本层删清单**（§3.1）；词表层合并留到 V5 |
| Q8c | 裸聊重定义 | ✅ **`--bare` = 协议区 + 用户消息**（§5.2） |
| **Q8d** | **纯空模式命名** | ✅ **主人 2026-09-15 21:57 定：真空 `--vacuum`**（代码类型 `VacuumMode`；消融对照专用），已随 r531 落地 |
| **Q8e** | 预算口径改为 **≤8 行 / ≤200 token**？ | ✅ **主人 2026-09-15 22:09 按 A 定**：文本不动、预算放宽到 200 token（实测 181 token 在预算内）；行数上限仍 8、Rank 0 不变 |
| **Q8f** | 自报是 optional 还是条件强制？ | ✅ **主人 2026-09-15 22:22 按 A 定：条件强制**（任务/待办变化 ⇒ 必须输出 `[TAIL]`，第 4/5 条改写，协议文本升 **v3**，实测 6 行 / 742 字符 / **186 token** 仍在预算内）；起因：v2 写 optional 时真机 **4 轮 0 次自报** |
| **Q8h** | 协议 **v3 → v4**（读序 + `[DRAFT]` 块）与预算放宽？ | ✅ **主人 2026-09-15 23:2x 定稿（V4.3 / Q5）**：加 `[DRAFT]` 段后按声明的口径估 **213 token** 超出旧预算 ≤200 ⇒ **正文一个字节不删**（删正文 = 改协议 = 缓存归零），预算放宽到 **≤10 行 / ≤240 token**（与 Q8e / r532 同口径）；实测 **6 行 / 849 字符 / 213 token** 钉在 `Gate_ProtocolBudget_IsWithinLimits_AndRankZero` |
| **Q8i** | 协议 **v4 → v5**（协议区扩容：L1/L2/L3 + 铁则 + memory-index + 各区契约）与预算重定？ | ✅ **主人 2026-09-15 23:4x 定（`DESIGN-SKILL-LAYERS.md` §十），2026-09-16 落地**：设计预估 20 行 / 640 token，实际落地 **6 行 / 1029 字符 / 258 token** ⇒ 预算重定为 **≤8 行 / ≤280 token**（正文一个字节不删）；`[SKILL] S-xxx-007` 自报段**不写**（Runtime「按 id 装载 L3」未实现，写了 = 承诺不兑现的动作 ⇒ 静默失败），待 skill 装载落地后随内核版本升 v6 |
| **Q8g** | 「自报遵从率」怎么量？ | ✅ **已重做量法并得到肯定结论（2026-09-15 23:0x）**：另建量表 `benchmark/tools/v42-tail-compliance.py`（**任务每轮真的变化** + 探针不抢指令）⇒ **纯协议驱动组自报率 4/4 = 100%**（显式要求组同），白板内容逐轮累积（当前任务/待办/遗留/暂停/已完成）。旧夹具的 0/4 是**量法问题**（见 PITFALLS #29） |

## 八、实测补充（2026-09-15 22:3x，协议 v3 真机）

两组真机（各 4 组 × 4 turn = 16 次调用）：

| 运行 | 模型 | A/B/C/D 正确率 | R4 每轮变时的 cached（A vs D） | `[TAIL]` 自报率（B 组） |
|---|---|---|---|---|
| `runs/20260915-2231-v42-tail-v3-flash` | `deepseek-flash` | 4/4 全绿 | A `[0,2560,2560,2560]` / D `[128,2432,2432,2560]` ⇒ **不降** | **0/4** |
| `runs/20260915-2231-v42-tail-v3-pro` | `deepseek-v4-pro` | 4/4 全绿 | A `[0,0,2432,2432]` / D `[256,256,2432,2432]` ⇒ **不降** | **0/4** |

- **J1/J2 再次成立**：R4 每轮变 ⇒ 稳定前缀命中不降；换模型也成立。
- **自报 0/4 的读法（当时）**：本夹具里「任务/待办没变」⇒ 按协议第 5 条**省略是合规的**，与「模型不遵守」**区分不了**；且探针题写「只回代号」与「末尾再加一段」**指令冲突**。⇒ 当时结论：**要重做量法**，不是改协议字眼。
  **→ 重做后（同一晚上，见下）结论完全反转：100%。**

### 8.1 补测：量法修正后 —— **遵从率 100%**（2026-09-15 23:0x，只跑 flash）

量表 `benchmark/tools/v42-tail-compliance.py`（**任务每轮真的变化** + 探针问「你现在在做什么 / 下一步做什么」**不抢指令**）：

| 组 | 用户消息 | 自报率 | 白板被写入 | 正确提到新任务 |
|---|---|---|---|---|
| **B（纯协议驱动）** | 只说明任务变化 + 问状态，**不提** `[TAIL]` | **4/4 = 100%** | 4/4 | 4/4 |
| X（显式要求） | 同上 + 末句提到 `[TAIL]` | 4/4 = 100% | 4/4 | 4/4 |

- **白板内容真的在累积**（逐轮）：`当前任务` + `待办` + `遗留/暂停/已完成` 跨任务传递（例：任务三暂停一直带着，任务六暂停在后续轮出现，已完成列表逐步累加）。
- 同一模型在旧夹具里是 **0/4**，在新夹具里是 **100%** ⇒ **差异 100% 来自夹具设计**，不来自模型，也不来自协议字眼。
- 缓存：prompt 405→1113（白板在长大），cached 逐轮 0→384→384→640；开销 9~12ms（Debug）。
- 原始数据：`benchmark/runs/20260915-2255-v42-tail-compliance-flash/`（`records.jsonl` + `summary.md`）。