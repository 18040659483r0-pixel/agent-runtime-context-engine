using AgentRuntime.Core;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Modules;
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

    private static SplitSession NewSession(TuiHarness h) =>
        new(h.Host, h.Ledger, h.Router, verbose: false);

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
    public async Task Split_区目录默认折叠_展开只看选中区()
    {
        using var h = new TuiHarness("split-expand");
        var session = NewSession(h);
        await session.StartAsync(Ct);

        // ① 默认（精简态）：六项全折叠，可扩展区是空的。
        var fresh = session.Frame(96, 30);
        Assert.Equal(6, fresh.Menu.Count);
        Assert.All(fresh.Menu, e => Assert.False(e.Expanded));
        Assert.Null(session.Expanded);
        Assert.Empty(fresh.DetailLines);

        // ② 面板命令的输出进右栏可扩展区（标题标明是哪个面板）。
        await session.SubmitAsync("/tail \"当前任务: 甲\"", Ct);
        var withPanel = session.Frame(96, 30);
        Assert.StartsWith("面板 /tail", withPanel.DetailTitle ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("[尾部]", Flatten(withPanel.DetailLines), StringComparison.Ordinal);

        // ③ Esc 清除面板输出。
        session.CollapseAll();
        Assert.Empty(session.Frame(96, 30).DetailLines);

        // ④ 展开 R4：目录项标记为展开，右栏出现白板正文。
        var r4Index = fresh.Menu.ToList().FindIndex(e => e.Region == StackRegion.R4);
        session.SelectMenu(r4Index);
        session.ToggleExpandSelected();

        var expanded = session.Frame(96, 30);
        Assert.Equal(StackRegion.R4, session.Expanded);
        Assert.True(expanded.Menu[r4Index].Expanded);
        Assert.Contains("正文", expanded.DetailTitle!, StringComparison.Ordinal);
        Assert.Contains("[TAIL]", Flatten(expanded.DetailLines), StringComparison.Ordinal);
        Assert.Contains("当前任务: 甲", Flatten(expanded.DetailLines), StringComparison.Ordinal);

        // ⑤ 再按一次收起。
        session.ToggleExpandSelected();
        Assert.Null(session.Expanded);
        Assert.Empty(session.Frame(96, 30).DetailLines);
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
        Assert.Contains("面板 /hits", text, StringComparison.Ordinal);              // 面板输出进详细块
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

        var lines = SplitRenderer.Render(session.Frame(width, height));

        // ① 尺寸精确：行数 = 高，每行显示宽度 = 宽（中文也不漂）。
        Assert.Equal(height, lines.Count);
        Assert.All(lines, l => Assert.Equal(width, TerminalText.WidthOf(l)));

        // ② 六区、合计、上一轮、详细块目录、输入行都在。
        var text = string.Join("\n", lines);
        foreach (var id in new[] { "R1-P", "R1", "R2", "R4", "R5", "R3" })
        {
            Assert.Contains(id, text, StringComparison.Ordinal);
        }

        Assert.Contains("合计", text, StringComparison.Ordinal);
        Assert.Contains("上一轮", text, StringComparison.Ordinal);
        Assert.Contains("▸ R1-P 协议", text, StringComparison.Ordinal);
        Assert.Contains("agent-runtime > ", text, StringComparison.Ordinal);

        // ③ 接缝齐全：顶边框有 ┬、底分隔有 ┴、每行两端都是框线（不错位）。
        Assert.Contains(lines, l => l.StartsWith('┌') && l.Contains('┬'));
        Assert.Contains(lines, l => l.Contains('┴'));
        Assert.All(lines, l => Assert.Contains(l[0], "┌│├└"));
        Assert.All(lines, l => Assert.Contains(l[^1], "┐│┤┘"));

        // ④ 状态块在右列上半（中缝行之前），详细块在它下面。
        var layout = new SplitLayout(width, height);
        var divider = lines.ToList().FindIndex(l => l.Contains('├') && l.Contains('┤') && !l.Contains('┴'));
        Assert.InRange(divider, 1, lines.Count - 1);
        Assert.True(divider > layout.StatusRows - 1, "中缝应在状态块下方");
    }

    [Fact]
    public async Task 几何_状态块固定在右上_不随对话滚动()
    {
        using var h = new TuiHarness("split-fixed");
        var session = NewSession(h);
        await session.StartAsync(Ct);

        for (var i = 1; i <= 12; i++)
        {
            await session.SubmitAsync($"第{i}句话", Ct);
        }

        var layout = new SplitLayout(96, 30);
        var before = session.Frame(96, 30);
        var beforeLines = SplitRenderer.Render(before);
        var beforeLeft = beforeLines.Select(l => LeftColumn(l, layout.LeftWidth)).ToArray();

        // 把对话滚到顶（左栏动）
        session.FocusPane(PaneFocus.Input);
        session.ScrollBy(-999);   // 向上滚到顶（↑ 的方向）

        var after = session.Frame(96, 30);
        var afterLines = SplitRenderer.Render(after);

        // ① 左栏确实滚了。
        Assert.NotEqual(beforeLeft, afterLines.Select(l => LeftColumn(l, layout.LeftWidth)).ToArray());

        // ② 右列（状态块 + 中缝 + 详细块）**逐字节不动** —— 这就是「固定在右上角」。
        for (var i = 1; i < beforeLines.Count - 2; i++)
        {
            Assert.Equal(RightColumn(beforeLines[i], layout.LeftWidth), RightColumn(afterLines[i], layout.LeftWidth));
        }

        // ③ 模型层同样不动。
        Assert.Equal(SplitRenderer.StatusCells(before, layout), SplitRenderer.StatusCells(after, layout));
    }

    [Fact]
    public async Task 几何_窄时省列_矮时省行_但不错位()
    {
        using var h = new TuiHarness("split-compress");
        var session = NewSession(h);
        await session.StartAsync(Ct);
        await session.SubmitAsync("/tail \"当前任务: 甲\"", Ct);

        // 清掉面板输出，让详细块回到**区目录**（折叠态）
        session.CollapseAll();

        // ① 窄（72×20 ⇒ 右列 24 格）：先丢版本、再丢掉指纹列 —— 但 Δ 与次序必须在。
        var narrow = new SplitLayout(72, 20);
        Assert.Equal(24, narrow.RightWidth);
        Assert.Equal(0, narrow.Columns.FingerprintChars);
        Assert.False(narrow.Columns.ShowVersion);

        var narrowLines = SplitRenderer.Render(session.Frame(72, 20));
        Assert.Equal(20, narrowLines.Count);
        Assert.All(narrowLines, l => Assert.Equal(72, TerminalText.WidthOf(l)));

        var narrowText = string.Join("\n", narrowLines);
        Assert.Contains("R1-P", narrowText, StringComparison.Ordinal);
        Assert.DoesNotContain(session.Frame(72, 20).Regions[0].Fingerprint, narrowText, StringComparison.Ordinal);

        // ② 矮（80×17）：省掉「合计」与「上一轮」行，但六区表仍在，且不错位。
        var shortLayout = new SplitLayout(80, 17);
        Assert.False(shortLayout.ShowTotalsRow);
        Assert.False(shortLayout.ShowUsageRow);

        var shortLines = SplitRenderer.Render(session.Frame(80, 17));
        Assert.Equal(17, shortLines.Count);
        Assert.All(shortLines, l => Assert.Equal(80, TerminalText.WidthOf(l)));

        var shortText = string.Join("\n", shortLines);
        Assert.DoesNotContain("合计", shortText, StringComparison.Ordinal);
        Assert.DoesNotContain("上一轮", shortText, StringComparison.Ordinal);
        Assert.Contains("R1-P", shortText, StringComparison.Ordinal);
        Assert.Contains("▸ R2", shortText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 几何_左列自顶向下填充_空状态不留半屏空白()
    {
        using var h = new TuiHarness("split-topleft");
        var session = NewSession(h);
        await session.StartAsync(Ct);

        var layout = new SplitLayout(96, 30);
        var fresh = SplitRenderer.Render(session.Frame(96, 30));

        // ① 会话刚起来（就一行引导语）：内容从**正文第一行**开始 —— 上方不留半屏空白。
        //    （lines[0] 是顶边框，lines[1] 是左栏标题行，lines[2] 才是第一条消息）
        Assert.Contains("Tab 切焦点", LeftColumn(fresh[2], layout.LeftWidth), StringComparison.Ordinal);
        Assert.DoesNotContain("…", LeftColumn(fresh[2], layout.LeftWidth), StringComparison.Ordinal);

        // ② 内容不够一屏时，空白留在**下方**（最后一个正文行是空的）。
        Assert.True(string.IsNullOrWhiteSpace(LeftColumn(fresh[layout.BodyRows], layout.LeftWidth)));

        // ③ 内容超过一屏：自动贴底（最新仍在最下），↑ 可回看。
        for (var i = 1; i <= 12; i++)
        {
            await session.SubmitAsync($"第{i}句话", Ct);
        }

        var atBottom = string.Join("\n", SplitRenderer.Render(session.Frame(96, 30)).Select(l => LeftColumn(l, layout.LeftWidth)));
        Assert.Contains("第12句话", atBottom, StringComparison.Ordinal);
        Assert.DoesNotContain("第1句话", atBottom, StringComparison.Ordinal);

        session.FocusPane(PaneFocus.Input);
        session.ScrollBy(-999);   // 向上滚到顶
        var atTop = string.Join("\n", SplitRenderer.Render(session.Frame(96, 30)).Select(l => LeftColumn(l, layout.LeftWidth)));
        Assert.Contains("第1句话", atTop, StringComparison.Ordinal);
        Assert.DoesNotContain("第12句话", atTop, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 几何_80列下区名字节Δ三列完整且不出现省略号()
    {
        using var h = new TuiHarness("split-narrow80");
        var session = NewSession(h);
        await session.StartAsync(Ct);
        await session.SubmitAsync("/tail \"当前任务: 甲\"", Ct);
        await session.SubmitAsync("一句话", Ct);
        session.CollapseAll();

        var layout = new SplitLayout(80, 24);
        var frame = session.Frame(80, 24);
        var lines = SplitRenderer.Render(frame);
        var right = string.Join("\n", lines.Select(l => RightColumn(l, layout.LeftWidth)));

        // ① 窄屏整列略（不截字）：指纹列整体不进来。
        Assert.Equal(0, layout.Columns.FingerprintChars);
        Assert.False(layout.Columns.ShowVersion);

        // ② 区名 / 字节 / Δ 三列必须**完整可读**。
        foreach (var row in frame.Regions)
        {
            Assert.Contains(row.Id, right, StringComparison.Ordinal);
            Assert.Contains(TerminalText.Number(row.Bytes), right, StringComparison.Ordinal);
            Assert.Contains(TerminalText.Delta(row.Delta), right, StringComparison.Ordinal);
            Assert.DoesNotContain(row.Fingerprint[..8], right, StringComparison.Ordinal);
        }

        // ③ 右列一个省略号都不许有（截字就是读不了）。
        Assert.DoesNotContain("…", right, StringComparison.Ordinal);
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

    private static string Flatten(IEnumerable<string> lines) => string.Join("\n", lines);
}
