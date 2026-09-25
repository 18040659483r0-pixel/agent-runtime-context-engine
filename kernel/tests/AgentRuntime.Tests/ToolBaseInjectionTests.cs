using AgentRuntime.Core.Tooling;
using AgentRuntime.Hosting;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>禁并行：下面的用例会改**进程级** cwd 与环境变量（别的用例在跑 exec 会被带偏）。</summary>
[CollectionDefinition("进程级基准", DisableParallelization = true)]
public sealed class ProcessWideBaseCollection
{
}

/// <summary>
/// 工具基准注射（2026-09-23，坑 #134/#135/#137 族）：<c>RuntimePaths.PinToolBase</c>
/// 把**进程 cwd** 钉到工作区（活版）+ 注入 <c>WB_ROOT</c> / <c>WB_LIVE</c> / <c>WB_ARCHIVE</c>；
/// 不配 / 路径不存在 ⇒ **不猜**（原样不动）。
/// <para>
/// 背景：2026-09-23 01:15 收尾，工具的相对路径基准是程序集目录 ⇒ `whitebox/workspace` 一类路径全部落空，
/// 连栽 4 次；随后又用 `cd ../../..` 的算术试错。
/// </para>
/// </summary>
[Collection("进程级基准")]
public sealed class ToolBaseInjectionTests
{
    [Fact]
    public void 注射_把_cwd_与三个根钉到工作区()
    {
        using var work = new SnapshotTestWorkspace("tool-base");
        var live = Path.Combine(work.Root, "software-company", "projects", "AgentRuntime", "whitebox", "workspace");
        Directory.CreateDirectory(live);
        Directory.CreateDirectory(Path.Combine(work.Root, "software-company", "whitebox-workspace"));

        var before = Directory.GetCurrentDirectory();
        try
        {
            var pinned = RuntimePaths.PinToolBase(live);

            Assert.NotNull(pinned);
            Assert.True(Directory.Exists(Environment.GetEnvironmentVariable("WB_ARCHIVE")),
                "WB_ARCHIVE 必须指向真实存在的工作副本（2026-09-23 修：曾少数一层 ⇒ 指向不存在的 projects/whitebox-workspace）");
            // cwd **不比字符串**：macOS `/var` → `/private/var` 是符号链接 ⇒ 同一目录两种拼法不相等（2026-09-23 实测）。
            // 语义判据：在活版放一枚标记，cwd 若真是活版，按**相对名**读得到。
            File.WriteAllText(Path.Combine(live, "basis-marker.txt"), "ok");
            Assert.True(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "basis-marker.txt")),
                "cwd 必须落在活版");
            Assert.Equal(Path.GetFullPath(live), Environment.GetEnvironmentVariable("WB_LIVE"));
            Assert.Equal(
                Path.GetFullPath(Path.Combine(live, "..", "..")),
                Environment.GetEnvironmentVariable("WB_ROOT"));                            // 代码仓
            Assert.Equal(
                Path.GetFullPath(Path.Combine(work.Root, "software-company", "whitebox-workspace")),
                Environment.GetEnvironmentVariable("WB_ARCHIVE"));                         // WB 存档区
        }
        finally
        {
            Directory.SetCurrentDirectory(before);
            foreach (var name in new[] { "WB_LIVE", "WB_ROOT", "WB_ARCHIVE" })
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }

    [Fact]
    public void 不配或不存在_不猜()
    {
        Assert.Null(RuntimePaths.PinToolBase(null));
        Assert.Null(RuntimePaths.PinToolBase("   "));
        Assert.Null(RuntimePaths.PinToolBase(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public async Task 注射后_exec_子进程看到活版基准与三个根()
    {
        // 端到端（走**真工具链**，不过模型）：这两件正是 01:15 收尾栽的东西 ——
        // ① 子进程的 cwd 是不是活版（相对路径能不能落地）；② `$WB_ROOT` 这类根常量看不看得见。
        using var work = new SnapshotTestWorkspace("tool-base-exec");
        var live = Path.Combine(work.Root, "projects", "AgentRuntime", "whitebox", "workspace");
        Directory.CreateDirectory(live);
        await File.WriteAllTextAsync(Path.Combine(live, "marker.txt"), "在活版里", CancellationToken.None);

        var before = Directory.GetCurrentDirectory();
        try
        {
            RuntimePaths.PinToolBase(live);

            var outcome = await new ExecTool().ExecuteAsync(
                new ToolContext(ToolLimits.Default, 0, null),
                ToolArgs.Parse("{\"command\":\"pwd && cat marker.txt && echo $WB_ROOT\"}"),
                CancellationToken.None);

            Assert.True(outcome.Ok);
            Assert.Contains(Path.GetFullPath(live), outcome.Text, StringComparison.Ordinal);                       // cwd = 活版
            Assert.Contains("在活版里", outcome.Text, StringComparison.Ordinal);                                    // 相对路径读得到
            Assert.Contains(Path.GetFullPath(Path.Combine(live, "..", "..")), outcome.Text, StringComparison.Ordinal);  // $WB_ROOT
        }
        finally
        {
            Directory.SetCurrentDirectory(before);
            foreach (var name in new[] { "WB_LIVE", "WB_ROOT", "WB_ARCHIVE" })
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }
}
