using System.Reflection;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// V3 **Append Stream** 测试 —— 守三件事：
/// ① 事件流**只追加**（API 层面就没有删除/重排的口子）；
/// ② 顺序是**身份**（序号 1..N 严格连续），坏了必须报错而不是静默带病；
/// ③ 它能**逐条**承载 L3 文档（技能 / 踩坑集），且不破坏消融与确定性闸门。
/// </summary>
public sealed class AppendStreamTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "agentruntime-stream-tests", $"{Guid.NewGuid():N}.jsonl");

    // ---------------- 只追加与顺序 ----------------

    [Fact]
    public void 追加_序号从1起严格连续()
    {
        var stream = new SessionAppendStream();

        var a = stream.Append(SessionEventKind.Memory, "第一次事件");
        var b = stream.Append(SessionEventKind.Knowledge, "第二次事件");
        var c = stream.Append(SessionEventKind.Pitfall, "第三次事件");

        Assert.Equal(1, a.Seq);
        Assert.Equal(2, b.Seq);
        Assert.Equal(3, c.Seq);
        Assert.Equal(3, stream.Cursor);
        stream.ValidateInvariants();
    }

    [Fact]
    public void 事件流_不提供删除或重排的公开入口()
    {
        var publicMethods = typeof(SessionAppendStream)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToArray();

        foreach (var forbidden in new[] { "Remove", "RemoveAt", "RemoveRange", "Insert", "Clear", "Sort", "Reverse", "Replace" })
        {
            Assert.DoesNotContain(forbidden, publicMethods);
        }

        // 读取一律只读视图；事件本身无可写属性。
        Assert.Equal(typeof(IReadOnlyList<SessionEvent>), typeof(SessionAppendStream).GetProperty(nameof(SessionAppendStream.Events))!.PropertyType);
        Assert.DoesNotContain(typeof(SessionEvent).GetProperties(), p => p.SetMethod is { IsPublic: true });
    }

    [Fact]
    public void 游标_只增不减_按游标增量读取()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.Memory, "e1");
        var cursor = stream.Cursor;
        stream.Append(SessionEventKind.Memory, "e2");
        stream.Append(SessionEventKind.Memory, "e3");

        var delta = stream.Since(cursor);

        Assert.Equal(2, delta.Count);
        Assert.Equal([2L, 3L], delta.Select(e => e.Seq));
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Since(stream.Cursor + 1));
    }

    // ---------------- 渲染 ----------------

    [Fact]
    public void 渲染_用户与助手保持原角色_其余为system()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.Rule, "铁则一");
        stream.Append(SessionEventKind.UserInput, "我叫张总");
        stream.Append(SessionEventKind.AgentOutput, "记住了");

        var messages = stream.ToMessages();

        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
        Assert.Equal("assistant", messages[2].Role);
    }

    [Fact]
    public void 渲染格式_行首为Tag_序号不进正文()
    {
        var stream = new SessionAppendStream();
        var @event = stream.Append(SessionEventKind.Pitfall, "小图对不上大图");

        // V4.1：行首是**标签（身份）**而不是序号（位置）—— 标签才可以直接被模型自报引用。
        Assert.Equal("E001 [PITFALL] 小图对不上大图", @event.Render());
        Assert.Equal(1, @event.Seq);
    }

    // ---------------- 持久化（只追加） ----------------

    [Fact]
    public void 持久化_追加后重放_序号与正文逐字一致()
    {
        var path = TempFile();
        try
        {
            var store = new SessionStreamStore(path);
            var module = new AppendStreamModule(store.Load(), store);

            module.AppendDocument(SessionEventKind.Skill, "技能正文一", "skills/a/SKILL.md");
            module.AppendDocument(SessionEventKind.Pitfall, "坑正文二", "docs/PITFALLS.md");

            var reloaded = new SessionStreamStore(path).Load();

            Assert.Equal(2, reloaded.Count);
            Assert.Equal("技能正文一", reloaded.Events[0].Text);
            Assert.Equal(SessionEventKind.Skill, reloaded.Events[0].Kind);
            Assert.Equal("skills/a/SKILL.md", reloaded.Events[0].Source);
            Assert.Equal(2, reloaded.Events[1].Seq);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 持久化_文件被改坏_序号断裂即报错()
    {
        var path = TempFile();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                "{\"seq\":1,\"kind\":\"Memory\",\"text\":\"一\"}\n" +
                "{\"seq\":3,\"kind\":\"Memory\",\"text\":\"跳号了\"}\n");

            var ex = Assert.Throws<InvalidDataException>(() => new SessionStreamStore(path).Load());
            Assert.Contains("期望序号 2", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 持久化_中文不转义_便于阅读与diff()
    {
        var path = TempFile();
        try
        {
            var store = new SessionStreamStore(path);
            store.Append(new SessionEvent(1, SessionEventKind.Memory, "中文正文"));

            var line = File.ReadAllText(path);

            Assert.Contains("中文正文", line);
            Assert.DoesNotContain("\\u", line);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------- 模块行为 ----------------

    [Fact]
    public async Task 模块_贡献顺序与事件顺序一致()
    {
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.Knowledge, "K1");
        stream.Append(SessionEventKind.Skill, "S1");
        var module = new AppendStreamModule(stream);

        var messages = new List<ChatMessage>();
        await module.ContributeAsync(new RuntimeContext("s1", 0), messages, TestContext.Current.CancellationToken);

        Assert.Equal(2, messages.Count);
        Assert.Contains("K1", messages[0].Content);
        Assert.Contains("S1", messages[1].Content);
    }

    [Fact]
    public async Task 模块_一轮问答后_流里多出user与agent两条()
    {
        var module = new AppendStreamModule(new SessionAppendStream());
        var engine = new AgentRuntimeEngine(FakeModelClient.Returning("记住了"), new RuntimeOptions { Model = "m" }, [module]);

        await engine.ChatAsync("我叫张总", TestContext.Current.CancellationToken);

        Assert.Equal(2, module.EventCount);
        Assert.Equal(SessionEventKind.UserInput, module.Stream.Events[0].Kind);
        Assert.Equal(SessionEventKind.AgentOutput, module.Stream.Events[1].Kind);
        Assert.Equal("记住了", module.Stream.Events[1].Text);
    }

    [Fact]
    public void 逐条加载_一次一条_且落盘()
    {
        var path = TempFile();
        try
        {
            var store = new SessionStreamStore(path);
            var module = new AppendStreamModule(store.Load(), store);

            var first = module.AppendDocument(SessionEventKind.Skill, "技能A", "skills/a/SKILL.md");
            var second = module.AppendDocument(SessionEventKind.Skill, "技能B", "skills/b/SKILL.md");

            Assert.Equal(1, first.Seq);
            Assert.Equal(2, second.Seq);
            Assert.Equal(2, File.ReadAllLines(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------- 与既有铁则的关系 ----------------

    [Fact]
    public async Task 消融_去掉事件流模块_冻结区贡献逐字不变()
    {
        var frozenRoot = Path.Combine(Path.GetTempPath(), "agentruntime-stream-tests", $"frozen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(frozenRoot, "rules"));
        File.WriteAllText(Path.Combine(frozenRoot, "rules", "global.md"), "<!-- frozen: version=1 -->\n铁则正文");

        try
        {
            var source = FrozenContentSource.FromDirectory(frozenRoot);
            var rules = new RulesModule(source);
            var stream = new AppendStreamModule(new SessionAppendStream());
            var token = TestContext.Current.CancellationToken;

            var messages = new List<ChatMessage>();
            await rules.ContributeAsync(new RuntimeContext("s", 0), messages, token);
            var snapshotWith = messages.Select(m => m.Content).ToArray();

            await stream.ContributeAsync(new RuntimeContext("s", 0), messages, token);

            // 追加事件流模块之后，冻结区贡献的那几条消息**逐字不变**（只在其后追加）。
            Assert.Equal(snapshotWith, messages.Take(snapshotWith.Length).Select(m => m.Content));
        }
        finally
        {
            Directory.Delete(frozenRoot, recursive: true);
        }
    }

    [Fact]
    public void 确定性闸门_事件流必须排在冻结区之后()
    {
        var source = FrozenContentSource.FromDirectory(null);
        var rules = new RulesModule(source);
        var stream = new AppendStreamModule(new SessionAppendStream());

        // 规范编排：冻结区在前，事件流在后 → 通过。
        var arranged = DeterminismGate.Arrange([stream, rules]);
        DeterminismGate.Validate(arranged);
        Assert.IsType<RulesModule>(arranged[0]);

        // 人为把动态的事件流放到冻结区之前 → 闸门必须拦下。
        var broken = new IRuntimeModule[] { stream, rules };
        Assert.Throws<InvalidDataException>(() => DeterminismGate.Validate(broken));
    }

    [Fact]
    public void 配置_会话模块与事件流模块互斥()
    {
        var config = new RuntimeConfiguration
        {
            BaseUrl = "https://example.invalid",
            Model = "m",
            Modules = ["session", "append-stream"],
        };

        var ex = Assert.Throws<InvalidDataException>(() => config.Validate());
        Assert.Contains("互斥", ex.Message);
    }

    [Fact]
    public void 配置_登记了append_stream模块名()
    {
        Assert.Contains("append-stream", RuntimeConfiguration.KnownModules);
    }
}
