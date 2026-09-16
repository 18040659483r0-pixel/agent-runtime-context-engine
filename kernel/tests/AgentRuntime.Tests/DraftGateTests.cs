using System.Reflection;
using AgentRuntime.Cli;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Draft;
using AgentRuntime.Core.Focus;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Core.Tail;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// V4.3 **次序闸门 / 不可变 / CLI 面**的测试 —— 守四件事：
/// ① **区域序（新）**：R2 → R4 → R5 → R3，违序报错不纠正（I4）；
/// ② **R3 居末**：R3 之后不得有任何段（I19，名正言顺的「焦点居末」）；
/// ③ **V4.2 回归**：R4 仍在 R2 之后、R5 之前（I20）；
/// ④ **无可变入口**：`DraftState` 无 setter / Remove / Move / Patch（I6）+ CLI 四条命令（I17）。
/// </summary>
/// <para>
/// ⚠️ <b>必须与 <c>TailPipelineTests</c> 同集合串行</b>：两者的 CLI 面测试都要 <c>Console.SetOut</c> 抓屏，
/// 而 <c>Console.Out</c> 是**进程级**的 —— 两个类并行时会互相抢重定向（抓到的内容会串台）。
/// </para>
[Collection("console-out")]
public sealed class DraftGateTests : IDisposable
{
    private readonly SnapshotTestWorkspace _workspace = new("draft-gate");

    public void Dispose() => _workspace.Dispose();

    private static bool IsInitOnly(MethodInfo setter) =>
        setter.ReturnParameter.GetRequiredCustomModifiers()
            .Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");

    // ---------------- I4 ----------------

    [Fact]
    public void Gate_DynamicRegionOrder_IsEnforced()
    {
        var stream = new AppendStreamModule(new SessionAppendStream());
        var tail = new CurrentTailModule();
        var draft = new DynamicDraftModule();
        var focus = new FocusModule(new SessionAppendStream());
        var rules = new RulesModule(FrozenContentSource.Empty);

        // 规范序：R2 → R4 → R5 → R3 ⇒ 通过，且冻结区仍在最前。
        var arranged = DeterminismGate.Arrange([stream, rules, tail, draft, focus]);
        Assert.Equal(
            new[] { "rules", "append-stream", "current-tail", "dynamic-draft", "focus" },
            arranged.Select(m => m.Name).ToArray());
        DeterminismGate.Validate(arranged);

        // **记号（区的编号）与位置（规范序）分离**：R2=2 / R4=4 / R5=5 / R3=3，但位置是 0/1/2/3。
        Assert.Equal(DeterminismGate.RankStream, DeterminismGate.DynamicRank(stream));
        Assert.Equal(DeterminismGate.RankTail, DeterminismGate.DynamicRank(tail));
        Assert.Equal(DeterminismGate.RankDraft, DeterminismGate.DynamicRank(draft));
        Assert.Equal(DeterminismGate.RankFocus, DeterminismGate.DynamicRank(focus));
        Assert.Equal(0, DeterminismGate.DynamicPosition(stream));
        Assert.Equal(1, DeterminismGate.DynamicPosition(tail));
        Assert.Equal(2, DeterminismGate.DynamicPosition(draft));
        Assert.Equal(3, DeterminismGate.DynamicPosition(focus));

        // 白板排到事件流之前 ⇒ **编排期报错**（不静默纠正），话术必须点出规范序与 R3 居末。
        var ex = Assert.Throws<InvalidDataException>(() => DeterminismGate.Arrange([tail, stream, focus]));
        Assert.Contains("第三道", ex.Message, StringComparison.Ordinal);
        Assert.Contains("current-tail", ex.Message, StringComparison.Ordinal);
        Assert.Contains("R2 → R4 → R5 → R3", ex.Message, StringComparison.Ordinal);
        Assert.Contains("R3 之后不得有任何段", ex.Message, StringComparison.Ordinal);

        // 已编好的次序也蒙混不过去（Validate 是同一道闸门）。
        Assert.Throws<InvalidDataException>(() => DeterminismGate.Validate([tail, stream, focus]));

        // 焦点排到事件流之前（V4.1 的旧判据）⇒ 照样报错。
        Assert.Throws<InvalidDataException>(() => DeterminismGate.Arrange([focus, stream, tail]));

        // 一个区只能有一个模块：两块白板 / 两块草稿 / 两个焦点 = 次序无定义。
        Assert.Throws<InvalidDataException>(() => DeterminismGate.Arrange([stream, tail, new CurrentTailModule()]));
        Assert.Throws<InvalidDataException>(() => DeterminismGate.Arrange([stream, draft, new DynamicDraftModule()]));
        Assert.Throws<InvalidDataException>(() => DeterminismGate.Arrange([stream, focus, new FocusModule(new SessionAppendStream())]));

        // 规范序的文字形式只有一处声明（帮助文本 / 报错话术都引它）。
        Assert.Equal("R2 → R4 → R5 → R3", DeterminismGate.DynamicSequenceText);
    }

