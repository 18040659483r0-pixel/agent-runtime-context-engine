using AgentRuntime.Core.Configuration;

namespace AgentRuntime.Hosting;

/// <summary>
/// **配置内相对路径的解析唯一家** —— 所有以「配置文件所在目录」为基准的路径都在这里解析。
/// <para>
/// 口径（从 CLI 原样上移，逐字节不变）：<c>~</c> 展开 → 已是绝对路径就取全路径 → 否则按
/// **配置文件所在目录**拼接。多个宿主（Cli / Tui）共用本类 ⇒ 同一份配置在两边指向同一批文件。
/// </para>
/// </summary>
public static class RuntimePaths
{
    /// <summary>冻结语料根：相对路径按**配置文件所在目录**解析，支持 ~ 展开；留空 = 无语料。</summary>
    public static string? ResolveFrozenRoot(string configPath, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        return ResolveAgainstConfig(configPath, root);
    }

    /// <summary>水位线路径：留空 → 默认位置（<c>~/.agentruntime/watermark.json</c>）。</summary>
    public static string ResolveWatermark(string configPath, string? watermark)
    {
        var value = string.IsNullOrWhiteSpace(watermark) ? "~/.agentruntime/watermark.json" : watermark;
        return ResolveAgainstConfig(configPath, value);
    }

    /// <summary>事件流文件路径；留空 = 空串（模块层据此判定「不落盘」）。</summary>
    public static string ResolveStream(string configPath, string? streamPath)
    {
        if (string.IsNullOrWhiteSpace(streamPath))
        {
            return string.Empty;
        }

        return ResolveAgainstConfig(configPath, streamPath);
    }

    /// <summary>快照路径：留空 → 默认 <c>~/.agentruntime/snapshot.json</c>。</summary>
    public static string ResolveSnapshot(string configPath, string? snapshot)
    {
        var value = string.IsNullOrWhiteSpace(snapshot) ? "~/.agentruntime/snapshot.json" : snapshot;
        return ResolveAgainstConfig(configPath, value);
    }

    /// <summary>焦点缓存路径：留空 → 默认 <c>~/.agentruntime/focus.json</c>。</summary>
    public static string ResolveFocus(string configPath, string? focus)
    {
        var value = string.IsNullOrWhiteSpace(focus) ? "~/.agentruntime/focus.json" : focus;
        return ResolveAgainstConfig(configPath, value);
    }

    /// <summary>尾部存储**目录**：留空 → 默认 <c>~/.agentruntime/tail</c>。</summary>
    public static string ResolveTail(string configPath, string? storePath)
    {
        var value = string.IsNullOrWhiteSpace(storePath) ? "~/.agentruntime/tail" : storePath;
        return ResolveAgainstConfig(configPath, value);
    }

    /// <summary>草稿存储**目录**：留空 → 默认 <c>~/.agentruntime/draft</c>。</summary>
    public static string ResolveDraft(string configPath, string? storePath)
    {
        var value = string.IsNullOrWhiteSpace(storePath) ? "~/.agentruntime/draft" : storePath;
        return ResolveAgainstConfig(configPath, value);
    }

    /// <summary>L3 技能仓库（V6）：留空 = **不接**（返回 null，不编默认路径 —— 猜错的地址表比没有更坏）。</summary>
    public static string? ResolveSkillRepo(string configPath, string? repo)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            return null;
        }

        return ResolveAgainstConfig(configPath, repo);
    }

    /// <summary>
    /// 技能**常驻层目录**（V6+）：留空 = **不接**（返回 null，同 <see cref="ResolveSkillRepo"/> 口径）。
    /// <para>⚠️ 新加路径字段**必须在这里**解析 —— 漏了就会按 **cwd** 猜（实测：换个 cwd 直接 DirectoryNotFound）。</para>
    /// </summary>
    public static string? ResolveSkillResident(string configPath, string? resident)
    {
        if (string.IsNullOrWhiteSpace(resident))
        {
            return null;
        }

        return ResolveAgainstConfig(configPath, resident);
    }

    /// <summary>把（可能带 <c>~</c> 的）相对路径按**配置文件所在目录**解析成绝对路径。</summary>
    public static string ResolveAgainstConfig(string configPath, string path)
    {
        var expanded = RuntimeConfiguration.ExpandHome(path);
        if (Path.IsPathRooted(expanded))
        {
            return Path.GetFullPath(expanded);
        }

        var configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))
            ?? throw new InvalidDataException($"无法确定配置文件目录：{configPath}");
        return Path.GetFullPath(Path.Combine(configDir, expanded));
    }
}
