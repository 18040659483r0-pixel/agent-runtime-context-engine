using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **L0 · 生命周期聚合**（`docs/DESIGN-LIFECYCLE-UX.md`）—— 守四件事：
/// <list type="number">
/// <item><b>架构闸门</b>：<c>Runtime 可以产生任意多个内部 Turn，但不得因此产生等量的用户界面消息</c>
/// ⇒ 「N 轮自续跑 = **1** 个生命周期」；</item>
/// <item><b>边界规则</b>：<b>每条 <c>UserInput</c> 都开自己的卡</b>（「答复决策」与「另问一事」用指针分开 ——
/// 主人 2026-09-21 定「需要分清楚」）；<c>[NEED-USER]</c> 之后那张新卡带上指回上一张的 <c>AnswersId</c>；
/// 没终局就让位 ⇒ <c>未终局</c>；</item>
/// <item><b>照实计数</b>：轮 / 工具 / 拒绝 / 风险声明 / 终局冲突，一个不臆造；</item>
/// <item><b>不知道就说不知道</b>：没有用量账本 ⇒ 用量为<b>空</b>（不是 0）。</item>
/// </list>
/// </summary>
public sealed class LifecycleAggregatorTests
{
    // ---------------- 夹具 ----------------

    private static SessionAppendStream Flow()
    {
        var stream = new SessionAppendStream();
        return stream;
    }

    private static SessionEvent User(SessionAppendStream stream, string text) =>
        stream.Append(SessionEventKind.UserInput, text);

    private static SessionEvent Say(SessionAppendStream stream, string text) =>
        stream.Append(SessionEventKind.AgentOutput, text);

    private static SessionEvent Tool(SessionAppendStream stream, string text) =>
        stream.Append(SessionEventKind.ToolResult, text);

    private static SessionEvent Denied(SessionAppendStream stream, string text) =>
        stream.Append(SessionEventKind.ToolDenied, text);

    // ---------------- 架构闸门：N 轮 ⇒ 1 条用户消息 ----------------

    [Fact]
    public void 五轮自续跑只产生一个生命周期()
    {
        var flow = Flow();
        User(flow, "把 run.sh 修好");
        Say(flow, "先看一下。[TOOL] read {\"path\":\"run.sh\"}");
        Tool(flow, "read run.sh → 42 行");
        Say(flow, "再改一处。[TOOL] edit {\"path\":\"run.sh\"}");
        Tool(flow, "edit run.sh → +2/-1");
        Say(flow, "跑一下。[TOOL] exec {\"command\":\"sh run.sh\"}");
        Tool(flow, "exec sh run.sh → ok");
        Say(flow, "复查。[TOOL] read {\"path\":\"run.sh\"}");
        Tool(flow, "read run.sh → 44 行");
        Say(flow, "[DONE] run.sh 修好了（退出码 0）");

        var report = LifecycleAggregator.Aggregate(flow);

        // ← 这一条就是架构口径的闸门：Turn 是计算单位，不是交互单位。
        Assert.Single(report.Lifecycles);

        var lifecycle = report.Lifecycles[0];
        Assert.Equal(5, lifecycle.Turns);
        Assert.Equal(4, lifecycle.ToolCalls);
        Assert.Equal(TaskLifecycleStatus.Done, lifecycle.Status);
        Assert.Equal("run.sh 修好了（退出码 0）", lifecycle.Result);
        Assert.Equal("把 run.sh 修好", lifecycle.Request);
        Assert.Equal(0, lifecycle.Answers);
        Assert.Equal(10, lifecycle.Trace.Count);
        Assert.Equal(10, report.TotalEvents);
        Assert.Equal(0, report.SetupEvents);
    }

    // ---------------- 边界规则 ----------------

