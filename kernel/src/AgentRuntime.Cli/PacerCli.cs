using System.Globalization;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Worker;
using AgentRuntime.Models;
using AgentRuntime.Providers;
using AgentRuntime.Hosting.Worker;

namespace AgentRuntime.Cli;

/// <summary>
/// **`pacer` 子命令**（用户 2026-10-09 定稿）—— 节拍器的**校准与重置**面。
///
/// <list type="table">
/// <item><c>pacer show</c>：打账本（速率范围 / 中位 / 当前闸门）</item>
/// <item><c>pacer reset --why "换 provider"</c>：账本清零（**非破坏**：写一条带时刻与理由的标记，老样本不再进账）</item>
/// <item><c>pacer calib [--repeat 5]</c>：**初次全量校准** —— 标准用例 ×N ⇒ 派发中位 / tok/s 范围</item>
/// <item><c>pacer probe</c>：**临时快测** —— 只测一条最小 ask 的 tok/s（出 300s 问题的那一刻跑它）</item>
/// </list>
///
/// <para><b>诚实标注</b>：本机的 CLI 只带 **worker**（没有 master 在环）⇒ 它量到的是**派发级**（一次 worker 运行）。
/// **lifecycle**（一个用户级任务 = N 次派发）由用例表里的**声明派发数**×派发中位**估**出来，并会明写"估"。</para>
/// </summary>
public static class PacerCli
{
    /// <summary>标准用例（= 验收料 v6 前 4 条；声明派发数用于估算 lifecycle）。</summary>
    private static readonly (string Name, string Material, int Dispatches)[] Cases =
    [
        ("fact-read", "[PACE-CALIB] 我要 .stage1/acc/acc-<NONCE>.txt 的计量读数（字节/行数/sha256）。", 3),
        ("verbatim", "[PACE-CALIB] 我要 docs/PITFALLS.md 前三行的原文，逐字。", 2),
        ("locate", "[PACE-CALIB] 仓里哪些文件提到「收尾水位」？给我清单（地址 + 计数）。", 4),
        ("write-new", "[PACE-CALIB] 在 .stage1/acc/ 下新建唯一目录并写一个三行的 verify.txt（逐行：acc=write / nonce=<NONCE> / done）。", 3),
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        var sub = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "show";
        var cfg = Arg(args, "--config") ?? DefaultConfig();
        RuntimeConfiguration config;
        try
        {
            config = RuntimeConfiguration.Load(cfg);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[pacer] 读不到配置 {cfg}：{ex.Message}");
            return 2;
        }

        // ⚠️ 配置里的**相对路径按配置文件所在目录**解析（与宿主同一口径）——不能按 cwd 兜。
        if (!string.IsNullOrWhiteSpace(config.Worker.Home) && !Path.IsPathRooted(config.Worker.Home))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(cfg)) ?? ".";
            config.Worker.Home = Path.GetFullPath(Path.Combine(dir, config.Worker.Home));
        }

        var root = WorkerPaths.Root(config.Worker);
        var provider = WorkerPaths.Provider(config.Worker);

        switch (sub)
        {
            case "show":
                return Show(root, provider, config);
            case "master":
                return Master(cfg, config, Arg(args, "--stream"));
            case "reset":
                var why = Arg(args, "--why") ?? "(未写理由)";
                Console.WriteLine($"[pacer] 已重置：{PaceLedger.MarkReset(root, provider, why)}");
                Console.WriteLine("[pacer] 老样本**一个字节没删**（只是不再进账）；下次 scan 起新账。");
                return 0;
            case "probe":
                return await ProbeAsync(root, provider, config);
            case "calib":
                return args.Contains("--with-master")
                    ? await CalibWithMasterAsync(cfg, config, (int)Num(args, "--repeat", 5))
                    : await CalibAsync(root, provider, config, (int)Num(args, "--repeat", 5));
            default:
                Console.Error.WriteLine("用法：pacer show | master [--stream <流文件>] | reset --why … | calib [--repeat N] | probe --config <path>");
                return 2;
        }
    }

    /// <summary>
    /// **master 侧此刻速率**（2026-10-10 用户令）：读 <c>&lt;流&gt;.usage.xml</c>（主 provider 的每轮 token/耗时）。
    /// <para><b>为什么要有它</b>：worker 有节拍账本（<c>pacer show</c>），而 master 侧只有一个**硬超时墙** ——
    /// 真机实测端点速率能 240 tok/s → **3 tok/s**（多轮 250~1330s）；墙太紧就会把会话打断，而「此刻多慢」**无处可查**。
    /// 判据可复算：每轮速率 = CompletionTokens / (TotalMs/1000)。</para>
    /// </summary>
    private static int Master(string cfg, RuntimeConfiguration config, string? streamArg)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(cfg)) ?? ".";
        var stream = streamArg
            ?? (string.IsNullOrWhiteSpace(config.Stream.Path) ? null : Path.GetFullPath(Path.Combine(dir, config.Stream.Path)));
        var ledger = stream is null ? null : stream + ".usage.xml";
        if (ledger is null || !File.Exists(ledger))
        {
            Console.Error.WriteLine($"[pacer] 读不到 master 用量账（{ledger ?? "(未配 stream)"}）—— 用 `--stream <流文件>` 指一个（或先跑一轮）。");
            return 2;
        }

        var text = File.ReadAllText(ledger);
        var matches = System.Text.RegularExpressions.Regex.Matches(text,
            @"<Messages>(\d+)</Messages><PromptTokens>(\d+)</PromptTokens><CachedTokens>(\d+)</CachedTokens><UncachedTokens>(\d+)</UncachedTokens><CompletionTokens>(\d+)</CompletionTokens><HitRate>([\d.]+)</HitRate><BlockRemainder>-?\d+</BlockRemainder><RuntimeOverheadMs>([\d.]+)</RuntimeOverheadMs><TotalMs>([\d.]+)</TotalMs>");
        if (matches.Count == 0)
        {
            Console.WriteLine("[pacer] master 账里没有轮次（还没跑过 / 账没落）。");
            return 0;
        }

        var rows = new List<(double Comp, double Ms)>();
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            rows.Add((double.Parse(m.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture),
                      double.Parse(m.Groups[8].Value, System.Globalization.CultureInfo.InvariantCulture)));
        }

        // 硬墙 = **声明的上界**（2026-10-15 起不再按账本收窄 —— 收窄会把正常但慢的一轮在中途砍掉）。
        // 「等得太久」改由 **5 分钟提示**承担（只看不砍，见 Tui/SplitSession.TurnWaitNoticeMs）。
        var wall = config.TimeoutSeconds;
        Console.WriteLine($"[pacer] master 账 = {ledger} · 轮数 {rows.Count} · **硬墙 = {wall}s**（声明上界）· 提示点 = {PaceWall.NoticeSeconds}s（只看不砍）");
        foreach (var (comp, ms) in rows.TakeLast(8))
        {
            var rate = comp / Math.Max(0.001, ms / 1000.0);
            Console.WriteLine($"   {comp,6:0} tok / {ms / 1000,7:0.0}s ⇒ {rate,6:0.0} tok/s" + (ms > wall * 1000 ? "   ⚠️ 超墙" : string.Empty));
        }

        var over = rows.Count(r => r.Ms > wall * 1000);
        Console.WriteLine($"[pacer] 超过 {wall}s 墙的轮：{over}/{rows.Count}"
                          + (over > 0 ? " ⇒ 抬高 timeoutSeconds（或查端点慢时段 —— 它是**慢**，不是不可达）" : string.Empty));
        return 0;
    }

    private static int Show(string root, string provider, RuntimeConfiguration config)
    {
        var b = PaceLedger.Scan(root, provider);
        Console.WriteLine($"[pacer] 端点 = {provider} · 家 = {root}");
        if (b.Samples == 0)
        {
            Console.WriteLine("[pacer] 账本是空的（没跑过，或被 reset 过）⇒ 闸门 = 不掐表。");
            return 0;
        }

        Console.WriteLine($"[pacer] 样本 {b.Samples} · 派发中位 {b.MedianSeconds:0.0}s（轮数中位 {b.MedianTurns:0.0} · 输出中位 {b.MedianCompletion:0} tok · 未缓存中位 {b.MedianUncached:0} tok）");
        Console.WriteLine($"[pacer] 长跑档: prefill {b.PrefillTokPerSec:0} tok/s · decode {b.DecodeTokPerSec:0.0} tok/s · 每轮 {b.OverheadSeconds:0.00}s");
        Console.WriteLine($"[pacer] 近期档（最近 {b.RecentWindow} 次）: prefill {b.RecentPrefillTokPerSec:0} · decode {b.RecentDecodeTokPerSec:0.0} tok/s");
        var v = b.RecentDecodeTokPerSec > 0 ? b.RecentDecodeTokPerSec : b.DecodeTokPerSec;
        var budget = PaceBudget.Of(v, config.Worker.Pace.ConstantTokens, config.Worker.Pace.MinSeconds,
            config.Worker.Pace.MaxSeconds, config.Worker.Pace.HardBelowTokPerSec);
        Console.WriteLine($"[pacer] ⇒ 闸门 {budget.GateSeconds:0}s（{budget.Band}）· {budget.Note}");
        return 0;
    }

    private static async Task<int> ProbeAsync(string root, string provider, RuntimeConfiguration config)
    {
        var worker = BuildWorker(config);
        if (worker is null)
        {
            return 2;
        }

        Console.WriteLine("[pacer] probe：一条最小 ask（不跑整套用例）…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await worker.DispatchAsync(new WorkerRequest
        {
            Task = "只回一句：PACE-OK（不要调用任何工具）。",
            Base = Directory.GetCurrentDirectory(),
            Window = [],
        });
        sw.Stop();
        var tok = result.Usage.CompletionTokens;
        var secs = Math.Max(0.001, result.Usage.ElapsedMs / 1000.0);
        Console.WriteLine($"[pacer] probe 结果：{tok} tok / {secs:0.0}s ⇒ **{tok / secs:0.0} tok/s**（墙钟 {sw.Elapsed.TotalSeconds:0.0}s）");
        var budget = PaceBudget.Of(tok / secs, config.Worker.Pace.ConstantTokens, config.Worker.Pace.MinSeconds,
            config.Worker.Pace.MaxSeconds, config.Worker.Pace.HardBelowTokPerSec);
        Console.WriteLine($"[pacer] ⇒ 此刻闸门 {budget.GateSeconds:0}s（{budget.Band}）· {budget.Note}");
        return 0;
    }

    private static async Task<int> CalibAsync(string root, string provider, RuntimeConfiguration config, int repeat)
    {
        var worker = BuildWorker(config);
        if (worker is null)
        {
            return 2;
        }

        repeat = Math.Clamp(repeat, 1, 20);
        Console.WriteLine($"[pacer] calib：标准用例 ×{repeat}（每用例 = 声明的派发数用于**估** lifecycle）");
        Console.WriteLine("[pacer] ⚠️ 只带 worker ⇒ 下面量到的是**派发级**；lifecycle 一列是**估**（派发中位 × 声明派发数）。");

        var allTokens = new List<double>();
        var allSecs = new List<double>();
        var rows = new List<(string Name, double Median, int Dispatches, double TokPerSec)>();
        foreach (var (name, material, dispatches) in Cases)
        {
            var secs = new List<double>();
            var rates = new List<double>();
            for (var i = 0; i < repeat; i++)
            {
                var text = material.Replace("<NONCE>", Guid.NewGuid().ToString("N")[..12]);
                var r = await worker.DispatchAsync(new WorkerRequest { Task = text, Base = Directory.GetCurrentDirectory(), Window = [] });
                var s = Math.Max(0.001, r.Usage.ElapsedMs / 1000.0);
                secs.Add(s);
                if (r.Usage.CompletionTokens > 0)
                {
                    rates.Add(r.Usage.CompletionTokens / s);
                    allTokens.Add(r.Usage.CompletionTokens);
                    allSecs.Add(s);
                }
            }

            secs.Sort();
            var med = secs[secs.Count / 2];
            var rate = rates.Count > 0 ? rates.OrderBy(x => x).ElementAt(rates.Count / 2) : 0;
            rows.Add((name, med, dispatches, rate));
        }

        Console.WriteLine();
        Console.WriteLine("| 用例 | 派发中位 | 估 lifecycle | decode 中位 |");
        Console.WriteLine("|---|---|---|---|");
        foreach (var (name, med, dispatches, rate) in rows)
        {
            Console.WriteLine($"| {name} | {med:0.0}s | ~{med * dispatches:0}s | {rate:0.0} tok/s |");
        }

        var allRates = allTokens.Zip(allSecs, (t, s) => t / s).OrderBy(x => x).ToArray();
        if (allRates.Length > 0)
        {
            var vMed = allRates[allRates.Length / 2];
            Console.WriteLine();
            Console.WriteLine($"[pacer] tok/s 范围：min {allRates[0]:0.0} · 中位 {vMed:0.0} · max {allRates[^1]:0.0}");
            var budget = PaceBudget.Of(vMed, config.Worker.Pace.ConstantTokens, config.Worker.Pace.MinSeconds,
                config.Worker.Pace.MaxSeconds, config.Worker.Pace.HardBelowTokPerSec);
            Console.WriteLine($"[pacer] ⇒ 按中位速度，闸门 = {budget.GateSeconds:0}s（{budget.Band}）");
        }

        return 0;
    }

    /// <summary>
    /// **带 master 的校准**（`--with-master`）：跑**真 lifecycle** —— 复用 CLI 自己的 master 轮次
    /// （<c>Program.RunOnceAsync</c>，含自动接续），量「一个用例从发料到出终局」的墙钟。
    /// <para>这才是用户要的「**work task lifecycle**」口径；不带这个开关时量的是**派发级**（worker only）。</para>
    /// </summary>
    private static async Task<int> CalibWithMasterAsync(string cfgPath, RuntimeConfiguration config, int repeat)
    {
        repeat = Math.Clamp(repeat, 1, 10);
        var apiKey = config.ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.Error.WriteLine("[pacer] 主 provider 取不到 Key ⇒ --with-master 跑不了");
            return 2;
        }

        using var host = AgentRuntime.Hosting.RuntimeHost.Boot(config, new AgentRuntime.Hosting.HostOverrides(), apiKey);
        Console.WriteLine($"[pacer] calib --with-master：真 lifecycle（master 在环）×{repeat} —— 每个用例从发料到出终局计时");
        Console.WriteLine("⚠️ master 自己的模型耗时也算在内（用户等的就是它）。");

        foreach (var (name, material, _) in Cases)
        {
            var secs = new List<double>();
            for (var i = 0; i < repeat; i++)
            {
                var text = material.Replace("<NONCE>", Guid.NewGuid().ToString("N")[..12]);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await Program.RunOnceAsync(host, verbose: false, [text], autoContinue: 60).ConfigureAwait(false);
                sw.Stop();
                secs.Add(sw.Elapsed.TotalSeconds);
                Console.WriteLine($"[pacer]   {name} #{i + 1}: {sw.Elapsed.TotalSeconds:0.0}s");
            }

            secs.Sort();
            var med = secs[secs.Count / 2];
            Console.WriteLine($"[pacer] {name}: **lifecycle 中位 {med:0.0}s**（min {secs[0]:0.0} / max {secs[^1]:0.0}）");
        }

        return 0;
    }

    private static MinimalWorker? BuildWorker(RuntimeConfiguration config)
    {
        config.Worker.Validate();
        var key = ResolveWorkerKey(config.Worker.Provider);
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine($"[pacer] 取不到 worker Key（环境变量 {config.Worker.Provider.ApiKeyEnv} / 文件 {config.Worker.Provider.ApiKeyFile}）");
            return null;
        }

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(config.Worker.Provider.TimeoutSeconds) };
        var client = new OpenAICompatibleClient(http, new OpenAICompatibleOptions
        {
            BaseUrl = config.Worker.Provider.BaseUrl,
            ApiKey = key,
            TimeoutSeconds = config.Worker.Provider.TimeoutSeconds,
            ServiceTier = config.Worker.Provider.ServiceTier,
        });
        return new MinimalWorker(client, config.Worker);
    }

    /// <summary>worker 的 Key：环境变量 → 文件（**与 CLI/宿主同一口径**；绝不从配置正文读）。</summary>
    private static string? ResolveWorkerKey(WorkerProviderConfiguration p)
    {
        var env = Environment.GetEnvironmentVariable(p.ApiKeyEnv);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        if (string.IsNullOrWhiteSpace(p.ApiKeyFile))
        {
            return null;
        }

        var path = p.ApiKeyFile.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), p.ApiKeyFile[2..])
            : p.ApiKeyFile;
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static double Num(string[] args, string name, double fallback) =>
        double.TryParse(Arg(args, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static string DefaultConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentRuntime.slnx")))
        {
            dir = dir.Parent;
        }

        var guess = Path.Combine(dir?.FullName ?? Directory.GetCurrentDirectory(), "src", "AgentRuntime.Cli", "config.stage1.xml");
        return File.Exists(guess) ? guess : Path.Combine(Directory.GetCurrentDirectory(), "config.stage1.xml");
    }
}
