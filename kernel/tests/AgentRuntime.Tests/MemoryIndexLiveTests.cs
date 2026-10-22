using AgentRuntime.Core;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Models;
using AgentRuntime.Modules;
using Xunit;

namespace AgentRuntime.Tests;

/// <summary>
/// **记忆索引是活版**（2026-10-22 主人令）：索引 = <c>memory/</c> 的**指针表**，每轮现读，
/// **不进 R1 冻结前缀**（否则每次收尾多一行 ⇒ 整段前缀换掉、缓存全归零）。
/// <para>三条：① 照读文件原文并成一段 system 消息；② 缺文/空文 ⇒ 零注入；③ **不是**冻结区模块（结构上就进不了 R1）。</para>
/// </summary>
public sealed class MemoryIndexLiveTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wb-memidx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<List<ChatMessage>> ContributeAsync(MemoryIndexLiveModule m)
    {
        var messages = new List<ChatMessage>();
        await m.ContributeAsync(new RuntimeContext("s", 0), messages, default);
        return messages;
    }

    [Fact]
    public async Task 照读索引文件_成一段system消息()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, MemoryIndexLiveModule.RelativePath),
                "# 索引\n| 10-22 | 今天做了什么 | `2026-10-22.md` |");
            var messages = await ContributeAsync(new MemoryIndexLiveModule(dir));

            var single = Assert.Single(messages);
            Assert.Equal("system", single.Role);
            Assert.Contains("记忆索引", single.Content, StringComparison.Ordinal);
            Assert.Contains("2026-10-22.md", single.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task 缺文件或空文件_零注入()
    {
        var dir = TempDir();
        try
        {
            Assert.Empty(await ContributeAsync(new MemoryIndexLiveModule(dir)));

            File.WriteAllText(Path.Combine(dir, MemoryIndexLiveModule.RelativePath), "   \n");
            Assert.Empty(await ContributeAsync(new MemoryIndexLiveModule(dir)));

            Assert.Empty(await ContributeAsync(new MemoryIndexLiveModule(null)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 不是冻结区模块_因此进不了R1()
    {
        var m = new MemoryIndexLiveModule(null);
        Assert.IsNotAssignableFrom<IFrozenZoneModule>(m);
        Assert.Equal("memory-index", m.Name);
    }
}
