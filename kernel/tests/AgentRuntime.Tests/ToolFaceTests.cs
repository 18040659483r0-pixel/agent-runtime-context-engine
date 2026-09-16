using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using AgentRuntime.Hosting;
using AgentRuntime.Tui;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **工具面（G1）+ 审批闸门（G2）的不变量测试** —— <c>docs/DESIGN-TOOL-FACE.md</c> §五 的 F1~F5，
/// 每条一个测试，且**负例必须有牙**。
/// <list type="number">
/// <item><b>F1</b> 工具调用不改变已有段的字节（T1 的延伸）。</item>
/// <item><b>F2</b> 工具结果是事件、行号即地址、可重放。</item>
/// <item><b>F3</b> <c>/ablate tool</c> 关得掉工具模块，但摘不掉协议区（P3 仍成立）——且关了**真的不兑现**。</item>
/// <item><b>F4</b> fail-closed：无法判定 / 审批缺失 ⇒ 必须拒绝（文件真的没被写）。</item>
/// <item><b>F5</b> 审批账本不进 prompt（逐字节断言）。</item>
/// </list>
/// <para>另加「正例控制组」：批准后**确实执行** —— 否则闸门可能是个恒真的摆设。</para>
/// </summary>
public sealed class ToolFaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------- 测试台 ----------------

    /// <summary>一个独立工具面测试台：临时沙箱根 + 事件流 + 假模型 + 可换回复。</summary>
    private sealed class Bench : IDisposable
    {
        public Bench(string reply, string modules = "append-stream,tool", ToolLimits? limits = null,
            IApprovalGate? gate = null, ApprovalLedger? ledger = null)
        {
            Workspace = new SnapshotTestWorkspace("tool-face");
            Work = Workspace.File("work");
            Directory.CreateDirectory(Work);
            StreamPath = Workspace.File("stream.jsonl");

            Config = new RuntimeConfiguration
            {
                BaseUrl = "https://example.invalid/v1",
                Model = "m",
                Modules = RuntimeHost.SplitList(modules),
                Stream = new StreamConfiguration { Path = StreamPath },
            };

            Limits = limits ?? ToolLimits.Default;
            Gate = gate ?? new NonInteractiveApprovalGate();
            Ledger = ledger ?? new ApprovalLedger();
            Reply = reply;

            Client = new FakeModelClient((_, _) => Task.FromResult(Response(Reply)));
            Build();
        }

        public SnapshotTestWorkspace Workspace { get; }

        /// <summary>测试用的「工作区」（临时目录）。</summary>
        public string Work { get; }

        /// <summary>裸文件名 ⇒ **绝对路径**。工具面已**不做路径围栏**（主人 2026-09-16 03:00 定），
        /// 相对路径会按进程 cwd 解析 ⇒ 测试必须给绝对路径。</summary>
        public string Abs(string name) => Path.Combine(Work, name);

        public string StreamPath { get; }

        public RuntimeConfiguration Config { get; }

        public ToolLimits Limits { get; }

        public IApprovalGate Gate { get; }

        public ApprovalLedger Ledger { get; }

        public FakeModelClient Client { get; }

        /// <summary>模型下一次的回复（换回复不用重建测试台）。</summary>
        public string Reply { get; set; }

        public IReadOnlyList<IRuntimeModule> Modules { get; private set; } = [];

        public AgentRuntimeEngine Engine { get; private set; } = null!;

        /// <summary>按当前配置重新装配（闸门 / 账本换掉后用）。</summary>
        public void Build() =>
            BuildWith(Limits, Gate, Ledger);

        public void BuildWith(ToolLimits limits, IApprovalGate gate, ApprovalLedger ledger)
        {
            Modules = ModuleRegistry.Create(Config, toolLimits: limits, toolGate: gate, toolLedger: ledger);
            Engine = new AgentRuntimeEngine(Client, new RuntimeOptions { Model = "m" }, Modules);
        }

        public AppendStreamModule? Stream => Modules.OfType<AppendStreamModule>().FirstOrDefault();

        public ToolModule? Tool => Modules.OfType<ToolModule>().FirstOrDefault();

        public IReadOnlyList<SessionEvent> Events => Stream?.Stream.Events ?? [];

        public Task<RuntimeResult> TurnAsync(string message) => Engine.ChatAsync(message, Ct);

        /// <summary>沙箱里写一个文件（测试前置）。</summary>
        public string WriteFile(string name, string content)
        {
            var path = Path.Combine(Work, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            Workspace.Dispose();
            GC.SuppressFinalize(this);
        }

        private static ChatResponse Response(string text) => new()
        {
            Id = "fake-1",
            Model = "fake-model",
            Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text), FinishReason = "stop" }],
            Usage = new TokenUsage { PromptTokens = 3, CompletionTokens = 4, TotalTokens = 7 },
        };
    }

    /// <summary>prompt 的逐字节可比形态（角色 + 正文，逐条拼）——F5 用它做「逐字节不变」断言。</summary>
    private static async Task<string> PromptBytesAsync(IReadOnlyList<IRuntimeModule> modules, string message)
    {
        var request = await RequestAssembler.AssembleAsync(
            new RuntimeOptions { Model = "m" }, modules, new RuntimeContext(null, 0), message, Ct);
        return string.Join("\u0001", request.Messages.Select(m => $"{m.Role}:{m.Content}"));
    }

    // ---------------- F1：工具调用不改变已有段的字节 ----------------

    [Fact]
    public async Task F1_工具调用不改变已有段的字节()
    {
        using var b = new Bench("回答一");
        b.WriteFile("a.txt", "第一行\n第二行\n");

        await b.TurnAsync("问题一");

        var rendersBefore = b.Events.Select(e => e.Render()).ToArray();
        var prefixBefore = FrozenPrefix.Assemble(b.Modules).PromptText;
        var promptBefore = await PromptBytesAsync(b.Modules, "问题二");

        // 第二轮：模型点了一次读文件。
        b.Reply = $"[TOOL] read {{\"path\":\"{b.Abs("a.txt")}\"}}";
        await b.TurnAsync("问题二");

        // ① 已有事件**逐字节不变**（只追加，不改历史）。
        Assert.Equal(rendersBefore, b.Events.Take(rendersBefore.Length).Select(e => e.Render()).ToArray());

        // ② 冻结前缀（协议区 + 稳定前缀）**逐字节不变** —— 工具面没有动头部。
        Assert.Equal(prefixBefore, FrozenPrefix.Assemble(b.Modules).PromptText);
        Assert.StartsWith(ProtocolText.Text, FrozenPrefix.Assemble(b.Modules).PromptText, StringComparison.Ordinal);

        // ③ 「改动」确实发生了（否则上面的不变是白测）：前面的事件与第二轮后的前缀**相同**，
        //    但流里多出了用户输入 / 模型输出 / 工具结果三条。
        Assert.Equal(rendersBefore.Length + 3, b.Events.Count);
        Assert.Equal(SessionEventKind.ToolResult, b.Events[^1].Kind);

        // ④ 第二轮请求的前 N 条消息与「装配前后的同一个模块集」一致（同一份字节，只是尾部多了东西）。
        var promptAfter = await PromptBytesAsync(b.Modules, "问题二");
        Assert.Equal(promptBefore, promptAfter[..promptBefore.Length]);
    }

    // ---------------- F2：结果是事件、行号即地址、可重放 ----------------

    [Fact]
    public async Task F2_工具结果是事件_行号即地址_可重放()
    {
        using var b = new Bench("ok");
        var file = b.WriteFile("a.txt", "第一行\n第二行\n");
        b.Reply = $"[TOOL] read {{\"path\":\"{file}\"}}";

        await b.TurnAsync("读一下");

        var @event = b.Events[^1];
        Assert.Equal(SessionEventKind.ToolResult, @event.Kind);
        Assert.Equal("read", @event.Source);                  // 账本字段：哪一个工具
        Assert.Contains("第二行", @event.Text, StringComparison.Ordinal);

        // 行号即地址：JSONL 第 N 行就是 seq N 的那一条（含 tag）。
        var lines = File.ReadAllLines(b.StreamPath);
        Assert.Equal(b.Events.Count, lines.Length);
        Assert.Contains($"\"seq\":{@event.Seq}", lines[@event.Seq - 1], StringComparison.Ordinal);
        Assert.Contains($"\"tag\":\"{@event.Tag}\"", lines[@event.Seq - 1], StringComparison.Ordinal);
        Assert.Contains("ToolResult", lines[@event.Seq - 1], StringComparison.Ordinal);

        // 可重放：从磁盘重放 ⇒ 逐条与内存一致，且不变量校验通过。
        var replayed = new SessionStreamStore(b.StreamPath).Load();
        replayed.ValidateInvariants();
        Assert.Equal(
            b.Events.Select(e => (e.Seq, e.Tag, e.Kind, e.Text, e.Source)).ToArray(),
            replayed.Events.Select(e => (e.Seq, e.Tag, e.Kind, e.Text, e.Source)).ToArray());

        // 重放后接续：工具结果照旧进 prompt ⇒ 模型下一轮看得见（协议第 4 条「必须等结果事件」）。
        b.Reply = "我看到了";
        await b.TurnAsync("继续");
        var sent = b.Client.LastRequest!.Messages.Select(m => m.Content).ToArray();
        Assert.Contains(sent, c => c.Contains("第二行", StringComparison.Ordinal));
    }

    // ---------------- F3：ablate 关得掉工具模块，摘不掉协议区 ----------------

    [Fact]
    public async Task F3_ablate_摘得掉工具模块_摘不掉协议区()
    {
        using var b = new Bench("ok", modules: "append-stream,tool,focus");

        Assert.Contains(b.Modules, m => m is ToolModule);                  // 装配里有工具面
        Assert.Equal("protocol", b.Modules[0].Name);                       // 协议区永远 Rank 0

        // 可摘清单里**有 tool、没有 protocol**；摘协议区必须报错（P3）。
        Assert.Contains("tool", AblationService.AblatableModules);
        Assert.DoesNotContain("protocol", AblationService.AblatableModules);
        AblationService.EnsureAblatable("tool");
        var ex = Assert.Throws<InvalidDataException>(() => AblationService.EnsureAblatable("protocol"));
        Assert.Contains("不可摘", ex.Message, StringComparison.Ordinal);

        // 摘掉 tool 后重建：工具模块真的没了，协议区仍在（内核守卫复验）。
        var ablated = RuntimeHost.BuildModules(b.Config, "append-stream,focus", vacuum: false);
        ModuleRegistry.EnsureProtocolPresent(ablated);
        Assert.DoesNotContain(ablated, m => m is ToolModule);
        Assert.True(ablated[0] is ProtocolModule);
        Assert.Equal(ProtocolText.Text, (await RequestAssembler.AssembleAsync(
            new RuntimeOptions { Model = "m" }, ablated, new RuntimeContext(null, 0), "你好", Ct)).Messages[0].Content);

        // **有牙**：工具面被摘后，模型点工具也**不兑现**（既不执行也不记事件）—— 消融真的关掉了它。
        var stream = ablated.OfType<AppendStreamModule>().Single();
        var engine = new AgentRuntimeEngine(b.Client, new RuntimeOptions { Model = "m" }, ablated);
        b.Reply = $"[TOOL] read {{\"path\":\"{b.Abs("a.txt")}\"}}";
        await engine.ChatAsync("读一下", Ct);

        Assert.DoesNotContain(stream.Stream.Events, e => e.Kind is SessionEventKind.ToolResult or SessionEventKind.ToolDenied);
    }

    // ---------------- F4：fail-closed 有牙 ----------------

    [Fact]
    public async Task F4_fail_closed_无法判定必须拒绝()
    {
        using var b = new Bench("ok", gate: new UnknownApprovalGate());
        b.Reply = $"[TOOL] write {{\"path\":\"{b.Abs("new.txt")}\",\"content\":\"不该出现的字节\"}}";

        await b.TurnAsync("写一个文件");

        var @event = b.Events[^1];
        Assert.Equal(SessionEventKind.ToolDenied, @event.Kind);
        Assert.Contains("拒绝", @event.Text, StringComparison.Ordinal);

        // 有牙：文件**真的没被创建**（不是「记了一条拒绝但照写」）。
        Assert.False(File.Exists(Path.Combine(b.Work, "new.txt")));

        // 留痕：审批进了账本，且结局是 Unknown（→ 按 fail-closed 当拒绝）。
        var entry = Assert.Single(b.Ledger.Entries);
        Assert.Equal(ApprovalDecision.Unknown, entry.Decision);
        Assert.Equal(ApprovalActors.Unknown, entry.Actor);
        Assert.Equal("write", entry.Tool);
    }

    [Fact]
    public async Task F4_默认非交互闸门_一律拒绝()
    {
        // 不注入任何闸门 ⇒ 出厂档 = NonInteractiveApprovalGate（非交互 / 无 TTY）。
        using var b = new Bench("ok");
        b.Reply = $"[TOOL] write {{\"path\":\"{b.Abs("new.txt")}\",\"content\":\"x\"}}";

        await b.TurnAsync("写一个文件");

        Assert.Equal(SessionEventKind.ToolDenied, b.Events[^1].Kind);
        Assert.False(File.Exists(Path.Combine(b.Work, "new.txt")));
        Assert.Equal(ApprovalActors.NonInteractive, Assert.Single(b.Ledger.Entries).Actor);
    }

    [Fact]
    public async Task F4_一次一批_没点头就不算数()
    {
        var gate = new OneShotApprovalGate();
        using var b = new Bench("ok", gate: gate);

        // 人只对**另一个**动作点过头（不同参数 ⇒ 不同摘要）。
        gate.Grant("write", ToolArgs.Parse($"{{\"path\":\"{b.Abs("other.txt")}\",\"content\":\"x\"}}"));
        b.Reply = $"[TOOL] write {{\"path\":\"{b.Abs("new.txt")}\",\"content\":\"x\"}}";

        await b.TurnAsync("写一个文件");

        Assert.Equal(SessionEventKind.ToolDenied, b.Events[^1].Kind);
        Assert.False(File.Exists(Path.Combine(b.Work, "new.txt")));
        Assert.Equal(1, gate.PendingCount);                       // 那条许可**没被用掉**（不是这个动作）
    }

    [Fact]
    public async Task F4_正例_批准后确实执行()
    {
        // 控制组：证明闸门不是「恒真拒绝」的摆设 —— 同一个动作，人点头后**真的执行**。
        var gate = new OneShotApprovalGate();

        using var b = new Bench("ok", gate: gate);
        var target = b.Abs("new.txt");
        gate.Grant("write", ToolArgs.Parse($"{{\"path\":\"{target}\",\"content\":\"x\"}}"));
        b.Reply = $"[TOOL] write {{\"path\":\"{target}\",\"content\":\"x\"}}";

        await b.TurnAsync("写一个文件");

        Assert.Equal(SessionEventKind.ToolResult, b.Events[^1].Kind);
        Assert.Equal("x", File.ReadAllText(target));

        var entry = Assert.Single(b.Ledger.Entries);
        Assert.Equal(ApprovalDecision.Approved, entry.Decision);
        Assert.Equal(ApprovalActors.Human, entry.Actor);
        Assert.Equal(0, gate.PendingCount);                       // 一次一批：点头被用掉了
    }

    [Fact]
    public async Task F4_一次点头只覆盖一次_但会话内同类动作由授权复用免问()
    {
        var gate = new OneShotApprovalGate();

        using var b = new Bench("ok", gate: gate);
        var target = b.Abs("new.txt");
        gate.Grant("write", ToolArgs.Parse($"{{\"path\":\"{target}\",\"content\":\"x\"}}"));
        b.Reply = $"[TOOL] write {{\"path\":\"{target}\",\"content\":\"x\"}}";

        await b.TurnAsync("第一次");
        await b.TurnAsync("第二次");                              // 同一个动作，再来一次

        // ⚠️ **语义变化**（主人 2026-09-16 19:14 定：「授权过一次的东西确实不需要再次授权」）：
        //   • 闸门本身仍是「一次一批」（`OneShotApprovalGate` 的语义未变，许可取走即失效）；
        //   • 新增的**判定层**把这次点头记成会话 Grant ⇒ 同一 (能力, 目标) 再来**不再打扰人**。
        // 事件序：user / agent / result / user / agent / result
        Assert.Equal(SessionEventKind.ToolResult, b.Events[^4].Kind);
        Assert.Equal(SessionEventKind.ToolResult, b.Events[^1].Kind);   // 第二次直接兑现（复用）
        Assert.Single(b.Ledger.Entries);                                // 账本只记「人点头」那一次
        Assert.Equal(ApprovalDecision.Approved, b.Ledger.Entries[0].Decision);
    }

    [Fact]
    public async Task F4_同会话内_must_ask_永不复用_每次都要批()
    {
        // 负例（授权复用的边界）：发布 / 不可逆类动作**每次**都要重新点头，不记 Grant。
        // 闸门一律拒 ⇒ 既证明「每次都问」，又不会真去跑命令。
        var gate = new ScriptedApprovalGate(ApprovalDecision.Denied, ApprovalDecision.Denied);

        using var b = new Bench("ok", gate: gate);
        b.Reply = "[TOOL] exec {\"command\":\"svn commit -m x\"}";

        await b.TurnAsync("第一次");
        await b.TurnAsync("第二次");

        Assert.Equal(2, gate.AskCount);
        Assert.Equal(2, b.Ledger.Entries.Count);
        Assert.All(b.Ledger.Entries, e => Assert.Equal(ToolRisk.Critical, e.Risk));
    }

    [Fact]
    public async Task F4_模型自批不算_回复里写批准也仍需人点头()
    {
        // 模型在正文里自称「已批准」——那不构成任何审批（它只是普通文本）。
        // ⚠️ 块头必须**在行首**（与 [FOCUS] / [TAIL] / [DRAFT] / [L3] 同一分节纪律）：
        // 行中间的 [TOOL] 不算块 ⇒ 连解析都不会发生（连拒绝事件都没有）。
        var target = Path.Combine(Path.GetTempPath(), "agentruntime-self-approve-must-not-exist", "new.txt");
        using var b = new Bench($"好的，我已批准：\n[TOOL] write {{\"path\":\"{target}\",\"content\":\"x\"}}");

        await b.TurnAsync("写一个文件");

        Assert.Equal(SessionEventKind.ToolDenied, b.Events[^1].Kind);
        Assert.False(File.Exists(target));

        // 反例（行为钉死）：把块写在行中间 ⇒ 不识别（无事件、无执行）。
        using var inline = new Bench($"好的，我已批准：[TOOL] write {{\"path\":\"{target}\",\"content\":\"x\"}}");
        await inline.TurnAsync("写一个文件");
        Assert.Equal(2, inline.Events.Count);                     // 只有 user + agent 两条
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task F4_只读工具免批_不打扰人()
    {
        // 即使闸门预设「一律拒绝」，只读工具也照跑，而且**根本不去问**（AskCount = 0）。
        var gate = new ScriptedApprovalGate(ApprovalDecision.Denied, ApprovalDecision.Denied);
        using var b = new Bench("ok", gate: gate);
        var file = b.WriteFile("a.txt", "内容");
        b.Reply = $"[TOOL] read {{\"path\":\"{file}\"}}";

        await b.TurnAsync("读一下");

        Assert.Equal(SessionEventKind.ToolResult, b.Events[^1].Kind);
        Assert.Equal(0, gate.AskCount);
        Assert.Empty(b.Ledger.Entries);                            // 免批的动作不进审批账本（没批过）
    }

    // ---------------- F5：审批账本不进 prompt ----------------

    [Fact]
    public async Task F5_审批账本变更_prompt逐字节不变()
    {
        var ledgerA = new ApprovalLedger();
        using var a = new Bench("ok", ledger: ledgerA);

        var before = await PromptBytesAsync(a.Modules, "问题");

        // 账本变更（记一条人工批准）——**不进 prompt**。
        ledgerA.Record(0, "write", ToolRisk.Mutating, "write {\"content\":\"x\",\"path\":\"secret.txt\"}",
            ApprovalDigest.Of("write", "{\"content\":\"x\",\"path\":\"secret.txt\"}"),
            ApprovalDecision.Approved, ApprovalActors.Human, "人工点头");

        var after = await PromptBytesAsync(a.Modules, "问题");
        Assert.Equal(before, after);                               // 逐字节不变

        /// <summary>演示：账本里塞三条记录 ⇒ prompt 仍与上面逐字节相同（**不进 prompt**）。</summary>
        var ledgerB = new ApprovalLedger();
        for (var i = 0; i < 3; i++)
        {
            ledgerB.Record(i, "write", ToolRisk.Mutating, $"write {{\"path\":\"f{i}.txt\",\"content\":\"x{i}\"}}", $"digest-{i}",
                ApprovalDecision.Denied, ApprovalActors.Human, "拒绝");
        }

        using var b = new Bench("ok", ledger: ledgerB);
        Assert.Equal(before, await PromptBytesAsync(b.Modules, "问题"));

        // 有牙：账本正文与 prompt 里的字节**不重叠**（账本内容没混进去）。
        Assert.Contains("审批", string.Join("\n", ledgerA.Render()), StringComparison.Ordinal);
        Assert.DoesNotContain("审批", before, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.txt", before, StringComparison.Ordinal);
    }

    // ---------------- 装配闸门（没有事件落点就不装工具面） ----------------

    [Fact]
    public void 装配_tool_必须有事件落点_否则报错()
    {
        var config = new RuntimeConfiguration
        {
            BaseUrl = "https://example.invalid/v1",
            Model = "m",
            Modules = ["tool"],                                    // 只有 tool，没有 append-stream
        };

        var ex = Assert.Throws<InvalidDataException>(() => ModuleRegistry.Create(config));
        Assert.Contains("append-stream", ex.Message, StringComparison.Ordinal);
        Assert.Contains("事件", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 装配_bare与vacuum_仍成立且不带工具面()
    {
        var bare = ModuleRegistry.Create(new RuntimeConfiguration
        {
            BaseUrl = "https://example.invalid/v1",
            Model = "m",
            Modules = [],
        });

        // 裸聊 = 协议区 + 本轮用户消息（既有语义不许破）。
        Assert.Equal(["protocol"], bare.Select(m => m.Name));
        Assert.DoesNotContain(bare, m => m is ToolModule);

        // 真空 = 连协议区都不挂。
        Assert.Empty(ModuleRegistry.Create(new RuntimeConfiguration
        {
            BaseUrl = "https://example.invalid/v1",
            Model = "m",
            Modules = ["append-stream", "tool"],
        }, VacuumMode.On));
    }

    [Fact]
    public void 工具名封闭集与已装工具集一致()
    {
        var installed = ToolSet.Default().Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        var declared = ToolNames.All.OrderBy(n => n, StringComparer.Ordinal).ToArray();

        // 「协议说能点」与「运行时有得兑现」必须是同一张名单（否则第 4 条就是空头支票）。
        Assert.Equal(declared, installed);

        // 分级钉死：读免批，改动类需批，未登记的一律归对外类（must-ask）。
        Assert.False(ToolNames.RequiresApproval("read"));
        Assert.False(ToolNames.RequiresApproval("list"));
        Assert.True(ToolNames.RequiresApproval("write"));
        Assert.True(ToolNames.RequiresApproval("edit"));
        // exec 现在**在册**（主人 2026-09-16 12:58 定：自持所需）；它改本机状态 ⇒ 需批。
        // 而「未登记的怪名字」仍然归对外类 must-ask ⇒ 默认拒（fail-closed），这条没变。
        Assert.True(ToolNames.IsKnown("exec"));
        Assert.Equal(ToolRisk.Mutating, ToolNames.RiskOf("exec"));
        Assert.Equal(ToolRisk.Outbound, ToolNames.RiskOf("send-email"));
        Assert.True(ToolNames.RequiresApproval("send-email"));
        Assert.Equal(ToolRisk.Outbound, ToolNames.RiskOf("send-email"));
        Assert.False(ToolNames.IsKnown("send-email"));
    }

    [Fact]
    public void 协议块头清单包含TOOL_且别的块不会吞掉它()
    {
        Assert.Equal("[TOOL]", ProtocolText.ToolPrefix);
        Assert.Contains(ProtocolText.ToolPrefix, ProtocolText.ReportBlockHeaders);
        Assert.Contains(ProtocolText.ToolPrefix, ProtocolText.Text, StringComparison.Ordinal);

        // 分节纪律（PITFALLS #30）：[TAIL] 块遇到 [TOOL] 块头即停 —— 不把工具块当尾部正文吞掉。
        Assert.True(CurrentTailService_ParsesWithoutTools());
        Assert.Equal(new[] { "任务A" }, TailLines("[TAIL]\n任务A\n[TOOL] read {\"path\":\"a.txt\"}"));
        Assert.Equal(new[] { "草稿A" }, DraftLines("[DRAFT]\n草稿A\n[TOOL] list {\"path\":\".\"}"));
    }

    private static bool CurrentTailService_ParsesWithoutTools() =>
        AgentRuntime.Core.Tail.CurrentTailService.TryParseReport("[TAIL]\n任务A\n[TOOL] read {\"path\":\"a.txt\"}", out var lines)
        && lines.Count == 1;

    private static IReadOnlyList<string> TailLines(string text) =>
        AgentRuntime.Core.Tail.CurrentTailService.TryParseReport(text, out var lines) ? lines : [];

    private static IReadOnlyList<string> DraftLines(string text) =>
        DraftService.TryParseReport(text, out var lines) ? lines : [];
}
