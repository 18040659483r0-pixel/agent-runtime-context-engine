using AgentRuntime.Core;
using AgentRuntime.Core.Frozen;
using AgentRuntime.Core.Lifecycle;
using AgentRuntime.Core.Security;
using AgentRuntime.Core.Stream;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Modules;
using AgentRuntime.Presentation;

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
    private readonly StyleTable? _style;

    public PanelRouter(RuntimeHost host, TurnLedger ledger, AblationService ablation, StyleTable? style = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _ablation = ablation ?? throw new ArgumentNullException(nameof(ablation));
        _style = style;
    }

    /// <summary>
    /// **命令清单（唯一声明处）** —— 只有**首个词命中这份名单**的行才算命令；其余一律当**普通文本**发给模型。
    /// <para>
    /// 为什么不再用「行首是 <c>/</c>」（主人 2026-09-22 22:2x 真机报）：他把一个绝对路径
    /// （<c>/tmp/Documents/…</c>）粘进输入区，整条被当成命令吃掉、没能发出去 ——
    /// **路径与命令长得一样**，所以判定要看**名字**，不能看第一个字符。
    /// </para>
    /// <para>要强制把一条以 <c>/</c> 开头的文本当文本发：写成 <c>//…</c>（见 <see cref="Unescape"/>）。</para>
    /// </summary>
    public static readonly IReadOnlySet<string> Commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "/help", "/?", "/stack", "/hits", "/closeout", "/reset", "/start", "/sessions", "/session",
        "/grants", "/grants-clear", "/strict", "/palette", "/trace", "/result", "/tail", "/tail-clear",
        "/draft", "/draft-clear", "/focus", "/snapshot", "/resume", "/fork", "/ablate", "/modules",
        "/decide", "/append", "/quit",
    };

    /// <summary>
    /// 是不是面板命令：**首个词必须是已登记的命令名**（而不再是「以 <c>/</c> 开头」）。
    /// <para><c>//…</c> 是转义（强制当文本）⇒ 不算命令；未登记的 <c>/foo</c> ⇒ 也当文本。</para>
    /// </summary>
    public static bool IsCommand(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '/' || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var end = trimmed.IndexOfAny([' ', '\t']);
        var head = end < 0 ? trimmed : trimmed[..end];
        return Commands.Contains(head);
    }

    /// <summary>
    /// **「强制当文本」的转义**：行首 <c>//</c> ⇒ 去掉一个斜杠，其余逐字保留（只在**文本路径**上调用）。
    /// <para>例：输入 <c>//help</c> 发出去的是 <c>/help</c> 这行字，不跑面板命令。</para>
    /// </summary>
    public static string Unescape(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) ? trimmed[1..] : line;
    }

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

    /// <summary>
    /// **续跑一轮**（无新用户输入）—— 与 <see cref="RunTurnAsync"/> 同一条内核路径，
    /// 只是不追加用户消息（用户没说话就不该有用户事件）。
    /// </summary>
    public async Task<TurnOutcome> ContinueTurnAsync(bool verbose = false, CancellationToken cancellationToken = default)
    {
        var outcome = await _host.ContinueTurnAsync(verbose, cancellationToken).ConfigureAwait(false);
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
                    return Ok([.. _ledger.Render(ParseCount(args)), _host.LastBudget.Describe()]);

                case "/closeout":
                    return Ok(CloseoutLines());

                case "/reset":
                {
                    // **唯一的停顿点**（主人 2026-09-20 14:5x）：收尾一气呵成跑完，只在这里问一次
                    // 「reset + 自动 start（一气呵成）」还是「只 reset」——除了它，收尾全过程不再问人。
                    var report = SessionLifecycle.Reset(_host, DateTimeOffset.Now, LifecycleUsages());
                    List<string> lines = [.. report.Closeout.Lines(_host.Config.Frozen.Watermark), .. report.Lines()];

                    if (args.Length > 0 && string.Equals(args[0], "--start", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.AddRange(SessionLifecycle.Start(_host, DateTimeOffset.Now).Lines());
                    }
                    else
                    {
                        lines.Add("[收尾] 唯一停顿点（二选一，回一句即可）：**/reset --start**＝重开后自动开新会话（一气呵成）｜ **/start** ＝现在开新会话。");
                    }

                    return Ok(lines);
                }

                case "/start":
                    return Ok(SessionLifecycle.Start(_host, DateTimeOffset.Now).Lines());

                case "/sessions":
                    return Ok(VolumeLines());

                case "/session":
                    return Ok([.. SessionLines(), .. (args.Length > 0 && string.Equals(args[0], "--check", StringComparison.OrdinalIgnoreCase)
                        ? SessionLifecycle.Check(_host)
                        : ["[会话] 想核对指针/水位线/末态三者账目：/session --check"])]);

                case "/grants":
                    return Ok(GrantLines());

                case "/grants-clear":
                    return Ok(ClearGrants());

                case "/strict":
                    return Ok(StrictLines(args));

                case "/palette":
                    return Ok(PaletteLines());

                case "/trace":
                    return Ok(TraceLines());

                case "/append":
                    // 累积显示由 **TUI 会话**持有（它渲染卡）：这里只说明；真开关在 <c>SplitSession</c>。
                    return Ok(["[屏面] 累积显示（意图 / 打算 / 结果逐条追加，默认开）由 TUI 会话持有：/append on|off"]);

                case "/result":
                    return Ok(ResultLines());

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
        "[帮助] 一行普通文本 = 跑一个 turn（与 CLI 同一条路径）；**只有首个词命中下面这份命令名单**才算命令，其余一律当文本（绝对路径也算文本）；`//` 开头 = 强制当文本。",
        "[帮助] /help（同 /?）             本清单",
        "[帮助] /stack                    区栈逐字节（R1-P→R1→R2→R4→R5→R3：字节 / 指纹 / 版本 / 次序）",
        "[帮助] /stack dump <file>        把上表落盘（与屏上逐字节相同）",
        "[帮助] /hits [n]                 最近 n 轮：prompt / cached / uncached / 命中率 / 块余数 / 开销",
        "[帮助] /grants                    本会话授权清单（范围授权 / 一事一批 / 免问计数）",
        "[帮助] /grants-clear              一键撤销本会话全部授权（回到「每次重新留档」）",
        "[帮助] /strict [on|off]           严格模式：on ＝ 忽略 risk: 声明、非只读动作**逐条留档**（v13 起不再有「问人」这一步）/ off ＝ 恢复模型自判；不带参数 = 看当前",
        "[帮助] /palette                   呈现层预览：每个角色的样子 + 一张表格构件示例",
        "[帮助] /trace                     当前生命周期：完整执行轨迹（默认折叠的那一部分，一条不少）",
        "[帮助] /result                    当前生命周期：末轮正文原文（模型到底输出了什么）",
        "[帮助] /decide [n]                决策卡：不带参数 = 看选项（列到右栅详细块）；带参数 = 选第 n 条（发出去 = 那条选项原文）",
        "[帮助] /append [on|off]           累积显示：on ＝ 意图 / 打算 / 结果逐条追加（默认）；off ＝ 只显示最新一条",
        "[帮助] /tail                     白板（R4）全文 + 来源 + 上限余量 + 存储路径",
        "[帮助] /tail <文本>              人工覆盖白板（**与 CLI 的 --tail 同一函数**）",
        "[帮助] /tail-clear               清空白板（擦白板不是删历史）",
        "[帮助] /draft                    草稿（R5）全文 + 来源 + 上限余量 + 存储路径",
        "[帮助] /draft <文本>             人工覆盖草稿（**与 CLI 的 --draft 同一函数**）",
        "[帮助] /draft-clear              清空草稿（清草稿不是删历史）",
        "[帮助] /focus                    语义焦点（R3）+ 权重表 + band + 缓存命中",
        "[帮助] /closeout                 收尾：校验前缀 → 上报未收敛项（misc）→ 推进水位线（不调模型、不改语料）",
        "[帮助] /closeout [--auto]        收尾一气呵成：收尾窗口（静默放行）+ 收尾件 + 流水线命令 + 定格末态；末尾给唯一的二选一",
        "[帮助] /reset [--start]          结束本会话（收尾 + 归档 + 定格末态）；带 --start ＝**一气呵成**（reset 后立刻 start）",
        "[帮助] /start                     开新会话：新空事件流，**白板与草稿从上一份末态开始**（会话还在跑时会被拒：先 /reset）",
        "[帮助] /session [--check]        当前会话状态（+ --check：指针/水位线/末态三本账）",
        "[帮助] /sessions                  列出所有卷（旧卷原地保留；回某一卷给可照抄的命令）",
        "[帮助] /snapshot                 立即写一次恢复点并显示账本",
        "[帮助] /resume <path> [--fork <p>] 从快照恢复（孤儿尾部必须显式分叉）",
        "[帮助] /fork <快照> <新流>       只分叉不续写：把流在快照游标处复制到新文件（孤儿尾部处理）",
        "[帮助] /ablate <模块> [on|off]   会话内摘 / 挂模块（不写配置；protocol 不可摘）",
        "[帮助] /modules                  当前模块集",
        "[帮助] /quit                     退出（回 OpenClaw 主会话）",
        "[帮助] 键盘：Ctrl-C 一下 = 中断当前轮；**连按两下（2 秒内）= 退出**（自动恢复终端）；Ctrl-Q / /quit 也退。",
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

    /// <summary>
    /// <c>/sessions</c>：**列出所有卷**（每个会话一个事件流文件）—— 当前那卷标出来，回别的卷给可照抄的命令。
    /// <para>为什么要有它：收尾链条每转一圈就多一卷（旧卷全部原地保留）；没有清单，人会以为「历史丢了」。</para>
    /// </summary>
    private IReadOnlyList<string> VolumeLines()
    {
        var current = _host.Config.Stream.Path;
        var directory = string.IsNullOrWhiteSpace(current) ? null : Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return ["[卷] 本会话没落盘（未配 stream.path）⇒ 没有卷清单。"];
        }

        var files = Directory.EnumerateFiles(directory, "*.jsonl")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.LastWriteTime)
            .ToArray();

        var lines = new List<string>
        {
            $"[卷] 目录：{directory}（**旧卷全部原地保留**：收尾只归档，不删）",
            $"[卷] 共 {files.Length} 卷；当前：{Path.GetFileName(current!)}",
        };

        foreach (var file in files.Take(12))
        {
            var isCurrent = string.Equals(file.FullName, current, StringComparison.Ordinal);
            var isCorrupt = file.Name.Contains("corrupt", StringComparison.OrdinalIgnoreCase);
            lines.Add($"[卷]   {(isCurrent ? "▶" : " ")} {file.Name} · {file.Length / 1024} KB · {file.LastWriteTime:MM-dd HH:mm}" +
                      (isCorrupt ? "（已污染，保留待查）" : string.Empty));
        }

        lines.Add($"[卷] 回某一卷：重启时加 `--stream \"{(files.Length > 0 ? files[^1].FullName : "<卷路径>")}\"`（或 `whitebox --stream <卷>`）。");
        return lines;
    }

    /// <summary><c>/closeout</c>：收尾（含收尾窗口 + 流水线 + 收尾件），末尾给出**唯一的二选一**。</summary>
    private IReadOnlyList<string> CloseoutLines()
    {
        var lines = new List<string>(SessionLifecycle.Closeout(_host, DateTimeOffset.Now, lifecycles: LifecycleUsages()).Lines(_host.Config.Frozen.Watermark));
        lines.Add("[收尾] 唯一停顿点（二选一）：**/reset --start**＝结束本会话并立刻开新会话（一气呵成）｜ **/reset** ＝只结束、不自动开。");
        return lines;
    }

    /// <summary>
    /// <c>/session</c>：当前处于链条的哪一节（开中 / 已收尾待 start），以及末态摘要。
    /// <para>为什么要它：<c>reset</c> 与 <c>start</c> 之间是一个**真空档**（不接受轮次）——
    /// 状态必须一眼可查，否则「为什么它不回我话」会变成玄学。</para>
    /// </summary>
    private IReadOnlyList<string> SessionLines()
    {
        var lines = new List<string>
        {
            _host.SessionClosed
                ? "[会话] 状态：**已收尾、待 start**（不接受新轮次）—— 跑 /start 开新会话。"
                : "[会话] 状态：开中。",
            $"[会话] 第 {_host.ResetCount} 次 reset · 第 {_host.StartCount} 次 start · 轮次 {_host.Turn} · 事件流 {_host.StreamCount} 条",
            $"[会话] 事件流：{_host.Config.Stream.Path ?? "（未落盘，只存内存）"}",
            $"[会话] 会话标识：{_host.SessionId ?? "（无）"}",
        };

        var handover = _host.PendingHandover;
        if (handover is not null)
        {
            lines.Add($"[会话] 末态（start 的输入）：{handover.Describe()}");
        }
        else
        {
            var store = new HandoverStore(_host.Config.Lifecycle.Handover ?? HandoverStore.DefaultPath);
            var stored = store.TryLoad();
            lines.Add(stored is null
                ? $"[会话] 末态快照：无（{store.Path}）"
                : $"[会话] 末态快照（磁盘）：{stored.Describe()}");
        }

        return lines;
    }

    /// <summary>判定层（没装 tool 模块 ⇒ null）。</summary>
    private SecurityGateway? Gateway() =>
        _host.Modules.OfType<ToolModule>().FirstOrDefault()?.Runner.Security;

    /// <summary>
    /// 本会话的授权清单（<c>/grants</c>）—— **可见**才能谈得上「放心用它」。
    /// <para>三类分开列：范围授权（一次点头覆盖同类族）/ 一事一批 / 预授权；并给出免问与问人的计数。</para>
    /// </summary>
    private IReadOnlyList<string> GrantLines()
    {
        if (Gateway() is not { } gateway)
        {
            return ["[授权] 本会话没有工具面（未装 tool 模块 / 没有判定层）⇒ 没有授权清单。"];
        }

        var lines = new List<string>
        {
            "[授权] 会话授权 —— **进程结束即失效**（恢复 / 重启 / --fork 后重新问人）：",
            // v13：（判定层不再有阻断档）⇒「该问人 / 该拒」两个计数现在是**留档口径**（都放行）。
            // 旧文案写「问人 N 次 · 直接拒绝 N 次」是**骗人口径**（没人被问、也没人被拒）—— 2026-09-22 改。
            $"[授权] 免问 {gateway.AutoAllowed} 次 · 留档 {gateway.Asked + gateway.Denied} 次（原判「该问人」{gateway.Asked} / 「该拒」{gateway.Denied}；v13 起都不拦、不问，只记账）",
        };

        var scopes = gateway.Grants.Scopes;
        lines.Add($"[授权] 范围授权 {scopes.Count} 条（v13 起不再逐次问；这里是会话语义上的授权清单）：");
        foreach (var scope in scopes)
        {
            lines.Add($"[授权]   · {GrantScopes.Describe(scope)}");
        }

        var exact = gateway.Grants.Render(gateway.Now);
        lines.Add($"[授权] 一事一批 {exact.Count} 条（每次点头只覆盖它自己）：");
        foreach (var line in exact)
        {
            lines.Add($"[授权]   · {line}");
        }

        return lines;
    }

    /// <summary>一键撤销本会话全部授权（<c>/grants-clear</c>）—— 撤销不是禁用，只是回到「每次重新留档」。</summary>
    private IReadOnlyList<string> ClearGrants()
    {
        if (Gateway() is not { } gateway)
        {
            return ["[授权] 本会话没有工具面 ⇒ 没有可撤销的授权。"];
        }

        var scopes = gateway.Grants.ClearScopes();
        var before = gateway.Grants.Count;
        gateway.Grants.Clear();

        return
        [
            $"[授权] 已撤销本会话全部授权：范围 {scopes} 条 + 一事一批 {before} 条。",
            "[授权] v13 起没有「问人」这一步了：撤销后下一次同类动作**重新留档**（记一条新账），工具本身不禁用。",
        ];
    }

    /// <summary>
    /// <c>/strict [on|off]</c>：**一键回到「每条行动都留档」**（<c>docs/DESIGN-APPROVAL-V12.md</c> §五·2 的收回手段；**不用改配置**）。
    /// <para>
    /// <b>开</b> ＝ 判定层**忽略模型的 <c>risk:</c> 声明**（等价于未声明 ⇒ 按旧分类逐条走判定层，结论一律**留档**）；
    /// <b>关</b> ＝ 恢复「模型自判 <c>none</c> 即免问」（协议 v12 的默认口径）。
    /// 硬红线与声明无关，开关不动它；**已授权的一事一批 / 范围授权也不动** —— 要一并收窄用 <c>/grants-clear</c>。
    /// </para>
    /// <para>不带参数 = **只读**看一眼当前是严还是自判（不切）。与别的面板命令一样：不产生 prompt 段（T2）。</para>
    /// </summary>
    private IReadOnlyList<string> StrictLines(string[] args)
    {
        if (Gateway() is not { } gateway)
        {
            return ["[严格] 本会话没有工具面（未装 tool 模块 / 没有判定层）⇒ 没有可切换的判定。"];
        }

        if (args.Length == 0)
        {
            return
            [
                $"[严格] 当前：{(gateway.StrictMode ? "**开**（忽略 risk: 声明，非只读动作逐条**留档**）" : "**关**（模型自判 risk: none 即免问，仍留档判定层标记的专用目标 / 凭据类）")}。",
                "[严格] 切换：/strict on ＝ 回到「每条行动都留档」｜ /strict off ＝ 恢复模型自判。",
            ];
        }

        var on = ParseOnOff(args[0])!.Value;   // 非法值在 ParseOnOff 里抛（ExecuteAsync 转成错误行，状态不变）
        gateway.StrictMode = on;

        return on
            ?
            [
                "[严格] 已开：判定层**忽略模型的 risk: 声明** ⇒ 非只读动作全部回到「逐条留档」（等价于未声明；v13 起没有「问人」这一步，屏上不会出现 y/n）。",
                "[严格] v13 起**没有任何硬拦截**（受保护目标 / 提权 / 凭据 / 发布不可逆 / 可疑可执行一律只留档）；本开关只决定「模型的 risk: 声明是否被采纳」。已授权条目**仍在** —— 要一并收回用 /grants-clear。",
                "[严格] 会话级开关：进程结束即失效，**不改配置**；/strict off 恢复模型自判。",
            ]
            :
            [
                "[严格] 已关：恢复「模型自判 risk: none 即免问」（协议 v12 的默认口径）。",
                "[严格] 想再收紧：/strict on ｜ 想收回已授权：/grants-clear。",
            ];
    }

    /// <summary>
    /// <c>/palette</c>：**呈现层预览** —— 当前配色 + 每个角色的样子 + 一张表格构件示例。
    /// <para>用途：① 上色改得对不对，一眼看得出来（不用翻源码）；② 本身就是「标记写法」的活文档。</para>
    /// </summary>
    /// <summary>
    /// `/trace`：当前生命周期（最近一条用户消息引发的那个）的**完整执行轨迹**。
    /// <para>折叠是屏面的事、不是事实的事 —— 展开就是把**同一条投影**换成不折叠的那一种（同一个 Presenter）。</para>
    /// </summary>
    private IReadOnlyList<string> TraceLines()
    {
        var lifecycle = CurrentLifecycle();
        return lifecycle is null
            ? ["[轨迹] 还没有用户消息 —— 先打一句普通文本。"]
            : LifecyclePresenter.Card(lifecycle, expanded: true);
    }

    /// <summary>`/result`：当前生命周期的**末轮正文原文**（逐字，不做二次加工）。</summary>
    private IReadOnlyList<string> ResultLines()
    {
        var lifecycle = CurrentLifecycle();
        if (lifecycle is null)
        {
            return ["[结果] 还没有用户消息 —— 先打一句普通文本。"];
        }

        var last = lifecycle.Trace.LastOrDefault(static e => e.Kind == SessionEventKind.AgentOutput);
        if (last is null)
        {
            return ["[结果] 这一张卡还没有模型输出（模型还没说话，或本轮被取消）。"];
        }

        return
        [
            $"[结果] 生命周期 #{lifecycle.Id} 末轮正文（原文逐字；自报块也在里面 —— 那是「它到底输出了什么」的凭据）",
            string.Empty,
            .. last.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'),
        ];
    }

    /// <summary>宿主账本 → 聚合器要的每轮用量（位置对应第 i 个 <c>AgentOutput</c>；收尾摘要也要它）。</summary>
    private IReadOnlyList<LifecycleTurnUsage> LifecycleUsages() =>
        LifecycleAggregator.Usages(
            _ledger.Records.Select(static r => (r.PromptTokens, r.CachedTokens, r.CompletionTokens, r.TotalMs)));

    /// <summary>当前生命周期（投影自事件流 + 宿主的每轮账；没有则为空）。
    /// <para>⚠️ 走 <c>Aggregate(stream)</c>（内部 <c>Snapshot()</c>）—— 与帧渲染路径同一口径（2026-09-22 23:28 的 SIGABRT）。</para></summary>
    private TaskLifecycle? CurrentLifecycle()
    {
        var stream = _host.Modules.OfType<AppendStreamModule>().FirstOrDefault()?.Stream;
        return stream is null ? null : LifecycleAggregator.Aggregate(stream, LifecycleUsages()).Current;
    }

    private IReadOnlyList<string> PaletteLines()
    {
        // 表格里的「已做 / 待做」用**显式角色**（[[done]] / [[todo]]）—— 面板输出是字符串，
        // 角色靠标记穿过这条通道（ToMarkup 往返），这也是「表格也能上色」的那条路。
        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "呈现 IR + 样式表 + 标记 + 自动标注", "[[done]]已做[[/]]", "`Presentation/Markup.cs`" },
            new[] { "落屏上色（唯一上色处）", "[[done]]已做[[/]]", "`AnsiScreen.Draw`" },
            new[] { "面板 / 表格走结构化角色", "[[todo]]待做[[/]]", "`PanelOutcome` 改富文本" },
        };

        var style = _style ?? StyleTable.Standard;
        var lines = new List<string>
        {
            $"**呈现层预览** · 本次配色：{style.Describe()}（`--color` / `--color-depth` 可改）",
            $"**色卡**（与 OpenClaw 侧同色，十六进制原件）：专名 {StyleTable.HexOf(StyleRole.Noun)} · 路径 {StyleTable.HexOf(StyleRole.Path)} · 注意行 {StyleTable.HexOf(StyleRole.Attention)} · 已做 {StyleTable.HexOf(StyleRole.Done)} · 待做 {StyleTable.HexOf(StyleRole.Todo)} · 错误 {StyleTable.HexOf(StyleRole.Warn)} · 次要 {StyleTable.HexOf(StyleRole.Dim)}",
            "",
            "**角色一览**（每个角色一行，就是屏幕上真实的样子）：",
            "· [[noun]]专名（Noun）[[/]]　[[path]]文件地址 / 路径（Path）：`docs/DESIGN-PRESENTATION.md` · /tmp/wb-demo.txt[[/]]",
            "> 蓝色一整行（Attention）：行首写 > 加一个空格，整行就是这个色",
            "· [[done]]已做（Done）[[/]]　[[todo]]待做（Todo）[[/]]　[[warn]]错误 / 拒绝（Warn）[[/]]　[[dim]]次要注记（Dim）[[/]]　[[hint]]提示（Hint，海蓝）[[/]]",
            "· 加粗（Strong）：**整块覆盖**；自动标注出来的路径（黄）：docs/PITFALLS.md",
            "",
            "**表格构件**（做了什么 / 要做什么）：",
        };
        lines.AddRange(TextTable
            .Render(new[] { "事项", "状态", "证据" }, rows)
            .Select(static line => line.ToMarkup()));
        return lines;
    }

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
