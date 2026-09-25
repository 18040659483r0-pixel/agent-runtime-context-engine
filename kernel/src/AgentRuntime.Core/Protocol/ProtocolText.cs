namespace AgentRuntime.Core.Protocol;

/// <summary>
/// **协议区（R1-P）文本的唯一声明处** —— 远端 AI 与本地 Runtime 的交互契约。
/// <para>
/// 设计依据：<c>docs/DESIGN-PROTOCOL-ZONE.md</c>（位置 / 不可改性 / 预算）、
/// <c>docs/REPORT-PROTOCOL-ZONE.md</c> §三（英文协议文本 v6，逐字）。
/// </para>
/// <para><b>三条硬性质（都有可执行闸门）</b></para>
/// <list type="number">
/// <item><b>来源只能是代码</b>：本类型是唯一声明处 —— <c>RuntimeConfiguration</c> 不暴露任何协议区路径 / 开关，
/// 也不受 <c>--domains</c> 影响（任何域下都存在、永远全量）。</item>
/// <item><b>收尾不可改</b>：<c>CloseoutService</c> 的晋升目标白名单**显式排除** <see cref="Frozen.FrozenZone.Protocol"/>（尝试写入 ⇒ 抛错）。</item>
/// <item><b>用户不可摘</b>：<c>ModuleRegistry</c> 强制装配（<c>modules</c> / <c>--modules</c> / <c>--bare</c> 都摘不掉）。</item>
/// </list>
/// <para>
/// <b>只写「怎么对话」，不写「怎么做人」</b>：人设 / 语气 / 输出格式 / 报告模板属铁则区（用户语料），
/// 业务知识属 Knowledge，长期记忆属 MemoryIndex —— 一律**不进**本区。
/// </para>
/// <para>
/// <b>协议变更 = 改头部 = 缓存整体归零</b>（实测 T07：改头部 ⇒ 命中 0、成本数倍）
/// ⇒ 协议**只随内核版本**演进（不在收尾改、不在会话中改）。
/// <see cref="Version"/> 只进 <c>FrozenManifest</c> 账本，**不进 prompt**（版本是账本，不是内容）。
/// </para>
/// </summary>
public static class ProtocolText
{
    /// <summary>
    /// 协议文本版本（进 <c>FrozenManifest</c>，**不进 prompt**）。
    /// <para>字符串 "6" = 报告 §三 的英文协议文本 **v6**（补 <c>[L3]</c> 自报块：模型按 id 点条，Runtime 按 id 装载，
    /// 依据 <c>docs/DESIGN-SKILL-LAYERS.md</c> §五；v6 的前置 = 「按 id 装载 L3」已在 Runtime 落地）。
    /// 历史："5" = v5（协议区扩容：L1/L2/L3 用法 + 铁则 + memory-index + 各区契约并入）；
    /// "4" = v4（读序加入 <c>[DRAFT]</c> 段 + 自报条补 <c>[DRAFT]</c> 块格式/上限，主人 2026-09-15 23:2x 定稿）；
    /// "3" = v3（自报由 optional 改为**条件强制**，主人 2026-09-15 22:22 按方案 A 定；起因：v2 写 optional ⇒ 真机 4 轮 0 次自报，白板恒空）；
    /// "2" = v2（自报写 optional）；"1" = 中文草案。</para>
    /// </summary>
    /// <para><b>v13 → v14 → v15（2026-09-22）</b>：① <b>v14</b> 第 5 条放开「一次回复最多 4 次 [TOOL]」、第 2 条 bulk 边界写清、risk 写进「在 JSON 外面」；② <b>v15</b> 因 v14 实测变差而**收正第 2 条措辞**：一次要齐「你需要的窗口」，但**绝不整份倒**（工具结果上限 8,000 → 2,000 字符，且每条截断自带指针：read 给 offset、其余给 /trace）。</para>
    /// <para><b>v16 → v17 → v18（2026-09-22）</b>：<b>v18（04:1x）</b> 第 5 条把 <c>[TOOL]</c> 的**形状说死**并给**带引号的完整示例** ——
    /// v16 只把「工具名 + 各工具的键」压成 <c>exec{command,timeoutSeconds}</c> 这种紧凑形状，模型**照抄这个形状**
    /// 写出 <c>{command: "…", timeoutSeconds: 60}</c>（键没引号 ⇒ 非法 JSON ⇒ fail-closed 被拒），真机实测一场 11 轮里**白烧 3 轮**。
    /// 现改成：先给形状 <c>[TOOL] name {"key":"value"} risk: none</c> + 一个完整示例
    /// <c>[TOOL] read {"path":"src/Program.cs","maxLines":80} risk: none</c>，再把键清单标注为
    /// 「key names per tool」当**附录**（是**名字**，不是 JSON）—— **键清单不能替代形状**。</para>
    /// <para><b>v18 → v19（2026-09-22 16:4x，主人令「这次动完基本不用再动」）</b>：第 5 条把 <c>read</c> 的键补上 <c>symbol</c>，
    /// 并教一句「要看清一个东西的定义，就**按名字一次取整段**（<c>read {"path":"src/X.cs","symbol":"Name"}</c>），
    /// 别用小窗口一页一页翻大文件」。依据：同题六跑的 A/B（见 <c>benchmark/runs/20260922-1619-cap-ab-token-efficiency/</c>）——
    /// 上限调小（1,000）页数翻倍、token <b>+50%</b>；调大（4,000）体量 +43%、token +27%；在结果里附
    /// 「已读图 / 未读段 / 一次要齐」又**全部更差**（附待办清单 ⇒ 它去读满）⇒ 病不在窗口大小，
    /// 而在「**要几次才够**」：<c>SessionLifecycle.cs</c>（582 行）被 read 3~9 次，正因为它没有「取整段」这个读法。</para>
    /// <para><b>v19 → v20（2026-09-22 17:0x，主人定「这是交互体验的重要方面，不能由用户知识库去兜底」）</b>：第 6 条补**两条负向/正向口径** ——
    /// ① <b>「没结束就不发终局块」</b>（继续干就用 <c>[TAIL]</c>/<c>[DRAFT]</c>/<c>[FOCUS]</c>/<c>[TOOL]</c>），
    /// 且 <c>[NO-SOLUTION]</c> 只表示**已证此路不通**、不是「本轮没做完」；同轮还在点工具就不算终局。
    /// 依据：真机现场 <c>.stage1/stream-20260922-164509.jsonl</c> E011 —— 模型想表达「本轮不终局」却写了
    /// <c>[NO-SOLUTION] 本轮不发终端块 —— 取证未完，继续。</c> ⇒ 运行时照判**无解**、任务当场定格（
    /// <c>--lifecycle-show</c> 回放：#2 ✗ 无解）。
    /// ② <b>「停下来问人本身就是终局」</b>：要决定 / 要事实 / 要资源 ⇒ 一律用 <c>[NEED-USER]</c>；
    /// 用自然语言问一句而**不发终局块** ⇒ 卡永停 <c>● 进行中</c>、人看着像卡死。
    /// 依据：真机 <c>.stage1/stream-20260922-170759.jsonl</c> E248 —— 它问「L3 切条现在重建吗？请给 API key 路径」
    /// 却无终局块 ⇒ 卡 <c>#3 ● 进行中</c> 悬住（坑 #120）。
    /// <b>两次编辑之间无真机跑过 ⇒ 只付一次冷启</b>（主人 2026-09-22 21:5x 令）。
    /// 改协议 = 缓存整体归零（实测冷启税 ≈50k token），先量后定。</para>
    public const string Version = "22";