    [Fact]
    public void 连续两次请求各自一个生命周期()
    {
        var flow = Flow();
        User(flow, "第一件");
        Say(flow, "[DONE] 做完了");
        User(flow, "第二件");
        Say(flow, "[DONE] 也做完了");

        var report = LifecycleAggregator.Aggregate(flow);

        Assert.Equal(2, report.Lifecycles.Count);
        Assert.Equal("第一件", report.Lifecycles[0].Request);
        Assert.Equal("第二件", report.Lifecycles[1].Request);
        Assert.Equal(TaskLifecycleStatus.Done, report.Lifecycles[0].Status);
        Assert.Equal(2, report.Turns);
    }

    [Fact]
    public void 答复决策开自己的卡_但带上指回那张卡的链接()
    {
        var flow = Flow();
        User(flow, "帮我选一条路");
        Say(flow, "两条路都行。\n[NEED-USER] 请二选一：① 改现有层；② 新建独立层");
        User(flow, "②");
        Say(flow, "[DONE] 按②建了独立层");

        var report = LifecycleAggregator.Aggregate(flow);

        // 「分清楚」：两张卡，但第二张**指回**第一张的决策点（不再并卡）。
        Assert.Equal(2, report.Lifecycles.Count);

        var first = report.Lifecycles[0];
        Assert.Equal("帮我选一条路", first.Request);
        Assert.Equal(TaskLifecycleStatus.WaitingForUser, first.Status);
        Assert.Null(first.AnswersId);
        Assert.Single(first.Decisions);
        Assert.Contains("请二选一", first.Decisions[0]);
        Assert.Equal(1, first.Turns);

        var second = report.Lifecycles[1];
        Assert.Equal("②", second.Request);
        Assert.Equal(1, second.AnswersId);
        Assert.True(second.IsAnswer);
        Assert.Equal(TaskLifecycleStatus.Done, second.Status);
        Assert.Equal("按②建了独立层", second.Result);
        Assert.Equal(1, second.Turns);
        Assert.DoesNotContain(second.Trace, e => e.Kind == SessionEventKind.UserInput && e.Text == "帮我选一条路");
    }

    [Fact]
    public void 连续两次需要人可以接成链()
    {
        var flow = Flow();
        User(flow, "第一件");
        Say(flow, "[NEED-USER] 第一次问你");
        User(flow, "甲");
        Say(flow, "[NEED-USER] 第二次问你");
        User(flow, "乙");
        Say(flow, "[DONE] 完了");

        var report = LifecycleAggregator.Aggregate(flow);

        Assert.Equal(3, report.Lifecycles.Count);
        Assert.Null(report.Lifecycles[0].AnswersId);
        Assert.Equal(1, report.Lifecycles[1].AnswersId);
        Assert.Equal(2, report.Lifecycles[2].AnswersId);
        Assert.Equal(TaskLifecycleStatus.Done, report.Lifecycles[2].Status);
    }

    [Fact]
    public void 等人中的生命周期不算了结_所以不给收尾()
    {
        var flow = Flow();
        User(flow, "收尾");
        Say(flow, "[NEED-USER] 请批准写权限");

        var report = LifecycleAggregator.Aggregate(flow);
        var lifecycle = report.Lifecycles[0];

        Assert.Equal(TaskLifecycleStatus.WaitingForUser, lifecycle.Status);
        Assert.True(lifecycle.IsOpen);
        Assert.False(lifecycle.IsSettled);
        Assert.Same(lifecycle, report.Open);
        Assert.Same(lifecycle, report.Current);
    }

    [Fact]
    public void 没终局就让位_前一个记未终局_后一个记进行中()
    {
        var flow = Flow();
        User(flow, "第一件");
        Say(flow, "我看了一下，没说话");   // 没有终局块就停了
        User(flow, "在吗");
        Say(flow, "在。");

        var report = LifecycleAggregator.Aggregate(flow);

        Assert.Equal(2, report.Lifecycles.Count);
        Assert.Equal(TaskLifecycleStatus.Unsettled, report.Lifecycles[0].Status);
        Assert.Equal(TaskLifecycleStatus.Running, report.Lifecycles[1].Status);
        Assert.Equal("未终局", report.Lifecycles[0].StatusLabel);
        Assert.Same(report.Lifecycles[1], report.Open);
    }

