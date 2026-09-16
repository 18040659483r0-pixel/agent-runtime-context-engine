using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentRuntime.Core.Tooling;

namespace AgentRuntime.Core.Security;

/// <summary>一条**预授权**（人带外事先签的字）。</summary>
/// <param name="Id">编号（<c>PA-0001</c>；账本与报错话术引用它）。</param>
/// <param name="Capability">被授权的能力。</param>
/// <param name="Target">被授权的**确切目标**（规范化路径 / 命令原文）。</param>
/// <param name="IssuedAt">签发时刻。</param>
/// <param name="ExpiresAt">到期时刻（**必须有**：预授权无期限 = 永久后门）。</param>
/// <param name="Note">人写的理由（审计用）。</param>
/// <param name="IssuedBy">签发者（只认人）。</param>
public sealed record PreAuthorization(
    string Id,
    Capability Capability,
    string Target,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string Note,
    string IssuedBy = "human@tty")
{
    /// <summary>是不是已经过期。</summary>
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>面板/列表一行。</summary>
    public string Render(DateTimeOffset now) =>
        $"{Id} {Capabilities.Name(Capability)} @ {Target} · {(IsExpired(now) ? "已过期" : $"至 {ExpiresAt:yyyy-MM-dd HH:mm}")} · {Note}";
}

/// <summary>
/// **预授权存储**（<c>docs/DESIGN-SECURITY-GATEWAY.md</c> §十.1「预授权 Grant」）。
/// <para>
/// <b>它解决什么</b>：CI / 夜间 / 长任务**没有人在场**。正确做法不是「跳过审批」，而是
/// 人**事先带外**签一份**有范围、有期限**的授权 —— 运行时按它放行，
/// <b>放的仍然是「人点过头的那件事」</b>。
/// </para>
/// <para><b>落点</b>：默认 <c>~/.agentruntime/preauth/grants.json</c> —— 这个目录在
/// <see cref="SecurityPolicy"/> 的**受保护根**里 ⇒ Agent 的工具面**读不到也改不了**它。</para>
/// <para><b>它是存储区，不是缓存</b>（`METHODOLOGY` §十·23 的口径）：文件坏了**必须报错**，
/// 绝不"当作没有授权"降级 —— 那会把一次显式的放行悄悄变成拒绝，或反过来。
/// 另带一个**校验和**：手改/半截写 ⇒ 载入即报错。</para>
/// <para><b>有效期是硬要求</b>：没有 <see cref="PreAuthorization.ExpiresAt"/> 的授权 = 永久后门，构造期就拒绝。</para>
/// </summary>
public sealed class PreAuthorizationStore
{
    /// <summary>预授权**最长**有效期（人签的时候最多给这么久；要更久就再签一次）。</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(8);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string? _path;
    private readonly List<PreAuthorization> _entries = [];

