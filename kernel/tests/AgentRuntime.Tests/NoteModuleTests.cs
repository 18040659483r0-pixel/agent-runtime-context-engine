using AgentRuntime.Core;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **笔记落盘（D3-A）**：`[NOTE]` ⇒ 运行时确定性追加进当日 `memory/`（不经派发链）。
/// <para>由来：真机一次「记一笔」走了两轮派发、14 分钟、且被 worker 压缩改写（D3）。</para>
/// </summary>
public sealed class NoteModuleTests
{
    private sealed class Sink : IEventSink
    {
        public List<string> Events { get; } = [];

        public SessionEvent Append(SessionEventKind kind, string text, string? source = null)
        {
            Events.Add(text);
            return new SessionEvent(Events.Count, kind, text, source, $"E{Events.Count:000}");
        }
    }

    private static ChatResponse Reply(string text) =>
        new() { Choices = [new ChatChoice { Message = ChatMessage.Assistant(text) }] };

    private static Task ObserveAsync(NoteModule m, string reply) =>
        m.ObserveAsync(new RuntimeContext("s1", 0), new ChatRequest { Model = "m", Messages = [] }, Reply(reply), default)
            .AsTask();

    [Fact]
    public void 解析_单行与多行_到下一个块头为止()
    {
        var notes = NoteModule.Extract("[NOTE] 第一行\n第二行\n[FOCUS] E001\n[NOTE] 另一条");
        Assert.Equal(2, notes.Count);
        Assert.Equal("第一行\n第二行", notes[0]);
        Assert.Equal("另一条", notes[1]);
    }

    [Fact]
    public async Task 落盘_追加不覆盖_同一天两次都在()
    {
        var dir = Path.Combine(Path.GetTempPath(), "note-test-" + Guid.NewGuid().ToString("N"));
        var sink = new Sink();
        var m = new NoteModule(dir, sink);
        try
        {
            await ObserveAsync(m, "[NOTE] 甲");
            await ObserveAsync(m, "[NOTE] 乙");

            var path = Path.Combine(dir, "memory", DateTimeOffset.Now.ToString("yyyy-MM-dd") + ".md");
            Assert.True(File.Exists(path));
            var body = File.ReadAllText(path);
            Assert.Contains("甲", body, StringComparison.Ordinal);
            Assert.Contains("乙", body, StringComparison.Ordinal);   // 只追加 ⇒ 甲还在（不是覆盖）
            Assert.Contains(sink.Events, e => e.Contains("已落盘", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 未配活版根_不落盘_但不静默()
    {
        var sink = new Sink();
        var m = new NoteModule(null, sink);
        await ObserveAsync(m, "[NOTE] 无家可归");
        Assert.Contains(sink.Events, e => e.Contains("未落盘", StringComparison.Ordinal));
    }

    [Fact]
    public void 零注入_不贡献任何prompt字节()
    {
        var m = new NoteModule(null, null);
        var messages = new List<ChatMessage>();
        m.ContributeAsync(new RuntimeContext("s", 0), messages, default).AsTask().Wait();
        Assert.Empty(messages);
        Assert.Equal("note", m.Name);
    }

    [Fact]
    public async Task 笔记同时进记忆索引_同一条不重复()
    {
        var dir = Path.Combine(Path.GetTempPath(), "note-index-" + Guid.NewGuid().ToString("N"));
        var sink = new Sink();
        var m = new NoteModule(dir, sink);
        try
        {
            await ObserveAsync(m, "[NOTE] 索引也要有这一条");
            await ObserveAsync(m, "[NOTE] 索引也要有这一条");   // 同文重复 ⇒ 索引不重复写

            var index = Path.Combine(dir, NoteIndexFile);
            Assert.True(File.Exists(index), "索引文件应被创建");
            var body = File.ReadAllText(index);
            Assert.Contains("索引也要有这一条", body, StringComparison.Ordinal);
            Assert.Equal(1, body.Split("索引也要有这一条").Length - 1);
            Assert.Contains(sink.Events, e => e.Contains("索引已追加", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private const string NoteIndexFile = "MEMORYINDEX.md";
}
