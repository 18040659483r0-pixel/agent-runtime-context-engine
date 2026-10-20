using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Modules;

namespace AgentRuntime.Tests;

/// <summary>
/// **协议区（R1-P）闸门测试** —— P1~P4 四条不变量，全部做成可执行闸门（不靠纪律）：
/// <list type="number">
/// <item><b>P1 来源只能是代码</b>：<c>RuntimeConfiguration</c> 不得出现协议区路径 / 开关；<c>--domains</c> 不作用于协议区。</item>
/// <item><b>P2 收尾不可改</b>：晋升白名单显式排除 <c>FrozenZone.Protocol</c>，尝试写入 ⇒ 抛错（不静默跳过）。</item>
/// <item><b>P3 用户不可摘</b>：<c>modules</c> / <c>--modules</c>（空串 = 裸聊）都摘不掉协议模块 ⇒ 报错。</item>
/// <item><b>P4 预算</b>：行数 / token 在**声明预算**之内（口径：**先量后定**，每次改动后按实测重定；历任：≤8 行/≤280 token → … → **v12 = ≤12 行 / ≤900 token → v13 = ≤12 行 / ≤1,000 token**，见下方可复算来历）/ 必须 Rank 0 ⇒ 超限测试失败。</item>
/// </list>
/// 另加：协议区**字节稳定**、版本号只进账本不进 prompt、以及 <c>focus.reportHint</c> 删除后的迁移闸门。
/// </summary>
public sealed class ProtocolZoneTests
{



    private static RuntimeConfiguration Config(List<string>? modules = null, List<string>? domains = null) => new()
    {
        BaseUrl = "https://example.invalid/v1",
        Model = "m",
        Modules = modules ?? [],
        Frozen = new FrozenConfiguration(),
    };

    // ---------------- P4：预算 + Rank 0 ----------------