    /// <summary>
    /// **英文协议文本 v18** —— 照 <c>docs/REPORT-PROTOCOL-ZONE.md</c> §三 **逐字**。
    /// <para>
    /// v5 → v6（r570）：第 4 条自报条补一个 <c>[L3]</c> 块 —— 「要 L3 细节就点名 id」。
    /// <b>前置已成立</b>：Runtime 侧「按 id 装载 L3」已落地
    /// （<see cref="Skill.SkillLoader"/>，走既有 append 通道进流；见 <c>docs/DESIGN-SKILL-LAYERS.md</c> §五 ③）
    /// ⇒ 协议里写的动作**运行时会兑现**，不再是「承诺不兑现」（v5 不写它的理由）。
    /// v6 → v7（r575）：第 4 条再补一个 <c>[TOOL]</c> 块 —— 「要动手就点一次机器动作」，
    /// 带三条硬约束（一次回复只允许一次 / 必须等结果事件 / 被拒也是结果）。
    /// 除第 4 条外，其余 4 条与 v5 **逐字节相同**（正文一个字节未删、未改）。
    /// </para>
    /// <para><b>v7 → v8</b>：① 第 2 条去掉 SKILL 口径（**本系统只有 L1/L2/L3**；外部 skill 靠**吸收收敛**汇入）；
    /// ② 第 4 条块名 <c>[SKILL]</c> → <c>[L3]</c>（块头常量 <see cref="L3Prefix"/>）；
    /// ③ 第 4 条补「**怎么请求能力**」：<c>[TOOL]</c> 一次一个，runtime 会分级（可拒 / 可问人），被拒也是结果。</para>
    /// <para><b>v8 → v9（本次；主人 2026-09-20 12:3x 定：命名 <c>Solve Step</c> · 终局用**独立块** · 预算 ≤10 行 / ≤600 token）</b>
    /// —— 接入**求解语言**与**会话生命周期**，一次做完（改前缀 = 缓存整体归零，挤两次牙膏 = 归零两次）：
    /// ① 新增第 4 条「求解语言」：Equation / Known Conditions / Solution / <b>Solve Step（求解步）</b> / Solution Set / Intervention
    /// （<c>docs/DESIGN-SOLVE-LANGUAGE.md</c>；**协议与 UI 永远写全 "Solve Step"，绝不单写 "Step"** —— 与 <c>Turn</c> 同一手法，防与「步骤」混淆）；
    /// ② 旧第 4 条 → 第 5 条：<c>[TAIL]</c> 口径改为「**首两行 = solve 行 + step 行**，其后才是待办」
    /// （白板 R4 承担「当前解 / 求解步 i」，**不新增块**，见 <see cref="Tail.CurrentTailService.SolveHeader"/>）；
    /// ③ 新增第 6 条**终局三态** <c>[DONE]</c> / <c>[NO-SOLUTION]</c> / <c>[NEED-USER]</c> —— **三个独立块**，宿主据此**敢自动停**
    /// （此前 auto-continue 的停判据只有「模型没点工具」，把完成 / 卡住 / 等人 / 无解混成一件；见 <see cref="TerminalReport"/>）；
    /// ④ 新增第 7 条**收尾 / 重开**：closeout（收敛已定 + 记水位线）与 reset（新空事件流）是**设计内的交接，不是失败**；
    /// 稳定前缀与 <c>[TAIL]</c>/<c>[DRAFT]</c> **继承**、事件**不继承** ⇒ 白板草稿必须**自持**（不得依赖旧事件 id）；
    /// 上下文达窗口 <b>20%</b> 且任务已终局 ⇒ 应**提议**收尾 + 重开（阈值唯一声明处 = <see cref="ContextBudget.ProposeRatio"/>）。
    /// 实测 **9 行 / 2,390 字符 / 598 token** ⇒ **先量后定**：行 ≤10、token ≤600。</para>
    /// <para><b>v9 → v10（本次；主人 2026-09-20 13:3x 定「现在就改，不必攒着」）</b>：补**收尾三层**（第 7 条重写，第 8 条拆出 reset 语义）——
    /// ① <b>task 收尾</b>（每个终局块之后）：交该 task 的 handoff 条目（+ 有教训就交坑条目）；
    /// ② <b>session 收尾</b>（reset 之前）：把本会话产出的**全部知识源**（踩坑集 · 记忆 · handoff · 设计文档 · 词表 · 技能）
    /// **抽象进 L1/L2**、**晋级知识库新版本**、**推进记忆水位**、记收尾水位线；
    /// ③ <b>reset</b>：换一条空事件流（前缀与白板/草稿继承、事件不继承）。
    /// 依据：`docs/DESIGN-SOLVE-LANGUAGE.md` §四·五（含**知识来源清单** —— 只写「踩坑集 + 记忆」会漏掉四类源）。
    /// 实测 **10 行 / 2,699 字符 / 675 token** ⇒ **先量后定**：行 ≤12、token ≤700。
    /// 为何此刻改：WB 侧头部**几天没跑、缓存命中早已失效** ⇒ 归零代价 ≈ 0（无需攒窗口）。</para>
    /// <para><b>v10 → v11（本次；主人 2026-09-20 14:4x 定「把整个流程的操作手法写入冻结区协议区」）</b>：
    /// 第 7 条由「三层」扩成**可执行的操作手册**，并新增第 8/9 条把「谁执行、按什么顺序、失败怎么办」写死 ——
    /// 目标是**远端 AI 自己把收尾/重开跑完，不需要人类介入**：
    /// ① 三层各自交什么（task：handoff + 坑；session：抽象 L1/L2 + 晋级 + 推水位 + 记收尾水位线；reset/start）；
    /// ② **命令与路径由运行时在收尾到期时打印**（配置声明，不写死在协议里），AI 按序执行、逐个报结果、**失败即停并说清哪一步**；
    /// ③ 安全栏写进协议：**晋级只加版本、从不删历史 ⇒ 一切可回滚**（`--rollback`）；
    /// ④ 20% 阈值不再只是「提议」：**任务了结 + 运行时报达标 ⇒ 由 AI 自己跑 closeout + reset + start**。
    /// 实测 **11 行 / 3,122 字符 / 781 token** ⇒ **先量后定**：行 ≤12、token ≤800。</para>
    /// <para><b>v11 → v12（本次；主人 2026-09-20 16:1x 定「权限判定权下放给远端 AI 自判」）</b>：第 5 条 <c>[TOOL]</c> 那一句补
    /// <b><c>risk:</c> 声明</b> —— 「同一次调用自带风险声明：<c>risk: none</c> 或 <c>risk: &lt;可能弄坏什么&gt;</c>；
    /// 只有**自己判定**不会损害电脑 / 用户数据 / 公共安全时才写 none，其余（含犹豫）交给人类；
    /// **运行时的硬红线照旧拦**（受保护路径 · 凭据 · 发布/不可逆），**假声明会被记下来**」。
    /// 依据 <c>docs/DESIGN-APPROVAL-V12.md</c>（判定权归属升级；<c>[TOOL]</c> 仍是唯一块，**不新增块**）。
    /// 实测 **11 行 / 3,459 字符 / 865 token** ⇒ **先量后定**：行 ≤12、token ≤900。</para>
    /// <para><b>v12 → v13（本次；主人 2026-09-21 23:2x 定「放弃 runtime 的硬闸门，纯用纪律约束远端 AI」＋「凭据和私钥暂时也放行，不要有任何硬约束」）</b>：
    /// ① 第 5 条【<b>撤闸门</b>】去掉两句**已不成立**的话（"may refuse or ask a human" / "anything else, doubt included, goes to a human"），
    /// 换成事实：「runtime 对每次调用**分类、留档，然后照跑**；任务内部**不问人**，判断归模型」；声明那句补
    /// 「**留档不是点头**」；② 第 6 条【<b>终局前自判</b>】新增一句：终局之前必须自判有没有「可能造成巨大损失」的动作，
    /// 有则**不要终局**，停下用**自然语言**问用户要不要做。依据 <c>docs/DESIGN-APPROVAL-V13.md</c>。
    /// 实测 **11 行 / 3,612 字符 / 903 token** ⇒ **先量后定**：预算显式重定为 **≤12 行 / ≤1,000 token**。</para>
    /// <para><b>v18（本次；主人 2026-09-22 04:0x 令）</b>：第 5 条**把形状说死 + 给带引号的完整示例** ——
    /// v16 补的「工具名 + 各工具的键」只教了 <c>exec{command,timeoutSeconds}</c> 这一**紧凑形状**，模型于是照抄它写成
    /// <c>[TOOL] exec {command: "…", timeoutSeconds: 60}</c>（**JSON 的键没引号** ⇒ 不是合法 JSON ⇒ <c>ToolArgs.Parse</c> 拒，fail-closed）——
    /// 真机实测一场 11 轮问答里 **3 轮纯浪费**（E003/E004/E005 三条 <c>ToolDenied</c>，第 4 轮才自己改对）。
    /// 改法（**键清单只能当附录，不能替代形状**）：先给形状 <c>[TOOL] name {"key":"value"} risk: none</c>（键带引号）+
    /// 完整示例 <c>[TOOL] read {"path":"src/Program.cs","maxLines":80} risk: none</c>，键清单降级为「**key names per tool**」（说明它是**名字**、不是 JSON）。
    /// 实测 **12 行 / 4,841 字符 / 1,211 token** ⇒ **仍在 ≤13 行 / ≤1,300 token 之内，预算不动**（v15/v16 同例）。</para>
    /// <para><b>v20 → v21（2026-09-24 19:4x，主人定「报告必交 / 完整版 / 终局后下轮交」）</b>：新增**第 12 条·决策报告** ——
    /// 终局后宿主**请求一次**，模型用**一个 <c>[REPORT]</c> 块**交「人读的那篇文章」（need / step n / outcome / found[kind] / next[rec]），
    /// 块 ≤30 行、不铺原文/不写表格/不写颜色、可带 <c>(E###)</c> 锚；**缺失或形状不合 ⇒ 任务显示为「未提交报告」**（不拿原料充数）。
    /// 依据 <c>docs/DESIGN-LIFECYCLE-BRIEF.md</c>（v8）。**先量后定**：实测 13 行 / 6,534 字符 / **≈1,634 token** ⇒ 预算重定为 **≤13 行 / ≤1,800 token**（留 ~10% 余量）。
    /// 配套设施已在先：<c>DecisionReportParser</c>（r914）· <c>ReportPrefix</c> 进块头清单（解析边界，零缓存代价）。</para>
    /// <para><b>v21 → v22（2026-09-24 夜，主人令「协议区做净」）</b>：第 12 条补 <c>body:</c> <b>成品正文位</b> ——
    /// <b>成品</b>（题面 / 文案 / 方案正文这类要给人读的东西）逐字进报告；<b>原料</b>（日志 / 工具输出 / 事件）仍**不进**，
    /// 两者不是一类。依据坑 #154 / <c>§十·61</c>（「不铺原料」的口径缺「成品」一类 ⇒ 成品与原料一起被拍死）。
    /// <b>先量后定</b>：实测 13 行 / 6,792 字符 / ≈1,698 token ⇒ 预算 1,800 → <b>1,900</b>（留 ~10% 余量）。
    /// ⚠️ r958 只改了正文、**没升版本号**（提交信息/注释/水位都写 v22，常量仍是 "21"）⇒ 已加闸门
    /// <c>Gate_Protocol_VersionTracksTextBytes</c> 钉住「正文指纹 × 版本号」这一对，防同型复发。</para>
    /// <para>⚠️ 改动本常量 = 改协议 = 缓存整体归零 ⇒ 只在**内核版本**里改。</para>
    /// </summary>
    public const string Text =
        """
        [PROTOCOL] agent-runtime contract (kernel-owned; do not modify)
        1) Read in order: rules (constraints, they win) -> knowledge (L1 law + L2 problem map, detail by id) -> memory (an index, not history) -> events (E###, append-only) -> [TAIL] (working state) -> [DRAFT] (open items) -> [FOCUS] (ids to attend).
        2) Knowledge is L1/L2/L3 only; read by id, never in bulk (no whole doc or log) -- that rule is about knowledge. For source and files ask in ONE reply for the windows you need (not one window per turn), and never dump a whole file: every result comes back capped with a pointer, so continue with offset instead of re-reading. Loaded L3 strips are reference material, not your history. [FOCUSREPORT] = your own earlier self-report.
        3) Event line: "E004 [KIND] text"; KIND is a closed, self-describing set. History is immutable: never ask to delete or rewrite events; add a new event.
        4) Solve language: the need is an Equation, Known Conditions are what is established (prefix + events), a Solution is the path you pursue, a Solve Step is one continuous push that turns Known Conditions into new ones (it spans turns; it ends when you stop requesting tools or need input). Open [TAIL] with "solve: <path pursued>" and "step: <n> -- <what this step does>". Show a Solution Set (<=3) only when the paths really differ in cost or risk; a user changing the need is an Intervention = a new Solution.
        5) Self-report: end replies with "[FOCUS] E### E###" (existing ids only); when the task, todos or drafts change, add a "[TAIL]" block = solve line + step line + todos (<=12 lines) and/or a "[DRAFT]" block = open, unsettled items (<=16 lines); to load L3 detail add a "[L3]" block = the ids to load (e.g. S-xxx-007); to act on the machine add up to 4 "[TOOL]" blocks in ONE reply -- one action each, shape: [TOOL] name {"key":"value"} risk: none -- keys are quoted JSON, e.g. [TOOL] read {"path":"src/Program.cs","maxLines":80} risk: none; key names per tool: read{path,symbol,maxLines,offset,limit} list{path} write{path,content} edit{path,oldText,newText} exec{command,timeoutSeconds}; to see one thing defined, read it by symbol in a single call -- read {"path":"src/X.cs","symbol":"Name"} gives that whole definition (from its first line to its closing brace), which beats paging a big file window by window; ask at once for everything the step needs instead of one call per turn, the runtime runs them in order and answers each with its own result event (it also classifies each call and records the ones that would have needed a human -- nobody is asked inside a task, so the judgement is yours). Add a risk claim to that same line: "risk: none|<what could break>" (outside the braces); write "none" only when you judge it cannot harm the machine, the user's data or public safety -- a record is not an approval, and a false claim is recorded; wait for the result events, and never assume it ran.
        6) End a task with exactly one terminal block: "[DONE] <what was solved>"; "[NO-SOLUTION] <why>"; "[NEED-USER] <what you need>". A task that is NOT ending carries no terminal block at all -- keep working, and use the [TAIL]/[DRAFT]/[FOCUS]/[TOOL] blocks to continue; "[NO-SOLUTION]" means you proved the path impossible, never "not finished this round"; a reply that still asks for tools has not ended. Stopping to ask the user something (a decision, a fact, a resource) is itself an ending: end with "[NEED-USER] <what you need>"; a question in plain prose leaves the task hanging (nobody knows it is your turn). Before you end it, judge whether anything you did or are about to do could cause large or irreversible loss: if so, do not end it -- stop and ask the user, in plain language, whether to do it. Then the runtime stops and waits.
        7) Closeout has three layers, and you run them: task closeout (after every terminal block: write that task's handoff entry, plus a pitfall entry if it taught one); session closeout (converge everything the session produced -- pitfalls, memory, handoff, design docs, glossary, skills -- abstract it into L1/L2, promote the knowledge base to a new version, advance the memory watermark, then record the closeout watermark); reset (close the event stream) and start (open the next session).
        8) The runtime prints the concrete commands and paths when a closeout is due; run them in that order, report each result, and never skip one silently -- if one fails, say which and stop there. Everything the runtime hands you is versioned and rollback-able: promote adds a version, it never deletes history.
        9) reset keeps the prefix and your [TAIL]/[DRAFT] and drops the events; start loads [TAIL]/[DRAFT] from the closeout handover -- so keep both standing alone (never depend on old event ids). No human step is required: when the runtime reports the context reached 20% of the window and your task is done, run closeout + reset + start yourself.
        10) Omit only when nothing changed (state carries over). Never restate history or this contract.
        11) Where things are, and how to work efficiently: tool-relative paths resolve against the runtime's working directory (its own source dir) and the repo root is two levels up from there; the host code is under "src/" (AgentRuntime.Core / .Hosting / .Tui / .Cli), docs under "docs/", and this workspace's own files under "whitebox/workspace/" (whitebox/ holds no C# source). So locate first (grep / find), then read only the window you need -- never dump a whole file (every result is capped and carries a pointer to continue with), and spend your turns on deciding, not on paging.
        12) End every task with a decision report -- the article the user reads. The runtime asks for it after your terminal block, so put it in that next reply as ONE "[REPORT]" block: "need:" (one line: what this task had to settle); "step <n>:" (one line per solve step, in order, <=6: what that step settled -- not which tool you ran); "outcome:" (one or two lines: what is now true); "body:" (optional; the finished deliverable itself -- a question, a copy draft, a spec -- verbatim, take the lines you need); "found [blocked|failed|noticed]:" (<=3 lines: what blocked you or worried you); "next [rec]:" (<=3 lines, in order: what you recommend the user picks next -- mark the one you recommend with "rec"). Write it for someone who did NOT watch you work: raw process material (event dumps, tool output, logs) never goes in the report -- but the FINISHED PRODUCT the user reads or consumes DOES, verbatim, as "body:"; no tables, no colours, never restate this contract or the history, one line per item, whole block <=30 lines; add "(E###)" to a line only when it makes that claim checkable. The runtime renders this block as the report below the card and never edits your words; missing or malformed, the task is shown as unreported.
        """;

