using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Worker;
using AgentRuntime.Modules;
using AgentRuntime.Modules.Worker;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **worker 生命周期两件（②③，2026-09-28）**的闸门。
/// <para>②「停用要动两处」收成**一处**：<c>worker.enabled=false</c> ⇒ 生效清单自动剔掉派发模块。</para>
/// <para>③ memory 的**稳定家**（按 provider 分家）+ **独立水位线**（一次派发 = 一格）。</para>
/// </summary>
public sealed class WorkerLifecycleFixturesTests
{
    private static RuntimeConfiguration Cfg(bool enabled, params string[] modules) => new()
    {
        Modules = [.. modules],
        Worker = new WorkerConfiguration { Enabled = enabled },
    };

    // ---------------- ② 单开关：一处化 ----------------

    [Fact]
    public void 停用_清单里即使留着派发模块_生效清单也会剔掉它()
    {
        // 现场根因：旧实现**只加不剔** ⇒ 停用要动两处（改 enabled 之外还得手改 modules），
        // 漏一处启动直接抛「需要一个 worker runner」。现在剔的动作在**同一处**（EffectiveModules）。
        var off = ModuleRegistry.EffectiveModules(
            Cfg(false, "append-stream", ToolModule.ModuleName, WorkerDispatchModule.ModuleName));

        Assert.DoesNotContain(WorkerDispatchModule.ModuleName, off, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("append-stream", off);          // 其余的清单逐字不动（W1）
    }

    [Fact]
    public void 启用_没列派发模块_也会自动补上_且不重复()
    {
        var on = ModuleRegistry.EffectiveModules(Cfg(true, "append-stream", ToolModule.ModuleName));
        Assert.Contains(WorkerDispatchModule.ModuleName, on, StringComparer.OrdinalIgnoreCase);

        var listed = ModuleRegistry.EffectiveModules(
            Cfg(true, "append-stream", ToolModule.ModuleName, WorkerDispatchModule.ModuleName));
        Assert.Equal(1, listed.Count(m => string.Equals(m, WorkerDispatchModule.ModuleName, StringComparison.OrdinalIgnoreCase)));
    }

    // ---------------- ③ memory 的稳定家 + 独立水位线 ----------------

    [Fact]
    public void memory家_按provider分家_不按派发实例分家()
    {
        var root = "/tmp/wb-root";

        Assert.Equal("/tmp/wb-root/p1/memory", WorkerWatermark.MemoryFor(root, "p1"));
        Assert.Equal("/tmp/wb-root/p1/watermark.xml", WorkerWatermark.PathFor(root, "p1"));

        // 同 provider 的两次派发 ⇒ **同一个家**（换实例不丢结论）；不同 provider 才分家。
        Assert.Equal(WorkerWatermark.MemoryFor(root, "p1"), WorkerWatermark.MemoryFor(root, "p1"));
        Assert.NotEqual(WorkerWatermark.MemoryFor(root, "p1"), WorkerWatermark.MemoryFor(root, "p2"));
    }

    [Fact]
    public void 水位线_一次派发一格_只增_且记结算与否()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "wb-wm-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var path = Path.Combine(dir, WorkerWatermark.FileName);
            var now = DateTimeOffset.Now;

            Assert.Null(WorkerWatermark.Load(path));                       // 还没开工 ⇒ 不存在（不是「零」）

            var first = WorkerWatermark.Advance(path, "p1", "w1", dir, settled: true, now);
            Assert.Equal(1, first.Dispatches);
            Assert.True(first.LastSettled);
            Assert.Equal("w1", first.LastWorkerId);

            // 没结算的那一格照样推进，但记 settled=false（**结算才算收敛**）。
            var second = WorkerWatermark.Advance(path, "p1", "w2", dir, settled: false, now);
            Assert.Equal(2, second.Dispatches);
            Assert.False(second.LastSettled);
            Assert.Equal(2, WorkerWatermark.Load(path)!.Dispatches);       // 落盘了
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 水位线_坏文件必须报错_绝不静默重置()
    {
        // 唯一真相源不许降级（§十·23）：静默重置 = 「已收敛到哪」凭空归零，而没有闸门会喊。
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "wb-wm-bad-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var path = Path.Combine(dir, WorkerWatermark.FileName);
            File.WriteAllText(path, "<WorkerWatermark><Dispatches>不是数字</Dispatches>");

            Assert.ThrowsAny<Exception>(() => WorkerWatermark.Load(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