    [Fact]
    public void 模型还没说话时的第二句不另开生命周期()
    {
        var flow = Flow();
        User(flow, "补充一下：只改 TUI");
        User(flow, "算了，别改了");
        Say(flow, "[DONE] 没动");

        var report = LifecycleAggregator.Aggregate(flow);

        Assert.Single(report.Lifecycles);
        Assert.Equal(2, report.Lifecycles[0].UserMessages.Count);
        Assert.Equal(1, report.Lifecycles[0].Answers);
        Assert.Null(report.Lifecycles[0].AnswersId);
    }

    [Fact]
    public void 没人等人的时候新卡不带指针()
    {
        var flow = Flow();
        User(flow, "第一件");
        Say(flow, "[DONE] 好了");
        User(flow, "第二件");
        Say(flow, "[NEED-USER] 问你一句");
        User(flow, "无关的新问题");

        var report = LifecycleAggregator.Aggregate(flow);

        Assert.Equal(3, report.Lifecycles.Count);
        Assert.Null(report.Lifecycles[1].AnswersId);
        Assert.Equal(2, report.Lifecycles[2].AnswersId);
    }

    // ---------------- 照实计数 ----------------

    [Fact]
    public void 被拒的调用照实计数且永不折叠()
    {
        var flow = Flow();
        User(flow, "跑一下");
        Say(flow, "[TOOL] exec {\"cmd\":\"rm -rf /\"} risk: none");
        Denied(flow, "exec → 拒绝：硬红线（全局破坏面）");
        Say(flow, "[DONE] 换个安全做法");

        var lifecycle = LifecycleAggregator.Aggregate(flow).Lifecycles[0];

        Assert.Equal(1, lifecycle.Denied);
        Assert.Equal(0, lifecycle.ToolCalls);
        var denied = Assert.Single(lifecycle.Trace, e => e.NeverFold);
        Assert.Equal(SessionEventKind.ToolDenied, denied.Kind);
    }

    [Fact]
    public void 终局块冲突被记下来而不是静默取其一()
    {
        var flow = Flow();
        User(flow, "收尾");
        Say(flow, "[DONE] 一边\n[NO-SOLUTION] 另一边");

        var lifecycle = LifecycleAggregator.Aggregate(flow).Lifecycles[0];

        Assert.Equal(1, lifecycle.TerminalConflicts);
        Assert.True(lifecycle.IsSettled);
    }

    [Fact]
    public void 会话装配事件不属于任何任务()
    {
        var flow = Flow();
        flow.Append(SessionEventKind.Skill, "S-lc-000 技能正文", "skill-repo/x/L3.jsonl");
        User(flow, "第一件");
        Say(flow, "[DONE] 好了");

        var report = LifecycleAggregator.Aggregate(flow);

        Assert.Single(report.Lifecycles);
        Assert.Equal(1, report.SetupEvents);
        Assert.Equal(3, report.TotalEvents);
        // 对账：装配 + 各生命周期轨迹 = 总条数（一个不丢）
        Assert.Equal(report.TotalEvents, report.SetupEvents + report.Lifecycles.Sum(l => l.Trace.Count));
    }

    // ---------------- 用量：不知道就说不知道 ----------------

    [Fact]
    public void 没有用量账本时用量为空而不是零()
    {
        var flow = Flow();
        User(flow, "问一句");
        Say(flow, "[DONE] 答完了");

        var lifecycle = LifecycleAggregator.Aggregate(flow).Lifecycles[0];

        Assert.Null(lifecycle.Usage);
        Assert.Null(LifecycleAggregator.Aggregate(flow).Usage);
    }

