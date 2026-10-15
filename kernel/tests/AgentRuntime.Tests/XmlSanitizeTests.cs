using AgentRuntime.Core.Persistence;
using AgentRuntime.Core.Skill;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **XML 承载字符的边界**（2026-10-13 修）：<see cref="DataContractXml.Sanitize"/> 此前按 **UTF-16 码元**
/// 逐个判「是不是 XML 允许的字符」，而 <c>Char</c> 产生式允许 <c>#x10000–#x10FFFF</c>
/// ⇒ **合法代理对（emoji / 星形平面字）被判非法，静默折成 U+FFFD**（注释本意只说「孤立代理项」）。
/// 本组判据：合法代理对**逐字保留**；只有**孤立**代理项与 C0 控制符才换 U+FFFD。
/// </summary>
public sealed class XmlSanitizeTests
{
    [Fact]
    public void 合法代理对_emoji_逐字保留()
    {
        const string s = "🍒 樱桃 🚀 끝";
        Assert.Equal(s, DataContractXml.Sanitize(s));
    }

    [Fact]
    public void 孤立代理项_才换_UFFFD()
    {
        var lone = "a\uD83C" + "b";     // 高代理项后不是低代理项
        Assert.Equal("a\uFFFDb", DataContractXml.Sanitize(lone));

        var loneLow = "a\uDE00b";        // 孤立低代理项
        Assert.Equal("a\uFFFDb", DataContractXml.Sanitize(loneLow));
    }

    [Fact]
    public void C0控制符_换_UFFFD_而TAB换行保留()
    {
        Assert.Equal("a\uFFFDb", DataContractXml.Sanitize("a\u0001b"));
        Assert.Equal("a\tb\nc\rd", DataContractXml.Sanitize("a\tb\nc\rd"));
    }

    [Fact]
    public void 片段往返_emoji_不丢()
    {
        var row = new StripRow
        {
            Id = "S-aedit-000", Skill = "agent-file-editing", Seq = 1,
            Title = "🍒 带 emoji 的标题", Kind = "WholeSkill", Domain = "software",
            Text = "第一行 🍒\n第二行 🚀\t制表\n第三行",
        };

        var line = DataContractXml.ToFragment(row);
        var back = DataContractXml.FromFragment<StripRow>(line);

        Assert.Equal(row.Text, back.Text);
        Assert.Equal(row.Title, back.Title);
    }
}