    /// <param name="path">落点（JSON）；null = 只存内存（测试用）。</param>
    public PreAuthorizationStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFullPath(path);
        if (_path is not null)
        {
            Load();
        }
    }

    /// <summary>出厂落点（`~/.agentruntime/preauth/grants.json`）。
    /// <para>⚠️ **主目录判不出 ⇒ 报错，不回退到当前目录**：本机实测（2026-09-16）
    /// <c>SpecialFolder.UserProfile</c> 在 <c>HOME</c> 指向不存在的位置时返回空串，
    /// <c>Path.Combine("", …)</c> 就会**变成相对路径**——测试脚本因此把预授权写进了**仓库目录**。
    /// 存储区宁可报错，也不静默写到一个“碰巧是 cwd”的地方。</para>
    /// </summary>
    public static string DefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            throw new InvalidDataException(
                "判不出用户主目录（~）⇒ 拒绝：预授权账本不能落到“碰巧是当前目录”的地方；请显式指定 --preauth-store。");
        }

        return System.IO.Path.Combine(home, ".agentruntime", "preauth", "grants.json");
    }

    /// <summary>落点。</summary>
    public string? StorePath => _path;

    /// <summary>全部条目（含已过期；列表时再过滤）。</summary>
    public IReadOnlyList<PreAuthorization> Entries => _entries;

    /// <summary>签一份（<b>只能由带外的人那条路径调用</b>；有效期上限见 <see cref="MaxLifetime"/>）。</summary>
    public PreAuthorization Issue(Capability capability, string target, TimeSpan lifetime, string note, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            throw new ToolUsageException("预授权的目标不能为空（预授权必须是**确切目标**，不做通配）。");
        }

        if (lifetime <= TimeSpan.Zero || lifetime > MaxLifetime)
        {
            throw new ToolUsageException($"预授权有效期必须在 0 ~ {MaxLifetime.TotalHours:0} 小时之间（无期限授权 = 永久后门）。");
        }

        var entry = new PreAuthorization(
            $"PA-{_entries.Count + 1:0000}", capability, target, now, now + lifetime, note);

        _entries.Add(entry);
        Save();
        return entry;
    }

    /// <summary>按编号撤销（返回是否撤到了）。</summary>
    public bool Revoke(string id, DateTimeOffset now)
    {
        var removed = _entries.RemoveAll(e => string.Equals(e.Id, id.Trim(), StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save();
        }

        return removed;
    }

    /// <summary>还没过期的条目。</summary>
    public IReadOnlyList<PreAuthorization> Active(DateTimeOffset now) => _entries.Where(e => !e.IsExpired(now)).ToArray();

    /// <summary>这条 (能力, 目标) 有没有**未过期**的预授权。</summary>
    public PreAuthorization? Find(Capability capability, string target, DateTimeOffset now) =>
        _entries.FirstOrDefault(e => e.Capability == capability
                                    && string.Equals(e.Target, target, StringComparison.Ordinal)
                                    && !e.IsExpired(now));

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;   // 没有文件 = 没有预授权（这是**正常**状态，不是坏文件）
        }

        PreAuthFile? file;
        try
        {
            file = JsonSerializer.Deserialize<PreAuthFile>(File.ReadAllText(_path), ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"预授权文件坏了（{_path}）：{ex.Message} —— 存储区不做降级，请人工处理。", ex);
        }

        if (file is null)
        {
            throw new InvalidDataException($"预授权文件无法解析：{_path}");
        }

        var canonical = Canonical(file.Entries);
        if (!string.Equals(canonical.Checksum, file.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"预授权文件**校验和不符**（{_path}）：期望 {file.Checksum}、实算 {canonical.Checksum}。" +
                "手改 / 半截写 / 被篡改都可能；**存储区不做静默降级**。");
        }

        _entries.Clear();
        _entries.AddRange(canonical.Entries);
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var canonical = Canonical(_entries);
        var payload = new PreAuthFile(1, canonical.Entries, canonical.Checksum);
        File.WriteAllText(_path, JsonSerializer.Serialize(payload, WriteOptions), new UTF8Encoding(false));
    }

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>逐字节确定的规范化（排序 + 定长时间格式）⇒ 校验和可复算。</summary>
    private static (List<PreAuthorization> Entries, string Checksum) Canonical(IEnumerable<PreAuthorization> entries)
    {
        var list = entries
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => e with
            {
                IssuedAt = e.IssuedAt.ToUniversalTime(),
                ExpiresAt = e.ExpiresAt.ToUniversalTime(),
            })
            .ToList();

        var sb = new StringBuilder();
        foreach (var e in list)
        {
            sb.Append(e.Id).Append('\u0001')
              .Append(Capabilities.Name(e.Capability)).Append('\u0001')
              .Append(e.Target).Append('\u0001')
              .Append(e.IssuedAt.ToString("O")).Append('\u0001')
              .Append(e.ExpiresAt.ToString("O")).Append('\u0001')
              .Append(e.Note).Append('\u0001')
              .Append(e.IssuedBy).Append('\n');
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        return (list, hash);
    }

    private sealed record PreAuthFile(int Version, List<PreAuthorization> Entries, string Checksum);
}
