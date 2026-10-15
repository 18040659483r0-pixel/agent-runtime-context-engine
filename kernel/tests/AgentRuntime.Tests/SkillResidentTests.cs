using System.Text;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Persistence;
using AgentRuntime.Core.Skill;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **技能常驻层（L1+L2）** 的闸门：稳定（逐字节）、域过滤、坏数据报错、装饰器只挂知识区 Global 槽。
/// <para>为什么值得这么细：常驻层进 <b>R1 稳定前缀</b> —— 它一变，前缀缓存整体归零；
/// 它静默变空，模型就「以为自己有知识、其实没有」。两条都在这里钉住。</para>
/// <para>A4 尾：常驻层源改 <c>l1.xml</c>/<c>l2.xml</c>（行式 XML 片段流）；夹具按真实序列化生成。</para>
/// </summary>
public sealed class SkillResidentTests
{
    /// <summary>造一段 L1 片段（与运行时序列化同一通道 —— 夹具不会与产物分叉）。</summary>
    private static string L1(string skill, string text) =>
        DataContractXml.ToFragment(new L1Row { Skill = skill, Text = text }) + "\n";

    /// <summary>造一段 L2 片段。</summary>
    private static string L2(string skill, string domain, string q, params string[] ids) =>
        DataContractXml.ToFragment(new L2Row { Skill = skill, Domain = domain, Question = q, Ids = [.. ids] }) + "\n";

    private static string WriteDir(string l1, string l2, string l1Name = "l1.xml", string l2Name = "l2.xml")
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-resident-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, l1Name), l1, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, l2Name), l2, new UTF8Encoding(false));
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void 加载_L1按技能升序_与文件次序无关()
    {
        var dir = WriteDir(
            L1("zeta", "Z 的法则") + L1("alpha", "A 的法则"),
            L2("zeta", "software", "Z 怎么办", "S-zeta-001"));

        try
        {
            var resident = SkillResident.Load(dir);
            Assert.Equal(["alpha", "zeta"], resident.L1.Select(r => r.Skill));
            Assert.Contains("A 的法则", resident.Text, StringComparison.Ordinal);
            Assert.Contains("S-zeta-001", resident.Text, StringComparison.Ordinal);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void 同一份输入_两次加载逐字节相同()
    {
        var dir = WriteDir(
            L1("a", "法则 A"),
            L2("a", "software", "问题一", "S-a-001") + L2("a", "ops", "问题二", "S-a-002"));

        try
        {
            var one = SkillResident.Load(dir);
            var two = SkillResident.Load(dir);
            Assert.Equal(one.Text, two.Text);
            Assert.Equal(one.Bytes, two.Bytes);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void 域过滤_只留该域的L2_L1始终全量()
    {
        var dir = WriteDir(
            L1("a", "法则 A") + L1("b", "法则 B"),
            L2("a", "software", "软件问题", "S-a-001") + L2("a", "ops", "运维问题", "S-a-002"));

        try
        {
            var resident = SkillResident.Load(dir, ["software"]);
            Assert.Equal(2, resident.L1.Count);
            Assert.Single(resident.L2);
            Assert.Equal("软件问题", resident.L2[0].Question);
            Assert.Contains("域过滤：software", resident.Text, StringComparison.Ordinal);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void 坏数据_报错而不是静默降级()
    {
        var noIds = WriteDir(
            L1("a", "法则"),
            DataContractXml.ToFragment(new L2Row { Skill = "a", Domain = "software", Question = "没有问题条目", Ids = [] }) + "\n");
        var noL1 = WriteDir(
            DataContractXml.ToFragment(new L1Row { Skill = "a", Text = string.Empty }) + "\n",
            L2("a", "software", "Q", "S-a-001"));

        try
        {
            Assert.Throws<InvalidDataException>(() => SkillResident.Load(noIds));
            Assert.Throws<InvalidDataException>(() => SkillResident.Load(noL1));
        }
        finally
        {
            Cleanup(noIds);
            Cleanup(noL1);
        }
    }

    [Fact]
    public void 缺文件_报错并提示可重建()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-resident-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var ex = Assert.Throws<FileNotFoundException>(() => SkillResident.Load(dir));
            Assert.Contains("可重建", ex.Message, StringComparison.Ordinal);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void 带下划线的仓库根命名也认()
    {
        var dir = WriteDir(
            L1("a", "法则"),
            L2("a", "software", "Q", "S-a-001"),
            l1Name: "_l1.xml", l2Name: "_l2.xml");

        try
        {
            var resident = SkillResident.Load(dir);
            Assert.Single(resident.L1);
        }
        finally { Cleanup(dir); }
    }

    // ---------------- 装饰器：只挂知识区 Global 槽 ----------------

    private sealed class StubSource(FrozenContent? content) : IFrozenContentSource
    {
        public FrozenContent? TryGet(FrozenSlot slot) => content;
    }

    [Fact]
    public void 装饰器_只改知识区Global_其他槽原样()
    {
        var dir = WriteDir(
            L1("a", "法则"),
            L2("a", "software", "Q", "S-a-001"));

        try
        {
            var resident = SkillResident.Load(dir);
            var inner = new StubSource(new FrozenContent("v9", "原有知识正文"));
            var source = new SkillResidentContentSource(inner, resident);

            var knowledge = source.TryGet(new FrozenSlot(FrozenZone.Knowledge, FrozenLayer.Global))!;
            Assert.Contains("原有知识正文", knowledge.Text, StringComparison.Ordinal);
            Assert.Contains("法则", knowledge.Text, StringComparison.Ordinal);
            Assert.Contains("S-a-001", knowledge.Text, StringComparison.Ordinal);

            var rules = source.TryGet(new FrozenSlot(FrozenZone.Rules, FrozenLayer.Global))!;
            Assert.Equal("原有知识正文", rules.Text);          // 非知识区：一字不改
            Assert.Equal("v9", rules.Version);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void 装饰器_内层没有知识文件时常驻层仍然生效()
    {
        var dir = WriteDir(
            L1("a", "法则"),
            L2("a", "software", "Q", "S-a-001"));

        try
        {
            var resident = SkillResident.Load(dir);
            var source = new SkillResidentContentSource(new StubSource(null), resident);

            var knowledge = source.TryGet(new FrozenSlot(FrozenZone.Knowledge, FrozenLayer.Global))!;
            Assert.Equal(resident.Text, knowledge.Text);
            Assert.StartsWith("resident:", knowledge.Version, StringComparison.Ordinal);

            Assert.Null(source.TryGet(new FrozenSlot(FrozenZone.Rules, FrozenLayer.Global)));
        }
        finally { Cleanup(dir); }
    }
}