    // ---------------- I19 ----------------

    [Fact]
    public void Gate_FocusIsLast_IsEnforced()
    {
        var stream = new AppendStreamModule(new SessionAppendStream());
        var tail = new CurrentTailModule();
        var draft = new DynamicDraftModule();
        var focus = new FocusModule(new SessionAppendStream());

        // 「焦点居末」现在是**字面意义上的规范**：R3 之后什么都没有 ⇒ 通过。
        DeterminismGate.Validate([stream, tail, draft, focus]);
        DeterminismGate.EnsureFocusIsLastDynamicRegion([stream, tail, draft, focus]);

        // 焦点之后出现任何段 ⇒ 报错不纠正（那正是 V4.2 的旧序 R2 → R3 → R4 → R5）。
        var ex = Assert.Throws<InvalidDataException>(() => DeterminismGate.Arrange([stream, focus, tail]));
        Assert.Contains("R3 之后不得有任何段", ex.Message, StringComparison.Ordinal);
        Assert.Contains("current-tail", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => DeterminismGate.Arrange([stream, focus, draft]));
        Assert.Throws<InvalidDataException>(() => DeterminismGate.Arrange([stream, focus, tail, draft]));

        // 旧名字是同一道闸门（薄包装直接转调）。
        Assert.Throws<InvalidDataException>(() => DeterminismGate.EnsureFocusIsLastDynamicRegion([focus, stream]));

        // 真实装配路径：配置把 focus 写在最后 ⇒ 组合根**保序**装配 ⇒ 闸门通过。
        var config = new RuntimeConfiguration
        {
            BaseUrl = "https://example.invalid",
            Model = "m",
            Modules = ["rules", "append-stream", "current-tail", "dynamic-draft", "focus"],
        };

        var modules = ModuleRegistry.Create(config);
        Assert.Equal(
            new[] { "protocol", "rules", "append-stream", "current-tail", "dynamic-draft", "focus" },
            modules.Select(m => m.Name).ToArray());
        DeterminismGate.Validate(modules);
    }

    // ---------------- I20 ----------------

    [Fact]
    public void Gate_TailBeforeDraft_IsEnforced()
    {
        var stream = new AppendStreamModule(new SessionAppendStream());
        var tail = new CurrentTailModule();
        var draft = new DynamicDraftModule();
        var focus = new FocusModule(new SessionAppendStream());

        // V4.2 回归：R4 **仍在** R2 之后、R5 之前（白板先于草稿）。
        var arranged = DeterminismGate.Arrange([stream, tail, draft, focus]);
        Assert.Equal(
            new[] { "append-stream", "current-tail", "dynamic-draft", "focus" },
            arranged.Select(m => m.Name).ToArray());
        DeterminismGate.Validate(arranged);

        // 草稿抢在白板之前（R5 与 R4 位置颠倒）⇒ 报错。
        var ex = Assert.Throws<InvalidDataException>(() => DeterminismGate.Arrange([stream, draft, tail, focus]));
        Assert.Contains("dynamic-draft", ex.Message, StringComparison.Ordinal);
        Assert.Contains("R2 → R4 → R5 → R3", ex.Message, StringComparison.Ordinal);

        // 草稿越过事件流 ⇒ 同样报错（R5 不得排到 R2 之前）。
        Assert.Throws<InvalidDataException>(() => DeterminismGate.Arrange([draft, stream, tail, focus]));
    }

    // ---------------- I6 ----------------

    [Fact]
    public void DraftState_HasNoPartialMutators()
    {
        var type = typeof(DraftState);
        Assert.True(type.IsSealed);

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(properties);

        foreach (var property in properties)
        {
            if (property.SetMethod is null)
            {
                continue;
            }

            // 连 init 都允许，但**不允许**普通 setter（草稿只能整块覆盖，不能就地被改）。
            Assert.True(IsInitOnly(property.SetMethod), $"{property.Name} 暴露了可写 setter —— DraftState 必须不可变。");
        }

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => !n.StartsWith("get_", StringComparison.Ordinal) && !n.StartsWith("set_", StringComparison.Ordinal))
            .ToArray();

        foreach (var forbidden in new[]
                 {
                     "Remove", "RemoveAt", "RemoveRange", "Insert", "Add", "Clear", "Set", "Update",
                     "Replace", "Sort", "Move", "Patch",
                 })
        {
            Assert.DoesNotContain(forbidden, methods);
        }

        // 模块上也不得有**局部编辑**入口（「删 / 改 / 重排」只能靠整块覆盖）。
        var moduleMethods = typeof(DynamicDraftModule)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(n => !n.StartsWith("get_", StringComparison.Ordinal))
            .ToArray();

        foreach (var forbidden in new[] { "Remove", "RemoveAt", "Insert", "Move", "Patch", "SetLine" })
        {
            Assert.DoesNotContain(forbidden, moduleMethods);
        }