    /// <summary>
    /// 往回复末尾找**自报块头**时最多回看的行数（**唯一声明处**）。
    /// <para>
    /// 为什么需要窗口：块与块会**互相遮挡** —— 模型常把 <c>[L3]</c> 放在 <c>[TAIL]</c> 之前
    /// ⇒ 窗口必须覆盖「一次回复里所有自报块的总行数」。
    /// <b>实测（2026-09-16 18:4x，真机）</b>：窗口 = 5 行时，<c>[L3]</c> 被一个 6 行的 <c>[TAIL]</c>
    /// 压到窗口外 ⇒ **静默不装载**（兑现率 1/3；见 <c>benchmark/tools/v6-skill-compliance.py</c>）。
    /// </para>
    /// <para>
    /// 历史：四个块各自写过一份常数（<c>[TAIL]</c>/<c>[DRAFT]</c> = 64、<c>[L3]</c>/<c>[FOCUS]</c> = 5）
    /// ⇒ 这正是「口径分裂」的标本，现统一到本常量。
    /// </para>
    /// </summary>
    public const int ReportScanLines =
        ReportMaxLines + TailMaxLines + DraftMaxLines + ToolMaxCallsPerReply + ReportScanMargin;

    /// <summary>
    /// 窗口**余量**（焦点 / L3 / 同族块 + 安全边际）—— 与各段上限相加即 <see cref="ReportScanLines"/>。
    /// <para>
    /// <b>P2（2026-09-24）—— 窗口改成「各段上限之和」而不是拍一个数</b>：
    /// 新增 <c>[REPORT]</c>（最长 <see cref="ReportMaxLines"/> 行）后，旧的 64 一旦报告上线就会被压爆 ——
    /// 而**压爆的形状就是静默不装载**（先例：窗口 5 行时 <c>[L3]</c> 被 6 行 <c>[TAIL]</c> 压出 ⇒ 静默丢失，2026-09-16 实测）。
    /// 用常量表达式写死之后，任一段上限一改，窗口**自动跟着变**（不会再静默落后）。
    /// </para>
    /// </summary>
    public const int ReportScanMargin = 16;

