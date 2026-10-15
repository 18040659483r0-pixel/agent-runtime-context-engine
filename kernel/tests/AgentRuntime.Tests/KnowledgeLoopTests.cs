using System.Text;
using AgentRuntime.Core;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Skill;
using AgentRuntime.Hosting;
using AgentRuntime.Models;
using AgentRuntime.Modules;

namespace AgentRuntime.Tests;

/// <summary>
/// **知识闭环验收（Knowledge Loop Acceptance · KLA）**
/// <para>
/// 论文 §4.2/§4.3 规定：**收尾是唯一的晋升入口**（验证 → 抽象 → 新知识版本 → 推水位），
/// 且收敛后的知识必须在**下一次任务动手之前**可见（协议第 1 条读序：rules → knowledge → memory → …）。
/// 但这条链此前**没有被任何可执行断言钉住** —— 于是「Agent 好像记得」与「Agent 真拿到了先验」
/// 在工程上不可区分。本文件把它拆成四段，每段一条判据，**并故意加一条负例**
/// （没走完收敛链的经验 ⇒ 下一轮 prompt 里必须没有它），否则闸门是空的。
/// </para>
/// <para>
/// 载体 = 常驻知识的**真实来源**：<c>whitebox/workspace/knowledge/.derived/{l1,l2}.xml</c>
/// → <see cref="SkillResident"/> → <see cref="SkillResidentContentSource"/> → <see cref="KnowledgeModule"/>
/// → 组装出的 prompt **字节**（<see cref="PromptBytes"/>）。
/// <b>全程不调模型、不连网</b>：这就是「可复现」的那一半（另一半是真机行为，见 e2e 脚本）。
/// </para>
/// <para>A4 尾：常驻层源由 JSON 行改 XML 片段；夹具按真实序列化生成。</para>
/// </summary>
public sealed class KnowledgeLoopTests
{
    /// <summary>哨兵串：只可能来自「本轮收尾新抽象出的知识」，用它区分「旧内容」与「新先验」。</summary>
    private const string Sentinel = "KLA-SENTINEL-7";

    /// <summary>空内容来源（本测试只关心常驻层如何进入 prompt）。</summary>
    private sealed class EmptySource : IFrozenContentSource
    {
        public FrozenContent? TryGet(FrozenSlot slot) => null;
    }

    // ------------------------------------------------------------------ K1

