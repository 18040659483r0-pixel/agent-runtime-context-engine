using AgentRuntime.Core.Frozen;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **worker 自己的记忆索引源**（2026-10-10 主人令 · 治水位待办 ①）：<c>memory-index</c> 一区改指
/// <b>worker 自己的家</b>；**铁则区 / 知识区仍与 master 同一份**。
/// <para>两半都要（§十·30）：① 换了源 ⇒ worker 那份**真的被读到**；② 只换一格 ⇒ 别的区**不许**跟着换；
/// ③ 没写 worker 索引 ⇒ 该段**不存在**（不冒充 master 的）。</para>
/// </summary>
public sealed class WorkerMemoryIndexSourceTests
{
    private static string WriteFile(string dir, string relative, string content)
    {
        var path = Path.Combine(dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void memory_index走worker自己的家_其余区仍走master那份()
    {
        var master = Directory.CreateTempSubdirectory("wb-master-").FullName;
        WriteFile(master, Path.Combine("rules", "global.md"), "MASTER-RULES");
        WriteFile(master, Path.Combine("knowledge", "global.md"), "MASTER-KNOWLEDGE");
        WriteFile(master, Path.Combine("memory", "index.md"), "MASTER-INDEX");

        var home = Directory.CreateTempSubdirectory("wb-worker-").FullName;
        WriteFile(home, Path.Combine("memory", "index.md"), "WORKER-INDEX");

        var src = WorkerMemoryIndexSource.Of(new FileFrozenContentSource(master), home);

        // ① 记忆索引 ⇒ worker 自己那份
        var index = src.TryGet(new FrozenSlot(FrozenZone.MemoryIndex, FrozenLayer.Global));
        Assert.NotNull(index);
        Assert.Equal("WORKER-INDEX", index!.Text);

        // ② 铁则 / 知识 ⇒ 仍与 master 同一份
        Assert.Equal("MASTER-RULES", src.TryGet(new FrozenSlot(FrozenZone.Rules, FrozenLayer.Global))!.Text);
        Assert.Equal("MASTER-KNOWLEDGE", src.TryGet(new FrozenSlot(FrozenZone.Knowledge, FrozenLayer.Global))!.Text);
    }

    [Fact]
    public void worker没写索引_该段不存在_不冒充master那份()
    {
        var master = Directory.CreateTempSubdirectory("wb-master2-").FullName;
        WriteFile(master, Path.Combine("memory", "index.md"), "MASTER-INDEX");
        var bare = Directory.CreateTempSubdirectory("wb-worker2-").FullName;

        var src = WorkerMemoryIndexSource.Of(new FileFrozenContentSource(master), bare);

        Assert.Null(src.TryGet(new FrozenSlot(FrozenZone.MemoryIndex, FrozenLayer.Global)));
    }
}
