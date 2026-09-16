using System.Text.Json;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Skill;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **「按 id 装载 L3」测试**（<c>docs/DESIGN-SKILL-LAYERS.md</c> §三/§五 + §七 S1~S4/S7/S11）。
/// <para>
/// 守四件事：
/// </para>
/// <list type="number">
/// <item><b>行号即地址</b>：<c>L3.jsonl</c> 第 N 行 = <c>seq</c> N；id 语法 <c>S-&lt;短名&gt;-&lt;NNN&gt;</c>。</item>
/// <item><b>按 id 装载</b>：条正文**逐字节**经既有 append 通道进流（不摘要、不加壳）。</item>
/// <item><b>只装被点名的</b>：<c>[L3]</c> 自报块点名才装；空号 ⇒ 不装 + 有告警（不静默）。</item>
/// <item><b>坏数据必须报错</b>：地址表不可重建 ⇒ 行号/seq 不符、id 重复、非 JSON 一律抛错。</item>
/// </list>
/// <para>
/// <b>负例有牙</b>：每一条闸门都配一个「故意做坏 ⇒ 必须红」的反例（METHODOLOGY §十·30）。
/// </para>
/// </summary>
public sealed class SkillLoadingTests
{
    // ---------------- 脚手架 ----------------

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentruntime-skill-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>造一行 <c>L3.jsonl</c>（字段序与真实产物一致）。</summary>
    private static string Line(string id, int seq, string text, string kind = "Procedure", string skill = "demo") =>
        JsonSerializer.Serialize(new
        {
            id,
            skill,
            seq,
            title = $"条 {id}",
            kind,
            domain = "software",
            text,
        });

