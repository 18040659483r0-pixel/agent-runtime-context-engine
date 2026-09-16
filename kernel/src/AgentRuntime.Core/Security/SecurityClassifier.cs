using AgentRuntime.Core.Tooling;

namespace AgentRuntime.Core.Security;

/// <summary>
/// **动作分类器** —— 把「工具名 + 结构化参数」翻成 <see cref="SecurityAction"/>（判定层的唯一入口）。
/// <para>
/// 只做翻译，**不做判断**（判断在 <see cref="SecurityGateway"/>）；判不出时返回**最保守**的形状
/// （空目标 + 不改状态的能力），由判定层按 fail-closed 处理。
/// </para>
/// </summary>
public static class SecurityClassifier
{
    /// <summary>分类一次工具调用。<paramref name="effect"/> 是给人看的那句话（来自工具自己的渲染）。</summary>
    /// <param name="tool">工具名（已在更早处校验过是否登记）。</param>
    /// <param name="args">结构化参数。</param>
    /// <param name="effect">人话说明。</param>
    /// <param name="grants">会话 Grant 集（用于判「这个可执行文件是不是本会话写出来的」）。</param>
    public static SecurityAction Classify(string tool, ToolArgs args, string effect, GrantSet? grants = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var name = tool?.Trim().ToLowerInvariant();

        return name switch
        {
            ToolNames.Read or ToolNames.List => FileAction(Capability.FsRead, args, effect),
            ToolNames.Write or ToolNames.Edit => FileAction(Capability.FsWrite, args, effect),
            ToolNames.Exec => ExecAction(args, effect, grants),
            _ => new SecurityAction(Capability.FsRead, string.Empty, effect),
        };
    }

    private static SecurityAction FileAction(Capability capability, ToolArgs args, string effect)
    {
        var path = TryNormalize(ToolPaths.NormalizePathArgument(args));
        return new SecurityAction(capability, path, effect);
    }

    private static SecurityAction ExecAction(ToolArgs args, string effect, GrantSet? grants)
    {
        var command = args.OptionalString("command") ?? string.Empty;
        var program = SecurityPolicy.FirstProgram(command);
        var token = SecurityPolicy.FirstProgramToken(command);

        // 「本会话写出来的可执行文件」：程序的 token 像路径（含 /）且落在污染集里 ⇒ 标可疑（V2 例 7 / 洞 W2）。
        var untrusted = false;
        if (!string.IsNullOrWhiteSpace(token) && token.Contains(Path.DirectorySeparatorChar))
        {
            untrusted = grants?.IsTainted(TryNormalize(token)) == true;
        }

        // 宽能力：解释器 / 构建 / 容器，或**带内联代码**（`python -c …`）—— 每次都要重新点头（洞 W1）。
        var wide = SecurityPolicy.IsWide(program) || SecurityPolicy.HasInlineCode(command);

        // 多面展开：一条命令跨几个面（proc / net / fs）⇒ 每个面各自授权（V2 例 8）。
        var facets = CommandFacets.Of(command);

        return new SecurityAction(Capability.ProcExec, command, effect, untrusted, wide, facets);
    }

    /// <summary>规范化失败（判不出真身）⇒ 空串 —— 判定层按 fail-closed 处理，绝不「尽力猜一个」。</summary>
    private static string TryNormalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return ToolPaths.Normalize(path);
        }
        catch (ToolUsageException)
        {
            return string.Empty;
        }
    }
}
