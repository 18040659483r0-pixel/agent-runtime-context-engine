using System.Reflection;
using AgentRuntime.Cli;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// V4.2 **闸门 / 上限 / 自报容错 / CLI 面**的测试 —— 守四件事：
/// ① 状态**不可变**（I6）；
/// ② 超限 ⇒ **报告并沿用上一版**，不截断不卡轮（I8）；
/// ③ 自报缺失 / 解析不到 ⇒ 沿用上一版，不报错（I16）；
/// ④ CLI 四条命令的行为（I17）。
/// <para>
/// ⚠️ V4.3 起的**次序闸门**（原 I4 「R2 → R3 → R4 → R5」与原 I18 「焦点之后只允许 R4/R5」）已**作废并重述**
/// 为「R2 → R4 → R5 → R3（R3 居末）」，测试随之移到 <c>DraftGateTests</c>
/// （<c>Gate_DynamicRegionOrder_IsEnforced</c> / <c>Gate_FocusIsLast_IsEnforced</c> / <c>Gate_TailBeforeDraft_IsEnforced</c>）——
/// 本文件不再重复钉同一道闸门。
/// </para>
/// </summary>
/// <para>
/// ⚠️ <b>必须与 <c>DraftGateTests</c> 同集合串行</b>：两者的 CLI 面测试都要 <c>Console.SetOut</c> 抓屏，
/// 而 <c>Console.Out</c> 是**进程级**的 —— 两个类并行时会互相抢重定向（抓到的内容会串台）。
/// </para>
[Collection("console-out")]
public sealed class TailPipelineTests : IDisposable
{
    private readonly SnapshotTestWorkspace _workspace = new("tail-pipeline");

    public void Dispose() => _workspace.Dispose();

