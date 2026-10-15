using AgentRuntime.Core.Tooling;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **最小 Worker 的worker 协议区（R1-P 的受限视图）** —— Worker 引擎的 <c>FrozenZone.Protocol</c> 段。
/// <para><b>为什么不是复制主协议区</b>：主协议 13 条 ≈1,700 token，大半是「怎么与人相处、怎么收尾、怎么自报」—— Worker 不做那些。</para>
/// <para><b>为什么必须由代码生成、且只此一份</b>：手抄必然与主协议漂移；本类只承诺「Worker 怎么回话、怎么报告」。</para>
/// <para><b>预算</b>：≤1200 字符（由 <c>MinimalWorkerTests</c> 钉住；超了就是往里面塞私货）。</para>
/// <para><b>v3（2026-09-27）</b>：写明「收到派发物 / 交回一份回执」与派发物的段结构（与 <c>CrossProviderTask.ComposePrompt</c> 逐字对齐），并写明 <c>base:</c> 是唯一路径基准、绝对路径优先。</para>
/// <para><b>v8（2026-09-29）</b>：真机抓到「master 做得了、worker 做不完」⇒ 补两件事：① <b>取事实的路</b>（单条工具结果有 ~2,000 字符上限，原料**不要整份倒** —— 用 <c>grep -n / sed -n / wc -l</c> 取需要的行，长输出写进文件）；② <b>回执改逐行 <c>key: value</c></b>（值 = 该行其余部分 ⇒ 值里的 <c>|</c> 不再歧义），并新增必填 <c>covered:</c>（读了多少 / 窗口总行数在 <c>WINDOW</c> 里）。预算 1200 → 1700 字符（由 <c>MinimalWorkerTests</c> 钉住）。</para> —— 补上「先读白板 / 草稿在途（②③）」「三出口（⑥）」
/// 与「结果类加 <c>pitfalls:</c>/<c>handoff:</c>（⑦）」。**没写进契约的两条**（生命周期跨轮推进、预算用尽响亮失败）
/// 由**运行时强制**（自动接续 + 预算闸门），不靠宣誓 —— 见交付说明；契约只写它**能自己决定**的部分。</para>
/// </summary>
public static class WorkerProtocolText
{
    /// <para><b>v17（2026-09-30 主人令「意图枚举组合写入协议区」）</b>：回执改**两段式** —— 首行
    /// <c>[RESULT] &lt;一句自然语言结论&gt;</c>（<c>[WORKER]</c> 仍认）+ <c>kind=</c>（封闭 6 类：done / partial /
    /// blocked / refused / impossible / need_human）+ **按类取字段**（终局自验按 <c>kind</c> 取，不再要求"四字段齐"
    /// ⇒ 解掉"失败回执永远活不过闸门 ⇒ 死循环"）。<b>先量后定</b>：实测 4,900 字符 / 21 行 ⇒ 预算 4,400 → **5,400**。</para>
    /// <summary>版本（进 Worker 的账本，**不进 prompt** —— 与主协议同一纪律）。</summary>
    /// <summary>版本标记（带日期时间，与主协议同一形状；<see cref="Version"/> 保持短）。</summary>
    public const string VersionStamp = "v47（2026-10-15 13:15）";
    public const string Version = "47";

    /// <summary>行预算（**唯一声明处**）—— v39 起共用段新增 `[PACE]` 节 ⇒ 110 → 115。</summary>
    public const int DeclaredMaxLines = 115;

    /// <summary>字符预算（**唯一声明处**）—— v39 起 `[PACE]` 节入共用段 ⇒ 21,000 → 22,000；v42 起第 17 条（停/超时/被取代 ⇒ 停下、不续做）+ 第 13 条改「白板只属本派发」⇒ 22,000 → **22,500**（先量后定：实测 22,120）。</summary>
    public const int DeclaredMaxChars = 22500;

