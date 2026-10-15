using AgentRuntime.Core.Persistence;
using AgentRuntime.Core.Skill;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **L4 同源**（2026-10-10 主人令）：L3 收敛为「分类总目 + 路由」后，**正文住在同目录的 `l4.xml`** ⇒
/// 文件形态的 <see cref="SkillIndex"/> 也必须把它扫进来（否则模型点 `[L3] L4-xxx` 落空、master 取不到正文）。
/// <para>两半都要：① 同目录 l4 **真的被收进索引**；② 分类条（**没有 `text`**）不报错、照旧可寻址。</para>
/// <para>A4 尾：源由 JSON 行改 XML 片段（夹具按真实序列化生成）。</para>
/// </summary>
public sealed class L4IndexTests
{
    private static string Frag(StripRow row) => DataContractXml.ToFragment(row) + "\n";

    [Fact]
    public void 文件形态也扫同目录l4_分类条无正文也不报错()
    {
        var dir = Directory.CreateTempSubdirectory("wb-l4-").FullName;
        // L3 = 分类总目（**没有 text**）—— 这正是新形态
        File.WriteAllText(Path.Combine(dir, "l3.xml"),
            Frag(new StripRow { Id = "S-cat-001", Skill = "分类", Seq = 1, Title = "分类", Kind = "Category" }));
        // L4 = 原 SKILL 正文（逐字）
        File.WriteAllText(Path.Combine(dir, "l4.xml"),
            Frag(new StripRow { Id = "L4-skill-001", Skill = "skill", Seq = 1, Title = "整份正文", Kind = "Heavy", Text = "BODY" }));

        var index = SkillIndex.Load(Path.Combine(dir, "l3.xml"));

        Assert.True(index.TryGet("S-cat-001", out var cat));      // 分类条可寻址（缺 text 不报错）
        Assert.Equal(string.Empty, cat.Text);
        Assert.True(index.TryGet("L4-skill-001", out var l4));    // 同目录 l4 **被收进来**
        Assert.Equal("BODY", l4.Text);
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void 没有同目录l4时_行为逐字不变()
    {
        var dir = Directory.CreateTempSubdirectory("wb-l3only-").FullName;
        File.WriteAllText(Path.Combine(dir, "l3.xml"),
            Frag(new StripRow { Id = "S-old-001", Skill = "old", Seq = 1, Title = "t", Kind = "Reference", Text = "OLD" }));

        var index = SkillIndex.Load(Path.Combine(dir, "l3.xml"));

        Assert.Single(index.Ids);
        Assert.Equal("OLD", index.Get("S-old-001").Text);
    }

    /// <summary>**旧 id 墓碑**（2026-10-10）：589 条切片收敛为 10 条分类后，旧 `S-xxx-nnn` 通过
    /// 同目录 `l3-tombstones.tsv` **别名**到所属分类 —— 历史引用不落空；`Ids`（规范清单）不变。</summary>
    [Fact]
    public void 旧id经墓碑别名到分类_且规范清单不变()
    {
        var dir = Directory.CreateTempSubdirectory("wb-tomb-").FullName;
        File.WriteAllText(Path.Combine(dir, "l3.xml"),
            Frag(new StripRow { Id = "S-cat-001", Skill = "分类", Seq = 1, Title = "分类", Kind = "Category" }));
        File.WriteAllText(Path.Combine(dir, "l3-tombstones.tsv"),
            "S-old-007\tS-cat-001\tx\t旧标题\n");

        var index = SkillIndex.Load(Path.Combine(dir, "l3.xml"));

        Assert.True(index.TryGet("S-old-007", out var aliased));   // 旧 id 仍可寻址
        Assert.Equal("S-cat-001", aliased.Id);
        Assert.Single(index.Ids);                                   // 规范清单不变（别名不进 Ids）
    }
}
