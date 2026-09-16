using AgentRuntime.Core;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Modules;

namespace AgentRuntime.Tui;

/// <summary>面板执行结果：屏上行 + 是否出错（出错也走纯文本面，不抛到 REPL 外层）。</summary>
public sealed record PanelOutcome(bool IsError, IReadOnlyList<string> Lines);

/// <summary>
/// **面板命令路由器** —— 所有 <c>/</c> 开头的行都从这里走。
/// <para>
/// 边界铁则（T2）：本类**不产生新的 prompt 段**，只做三件事：
/// ① 调既有面板（<c>Hosting.Panels</c>，与 CLI 的 <c>--xxx-show</c> 同一个函数）；
/// ② 调既有写入口（<c>--tail</c> / <c>--draft</c> 同一函数）；
/// ③ 调既有的内核守卫（<c>ModuleRegistry.EnsureProtocolPresent</c>）。
/// 屏面上的一切都是**纯文本**：可落盘、可 diff（T3）。
/// </para>
/// </summary>
public sealed class PanelRouter
{
    private readonly RuntimeHost _host;
    private readonly TurnLedger _ledger;
    private readonly AblationService _ablation;

    public PanelRouter(RuntimeHost host, TurnLedger ledger, AblationService ablation)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _ablation = ablation ?? throw new ArgumentNullException(nameof(ablation));
    }

    /// <summary>是不是面板命令（<c>/</c> 开头）。</summary>
    public static bool IsCommand(string line) =>
        !string.IsNullOrEmpty(line) && line.TrimStart().StartsWith('/');

    /// <summary>
    /// **跑一轮对话**（普通文本行的唯一入口）：与 CLI 同一条内核路径 + 记一笔命中账。
    /// <para>账本由本类持有：面板不记账，只有真实发生的轮次进账（P2 的可信度就建立在这一点上）。</para>
    /// </summary>
    public async Task<TurnOutcome> RunTurnAsync(string message, bool verbose = false, CancellationToken cancellationToken = default)
    {
        var outcome = await _host.RunTurnAsync(message, verbose, labelTurn: true, cancellationToken).ConfigureAwait(false);
        _ledger.Add(outcome.Result, _host.Turn);
        return outcome;
    }

    /// <summary>执行一条面板命令（纯文本输出；出错不抛，返回 <see cref="PanelOutcome.IsError"/>）。</summary>
    public async Task<PanelOutcome> ExecuteAsync(string commandLine, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stderr);

        var parts = commandLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return Error("空命令。用 /help 看可用面板。");
        }

        var head = parts[0].ToLowerInvariant();
        var args = parts.Skip(1).ToArray();

        try
        {
            switch (head)
            {
                case "/help" or "/?":
                    return Ok(HelpLines());

                case "/stack":
                    return Ok(await StackAsync(args, cancellationToken).ConfigureAwait(false));

                case "/hits":
                    return Ok(_ledger.Render(ParseCount(args)));

                case "/tail":
                    return args.Length == 0
                        ? Ok(TailPanel.Render(_host.Config))
                        : Ok([$"[尾部] {TailPanel.Override(_host.Config, ArgumentText(args), LiveTail())}"]);

                case "/tail-clear":
                    return Ok([$"[尾部] {TailPanel.Override(_host.Config, null, LiveTail())}"]);

                case "/draft":
                    return args.Length == 0
                        ? Ok(DraftPanel.Render(_host.Config))
                        : Ok([$"[草稿] {DraftPanel.Override(_host.Config, ArgumentText(args), LiveDraft())}"]);

                case "/draft-clear":
                    return Ok([$"[草稿] {DraftPanel.Override(_host.Config, null, LiveDraft())}"]);

                case "/focus":
                    return Ok(FocusPanel.Render(_host.Config));

                case "/snapshot":
                {
                    var snapshot = _host.WriteSnapshotOnce();
                    var lines = new List<string> { $"[快照] 已写入：{_host.Config.Snapshot.Path}" };
                    lines.AddRange(SnapshotPanel.Describe(_host.Config, snapshot));
                    return Ok(lines);
                }

                case "/resume":
                    if (args.Length == 0)
                    {
                        return Error("/resume 需要快照路径：/resume <快照文件> [--fork <新流文件>]");
                    }

                    return Ok(Resume(args, stderr));

                case "/fork":
                    if (args.Length < 2)
                    {
                        return Error("/fork 需要快照路径与新流文件：/fork <快照文件> <新流文件>");
                    }

                    return Ok(Fork(args[0], args[1], stderr));

                case "/ablate":
                    if (args.Length == 0)
                    {
                        return Error("/ablate 需要模块名：/ablate <模块> [on|off]（不带 on/off = 摘掉）");
                    }

                    return Ok(_ablation.Apply(_host, args[0], ParseOnOff(args.Length > 1 ? args[1] : null)));

                case "/modules":
                    return Ok(ModuleLines());

                default:
                    return Error($"未知面板命令：{head}（用 /help 看可用面板）");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Error(ex.Message);
        }
    }

    /// <summary>面板清单（也是「习惯映射」的索引：哪件事按哪个键）。</summary>
    public static IReadOnlyList<string> HelpLines() =>
    [
        "[帮助] 一行普通文本 = 跑一个 turn（与 CLI 同一条路径）；`/` 开头 = 面板命令。",
        "[帮助] /help                     本清单",
        "[帮助] /stack                    区栈逐字节（R1-P→R1→R2→R4→R5→R3：字节 / 指纹 / 版本 / 次序）",
        "[帮助] /stack dump <file>        把上表落盘（与屏上逐字节相同）",
        "[帮助] /hits [n]                 最近 n 轮：prompt / cached / uncached / 命中率 / 块余数 / 开销",
        "[帮助] /tail                     白板（R4）全文 + 来源 + 上限余量 + 存储路径",
        "[帮助] /tail <文本>              人工覆盖白板（**与 CLI 的 --tail 同一函数**）",
        "[帮助] /tail-clear               清空白板（擦白板不是删历史）",
        "[帮助] /draft                    草稿（R5）全文 + 来源 + 上限余量 + 存储路径",
        "[帮助] /draft <文本>             人工覆盖草稿（**与 CLI 的 --draft 同一函数**）",
        "[帮助] /draft-clear              清空草稿（清草稿不是删历史）",
        "[帮助] /focus                    语义焦点（R3）+ 权重表 + band + 缓存命中",
        "[帮助] /snapshot                 立即写一次恢复点并显示账本",
        "[帮助] /resume <path> [--fork <p>] 从快照恢复（孤儿尾部必须显式分叉）",
        "[帮助] /fork <快照> <新流>       只分叉不续写：把流在快照游标处复制到新文件（孤儿尾部处理）",
        "[帮助] /ablate <模块> [on|off]   会话内摘 / 挂模块（不写配置；protocol 不可摘）",
        "[帮助] /modules                  当前模块集",
        "[帮助] /quit                     退出（回 OpenClaw 主会话）",
        "[帮助] 不变量：面板只读刷新**不改**送进模型的 prompt 字节（T1）；写动作一律走既有写入口（T2）。",
    ];

    private async Task<IReadOnlyList<string>> StackAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            return await StackPanel.RenderAsync(_host.Modules, _host.NextContext, cancellationToken).ConfigureAwait(false);
        }

        if (args.Length >= 2 && string.Equals(args[0], "dump", StringComparison.OrdinalIgnoreCase))
        {
            var lines = await StackPanel
                .RenderAsync(_host.Modules, _host.NextContext, cancellationToken)
                .ConfigureAwait(false);
            var path = await StackPanel
                .DumpAsync(_host.Modules, _host.NextContext, args[1], cancellationToken)
                .ConfigureAwait(false);
            return [.. lines, $"[区栈] 已落盘：{path}（{lines.Count} 行，UTF-8 无 BOM）"];
        }

        return ["[错误] 用法：/stack 或 /stack dump <file>"];
    }

    private IReadOnlyList<string> Resume(string[] args, TextWriter stderr)
    {
        string? fork = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--fork", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                fork = args[++i];
            }
        }

        // 分叉会改写 config.Stream.Path ⇒ 必须先恢复、再按新流重建模块。
        var snapshot = ResumeSupport.ApplyResume(_host.Config, args[0], fork, stderr);
        _host.RebuildModules();
        ResumeSupport.ReportResume(snapshot, _host.Config, _host.Modules, stderr);

        return
        [
            "[恢复] 已按恢复点重建模块集（本会话后续轮次续写该流）。",
        ];
    }

    /// <summary>孤儿尾部处理入口（/fork）：只分叉、不续写（续写是 /resume 的事）。</summary>
    private IReadOnlyList<string> Fork(string snapshotPath, string forkPath, TextWriter stderr) =>
        ResumeSupport.ForkStream(_host.Config, snapshotPath, forkPath, stderr);

    private IReadOnlyList<string> ModuleLines()
    {
        var modules = _host.Modules;
        return
        [
            $"[模块] 当前装配：{(modules.Count == 0 ? "（无 · 真空）" : string.Join(", ", modules.Select(m => m.Name)))}",
            $"[模块] 标签：{RuntimeHost.DescribeModules(modules)}",
            $"[模块] 配置里的 modules：{(_host.Config.Modules.Count == 0 ? "（空 = 裸聊）" : string.Join(", ", _host.Config.Modules))}",
            $"[模块] 本会话已摘：{(_ablation.Disabled.Count == 0 ? "（无）" : string.Join("、", _ablation.Disabled))}",
        ];
    }

    /// <summary>
    /// 命令参数 → 文本（<c>/tail "a\nb"</c> / <c>/draft "a\nb"</c>）：
    /// ① 拼回一整串；② 去掉一层成对引号；③ 把字面 <c>\n</c> 当换行。
    /// <para>REPL 天生按行读，多行文本必须能在一行里表达出来 —— 否则白板 / 草稿每次只能写一行。</para>
    /// </summary>
    internal static string ArgumentText(IReadOnlyList<string> args)
    {
        var text = string.Join(' ', args);
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1];
        }

        return text.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\r", "\r", StringComparison.Ordinal);
    }

    /// <summary>引擎里那个活着的白板模块（没挂 = null）。</summary>
    private CurrentTailModule? LiveTail() => _host.Modules.OfType<CurrentTailModule>().FirstOrDefault();

    /// <summary>引擎里那个活着的草稿模块（没挂 = null）。</summary>
    private DynamicDraftModule? LiveDraft() => _host.Modules.OfType<DynamicDraftModule>().FirstOrDefault();

    private static int ParseCount(string[] args) =>
        args.Length > 0 && int.TryParse(args[0], out var n) ? n : 3;

    private static bool? ParseOnOff(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "on" or "true" or "1" or "yes" => true,
        "off" or "false" or "0" or "no" => false,
        _ => throw new InvalidDataException($"只支持 on|off，当前为 \"{value}\"。"),
    };

    private static PanelOutcome Ok(IReadOnlyList<string> lines) => new(false, lines);

    private static PanelOutcome Error(string message) => new(true, [$"[错误] {message}"]);
}
