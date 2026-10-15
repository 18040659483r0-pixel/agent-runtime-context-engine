using System.Runtime.Serialization;
using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **worker 自己的收尾水位线**（③，2026-09-28）—— **独立一条**，与 master 的 <c>CloseoutWatermark</c> 互不共用。
/// <para>
/// 它回答一个问题：**worker 的 memory 已收敛到哪**。判据是可机器比对的**事实**
/// （最后一次结算的派发 id + 累计派发数 + 时刻），**不是描述性文字**（§十·11 / §十·45）。
/// </para>
/// <para>
/// 谁推进它：**worker 不自收尾**，所以由执行器在**每次结算后**推进一格（一次派发 = 一格）——
/// master 收尾时读 worker 的 memory + 这条水位线，就知道「它记到哪了」。
/// </para>
/// <para>
/// <b>坏文件必须报错，不降级</b>（§十·23）：它是唯一真相源，静默重置比报错贵得多
/// （重置 = 「已收敛到哪」这个事实凭空归零，而没有任何人会喊）。
/// </para>
/// <para>
/// 落盘 = <see cref="DataContractXml"/>（0 依赖 XML；**类型单一来源**：本类型既是领域对象也是落盘对象，
/// 字段顺序由 <see cref="DataMemberAttribute.Order"/> 显式固定 ⇒ 字节可复现）。
/// </para>
/// </summary>
[DataContract(Namespace = "")]
public sealed class WorkerWatermark
{
    /// <summary>provider 名（memory 按它分家）。</summary>
    [DataMember(Order = 0)]
    public string Provider { get; init; } = string.Empty;

    /// <summary>最后一次派发的 id。</summary>
    [DataMember(Order = 1)]
    public string LastWorkerId { get; init; } = string.Empty;

    /// <summary>最后一次是否交了 <c>[WORKER]</c> 块（没交 ⇒ 那一格不算收敛）。</summary>
    [DataMember(Order = 2)]
    public bool LastSettled { get; init; }

    /// <summary>累计派发数（只增）。</summary>
    [DataMember(Order = 3)]
    public int Dispatches { get; init; }

    /// <summary>memory 的家（稳定家；按 provider 标识分家 —— providerName，缺省回落 model 名）。</summary>
    [DataMember(Order = 4)]
    public string MemoryPath { get; init; } = string.Empty;

    /// <summary>最后推进时刻。</summary>
    [DataMember(Order = 5)]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>文件名（唯一声明处）。</summary>
    public const string FileName = "watermark.xml";

    /// <summary>
    /// 默认落点：<c>&lt;root&gt;/&lt;标识&gt;/watermark.xml</c>。
    /// <para><b>标识 = provider 标识</b>：<c>worker.providerName</c>；**缺省回落 model 名**（<c>Slug(model)</c>）——
    /// 2026-09-29 真机实测：没设 <c>providerName</c> 时目录名就是 model 名（如 <c>deepseek-ai-DeepSeek-V4-Flash-0731</c>），
    /// **不是**平台名。<see cref="Provider"/> 字段就是它（保留此名以免动账本与配置；读作「标识」）。</para>
    /// </summary>
    public static string PathFor(string root, string provider) => Path.Combine(root, provider, FileName);

    /// <summary>默认 memory 家：<c>&lt;root&gt;/&lt;标识&gt;/memory</c>（**按 provider 标识分家（providerName；缺省回落 model 名），不按派发实例分家**）。</summary>
    public static string MemoryFor(string root, string provider) => Path.Combine(root, provider, "memory");

    /// <summary>worker 持续事件流的文件名。</summary>
    public const string StreamFileName = "stream.xml";

    /// <summary>
    /// **worker 的持续事件流落点**（主人 2026-09-29）：<c>&lt;root&gt;/&lt;标识&gt;/stream.xml</c> ——
    /// 与 <see cref="MemoryFor"/> **同一条纪律**（按 provider 分家，**不按派发实例分家**）。
    /// <para>旧写法挂在 <c>&lt;root&gt;/&lt;provider&gt;/&lt;workerId&gt;/stream.xml</c> 下 ⇒ 上一条的会话痕迹对下一条不可见，
    /// 「派生自同一台 engine 的第二个会话」只是句空话。</para>
    /// </summary>
    public static string StreamFor(string root, string provider) => Path.Combine(root, provider, StreamFileName);

    /// <summary>
    /// **归档 worker 的持续流**（reset 时与 master 一并，主人 2026-09-29）—— 只移动、绝不删除；
    /// 返回归档到哪（没有流 ⇒ null）。判据是可机器比对的事实（文件真的没了 / 真的在 archive 下）。
    /// </summary>
    public static string? ArchiveStream(string root, string provider, DateTimeOffset now)
    {
        var path = StreamFor(root, provider);
        if (!File.Exists(path))
        {
            return null;
        }

        var archiveDir = Path.Combine(root, provider, "archive");
        Directory.CreateDirectory(archiveDir);
        var stamp = now.ToString("yyyyMMdd-HHmmss");
        var dst = Path.Combine(archiveDir, $"stream-{stamp}.xml.keep");
        var n = 2;
        while (File.Exists(dst))
        {
            dst = Path.Combine(archiveDir, $"stream-{stamp}-{n}.xml.keep");
            n++;
        }

        File.Move(path, dst);
        return dst;
    }