    /// <summary>按行拆开（协议区文本的**行**定义；测试据它做行数闸门）。</summary>
    public static IReadOnlyList<string> Lines { get; } = Text.Split('\n');

    /// <summary>字符数（含空白；token 估算的输入）。</summary>
    public static int CharCount { get; } = Text.Length;

    /// <summary>**规格声明的行数预算**（<c>docs/REPORT-PROTOCOL-ZONE.md</c> §四 P4 / §七 Q8i）：≤ 8 行。
    /// <para><b>v17 起</b>为 **≤13 行**（v17 增第 11 条后 12 行；v18 仍 12 行 ⇒ 不动）。</para></summary>
    public const int DeclaredMaxLines = 13;

    /// <summary>
    /// **规格声明的预算**（<c>docs/REPORT-PROTOCOL-ZONE.md</c> §四 P4 / §七 Q8i）：≤ 8 行 且 ≤ 280 token。
    /// <para>
    /// 主人 2026-09-15 23:4x **定（协议区扩容 / <c>docs/DESIGN-SKILL-LAYERS.md</c> §十）**：设计预估 20 行 / 640 token，
    /// 实际落地 **6 行 / ≤280 token**（各区契约压进第 1/2 条即可覆盖，符合「最小化」）⇒ 预算重定为
    /// **≤8 行 / ≤280 token**（正文一个字节不删；实测 6 行 / 1029 字符 / 258 token）。
    /// </para>
    /// <para>
    /// <b>v6（本次）先重算、再定预算</b>：补 <c>[L3]</c> 块后正文实测 **6 行 / 1106 字符 / 277 token**
    /// ⇒ **仍在 ≤8 行 / ≤280 token 之内，预算无需放宽**（口径仍是 <see cref="CharsPerToken"/>）。
    /// </para>
    /// </summary>
    /// <para><b>v19</b>：第 5 条 <c>read</c> 补 <c>symbol</c>（按名字取整段）后实测 <b>1269 token / 12 行</b> ⇒ **仍在 ≤13 行 / ≤1,300 token 之内，预算不动**。</para>
    /// <para><b>v20</b>：第 6 条补**两句**（没结束不发终局块 + [NO-SOLUTION] 的负向口径 + 还在点工具不算终局；
    /// 以及「**停下来问人本身就是终局 ⇒ 用 [NEED-USER]**」）后实测见下方记录 ⇒ 预算按「先量后定」上移一档（行数仍 12 ⇒ 行预算不动）。</para>
    public const int DeclaredMaxTokens = 1900;

