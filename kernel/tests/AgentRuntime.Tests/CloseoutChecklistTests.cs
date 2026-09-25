using System.Security.Cryptography;
using System.Text.Json;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Hosting;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **收尾件检查**（协议 v10 第 7 条的判据）—— 把「收尾要交什么」从散文变成可执行清单。
/// <para>
/// 五条不变量各一个测试：① 没配工作区 ⇒ **明说未配**（不假装查过）；② 水位之后的日记 ⇒ 计为待抽象；
/// ③ 今天的 handoff / 坑 ⇒ 计为已交（没有就待处理）；④ 知识库**只报事实**（md 指纹 vs 账本指纹），
/// 判定/晋级不在这里复制（单一来源）；⑤ 三层与八类知识源**是数据**（协议文本 / 文档 / 检查共用一份）。
/// </para>
/// </summary>
public sealed class CloseoutChecklistTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-20T13:40:00+08:00");

    private sealed class Work : IDisposable
    {
        public Work()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "agentruntime-closeout-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Path(string relative) => System.IO.Path.Combine(Root, relative);

        public void Write(string relative, string text)
        {
            var full = Path(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        /// <summary>造一个「水位停在 09-17、但已经有 09-18/09-19 两篇日记」的工作区。</summary>
        public Work WithMemory(string watermark = "2026-09-17", params string[] diaryDates)
        {
            Write("memory/收尾水位.md", $"- **当前水位**：截至 {watermark} —— 当日文件 `memory/{watermark}.md` 已写；\n");
            foreach (var date in diaryDates)
            {
                Write($"memory/{date}.md", "记录\n");
            }

            return this;
        }

        public Work WithHandoff(string fileName) => WithFile($"handoff/{fileName}", "交接\n");

        public Work WithFile(string relative, string text)
        {
            Write(relative, text);
            return this;
        }

        /// <summary>造知识库：<paramref name="inSync"/> = md 与账本指纹一致（无未收集改动）。</summary>
        public Work WithKnowledge(bool inSync, int versions = 3)
        {
            var md = Path("knowledge/knowledge.md");
            Write("knowledge/knowledge.md", "# 知识库 v3\n");
            Directory.CreateDirectory(Path("knowledge/versions"));
            for (var i = 1; i <= versions; i++)
            {
                Write($"knowledge/versions/knowledge-v{i:0000}.md", $"v{i}\n");
            }

            var sha = Sha256(File.ReadAllBytes(md));
            Write("knowledge/.ledger.json", JsonSerializer.Serialize(new
            {
                schema = "knowledge-repo/ledger/1",
                current = new { version = versions, file = $"versions/knowledge-v{versions:0000}.md", sha256 = sha },
            }));

            if (!inSync)
            {
                File.AppendAllText(md, "又改了一行\n");
            }

            return this;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }

        private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static RuntimeConfiguration Config(string? workspace, string? pitfalls = null, string? stream = null) => new()
    {
        BaseUrl = "https://example.invalid/v1",
        Model = "m",
        Lifecycle = new LifecycleConfiguration { Workspace = workspace, Pitfalls = pitfalls },
        Stream = new StreamConfiguration { Path = stream },
    };

    // ---------------- ① 没配 ⇒ 明说未配 ----------------

    [Fact]
    public void 没配工作区_五项都标未配_并给出一条说明()
    {
        var result = CloseoutChecklist.Inspect(Config(workspace: null), Now);

        Assert.Equal(5, result.Items.Count);
        Assert.All(result.Items, i => Assert.Equal(CloseoutItemStatus.Unconfigured, i.Status));
        Assert.Equal(0, result.PendingCount);
        Assert.Contains(result.Notes, n => n.Contains("不假装查过", StringComparison.Ordinal));

        var lines = CloseoutChecklist.Render(result, draftLines: 0, misc: []);
        Assert.Contains(lines, l => l.Contains("➖", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("三层", StringComparison.Ordinal));
    }

    [Fact]
    public void 配了工作区但目录不存在_同样走未配_不抛不猜()
    {
        var result = CloseoutChecklist.Inspect(Config(workspace: "/definitely/not/here"), Now);
        Assert.All(result.Items, i => Assert.Equal(CloseoutItemStatus.Unconfigured, i.Status));
    }

    // ---------------- ② 记忆：晚于水位 ⇒ 待抽象 ----------------

    [Fact]
    public void 记忆_缺日记与未抽象分开报_缺件不当成没件()
    {
        // ① **有会话的日子没写日记 ⇒ 缺件**（PITFALLS #93：旧判据在这里给假绿 —— 「0 篇」+ ✅）
        using var work = new Work().WithMemory("2026-09-17", "2026-09-16", "2026-09-17");
        var missingToday = CloseoutChecklist.Inspect(Config(work.Root), Now);   // Now = 2026-09-20
        var item = Assert.Single(missingToday.Items, i => i.Id == "memory");
        Assert.Equal(CloseoutItemStatus.Pending, item.Status);
        Assert.Contains("缺 1 篇", item.Detail, StringComparison.Ordinal);
        Assert.Contains("2026-09-20.md", item.Detail, StringComparison.Ordinal);

        // ② 水位日与今天的日记都在 ⇒ Ok
        work.WithMemory("2026-09-20", "2026-09-19", "2026-09-20");
        var clean = CloseoutChecklist.Inspect(Config(work.Root), Now);
        Assert.Equal(CloseoutItemStatus.Ok, Assert.Single(clean.Items, i => i.Id == "memory").Status);

        // ③ 日记都写了但晚于水位 ⇒ **未抽象**（待处理，但不是缺件）
        work.WithMemory("2026-09-17", "2026-09-18", "2026-09-19", "2026-09-20");
        var pending = CloseoutChecklist.Inspect(Config(work.Root), Now);
        var item2 = Assert.Single(pending.Items, i => i.Id == "memory");
        Assert.Equal(CloseoutItemStatus.Pending, item2.Status);
        Assert.Contains("3 篇未抽象", item2.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void 记忆_有流文件的日子也算应有日记_没会话的日子不算()
    {
        using var work = new Work().WithMemory("2026-09-17", "2026-09-17", "2026-09-20");
        // 09-19 那天真跑过会话（流文件为证）但没写日记 ⇒ 应当报缺；09-18 没会话 ⇒ 不该被要求
        work.WithFile(".stage1/stream-20260919-120000.jsonl", "{}\n");

        var result = CloseoutChecklist.Inspect(
            Config(work.Root, stream: work.Path(".stage1/stream-20260920-130000.jsonl")), Now);

        var item = Assert.Single(result.Items, i => i.Id == "memory");
        Assert.Equal(CloseoutItemStatus.Pending, item.Status);
        Assert.Contains("2026-09-19.md", item.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-09-18.md", item.Detail, StringComparison.Ordinal);
    }

    // ---------------- ②·五 顶层文件指针（PITFALLS #92） ----------------

    [Fact]
    public void 顶层指针_点名的文件缺了就报_改名对照行不算指针()
    {
        using var work = new Work().WithMemory("2026-09-20", "2026-09-20");
        work.WithFile("AGENTS.md", """
            > 常驻只有 `AGENTS.md` + `KNOWLEDGE.md`；记忆索引见 `MEMORYINDEX.md`。
            > ⚠️ 本端改名：`METHODOLOGY.md` ⇒ `KNOWLEDGE.md`（**这一行不是指针**）
            > 水位见 `memory/收尾水位.md`；今天写 `memory/YYYY-MM-DD.md`
            """);
        work.WithFile("KNOWLEDGE.md", "# 知识库\n");
        work.WithFile("MEMORYINDEX.md", "# 记忆索引\n");

        var ok = CloseoutChecklist.Inspect(Config(work.Root), Now);
        var okItem = Assert.Single(ok.Items, i => i.Id == "pointers");
        Assert.Equal(CloseoutItemStatus.Ok, okItem.Status);       // 改名行 / 占位符都不算

        // 真缺一个（改名了但指针没改） ⇒ 报缺
        work.WithFile("AGENTS.md", "> 见 `KNOWLEDGE.md` 与 `GONE.md`\n");
        var bad = CloseoutChecklist.Inspect(Config(work.Root), Now);
        var badItem = Assert.Single(bad.Items, i => i.Id == "pointers");
        Assert.Equal(CloseoutItemStatus.Pending, badItem.Status);
        Assert.Contains("GONE.md", badItem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("KNOWLEDGE.md", badItem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void 顶层指针_按历史原样那行_真的能抓到漂移_豁免只对旧名生效()
    {
        // 这一行是 [WB] 侧 AGENTS.md 改名前**逐字原样**的一句（含「已并」）。
        // 当时真缺的是 METHODOLOGY.md 与 memory/INDEX.md；而 INDEX.md 是「原」名（**不该**被报）。
        using var work = new Work().WithMemory("2026-09-20", "2026-09-20");
        work.WithFile("AGENTS.md",
            "> 其余一切内容见 **`METHODOLOGY.md` 顶部「索引（唯一入口）」**（原 `INDEX.md` 已并为其薄壳，2026-09-14），按关键词定位后 `read`；记忆见 `memory/INDEX.md`；环境/部署见 `TOOLS.md`。\n");

        var result = CloseoutChecklist.Inspect(Config(work.Root), Now);
        var item = Assert.Single(result.Items, i => i.Id == "pointers");

        // 真缺的三个：METHODOLOGY.md（改名了）、memory/INDEX.md（改名了）、TOOLS.md（本端根本没有）
        Assert.Equal(CloseoutItemStatus.Pending, item.Status);
        Assert.Contains("缺 3 个", item.Detail, StringComparison.Ordinal);
        Assert.Contains("METHODOLOGY.md", item.Detail, StringComparison.Ordinal);
        Assert.Contains("memory/INDEX.md", item.Detail, StringComparison.Ordinal);
        Assert.Contains("TOOLS.md", item.Detail, StringComparison.Ordinal);

        // 「原 `INDEX.md`」是旧名 ⇒ **不**报（邻近豁免只对旧名生效）
        Assert.DoesNotContain("、INDEX.md", item.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("INDEX.md、", item.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void 水位读不出_标_Unknown_而不是_Ok()
    {
        using var work = new Work().WithMemory("2026-09-17", "2026-09-18");
        work.Write("memory/收尾水位.md", "# 水位（格式不认识了）\n");

        var result = CloseoutChecklist.Inspect(Config(work.Root), Now);
        var memory = Assert.Single(result.Items, i => i.Id == "memory");
        Assert.Equal(CloseoutItemStatus.Unknown, memory.Status);   // 看不清 ≠ 没问题
    }

    [Theory]
    [InlineData("- **当前水位**：截至 2026-09-17 —— 当日文件已写；", "2026-09-17")]
    [InlineData("> 水位：last_organized_date = 2026-09-16", "2026-09-16")]
    [InlineData("没有水位字样 2026-09-20", null)]
    public void 水位解析_认两种写法(string text, string? expected) =>
        Assert.Equal(expected, CloseoutChecklist.TryReadWatermark(text));

    // ---------------- ③ handoff / 坑：今天的算交了 ----------------

    [Fact]
    public void handoff_今天有条目才算交_没有就待处理()
    {
        using var work = new Work().WithMemory("2026-09-20");
        work.WithFile("handoff/.keep", string.Empty);   // 目录在但今天还没条目

        var none = CloseoutChecklist.Inspect(Config(work.Root), Now);
        Assert.Equal(CloseoutItemStatus.Pending, Assert.Single(none.Items, i => i.Id == "handoff").Status);

        work.WithHandoff("17-白板求解口径-给001-2026-09-20.md");
        var some = CloseoutChecklist.Inspect(Config(work.Root), Now);
        var item = Assert.Single(some.Items, i => i.Id == "handoff");
        Assert.Equal(CloseoutItemStatus.Ok, item.Status);
        Assert.Contains("1 件", item.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void 坑集_按条目里的日期数今天的()
    {
        using var work = new Work().WithMemory("2026-09-20");
        work.WithFile("docs/PITFALLS.md", """
            ## 81. 旧的（2026-09-17，上次）
            ## 82. 今天的（2026-09-20，本次）
            ## 83. 今天的（2026-09-20，本次）
            """);

        var result = CloseoutChecklist.Inspect(Config(work.Root, work.Path("docs/PITFALLS.md")), Now);
        var item = Assert.Single(result.Items, i => i.Id == "pitfalls");
        Assert.Equal(CloseoutItemStatus.Ok, item.Status);
        Assert.Contains("2 条", item.Detail, StringComparison.Ordinal);

        // 没配坑集 ⇒ 未配（不校验），不是「0 条 ⇒ 缺件」。
        var unconfigured = CloseoutChecklist.Inspect(Config(work.Root), Now);
        Assert.Equal(CloseoutItemStatus.Unconfigured, Assert.Single(unconfigured.Items, i => i.Id == "pitfalls").Status);
    }

    // ---------------- ④ 知识库：只报事实 ----------------

    [Fact]
    public void 知识库_只报指纹事实_不等于可晋级_相等则_Ok()
    {
        using var work = new Work().WithMemory("2026-09-20").WithKnowledge(inSync: true);

        var ok = CloseoutChecklist.Inspect(Config(work.Root), Now);
        var item = Assert.Single(ok.Items, i => i.Id == "knowledge");
        Assert.Equal(CloseoutItemStatus.Ok, item.Status);
        Assert.Contains("相等", item.Detail, StringComparison.Ordinal);
        Assert.Contains("3 个", item.Detail, StringComparison.Ordinal);
        Assert.Contains("knowledge-repo.py", item.Detail, StringComparison.Ordinal);   // 判定/晋级归工具（单一来源）

        // md 改了但没晋级（或已改未收）⇒ 指纹不等 ⇒ 待处理。
        work.WithKnowledge(inSync: false);
        var pending = CloseoutChecklist.Inspect(Config(work.Root), Now);
        var changed = Assert.Single(pending.Items, i => i.Id == "knowledge");
        Assert.Equal(CloseoutItemStatus.Pending, changed.Status);
        Assert.Contains("不等", changed.Detail, StringComparison.Ordinal);
    }

    // ---------------- ⑤ 三层 / 八类源：数据化 ----------------

    [Fact]
    public void 收尾三层与知识来源清单_是数据且与协议正文对得上()
    {
        Assert.Equal(["task", "session", "reset"], CloseoutLayers.All.Select(l => l.Id));
        Assert.Contains("handoff", CloseoutLayers.Task.Duty, StringComparison.Ordinal);
        Assert.Contains("L1/L2", CloseoutLayers.Session.Duty, StringComparison.Ordinal);
        Assert.Contains("自持", CloseoutLayers.Reset.Duty, StringComparison.Ordinal);

        // 八类源：前三条是常识项，后五条是最容易漏的（主人 2026-09-20 提醒）。
        Assert.Equal(8, KnowledgeSources.All.Count);
        foreach (var id in new[] { "pitfalls", "memory", "handoff", "docs", "glossary", "skills", "draft", "external" })
        {
            Assert.Contains(KnowledgeSources.All, s => s.Id == id);
        }

        // 协议正文必须点到这三层与「晋级」（否则数据与契约各说一套）。
        Assert.Contains("Closeout has three layers", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("promote the knowledge base", ProtocolText.Text, StringComparison.Ordinal);
        Assert.Contains("handoff", ProtocolText.Text, StringComparison.Ordinal);
    }
}
