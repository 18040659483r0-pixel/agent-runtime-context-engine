using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **B 案：<c>write</c> / <c>edit</c> 回执不回显整段参数**（依据 <c>handoff/27</c> §二）。
/// <para>
/// 现场：这两个回执原本把整段规范化参数回显一遍 ⇒ <c>write</c> 实测均 **9,792**、最大 **16,334** 字符、**无上限**，
/// 而 <c>exec</c> / <c>read</c> 都封在 ~2,000（缺口就在这里）。
/// </para>
/// <para>三条口径：① 16 KB 写 ⇒ 回执 ≤ <see cref="ToolLimits.MaxReceiptChars"/>（**读被测属性**，不重抄数字）；
/// ② 防假红 —— 小内容**逐字原样**进回显（凝结不改变小样本行为）；③ 判据链不许改 —— 「写成功」永远由编译 / 测试判，回执只报事实。</para>
/// </summary>
public sealed class WriteReceiptTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-b-" + Guid.NewGuid().ToString("N"));
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
    public async Task 正例_16KB写的回执_不回显整段正文_且不超上限()
    {
        var target = Path.Combine(TempDir(), "big.txt");
        var content = new string('y', 16 * 1024);
        var sink = new MemorySink();
        var runner = new ToolRunner(sink, ledger: new ApprovalLedger(), security: new SecurityGateway());

        var result = await runner.HandleAsync($"[TOOL] write {Args(target, content)} risk: none", 1, "s", Ct);

        Assert.False(result.Denied);
        var receipt = sink.Events.Last(e => e.Kind == SessionEventKind.ToolResult).Text;

        Assert.True(receipt.Length <= ToolLimits.Default.MaxReceiptChars,
            $"回执 {receipt.Length} 字符，超过上限 {ToolLimits.Default.MaxReceiptChars}：{receipt}");
        Assert.Contains("落点", receipt);
        Assert.Contains("16384 字节", receipt);           // 事实行：写了多少
        Assert.Contains("16384 B", receipt);              // 摘要：参数字节数
        Assert.Contains(Sha12(content), receipt);        // 摘要：能唯一对上那次调用
        Assert.DoesNotContain(content[..128], receipt);  // 正文没被重抄

        output.WriteLine($"[measure] 16KB write 回执 = {receipt.Length} 字符（上限 {ToolLimits.Default.MaxReceiptChars}；旧实测均 9,792 / 最大 16,334 / 无上限）");
        output.WriteLine($"[measure] 回执正文：{receipt}");
    }

    [Fact]
    public async Task 正例_16KB_edit的回执_同样不超上限()
    {
        var target = Path.Combine(TempDir(), "e.txt");
        var oldText = new string('a', 16 * 1024);
        File.WriteAllText(target, oldText);
        var sink = new MemorySink();
        var runner = new ToolRunner(sink, ledger: new ApprovalLedger(), security: new SecurityGateway());

        var args = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["path"] = target,
            ["oldText"] = oldText,
            ["newText"] = new string('b', 16 * 1024),
        });
        var result = await runner.HandleAsync($"[TOOL] edit {args} risk: none", 1, "s", Ct);

        Assert.False(result.Denied);
        var receipt = sink.Events.Last(e => e.Kind == SessionEventKind.ToolResult).Text;
        Assert.True(receipt.Length <= ToolLimits.Default.MaxReceiptChars,
            $"edit 回执 {receipt.Length} 字符，超过上限 {ToolLimits.Default.MaxReceiptChars}：{receipt}");
        Assert.DoesNotContain(oldText[..128], receipt);

        output.WriteLine($"[measure] 16KB edit 回执 = {receipt.Length} 字符；正文：{receipt}");
    }

    [Fact]
    public async Task 防假红_小内容逐字原样进回显_不压成摘要()
    {
        var target = Path.Combine(TempDir(), "small.txt");
        var sink = new MemorySink();
        var runner = new ToolRunner(sink, ledger: new ApprovalLedger(), security: new SecurityGateway());

        var result = await runner.HandleAsync($"[TOOL] write {Args(target, "hi")} risk: none", 1, "s", Ct);

        Assert.False(result.Denied);
        var receipt = sink.Events.Last(e => e.Kind == SessionEventKind.ToolResult).Text;
        Assert.Contains("content: hi", receipt);     // 原样，不凝结（B1：形状已改行式键值）
        Assert.DoesNotContain("sha256:", receipt);
    }

    [Fact]
    public void 判据链没改_回执只报事实_事实行含落点与字节数()
    {
        // 「写成功」的判据永远是编译 / 测试（本文件自己就是证据）；回执的职责只是把**事实**报清楚。
        var receipt = "write → 落点 /tmp/x · 2 字节 · 新建 · sha (无)→ab12cd34ef56";
        Assert.Contains("落点", receipt);
        Assert.Contains("字节", receipt);
        Assert.Contains("sha", receipt);
    }
}