    /// <summary>
    /// **可执行闸门的 token 上限**（同 <see cref="DeclaredMaxTokens"/>；闸门不得比声明更紧）。
    /// <para>实测值与声明值双双钉在 <c>Gate_ProtocolBudget_IsWithinLimits_AndRankZero</c>。</para>
    /// <para><b>v12</b>：正文补 <c>[TOOL] risk:</c> 声明（判定权归属升级）后实测 <b>865 token</b> ⇒ 预算重定为 **≤12 行 / ≤900 token**。</para>
    /// <para><b>v13</b>：撤闸门 + 终局前自判后实测 <b>903 token</b> ⇒ 预算重定为 **≤12 行 / ≤1,000 token**（原因写明：又一次新契约，且主人明确“可无视缓存代价”）。</para>
    /// <para><b>v14</b>：第 5 条放开「一次回复最多 4 次 [TOOL]」+ 第 2 条把 bulk 边界写清 + 第 5 条补「risk 在 JSON 外」后
    /// 实测 <b>995 token</b> ⇒ 预算重定为 **≤12 行 / ≤1,100 token**（留 ~10% 余量：v13 的 995/1,000 只剩 5 token，
    /// 一次措辞微调就要重议预算）。</para>
    /// <para><b>v15</b>：v14 放开后**实测变差**（同一提问 9 轮/16.3k → <b>12 轮/37.1k</b>：读的边界被放宽 + 结果不限量 + 路径基准不明）
    /// ⇒ 第 2 条措辞收正（一次要齐「你需要的窗口」，**绝不整份倒**；结果有上限 + 指针，续读用 offset），实测 <b>1008 token</b> ⇒ **仍在 ≤1,100 之内，预算不动**。</para>
    /// <para><b>v16 / v17</b>：v16 第 5 条补「工具名 + 各工具的键」（模型不再猜 <c>cmd</c>/<c>bash</c>）；v17 新增第 11 条
    /// **「东西在哪 + 怎么干得快」**（cwd / 仓库根 / <c>src/</c> / <c>whitebox/workspace/</c> + 先定位后读窗口）。
    /// 依据：主人 2026-09-22 03:45 定「**工具调用级别的效率问题属协议层，不属用户铁则区；扩大协议区容量**」——
    /// 实测 v16 那一跑 7 轮里 **3 轮纯在找代码在哪**（模型在 <c>whitebox/</c> 里 grep C#）。
    /// 实测 <b>1180 token / 12 行</b> ⇒ 预算按「先量后定」扩容为 **≤13 行 / ≤1,300 token**（留 ~10% 余量）。</para>
    /// <para><b>v18</b>：第 5 条把 <c>[TOOL]</c> 形状说死 + 给带引号完整示例（v16 的键清单只当附录）后
    /// 实测 <b>1211 token / 12 行</b> ⇒ **仍在 ≤13 行 / ≤1,300 token 之内，预算不动**（先量后定）。</para>
    /// </summary>
    /// <para><b>v19</b>：见上（1269 token ⇒ 预算不动）。</para>
    /// <para><b>v20</b>：第 6 条补负向口径（没结束不发终局块 / <c>[NO-SOLUTION]</c> 只表示已证此路不通 / 还在点工具不算终局 / **停下来问人本身就是终局 ⇒ <c>[NEED-USER]</c>**）后
    /// 实测 **1390 token / 12 行** ⇒ 预算随之重定为 **≤13 行 / ≤1,500 token**：**留 ~8% 余量**（照 v14 的教训 ——
    /// v13 的 995/1,000 只剩 5 token，一次措辞微调就要重议预算）。依据：真机假终局块 + 「问人却没发终局块」两次现场，主人 2026-09-22 17:0x/21:5x 令。</para>
    public const int MaxTokens = 1900;

