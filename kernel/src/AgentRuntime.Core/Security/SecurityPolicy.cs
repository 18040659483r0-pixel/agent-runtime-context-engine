using AgentRuntime.Core.Tooling;

namespace AgentRuntime.Core.Security;

/// <summary>
/// **安全策略内核**（`docs/DESIGN-SECURITY-GATEWAY.md` §9.6 第 1 条）。
/// <para>
/// <b>为什么是代码而不是配置</b>：策略若放在可写的配置文件里，「改策略」本身就是解锁手段。
/// 因此本类是**唯一声明处**，编译进二进制；外部配置将来只能**往严里加**，不能放宽。
/// （与协议区同一条纪律：来源只能是代码、收尾不可改、Agent 不可摘。）
/// </para>
/// <para>
/// 本版只做三件最要紧的事：<b>保护目标</b>（写它 = 直接拒绝）、<b>提权</b>（拒绝）、
/// <b>宽能力 / 凭据形状</b>（标记出来，交给判定层永不自动放行）。
/// </para>
/// </summary>
public static class SecurityPolicy
{
    /// <summary>
    /// 受保护的文件/目录名（在用户主目录下；**唯一声明处**）。
    /// <para>写 / 删 / 执行这些位置下的东西 ⇒ 直接 <c>DENY</c>（不进入审批，不给「也许点一下就好」的机会）。</para>
    /// </summary>
    private static readonly string[] ProtectedUnderHome =
    [
        ".ssh",            // 私钥：一旦被读/改/换，所有下游信任都塌
        ".agentruntime",   // 运行时的密钥、恢复点、水位、账本
        ".aws", ".gnupg", ".kube", ".docker",
    ];

    /// <summary>凭据形状的文件名（小写全等）与后缀；命中 ⇒ 视为凭据目标（写/删 = 拒绝；读 = 需批）。</summary>
    private static readonly string[] CredentialNames =
    [
        "api_key", "apikey", "credentials", "credential", "id_rsa", "id_ed25519",
        ".env", ".netrc", "secrets.json", "token", "tokens",
    ];

    private static readonly string[] CredentialSuffixes = [".pem", ".key", ".p12", ".pfx", ".jks"];

    /// <summary>提权类程序（首词命中即提权）。</summary>
    private static readonly string[] PrivilegePrograms = ["sudo", "su", "doas", "launchctl", "chown", "chmod"];

    /// <summary><b>宽能力</b>程序：放行它们 = 把它们能做的一切都放行（V2 洞 W1）。</summary>
    private static readonly string[] WidePrograms =
    [
        "python", "python3", "perl", "ruby", "node", "deno", "bun",
        "sh", "bash", "zsh", "osascript", "awk", "sed", "eval",
        "make", "docker", "kubectl",
    ];

    /// <summary>受保护根目录（绝对路径；主目录判不出时只返回运行时目录）。</summary>
    public static IReadOnlyList<string> ProtectedRoots()
    {
        var roots = new List<string>();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            roots.Add(Path.Combine(home, ".ssh"));
            foreach (var name in ProtectedUnderHome)
            {
                roots.Add(Path.Combine(home, name));
            }
        }