    [Fact]
    public void 用量按位置分配给轮次并合计()
    {
        var flow = Flow();
        User(flow, "两轮");
        Say(flow, "第一轮。[TOOL] read {\"path\":\"a\"}");
        Tool(flow, "read a → ok");
        Say(flow, "[DONE] 第二轮收尾");

        var usages = new List<LifecycleTurnUsage>
        {
            new(1_000, 900, 20, 120.5),
            new(1_200, 1_100, 30, 80.25),
        };

        var lifecycle = LifecycleAggregator.Aggregate(flow, usages).Lifecycles[0];

        Assert.NotNull(lifecycle.Usage);
        Assert.Equal(2, lifecycle.Usage!.Turns);
        Assert.Equal(2_200, lifecycle.Usage.PromptTokens);
        Assert.Equal(2_000, lifecycle.Usage.CachedTokens);
        Assert.Equal(200, lifecycle.Usage.UncachedTokens);
        Assert.Equal(50, lifecycle.Usage.CompletionTokens);
        Assert.Equal(200.75, lifecycle.Usage.ElapsedMs, 3);
        Assert.Equal(2, lifecycle.Usage.Turns);
        Assert.Contains("2 轮", lifecycle.Usage.Describe());
    }

    [Fact]
    public void 用量少给了几轮_剩下的记为未知而不是零()
    {
        var flow = Flow();
        User(flow, "两轮");
        Say(flow, "第一轮");
        Say(flow, "[DONE] 第二轮");

        var lifecycle = LifecycleAggregator.Aggregate(flow, [new LifecycleTurnUsage(500, 400, 10, 50)]).Lifecycles[0];

        Assert.NotNull(lifecycle.Usage);
        Assert.Equal(1, lifecycle.Usage!.Turns);   // 只记了 1 轮 —— 记账口径，不是轮数口径
        Assert.Equal(2, lifecycle.Turns);
        Assert.Equal(500, lifecycle.Usage.PromptTokens);
    }

    // ---------------- 纯函数 ----------------

    [Fact]
    public void 同一条流喂两次结果相同()
    {
        var flow = Flow();
        User(flow, "第一件");
        Say(flow, "看看。[TOOL] read {\"path\":\"a\"}");
        Tool(flow, "read a → ok");
        Say(flow, "[NEED-USER] 要不要继续");
        User(flow, "要");
        Say(flow, "[DONE] 完了");

        var first = LifecycleAggregator.Aggregate(flow);
        var second = LifecycleAggregator.Aggregate(flow);

        Assert.Equal(first.Describe(), second.Describe());
        Assert.Equal(first.Lifecycles.Count, second.Lifecycles.Count);
        for (var i = 0; i < first.Lifecycles.Count; i++)
        {
            Assert.Equal(first.Lifecycles[i].Describe(), second.Lifecycles[i].Describe());
            Assert.Equal(first.Lifecycles[i].Result, second.Lifecycles[i].Result);
            Assert.Equal(first.Lifecycles[i].Trace.Count, second.Lifecycles[i].Trace.Count);
        }
    }

    [Fact]
    public void 从流文件聚合_与直接从流聚合一致()
    {
        var path = Path.Combine(Path.GetTempPath(), "agentruntime-lifecycle-tests", $"{Guid.NewGuid():N}.jsonl");
        var store = new SessionStreamStore(path);
        var flow = Flow();
        User(flow, "第一件");
        Say(flow, "[DONE] 好了");
        foreach (var @event in flow.Events)
        {
            store.Append(@event);
        }

        var fromFile = LifecycleAggregator.AggregateFile(path);
        var fromMemory = LifecycleAggregator.Aggregate(new SessionStreamStore(path).Load());

        Assert.Equal(fromMemory.Describe(), fromFile.Describe());
        Assert.Single(fromFile.Lifecycles);
        Assert.Equal("第一件", fromFile.Lifecycles[0].Request);
    }

    [Fact]
    public void 空流不炸且报告为空()
    {
        var report = LifecycleAggregator.Aggregate(new SessionAppendStream());

        Assert.Empty(report.Lifecycles);
        Assert.Null(report.Current);
        Assert.Null(report.Open);
        Assert.Null(report.Usage);
        Assert.Equal(0, report.Turns);
    }
}
