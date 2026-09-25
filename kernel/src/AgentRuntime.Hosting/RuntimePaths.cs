using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Frozen;

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

    /// <summary>
    /// **用量账本路径**（F，2026-09-22）—— 账本跟随**流卷**走：<c>&lt;stream&gt;.usage.jsonl</c>。
    /// <para>
    /// 为什么挂在流上而不是单独配：一次 <c>reset/start</c> 换一卷新流 ⇒ 账也自然分卷，
    /// 不会把两节会话的钱混在一个账上（与「账本是会话级」同一条口径）。
    /// 流没配（不落盘）⇒ <c>null</c>（不要给个默认位置：默认位置会惄惄往真实工作区写）。
    /// </para>
    /// </summary>
    public static string? UsageOf(string? streamPath) =>
        string.IsNullOrWhiteSpace(streamPath) ? null : streamPath + ".usage.jsonl";

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

    /// <summary>
    /// 收尾工作区（WB 语料根）：留空 = **不配**（返回 null —— 猜错的工作区比没有更坏，与技能仓库同一纪律）。
    /// </summary>
    public static string? ResolveLifecycleWorkspace(string configPath, string? workspace) =>
        string.IsNullOrWhiteSpace(workspace) ? null : ResolveAgainstConfig(configPath, workspace);

    /// <summary>
    /// **工具基准注射**（2026-09-23，坑 #134/#135/#137 族）：把**进程 cwd** 钉到工作区（活版），
    /// 并把三个绝对根注入环境变量 <c>WB_ROOT</c>（代码仓）/ <c>WB_LIVE</c>（活版）/ <c>WB_ARCHIVE</c>（WB 存档区），
    /// 工具子进程继承。
    /// <para>
    /// 为什么：工具的相对路径基准 = **进程 cwd**（TUI 里 = 程序集目录 <c>src/AgentRuntime.Tui</c>），
    /// 于是 <c>whitebox/workspace</c> 一类相对路径全部落空（2026-09-23 01:15 收尾连栽 4 次）；
    /// 而 <c>cd ../../..</c> 的算术又会把路径拼成 <c>projects/projects/…</c>。
    /// </para>
    /// <para>
    /// **不猜**：工作区没配 / 路径不存在 ⇒ 原样不动，返回 <c>null</c>（与 <see cref="ResolveLifecycleWorkspace"/> 同一纪律）。
    /// 调用点只在真入口（<c>RuntimeHost.Boot</c>）—— 测试走的 <c>BootWith</c> 不受影响。
    /// </para>
    /// </summary>
    public static (string Root, string Live, string Archive)? PinToolBase(string? workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return null;
        }

        var live = Path.GetFullPath(workspace);
        var root = Path.GetFullPath(Path.Combine(live, "..", ".."));                                 // projects/AgentRuntime
        var archive = Path.GetFullPath(Path.Combine(live, "..", "..", "..", "..", "whitebox-workspace")); // software-company/whitebox-workspace（WB 存档区；比代码仓多一层）

        Environment.SetEnvironmentVariable("WB_LIVE", live);
        Environment.SetEnvironmentVariable("WB_ROOT", root);
        Environment.SetEnvironmentVariable("WB_ARCHIVE", archive);

        if (!string.Equals(Directory.GetCurrentDirectory(), live, StringComparison.Ordinal))
        {
            Directory.SetCurrentDirectory(live);
        }

        return (root, live, archive);
    }

    /// <summary>
    /// 末态快照落点：留空 → 默认 <c>~/.agentruntime/handover.json</c>（**有唯一默认**，不靠猜 —— 与白板/草稿同规）。
    /// </summary>
    public static string ResolveHandover(string configPath, string? handover)
    {
        var value = string.IsNullOrWhiteSpace(handover) ? HandoverStore.DefaultPath : handover;
        return ResolveAgainstConfig(configPath, value);
    }

    /// <summary>踩坑集文件：留空 = **不配**（同上）。</summary>
    public static string? ResolveLifecyclePitfalls(string configPath, string? pitfalls) =>
        string.IsNullOrWhiteSpace(pitfalls) ? null : ResolveAgainstConfig(configPath, pitfalls);

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