        // 运行时自己所在的目录：**默认不保护** —— 保护它会挡住 WB 自迭代（见 ProtectSelf 注释）。
        if (ProtectSelf())
        {
            var baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDir))
            {
                roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir)));
            }
        }

        return roots;
    }

    /// <summary>
    /// 是否把「运行时自己所在目录」也纳入保护集（**默认否**）。
    /// <para><b>为什么默认否（2026-09-16 21:2x，主人定）</b>：WB 要能在自己的 TUI 里
    /// <b>改自己、编自己、跑自己</b>（自迭代）。一旦这个目录进保护集，<c>dotnet build</c>
    /// 写自己的输出目录就会被围栏拒掉 ⇒ <b>自迭代被自己的笼子挡住</b>。
    /// 现在还不是交付物（是半成品），所以<b>自迭代优先</b>；保护集只留「人的钥匙与私有文件」
    ///（<c>~/.ssh</c> / <c>~/.agentruntime</c> / <c>~/.aws</c> 等）。</para>
    /// <para>交付物阶段若想连「改判定者本身」也硬拒，设 <c>WB_PROTECT_SELF=1</c> 即可（无需改代码）。</para>
    /// </summary>
    public const string ProtectSelfEnv = "WB_PROTECT_SELF";

    /// <summary>保护自身开关（**纯函数**，便于离线测）：<c>WB_PROTECT_SELF=1/on/true/yes</c> ⇒ 保护运行时自身目录。</summary>
    public static bool ProtectSelf()
    {
        var raw = Environment.GetEnvironmentVariable(ProtectSelfEnv)?.Trim();
        return !string.IsNullOrEmpty(raw) && raw is "1" or "on" or "ON" or "true" or "TRUE" or "yes";
    }

    /// <summary>是不是受保护目标（**按规范化路径的目录包含关系判**；判不出 ⇒ false，由调用方按能力继续判）。</summary>
    public static bool IsProtected(string? normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        foreach (var root in ProtectedRoots())
        {
            if (IsSameOrUnder(normalizedPath, root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>路径相等，或位于该目录之下（段边界比较，`/a/bc` 不算在 `/a/b` 下）。</summary>
    public static bool IsSameOrUnder(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        if (string.Equals(path, normalizedRoot, StringComparison.Ordinal))
        {
            return true;
        }

        return path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// **范围授权的永久例外**（写 → 执行链的那些形状）：即使人已经授权了某个目录，
    /// 写这些目标也**照旧每次问**。
    /// <para>为什么（主人 2026-09-17 选 A 时的硬约束）：目录级放行最容易打开的洞是「写一个东西，然后让它被执行」——
    /// <c>.git/hooks/*</c>（git 自己会跑）、构建文件（<c>*.csproj</c>/<c>*.props</c>/<c>*.targets</c>，构建时会跑代码）、
    /// 脚本（<c>*.sh</c>/<c>*.py</c>/<c>*.ps1</c> …）。这些形状不进范围。</para>
    /// <para><b>判不出 ⇒ 算例外</b>（fail-closed：宁可多问一次）。</para>
    /// </summary>
    public static bool ScopeCarveOut(string? normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return true;
        }

        // 路径里出现 .git 段（含 .git/hooks/*）⇒ 例外。
        if (normalizedPath.Split(Path.DirectorySeparatorChar).Contains(".git", StringComparer.Ordinal))
        {
            return true;
        }

        var name = Path.GetFileName(normalizedPath);
        if (ScopeCarveOutNames.Contains(name))
        {
            return true;
        }

        var lower = name.ToLowerInvariant();
        return ScopeCarveOutSuffixes.Any(suffix => lower.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>范围授权的例外文件名（全等）。</summary>
    private static readonly string[] ScopeCarveOutNames =
        ["Makefile", "makefile", "GNUmakefile", "package.json", "Dockerfile", "dockerfile", "CMakeLists.txt"];

    /// <summary>范围授权的例外后缀（小写）。写它们 = 可能在写一个会被执行的东西。</summary>
    private static readonly string[] ScopeCarveOutSuffixes =
        [".csproj", ".props", ".targets", ".sln", ".slnx", ".sh", ".bash", ".zsh", ".fish", ".py", ".ps1", ".plist", ".scpt", ".command"];

    /// <summary>凭据形状的路径（按文件名 / 后缀判）。</summary>
    public static bool IsCredentialLike(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var name = Path.GetFileName(path).ToLowerInvariant();
        if (CredentialNames.Contains(name))
        {
            return true;
        }

        var lower = path.ToLowerInvariant();
        return CredentialSuffixes.Any(suffix => lower.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>命令里有没有提权（首词或任一 token 的基名命中；保守：宁严不宽）。</summary>
    public static bool IsPrivilegeEscalation(string? command)
    {
        var program = FirstProgram(command);
        return program is not null && PrivilegePrograms.Contains(program);
    }

    /// <summary>宽的、能吞下一切的程序（解释器 / 构建 / 容器）。</summary>
    public static bool IsWide(string? program) =>
        !string.IsNullOrWhiteSpace(program) && WidePrograms.Contains(program.Trim().ToLowerInvariant());

    /// <summary>命令里**含内联代码**（如 <c>python -c "…"</c> / <c>sh -c …</c>）⇒ 一定标记为宽。</summary>
    public static bool HasInlineCode(string? command) =>
        !string.IsNullOrWhiteSpace(command)
        && System.Text.RegularExpressions.Regex.IsMatch(command, @"(^|\s)-[a-zA-Z]*c[a-zA-Z]*\s", System.Text.RegularExpressions.RegexOptions.None);

    /// <summary>命令的**首个程序名**（基名、小写；判不出 ⇒ null）。</summary>
    public static string? FirstProgram(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        foreach (var token in command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 0 || token[0] == '-')
            {
                continue;
            }

            var name = token.Trim('\'', '"');
            var slash = name.LastIndexOfAny(['/', '\\']);
            if (slash >= 0)
            {
                name = name[(slash + 1)..];
            }

            return name.Length == 0 ? null : name.ToLowerInvariant();
        }

        return null;
    }

    /// <summary>命令里的首个程序**原始 token**（带路径时用于判定「本会话写出来的可执行文件」）。</summary>
    public static string? FirstProgramToken(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        foreach (var token in command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 0 || token[0] == '-')
            {
                continue;
            }

            return token.Trim('\'', '"');
        }

        return null;
    }

    /// <summary>给审批面用的「为什么它是宽的 / 可疑的」解释（唯一话术来源）。</summary>
    public static string WideReason(string? program) =>
        $"{program} 是解释器/构建类程序 —— 放行它等价于放行它能做的一切（V2 洞 W1）⇒ 每次都要重新点头，不记 Grant。";

    /// <summary>
    /// **命令里碰没碰到受保护路径**（例如 <c>rm ~/.ssh/id_rsa</c> / <c>write</c> 一个受保护目录下的文件）。
    /// <para>只看**像路径的 token**（含 <c>/</c> 或以 <c>~</c> 开头），逐个规范化后判 —— 判不出的 token 跳过，
    /// 不因此放行（命令本身仍然要人点头）。</para>
    /// </summary>
    public static bool CommandTouchesProtected(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        foreach (var rawToken in command.Split([' ', '\t', '"', '\''], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Trim('"', '\'', ';', ',', ')', '(');
            if (token.Length == 0 || token[0] == '-')
            {
                continue;
            }

            if (!token.Contains('/') && !token.StartsWith('~'))
            {
                continue;   // 不像路径（`status` / `-m` 之类）⇒ 不看
            }

            string normalized;
            try
            {
                normalized = ToolPaths.Normalize(token);
            }
            catch (ToolUsageException)
            {
                continue;   // 规范化失败 ⇒ 跳过（命令照旧要人点头，不是放行）
            }

            if (IsProtected(normalized))
            {
                return true;
            }
        }

        return false;
    }
}
