using System.Runtime.Serialization;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// **会话指针**（`session.xml`）—— 「**当前这一节是哪个会话**」的跨进程记录。
/// <para>
/// 为什么非有它不可：`/reset` → `/start` 换的是**新的事件流文件**，而配置文件里那条 `stream.path` 是**启动基线**。
/// 没有指针 ⇒ 进程一重启就回到旧卷（今天靠人手改配置顶上）。指针把这件事做成机制：
/// </para>
/// <code>
/// 启动 → 读指针 → running？接上它那条流
///                 └ closed？**自动 start**（开新流 + 从末态装载 [TAIL]/[DRAFT]）
/// 无指针 → 按配置的 stream.path（首次使用 / 手工指定 --stream 时）
/// </code>
/// <para><b>显式优先</b>：命令行 <c>--stream</c> 永远压过指针（人要回某一卷时不受指针干扰）。</para>
/// </summary>
[DataContract(Name = "Session", Namespace = "")]
public sealed record SessionPointer(
    [property: DataMember(Order = 0)] string? StreamPath,
    [property: DataMember(Order = 1)] bool Closed,
    [property: DataMember(Order = 2)] DateTimeOffset At,
    [property: DataMember(Order = 3)] int SessionNumber,
    [property: DataMember(Order = 4)] string? FromHandoverAt,
    // 2026-10-24：收尾是否**真的完成**（清单全绿 / 回执齐）。旧文件没有这一项 ⇒ 默认 true（老行为不变）。
    // false + Closed=true 的含义：reset 过了，但收尾没做成 ⇒ 下次进入应**接回原流**把收尾做完，而不是开新流。
    [property: DataMember(Order = 5)] bool? CloseoutComplete = null)
{
    /// <summary>一行短述（启动横幅 / <c>/session</c> 用）。</summary>
    public string Describe() =>
        Closed
            ? CloseoutComplete != false
                ? $"已收尾、待 start（@ {At:yyyy-MM-dd HH:mm:ss}）⇒ 启动会自动 start 新会话"
                : $"收尾**未完成**、待续（@ {At:yyyy-MM-dd HH:mm:ss}）⇒ 启动会接回原流"
            : $"第 {SessionNumber} 节 · 事件流 {StreamPath ?? "（未落盘）"}（@ {At:yyyy-MM-dd HH:mm:ss}）";
}

/// <summary>会话指针的读写（原子写；坏文件**报错不降级**）。落点与末态快照同目录。</summary>
public sealed class SessionPointerStore
{
    /// <summary>文件名（放在末态快照所在目录里 —— 生命周期状态住一起，少一个配置键）。</summary>
    public const string FileName = "session.xml";

    public SessionPointerStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    /// <summary>指针文件路径（由末态快照路径推出同目录下的 <c>session.xml</c>）。</summary>
    public static string PathFor(string handoverPath) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(handoverPath) ?? string.Empty, FileName);

    public string Path { get; }

    /// <summary>读（不存在 ⇒ null；坏文件 ⇒ 抛错）。</summary>
    public SessionPointer? TryLoad()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        return DataContractXml.ReadFile<SessionPointer>(Path);
    }

    /// <summary>原子写。</summary>
    public void Save(SessionPointer pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        DataContractXml.WriteFileAtomic(Path, pointer);
    }
}
