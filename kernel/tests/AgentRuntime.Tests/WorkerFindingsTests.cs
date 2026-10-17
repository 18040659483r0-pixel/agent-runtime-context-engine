using System;
using System.IO;
using System.Linq;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Worker;
using AgentRuntime.Hosting;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **worker 产出汇流**（2026-10-06 用户令）：收尾时 master 读 worker 的坑集 + memory，
/// 把新问题收敛进公共 L1/L2 与铁则区；收敛动作由 master 做，运行时**只负责让它不可能被漏掉 + 记账**。
/// </summary>
public sealed class WorkerFindingsTests
{
    [Fact]
    public void 未吸收与已吸收_在同一份文件里记账()
    {
        var dir = Directory.CreateTempSubdirectory("wf-").FullName;
        var inbox = Path.Combine(dir, "worker-findings.txt");
        File.WriteAllText(
            inbox,
            "2026-10-06 16:00 [worker-pitfall] w1 · 坑一\n"
            + "2026-10-06 16:01 [worker-handoff] w1 · 交接一\n",
            System.Text.Encoding.UTF8);

        var d = WorkerFindings.Read(inbox, null);
        Assert.Equal(2, d.Total);
        Assert.Equal(0, d.Absorbed);
        Assert.Equal(2, d.PendingCount);
        Assert.Equal(2, d.Pending.Count);

        WorkerFindings.MarkAbsorbed(inbox, d.PendingCount, DateTimeOffset.Now);

        var d2 = WorkerFindings.Read(inbox, null);
        Assert.Equal(2, d2.Total);
        Assert.Equal(2, d2.Absorbed);
        Assert.Equal(0, d2.PendingCount);   // 收敛后归零（否则收尾项永远挂红）
        Assert.Empty(d2.Pending);
    }

    [Fact]
    public void worker_memory_按_provider_家统计()
    {
        var root = Directory.CreateTempSubdirectory("wr-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "deepinfra", "memory"));
        File.WriteAllText(Path.Combine(root, "deepinfra", "memory", "log.xml"), "<x/>");
        File.WriteAllText(Path.Combine(root, "deepinfra", "memory", "more.xml"), "<x/>");

        var d = WorkerFindings.Read(null, root);
        Assert.Equal(2, d.MemoryFiles);
        Assert.EndsWith(Path.Combine("deepinfra", "memory"), d.MemoryRoot);
    }

    [Fact]
    public void 没配收件箱_就是Unconfigured_不假装查过()
    {
        var item = CloseoutChecklist.Inspect(new RuntimeConfiguration(), DateTimeOffset.Now)
            .Items.Single(i => i.Id == "worker-findings");
        Assert.Equal(CloseoutItemStatus.Unconfigured, item.Status);
    }

    [Fact]
    public void 有未收敛条_收尾件挂Pending_收敛后转Ok()
    {
        var dir = Directory.CreateTempSubdirectory("wfc-").FullName;
        var workspace = Path.Combine(dir, "ws");
        Directory.CreateDirectory(Path.Combine(workspace, "memory"));
        File.WriteAllText(
            Path.Combine(workspace, "memory", "收尾水位.md"),
            "## ⚡ 快速读\n- **当前水位**：2026-01-01\n",
            System.Text.Encoding.UTF8);

        var inbox = Path.Combine(dir, "findings.txt");
        File.WriteAllText(inbox, "2026-10-06 16:00 [worker-pitfall] w1 · 坑一\n", System.Text.Encoding.UTF8);

        var config = new RuntimeConfiguration();
        config.Lifecycle.Workspace = workspace;
        config.Worker.PitfallsInbox = inbox;

        var pending = CloseoutChecklist.Inspect(config, DateTimeOffset.Now).Items.Single(i => i.Id == "worker-findings");
        Assert.Equal(CloseoutItemStatus.Pending, pending.Status);
        Assert.Contains("待收敛", pending.Detail, StringComparison.Ordinal);

        WorkerFindings.MarkAbsorbed(inbox, 1, DateTimeOffset.Now);

        var ok = CloseoutChecklist.Inspect(config, DateTimeOffset.Now).Items.Single(i => i.Id == "worker-findings");
        Assert.Equal(CloseoutItemStatus.Ok, ok.Status);
    }
}
