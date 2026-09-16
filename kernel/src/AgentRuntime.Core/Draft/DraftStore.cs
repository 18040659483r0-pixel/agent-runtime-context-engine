using System.Text;
using System.Text.Json;
using AgentRuntime.Core.Frozen;

namespace AgentRuntime.Core.Draft;

/// <summary>
/// **草稿存储区（R5）—— 唯一真相源**。
/// <para>
/// ⚠️ <b>与 <c>focus.json</c> 的口径**正好相反**</b>（PITFALLS #24 判据：能否重建决定坏文件处置）：
/// </para>
/// <list type="bullet">
/// <item><c>focus.json</c> 是**可重建的加速缓存**（流才是真相源）⇒ 坏掉降级当「没有缓存」；</item>
/// <item>本存储**就是真相源**（草稿不在流里、无法从流重算）⇒ **坏文件必须报错**，
/// 绝不做「当没有」的降级（那等于把主人的草稿悄悄清空还装作没事）。</item>
/// </list>
/// <para>
/// 写入**只有整块原子覆盖一条路**（临时文件 + 改名）：没有 <c>Remove(i)</c> / <c>Move(i,j)</c> / <c>Patch</c> ——
/// 局部编辑会引入不可复算的增量操作，与「账本可逐字节 diff」的既有纪律冲突；
/// 语义上的「删 / 改 / 重排」由**整块新内容**表达（见 <c>DynamicDraftModule.ObserveAsync</c>）。
/// </para>
/// <para>位置：<c>&lt;目录&gt;/&lt;sessionId&gt;.json</c>（默认目录 <c>~/.agentruntime/draft</c>，由组合根解析）。</para>
/// </summary>
public sealed class DraftStore
{
    private static readonly JsonSerializerOptions Options = FrozenJson.Options;

    /// <summary>未指定 sessionId 时使用的键（单会话日常使用）。</summary>
    public const string DefaultSessionKey = "default";

    public DraftStore(string directory)
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

    /// <summary>某个会话的草稿文件路径。</summary>
    public string PathFor(string? sessionId) =>
        System.IO.Path.Combine(Directory, KeyOf(sessionId) + ".json");

    /// <summary>文件是否已存在（只读判断）。</summary>
    public bool Exists(string? sessionId) => File.Exists(PathFor(sessionId));

    /// <summary>
    /// 读取草稿；**文件不存在 ⇒ null**（新会话）；**坏文件 ⇒ 抛 <see cref="InvalidDataException"/>**（不降级）。
    /// </summary>
    public DraftEntry? TryLoad(string? sessionId)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path))
        {
            return null;
        }

        DraftEntry? entry;
        try
        {
            entry = JsonSerializer.Deserialize<DraftEntry>(File.ReadAllText(path, Encoding.UTF8), Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"草稿存储文件不是合法 JSON：{path}（{ex.Message}）—— 草稿是**唯一真相源**（不可从流重建），" +
                "坏文件必须报错，不做「当没有」的降级。", ex);
        }

        return entry ?? throw new InvalidDataException($"草稿存储内容为空：{path}（唯一真相源，拒绝当作空草稿）。");
    }

    /// <summary>读取草稿；文件缺失 ⇒ 空默认（不是错误）。</summary>
    public DraftEntry Load(string? sessionId) => TryLoad(sessionId) ?? DraftEntry.Empty(KeyOf(sessionId));

    /// <summary>原子写入草稿（整块覆盖；临时文件 + 改名；失败不留半成品）。</summary>
    public void Save(DraftEntry entry)
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
