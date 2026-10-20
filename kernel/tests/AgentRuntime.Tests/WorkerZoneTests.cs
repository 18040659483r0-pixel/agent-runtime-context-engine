using AgentRuntime.Core.Worker;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **worker 的 R1 = 铁则区 + 知识区（L1+L2）**（2026-10-06 主人令 · 方案 A：与 master **逐字节同一份**）。
/// <para>闸门要有牙：成员与次序的**唯一声明处**是 <see cref="ModuleRegistry.WorkerModules"/> ——
/// 有人把两区拿掉、或把「随派发物走的知识块」加回来，本组必须红。</para>
/// </summary>
public sealed class WorkerZoneTests
{
    [Fact]
    public void worker_模块集含铁则区与知识区_且不含派发知识块()
    {
        Assert.Contains("rules", ModuleRegistry.WorkerModules);
        Assert.Contains("knowledge", ModuleRegistry.WorkerModules);
        // 2026-10-07（主人令）：R1 的第三个区必须装上 —— 少了它 worker 会**乱引用已发生过的内容**（潜在漏洞）。
        Assert.Contains("memory-index", ModuleRegistry.WorkerModules);
        Assert.DoesNotContain("worker-knowledge", ModuleRegistry.WorkerModules);
    }

    [Fact]
    public void 知识只走自持_派发物没有知识段()
    {
        Assert.DoesNotContain("KNOWLEDGE:", new CrossProviderTask { Task = "x" }.ComposePrompt());
    }

    [Fact]
    public void 契约里点的_L3L4_地址真的存在()
    {
        // §十·48：契约里写的兜底路径必须**真能走通** —— worker 要按这个地址取 L3/L4，
        // 而旧地址（`knowledge/knowledge.md` / `skill-repo/`）在仓库根下**不存在**（死路，2026-10-06 查实）。
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        Assert.Contains("whitebox/workspace/knowledge/.derived/nodes.xml", WorkerProtocolText.Text, StringComparison.Ordinal);
        Assert.True(
            System.IO.File.Exists(System.IO.Path.Combine(dir!.FullName, "whitebox", "workspace", "knowledge", ".derived", "nodes.xml")),
            "契约里点的 L3/L4 地址在仓库里不存在（死路）—— 改了地址就要同步改契约。");
    }
}
