using AgentRuntime.Core.Frozen;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **内置收尾**（用户 2026-10-11 定稿形态）：原料（memory/踩坑集/handoff，水位线之后）→ **一次问法** →
/// 运行时写**两个落点**（`KNOWLEDGE.md` + 铁则区 `AGENTS.md`）→ **水位线由运行时提高**。
/// </summary>
public sealed class CloseoutJobTests
{
    private static string TempWs()
    {
        var ws = Path.Combine(Path.GetTempPath(), "wb-co-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(ws, "memory"));
        Directory.CreateDirectory(Path.Combine(ws, "handoff"));
        return ws;
    }

    private static (string Ws, CloseoutContext Ctx, string[] Roots) Scene(string watermark = "2026-10-10")
    {
        var ws = TempWs();
        File.WriteAllText(Path.Combine(ws, "memory", "收尾水位.md"),
            "- **当前水位**：**" + watermark + "**\n\n## 水位（每次收尾后更新）\n- 收敛点：" + watermark);
        File.WriteAllText(Path.Combine(ws, "memory", "2026-10-11.md"), "new-daily（含：用户加了一条铁则）");
        File.WriteAllText(Path.Combine(ws, "handoff", "55-co-2026-10-11.md"), "handoff");
        File.WriteAllText(Path.Combine(ws, "KNOWLEDGE.md"), "old");
        File.WriteAllText(Path.Combine(ws, "AGENTS.md"),
            "# AGENTS\n\n## ⚠️ 铁则 1 · 甲\n正文甲\n\n## ⚠️ 铁则 2 · 乙\n正文乙\n");
        var ctx = new CloseoutContext(ws, Path.Combine(ws, "docs", "PITFALLS.md"), DateTimeOffset.Now,
            Path.Combine(ws, "memory", "收尾水位.md"), "## 水位");
        return (ws, ctx, [ws]);
    }

    [Fact]
    public void 原料_只取水位线之后的新内容_三者同类()
    {
        var (ws, ctx, _) = Scene();
        var material = CloseoutJob.Gather([new MemorySource(ws), new HandoffSource(ws)], ctx);

        Assert.All(material, s => Assert.True(s.Watermark == "2026-10-10"));
        var memory = material.Single(s => s.Id == "memory");
        Assert.Equal(["2026-10-11.md"], memory.Pending);              // 水位线之后的才有
        var handoff = material.Single(s => s.Id == "handoff");
        Assert.Contains("55-co-2026-10-11.md", handoff.Pending);
    }

    [Fact]
    public async Task 材料被截断_绝不推进水位_否则假干净()
    {
        // 2026-10-11 用户令：「截断时不推进水位比较保险」。上限之外的部分会被静默跳过 ⇒ 推进 = 假干净。
        var (ws, _, roots) = Scene();
        for (var d = 1; d <= 45; d++)
        {
            File.WriteAllText(Path.Combine(ws, "memory", $"2026-11-{d:00}.md"), "x");
        }

        var wl = Path.Combine(ws, "memory", "收尾水位.md");
        var ctx = new CloseoutContext(ws, Path.Combine(ws, "docs", "PITFALLS.md"), DateTimeOffset.Now, wl, "## 水位");
        var run = await CloseoutJob.RunAsync(
            FakeModelClient.Returning("[CLOSEOUT] knowledge\nL1 · x\n[/CLOSEOUT]"),
            [new MemorySource(ws)], [new RulesSink(ws)], ctx, roots);

        Assert.Contains(run.Lines, l => l.Contains("截断") && l.Contains("不推进水位线"));
        Assert.DoesNotContain("知识水位（运行时写）", File.ReadAllText(wl), StringComparison.Ordinal);   // 水位**没动**
    }

    [Fact]
    public void 水位读不出_不静默跳过_而是全取_fail_closed()
    {
        // 2026-10-11 用户令「确保真收尾」：旧口径是「读不出 ⇒ 空 ⇒ 没有新材料 ⇒ 不调模型」，
        // 再自己写一条「已收敛至今天」⇒ **永久假干净**。判据：读不出 ⇒ **全取**，绝不静默跳过。
        var (ws, _, _) = Scene();
        File.WriteAllText(Path.Combine(ws, "memory", "收尾水位.md"),
            "## 水位（每次收尾后更新）\n- 收敛点：无日期行\n");
        var ctx = new CloseoutContext(ws, Path.Combine(ws, "docs", "PITFALLS.md"), DateTimeOffset.Now,
            Path.Combine(ws, "memory", "收尾水位.md"), "## 水位");

        var slice = new MemorySource(ws).Gather(ctx);

        Assert.Equal("（读不出）", slice.Watermark);
        Assert.Equal(["2026-10-11.md"], slice.Pending);   // 全取，不是空
    }

    [Fact]
    public void 问法_两问俱在_且材料只读给出()
    {
        var (ws, ctx, _) = Scene();
        var ask = CloseoutJob.BuildAsk(
            CloseoutJob.Gather([new MemorySource(ws)], ctx),
            [new RulesSink(ws), new RulesSink(ws)]);

        Assert.Contains("铁则要新增或改动", ask, StringComparison.Ordinal);
        Assert.Contains("知识树不在这里生长", ask, StringComparison.Ordinal);
        Assert.Contains(CloseoutJob.WriteHeader + " rules", ask, StringComparison.Ordinal);
        Assert.Contains("不要回写材料", ask, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 一次做完_一次调用_运行时常驻写两落点_水位由运行时提()
    {
        var (ws, ctx, roots) = Scene();
        var reply = "[CLOSEOUT] rules\n## ⚠️ 铁则 3 · 丙\n正文丙\n[/CLOSEOUT]";
        var client = FakeModelClient.Returning(reply);

        var run = await CloseoutJob.RunAsync(
            client,
            [new MemorySource(ws), new HandoffSource(ws)],
            [new RulesSink(ws), new RulesSink(ws)],
            ctx, roots);

        Assert.Equal(1, run.ModelCalls);                       // 判据①：一次调用
        Assert.Equal(1, client.CallCount);
        var agents = File.ReadAllText(Path.Combine(ws, "AGENTS.md"));
        Assert.Contains("铁则 3 · 丙", agents, StringComparison.Ordinal);      // 新号 ⇒ 插在最后一条铁则之后
        Assert.Contains("铁则 2 · 乙", agents, StringComparison.Ordinal);

        var wl = File.ReadAllText(Path.Combine(ws, "memory", "收尾水位.md"));
        Assert.Contains("知识水位（运行时写）", wl, StringComparison.Ordinal);   // 水位由运行时提
    }

    [Fact]
    public async Task 铁则按编号直达_同号替换_且不认识的目标不写()
    {
        var (ws, ctx, roots) = Scene();
        var client = FakeModelClient.Returning(
            "[CLOSEOUT] rules\n## ⚠️ 铁则 2 · 乙（改）\n新正文\n[/CLOSEOUT]\n"
            + "[CLOSEOUT] unknown\n别写我\n[/CLOSEOUT]");

        await CloseoutJob.RunAsync(client, [new MemorySource(ws)], [new RulesSink(ws)], ctx, roots);

        var agents = File.ReadAllText(Path.Combine(ws, "AGENTS.md"));
        Assert.Contains("铁则 2 · 乙（改）", agents, StringComparison.Ordinal);
        Assert.DoesNotContain("正文乙", agents, StringComparison.Ordinal);      // 同号整段替换
        Assert.DoesNotContain("别写我", agents, StringComparison.Ordinal);      // 未知落点不写
    }

    [Fact]
    public async Task 水位线之后没有新材料_就不调模型()
    {
        var (ws, ctx, roots) = Scene(watermark: "2026-10-11");
        var client = FakeModelClient.Returning("[CLOSEOUT] knowledge\nx\n[/CLOSEOUT]");

        var run = await CloseoutJob.RunAsync(client, [new MemorySource(ws), new HandoffSource(ws)],
            [new RulesSink(ws)], ctx, roots);

        Assert.Equal(0, run.ModelCalls);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public void 白名单外_写入被拒()
    {
        var (ws, _, roots) = Scene();
        var outside = Path.Combine(Path.GetTempPath(), "wb-co-out-" + Guid.NewGuid().ToString("N") + ".md");
        Assert.Throws<InvalidDataException>(() => new RulesSink(ws).Apply("## ⚠️ 铁则 9 · 丙\n正文", [Path.Combine(ws, "memory")]));
        Assert.False(File.Exists(outside));
        _ = roots;
    }
}
