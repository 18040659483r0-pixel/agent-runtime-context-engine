using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **括号粘贴收集器**的闸门（2026-09-24 主人真机报 + 令）。
/// <para>
/// 病灶（真机实证）：TUI 只认 <c>ConsoleKey.Enter</c> ⇒ 多行粘贴被拆成 N 条用户消息，
/// 每条都把当轮**打断**一次（流里留下 N 条「用户中断了上一轮 …… 该轮没有终局块」的 Hint），
/// 而真正活下来的任务文本**只剩最后一行**。
/// </para>
/// <para>
/// 守三条：① 一整块粘贴**只产出一次**（里面的换行不是提交键）；② 单独的 Esc **仍是** Esc（不被吞）；
/// ③ 匹配失败**不丢字**（吃进来的字符按原顺序放回）。
/// </para>
/// <para>读源是注入的委托 ⇒ 这些用例不碰真终端（判据是状态机，不是「手工粘一次看看」）。</para>
/// </summary>
public sealed class BracketedPasteTests
{
    // ---------------- 脚本与假读源 ----------------

    private static ConsoleKeyInfo Esc() => new('\u001b', ConsoleKey.Escape, false, false, false);

    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.None, false, false, false);

    /// <summary>一整块粘贴的**字节脚本**：<c>ESC[200~</c> + 正文 + <c>ESC[201~</c>（与真终端给的逐键一致）。</summary>
    private static IEnumerable<ConsoleKeyInfo> PasteBytes(string body) =>
        new[] { Esc(), Ch('['), Ch('2'), Ch('0'), Ch('0'), Ch('~') }
            .Concat(body.Select(Ch))
            .Concat(new[] { Esc(), Ch('['), Ch('2'), Ch('0'), Ch('1'), Ch('~') });

    /// <summary>按脚本喂键的假读源（<c>More</c> = 「后面还有字节」＝ Console.KeyAvailable）。</summary>
    private sealed class Script
    {
        private readonly List<ConsoleKeyInfo> _keys;
        private int _at;

        public Script(IEnumerable<ConsoleKeyInfo> keys) => _keys = [.. keys];

        public bool More() => _at < _keys.Count;

        public ConsoleKeyInfo Read()
        {
            Assert.True(_at < _keys.Count, "脚本已读完 —— 收集器不该读到收尾标记之外");
            return _keys[_at++];
        }

        public PasteCollector Collector(int markWaitMs = 0) => new(Read, More, markWaitMs: markWaitMs);
    }

    // ---------------- ① 粘贴必须是一整块 ----------------

    [Fact]
    public void 一整块多行粘贴_只产出一块_且换行原样保留()
    {
        var script = new Script(PasteBytes("第一行\n第二行\n第三行").Append(Ch('x')));
        var collector = script.Collector();

        var first = collector.Read();

        Assert.True(first.IsPaste);
        Assert.Equal("第一行\n第二行\n第三行", first.Paste);

        // 关键断言：那三个换行**没有**变成三次提交 —— 下一个读到的就是粘贴块之后的那个键。
        var next = collector.Read();
        Assert.False(next.IsPaste);
        Assert.Equal('x', next.Key.KeyChar);
    }

    [Fact]
    public void 连续两次粘贴_各自成块()
    {
        var script = new Script(PasteBytes("a\nb").Concat(PasteBytes("c\nd")));
        var collector = script.Collector();

        Assert.Equal("a\nb", collector.Read().Paste);
        Assert.Equal("c\nd", collector.Read().Paste);
    }

    [Fact]
    public void 空粘贴_得到空块_由调用方决定收不收()
    {
        var script = new Script(PasteBytes(string.Empty));
        var block = script.Collector().Read();

        Assert.True(block.IsPaste);
        Assert.Equal(string.Empty, block.Paste);
    }

    [Fact]
    public void CR_与_CRLF_一律归一到LF()
    {
        var script = new Script(PasteBytes("a\r\nb\rc"));
        Assert.Equal("a\nb\nc", script.Collector().Read().Paste);
    }

    [Fact]
    public void 粘贴体里的孤立Esc_当作内容而不是收尾()
    {
        var script = new Script(PasteBytes("a\u001bb"));
        var block = script.Collector().Read();

        Assert.True(block.IsPaste);
        Assert.Equal("a\u001bb", block.Paste);

        // ESC 后面跟着的不是 [201~ ⇒ 它是正文；而且它后面的 'b' 也一个字都不能丢。
        Assert.False(block.Paste!.Contains("[201~", StringComparison.Ordinal));
    }

    // ---------------- ② 单独的 Esc 不许被吞 ----------------

    [Fact]
    public void 单独按一下Esc_仍然是Esc()
    {
        var script = new Script([Esc(), Ch('a')]);
        var collector = script.Collector();

        var first = collector.Read();
        Assert.False(first.IsPaste);
        Assert.Equal(ConsoleKey.Escape, first.Key.Key);

        Assert.Equal('a', collector.Read().Key.KeyChar);
    }

    [Fact]
    public void Esc后面不匹配标记时_吃进来的字符全部还原_一个字不丢()
    {
        var script = new Script([Esc(), Ch('['), Ch('2'), Ch('x')]);
        var collector = script.Collector();

        Assert.Equal(ConsoleKey.Escape, collector.Read().Key.Key);

        // 被试探性吃掉的三字符必须**按原顺序**回来（渲染层才看不到丢字）。
        Assert.Equal('[', collector.Read().Key.KeyChar);
        Assert.Equal('2', collector.Read().Key.KeyChar);
        Assert.Equal('x', collector.Read().Key.KeyChar);
    }

    [Fact]
    public void 没开括号粘贴的普通输入_逐键原样()
    {
        var script = new Script([Ch('h'), Ch('i'), Ch('\n')]);
        var collector = script.Collector();

        Assert.Equal('h', collector.Read().Key.KeyChar);
        Assert.Equal('i', collector.Read().Key.KeyChar);

        var enter = collector.Read();
        Assert.False(enter.IsPaste);
        Assert.Equal(ConsoleKey.None, enter.Key.Key);
        Assert.Equal('\n', enter.Key.KeyChar);
    }

    // ---------------- ③ 标记分两次到达也认得（时序不靠猜） ----------------

    [Fact]
    public async Task 标记晚到几毫秒_仍能识别为粘贴()
    {
        var clock = 0L;
        var script = new Script(PasteBytes("晚到的粘贴"));

        // 模拟「终端的写入分了两笔」：前 10ms 说没数据，之后才有。
        bool More() => clock >= 10 && script.More();
        var collector = new PasteCollector(script.Read, More, () => clock, markWaitMs: 100);

        var pumping = Task.Run(() =>
        {
            while (clock < 60)
            {
                Interlocked.Increment(ref clock);
                Thread.Sleep(1);
            }
        }, TestContext.Current.CancellationToken);

        var block = collector.Read();
        await pumping;

        Assert.True(block.IsPaste);
        Assert.Equal("晚到的粘贴", block.Paste);
    }

    // ---------------- ④ 进出场：标记必须在，且必须收干净 ----------------

    [Fact]
    public void 进屏打开粘贴标记_离屏关掉它()
    {
        var writer = new StringWriter();
        using (var screen = new AnsiScreen(output: writer))
        {
            screen.Enter();
            screen.Draw(["帧"]);
        }

        var written = writer.ToString();
        Assert.Contains(BracketedPaste.Enable, written, StringComparison.Ordinal);
        Assert.Contains(BracketedPaste.Disable, written, StringComparison.Ordinal);
        Assert.True(
            written.IndexOf(BracketedPaste.Disable, StringComparison.Ordinal)
            > written.IndexOf(BracketedPaste.Enable, StringComparison.Ordinal),
            "关标记必须写在开标记之后（否则退出时留着标记污染下一个程序）");
    }
}