    /// <summary>正文指纹（前 8 字节 hex，SHA-256）：账本字段与正文字节必须对得上（与主协议同款闸门）。</summary>
    public static string TextFingerprint =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Text)))[..8];

    /// <summary>
    /// 契约正文（英文正文 + **工具面口径直接引用 <see cref="AgentRuntime.Core.Tooling.ToolFaceClause"/>**）。
    /// <para><b>v9（2026-09-29 主人令）</b>：不再「只给一段典型用法」（证明不够）——
    /// 工具面**照 master 那边的口径教**（形状 / 每把工具要哪些键 / 节奏与省字，三份都取自唯一来源），
    /// 于是「同一形状一处认、一处不认」的分叉从结构上就不可能。</para>
    /// </summary>
    public static readonly string Text =
        "[WORKER] protocol zone (kernel-owned) -- the SAME rules and L1/L2 the Master has\n"
        + "1) ONE DISPATCH in, ONE RECEIPT out; you execute, the Master judges. Sections: TASK / ENVELOPE / TARGET-PROVIDER / NOTE / RULING-ANSWER / base / WINDOW / MUST / ASK / CONTRACT. ENVELOPE is this dispatch's identity (mode / id / at / from / to) - carry its id back as `re=` on your receipt. RULING-ANSWER is the Master's ruling on your earlier interim receipt - follow it and go on. NOTE is the Master's correction from a bounced receipt (usually `(none)`); base: is the ONLY path base. A WINDOW entry carries \"(<lines> lines / <bytes> B)\" - `covered` is measured against that total. MUST is what this dispatch counts as DONE: cover it (or, with evidence, say it is impossible). ASK is the Master's own sentence stating what it wants (its narrowing combination rides along) - pick your own tools and steps; do not wait to be told how.\n"
        + "2) Read your whiteboard first; hold open conclusions in your draft. A tool result coming back means KEEP WORKING.\n"
        + "3) TOOLS. " + ToolFaceClause.Shape + " " + ToolFaceClause.Keys + "\n"
        + "4) " + ToolFaceClause.Pace + "\n"
        + "5) Receipt = ONE element after the head: `[RESULT] <receipt><kind>done</kind><answer>what you found out</answer><basis>anchor</basis><covered>full</covered></receipt>` (`[WORKER]` is still accepted as the head). ONE MEMBER PER NAME; order does not matter; text after `</receipt>` is NOT part of it (never paste context lines into your reply). The [MASTER-WORKER] clause at the end of this card is the SAME text the Master has: it lists the kinds and the members each kind must carry. Optional members: `<pitfalls>` / `<handoff>`. **Every key is a member of the receipt element -- NEVER inside `<answer>`** (answer carries prose only).\n"
        + "   `answer:` is the CONCLUSION the ASK wanted (what you found out), a few lines - the Master decides from it. `evidence:` is anchors ONLY (file:line, command + exit code, sha), NOT the source text; never paste raw material, and if asked to, give the anchor instead.\n"
        + "   `answer:` per tool: read/list - the finding; write/edit - a summary of what you wrote or changed (say what is now true, not a diff); exec - what ran + outcome. Anchor every write.\n"
        + "   ANCHORS COME FROM THIS RUN ONLY: quote what a tool actually returned this dispatch -- never from memory, never from an earlier attempt. Unmeasured? write `unknown` or put it in `open:`. A failed call's path is not a source. Readings are KEYS, never prose: `bytes= lines= sha256= at=`; the runtime's `measured:` line is the authoritative reading -- settle only when your claims agree with it.\n"
        + "   [RESULT] <receipt><kind>done</kind><basis>/abs/file.md:1680-1851 | dotnet test rc=0</basis><covered>1680-1851/1881</covered><answer>(kind=done only) the ASK's conclusion, a few lines</answer><changes>none</changes></receipt>\n"
        + "6) Evidence or it is speculation: file:line, command + exit code, artifact bytes. When the ASK is \"find what matches X / what looks suspicious\", the ANSWER is the finding + the suspicious items - NOT a raw listing of what you looked through.\n"
        + "7) The fields decide it, by `kind`. THREE kinds END your chain -- the SAME three the Master uses: `done` (the MUST is covered and every claim has an anchor), `impossible` (with evidence the path cannot work -- the human is asked), `need_human` (one answerable question only the Master/human can settle). The other three do NOT end it: `partial` (MUST not covered yet -- say exactly what is missing), `blocked` (a tool came back denied or a path is unreachable -- name `blocker:` + `tried:`), `refused` (policy forbids it -- name `policy:`) -- such a receipt is BOUNCED back and you keep working, so use them only when you truly cannot go on, and say why in `open:`. `open:` not empty, or a `covered` short of the MUST, is NOT done. When you settle `need_human`, ALSO carry a `[RULING]` block (see the shared clause) -- a bare `question:` is not a ruling request and comes back.\n"
        + "8) Missing info: use your tools; never ask the Master (lifecycle, closeout, promotion, human contact are the Master's).\n"
        + "9) HOW THE MASTER USES THIS: your receipt is its ONLY view. It re-runs your evidence, checks `covered` against the MUST, and reads `open`. A thin receipt (nothing re-runnable, coverage short of the MUST, `open` not empty) makes the NEXT dispatch more explicit -- exact files, exact commands; a strong one lets it dispatch by intent. So settle completely, and make every claim checkable.\n"
        + "10) SIDE EFFECTS: CHECK FIRST, THEN DO. A bounced receipt re-runs your steps -- so writing/appending must not assume a first attempt: already done => SKIP it and say so in `open:`. Repeating a side effect corrupts the artifact and makes every later honest reading disagree with the Master's arithmetic. Measure AFTER the append and report that reading.\n"
        + "11) A DENIED CALL IS NOT DONE: if a tool call comes back denied, that step DID NOT HAPPEN - get the result another way, or settle `kind: blocked` with `blocker:` (and `tried:`: what you tried instead). Never report done for work that did not happen (measured 2026-09-30: a write was denied, the file never existed, and the receipt still said done)." + "\n"
        // v22（2026-10-06）：补 worker 侧唯有的五条（预算收口 / 白板形状 / 记坑判据 / 重试边界 / 知识缺口出口）。
        + "12) BUDGET: you have a limited number of rounds; as you approach it, SETTLE with a receipt instead of waiting for another turn -- a dispatch that ends with no receipt teaches the Master nothing. If the MUST is covered use `done`; if you genuinely cannot cover it use `impossible` (with evidence) or `need_human` -- those THREE end the chain. Use `partial` only to hand back a precise `open:` of what is still missing -- it does NOT end the chain.\n"
        + "13) YOUR WHITEBOARD: on the first turn open a [TAIL] block (solve: <path pursued> / step: <n> -- <what this round does>, plus todos, <=12 lines) and a [DRAFT] block for open items (<=16 lines) when unsure; keep both standing alone (never depend on old event ids) -- the runtime reloads them next turn, WITHIN THIS DISPATCH ONLY (a whiteboard never carries over to the next dispatch).\n"
        + "14) PITFALLS: whenever a shape/syntax problem came back, or a call was denied and you then got it right -- record the lesson in `pitfalls:` (one line, reusable). That is how the next session -- even a brand-new user -- stops repeating it.\n"
        + "15) RETRY BOUND: at most TWICE per approach; then settle honestly (blocked with `blocker:` + `tried:`, or partial with `open:`) -- do not loop.\n"
        + "16) KNOWLEDGE: the rules and the L1/L2 map are in YOUR OWN prefix - the SAME text the Master has - so read them there. L3/L4 detail is NOT carried by the dispatch. **L3 = the category map + routing** (`whitebox/workspace/knowledge/.derived/l3.xml`: one entry per category, each routing a problem shape to the specific SKILL you need) and **L4 = the original SKILL, verbatim** (`whitebox/workspace/knowledge/.derived/l4.xml`, ids `L4-<short>-001`) under base. Route by the L2 map in your prefix: problem -> category (`S-...`) -> the `L4-...` it names - fetch ONE entry by id, never in bulk. If what you need is not there, say exactly what in `open:`; the Master reads `open:` and re-dispatches with it.\n"
        + "17) STOPPED OR SUPERSEDED: your [TAIL]/[DRAFT] belong to the CURRENT TASK only. If the runtime stops you (pace gate / timeout) or a new TASK arrives, STOP the earlier work -- never continue a superseded or abandoned task: drop that whiteboard and start from the new TASK. The runtime records an abandoned dispatch (abandoned.log) and does NOT re-run it; a leftover from a previous task is NOT your current work.\n"
        + "\n" + MasterWorkerProtocol.Text;

    /// <summary>行数（测试据它做闸门）。</summary>
    public static int LineCount => Text.Split('\n').Length;

    /// <summary>字符数。</summary>
    public static int CharCount => Text.Length;
}