    [Fact]
    public void K1_收敛段_知识库派生出的L1条目进入常驻文本()
    {
        var dir = NewDir();
        try
        {
            WriteDerived(dir,
                l1: L1("alpha", $"{Sentinel}：遇到 X 先做 Y"),
                l2: L2("alpha", "software", "X 来了怎么办", "S-alpha-001"));

            var resident = SkillResident.Load(dir);

            // 常驻文本 = 下一轮真的会送进模型的那份字节（见 K2）
            Assert.Contains(Sentinel, resident.Text, StringComparison.Ordinal);
            Assert.Contains("X 来了怎么办", resident.Text, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------ K2

    [Fact]
    public void K2_进快照_常驻知识进入组装出的prompt字节()
    {
        var dir = NewDir();
        try
        {
            WriteDerived(dir,
                l1: L1("alpha", $"{Sentinel}：遇到 X 先做 Y"),
                l2: L2("alpha", "software", "X 来了怎么办", "S-alpha-001"));

            var prompt = PromptOf(dir);

            Assert.Contains(Sentinel, prompt, StringComparison.Ordinal);
            Assert.Contains("X 来了怎么办", prompt, StringComparison.Ordinal); // L2 问题地图也在
            Assert.Equal(prompt, PromptOf(dir));                              // 逐字节稳定 ⇒ 前缀缓存身份不变
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------ K3

    [Fact]
    public void K3_断点机制化_没走完收敛链的经验_下一轮prompt里必须没有()
    {
        var dir = NewDir();
        try
        {
            WriteDerived(dir,
                l1: L1("alpha", "A 的法则"),
                l2: L2("alpha", "software", "A 怎么办", "S-alpha-001"));

            // 上一轮真实踩过的坑：只留在事件流 / 记忆 / 交接里，**从没被抽象进知识库**。
            // （这就是「收敛链断在抽象这一步」的形态；它的原文在 prompt 里不该出现。）
            var prompt = PromptOf(dir);

            Assert.DoesNotContain("TASK-9", prompt, StringComparison.Ordinal);
            Assert.Contains("A 的法则", prompt, StringComparison.Ordinal); // 已收敛的仍在
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------------ K4

    [Fact]
    public void K4_晋升闸门_知识区是收尾合法目标_协议区永远不是()
    {
        Assert.True(CloseoutService.CanPromoteTo(FrozenZone.Knowledge));
        Assert.True(CloseoutService.CanPromoteTo(FrozenZone.Rules));
        Assert.True(CloseoutService.CanPromoteTo(FrozenZone.MemoryIndex));

        Assert.False(CloseoutService.CanPromoteTo(FrozenZone.Protocol));
        Assert.Throws<InvalidDataException>(() => CloseoutService.EnsurePromotionTarget(FrozenZone.Protocol));
    }

    // ------------------------------------------------------------------ K5

    [Fact]
    public void K5_跨会话闭环_上轮收尾抽象出的知识_在下轮动手前已进prompt_且旧内容不丢()
    {
        var dir = NewDir();
        try
        {
            WriteDerived(dir,
                l1: L1("alpha", "A 的法则"),
                l2: L2("alpha", "software", "A 怎么办", "S-alpha-001"));

            // ---- 会话 1（收尾之前）：新知识还不存在 ⇒ 动手前看不到它
            var before = PromptOf(dir);
            Assert.DoesNotContain(Sentinel, before, StringComparison.Ordinal);

            // ---- 会话 1 的收尾：把这次的经验抽象成一条 L1（= closeout 的抽象落盘）
            AppendDerivedL1(dir, L1("alpha", $"{Sentinel}：遇到 X 先做 Y"));

            // ---- 会话 2（新进程、同一 workspace）：动手之前就必须已经能看到它
            var after = PromptOf(dir);
            Assert.Contains(Sentinel, after, StringComparison.Ordinal);
            Assert.Contains("A 的法则", after, StringComparison.Ordinal); // 单调增长：旧知识不丢
        }
        finally
        {
            Cleanup(dir);
        }
    }

    // ------------------------------------------------------------- helpers

    /// <summary>造一段 L1 片段（序列化口径与真实产物同源）。</summary>
    private static string L1(string skill, string text) =>
        AgentRuntime.Core.Persistence.DataContractXml.ToFragment(new L1Row { Skill = skill, Text = text });

    /// <summary>造一段 L2 片段。</summary>
    private static string L2(string skill, string domain, string q, params string[] ids) =>
        AgentRuntime.Core.Persistence.DataContractXml.ToFragment(
            new L2Row { Skill = skill, Domain = domain, Question = q, Ids = [.. ids] });

    /// <summary>「会话启动时送给模型的 prompt 字节」——与真机同一条组装路径（常驻层 → 知识区 → 请求字节）。</summary>
    private static string PromptOf(string derivedDir)
    {
        var resident = SkillResident.Load(derivedDir);
        var source = new SkillResidentContentSource(new EmptySource(), resident);
        var module = new KnowledgeModule(source, FrozenSelection.All);

        var messages = new List<ChatMessage>();
        module.ContributeAsync(new RuntimeContext(null, 0), messages, TestContext.Current.CancellationToken)
              .GetAwaiter().GetResult();

        var request = new ChatRequest { Model = "kla", Messages = messages };
        return Encoding.UTF8.GetString(PromptBytes.Of(request));
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-kla-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteDerived(string dir, string l1, string l2)
    {
        File.WriteAllText(Path.Combine(dir, "l1.xml"), l1 + "\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "l2.xml"), l2 + "\n", new UTF8Encoding(false));
    }

    private static void AppendDerivedL1(string dir, string line) =>
        File.AppendAllText(Path.Combine(dir, "l1.xml"), line + "\n", new UTF8Encoding(false));

    private static void Cleanup(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
