using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// V4.1 **焦点进 prompt 的那一层**测试 —— 守五件事：
/// ① 焦点**不改历史**：移动焦点后流文件逐字节不变（I1）；
/// ② **空焦点零注入**（I2）；
/// ③ **消融等价**：不挂 focus ⇒ 消息序列与上一版逐条相等（I3）；
/// ④ **第三道闸门**：focus 排在 append-stream 之前 ⇒ 编排期报错（I8）；
/// ⑤ 焦点既**不是冻结区**（I9），又必须**居末**（I17）。
/// </summary>
public sealed class FocusPipelineTests : IDisposable
{
    private readonly SnapshotTestWorkspace _workspace = new("focus-pipeline");

    public void Dispose() => _workspace.Dispose();

    private const string ReportHint = "回复末尾用一行 [FOCUS] E### E### 标出本轮依据的事件标签";

    /// <summary>造一条「两轮 + 自报」的流文件，返回路径（供焦点算权重）。</summary>
    private string SeededStreamPath()
    {
        var path = _workspace.File("stream.jsonl");
        var store = new SessionStreamStore(path);
        var stream = new SessionAppendStream();

        foreach (var @event in SeededEvents())
        {
            stream.AppendPreservingTag(@event.Kind, @event.Text, @event.Tag);
        }

        foreach (var @event in stream.Events)
        {
            store.Append(@event);
        }

        return path;
    }

    private static IEnumerable<SessionEvent> SeededEvents()
    {
        yield return new SessionEvent(1, SessionEventKind.Rule, ReportHint);
        yield return new SessionEvent(2, SessionEventKind.Knowledge, "任务三的代号是 B7");
        yield return new SessionEvent(3, SessionEventKind.UserInput, "问题一");
        yield return new SessionEvent(4, SessionEventKind.AgentOutput, "回答一");
        yield return new SessionEvent(5, SessionEventKind.FocusReport, "E002");
        yield return new SessionEvent(6, SessionEventKind.UserInput, "问题二");
        yield return new SessionEvent(7, SessionEventKind.AgentOutput, "回答二");
        yield return new SessionEvent(8, SessionEventKind.FocusReport, "E002 E003");
    }

    // ---------------- I1 ----------------

