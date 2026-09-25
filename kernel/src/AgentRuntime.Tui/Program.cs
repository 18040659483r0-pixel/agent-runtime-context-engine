using System.Globalization;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;
using AgentRuntime.Core.Session;
using AgentRuntime.Presentation;

namespace AgentRuntime.Tui;

/// <summary>
/// **Runtime TUI —— AgentRuntime 的第一个宿主实现 + 观测台**。
/// <para>
/// 形态（MVP = 方案 A）：**REPL + 面板命令**，零第三方库、跨平台一致、输出全是纯文本
/// （可落盘、可 diff、可进实验记录）。
/// </para>
/// <code>
/// agent-runtime > 把这轮的部署代号确认一下
/// [mock] 收到：把这轮的部署代号确认一下
///   └ 本轮 prompt 12 / cached 0 (0.0%) / 开销 0.4ms
/// agent-runtime /stack       ← 面板：区栈逐字节
/// agent-runtime /hits 4      ← 面板：最近 4 轮的命中账
/// </code>
/// <para>
/// 不变量（T1~T6，与 <c>docs/DESIGN-V4.4-TUI.md</c> §五 一一对应）：
/// 显示不改字节 · 不是注入源 · 输出可复算 · 配置只读 · 快照纪律不变 · 协议区不可摘。
/// </para>
/// <para>
/// **零成本演示**：配 <c>tools/mock-provider</c> 即可端到端跑通（不要密钥、不花钱）。
/// </para>
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitProvider = 3;

    private static async Task<int> Main(string[] args)
    {
        var options = TuiOptions.Parse(args);

        if (options.Help)
        {
            PrintUsage();
            return ExitOk;
        }

        try
        {
            var configPath = options.Config ?? RuntimeHost.LocateDefaultConfig();
            var overrides = options.ToOverrides();
            var config = RuntimeHost.ResolveConfiguration(configPath, overrides);

            if (options.Rest.Count > 0)
            {
                Console.Error.WriteLine($"[tui] 不认识的位置参数：{string.Join(" ", options.Rest)}（TUI 不收裸消息：起来之后一行一行输）");
                return ExitUsage;
            }

            var apiKey = config.ResolveApiKey();
            if (apiKey is null)
            {
                Console.Error.WriteLine(config.DescribeMissingApiKey());
                return ExitUsage;
            }

            // v13：工具面**不再有审批闸门**（主人 2026-09-21 定：放弃 runtime 的硬闸门）⇒
            // 本该问人的动作由 ToolRunner **留档 + 放行**，右侧授权区不再出现。
            // 自动接续的预算钟照旧（它只服务续跑，与审批无关）。
            var budget = new ContinuationBudget();

            using var host = RuntimeHost.Boot(config, overrides, apiKey);

            // 收尾检查：TUI 是长驻宿主，问「现在要不要收尾」会打断 REPL ⇒ 只报告（chat: true）。
            RuntimeHost.WarnPendingCloseout(config, host.Modules, chat: true, Console.Error);

            // 收尾窗口（always 模式）：本会话常开 —— 先开，再谈会话指针（顺序无所谓，但说清楚更好读）。
            foreach (var windowLine in SessionLifecycle.OpenWindowAtBoot(host))
            {
                Console.Error.WriteLine(windowLine);
            }

            // 会话指针：running ⇒ 已接上那一节；closed ⇒ **自动 start**（人不在场也接得上）。
            var bootLines = SessionLifecycle.StartAtBoot(host);
            foreach (var line in bootLines)
            {
                Console.Error.WriteLine(line);
            }

            return await RunAsync(host, configPath, options, budget);
        }
        catch (HostUsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitUsage;
        }
        catch (ModelClientException ex)
        {
            Console.Error.WriteLine($"[provider] {ex.Message}");
            if (!string.IsNullOrEmpty(ex.ResponseBody))
            {
                Console.Error.WriteLine($"[provider:body] {ex.ResponseBody}");
            }

            return ExitProvider;
        }
        catch (OperationCanceledException)
        {
            // 取消**不是崩溃**（v14 · 2026-09-22）：Ctrl-C 一下 = 中断当前轮（会话继续，在 `SplitSession` 里接住）；
            // 两下 = 退出，走 `CtrlCQuit`（先恢复终端，再以 130 退）。这里只兵底：保证**取消不会变成未捕获异常**。
            return 130;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"[config] {ex.Message}");
            return ExitUsage;
        }
        catch (Exception ex)
        {
            // **兜底：先落盘，再重抛**（2026-09-22 03:49 真机 SIGABRT 后加）。
            // 那次只留下 .ips（全是原生帧、**没有托管异常名与栈**），而 TUI 退出时恢复终端 ⇒ 屏上的
            // `Unhandled exception …` 一滚就没了，手里只剩「它崩了」。落盘后**行为不变**（照旧往上抛 ⇒ 退出码一致），
            // 只是现场多了一份：异常全文 + 配置 + **流末事件**。
            CrashDump.Write(ex, options.Config);
            Console.Error.WriteLine($"[tui] 未捕获异常已落盘：{CrashDump.Path}");
            Console.Error.WriteLine($"[tui] {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 按 <c>--ui</c> 与环境事实选界面：双栏（默认）还是纯文本（可落盘、可 diff）。
    /// <para>降级不静默：自动落回纯文本时会把理由打在 stderr 上。</para>
    /// </summary>
    private static async Task<int> RunAsync(
        RuntimeHost host,
        string configPath,
        TuiOptions options,
        ContinuationBudget budget)
    {
        var requestedSplit = UiPolicy.ParseRequest(options.Ui);
        var headless = options.SnapshotPath is not null;
        var (width, height) = UiPolicy.ProbeTerminalSize();
        var stdoutIsTty = !Console.IsOutputRedirected;

        // 呈现层：颜色策略（**唯一决策处**）—— 非 TTY / NO_COLOR / TERM=dumb / --color never ⇒ 不上色。
        // 色深：--color-depth 显式给，否则由 COLORTERM / TERM 判（256 色 ＝ 鲜艳档）。
        var style = StyleTable.For(
            StyleTable.ParseMode(options.Color),
            stdoutIsTty,
            Environment.GetEnvironmentVariable("NO_COLOR"),
            Environment.GetEnvironmentVariable("TERM"),
            Environment.GetEnvironmentVariable("COLORTERM"),
            StyleTable.ParseDepth(options.ColorDepth));

        var decision = UiPolicy.Decide(new UiPolicy.Environment(
            RequestsSplit: requestedSplit,
            HeadlessFrame: headless,
            StdoutIsTty: !Console.IsOutputRedirected,            StdinIsTty: !Console.IsInputRedirected,
            Width: width,
            Height: height));

        if (!decision.IsSplit)
        {
            if (requestedSplit)
            {
                // 只在「用户想要双栏但环境不答应」时说理由；显式 --ui plain 不刷屏。
                Console.Error.WriteLine($"[ui] {decision.Reason}");
            }

            return await RunPlainAsync(host, configPath, options.Verbose, options.Continuation, budget);
        }

        var ledger = new TurnLedger();
        var ablation = new AblationService();
        var router = new PanelRouter(host, ledger, ablation, style);
        // v12：把 TUI 的人类设置（就地展开那几项）落到 ~/.agentruntime/tui.json —— 下次拉起照旧。
        var session = new SplitSession(host, ledger, router, options.Verbose, options.Continuation, budget,
            new TuiStateStore(TuiStateStore.DefaultPath()), style);

        // v13：不再有审批面 / 授权区接线 —— 右侧那块区域整个消失（留档在事件流与账本里）。

        if (headless)
        {
            var (frameWidth, frameHeight) = UiPolicy.ParseFrameSize(options.FrameSize, options.Cols, options.Rows);
            return await session.RunHeadlessAsync(options.SnapshotPath!, frameWidth, frameHeight);
        }

        return await session.RunInteractiveAsync();
    }

    /// <summary>REPL：一行普通文本 = 一个 turn；<c>/</c> 开头 = 面板命令。</summary>
    private static async Task<int> RunPlainAsync(
        RuntimeHost host,
        string configPath,
        bool verbose,
        ContinuationSettings continuation,
        ContinuationBudget budget)
    {
        var ledger = new TurnLedger();
        var ablation = new AblationService();
        var router = new PanelRouter(host, ledger, ablation);

        // 纯文本模式同样走呈现层（**同一条投影**）：标记被吃掉 ⇒ 此处产物与双栏屏上正文逐字节同。
        var presenter = Presenter.PlainOnly;

        foreach (var line in Banner(host, configPath, verbose, continuation))
        {
            Console.Out.WriteLine(line);
        }

        while (true)
        {
            Console.Out.Write("agent-runtime > ");
            Console.Out.Flush();

            var line = Console.ReadLine();
            if (line is null)
            {
                break;
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (Console.IsInputRedirected)
            {
                // 非交互（管道 / 重定向）时回显输入行：让转录本身可读、可 diff（缺了它，只看得见输出看不见命令）。
                Console.Out.WriteLine(line);
            }

            if (line is "exit" or "quit" or "/quit")
            {
                break;
            }

            if (PanelRouter.IsCommand(line))
            {
                var outcome = await router.ExecuteAsync(line, Console.Error);
                foreach (var text in outcome.Lines)
                {
                    Console.Out.WriteLine(presenter.Plain(text));
                }

                Console.Out.Flush();
                continue;
            }

            // 已收尾待 start：不接受新轮次（与 split 模式同一条守卫）。
            if (host.SessionClosed)
            {
                Console.Out.WriteLine("[会话] 已收尾（待 start）：先 /start 开新会话，或 /session 看状态。");
                Console.Out.Flush();
                continue;
            }

            try
            {
                var cursor = host.StreamCount;
                var outcome = await router.RunTurnAsync(PanelRouter.Unescape(line), verbose);
                WritePlainOutcome(outcome, ledger);

                // 自动接续：工具结果落地就由宿主接着问（不用人打「继续」）；
                // 停下由模型决定（下一轮不再点工具），上限/预算只兜底。
                await TurnContinuation.RunAsync(
                    host,
                    cursor,
                    continuation,
                    budget,
                    step: token => router.ContinueTurnAsync(verbose, token),
                    onOutcome: outcome2 =>
                    {
                        WritePlainOutcome(outcome2, ledger);
                        return ValueTask.CompletedTask;
                    },
                    trace: Console.Error.WriteLine);
            }
            catch (ModelClientException ex)
            {
                Console.Error.WriteLine($"[provider] {ex.Message}");
            }
        }

        Console.Out.WriteLine("[退出] 已离开 Runtime TUI（状态在流 / 存储区 / 快照里原地保留）。");
        return ExitOk;
    }

    /// <summary>一轮的落地输出（**正常轮与续跑轮共用**，次序不变）：告警（stderr）→ 响应（stdout）→ 一轮小结 → 诊断（stderr）。</summary>
    private static void WritePlainOutcome(TurnOutcome outcome, TurnLedger ledger)
    {
        foreach (var text in outcome.StderrBeforeResponse)
        {
            Console.Error.WriteLine(text);
        }

        Console.Out.WriteLine(Presenter.PlainOnly.Plain(outcome.Result.Response));

        var record = ledger.Records[^1];
        Console.Out.WriteLine($"  └ {TurnLedger.Summary(record)}");
        Console.Out.Flush();

        foreach (var text in outcome.StderrAfterResponse)
        {
            Console.Error.WriteLine(text);
        }
    }

    private static IReadOnlyList<string> Banner(
        RuntimeHost host,
        string configPath,
        bool verbose,
        ContinuationSettings continuation)
    {
        var lines = new List<string>
        {
            "AgentRuntime TUI —— REPL + 面板（与 CLI 同一条内核路径；零第三方库）",
            $"[模块] {RuntimeHost.DescribeModules(host.Modules)}",
            $"[配置] {configPath}",
            $"[存储] 流 {Show(host.Config.Stream.Path)} / 快照 {Show(host.Config.Snapshot.Path)} / 白板 {Show(host.Config.CurrentTail.StorePath)} / 草稿 {Show(host.Config.DynamicDraft.StorePath)}",
            $"[续跑] 自动接续 {continuation.Describe()}（--no-auto-continue 可关；Ctrl-C 可中断）",
            $"[预算] {BudgetLine(host.Config)}",
            $"[会话] {(host.SessionClosed ? "已收尾、待 start（/start 开新会话）" : $"开中 · 第 {host.ResetCount} 次 reset / 第 {host.StartCount} 次 start")}" +
            (host.BootPointer is null ? string.Empty : $" · 指针：{host.BootPointer.Describe()}"),
            "[用法] 一行普通文本 = 跑一个 turn；/ 开头 = 面板；/help 看清单；exit 或 Ctrl-D 退出。",
        };

        if (verbose)
        {
            lines.Add("[用法] --verbose 已开：每轮诊断（token / 缓存 / 耗时）走 stderr。");
        }

        return lines;
    }

    private static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "（未配置）" : value;

    /// <summary>
    /// 横幅里的预算行：窗口是**模型事实**（配置 <c>contextWindow</c>）⇒ 配了才谈得上「达 20% 提议收尾」。
    /// <para>没配就明说「窗口未知 ⇒ 不提议」——否则这条静默缺失会让人以为机制没做。</para>
    /// </summary>
    private static string BudgetLine(RuntimeConfiguration config) =>
        config.ContextWindow > 0
            ? $"窗口 {config.ContextWindow} token；达窗口 {ContextBudget.ProposePercent:0}% 且任务已终局（终局块）⇒ 提议 /closeout + /reset"
            : "窗口未知（config.contextWindow = 0 或未配）⇒ **不提议收尾**（填上即开）";

    private static void PrintUsage()
    {
        Console.WriteLine("AgentRuntime TUI —— 双栏（左对话 / 右上下文）+ 纯文本模式。");
        Console.WriteLine();
        Console.WriteLine("用法：AgentRuntime.Tui [选项]");
        Console.WriteLine();
        Console.WriteLine("选项（与 CLI 同名者语义一致）：");
        Console.WriteLine("  --config <path>      配置文件（默认在当前目录 / 程序目录找 config.json）");
        Console.WriteLine("  --api-key-file <p>   密钥文件**路径**（只传路径；内容不进 argv / 日志；环境变量优先）");
        Console.WriteLine("  --bare               裸聊：仅协议区（R1-P）");
        Console.WriteLine("  --vacuum             真空模式：一条 system 都没有（消融对照专用）");
        Console.WriteLine("  --modules <a,b>      覆盖配置里的模块列表（空串 = 裸聊；协议区永远强制装配）");
        Console.WriteLine("  --domains <a,b>      拉起前选择专业领域（空串 = 全部加载）");
        Console.WriteLine("  --stream <path>      覆盖 config.stream.path（事件流文件，JSONL，只追加）");
        Console.WriteLine("  --no-snapshot        关闭「每轮成功后自动写快照」");
        Console.WriteLine("  --focus <E###,…>     显式覆盖焦点；--focus-clear 清空；--focus-policy report|explicit");
        Console.WriteLine("  --tail-report <p>    on|off：关掉白板自报（运行开关，不是改协议）");
        Console.WriteLine("  --draft-report <p>   on|off：关掉草稿自报（同构）");
        Console.WriteLine("  --verbose, -v        每轮诊断（token / 缓存 / 耗时）打到 stderr");
        Console.WriteLine("  --auto-continue <n>  自动接续的安全上限轮数（0 = 关；**TUI 默认开 25**）");
        Console.WriteLine("  --no-auto-continue   关掉自动接续（回到「一轮之后等你说话」）");
        Console.WriteLine("  --auto-budget <min>  自动接续的**经过时间**预算（分钟；0 = 不限；默认 30）");
        Console.WriteLine("  --ui split|plain     split（默认）= 自绘 ANSI 双栏；plain = REPL + 纯文本面板");
        Console.WriteLine("  --color auto|always|never  落屏上色策略（默认 auto：TTY 且非 NO_COLOR/TERM=dumb 才上色）");
        Console.WriteLine("  --color-depth auto|16|256|truecolor  色深（默认 auto；与 OpenClaw 侧同色，256/真彩都很温和）");
        Console.WriteLine("  --snapshot <file>    配合 --ui split：无头渲染**一帧**到文件（纯文本帧，可 diff / 可 CI 断言）");
        Console.WriteLine("  --frame-size WxH     帧尺寸（默认 96x30 = 笔记本半屏目标；80x24 也不垮）");
        Console.WriteLine("  --cols N --rows N    同上，分开写（与 --frame-size 可混用）");
        Console.WriteLine("  --help, -h           显示本帮助");
        Console.WriteLine();
        Console.WriteLine("几何：状态块固定右上角（≈窗口 1/6）· 右列上=状态下=详细 · 左列对话占 2/3 且新消息贴底。");
        Console.WriteLine("自动降级（保证可管道 / 可记录）：宽 <72 列、高 <20 行、或 stdout/stdin 不是 TTY ⇒ 落回 plain。");
        Console.WriteLine();
        Console.WriteLine("双栏按键：");
        Console.WriteLine("  Tab            左 / 右栏焦点（左=输入行与对话流，右=上下文区栈）");
        Console.WriteLine("  ↑↓ / PgUp PgDn 滚动（左栏输对话流；右栏移选中项 / 滚展开正文）");
        Console.WriteLine("  Enter / →      就地展开 / 收起选中区（正文摊在它自己那一行下面；设置记在 ~/.agentruntime/tui.json）");
        Console.WriteLine("  ←              同上（就地展开 / 收起）");
        Console.WriteLine("  Esc            清除面板输出（**不改**就地展开的设置）");
        Console.WriteLine("  （v13 起**没有审批面**：本该问人的动作只留档、照样跑，屏上不再出现 y/N 询问）");
        Console.WriteLine("  /xxx           面板命令（输出进右栏）；/quit 或 Ctrl+Q 退出");
        Console.WriteLine();
        Console.WriteLine("会话内面板（纯文本；详见 /help）：");
        Console.WriteLine("  /stack [dump <file>] 区栈逐字节（字节 / sha256 前 12 位 / 版本 / 次序）");
        Console.WriteLine("  /hits [n]            最近 n 轮的 prompt / cached / uncached / 命中率 / 块余数 / 开销");
        Console.WriteLine("  /tail · /draft       白板 / 草稿全文 + 来源 + 上限余量 + 存储路径（带参数 = 走既有写入口）");
        Console.WriteLine("  /session             会话状态：开中 / 已收尾待 start + 第几次 reset/start + 末态摘要");
        Console.WriteLine("  /closeout            收尾：校验前缀 → 上报未收敛项 → 推进水位线 → **定格末态**（不调模型、不改语料）");
        Console.WriteLine("  /reset               结束本会话：收尾 + 定格末态 + 旧流原地归档（历史清零；下一步 /start）");
        Console.WriteLine("  /start               开新会话：新空事件流，白板/草稿从上一份末态开始");
        Console.WriteLine("  /focus · /snapshot · /resume · /ablate · /modules · /help · /quit");
        Console.WriteLine();
        Console.WriteLine($"可用模块：{string.Join("、", RuntimeConfiguration.KnownModules)}（protocol 不可摘）");
        Console.WriteLine("零成本演示：| python3 tools/mock-provider/mock_server.py + --config src/AgentRuntime.Tui/config.demo.json");
    }
}