        // 空草稿是共享的常量：不能被谁改坏。
        Assert.True(DraftState.Empty.IsEmpty);
        Assert.Equal(0, DraftState.Empty.LineCount);
        Assert.Equal(string.Empty, DraftState.Empty.Text);
    }

    // ---------------- I17 ----------------

    [Fact]
    public void Cli_DraftCommands_BehaveAsSpecified()
    {
        var directory = _workspace.File("draft");
        var config = new RuntimeConfiguration
        {
            BaseUrl = "https://example.invalid",
            Model = "m",
            DynamicDraft = new DynamicDraftConfiguration { StorePath = directory },
        };

        var store = new DraftStore(directory);

        // ① --draft "文本"：人工覆盖（来源 manual）。
        var ack = Program.ApplyDraftOverride(config, "构想: 把 R5 做成整块覆盖\n待确认: 上限 16 行");
        Assert.Contains("人工覆盖", ack, StringComparison.Ordinal);
        Assert.Contains("草稿", ack, StringComparison.Ordinal);

        var entry = store.Load(null);
        Assert.Equal(new[] { "构想: 把 R5 做成整块覆盖", "待确认: 上限 16 行" }, entry.Draft);
        Assert.Equal(DraftSources.Manual, entry.Source);

        // ② --draft-show：**只读** —— 打印草稿全文 / 来源 / 轮次 / 上限余量 / 存储路径，且不改一个字节。
        var before = File.ReadAllBytes(store.PathFor(null));
        var shown = CaptureShow(config);

        Assert.Contains("[DRAFT]", shown, StringComparison.Ordinal);
        Assert.Contains("构想: 把 R5 做成整块覆盖", shown, StringComparison.Ordinal);
        Assert.Contains($"来源        : {DraftSources.Manual}", shown, StringComparison.Ordinal);
        Assert.Contains("轮次        :", shown, StringComparison.Ordinal);
        Assert.Contains("余量        :", shown, StringComparison.Ordinal);
        Assert.Contains($"存储路径    : {store.PathFor(null)}", shown, StringComparison.Ordinal);
        Assert.Contains($"≤{ProtocolText.DraftMaxLines} 行 / ≤{ProtocolText.DraftMaxChars} 字符", shown, StringComparison.Ordinal);
        Assert.Contains("整块覆盖", shown, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(store.PathFor(null)));

        // ③ --draft-clear：清空（回到零注入），来源 = empty。
        var clearedAck = Program.ApplyDraftOverride(config, null);
        Assert.Contains("清空", clearedAck, StringComparison.Ordinal);
        Assert.Empty(store.Load(null).Draft);
        Assert.Equal(DraftSources.Empty, store.Load(null).Source);
        Assert.Contains("（空，零注入）", CaptureShow(config), StringComparison.Ordinal);

        // ④ --draft-report on|off：运行开关（关自报 ≠ 改协议），非法值当场报错。
        Assert.True(Program.ParseOnOff("on", "--draft-report"));
        Assert.False(Program.ParseOnOff("off", "--draft-report"));
        Assert.False(Program.ParseOnOff(" false ", "--draft-report"));
        var ex = Assert.Throws<InvalidDataException>(() => Program.ParseOnOff("maybe", "--draft-report"));
        Assert.Contains("--draft-report", ex.Message, StringComparison.Ordinal);

        // ⑤ 存储坏掉时 --draft-show 必须**报错**（不是显示一个空草稿）—— 唯一真相源口径。
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
            Program.ShowDraft(config);
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }

    // ---------------- 配置口径（与 R4 / focus.reportHint 同） ----------------

    [Fact]
    public void Config_DynamicDraftKeys_AreRejected()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentruntime-draft-config", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");

            // 只留 storePath ⇒ 正常加载。
            File.WriteAllText(path, """
            {
              "baseUrl": "https://example.invalid/v1",
              "model": "m",
              "dynamicDraft": { "storePath": "~/.agentruntime/draft" }
            }
            """);
            Assert.Equal("~/.agentruntime/draft", RuntimeConfiguration.Load(path).DynamicDraft.StorePath);

            // 出现 maxLines / maxChars / reportHint ⇒ **显式拒绝**（上限与自报格式在协议区）。
            foreach (var badKey in new[] { "maxLines", "maxChars", "reportHint" })
            {
                File.WriteAllText(path, $$"""
                {
                  "baseUrl": "https://example.invalid/v1",
                  "model": "m",
                  "dynamicDraft": { "storePath": "x", "{{badKey}}": 1 }
                }
                """);

                var ex = Assert.Throws<InvalidDataException>(() => RuntimeConfiguration.Load(path));
                Assert.Contains($"dynamicDraft.{badKey}", ex.Message, StringComparison.Ordinal);
                Assert.Contains("协议区", ex.Message, StringComparison.Ordinal);
            }

            // 模块名已在 KnownModules 登记（否则配置一写就报「未知模块」）。
            Assert.Contains("dynamic-draft", RuntimeConfiguration.KnownModules);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
