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
        Frozen = new FrozenConfiguration { Domains = domains ?? [] },
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
        //       实测 **6,792 字符 ⇒ 1698 token**、13 行 ⇒ 按「先量后定」重定预算为 **≤13 行 / ≤1,900 token**（留 ~10% 余量）。
        Assert.Equal(1698, ProtocolText.EstimatedTokens);
        Assert.Equal(1900, ProtocolText.DeclaredMaxTokens);
        Assert.True(ProtocolText.EstimatedTokens <= ProtocolText.DeclaredMaxTokens);
        Assert.True(ProtocolText.MaxTokens >= ProtocolText.DeclaredMaxTokens);
        Assert.Equal(1900, ProtocolText.MaxTokens);
        Assert.Equal(13, ProtocolText.DeclaredMaxLines);
        Assert.True(ProtocolText.Lines.Count <= ProtocolText.DeclaredMaxLines);

        // v5：协议区扩容（L1/L2/L3 + 铁则 + memory-index + 各区契约）后 [DRAFT] 段仍在协议区（唯一声明处），
        // 且上限与读序都与闸门对得上；版本号升 "5"。
        // v6：补 [L3] 自报块（自报条里声明），版本号升 "6"，且它进了分节块头清单（PITFALLS #30）。
        // v7：补 [TOOL] 块；v8：去 SKILL 口径 + 块名 [SKILL]→[L3] + 补「怎么请求能力」，版本号升 "8"。
        // v20（2026-09-22 17:0x）：第 6 条补负向口径（没结束不发终局块；[NO-SOLUTION] 只表示已证此路不通），版本号升 "20"。
        // v21（2026-09-24 19:4x）：新增第 12 条**决策报告**（[REPORT] 块，块 ≤30 行，必交、缺失即「未提交」），版本号升 "21"。
        // v22（2026-09-24 夜）：第 12 条补 `body:` 成品正文位，版本号升 "22"（r958 只改正文漏升版本 ⇒ 本轮补齐 + 加闸门）。
        Assert.Equal("22", ProtocolText.Version);
        Assert.Contains(ProtocolText.DraftPrefix, ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains($"<={ProtocolText.DraftMaxLines} lines", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains($"<={ProtocolText.TailMaxLines} lines", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Equal(16, ProtocolText.DraftMaxLines);
        Assert.Equal(3000, ProtocolText.DraftMaxChars);

        // v6 的实质：协议里写得出 [L3] 块（且运行时真的兑现 —— 见 SkillLoadingTests）。
        Assert.Equal("[L3]", ProtocolText.L3Prefix);
        Assert.Contains(ProtocolText.L3Prefix, ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains(ProtocolText.L3Prefix, ProtocolText.ReportBlockHeaders);

        // v7 的实质：协议里写得出 [TOOL] 块（一次回复只允许一次；且运行时真的兑现 —— 见 ToolFaceTests）。
        Assert.Equal("[TOOL]", ProtocolText.ToolPrefix);
        Assert.Contains(ProtocolText.ToolPrefix, ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains(ProtocolText.ToolPrefix, ProtocolText.ReportBlockHeaders);

        // v12 的实质：协议的 [TOOL] 那一句要求带 risk: 声明（判定权归属升级 —— 模型自判、硬红线仍拦、假声明记录在案）。
        // 协议说的 = 代码要解析的：此处钉住字面量，解析侧钉在 ToolFaceTests/ApprovalV12Tests。
        Assert.Contains("risk: none", ProtocolText.Text, StringComparison.Ordinal);
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
        Assert.Contains("20% of the window", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Equal(0.20, ContextBudget.ProposeRatio);
        Assert.Contains("closeout", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("reset", ProtocolText.Text, StringComparison.Ordinal);

        // v21 的实质：协议正文里写得出 [REPORT]（且运行时真的能收 —— 见 DecisionReportTests）。
        Assert.Equal("[REPORT]", ProtocolText.ReportPrefix);
        Assert.Contains(ProtocolText.ReportPrefix, ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains(ProtocolText.ReportPrefix, ProtocolText.ReportBlockHeaders);
        Assert.Contains($"<={ProtocolText.ReportMaxLines} lines", ProtocolText.Text, StringComparison.Ordinal);

        Assert.Equal(9, ProtocolText.ReportBlockHeaders.Length);   // [FOCUS] / [TAIL] / [DRAFT] / [L3] / [TOOL] / [REPORT] / [DONE] / [NO-SOLUTION] / [NEED-USER]
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
        Assert.Equal("22", ProtocolText.Version);

        // v6+ 的 skill 配置只允许「**路径**」与「**内容选择**」：
        //   Repo/Resident = 路径（地址表 / 常驻层目录）；ResidentDomains = 常驻层的域过滤。
        // 不得出现行数 / 上限 / **协议开关**这些「用户改协议」的口子。
        // ⚠️ 白名单是**穷举**的：新增属性必须改这里（改不动的默认拒绝），否则这个闸门就漏了。
        // 为什么域过滤不算口子：它与 FrozenConfiguration.Domains 同族 —— 选的是**装哪些内容**，不是改协议。
        var skillProperties = typeof(SkillConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Repo", "Resident", "ResidentDomains"], skillProperties);
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
            "read{path,symbol,maxLines,offset,limit} list{path} write{path,content} edit{path,oldText,newText} exec{command,timeoutSeconds}",
            face);
        Assert.Contains(face, ProtocolText.Text, StringComparison.Ordinal);

        // v18 的实质（主人 2026-09-22 04:0x 令）：**键清单不能替代形状** —— v16 只教了 `exec{command,timeoutSeconds}`
        // 这种紧凑形状，模型照抄它写成 `{command: "…", timeoutSeconds: 60}`（键没引号 ⇒ 非法 JSON ⇒ 被拒），
        // 真机一场 11 轮里白烧 3 轮。⇒ 形状必须显式说死并带**带引号的完整示例**；键清单只能当附录。
        Assert.Contains("shape: [TOOL] name {\"key\":\"value\"} risk: none", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("keys are quoted JSON", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("key names per tool:", ProtocolText.Text, StringComparison.Ordinal);   // 附录自报是「键名」不是 JSON

        // v19（2026-09-22 16:4x）：`read` 多一个**读法** —— 按符号一次取整段（治「一个 582 行的文件被 read 3~9 次」）。
        // 协议里必须同时出现：① 键清单里有 symbol；② 教会它「按名字取整段」这句（否则加了键也白加）。
        Assert.Contains("symbol", face, StringComparison.Ordinal);
        Assert.Contains("read it by symbol", ProtocolText.Text, StringComparison.Ordinal);

        // v20（2026-09-22 17:0x）：第 6 条补**负向口径** —— 三句都必须**真在正文里**（现场：假终局块把任务判无解）。
        Assert.Contains("A task that is NOT ending carries no terminal block at all", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("never \"not finished this round\"", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("a reply that still asks for tools has not ended", ProtocolText.Text, StringComparison.Ordinal);
        // ② 21:5x：「停下来问人本身就是终局」——问句不发块 ⇒ 卡永停 ● 进行中（坑 #120）。
        Assert.Contains("Stopping to ask the user something", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("a question in plain prose leaves the task hanging", ProtocolText.Text, StringComparison.Ordinal);
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
        Assert.Equal(ProtocolText.Text, copy);
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
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, """
            {
              "baseUrl": "https://example.invalid/v1",
              "model": "m",
              "focus": { "policy": "report", "reportHint": "回复末尾用一行 [FOCUS] E### E###" }
            }
            """);

            var ex = Assert.Throws<InvalidDataException>(() => RuntimeConfiguration.Load(path));
            Assert.Contains("reportHint", ex.Message, StringComparison.Ordinal);
            Assert.Contains("协议区", ex.Message, StringComparison.Ordinal);

            // 去掉该键 ⇒ 正常加载（其余 focus 参数照旧生效）。
            File.WriteAllText(path, """
            {
              "baseUrl": "https://example.invalid/v1",
              "model": "m",
              "focus": { "policy": "report", "minWeight": 0.95 }
            }
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

        Assert.Equal(expected, actual);
    }

    /// <summary>版本 → 正文 sha256（**改正文必须登记**；见 <see cref="Gate_Protocol_VersionTracksTextBytes"/>）。</summary>
    private static readonly Dictionary<string, string> KnownTextFingerprints = new(StringComparer.Ordinal)
    {
        // v21 及更早的指纹没留（r958 之前的正文已不可追证）；从 v22 起逐版登记。
        ["22"] = "5eb891b39e0d7f5c3b60929c515fcb8d193266e74d90e742552ec57a1b916e3d",
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
