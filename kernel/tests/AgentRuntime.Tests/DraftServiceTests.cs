using AgentRuntime.Core;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// V4.3 **渲染 / 上限 / 自报容错 / 整块覆盖**的测试 —— 守五件事：
/// ① 渲染**逐字节稳定**（同状态 ⇒ 同文本）（I7）；
/// ② **上限**超限 ⇒ 报告并沿用上一版草稿（不截断、不卡轮）（I8）；
/// ③ 自报缺失 / 解析不到 ⇒ **沿用上一版**，不报错（I15）；
/// ④ **整块覆盖语义**：写入口唯一，「删除全部」= 空覆盖（I16）；
/// ⑤ 分节符：一个回复带两块时，各自解析**不互相吞并**。
/// </summary>
public sealed class DraftServiceTests
{
    private static ChatResponse Reply(string text) => new()
    {
        Id = "fake",
        Model = "fake-model",
        Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text), FinishReason = "stop" }],
    };

    // ---------------- I7 ----------------

    [Fact]
    public void Draft_Render_IsByteStable()
    {
        // 空白噪音（前后空格 / 空行 / 制表符）不得泄漏进 prompt：归一化后同输入 ⇒ 同文本。
        var noisy = new[] { "  构想: 草稿要能整块覆盖  ", string.Empty, "\t待确认: 上限 16 行", "   " };

        var first = DraftService.Render(DraftService.Snapshot(noisy, DraftSources.Report, 3));
        var second = DraftService.Render(DraftService.Snapshot(noisy, DraftSources.Report, 3));

        Assert.Equal("[DRAFT]\n构想: 草稿要能整块覆盖\n待确认: 上限 16 行", first);
        Assert.Equal(first, second);

        // 行序是语义：换序 ⇒ 文本必须不同。
        var swapped = new[] { "待确认: 上限 16 行", "构想: 草稿要能整块覆盖" };
        Assert.NotEqual(first, DraftService.Render(DraftService.Snapshot(swapped, DraftSources.Report, 3)));

        // 两个渲染入口同一口径（预算判定与诊断共用 RenderLines）。
        Assert.Equal(first, DraftService.RenderLines(noisy));

        // 空 ⇒ 空串（零注入的来源）。
        Assert.Equal(string.Empty, DraftService.Render(DraftState.Empty));
        Assert.Equal(string.Empty, DraftService.RenderLines([]));
    }

    // ---------------- I8 ----------------

    [Fact]
    public async Task Draft_OverLimit_KeepsPreviousAndReports()
    {
        var token = TestContext.Current.CancellationToken;
        var options = new DraftOptions();   // 16 行 / 3000 字符（协议区声明）
        var module = new DynamicDraftModule(null, options);

        module.Override(["构想: 好的一版"]);
        var previous = module.Current.Lines;

        // ① 行数超限（17 > 16）⇒ 报告 + **沿用上一版**：不截断、不卡轮、不抛错。
        var tooMany = Enumerable.Range(1, options.MaxLines + 1).Select(i => $"构想 {i}").ToArray();
        await module.ObserveAsync(
            new RuntimeContext("s", 0),
            new ChatRequest(),
            Reply("好的\n[DRAFT]\n" + string.Join('\n', tooMany)),
            token);

        Assert.Equal(previous, module.Current.Lines);
        Assert.Single(module.Warnings);
        Assert.Contains("沿用上一版草稿", module.Warnings[0], StringComparison.Ordinal);
        Assert.Contains($"{options.MaxLines + 1} 行", module.Warnings[0], StringComparison.Ordinal);

        // ② 字符超限（行数没超）⇒ 同样沿用上一版。
        var longLine = new string('字', ProtocolText.DraftMaxChars + 1);
        await module.ObserveAsync(new RuntimeContext("s", 1), new ChatRequest(), Reply($"[DRAFT]\n{longLine}"), token);

        Assert.Equal(previous, module.Current.Lines);
        Assert.Single(module.Warnings);
        Assert.Contains("字符", module.Warnings[0], StringComparison.Ordinal);

        // ③ 人工覆盖也不越协议上限（上限是协议的事，命令面不能绕过）。
        Assert.Throws<InvalidDataException>(() => module.Override(tooMany));

        // ④ 上限常量确实来自协议区（不是配置、不是魔法数字）。
        Assert.Equal(ProtocolText.DraftMaxLines, options.MaxLines);
        Assert.Equal(ProtocolText.DraftMaxChars, options.MaxChars);
        Assert.Equal(16, options.MaxLines);
        Assert.Equal(3000, options.MaxChars);
    }

    // ---------------- I15 ----------------

    [Fact]
    public async Task Draft_ReportMissing_KeepsPrevious()
    {
        var token = TestContext.Current.CancellationToken;
        var module = new DynamicDraftModule();
        module.Override(["构想: 保持这一版"]);
        var previous = module.Current.Lines;

        // ① 回复里根本没有 [DRAFT] 段 ⇒ 沿用上一版，**不报错**（模型没配合不是数据错误）。
        await module.ObserveAsync(new RuntimeContext("s", 0), new ChatRequest(), Reply("就是一段普通回答，没有自报。"), token);
        Assert.Equal(previous, module.Current.Lines);
        Assert.Empty(module.Warnings);

        // ② 有 [DRAFT] 头但正文空 ⇒ 同样沿用上一版。
        await module.ObserveAsync(new RuntimeContext("s", 1), new ChatRequest(), Reply("回答\n[DRAFT]\n\n   "), token);
        Assert.Equal(previous, module.Current.Lines);
        Assert.Empty(module.Warnings);

        // ③ 自报离结尾太远（超出回看窗口）⇒ 当作没有自报，沿用上一版。
        // 填充量由**窗口常量**推出（P2 起窗口 = 各段上限之和 + 余量 ⇒ 写死 70 会随窗口变化而失效）。
        var faraway = "[DRAFT]\n构想: 远处的\n" + string.Join('\n', Enumerable.Repeat("填充", AgentRuntime.Core.Protocol.ProtocolText.ReportScanLines + 10));
        await module.ObserveAsync(new RuntimeContext("s", 2), new ChatRequest(), Reply(faraway), token);
        Assert.Equal(previous, module.Current.Lines);
        Assert.Empty(module.Warnings);

        // ④ --draft-report off：本轮**不采纳**自报（协议未变，只是不采信）。
        var off = new DynamicDraftModule(null, new DraftOptions { ReportEnabled = false });
        off.Override(["构想: 人工版"]);
        await off.ObserveAsync(new RuntimeContext("s", 0), new ChatRequest(), Reply("[DRAFT]\n构想: 模型版"), token);
        Assert.Equal(new[] { "构想: 人工版" }, off.Current.Lines);
        Assert.Empty(off.Warnings);
    }

    // ---------------- I16 ----------------

    [Fact]
    public void Draft_Write_IsWholeBlockOnly()
    {
        var module = new DynamicDraftModule();

        // 整块覆盖：新自报**替换**整份内容，而不是与旧的合并（没有增量操作）。
        module.Override(["构想 A", "构想 B", "待确认 C"]);
        Assert.Equal(new[] { "构想 A", "构想 B", "待确认 C" }, module.Current.Lines);

        module.Override(["构想 D"]);
        Assert.Equal(new[] { "构想 D" }, module.Current.Lines);   // A/B/C 整块消失
        Assert.Equal(DraftSources.Manual, module.Current.Source);

        // 「重排」也只能靠整块新内容：给出换序后的整份 ⇒ 文本随之改变（不是 Move 出来的）。
        var rendered = DraftService.Render(module.Current);
        module.Override(["构想 D", "追加 E"]);
        Assert.Equal("[DRAFT]\n构想 D\n追加 E", DraftService.Render(module.Current));
        Assert.NotEqual(rendered, DraftService.Render(module.Current));

        // 「删除全部」= **空覆盖**（等价于 Clear，不是局部删）。
        module.Clear();
        Assert.True(module.Current.IsEmpty);
        Assert.Equal(DraftSources.Empty, module.Current.Source);
        Assert.Equal(string.Empty, DraftService.Render(module.Current));

        // 存储区同样是整块覆盖（写入口唯一）：文件里不残留旧字节。
        var directory = Path.Combine(Path.GetTempPath(), "agentruntime-draft-whole", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DraftStore(directory);
            var persisted = new DynamicDraftModule(store, new DraftOptions());
            persisted.Override(["构想 一", "构想 二"]);
            Assert.Equal(2, store.Load(null).Draft.Count);

            persisted.Override(["只剩下这一条"]);
            Assert.Equal(new[] { "只剩下这一条" }, store.Load(null).Draft);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // ---------------- 分节符：两块互不吞并 ----------------

    [Fact]
    public void Draft_ParseStopsAtNextBlockHeader()
    {
        // 一个回复带两块：白板在前、草稿在后（协议第 4 条允许两块同时自报）。
        var reply = "回答\n[TAIL]\n当前任务: 实现 R5\n[DRAFT]\n构想: 草稿可整块覆盖\n待确认: 上限 16 行";

        Assert.True(CurrentTailService.TryParseReport(reply, out var tailLines));
        Assert.Equal(new[] { "当前任务: 实现 R5" }, tailLines);          // 白板**不吞** [DRAFT] 块

        Assert.True(DraftService.TryParseReport(reply, out var draftLines));
        Assert.Equal(new[] { "构想: 草稿可整块覆盖", "待确认: 上限 16 行" }, draftLines);

        // 反序（草稿在前、自报行在后）：草稿**不吞** [FOCUS] 行。
        Assert.True(DraftService.TryParseReport("[DRAFT]\n构想: A\n[FOCUS] E001 E002", out var onlyDraft));
        Assert.Equal(new[] { "构想: A" }, onlyDraft);

        // 头行同侧的写法照旧收（与 R4 同口径）。
        Assert.True(DraftService.TryParseReport("回答\n[DRAFT] 构想: 同行", out var inline));
        Assert.Equal(new[] { "构想: 同行" }, inline);

        // 两块都在时，白板随后还能正常吃自己的正文（互不影响）。
        Assert.True(CurrentTailService.TryParseReport("[TAIL] 当前任务: 同行", out var inlineTail));
        Assert.Equal(new[] { "当前任务: 同行" }, inlineTail);
    }
}