    /// <summary>英文 token 估算口径：~4 字符/token（规格 §三 §3.1 声明的口径）。</summary>
    public const double CharsPerToken = 4.0;

    /// <summary>按 ~4 字符/token 估的 token 数（向上取整）。</summary>
    public static int EstimatedTokens { get; } = (int)Math.Ceiling(CharCount / CharsPerToken);

    // ---------------- 与协议文本同处声明的段上限（配置里不再出现） ----------------

    /// <summary><c>[FOCUS]</c> 段前缀（与 <c>[TAIL]</c> / <c>[DRAFT]</c> 同族的自报块头）。</summary>
    public const string FocusPrefix = "[FOCUS]";

    /// <summary><c>[TAIL]</c> 段前缀（与 <c>[FOCUS]</c> / <c>[RULE]</c> / <c>[KNOWLEDGE]</c> 同族）。</summary>
    public const string TailPrefix = "[TAIL]";

    /// <summary><c>[TAIL]</c> 段行数上限（= 协议第 4 条里的 <c>&lt;=12 lines</c>；唯一声明处）。</summary>
    public const int TailMaxLines = 12;

    /// <summary>
    /// <c>[TOOL]</c> 一次回复的**调用次数上限**（= 协议第 5 条里的 <c>up to 4 calls per reply</c>；唯一声明处）。
    /// <para><b>v14（本次；主人 2026-09-22 03:2x 定「允许多个 tool/turn」）</b>：旧口径是
    /// <c>one call per reply</c>，后果是真机实测的一场简单提问跑 9 轮，其中 **4 轮是「读一点、看一点、再读一点」**
    /// —— 一次只准点一个动作，取证就只能一轮一轮地挤，而每轮都要重发整份上下文。
    /// 上限仍设：一次回复无限点动作会让结果体量失控，也让人看不懂这一步在干什么。</para>
    /// </summary>
    public const int ToolMaxCallsPerReply = 4;

