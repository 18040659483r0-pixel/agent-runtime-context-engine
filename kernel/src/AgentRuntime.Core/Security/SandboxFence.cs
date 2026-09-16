using System.Text;

namespace AgentRuntime.Core.Security;

/// <summary>
/// **执行层围栏**（T3 下半段第一刀）：让 <c>exec</c> 的子进程跑在 macOS **seatbelt** 沙箱里，
/// 把 <see cref="SecurityPolicy"/> 的「保护集」从**声明**变成**内核强制的边界**。
/// <para>
/// <b>为什么需要它</b>（<c>docs/DESIGN-SECURITY-GATEWAY.md</c> §9.6 / §9.5）：声明层只能挡住「走网关」的动作；
/// 一旦 <c>exec</c> 是「万能管子」，被约束的一方可以**不走网关**（解释器 / <c>rm</c> / <c>&gt;&gt;</c> / 符号链接）
/// 去读凭据、改账本、换二进制。沙箱把这条路在内核里堵掉。
/// </para>
/// <para>
/// <b>与「真降权」的差距（诚实写在这里）</b>：本围栏**不改 uid** —— 子进程仍是当前用户，
/// 所以它挡得住「围栏内明令禁止的事」，挡不住「同 uid 的进程绕过 [WB] 自己去做的事」
/// （例如用户自己开的终端、或另起一个不经过 [WB] 的进程）。要点：**它把 [WB] 自己能造成的副作用关进了笼子**，
/// 但「[WB] 之外的同 uid 程序」仍在本围栏之外 —— 那需要另一 uid / 容器（见 §9.4 第 1 条，未做）。
/// </para>
/// <para>
/// <b>开关（fail-closed）</b>：<c>WB_SANDBOX=1</c> 强制开、<c>=0</c> 强制关；不设时**跟随判定端**（设了
/// <c>WB_GATE_SOCKET</c> 就默认开）。开了但本机没有 <c>sandbox-exec</c> ⇒ **拒绝执行**（不静默降级成无围栏）。
/// </para>
/// </summary>
public static class SandboxFence
{
    public const string EnableEnv = "WB_SANDBOX";
    public const string GateEnv = "WB_GATE_SOCKET";

    /// <summary>seatbelt 解释器（macOS 自带；缺失 ⇒ 围栏不可用）。</summary>
    public const string SandboxExec = "/usr/bin/sandbox-exec";

    private static string? _profilePath;

    /// <summary>围栏是否启用（显式开关优先；否则跟随判定端模式）。</summary>
    public static bool IsEnabled =>
        ResolveEnabled(Environment.GetEnvironmentVariable(EnableEnv), Environment.GetEnvironmentVariable(GateEnv));

    /// <summary>
    /// 开关判据（**纯函数**，便于离线测）：显式值非空则以它为准；为空则**跟随判定端**（设了 <c>WB_GATE_SOCKET</c> 就默认开）。
    /// </summary>
    public static bool ResolveEnabled(string? explicitFlag, string? gateSocket)
    {
        var raw = explicitFlag?.Trim();
        if (!string.IsNullOrEmpty(raw))
        {
            return raw is "1" or "on" or "ON" or "true" or "TRUE" or "yes";
        }

        return !string.IsNullOrWhiteSpace(gateSocket);
    }

    /// <summary>本机能不能真的上围栏（判据是**文件真的在**，不是「我们以为它在」）。</summary>
    public static bool Available => File.Exists(SandboxExec);

    /// <summary>提权程序的候选绝对路径（只有真的存在的才写进轮廓）。</summary>
    private static IEnumerable<string> PrivilegeExecutables()
    {
        string[] programs = ["sudo", "su", "doas", "launchctl", "chown", "chmod"];
        string[] dirs = ["/usr/bin", "/bin", "/usr/sbin", "/sbin"];
        foreach (var dir in dirs)
        {
            foreach (var program in programs)
            {
                var path = Path.Combine(dir, program);
                if (File.Exists(path))
                {
                    yield return path;
                }
            }
        }
    }

    /// <summary>受保护目标（与判定层**同一个唯一声明处**；不是第二份清单）。</summary>
    public static IReadOnlyList<string> ProtectedRoots() => SecurityPolicy.ProtectedRoots();

    /// <summary>
    /// 生成 seatbelt 轮廓。规则**顺序敏感**（seatbelt 取**最后一条匹配**）：先 <c>allow default</c>，
    /// 再逐条 <c>deny</c> —— 于是「读不出来的东西读不出来、写不了的东西写不了」由内核保证。
    /// </summary>
    public static string BuildProfile()
    {
        var sb = new StringBuilder();
        sb.Append("(version 1)\n");
        sb.Append("(allow default)\n");
        sb.Append("; 保护集：读与写都拒（声明层同源：SecurityPolicy.ProtectedRoots）\n");

        foreach (var root in ProtectedRoots())
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            sb.Append("(deny file-read* file-write* (subpath ").Append(Quote(root)).Append("))\n");
        }

        sb.Append("; 提权：直接拒（与 SecurityPolicy 的 PrivilegePrograms 同源，派生出绝对路径）\n");
        foreach (var exe in PrivilegeExecutables())
        {
            sb.Append("(deny process-exec (literal ").Append(Quote(exe)).Append("))\n");
        }

        return sb.ToString();
    }

    private static string Quote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>轮廓落盘（0600；进程内只写一次）。</summary>
    public static string ProfilePath()
    {
        if (_profilePath is not null && File.Exists(_profilePath))
        {
            return _profilePath;
        }

        var dir = Path.Combine(Path.GetTempPath(), "wb-sandbox");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "profile.sb");
        File.WriteAllText(path, BuildProfile(), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception)
            {
                // 权限设不上不致命（轮廓本身不含秘密；它只是规则）。
            }
        }

        _profilePath = path;
        return path;
    }

    /// <summary>丢弃轮廓缓存（进程内只写一次；环境切换后要重建时用）。</summary>
    public static void ResetProfileCache() => _profilePath = null;

    /// <summary>
    /// 把一条 shell 命令包进围栏：<c>sandbox-exec -f &lt;轮廓&gt; /bin/sh -c &lt;命令&gt;</c>。
    /// 传参走 argv（不经 shell 二次拼接），所以命令原文不会被引号吃掉。
    /// </summary>
    public static (string FileName, IReadOnlyList<string> Arguments) Wrap(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var args = new List<string> { "-f", ProfilePath(), "/bin/sh", "-c", command };
        return (SandboxExec, args);
    }

    /// <summary>给审批面用的一句话（人在点头之前应该看到「这一次是不是在笼子里跑」）。</summary>
    public static string Summary()
    {
        if (!IsEnabled)
        {
            return "执行层围栏：**关**（子进程与 [WB] 同域）——「万能管子」仍开着。";
        }

        if (!Available)
        {
            return $"执行层围栏：**启用但不可用**（找不到 {SandboxExec}）⇒ fail-closed，命令会被拒绝。";
        }

        var roots = ProtectedRoots().Count;
        return $"执行层围栏：**开**（seatbelt 内核级）—— 拒读写保护集 {roots} 处 · "
             + $"拒提权 {PrivilegeExecutables().Count()} 个程序；子进程仍以当前用户身份运行（未降 uid）。";
    }
}
