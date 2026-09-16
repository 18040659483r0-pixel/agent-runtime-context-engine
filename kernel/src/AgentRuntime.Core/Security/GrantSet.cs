namespace AgentRuntime.Core.Security;

/// <summary>
/// **授权集合**（两类，来源不同、性质不同）：
/// <list type="number">
/// <item><b>会话 Grant</b>（主人 2026-09-16 19:14）—— 人**当场点头**之后记下来的，<b>易失</b>：
/// 只在内存、进程结束即失效；恢复 / 重启 / <c>--fork</c> ⇒ 重新问人。</item>
/// <item><b>预授权</b>（<see cref="PreAuthorization"/>）—— 人**事先带外**签的字，<b>有范围 + 有到期</b>，
/// 用来支撑 CI / 夜间 / 无人长任务（<c>DESIGN-SECURITY-GATEWAY.md</c> §十.1）。</item>
/// </list>
/// <para>
/// 两者都**不进 prompt**、**不写审批账本**（Grant 复用不是「人点头」这个事实；那次点头/签字本身已有记录）。
/// </para>
/// <para>
/// <b>粒度故意窄</b>：一条 = <c>(能力, 规范化后的确切目标)</c>；不做通配、不做目录级放行 ——
/// 本项目目录里含可执行内容（<c>.git/hooks</c> / <c>*.csproj</c>），目录级放行会把洞 W2（写→执行链）直接打开。
/// </para>
/// <para>第三本账是<b>污染集</b>：本会话写过的文件/目录 —— 用来判「这个可执行文件是不是我自己刚写出来的」。</para>
/// </summary>
public sealed class GrantSet
{
    private readonly List<(Capability Capability, string Target, PreAuthorization? PreAuth)> _grants = [];
    private readonly HashSet<string> _tainted = new(StringComparer.Ordinal);

    /// <summary>有效条目数（会话 Grant + 预授权）。</summary>
    public int Count => _grants.Count;

    /// <summary>预授权条数。</summary>
    public int PreAuthorizedCount => _grants.Count(g => g.PreAuth is not null);

    /// <summary>被本会话写过的路径数（污染集大小）。</summary>
    public int TaintedCount => _tainted.Count;

    /// <summary>这条 (能力, 目标) 有没有仍然有效的授权（会话 Grant 或未过期的预授权）。</summary>
    public bool Covers(Capability capability, string target, DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(target)
        && _grants.Any(g => g.Capability == capability
                            && string.Equals(g.Target, target, StringComparison.Ordinal)
                            && (g.PreAuth is null || !g.PreAuth.IsExpired(now)));

    /// <summary>命中的**预授权**（没有 ⇒ null）。用来在放行理由里点名是哪一张签字。</summary>
    public PreAuthorization? PreAuthorized(Capability capability, string target, DateTimeOffset now) =>
        _grants.FirstOrDefault(g => g.PreAuth is not null
                                    && g.Capability == capability
                                    && string.Equals(g.Target, target, StringComparison.Ordinal)
                                    && !g.PreAuth.IsExpired(now)).PreAuth;

    /// <summary>记一条**会话 Grant**（只能由「人当场点头」那条路径调用）。</summary>
    public void Add(Capability capability, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        _grants.Add((capability, target, null));
    }

    /// <summary>装一条**预授权**（只能来自带外开出来的那本账）。</summary>
    public void AddPreAuthorized(PreAuthorization preAuthorization)
    {
        ArgumentNullException.ThrowIfNull(preAuthorization);
        _grants.Add((preAuthorization.Capability, preAuthorization.Target, preAuthorization));
    }

    /// <summary>把存储里**仍然有效**的预授权装进来（载入一次即可；过期由 <see cref="Covers"/> 按时刻判）。</summary>
    public int LoadPreAuthorized(PreAuthorizationStore store, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(store);

        var added = 0;
        foreach (var entry in store.Active(now))
        {
            AddPreAuthorized(entry);
            added++;
        }

        return added;
    }

    /// <summary>全部授权（诊断 / 面板用；顺序不保证）。</summary>
    public IReadOnlyList<string> Render(DateTimeOffset now) =>
        _grants
            .Select(g => g.PreAuth is null
                ? $"（会话）{Capabilities.Name(g.Capability)} @ {g.Target}"
                : $"（预授权 {g.PreAuth.Id}，{(g.PreAuth.IsExpired(now) ? "已过期" : $"至 {g.PreAuth.ExpiresAt:HH:mm}")}）{Capabilities.Name(g.Capability)} @ {g.Target}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

    /// <summary>撤销全部**会话** Grant（会话结束 / 用户改主意）。预授权不在这里撤（由存储的 <c>Revoke</c> 管）。</summary>
    public void Clear() => _grants.RemoveAll(g => g.PreAuth is null);

    /// <summary>记「本会话写出了这个路径」（文件本身 + 它所在目录都算污染）。</summary>
    public void NoteWritten(string? normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return;
        }

        _tainted.Add(normalizedPath);

        var directory = Path.GetDirectoryName(normalizedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _tainted.Add(directory);
        }
    }

    /// <summary>这个路径是不是本会话写出来的（⇒ 执行它必须重新点头，永不自动放行）。</summary>
    public bool IsTainted(string? normalizedPath) =>
        !string.IsNullOrWhiteSpace(normalizedPath) && _tainted.Contains(normalizedPath);

    /// <summary>污染集快照（诊断 / 测试）。</summary>
    public IReadOnlyList<string> Tainted() => _tainted.OrderBy(s => s, StringComparer.Ordinal).ToArray();
}
