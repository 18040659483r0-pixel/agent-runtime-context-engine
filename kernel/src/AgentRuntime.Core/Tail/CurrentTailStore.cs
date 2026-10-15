using AgentRuntime.Core.Persistence;

namespace AgentRuntime.Core.Tail;

public sealed class CurrentTailStore
{
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
        System.IO.Path.Combine(Directory, KeyOf(sessionId) + ".xml");

    /// <summary>文件是否已存在（只读判断）。</summary>
    public bool Exists(string? sessionId) => File.Exists(PathFor(sessionId));

    /// <summary>
    /// **跨会话承接**（主人 2026-10-14 20:40 令）：白板 R4 / 草稿 R5 是**工作台面**，不是「本次会话的临时物」。
    /// <para>本会话还没有自己那份（新 sessionId ⇒ 新文件名）时，就从**同目录里最近改动的**那一份 **copy** 过来
    /// （旧件**不动** —— 旧会话自己那份留着，可回溯）。</para>
    /// <para><b>为什么必须有</b>（真机现场）：主人在上一会话交代「记录下个 session 要继续做的活」，
    /// 新会话却拿到**空白**白板/草稿 ⇒ 远端 AI 只能自己去翻旧会话遗留 ⇒ 白烧巨量时间。
    /// 协议第 7/9 条本来就写着「reset 定格 · start 从末态装载」—— 这条把它补到**冷启动**这一路。</para>
    /// </summary>
    /// <returns>承接来源的键；没有可承接的（或本会话已有）⇒ <c>null</c>。</returns>
    public string? CarryFromNewest(string? sessionId)
    {
        var target = PathFor(sessionId);
        if (File.Exists(target) || !System.IO.Directory.Exists(Directory))
        {
            return null;
        }

        var newest = new DirectoryInfo(Directory)
            .GetFiles("*.xml")
            .Where(f => !string.Equals(f.FullName, target, StringComparison.Ordinal))
            // **跳过「空台面」的件**（2026-10-15 真机：最近改动的恰是空件 ⇒ 承接一份空的进来；
            //   主人的口径：台面**只要更新过一次就永远不可能是空的**）⇒ 宁取次新的**有内容**那一份。
            .Where(f => !IsEmptyState(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (newest is null)
        {
            return null;
        }

        File.Copy(newest.FullName, target, overwrite: false);
        return Path.GetFileNameWithoutExtension(newest.Name);
    }


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
            entry = DataContractXml.ReadFile<CurrentTailEntry>(path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"尾部存储文件不是合法内容：{path}（{ex.Message}）—— 尾部是**唯一真相源**（不可从流重建），" +
                "坏文件必须报错，不做「当没有」的降级。", ex);
        }

        entry = entry ?? throw new InvalidDataException($"尾部存储内容为空：{path}（唯一真相源，拒绝当作空白板）。");
        return entry with { Tail = entry.Tail ?? [] };
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

        DataContractXml.WriteFileAtomic(path, entry);
    }

    /// <summary>路径展开（支持 <c>~/</c>），与 CLI 其余路径同规。</summary>
    private static string RuntimeDirectory(string directory) =>
        directory.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), directory[2..])
            : directory;

    /// <summary>
    /// 这份台面件是不是**空台面**（承接时跳过它 —— 主人 2026-10-15 口径：台面**只要更新过一次就永远不可能是空的**）。
    /// <para>🔑 判据必须**先加载**，不能看字节：① 能加载且 **0 行** ⇒ 空台面 ⇒ 跳过；
    /// ② **加载失败（坏文件）⇒ 照旧拷过去**（fail-closed：坏文件必须响亮报错、绝不静默当没有 —— 本存储的既有纪律）；
    /// ③ 有内容 ⇒ 拷。</para>
    /// </summary>
    private bool IsEmptyState(FileInfo file)
    {
        try
        {
            var id = System.IO.Path.GetFileNameWithoutExtension(file.Name);
            return (TryLoad(id)?.Tail.Length ?? 0) == 0;
        }
        catch (Exception)
        {
            return false;   // 坏文件 ⇒ 不算「空台面」⇒ 照旧拷（让 loader 响亮报错）
        }
    }
}
