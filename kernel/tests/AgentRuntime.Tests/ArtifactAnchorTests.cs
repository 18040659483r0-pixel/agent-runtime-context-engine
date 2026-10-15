using System.Text.Json;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **G1（2026-10-11）· 产物锚账**：运行时把「本任务碰过的产物读数（**实测**，模型编不了）」记成会话账，
/// 收尾/交接时直接给全 ⇒ 模型不必重读。
/// <para>
/// 依据：真机 M10（收尾件任务）为写一份 handoff 把 <c>big.txt</c> **逐窗重读 19 次**
/// （<c>tools/master-testsuite/RUNS.md</c> §21.3）；运行时手上就有盘，记一次即可在
/// <c>RuntimeHost.TaskCloseoutLines</c> 里说给模型听。
/// </para>
/// </summary>
public sealed class ArtifactAnchorTests
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

    private static string Args(string path) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = path });

    [Fact]
    public async Task 正例_read成功_记一条实测锚_同份只记一次_失败不记()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-g1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "big.txt");
        await File.WriteAllTextAsync(file, "l1\nl2\nl3\n", Ct);

        var sink = new MemorySink();
        var runner = new ToolRunner(sink, ledger: new ApprovalLedger(), security: new SecurityGateway());

        var r = await runner.HandleAsync($"[TOOL] read {Args(file)} risk: none", 1, "s", Ct);
        Assert.False(r.Denied);

        Assert.Equal(1, runner.Artifacts.Count);
        var one = runner.Artifacts[0];
        Assert.Contains("measured:", one, StringComparison.Ordinal);
        Assert.Contains("big.txt", one, StringComparison.Ordinal);
        Assert.Contains("bytes=9", one, StringComparison.Ordinal);
        Assert.Contains("lines=3", one, StringComparison.Ordinal);
        Assert.Contains("sha256=", one, StringComparison.Ordinal);

        // 负例 ①：同一份**再读**一次 ⇒ 仍只一条（不重复记账）。
        await runner.HandleAsync($"[TOOL] read {Args(file)} risk: none", 2, "s", Ct);
        Assert.Equal(1, runner.Artifacts.Count);

        // 负例 ②：读**不存在**的文件（失败）⇒ 不记账（不凭空造）。
        await runner.HandleAsync($"[TOOL] read {Args(file + ".nope")} risk: none", 3, "s", Ct);
        Assert.Equal(1, runner.Artifacts.Count);

        // 负例 ③：不碰文件的动作（list）⇒ 不记账。
        await runner.HandleAsync($"[TOOL] list {Args(dir)} risk: none", 4, "s", Ct);
        Assert.Equal(1, runner.Artifacts.Count);
    }
}
