using System.Reflection;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Modules;

namespace AgentRuntime.Tests;

/// <summary>
/// **协议区（R1-P）闸门测试** —— P1~P4 四条不变量，全部做成可执行闸门（不靠纪律）：
/// <list type="number">
/// <item><b>P1 来源只能是代码</b>：<c>RuntimeConfiguration</c> 不得出现协议区路径 / 开关；<c>--domains</c> 不作用于协议区。</item>
/// <item><b>P2 收尾不可改</b>：晋升白名单显式排除 <c>FrozenZone.Protocol</c>，尝试写入 ⇒ 抛错（不静默跳过）。</item>
/// <item><b>P3 用户不可摘</b>：<c>modules</c> / <c>--modules</c>（空串 = 裸聊）都摘不掉协议模块 ⇒ 报错。</item>
/// <item><b>P4 预算</b>：行数 ≤ 8 · token ≤ 280（规定口径；主人 2026-09-15 23:4x 定，协议区扩容 / <c>docs/DESIGN-SKILL-LAYERS.md</c> §十，协议 v4 → v5；**协议 v6 先重算再定预算：实测 277 token ⇒ 预算不变**）/ 必须 Rank 0 ⇒ 超限测试失败。</item>
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
        Assert.Equal(378, ProtocolText.EstimatedTokens);
        Assert.Equal(400, ProtocolText.DeclaredMaxTokens);
        Assert.True(ProtocolText.EstimatedTokens <= ProtocolText.DeclaredMaxTokens);
        Assert.True(ProtocolText.MaxTokens >= ProtocolText.DeclaredMaxTokens);
        Assert.Equal(400, ProtocolText.MaxTokens);

        // v5：协议区扩容（L1/L2/L3 + 铁则 + memory-index + 各区契约）后 [DRAFT] 段仍在协议区（唯一声明处），
        // 且上限与读序都与闸门对得上；版本号升 "5"。
        // v6：补 [L3] 自报块（自报条里声明），版本号升 "6"，且它进了分节块头清单（PITFALLS #30）。
        // v7：补 [TOOL] 块；v8：去 SKILL 口径 + 块名 [SKILL]→[L3] + 补「怎么请求能力」，版本号升 "8"。
        Assert.Equal("8", ProtocolText.Version);
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
        Assert.Equal(5, ProtocolText.ReportBlockHeaders.Length);   // [FOCUS] / [TAIL] / [DRAFT] / [L3] / [TOOL]
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
        Assert.Equal("8", ProtocolText.Version);

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
}