    [Fact]
    public async Task Focus_DoesNotTouchStreamFile()
    {
        var path = SeededStreamPath();
        var before = File.ReadAllBytes(path);
        var stream = new SessionStreamStore(path).Load();

        var token = TestContext.Current.CancellationToken;

        // 先用自报权重算一次 band。
        var messages = new List<ChatMessage>();
        await new FocusModule(stream, new FocusOptions()).ContributeAsync(new RuntimeContext("s", 0), messages, token);
        Assert.Single(messages);

        // 再**移动焦点**（显式覆盖），band 变了 —— 但流文件必须逐字节不变。
        var moved = new List<ChatMessage>();
        await new FocusModule(stream, new FocusOptions { Explicit = ["E001", "E006"] })
            .ContributeAsync(new RuntimeContext("s", 1), moved, token);
        Assert.Equal("[FOCUS] E001 E006", moved[0].Content);
        Assert.NotEqual(messages[0].Content, moved[0].Content);

        // 再清空焦点：历史依然一个字都不动（焦点只导航，不改写）。
        var cleared = new List<ChatMessage>();
        await new FocusModule(stream, new FocusOptions { Cleared = true })
            .ContributeAsync(new RuntimeContext("s", 2), cleared, token);
        Assert.Empty(cleared);

        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // ---------------- I2 ----------------

    [Fact]
    public async Task Focus_Empty_ContributesNothing()
    {
        var token = TestContext.Current.CancellationToken;

        // 空流 + 默认策略 ⇒ 空焦点 ⇒ 一条消息都没有。
        var empty = new List<ChatMessage>();
        await new FocusModule(new SessionAppendStream()).ContributeAsync(new RuntimeContext("s", 0), empty, token);
        Assert.Empty(empty);

        // 有自报但权重都低于阈值（半衰期被压到极小）⇒ 同样零注入。
        var stream = new SessionStreamStore(SeededStreamPath()).Load();
        var starved = new List<ChatMessage>();
        await new FocusModule(stream, new FocusOptions { MinWeight = 999 })
            .ContributeAsync(new RuntimeContext("s", 0), starved, token);
        Assert.Empty(starved);

        // 清空 / 只认人工设定（没给 --focus）⇒ 也是零注入。
        foreach (var options in new[] { new FocusOptions { Cleared = true }, new FocusOptions { Policy = FocusPolicy.Explicit } })
        {
            var none = new List<ChatMessage>();
            await new FocusModule(stream, options).ContributeAsync(new RuntimeContext("s", 0), none, token);
            Assert.Empty(none);
        }
    }

    // ---------------- I3 ----------------

    [Fact]
    public async Task Focus_Removed_MatchesPreviousVersion()
    {
        var token = TestContext.Current.CancellationToken;
        var stream = new SessionStreamStore(SeededStreamPath()).Load();

        // 参考序列 = 上一版（V4）的口径：事件按序进 prompt（渲染行首 = Tag = 本版 §一 定的改法）。
        var reference = stream.ToMessages().Select(m => m.Content).ToArray();

        // A：不挂 focus。
        var without = new List<ChatMessage>();
        await new AppendStreamModule(stream).ContributeAsync(new RuntimeContext("s", 0), without, token);

        // B：挂 focus，但焦点为空（清空）⇒ 必须与 A **逐条相等**（零注入）。
        var withEmptyFocus = new List<ChatMessage>();
        await new AppendStreamModule(stream).ContributeAsync(new RuntimeContext("s", 0), withEmptyFocus, token);
        await new FocusModule(stream, new FocusOptions { Cleared = true }).ContributeAsync(new RuntimeContext("s", 0), withEmptyFocus, token);

        Assert.Equal(reference, without.Select(m => m.Content));
        Assert.Equal(without.Select(m => m.Content), withEmptyFocus.Select(m => m.Content));

        // C：焦点非空 ⇒ 只在**最后**多一行 band；前面每一条（含正文）逐字不变。
        var withBand = new List<ChatMessage>();
        await new AppendStreamModule(stream).ContributeAsync(new RuntimeContext("s", 0), withBand, token);
        await new FocusModule(stream, new FocusOptions()).ContributeAsync(new RuntimeContext("s", 0), withBand, token);

        Assert.Equal(without.Count + 1, withBand.Count);
        Assert.Equal(without.Select(m => m.Content), withBand.Take(without.Count).Select(m => m.Content));
        Assert.Equal("[FOCUS] E002 E003", withBand[^1].Content);
        Assert.Equal("system", withBand[^1].Role);
    }

    // ---------------- I8 ----------------

    [Fact]
    public void Gate_FocusBeforeStream_IsRejected()
    {
        var focus = new FocusModule(new SessionAppendStream());
        var stream = new AppendStreamModule(new SessionAppendStream());
        var rules = new RulesModule(FrozenContentSource.Empty);

        // 人为把 focus 排在 append-stream 之前 ⇒ **编排期报错**（不静默纠正）。
        var broken = new IRuntimeModule[] { rules, focus, stream };
        var ex = Assert.Throws<InvalidDataException>(() => DeterminismGate.Arrange(broken.ToArray()));
        Assert.Contains("第三道", ex.Message, StringComparison.Ordinal);
        Assert.Contains("append-stream", ex.Message, StringComparison.Ordinal);

        // 已编排好的次序也不能蒙混过关（Validate 同一道闸门）。
        var ex2 = Assert.Throws<InvalidDataException>(() => DeterminismGate.Validate(broken.ToArray()));
        Assert.Contains("第三道", ex2.Message, StringComparison.Ordinal);

        // 规范次序（焦点在 append-stream 之后）⇒ 通过，且焦点落在最后。
        var good = DeterminismGate.Arrange([stream, rules, focus]);
        Assert.Equal(new[] { "rules", "append-stream", "focus" }, good.Select(m => m.Name).ToArray());
        DeterminismGate.Validate(good);

        // 焦点模块只能有一个。
        Assert.Throws<InvalidDataException>(() => DeterminismGate.Arrange(
        [
            new AppendStreamModule(new SessionAppendStream()),
            new FocusModule(new SessionAppendStream()),
            new FocusModule(new SessionAppendStream()),
        ]));
    }

    // ---------------- I9 ----------------

    [Fact]
    public void FocusModule_IsNotFrozenZone()
    {
        Assert.False(typeof(IFrozenZoneModule).IsAssignableFrom(typeof(FocusModule)));
        Assert.True(typeof(IFocusRegionModule).IsAssignableFrom(typeof(FocusModule)));

        // 挂了 focus 也不改变冻结前缀指纹（焦点不是稳定前缀的一部分）。
        var modules = new IRuntimeModule[] { new RulesModule(FrozenContentSource.Empty) };
        var withFocus = new IRuntimeModule[] { new RulesModule(FrozenContentSource.Empty), new FocusModule(new SessionAppendStream()) };
        Assert.Equal(FrozenPrefix.Assemble(modules).Id, FrozenPrefix.Assemble(withFocus).Id);
    }

    // ---------------- I17 ----------------

    [Fact]
    public async Task Focus_IsLastDynamicRegion()
    {
        var config = new RuntimeConfiguration
        {
            BaseUrl = "https://example.invalid",
            Model = "m",
            Modules = ["rules", "append-stream", "focus"],
            Stream = new StreamConfiguration { Path = SeededStreamPath() },
        };

        var modules = ModuleRegistry.Create(config);

        // 编排次序：协议区（R1-P，强制装配、最前）→ 冻结区（R1）→ 事件流（R2）→ 焦点（R3）。
        Assert.Equal(new[] { "protocol", "rules", "append-stream", "focus" }, modules.Select(m => m.Name).ToArray());

        var engine = new AgentRuntimeEngine(FakeModelClient.Returning("好"), new RuntimeOptions { Model = "m" }, modules);
        var result = await engine.ChatAsync("本轮问题", TestContext.Current.CancellationToken);

        var contents = result.Request.Messages.Select(m => m.Content).ToArray();

        // 最后一条永远是**本 turn 的用户消息**（引擎追加），band 紧邻它之前 ——
        // 且焦点贡献的消息位于全部动态区域之后（协议区 1 + 8 条流事件 + 1 行 band + 1 条用户消息）。
        Assert.Equal("本轮问题", contents[^1]);
        Assert.Equal("[FOCUS] E002 E003", contents[^2]);
        Assert.Equal(11, contents.Length);
        Assert.Equal(ProtocolText.Text, contents[0]);                              // R1-P 占据最前
        Assert.Contains("[RULE]", contents[1], StringComparison.Ordinal);          // 冻结/流区里没有 band，只有流事件
    }
}
