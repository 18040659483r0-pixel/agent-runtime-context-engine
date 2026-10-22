using System.Text;
using AgentRuntime.Core;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// **记忆索引（活版）** —— 把 <c>&lt;workspace&gt;/MEMORYINDEX.md</c> 当成**每轮现读的索引**注入，
/// 而不是冻结进 R1 前缀（主人 2026-10-22：「index 应该是完整版 memory 的索引条目……是活版，不直接进入 R1」）。
/// <para>为什么这样分工：索引每条收尾都会长一行 —— 冻进 R1 就等于**每次收尾都把整段前缀换掉**，
/// 缓存从第一个字节起归零；而它本身只是**指针表**，放在活版里读一次即可，代价远小于重算整段前缀。</para>
/// <para>三条硬约束：</para>
/// <list type="number">
/// <item>**不实现** <see cref="Core.Frozen.IFrozenZoneModule"/> ⇒ 不进 R1（与 tail/draft/focus 同一族）。</item>
/// <item>**文件缺失 / 空** ⇒ 一条消息都不产生（零注入，行为回到「没装这个模块」）。</item>
/// <item>**逐字节照读**：不重排、不摘要、不改写 —— 索引的维护是收尾的事，注入只负责搬运。</item>
/// </list>
/// <para>与冻结版 <see cref="MemoryIndexModule"/> 的关系：worker 仍用自己的冻结索引（<c>&lt;workerHome&gt;/memory/index.md</c>）；
/// master 用本类。两者**同名不同源**，装配处按角色选择（这条分工只在一处声明）。</para>
/// </summary>
public sealed class MemoryIndexLiveModule : RuntimeModuleBase
{
    /// <summary>模块名（与配置里的 <c>memory-index</c> 保持同一名字：换实现不换开关）。</summary>
    public const string ModuleName = "memory-index";

    /// <summary>文件相对活版根的路径（唯一声明处）。</summary>
    public const string RelativePath = "MEMORYINDEX.md";

    private readonly string _path;

    /// <param name="workspace">活版根（绝对路径）；空 ⇒ 本模块退化成空实现（一条消息都不产生）。</param>
    public MemoryIndexLiveModule(string? workspace)
    {
        _path = string.IsNullOrWhiteSpace(workspace)
            ? string.Empty
            : System.IO.Path.Combine(workspace, RelativePath);
    }

    public override string Name => ModuleName;

    /// <summary>索引文件绝对路径（只读诊断用；空 = 没配活版根）。</summary>
    public string Path => _path;

    /// <summary>**零缓存驻留**：贡献一段 system 消息（内容 = 文件原文）；缺文件 ⇒ 零注入。</summary>
    public override ValueTask ContributeAsync(
        RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (_path.Length == 0 || !File.Exists(_path))
        {
            return ValueTask.CompletedTask;
        }

        var text = File.ReadAllText(_path, Encoding.UTF8).Trim();
        if (text.Length == 0)
        {
            return ValueTask.CompletedTask;
        }

        messages.Add(ChatMessage.System(
            "[记忆索引] 以下是活版的记忆索引（不是全文）：每条指向 memory/ 下的一天；需要细节时按指针去读那一天，别凭印象回忆。\n"
            + text));
        return ValueTask.CompletedTask;
    }
}