    /// <summary><c>[TAIL]</c> 段字符上限（协议区声明，避免「上限与协议文本两处分裂」）。</summary>
    public const int TailMaxChars = 2000;

    /// <summary><c>[DRAFT]</c> 段前缀（V4.3 · R5；与 <c>[TAIL]</c> 同族）。</summary>
    public const string DraftPrefix = "[DRAFT]";

    /// <summary>
    /// <c>[L3]</c> 段前缀（**v6**；与 <c>[FOCUS]</c> / <c>[TAIL]</c> / <c>[DRAFT]</c> 同族）。
    /// <para>模型用它**点名要哪几条 L3**：「<c>[L3] S-bench-007</c>」= 请把这两条正文装进事件流。</para>
    /// </summary>
    public const string L3Prefix = "[L3]";

    /// <summary>
    /// <c>[TOOL]</c> 段前缀（**v7**；与 <c>[FOCUS]</c> / <c>[TAIL]</c> / <c>[DRAFT]</c> / <c>[L3]</c> 同族）。
    /// <para>
    /// 模型用它**点一次机器动作**：「<c>[TOOL] read {"path":"..."}</c>」。
    /// <b>一次回复最多 4 次</b>（<see cref="ToolMaxCallsPerReply"/>；v14 起，见协议第 5 条）—— 调用与结果一一对应（按发出顺序），方便逐条进流与逐条审批。
    /// </para>
    /// </summary>
    public const string ToolPrefix = "[TOOL]";