    [Fact]
    public void Gate_ProtocolBudget_IsWithinLimits_AndRankZero()
    {
        // 行数：≤ 规格声明的 8 行。
        Assert.True(
            ProtocolText.Lines.Count <= ProtocolText.DeclaredMaxLines,
            $"协议区文本 {ProtocolText.Lines.Count} 行，超出预算 {ProtocolText.DeclaredMaxLines} 行。");

        // 位置：必须 Rank 0（最前），且在铁则之前 —— 这条同时钉住「协议先于一切」。
        Assert.Equal(FrozenZone.Protocol, FrozenZoneTopology.Ordered[0]);
        Assert.Equal(0, FrozenZoneTopology.Rank(FrozenZone.Protocol));
        Assert.True(FrozenZoneTopology.Rank(FrozenZone.Protocol) < FrozenZoneTopology.Rank(FrozenZone.Rules));
        Assert.Equal(FrozenZoneTier.Protocol, FrozenZoneTopology.TierOf(FrozenZone.Protocol));

        // token：规格口径 = 英文 ~4 字符/token（唯一估算处 CharsPerToken）。
        Assert.Equal(4.0, ProtocolText.CharsPerToken);
        Assert.Equal((int)Math.Ceiling(ProtocolText.CharCount / ProtocolText.CharsPerToken), ProtocolText.EstimatedTokens);
        Assert.True(
            ProtocolText.EstimatedTokens <= ProtocolText.DeclaredMaxTokens,
            $"协议区文本 {ProtocolText.EstimatedTokens} token，超出预算 {ProtocolText.DeclaredMaxTokens} token。");

        // ⚠️ **三次放宽/重定的来历（可复算，不靠记性）**：
        //    ① r532（主人 2026-09-15 22:09 按 A 定）：正文实测 723 字符 ⇒ 181 token > 旧声明 160 ⇒ 预算放宽到 ≤200。
        //    ② V4.3（主人 2026-09-15 23:2x 定稿 Q5）：加入 [DRAFT] 段后正文 849 字符 ⇒ 213 token > 200 ⇒ 放宽到 ≤10 行 / ≤240 token。
        //    ③ v5（主人 2026-09-15 23:4x 定，协议区扩容 / DESIGN-SKILL-LAYERS §十）：正文 1029 字符 ⇒ 258 token > 240
        //       ⇒ 预算重定为 ≤8 行 / ≤280 token（正文一个字节不删；行数回到 8 是因为 v5 实测只有 6 行）。
        //    ④ v6（协议区补 [L3] 自报块，依据 DESIGN-SKILL-LAYERS §五；前置 = 「按 id 装载 L3」已落地）：
        //       **先重算再定预算** —— 正文 1106 字符 ⇒ 277 token **仍在 ≤8 行 / ≤280 token 之内，预算不变**。
        //    ⑤ v7（协议区补 [TOOL] 块，依据 DESIGN-TOOL-FACE §六；前置 = 工具面同期落地）：
        //       **先重算再定预算** —— 正文 1323 字符 ⇒ 331 token > 280 ⇒ 预算重定为 **≤8 行 / ≤340 token**
        //       （其余 5 条逐字节未改；行数仍 6）。
        //    ⑥ v8（主人 2026-09-16 22:19/22:23 定，**与「知识库 18KB 精简」同窗口**）：
        //       ① 第 2 条去掉 SKILL 口径（**本系统只有 L1/L2/L3**；外部 skill 靠**吸收收敛**汇入）；
        //       ② 块名 [SKILL] → [L3]（唯一声明处 `ProtocolText.L3Prefix`）；
        //       ③ 第 4 条补「**怎么请求能力**」（DESIGN-SECURITY-GATEWAY §八 S0）。
        //       **先重算再定预算** —— 正文 1559 字符 ⇒ 378 token > 340 ⇒ 预算重定为 **≤8 行 / ≤400 token**（行数仍 6）。
        // 下面三个数（实测值 / 预算 / 闸门）都钉住，谁要复核直接看这三行。
        //    ⑦ v9（主人 2026-09-20 12:3x 定：命名 Solve Step · 终局用独立块 · 预算 ≤10 行 / ≤600 token）：
        //       ① 新增第 4 条「求解语言」（Equation / Known Conditions / Solution / Solve Step / Solution Set / Intervention）；
        //       ② 旧第 4 条 → 第 5 条，[TAIL] 首两行改为 solve / step（白板承担「当前解 / 求解步 i」，**不新增块**）；
        //       ③ 新增第 6 条终局三态（[DONE] / [NO-SOLUTION] / [NEED-USER]，**三个独立块**）；
        //       ④ 新增第 7 条收尾 / 重开（closeout + reset 是设计内交接；≥20% 窗口且任务了结 ⇒ 提议）。
        //       **先量后定** —— 正文 2,390 字符 ⇒ 598 token、9 行 ⇒ 预算 ≤10 行 / ≤600 token。
        //    ⑧ v10（主人 2026-09-20 13:3x 定「现在就改，不必攒着」）：第 7 条补**收尾三层**
        //       （task 收尾：handoff + 坑 / session 收尾：抽象 L1/L2 + 晋级 + 推进水位 + 记收尾水位线 / reset）。
        //       实测 2,699 字符 ⇒ 675 token、10 行 ⇒ 预算重定为 **≤12 行 / ≤700 token**。
        //       为何此刻改：WB 侧头部几天没跑、缓存命中早已失效 ⇒ 归零代价 ≈ 0。
        //    ⑨ v11（主人 2026-09-20 14:4x「把整个流程的操作手法写入冻结区协议区」）：第 7 条扩成**操作手册**，
        //       新增第 8/9 条写死「谁执行 / 什么顺序 / 失败怎么办 / 一切可回滚 / 20% 达标即自动跑」。
        //       实测 3,122 字符 ⇒ 781 token、11 行 ⇒ 预算 **≤12 行 / ≤800 token**。
        //    ⑩ v12（主人 2026-09-20 16:1x「权限判定权下放给远端 AI 自判」，依据 docs/DESIGN-APPROVAL-V12.md）：
        //       第 5 条 [TOOL] 补 **risk: 声明**（risk: none|<what could break>；不会损害电脑/数据/公共安全才写 none；
        //       硬红线照旧拦；假声明被记下来）—— **不新增块**。
        //       实测 3,459 字符 ⇒ 865 token、11 行 ⇒ 预算重定为 **≤12 行 / ≤900 token**。
        //    ⑪ v13（主人 2026-09-21 23:2x「放弃 runtime 的硬闸门，纯用纪律约束远端 AI」＋「凭据和私钥暂时也放行」，
        //       依据 docs/DESIGN-APPROVAL-V13.md）：撤闸门（第 5 条改成事实：分类 + 留档 + 照跑，任务内不问人）+ 终局前自判（第 6 条）。
        //       实测 3,612 字符 ⇒ 903 token、11 行 ⇒ 预算显式重定为 **≤12 行 / ≤1,000 token**。
        //    ⑫ v14（主人 2026-09-22 03:2x「允许多个 tool/turn」＋「第 2 条边界写清」）：
        //       第 5 条一次回复最多 4 次 [TOOL]（旧 one call per reply；实测 9 轮里 4 轮是「读一点看一点」）+ 第 2 条 bulk 边界 + risk 在 JSON 外。
        //       实测 3,980 字符 ⇒ 995 token、11 行 ⇒ 预算重定为 **≤12 行 / ≤1,100 token**。
        //    ⑬ v15（主人 2026-09-22 03:3x「A+B+C」）：v14 实测变差（9 轮/16.3k → 12 轮/37.1k）⇒ 第 2 条措辞收正（要齐窗口、绝不整份倒；结果有上限+指针）。
        //       实测 4,029 字符 ⇒ 1008 token、11 行 ⇒ **仍在 ≤12 行 / ≤1,100 token 之内，预算不动**。
        //    ⑭ v16（主人 2026-09-22 03:3x「顺手做了」）：第 5 条补上「工具名 + 各工具的键」（同一处渲染 = ToolNames.FaceText）。
        //       实测 4,138 字符 ⇒ 1035 token、11 行 ⇒ **仍在 ≤12 行 / ≤1,100 token 之内，预算不动**。
        //    ⑮ v16 / v17（主人 2026-09-22 03:4x）：v16 第 5 条补工具面（名字 + 各工具的键）；
        //       v17 新增第 11 条「东西在哪 + 怎么干得快」（工具调用效率属协议层，不属用户铁则区）。
        //       实测 4,719 字符 ⇒ 1180 token、12 行 ⇒ 预算**扩容**为 **≤13 行 / ≤1,300 token**。
        //    ⑯ v18（主人 2026-09-22 04:0x 令）：第 5 条把 [TOOL] **形状说死 + 给带引号完整示例**
        //       （v16 的键清单只当**附录** —— 键清单不能替代形状）。起因：真机实测模型照抄 v16 的紧凑形状
        //       ⇒ 写出 {command: "…", timeoutSeconds: 60}（**键无引号** ⇒ 非法 JSON ⇒ fail-closed），
        //       一场 11 轮问答里 **3 轮纯浪费**（E003/E004/E005 三条 ToolDenied，第 4 轮才自己改对）。
        //       实测 4,841 字符 ⇒ 1211 token、12 行 ⇒ **仍在 ≤13 行 / ≤1,300 token 之内，预算不动**。
        //    ⑰ v19（主人 2026-09-22 16:4x 令「动协议区，这次动完基本不用再动」）：第 5 条 read 的键补 `symbol`
        //       + 教会「按名字一次取整段」。依据：同题六跑的 A/B —— 上限两方向都更差、附注三版都更差（坑 #114~#116）
        //       ⇒ 病不在窗口而在「要几次才够」。实测 5,073 字符 ⇒ 1269 token、12 行 ⇒ **仍在 ≤13 行 / ≤1,300 token 之内，预算不动**。
        //    ⑱ v20（主人 2026-09-22 17:0x：「**这是交互体验的重要方面，不能由用户知识库去兜底**」）：第 6 条补**负向口径** ——
        //       「没结束就不发终局块（继续干用 [TAIL]/[DRAFT]/[FOCUS]/[TOOL]）」+「`[NO-SOLUTION]` 只表示已证此路不通，
        //       不是『本轮没做完』」+「同轮还在点工具就不算终局」。起因：真机 `.stage1/stream-20260922-164509.jsonl` E011 ——
        //       模型想表达「本轮不终局」却写了 `[NO-SOLUTION] 本轮不发终端块 —— 取证未完，继续。`
        //       ⇒ 运行时照判**无解**（`--lifecycle-show` 回放：#2 ✗ 无解），任务当场定格、8 个工具的结果回来后再无下一轮。
        //       实测 5,347 字符 ⇒ 1337 token、12 行 ⇒ 预算按「先量后定」重定为 **≤13 行 / ≤1,400 token**。
        //       ② 21:5x（同一 v20 未真机跑过 ⇒ **只付一次冷启**）：再补一句「**停下来问人本身就是终局 ⇒ 用 [NEED-USER]**；
        //       自然语言问句不能替代它」。起因：真机 `.stage1/stream-20260922-170759.jsonl` E248 —— 它问「L3 切条现在重建吗？给 API key 路径」
        //       却没发终局块 ⇒ 卡 `#3 ● 进行中` 悬住（坑 #120）。实测 5,558 字符 ⇒ **1390 token**、12 行
        //       ⇒ 预算上移一档为 **≤13 行 / ≤1,500 token**（留 ~8% 余量，照 v14 的教训）。
        // v21（2026-09-24 19:4x，主人定「报告必交 / 完整版 / 终局后下轮交」）：新增**第 12 条·决策报告**（[REPORT] 块，≤30 行）——
        //       实测 **6,534 字符 ⇒ 1634 token**、13 行 ⇒ 预算按「先量后定」重定为 **≤13 行 / ≤1,800 token**（留 ~10% 余量）。
        // v22（2026-09-24 夜，主人令「协议区做净」）：第 12 条补 **`body:` 成品正文位**（题面 / 文案 / 方案正文逐字进；原料仍不进）——
        //       依据坑 #154 / §十·61（「不铺原料」的口径缺「成品」一类 ⇒ 成品与原料一起被拍死）。
        //       v23（2026-09-25）：第 13 条【派发给 Worker】上线 ⇒ 实测 **7,562 字符 ⇒ 1891 token**、14 行 ⇒ 预算重定 **≤14 行 / ≤2,100 token**（留 ~10% 余量）。
        //       v24（2026-09-25）：第 13 条措辞改强命令式（Dispatch BY DEFAULT）+ 阈值与 WorkerConfiguration.BulkTokens(3,000) 同锚
        //       ⇒ 实测 **7,870 字符 ⇒ 1,968 token**、14 行 ⇒ 预算重定 **≤14 行 / ≤2,200 token**。
        // v25（2026-09-25 夜，主人令「本次一次改够」）：① 第 5 条工具队列口径（≈4 为**节奏**；超出**排队、不拒、不丢**）
        //       ② 第 12 条「报告轮不带终局块」（DESIGN-LIFECYCLE-BRIEF §十三-derived #2 挂账落地）
        //       ⇒ 实测 **8,589 字符 ⇒ 2,148 token**、14 行 ⇒ 预算上移 **≤14 行 / ≤2,400 token**（留 ~10% 余量）。
        // v31（2026-09-29 21:3x · 主人令「改 master 协议区」）：第 5 条键清单**按角色渲染**（master 只 index/scope，
        //       由 ToolFaceClause.MasterKeys 渲染）+ 第 13 条补 `ask:` / 回执读数 / 地址一律绝对
        //       ⇒ 实测 **9,267 字符 ⇒ 2,317 token**、14 行 ⇒ **仍在 ≤14 行 / ≤2,400 token 之内，预算不动**（先量后定）。
        // v32（2026-09-30 01:3x · 主人令「master 和 worker 的协议区都要改，让 worker 也理解 master 是怎么使用它的」）：
        //       第 13 条补「**按回执校准派发详细度**」（薄 => 说细：具体文件/命令/答案形状，并拆小；强 => 按意图派）
        //       + worker 的载荷预算小、记忆跨派发保留 ⇒ 实测 **9,565 字符 ⇒ 2,392 token**、14 行 ⇒ **仍在 ≤2,400 之内，预算不动**（先量后定）。
        // v58（2026-10-09 22:15 · 主人令 · D3-A）：第 5 条补一句 **[NOTE] 块**（把一段话落进当日 memory，运行时确定性写盘，不经派发链）
        //       ⇒ 实测 **6,247 token** ⇒ **仍在 ≤110 行 / ≤6,300 token 之内，预算不动**（先量后定）。
        // v64（2026-10-14 22:05 · 主人令「过程中每一轮都刷」）：第 5 条回转 v62-B ——
        //       `[TAIL]`/`[DRAFT]` 由「只在决策报告轮写一次」改为**每条回复都刷新当前工作状态**
        //       ⇒ 实测 **8,856 字符 ⇒ 2,214 token**、14 行 ⇒ **仍在 ≤110 行 / ≤2,300 token 之内，预算不动**（先量后定）。
        // v65（2026-10-14 23:50 · 主人令「b 也要做」）：第 5 条补**长正文纪律**（长值用 `key: <<` 块，禁塞进 `{...}`）
        //       ⇒ 实测 **9,176 字符 ⇒ 2,294 token**、14 行 ⇒ **仍在 ≤110 行 / ≤2,300 token 之内，预算不动**（先量后定）。
        // v66（2026-10-15 00:25 · 主人令「不要等到任务终局再提」）：第 7 条 task closeout 话术改写（坑/handoff 当轮就说）
        //       ⇒ 实测 **9,276 字符 ⇒ 2,319 token**、14 行 ⇒ 预算按「先量后定」重定为 **≤110 行 / ≤2,400 token**。
        // v67（2026-10-15 01:35 · 主人令「改协议」）：第 5 条回转 v64 —— **[TAIL]/[DRAFT] 只在真变了时才写**
        //       （没变就一个块都不写；重写未变状态 = 纯噪声）。
        //       ⇒ 实测 **9,279 字符 ⇒ 2,320 token**、14 行 ⇒ **仍在 ≤110 行 / ≤2,400 token 之内，预算不动**（先量后定）。
        // v68（2026-10-15 13:2x · 主人令「用协议区的方式把这个改好」）：第 5 条去旧形状尾巴（`{...}`/`key: <<`）
        //       ⇒ 实测 **9,271 字符 ⇒ 2,313 token**、14 行 ⇒ **仍在 ≤110 行 / ≤2,400 token 之内，预算不动**（先量后定）。
        // v69（2026-10-16 13:30 · 主人令「rules 平时只读、收尾中收敛；改写请求单 + 水位线」）：新增**第 15 条**
        //       ——  改 rules 不许直接写，只往 `whitebox/workspace/rules-changeOrder.md` 追加一行 [open] 请求；
        //       收尾把 [open] 收敛进真 rules 并推进该文件的水位线（只由收尾写）。
        //       ⇒ 实测 **9,877 字符 ⇒ 2,470 token**、15 行 ⇒ 预算按「先量后定」重定为 **≤110 行 / ≤2,500 token**。
        // v70（2026-10-16 13:55 · 主人令「扫一眼协议区有没有跟这几轮改动冲突的」）：**一次改够五处** ——
        //       ① 第 5 条：**memory 是只读兜底层**，不许用工具直接写（用 [NOTE]）；② 第 7 条：当日记忆**经 [NOTE] 收敛**；
        //       ③ 第 11 条：补「收尾的可扩展件在 tools/closeout/（requirements/<id>.xml），改前先读仓库根 EXTENSIBILITY.md」；
        //       ④ 第 15 条：补「对 rules 区的其它落定（含条款 6 的 USER-PREFERENCES）也走请求、不直接写」；
        //       ⑤ 版本 69 → 70。⇒ 实测 **10,448 字符 ⇒ 2,612 token**、15 行 ⇒ 预算重定为 **≤110 行 / ≤2,700 token**。
        // v71（2026-10-16 23:58 · 主人令「skill 已从 WB 退役，不要再看到任何 skill 的内容」）：
        //       协议正文里两处 skill 措辞去词（第 2 条「L4 = 原 skill 逐字」→「L4 = 逐字原件」；
        //       第 5 条「L4-…-001 a skill」→「an entry」）⇒ 实测 **10,442 字符 ⇒ 2,611 token**、15 行 ⇒ 预算不动。
        // v77（2026-10-18）：新增第 16 条「排障纪律」（同一错误两次即停 / E160006 走全新本地 WC /
        // 旧 TAIL/DRAFT 不反复自证 / 含 shell 元字符的脚本走单块 + 文件）；实测 **12,106 字符 ⇒ 3,027 token**、
        // 16 行 ⇒ 预算按先量后定为 **≤3,250**（v79 再补“不得复用坏路径名”）。
        // v86（2026-10-20）：新增第 20 条「收尾回执 + 两库生长硬闸门」⇒ 实测 15,714 字符 ⇒ 3,929 token、20 行。
        Assert.Equal(3929, ProtocolText.EstimatedTokens);
        Assert.Equal(4300, ProtocolText.DeclaredMaxTokens);
        Assert.True(ProtocolText.EstimatedTokens <= ProtocolText.DeclaredMaxTokens);
        Assert.True(ProtocolText.MaxTokens >= ProtocolText.DeclaredMaxTokens);
        Assert.Equal(4300, ProtocolText.MaxTokens);
        Assert.Equal(110, ProtocolText.DeclaredMaxLines);
        Assert.True(ProtocolText.Lines.Count <= ProtocolText.DeclaredMaxLines);

        // v5：协议区扩容（L1/L2/L3 + 铁则 + memory-index + 各区契约）后 [DRAFT] 段仍在协议区（唯一声明处），
        // 且上限与读序都与闸门对得上；版本号升 "5"。
        // v6：补 [L3] 自报块（自报条里声明），版本号升 "6"，且它进了分节块头清单（PITFALLS #30）。
        // v7：补 [TOOL] 块；v8：去 SKILL 口径 + 块名 [SKILL]→[L3] + 补「怎么请求能力」，版本号升 "8"。
        // v20（2026-09-22 17:0x）：第 6 条补负向口径（没结束不发终局块；[NO-SOLUTION] 只表示已证此路不通），版本号升 "20"。
        // v21（2026-09-24 19:4x）：新增第 12 条**决策报告**（[REPORT] 块，块 ≤30 行，必交、缺失即「未提交」），版本号升 "21"。
        // v24（2026-09-25）：第 13 条【派发给 Worker】口径落地；v25（同日夜）：第 5 条队列口径 + 第 12 条报告轮，版本号升 "25"。v26（2026-09-27）：第 5 条工具面补 index{symbol}（协议正文与工具面同一份渲染），版本号升 "26"。v27（2026-09-27）：第 5 条工具面补 scope{task}（任务地址表入口），版本号升 "27"。v28（2026-09-29）：第 5 条工具面形状改**行式键值**（去 JSON —— 与运行时工具面 / worker worker 协议区同一口径），版本号升 "28"。v64（2026-10-14）：第 5 条 [TAIL]/[DRAFT] 回转**每轮刷新**（v62-B 作废）。v65：长正文纪律。v66：task closeout 当轮说。v67（2026-10-15）：第 5 条回转 v64 —— **[TAIL]/[DRAFT] 只在真变了时才写**（没变就一个块都不写），版本号升 "67"。
        Assert.EndsWith("86", ProtocolText.Version, StringComparison.Ordinal);
        // 2026-10-06 主人令：版本标记必须带**日期时间**（加载时一眼确认是不是最新）。
        Assert.Matches(@"^v86（\d{4}-\d{2}-\d{2} \d{2}:\d{2}）$", ProtocolText.VersionStamp);
        Assert.Contains(ProtocolText.DraftPrefix, ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains($"<={ProtocolText.DraftMaxLines} lines", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains($"<={ProtocolText.TailMaxLines} lines", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Equal(16, ProtocolText.DraftMaxLines);
        Assert.Equal(3000, ProtocolText.DraftMaxChars);

        // v74：协议里写得出 [KN] 块（统一取知识；旧 [L3] 只作兼容读）。
        Assert.Equal("[KN]", ProtocolText.KnPrefix);
        Assert.Contains(ProtocolText.KnPrefix, ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains(ProtocolText.KnPrefix, ProtocolText.ReportBlockHeaders);

        // v7 的实质：协议里写得出 [TOOL] 块（一次回复只允许一次；且运行时真的兑现 —— 见 ToolFaceTests）。
        Assert.Equal("[TOOL]", ProtocolText.ToolPrefix);
        Assert.Contains(ProtocolText.ToolPrefix, ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains(ProtocolText.ToolPrefix, ProtocolText.ReportBlockHeaders);

        // v12 的实质：协议的 [TOOL] 那一句要求带 risk: 声明（判定权归属升级 —— 模型自判、硬红线仍拦、假声明记录在案）。
        // 协议说的 = 代码要解析的：此处钉住字面量，解析侧钉在 ToolFaceTests/ApprovalV12Tests。
        Assert.Contains("<risk>none</risk>", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("a false claim is recorded", ProtocolText.Text, StringComparison.Ordinal);

        // v9 的实质（三块都写进正文 + 都进分节块头清单 —— 否则终局块会被前一块当正文吞掉，PITFALLS #30）：
        Assert.Equal(["[DONE]", "[NO-SOLUTION]", "[NEED-USER]"], ProtocolText.TerminalHeaders);
        foreach (var header in ProtocolText.TerminalHeaders)
        {
            Assert.Contains(header, ProtocolText.Text, StringComparison.Ordinal);
            Assert.Contains(header, ProtocolText.ReportBlockHeaders);
        }

        // v9 的求解口径与 20% 阈值也在正文里（协议说的 = 代码声明的，不能两处各说一套）。
        Assert.Contains("Solve Step", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("solve: ", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("step: ", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("80% of the window", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Equal(0.80, ContextBudget.ProposeRatio);
        Assert.Contains("closeout", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("reset", ProtocolText.Text, StringComparison.Ordinal);

        // v21 的实质：协议正文里写得出 [REPORT]（且运行时真的能收 —— 见 DecisionReportTests）。
        Assert.Equal("[REPORT]", ProtocolText.ReportPrefix);
        Assert.Contains(ProtocolText.ReportPrefix, ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains(ProtocolText.ReportPrefix, ProtocolText.ReportBlockHeaders);
        Assert.Contains($"<={ProtocolText.ReportMaxLines} lines", ProtocolText.Text, StringComparison.Ordinal);

        Assert.Equal(13, ProtocolText.ReportBlockHeaders.Length);  // + [DISPATCH]（v23）+ [INTENT]（v33）+ [NOTE]（v58 · D3-A）+ [KN]（v74）
        Assert.Contains(ProtocolText.DispatchPrefix, ProtocolText.ReportBlockHeaders);
    }

    // ---------------- P1：来源只能是代码 ----------------

    [Fact]
    public void Gate_Protocol_SourceIsCodeOnly()
    {
        // 类型上就没有配置入口：RuntimeConfiguration（含各嵌套配置段）不得出现任何「协议」路径 / 开关。
        var types = new[]
        {
            typeof(RuntimeConfiguration),
            typeof(FrozenConfiguration),
            typeof(StreamConfiguration),
            typeof(SnapshotConfiguration),
            typeof(FocusConfiguration),
        };

        foreach (var type in types)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.False(
                    property.Name.Contains("protocol", StringComparison.OrdinalIgnoreCase),
                    $"{type.Name}.{property.Name} 暴露了协议区入口 —— 协议来源只能是代码（ProtocolText 常量）。");
            }
        }

        // 协议模块的构造也必须无参数（没有文件 / 配置 / 域选择可传）。
        Assert.Empty(typeof(ProtocolModule).GetConstructors().Single().GetParameters());

        // 协议文本的唯一声明处是代码常量：改代码才能改协议。
        Assert.False(string.IsNullOrWhiteSpace(ProtocolText.Text));
        Assert.EndsWith("86", ProtocolText.Version, StringComparison.Ordinal);
        // 2026-10-06 主人令：版本标记必须带**日期时间**（加载时一眼确认是不是最新）。
        Assert.Matches(@"^v86（\d{4}-\d{2}-\d{2} \d{2}:\d{2}）$", ProtocolText.VersionStamp);

        // 知识树配置只允许「**路径**」：Repo = 地址表。
        // 不得出现行数 / 上限 / **协议开关**这些「用户改协议」的口子。
        // ⚠️ 白名单是**穷举**的：新增属性必须改这里（改不动的默认拒绝），否则这个闸门就漏了。
        // 2026-10-10（去重 A）：Resident / ResidentDomains **已退役** —— 技能常驻层不再挂载（冻结块二已是同一份）。
        var skillProperties = typeof(KnowledgeConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Repo"], skillProperties);
        Assert.All(skillProperties, n =>
            Assert.DoesNotContain("protocol", n, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Gate_Protocol_IsNotAffectedByDomains()
    {
        // --domains 不作用于协议区：任何域选择下，协议段都存在、且**逐字节全量相同**。
        foreach (var domains in new[] { new List<string>(), ["software"], ["finance"], ["software", "finance"] })
        {
            var prefix = FrozenPrefix.Assemble(ModuleRegistry.Create(Config(domains: domains)));

            var protocol = Assert.Single(prefix.Sections, s => s.Zone == FrozenZone.Protocol);
            Assert.Equal(ProtocolText.Text, protocol.Text);
            Assert.Equal(FrozenZone.Protocol, prefix.Sections[0].Zone);   // 永远在全量前缀的第一段
            Assert.Equal("protocol.global", protocol.SectionId);
        }
    }

    // ---------------- P2：收尾不可改 ----------------

    [Fact]
    public void Gate_Protocol_CannotBePromotedByCloseout()
    {
        // 白名单**显式排除**协议区（其余三区照旧可晋级）。
        Assert.DoesNotContain(FrozenZone.Protocol, CloseoutService.PromotionTargets);
        Assert.Equal(
            new[] { FrozenZone.Rules, FrozenZone.Knowledge, FrozenZone.MemoryIndex },
            CloseoutService.PromotionTargets.ToArray());

        Assert.False(CloseoutService.CanPromoteTo(FrozenZone.Protocol));
        foreach (var zone in CloseoutService.PromotionTargets)
        {
            Assert.True(CloseoutService.CanPromoteTo(zone));
            Assert.Equal(zone, CloseoutService.EnsurePromotionTarget(zone));
        }

        // 尝试写入协议区 ⇒ **抛错**（不静默跳过）。
        var ex = Assert.Throws<InvalidDataException>(() => CloseoutService.EnsurePromotionTarget(FrozenZone.Protocol));
        Assert.Contains("协议区", ex.Message, StringComparison.Ordinal);
        Assert.Contains("不静默跳过", ex.Message, StringComparison.Ordinal);
    }

    // ---------------- P3：用户不可摘 ----------------

    [Fact]
    public void Gate_Protocol_CannotBeRemoved()
    {
        // modules / --modules / --bare（= 空模块列表）都摘不掉协议模块。
        var cases = new List<string>?[]
        {
            null,                 // config 未给 modules（默认空）
            [],                   // `--bare` / `--modules ""`
            ["session"],
            ["rules", "knowledge", "memory-index", "focus"],
            ["protocol"],         // 显式列出也只是幂等请求（不重复装配）
        };

        foreach (var modules in cases)
        {
            var built = ModuleRegistry.Create(Config(modules: modules));

            Assert.Equal("protocol", built[0].Name);
            Assert.IsType<ProtocolModule>(built[0]);
            Assert.Single(built, m => m is ProtocolModule);   // 恰好一个（不会因列出而重复）
            Assert.Equal(FrozenZone.Protocol, ((IFrozenZoneModule)built[0]).Zone);
            DeterminismGate.Validate(built);
        }

        // 「结果」层守卫：任何缺协议的模块集 ⇒ 报错（闸门有牙，不靠调用方自觉）。
        var ex = Assert.Throws<InvalidDataException>(() =>
            ModuleRegistry.EnsureProtocolPresent([new SessionModule()]));
        Assert.Contains("不可摘除", ex.Message, StringComparison.Ordinal);

        ModuleRegistry.EnsureProtocolPresent([new ProtocolModule(), new SessionModule()]);   // 正向：不抛
    }

    // ---------------- 字节稳定 + 版本号只进账本 ----------------

    [Fact]
    public void Gate_Protocol_IsByteStable_AndVersionStaysInLedger()
    {
        var first = FrozenPrefix.Assemble(ModuleRegistry.Create(Config()));
        var second = FrozenPrefix.Assemble(ModuleRegistry.Create(Config()));

        // 两次装配逐字节相同（协议区是前缀里最稳定的字节段）。
        Assert.Equal(first.PromptText, second.PromptText);
        Assert.Equal(first.Id, second.Id);
        Assert.StartsWith(ProtocolText.Text, first.PromptText, StringComparison.Ordinal);

        // 版本号只进 manifest，不进 prompt（段名也不进 prompt）。
        var manifest = FrozenManifest.FromSnapshot(first);
        Assert.Equal(ProtocolText.Version, manifest.Entries.Single(e => e.SectionId == "protocol.global").Version);
        Assert.DoesNotContain("protocol.global", first.PromptText, StringComparison.Ordinal);
        Assert.DoesNotContain("协议区", first.PromptText, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_协议正文里的工具面_与代码同一份渲染()
    {
        // 2026-09-22（主人令「顺手做了」）：协议第 5 条补上「名字 + 各工具的键」—— 模型第一次点动作就能写对
        // （实测它先前猜 `bash` / `cmd`，白烧一轮）。这张面由 `ToolNames.FaceText` **同一处渲染**：
        // 谁改了工具、或改了它的参数名，这里就红 —— 防「协议里写的」与「运行时认的」两处漂移。
        var face = ToolNames.FaceText(ToolSet.Default());
        Assert.Equal(
            "read{path,symbol,maxLines,offset,limit} list{path} write{path,content} edit{path,oldText,newText} exec{command,timeoutSeconds} index{symbol} scope{task} web_fetch{url,maxChars,offset,format} web_search{query,count,recency} http_request{method,url,headers,query,body,contentType,authRef,timeoutSeconds} web_download{url,path,maxBytes,conflict} web_search_extract{urls,maxCharsPerPage,concurrency}",
            face);
        Assert.Contains(ToolFaceClause.MasterKeys, ProtocolText.Text, StringComparison.Ordinal);
        // 2026-10-10（主人令「把工具交回 master」）：master 面 = **全量**（<c>ToolNames.MasterFace = All</c>）
        // ⇒ 正文第 5 条必须列出全部 7 把（正文说的 = 运行时给的，同一处渲染）。
        Assert.Equal(ToolFaceClause.KeysText(ToolNames.MasterFace), ToolFaceClause.MasterKeys);
        Assert.Contains("read{path", ProtocolText.Text, StringComparison.Ordinal);

        // v18 的实质（主人 2026-09-22 04:0x 令）：**键清单不能替代形状** —— v16 只教了 `exec{command,timeoutSeconds}`
        // 这种紧凑形状，模型照抄它写成 `{command: "…", timeoutSeconds: 60}`（键没引号 ⇒ 非法 JSON ⇒ 被拒），
        // 真机一场 11 轮里白烧 3 轮。⇒ 形状必须显式说死并带**带引号的完整示例**；键清单只能当附录。
        // v28（2026-09-29 主人令）：形状改**行式键值**（去 JSON）—— 与运行时工具面、worker worker 协议区**同一口径**；
        // 旧 JSON 口径必须已消失（否则就是两套口径共存）。
        Assert.Contains("<call><name>", ProtocolText.Text, StringComparison.Ordinal);                       // v48：工具形状改 XML 元素
        Assert.Contains("verbatim, no escaping", ProtocolText.Text, StringComparison.Ordinal);            // 多行正文写在成员里
        Assert.DoesNotContain("keys are quoted JSON", ProtocolText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"key\":\"value\"}", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("key names per tool:", ProtocolText.Text, StringComparison.Ordinal);   // 附录自报是「键名」不是 JSON

        // v19（2026-09-22 16:4x）：`read` 多一个**读法** —— 按符号一次取整段（治「一个 582 行的文件被 read 3~9 次」）。
        // v31：master 侧只剩 `index`，所以这句改挂在 `index it by symbol` 上（同一读法、同一措辞）。
        Assert.Contains("symbol", face, StringComparison.Ordinal);
        Assert.Contains("index it by symbol", ProtocolText.Text, StringComparison.Ordinal);

        // 2026-10-10（主人令「协议区拔干净 worker」）：第 13 条与 [MASTER-WORKER] 共用段**已拔出**
        // ⇒ 这些 worker 记号**不应**再出现在 master 协议正文里（逐字留档见 docs/BACKUP-WORKER-PROTOCOL.md）。
        Assert.DoesNotContain("[INTENT]", ProtocolText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[MASTER-WORKER]", ProtocolText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("[DISPATCH]", ProtocolText.Text, StringComparison.Ordinal);

        // v20（2026-09-22 17:0x）：第 6 条补**负向口径** —— 三句都必须**真在正文里**（现场：假终局块把任务判无解）。
        // 2026-10-06（主人令）：第 6 条重写成「**默认续 + 停只三模式**」——旧口径（"不结束就不带终局块"/
        // "停下来问人本身就是终局"）已被替换，断言随之改为新口径。
        Assert.Contains("DEFAULT IS CONTINUE", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("exactly THREE kinds", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("the runtime never interferes", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("USER-PREFERENCES", ProtocolText.Text, StringComparison.Ordinal);
    }

    // ---------------- 文档副本闸门（PITFALLS #81 的正解：副本漂移会被闸门抓住） ----------------

    /// <summary>
    /// <c>docs/REPORT-PROTOCOL-ZONE.md</c> §三 的 <c>```text</c> 围栏 = 代码 <see cref="ProtocolText.Text"/> 的**逐字副本**。
    /// <para>PITFALLS #81：该文件曾自称「唯一真相源」，而代码注释又写「照本文件逐字」⇒ **两处互指**，
    /// 副本自 v8 起漂移而**没有任何闸门变红**。此闸门把「人肉同步」换成机器检查。</para>
    /// <para>改协议的正确顺序：只改代码 ⇒ 跑本闸门 ⇒ 按提示重打副本（副本仍是派生视图，不是真相源）。</para>
    /// </summary>
    [Fact]
    public void Gate_ProtocolDocCopy_MatchesCode()
    {
        var doc = Path.Combine(RepoRoot(), "docs", "REPORT-PROTOCOL-ZONE.md");
        Assert.True(File.Exists(doc), $"找不到协议区文档：{doc}");
        var text = File.ReadAllText(doc, Encoding.UTF8);

        const string Fence = "```text\n";
        var start = text.IndexOf(Fence, StringComparison.Ordinal);
        Assert.True(start >= 0, "协议区文档里找不到 ```text 围栏（副本的唯一落点）。");

        var end = text.IndexOf("\n```", start + Fence.Length, StringComparison.Ordinal);
        Assert.True(end > start, "```text 围栏没有闭合。");

        var copy = text[(start + Fence.Length)..end];
        if (ProtocolText.Text != copy)
        {
            // 诊断辅助：把**代码真值**落一份，便于逐字重打副本（不猜）。
            // ⚠️ **必须无 BOM**：`Encoding.UTF8` 写出时**带 UTF-8 BOM**（`EF BB BF`）⇒ 照该文件重打副本，
            // 文档里就混入一个**看不见的 U+FEFF** ⇒ 逐字比对恒红（2026-10-10 真机踩过，就是本闸门所谓的“反常”）。
            File.WriteAllText(
                Path.Combine(RepoRoot(), "docs", ".protocol-text.actual.txt"),
                ProtocolText.Text,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        Assert.Equal(ProtocolText.Text, copy);
    }

    /// <summary>
    /// <c>docs/REPORT-PROTOCOL-ZONE.md</c> §三 的**元数据**（标题里的版本号 / 「规模」行 / §四 P4 预算行）
    /// 必须与代码真值一致。
    /// <para>来历（2026-10-06）：<see cref="Gate_ProtocolDocCopy_MatchesCode"/> 只闸住 <c>```text</c> 围栏，
    /// 标题却一路停在 v32、「规模」行停在「14 行 / 9,565 英文字符 / 2392 token」、预算行停在 v25 的 ≤2,400
    /// ⇒ **副本是新的、元数据是旧的**，而没有任何闸门会喊（同族：闸门只断言自己的不变量）。</para>
    /// <para>改协议的正确顺序：只改代码 ⇒ 跑本闸门 ⇒ 按提示重打这两行（元数据仍是派生视图，不是真相源）。</para>
    /// </summary>
    [Fact]
    public void Gate_ProtocolDocMetadata_MatchesCode()
    {
        var doc = Path.Combine(RepoRoot(), "docs", "REPORT-PROTOCOL-ZONE.md");
        Assert.True(File.Exists(doc), $"找不到协议区文档：{doc}");
        var text = File.ReadAllText(doc, Encoding.UTF8);

        // 2026-10-06 主人令：元数据带**日期时间**（VersionStamp），加载时一眼确认是不是最新。
        Assert.Contains($"## 三、逐字协议文本 **{ProtocolText.VersionStamp}**", text, StringComparison.Ordinal);
        Assert.Contains(
            $"- 规模：**{ProtocolText.Lines.Count:N0} 行 / {ProtocolText.CharCount:N0} 字符 / {ProtocolText.EstimatedTokens:N0} token（{ProtocolText.VersionStamp}）**",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            $"≤{ProtocolText.DeclaredMaxLines:N0} 行 / **≤{ProtocolText.DeclaredMaxTokens:N0} token**",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>从测试目录向上找含 <c>AgentRuntime.slnx</c> 的项目根（与 <c>CorpusLockTests</c> 同一手法）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到含 AgentRuntime.slnx 的项目根。");
    }

    // ---------------- 迁移：focus.reportHint 已删除 ----------------

    [Fact]
    public void Config_FocusReportHint_IsRemoved_AndLoudlyRejected()
    {
        // 类型上不存在该配置项（也不存在 FocusOptions.ReportHint）。
        Assert.DoesNotContain(
            typeof(FocusConfiguration).GetProperties(),
            p => p.Name.Contains("reportHint", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(FocusOptions).GetProperties(),
            p => p.Name.Contains("reportHint", StringComparison.OrdinalIgnoreCase));

        // 旧配置里残留该键 ⇒ 显式拒绝（不静默忽略：留着它的人会以为它还在起作用）。
        var dir = Path.Combine(Path.GetTempPath(), "agentruntime-protocol-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.xml");
            File.WriteAllText(path, """
            <RuntimeConfiguration>
              <BaseUrl>https://example.invalid/v1</BaseUrl>
              <Focus>
                <Policy>report</Policy>
                <ReportHint>回复末尾用一行 [FOCUS] E### E###</ReportHint>
              </Focus>
              <Model>m</Model>
            </RuntimeConfiguration>
            """);

            var ex = Assert.Throws<InvalidDataException>(() => RuntimeConfiguration.Load(path));
            Assert.Contains("reportHint", ex.Message, StringComparison.Ordinal);
            Assert.Contains("协议区", ex.Message, StringComparison.Ordinal);

            // 去掉该键 ⇒ 正常加载（其余 focus 参数照旧生效）。
            File.WriteAllText(path, """
            <RuntimeConfiguration>
              <BaseUrl>https://example.invalid/v1</BaseUrl>
              <Focus>
                <MinWeight>0.95</MinWeight>
                <Policy>report</Policy>
              </Focus>
              <Model>m</Model>
            </RuntimeConfiguration>
            """);
            var config = RuntimeConfiguration.Load(path);
            Assert.Equal(0.95, config.Focus.MinWeight);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------- 新闸门：协议正文里每个「块形 token」都必须有主 ----------------

    /// <summary>
    /// **有主集合三分法**（穷举；默认拒绝）：协议正文里出现的每个 <c>[XXX]</c> 只能属于
    /// ① **模型发的块**（<see cref="ProtocolText.ReportBlockHeaders"/>：解析边界）；
    /// ② **运行时发的块**（事件标签，由 <c>SessionEventKind</c> 名派生 —— 与 <c>SessionEvent.Label</c> 同源）；
    /// ③ **元记号**（契约头 / 语法占位符，非块）。
    /// <para>来历（2026-09-21）：一次「R1 还能不能再精简」的复核中，<c>[FOCUSREPORT]</c> 被 <c>grep</c> 字面量误判为死词 ——
    /// 它其实是**运行时块头**（由枚举名拼出，源码里没有这个字面量），删掉那句 = 删掉「注入事件是你自己的自报」的
    /// **唯一澄清**。本闸门把那次量法错变成永久守卫（PITFALLS #91：<c>grep 关键字 != 路径依赖</c>）。</para>
    /// </summary>
    [Fact]
    public void Gate_Protocol_EveryBlockTokenHasAnOwner()
    {
        // 正例：现协议正文**零孤儿**。
        Assert.Empty(FindOrphanBlockTokens(ProtocolText.Text));

        // 负例：喂一个假块 ⇒ 必须被报出（证明闸门有牙，PITFALLS #40 量法假阴性）。
        Assert.Equal(["[FOOBAR]"], FindOrphanBlockTokens("hello [FOOBAR] world"));

        // 反向自检：白名单必须含运行时块头 [FOCUSREPORT]，且正文必须仍提到它 ——
        // 谁删掉「[FOCUSREPORT] = your own earlier self-report」那句，本闸门即变红。
        Assert.Contains("[FOCUSREPORT]", OwnerSet());
        Assert.Contains("[FOCUSREPORT]", ProtocolText.Text, StringComparison.Ordinal);
    }

    /// <summary>块形 token：<c>[ABC]</c> / <c>[A-B]</c> / <c>[L3]</c>（含数字与连字符，否则会漏掉 <c>[L3]</c>）。</summary>
    private static readonly System.Text.RegularExpressions.Regex BlockToken =
        new(@"\[[A-Z][A-Z0-9-]*\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>有主集合 = 模型发的块 ∪ 运行时发的块 ∪ 元记号（穷举；新增 token 必须在此显式分类）。</summary>
    private static HashSet<string> OwnerSet()
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);

        // ① 模型发的块（自报 / 终局）—— 解析边界，唯一声明处。
        foreach (var h in ProtocolText.ReportBlockHeaders) owners.Add(h);
        // v33：worker 侧回执块头（worker 协议区 v17）—— 正文提到它只为解释回执形状，它不是 master 的自报块。
        owners.Add("[RESULT]");
        owners.Add("[MASTER-WORKER]");   // v35：共用协议段（两边逐字同一份）
        owners.Add("[DUTY]");            // v37：形状/实质分工节（共用段的一节，不是模型发的块）
        owners.Add("[SPLIT]");
        owners.Add("[BASE]");
        owners.Add("[CHAIN]");
        owners.Add("[WINDOW]");          // v43：窗口规则（读源/写目标）
        owners.Add("[RULING]");         // v46：临时回执（裁决请求；worker 发的块）
        owners.Add("[ENVELOPE]");       // v47：消息信封（两侧都发）
        owners.Add("[RULING-ANSWER]");  // v47：裁定回信（master 发）
        owners.Add("[ABORT]");          // v47：作废一次在飞派发（master 发）
        owners.Add("[FEEDBACK]");
        owners.Add("[PACE]");   // v54：节拍段（共用段的一节；运行时按它掐表停 worker）        // v43：反馈两类的**唯一声明表**渲染出来的那一节          // v42：默认续 + 停三模式（共用段的一节）             // v41：按需核实路径基准（共用段的一节，不是模型发的块头语法）           // v38：派发物段结构 / 工具面边界 / 路径基准 / 证据包（共用段的一节）

        // ② 运行时发的块 —— 与 SessionEvent.Label 同源（`[` + 枚举名大写 + `]`），**不手写字符串**。
        foreach (var name in Enum.GetNames<AgentRuntime.Core.Stream.SessionEventKind>())
            owners.Add($"[{name.ToUpperInvariant()}]");

        // ③ 元记号：不是块，是契约头与语法占位符（显式列出 ⇒ 新记号必须表态，fail-closed）。
        owners.Add("[PROTOCOL]");   // 契约头（第一行）
        owners.Add("[KIND]");       // 事件行语法示例里的占位符，非真块

        return owners;
    }

    // ---------------- 新闸门：正文字节变了，版本号必须跟着变 ----------------

    /// <summary>
    /// **正文指纹 × 版本号**（2026-09-24 夜新增）。
    /// <para>来历：r958 把第 12 条补了 <c>body:</c> 成品正文位，**却没升版本号** ——
    /// 提交信息、代码注释、水位三处都写「v22」，而 <see cref="ProtocolText.Version"/> 从 r955 到 r963
    /// 一路是 <c>"21"</c>。后果不是崩溃，是**账本撒谎**：屏上版本、文档标题、代码常量三处互斥，
    /// 而且**没有任何闸门会喊**（已有一条副本闸门，但它只盯「文档 == 代码」，不盯「版本 == 正文」）。</para>
    /// <para>判据：<see cref="ProtocolText.Text"/> 的 sha256 必须与**它自己声明的版本号**对得上。
    /// 改正文不登记新版本 ⇒ 指纹对不上、红；只升版本不登记 ⇒ 表里没这个版本、红。</para>
    /// </summary>
    [Fact]
    public void Gate_Protocol_VersionTracksTextBytes()
    {
        var actual = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(ProtocolText.Text))).ToLowerInvariant();

        Assert.True(
            KnownTextFingerprints.TryGetValue(ProtocolText.Version, out var expected),
            $"协议正文声明的版本 \"{ProtocolText.Version}\" 不在指纹表里 —— 改了正文就必须：" +
            "① 升 ProtocolText.Version；② 把新指纹登记进 KnownTextFingerprints（见本测试的来历）。");

        Assert.True(
            string.Equals(expected, actual, StringComparison.Ordinal),
            $"主协议正文指纹不符：登记 v{ProtocolText.Version} = {expected}；实测 = {actual}");
    }

    [Fact]
    public void Gate_协议示例只用本面工具_不得演示worker专属工具()
    {
        // 2026-10-09 真机：第 5 条的**形状示例**用了 `<name>exec</name>`（worker 面的工具）⇒ master 照抄 exec ⇒ 被拒。
        // 示例就是教形状 ⇒ 它只能演示**本角色面里**的工具（§十·63：示例与真形状必须一致）。
        Assert.DoesNotContain("<name>exec</name>", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("<name>index</name>", ProtocolText.Text, StringComparison.Ordinal);
    }

    /// <summary>版本 → 正文 sha256（**改正文必须登记**；见 <see cref="Gate_Protocol_VersionTracksTextBytes"/>）。</summary>
    private static readonly Dictionary<string, string> KnownTextFingerprints = new(StringComparer.Ordinal)
    {
        // v21 及更早的指纹没留（r958 之前的正文已不可追证）；从 v22 起逐版登记。
        ["22"] = "5eb891b39e0d7f5c3b60929c515fcb8d193266e74d90e742552ec57a1b916e3d",
        ["23"] = "8d7be6a55d4418cfca7801b264135466a183e553bf58d79bc29d987a4148c39c",
        ["24"] = "fc2b83339a504b6d4e96caf42028190740201231ccf1133e9e406ef8224015c7",
        ["25"] = "4fae937d72792885970f1c407b39fbcbc9d8aa10569e322184584bbc05774042",
        ["26"] = "c0cdfffca5baed3250966b475c5077a21a18ca69f65aacf3c7bfa02b2db05f02",
        ["27"] = "a64c50542b347536e4ad11bc0a528695f84ace097999be677eefeadadb449558",
        // v28（2026-09-29）：第 5 条工具面形状改**行式键值**（去 JSON —— 与运行时工具面 / worker worker 协议区同一口径）。
        ["28"] = "62ffd659d96c0f72ba4bfa8c74620c3d8009fa9120a0270391918a5667d4d9f2",
        // v29（2026-09-29）：第 13 条派发物新增 `must:`（应有集合）—— 运行时按它核 worker 的 `covered:`。
        ["29"] = "c333763ad5c3f944646cb113c41f894c32d04742cc1e2e587c63a021c0c0e62a",
        // v30（2026-09-29）：分工口径 —— 第 2/13 条把「读原料 / doing」整体划给 Worker，删「快看一眼归你」。
        ["30"] = "da4adbc4223fddfc88fe05edaba2bd119d7befef59af17e05d7502a7d58f78b0",
        // v31（2026-09-29）：第 5 条键清单**按角色渲染**（master 面只剩 index/scope，由 ToolFaceClause.MasterKeys 渲染）；
        // 第 13 条补 `ask:` + 回执读数（answer=/evidence=/changes=/open=/covered=）+ 「地址一律绝对」。
        ["31"] = "ed4fecc47013164e302ce858daa70d46e3fec042163fdd7f11f2c34a3f7baa0c",
        ["32"] = "f7e48c005db67f335b054ee878dddfc465cd63ef57b1c6a60f45110ba6c10385",
        // v33（2026-09-30）：第 13 条改「目的句 + 模型自选枚举组合」（8 类）+ 回执按 kind 取字段；预算 2400 → 2800。
        ["33"] = "371d7b6f033fdf74837c1f5da5fc000073075caea66786e504dd0f68cf3a7d21",
        // v34（2026-09-30 16:47 主人口径「运行时只按纯净规则走，不兜底」）：第 13 条写死——
        // 回执**必须给出该组合的交付键**（缺 ⇒ 不算 done、退回），且没有"不需要"这一档（键不适用 = 选错了类或 criteria）；
        // 形状：一行一键，键与分隔符之间可有空白。
        ["34"] = "81b7ccada7b00d904ee71134084b51740080b0c10c7a12dd31fa062c494cc5a5",
        // v35（2026-09-30 16:58 主人令）：**master 与 worker 具有同一份** master-worker 模式协议段 ——
        // 第 13 条的内联清单删掉，改由 MasterWorkerProtocol 渲染、两侧各自逐字嵌入。
        // ⚠️ v35 的正文**未登记**（当日直接从 v34 改到 v35 再改到 v36，漏登记；v35 正文亦已不可追证，
        //    同 v21 及更早的口径）—— 账面上这**是一处已知缺口**，不是「v35 = v36」。
        ["36"] = "c710854a93933f1391dda4aeae1551d43ec140491c161cacabd151b7bd69bb2d",
        // v37（2026-10-06 主人令「运行时绝不担保/兜底实质层，只管形状层」）：共用段新增 [DUTY] 节
        // （实质由 Worker 自证 + master 最小代价复算证据 + 运行时只管形状层）；行预算 70→75。
        ["43"] = "beb37e3e264262a7ae641ee5ea83074c3ac5fa982bc56253f5d8516ef0cb721d",
        // v44（2026-10-07 15:15）：共用段新增**回执组合的轴**（照派发的 modifiers 一一对账）。
        ["44"] = "708ada5a033d02b007e313e49ba3faea65c4590a81786546fc4e5340b14b9e31",
        // v45（2026-10-07 15:30 审计）：[CHAIN] 补 **worker 的三种停**（done/impossible/need_human；其余继续）+ [SPLIT] 点明 **worker 的决策报告 = 回执的机器形态**。
        ["45"] = "a3b06f0959913566c00b455a03802202e2cf5340f2a3dee2947acd8f4a7c5e51",
        // v56（2026-10-09 13:10）：[PACE] 段改「按次派发闸门」（一次派发 ≠ 一次生成）+ master 可直接 `[PACE] gate=<秒>` 设定。
        ["56"] = "d9c2c2e91d85141f8f9b419cda6ebcc03cac773019361400e1ecb2bb824b04c4",
        // v57（2026-10-09 19:2x）：第 5 条**形状示例改用 master 面工具**（exec → index）—— 示例是 worker 形状会让 master 照抄 exec。
        ["57"] = "4deec71ed316f6cd244265cfebbd70ae4213f3141bf7399cd7355eb8b21018b8",
        // v58（2026-10-09 22:3x）：第 5 条补一句 **[NOTE] 块**（把一段话落进当日 memory，运行时确定性写盘，不经派发链）；`[NOTE]` 进分节块头清单。
        ["58"] = "5c4e2ada0fa2a63825801001e98ae02c63054253f060234c6cdac009c6d707b1",
        // v59（2026-10-10 13:40 · 待办 1）：master 协议口径统一 ——「L1/L2/L3 only」→ **L1/L2/L3/L4**；
        // L3 = 分类总目+路由（`S-...`）/ L4 = 原 SKILL 逐字（`L4-...`）；第 5 条 `[L3]` 示例改「分类 id / 技能 id」。
        ["59"] = "1a6ec5861853ff462000b4799ed8bbbbf1eeb863ee7127b5dc0a8f1013a2e2d4",
        // v60（2026-10-10 13:55 · 待办 3b）：新增第 14 条 —— 按体量排工（零散先做 / 大的先切再发 / `[PACE]` 事前建议 = 指令）。
        ["60"] = "2a3e146066e8013c2122bbcda02349ddc270ba5f9fa46057573e006dcd66ca46",
        // v61（2026-10-10 19:55 · 主人令）：**拔掉 worker** —— 删第 13 条、去第 2/5 条的 worker 句、不再拼 [MASTER-WORKER] 共用段；
        // 逐字留档见 docs/BACKUP-WORKER-PROTOCOL.md。协议正文 25,655 → 8,288 字符。
        ["61"] = "9084e2083bd68616b844ccaf397f2d844c18333662b3e2f00b952a21588e8b34",
        // v62（2026-10-11 17:44）：A = 允许多块/独立取证一次发齐（"one action per block"，明说可 4 块同回复）；
        // B = 不再每轮重复 [TAIL]/[DRAFT]（只在决策报告那一轮写一次）；正文 8,288 → 8,736 字符。
        ["62"] = "7fe45e540a6b24039d186023a4ecfd75fe1e42e7ac5bd9bf1d86654e6ac41d50",
        // v46（2026-10-07 15:50）：共用段新增 **[RULING] 临时回执**（裁决请求：6 类封闭枚举）；预算 5200 → 5800。
        ["46"] = "48b087feab34ed1b7329a2885ced0ef47f5c025a38940a1726ec8525746ef776",
        // v47（2026-10-07 16:05）：共用段新增 **信封**（身份/时刻/id/re/模式）· **裁定回信**（[RULING-ANSWER]）· **作废**（[ABORT]）；行预算 95 → 100。
        ["47"] = "93e4214df0e2dd927bbed055cf9c6b4d76b4b5749b7c7b618db54cf70c890837",
        // v48（2026-10-07 16:50）：**四面换 XML** —— 工具/派发/回执/裁定/回复的形状改元素；ToolFaceClause.Shape 同步。
        ["48"] = "1bed5277e36ba9aae882686c762b5a3c3132fd87b291d74c042bfb4441398ce2",
        // v53（2026-10-08 20:45）：[MASTER-WORKER] 共用段的 side_effects 改由 DispatchIntent.DefaultSideEffects **渲染**
        // （它的唯一合法值按 kind 固定）—— 真机：master 给 EXEC 写 none ⇒ 派发被退回、白走一轮（条目 2 / 条目 7）。
        ["53"] = "8d7ff49ac00ea6b2115a18fca2dfa1d1036d3c652a3ab31cb48ff3071511ae5d",
        // v54（2026-10-09）：共用段新增 **[PACE] 节拍段**（上限 = 6300/v 夹在 [100,300]s；<15 tok/s 不拆、问人）。
        ["54"] = "c4119968e25af5640fc39c518142e97a054b7192f9cd4af288df97cc2314c2b0",
        // v55（2026-10-09）：[PACE] 段补 **master 可直接设上限**（`[PACE] ceiling=300|600|off`，白名单外响亮拒绝）。
        ["55"] = "58688fdf80071ae9843bac5f35f9a5f0259088a066274e22833bba9821359336",
        // v49（2026-10-07 17:15）：交付键**元素化**（members/delivers 用 <key>）+「NEVER inside <answer>」；示范同步。
        ["49"] = "4e130b26c2339050436a6fed905bf23b1c0f9aa0e435107f4a38ee1b26b580f5",
        // v50（2026-10-07 17:50）：[FEEDBACK] 补「同一个格式缺口在同一 task 内重复 ⇒ 升级为实错（记坑）」。
        ["50"] = "78020ca842cfb17a2b91bdff9d9391aed908e0617c41da83417327d2ffe41448",
        // v51（2026-10-07 19:30）：**第 13 条里的两处旧形一并改 XML**（派发形状 + 回执形状）——
        // 此前它俩与共用段的 XML 形状并存 ⇒ 模型在两种形状间摇摆（PITFALLS #281）。
        ["51"] = "22499bb55ef8a8b7e53d726c14da4c6816efe6ce1d4a4b97ec7190831d4b3e56",
        // v52（2026-10-07 22:20）：共用段 [DUTY] 补**读数时效** —— 读数是本轮那一次运行的属性，不许被记忆／过往回执覆盖。
        ["52"] = "c2ea4e94b476903a80bca8d8839e60d93454e45134db15db4c41d36adc18e4fd",
        ["63"] = "4ea625efbdd565447d80310f141f1d82843869368a8236e8b800a978bad05cc5",
        // v64（2026-10-14 22:05 · 主人令「过程中每一轮都刷」）：第 5 条**回转** v62-B ——
        // `[TAIL]`/`[DRAFT]` 由「只在决策报告轮写一次」改为**每条回复都刷新当前工作状态**
        // （台面 R4/R5 随每个任务生命周期动态刷新；排在事件流 R2 之后 ⇒ 不吃前缀缓存）。
        ["64"] = "32129650f238c59161d5a82b3ec9a161ffc620da0db124e0599bce749ba3f4cc",
        // v65（2026-10-14 23:50 · 主人令「b 也要做」）：第 5 条补**长正文纪律** —— 长值（整篇文档 / 脚本 /
        //   带引号的命令）用 `key: <<` 块逐字写，**禁塞进 `{...}`**（那是解析器唯一救不回来的形状）。
        ["65"] = "48cd6ef1f015a028ef5187f7a89f24b56f3f55a99f8bb3e8e6d722e4ce1d0cef",
        // v66（2026-10-15 00:25 · 主人令「不要等到任务终局再提」）：第 7 条 task closeout 话术改写 ——
        //   坑 / handoff **遇到就在当轮说**（不攒到终局）；预算 2,300 → 2,400（先量后定）。
        ["66"] = "318a29b1c4f0a4a465401f13196c44e3a487226e445600a9b43d8e620895916b",
        // v67（2026-10-15 01:35 · 主人令「改协议」）：第 5 条回转 v64 —— [TAIL]/[DRAFT] **只在真变了时才写**
        //   （没变就一个块都不写；台面不再被每轮重写）。
        ["67"] = "28a4fd3d3de09d4b20c05e84c9d3e7eec2a73b2390fb99a8e810ae5ec835e38a",
        // v68（2026-10-15 13:2x · 主人令「用协议区的方式把这个改好」）：第 5 条**去旧形状尾巴** ——
        //       删 `{...}` / `key: <<` 那半句（已退役的 JSON/行式），改成「长正文放进元素里、或写文件传路径」。
        //       来历：真机模型照旧先验写 `[TOOL] read {"path": /abs}`（值没引号）⇒ 整篇拒；
        //       而协议区自己还留着 `{...}` 字样 + 拒因话术教「行式键值」⇒ **三处教三种形状**。
        ["68"] = "76c92c26c02a9153159896af4a080c86798e873a49bd84ace2c46c87d20d2501",
        ["69"] = "139768694887e7b3cd94a3a1147345a099dd6245d2d988416b8365e18e23b0b2",
        ["70"] = "e486b0856b530726c5f9e2ce09aa8b26a2829c57860d3d93a00d0b400be0c938",
        ["71"] = "6f32a55802b4afc9d75ad38ca2f36e83b43ca19a2204d68866a3928c2a152975",
        // v72（2026-10-17 15:52 · 主人令：80% 是唯一停手点，不必等任务终局；收尾后 reset→start 接着做同一任务）。
        ["72"] = "a3d8d266abadbcc6083281c2b21fc524f80b815f4e4fb08cbaf7a06589c6df51",
        // v73（2026-10-17 16:10 · 主人令：决策库与项目级设计原则书都走协议区认知，但正文是**活版**、按需读、绑项目）。
        ["73"] = "c96ee0db2121f1b6f4fde4d4a3a084719a88b3e6679d1f5fbf506d82e7cdcd29",
        ["74"] = "618f10c88fa558435157321665be94269fbc801e23f85f1d3a8ae809e77766b0",
        // v75（2026-10-18）：工具面新增 5 个网络工具（web_fetch / web_search / http_request / web_download / web_search_extract）。
        ["75"] = "a790a912fdc06331f3b1c79cda7fffb8ceef4c5603ab158f9e3ce5d8440a8c91",
        // v76（2026-10-18）：第 11 条补 build 纪律（批量改完再 build / 只 build 变更项目 / 最后全量验证一次）。
        ["76"] = "ca34696c8a07c1883a053c91dd1bbf478bc954be64f171b9bab4b94eeb9bcd47",
        // v77（2026-10-18）：第 16 条排障纪律（同一错误两次即停 / E160006 换全新本地 WC / 旧 TAIL/DRAFT 不自证）。
        ["77"] = "6f4455588491ad304b76f358ba6b733d5aa9165cc26c0fcf2aa710cf49c36cea",
        ["78"] = "9987f1745ee809a7cd1ddd1129a5ba3f6122e929b710f759a6cbcd221c1e60ad",
        ["79"] = "940917af008a553741be7902943b53deba375bca8d5203f3b0e4925f7206871d",
        // v80（2026-10-18）：第 17 条「每轮都以 [FOCUS] 结尾（无内容写 none）」+ 第 11 条「工具路径一律绝对」
        ["80"] = "bfc585d04aa028b00b7e2c15d5d48a5505f9ae8eafbb89b0a6f1921308e5b166",
        // v81（2026-10-18）：第 17 条措辞改成「本轮依据的事件」（否则模型一律写 none ⇒ 机制触发但无导航价值）
        ["81"] = "134fc89f66ad57d6dbcc0f86b73dba23cd726cb55a3d311e0b6dfe0391dae8d4",
        // v82（2026-10-19）：**正文未改**，只升版本号 —— 归一兜底（DSML 残形 / 重复 </call>）与焦点自动补位
        //   都是**运行时行为**，不占协议字节 ⇒ 正文指纹与 v81 逐字节相同（这是有意的：字节身份不与行为版本混同）。
        ["82"] = "134fc89f66ad57d6dbcc0f86b73dba23cd726cb55a3d311e0b6dfe0391dae8d4",
        // v83（2026-10-19 14:0x · 主人令）：新增第 18 条「XML 是唯一工具面；provider 不传 tools/functions」。
        ["83"] = "793abf17e5a21ed99064adac9e5dc982fdc817d3f9d9d87ce0541289bf529e09",
        // v84（2026-10-19 14:3x · 主人令「再次修改协议区补完这个事」）：第 12 条改为**终局块在前、报告紧随其后、同一轮**。
        ["84"] = "3b00e86d11132a806e3202b7ab771758ed91e490e3b68615169666219055fdcf",
        ["85"] = "c62c18af5ddeb6637f1e96ff9a5ea715dce0f262b513b4118e375403f6fdfdd6",
        // v86（2026-10-20 22:30 · 主人令）：新增第 20 条「收尾回执 + 两库生长硬闸门」。
        ["86"] = "46c7e48db5db9f5b57bb386e76e61e85a567456da99407c5fbba6148df4a8588",
    };

    /// <summary>找出「无主」的块形 token（去重、排序，便于断言）。</summary>
    private static string[] FindOrphanBlockTokens(string text)
    {
        var owners = OwnerSet();
        return BlockToken.Matches(text)
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .Where(t => !owners.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();
    }
}