    private static ChatResponse Reply(string text) => new()
    {
        Id = "fake",
        Model = "fake-model",
        Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text), FinishReason = "stop" }],
    };

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    // ---------------- I4 / I18（V4.3 作废重述） ----------------

    // 动态区次序闸门已改为 **R2 → R4 → R5 → R3（R3 居末）**，测试随之搬到 DraftGateTests：
    //   · Gate_DynamicRegionOrder_IsEnforced（I4）
    //   · Gate_FocusIsLast_IsEnforced（I19）
    //   · Gate_TailBeforeDraft_IsEnforced（I20）
    // 旧断言（「R2 → R3 → R4」与「焦点之后允许 R4/R5」）按新规范序已不再成立，不保留双份口径。

    // ---------------- I6 ----------------

    [Fact]
    public void TailState_HasNoMutators()
    {
        var type = typeof(CurrentTailState);
        Assert.True(type.IsSealed);

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(properties);

        foreach (var property in properties)
        {
            if (property.SetMethod is null)
            {
                continue;
            }

            // 连 init 都允许，但**不允许**普通 setter（白板只能整份覆盖，不能就地被改）。
            Assert.True(IsInitOnly(property.SetMethod), $"{property.Name} 暴露了可写 setter —— CurrentTailState 必须不可变。");
        }

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => !n.StartsWith("get_", StringComparison.Ordinal) && !n.StartsWith("set_", StringComparison.Ordinal))
            .ToArray();

        foreach (var forbidden in new[] { "Remove", "RemoveAt", "RemoveRange", "Insert", "Add", "Clear", "Set", "Update", "Replace", "Sort" })
        {
            Assert.DoesNotContain(forbidden, methods);
        }

        // 空空板是共享的常量：不能被谁改坏。
        Assert.True(CurrentTailState.Empty.IsEmpty);
        Assert.Equal(0, CurrentTailState.Empty.LineCount);
        Assert.Equal(string.Empty, CurrentTailState.Empty.Text);
    }

    // ---------------- I8 ----------------

    [Fact]
    public async Task Tail_OverLimit_KeepsPreviousAndReports()
    {
        var token = TestContext.Current.CancellationToken;
        var options = new CurrentTailOptions();   // 12 行 / 2000 字符（协议区声明）
        var module = new CurrentTailModule(null, options);

        module.Override(["当前任务: 好的一版"]);
        var previous = module.Current.Lines;

        // ① 行数超限（13 > 12）⇒ 报告 + **沿用上一版**：不截断、不卡轮、不抛错。
        var tooMany = Enumerable.Range(1, options.MaxLines + 1).Select(i => $"待办 {i}").ToArray();
        await module.ObserveAsync(
            new RuntimeContext("s", 0),
            new ChatRequest(),
            Reply("好的\n[TAIL]\n" + string.Join('\n', tooMany)),
            token);

        Assert.Equal(previous, module.Current.Lines);
        Assert.Single(module.Warnings);
        Assert.Contains("沿用上一版白板", module.Warnings[0], StringComparison.Ordinal);
        Assert.Contains($"{options.MaxLines + 1} 行", module.Warnings[0], StringComparison.Ordinal);

        // ② 字符超限（行数没超）⇒ 同样沿用上一版。
        var longLine = new string('字', ProtocolText.TailMaxChars + 1);
        await module.ObserveAsync(new RuntimeContext("s", 1), new ChatRequest(), Reply($"[TAIL]\n{longLine}"), token);

        Assert.Equal(previous, module.Current.Lines);
        Assert.Single(module.Warnings);
        Assert.Contains("字符", module.Warnings[0], StringComparison.Ordinal);

        // ③ 人工覆盖也不越协议上限（上限是协议的事，命令面不能绕过）。
        Assert.Throws<InvalidDataException>(() => module.Override(tooMany));

        // ④ 上限常量确实来自协议区（不是配置、不是魔法数字）。
        Assert.Equal(ProtocolText.TailMaxLines, options.MaxLines);
        Assert.Equal(ProtocolText.TailMaxChars, options.MaxChars);
    }

    // ---------------- I16 ----------------

    [Fact]
    public async Task Tail_ReportMissing_KeepsPrevious()
    {
        var token = TestContext.Current.CancellationToken;
        var module = new CurrentTailModule();
        module.Override(["当前任务: 保持这一版"]);
        var previous = module.Current.Lines;

        // ① 回复里根本没有 [TAIL] 段 ⇒ 沿用上一版，**不报错**（模型没配合不是数据错误）。
        await module.ObserveAsync(new RuntimeContext("s", 0), new ChatRequest(), Reply("就是一段普通回答，没有自报。"), token);
        Assert.Equal(previous, module.Current.Lines);
        Assert.Empty(module.Warnings);

        // ② 有 [TAIL] 头但正文空 ⇒ 同样沿用上一版。
        await module.ObserveAsync(new RuntimeContext("s", 1), new ChatRequest(), Reply("回答\n[TAIL]\n\n   "), token);
        Assert.Equal(previous, module.Current.Lines);
        Assert.Empty(module.Warnings);

        // ③ 自报离结尾太远（超出回看窗口）⇒ 当作没有自报，沿用上一版。
        var faraway = "[TAIL]\n当前任务: 远处的\n" + string.Join('\n', Enumerable.Repeat("填充", 70));
        await module.ObserveAsync(new RuntimeContext("s", 2), new ChatRequest(), Reply(faraway), token);
        Assert.Equal(previous, module.Current.Lines);
        Assert.Empty(module.Warnings);

        // ④ --tail-report off：本轮**不采纳**自报（协议未变，只是不采信）。
        var off = new CurrentTailModule(null, new CurrentTailOptions { ReportEnabled = false });
        off.Override(["当前任务: 人工版"]);
        await off.ObserveAsync(new RuntimeContext("s", 0), new ChatRequest(), Reply("[TAIL]\n当前任务: 模型版"), token);
        Assert.Equal(new[] { "当前任务: 人工版" }, off.Current.Lines);
        Assert.Empty(off.Warnings);
    }

    // ---------------- I17 ----------------

    [Fact]
    public void Cli_TailCommands_BehaveAsSpecified()
    {
        var directory = _workspace.File("tail");
        var config = new RuntimeConfiguration
        {
            BaseUrl = "https://example.invalid",
            Model = "m",
            CurrentTail = new CurrentTailConfiguration { StorePath = directory },
        };

        var store = new CurrentTailStore(directory);

        // ① --tail "文本"：人工覆盖（来源 manual）。
        var ack = Program.ApplyTailOverride(config, "当前任务: 实现 R4\n待办: 闸门放宽");
        Assert.Contains("人工覆盖", ack, StringComparison.Ordinal);

        var entry = store.Load(null);
        Assert.Equal(new[] { "当前任务: 实现 R4", "待办: 闸门放宽" }, entry.Tail);
        Assert.Equal(CurrentTailSources.Manual, entry.Source);

        // ② --tail-show：**只读** —— 打印白板全文 / 来源 / 轮次 / 上限余量 / 存储路径，且不改一个字节。
        var before = File.ReadAllBytes(store.PathFor(null));
        var shown = CaptureShow(config);

        Assert.Contains("[TAIL]", shown, StringComparison.Ordinal);
        Assert.Contains("当前任务: 实现 R4", shown, StringComparison.Ordinal);
        Assert.Contains("待办: 闸门放宽", shown, StringComparison.Ordinal);
        Assert.Contains($"来源        : {CurrentTailSources.Manual}", shown, StringComparison.Ordinal);
        Assert.Contains("轮次        :", shown, StringComparison.Ordinal);
        Assert.Contains("余量        :", shown, StringComparison.Ordinal);
        Assert.Contains($"存储路径    : {store.PathFor(null)}", shown, StringComparison.Ordinal);
        Assert.Contains($"≤{ProtocolText.TailMaxLines} 行 / ≤{ProtocolText.TailMaxChars} 字符", shown, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(store.PathFor(null)));

        // ③ --tail-clear：清空（回到零注入），来源 = empty。
        var clearedAck = Program.ApplyTailOverride(config, null);
        Assert.Contains("清空", clearedAck, StringComparison.Ordinal);
        Assert.Empty(store.Load(null).Tail);
        Assert.Equal(CurrentTailSources.Empty, store.Load(null).Source);
        Assert.Contains("（空，零注入）", CaptureShow(config), StringComparison.Ordinal);

        // ④ --tail-report on|off：运行开关（关自报 ≠ 改协议），非法值当场报错。
        Assert.True(Program.ParseOnOff("on", "--tail-report"));
        Assert.True(Program.ParseOnOff("ON", "--tail-report"));
        Assert.False(Program.ParseOnOff("off", "--tail-report"));
        Assert.False(Program.ParseOnOff(" false ", "--tail-report"));
        var ex = Assert.Throws<InvalidDataException>(() => Program.ParseOnOff("maybe", "--tail-report"));
        Assert.Contains("--tail-report", ex.Message, StringComparison.Ordinal);

        // ⑤ 存储坏掉时 --tail-show 必须**报错**（不是显示一个空板）—— 唯一真相源口径。
        File.WriteAllText(store.PathFor(null), "{ 坏掉的 JSON ");
        Assert.Throws<InvalidDataException>(() => CaptureShow(config));
    }

    private static string CaptureShow(RuntimeConfiguration config)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            Program.ShowTail(config);
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }
}
