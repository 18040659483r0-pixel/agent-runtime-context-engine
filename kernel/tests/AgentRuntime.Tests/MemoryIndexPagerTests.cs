using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **记忆索引分页取**（2026-10-24 主人令）：索引不常驻、不每轮拼 —— 模型点一段，运行时把那段作为
/// 一条 append-only 事件写进 R2。三件硬事：① 零注入 ② 切片逐字来自文件 ③ 认得出 day/week/month/all。
/// </summary>
public sealed class MemoryIndexPagerTests
{
    private sealed class Sink : IEventSink
    {
        public List<(SessionEventKind Kind, string Text, string? Source)> Events { get; } = [];

        public SessionEvent Append(SessionEventKind kind, string text, string? source = null)
        {
            Events.Add((kind, text, source));
            return new SessionEvent(Events.Count, kind, text, source, $"E{Events.Count:000}");
        }
    }

    private static string WorkspaceWithIndex()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-idx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, MemoryIndexPagerModule.RelativePath), """
            # 索引
            ## 一、时间线索引
            | 日期 | 发生了什么 | 文件 |
            |---|---|---|
            | 10-20 | 甲 | `2026-10-20.md` |
            | 10-21 | 乙 | `2026-10-21.md` |
            | 10-22 | 丙 | `2026-10-22.md` |

            ## 附：[NOTE] 自动追加（按日）
            - 2026-10-22 22:03 — 丙的补记（`memory/2026-10-22.md`）
            """);
        return dir;
    }

    [Fact]
    public async Task 零注入_不往prompt里放任何字节()
    {
        var dir = WorkspaceWithIndex();
        try
        {
            var module = new MemoryIndexPagerModule(dir);
            var messages = new List<ChatMessage>();
            await module.ContributeAsync(new RuntimeContext("s", 0), messages, default);
            Assert.Empty(messages);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("month:10", 4)]        // 3 条表行 + 1 条自动行（同日都在）
    [InlineData("day:10-22", 2)]       // 表行 + 自动行
    [InlineData("week:10-20..10-21", 2)]
    [InlineData("all", 4)]
    public void 按翻页幅度切一段(string spec, int expectHits)
    {
        var dir = WorkspaceWithIndex();
        try
        {
            var slice = new MemoryIndexPagerModule(dir).BuildSlice(spec, out var error);
            Assert.Null(error);
            Assert.Contains($"命中 {expectHits} 条", slice, StringComparison.Ordinal);
            Assert.Contains("2026-10-2", slice, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 乱写幅度_明确报错不瞎猜()
    {
        var dir = WorkspaceWithIndex();
        try
        {
            var slice = new MemoryIndexPagerModule(dir).BuildSlice("season:Q4", out var error);
            Assert.NotNull(error);
            Assert.Contains("day:", error, StringComparison.Ordinal);
            Assert.Empty(slice);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 回复里点了_IDX_就落成一条事件()
    {
        var dir = WorkspaceWithIndex();
        try
        {
            var sink = new Sink();
            var module = new MemoryIndexPagerModule(dir, sink);
            var reply = new ChatResponse
            {
                Choices = [new ChatChoice { Message = ChatMessage.Assistant("好，取一段。\n[IDX] day:10-22\n[FOCUS] none") }],
            };

            await module.ObserveAsync(new RuntimeContext("s", 0), new ChatRequest { Model = "m", Messages = [] }, reply, default);

            var e = Assert.Single(sink.Events);
            Assert.Equal(SessionEventKind.Memory, e.Kind);
            Assert.Equal("memory-index", e.Source);
            Assert.Contains("10-22", e.Text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 正文里提到_IDX_不算点单_不产生假事件()
    {
        var dir = WorkspaceWithIndex();
        try
        {
            var sink = new Sink();
            var module = new MemoryIndexPagerModule(dir, sink);
            var reply = new ChatResponse
            {
                Choices = [new ChatChoice { Message = ChatMessage.Assistant("上一轮我用 [IDX] month:10` 取回了 22 条，逐条如下。") }],
            };

            await module.ObserveAsync(new RuntimeContext("s", 0), new ChatRequest { Model = "m", Messages = [] }, reply, default);
            Assert.Empty(sink.Events);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
