using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Index;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Tooling;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **SourceIndex（可重建投影）的不变量测试** —— 批次 1 的判据四件：
/// ① 真身变了 ⇒ 报「投影陈旧」并重建（绝不静默使用）；② 投影移走 ⇒ 逐字节重建；
/// ③ 摘要只走按需区段（真跑一次 index 后**常驻前缀逐字节不变**）；④ 工具面 index 入口可用且只读。
/// <para>三条硬约束各有一枚钉子：Map 只定位（真身才算数）/ 不许进 R1 / fail-open。</para>
/// </summary>
public sealed class SourceIndexTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>与协议区测试同款的空模块配置（协议区仍在前缀最前，便于逐字节比对）。</summary>
    private static RuntimeConfiguration Config() => new()
    {
        BaseUrl = "https://example.invalid/v1",
        Model = "m",
        Modules = [],
        Frozen = new FrozenConfiguration(),
    };

    /// <summary>
    /// 造一个**像仓库根**的沙箱：有 <c>src/</c> + <c>AgentRuntime.slnx</c>。
    /// <para>后者是 <c>WindowResolver.FindRepoRoot</c> 认的两个标记之一 —— 沙箱必须是**合法**的仓库根，
    /// 否则 index 会走 fail-open 分支（那是另一条判据，不该混进本条）。</para>
    /// </summary>
    private static string Sandbox()
    {
        var root = Path.Combine(Path.GetTempPath(), "wb-srcidx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "AgentRuntime.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(root, "src", "A.cs"), "namespace N;\npublic sealed class A { }\n");
        return root;
    }

    // ---------------- ① 陈旧 ⇒ 报「投影陈旧」并重建（绝不静默使用） ----------------

    [Fact]
    public void 真身变了_报投影陈旧并重建()
    {
        var root = Sandbox();
        try
        {
            SourceIndexStore.RebuildNow(root);
            var path = SourceIndexStore.ProjectionPath(root);
            var first = File.ReadAllText(path);

            // 改**真身**（不是手改投影）：新增一个文件 ⇒ 投影必然与真身不一致。
            File.WriteAllText(Path.Combine(root, "src", "B.cs"), "namespace N;\npublic sealed class B { }\n");

            var check = SourceIndexStore.Check(root, rebuildWhenStale: true);
            Assert.Equal(IndexFreshness.Stale, check.Freshness);
            Assert.True(check.Rebuilt);
            Assert.Contains("陈旧", check.Detail, StringComparison.Ordinal);

            var second = File.ReadAllText(path);
            Assert.NotEqual(first, second);
            Assert.Contains("B.cs", second, StringComparison.Ordinal);   // 新地图照着真身重算过

            // 重建之后应当即新鲜（陈旧只报一次，且已按真身修好）。
            Assert.Equal(IndexFreshness.Fresh, SourceIndexStore.Check(root).Freshness);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------------- ② 投影移走 ⇒ 逐字节重建 ----------------

    [Fact]
    public void 投影移走_能逐字节重建()
    {
        var root = Sandbox();
        var backup = Path.Combine(Path.GetTempPath(), "wb-srcidx-backup-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            SourceIndexStore.RebuildNow(root);
            var path = SourceIndexStore.ProjectionPath(root);
            var before = File.ReadAllBytes(path);

            // 移走（不是删）：既满足「删掉后重建」的语义，又留下原始字节做比对。
            File.Move(path, backup);

            var check = SourceIndexStore.Check(root, rebuildWhenStale: true);
            Assert.Equal(IndexFreshness.Missing, check.Freshness);
            Assert.True(check.Rebuilt);

            var after = File.ReadAllBytes(path);
            Assert.Equal(before, after);   // 逐字节一致（渲染口径：LF / 无 BOM / 无时间戳 / 无机器路径 / 固定排序）
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            if (File.Exists(backup)) File.Delete(backup);
        }
    }

    // ---------------- ③ 摘要只走按需区段：真跑一次 index，常驻前缀逐字节不变 ----------------

    [Fact]
    public async Task 摘要不进常驻前缀()
    {
        var root = Sandbox();
        try
        {
            var before = FrozenPrefix.Assemble(ModuleRegistry.Create(Config()));

            // 真跑一次（同一个工具、同一条路径 —— 不是造一个假摘要去断言）。
            var outcome = await new IndexTool().ExecuteAsync(
                new ToolContext(new ToolLimits { WorkingDirectory = root }, 0, null),
                ToolArgs.Parse("{}"),
                Ct);

            Assert.True(outcome.Ok);
            Assert.Contains("【结构地图】", outcome.Text, StringComparison.Ordinal);

            var after = FrozenPrefix.Assemble(ModuleRegistry.Create(Config()));
            Assert.Equal(before.PromptText, after.PromptText);
            Assert.Equal(before.Id, after.Id);

            // 反向自检：摘要**根本没进**常驻前缀（不写这一条，上面两行可能恒真）。
            Assert.DoesNotContain("【结构地图】", before.PromptText, StringComparison.Ordinal);
            Assert.DoesNotContain("SourceIndex", before.PromptText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---------------- ④ 工具面 index 入口：已登记、只读、进得去协议正文 ----------------

    [Fact]
    public void 工具面_index_入口可用()
    {
        var tools = ToolSet.Default();
        Assert.Contains(ToolNames.Index, tools.Select(t => t.Name));
        Assert.Contains(ToolNames.Index, ToolNames.All);

        var index = tools.Single(t => t.Name == ToolNames.Index);
        Assert.Equal(ToolRisk.ReadOnly, index.Risk);          // 只读 ⇒ 免批（与 ToolNames 的分级同源）

        var face = ToolNames.FaceText(tools);
        Assert.Contains("index{symbol}", face, StringComparison.Ordinal);
        // v31：协议正文只列 master 面（index/scope）—— 与代码同一份来源渲染（ToolFaceClause.MasterKeys）。
        Assert.Contains(ToolFaceClause.MasterKeys, ProtocolText.Text, StringComparison.Ordinal);   // 协议与代码同一份渲染
    }
}