    /// <summary>造一个仓库形态（<c>&lt;root&gt;/&lt;skill&gt;/L3.jsonl</c>）。</summary>
    private static string Repository(params (string Skill, string[] Lines)[] skills)
    {
        var root = TempDir();
        foreach (var (skill, lines) in skills)
        {
            var dir = Path.Combine(root, skill);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "L3.jsonl"), string.Join('\n', lines) + "\n");
        }

        return root;
    }

    // ---------------- S4：id 语法 + 行号即地址 ----------------

    [Fact]
    public void SkillId_语法_正例绿_反例红()
    {
        Assert.True(SkillId.IsWellFormed("S-bench-007"));
        Assert.True(SkillId.IsWellFormed("S-svnwf-000"));      // hybrid 的「整份」条
        Assert.True(SkillId.IsWellFormed("S-ctxidx-123"));

        // 反例：前缀不对 / 短名带横线 / 序号不是 3 位 / 空 / 大小写。
        foreach (var bad in new[] { "E-bench-007", "S-ben-ch-007", "S-bench-07", "S-bench-0007", "", "S--007", "S-bench-007x" })
        {
            Assert.False(SkillId.IsWellFormed(bad), $"\"{bad}\" 不该被判为合法 id。");
        }

        Assert.True(SkillId.TryParse("S-bench-007", out var shortName, out var seq));
        Assert.Equal("bench", shortName);
        Assert.Equal(7, seq);
        Assert.False(SkillId.TryParse("nope", out _, out _));

        Assert.Equal("S-bench-007", SkillId.Format("bench", 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => SkillId.Format("bench", 1000));
    }

    [Fact]
    public void SkillIndex_仓库形态_行号即地址且可逐条取()
    {
        var root = Repository(
            ("bench", [Line("S-bench-001", 1, "第一条正文", skill: "bench"), Line("S-bench-002", 2, "第二条正文", skill: "bench")]),
            ("svnwf", [Line("S-svnwf-000", 1, "整份正文", kind: "WholeSkill", skill: "svnwf")]));

        var index = SkillIndex.FromRepository(root);

        Assert.Equal(3, index.Count);
        Assert.True(index.TryGet("S-bench-002", out var strip));
        Assert.Equal("第二条正文", strip.Text);          // 第 2 行 ⇒ 第 2 条（行号即地址）
        Assert.Equal(2, strip.Seq);
        Assert.Equal("bench", strip.Skill);
        Assert.True(index.TryGet("s-svnwf-000", out _));  // 大小写不敏感
        Assert.False(index.TryGet("S-bench-999", out _));

        // 确定性：同输入 ⇒ 同 id 次序。
        Assert.Equal(index.Ids, SkillIndex.FromRepository(root).Ids);
    }

    [Fact]
    public void SkillIndex_派生单文件形态_也可按id取()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "l3.jsonl");
        File.WriteAllText(file, Line("S-ctxidx-001", 1, "派生索引里的正文") + "\n");

        var index = SkillIndex.Load(file);   // 文件 ⇒ 单文件形态（不必是目录）

        Assert.Equal(1, index.Count);
        Assert.Equal("派生索引里的正文", index.Get("S-ctxidx-001").Text);
    }

    // ---------------- 负例：坏数据必须报错（闸门有牙） ----------------

    [Fact]
    public void SkillIndex_seq与行号不符_必须报错()
    {
        var root = Repository(("demo", [Line("S-demo-001", 1, "a"), Line("S-demo-002", 9, "b")]));

        var ex = Assert.Throws<InvalidDataException>(() => SkillIndex.FromRepository(root));
        Assert.Contains("行号即地址", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SkillIndex_id重复_必须报错()
    {
        var root = Repository(("a", [Line("S-demo-001", 1, "a", skill: "a")]), ("b", [Line("S-demo-001", 1, "b", skill: "b")]));

        var ex = Assert.Throws<InvalidDataException>(() => SkillIndex.FromRepository(root));
        Assert.Contains("重复", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SkillIndex_非JSON或缺字段或坏id_必须报错()
    {
        var dir = TempDir();

        var notJson = Path.Combine(dir, "bad1.jsonl");
        File.WriteAllText(notJson, "这不是 JSON\n");
        Assert.Throws<InvalidDataException>(() => SkillIndex.FromJsonl(notJson));

        var noText = Path.Combine(dir, "bad2.jsonl");
        File.WriteAllText(noText, "{\"id\":\"S-demo-001\",\"seq\":1}\n");
        var ex = Assert.Throws<InvalidDataException>(() => SkillIndex.FromJsonl(noText));
        Assert.Contains("text", ex.Message, StringComparison.Ordinal);

        var badId = Path.Combine(dir, "bad3.jsonl");
        File.WriteAllText(badId, Line("nope-001", 1, "x"));
        Assert.Throws<InvalidDataException>(() => SkillIndex.FromJsonl(badId));
    }

    [Fact]
    public void SkillIndex_取不到id_报错时必须说清不在索引里()
    {
        var root = Repository(("demo", [Line("S-demo-001", 1, "a")]));
        var ex = Assert.Throws<InvalidDataException>(() => SkillIndex.FromRepository(root).Get("S-demo-042"));
        Assert.Contains("S-demo-042", ex.Message, StringComparison.Ordinal);
    }

    // ---------------- S7：按 id 装载（走既有 append 通道） ----------------

    [Fact]
    public void SkillLoader_按id装载_正文逐字节进流()
    {
        var root = Repository(("demo", [Line("S-demo-001", 1, "第一条"), Line("S-demo-002", 2, "整份正文\n第二行")]));
        var index = SkillIndex.FromRepository(root);
        var stream = new SessionAppendStream();
        var loader = new SkillLoader(index, stream);

        var @event = loader.Load("S-demo-002");

        Assert.Equal(SessionEventKind.Skill, @event.Kind);
        Assert.Equal("整份正文\n第二行", @event.Text);        // 逐字节，一字不改
        Assert.Equal("S-demo-002", @event.Source);          // 账本：装了哪几条可审计（S11）
        Assert.Equal(1, stream.Count);
        stream.ValidateInvariants();
    }

    [Fact]
    public void SkillLoader_同一条不重复装载_且空号抛错()
    {
        var root = Repository(("demo", [Line("S-demo-001", 1, "第一条")]));
        var loader = new SkillLoader(SkillIndex.FromRepository(root), new SessionAppendStream());

        var first = loader.Load("S-demo-001");
        var second = loader.Load("S-demo-001");

        Assert.Same(first, second);          // 已经装过 ⇒ 返回同一条，不追加第二份
        Assert.Equal(1, loader.Stream.Count);
        Assert.True(loader.IsLoaded("S-demo-001"));

        Assert.Throws<InvalidDataException>(() => loader.Load("S-demo-404"));
        Assert.Equal(1, loader.Stream.Count);  // 抛错不留下半截状态
    }

    [Fact]
    public void SkillLoader_装载会落盘_重放能取回()
    {
        var root = Repository(("demo", [Line("S-demo-001", 1, "落盘正文")]));
        var streamPath = Path.Combine(TempDir(), "stream.jsonl");
        var store = new SessionStreamStore(streamPath);

        new SkillLoader(SkillIndex.FromRepository(root), store.Load(), store).Load("S-demo-001");

        var replayed = new SessionStreamStore(streamPath).Load();
        var @event = Assert.Single(replayed.Events);
        Assert.Equal(SessionEventKind.Skill, @event.Kind);
        Assert.Equal("落盘正文", @event.Text);
        Assert.Equal("S-demo-001", @event.Source);
    }

    // ---------------- [L3] 自报块（协议 v6） ----------------

    [Fact]
    public void SkillReport_解析点名_并在下一个块头即停()
    {
        Assert.True(SkillReport.TryParse("答复正文\n[L3] S-demo-001 S-demo-002", out var ids));
        Assert.Equal(["S-demo-001", "S-demo-002"], ids);

        // 分节纪律（PITFALLS #30）：读到下一个块头即停，不吞别人的正文。
        Assert.True(SkillReport.TryParse(
            "[L3] S-demo-001\n[TAIL]\n当前任务: 不该被当成 id", out var mixed));
        Assert.Equal(["S-demo-001"], mixed);

        // 反例：没块 / 块里全是废话 ⇒ 不产生点名单。
        Assert.False(SkillReport.TryParse("普通回复，没有任何块", out var none));
        Assert.Empty(none);
        Assert.False(SkillReport.TryParse("[L3] 看文档吧", out var noise));
        Assert.Empty(noise);
    }

    // ---------------- 回归：被后面的块「压住」也要认（真机实测 2026-09-16 18:4x） ----------------

    [Fact]
    public void SkillReport_被长的TAIL块压住_仍能解析()
    {
        // 真机形状（`benchmark/tools/v6-skill-compliance.py` 的 t1/t3）：[L3] 在前面，后面跟着一个 6 行的 [TAIL] 块。
        // 旧口径（窗口 = 5 行）把 [L3] 压到窗口外 ⇒ **静默不装载**（兑现率 1/3，且不报错）。
        var reply = string.Join('\n',
            "先取能力正文。",
            "",
            "[L3] S-demo-001",
            "",
            "[TAIL]",
            "任务：给出模板",
            "- [ ] 一",
            "- [ ] 二",
            "- [ ] 三",
            "- [ ] 四");

        Assert.True(SkillReport.TryParse(reply, out var ids));
        Assert.Equal(["S-demo-001"], ids);
    }

    [Fact]
    public void 自报块窗口_唯一声明处_且窗宽是一个回复里所有块的总行数量级()
    {
        // 闸门：窗口常数的**唯一声明处 = 协议区**（四处各写一份 = 口径分裂的标本）；
        // 窗宽必须 ≥ 64（[TAIL] ≤12 行 + [DRAFT] ≤16 行 + 正文）——缩它就等于让某个块静默消失。
        Assert.Equal(64, ProtocolText.ReportScanLines);
    }

    // ---------------- 模块接线：模型点名 ⇒ 按 id 进流 ----------------

    [Fact]
    public async Task AppendStream_兑现SKILL自报_按id装载进流()
    {
        var root = Repository(("demo", [Line("S-demo-001", 1, "要点一"), Line("S-demo-002", 2, "要点二")]));
        var module = new AppendStreamModule(
            new SessionAppendStream(),
            skills: SkillIndex.FromRepository(root));

        await module.ObserveAsync(Context(), Request("问题"), Response("答复\n[L3] S-demo-002"), CancellationToken.None);

        var skillEvents = module.Stream.Events.Where(e => e.Kind == SessionEventKind.Skill).ToArray();
        var loaded = Assert.Single(skillEvents);
        Assert.Equal("要点二", loaded.Text);
        Assert.Equal("S-demo-002", loaded.Source);
        Assert.Empty(module.Warnings);
    }

    [Fact]
    public async Task AppendStream_点名空号_不装载但必须告警()
    {
        var root = Repository(("demo", [Line("S-demo-001", 1, "要点一")]));
        var module = new AppendStreamModule(new SessionAppendStream(), skills: SkillIndex.FromRepository(root));

        await module.ObserveAsync(Context(), Request("问题"), Response("答复\n[L3] S-demo-999"), CancellationToken.None);

        Assert.DoesNotContain(module.Stream.Events, e => e.Kind == SessionEventKind.Skill);
        var warning = Assert.Single(module.Warnings);      // 不静默：说了出来
        Assert.Contains("S-demo-999", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppendStream_没接地址表_SKILL自报不产生任何事件()
    {
        var module = new AppendStreamModule(new SessionAppendStream());

        await module.ObserveAsync(Context(), Request("问题"), Response("答复\n[L3] S-demo-001"), CancellationToken.None);

        Assert.DoesNotContain(module.Stream.Events, e => e.Kind == SessionEventKind.Skill);
        Assert.Empty(module.Warnings);
    }

    // ---------------- CLI：--skill-use（离线，不调模型） ----------------

    [Fact]
    public void Cli_SkillUse_把条装进流_未知id报错()
    {
        var root = Repository(("demo", [Line("S-demo-001", 1, "命令行装的正文")]));
        var config = new RuntimeConfiguration
        {
            BaseUrl = "https://example.invalid/v1",
            Model = "m",
            Stream = new StreamConfiguration { Path = Path.Combine(TempDir(), "stream.jsonl") },
            Skill = new SkillConfiguration { Repo = root },
        };
        config.Validate();

        Assert.Equal(0, AgentRuntime.Cli.Program.UseSkillStrips(config, "S-demo-001"));

        var replayed = new SessionStreamStore(config.Stream.Path!).Load();
        var @event = Assert.Single(replayed.Events);
        Assert.Equal("命令行装的正文", @event.Text);

        // 再来一次：不重复装载（仍然只有 1 条）。
        Assert.Equal(0, AgentRuntime.Cli.Program.UseSkillStrips(config, "S-demo-001"));
        Assert.Single(new SessionStreamStore(config.Stream.Path!).Load().Events);

        // 负例：地址表里没有它 ⇒ 抛错（不猜、不静默）。
        Assert.Throws<InvalidDataException>(() => AgentRuntime.Cli.Program.UseSkillStrips(config, "S-demo-404"));
    }

    // ---------------- 协议 v6 预算闸门（正例绿 + 负例有牙） ----------------

    [Fact]
    public void Gate_ProtocolBudget_负例_超预算的文本必须被判违规()
    {
        // 正例：现文本在预算内（同一条判据）。
        Assert.Empty(ProtocolBudget.Violations(ProtocolText.Text, ProtocolText.DeclaredMaxLines, ProtocolText.DeclaredMaxTokens));

        // 负例 1：token 超预算（**按预算算超出量**，不写死字符数 —— 否则预算一放宽，夹具就不咬了；PITFALLS「夹具要让条件真的触发」）。
        var fat = ProtocolText.Text + "\n"
                  + new string('x', (int)(ProtocolText.DeclaredMaxTokens * ProtocolText.CharsPerToken) + 8);
        var tokenViolations = ProtocolBudget.Violations(fat, ProtocolText.DeclaredMaxLines, ProtocolText.DeclaredMaxTokens);
        Assert.Contains(tokenViolations, v => v.Contains("token", StringComparison.Ordinal));

        // 负例 2：行数超预算。
        var tall = ProtocolText.Text + "\n" + string.Join('\n', Enumerable.Repeat("x", ProtocolText.DeclaredMaxLines));
        var lineViolations = ProtocolBudget.Violations(tall, ProtocolText.DeclaredMaxLines, ProtocolText.DeclaredMaxTokens);
        Assert.Contains(lineViolations, v => v.Contains("行数", StringComparison.Ordinal));
    }

    // ---------------- 小工具 ----------------

    private static RuntimeContext Context() => new(null, 0);

    private static ChatRequest Request(string userText) => new()
    {
        Messages = [ChatMessage.User(userText)],
    };

    private static ChatResponse Response(string text) => new()
    {
        Choices = [new ChatChoice { Index = 0, Message = ChatMessage.Assistant(text) }],
    };
}
