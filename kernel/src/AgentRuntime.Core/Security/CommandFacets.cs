namespace AgentRuntime.Core.Security;

/// <summary>
/// **命令的多面展开**（V2 §二 Facets / §2.1 例 8）—— <b>唯一声明处</b>。
/// <para>
/// 一条命令常常同时跨几个面：<c>npm install x</c> = 执行程序 + 联网 + 写盘。
/// 设计要的是：**每个面各自授权，任一面被拒 ⇒ 整条不做**（不是"放行了 proc 就随便它联网写盘"）。
/// </para>
/// <para>判据故意保守（只认能一眼看出的形状），且**只用于"要不要多问几个面"，不影响风险档**。</para>
/// </summary>
public static class CommandFacets
{
    /// <summary>联网类程序（会自己开 socket 的程序）。</summary>
    private static readonly string[] NetworkPrograms =
        ["curl", "wget", "git", "svn", "npm", "yarn", "pnpm", "pip", "pip3", "brew", "ssh", "scp", "rsync", "gh"];

    /// <summary>写盘形状：重定向 / 安装类子命令。</summary>
    private static readonly string[] WriteSubcommands = ["install", "add", "clone", "checkout", "update", "upgrade", "pull", "fetch"];

    /// <summary>这条命令展开出的面（名字用 <see cref="Capabilities"/> 的点名；**不含主面之外时返回 null**）。</summary>
    public static IReadOnlyList<string>? Of(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var program = SecurityPolicy.FirstProgram(command);
        var facets = new List<string> { Capabilities.Name(Capability.ProcExec) };

        if (program is not null && NetworkPrograms.Contains(program))
        {
            facets.Add(Capabilities.Name(Capability.NetRequest));
        }

        if (HasRedirect(command) || (program is not null && WriteSubcommands.Any(s => HasToken(command, s))))
        {
            facets.Add(Capabilities.Name(Capability.FsWrite));
        }

        return facets.Count > 1 ? facets : null;
    }

    private static bool HasRedirect(string command) =>
        command.Contains(">>", StringComparison.Ordinal) || command.Contains(" > ", StringComparison.Ordinal);

    private static bool HasToken(string command, string name) =>
        command.Split([' ', '\t', '&', '|', ';'], StringSplitOptions.RemoveEmptyEntries)
               .Any(t => string.Equals(t.Trim('\'', '"'), name, StringComparison.OrdinalIgnoreCase));
}
