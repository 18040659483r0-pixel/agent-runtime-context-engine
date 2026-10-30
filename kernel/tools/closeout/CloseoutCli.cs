using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
    /// **收尾的扩展环节**（主人 2026-10-16 12:2x 定）：CORE 对 VCS 零引用，版本库那几件事全在实现里。
    /// <para>`NoArchive` = 标准模式（不用任何 SVN/Git）；`SvnArchive` = 我们自己用的那个扩展选项。</para>
    /// <para>⚠️ 第 1 批**保持与旧行为逐字节一致**（选择仍按本仓根 `.svn` 推断）；第 3 批改显式配置。</para>
    /// </summary>
    /// <summary>**模式由声明决定**（`tools/closeout/archive.xml`）；没声明才按 `.svn` 推断，且会把「这是推断」说出来。</summary>
    internal static ArchiveSelection ArchiveSel => CloseoutArchiveFactory.ResolveWithFallback();

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
    private static readonly Regex ConvergeRe = new(@"- \*\*收敛点（机器可判）\*\*：存档 r(\d+|none|\?)[^\n]*", RegexOptions.Compiled);

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
        "     ③ --l4-sync（条目→L4 机械搬运；--apply 里也会自动跑一次）\n" +
        "     ④ --kn-plan → --kn-link（能挂就挂；全部挂不上才新建，并逐层递归到 L1）\n" +
        "     ⑤ --apply（幂等重生派生物）→ ⑥ --promote（**两库一起晋级**）\n" +
        "     ⑦ 提交（代码仓 + 存档区）→ ⑧ --close（绿才写收敛点）→ ⑨ 再提交（水位 + 账本）→ ⑩ 再 --check\n" +
        "   ⚠️ 路径基准看 `--where`，**不看 shell 的 cd**（工具 cwd = 程序集目录，相对路径会全落空）。";

    private sealed record Gate(string Name, string Sev, string[] Cs, string Why);

    private static readonly Gate[] Gates =
    [
        new("坑集索引", "block", ["--pitfalls-index-check", PitfallsSrc], "源改 ⇒ 必须重跑索引（派生落后于源）"),
        new("公开坑集变体", "block", ["--pitfalls-public-check", PitfallsSrc], "公开变体必须与重算一致"),
        new("知识树", "report", ["--kn-check", Live],
            "知识树结构 + 派生层逐字节一致（只常驻 L1）；红 ⇒ 修活版并重建派生"),
        new("冻结语料", "report", ["--frozen-corpus", Live, "--frozen-corpus-check"],
            "主人 2026-09-23 令：语料 bump 类属记账项、不阻断收尾（但仍上账）"),
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

        // 自动退化（2026-10-18 主人令）：SVN 不可达 ⇒ 本进程已改走 NoArchive。
        // 这里**不判 block**：收尾不应该因为没 SVN 就无法完成；但要留下一条可见的 report 红。
        if (sel.Kind == "none" && sel.Declared && sel.Note.Contains("自动降级", StringComparison.Ordinal))
        {
            return R("存档声明", "report", false, sel.Note,
                ["按「连不上一次 SVN 就立即退化为不用 SVN」执行；本次收尾不写存档区、不提交 SVN",
                 "要恢复 SVN 收尾：服务器恢复后重新跑一次即可（探针成功就不再降级）"],
                "收尾不被版本库不可达阻断；降级必须可见");
        }
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
        if (hits.Count > 0 && revNow is int rn
            && int.TryParse(hits[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hitRev)
            && hitRev < rn)
        {
            var gap = rn - hitRev;
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
            ok ? null :
            [
                "规则：新坑必须声明落点类别；确定性坑应做成闸门（gate）",
                // 2026-10-22：把「该去哪声明、谁能改」直接印出来（真机为此白查了 2 轮）。
                $"声明落在：{Path.GetRelativePath(Project, PlacementFile)}（tools/ 属 OC 面 ⇒ WB 不改它，只在公告板提请求）",
            ],
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
    /// <summary>用户决策取向：三本齐 + 决策库四层齐 + 条目都有状态（**只上账 · report**，不阻断 —— 主人 2026-10-17 令）。</summary>
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

    // ================================================================ 双库生长 + 回执（v86）

    internal static readonly string ReceiptPath = Path.Combine(Project, "tools", "closeout", "receipt.xml");

    internal sealed record KnowledgeFacts(
        int Version, string File, string Sha, long Bytes, string LiveSha,
        int L1, int L2, int L3, int L4, int Orphans, int Unmarked, bool FileOk);

    private static int CountKn(string text, string layer) =>
        Regex.Matches(text, @"<!--kn:\s*" + layer + "-").Count;

    /// <summary>L4 区里**没有节点标记**的正文块数 —— 派生树看不见它们（summary 的 Orphans 会假绿）。</summary>
    internal static int UnmarkedL4Blocks(string text)
    {
        var start = text.IndexOf("## L4", StringComparison.Ordinal);
        if (start < 0) return 0;
        var nl = text.IndexOf('\n', start);
        if (nl < 0) return 0;
        var body = text[(nl + 1)..];
        var end = body.IndexOf("\n## ", StringComparison.Ordinal);
        if (end >= 0) body = body[..end];
        var blocks = Regex.Split(body, @"\r?\n\s*\r?\n")
            .Select(b => b.Trim())
            .Where(b => b.Length > 0
                        && !b.StartsWith(">", StringComparison.Ordinal)
                        && !b.StartsWith("#", StringComparison.Ordinal))
            .ToList();
        return blocks.Count(b => !b.Contains("<!--kn:", StringComparison.Ordinal));
    }

    internal static KnowledgeFacts ReadKnowledge()
    {
        var ledger = Path.Combine(KnowledgeRoot, ".ledger.xml");
        if (!File.Exists(ledger) || !File.Exists(KnowledgeMd))
        {
            return new KnowledgeFacts(0, string.Empty, string.Empty, 0, string.Empty, 0, 0, 0, 0, 0, 0, false);
        }

        var cur = XDocument.Load(ledger).Root?.Element("Current");
        var version = int.TryParse(cur?.Element("Version")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var file = cur?.Element("File")?.Value ?? string.Empty;
        var sha = cur?.Element("Sha256")?.Value ?? string.Empty;
        var bytes = long.TryParse(cur?.Element("Bytes")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0;
        var path = file.Length == 0 ? string.Empty : Path.Combine(KnowledgeRoot, file);
        var fileOk = path.Length > 0 && File.Exists(path) && string.Equals(Sha256File(path), sha, StringComparison.OrdinalIgnoreCase);

        var text = File.ReadAllText(KnowledgeMd, Encoding.UTF8);
        var orphan = 0;
        var summary = Path.Combine(KnowledgeRoot, ".derived", "summary.xml");
        if (File.Exists(summary))
        {
            int.TryParse(XDocument.Load(summary).Root?.Element("Orphans")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out orphan);
        }

        return new KnowledgeFacts(
            version, file, sha, bytes, Sha256File(KnowledgeMd),
            CountKn(text, "L1"), CountKn(text, "L2"), CountKn(text, "L3"), CountKn(text, "L4"),
            orphan, UnmarkedL4Blocks(text), fileOk);
    }

    /// <summary>知识库生长：版本文件对得上 + 四层齐 + L4 无未标记正文 + 内容变了必须晋级。</summary>
    internal static Res CheckKnowledgeGrowth()
    {
        var k = ReadKnowledge();
        var problems = new List<string>();
        if (k.Version <= 0) problems.Add("账本缺 Current.Version");
        if (!k.FileOk) problems.Add($"当前版本文件缺失或 sha 对不上（v{k.Version} / {k.File}）");
        if (k.Unmarked > 0) problems.Add($"L4 区有 {k.Unmarked} 段无节点标记（孤儿正文，派生树看不见）");
        if (k.Orphans > 0) problems.Add($"派生树 Orphans={k.Orphans}");
        if (k.L1 == 0 || k.L2 == 0 || k.L3 == 0 || k.L4 == 0) problems.Add("四层不齐");

        int? prevVersion = null;
        string? prevLiveSha = null;
        if (File.Exists(ReceiptPath))
        {
            var kn = XDocument.Load(ReceiptPath).Root?.Element("Knowledge");
            if (int.TryParse(kn?.Attribute("version")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pv)) prevVersion = pv;
            prevLiveSha = kn?.Attribute("liveSha")?.Value;
        }

        if (prevVersion is int previous && prevLiveSha is not null
            && !string.Equals(k.LiveSha, prevLiveSha, StringComparison.OrdinalIgnoreCase)
            && k.Version <= previous)
        {
            problems.Add($"活版内容变了但版本仍 v{k.Version}（上次回执 v{previous}）⇒ 本次收尾没有晋级");
        }

        var ok = problems.Count == 0;
        return R("知识库生长", "block", ok,
            ok
                ? $"v{k.Version} · L1 {k.L1}/L2 {k.L2}/L3 {k.L3}/L4 {k.L4} · 未标记 L4 {k.Unmarked} · Orphans {k.Orphans}"
                : string.Join("；", problems),
            problems,
            "收尾必须两库生长：知识版本晋级 + L4 全部有节点标记；内容变了不晋级 = 没生长");
    }

    /// <summary>
    /// **条目 → L4 覆盖**（2026-10-27）：坑集/决策的每一条现场条目都必须有一条带 `src=…#id` 的 L4 节点。
    /// <para>水位 = 标记本身（不另建文件）；缺口用 `--l4-gap` 可单独查看。</para>
    /// </summary>
    internal static Res CheckEntryToL4()
    {
        var (above, backlog) = L4Gap.Gap();
        var problems = new List<string>();
        if (above.Count > 0)
        {
            problems.Add($"水位以上有 {above.Count} 条未进 L4：" + string.Join(", ", above.Take(12))
                         + (above.Count > 12 ? $" …（共 {above.Count}）" : string.Empty));
        }

        var ok = above.Count == 0;
        var summary = ok
            ? $"水位以上全部有 L4 锚 · 存量欠账 {backlog} 条（--l4-sync 分批回填）"
            : string.Join("；", problems);
        return R("条目→L4 覆盖", "block", ok, summary, problems,
            "条目必须逐条变成 L4 节点（`src=…#id`）并由远端 AI 抽象 L3/L2/L1；水位见 tools/closeout/l4-watermark.xml");
    }

    /// <summary>
    /// **知识树链完整性**（2026-10-27 主人问「如何让生长真正成立」当场量出的窟窿）：
    /// 旧闸门只数节点（L1~L4 各多少）与一个「orphans」，而实测 424 个 L4 里 **395 个没有任何父节点引用**、
    /// 29 条 L1/L2/L3 各只挂 1 个子节点（29 条平行细链）—— 照样印绿。
    /// <para>本闸门验的是**链**，不是**数**：单亲、层序、可达、深度。</para>
    /// </summary>
    internal static Res CheckKnowledgeLinks()
    {
        var path = Path.Combine(Live, "knowledge", "knowledge.md");
        if (!File.Exists(path))
        {
            return R("知识树链", "block", false, "找不到 knowledge.md", [], "知识树必须可解析");
        }

        var text = File.ReadAllText(path);
        var nodes = new Dictionary<string, (int Layer, List<string> Next)>(StringComparer.Ordinal);
        var rx = new Regex(@"<!--kn:\s*(L([1-4])-[^\s>]+)([^>]*)-->", RegexOptions.Multiline);
        foreach (Match m in rx.Matches(text))
        {
            var id = m.Groups[1].Value;
            var layer = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var next = new List<string>();
            var at = m.Groups[3].Value.IndexOf("next=", StringComparison.Ordinal);
            if (at >= 0)
            {
                var raw = m.Groups[3].Value[(at + 5)..].Split(' ', '>')[0];
                next.AddRange(raw.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            nodes[id] = (layer, next);
        }

        var problems = new List<string>();
        var parents = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (id, node) in nodes)
        {
            foreach (var child in node.Next)
            {
                if (!nodes.TryGetValue(child, out var target))
                {
                    problems.Add($"{id} → {child}（目标不存在）");
                    continue;
                }

                if (target.Layer != node.Layer + 1)
                {
                    problems.Add($"{id}(L{node.Layer}) → {child}(L{target.Layer})（跨层/反向）");
                }

                parents[child] = parents.TryGetValue(child, out var c) ? c + 1 : 1;
            }
        }

        // 单亲：每个非 L1 节点必须被**恰好一个**父引用（0 = 孤儿；>1 = 两张树粘在一起）。
        foreach (var (id, node) in nodes.Where(static p => p.Value.Layer > 1))
        {
            parents.TryGetValue(id, out var count);
            if (count == 0)
            {
                problems.Add($"{id} 无父（孤儿）");
            }
            else if (count > 1)
            {
                problems.Add($"{id} 有 {count} 个父（不是一棵树）");
            }
        }

        // 可达 + 深度：从每个 L1 往下 DFS，必须走到 L4，且 L4 只在这一条路上。
        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        void Walk(string id, int d)
        {
            if (depth.TryGetValue(id, out var seen) && seen >= d)
            {
                return;
            }

            depth[id] = d;
            if (!nodes.TryGetValue(id, out var node))
            {
                return;
            }

            foreach (var child in node.Next)
            {
                Walk(child, d + 1);
            }
        }

        foreach (var id in nodes.Where(static p => p.Value.Layer == 1).Select(static p => p.Key))
        {
            Walk(id, 1);
        }

        var unreachable = nodes.Keys.Where(id => !depth.ContainsKey(id)).ToList();
        if (unreachable.Count > 0)
        {
            problems.Add($"从 L1 不可达：{unreachable.Count} 个（如 {string.Join(", ", unreachable.Take(5))}）");
        }

        var wrongDepth = nodes.Where(p => p.Value.Layer == 4 && depth.TryGetValue(p.Key, out var d) && d != 4)
            .Select(p => p.Key).ToList();
        if (wrongDepth.Count > 0)
        {
            problems.Add($"深度不等于 4 的 L4：{wrongDepth.Count} 个");
        }

        var fanout = nodes.GroupBy(p => p.Value.Layer)
            .ToDictionary(g => g.Key, g => g.Max(p => p.Value.Next.Count));
        var l4 = nodes.Count(p => p.Value.Layer == 4);

        var ok = problems.Count == 0;
        return R("知识树链", "block", ok,
            ok
                ? $"单亲/层序/可达/深度全部成立 · L4 {l4} 条 · 层数 {nodes.Count(p => p.Value.Layer == 1)}/{nodes.Count(p => p.Value.Layer == 2)}/{nodes.Count(p => p.Value.Layer == 3)} · 最大扇出 L1={fanout.GetValueOrDefault(1)} L2={fanout.GetValueOrDefault(2)} L3={fanout.GetValueOrDefault(3)}"
                  + (nodes.Count(p => p.Value.Layer == 2) >= nodes.Count(p => p.Value.Layer == 3) ? " · ⚠️ 分组未压缩（L2 ≥ L3：一个 L2 没盖多个 L3）" : string.Empty)
                : $"链不完整：{problems.Count} 处（前 3：{string.Join("；", problems.Take(3))}）",
            problems,
            "知识树是**链**不是**数**：每个 L4 必须被恰好一个 L3 引用，L3→L2→L1 同理，深度恰 4");
    }

    /// <summary>
    /// **L4 来源锚**（2026-10-27 主人令）：每条 L4 必须带 `src=<文件>#<id>`，否则它不可回溯到任何现场条目。
    /// <para>为什么单列一条：`--l4-sync` 生成的一定带；**手写的 L4** 常常不带，
    /// 而「未标记 L4」那条只查节点标记、不查来源 —— 于是"凭印象写的知识"照样绿。</para>
    /// </summary>
    internal static Res CheckL4Sources()
    {
        var path = Path.Combine(Live, "knowledge", "knowledge.md");
        if (!File.Exists(path))
        {
            return R("L4 来源锚", "block", false, "找不到 knowledge.md", [], "每条 L4 必须可回溯到现场条目");
        }

        var text = File.ReadAllText(path);
        var all = Regex.Matches(text, @"<!--kn:\s*(L4-[^\s>]+)([^>]*)-->", RegexOptions.Multiline);
        var missing = new List<string>();
        foreach (Match m in all)
        {
            if (!m.Groups[2].Value.Contains("src=", StringComparison.Ordinal))
            {
                missing.Add(m.Groups[1].Value);
            }
        }

        var ok = missing.Count == 0;
        return R("L4 来源锚", "block", ok,
            ok ? $"{all.Count} 条 L4 全部带 src=" : $"{missing.Count}/{all.Count} 条 L4 缺 src=（如 {string.Join(", ", missing.Take(5))}）",
            missing,
            "L4 是证据层：每条必须写清它来自哪个文件哪一条（src=<文件>#<id>）");
    }

    /// <summary>
    /// **决策树链**（2026-10-27 主人令：决策库要与知识库同效）：单亲 · 逐层 · 可达 · 深度 4 · 每条 L4 带 D 号。
    /// <para>形态：L1 `- **L1-0001 标题**：… → L2-0001（源 D-0001）`；L2 `- **L2-0001** → L3-0001`；
    /// L3 `- **L3-0001**（源 L2-0001）…`；L4 `- **L4-0001** 源 L3-0001 · D-0001：「原话」`。</para>
    /// </summary>
    internal static Res CheckDecisionLinks()
    {
        var path = Path.Combine(Live, "DECISION-LIBRARY.md");
        if (!File.Exists(path))
        {
            return R("决策树链", "block", false, "找不到 DECISION-LIBRARY.md", [], "决策库必须可解析");
        }

        var nodes = new Dictionary<string, (int Layer, string? Parent, List<string> Child, string Line)>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllText(path).Replace("\r\n", "\n").Split('\n'))
        {
            var m = Regex.Match(line, @"-\s*\*\*(L([1-4])-\d{4})\b");
            if (!m.Success)
            {
                continue;
            }

            var parent = Regex.Match(line, @"源\s*(L[1-4]-\d{4})");
            var child = Regex.Matches(line, @"→\s*(L[1-4]-\d{4})").Select(x => x.Groups[1].Value).ToList();
            nodes[m.Groups[1].Value] = (
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                parent.Success ? parent.Groups[1].Value : null,
                child,
                line);
        }

        var problems = new List<string>();
        var parents = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (id, node) in nodes)
        {
            if (node.Parent is { } p)
            {
                parents[id] = parents.TryGetValue(id, out var c) ? c + 1 : 1;
                if (!nodes.TryGetValue(p, out var pn))
                {
                    problems.Add($"{id} 的父 {p} 不存在");
                }
                else if (pn.Layer != node.Layer - 1)
                {
                    problems.Add($"{id}(L{node.Layer}) 的父 {p}(L{pn.Layer}) 不是上一层");
                }
            }

            foreach (var child in node.Child)
            {
                if (!nodes.TryGetValue(child, out var cn))
                {
                    problems.Add($"{id} → {child}（不存在）");
                }
                else if (cn.Layer != node.Layer + 1)
                {
                    problems.Add($"{id}(L{node.Layer}) → {child}(L{cn.Layer}) 不是下一层");
                }
            }
        }

        // `→` 是父节点的子指针 ⇒ 对**没有显式 `源`** 的节点，用它反推父（L1→L2、L2→L3 就靠这条）。
        var implicitParents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (id, node) in nodes)
        {
            foreach (var child in node.Child)
            {
                if (!implicitParents.TryGetValue(child, out var list))
                {
                    implicitParents[child] = list = [];
                }

                list.Add(id);
            }
        }

        foreach (var (id, node) in nodes.Where(static p => p.Value.Layer > 1))
        {
            var explicitCount = parents.TryGetValue(id, out var count) ? count : 0;
            if (explicitCount == 0 && implicitParents.TryGetValue(id, out var from))
            {
                count = from.Count;
                if (count == 1)
                {
                    parents[id] = 1;
                }
            }

            if (count == 0)
            {
                problems.Add($"{id} 无父（孤儿）");
            }
            else if (count > 1)
            {
                problems.Add($"{id} 有 {count} 个父");
            }
        }

        // 子集 = 显式 → 的目标 ∪ 以我为父的节点。
        var kids = nodes.ToDictionary(
            static p => p.Key,
            p => p.Value.Child.Concat(nodes.Where(x => x.Value.Parent == p.Key).Select(x => x.Key)).Distinct(StringComparer.Ordinal).ToList(),
            StringComparer.Ordinal);

        var depth = new Dictionary<string, int>(StringComparer.Ordinal);
        void Walk(string id, int d)
        {
            if (depth.TryGetValue(id, out var seen) && seen >= d)
            {
                return;
            }

            depth[id] = d;
            foreach (var k in kids.TryGetValue(id, out var list) ? list : [])
            {
                Walk(k, d + 1);
            }
        }

        foreach (var id in nodes.Where(static p => p.Value.Layer == 1).Select(static p => p.Key))
        {
            Walk(id, 1);
        }

        var unreachable = nodes.Keys.Where(id => !depth.ContainsKey(id)).ToList();
        if (unreachable.Count > 0)
        {
            problems.Add($"从 L1 不可达：{unreachable.Count} 个（如 {string.Join(", ", unreachable.Take(5))}）");
        }

        var l4 = nodes.Where(static p => p.Value.Layer == 4).ToList();
        var noSource = l4.Where(static p => !Regex.IsMatch(p.Value.Line, @"D-\d{4}")).Select(static p => p.Key).ToList();
        if (noSource.Count > 0)
        {
            problems.Add($"L4 缺 D 号：{noSource.Count} 个（如 {string.Join(", ", noSource.Take(5))}）");
        }

        var wrongDepth = l4.Count(p => depth.TryGetValue(p.Key, out var d) && d != 4);
        if (wrongDepth > 0)
        {
            problems.Add($"深度不等于 4 的 L4：{wrongDepth} 个");
        }

        var counts = new[] { 1, 2, 3 }.Select(l => nodes.Count(p => p.Value.Layer == l)).ToArray();
        var ok = problems.Count == 0;
        return R("决策树链", "block", ok,
            ok
                ? $"单亲/层序/可达/深度全部成立 · L4 {l4.Count} 条 · 层数 {counts[0]}/{counts[1]}/{counts[2]}"
                  + (counts[1] >= counts[2] ? " · ⚠️ 分组未压缩（L2 ≥ L3）" : string.Empty)
                : $"链不完整：{problems.Count} 处（前 3：{string.Join("；", problems.Take(3))}）",
            problems,
            "决策库与知识库同形：每个 L4 被恰好一个 L3 引用，L3→L2→L1 同理，深度恰 4，每条 L4 带 D 号");
    }

    /// <summary>决策库收敛：条目全部有 L4 归属、无孤儿、上行可达。</summary>
    internal static Res CheckDecisionGrowth()
    {
        DecisionConverge.Analysis a;
        try
        {
            a = DecisionConverge.Analyze(Live);
        }
        catch (Exception ex)
        {
            return R("决策库收敛", "block", false, ex.Message, [ex.Message], "决策库必须可解析、条目全部收敛");
        }

        var ok = a.Entries > 0 && a.Gaps == 0 && a.Orphans == 0
                 && a.L1 > 0 && a.L2 > 0 && a.L3 > 0 && a.L4 > 0;
        var detail = new List<string>();
        if (a.Gaps > 0) detail.Add("空档：" + string.Join(", ", a.GapIds));
        if (a.Orphans > 0) detail.Add("孤儿：" + string.Join(", ", a.OrphanIds));
        return R("决策库收敛", "block", ok,
            $"{a.Converged}/{a.Entries} 已收敛 · 空档 {a.Gaps} · 孤儿 {a.Orphans} · L1={a.L1} L2={a.L2} L3={a.L3} L4={a.L4}",
            detail,
            "水位推进不等于收敛：每条 D 都必须走到 L4→L3→L2→L1");
    }

    /// <summary>收尾回执（4 组事实 + 与上次的增量）—— OC 只核这几个字段，不重读全文。</summary>
    internal static XElement BuildReceipt(string? archiveOverride = null, string? projectOverride = null)
    {
        var k = ReadKnowledge();
        var a = DecisionConverge.Analyze(Live);
        // 两个版本都给全 ⇒ 不探远端（--receipt 用它，避免断网时 2×45s 白等）。
        ArchiveReadings? readings = archiveOverride is null || projectOverride is null ? Vcs.Read() : null;
        var aRev = archiveOverride ?? readings?.ArchiveRev?.ToString(CultureInfo.InvariantCulture) ?? "none";
        var pRev = projectOverride ?? readings?.ProjectRev?.ToString(CultureInfo.InvariantCulture) ?? "none";

        string? prevAt = null;
        string? prevLiveSha = null;
        int? prevVersion = null;
        var prevL1 = 0;
        var prevGaps = 0;
        if (File.Exists(ReceiptPath))
        {
            var root = XDocument.Load(ReceiptPath).Root;
            var kn = root?.Element("Knowledge");
            var de = root?.Element("Decision");
            prevAt = root?.Attribute("at")?.Value;
            prevLiveSha = kn?.Attribute("liveSha")?.Value;
            if (int.TryParse(kn?.Attribute("version")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pv)) prevVersion = pv;
            int.TryParse(kn?.Attribute("l1")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out prevL1);
            int.TryParse(de?.Attribute("gaps")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out prevGaps);
        }

        return new XElement("CloseoutReceipt",
            new XAttribute("schema", "1"),
            new XAttribute("at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            new XElement("Convergence",
                new XAttribute("archive", aRev),
                new XAttribute("project", pRev),
                new XAttribute("fingerprint", FingerprintParts()[..12])),
            new XElement("Knowledge",
                new XAttribute("version", k.Version),
                new XAttribute("file", k.File),
                new XAttribute("bytes", k.Bytes),
                new XAttribute("sha256", k.Sha),
                new XAttribute("liveSha", k.LiveSha),
                new XAttribute("l1", k.L1),
                new XAttribute("l2", k.L2),
                new XAttribute("l3", k.L3),
                new XAttribute("l4", k.L4),
                new XAttribute("orphans", k.Orphans),
                new XAttribute("unmarkedL4", k.Unmarked),
                new XAttribute("promoted", k.FileOk)),
            new XElement("Decision",
                new XAttribute("entries", a.Entries),
                new XAttribute("converged", a.Converged),
                new XAttribute("gaps", a.Gaps),
                new XAttribute("orphans", a.Orphans),
                new XAttribute("l1", a.L1),
                new XAttribute("l2", a.L2),
                new XAttribute("l3", a.L3),
                new XAttribute("l4", a.L4)),
            new XElement("Vcs",
                new XAttribute("archiveRev", aRev),
                new XAttribute("projectRev", pRev)),
            new XElement("Growth",
                new XAttribute("sinceReceipt", prevAt ?? "none"),
                new XAttribute("knowledgeVersionDelta", prevVersion is int before ? k.Version - before : k.Version),
                new XAttribute("l1Delta", k.L1 - prevL1),
                new XAttribute("decisionGapDelta", a.Gaps - prevGaps)));
    }

    /// <summary>水位里的收敛点版本（读不到 ⇒ ("","")，由调用方回退到远端读数）。</summary>
    private static (string Archive, string Project) WatermarkRevs()
    {
        if (!File.Exists(WaterLive)) return (string.Empty, string.Empty);
        var m = Regex.Match(File.ReadAllText(WaterLive, Encoding.UTF8),
            @"- \*\*收敛点（机器可判）\*\*：存档 r(\d+)[^\n]*?代码仓 r(\d+)", RegexOptions.Multiline);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : (string.Empty, string.Empty);
    }

    /// <summary>`--receipt`：打印回执；`--write` 同时落 `tools/closeout/receipt.xml`（收尾基线）。</summary>
    internal static int Receipt(bool write)
    {
        // 只读回执：版本号取**水位里已盖章的收敛点**，不为了两个数字去等远端探针。
        var (aRev, pRev) = WatermarkRevs();
        var receipt = BuildReceipt(aRev, pRev);
        Console.WriteLine(receipt.ToString());
        if (write)
        {
            receipt.Save(ReceiptPath);
            Console.WriteLine("[回执] 已写 " + ReceiptPath);
        }

        return 0;
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

        // v86：两库生长是**收尾完成度**的一部分（不是可选报告）。
        res.Add(CheckKnowledgeGrowth() with { Module = "knowledge", Class = "core" });
        res.Add(CheckDecisionGrowth() with { Module = "decision", Class = "core" });

        // 2026-10-27 主人令：坑/决策条目必须**逐条进 L4**（再由远端 AI 抽象到 L3/L2/L1）。
        res.Add(CheckEntryToL4() with { Module = "knowledge", Class = "core" });
        res.Add(CheckKnowledgeLinks() with { Module = "knowledge", Class = "core" });
        res.Add(CheckL4Sources() with { Module = "knowledge", Class = "core" });
        res.Add(CheckDecisionLinks() with { Module = "decision", Class = "core" });
        res.Add(CheckDecisionVersion() with { Module = "decision", Class = "core" });

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

        // **非绿项一行给全**（省掉「再翻一遍全文找红项」的那一轮）。
        var redNames = results.Where(r => !r.Ok).Select(r => r.Name).Distinct().ToArray();
        if (redNames.Length > 0)
        {
            Console.WriteLine($"非绿项（{redNames.Length}）：{string.Join(" · ", redNames)}");
        }

        // **命令与落点直接给全**（2026-10-22 真机：模型为找 `--promote` 的正确调用与坑集真实落点，白烧 7 轮）。
        // 协议第 23 条要求「收尾=固定流水线、每条一次、不读源码猜 flag」——那就必须把可照抄的命令印在这里。
        Console.WriteLine();
        Console.WriteLine("[WB] 收尾命令（照抄；每条只跑一次；下面全是绝对路径）");
        Console.WriteLine($"  1) dotnet {SelfDll} --check");
        Console.WriteLine("  2) 写正文（一律绝对路径，写完回读字节）");
        Console.WriteLine($"      坑集：{Path.Combine(Project, "docs", "PITFALLS.md")}");
        Console.WriteLine($"      知识：{KnowledgeMd}（四层；写完跑 --promote 才升版本）");
        Console.WriteLine($"      日记：{Path.Combine(Live, "memory", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md")}");
        Console.WriteLine($"  3) dotnet {SelfDll} --apply");
        Console.WriteLine($"  4) dotnet {SelfDll} --promote");
        Console.WriteLine($"  5) dotnet {SelfDll} --close");
        Console.WriteLine($"  6) dotnet {SelfDll} --check");
        Console.WriteLine($"  （路径拿不准：dotnet {SelfDll} --where）");

        if (results.Any(r => r.Name == "存档声明" && r.Summary.Contains("自动降级", StringComparison.Ordinal)))
        {
            Console.WriteLine("⛔ 本轮 SVN 降级：**不要执行 svn commit / svn update**；跳过提交与存档区，记为 blocked 即可。");
            Console.WriteLine("   （服务器恢复后重跑一次 `--check` 即自动退出降级，再按正常顺序补提交。）");
        }

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
        Console.WriteLine("  正文落点（**都在这里，别去活版猜**）：");
        Console.WriteLine($"     坑集  {Path.Combine(Project, "docs", "PITFALLS.md")}");
        Console.WriteLine($"     知识  {KnowledgeMd}");
        Console.WriteLine($"     日记  {Path.Combine(Live, "memory", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".md")}");
        Console.WriteLine($"     水位  {WaterLevel}");
        Console.WriteLine();
        Console.WriteLine("  规范调用（绝对路径，任何 cwd 都能跑）：");
        Console.WriteLine($"     dotnet {SelfDll} --check");
        Console.WriteLine();
        Console.WriteLine(CloseOrder);
        return 0;
    }

    // ================================================================ actions

    /// <summary>
    /// **知识晋级**（`--promote`）：把活版 `knowledge.md` 拍成 `versions/knowledge-vNNNN.md` 并更新账本
    /// 的 `Current` 与 `Versions`（只增不删）。
    /// <para>幂等：活版 sha == Current.sha ⇒ 直接 no-op。收尾流程用它，**不靠人手工改 XML**。</para>
    /// </summary>
    public static int Promote(bool dry)
    {
        var ledgerPath = Path.Combine(KnowledgeRoot, ".ledger.xml");
        if (!File.Exists(KnowledgeMd) || !File.Exists(ledgerPath))
        {
            Console.Error.WriteLine($"[KN] 缺文件：{KnowledgeMd} / {ledgerPath}");
            return 2;
        }

        var doc = XDocument.Load(ledgerPath);
        var current = doc.Root?.Element("Current") ?? throw new InvalidDataException("知识账本缺 <Current>");
        var version = int.Parse(current.Element("Version")?.Value ?? "0", CultureInfo.InvariantCulture);
        var currentSha = current.Element("Sha256")?.Value ?? string.Empty;
        var liveSha = Sha256File(KnowledgeMd);
        var bytes = new FileInfo(KnowledgeMd).Length;

        if (string.Equals(liveSha, currentSha, StringComparison.OrdinalIgnoreCase))
        {
Console.WriteLine($"[KN] 活版与 v{version} 逐字节一致 ⇒ 无需晋级（no-op）");
            return PromoteDecisions(dry);   // 知识库没事不等于决策库没事
        }

        var next = version + 1;
        var relative = $"versions/knowledge-v{next:0000}.md";
        var target = Path.Combine(KnowledgeRoot, relative);
        var note = KnowledgeNote();

        Console.WriteLine($"[KN] 晋级 v{version} → v{next}：{relative}（{bytes} B · sha {liveSha[..12]}）");
        if (dry) return 0;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(KnowledgeMd, target, overwrite: true);

        current.SetElementValue("Version", next);
        current.SetElementValue("File", relative);
        current.SetElementValue("Sha256", liveSha);
        current.SetElementValue("Bytes", bytes);
        current.SetElementValue("At", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        current.SetElementValue("Source", "promote");
        current.SetElementValue("Origin", "closeout --promote");
        current.SetElementValue("Note", note);

        var versions = doc.Root!.Element("Versions");
        versions?.Add(new XElement("Version",
            new XElement("Version", next),
            new XElement("File", relative),
            new XElement("Sha256", liveSha),
            new XElement("Bytes", bytes),
            new XElement("At", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            new XElement("Source", "promote"),
            new XElement("Origin", "closeout --promote"),
            new XElement("Note", note)));

        doc.Save(ledgerPath);
        Console.WriteLine("[KN] 账本已更新（Current + Versions 两处）");

        // **决策库同款通道**（2026-10-27 主人令）：活版变了也必须晋级，不能只有知识库有版本。
        return PromoteDecisions(dry);
    }

    // ================================================================ 决策库晋级（另有账本）

    private static string DecisionRoot => Path.Combine(Live, "decision-versions");
    private static string DecisionLedger => Path.Combine(DecisionRoot, ".ledger.xml");
    private static string DecisionMd => Path.Combine(Live, "DECISION-LIBRARY.md");

    /// <summary>决策库晋级：活版 → decision-versions/DECISION-LIBRARY-vNNNN.md + 账本两处（已在 = no-op）。</summary>
    private static int PromoteDecisions(bool dry)
    {
        if (!File.Exists(DecisionMd))
        {
            Console.WriteLine("[DC] 找不到 DECISION-LIBRARY.md ⇒ 跳过晋级");
            return 0;
        }

        Directory.CreateDirectory(DecisionRoot);
        XDocument doc;
        if (File.Exists(DecisionLedger))
        {
            doc = XDocument.Load(DecisionLedger);
            if (doc.Root?.Element("Current") is null)
            {
                throw new InvalidDataException("决策账本缺 <Current>");
            }
        }
        else
        {
            // 首次：先登记已有版本（若 v0001 在就按它的 sha 起账），下一次再推新版本。
            var existing = Path.Combine(DecisionRoot, "DECISION-LIBRARY-v0001.md");
            var has = File.Exists(existing);
            doc = new XDocument(new XElement("Ledger",
                new XElement("Schema", "decision-repo/ledger/1"),
                new XElement("Current",
                    new XElement("Version", has ? 1 : 0),
                    new XElement("File", has ? "DECISION-LIBRARY-v0001.md" : string.Empty),
                    new XElement("Sha256", has ? Sha256File(existing) : string.Empty),
                    new XElement("Bytes", has ? new FileInfo(existing).Length : 0),
                    new XElement("At", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    new XElement("Source", "init"),
                    new XElement("Origin", "closeout --promote"),
                    new XElement("Note", has ? "初始版本" : "空账")),
                new XElement("Versions")));
        }

        var current = doc.Root!.Element("Current")!;
        var version = int.Parse(current.Element("Version")?.Value ?? "0", CultureInfo.InvariantCulture);
        var currentSha = current.Element("Sha256")?.Value ?? string.Empty;
        var liveSha = Sha256File(DecisionMd);
        var bytes = new FileInfo(DecisionMd).Length;

        if (string.Equals(liveSha, currentSha, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[DC] 活版与 v{version} 逐字节一致 ⇒ 无需晋级（no-op）");
            return 0;
        }

        var next = version + 1;
        var relative = $"DECISION-LIBRARY-v{next:0000}.md";
        var target = Path.Combine(DecisionRoot, relative);

        Console.WriteLine($"[DC] 晋级 v{version} → v{next}：{relative}（{bytes} B · sha {liveSha[..12]}）");
        if (dry)
        {
            return 0;
        }

        File.Copy(DecisionMd, target, overwrite: true);
        current.SetElementValue("Version", next);
        current.SetElementValue("File", relative);
        current.SetElementValue("Sha256", liveSha);
        current.SetElementValue("Bytes", bytes);
        current.SetElementValue("At", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        current.SetElementValue("Source", "promote");
        current.SetElementValue("Origin", "closeout --promote");
        current.SetElementValue("Note", DecisionNote());

        doc.Root.Element("Versions")?.Add(new XElement("Version",
            new XElement("Version", next),
            new XElement("File", relative),
            new XElement("Sha256", liveSha),
            new XElement("Bytes", bytes),
            new XElement("At", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            new XElement("Source", "promote"),
            new XElement("Origin", "closeout --promote"),
            new XElement("Note", DecisionNote())));

        doc.Save(DecisionLedger);
        Console.WriteLine("[DC] 账本已更新（Current + Versions 两处）");
        return 0;
    }

    /// <summary>决策库版本闸门：账本可读 + 版本文件在且 sha 对得上 + 活版变了必须晋级。</summary>
    internal static Res CheckDecisionVersion()
    {
        if (!File.Exists(DecisionMd))
        {
            return R("决策库晋级", "block", false, "找不到 DECISION-LIBRARY.md", [], "决策库必须有版本通道");
        }

        if (!File.Exists(DecisionLedger))
        {
            return R("决策库晋级", "block", false, "缺 decision-versions/.ledger.xml（先跑一次 --promote）", [], "决策库与知识库同规：内容变更必须晋级");
        }

        var current = XDocument.Load(DecisionLedger).Root?.Element("Current");
        var version = int.Parse(current?.Element("Version")?.Value ?? "0", CultureInfo.InvariantCulture);
        var file = current?.Element("File")?.Value ?? string.Empty;
        var sha = current?.Element("Sha256")?.Value ?? string.Empty;
        var path = Path.Combine(DecisionRoot, file);
        var problems = new List<string>();

        if (version <= 0) problems.Add("账本 Version 为 0");
        if (!File.Exists(path)) problems.Add($"版本文件不存在：{file}");
        else if (!string.Equals(Sha256File(path), sha, StringComparison.OrdinalIgnoreCase)) problems.Add($"版本文件 sha 与账本不符：{file}");

        if (string.Equals(Sha256File(DecisionMd), sha, StringComparison.OrdinalIgnoreCase) == false)
        {
            problems.Add($"活版已变但未晋级（当前 v{version}）⇒ 跑 --promote");
        }

        var ok = problems.Count == 0;
        return R("决策库晋级", "block", ok,
            ok ? $"v{version} · {file} · sha {sha[..Math.Min(12, sha.Length)]}" : string.Join("；", problems),
            problems,
            "决策库与知识库同规：活版变化必须晋级到 decision-versions/ 并在账本两处登记");
    }

    private static string DecisionNote()
    {
        try
        {
            var a = DecisionConverge.Analyze(Live);
            return $"{a.Converged}/{a.Entries} 已收敛 · L1 {a.L1}/L2 {a.L2}/L3 {a.L3}/L4 {a.L4} · 空档 {a.Gaps} · 孤儿 {a.Orphans}";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string KnowledgeNote()
    {
        var summary = Path.Combine(KnowledgeRoot, ".derived", "summary.xml");
        if (!File.Exists(summary)) return string.Empty;
        try
        {
            var root = XDocument.Load(summary).Root;
            return $"L1 {root?.Element("L1")?.Value} / L2 {root?.Element("L2")?.Value} / L3 {root?.Element("L3")?.Value} / L4 {root?.Element("L4")?.Value}"
                   + $" · 边 {root?.Element("Edges")?.Value} · Orphans {root?.Element("Orphans")?.Value}";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>幂等重生派生物：坑集索引 / 公开变体 / 水位索引区 + worker 自己的记忆索引。</summary>
    public static int Apply()
    {
        Console.WriteLine("[WB] 收尾流水线 --apply（幂等重生派生物）");
        CompactToolFaceInbox();
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
            // 2026-10-22：知识树派生**也归 --apply**（真机：收尾写完四层却没重派生 ⇒ 复检红「派生层与重算不一致」，
            // 模型得再花一轮 `--kn-build`）。它是纯确定性重生 ⇒ 与上面几步同一性质。
            ("知识树派生", ["--kn-build", Live]),
            // 主人 2026-10-14 令：改源码语料（USER.md 等）⇒ 收尾时**自动重生 + 漏账 auto-bump**，
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

        // **条目 → L4 的机械搬运也归 --apply**（2026-10-27 主人令「默认就该这样，不靠临场指令」）：
        // 这一步是确定性的（逐条逐字 + 水位），与上面几步同一性质；语义归位（--kn-link）仍由远端 AI 做。
        Console.WriteLine("  ── 条目 → L4（机械搬运）");
        if (L4Gap.Sync(dryRun: false, max: 0) != 0)
        {
            Console.WriteLine("  ❌ 条目→L4 搬运失败");
            return 1;
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

    /// <summary>工具面拒绝收件箱：同一条拒因只留一行（带次数），总量封顶 40 —— 防 189 条同类噪声淹没真缺陷。</summary>
    private static void CompactToolFaceInbox()
    {
        var path = Path.Combine(Project, "docs", "TOOLFACE-PITFALLS.md");
        if (!File.Exists(path)) return;

        var lines = File.ReadAllLines(path, Encoding.UTF8).Where(l => l.Trim().Length > 0).ToList();
        var groups = new Dictionary<string, (string At, int Count)>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var line in lines)
        {
            var m = Regex.Match(line, @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}) \[[^\]]+\] (.*)$");
            var key = m.Success ? m.Groups[2].Value : line;
            var at = m.Success ? m.Groups[1].Value : string.Empty;
            if (groups.TryGetValue(key, out var cur)) groups[key] = (at, cur.Count + 1);
            else { groups[key] = (at, 1); order.Add(key); }
        }

        var output = order.TakeLast(40).Select(k => $"{groups[k].At} [toolface-rejection] {k}" + (groups[k].Count > 1 ? $"（共 {groups[k].Count} 次）" : string.Empty)).ToList();
        if (output.Count != lines.Count || lines.Any(l => l.Contains("（共 ", StringComparison.Ordinal)))
        {
            File.WriteAllLines(path, output, new UTF8Encoding(false));
            Console.WriteLine($"[WB] 工具面收件箱：{lines.Count} 行 → {output.Count} 行（同类合并）");
        }
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

    /// <summary>降级不得抹掉更精确的既有读数（坑 #343）：读不到 ⇒ 保留上一轮 rX/rY，并标「本轮未复核」。</summary>
    internal static (string Archive, string Project, bool Kept) PreserveConverge(string archive, string project)
    {
        static bool Missing(string v) => v is "none" or "?" || string.IsNullOrWhiteSpace(v);
        if (!Missing(archive) && !Missing(project)) return (archive, project, false);
        if (!File.Exists(WaterLive)) return (archive, project, false);

        var text = File.ReadAllText(WaterLive, Encoding.UTF8);
        var m = Regex.Match(text, @"- \*\*收敛点（机器可判）\*\*：存档 r(\d+)[^\n]*?代码仓 r(\d+)", RegexOptions.Multiline);
        if (!m.Success) return (archive, project, false);

        var a = Missing(archive) ? m.Groups[1].Value : archive;
        var p = Missing(project) ? m.Groups[2].Value : project;
        return (a, p, true);
    }

    private static string RevLabel(string value) =>
        value is "none" or "?" || string.IsNullOrWhiteSpace(value) ? "none" : $"r{value}";

    private static string WriteConvergeLine(string archiveRev, string projectRev, int src, int idx, int parityOk, int parityTotal, string fingerprint, int blockRed = 0,
        int knowledgeVersion = 0, int knowledgeL1 = 0, int knowledgeL4 = 0, int decisionConverged = 0, int decisionEntries = 0,
        bool unverified = false)
    {
        // block 红**不再拒写**，但必须留在收敛点行里（主人 2026-10-17 令「留一点牙」：真问题不许被淹没）。
        var redMark = blockRed > 0 ? $" · ⚠️ block 红 {blockRed}（未收敛就收的尾）" : string.Empty;
        var keptMark = unverified
            ? $" · 沿用上次已复核 {RevLabel(archiveRev)}/{RevLabel(projectRev)}（本轮远端不可达，未复核；非降级覆盖）"
            : string.Empty;
        var libs = knowledgeVersion > 0
            ? $" · 知识 v{knowledgeVersion}（L1 {knowledgeL1}/L4 {knowledgeL4}）· 决策 {decisionConverged}/{decisionEntries}"
            : string.Empty;
        var line = $"{ConvergeMark}存档 {RevLabel(archiveRev)} · 代码仓 {RevLabel(projectRev)} · 坑集 {src}/{idx} · 活版↔存档区 {parityOk}/{parityTotal} · 指纹 {fingerprint[..12]}{libs}{redMark}{keptMark}";
        InsertConvergeLine(WaterLive, line);   // CORE 只写**自己那份**；第二副本交给 `Vcs.Sync`。
        return line;
    }

    /// <summary>顺序闸门（**留牙版** · 主人 2026-10-17 令）：block 全绿 ⇒ 照旧写收敛点；
    /// 红 ⇒ **不再拒写**（只警告），但把欠账写进水位行 + 账本 —— 「只警告不阻断」与「真问题不许被淹没」两件同时成立。</summary>
    public static int Close(bool dry)
    {
        Console.WriteLine("[WB] 收尾流水线 --close（顺序闸门：先绿，再写收敛点）");
        // 清单与 `--check` **同一份**（`AllChecks`）：“一条口径只许一处声明” ——
        // 两处各写一份必然分叉（2026-10-15 真机：`--close` 少抄清单 ⇒ 两入口判据不一致）。
        var results = AllChecks();

        // 拒绝判据**只看 Core（必达）模块**的 block 红 —— 扩展模块（如 archive：SVN/VCS）红了、甚至不可达，
        // 都只上账、**不阻断**（主人 2026-10-16 13:32：「允许扩展模块不正常的情况下也可以直接收尾」）。
        var blockRed = results.Where(r => r.Sev == "block" && !r.Ok && r.Name != Owns[0] && r.Class == "core").ToList();
        if (blockRed.Count > 0)
        {
            // 主人 2026-10-17 令（**留牙版**）：「运行时与闸门不许强制阻断，而是留下警告 —— 阻断之后很难推测根本原因」；
            // 但同一句话的下半段是「**真问题不能被淹没**」⇒ 于是：**照样写收敛点**，同时把欠账**写进水位行 + 账本**。
            // ⇒ 牙留在「记账」上：带着几条 block 红收的尾，水位那一行看得见。
            Console.WriteLine($"⚠️  带着 {blockRed.Count} 条 block 红收尾（**不再拒写** —— 主人 2026-10-17 令：只警告不阻断）。欠账：");
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
        // 两库读数（v86）：既进回执，也进收敛点行 —— 水位推进时必须能看出两库长到哪。
        var know = ReadKnowledge();
        var dec = DecisionConverge.Analyze(Live);
        // 版本库那几件事实整个来自**扩展读数**（CORE 不再自己问 svn）。
        var readings = Vcs.Read();
        var total = readings.ParityTotal ?? 0;
        var aRev = readings.ArchiveRev?.ToString(CultureInfo.InvariantCulture) ?? "none";
        var pRev = readings.ProjectRev?.ToString(CultureInfo.InvariantCulture) ?? "none";
        // 坑 #343：探针超时 ⇒ 不覆盖上一轮已复核的 rX/rY，改成「保留 + 本轮未复核」。
        var (aEff, pEff, kept) = PreserveConverge(aRev, pRev);

        if (dry)
        {
            Console.WriteLine($"[dry-run] 将写入收敛点：存档 {RevLabel(aEff)} · 代码仓 {RevLabel(pEff)} · 坑集 {src}/{idxLines} · 活版↔存档区 {total}/{total} · 指纹 {fp[..12]} · 知识 v{know.Version}（L1 {know.L1}/L4 {know.L4}）· 决策 {dec.Converged}/{dec.Entries}");
            return 0;
        }

        // v86 收尾回执：4 组事实 + 与上次的增量；OC 只核这几行。
        var receipt = BuildReceipt(aEff, pEff);
        receipt.Save(ReceiptPath);
        Console.WriteLine($"   回执：{ReceiptPath}（知识 v{know.Version} · 决策 {dec.Converged}/{dec.Entries} · 空档 {dec.Gaps}）");

        var line = WriteConvergeLine(aEff, pEff, src, idxLines, total, total, fp, blockRed.Count,
            know.Version, know.L1, know.L4, dec.Converged, dec.Entries, kept);

        var existing = LoadLedger();
        var debt = existing?.ReportDebt is { Count: > 0 } ex
            ? ex
            : new List<DebtItem>
            {
                new() { Name = "知识树", Why = "派生落后于活版 ⇒ 跑 `AgentRuntime.Cli --kn-build <workspace>` 一条命令即可修" },
                new() { Name = "冻结语料", Why = "主人 2026-09-23 令：语料 bump 属记账项、不阻断收尾" },
            };

        // 欠账按**当前红绿**重算：已经变绿的项不再挂账（#332/#333 这类“历史欠账”不能永远漂着）。
        debt = debt.Where(d =>
        {
            var same = results.FirstOrDefault(r => string.Equals(r.Name, d.Name, StringComparison.Ordinal));
            return same is null || !same.Ok;
        }).ToList();

        // 带着红的 block 项也记进账本（与水位行同一份事实的第二个落点 —— 不许只写一处）。
        foreach (var r in blockRed)
        {
            if (debt.All(d => d.Name != r.Name))
            {
                debt.Add(new DebtItem { Name = r.Name, Why = $"block 红未清就收尾（主人 2026-10-17 令：只警告不阻断）：{r.Summary}" });
            }
        }

        SaveLedger(new CloseoutLedger
        {
            Schema = 1,
            Fingerprint = fp,
            // 降级时也保留已复核的版本号（同 #343：账本不得比水位更模糊）。
            LastCloseoutRev = int.TryParse(aEff, out var aLedger) ? aLedger : readings.ArchiveRev,
            LastCloseoutRevProject = int.TryParse(pEff, out var pLedger) ? pLedger : readings.ProjectRev,
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

        var liveWs = Path.Combine(pr, "whitebox", "workspace");
        var wl = Path.Combine(liveWs, "memory", "收尾水位.md");
        var gl = Path.Combine(pr, "tools", "closeout", "gate-ledger.xml");
        var led = Path.Combine(pr, "tools", "closeout", "ledger.xml");
        var pf = Path.Combine(pr, "docs", "PITFALLS.md");
        var la = Path.Combine(pr, "whitebox", "workspace", "AGENTS.md");
        var memindex = Path.Combine(liveWs, "MEMORYINDEX.md");
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
            ("声明的指针悬空", "坑集落点", [(gl, t => t.Replace("tests/AgentRuntime.Tests/ToolBaseInjectionTests.cs::注射后_exec_子进程看到活版基准与三个根", "tests/AgentRuntime.Tests/ToolBaseInjectionTests.cs::不存在的锚"))]),
            ("todo 计数超基线", "坑集落点", [(gl, t => Regex.Replace(t, "<TodosMax>\\d+</TodosMax>", "<TodosMax>1</TodosMax>"))]),
            ("账本指纹过期", "账本对账", [(led, StaleFingerprint)]),
            ("当日日记被清空", "素材区存在性", [(Path.Combine(liveWs, "memory", wd + ".md"), _ => "")]),
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
        // 负例要在「声明 svn」语义下逐项变红；自动降级只在真实收尾时生效。
        Environment.SetEnvironmentVariable("CLOSEOUT_NO_SVN_FALLBACK", "1");
        var baseRes = RunCheck();
        Console.WriteLine($"[WB] closeout --selftest（负例自证，全部在副本上：{tmp}）");
        Console.WriteLine("   基线：" + string.Join("，", baseRes.Select(kv => $"{kv.Key}={(kv.Value ? "绿" : "红")}")));
        var passed = 0;
        var parityApplicable = !string.Equals(SvnArchive.ArchiveRootPath, Live, StringComparison.Ordinal);
        bool Applicable(string expect) => parityApplicable || !string.Equals(expect, "活版 ↔ 存档区", StringComparison.Ordinal);
        foreach (var (name, expect, muts) in cases)
        {
            if (!Applicable(expect))
            {
                Console.WriteLine($"  ⚪ {name,-24} 不适用：存档区 = 活版（同一棵 WC，无第二副本）");
                continue;
            }
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

        Environment.SetEnvironmentVariable("CLOSEOUT_NO_SVN_FALLBACK", null);   // 假 svn 负例要验证「自动降级」
        // --- 单项：**超时/降级**是否生效（假 svn 永远不返回）—— 唯一在真仓只读上执行的一步 ---
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
            var declaration = DataContractXml.FromDocument<CheckReport>(outFake).Results
                .FirstOrDefault(x => x.Name == "存档声明");
            fakeOk = declaration is { Ok: false }
                     && declaration.Summary.Contains("自动降级", StringComparison.Ordinal)
                     && elapsed < 60;
        }
        catch
        {
            fakeOk = false;
        }

        Console.WriteLine($"  {(fakeOk ? "✅" : "❌")} {"假 svn 挂起（远端问不到）",-24} 期望「存档声明」自动降级且**限时返回** → {(fakeOk ? "已降级 none（没挂死）" : "没降级 / 被挂死")}（{elapsed:0.0}s）");
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
        // 用可切换的罐头输出，不再猜「存档区目录名」（存档区=活版后目录名判据已失效）。
        var unverOut = "?       tools/newdir/Program.cs\n?       benchmark/runs/20260101-x/meta.xml\n";
        // 罐头按**根**返回：`svn status <root>` 只列该根下的文件；ArchiveRoot(=Live) 不在代码仓根外，
        // 不能把代码仓的 `benchmark/runs` 也算到存档区头上（否则正例被误伤）。
        RunHook = (file, list, _, _) => file == "svn" && list.Length >= 2 && list[0] == "status"
            ? (0, string.Equals(list[1], CloseoutCli.Project, StringComparison.Ordinal) ? unverOut : "")
            : (0, "");
        var unverRed = SvnArchive.CheckUnversioned().Ok == false;
        unverOut = "?       benchmark/runs/20260101-x/meta.xml\n";
        var unverGreen = SvnArchive.CheckUnversioned().Ok;
        RunHook = null;
        var unverOk = unverRed && unverGreen;
        Console.WriteLine($"  {(unverOk ? "✅" : "❌")} {"未纳版件（罐头 svn）",-24} 期望「未纳版源码 ⇒ 红 / 只剩产物 ⇒ 绿」 → {(unverOk ? "两个都对" : "负例没拦住 / 正例被误伤")}");
        if (unverOk)
        {
            passed++;
        }

        // --- 单项：降级不得抹掉更精确的既有读数（坑 #343）---
        var (keepA, keepP, keepKept) = CloseoutCli.PreserveConverge("none", "none");
        var (exactA, exactP, exactKept) = CloseoutCli.PreserveConverge("136", "137");
        var preserveOk = keepKept && !exactKept
                         && keepA is not ("none" or "?") && keepP is not ("none" or "?")
                         && exactA == "136" && exactP == "137";
        Console.WriteLine($"  {(preserveOk ? "✅" : "❌")} {"降级保留既有收敛点",-24} 期望「none ⇒ 保留 rX/rY；已有真值 ⇒ 不标未复核」 → {(preserveOk ? $"保留 r{keepA}/r{keepP}" : "没保住")}");
        if (preserveOk) passed++;

        var total = cases.Count(c => Applicable(c.Expect)) + 5;
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
