using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **R2 全程 append-only，不折叠**（主人 2026-10-25 10:0x 定）。
/// <para>
/// 口径：折叠是为了省 token，但每折一次就是把 R2 中段整段重发（实测命中 74.9%，折叠轮 45.8%）；
/// 注意力由 R3 焦点解决、叙事由 [任务台账] 承担 ⇒ 事件流一个字节都不折，窗口边界交给宿主分档提示。
/// </para>
/// <para>唯一例外：调用方显式给 <c>maxEvents</c>（窗口由它自己裁）。</para>
/// </summary>
public sealed class R2NoFoldTests
{
    private static async Task<List<ChatMessage>> ContributeAsync(AppendStreamModule module)
    {
        var messages = new List<ChatMessage>();
        await module.ContributeAsync(new RuntimeContext("s", 0), messages, default);
        return messages;
    }

    [Fact]
    public async Task 超大历史_也不折叠_逐条原文且在序()
    {
        var stream = new SessionAppendStream();
        for (var i = 0; i < 600; i++)
        {
            stream.Append(SessionEventKind.Memory, new string((char)('a' + i % 26), 1500));   // 900k 字符
        }

        var module = new AppendStreamModule(stream);
        var messages = await ContributeAsync(module);

        Assert.Equal(600, messages.Count);
        Assert.DoesNotContain(messages, m => m.Content.StartsWith("[历史摘要]", StringComparison.Ordinal));
        Assert.Contains(new string('a', 1500), messages[0].Content, StringComparison.Ordinal);
        Assert.Contains(new string((char)('a' + 599 % 26), 1500), messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 显式MaxEvents窗口_仍由调用方裁()
    {
        var stream = new SessionAppendStream();
        for (var i = 0; i < 60; i++)
        {
            stream.Append(SessionEventKind.Memory, "第 " + i + " 条");
        }

        var module = new AppendStreamModule(stream, maxEvents: 5);
        var messages = await ContributeAsync(module);

        Assert.Equal(5, messages.Count);
        Assert.EndsWith("第 55 条", messages[0].Content, StringComparison.Ordinal);
        Assert.EndsWith("第 59 条", messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 小历史_原文照旧()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "做 A");
        stream.Append(SessionEventKind.AgentOutput, "[RESULT] 好了");

        var messages = await ContributeAsync(new AppendStreamModule(stream));

        Assert.Equal(2, messages.Count);
        Assert.EndsWith("做 A", messages[0].Content, StringComparison.Ordinal);
    }
}
