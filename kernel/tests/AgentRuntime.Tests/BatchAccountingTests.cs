using AgentRuntime.Core.Security;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **每动作记账税**（P1-2 §12.3 减法 2）—— 同批同形状的 `risk:` 自判共用**一条代表**。
/// <para>每条不变量两件东西（§十·30：闸门要有牙，正例绿 ≠ 有牙）：</para>
/// <list type="number">
/// <item>一条**应红**：同批同形状 ⇒ 只准 1 条；撞硬线 ⇒ 该逐条记的必须逐条红。</item>
/// <item>一条**防假红**：不同形状 / 单调用 ⇒ 必须**不**合并（照旧各记一条）。</item>
/// </list>
/// <para>夹具喂的是**事件流**而不是税务读数：判据是纯函数；税务读数另有尺子（path-triage）。</para>
/// </summary>
public sealed class BatchAccountingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class MemorySink : IEventSink
    {
        public List<SessionEvent> Events { get; } = [];

        public SessionEvent Append(SessionEventKind kind, string text, string? source = null)
        {
            var @event = new SessionEvent(Events.Count + 1, kind, text, source);
            Events.Add(@event);
            return @event;
        }
    }

    private static int RiskClaims(MemorySink sink) =>
        sink.Events.Count(e => e.Kind == SessionEventKind.RiskClaimed);

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-batch-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---------------- 应红：同批同形状 ⇒ 只 1 条 ----------------

    [Fact]
    public async Task 同批同形状_共用一条risk自判()
    {
        var dir = TempDir();
        try
        {
            var sink = new MemorySink();
            var runner = new ToolRunner(sink, security: new SecurityGateway());
            var reply =
                $"[TOOL] write {{\"path\":\"{Path.Combine(dir, "a.txt")}\",\"content\":\"1\"}} risk: none\n" +
                $"[TOOL] write {{\"path\":\"{Path.Combine(dir, "b.txt")}\",\"content\":\"2\"}} risk: none";

            await runner.HandleAsync(reply, 1, "s", Ct);

            Assert.Equal(1, RiskClaims(sink));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------- 防假红：不同形状 ⇒ 各判一次 ----------------

    [Fact]
    public async Task 不同形状_不继承()
    {
        var first = TempDir();
        var second = TempDir();
        try
        {
            var sink = new MemorySink();
            var runner = new ToolRunner(sink, security: new SecurityGateway());
            var reply =
                $"[TOOL] write {{\"path\":\"{Path.Combine(first, "a.txt")}\",\"content\":\"1\"}} risk: none\n" +
                $"[TOOL] write {{\"path\":\"{Path.Combine(second, "b.txt")}\",\"content\":\"2\"}} risk: none";

            await runner.HandleAsync(reply, 1, "s", Ct);

            Assert.Equal(2, RiskClaims(sink));
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    // ---------------- 防假红：单调用 ⇒ 与旧行为逐字节相同 ----------------

    [Fact]
    public async Task 单调用_不合并且正文与旧版一致()
    {
        var dir = TempDir();
        try
        {
            var sink = new MemorySink();
            var runner = new ToolRunner(sink, security: new SecurityGateway());
            var target = Path.Combine(dir, "only.txt");

            await runner.HandleAsync($"[TOOL] write {{\"path\":\"{target}\",\"content\":\"x\"}} risk: none", 1, "s", Ct);

            var claim = Assert.Single(sink.Events, e => e.Kind == SessionEventKind.RiskClaimed);
            Assert.DoesNotContain("共用此条", claim.Text, StringComparison.Ordinal);
            Assert.Contains("⇒ 自主放行 · write", claim.Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------- 应红：同批同形状 ⇒ 留档也只 1 条 ----------------

    [Fact]
    public async Task 同批同形状_留档合并成一条且账本仍逐项()
    {
        var dir = TempDir();
        try
        {
            var sink = new MemorySink();
            var ledger = new ApprovalLedger();
            var gateway = new SecurityGateway();
            var runner = new ToolRunner(sink, ledger: ledger, security: gateway);
            var reply =
                $"[TOOL] write {{\"path\":\"{Path.Combine(dir, "a.txt")}\",\"content\":\"1\"}}\n" +
                $"[TOOL] write {{\"path\":\"{Path.Combine(dir, "b.txt")}\",\"content\":\"2\"}}";

            await runner.HandleAsync(reply, 1, "s", Ct);

            // 流里：**一条**批记录（项数写在正文里）；账本里：**逐项**都在（可复原“哪一批含哪几项”）。
            var filed = Assert.Single(sink.Events, e => e.Kind == SessionEventKind.PermissionFiled);
            Assert.Contains("同批 2 项共用此条", filed.Text, StringComparison.Ordinal);
            Assert.Equal(2, ledger.Entries.Count);
            Assert.All(ledger.Entries, e => Assert.Equal(ApprovalDecision.Filed, e.Decision));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------- 防假红：单调用 ⇒ 顺序与条数都与旧行为逐字节相同 ----------------

    [Fact]
    public async Task 单调用_留档立即发且顺序不变()
    {
        var dir = TempDir();
        try
        {
            var sink = new MemorySink();
            var runner = new ToolRunner(sink, ledger: new ApprovalLedger(), security: new SecurityGateway());

            await runner.HandleAsync(
                $"[TOOL] write {{\"path\":\"{Path.Combine(dir, "one.txt")}\",\"content\":\"x\"}}", 1, "s", Ct);

            // 旧的硬顺序：**留档在结果之前**（留档是“本该问人”的记录，不是事后补记）。
            Assert.Equal(
                [SessionEventKind.PermissionFiled, SessionEventKind.ToolResult],
                sink.Events.Select(static e => e.Kind));
            Assert.DoesNotContain("共用此条", sink.Events[0].Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---------------- 应红：撞硬线的声明 ⇒ 逐条记（不许降档 / 不许合并） ----------------

    [Fact]
    public async Task 撞硬线_同形状也逐条记不许合并()
    {
        // **同形状**的两条 must-ask 命令（`rmdir` 在 must-ask 表里 ⇒ 声明 none 与硬线冲突）。
        // 用**无参** `rmdir` 取证据：它只会报「缺操作数」，不碰任何真实数据。
        var sink = new MemorySink();
        var runner = new ToolRunner(sink, security: new SecurityGateway());
        var reply =
            "[TOOL] exec {\"command\":\"rmdir\"} risk: none\n" +
            "[TOOL] exec {\"command\":\"rmdir\"} risk: none";

        await runner.HandleAsync(reply, 1, "s", Ct);

        // 同形状 ⇒ 否则会被合并成 1 条；撞硬线 ⇒ **两条都要留痕**（“撒谎会被记下来”，逐条可见）。
        Assert.Equal(2, RiskClaims(sink));
        Assert.All(
            sink.Events.Where(e => e.Kind == SessionEventKind.RiskClaimed),
            e => Assert.Contains("与硬线冲突", e.Text, StringComparison.Ordinal));
    }
}
