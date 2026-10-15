using AgentRuntime.Cli;
using AgentRuntime.Core.Knowledge;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **B4-b 依赖件**：坑集/交接 → 逐条 JSONL 索引（C# 版）。
/// 逐字节（派生产物）与逐行（<c>--check</c>）等价已在真机与 <c>l3-unify.py</c> 对证。
/// </summary>
public sealed class L3UnifyCliTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "wb-l3u-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void 坑集_按二级标题切条_行号即地址()
    {
        var dir = TempDir();
        var md = Path.Combine(dir, "PITFALLS.md");
        File.WriteAllText(md, "# 坑集\n\n## 一、甲\n正文1\n\n## 二、乙\n正文2\n");

        var (records, _) = L3UnifyCli.Derive("pitfalls", md, "rt");

        Assert.Equal(2, records.Count);
        Assert.Equal("P-rt-001", records[0].Id);
        Assert.Equal("一、甲", records[0].Title);
        Assert.Equal(3, records[0].StartLine);
        Assert.Equal(5, records[0].EndLine);
        Assert.Equal("P-rt-002", records[1].Id);
    }

    [Fact]
    public void 无二级标题_整份一条_H1作标题()
    {
        var dir = TempDir();
        var md = Path.Combine(dir, "h.md");
        File.WriteAllText(md, "# 只有标题\n正文\n");

        var (records, _) = L3UnifyCli.Derive("pitfalls", md, "rt");

        Assert.Equal("只有标题", Assert.Single(records).Title);
        Assert.Equal(1, records[0].StartLine);
    }

    [Fact]
    public void 往返与id稳定_闸门全绿()
    {
        var dir = TempDir();
        var md = Path.Combine(dir, "PITFALLS.md");
        File.WriteAllText(md, "# 坑集\n\n## 一、甲\n正文1\n\n## 二、乙\n正文2\n");
        var outDerived = Path.Combine(dir, ".derived", "pitfalls.xml");
        var ledger = Path.Combine(dir, ".unify-ledger.xml");

        Assert.Equal(0, L3UnifyCli.Run(md, "pitfalls", "rt", outDerived, "derive", ledger, null));

        var r = L3UnifyCli.Check("pitfalls", md, "rt", outDerived, ledger, null);
        Assert.Equal(0, r.Code);
        Assert.Contains("id 唯一             共 2 条", r.Stdout, StringComparison.Ordinal);
        Assert.Contains("往返逐字节         1/1", r.Stdout, StringComparison.Ordinal);
        Assert.Contains("[CHECK] 全部通过", r.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void 交接_文件名前导号重复_加字母后缀()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "04-甲.md"), "# 甲\nA\n");
        File.WriteAllText(Path.Combine(dir, "04-乙.md"), "# 乙\nB\n");

        var (records, _) = L3UnifyCli.Derive("handoff", dir, null);

        Assert.Equal(["H-04-001", "H-04a-001"], records.Select(r => r.Id).ToArray());
    }
}
