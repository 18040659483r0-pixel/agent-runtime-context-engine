using AgentRuntime.Core.Tooling;

namespace AgentRuntime.Core.Security;

/// <summary>范围授权的**粒度种类**（闭集；新增 = 在这里加一个值 + 一条 <see cref="GrantScopes.Covers"/> 分支）。</summary>
public enum GrantScopeKind
{
    /// <summary>等价于旧的「一次一批」：只覆盖同样确切目标（保底形态）。</summary>
    Exact,

    /// <summary>目录级：覆盖该目录**之下**的同类动作（写 / 删）。</summary>
    Directory,

    /// <summary>程序级：覆盖**同一条命令形状**（程序 + 子命令）的执行。</summary>
    Program,
}

/// <summary>
/// **一条范围授权**（主人 2026-09-17 02:52 选 A）—— 「点头一次，同类自动过」的那张纸。
/// <para>
/// 它是**人当场选出来的**（审批面上的 <c>r</c>：批准并记住这个范围），
/// <b>不是</b>系统自己攒的、<b>不是</b>模型能写进来的。三条边界：
/// </para>
/// <list type="number">
/// <item><b>会话级、易失</b>：只在内存，进程结束即失效（与既有会话 Grant 同命）；</item>
/// <item><b>盖不住硬拒与永不自动放行的三类</b>：受保护目标 / 提权 / 凭据（硬拒，判定在前），
/// must-ask / 宽能力 / 可疑可执行（判定在前）—— 范围授权**排在它们之后**，天生过不去；</item>
/// <item><b>写类有永久例外</b>：<c>.git/</c>、构建文件（<c>*.csproj</c> / <c>*.props</c> / <c>*.targets</c>）、
/// 脚本（<c>*.sh</c> / <c>*.py</c> / <c>*.ps1</c> …）这些「写 → 执行」链的形状**照旧每次问**
/// （<see cref="SecurityPolicy.ScopeCarveOut"/>）。</item>
/// </list>
/// </summary>
/// <param name="Capability">能力。</param>
/// <param name="Kind">粒度种类。</param>
/// <param name="Scope">范围本身（目录真身 / 命令形状 / 确切目标）。</param>
public sealed record ScopeGrant(Capability Capability, GrantScopeKind Kind, string Scope);

/// <summary>
/// **范围授权的计算与匹配**（唯一声明处）。
/// <para>两件事：① 这一项动作**能提供什么范围**（<see cref="OfferFor"/>，审批面上给人看的）；</para>
/// <para>② 某条动作**在不在**某个范围里（<see cref="Covers"/>）。</para>
/// </summary>
public static class GrantScopes
{
    /// <summary>
    /// 这一项动作能提供的范围（<c>null</c> = 这一类不提供范围授权）。
    /// <list type="bullet">
    /// <item><b>写 / 删</b> ⇒ <b>目标所在目录</b>（只覆盖该目录之下）；</item>
    /// <item><b>执行</b> ⇒ <b>命令形状</b> = 程序名 + （最多再一个）非开关词，遇开关即停 ——
    /// 例如 <c>dotnet build -c Release</c> ⇒ 记住 <c>dotnet build</c>：同一子命令的后续调用免问，
    /// 换子命令（<c>dotnet run</c>）照旧问；</item>
    /// <item>其余（只读 / 网络）⇒ 不给（只读本来就免批；网络类归多面展开，各面各自授权）。</item>
    /// </list>
    /// </summary>
    public static ScopeGrant? OfferFor(Capability capability, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        switch (capability)
        {
            case Capability.FsWrite or Capability.FsDelete:
            {
                var directory = DirectoryOf(target);
                return string.IsNullOrWhiteSpace(directory)
                    ? null
                    : new ScopeGrant(capability, GrantScopeKind.Directory, directory);
            }

            case Capability.ProcExec:
            {
                var shape = CommandShapeOf(target);
                return string.IsNullOrWhiteSpace(shape)
                    ? null
                    : new ScopeGrant(capability, GrantScopeKind.Program, shape);
            }

            default:
                return null;
        }
    }

    /// <summary>这条动作在不在这个范围里。</summary>
    public static bool Covers(ScopeGrant grant, Capability capability, string? target)
    {
        ArgumentNullException.ThrowIfNull(grant);

        if (grant.Capability != capability || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        return grant.Kind switch
        {
            GrantScopeKind.Directory => SecurityPolicy.IsSameOrUnder(target, grant.Scope),
            GrantScopeKind.Program => string.Equals(CommandShapeOf(target), grant.Scope, StringComparison.Ordinal),
            _ => string.Equals(grant.Scope, target, StringComparison.Ordinal),
        };
    }

    /// <summary>审批面 / 面板上的一行人话（**决定型内容**：人要知道自己放行了什么）。</summary>
    public static string Describe(ScopeGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        return grant.Kind switch
        {
            GrantScopeKind.Directory =>
                $"本会话允许 {Capabilities.Name(grant.Capability)} @ {grant.Scope}/**（v13：不再逐次问，只留档）",
            GrantScopeKind.Program =>
                $"本会话允许 {Capabilities.Name(grant.Capability)} @ 「{grant.Scope} …」（换子命令仍会留档）",
            _ => $"本会话允许 {Capabilities.Name(grant.Capability)} @ {grant.Scope}",
        };
    }

    /// <summary>
    /// **命令形状**：程序名 + （最多再一个）非开关词；遇到以 <c>-</c> 开头的词就停下。
    /// <para><c>dotnet build -c Release</c> ⇒ <c>dotnet build</c>；<c>ls -la</c> ⇒ <c>ls</c>；<c>dotnet</c> ⇒ <c>dotnet</c>。</para>
    /// </summary>
    public static string? CommandShapeOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var words = new List<string>(2);
        foreach (var token in command.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.StartsWith('-'))
            {
                break;
            }

            var word = token.Trim('\'', '"');
            if (word.Length == 0)
            {
                continue;
            }

            words.Add(word);
            if (words.Count == 2)
            {
                break;
            }
        }

        return words.Count == 0 ? null : string.Join(' ', words);
    }

    private static string? DirectoryOf(string target)
    {
        try
        {
            var trimmed = target.TrimEnd(Path.DirectorySeparatorChar);
            return Path.GetDirectoryName(trimmed);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
