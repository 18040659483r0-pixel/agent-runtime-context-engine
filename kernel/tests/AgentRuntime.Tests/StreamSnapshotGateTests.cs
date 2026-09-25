using System.Text.RegularExpressions;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Stream;

namespace AgentRuntime.Tests;

/// <summary>
/// **「渲染路径不得直读活表」闸门**（2026-09-22 23:28 真机 SIGABRT，`last-crash.txt` 指名：
/// `LifecycleAggregator.Aggregate` 枚举活表时工具结果正在追加 ⇒ `Collection was modified` ⇒ 未捕获 → abort）。
/// <para>
/// 坑 #88 的修法（「跨线程读一律走 <c>Snapshot()</c>」）当年**只落到了部分读者** —— 帧渲染路径与宿主读口都还直读活表。
/// 本闸门把它变成**可执行**的：源码里再出现 `<c>.Stream.Events</c>` 就红，而不是等下一次真机崩。
/// </para>
/// </summary>
public sealed class StreamSnapshotGateTests
{
    [Fact]
    public void 渲染与宿主路径不得直读活表_只准走Snapshot()
    {
        var offenders = new List<string>();
        foreach (var dir in new[] { "AgentRuntime.Tui", "AgentRuntime.Hosting" })
        {
            var root = Path.Combine(RepoRoot(), "src", dir);
            foreach (var file in Directory.EnumerateFiles(root, "*.cs"))
            {
                var text = File.ReadAllText(file);
                var matches = Regex.Matches(text, @"\.Stream\.Events\b");
                if (matches.Count > 0)
                {
                    offenders.Add($"{dir}/{Path.GetFileName(file)} ×{matches.Count}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "这些文件仍在直读流活表（并发追加时会「集合已修改」⇒ 未捕获 ⇒ SIGABRT）；改用 `Stream.Snapshot()` 或 "
            + "`LifecycleAggregator.Aggregate(stream, …)`：\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void 投影入口必须取当拍拷贝()
    {
        var text = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "AgentRuntime.Core", "Lifecycle", "LifecycleAggregator.cs"));
        var overload = Regex.Match(text, @"Aggregate\(SessionAppendStream stream[^}]*?\}");
        Assert.True(overload.Success, "找不到 `Aggregate(SessionAppendStream …)` 重载（渲染路径的合法入口）。");
        Assert.Contains("stream.Snapshot()", overload.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("Aggregate(stream.Events", overload.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 追加与投影并发_不抛集合已修改()
    {
        // 真现场的重演（免费、确定性足够）：一边追加（线程池），一边走**渲染路径的入口**投影（主线程）。
        var stream = new SessionAppendStream();
        stream.Append(SessionEventKind.UserInput, "开始");

        var writer = Task.Run(async () =>
        {
            for (var i = 0; i < 2_000; i++)
            {
                stream.Append(
                    i % 3 == 0 ? SessionEventKind.ToolResult : SessionEventKind.AgentOutput,
                    $"第 {i} 条（并发写）",
                    i % 3 == 0 ? "exec" : null);
                if (i % 50 == 0)
                {
                    await Task.Yield();
                }
            }
        });

        for (var i = 0; i < 2_000; i++)
        {
            var report = LifecycleAggregator.Aggregate(stream);      // ← 渲染路径入口（内部 Snapshot）
            _ = report.Current;
        }

        await writer;
    }

    /// <summary>从测试目录向上找含 <c>AgentRuntime.slnx</c> 的项目根（与 <c>ProtocolZoneTests</c> 同一手法）。</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到含 AgentRuntime.slnx 的项目根。");
    }
}