    /// <summary>推进一格（读旧值 → 计数 +1 → 写回）。坏文件 ⇒ 抛（不降级）。</summary>
    public static WorkerWatermark Advance(
        string path, string provider, string workerId, string memoryPath, bool settled, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var previous = Load(path);
        var next = new WorkerWatermark
        {
            Provider = provider,
            LastWorkerId = workerId,
            LastSettled = settled,
            Dispatches = (previous?.Dispatches ?? 0) + 1,
            MemoryPath = memoryPath,
            UpdatedAt = now,
        };

        Save(path, next);
        return next;
    }

    /// <summary>
    /// **worker 集合**（主人 2026-09-29 18:50）：<c>&lt;root&gt;</c> 下的**家名**（= provider 标识目录）。
    /// <para>判据 = 「像一个家」（<c>watermark.xml</c> / <c>stream.xml</c> / <c>memory/</c> 至少有一样），
    /// **不是**「目录存在」—— 根下可能有 archive / 临时目录，那不是 worker。</para>
    /// <para>顺序 = **名字升序**（确定性、可复算；单/多 worker 同一套码）。根不存在 ⇒ 空集合（不抛）。</para>
    /// </summary>
    public static IReadOnlyList<string> FamiliesOf(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        var names = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            if (name.Length == 0
                || !(File.Exists(Path.Combine(dir, FileName))
                     || File.Exists(Path.Combine(dir, StreamFileName))
                     || Directory.Exists(Path.Combine(dir, "memory"))))
            {
                continue;
            }

            names.Add(name);
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    /// **遍历集合逐个归档**（reset 时）：返回 (归档到的落点, 失败)。**一个失败不静默跳过** ——
    /// 失败项写清「哪个家 + 什么错」，由调用方进报告与流。
    /// <para>单 worker = 集合里只有一家 ⇒ 行为与旧路径逐字相同（旧写法只归档「当前那一个」）。</para>
    /// </summary>
    public static (IReadOnlyList<string> Archived, IReadOnlyList<string> Failed) ArchiveAll(
        string root, DateTimeOffset now)
    {
        var archived = new List<string>();
        var failed = new List<string>();
        foreach (var family in FamiliesOf(root))
        {
            try
            {
                if (ArchiveStream(root, family, now) is { } dest)
                {
                    archived.Add(dest);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{family}：{ex.GetType().Name} {ex.Message}");
            }
        }

        return (archived, failed);
    }

    /// <summary>
    /// **会话起点逐个读集合**（start 侧，主人 2026-09-29 18:50）：每家一行读数（家名 · 累计派发 · 末次是否结算）。
    /// <para>与 <see cref="ArchiveAll"/> 的分工照抄「reset 结束 / start 开始」：reset **归档**每一条流，
    /// start **宣告**每一家的状态 —— 两边都**遍历同一个集合**（<see cref="FamiliesOf"/>），不是只动当前那一个。</para>
    /// <para>坏 watermark ⇒ 不抛（start 不该因为一个坏账本起不来），如实标 <c>（账本读不出）</c>。</para>
    /// </summary>
    public static IReadOnlyList<string> DescribeFamilies(string root)
    {
        var lines = new List<string>();
        foreach (var family in FamiliesOf(root))
        {
            var path = PathFor(root, family);
            WorkerWatermark? wm;
            try
            {
                wm = Load(path);
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or System.Xml.XmlException)
            {
                lines.Add($"{family}：（账本读不出）");
                continue;
            }

            lines.Add(wm is null
                ? $"{family}：还没有派发记录"
                : $"{family}：累计派发 {wm.Dispatches} 次 · 末次{(wm.LastSettled ? "已结算" : "**未结算**")}");
        }

        return lines;
    }

    /// <summary>读（不存在 ⇒ null；**存在但坏 ⇒ 抛** —— 唯一真相源不许静默重置）。</summary>
    public static WorkerWatermark? Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        var watermark = DataContractXml.ReadFile<WorkerWatermark>(path);
        return new WorkerWatermark
        {
            Provider = watermark.Provider ?? string.Empty,
            LastWorkerId = watermark.LastWorkerId ?? string.Empty,
            LastSettled = watermark.LastSettled,
            Dispatches = watermark.Dispatches,
            MemoryPath = watermark.MemoryPath ?? string.Empty,
            UpdatedAt = watermark.UpdatedAt,
        };
    }

    /// <summary>写（UTF-8 无 BOM、LF 结尾 —— 与冻结层同一纪律）。</summary>
    public static void Save(string path, WorkerWatermark watermark)
    {
        ArgumentNullException.ThrowIfNull(watermark);
        DataContractXml.WriteFile(path, watermark);
    }
}
