using System.Globalization;
using AgentRuntime.Core;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Hosting;
using AgentRuntime.Hosting.Panels;

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

            // 工具面审批闸门（G2）：审批面进右栏详细块（交互模式）/ 退回 stderr（纯文本或非交互）。
            var approvalGate = TuiApprovalGate.ForConsole();
            using var host = RuntimeHost.Boot(config, overrides, apiKey, toolGate: approvalGate);

            // 收尾检查：TUI 是长驻宿主，问「现在要不要收尾」会打断 REPL ⇒ 只报告（chat: true）。
            RuntimeHost.WarnPendingCloseout(config, host.Modules, chat: true, Console.Error);

            return await RunAsync(host, configPath, options, approvalGate);
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
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"[config] {ex.Message}");
            return ExitUsage;
        }
    }

    /// <summary>
    /// 按 <c>--ui</c> 与环境事实选界面：双栏（默认）还是纯文本（可落盘、可 diff）。
    /// <para>降级不静默：自动落回纯文本时会把理由打在 stderr 上。</para>
    /// </summary>
    private static async Task<int> RunAsync(RuntimeHost host, string configPath, TuiOptions options, TuiApprovalGate approvalGate)
    {
        var requestedSplit = UiPolicy.ParseRequest(options.Ui);
        var headless = options.SnapshotPath is not null;
        var (width, height) = UiPolicy.ProbeTerminalSize();

        var decision = UiPolicy.Decide(new UiPolicy.Environment(
            RequestsSplit: requestedSplit,
            HeadlessFrame: headless,
            StdoutIsTty: !Console.IsOutputRedirected,
            StdinIsTty: !Console.IsInputRedirected,
            Width: width,
            Height: height));

        if (!decision.IsSplit)
        {
            if (requestedSplit)
            {
                // 只在「用户想要双栏但环境不答应」时说理由；显式 --ui plain 不刷屏。
                Console.Error.WriteLine($"[ui] {decision.Reason}");
            }

            return await RunPlainAsync(host, configPath, options.Verbose);
        }

        var ledger = new TurnLedger();
        var ablation = new AblationService();
        var router = new PanelRouter(host, ledger, ablation);
        var session = new SplitSession(host, ledger, router, options.Verbose);

        // 审批面挂到右栏详细块（Present/Clear 都在会话上；闸门只负责取键）。
        approvalGate.Present = session.ShowApproval;
        approvalGate.Clear = session.ClearApproval;

        // S3：固定**授权区**（批量 / 逐项）。长内容需 Enter 展开后才能批准 —— 规则在内核，UI 只负责画。
        approvalGate.ShowBatch = session.ShowApprovalBatch;
        approvalGate.ClearBatch = session.ClearApproval;

        if (headless)
        {
            var (frameWidth, frameHeight) = UiPolicy.ParseFrameSize(options.FrameSize, options.Cols, options.Rows);
            return await session.RunHeadlessAsync(options.SnapshotPath!, frameWidth, frameHeight);
        }

        return await session.RunInteractiveAsync();
    }

    /// <summary>REPL：一行普通文本 = 一个 turn；<c>/</c> 开头 = 面板命令。</summary>
    private static async Task<int> RunPlainAsync(RuntimeHost host, string configPath, bool verbose)
    {
        var ledger = new TurnLedger();
        var ablation = new AblationService();
        var router = new PanelRouter(host, ledger, ablation);

        foreach (var line in Banner(host, configPath, verbose))
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
                    Console.Out.WriteLine(text);
                }

                Console.Out.Flush();
                continue;
            }

            try
            {
                var outcome = await router.RunTurnAsync(line, verbose);

                // 与 CLI 同次序：告警（stderr）→ 响应（stdout）→ 一轮小结 → 诊断（stderr）。
                foreach (var text in outcome.StderrBeforeResponse)
                {
                    Console.Error.WriteLine(text);
                }

                Console.Out.WriteLine(outcome.Result.Response);

                var record = ledger.Records[^1];
                Console.Out.WriteLine($"  └ {TurnLedger.Summary(record)}");
                Console.Out.Flush();

                foreach (var text in outcome.StderrAfterResponse)
                {
                    Console.Error.WriteLine(text);
                }
            }
            catch (ModelClientException ex)
            {
                Console.Error.WriteLine($"[provider] {ex.Message}");
            }
        }

        Console.Out.WriteLine("[退出] 已离开 Runtime TUI（状态在流 / 存储区 / 快照里原地保留）。");
        return ExitOk;
    }

    private static IReadOnlyList<string> Banner(RuntimeHost host, string configPath, bool verbose)
    {
        var lines = new List<string>
        {
            "AgentRuntime TUI —— REPL + 面板（与 CLI 同一条内核路径；零第三方库）",
            $"[模块] {RuntimeHost.DescribeModules(host.Modules)}",
            $"[配置] {configPath}",
            $"[存储] 流 {Show(host.Config.Stream.Path)} / 快照 {Show(host.Config.Snapshot.Path)} / 白板 {Show(host.Config.CurrentTail.StorePath)} / 草稿 {Show(host.Config.DynamicDraft.StorePath)}",
            "[用法] 一行普通文本 = 跑一个 turn；/ 开头 = 面板；/help 看清单；exit 或 Ctrl-D 退出。",
        };

        if (verbose)
        {
            lines.Add("[用法] --verbose 已开：每轮诊断（token / 缓存 / 耗时）走 stderr。");
        }

        return lines;
    }

    private static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "（未配置）" : value;

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
        Console.WriteLine("  --ui split|plain     split（默认）= 自绘 ANSI 双栏；plain = REPL + 纯文本面板");
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
        Console.WriteLine("  Enter / →      展开选中区的正文（再按一次收起）");
        Console.WriteLine("  Esc            收起展开正文 / 清除面板输出");
        Console.WriteLine("  /xxx           面板命令（输出进右栏）；/quit 或 Ctrl+Q 退出");
        Console.WriteLine();
        Console.WriteLine("会话内面板（纯文本；详见 /help）：");
        Console.WriteLine("  /stack [dump <file>] 区栈逐字节（字节 / sha256 前 12 位 / 版本 / 次序）");
        Console.WriteLine("  /hits [n]            最近 n 轮的 prompt / cached / uncached / 命中率 / 块余数 / 开销");
        Console.WriteLine("  /tail · /draft       白板 / 草稿全文 + 来源 + 上限余量 + 存储路径（带参数 = 走既有写入口）");
        Console.WriteLine("  /focus · /snapshot · /resume · /ablate · /modules · /help · /quit");
        Console.WriteLine();
        Console.WriteLine($"可用模块：{string.Join("、", RuntimeConfiguration.KnownModules)}（protocol 不可摘）");
        Console.WriteLine("零成本演示：| python3 tools/mock-provider/mock_server.py + --config src/AgentRuntime.Tui/config.demo.json");
    }
}
