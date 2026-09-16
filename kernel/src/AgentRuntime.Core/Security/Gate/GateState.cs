using AgentRuntime.Core.Tooling;

namespace AgentRuntime.Core.Security.Gate;

/// <summary>
/// **判定端的状态目录**（T3 的"信任域"落点）—— <c>docs/DESIGN-SECURITY-GATEWAY.md</c> §9.3 T3。
/// <para>
/// 出厂位置 <c>/usr/local/var/wb-gate</c>，**属主是判定端那个 uid**、模式 **0700**：
/// 于是 Agent 所在的 uid **连读都读不到**（不是"我们说它不能读"，而是 OS 说"你打不开"）。
/// </para>
/// <para>
/// <b>启动自检 fail-closed</b>：目录存在但模式不是 0700 ⇒ **拒绝启动**（不"凑合着用"）。
/// 这条自检是 T3 的牙：状态目录一旦被放宽，整套判定就退化成"声明"。
/// </para>
/// </summary>
public static class GateState
{
    /// <summary>出厂**根**目录（须由安装脚本创建、属主 = 判定端 uid；模式 0755 以便穿行到套接字）。</summary>
    public const string DefaultRoot = "/usr/local/var/wb-gate";

    /// <summary>根目录要求的模式（可穿行；**状态子目录**另有 0700 强制）。</summary>
    public const UnixFileMode RequiredRootMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                              | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                              | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>要求的模式：只有属主可读写进入（0700）。</summary>
    public const UnixFileMode RequiredMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>套接字文件名。</summary>
    public const string SocketFileName = "gate.sock";

    /// <summary>预授权账本文件名。</summary>
    public const string LedgerFileName = "grants.json";

    /// <summary>根目录。</summary>
    public static string RootDirectory(string? root = null) => root ?? DefaultRoot;

    /// <summary>套接字路径（在**根**目录下：能被 Agent 那个 uid 连接）。</summary>
    public static string SocketPath(string? root = null) => Path.Combine(RootDirectory(root), SocketFileName);

    /// <summary>**状态**目录（账本的家；0700，只有判定端读得到）。</summary>
    public static string StateDirectory(string? root = null) => Path.Combine(RootDirectory(root), "state");

    /// <summary>预授权账本路径（在状态目录里 ⇒ 属主 0700 保护）。</summary>
    public static string LedgerPath(string? root = null) => Path.Combine(StateDirectory(root), LedgerFileName);

    /// <summary>确保**根**目录存在且可穿行（套接字要在里面；它不需要保密）。</summary>
    public static void EnsureRoot(string root)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("判定端的信任域依赖 Unix 文件模式与域套接字 —— Windows 上本版本不支持。");
        }

        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            File.SetUnixFileMode(root, RequiredRootMode);
        }
    }

    /// <summary>
    /// **确保状态目录可信**：不存在就按 0700 建；存在但模式不是 0700 ⇒ 抛错（不降级）。
    /// </summary>
    public static void EnsureSecure(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("判定端的信任域依赖 Unix 文件模式（0700）与域套接字 —— Windows 上本版本不支持。");
        }

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory, RequiredMode);
            return;
        }

        var mode = File.GetUnixFileMode(directory);
        if ((mode & ~RequiredMode) != 0)
        {
            throw new InvalidDataException(
                $"状态目录 {directory} 的模式是 {mode}（应为 {RequiredMode}）—— 别人读得到它 ⇒ " +
                "判定端的信任域不成立，**拒绝启动**（请把属主/模式改回判定端 uid + 0700）。");
        }
    }

    /// <summary>诊断：目录当前模式（判不出 ⇒ null）。</summary>
    public static UnixFileMode? ModeOf(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return Directory.Exists(directory) ? File.GetUnixFileMode(directory) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return null;
        }
    }
}
