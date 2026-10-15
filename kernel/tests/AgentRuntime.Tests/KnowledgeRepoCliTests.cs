using System.Text;
using AgentRuntime.Cli;
using AgentRuntime.Core.Knowledge;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **B4-b 切片 1**：知识库维护的 C# 版（<c>--derive-only</c>）—— 钉**形状**：
/// 技能头判据（下一行须是标记）/ 切片对称剥换行 / 重建按 <c>\n</c> 连接 / JSON 字节形 / 往返三态。
/// <para>逐字节等价已在真机与 python 对证（同一份 <c>knowledge.md</c> + <c>.ledger.json</c> 各跑一次 ⇒ <c>diff -r</c> 全树 SAME）。</para>
/// </summary>
public sealed class KnowledgeRepoCliTests
{
    private const string DemoMd =
        "# 头\n\n"
        + "## demo\n"
        + "<!-- skill: demo | src: /nonexistent/demo/SKILL.md | ends_with_newline: true | mode: indexed -->\n"
        + "```yaml\n---\nname: \"demo\"\n---\n```\n"
        + "**L1 法则**：法则正文\n\n"
        + "**L2 问题地图**\n"
        + "- [ops] 某个问题 → 见 L3 S-demo-001\n\n"
        + "## notaskill\n"
        + "这不是技能头（下一行没有标记）。\n\n"
        + "### S-demo-001 切片一\n<!--wrap: 1 1-->\n\n正文段落\n\n"
        + "### S-demo-002 切片二\n<!--wrap: 0 1-->\n\n更多正文\n";

    [Fact]
    public void 技能头_只有下一行是标记才算()
    {
        var skills = KnowledgeRepoCli.ParseMd(Encoding.UTF8.GetBytes(DemoMd));
        var s = Assert.Single(skills);
        Assert.Equal("demo", s.Name);
        Assert.Equal("/nonexistent/demo/SKILL.md", s.Src);
        Assert.True(s.EndsWithNewline);
        Assert.Equal("法则正文", s.L1);
        Assert.Equal(2, s.Strips.Count);
        Assert.Equal("S-demo-001", s.Strips[0].Id);
        Assert.Equal("切片一", s.Strips[0].Title);
    }

    [Fact]
    public void 切片正文_对称剥换行_wrap引导尾随()
    {
        var s = KnowledgeRepoCli.ParseMd(Encoding.UTF8.GetBytes(DemoMd))[0];
        Assert.Equal("\n正文段落\n", s.Strips[0].Text);   // wrap: 1 1 ⇒ 前 1 / 后 1
        Assert.Equal("更多正文\n", s.Strips[1].Text);      // wrap: 0 1 ⇒ 前 0 / 后 1
    }

