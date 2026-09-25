using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Tail;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Modules;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **Runtime TUI（MVP）的不变量测试** —— 对应 <c>docs/DESIGN-V4.4-TUI.md</c> §五 的 T1~T6。
/// <para>
/// 守四件事：
/// </para>
/// <list type="number">
/// <item><b>T1 不改字节</b>：面板调用**前后**送进模型的 prompt 字节哈希相同（且磁盘状态一个字节不动）。</item>
/// <item><b>T2 不是注入源</b>：TUI 里没有任何 <see cref="IRuntimeModule"/> 实现；且「TUI 一轮」与「纯引擎一轮」逐字节相同。</item>
/// <item><b>T3 可复算</b>：面板输出纯文本、无 ANSI、可落盘，落盘内容与屏上逐字节一致、重跑一致。</item>
/// <item><b>T6 协议区不可摘</b>：<c>/ablate protocol</c> 必须报错（且不改变模块集）。</item>
/// </list>
/// <para>顺带守 T4（<c>/ablate</c> 不写配置文件）与写入口唯一（<c>/tail</c> 与 <c>--tail</c> 是同一函数）。
/// </para>
/// </summary>
public sealed class TuiInvariantTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Flatten(IReadOnlyList<string> lines) => string.Join("\n", lines);

    // ---------------- T1：面板不改字节（最重要） ----------------

    [Fact]
    public async Task T1_面板调用前后_prompt字节哈希必须相同()
    {
        using var h = new TuiHarness("tui-t1");
        var commands = new[] { "/help", "/stack", "/hits", "/tail", "/draft", "/focus", "/modules" };

        var before = PromptBytes.Sha256Of(await h.Host.PreviewRequestAsync("第一轮问题", Ct));
        var stackBefore = Flatten(await StackPanel.RenderAsync(h.Host.Modules, h.Host.NextContext, Ct));
        var stateBefore = h.StateBytes();

        foreach (var command in commands)
        {
            var outcome = await h.Router.ExecuteAsync(command, h.Stderr, Ct);
            Assert.False(outcome.IsError, $"{command} 应为只读面板：{Flatten(outcome.Lines)}");
        }

        var after = PromptBytes.Sha256Of(await h.Host.PreviewRequestAsync("第一轮问题", Ct));
        var stackAfter = Flatten(await StackPanel.RenderAsync(h.Host.Modules, h.Host.NextContext, Ct));

        // ① 送进模型的字节：一个字节都没变。
        Assert.Equal(before, after);

        // ② 面板自己的输出也可复算（同一状态渲染两次逐字节相同）。
        Assert.Equal(stackBefore, stackAfter);

        // ③ 磁盘上的状态文件同样一个字节不动（「只读」不是只对内存说的）。
        var stateAfter = h.StateBytes();
        Assert.Equal(stateBefore.Keys, stateAfter.Keys);
        foreach (var (path, bytes) in stateBefore)
        {
            Assert.Equal(bytes, stateAfter[path]);
        }
    }

    [Fact]
    public async Task T1_调过面板之后_预览与真正发出去的请求逐字节相同()
    {
        using var h = new TuiHarness("tui-t1b");

        // 先真的跑一轮，让状态动起来（流 / 白板 / 快照都有内容）。
        await h.Router.RunTurnAsync("第一轮", verbose: false, cancellationToken: Ct);
        foreach (var command in new[] { "/stack", "/hits", "/tail", "/draft", "/focus" })
        {
            await h.Router.ExecuteAsync(command, h.Stderr, Ct);
        }

        // 预览（只读）→ 真发一轮 → 两者必须逐字节相同：观测面看到的 prompt 就是发出去的 prompt。
        var preview = await h.Host.PreviewRequestAsync("第二轮", Ct);
        await h.Router.RunTurnAsync("第二轮", verbose: false, cancellationToken: Ct);

        Assert.NotNull(h.Client.LastRequest);
        Assert.Equal(PromptBytes.Of(preview), PromptBytes.Of(h.Client.LastRequest!));
    }

    // ---------------- T2：不是注入源 ----------------

    [Fact]
    public void T2_TUI程序集里没有任何prompt段实现()
    {
        var assembly = typeof(PanelRouter).Assembly;

        foreach (var type in assembly.GetTypes())
        {
            var isImplementer = type is { IsInterface: false, IsAbstract: false }
                && (typeof(IRuntimeModule).IsAssignableFrom(type)
                    || type.GetInterfaces().Any(i => i.Name is "IFrozenZoneModule" or "IFocusRegionModule" or "ITailRegionModule" or "IDraftRegionModule"));

            Assert.False(isImplementer, $"{type.FullName} 实现了 prompt 段接口 —— TUI 只能是装配 + 显示 + 转发（T2）。");
        }
    }

    [Fact]
    public async Task T2_TUI跑一轮与纯内核跑一轮_送进模型的字节相同()
    {
        // 两个**内容相同**的工作区：一个走 TUI 宿主，一个走纯内核 —— 谁都不该多出 / 少掉一个字节。
        using var tuiSide = new TuiHarness("tui-t2-tui");
        using var engineSide = new TuiHarness("tui-t2-engine");

        await tuiSide.Router.RunTurnAsync("同一句话", verbose: false, cancellationToken: Ct);

        var engine = new AgentRuntimeEngine(engineSide.Client, new RuntimeOptions { Model = engineSide.Config.Model }, ModuleRegistry.Create(engineSide.Config));
        await engine.ChatAsync("同一句话", Ct);

        Assert.NotNull(tuiSide.Client.LastRequest);
        Assert.NotNull(engineSide.Client.LastRequest);
        Assert.Equal(PromptBytes.Of(engineSide.Client.LastRequest!), PromptBytes.Of(tuiSide.Client.LastRequest!));
    }

    // ---------------- T3：可复算 ----------------

    [Fact]
    public async Task T3_面板输出是纯文本_落盘与屏上逐字节一致_且可重跑()
    {
        using var h = new TuiHarness("tui-t3");
        var dump = h.Workspace.File("stack-dump.txt");

        var outcome = await h.Router.ExecuteAsync($"/stack dump {dump}", h.Stderr, Ct);
        Assert.False(outcome.IsError, Flatten(outcome.Lines));

        var onScreen = outcome.Lines.Where(l => !l.StartsWith("[区栈] 已落盘", StringComparison.Ordinal)).ToArray();
        var onDisk = File.ReadAllText(dump);

        // 屏上与盘上逐字节相同（T3）。
        Assert.Equal(StackPanel.Join(onScreen), onDisk);

        // 纯文本：无 ANSI 转义、无 CR（可 diff、可进实验记录）。
        Assert.DoesNotContain("\u001b", onDisk, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', onDisk);

        // 可重跑：同一状态下再 dump 一次，逐字节相同。
        var second = h.Workspace.File("stack-dump-2.txt");
        await h.Router.ExecuteAsync($"/stack dump {second}", h.Stderr, Ct);
        Assert.Equal(onDisk, File.ReadAllText(second));
    }

    [Fact]
    public async Task T3_所有面板的输出都是纯文本()
    {
        using var h = new TuiHarness("tui-t3b");
        await h.Router.RunTurnAsync("一句话", verbose: false, cancellationToken: Ct);

        foreach (var command in new[] { "/help", "/stack", "/hits", "/tail", "/draft", "/focus", "/modules", "/snapshot" })
        {
            var outcome = await h.Router.ExecuteAsync(command, h.Stderr, Ct);
            var text = Flatten(outcome.Lines);

            Assert.False(outcome.IsError, $"{command}: {text}");
            Assert.NotEmpty(outcome.Lines);
            Assert.DoesNotContain("\u001b", text, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', text);
        }
    }

    // ---------------- T6：协议区不可摘 ----------------

    [Fact]
    public async Task T6_ablate_protocol必须报错()
    {
        using var h = new TuiHarness("tui-t6");
        var before = h.Host.Modules.Select(m => m.Name).ToArray();

        // ① 服务层：直接抛（调用方绕不过去的类型事实）。
        var direct = Assert.Throws<InvalidDataException>(() => AblationService.EnsureAblatable("protocol"));
        Assert.Contains("不可摘", direct.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => h.Ablation.Apply(h.Host, "protocol", null));
        Assert.Throws<InvalidDataException>(() => h.Ablation.Apply(h.Host, "PROTOCOL", true));

        // ② 面板面：/ablate protocol 出错误行，且模块集**纹丝不动**。
        var outcome = await h.Router.ExecuteAsync("/ablate protocol", h.Stderr, Ct);
        Assert.True(outcome.IsError);
        Assert.Contains("不可摘", Flatten(outcome.Lines), StringComparison.Ordinal);

        Assert.Equal(before, h.Host.Modules.Select(m => m.Name).ToArray());
        Assert.Contains(h.Host.Modules, m => m is ProtocolModule);
    }

    // ---------------- T4：/ablate 只影响本会话，不写配置 ----------------

    [Fact]
    public async Task T4_会话内消融不写配置文件()
    {
        using var h = new TuiHarness("tui-t4");
        var configBefore = File.ReadAllBytes(h.ConfigPath);

        var outcome = await h.Router.ExecuteAsync("/ablate current-tail", h.Stderr, Ct);
        Assert.False(outcome.IsError, Flatten(outcome.Lines));
        Assert.DoesNotContain(h.Host.Modules, m => m is CurrentTailModule);

        // 配置文件的字节一个都没变；且协议区仍在（P3 闸门复验）。
        Assert.Equal(configBefore, File.ReadAllBytes(h.ConfigPath));
        Assert.Contains(h.Host.Modules, m => m is ProtocolModule);

        // 挂回去：模块集恢复原样。
        var restore = await h.Router.ExecuteAsync("/ablate current-tail on", h.Stderr, Ct);
        Assert.False(restore.IsError, Flatten(restore.Lines));
        Assert.Contains(h.Host.Modules, m => m is CurrentTailModule);
    }

    [Fact]
    public async Task T4_ablate未知模块必须报错并列清单()
    {
        using var h = new TuiHarness("tui-t4b");

        var outcome = await h.Router.ExecuteAsync("/ablate 不存在的模块", h.Stderr, Ct);

        Assert.True(outcome.IsError);
        Assert.Contains("未知模块", Flatten(outcome.Lines), StringComparison.Ordinal);
        Assert.Contains("current-tail", Flatten(outcome.Lines), StringComparison.Ordinal);
    }

    // ---------------- 写入口唯一 / 面板不记账 ----------------

    [Fact]
    public async Task 面板写入口与CLI的_tail_是同一函数_结果逐字节相同()
    {
        using var h = new TuiHarness("tui-write");
        const string text = "当前任务: 面板写入\n待办: 与 CLI 同源";

        // ① TUI 面板写（走既有写入口）。
        var outcome = await h.Router.ExecuteAsync($"/tail \"当前任务: 面板写入\\n待办: 与 CLI 同源\"", h.Stderr, Ct);
        Assert.False(outcome.IsError, Flatten(outcome.Lines));

        var viaTui = new CurrentTailStore(h.Config.CurrentTail.StorePath!).Load(null);

        // ② CLI 入口写同一段文本（同一个 TailPanel.Override）。
        AgentRuntime.Cli.Program.ApplyTailOverride(h.Config, text);
        var viaCli = new CurrentTailStore(h.Config.CurrentTail.StorePath!).Load(null);

        Assert.Equal(viaCli.Tail, viaTui.Tail);
        Assert.Equal(viaCli.Source, viaTui.Source);
        Assert.Equal(new[] { "当前任务: 面板写入", "待办: 与 CLI 同源" }, viaTui.Tail);

        // ③ 写完之后引擎里那个活实例也照读新值（否则下一轮 prompt 还是旧白板）。
        var live = h.Host.Modules.OfType<CurrentTailModule>().Single();
        Assert.Equal(viaTui.Tail, live.Current.Lines);
    }

    [Fact]
    public async Task 命中账本只记真实轮次_面板不记账()
    {
        using var h = new TuiHarness("tui-ledger");

        await h.Router.ExecuteAsync("/hits", h.Stderr, Ct);
        Assert.Empty(h.Ledger.Records);

        await h.Router.RunTurnAsync("一句话", verbose: false, cancellationToken: Ct);
        Assert.Single(h.Ledger.Records);

        foreach (var command in new[] { "/stack", "/hits", "/tail", "/draft", "/focus" })
        {
            await h.Router.ExecuteAsync(command, h.Stderr, Ct);
        }

        Assert.Single(h.Ledger.Records);
        Assert.Contains("块余数", Flatten(h.Ledger.Render(3)), StringComparison.Ordinal);
    }

    // ---------------- T5：快照纪律在 TUI 面同样成立 ----------------

    [Fact]
    public async Task T5_孤儿尾部时_resume必须显式分叉()
    {
        using var h = new TuiHarness("tui-t5", autoSnapshot: false);

        // 快照停在游标 0；随后又跑了 1 轮 ⇒ 流比快照多出 2 条孤儿尾部。
        h.Host.WriteSnapshotOnce();
        await h.Router.RunTurnAsync("第一轮", verbose: false, cancellationToken: Ct);

        var outcome = await h.Router.ExecuteAsync($"/resume {h.Workspace.File("snapshot.json")}", h.Stderr, Ct);

        Assert.True(outcome.IsError);
        Assert.Contains("孤儿尾部", Flatten(outcome.Lines), StringComparison.Ordinal);
    }

    // ---------------- 面板内容本身（P1 的六区账） ----------------

    [Fact]
    public async Task P1_区栈按规范序列出六个区_含字节指纹版本与次序()
    {
        using var h = new TuiHarness("tui-p1");
        await h.Router.RunTurnAsync("一句话", verbose: false, cancellationToken: Ct);

        var lines = await StackPanel.RenderAsync(h.Host.Modules, h.Host.NextContext, Ct);
        var text = Flatten(lines);

        Assert.Contains("R1-P", text, StringComparison.Ordinal);
        Assert.Contains("次序", text, StringComparison.Ordinal);
        Assert.Contains("sha256", text, StringComparison.Ordinal);
        Assert.Contains("零注入区", text, StringComparison.Ordinal);

        var layers = await StackPanel.InspectAsync(h.Host.Modules, h.Host.NextContext, Ct);

        // 次序 = 规范序 R1-P → R1 → R2 → R4 → R5 → R3（R3 居末）。
        Assert.Equal(
            new[] { StackRegion.R1P, StackRegion.R1, StackRegion.R2, StackRegion.R4, StackRegion.R5, StackRegion.R3 },
            layers.Select(l => l.Region).ToArray());
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, layers.Select(l => l.Order).ToArray());

        // R1-P 的字节 = 协议文本的 UTF-8 字节；指纹 = 前 12 位。
        var protocol = layers.Single(l => l.Region == StackRegion.R1P);
        Assert.Equal(StackPanel.BytesOf(AgentRuntime.Core.Protocol.ProtocolText.Text), protocol.Bytes);
        Assert.Equal(StackPanel.FingerprintOf(AgentRuntime.Core.Protocol.ProtocolText.Text), protocol.Fingerprint);
        Assert.Equal("v22", protocol.VersionLabel);   // 协议 v22：第 12 条·决策报告（含 `body:` 成品正文位）—— 主人 2026-09-24 19:1x/夜 定

        // R1 = 全部冻结段（与 FrozenPrefix 的拼接口径一致）。
        var frozen = layers.Single(l => l.Region == StackRegion.R1);
        var expectedPrefix = AgentRuntime.Core.Frozen.FrozenPrefix.Assemble(h.Host.Modules).PromptText;
        Assert.Equal(StackPanel.BytesOf(expectedPrefix), protocol.Bytes + frozen.Bytes + 2 /* 段间空行 */);

        // 空区指纹 = 空串的 sha256 前 12 位（可核对：e3b0c44298fc…）。
        var focus = layers.Single(l => l.Region == StackRegion.R3);
        Assert.True(focus.IsEmpty);
        Assert.Equal(StackPanel.FingerprintOf(string.Empty), focus.Fingerprint);
    }

    [Fact]
    public async Task P2_命中账本含prompt_cached_uncached_命中率_块余数与开销()
    {
        using var h = new TuiHarness("tui-p2");
        await h.Router.RunTurnAsync("一句话", verbose: false, cancellationToken: Ct);

        var record = Assert.Single(h.Ledger.Records);
        var text = Flatten(h.Ledger.Render(5));

        Assert.Equal(StackPanel.CacheBlockTokens, 64);
        Assert.Contains("prompt", text, StringComparison.Ordinal);
        Assert.Contains("cached", text, StringComparison.Ordinal);
        Assert.Contains("uncached", text, StringComparison.Ordinal);
        Assert.Contains("命中率", text, StringComparison.Ordinal);
        Assert.Contains("块余数", text, StringComparison.Ordinal);
        Assert.Contains("开销ms", text, StringComparison.Ordinal);
        Assert.Equal(record.PromptTokens % 64, record.BlockRemainder);
        Assert.Equal(record.PromptTokens - record.CachedTokens, record.UncachedTokens);
    }

    [Fact]
    public async Task P3_白板与草稿面板显示全文来源上限余量与存储路径()
    {
        using var h = new TuiHarness("tui-p3");

        await h.Router.ExecuteAsync("/tail \"当前任务: 甲\\n待办: 乙\"", h.Stderr, Ct);
        await h.Router.ExecuteAsync("/draft \"构想: 丙\"", h.Stderr, Ct);

        var tail = Flatten(TailPanel.Render(h.Config));
        var draft = Flatten(DraftPanel.Render(h.Config));

        Assert.Contains("[TAIL]", tail, StringComparison.Ordinal);
        Assert.Contains("当前任务: 甲", tail, StringComparison.Ordinal);
        Assert.Contains("来源", tail, StringComparison.Ordinal);
        Assert.Contains("余量", tail, StringComparison.Ordinal);
        Assert.Contains("存储路径", tail, StringComparison.Ordinal);
        Assert.Contains("≤12 行", tail, StringComparison.Ordinal);

        Assert.Contains("[DRAFT]", draft, StringComparison.Ordinal);
        Assert.Contains("构想: 丙", draft, StringComparison.Ordinal);
        Assert.Contains("≤16 行", draft, StringComparison.Ordinal);

        // 只读渲染不改磁盘（T1 的 P3 面）。
        var before = h.StateBytes();
        _ = TailPanel.Render(h.Config);
        _ = DraftPanel.Render(h.Config);
        var after = h.StateBytes();
        foreach (var (path, bytes) in before)
        {
            Assert.Equal(bytes, after[path]);
        }
    }

    // ---------------- P4：焦点面板悬空标签告警 ----------------

    [Fact]
    public void P4_焦点面板_悬空标签报警_不静默()
    {
        using var h = new TuiHarness("tui-p4-dangling");

        // 显式焦点指向流里不存在的标签（流此刻是空的）⇒ 必须报警，不静默。
        h.Config.Focus.Explicit = ["E999"];
        var text = Flatten(FocusPanel.Render(h.Config));

        Assert.Contains("E999", text, StringComparison.Ordinal);
        Assert.Contains("悬空", text, StringComparison.Ordinal);
        Assert.Contains("⚠️", text, StringComparison.Ordinal);

        // 反向：焦点清空 ⇒ 无悬空警告。
        h.Config.Focus.Explicit = [];
        Assert.DoesNotContain("悬空", Flatten(FocusPanel.Render(h.Config)), StringComparison.Ordinal);
    }

    [Fact]
    public void P4_焦点面板_提示词取自报行_不取读序行()
    {
        using var h = new TuiHarness("tui-p4-hint");

        var hint = Flatten(FocusPanel.Render(h.Config))
            .Split('\n')
            .First(l => l.Contains("[焦点] 提示词", StringComparison.Ordinal));

        // 应显示自报行（含 [FOCUS] E###），而不是读序行（Read in order）。
        Assert.Contains("E###", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("Read in order", hint, StringComparison.Ordinal);
    }

    // ---------------- P5：快照账本字段完整 + /fork 孤儿尾部处理 ----------------

    [Fact]
    public void P5_快照面板_账本含模型字段()
    {
        using var h = new TuiHarness("tui-p5-ledger", autoSnapshot: false);

        var snapshot = h.Host.WriteSnapshotOnce();
        var text = Flatten(SnapshotPanel.Describe(h.Config, snapshot));

        Assert.Contains("前缀指纹", text, StringComparison.Ordinal);
        Assert.Contains("焦点", text, StringComparison.Ordinal);
        Assert.Contains("当前尾部", text, StringComparison.Ordinal);
        Assert.Contains("动态草稿", text, StringComparison.Ordinal);
        Assert.Contains("流游标", text, StringComparison.Ordinal);
        Assert.Contains("模型", text, StringComparison.Ordinal);
        Assert.Contains("fake-model", text, StringComparison.Ordinal);   // 换模型后能一眼对账
    }

    [Fact]
    public async Task P5_fork_只分叉不续写_原流只读保留()
    {
        using var h = new TuiHarness("tui-p5-fork", autoSnapshot: false);

        // 快照停在游标 2；随后再跑 1 轮 ⇒ 流比快照多出 2 条孤儿尾部。
        await h.Router.RunTurnAsync("第一轮", verbose: false, cancellationToken: Ct);   // 流 2 条
        h.Host.WriteSnapshotOnce();                                                     // 快照游标 2
        await h.Router.RunTurnAsync("第二轮", verbose: false, cancellationToken: Ct);   // 流 4 条

        var forkPath = h.Workspace.File("fork.jsonl");
        var streamFile = h.Workspace.File("stream.jsonl");
        var streamBefore = File.ReadAllBytes(streamFile);
        var streamPathBefore = h.Config.Stream.Path;

        var outcome = await h.Router.ExecuteAsync(
            $"/fork {h.Workspace.File("snapshot.json")} {forkPath}", h.Stderr, Ct);

        Assert.False(outcome.IsError, Flatten(outcome.Lines));
        Assert.Contains("孤儿尾部", Flatten(outcome.Lines), StringComparison.Ordinal);
        Assert.Contains("只读保留", Flatten(outcome.Lines), StringComparison.Ordinal);

        // 原流一个字节未动（只读）；新流 = 原流前 2 条。
        Assert.Equal(streamBefore, File.ReadAllBytes(streamFile));
        Assert.Equal(2, File.ReadAllLines(forkPath).Length);

        // /fork 不切续写目标（config.Stream.Path 不变；续写是 /resume 的事）。
        Assert.Equal(streamPathBefore, h.Config.Stream.Path);
    }

    [Fact]
    public async Task P5_fork_目标已存在_拒绝覆盖()
    {
        using var h = new TuiHarness("tui-p5-fork-exists", autoSnapshot: false);
        h.Host.WriteSnapshotOnce();

        var forkPath = h.Workspace.File("fork.jsonl");
        File.WriteAllText(forkPath, "占位");

        var outcome = await h.Router.ExecuteAsync(
            $"/fork {h.Workspace.File("snapshot.json")} {forkPath}", h.Stderr, Ct);

        Assert.True(outcome.IsError);
        Assert.Contains("已存在", Flatten(outcome.Lines), StringComparison.Ordinal);
    }

    // ---------------- P6：消融可见反馈（本会话已摘） ----------------

    [Fact]
    public async Task 消融面板输出_含本会话已摘()
    {
        using var h = new TuiHarness("tui-p6");

        var outcome = await h.Router.ExecuteAsync("/ablate dynamic-draft", h.Stderr, Ct);

        Assert.False(outcome.IsError, Flatten(outcome.Lines));
        Assert.Contains("本会话已摘", Flatten(outcome.Lines), StringComparison.Ordinal);
        Assert.Contains("dynamic-draft", Flatten(outcome.Lines), StringComparison.Ordinal);

        // 挂回后「本会话已摘」变「（无）」。
        var restore = await h.Router.ExecuteAsync("/ablate dynamic-draft on", h.Stderr, Ct);
        Assert.False(restore.IsError, Flatten(restore.Lines));
        Assert.Contains("本会话已摘：（无）", Flatten(restore.Lines), StringComparison.Ordinal);
    }
}
