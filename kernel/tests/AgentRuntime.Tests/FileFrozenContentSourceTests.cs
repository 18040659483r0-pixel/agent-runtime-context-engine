using AgentRuntime.Core;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Modules;

namespace AgentRuntime.Tests;

/// <summary>
/// 磁盘冻结语料来源测试：布局映射、版本头、缺失即不贡献、防目录穿越、切域省 token。
/// </summary>
public sealed class FileFrozenContentSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "frozen-test-" + Guid.NewGuid().ToString("N"));

    public FileFrozenContentSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static FrozenSlot Slot(FrozenZone zone, FrozenLayer layer, string? domain = null, string? project = null) =>
        new(zone, layer, domain, project);

    [Fact]
    public void 读取全局段_并剥离版本头()
    {
        Write("rules/global.md", "<!-- frozen: version=8 -->\n铁则正文");

        var content = new FileFrozenContentSource(_root).TryGet(Slot(FrozenZone.Rules, FrozenLayer.Global));

        Assert.NotNull(content);
        Assert.Equal("8", content!.Version);
        Assert.Equal("铁则正文", content.Text);
        Assert.DoesNotContain("frozen:", content.Text);
    }

    [Fact]
    public void 无版本头_用正文哈希兜底_且内容变则版本变()
    {
        Write("knowledge/global.md", "甲");
        var source = new FileFrozenContentSource(_root);

        var first = source.TryGet(Slot(FrozenZone.Knowledge, FrozenLayer.Global))!;
        Assert.False(string.IsNullOrWhiteSpace(first.Version));

        Write("knowledge/global.md", "乙");
        var second = source.TryGet(Slot(FrozenZone.Knowledge, FrozenLayer.Global))!;

        Assert.NotEqual(first.Version, second.Version);
    }

    [Fact]
    public void 文件缺失_返回null()
    {
        var content = new FileFrozenContentSource(_root).TryGet(Slot(FrozenZone.Knowledge, FrozenLayer.Global));

        Assert.Null(content);
    }

    [Fact]
    public void expert层_按域读文件()
    {
        Write("knowledge/expert/software.md", "软件知识");
        Write("knowledge/expert/finance.md", "财务知识");
        var source = new FileFrozenContentSource(_root);

        Assert.Equal("软件知识", source.TryGet(Slot(FrozenZone.Knowledge, FrozenLayer.Expert, "software"))!.Text);
        Assert.Null(source.TryGet(Slot(FrozenZone.Knowledge, FrozenLayer.Expert, "law")));
    }

    [Fact]
    public void project层与记忆索引_各自映射到文件()
    {
        Write("rules/project/AgentRuntime.md", "项目铁则");
        Write("memory/index.md", "记忆索引");
        var source = new FileFrozenContentSource(_root);

        Assert.Equal("项目铁则", source.TryGet(Slot(FrozenZone.Rules, FrozenLayer.Project, project: "AgentRuntime"))!.Text);
        Assert.Equal("记忆索引", source.TryGet(Slot(FrozenZone.MemoryIndex, FrozenLayer.Global))!.Text);
    }

    [Fact]
    public void 非法标识_拒绝以防目录穿越()
    {
        var source = new FileFrozenContentSource(_root);

        Assert.Null(source.TryGet(Slot(FrozenZone.Knowledge, FrozenLayer.Project, project: "../secret")));
        Assert.Null(source.TryGet(Slot(FrozenZone.Knowledge, FrozenLayer.Expert, domain: "a/b")));
    }

    [Fact]
    public void CRLF与LF_读出后归一化一致()
    {
        Write("knowledge/global.md", "第一行\r\n第二行");
        Write("knowledge/expert/software.md", "第一行\n第二行");
        var source = new FileFrozenContentSource(_root);

        Assert.Equal(
            source.TryGet(Slot(FrozenZone.Knowledge, FrozenLayer.Expert, "software"))!.Text,
            source.TryGet(Slot(FrozenZone.Knowledge, FrozenLayer.Global))!.Text);
    }

    [Fact]
    public void 工厂_空根返回空来源_缺失目录抛错()
    {
        Assert.Null(FrozenContentSource.FromDirectory(null).TryGet(Slot(FrozenZone.Rules, FrozenLayer.Global)));
        Assert.Throws<InvalidDataException>(() => FrozenContentSource.FromDirectory(Path.Combine(_root, "nope")));
    }

    [Fact]
    public async Task 切域_只加载选中域_其它域不进prompt()
    {
        Write("rules/global.md", "<!-- frozen: version=2 -->\n通用铁则");
        Write("rules/expert/software.md", "软件铁则");
        Write("rules/expert/finance.md", "财务铁则");
        Write("memory/index.md", "索引");

        var source = FrozenContentSource.FromDirectory(_root);
        var softwareOnly = new RulesModule(source, new FrozenSelection(["software"]));

        var messages = new List<AgentRuntime.Models.ChatMessage>();
        await softwareOnly.ContributeAsync(new RuntimeContext(null, 0), messages, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "通用铁则", "软件铁则" }, messages.Select(m => m.Content));
        Assert.DoesNotContain("财务铁则", messages.Select(m => m.Content));

        var all = new RulesModule(source, FrozenSelection.All);
        var allMessages = new List<AgentRuntime.Models.ChatMessage>();
        await all.ContributeAsync(new RuntimeContext(null, 0), allMessages, TestContext.Current.CancellationToken);

        // 全部加载时同样只有「有内容的域」被贡献（其余文件不存在 ⇒ 不贡献）
        Assert.Equal(new[] { "通用铁则", "财务铁则", "软件铁则" }, allMessages.Select(m => m.Content));
    }
}
