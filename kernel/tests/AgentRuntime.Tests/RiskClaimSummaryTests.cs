using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **A 案：<c>RiskClaimed</c> 只记「声明 + 参数摘要」**（依据 <c>handoff/27-master-worker-split-ABC</c> §一）。
/// <para>
/// 现场：同一份工具参数原本在流里出现三遍（<c>[TOOL]</c> / <c>RiskClaimed</c> / <c>ToolResult</c>），
/// 其中 <c>RiskClaimed</c> 一项重抄 = 49,966 / 50,830 = **98.3%**，单件最大 **16,202** 字符。
/// </para>
/// <para>四条口径（正例 / 应红 / 防假红 / 退化）：</para>
/// <list type="number">
/// <item>16 KB 写 ⇒ 审计事件 **≤ 上限**，且落点 + sha256 + 字节数都在（可审计不丢）；</item>
/// <item>**旧形状（重抄整段）必然超过上限** ⇒ 闸门有牙（不是「改了就算」）；</item>
/// <item>短参数**原样保留** ⇒ 凝结不改变小样本行为（防假红）；</item>
/// <item>额度用尽时**只保声明**并明说 ⇒ 不产出半截摘要（不静默）。</item>
/// </list>
/// <para>上限一律**读被测常量** <see cref="ToolRunner.RiskClaimedEventMaxChars"/>，不重抄数字（坑 #138）。</para>
/// </summary>
public sealed class RiskClaimSummaryTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

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

    private static string Args(string path, string content) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = path, ["content"] = content });

    private static string Sha12(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..12];

    [Fact]
    public async Task 正例_16KB写的审计事件_不重抄正文_落点与sha都在()
    {
        var target = Path.Combine(TempDir(), "big.txt");
        var content = new string('x', 16 * 1024);
        var sink = new MemorySink();
        var security = new SecurityGateway();
        var runner = new ToolRunner(sink, ledger: new ApprovalLedger(), security: security);

        var result = await runner.HandleAsync($"[TOOL] write {Args(target, content)} risk: none", 1, "s", Ct);

        Assert.False(result.Denied);
        Assert.True(File.Exists(target));

        var audit = sink.Events.Single(e => e.Kind == SessionEventKind.RiskClaimed);
        Assert.True(audit.Text.Length <= ToolRunner.RiskClaimedEventMaxChars,
            $"审计事件 {audit.Text.Length} 字符，超过上限 {ToolRunner.RiskClaimedEventMaxChars}：{audit.Text}");
        Assert.StartsWith("risk: none", audit.Text);            // 声明逐字保留
        Assert.Contains("16384 B", audit.Text);                 // 字节数
        Assert.Contains(Sha12(content), audit.Text);            // 能唯一对上那条调用
        Assert.Contains("big.txt", audit.Text);                 // 落点
        Assert.DoesNotContain(content[..128], audit.Text);      // 正文没被重抄

        // 量数上屏（**先量后定**的依据；旧形状同题实测 16,202 字符）：
        output.WriteLine($"[measure] 16KB 写 · 审计事件 = {audit.Text.Length} 字符（上限 {ToolRunner.RiskClaimedEventMaxChars}）");
        output.WriteLine($"[measure] 事件正文：{audit.Text}");
    }

    [Fact]
    public void 应红_旧形状重抄整段参数_必然超过上限()
    {
        var content = new string('x', 16 * 1024);
        var args = ToolArgs.Parse(Args("/tmp/wb-a-legacy.txt", content));

        // 旧形状 = 把整段规范化参数重抄进事件（本改造前的行为）。
        var legacy = $"risk: none（模型自判）⇒ 自主放行 · write {args.Canonical}";
        Assert.True(legacy.Length > ToolRunner.RiskClaimedEventMaxChars,
            $"旧形状只有 {legacy.Length} 字符 ⇒ 闸门测不出问题（夹具失去意义）");

        // 新形状：给定额度后必在上限之内。
        var now = ToolRunner.SummarizeCall("write", args, ToolRunner.RiskClaimedEventMaxChars - 40);
        Assert.True(now.Length <= ToolRunner.RiskClaimedEventMaxChars - 40, $"新形状 {now.Length} 字符");
        Assert.Contains(Sha12(content), now);
    }

    [Fact]
    public void 防假红_短参数原样保留_凝结不改小样本行为()
    {
        var summary = ToolArgs.Parse(Args("/tmp/wb-a-small.txt", "hi")).Summary();

        // B1（2026-09-29）：短参数仍**逐字原样**（不压成摘要），只是形状改成了行式键值。
        Assert.Contains("content: hi", summary);
        Assert.Contains("/tmp/wb-a-small.txt", summary);
        Assert.DoesNotContain("sha256:", summary);      // 短内容不该被压成摘要
    }

    [Fact]
    public void 退化_额度用尽时只保声明_并明说()
    {
        var args = ToolArgs.Parse(Args("/tmp/wb-a-none.txt", "x"));
        var text = ToolRunner.SummarizeCall("write", args, 0);

        Assert.Contains("无额度", text);
    }
}
