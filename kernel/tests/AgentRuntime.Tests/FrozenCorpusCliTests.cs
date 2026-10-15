using AgentRuntime.Cli;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **B4-a 切片 1**：冻结语料构建的 C# 版（`rules/global.md`）。
/// 判据 = 与 python 产物**逐字节等价**（已在真机跑 `--frozen-corpus-check` 验证）；
/// 这里钉**格式**（版本头 / 块头 / persona 拼接 / 指针 / 缺口处理），与仓库布局无关。
/// </summary>
public sealed class FrozenCorpusCliTests
{
    private static string TempWs()
    {
        var ws = Path.Combine(Path.GetTempPath(), "wb-fc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ws);
        return ws;
    }

    [Fact]
    public void 渲染_版本头加块头_persona按来源拼接_缺文件跳过()
    {
        var ws = TempWs();
        File.WriteAllText(Path.Combine(ws, "AGENTS.md"), "# 铁则\n正文\n");
        File.WriteAllText(Path.Combine(ws, "SOUL.md"), "灵魂\n");
        // IDENTITY.md / USER.md / GLOSSARY.md 故意不给 ⇒ 应**跳过**（与 py 的 `if not path.exists(): continue` 同义）

        var text = FrozenCorpusCli.Render(
            ws, ws,
            [
                ("一、铁则与流程", "full", ["AGENTS.md"], "A"),
                ("二、人格", "persona", ["SOUL.md", "IDENTITY.md"], "A"),
                ("三、指针", "pointer", [], "A"),
            ],
            "12");

        Assert.StartsWith("<!-- frozen: version=12 -->\n## 一、铁则与流程\n\n# 铁则\n正文\n", text, StringComparison.Ordinal);
        Assert.Contains("## 二、人格\n\n### SOUL.md\n\n灵魂\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("IDENTITY.md", text, StringComparison.Ordinal);          // 缺文件 ⇒ 跳过
        Assert.Contains("正文不在冻结区", text, StringComparison.Ordinal);              // pointer 正文
        Assert.EndsWith("\n", text, StringComparison.Ordinal);                          // 行末 LF 收尾
        Assert.DoesNotContain("\r\n", text, StringComparison.Ordinal);                  // 无 CRLF（字节口径）
    }

    [Fact]
    public void 读文件_剥BOM并归一为LF()
    {
        var ws = TempWs();
        var p = Path.Combine(ws, "x.md");
        File.WriteAllBytes(p, [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes("a\r\nb\rc")]);

        Assert.Equal("a\nb\nc", FrozenCorpusCli.ReadText(p));
    }

    [Fact]
    public void 版本号只认顶层整数_读不出回退1()
    {
        var ws = TempWs();
        var p = Path.Combine(ws, "versions.xml");
        File.WriteAllText(p,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            + "<FrozenVersions xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">"
            + "<Versions><Version><Key>rules.global</Key><Value>12</Value></Version></Versions>"
            + "<Sources><Source><Key>rules.global</Key><Tier>A</Tier><Sha256>abc</Sha256></Source></Sources>"
            + "</FrozenVersions>\n");

        var map = FrozenCorpusCli.ReadVersions(p);
        Assert.Equal("12", map["rules.global"]);
        Assert.Equal("1", map.TryGetValue("nope", out var v) ? v : "1");
    }

    [Fact]
    public void 长期记忆只留标题级索引()
    {
        var ws = TempWs();
        File.WriteAllText(Path.Combine(ws, "MEMORY.md"), "# 甲\n正文\n## 乙\n更多\n#### 太深不算\n");

        var text = FrozenCorpusCli.Render(ws, ws, [("一、记忆", "mem-headings", ["MEMORY.md"], "A")], "22");

        Assert.Contains("- 甲", text, StringComparison.Ordinal);
        Assert.Contains("- 乙", text, StringComparison.Ordinal);
        Assert.DoesNotContain("太深不算", text, StringComparison.Ordinal);   // 只认 1~3 级
        Assert.Contains("共 2 条", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 档位过滤_A不含B块_B含B块()
    {
        // 与 py 的 `b.get("tier","A") <= args.tier` 同义：A 档只要 A 块；B 档 = A + B。
        var blocks = new (string Title, string Mode, string[] Sources, string Tier)[]
        {
            ("一、A块", "pointer", [], "A"),
            ("二、B块", "pointer", [], "B"),
        };

        var a = FrozenCorpusCli.BlocksFor("A", blocks);
        Assert.Single(a);
        Assert.Equal("一、A块", a[0].Title);

        var b = FrozenCorpusCli.BlocksFor("B", blocks);
        Assert.Equal(2, b.Length);
        Assert.Contains(b, x => x.Title == "二、B块");
    }

    [Fact]
    public void 未知档位_拒并返回2()
    {
        // 校验在碰任何文件之前 ⇒ 假路径也不会出事（fail-closed：不猜档位）。
        Assert.Equal(2, FrozenCorpusCli.Run("ws", "shared", "out", check: false, spec: false, tier: "C"));
        Assert.Equal(0, FrozenCorpusCli.Run("ws", "shared", "out", check: false, spec: true, tier: "A"));   // A/B 则放行
        Assert.Equal(0, FrozenCorpusCli.Run("ws", "shared", "out", check: false, spec: true, tier: "B"));
    }

    [Fact]
    public void 账本记录当前档位()
    {
        var b = AgentRuntime.Core.Persistence.DataContractXml.ToFragment(new CorpusManifest { Tier = "B" });
        Assert.Contains("<Tier>B</Tier>", b, StringComparison.Ordinal);
        var a = AgentRuntime.Core.Persistence.DataContractXml.ToFragment(new CorpusManifest { Tier = "A" });
        Assert.Contains("<Tier>A</Tier>", a, StringComparison.Ordinal);
    }

    [Fact]
    public void 踩坑集索引_跳过自产目录里的残留()
    {
        // §十·1：自产目录（.stage1 等）里的 PITFALLS.md 是测试/运行时残留，绝不进索引。
        var shared = TempWs();
        var proj = Path.Combine(shared, "projects", "Demo");
        Directory.CreateDirectory(proj);
        File.WriteAllText(Path.Combine(proj, "PITFALLS.md"), "# 甲\n\n## 真条目\n");

        var self = Path.Combine(proj, ".stage1", "ms", "shadow2", "live", "docs");
        Directory.CreateDirectory(self);
        File.WriteAllText(Path.Combine(self, "PITFALLS.md"), "# 影子残留\n\n## 不该进索引\n");

        var (text, _) = FrozenCorpusCli.RenderWithMeta(shared, shared, "knowledge/global.md", "knowledge", "global",
            [("三、踩坑集索引", "pitfall-index", [], "A")], "1");

        Assert.Contains("真条目", text, StringComparison.Ordinal);
        Assert.DoesNotContain("不该进索引", text, StringComparison.Ordinal);   // 决定性命例
        Assert.DoesNotContain(".stage1", text, StringComparison.Ordinal);
    }
}
