using AgentRuntime.Core.Configuration;

namespace AgentRuntime.Core.Security;

/// <summary>收尾窗口里要静默放行的一项（能力 + 代表目标；范围由 <see cref="GrantScopes.OfferFor"/> 推出来）。</summary>
public sealed record CloseoutWindowItem(Capability Capability, string Target, string Why);

/// <summary>
/// **收尾窗口**（主人 2026-09-20 14:5x 定）：「这部分在收尾阶段默认放行 —— 这已经是用户明确授权的一个 tasklife 收尾动作…
/// **整个收尾都应该是一气呵成，用户授权一次，就全部自动静默走完**」。
/// <para>
/// 做法：收尾开始时，把「收尾**必须**动的那些东西」按既有**范围授权**机制一次性记下（会话级、易失）——
/// 之后同一批动作**不再逐条问人**；收尾结束（<c>/reset</c>）时一并撤销。
/// </para>
/// <para><b>边界（不许越）</b>：只覆盖「收尾**已知要写**的那几个目录 / 那几条命令形状」；
/// must-ask（发布 / 不可逆）· 宽能力 · 可疑可执行 · 受保护目标**永远盖不住** —— 那些是判定链的次序保证，不是约定。
/// 用户随时可以问「现在的状况」并复核：<c>/session --check</c> + <c>/grants</c>。</para>
/// </summary>
public static class CloseoutWindow
{
    /// <summary>窗口要放行的目标（**数据化**，由配置推导；没有配置就没有窗口）。</summary>
    public static IReadOnlyList<CloseoutWindowItem> Items(RuntimeConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var items = new List<CloseoutWindowItem>();
        var workspace = config.Lifecycle.Workspace;

        if (!string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace))
        {
            // 收尾要写的语料（**代表文件**：范围由它们的所在目录推出 —— 写目录比写根更小、更可审计）。
            items.Add(new CloseoutWindowItem(Capability.FsWrite, Path.Combine(workspace, "KNOWLEDGE.md"), "抽象 L1/L2 进知识库正文"));
            items.Add(new CloseoutWindowItem(Capability.FsWrite, Path.Combine(workspace, "memory", "当日记忆.md"), "写当日记忆"));
            items.Add(new CloseoutWindowItem(Capability.FsWrite, Path.Combine(workspace, "handoff", "交接件.md"), "交 task 的 handoff 条目"));
            items.Add(new CloseoutWindowItem(Capability.FsWrite, Path.Combine(workspace, "knowledge", "knowledge.md"), "晋级知识库（版本 +1）"));
            items.Add(new CloseoutWindowItem(Capability.FsWrite, Path.Combine(workspace, "BULLETIN.md"), "公告板条目"));
        }

        if (!string.IsNullOrWhiteSpace(config.Lifecycle.Pitfalls))
        {
            items.Add(new CloseoutWindowItem(Capability.FsWrite, config.Lifecycle.Pitfalls, "交坑条目（L3 归位）"));
        }

        // 流水线命令：只在**配置里声明过**的那些命令形状（python3 …）上放行。
        foreach (var command in config.Lifecycle.Pipeline)
        {
            items.Add(new CloseoutWindowItem(Capability.ProcExec, command, "收尾流水线（判定 / 晋级 / 派生 / 重建）"));
        }

        return items;
    }

    /// <summary>打开窗口（把范围授权一次性记下）。返回**给人看的**那几行（放行了什么，逐条写清）。</summary>
    public static IReadOnlyList<string> Open(SecurityGateway? gateway, RuntimeConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var items = Items(config);
        if (items.Count == 0)
        {
            return ["[收尾窗口] 未配 lifecycle（工作区 / 坑集 / 流水线）⇒ **没有可放行的东西**（收尾仍可做，但每一步都会照常问）。"];
        }

        if (gateway is null)
        {
            return ["[收尾窗口] 本会话没有工具面（未装 tool 模块）⇒ 不需要放行（也没有能静默执行的动作）。"];
        }

        var lines = new List<string>();
        var opened = 0;

        foreach (var item in items)
        {
            if (GrantScopes.OfferFor(item.Capability, item.Target) is not { } scope)
            {
                continue;
            }

            gateway.OnHumanApprovedScope(scope);   // ← 「用户授权一次」就是这里：授权来自主人的明确指示
            opened++;
            lines.Add($"[收尾窗口] ✅ {GrantScopes.Describe(scope)}（{item.Why}）");
        }

        lines.Insert(0, $"[收尾窗口] 已开：**静默放行 {opened} 条**（会话级、易失；收尾结束即撤销）。");
        lines.Add("[收尾窗口] 边界（v13）：runtime **不再拦截任何动作** —— 本该问人的一律**留档 + 放行**；"
                  + "危险动作由**远端 AI 在终局前自判**，用语言问你（见 docs/DESIGN-APPROVAL-V13.md）。");
        lines.Add("[收尾窗口] 随时可核：`/grants` 看放行了什么；`/session --check` 看账目；不满意 ⇒ `/grants-clear` 一键收窄。");
        return lines;
    }

    /// <summary>关闭窗口（收尾结束 / 进入新会话：把范围授权撤销）。</summary>
    public static int Close(SecurityGateway? gateway) => gateway?.Grants.ClearScopes() ?? 0;
}
