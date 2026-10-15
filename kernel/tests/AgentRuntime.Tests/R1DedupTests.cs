using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **R1 去重**（2026-10-10 主人令 · 方案 A）：冻结语料 <c>knowledge/global.md</c> 的**块二**本来就是
/// 技能 L1+L2（`knowledge-l12` 读的正是同一组 `.derived/l1.jsonl` / `l2.jsonl`）⇒ 不许再靠
/// <see cref="SkillResidentContentSource"/> **追加一遍**（实测 master 白付 15,431 字符 ≈ 3,858 tok/轮）。
/// </summary>
public sealed class R1DedupTests
{
    private const string ResidentL1 = "RESIDENT-L1-SENTINEL";
    private const string ResidentL2 = "RESIDENT-L2-SENTINEL";

    private static (string Frozen, string Derived) Sandbox(string frozenBody)
    {
        var frozen = Directory.CreateTempSubdirectory("wb-dedup-frozen-").FullName;
        Directory.CreateDirectory(Path.Combine(frozen, "knowledge"));
        File.WriteAllText(Path.Combine(frozen, "knowledge", "global.md"), frozenBody + "\n");

        var derived = Directory.CreateTempSubdirectory("wb-dedup-derived-").FullName;
        File.WriteAllText(Path.Combine(derived, "l1.xml"),
            AgentRuntime.Core.Persistence.DataContractXml.ToFragment(
                new AgentRuntime.Core.Skill.L1Row { Skill = "alpha", Text = ResidentL1 }) + "\n");
        File.WriteAllText(Path.Combine(derived, "l2.xml"),
            AgentRuntime.Core.Persistence.DataContractXml.ToFragment(
                new AgentRuntime.Core.Skill.L2Row { Skill = "alpha", Domain = "ops", Question = ResidentL2, Ids = ["S-alpha-001"] }) + "\n");
        return (frozen, derived);
    }

    [Fact]
    public void knowledge区不许再挂resident装饰器_且不装两遍()
    {
        const string frozenBlock2 = "FROZEN-BLOCK2-L2-SENTINEL";
        var (frozen, _) = Sandbox(frozenBlock2);

        var config = new RuntimeConfiguration
        {
            Modules = ["knowledge"],
            Frozen = new FrozenConfiguration { Root = frozen },
        };

        var module = ModuleRegistry.Create(config).OfType<KnowledgeModule>().Single();
        var text = string.Join("\n", module.Sections().Select(s => s.Text));

        Assert.Contains(frozenBlock2, text, StringComparison.Ordinal);                  // 冻结块二在
        Assert.DoesNotContain(ResidentL1, text, StringComparison.Ordinal);              // resident 不再追加
        Assert.DoesNotContain(ResidentL2, text, StringComparison.Ordinal);
        Assert.IsNotType<SkillResidentContentSource>(module.ContentSource);             // 来源**永远不是**那个装饰器
    }

    /// <summary>
    /// **机制**：手工把 resident 接回去 ⇒ 同一个哨兵出现**两次**（这就是那个 bug）。
    /// 它同时是上面那条的**应红方向**的证据 —— 谁再把装饰器挂上，上面那条就红。
    /// </summary>
    [Fact]
    public void 手工接回resident_同一哨兵出现两次()
    {
        var (frozen, derived) = Sandbox(ResidentL2);   // 冻结块二里就有同一个哨兵

        var resident = AgentRuntime.Core.Skill.SkillResident.Load(derived, null);
        var source = new SkillResidentContentSource(FrozenContentSource.FromDirectory(frozen), resident);
        var text = string.Join("\n",
            new KnowledgeModule(source, new FrozenSelection("")).Sections().Select(s => s.Text));

        Assert.Equal(2, text.Split(ResidentL2, StringSplitOptions.None).Length - 1);
    }
}