    /// <summary>
    /// <c>[DONE]</c> 段前缀（**v9**）—— 终局三态之一：**得解**（一条任务以此收尾）。
    /// <para>三态是**独立块**（主人 2026-09-20 定「感觉需要硬核一些」⇒ 不用三态合一的 <c>[SOLVE]</c>）。
    /// 判据与解析唯一家 = <see cref="TerminalReport"/>。</para>
    /// </summary>
    public const string DonePrefix = "[DONE]";

    /// <summary><c>[NO-SOLUTION]</c> 段前缀（**v9**）—— 终局三态之二：**无解**（必须带原因）。</summary>
    public const string NoSolutionPrefix = "[NO-SOLUTION]";

    /// <summary><c>[NEED-USER]</c> 段前缀（**v9**）—— 终局三态之三：**需要人**（要决定 / 要信息 / 确实卡住）。</summary>
    public const string NeedUserPrefix = "[NEED-USER]";

    /// <summary>
    /// **终局块头清单**（v9）—— 三者**互斥**：一次回复最多一个（多于一个 ⇒ 报告，不猜）。
    /// </summary>
    public static readonly string[] TerminalHeaders = [DonePrefix, NoSolutionPrefix, NeedUserPrefix];

    /// <summary><c>[DRAFT]</c> 段行数上限（= 协议第 4 条里的 <c>&lt;=16 lines</c>；唯一声明处）。</summary>
    public const int DraftMaxLines = 16;

    /// <summary><c>[DRAFT]</c> 段字符上限（草稿天然比白板长；协议区声明，配置无权改）。</summary>
    public const int DraftMaxChars = 3000;

    /// <summary>
    /// **决策报告块头**（P1/R1-P 第 12 条的内容契约，`docs/DESIGN-LIFECYCLE-BRIEF.md`）。
    /// <para>
    /// 它**已经是解析边界**（进 <see cref="ReportBlockHeaders"/>）：否则报告会被前一块的正文吞掉
    /// （`[TOOL]` v7 的教训，PITFALLS #30）。
    /// </para>
    /// <para>
    /// ⚠️ **本常量进清单不改协议正文 ⇒ 零缓存代价**：缓存盯的是 <see cref="Text"/> 的字节；
    /// 正文里写这一条（第 12 条）是**下一步（P2）**、要随内核版本一次做完并重跑遵从率（`§十·22`）。
    /// 现在先立**解析边界**，是为了让解析器与夹具能独立成立（宿主侧先能收、能拒、能复算）。
    /// </para>
    /// <para>
    /// 命名：**不叫 <c>[DECISION]</c>** —— 「决策」二字已被**决策卡**（<c>[NEED-USER]</c>）占用，
    /// 避免两样东西同叫「决策」（同「协议与 UI 永远写全 Solve Step」的同一手法）。
    /// </para>
    /// </summary>
    public const string ReportPrefix = "[REPORT]";

    /// <summary>报告块**行数上限**（形状约束；展示层**不设上限、不折叠** —— 主人 19:1x 定）。</summary>
    public const int ReportMaxLines = 30;

    /// <summary>报告里 <c>step</c> 条数上限。</summary>
    public const int ReportMaxSteps = 6;

    /// <summary>报告里 <c>found</c> 条数上限。</summary>
    public const int ReportMaxFound = 3;

    /// <summary>报告里 <c>next</c> 条数上限（与决策卡可选项同量级）。</summary>
    public const int ReportMaxNext = 3;

    /// <summary>
    /// **自报块头清单**（<c>[FOCUS]</c> / <c>[TAIL]</c> / <c>[DRAFT]</c> / <c>[L3]</c> / <c>[TOOL]</c> / <c>[REPORT]</c>）
    /// —— 解析一个块时，遇到**下一个块头即停**
    /// （一个回复可以同时带白板与草稿两块；不设分节符，先解析的那块会把后一块的正文吞进去）。
    /// <para>唯一声明处：块头是协议的一部分，只在这里出现一次。</para>
    /// <para>
    /// <b>v7</b>：把 <see cref="ToolPrefix"/> 也列进来 —— 它同样是「一个回复末尾的块」（协议第 4 条），
    /// 若不在清单里，<c>[TOOL] read {"path":"a"}</c> 会被前面那一块当成正文吞掉（PITFALLS #30 的同一类错）。
    /// </para>
    /// </summary>
    public static readonly string[] ReportBlockHeaders =
        [FocusPrefix, TailPrefix, DraftPrefix, L3Prefix, ToolPrefix, ReportPrefix, .. TerminalHeaders];
}
