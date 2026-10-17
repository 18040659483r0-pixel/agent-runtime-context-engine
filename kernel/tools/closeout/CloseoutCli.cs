using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Knowledge;
using AgentRuntime.Core.Persistence;

namespace CloseoutTool;

/// <summary>
/// **[WB] 收尾流水线（C# 版）** —— <c>tools/closeout.py</c> 的逐项等价移植（A1 旗舰刀）。
///
/// <para>把「收尾欠不欠账」变成**可随时跑、可自证**的机器判据。子命令：
/// <c>--closeout-check</c>（只读逐项重算）· <c>--closeout-json</c>（机器可读）·
/// <c>--closeout-apply</c>（幂等重生派生物）· <c>--closeout-close</c>（绿才写收敛点）·
/// <c>--closeout-where</c>（打印根目录与规范调用）。</para>
///
/// <para><b>设计要点（照搬 py）</b>：只做**确定性**的事（重跑闸门 / 重算计数 / 比对两副本）；判断归模型。
/// 根目录**自定位**（不依赖 cwd）· severity：block ⇒ 红则收尾不算收敛；report ⇒ 允许红但必须上账。</para>
/// </summary>
public static class CloseoutCli
{
    // ---------------- 根目录（自定位：dll 在 src/AgentRuntime.Cli/bin/<cfg>/net10.0/ 下 ⇒ 上溯 5 层 = 代码仓） ----------------
    public static readonly string Project =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public static readonly string Live = Path.Combine(Project, "whitebox", "workspace");

    private static readonly string Here = Path.Combine(Project, "tools");
    private static readonly string LedgerDir = Path.Combine(Here, "closeout");
    private static readonly string LedgerPath = Path.Combine(LedgerDir, "ledger.xml");
    private static readonly string PlacementFile = Path.Combine(LedgerDir, "gate-ledger.xml");

    private static readonly string PitfallsSrc = Path.Combine(Project, "docs", "PITFALLS.md");
    private static readonly string PitfallsIndex = Path.Combine(Project, "docs", "PITFALLS-INDEX.md");
    private static readonly string PitfallsPublic = Path.Combine(Project, "docs", "PITFALLS-public.md");

    /// <summary>
    /// **收尾的扩展环节**（用户 2026-10-16 12:2x 定）：CORE 对 VCS 零引用，版本库那几件事全在实现里。
    /// <para>`NoArchive` = 标准模式（不用任何 SVN/Git）；`SvnArchive` = 我们自己用的那个扩展选项。</para>
    /// <para>⚠️ 第 1 批**保持与旧行为逐字节一致**（选择仍按本仓根 `.svn` 推断）；第 3 批改显式配置。</para>
    /// </summary>
    /// <summary>**模式由声明决定**（`tools/closeout/archive.xml`）；没声明才按 `.svn` 推断，且会把「这是推断」说出来。</summary>
    internal static ArchiveSelection ArchiveSel => CloseoutArchiveFactory.Resolve();

    internal static ICloseoutArchive Vcs => ArchiveSel.Impl;

    /// <summary>**生效的模块集**（`tools/closeout/requirements/` 优先；没声明目录 ⇒ 内置默认）。
    /// <para>进程内算一次 —— 收尾是「一次调用一个进程」⇒ **改声明，下一轮即生效**（不重编、不重启）。</para></summary>
    internal static RequirementSet Modules { get; } = RequirementSet.Load();

    /// <summary>判据 severity：**声明优先**，没声明 ⇒ 代码默认（C 批）。</summary>
    internal static string SevOf(string checkId, string fallback) => Modules.SevOf(checkId) ?? fallback;

