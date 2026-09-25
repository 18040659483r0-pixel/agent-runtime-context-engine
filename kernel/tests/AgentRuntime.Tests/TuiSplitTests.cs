using AgentRuntime.Core;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Skill;
using System.Text;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Modules;
using AgentRuntime.Presentation;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **双栏界面（<c>--ui split</c>）的测试** —— 对应 <c>docs/DESIGN-V4.4-TUI.md</c> §二 / §二·1。
/// <para>守四件事：</para>
/// <list type="number">
/// <item><b>T1</b>：右栏的字节/指纹来自**真发出去的那一份请求**（<c>PromptBytes</c>），且看面板不改字节。</item>
/// <item><b>Δ 正确</b>：逐区增量 = 当前 − 上一次刷新，写操作与轮次各自算对。</item>
/// <item><b>降级可判</b>：宽 <c>&lt;100</c> / 非 TTY / 显式 plain ⇒ 纯文本（判定是纯函数，可单测）。</item>
/// <item><b>T6</b>：双栏面同样 <c>/ablate protocol</c> 必错。</item>
/// </list>
/// <para>⚠️ 本类与其它「抓 Console」的测试**同集合串行**：<c>Console</c> 是进程级的，并行会互相串台。</para>
/// </summary>
[Collection("console-out")]
public sealed class TuiSplitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SplitSession NewSession(TuiHarness h, AgentRuntime.Core.Session.TuiStateStore? tuiState = null) =>
        new(h.Host, h.Ledger, h.Router, verbose: false, tuiState: tuiState);

    private static SplitRegionRow Row(SplitFrame frame, StackRegion region) =>
        frame.Regions.Single(r => r.Region == region);

    // ---------------- T1 ----------------

    [Fact]
    public async Task Split_T1_帧里的字节就是真发出去的那一份()
    {
        using var h = new TuiHarness("split-t1");
        var session = NewSession(h);
        await session.StartAsync(Ct);

        await session.SubmitAsync("第一句", Ct);

        Assert.NotNull(h.Client.LastRequest);       // 真发了一轮
        Assert.NotNull(session.Shown);

        // ① 显示的指纹/字节 = PromptBytes(真正发出去的那份请求)。
        Assert.Equal(PromptBytes.Sha256Of(h.Client.LastRequest!), session.Shown!.Sha256);
        Assert.Equal(PromptBytes.CountOf(h.Client.LastRequest!), session.Shown.Bytes);

        // ② 区栈里每一段的指纹，都能在**真发出去的请求的消息里**找到同字节的那一条
        //    （T1 的取证：显示的字节 = 请求里的字节，不是另拼的一份）。
        var contributed = h.Client.LastRequest!.Messages.Count - 1;
        Assert.Equal(contributed, session.Shown.Layers.Sum(l => l.MessageCount));

        var inRequest = h.Client.LastRequest.Messages
            .Take(contributed)
            .Select(m => StackPanel.FingerprintOf(m.Content))
            .ToList();
        foreach (var segment in session.Shown.Layers.SelectMany(l => l.Segments))
        {
            Assert.Contains(segment.Fingerprint, inRequest);
        }

        // ③ 看面板不改字节：面板调用前后，预览哈希必须相同。
        var before = PromptBytes.Sha256Of(await h.Host.PreviewRequestAsync("第二句", Ct));
        foreach (var command in new[] { "/stack", "/hits", "/tail", "/draft", "/focus", "/modules" })
        {
            var outcome = await h.Router.ExecuteAsync(command, h.Stderr, Ct);
            Assert.False(outcome.IsError, Flatten(outcome.Lines));
        }

        var after = PromptBytes.Sha256Of(await h.Host.PreviewRequestAsync("第二句", Ct));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Split_区栈字节合计与请求正文可比()
    {
        using var h = new TuiHarness("split-t1b");
        var session = NewSession(h);
        await session.StartAsync(Ct);
        await session.SubmitAsync("一句话", Ct);

        var frame = session.Frame(96, 30);

        // 合计 = 各区正文按规范序拼接（零注入区不贡献字节）。
        Assert.Equal(StackPanel.TotalBytes(session.Shown!.Layers), frame.Totals.RegionBytes);

        // 区栈字节 ≤ 请求体字节（请求体还含 JSON 包装与用户消息）。
        Assert.True(frame.Totals.RegionBytes < frame.Totals.RequestBytes, "区栈字节应小于整份请求体");

        // 六行区 + 次序 1..6（R3 居末）。
        Assert.Equal(6, frame.Regions.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6], frame.Regions.Select(r => r.Order).ToArray());
        Assert.Equal(StackRegion.R3, frame.Regions[^1].Region);
    }

    // ---------------- Δ ----------------

    [Fact]
    public void Split_Δ_由基准表算得()
    {
        // 纯函数层：基准里没有的区视为「原本就这么多」（Δ=0），有的区给差。
        var layers = StackPanel.Compose(
        [
            new ContributedMessage(new ProtocolModule(), new AgentRuntime.Models.ChatMessage { Role = "system", Content = "协议" }),
        ]);

        var none = SplitRenderer.Deltas(layers, baseline: null);
        Assert.Equal(0, none[StackRegion.R1P]);

        var baseline = new Dictionary<StackRegion, int> { [StackRegion.R1P] = layers[0].Bytes - 40 };
        var deltas = SplitRenderer.Deltas(layers, baseline);
        Assert.Equal(40, deltas[StackRegion.R1P]);
        Assert.Equal(0, deltas[StackRegion.R2]);
    }

    [Fact]
    public async Task Split_Δ_写白板只动R4_跑轮次后R2增长()
    {
        using var h = new TuiHarness("split-delta");
        var session = NewSession(h);
        await session.StartAsync(Ct);

        var start = session.Frame(96, 30);

        // ① 写白板（走既有写入口）⇒ 只有 R4 变，且 Δ = 之后的字节 − 之前的字节。
        await session.SubmitAsync("/tail \"当前任务: 甲\\n待办: 乙\"", Ct);
        var afterTail = session.Frame(96, 30);

        var r4 = Row(afterTail, StackRegion.R4);
        Assert.Equal(r4.Bytes - Row(start, StackRegion.R4).Bytes, r4.Delta);
        Assert.True(r4.Delta > 0, "写白板后 R4 应为正增量");
        foreach (var region in new[] { StackRegion.R1P, StackRegion.R1, StackRegion.R3 })
        {
            Assert.Equal(0, Row(afterTail, region).Delta);
        }

        // ② 跑一轮之后（本轮 prompt 已含白板），再跑一轮 ⇒ R2 因上一轮的事件而增长。
        await session.SubmitAsync("第一句", Ct);
        await session.SubmitAsync("第二句", Ct);
        var afterTwoTurns = session.Frame(96, 30);

        Assert.True(Row(afterTwoTurns, StackRegion.R2).Delta > 0, "上一轮的事件应让 R2 正增长");
        Assert.Equal(Row(afterTwoTurns, StackRegion.R4).Bytes, Row(afterTwoTurns, StackRegion.R4).Bytes);
    }

    [Fact]
    public async Task Split_Δ_消融一区后该区为零注入()
    {
        using var h = new TuiHarness("split-ablate");
        var session = NewSession(h);
        await session.StartAsync(Ct);
        await session.SubmitAsync("/draft \"构想: 甲\"", Ct);

        var before = session.Frame(96, 30);
        Assert.True(Row(before, StackRegion.R5).Bytes > 0);

        await session.SubmitAsync("/ablate dynamic-draft", Ct);
        var after = session.Frame(96, 30);

        var r5 = Row(after, StackRegion.R5);
        Assert.Equal(0, r5.Bytes);
        Assert.Equal(-Row(before, StackRegion.R5).Bytes, r5.Delta);
        Assert.Equal(0, Row(after, StackRegion.R1P).Delta);
    }

    [Fact]
    public async Task Split_ablate_右栏可见本会话已摘且Δ当场刷新()
    {
        using var h = new TuiHarness("split-ablate-feedback");
        var session = NewSession(h);
        await session.StartAsync(Ct);
        await session.SubmitAsync("/draft \"构想: 甲\"", Ct);

        var before = session.Frame(96, 30);
        Assert.True(Row(before, StackRegion.R5).Bytes > 0);

        await session.SubmitAsync("/ablate dynamic-draft", Ct);
        var after = session.Frame(96, 30);

        // ① 右栏详细块显示消融面板输出，并点名「本会话已摘」。
        Assert.StartsWith("面板 /ablate", after.DetailTitle ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("本会话已摘", Flatten(after.DetailLines), StringComparison.Ordinal);
        Assert.Contains("dynamic-draft", Flatten(after.DetailLines), StringComparison.Ordinal);

        // ② 状态块当场刷新：R5 归零、Δ 为负（不是「屏上改了、右栏还是旧的」）。
        var r5 = Row(after, StackRegion.R5);
        Assert.Equal(0, r5.Bytes);
        Assert.Equal(-Row(before, StackRegion.R5).Bytes, r5.Delta);
    }

    // ---------------- 降级判定 ----------------

    [Theory]
    [InlineData(true, false, true, true, 96, 30, UiMode.Split, "")]
    [InlineData(false, false, true, true, 96, 30, UiMode.Plain, "--ui plain")]
    [InlineData(true, true, false, false, 0, 0, UiMode.Split, "无头帧")]
    [InlineData(true, false, false, true, 96, 30, UiMode.Plain, "stdout")]
    [InlineData(true, false, true, false, 96, 30, UiMode.Plain, "stdin")]
    [InlineData(true, false, true, true, 71, 30, UiMode.Plain, "72")]
    [InlineData(true, false, true, true, 96, 19, UiMode.Plain, "20")]
    public void UiPolicy_降级判定(
        bool requestsSplit,
        bool headless,
        bool stdoutTty,
        bool stdinTty,
        int width,
        int height,
        UiMode expected,
        string reasonFragment)
    {
        var decision = UiPolicy.Decide(new UiPolicy.Environment(requestsSplit, headless, stdoutTty, stdinTty, width, height));

        Assert.Equal(expected, decision.Mode);
        if (reasonFragment.Length > 0)
        {
            Assert.Contains(reasonFragment, decision.Reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UiPolicy_取值解析与帧尺寸()
    {
        Assert.True(UiPolicy.ParseRequest(null));
        Assert.True(UiPolicy.ParseRequest("split"));
        Assert.False(UiPolicy.ParseRequest("plain"));

        var bad = Assert.Throws<InvalidDataException>(() => UiPolicy.ParseRequest("fancy"));
        Assert.Contains("--ui", bad.Message, StringComparison.Ordinal);

        // 默认 = 笔记本半屏目标 96×30；--cols / --rows / --frame-size 可任意组合。
        Assert.Equal((96, 30), UiPolicy.ParseFrameSize(null));
        Assert.Equal((80, 24), UiPolicy.ParseFrameSize(null, cols: 80, rows: 24));
        Assert.Equal((80, 30), UiPolicy.ParseFrameSize("100x30", cols: 80));
        Assert.Equal((120, 40), UiPolicy.ParseFrameSize("120x40"));
        Assert.Throws<InvalidDataException>(() => UiPolicy.ParseFrameSize("40x10"));
        Assert.Throws<InvalidDataException>(() => UiPolicy.ParseFrameSize("nonsense"));
    }

    // ---------------- 帧几何 / 可复算 ----------------

    [Fact]
    public async Task Split_帧几何固定_不随轮次增长()
    {
        using var h = new TuiHarness("split-frame");
        var session = NewSession(h);
        await session.StartAsync(Ct);

        var first = SplitRenderer.Render(session.Frame(96, 30));
        await session.SubmitAsync("甲", Ct);
        await session.SubmitAsync("乙", Ct);
        var later = SplitRenderer.Render(session.Frame(96, 30));

        // 行数 = 高；每行显示宽度 = 宽（边框不会因中文漂移）。
        Assert.Equal(30, first.Count);
        Assert.Equal(30, later.Count);
        foreach (var line in later)
        {
            Assert.Equal(96, TerminalText.WidthOf(line));
        }

        // 纯文本、无 ANSI 转义（可落盘、可 diff）。
        Assert.DoesNotContain("\u001b", SplitRenderer.RenderText(session.Frame(96, 30)), StringComparison.Ordinal);

        // 右栏固定区不随轮次增长：区表所在的那一段（首个「合计」行）行号相同。
        var firstTotal = first.ToList().FindIndex(l => l.Contains("合计", StringComparison.Ordinal));
        var laterTotal = later.ToList().FindIndex(l => l.Contains("合计", StringComparison.Ordinal));
        Assert.Equal(firstTotal, laterTotal);

        // 同一帧渲染两次 ⇒ 逐字节相同（可复算）。
        Assert.Equal(
            SplitRenderer.RenderText(session.Frame(96, 30)),
            SplitRenderer.RenderText(session.Frame(96, 30)));
    }

    [Fact]
    public async Task Split_区目录默认全展开_选中项就地展开()
    {
        using var h = new TuiHarness("split-expand");
        var session = NewSession(h);
        await session.StartAsync(Ct);

        // ① 默认态（v13）：六项**全部就地展开**（主人 2026-09-21 23:5x：「你全部展开吧」）——
        //    正文摊在每个模块自己那一行下面，不用再按 Enter。旧行为（默认只展开 R4/R5/R3 + 落盘）已废。
        var fresh = session.Frame(96, 30);
        Assert.Equal(6, fresh.Menu.Count);
        Assert.True(fresh.Menu[0].Expanded);          // 菜单上的标记 = 「**选中项**就地展开着」，不是「这一项展开着」
        Assert.NotNull(session.Expanded);
        Assert.Empty(fresh.DetailLines);
        Assert.Equal(StackPanel.Ordered.Count, session.InlineExpanded.Count);

        // ② 面板命令的输出进右栏可扩展区（标题标明是哪个面板）。
        await session.SubmitAsync("/tail \"当前任务: 甲\"", Ct);
        var withPanel = session.Frame(96, 30);
        Assert.StartsWith("面板 /tail", withPanel.DetailTitle ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[尾部]", Flatten(withPanel.DetailLines), StringComparison.Ordinal);

        // ③ Esc 清除面板输出（**不动就地展开那几项** —— 展开态归目录键，不归面板键）。
        session.CollapseAll();
        Assert.Empty(session.Frame(96, 30).DetailLines);
        Assert.Equal(StackPanel.Ordered.Count, session.InlineExpanded.Count);

        // ④ 选中 R4：目录项标为展开，**并且不覆盖详细块**（v12：Enter = 就地插入 / 收起）。
        var r4Index = fresh.Menu.ToList().FindIndex(e => e.Region == StackRegion.R4);
        session.SelectMenu(r4Index);
        Assert.Equal(StackRegion.R4, session.Expanded);
        Assert.True(session.Frame(96, 30).Menu[r4Index].Expanded);

        // ⑤ Enter = 收起这一项（就地插入的反面）；详细块仍然干净（没被正文占掉）。
        session.ToggleExpandSelected();
        var collapsed = session.Frame(96, 30);
        Assert.Null(session.Expanded);
        Assert.DoesNotContain(StackRegion.R4, session.InlineExpanded);
        Assert.Empty(collapsed.DetailLines);
        Assert.Null(collapsed.DetailTitle);

        // ⑥ 再按一次：就地插回。
        session.ToggleExpandSelected();
        Assert.Equal(StackRegion.R4, session.Expanded);
        Assert.Contains(StackRegion.R4, session.InlineExpanded);
    }


    // ---------------- T6：协议区不可摘（双栏面） ----------------

    [Fact]
    public async Task Split_ablate_protocol必须报错()
    {
        using var h = new TuiHarness("split-t6");
        var session = NewSession(h);
        await session.StartAsync(Ct);

        var before = h.Host.Modules.Select(m => m.Name).ToArray();

        await session.SubmitAsync("/ablate protocol", Ct);

        // 服务层仍然是「绕不过去的类型事实」。
        Assert.Throws<InvalidDataException>(() => AblationService.EnsureAblatable("protocol"));

        // 双栏面：错误进右栏（标题标明错误）+ 左栏留一行提示；模块集纹丝不动。
        var frame = session.Frame(96, 30);
        Assert.Contains("错误", frame.DetailTitle!, StringComparison.Ordinal);
        Assert.Contains("不可摘", Flatten(frame.DetailLines), StringComparison.Ordinal);
        Assert.Contains("不可摘", Flatten(frame.Conversation.Select(c => c.Text)), StringComparison.Ordinal);
        Assert.Equal(before, h.Host.Modules.Select(m => m.Name).ToArray());
        Assert.Contains(h.Host.Modules, m => m is ProtocolModule);
    }

    // ---------------- 无头一帧落盘（端到端） ----------------

    [Fact]
    public async Task Split_无头模式写出一帧纯文本文件()
    {
        using var h = new TuiHarness("split-headless");
        var session = NewSession(h);
        var path = h.Workspace.File("frame.txt");
        var originalIn = Console.In;

        try
        {
            Console.SetIn(new StringReader("这轮的部署代号确认一下\n/hits\n/quit\n"));
            await session.RunHeadlessAsync(path, 96, 30, Ct);
        }
        finally
        {
            Console.SetIn(originalIn);
        }

        Assert.True(File.Exists(path));
        var lines = File.ReadAllLines(path);

        Assert.Equal(30, lines.Length);
        Assert.All(lines, l => Assert.Equal(96, TerminalText.WidthOf(l)));
        Assert.DoesNotContain(lines, l => l.Contains('\u001b'));

        var text = File.ReadAllText(path);
        Assert.Contains("这轮的部署代号确认一下", text, StringComparison.Ordinal);  // 左栏有对话
        Assert.Contains("R1-P", text, StringComparison.Ordinal);                    // 状态块有区表
        Assert.Contains("合计", text, StringComparison.Ordinal);
        Assert.Contains("/hits", text, StringComparison.Ordinal);              // 面板输出进详细块
    }

    // ---------------- 几何：状态块固定 / 半屏放得下 / 窄矮自适应 ----------------

    [Theory]
    [InlineData(96, 30)]   // 笔记本半屏目标
    [InlineData(80, 24)]   // 不得垮的下限
    public async Task 几何_半屏目标与下限都放得下(int width, int height)
    {
        using var h = new TuiHarness($"split-geom-{width}x{height}");
        var session = NewSession(h);
        await session.StartAsync(Ct);
        await session.SubmitAsync("/tail \"当前任务: 甲\"", Ct);
        await session.SubmitAsync("一句话", Ct);

        var layout = new SplitLayout(width, height);
        var lines = SplitRenderer.Render(session.Frame(width, height));
        var text = string.Join("\n", lines);

        // ① 尺寸精确：行数 = 高，每行显示宽度 = 宽（中文也不漂）。
        Assert.Equal(height, lines.Count);
        Assert.All(lines, l => Assert.Equal(width, TerminalText.WidthOf(l)));

        // ② 顶框：全档给六区逐区 ≈token；中/底线档把区栈压成合计（口径按版式档位，主人 02:2x 定）。
        if (layout.TopBandContentRows >= 5)
        {
            foreach (var id in new[] { "R1-P", "R1", "R2", "R4", "R5", "R3" })
            {
                Assert.Contains(id, text, StringComparison.Ordinal);
            }
        }
        else
        {
            Assert.Contains("≈token", text, StringComparison.Ordinal);
        }

        Assert.Contains("合计", text, StringComparison.Ordinal);
        Assert.Contains("本 task", text, StringComparison.Ordinal);
        Assert.Contains("agent-runtime > ", text, StringComparison.Ordinal);

        // ③ 单栏：顶框一条线到底（**没有分缝 ┬**），每行两端都是框线（不错位）。
        Assert.Contains(lines, l => l.StartsWith('┌') && l.EndsWith('┐'));
        Assert.DoesNotContain(lines, l => l.Contains('┬'));
        Assert.All(lines, l => Assert.Contains(l[0], "┌│├└"));
        Assert.All(lines, l => Assert.Contains(l[^1], "┐│┤┘"));

        // ④ 首条分隔线 = 顶框的下框线；正文从它下面开始。
        var divider = lines.ToList().FindIndex(l => l.Contains('├') && l.Contains('┤'));
        Assert.Equal(layout.TopBandRows - 1, divider);
    }

    [Fact]
    public async Task 几何_顶框固定_不随正文滚动()
    {
        using var h = new TuiHarness("split-fixed");
        var session = NewSession(h);
        await session.StartAsync(Ct);
        // ⚠️ 对话里**只有人话**（每轮账本行不进对话）⇒ 要真正超过一屏得多来几轮。
        for (var i = 1; i <= 40; i++)
        {
            await session.SubmitAsync($"第{i}句话", Ct);
        }

        var layout = new SplitLayout(96, 30);
        var beforeLines = SplitRenderer.Render(session.Frame(96, 30));

        session.ScrollBy(-999);   // 正文向上滚到顶（↑ 的方向）
        var afterLines = SplitRenderer.Render(session.Frame(96, 30));

        // ① 正文那一段确实滚了。
        var from = layout.TopBandRows;
        var to = beforeLines.Count - 2 - SplitLayout.StripRowsFor(30) - layout.InputRows;
        Assert.True(to > from + 1, "正文得真的超过一屏，这条才有意义");
        Assert.NotEqual(
            beforeLines.Skip(from).Take(to - from).ToArray(),
            afterLines.Skip(from).Take(to - from).ToArray());

        // ② **顶框逐字节不动** —— v15 单栏的「固定」就指这一条（原先是「右列固定」）。
        for (var i = 0; i < layout.TopBandRows; i++)
        {
            Assert.Equal(beforeLines[i], afterLines[i]);
        }
    }

    [Fact]
    public void 输入区_行高随内容立刻变化_只在停稳后收窄()
    {
        // v13 修 BUG（主人 2026-09-21 报）：旧的两段口径（键入中只看显式换行 / 停稳才软换行）
        // 会让行高**跳回一行**、且要等几百毫秒才长高。
        // 新口径：**行高只按内容算**（立刻、软换行、上限 5 行）；收窄由宿主在停稳后做。
        var longLine = new string('中', 60);          // 显示宽度 120 列

        // 立刻就是 2 行（不再等停稳）。
        Assert.Equal(2, SplitRenderer.InputRowsFor(longLine, 80));
        Assert.Equal(1, SplitRenderer.InputRowsFor("zhongwen", 80));

        // 显式换行照旧算（它是人手敲的）。
        Assert.Equal(2, SplitRenderer.InputRowsFor("第一行\n短", 80));
        Assert.Equal(3, SplitRenderer.InputRowsFor("a\nb\nc", 80));

        // 上限：再长也只在 5 行里滚。
        Assert.Equal(SplitLayout.MaxInputRows, SplitRenderer.InputRowsFor(new string('中', 600), 80));
        Assert.Equal(400, SplitRenderer.InputSettleMs);                 // 它现在只当「收窄防抖窗口」

        // 行高与渲染**同一把尺子**：段数 = 逻辑行占的显示行数。
        var segments = SplitRenderer.InputSegments(longLine, 60);
        Assert.Equal(2, segments.Count);
        Assert.All(segments, s => Assert.True(TerminalText.WidthOf(s) <= 60));   // 每段不超可用列宽
        Assert.Equal(longLine, string.Concat(segments));                                            // 切段不丢字

        // 帧高与正文行数跟着变（行高长了一行，正文就少一行）。
        using var h = new TuiHarness("tui-input-rows");
        var session = NewSession(h);
        var one = SplitLayoutFor(session, "");
        var two = SplitLayoutFor(session, "a\nb");
        Assert.Equal(one.BodyRows - 1, two.BodyRows);
    }

    private static SplitLayout SplitLayoutFor(AgentRuntime.Tui.SplitSession session, string input)
    {
        _ = session;
        var rows = AgentRuntime.Tui.SplitRenderer.InputRowsFor(input, 80);
        return new SplitLayout(80, 30, rows);
    }

    /// <summary>
    /// **自绘标记格 == <c>CaretPosition</c> 指的格**（2026-09-22「IME 第二刀」的不变量）。
    /// <para>为什么必须有这条：终端光标定位用的是**视觉列**，而帧行里 CJK 占 2 格 ⇒ 拿字符下标当列号
    /// 在纯 ASCII 输入上照样绿，一打中文就偏。空 / ASCII / CJK 三种输入各测一次。</para>
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("中文")]
    public void 光标_定位格就是自绘标记格(string input)
    {
        var frame = FrameWithInput(input, input.Length);
        var caret = SplitRenderer.CaretPosition(frame);

        Assert.NotNull(caret);
        var (row, col) = caret!.Value;
        var lines = SplitRenderer.Render(frame);

        Assert.InRange(row, 1, lines.Count);
        Assert.Equal("_", TerminalText.SliceColumns(lines[row - 1], col - 1, 1));

        // 行号必须落在**输入面板**里（末行 = 下边框，其上方 InputRows 行才是面板）。
        var layout = new SplitLayout(frame.Width, frame.Height, frame.InputRows);
        Assert.InRange(row, lines.Count - layout.InputRows, lines.Count - 1);
    }

    /// <summary>只给一条输入缓冲的帧（测光标定位用；其余字段取空值）。</summary>
    private static SplitFrame FrameWithInput(string input, int caret, int width = 80, int height = 24) => new()
    {
        Title = "AgentRuntime TUI",
        Subtitle = "t",
        Conversation = [],
        Regions = [],
        Totals = new SplitTotals(0, 0, null),
        RequestNote = string.Empty,
        Menu = [],
        MenuSelection = 0,
        DetailLines = [],
        DetailScroll = 0,
        ConversationScroll = 0,
        Input = input,
        InputRows = SplitLayout.InputPanelRowsFor(height),
        Caret = caret,
        Focus = PaneFocus.Input,
        Width = width,
        Height = height,
    };

    /// <summary>只给一段对话流的帧（测对话行渲染用；其余字段取空值）。</summary>
    private static SplitFrame FrameWithConversation(params ConversationEntry[] entries) => new()
    {
        Title = "AgentRuntime TUI",
        Subtitle = "t",
        Conversation = entries,
        Regions = [],
        Totals = new SplitTotals(0, 0, null),
        RequestNote = string.Empty,
        Menu = [],
        MenuSelection = 0,
        DetailLines = [],
        DetailScroll = 0,
        ConversationScroll = 0,
        Input = string.Empty,
        InputRows = SplitLayout.InputPanelRowsFor(24),
        Caret = 0,
        Focus = PaneFocus.Input,
        Width = 80,
        Height = 24,
    };

    [Fact]
    public void 用户行_you大于号前缀_整行斜体_上下各空一行()
    {
        // 主人 2026-09-22 定：对话流里**自己问的那一句**要一眼瞄到 ⇒ `you > ` 前缀（两边各一个空格）
        // + 整行**斜体** + 上下各空一行。三条都是契约，缺一条人就看不出「这句是我问的」。
        var frame = FrameWithConversation(
            new ConversationEntry("you", "再确认一次部署代号"),
            new ConversationEntry("ai", "好。"));
        var layout = new SplitLayout(frame.Width, frame.Height, frame.InputRows);
        var rows = SplitRenderer.ConversationRich(frame, layout).ToList();

        var at = rows.FindIndex(static r => r.Text.StartsWith("you > ", StringComparison.Ordinal));
        Assert.True(at > 0 && at < rows.Count - 1, $"用户行要在中间（实际第 {at} 行 / 共 {rows.Count} 行）");
        Assert.Equal("you > 再确认一次部署代号", rows[at].Text);
        Assert.Equal(string.Empty, rows[at - 1].Text);                       // 上面空一行
        Assert.Equal(string.Empty, rows[at + 1].Text);                       // 下面空一行

        // 整行统一斜体：行内标记**不抢**角色（用户输入逐字显示，自己打的 `**` / `> ` 不会被当标记）。
        var span = Assert.Single(rows[at].Spans);
        Assert.Equal(StyleRole.You, span.Role);
        Assert.Equal(0, span.Start);
        Assert.Equal(rows[at].Text.Length, span.Length);

        // 落屏那一笔：SGR `3` = 斜体；三档色深同一个码（字体属性不随色深变）。
        Assert.Equal("\u001b[3m", StyleTable.Standard.Sgr(StyleRole.You));
        Assert.Equal("\u001b[3m", StyleTable.Standard16.Sgr(StyleRole.You));
        Assert.Equal("\u001b[3m", StyleTable.StandardTrueColor.Sgr(StyleRole.You));
    }

    [Fact]
    public async Task 本轮被取消_不再把整个会话带走()
    {
        // 2026-09-22 01:39 主人真机报「说一句收尾就自动退出」：Ctrl-C 一下 / 思考中按 Enter（插话）取消的是**这一轮**，
        // 而取消以前没人接 ⇒ TaskCanceledException 冒到 Main ⇒ 未捕获 ⇒ 整个 TUI 被 abort（实测退出码 -6）。
        // 这里用**会挂住的假模型**把取消卡在「请求在飞」那一刻（真实路径）。
        using var h = new TuiHarness("split-cancel");
        var hanging = new FakeModelClient(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);   // 一直等到被取消
            return null!;
        });
        using var host = RuntimeHost.BootWith(http: null, hanging, h.Config);
        var router = new PanelRouter(host, h.Ledger, h.Ablation);
        var session = new SplitSession(host, h.Ledger, router, verbose: false, tuiState: null);
        await session.StartAsync(Ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = session.SubmitAsync("这句会被取消", cts.Token);
        await Task.Delay(300, Ct);      // 等它真的进到「等远端」
        cts.Cancel();                   // = Ctrl-C 一下

        await run;                      // 必须**不抛**（取消不是崩溃）

        var lines = SplitRenderer.Render(session.Frame(96, 30));
        Assert.Contains(lines, l => l.Contains("本轮已中断", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 口径_顶框说本task与近token_不写本轮()
    {
        // 主人 2026-09-22 02:0x / 02:2x：「轮」= 一次模型调用（计算单位）；「生命周期 / task」= 一次用户请求。
        // v15 单栏：口径全在**顶部框**（会话 / 区栈）—— 不再有「状态块」与「Δ列」。
        using var h = new TuiHarness("split-wording");
        var session = NewSession(h);
        await session.StartAsync(Ct);
        await session.SubmitAsync("口径", Ct);

        var lines = SplitRenderer.Render(session.Frame(96, 30));
        Assert.Contains(lines, l => l.Contains("会话", StringComparison.Ordinal) && l.Contains("本 task", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("区栈", StringComparison.Ordinal) && l.Contains("≈token", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("Δ本轮", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("状态 · 本轮", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 提示条_带海蓝角色_且正文与纯文本帧逐字节一致()
    {
        // 主人 2026-09-22 02:0x：输入区上方那条提示改海蓝（StyleRole.Hint）。
        // 角色是零宽标记 ⇒ 不得改变帧正文（plain == rich 闸门照旧）。
        using var h = new TuiHarness("split-hint");
        var session = NewSession(h);
        await session.StartAsync(Ct);
        await session.SubmitAsync("提示条", Ct);

        var rich = SplitRenderer.RenderRich(session.Frame(96, 30));
        var plain = SplitRenderer.Render(session.Frame(96, 30));

        Assert.Contains(rich, static r => r.Spans.Any(static s => s.Role == StyleRole.Hint));
        Assert.Equal(plain, rich.Select(static r => r.Text).ToArray());
    }

    [Fact]
    public void 终局正文不再两遍_info行给卡让位()
    {
        // 2026-09-22 01:3x 主人真机报：「得解后终局的内容描述了两遍」——
        // info 行 `[终局] 得解[DONE]：<正文>` 与卡上 `结果：<正文>` 是**同一段文字**。
        // split 模式掐掉 info 行（卡承载它），辅助行照旧显示（卡上没有它们）。
        var response = "做完了。\n[DONE] R1 协议区正文无可零损精简";
        var terminal = TerminalReport.Parse(response).Describe();

        var kept = SplitSession.VisibleInfoLines(
            [terminal, "[终局] 它在等你（要决定 / 要信息）—— 回一句即可。"],
            response);

        Assert.Single(kept);
        Assert.Contains("它在等你", kept[0], StringComparison.Ordinal);
        Assert.DoesNotContain(kept, l => l.Contains("[DONE]", StringComparison.Ordinal));
    }

    [Fact]
    public void 绘制_末行之后不写换行_整屏帧不会被上滚一行()
    {
        // 坑（2026-09-20 真终端抓到）：末行之后多写一个 \r\n ⇒ 画面整体上滚一行 ⇒
        // **顶边框（标题 / 协议版本 / task 读秒）被滚掉**。帧高 = 终端行数时，这条必须成立。
        var output = new StringWriter();
        var screen = new AgentRuntime.Tui.AnsiScreen(output);
        screen.Draw(["AAA", "BBB", "CCC"]);

        var text = output.ToString();
        Assert.Equal(2, text.Split("\r\n").Length - 1);              // 3 行 ⇒ 只在**行间**换行
        Assert.DoesNotContain("CCC\r\n", text, StringComparison.Ordinal);   // 末行之后没有换行（关键）
        Assert.Contains("CCC", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 几何_窄时省列_矮时省行_但不错位()
    {
        using var h = new TuiHarness("split-compress");
        var session = NewSession(h);
        await session.StartAsync(Ct);
        await session.SubmitAsync("/tail \"当前任务: 甲\"", Ct);

        // ① 窄（72 列）：顶框完整、行宽不漂；**指纹不再上屏**（v15）。
        var narrowLines = SplitRenderer.Render(session.Frame(72, 20));
        Assert.Equal(20, narrowLines.Count);
        Assert.All(narrowLines, l => Assert.Equal(72, TerminalText.WidthOf(l)));
        var narrowText = string.Join("\n", narrowLines);
        Assert.Contains("会话", narrowText, StringComparison.Ordinal);
        Assert.Contains("≈token", narrowText, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Frame(72, 20).Regions[0].Fingerprint, narrowText, StringComparison.Ordinal);

        // ② 矮（80×17）：顶框降到最低档（会话 + 区栈合计），正文照旧、行宽不错位。
        var shortLayout = new SplitLayout(80, 17);
        Assert.Equal(2, shortLayout.TopBandContentRows);
        var shortLines = SplitRenderer.Render(session.Frame(80, 17));
        Assert.Equal(17, shortLines.Count);
        Assert.All(shortLines, l => Assert.Equal(80, TerminalText.WidthOf(l)));
        var shortText = string.Join("\n", shortLines);
        Assert.Contains("会话", shortText, StringComparison.Ordinal);
        Assert.Contains("合计", shortText, StringComparison.Ordinal);
        Assert.Contains("agent-runtime > ", shortText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 几何_正文自顶向下填充_空状态不留半屏空白()
    {
        using var h = new TuiHarness("split-topleft");
        var session = NewSession(h);
        await session.StartAsync(Ct);

        var layout = new SplitLayout(96, 30);
        var lines = SplitRenderer.Render(session.Frame(96, 30));

        // 尺寸精确；正文从顶框下框线的下一行开始（不留半屏空白），抬头行有内容。
        Assert.Equal(30, lines.Count);
        Assert.Contains("对话", lines[layout.TopBandRows], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("agent-runtime > ", StringComparison.Ordinal));

        // 正文区行数 = BodyRows（顶框 + 正文 + 分隔 + 状态条 + 输入 + 下框 = 高）。
        Assert.Equal(
            layout.TopBandRows + layout.BodyRows + 1 + SplitLayout.StripRowsFor(30) + layout.InputRows + 1,
            lines.Count);
    }

    [Fact]
    public async Task 几何_80列下区栈用近token且不出现省略号()
    {
        using var h = new TuiHarness("split-narrow80");
        var session = NewSession(h);
        await session.StartAsync(Ct);
        await session.SubmitAsync("/tail \"当前任务: 甲\"", Ct);
        await session.SubmitAsync("一句话", Ct);

        var frame = session.Frame(80, 24);
        var lines = SplitRenderer.Render(frame);
        var text = string.Join("\n", lines);

        // v15：区栈在顶框里、用 **≈token**（不再有 字节 / 指纹 列）；放不下**整段丢** ⇒ 不出现省略号。
        Assert.Contains("区栈", text, StringComparison.Ordinal);
        Assert.Contains("≈token", text, StringComparison.Ordinal);
        Assert.DoesNotContain("…", text, StringComparison.Ordinal);
        Assert.DoesNotContain(frame.Regions[0].Fingerprint, text, StringComparison.Ordinal);
        Assert.All(lines, l => Assert.Equal(80, TerminalText.WidthOf(l)));
    }

    /// <summary>取一行里左列（显示宽度口径切分）。</summary>
    private static string LeftColumn(string line, int leftWidth)
    {
        var index = 1;
        var used = 0;
        while (index < line.Length && used < leftWidth)
        {
            used += TerminalText.WidthOf(line[index].ToString());
            index++;
        }

        return line[1..index];
    }

    /// <summary>取一行里右列（显示宽度口径切分 —— 不能用字符下标，中文占两格）。</summary>
    private static string RightColumn(string line, int leftWidth)
    {
        var index = 1;                 // 跳过左外框
        var used = 0;
        while (index < line.Length && used < leftWidth)
        {
            used += TerminalText.WidthOf(line[index].ToString());
            index++;
        }

        return index + 1 <= line.Length ? line[(index + 1)..^1] : string.Empty;
    }

    // ---------------- 文本几何（边框不漂移的地基） ----------------

    [Fact]
    public void TerminalText_显示宽度与补齐()
    {
        Assert.Equal(7, TerminalText.WidthOf("中文abc"));
        Assert.Equal(4, TerminalText.WidthOf("▸ R1"));
        Assert.Equal(120, TerminalText.WidthOf(TerminalText.PadTo("中文", 120)));
        Assert.Equal("中文    ", TerminalText.PadTo("中文", 8));   // 中文 = 4 格 ⇒ 补 4 空格
        Assert.Equal("   中文", TerminalText.PadLeftTo("中文", 7));
        Assert.Equal(6, TerminalText.WidthOf(TerminalText.TruncateTo("abcdefghij", 6)));
        Assert.Equal("±0", TerminalText.Delta(0));
        Assert.Equal("+12", TerminalText.Delta(12));
        Assert.Equal("-3", TerminalText.Delta(-3));
        Assert.Equal("2,815", TerminalText.Number(2815));
    }

    [Fact]
    public void TuiOptions_解析双栏开关()
    {
        var options = TuiOptions.Parse(["--ui", "plain", "--snapshot", "f.txt", "--frame-size", "100x30", "--config", "c.json"]);

        Assert.Equal("plain", options.Ui);
        Assert.Equal("f.txt", options.SnapshotPath);
        Assert.Equal("100x30", options.FrameSize);
        Assert.Equal("c.json", options.Config);
        Assert.False(options.Help);

        var sized = TuiOptions.Parse(["--ui", "split", "--snapshot", "f.txt", "--cols", "80", "--rows", "24"]);
        Assert.Equal(80, sized.Cols);
        Assert.Equal(24, sized.Rows);

        Assert.Throws<InvalidDataException>(() => TuiOptions.Parse(["--cols", "wide"]));

        var defaults = TuiOptions.Parse([]);
        Assert.Null(defaults.Ui);
        Assert.Null(defaults.SnapshotPath);
        Assert.Null(defaults.Cols);
    }

    // ---------------- 「专家」行（主人 2026-09-22 令）----------------

    /// <summary>手造一个 R1 层（只为「专家」行纯函数测试；段 id 逐字给）。</summary>
    private static StackLayer R1With(params string[] segmentIds) =>
        new(StackRegion.R1, 2, "R1", "冷冻区", [], 1, "fp", "v1",
            [.. segmentIds.Select(id => new StackSegment(id, "1", 1, "fp"))], segmentIds.Length, "文本");

    /// <summary>「专家」行的一项（id + 字节）—— 字节只决定 ≈token 显示。</summary>
    private static ExpertDomain Expert(string id, int bytes = 4) => new(id, bytes);

    /// <summary>带「专家」域清单的帧（96×30 = 最高档）。</summary>
    private static SplitFrame FrameWithExperts(IReadOnlyList<ExpertDomain> domains) => new()
    {
        Title = "AgentRuntime TUI",
        Subtitle = "t",
        Conversation = [],
        Regions = [],
        ExpertDomains = domains,
        Totals = new SplitTotals(0, 0, null),
        RequestNote = string.Empty,
        Menu = [],
        MenuSelection = 0,
        DetailLines = [],
        DetailScroll = 0,
        ConversationScroll = 0,
        Input = string.Empty,
        InputRows = SplitLayout.InputPanelRowsFor(30),
        Caret = 0,
        Focus = PaneFocus.Input,
        Width = 96,
        Height = 30,
    };

    [Fact]
    public void 专家域_只认真的装进R1的段_次序按预设域()
    {
        // 段次序故意与预设次序相反：**同占用**时输出回到 `KnowledgeDomains` 的展示次序。
        var layers = new[] { R1With("knowledge.expert.finance", "knowledge.expert.software") };
        Assert.Equal(["software", "finance"], StackPanel.ExpertDomains(layers).Select(d => d.Id));

        // global 段 / 别的区 / 空 ⇒ 一个都不报（配了不等于装了）。
        Assert.Empty(StackPanel.ExpertDomains([R1With("rules.global", "knowledge.global")]));
        Assert.Empty(StackPanel.ExpertDomains([]));
    }

    [Fact]
    public void 专家域_技能常驻层的域按预设次序()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-expert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "l1.jsonl"),
            """{"skill":"a","l1":"法则 A"}""",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(dir, "l2.jsonl"),
            """
            {"skill":"a","domain":"photography","q":"问题一","ids":["S-a-001"]}
            {"skill":"a","domain":"software","q":"问题二","ids":["S-a-002"]}
            {"skill":"b","domain":"software","q":"问题三","ids":["S-b-001"]}
            """,
            new UTF8Encoding(false));

        try
        {
            var resident = SkillResident.Load(dir);
            var only = StackPanel.ExpertDomains([], resident);
            Assert.Equal(["software", "photography"], only.Select(d => d.Id));
            Assert.True(only[0].Bytes > only[1].Bytes, "按占用降序：software 两行 > photography 一行");

            // 冻结 Expert 段 ∪ 常驻域：同名只出现一次（字节**相加**）。
            var layers = new[] { R1With("knowledge.expert.software") };
            var merged = StackPanel.ExpertDomains(layers, resident);
            Assert.Equal(["software", "photography"], merged.Select(d => d.Id));
            Assert.Equal(only[0].Bytes + 1, merged[0].Bytes);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 专家行_高档显示_低档不占行_未装载要如实说()
    {
        // 96×30（最高档）：区栈与白板之间多一行「专家」；**每类带自己的 ≈token**（4 字节 ⇒ ≈1）。
        // 次序由宿主给（占用降序）—— 渲染不再排序，屏上就是宿主那一份。
        var lines = SplitRenderer.Render(FrameWithExperts([Expert("software", 400), Expert("ops", 40)]));
        Assert.Contains("专家 | 软件 ≈100 | 流程 ≈10", string.Join("\n", lines), StringComparison.Ordinal);

        // 屏面用**短名**（主人 2026-09-22 定），id 仍进账本：未知名回落 id，绝不编名字。
        Assert.Equal("凭据", Expert("security").Label);
        Assert.Equal("某新域", Expert("某新域").Label);

        // 屏上的数字 = **真装进 R1 的那份字节 ÷ 4**（唯一一条：与 prompt 同源，不另算）。
        var domainBytes = SkillResident.Load(
            Path.Combine(RepoRoot(), "whitebox/workspace/knowledge/.derived")).DomainBytes();
        var shown = StackPanel.ExpertDomains([], SkillResident.Load(
            Path.Combine(RepoRoot(), "whitebox/workspace/knowledge/.derived")));
        Assert.All(shown, d => Assert.Equal(domainBytes[d.Id] / 4, d.ApproxTokens));
        Assert.Equal(shown.OrderByDescending(d => d.Bytes).Select(d => d.Id), shown.Select(d => d.Id));

        // 一个都没装 ⇒ 如实写「（未装载）」，不许写预设清单。
        Assert.Contains(
            "专家 | （未装载）",
            string.Join("\n", SplitRenderer.Render(FrameWithExperts([]))),
            StringComparison.Ordinal);

        // 80×24（中档）：这一行不占地方（版式档位决定，不是内容决定）。
        var mid = FrameWithExperts([Expert("software")]);
        var midLines = SplitRenderer.Render(new SplitFrame
        {
            Title = mid.Title,
            Subtitle = mid.Subtitle,
            Conversation = [],
            Regions = [],
            ExpertDomains = mid.ExpertDomains,
            Totals = mid.Totals,
            RequestNote = mid.RequestNote,
            Menu = [],
            MenuSelection = 0,
            DetailLines = [],
            DetailScroll = 0,
            ConversationScroll = 0,
            Input = string.Empty,
            InputRows = SplitLayout.InputPanelRowsFor(24),
            Caret = 0,
            Focus = PaneFocus.Input,
            Width = 80,
            Height = 24,
        });
        Assert.DoesNotContain("专家", string.Join("\n", midLines), StringComparison.Ordinal);
        Assert.All(midLines, l => Assert.Equal(80, TerminalText.WidthOf(l)));
    }

    /// <summary>仓库根（测试运行目录在 bin/ 下，往上找到含 `whitebox/` 的那层）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "whitebox")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到仓库根（含 whitebox/ 的那层）。");
    }

    private static string Flatten(IEnumerable<string> lines) => string.Join("\n", lines);
}