    [Fact]
    public void 重建_L4文本按换行连接各切片()
    {
        // 判据（与 py 同）：frontmatter + "\n".join(切片文本) + 末尾换行 —— 切片之间**多一个**换行。
        var skills = KnowledgeRepoCli.ParseMd(Encoding.UTF8.GetBytes(DemoMd));
        var d = KnowledgeRepoCli.Derive(skills, DemoMd);
        var l4Frag = Encoding.UTF8.GetString(d.Files.First(f => f.Key == "l4.xml").Value).TrimEnd('\n');
        var l4 = AgentRuntime.Core.Persistence.DataContractXml.FromFragment<AgentRuntime.Core.Skill.StripRow>(l4Frag);
        Assert.Contains("正文段落\n\n更多正文", l4.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 派生_产出八个文件_源缺失算正常态()
    {
        var skills = KnowledgeRepoCli.ParseMd(Encoding.UTF8.GetBytes(DemoMd));
        var d = KnowledgeRepoCli.Derive(skills, DemoMd);
        Assert.Equal(8, d.Files.Count);
        Assert.Equal(
            ["domains.xml", "ids.xml", "l1.xml", "l2.xml", "l3.xml", "l4.xml", "roundtrip.xml", "summary.xml"],
            d.Files.Select(f => f.Key).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal("source_missing", Assert.Single(d.Roundtrip).State);
        Assert.Equal((0, 1, 0), KnowledgeRepoCli.RtCounts(d.Roundtrip));
    }

    [Fact]
    public void 域问题数_按行乘id对计数_不是行数()
    {
        // 一行指向两个 id ⇒ questions 计 2（与 py 的 `for i_ in ids: d["questions"] += 1` 同位）。
        const string md = "## demo\n"
            + "<!-- skill: demo | src: /nonexistent/demo/SKILL.md | ends_with_newline: true | mode: indexed -->\n"
            + "**L1 法则**：f\n\n**L2 问题地图**\n"
            + "- [ops] 一个问题 → 见 L3 S-a-001 S-b-001\n\n"
            + "### S-a-001 t\n<!--wrap: 0 0-->\n\nx\n";
        var d = KnowledgeRepoCli.Derive(KnowledgeRepoCli.ParseMd(Encoding.UTF8.GetBytes(md)), md);
        var domains = Encoding.UTF8.GetString(d.Files.First(f => f.Key == "domains.xml").Value);
        Assert.Contains("<Questions>2</Questions>", domains, StringComparison.Ordinal);
    }

    [Fact]
    public void 分类总目_解析路由_空条目丢弃()
    {
        const string md = "<!--cat S-abc-001 域甲-->\n#### S-abc-001 域甲\n\n"
                        + "- **这一类是什么**：甲类\n- **不覆盖**：别的\n- 问题一 → L4-aaa-001\n- 问题二 → L4-bbb-001\n\n"
                        + "<!--cat S-def-002 空类-->\n#### S-def-002 空类\n\n";
        var cats = KnowledgeRepoCli.ParseCategories(md);
        var c = Assert.Single(cats);           // 空「这一类是什么」的那条被丢弃
        Assert.Equal("S-abc-001", c.Id);
        Assert.Equal("甲类", c.What);
        Assert.Equal("别的", c.Not);
        Assert.Equal(2, c.Routes.Count);
        Assert.Equal("L4-aaa-001", c.Routes[0].L4);
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "wb-kr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void 三态判定_C_A_B()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "versions"));
        var md = "hello\n";
        File.WriteAllText(Path.Combine(dir, "knowledge.md"), md);
        File.WriteAllText(Path.Combine(dir, "versions", "knowledge-v0001.md"), md);
        var sha = KnowledgeRepoCli.Sha256Hex(Encoding.UTF8.GetBytes(md));
        var doc = new LedgerDoc
        {
            Schema = "x",
            Current = new LedgerVersion { Sha256 = sha },
            Versions = [new LedgerVersion { Version = 1, File = "versions/knowledge-v0001.md", Sha256 = sha }],
        };
        KnowledgeRepoCli.SaveLedgerDoc(dir, doc);

        var led = KnowledgeRepoCli.LoadLedgerInfo(dir);
        Assert.Equal("C", KnowledgeRepoCli.Judge(dir, Encoding.UTF8.GetBytes(md), led).State);   // md == 最新版本

        // 外部手改（md 与账本都不符）⇒ B
        var md2 = "hello\nworld\n";
        File.WriteAllText(Path.Combine(dir, "knowledge.md"), md2);
        Assert.Equal("B", KnowledgeRepoCli.Judge(dir, Encoding.UTF8.GetBytes(md2), KnowledgeRepoCli.LoadLedgerInfo(dir)).State);

        // 账本记的就是当前 md（但未晋级）⇒ A
        doc.Current = new LedgerVersion { Sha256 = KnowledgeRepoCli.Sha256Hex(Encoding.UTF8.GetBytes(md2)) };
        KnowledgeRepoCli.SaveLedgerDoc(dir, doc);
        Assert.Equal("A", KnowledgeRepoCli.Judge(dir, Encoding.UTF8.GetBytes(md2), KnowledgeRepoCli.LoadLedgerInfo(dir)).State);
    }

    [Fact]
    public void 校验_派生一致后全绿()
    {
        var dir = TempDir();
        var md = "<!--cat S-demo-001 演示域-->\n\n- **这一类是什么**：演示\n- 问题一 → L4-demo-001\n\n" + DemoMd;
        File.WriteAllText(Path.Combine(dir, "knowledge.md"), md);
        KnowledgeRepoCli.WriteDerived(dir, KnowledgeRepoCli.Derive(KnowledgeRepoCli.ParseMd(Encoding.UTF8.GetBytes(md)), md).Files);

        // 坑集/交接源给不存在的路径 ⇒ 跳过（不是失败）；下钻指针 L2→L3→L4 应能解析
        var code = KnowledgeRepoCli.Check(dir, Path.Combine(dir, "无坑集.md"), Path.Combine(dir, "无交接"), "rt");
        Assert.Equal(0, code);
    }

    private static (string Dir, string Md, string Sha) StateATemp()
    {
        var dir = TempDir();
        var md = "<!--cat S-demo-001 演示域-->\n\n- **这一类是什么**：演示\n- 问题 → L4-demo-001\n\n" + DemoMd;
        File.WriteAllText(Path.Combine(dir, "knowledge.md"), md);
        KnowledgeRepoCli.WriteDerived(dir, KnowledgeRepoCli.Derive(KnowledgeRepoCli.ParseMd(Encoding.UTF8.GetBytes(md)), md).Files);
        var sha = KnowledgeRepoCli.Sha256Hex(Encoding.UTF8.GetBytes(md));
        KnowledgeRepoCli.SaveLedgerDoc(dir, new LedgerDoc
        {
            Schema = "knowledge-repo/ledger/1",
            Current = new LedgerVersion { Sha256 = sha },
        });
        return (dir, md, sha);
    }

    [Fact]
    public void 晋级_态A_追加版本快照()
    {
        // 前置口径（2026-10-11 修）：derive 要**传 md_text** —— 否则 l3/ids 恒空、与磁盘永不相等 ⇒ 恒拒。
        var (dir, _, sha) = StateATemp();

        Assert.Equal(0, KnowledgeRepoCli.Run(dir, "promote", null, false));
        Assert.True(File.Exists(Path.Combine(dir, "versions", "knowledge-v0001.md")));
        var led = KnowledgeRepoCli.LoadLedgerInfo(dir);
        Assert.Equal(sha, led.CurrentSha256);
        Assert.Single(led.Versions);
    }

    [Fact]
    public void 晋级_派生不一致_拒绝且不建版本()
    {
        var dir = TempDir();
        var md = "<!--cat S-demo-001 演示域-->\n\n- **这一类是什么**：演示\n- 问题 → L4-demo-001\n\n" + DemoMd;
        File.WriteAllText(Path.Combine(dir, "knowledge.md"), md);
        // **不写** .derived ⇒ 前置「派生能从活版重建且逐字节相等」不成立
        var sha = KnowledgeRepoCli.Sha256Hex(Encoding.UTF8.GetBytes(md));
        KnowledgeRepoCli.SaveLedgerDoc(dir, new LedgerDoc
        {
            Schema = "x",
            Current = new LedgerVersion { Sha256 = sha },
        });

        Assert.Equal(1, KnowledgeRepoCli.Run(dir, "promote", null, false));
        Assert.False(Directory.Exists(Path.Combine(dir, "versions")));
    }
}