    /// <summary>判据的数值参数：**声明优先**，没声明 ⇒ 代码默认（C 批）。</summary>
    internal static double ParamOf(string checkId, string key, double fallback) =>
        double.TryParse(Modules.ParamOf(checkId, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    // **落点全部由扩展提供**：CORE 只知道自己写的**那份活版水位**；第二副本是扩展的事（`Sync()`）。
    private static string KnowledgeRoot => Vcs.KnowledgeRoot;

    private static string KnowledgeMd => Path.Combine(KnowledgeRoot, "knowledge.md");

    private static string WaterLevel => Vcs.WatermarkPath;

    /// <summary>CORE **自己写的**那一份水位（活版）。扩展要不要镜像出第二份，与 CORE 无关。</summary>
    private static readonly string WaterLive = Path.Combine(Live, "memory", "收尾水位.md");

    public const string ConvergeMark = "- **收敛点（机器可判）**：";
    private static readonly Regex ConvergeRe = new(@"- \*\*收敛点（机器可判）\*\*：存档 r(\d+)[^\n]*", RegexOptions.Compiled);

    private static readonly string SessionPointerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentruntime", "session.xml");

    /// <summary>收敛点之后允许多少个提交仍算「正常」（收尾自己会再提交水位+账本 ≈2 个）。</summary>
    private const int WaterGapTolerance = 2;

    private static double GateTimeout =>
        double.TryParse(Environment.GetEnvironmentVariable("CLOSEOUT_TIMEOUT"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 900;

    private static string SelfDll => Assembly.GetExecutingAssembly().Location;

    /// <summary>门闸的 C# 入口 = **AgentRuntime.Cli**（本工具只调它，不自调）；未构建 ⇒ fail-closed。</summary>
    private static readonly string CsDll =
        Path.Combine(Project, "src", "AgentRuntime.Cli", "bin", "Release", "net10.0", "AgentRuntime.Cli.dll");

    private const string CsBuildHint = "dotnet build -c Release src/AgentRuntime.Cli/AgentRuntime.Cli.csproj";

    private static readonly string[] Owns = ["水位收敛点"];

    public const string CloseOrder =
        "   收尾硬顺序（照做，不要自己推）：\n" +
        "     ① --check（列欠账）→ ② 写正文：**一律绝对路径**，写完立刻复核落点与字节\n" +
        "     ③ --apply（幂等重生派生物）→ ④ 提交（代码仓 + push 存档区）\n" +
        "     ⑤ --close（绿才写收敛点）→ ⑥ 再提交（水位 + 账本）→ ⑦ 再 --check\n" +
        "   ⚠️ 路径基准看 `--where`，**不看 shell 的 cd**（工具 cwd = 程序集目录，相对路径会全落空）。";

    private sealed record Gate(string Name, string Sev, string[] Cs, string Why);

    private static readonly Gate[] Gates =
    [
        new("坑集索引", "block", ["--pitfalls-index-check", PitfallsSrc], "源改 ⇒ 必须重跑索引（派生落后于源）"),
        new("公开坑集变体", "block", ["--pitfalls-public-check", PitfallsSrc], "公开变体必须与重算一致"),
        new("知识树", "report", ["--kn-check", Live],
            "知识树结构 + 派生层逐字节一致（只常驻 L1）；红 ⇒ 修活版并重建派生"),
        new("冻结语料", "report", ["--frozen-corpus", Live, "--frozen-corpus-check"],
            "用户 2026-09-23 令：语料 bump 类属记账项、不阻断收尾（但仍上账）"),
    ];

    // ================================================================ runner

    /// <summary>**仅自证用**：替换子进程执行（罐头 svn 测试）；非 null 时 <see cref="Run"/> 直接走它。</summary>
    internal static Func<string, string[], string?, int, (int Rc, string Out)>? RunHook;


    internal static (int Rc, string Out) Run(string file, IEnumerable<string> args, string? cwd = null, double timeoutSec = -1, IDictionary<string, string>? env = null)
    {
        var list = args.ToArray();

        // 自证钩子优先：它替换的是**子进程执行**。
        // 顺序反了会把依赖 RunHook 的负例全部短路（真机当场：两条罐头 svn 负例判假红）。
        if (RunHook is not null)
        {
            return RunHook(file, list, cwd ?? Project, (int)(timeoutSec < 0 ? GateTimeout : timeoutSec));
        }

        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = cwd ?? Project,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (env is not null)
        {
            foreach (var (k, v) in env)
            {
                psi.Environment[k] = v;
            }
        }

        foreach (var a in list)
        {
            psi.ArgumentList.Add(a);
        }

        var secs = timeoutSec < 0 ? GateTimeout : timeoutSec;
        try
        {
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(secs)))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return (124, $"⏱ 超时：`{string.Join(' ', list.Take(4))}` 在 {secs:0} 秒内没有返回（网络 / 可达性故障 —— 这不是「慢」，是拿不到读数）");
            }

            return (p.ExitCode, so.GetAwaiter().GetResult() + se.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (127, ex.Message);
        }
    }

    private static (int Rc, string Out) Cs(params string[] args) => Run("dotnet", [CsDll, .. args]);

    internal static List<string> TailLines(string text, int n = 4) =>
        [.. text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).TakeLast(n)];

    internal static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    /// <summary>收敛指纹 = 收尾拥有的三件派生物的内容哈希（r 号会随提交变，内容不会）。</summary>
    private static string FingerprintParts()
    {
        var parts = new List<string>();
        foreach (var p in new[] { PitfallsIndex, PitfallsPublic, KnowledgeMd })
        {
            parts.Add(Path.GetFileName(p) + ":" + (File.Exists(p) ? Sha256File(p) : "MISSING"));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)))).ToLowerInvariant();
    }

    private static (int Count, long MaxId) CountPitfalls()
    {
        if (!File.Exists(PitfallsSrc))
        {
            return (0, 0);
        }

        var ids = Regex.Matches(File.ReadAllText(PitfallsSrc, Encoding.UTF8), @"^##\s+(\d+)[a-z]?\.", RegexOptions.Multiline)
            .Select(m => long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        return (ids.Length, ids.Length == 0 ? 0 : ids.Max());
    }

    // ================================================================ checks
    internal sealed record Res(string Name, string Sev, bool Ok, string Summary, List<string> Detail, string Why,
        string Module = "", string Class = "");

    internal static Res R(string name, string sev, bool ok, string summary, IEnumerable<string>? detail = null, string why = "")
    {
        // ⚠️ 这里**没有**「按 check 名把红改成绿」的归一了（2026-10-16 第 2 批删除）—— 那是**用字符串当类型**。
        // 「没有 VCS」应该表现为**那几项检查根本不存在**（`NoArchive.Checks()` 返空），
        // 而不是让 CORE 事后把它们判绿。归一是症状，根因是 VCS 检查项被写死在 CORE 清单里（已搬进扩展）。
        return new(name, sev, ok, summary, detail?.ToList() ?? [], why);
    }

    /// <summary>
    /// **存档声明**（第一项 · 第 3 批）：用哪个版本库扩展由**声明**决定（`tools/closeout/archive.xml`），
    /// 不再从「有没有 `.svn`」猜；并且**「声明即自证」** ——
    /// 声明了 `svn` 却证明不了（本仓不是工作副本 / 读不到仓库 URL）⇒ **响亮红**，**绝不静默降级**。
    /// <para>没声明（老布局）⇒ 按 `.svn` 推断，但**把「这是推断」写在脸上**（不算 ✅）—— 今天那一族假红假绿
    /// （家目录游离 `.svn` / 空壳存档区 / 只认 http）全是「推断式判据 + 静默降级」长出来的。</para>
    /// </summary>
    internal static Res CheckArchiveDeclaration()
    {
        var sel = ArchiveSel;
        var why = sel.Impl.Prove();
        var proved = why is null;
        var ok = proved && sel.Declared;
        var sev = !proved && sel.Declared ? "block" : "report";
        var summary = proved
            ? $"{sel.Kind} · {sel.Note}"
            : $"声明 {sel.Kind} 但**证明不了**：{why}";
        return R("存档声明", sev, ok, summary,
            ok ? null : [$"声明文件：{CloseoutArchiveFactory.DeclarationPath}",
                         "「声明即自证」：声明的模式必须能当场证明 —— 不猜、不静默降级"],
            ok ? "收尾用哪个版本库扩展，由**声明**决定（不是从 `.svn` 猜）" : "声明与事实不符 / 还没声明");
    }


    private static bool? SessionClosed()
    {
        try
        {
            return new AgentRuntime.Core.Frozen.SessionPointerStore(SessionPointerPath).TryLoad()?.Closed;
        }
        catch
        {
            return null;
        }
    }

    internal static Res CheckWaterLevel()
    {
        var sev = SevOf("memory.watermark", "block");
        var gapTolerance = (int)ParamOf("memory.watermark", "waterGapTolerance", WaterGapTolerance);
        var tolerance = gapTolerance;
        if (!File.Exists(WaterLevel))
        {
            return R("水位收敛点", sev, false, "水位文件不存在", [WaterLevel], "收尾的收敛点写在水位里");
        }

        var txt = File.ReadAllText(WaterLevel, Encoding.UTF8);
        var hits = ConvergeRe.Matches(txt).Select(m => m.Groups[1].Value).ToList();
        var n = Regex.Matches(txt, Regex.Escape(ConvergeMark)).Count;
        if (n == 0)
        {
            return R("水位收敛点", sev, false, $"水位无「{ConvergeMark.Trim()}…rNNN」行",
                ["--close 会写这一行；现在缺 ⇒ 机器对账无操作数"], "收尾铁则 step 0 的机器对账需要有 r 号可比");
        }

        var detail = new List<string>();
        // **读数归扩展**：没有版本库读数（标准模式）⇒ **不比 r 号，只判形态**（有行 · 唯一）。
        var revNow = Vcs.Read().ArchiveRev;
        if (hits.Count > 0 && revNow is int rn && int.Parse(hits[0], CultureInfo.InvariantCulture) < rn)
        {
            var gap = rn - int.Parse(hits[0], CultureInfo.InvariantCulture);
            var closed = SessionClosed();
            if (closed == true && gap > tolerance)
            {
                return R("水位收敛点", sev, false,
                    $"会话**已收尾**，水位仍落后 {gap} 个提交（r{hits[0]} → r{rn}）⇒ 上一轮收尾**没走完**",
                    [$"判据：closed=true **且** 落后 > {tolerance} 个提交（声明可改 `memory.watermark.waterGapTolerance`；缺省 {WaterGapTolerance}）",
                     "修：补齐内容层 → --apply → 提交 → --close（绿才写收敛点）",
                     "为什么不能像旧版那样判「正常」：那等于替中断开脱（坑 #156）"],
                    "收尾不能在中途被打断后**静默**留欠账");
            }

            detail.Add($"收敛点 r{hits[0]} 之后存档又推进到 r{rn}（{gap} 个提交）；会话仍在跑 ⇒ 正常推进（写完收敛点后还会有提交）；硬判据是内容指纹，收尾时 --close 重盖章");
        }

        if (n > 1)
        {
            return R("水位收敛点", sev, false, $"收敛点行重复（{n} 行）⇒ 必须唯一", null,
                "收敛点必须唯一，否则对账读哪一个都可能读错");
        }

        return R("水位收敛点", sev, revNow is null || hits.Count > 0,
            hits.Count > 0
                ? $"存档 r{hits[0]}"
                : revNow is null ? "已写（标准模式：无版本库读数 ⇒ 只判形态）" : "存档 r?（读不到 r 号）",
            detail,
            "收尾铁则 step 0 的机器对账需要有 r 号可比");
    }

    /// <summary>pointer = '&lt;相对路径&gt;[::&lt;必须出现的子串&gt;]'，在 PROJECT / ARCHIVE / LIVE 下逐个解析（任一根站得住即算）。</summary>
    private static (string? Hit, string? Err, int Present) ResolvePointer(string? ptr, string[]? roots = null)
    {
        if (string.IsNullOrEmpty(ptr))
        {
            return (null, "pointer 为空", 0);
        }

        var idx = ptr.IndexOf("::", StringComparison.Ordinal);
        var path = idx < 0 ? ptr : ptr[..idx];
        var needle = idx < 0 ? "" : ptr[(idx + 2)..];
        var hits = new List<string>();
        var present = 0;
        var missed = 0;
        foreach (var root in roots ?? [Project, .. Vcs.Roots])
        {
            var cand = Path.Combine(root, path);
            if (!File.Exists(cand))
            {
                continue;
            }

            present++;
            if (needle.Length == 0)
            {
                hits.Add(cand);
                continue;
            }

            if (File.ReadAllText(cand, Encoding.UTF8).Contains(needle, StringComparison.Ordinal))
            {
                hits.Add(cand);
            }
            else
            {
                missed++;
            }
        }

        if (hits.Count > 0)
        {
            return (hits[0], null, present);
        }

        if (needle.Length == 0)
        {
            return (null, $"路径不存在：{path}", present);
        }

        return present > 0
            ? (null, $"文件在但找不到子串 {needle}（指针悬空；查过 {missed} 个副本）", present)
            : (null, $"路径不存在：{path}", present);
    }

    private static string? LatestStream()
    {
        var d = Path.Combine(Project, ".stage1");
        if (!Directory.Exists(d))
        {
            return null;
        }

        var cand = Directory.EnumerateFiles(d)
            .Where(f => Path.GetFileName(f).StartsWith("stream-", StringComparison.Ordinal)
                     && f.EndsWith(".jsonl", StringComparison.Ordinal)
                     && !f.EndsWith(".usage.jsonl", StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return cand;
    }

    internal static Res CheckRecurrence()
    {
        var why = "记录「已记过却再犯」的错误，供以后查询抽象是否真的降低了复发";
        var tool = Path.Combine(Project, "tools", "experiments", "knowledge-loop", "bin", "Release", "net10.0", "VerifyLoop.dll");
        var stream = LatestStream();
        if (!File.Exists(tool))
        {
            return R("复发日志", "report", true, "验收器不在（跳过）", null, why);
        }

        if (stream is null)
        {
            return R("复发日志", "report", true, "无事件流（跳过）", null, why);
        }

        var (rc, outText) = Run("dotnet", [tool, "--recurrence", stream, "--log"]);
        var first = outText.Trim().Length > 0 ? outText.Trim().Split('\n')[0] : "";
        var summary = first.Length > 0 ? first.Replace("[recurrence] ", "") : $"rc={rc}";
        return R("复发日志", "report", rc == 0,
            summary.Length > 160 ? summary[..160] : summary,
            rc == 0 ? null : TailLines(outText, 6),
            $"记录「已记过却再犯」的错误（流 {Path.GetFileName(stream)}）；落账 knowledge/.recurrence.xml");
    }

    private static readonly string[] PlacementKinds = ["gate", "resident", "human", "todo"];

    internal static Res CheckPitfallPlacements()
    {
        var sev = SevOf("pitfalls.placements", "block");
        if (!File.Exists(PlacementFile))
        {
            return R("坑集落点", sev, false, $"未建立 {Path.GetRelativePath(Project, PlacementFile)}",
                ["新坑必须声明落点类别：gate / resident / human / todo"],
                "「写错那一刻模型看得见什么」要有机器判据");
        }

        var gl = LoadGateLedger();
        if (gl is null)
        {
            return R("坑集落点", sev, false, $"账本读不出：{Path.GetRelativePath(Project, PlacementFile)}",
                ["`gate-ledger` 应是强类型 XML（A4）；读不出 ⇒ 先修账本"],
                "「写错那一刻模型看得见什么」要有机器判据");
        }

        var baseline = gl.BaselineMaxId;
        var todosMax = (int)ParamOf("pitfalls.placements", "todoBaseline", gl.TodosMax);

        var ids = Regex.Matches(File.ReadAllText(PitfallsSrc, Encoding.UTF8), @"^##\s+(\d+)[a-z]?\.", RegexOptions.Multiline)
            .Select(m => long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().OrderBy(x => x).ToArray();
        var newIds = ids.Where(i => i > baseline).ToArray();

        var decls = gl.Declarations.ToDictionary(d => (long)d.Id, d => d);

        var undeclared = newIds.Where(i => !decls.ContainsKey(i)).ToArray();
        var dangling = new List<string>();
        var badKind = new List<string>();
        var noWhy = new List<string>();
        foreach (var (id, d) in decls.OrderBy(kv => kv.Key))
        {
            if (!PlacementKinds.Contains(d.Kind))
            {
                badKind.Add("#" + id);
                continue;
            }

            if (d.Kind is "gate" or "resident")
            {
                var (_, err, _) = ResolvePointer(d.Pointer);
                if (err is not null)
                {
                    dangling.Add($"#{id} {err}");
                }
            }
            else if (string.IsNullOrEmpty(d.Why))
            {
                noWhy.Add("#" + id);
            }
        }

        var todos = decls.Count(kv => kv.Value.Kind == "todo");
        var gates = decls.Count(kv => kv.Value.Kind == "gate");
        var resident = decls.Count(kv => kv.Value.Kind == "resident");
        var human = decls.Count(kv => kv.Value.Kind == "human");

        var problems = new List<string>();
        if (undeclared.Length > 0)
        {
            problems.Add("新坑未声明落点：" + string.Join(", ", undeclared.Select(i => $"#{i}")));
        }

        if (dangling.Count > 0)
        {
            problems.Add("指针悬空：" + string.Join("；", dangling));
        }

        if (badKind.Count > 0)
        {
            problems.Add("kind 非法：" + string.Join(", ", badKind));
        }

        if (noWhy.Count > 0)
        {
            problems.Add("human/todo 缺 why：" + string.Join(", ", noWhy));
        }

        if (todos > todosMax)
        {
            problems.Add($"todo（应为闸门未做）{todos} 条 > 基线 {todosMax} ⇒ 先把旧 todo 变成闸门，或显式抬基线");
        }

        var ok = problems.Count == 0;
        return R("坑集落点", sev, ok,
            ok
                ? $"声明 {decls.Count} 条（gate {gates} / resident {resident} / human {human} / todo {todos}，todo 基线 {todosMax}）· 新坑 0 条未声明"
                : string.Join("；", problems),
            ok ? null : ["规则：新坑必须声明落点类别；确定性坑应做成闸门（gate）"],
            "「写错那一刻模型看得见什么」要有机器判据（坑 #130 / §十·51）");
    }

    private static string MemIndexTimeline(string text)
    {
        var i = text.IndexOf("## 一、时间线索引", StringComparison.Ordinal);
        if (i < 0)
        {
            return "";
        }

        var j = text.IndexOf("\n## ", i + 1, StringComparison.Ordinal);
        return text[i..(j > 0 ? j : text.Length)];
    }

    /// <summary>
    /// **自己的**读数（E 批·兜底层自锁）：最新一篇 <c>memory/YYYY-MM-DD.md</c> 是哪一天（没有 ⇒ null）。
    /// <para>为什么要有它：`memory` 是**兜底模块** —— 别的收尾模块全坏时它仍要能自检 ⇒
    /// 它的判据**先问自己的落点**；别人的读数只是补充，拿不到 **不许**变成“记忆坏了”。</para>
    /// </summary>
    private static string? LatestMemoryDay()
    {
        var dir = Path.Combine(Live, "memory");
        if (!Directory.Exists(dir))
        {
            return null;
        }

        return Directory.EnumerateFiles(dir, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is { Length: 10 } && n[4] == '-' && n[7] == '-')
            .OrderByDescending(n => n, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    internal static Res CheckSzonePresence()
    {
        var sev = SevOf("memory.szone", "block");
        var minBytes = (long)ParamOf("memory.szone", "minBytes", 300);
        // **兜底层自锁**（E 批）：先问**自己**的落点 —— 最新一篇当日记忆是几号；
        // 记忆系统不许“靠别人才会自检”。再退到扩展读数 / 账本（都拿不到 ⇒ 说清是**读数缺失**，不是“记忆坏了”）。
        var d = LatestMemoryDay() ?? Vcs.Read().LastCommitDate ?? LoadLedger()?.WaterLevelDate;
        if (string.IsNullOrEmpty(d))
        {
            return R("素材区存在性", sev, false, "读不到「最后活动日」：自己的 memory/ 下没有 YYYY-MM-DD.md，扩展读数与账本也不可用",
                [$"自己的落点：{Path.Combine(Live, "memory")}"], "应有集合判据（METHODOLOGY §十·16）");
        }

        var problems = new List<string>();
        (string Label, string Root)[] szoneRoots = Vcs.ArchiveRoot is { } ar ? [("存档区", ar), ("活版", Live)] : [("活版", Live)];
        foreach (var (label, root) in szoneRoots)
        {
            var p = Path.Combine(root, "memory", d + ".md");
            if (!File.Exists(p))
            {
                problems.Add($"{label}缺 memory/{d}.md");
            }
            else if (new FileInfo(p).Length < minBytes)
            {
                problems.Add($"{label}的 memory/{d}.md 只有 {new FileInfo(p).Length} B（像占位）");
            }
        }

        var md = d[5..];
        if (!File.Exists(WaterLevel))
        {
            problems.Add("水位正文 不存在");
        }
        else if (!Regex.IsMatch(File.ReadAllText(WaterLevel, Encoding.UTF8), $"^- \\*\\*{Regex.Escape(d)}", RegexOptions.Multiline))
        {
            problems.Add($"水位无 {d} 的当日段（`- **{d}…` 行）");
        }

        var idx = Path.Combine(Vcs.ArchiveRoot ?? Live, "MEMORYINDEX.md");
        if (!File.Exists(idx))
        {
            problems.Add("MEMORYINDEX 不存在");
        }
        else if (!Regex.IsMatch(MemIndexTimeline(File.ReadAllText(idx, Encoding.UTF8)), $@"^\|\s*\**{Regex.Escape(md)}\**\s*\|", RegexOptions.Multiline))
        {
            problems.Add($"MEMORYINDEX 时间线无 {md} 行（`| {md} | …`）");
        }

        var ok = problems.Count == 0;
        return R("素材区存在性", sev, ok,
            ok ? $"{d} 的素材区三件齐全（日记 ×2 · 水位当日段 · 索引当日行）" : string.Join("；", problems),
            ok ? null : ["应有集合判据：闸门管「做了的对不对」，这一项管「该做的有没有做」"],
            "判据按**应有集合**算，不按已有集合算（METHODOLOGY §十·16 / 坑 #137）");
    }

    internal static Res CheckLedger()
    {
        var led = LoadLedger();
        if (led is null)
        {
            return R("账本对账", "report", false, "账本不存在（首次 --close 会建）",
                ["跑 `closeout --close` 建立收敛点"], "账本 = 收敛点/计数/已知欠账的唯一家");
        }

        var (src, _) = CountPitfalls();
        var oldFp = led.Fingerprint;
        var line = $"收敛点 r{led.LastCloseoutRev} · 坑集 {led.PitfallsSrc}→{src}/最大 #{led.MaxPitfallId} · 指纹 {oldFp[..Math.Min(12, oldFp.Length)]}";
        var fpNow = FingerprintParts();
        var changed = fpNow != led.Fingerprint;
        var d = new List<string>();
        if (changed)
        {
            d.Add($"派生指纹已变（{oldFp[..Math.Min(12, oldFp.Length)]} → {fpNow[..12]}）⇒ 收尾时先 --apply 再 --close 重新盖章");
        }

        if (led.PitfallsSrc != src)
        {
            d.Add($"坑集条数 {led.PitfallsSrc} → {src}（自上次收敛以来有新增 ⇒ 收尾要重跑索引并记账）");
        }

        foreach (var it in led.ReportDebt)
        {
            d.Add($"已知欠账：{it.Name} —— {it.Why}");
        }

        return R("账本对账", "report", !changed, line, d, "计数/指纹变了要有人管；已知欠账在此集中声明");
    }

    private static List<string> WorkerMemoryDirs()
    {
        var home = Path.Combine(Project, "whitebox", "workspace", "worker");
        if (!Directory.Exists(home))
        {
            return [];
        }

        return [.. Directory.EnumerateDirectories(home)
            .Where(d => Directory.Exists(Path.Combine(d, "memory")))
            .OrderBy(d => d, StringComparer.Ordinal)];
    }


    // ================================================================ ledger I/O
    private static CloseoutLedger? LoadLedger()
    {
        if (!File.Exists(LedgerPath))
        {
            return null;
        }

        try
        {
            return DataContractXml.ReadFile<CloseoutLedger>(LedgerPath);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveLedger(CloseoutLedger data)
    {
        RequireWritable(LedgerPath, When.During);   // D 批：账本也过闸
        Directory.CreateDirectory(LedgerDir);
        DataContractXml.WriteFileAtomic(LedgerPath, data);
    }

    private static GateLedger? LoadGateLedger()
    {
        if (!File.Exists(PlacementFile))
        {
            return null;
        }

        try
        {
            return DataContractXml.ReadFile<GateLedger>(PlacementFile);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// **模块声明自证**（模块 `closeout`，**锁定件**）：生效模块集、指纹、以及声明里的问题。
    /// <para>「声明即自证」：不认识的 id / 越界落点 / 必达落点缺失 / 锁定件被删或被降级 ⇒ **响亮红**，
    /// 绝不静默降级。锁定件（`memory` 兜底 · `closeout` 自证）**删了声明也不消失**。</para>
    /// </summary>
    /// <summary>用户决策取向：三本齐 + 决策库四层齐 + 条目都有状态（**只上账 · report**，不阻断 —— 用户 2026-10-17 令）。</summary>
    internal static Res CheckDecisionBooks()
    {
        var entries = Path.Combine(Live, "USER-DECISION-ENTRIES.md");
        var library = Path.Combine(Live, "DECISION-LIBRARY.md");
        var principles = Path.Combine(Live, "PROJECT-DESIGN-PRINCIPLES.md");

        var missing = new[] { entries, library, principles }.Where(x => !File.Exists(x)).ToList();
        if (missing.Count > 0)
        {
            return R("用户决策取向", "report", false, $"{missing.Count} 本缺文件", missing.Select(x => $"缺：{x}"));
        }

        var lib = File.ReadAllText(library, Encoding.UTF8);
        var layers = new[] { "## L1", "## L2", "## L3", "## L4" }
            .Where(h => !lib.Contains(h, StringComparison.Ordinal)).ToList();

        var ent = File.ReadAllText(entries, Encoding.UTF8);
        var blocks = Regex.Split(ent, @"^- \*\*\[D-", RegexOptions.Multiline).Skip(1).ToList();
        var noState = blocks.Count(b => !b.Contains("状态", StringComparison.Ordinal));

        var ok = layers.Count == 0 && noState == 0 && blocks.Count > 0;
        return R("用户决策取向", "report", ok,
            ok ? $"三本齐 · 决策库四层齐 · 条目 {blocks.Count} 条全部有状态"
               : $"决策库缺层 {layers.Count} · 条目缺状态 {noState} · 条目数 {blocks.Count}",
            layers.Select(h => $"缺层：{h}").Concat(noState > 0 ? [$"缺状态条目：{noState} 条"] : []));
    }

    internal static Res CheckModuleDeclarations()
    {
        var m = Modules;
        var head = m.Declared
            ? $"声明 {m.Modules.Count} 个模块 · 指纹 {m.Fingerprint}"
            : $"内置默认 {m.Modules.Count} 个模块（未声明）· 指纹 {m.Fingerprint}";
        return R("模块声明", "block", m.Problems.Count == 0,
            m.Problems.Count == 0 ? head : $"{m.Problems.Count} 处：{string.Join("；", m.Problems.Take(4))}",
            m.Problems.Count == 0
                ? [$"目录：{RequirementSet.Dir}"]
                : [.. m.Problems, $"目录：{RequirementSet.Dir}"],
            "声明即自证：模块集与落点要对得上；memory / closeout 是锁定件（删声明也不许消失、不许降级）");
    }

    /// <summary>跑一条**具名闸门**（模块自己声明它要哪几条 —— 闸门表仍只一处）。</summary>
    internal static Res RunGate(string name)
    {
        var g = Gates.FirstOrDefault(x => x.Name == name)
                ?? throw new InvalidDataException($"没有名为「{name}」的闸门（模块声明错 ⇒ 响亮报错，不静默跳过）");
        var (rc, outText) = Cs(g.Cs);
        return R(g.Name, g.Sev, rc == 0, rc == 0 ? "绿" : $"红（rc={rc}）", rc == 0 ? null : TailLines(outText), g.Why);
    }

    private static List<Res> AllChecks()
    {
        // **收尾流程 = 按模块跑它自己的判据**（模块清单 = `BuiltinRequirements`）。
        // A 批只做分组：判据本体一个字节没改 ⇒ 「条目集合 + 逐项红绿 + 摘要」与改前一致；
        // 变的只是**顺序**（按模块 Order 重排，属有意变更 —— 旧顺序里「坑集」的判据本就分居两头）。
        var res = new List<Res>();
        foreach (var req in Modules.Modules)
        {
            var cls = req.Class == ModuleClass.Core ? "core" : "extension";
            foreach (var r in req.Checks())
            {
                res.Add(r with { Module = req.Id, Class = cls });
            }
        }

        return res;
    }

    // ================================================================ entry
    public static int Check(bool xml)
    {
        // 「不适用」归一只有一处声明：**`R(...)`**（按 check 名判）。
        // ⚠️ 这里**不许再抄一份**：2026-10-15 真机 —— `Check()` 抄了一份、`--close` 那条路没抄，
        //     两条入口口径分叉（且抄的那份晚于枚举 ⇒ 根本拦不住 `CheckCopyParity` 的崩溃）。
        var results = AllChecks();

        var blockRed = results.Count(r => r.Sev == "block" && !r.Ok);
        var reportRed = results.Count(r => r.Sev == "report" && !r.Ok);

        if (xml)
        {
            var report = new CheckReport
            {
                Roots = new Roots { Project = Project, Live = Live, Archive = Vcs.ArchiveRoot ?? string.Empty },
                Results =
                [
                    .. results.Select(r => new CheckItem
                    {
                        Name = r.Name,
                        Sev = r.Sev,
                        Ok = r.Ok,
                        Summary = r.Summary,
                        Detail = [.. r.Detail],
                        Why = r.Why,
                        Module = r.Module,
                        Class = r.Class,
                    }),
                ],
                BlockRed = blockRed,
                ReportRed = reportRed,
            };
            Console.WriteLine(DataContractXml.ToDocument(report));
            return blockRed > 0 ? 1 : 0;
        }

        Console.WriteLine("[WB] 收尾流水线 --check（只读）");
        Console.WriteLine($"  PROJECT={Project}\n  LIVE={Live}\n  ARCHIVE={Vcs.ArchiveRoot ?? "（无：标准模式）"}\n");
        Console.WriteLine($"  模块 {Modules.Modules.Count} 个 · 指纹 {Modules.Fingerprint}（{(Modules.Declared ? "声明" : "内置默认")}）：" + string.Join(" · ", Modules.Modules
            .Select(r => $"{r.Id}[{(r.Class == ModuleClass.Core ? "core" : "ext")}/{r.Access}]")));
        Console.WriteLine();
        foreach (var r in results)
        {
            var mark = r.Ok ? "✅" : r.Sev == "block" ? "❌" : "⚠️ ";
            Console.WriteLine($"  {mark} {r.Name,-12} {r.Sev,-6} {r.Summary}");
            foreach (var d in r.Detail)
            {
                Console.WriteLine($"       {d}");
            }
        }

        Console.WriteLine();
        // 拒绝判据**只看 Core（必达）**：扩展（如 archive：SVN/VCS）红了、甚至不可达，都只上账、**不阻断**。
        var coreRed = results.Count(r => r.Sev == "block" && !r.Ok && r.Class == "core");
        var extRed = results.Count(r => !r.Ok && r.Class == "extension");
        Console.WriteLine($"汇总：block 红 {blockRed} 项（**Core {coreRed}** 项阻断 · 扩展 {extRed} 项仅上账）· report 红 {reportRed} 项 ⇒ {(coreRed > 0 ? $"未全绿（带 {coreRed} 条 Core block 红；--close **仍会写**，但会在水位行 + 账本记账）" : "阻塞项全绿（可 --close）")}");
        if (reportRed > 0)
        {
            Console.WriteLine("（report 项红色属**已知欠账/变化**，已上账；本轮不阻断，但不得悄悄变多）");
        }

        return blockRed > 0 ? 1 : 0;
    }

    public static int Where()
    {
        Console.WriteLine("[WB] 收尾流水线 · 根目录（**所有路径基准看这里**）");
        foreach (var (k, v) in new[] { ("PROJECT  代码仓", Project), ("LIVE     本机活版", Live), ("ARCHIVE  存档区（扩展）", Vcs.ArchiveRoot ?? "（无：标准模式）") })
        {
            Console.WriteLine($"  {k,-16} {v}");
        }

        Console.WriteLine();
        Console.WriteLine("  规范调用（绝对路径，任何 cwd 都能跑）：");
        Console.WriteLine($"     dotnet {SelfDll} --check");
        Console.WriteLine();
        Console.WriteLine(CloseOrder);
        return 0;
    }

    // ================================================================ actions
    /// <summary>幂等重生派生物：坑集索引 / 公开变体 / 水位索引区 + worker 自己的记忆索引。</summary>
    public static int Apply()
    {
        Console.WriteLine("[WB] 收尾流水线 --apply（幂等重生派生物）");
        if (!File.Exists(CsDll))
        {
            Console.WriteLine($"  ❌ C# CLI 未构建：{Path.GetRelativePath(Project, CsDll)}");
            Console.WriteLine($"     先跑：{CsBuildHint}");
            return 1;
        }

        var steps = new (string Name, string[] Args)[]
        {
            ("坑集索引", ["--pitfalls-index", PitfallsSrc]),
            ("公开坑集变体", ["--pitfalls-public", PitfallsSrc]),
            ("水位/知识索引区", ["--doc-index", Live]),
            // 用户 2026-10-14 令：改源码语料（USER.md 等）⇒ 收尾时**自动重生 + 漏账 auto-bump**，
            // 免得「改了源忘了重生」让 R1 停在旧副本（`--bump all` 只 bump 真变了的 key，无变化 = 幂等）。
            ("冻结语料重生", ["--frozen-corpus", Live, "--bump", "all"]),
        };
        foreach (var (name, a) in steps)
        {
            var (rc, outText) = Cs(a);
            var last = TailLines(outText, 1);
            var line = last.Count > 0 ? last[0] : "";
            Console.WriteLine($"  {(rc == 0 ? "✅" : "❌")} {name,-14} {(line.Length > 100 ? line[..100] : line)}");
            if (rc != 0)
            {
                return 1;
            }
        }

        Console.WriteLine($"  ✅ {"worker 记忆索引",-14} {RebuildWorkerIndex()} 份（worker 自己的家 ⇒ 与 master 不同源）");
        return 0;
    }

    private static string WriteWorkerIndex(string memoryDir)
    {
        var rows = new List<(string Name, int Lines)>();
        foreach (var name in Directory.EnumerateFiles(memoryDir).Select(Path.GetFileName)
                     .Where(n => n is not null && n != "index.md")
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            rows.Add((name!, File.ReadAllLines(Path.Combine(memoryDir, name!), Encoding.UTF8).Count(l => l.Trim().Length > 0)));
        }

        var abandoned = File.Exists(Path.Combine(memoryDir, "abandoned.log"));
        var body = new List<string>
        {
            "<!-- frozen: version=1 -->",
            "# worker memory index（worker 自己的记忆索引 · 与 master 不同源）",
            "",
            "> 本文件 = worker **自己**那份 memory 的索引；它的 R1 memory-index 区读它。",
            "> 抽象 / 收敛由 master 收尾时做 —— 这里只列**事实 + 指针**。",
            "",
            $"- 弃单 `abandoned.log`：{(abandoned ? "有" : "无")}",
        };
        foreach (var (name, lines) in rows)
        {
            body.Add($"- `{name}`：{lines} 行");
        }

        var outPath = Path.Combine(memoryDir, "index.md");
        File.WriteAllText(outPath, string.Join('\n', body) + "\n", new UTF8Encoding(false));
        return outPath;
    }

    private static int RebuildWorkerIndex()
    {
        var n = 0;
        foreach (var memoryDir in WorkerMemoryDirs())
        {
            WriteWorkerIndex(memoryDir);
            n++;
        }

        return n;
    }

    /// <summary>把「收敛点」行写进水位（**两份副本都写**），并保证恰好一行。</summary>
    /// <summary>
    /// **写守卫**（D 批）：写之前过闸 —— 不通过就**抛**（fail-closed、响亮）。
    /// <para>口径：「只读」是**真的写不了**，不是靠纪律；没人声明的角落也不许写。</para>
    /// </summary>
    internal static void RequireWritable(string path, When when)
    {
        if (Modules.GuardWritable(path, when) is { } why)
        {
            throw new InvalidDataException($"[写守卫] {why}");
        }
    }

    /// <summary>把收敛点插进**指定那一份**水位（幂等：先删旧行）。—— **规矩只此一处**；
    /// CORE 调它写自己那份（活版），扩展调它写自己的副本（存档区）。</summary>
    internal static void InsertConvergeLine(string path, string line)
    {
        RequireWritable(path, When.During);   // D 批：写之前过闸（只读模块 ⇒ 抛）
        // 旧红注**不再跨收尾存活**（2026-10-17 测试发现）：绿收尾后，上一条模型写的
        // 「- **⚠️ 本收敛点带红**：…」必须清掉；带红收尾由新收敛点行自己的 `⚠️ block 红 N` 表达。
        var raw = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Split('\n');
        var lines = new List<string>();
        var skippingRedNote = false;
        foreach (var ln in raw)
        {
            if (ln.StartsWith("- **⚠️ 本收敛点带红**", StringComparison.Ordinal))
            {
                skippingRedNote = true;
                continue;
            }

            if (skippingRedNote)
            {
                if (ln.Length == 0)
                {
                    skippingRedNote = false;
                }

                continue;
            }

            if (!ln.StartsWith(ConvergeMark, StringComparison.Ordinal))
            {
                lines.Add(ln);
            }
        }
        var outp = new List<string>();
        var done = false;
        foreach (var ln in lines)
        {
            outp.Add(ln);
            if (!done && ln.StartsWith("## 水位", StringComparison.Ordinal))
            {
                outp.Add(string.Empty);
                outp.Add(line);
                done = true;
            }
        }

        if (!done)
        {
            outp.Add(string.Empty);
            outp.Add(line);
        }

        File.WriteAllText(path, string.Join('\n', outp).TrimEnd('\n') + "\n", new UTF8Encoding(false));
    }

    private static string WriteConvergeLine(string archiveRev, string projectRev, int src, int idx, int parityOk, int parityTotal, string fingerprint, int blockRed = 0)
    {
        // block 红**不再拒写**，但必须留在收敛点行里（用户 2026-10-17 令「留一点牙」：真问题不许被淹没）。
        var redMark = blockRed > 0 ? $" · ⚠️ block 红 {blockRed}（未收敛就收的尾）" : string.Empty;
        var line = $"{ConvergeMark}存档 r{archiveRev} · 代码仓 r{projectRev} · 坑集 {src}/{idx} · 活版↔存档区 {parityOk}/{parityTotal} · 指纹 {fingerprint[..12]}{redMark}";
        InsertConvergeLine(WaterLive, line);   // CORE 只写**自己那份**；第二副本交给 `Vcs.Sync`。
        return line;
    }

    /// <summary>顺序闸门（**留牙版** · 用户 2026-10-17 令）：block 全绿 ⇒ 照旧写收敛点；
    /// 红 ⇒ **不再拒写**（只警告），但把欠账写进水位行 + 账本 —— 「只警告不阻断」与「真问题不许被淹没」两件同时成立。</summary>
    public static int Close(bool dry)
    {
        Console.WriteLine("[WB] 收尾流水线 --close（顺序闸门：先绿，再写收敛点）");
        // 清单与 `--check` **同一份**（`AllChecks`）：“一条口径只许一处声明” ——
        // 两处各写一份必然分叉（2026-10-15 真机：`--close` 少抄清单 ⇒ 两入口判据不一致）。
        var results = AllChecks();

        // 拒绝判据**只看 Core（必达）模块**的 block 红 —— 扩展模块（如 archive：SVN/VCS）红了、甚至不可达，
        // 都只上账、**不阻断**（用户 2026-10-16 13:32：「允许扩展模块不正常的情况下也可以直接收尾」）。
        var blockRed = results.Where(r => r.Sev == "block" && !r.Ok && r.Name != Owns[0] && r.Class == "core").ToList();
        if (blockRed.Count > 0)
        {
            // 用户 2026-10-17 令（**留牙版**）：「运行时与闸门不许强制阻断，而是留下警告 —— 阻断之后很难推测根本原因」；
            // 但同一句话的下半段是「**真问题不能被淹没**」⇒ 于是：**照样写收敛点**，同时把欠账**写进水位行 + 账本**。
            // ⇒ 牙留在「记账」上：带着几条 block 红收的尾，水位那一行看得见。
            Console.WriteLine($"⚠️  带着 {blockRed.Count} 条 block 红收尾（**不再拒写** —— 用户 2026-10-17 令：只警告不阻断）。欠账：");
            foreach (var r in blockRed)
            {
                Console.WriteLine($"   · {r.Name} — {r.Summary}");
            }

            Console.WriteLine("（照旧写收敛点；上面的欠账同时记进水位行与账本，不许静默）");
        }

        var (src, maxid) = CountPitfalls();
        var idxLines = 0;
        if (File.Exists(PitfallsIndex))
        {
            var m = Regex.Match(File.ReadAllText(PitfallsIndex, Encoding.UTF8), @"计数：\*\*(\d+) 条\*\*");
            idxLines = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        var fp = FingerprintParts();
        // 版本库那几件事实整个来自**扩展读数**（CORE 不再自己问 svn）。
        var readings = Vcs.Read();
        var total = readings.ParityTotal ?? 0;
        var aRev = readings.ArchiveRev?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var pRev = readings.ProjectRev?.ToString(CultureInfo.InvariantCulture) ?? "?";

        if (dry)
        {
            Console.WriteLine($"[dry-run] 将写入收敛点：存档 r{aRev} · 代码仓 r{pRev} · 坑集 {src}/{idxLines} · 活版↔存档区 {total}/{total} · 指纹 {fp[..12]}");
            return 0;
        }

        var line = WriteConvergeLine(aRev, pRev, src, idxLines, total, total, fp, blockRed.Count);

        var existing = LoadLedger();
        var debt = existing?.ReportDebt is { Count: > 0 } ex
            ? ex
            : new List<DebtItem>
            {
                new() { Name = "知识树", Why = "派生落后于活版 ⇒ 跑 `AgentRuntime.Cli --kn-build <workspace>` 一条命令即可修" },
                new() { Name = "冻结语料", Why = "用户 2026-09-23 令：语料 bump 属记账项、不阻断收尾" },
            };

        // 带着红的 block 项也记进账本（与水位行同一份事实的第二个落点 —— 不许只写一处）。
        foreach (var r in blockRed)
        {
            if (debt.All(d => d.Name != r.Name))
            {
                debt.Add(new DebtItem { Name = r.Name, Why = $"block 红未清就收尾（用户 2026-10-17 令：只警告不阻断）：{r.Summary}" });
            }
        }

        SaveLedger(new CloseoutLedger
        {
            Schema = 1,
            Fingerprint = fp,
            LastCloseoutRev = readings.ArchiveRev,
            LastCloseoutRevProject = readings.ProjectRev,
            MaxPitfallId = (int)maxid,
            PitfallsSrc = src,
            PitfallsIndex = idxLines,
            ParityFiles = total,
            WaterLevelDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ReportDebt = debt,
        });

        Console.WriteLine($"✅ 收敛点已写：{line}");
        foreach (var m in Vcs.Sync(line))   // 扩展自己的地盘（第二副本 / 提交…）—— CORE 不知道它干了什么
        {
            Console.WriteLine($"   {m}");
        }

        Console.WriteLine($"   账本：{LedgerPath}");
        Console.WriteLine(CloseOrder);
        return 0;
    }

    // ================================================================ selftest
    private static void CopyTree(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (name is ".svn" or "obj" or "bin")
            {
                continue;
            }

            CopyTree(dir, Path.Combine(dst, name));
        }

        foreach (var f in Directory.EnumerateFiles(src))
        {
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
        }
    }

    private static void RecursiveDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>负例自证：在 /tmp 的镜像副本上逐条注入缺陷，断言对应判据变红。绝不碰仓内文件；副本用完即删。</summary>
    public static int Selftest()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "closeout-selftest." + Guid.NewGuid().ToString("N")[..8]);
        var root = Path.Combine(tmp, "software-company");
        var pr = Path.Combine(root, "projects", "AgentRuntime");
        var ar = Path.Combine(root, "whitebox-workspace");
        CopyTree(Path.Combine(Project, "tools"), Path.Combine(pr, "tools"));

        // 镜像里也要有 **CLI 产物 + 指针目标**：本工具的闸门是调 `AgentRuntime.Cli` 跑的子进程；
        // 而「坑集落点」的指针对准 `tests/…` / `src/…` / `docs/…` ⇒ 不拷齐它们，那几条负例**恒红（空转，没有牙）**。
        var cliDir = Path.GetDirectoryName(CsDll)!;
        if (Directory.Exists(cliDir))
        {
            CopyTree(cliDir, Path.Combine(pr, Path.GetRelativePath(Project, cliDir)));   // CLI dll（跳过 bin 的 CopyTree 不会带它）
        }

        foreach (var sub in new[] { "src", "tests", "docs", "benchmark" })
        {
            CopyTree(Path.Combine(Project, sub), Path.Combine(pr, sub));
        }
        Directory.CreateDirectory(Path.Combine(pr, "docs"));
        foreach (var f in new[] { "PITFALLS.md", "PITFALLS-INDEX.md", "PITFALLS-public.md" })
        {
            File.Copy(Path.Combine(Project, "docs", f), Path.Combine(pr, "docs", f), overwrite: true);
        }

        CopyTree(Live, Path.Combine(pr, "whitebox", "workspace"));
        // 判据按**应有形状**写（§十·57）：存档区只「存在」不算数 —— 空壳目录（只剩 knowledge/）
        // 会把镜像拷成缺 memory/ 的半成品 ⇒ 夹具崩。认「有水位正文」才算真存档区。
        if (File.Exists(Path.Combine(SvnArchive.ArchiveRootPath, "memory", "收尾水位.md"))) { CopyTree(SvnArchive.ArchiveRootPath, ar); } else { CopyTree(Live, ar); }
        Directory.CreateDirectory(Path.Combine(pr, ".svn"));

        // 把 CLI 构建产物**按相对位置**复制过去 ⇒ 复制出来的 dll 自定位到镜像（与 py「把脚本拷进镜像」同义）。
        var dllDir = Path.GetDirectoryName(SelfDll)!;
        var mirrorDllDir = Path.Combine(pr, Path.GetRelativePath(Project, dllDir));
        CopyTree(dllDir, mirrorDllDir);
        var mirrorDll = Path.Combine(mirrorDllDir, Path.GetFileName(SelfDll));

        Dictionary<string, bool> RunCheck()
        {
            var (_, outT) = Run("dotnet", [mirrorDll, "--check", "--xml"], cwd: pr);
            var map = new Dictionary<string, bool>(StringComparer.Ordinal);
            try
            {
                foreach (var item in DataContractXml.FromDocument<CheckReport>(outT).Results)
                {
                    map[item.Name] = item.Ok;
                }
            }
            catch
            {
                // 解析不了 ⇒ 空表（用例会记为「缺失」）
            }

            return map;
        }

        var wl = Path.Combine(ar, "memory", "收尾水位.md");
        var gl = Path.Combine(pr, "tools", "closeout", "gate-ledger.xml");
        var led = Path.Combine(pr, "tools", "closeout", "ledger.xml");
        var pf = Path.Combine(pr, "docs", "PITFALLS.md");
        var la = Path.Combine(pr, "whitebox", "workspace", "AGENTS.md");
        var memindex = Path.Combine(ar, "MEMORYINDEX.md");
        var wd = LoadLedger()?.WaterLevelDate is { Length: > 0 } w ? w : "1970-01-01";

        static List<string> Lines(string t) => [.. t.Replace("\r\n", "\n").Split('\n')];

        string DropConverge(string t) => string.Join('\n', Lines(t).Where(l => !l.StartsWith(ConvergeMark, StringComparison.Ordinal))) + "\n";

        string DupConverge(string t)
        {
            var one = Lines(t).FirstOrDefault(l => l.StartsWith(ConvergeMark, StringComparison.Ordinal));
            return t + (one is not null ? "\n" + one + "\n" : "");
        }

        string StaleFingerprint(string t) =>
            Regex.Replace(t, "<Fingerprint>[^<]*</Fingerprint>", $"<Fingerprint>{new string('0', 64)}</Fingerprint>");

        var cases = new (string Name, string Expect, (string Path, Func<string, string> Fn)[] Muts)[]
        {
            ("源加了坑但索引没重跑", "坑集索引", [(pf, t => t + "\n## 999. 负例坑\n- 负例正文\n")]),
            ("水位缺收敛点行", "水位收敛点", [(wl, DropConverge)]),
            ("水位收敛点行重复", "水位收敛点", [(wl, DupConverge)]),
            ("活版与存档区不一致", "活版 ↔ 存档区", [(la, t => t + "\n<!-- 负例 -->\n")]),
            ("新坑未声明落点", "坑集落点", [(pf, t => t + "\n## 999. 负例新坑\n- 负例正文\n")]),
            ("声明的指针悬空", "坑集落点", [(gl, t => t.Replace("KNOWLEDGE.md::51.", "KNOWLEDGE.md::§十·999（不存在）"))]),
            ("todo 计数超基线", "坑集落点", [(gl, t => Regex.Replace(t, "<TodosMax>\\d+</TodosMax>", "<TodosMax>1</TodosMax>"))]),
            ("账本指纹过期", "账本对账", [(led, StaleFingerprint)]),
            ("当日日记被清空", "素材区存在性", [(Path.Combine(ar, "memory", wd + ".md"), _ => "")]),
            ("MEMORYINDEX 只有版本号、漏时间线行", "素材区存在性",
                [(memindex, t => Regex.Replace(t, $@"^\|\s*\**{Regex.Escape(wd[5..])}\**\s*\|.*$", "", RegexOptions.Multiline))]),
            ("水位无当日段（日期只出现在正文别处）", "素材区存在性",
                [(wl, t => string.Join('\n', Lines(t).Where(l => !l.StartsWith($"- **{wd}", StringComparison.Ordinal))) + "\n")]),
            ("活版独有改动未同步（白名单外的文件）", "活版 ↔ 存档区",
                [(Path.Combine(pr, "whitebox", "workspace", "knowledge", ".ledger.xml"), t => t + "\n")]),
        };

        // 自证：进程内罐头负例必须在「连 SVN」语义下跑 —— 否则 R() 的「不适用」过滤器
        // 会按名字把它们强制成绿（本地模式下 SvnLinked=false），负例就成了空转（§十·57）。
        CloseoutArchiveFactory.SvnLinkedOverride = true;
        var baseRes = RunCheck();
        Console.WriteLine($"[WB] closeout --selftest（负例自证，全部在副本上：{tmp}）");
        Console.WriteLine("   基线：" + string.Join("，", baseRes.Select(kv => $"{kv.Key}={(kv.Value ? "绿" : "红")}")));
        var passed = 0;
        foreach (var (name, expect, muts) in cases)
        {
            var saved = new List<(string Path, string Text)>();
            foreach (var (path, fn) in muts)
            {
                var txt = File.ReadAllText(path, Encoding.UTF8);
                saved.Add((path, txt));
                File.WriteAllText(path, fn(txt), new UTF8Encoding(false));
            }

            var res = RunCheck();
            bool? got = res.TryGetValue(expect, out var ok) ? ok : null;
            foreach (var (path, txt) in saved)
            {
                File.WriteAllText(path, txt, new UTF8Encoding(false));
            }

            var good = got == false;
            Console.WriteLine($"  {(good ? "✅" : "❌")} {name,-18} 期望「{expect}」变红 → {(got == false ? "红（拦住了）" : got == true ? "绿（没拦住！）" : "缺失")}");
            if (good)
            {
                passed++;
            }
        }

        // --- 单项：**超时**是否生效（假 svn 永远不返回）—— 唯一在真仓只读上执行的一步 ---
        var shimDir = Path.Combine(tmp, "shim");
        Directory.CreateDirectory(shimDir);
        var shim = Path.Combine(shimDir, "svn");
        File.WriteAllText(shim, "#!/bin/sh\nsleep 30\n", new UTF8Encoding(false));
        Run("chmod", ["+x", shim]);
        var t0 = Environment.TickCount64;
        var (_, outFake) = Run("dotnet", [SelfDll, "--check", "--xml"], cwd: Project, timeoutSec: 300,
            env: new Dictionary<string, string>
            {
                ["CLOSEOUT_SVN_TIMEOUT"] = "3",
                ["CLOSEOUT_FORCE_SVN"] = "1",
                ["PATH"] = shimDir + ":" + (Environment.GetEnvironmentVariable("PATH") ?? ""),
            });
        var elapsed = (Environment.TickCount64 - t0) / 1000.0;
        var fakeOk = false;
        try
        {
            var reach = DataContractXml.FromDocument<CheckReport>(outFake).Results
                .FirstOrDefault(x => x.Name == "可达性前置");
            fakeOk = reach?.Ok == false && elapsed < 240;
        }
        catch
        {
            fakeOk = false;
        }

        Console.WriteLine($"  {(fakeOk ? "✅" : "❌")} {"假 svn 挂起（远端问不到）",-24} 期望「可达性前置」变红且**限时返回** → {(fakeOk ? "红（没挂死）" : "没拦住 / 被挂死")}（{elapsed:0.0}s）");
        if (fakeOk)
        {
            passed++;
        }

        // --- 单项：**指针的顺序无关**（任一副本命中即算）---
        var probe = Path.Combine(tmp, "pointer-probe");
        var pj = Path.Combine(probe, "proj");
        var pa = Path.Combine(probe, "arch");
        var pl = Path.Combine(probe, "live");
        Directory.CreateDirectory(pj);
        Directory.CreateDirectory(pa);
        Directory.CreateDirectory(pl);
        File.WriteAllText(Path.Combine(pj, "doc.md"), "旧副本：还没有那句话\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(pa, "doc.md"), "新副本：这里有 NEEDLE\n", new UTF8Encoding(false));
        var (hit, hitErr, _) = ResolvePointer("doc.md::NEEDLE", [pj, pa, pl]);
        var (nowhere, nowhereErr, _) = ResolvePointer("doc.md::一句哪儿都没有的话", [pj, pa, pl]);
        var ptrOk = hit is not null && hitErr is null && nowhere is null && nowhereErr is not null;
        Console.WriteLine($"  {(ptrOk ? "✅" : "❌")} {"指针的顺序无关",-24} 期望「先命中的副本是旧版也不判悬空 + 哪儿都没仍要红」 → {(ptrOk ? "两个都对" : "顺序仍然决定结论 / 负例没拦住")}");
        if (ptrOk)
        {
            passed++;
        }

        // --- 单项：**删除登记**闸门有牙（罐头 svn 输出：有 `!` ⇒ 必须红；没有 ⇒ 必须绿）---
        RunHook = (_, _, _, _) => (0, "!       memory/test.md\nM       docs/PITFALLS.md\n");
        var withBang = SvnArchive.CheckRepoDeletions().Ok;
        RunHook = (_, _, _, _) => (0, "M       docs/PITFALLS.md\n");
        var withoutBang = SvnArchive.CheckRepoDeletions().Ok;
        RunHook = null;
        var delOk = withBang == false && withoutBang;
        Console.WriteLine($"  {(delOk ? "✅" : "❌")} {"删除登记（罐头 svn）",-24} 期望「有 `!` ⇒ 红 / 没有 ⇒ 绿」 → {(delOk ? "两个都对" : "负例没拦住 / 正例被误伤")}");
        if (delOk)
        {
            passed++;
        }

        // --- 单项：**未纳版件**闸门有牙（罐头 svn：未纳版源码 ⇒ 红；只剩产物 ⇒ 绿）---
        static bool IsArchive(string root) => root.EndsWith("whitebox-workspace", StringComparison.Ordinal);
        RunHook = (file, list, _, _) => file == "svn" && list.Length == 2
            ? (0, IsArchive(list[1]) ? "" : "?       tools/newdir/Program.cs\n?       benchmark/runs/20260101-x/meta.xml\n")
            : (0, "");
        var unverRed = SvnArchive.CheckUnversioned().Ok == false;
        RunHook = (file, list, _, _) => file == "svn" && list.Length == 2
            ? (0, IsArchive(list[1]) ? "" : "?       benchmark/runs/20260101-x/meta.xml\n")
            : (0, "");
        var unverGreen = SvnArchive.CheckUnversioned().Ok;
        RunHook = null;
        var unverOk = unverRed && unverGreen;
        Console.WriteLine($"  {(unverOk ? "✅" : "❌")} {"未纳版件（罐头 svn）",-24} 期望「未纳版源码 ⇒ 红 / 只剩产物 ⇒ 绿」 → {(unverOk ? "两个都对" : "负例没拦住 / 正例被误伤")}");
        if (unverOk)
        {
            passed++;
        }

        var total = cases.Length + 4;
        Console.WriteLine($"汇总：{passed}/{total} 负例按预期变红{(passed == total ? "" : "  ← 有负例没拦住")}");
        if (Environment.GetEnvironmentVariable("KEEP_SELFTEST") == "1")
        {
            Console.WriteLine($"   （KEEP_SELFTEST=1，副本保留在 {tmp}）");
        }
        else
        {
            RecursiveDelete(tmp);
        }

        return passed == total ? 0 : 1;
    }

}
