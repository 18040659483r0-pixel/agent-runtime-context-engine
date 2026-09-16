using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRuntime.Core.Frozen;

namespace AgentRuntime.Core.Tail;

/// <summary>
/// 尾部存储区的**落盘内容**：<c>{ sessionId, tail, source, turn, updatedAt }</c>。
/// <para>账本字段（source / turn / updatedAt）不进 prompt，只用于审计与复算。</para>
/// </summary>
public sealed record CurrentTailEntry
{
    /// <summary>会话键（存储文件名由此推出）。</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = CurrentTailStore.DefaultSessionKey;

    /// <summary>白板正文（逐行）。</summary>
    [JsonPropertyName("tail")]
    public IReadOnlyList<string> Tail { get; init; } = [];

    /// <summary>来源（见 <see cref="CurrentTailSources"/>）。</summary>
    [JsonPropertyName("source")]
    public string Source { get; init; } = CurrentTailSources.Empty;

    /// <summary>产生时的轮次（账本）。</summary>
    [JsonPropertyName("turn")]
    public int Turn { get; init; }

    /// <summary>记账时刻（账本；不参与任何指纹）。</summary>
    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>空空板（新会话 / 清除后）。</summary>
    public static CurrentTailEntry Empty(string sessionId) =>
        new() { SessionId = sessionId, Source = CurrentTailSources.Empty };

    /// <summary>由不可变状态落账。</summary>
    public static CurrentTailEntry FromState(string sessionId, CurrentTailState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new CurrentTailEntry
        {
            SessionId = sessionId,
            Tail = state.Lines.ToArray(),
            Source = state.Source,
            Turn = state.Turn,
            UpdatedAt = now,
        };
    }

    /// <summary>转成不可变状态（行 → 状态；标签账本现算）。</summary>
    public CurrentTailState ToState() => new()
    {
        Lines = CurrentTailService.Normalize(Tail),
        Tags = CurrentTailService.ExtractTags(Tail),
        Source = Source,
        Turn = Turn,
    };
}

/// <summary>
/// **尾部存储区（R4）—— 唯一真相源**。
/// <para>
/// ⚠️ <b>与 <c>focus.json</c> 的口径**正好相反**</b>（PITFALLS #24 判据：能否重建决定坏文件处置）：
/// </para>
/// <list type="bullet">
/// <item><c>focus.json</c> 是**可重建的加速缓存**（流才是真相源）⇒ 坏掉降级当「没有缓存」；</item>
/// <item>本存储**就是真相源**（当前状态不在流里、无法从流重算）⇒ **坏文件必须报错**，
/// 绝不做「当没有」的降级（那等于把主人的白板悄悄清空还装作没事）。</item>
/// </list>
/// <para>文件不存在 = 空默认（新会话，不是错误）；写入**原子**（临时文件 + 改名），
/// 于是任何时刻磁盘上要么是旧的完整白板、要么是新的完整白板。</para>
/// <para>位置：<c>&lt;目录&gt;/&lt;sessionId&gt;.json</c>（默认目录 <c>~/.agentruntime/tail</c>，由组合根解析）。</para>
/// </summary>
public sealed class CurrentTailStore
{
    private static readonly JsonSerializerOptions Options = FrozenJson.Options;

    /// <summary>未指定 sessionId 时使用的键（单会话日常使用）。</summary>
    public const string DefaultSessionKey = "default";

    public CurrentTailStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = System.IO.Path.GetFullPath(RuntimeDirectory(directory));
    }

    /// <summary>存储目录（绝对路径；不含密钥）。</summary>
    public string Directory { get; }

    /// <summary>会话键归一化（空 ⇒ <c>default</c>）；**拒绝**含路径分隔符的键（防目录穿越）。</summary>
    public static string KeyOf(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return DefaultSessionKey;
        }

        var key = sessionId.Trim();
        if (key.Contains('/') || key.Contains('\\') || key.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"非法 sessionId：\"{sessionId}\"（不得含路径分隔符或 ..）。");
        }

        return key;
    }

    /// <summary>某个会话的白板文件路径。</summary>
    public string PathFor(string? sessionId) =>
        System.IO.Path.Combine(Directory, KeyOf(sessionId) + ".json");

    /// <summary>文件是否已存在（只读判断）。</summary>
    public bool Exists(string? sessionId) => File.Exists(PathFor(sessionId));

    /// <summary>
    /// 读取白板；**文件不存在 ⇒ null**（新会话）；**坏文件 ⇒ 抛 <see cref="InvalidDataException"/>**（不降级）。
    /// </summary>
    public CurrentTailEntry? TryLoad(string? sessionId)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        CurrentTailEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize<CurrentTailEntry>(File.ReadAllText(path, Encoding.UTF8), Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"尾部存储文件不是合法 JSON：{path}（{ex.Message}）—— 尾部是**唯一真相源**（不可从流重建），" +
                "坏文件必须报错，不做「当没有」的降级。", ex);
        }

        return entry ?? throw new InvalidDataException($"尾部存储内容为空：{path}（唯一真相源，拒绝当作空白板）。");
    }

    /// <summary>读取白板；文件缺失 ⇒ 空默认（不是错误）。</summary>
    public CurrentTailEntry Load(string? sessionId) => TryLoad(sessionId) ?? CurrentTailEntry.Empty(KeyOf(sessionId));

    /// <summary>原子写入白板（临时文件 + 改名；失败不留半成品）。</summary>
    public void Save(CurrentTailEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var path = PathFor(entry.SessionId);
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            System.IO.Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(entry, Options).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        var temporary = $"{path}.tmp-{Guid.NewGuid():N}";

        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // 清理失败不掩盖原始异常（残留临时文件比半截 JSON 安全）。
                }
            }
        }
    }

    /// <summary>路径展开（支持 <c>~/</c>），与 CLI 其余路径同规。</summary>
    private static string RuntimeDirectory(string directory) =>
        directory.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), directory[2..])
            : directory;
}
