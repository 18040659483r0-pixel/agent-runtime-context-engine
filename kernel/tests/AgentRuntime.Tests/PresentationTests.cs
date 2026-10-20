using AgentRuntime.Core;
using AgentRuntime.Hosting;
using AgentRuntime.Models;
using AgentRuntime.Tui;
using AgentRuntime.Presentation;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **呈现层（Presentation）** —— <c>docs/DESIGN-PRESENTATION.md</c> 的落地取证。
/// <para>
/// 守四件事：
/// </para>
/// <list type="number">
/// <item><b>唯一投影</b>：<c>Render(帧)</c> 逐字节 == <c>RenderRich(帧)</c> 的正文（加上色不可能改布局）。</item>
/// <item><b>标记只是语法</b>：解析吃掉标记、正文不变；口径保守（glob 里的 <c>**</c> 不当粗体）。</item>
/// <item><b>落屏才上色</b>：SGR 只在 <see cref="StyleTable.Render"/> 出现；关掉即逐字节等于正文。</item>
/// <item><b>标注不造假</b>：自动标注只加段、不改正文，且不与既有段重叠。</item>
/// </list>
/// </summary>
public sealed class PresentationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------- 标记解析 ----------------

    [Fact]
    public void 标记_解析吃掉标记_正文一字不少不多()
    {
        var parsed = Markup.Parse("看 `docs/a.md` 与 **整块覆盖** 两点");

        Assert.Equal("看 docs/a.md 与 整块覆盖 两点", parsed.Text);
        // 两段角色：Path（docs/a.md）与 Strong（整块覆盖）。
        Assert.Contains(parsed.Spans, s => s.Role == StyleRole.Path && parsed.Text.Substring(s.Start, s.Length) == "docs/a.md");
        Assert.Contains(parsed.Spans, s => s.Role == StyleRole.Strong && parsed.Text.Substring(s.Start, s.Length) == "整块覆盖");
    }

    [Fact]
    public void 标记_口径保守_glob里的星号不当粗体()
    {
        // src/**/*.cs 是 glob：内容里含 / 与 * ⇒ 不算粗体（否则会把「加粗」误标在路径上）。
        var parsed = Markup.Parse("匹配 src/**/*.cs 这些文件");

        Assert.Equal("匹配 src/**/*.cs 这些文件", parsed.Text);
        Assert.DoesNotContain(parsed.Spans, s => s.Role == StyleRole.Strong);
    }

    [Fact]
    public void 标记_未配对的原样保留_不猜()
    {
        var parsed = Markup.Parse("只有半个 **加粗 与 半个 `反引号");

        Assert.Equal("只有半个 **加粗 与 半个 `反引号", parsed.Text);
        Assert.Empty(parsed.Spans);
    }

    [Fact]
    public void 标记_行首引用行_整行蓝字()
    {
        var parsed = Markup.Parse("> 这一行要特别注意");

        Assert.Equal("这一行要特别注意", parsed.Text);
        var span = Assert.Single(parsed.Spans);
        Assert.Equal(StyleRole.Attention, span.Role);
        Assert.Equal(0, span.Start);
        Assert.Equal(parsed.Text.Length, span.Length);
    }

    [Fact]
    public void 标记_显式角色_面板能把任意角色穿过字符串通道()
    {
        var parsed = Markup.Parse("[[done]]已做[[/]] 与 [[todo]]待做[[/]]");

        Assert.Equal("已做 与 待做", parsed.Text);
        Assert.Contains(parsed.Spans, s => s.Role == StyleRole.Done && parsed.Text.Substring(s.Start, s.Length) == "已做");
        Assert.Contains(parsed.Spans, s => s.Role == StyleRole.Todo && parsed.Text.Substring(s.Start, s.Length) == "待做");

        // 未知角色名 / 没闭合 ⇒ **原样保留**（不猜）。
        Assert.Equal("[[nope]]x[[/]]", Markup.Parse("[[nope]]x[[/]]").Text);
        Assert.Equal("[[done]]x", Markup.Parse("[[done]]x").Text);
        Assert.Equal("[[/]]", Markup.Parse("[[/]]").Text);
    }

    [Fact]
    public void 标记_角色往返_ToMarkup是Parse的逆()
    {
        var original = RichText.Of("abc已做", new RichSpan(3, 2, StyleRole.Done));

        var round = Markup.Parse(original.ToMarkup());

        Assert.Equal(original.Text, round.Text);
        Assert.Equal(StyleRole.Done, Assert.Single(round.Spans).Role);
        Assert.Equal((3, 2), (round.Spans[0].Start, round.Spans[0].Length));
    }

    // ---------------- 自动标注 ----------------

    [Fact]
    public void 标注_绝对路径与URL与文件名_只加段不改正文()
    {
        var text = RichText.From("见 /tmp/wb-demo.txt 与 https://docs.openclaw.ai 以及 docs/PITFALLS.md");
        var annotated = Annotator.Annotate(text);

        Assert.Equal(text.Text, annotated.Text);                 // 正文一字未动
        Assert.Contains(annotated.Spans, s => annotated.Text.Substring(s.Start, s.Length) == "/tmp/wb-demo.txt");
        Assert.Contains(annotated.Spans, s => annotated.Text.Substring(s.Start, s.Length).StartsWith("https://", StringComparison.Ordinal));
        Assert.Contains(annotated.Spans, s => annotated.Text.Substring(s.Start, s.Length) == "docs/PITFALLS.md");
    }

    [Fact]
    public void 标注_不与既有角色段重叠()
    {
        var parsed = Markup.Parse("看 `/tmp/wb-demo.txt` 就够了");
        var annotated = Annotator.Annotate(parsed);

        var spans = annotated.Spans.Where(s => annotated.Text.Substring(s.Start, s.Length) == "/tmp/wb-demo.txt").ToList();
        Assert.Single(spans);                                     // 代码段已经标过 ⇒ 不再叠一层
    }

    // ---------------- 富文本几何（角色段随切/随折） ----------------

    [Fact]
    public void 富文本_折行与截断时角色段跟着走()
    {
        var text = RichText.Of("abcdefghij", new RichSpan(0, 5, StyleRole.Path));

        var wrapped = text.Wrap(4);
        Assert.Equal(3, wrapped.Count);
        Assert.Equal("abcd", wrapped[0].Text);
        Assert.Equal("efgh", wrapped[1].Text);
        Assert.Equal("ij", wrapped[2].Text);
        Assert.Equal(StyleRole.Path, wrapped[0].Spans[0].Role);              // 全在 Path 段里
        Assert.Equal(StyleRole.Path, wrapped[1].Spans[0].Role);
        Assert.Equal(1, wrapped[1].Spans[0].Length);                          // 只有 'e' 还在段内
        Assert.Empty(wrapped[2].Spans);

        var truncated = text.TruncateTo(5);
        Assert.Equal(StyleRole.Path, Assert.Single(truncated.Spans).Role);
    }

    [Fact]
    public void 折行_与旧尺子逐行一致()
    {
        // WrapRanges 是唯一折行处：它与老的 Wrap 结果必须逐行相同（中文 / 硬换行 / 空行都试）。
        foreach (var text in new[] { "第一行很短", "中文字符中文字符中文字符", "a\n\nb", "行一\n行二很长很长很长" })
        {
            var lines = TerminalText.Wrap(text, 6);
            Assert.Equal(lines, RichText.From(text).Wrap(6).Select(l => l.Text).ToList());
        }
    }

    // ---------------- 样式表 / 落屏上色 ----------------

    [Fact]
    public void 样式表_关掉时逐字节等于正文_打开时插零宽SGR()
    {
        var text = RichText.Of("abc", new RichSpan(0, 3, StyleRole.Path));

        Assert.Equal("abc", StyleTable.Disabled.Render(text));

        // 默认（256 档，与 OpenClaw 同色）：浅金 222。
        var vivid = StyleTable.Standard.Render(text);
        Assert.Contains("\u001b[38;5;222m", vivid, StringComparison.Ordinal);
        Assert.Contains("\u001b[0m", vivid, StringComparison.Ordinal);         // 复位
        Assert.Equal(text.Text, StripSgr(vivid));                             // 零宽：去掉 SGR == 正文

        // 16 色兜底档：亮黄 93（沿用 aixterm 亮色）。
        Assert.Contains("\u001b[93m", StyleTable.Standard16.Render(text), StringComparison.Ordinal);

        // 真彩档：原色 38;2;240;201;135（不掺提亮前缀 —— 主人：「过于鲜艳会闪瞎眼睛」）。
        Assert.Contains("\u001b[38;2;240;201;135m", StyleTable.StandardTrueColor.Render(text), StringComparison.Ordinal);
    }

    [Fact]
    public void 样式表_配色取自OpenClaw主题_且不夹提亮前缀()
    {
        // 与 OC 的 TUI 主题逐字同色（accent / code / quote / success / accentSoft / error / dim）。
        Assert.Equal("#F6C453", StyleTable.HexOf(StyleRole.Noun));
        Assert.Equal("#F0C987", StyleTable.HexOf(StyleRole.Path));
        Assert.Equal("#8CC8FF", StyleTable.HexOf(StyleRole.Attention));
        Assert.Equal("#7DD3A5", StyleTable.HexOf(StyleRole.Done));
        Assert.Equal("#F2A65A", StyleTable.HexOf(StyleRole.Todo));
        Assert.Equal("#F97066", StyleTable.HexOf(StyleRole.Warn));
        Assert.Equal("#7B7F87", StyleTable.HexOf(StyleRole.Dim));
        Assert.Equal("—", StyleTable.HexOf(StyleRole.You));   // 斜体：字体属性、无色（主人 2026-09-22 定）

        // 除加粗（Strong）外，不带 "1;" 提亮前缀（上一版就是它把颜色"闪"起来的）。
        foreach (var role in new[] { StyleRole.Path, StyleRole.Attention, StyleRole.Done, StyleRole.Todo, StyleRole.Warn })
        {
            Assert.DoesNotContain("\u001b[1;", StyleTable.Standard.Sgr(role), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void 样式表_色深与颜色策略同一处判()
    {
        Assert.True(StyleTable.For(ColorMode.Auto, stdoutIsTty: true).Enabled);
        Assert.False(StyleTable.For(ColorMode.Auto, stdoutIsTty: false).Enabled);
        Assert.False(StyleTable.For(ColorMode.Auto, stdoutIsTty: true, noColor: "1").Enabled);
        Assert.False(StyleTable.For(ColorMode.Auto, stdoutIsTty: true, term: "dumb").Enabled);
        Assert.True(StyleTable.For(ColorMode.Always, stdoutIsTty: false).Enabled);
        Assert.False(StyleTable.For(ColorMode.Never, stdoutIsTty: true).Enabled);

        // 色深：COLORTERM 说 truecolor ⇒ 真彩；TERM 含 256color ⇒ 256；否则 16 色兜底；显式给值优先。
        Assert.Equal(ColorDepth.TrueColor, StyleTable.DetectDepth("xterm-256color", "truecolor"));
        Assert.Equal(ColorDepth.Color256, StyleTable.DetectDepth("xterm-256color"));
        Assert.Equal(ColorDepth.Basic, StyleTable.DetectDepth("xterm"));
        Assert.Equal(ColorDepth.Basic, StyleTable.For(ColorMode.Always, true, term: "xterm-256color", depth: ColorDepth.Basic).Depth);

        Assert.Equal(ColorMode.Always, StyleTable.ParseMode("always"));
        Assert.Throws<ArgumentException>(() => StyleTable.ParseMode("rainbow"));
        Assert.Null(StyleTable.ParseDepth("auto"));
        Assert.Equal(ColorDepth.Color256, StyleTable.ParseDepth("256"));
        Assert.Equal(ColorDepth.TrueColor, StyleTable.ParseDepth("truecolor"));
        Assert.Throws<ArgumentException>(() => StyleTable.ParseDepth("65536"));
    }

    [Fact]
    public void 门面_纯文本投影等于标记剥离()
    {
        const string source = "看 `docs/a.md` 与 **重点**";

        Assert.Equal("看 docs/a.md 与 重点", Presenter.PlainOnly.Plain(source));
        Assert.Equal(Presenter.PlainOnly.Plain(source), Presenter.PlainOnly.Rich(source).Text);
    }

    // ---------------- 表格构件 ----------------

    [Fact]
    public void 表格_画成简单表格_列宽按显示宽算()
    {
        var lines = TextTable.Render(
            ["事项", "状态"],
            [
                ["呈现层", "已做"],
                ["上色", "待做"],
            ]);

        Assert.Equal(6, lines.Count);                    // 上框 + 表头 + 中缝 + 两行 + 下框
        Assert.StartsWith("┌", lines[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("┘", lines[^1].Text, StringComparison.Ordinal);
        Assert.Contains("事项", lines[1].Text, StringComparison.Ordinal);

        // 纯文本投影就是一张能 diff 的 ASCII 表：每行显示宽度一致（中文占两格也不歪）。
        var width = TerminalText.WidthOf(lines[0].Text);
        Assert.All(lines, line => Assert.Equal(width, TerminalText.WidthOf(line.Text)));
    }

    [Fact]
    public void 表格_单元格里的标记照样解析_但纯文本投影不带标记()
    {
        var lines = TextTable.Render(["证据"], [["`docs/a.md`"]]);

        var row = lines.First(l => l.Text.Contains("docs/a.md", StringComparison.Ordinal));
        Assert.DoesNotContain("`", row.Text, StringComparison.Ordinal);
        Assert.Contains(row.Spans, s => s.Role == StyleRole.Path);
    }

    // ---------------- 唯一投影闸门（帧级） ----------------

    [Fact]
    public async Task 闸门_纯文本帧与富文本帧的正文逐字节相同()
    {
        using var harness = new TuiHarness("presentation-gate");
        var client = FakeModelClient.Returning("看 `docs/a.md` 与 **重点**\n[TAIL]\n当前任务: 甲");
        using var host = RuntimeHost.BootWith(http: null, client, harness.Config);
        var session = new SplitSession(
            host, harness.Ledger, new PanelRouter(host, harness.Ledger, harness.Ablation), verbose: false,
            ContinuationSettings.Off);

        await session.StartAsync(Ct);
        await session.SubmitAsync("随手记一句", Ct);
        await session.SubmitAsync("/palette", Ct);            // 面板输出里也带标记 + 表格

        foreach (var (width, height) in new[] { (96, 30), (80, 24), (72, 20) })
        {
            var frame = session.Frame(width, height);
            var plain = SplitRenderer.Render(frame);
            var rich = SplitRenderer.RenderRich(frame);

            Assert.Equal(plain.Count, rich.Count);
            Assert.Equal(plain, rich.Select(l => l.Text).ToArray());   // **同一条投影**
            Assert.All(rich, line => Assert.DoesNotContain("**", line.Text, StringComparison.Ordinal));
            Assert.All(rich, line => Assert.DoesNotContain("[[", line.Text, StringComparison.Ordinal));
        }

        // 面板输出里的**结构化角色**也真的到了屏面上（[[done]] / [[todo]] 被解析成段）。
        var palette = session.Frame(120, 34);
        var roles = SplitRenderer.RenderRich(palette).SelectMany(l => l.Spans).Select(s => s.Role).Distinct().ToList();
        Assert.Contains(StyleRole.Done, roles);
        Assert.Contains(StyleRole.Todo, roles);

        // 行数 / 列宽照旧（加了呈现层，布局一格没动）。
        var lines = SplitRenderer.Render(session.Frame(96, 30));
        Assert.Equal(30, lines.Count);
    }

    // ---------------- 宿主共用（转发器 / 项目边界） ----------------

    [Fact]
    public void 转发器_把它套在stdout上_逐行过呈现层()
    {
        var inner = new StringWriter();
        var writer = new PresentationWriter(inner, new Presenter(StyleTable.Standard));

        writer.Write("看 `docs/a.md` 与 **重点**\n");
        writer.Write("agent-runtime > ");      // REPL 提示符：**没有换行**，也要能冲出去
        writer.Flush();

        var text = inner.ToString();
        Assert.Contains("docs/a.md", text, StringComparison.Ordinal);                    // 正文照旧
        Assert.DoesNotContain("`", text, StringComparison.Ordinal);                       // 标记被吃掉
        Assert.DoesNotContain("**", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[38;5;222m", text, StringComparison.Ordinal);             // TTY 下上色（OC 同色）
        Assert.EndsWith("agent-runtime > ", text, StringComparison.Ordinal);              // 尾巴没丢

        // 关掉颜色 ⇒ 逐字节等于投影（纯文本产物与屏上正文同）。
        var plain = new StringWriter();
        var plainWriter = new PresentationWriter(plain, Presenter.PlainOnly);
        plainWriter.WriteLine("看 `docs/a.md`");
        plainWriter.Flush();
        Assert.Equal("看 docs/a.md\n", plain.ToString());
    }

    [Fact]
    public void 项目边界_呈现层不依赖任何其它AgentRuntime项目()
    {
        // 它是「宿主怎么画」，不是内核语义：一旦它反向依赖 Core / Hosting，两个宿主就没法共用了。
        var referenced = typeof(StyleTable).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("AgentRuntime.", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(referenced);
        Assert.Equal("AgentRuntime.Presentation", typeof(StyleTable).Assembly.GetName().Name);
    }

    private static string StripSgr(string text)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\u001b')
            {
                builder.Append(text[i]);
                continue;
            }

            while (i < text.Length && text[i] != 'm')
            {
                i++;
            }
        }

        return builder.ToString();
    }
}
