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
    public static readonly string Archive = Path.GetFullPath(Path.Combine(Project, "..", "..", "whitebox-workspace"));

    private static readonly string Here = Path.Combine(Project, "tools");
    private static readonly string LedgerDir = Path.Combine(Here, "closeout");
    private static readonly string LedgerPath = Path.Combine(LedgerDir, "ledger.xml");
    private static readonly string PlacementFile = Path.Combine(LedgerDir, "gate-ledger.xml");

    private static readonly string PitfallsSrc = Path.Combine(Project, "docs", "PITFALLS.md");
    private static readonly string PitfallsIndex = Path.Combine(Project, "docs", "PITFALLS-INDEX.md");
    private static readonly string PitfallsPublic = Path.Combine(Project, "docs", "PITFALLS-public.md");
    private static readonly string KnowledgeMd = Path.Combine(Archive, "knowledge", "knowledge.md");
    private static readonly string WaterArchive = Path.Combine(Archive, "memory", "收尾水位.md");
    private static readonly string WaterLive = Path.Combine(Live, "memory", "收尾水位.md");
    private static readonly string WaterLevel = WaterArchive;

    public const string ConvergeMark = "- **收敛点（机器可判）**：";
    private static readonly Regex ConvergeRe = new(@"- \*\*收敛点（机器可判）\*\*：存档 r(\d{3,})[^\n]*", RegexOptions.Compiled);

    private static readonly HashSet<string> ParitySkipDirs = [".svn", "worker"];
    private static readonly HashSet<string> ParitySkipFiles = [".DS_Store"];

    private static readonly string SessionPointerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentruntime", "session.xml");

    /// <summary>收敛点之后允许多少个提交仍算「正常」（收尾自己会再提交水位+账本 ≈2 个）。</summary>
    private const int WaterGapTolerance = 2;

    private static double SvnTimeout =>
        double.TryParse(Environment.GetEnvironmentVariable("CLOSEOUT_SVN_TIMEOUT"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 45;

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
        new("L3 往返", "report", ["--knowledge-repo", Path.Combine(Archive, "knowledge"), "--knowledge-check"],
            "知识库派生一致/往返/版本/下钻指针（**现已就地进程内**）；坑集/交接逐条索引层（l3-unify）已退役 ⇒ 本闸门不再要求它"),
        new("冻结语料", "report", ["--frozen-corpus-check", Live],
            "主人 2026-09-23 令：语料 bump 类属记账项、不阻断收尾（但仍上账）"),
    ];

    // ================================================================ runner

    /// <summary>**仅自证用**：替换子进程执行（罐头 svn 测试）；非 null 时 <see cref="Run"/> 直接走它。</summary>
    internal static Func<string, string[], string?, int, (int Rc, string Out)>? RunHook;

    private static (int Rc, string Out) Run(string file, IEnumerable<string> args, string? cwd = null, double timeoutSec = -1, IDictionary<string, string>? env = null)
    {
        var list = args.ToArray();
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

    private static List<string> TailLines(string text, int n = 4) =>
        [.. text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0).TakeLast(n)];

    private static string Sha256File(string path)
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

    /// <summary>**只问远端**：该 URL 上的最新提交号；拿不到 ⇒ (null, 起因)。绝不回落本地读数。</summary>
    private static (int? Rev, string Why) RemoteRev(string path)
    {
        var (rc, outText) = Run("svn", ["info", "--show-item", "url", path], timeoutSec: SvnTimeout);
        var url = outText.Trim();
        if (rc != 0 || !url.StartsWith("http", StringComparison.Ordinal))
        {
            return (null, $"取不到 URL（rc={rc}）：{(url.Length > 120 ? url[..120] : url)}");
        }

        var (rc2, out2) = Run("svn", ["log", "-l", "1", "-q", "-r", "HEAD:1", url], timeoutSec: SvnTimeout);
        var m = Regex.Match(out2.Trim(), @"^r(\d+)", RegexOptions.Multiline);
        if (rc2 != 0 || !m.Success)
        {
            var t = TailLines(out2, 1);
            return (null, t.Count > 0 ? t[0] : "远端查询无输出");
        }

        return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), "");
    }

    private static int? TreeRev(string path) => RemoteRev(path).Rev;
    private static int? ArchiveRev() => TreeRev(Archive);

    // ================================================================ checks
    private sealed record Res(string Name, string Sev, bool Ok, string Summary, List<string> Detail, string Why);

    private static Res R(string name, string sev, bool ok, string summary, IEnumerable<string>? detail = null, string why = "") =>
        new(name, sev, ok, summary, detail?.ToList() ?? [], why);

    private static List<Res> CheckGates()
    {
        var res = new List<Res>();
        foreach (var g in Gates)
        {
            var (rc, outText) = Cs(g.Cs);
            res.Add(R(g.Name, g.Sev, rc == 0, rc == 0 ? "绿" : $"红（rc={rc}）", rc == 0 ? null : TailLines(outText), g.Why));
        }

        return res;
    }

    private static Res CheckSvnDirty(string target, string name, string note, bool exclude = false)
    {
        var (rc, outText) = Run("svn", ["status", target]);
        if (rc != 0)
        {
            return R(name, "block", false, $"svn status 失败 rc={rc}", TailLines(outText), note);
        }

        var skipPrefix = exclude ? Path.Combine(Project, "whitebox") + Path.DirectorySeparatorChar : null;
        var dirty = new List<string>();
        var unver = 0;
        foreach (var ln in outText.Replace("\r\n", "\n").Split('\n'))
        {
            if (ln.Trim().Length == 0)
            {
                continue;
            }

            var path = ln.Length > 8 ? ln[8..].Trim() : ln.Trim();
            if (path.Length == 0)
            {
                continue;
            }

            var absP = Path.IsPathRooted(path) ? path : Path.Combine(target, path);
            if (skipPrefix != null && absP.StartsWith(skipPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (ln.StartsWith('?'))
            {
                unver++;
            }
            else
            {
                dirty.Add(Path.GetRelativePath(target, absP));
            }
        }

        return R(name, "block", dirty.Count == 0,
            dirty.Count == 0 ? "0 项" : $"{dirty.Count} 项：{string.Join(", ", dirty.Take(6))}",
            null, note + $"；未版本化 {unver} 项");
    }

    private static List<Res> CheckSvnDirtyBoth() =>
    [
        CheckSvnDirty(Project, "未提交（代码仓）",
            "代码仓（tools/ docs/ src/ tests/）不得留未提交项；活版目录走 wb-sync、不计入本项", exclude: true),
        CheckSvnDirty(Archive, "未提交（存档区）", "存档区是跨机真相，活版改完必须 push 并提交（wb-sync.sh push）"),
    ];

    private const string DeletionsWhy =
        "`!` = 本机缺失但没登记删除 ⇒ 一次 `svn update` 就把文件**恢复回来**（坑 #153）⇒ 跨副本删除必须 `svn delete --force` 再提交";

    private static Res CheckRepoDeletions()
    {
        var offenders = new List<string>();
        var unreadable = new List<string>();
        foreach (var (label, root) in new[] { ("代码仓", Project), ("存档区", Archive) })
        {
            var (rc, outText) = Run("svn", ["status", root]);
            if (rc != 0)
            {
                unreadable.Add($"{label} rc={rc}");
                continue;
            }

            foreach (var line in outText.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith('!'))
                {
                    offenders.Add($"{label}: {line[8..].Trim()}");
                }
            }
        }

        if (unreadable.Count > 0)
        {
            return R("删除登记", "block", false, $"svn status 读不到：{string.Join("，", unreadable)}", null, DeletionsWhy);
        }

        return R("删除登记", "block", offenders.Count == 0,
            offenders.Count == 0 ? "0 项" : $"{offenders.Count} 项：{string.Join(", ", offenders.Take(6))}", null, DeletionsWhy);
    }

    private const string UnverWhy =
        "构建输入 / 知识件必须是**已纳版**的：整块新目录漏 `svn add`，在只认 M/D/A/`!` 的旧判据下**静默绿**（坑 #317 —— 整个 `tools/wb-cli` 工程漏库，本机全绿、别端 checkout 必挂）";

    /// <summary>允许「未纳版」的子树（相对各自根）：运行产物 / 运行期目录，按设计不入库。</summary>
    private static readonly string[] UnverAllowProject = ["benchmark/runs", "whitebox"];

    private static readonly string[] UnverAllowArchive = ["worker"];

    /// <summary>
    /// **未纳版件**闸门（坑 #317 的机器判据）：`svn status` 的 `?`（未纳入版本控制）**也要红** ——
    /// 旧「未提交（代码仓）」只认 M/D/A/`!`，于是「整块新目录没 add」一路静默绿。
    /// 产物子树（<see cref="UnverAllowProject"/> / <see cref="UnverAllowArchive"/>）按设计豁免。
    /// 修法 = `svn add --parents` 后**回读提交路径清单**（`wb-cli commit` 内建）。
    /// </summary>
    private static Res CheckUnversioned()
    {
        var offenders = new List<string>();
        var unreadable = new List<string>();
        foreach (var (label, root, allow) in new[] { ("代码仓", Project, UnverAllowProject), ("存档区", Archive, UnverAllowArchive) })
        {
            var (rc, outText) = Run("svn", ["status", root]);
            if (rc != 0)
            {
                unreadable.Add($"{label} rc={rc}");
                continue;
            }

            foreach (var ln in outText.Replace("\r\n", "\n").Split('\n'))
            {
                if (!ln.StartsWith('?'))
                {
                    continue;
                }

                var path = ln.Length > 8 ? ln[8..].Trim() : ln.Trim();
                if (path.Length == 0)
                {
                    continue;
                }

                var absP = Path.IsPathRooted(path) ? path : Path.Combine(root, path);
                var rel = Path.GetRelativePath(root, absP).Replace('\\', '/');
                if (allow.Any(al => rel == al || rel.StartsWith(al + "/", StringComparison.Ordinal)))
                {
                    continue;
                }

                offenders.Add($"{label}: {rel}");
            }
        }

        if (unreadable.Count > 0)
        {
            return R("未纳版件", "block", false, $"svn status 读不到：{string.Join("，", unreadable)}", null, UnverWhy);
        }

        return R("未纳版件", "block", offenders.Count == 0,
            offenders.Count == 0 ? "0 项" : $"{offenders.Count} 项未纳版：{string.Join(", ", offenders.Take(6))}",
            offenders.Count == 0
                ? null
                : ["新建目录同样适用 —— `svn add --parents` 后必须**回读提交路径清单**（`wb-cli commit` 内建此步）",
                   $"产物子树按设计豁免：{string.Join("、", UnverAllowProject)} / {string.Join("、", UnverAllowArchive)}"],
            UnverWhy);
    }

    private static HashSet<string> TreeFiles(string root)
    {
        var outSet = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                if (!ParitySkipDirs.Contains(Path.GetFileName(d)))
                {
                    stack.Push(d);
                }
            }

            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (ParitySkipFiles.Contains(Path.GetFileName(f)))
                {
                    continue;
                }

                outSet.Add(Path.GetRelativePath(root, f));
            }
        }

        return outSet;
    }

    private static bool SameBytes(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length)
        {
            return false;
        }

        return Sha256File(a) == Sha256File(b);
    }

    private static Res CheckCopyParity()
    {
        var a = TreeFiles(Live);
        var b = TreeFiles(Archive);
        var diff = new List<string>();
        foreach (var rel in a.Intersect(b).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!SameBytes(Path.Combine(Live, rel), Path.Combine(Archive, rel)))
            {
                diff.Add(rel + "（字节不同）");
            }
        }

        diff.AddRange(a.Except(b).OrderBy(x => x, StringComparer.Ordinal).Select(rel => rel + "（活版有、存档区缺）"));
        diff.AddRange(b.Except(a).OrderBy(x => x, StringComparer.Ordinal).Select(rel => rel + "（存档区有、活版缺）"));
        var total = a.Union(b).Count();
        return R("活版 ↔ 存档区", "block", diff.Count == 0,
            diff.Count == 0 ? $"全部一致（{total} 个文件）" : $"{diff.Count} 处不一致（共 {total} 件）：{string.Join(", ", diff.Take(5))}",
            diff.Count == 0 ? null : ["逐件比对（整树，非白名单）；哪一侧独有/更新都算欠账"],
            "双向补录：活版 ⇄ 存档区只这一对，整树不得有任何一处不同");
    }

    /// <summary>本机会话是否**已收尾**（`session.xml` 指针的 `Closed`）。读数拿不到 ⇒ null（不猜）。</summary>
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

    private static Res CheckWaterLevel()
    {
        if (!File.Exists(WaterLevel))
        {
            return R("水位收敛点", "block", false, "水位文件不存在", [WaterLevel], "收尾的收敛点写在水位里");
        }

        var txt = File.ReadAllText(WaterLevel, Encoding.UTF8);
        var hits = ConvergeRe.Matches(txt).Select(m => m.Groups[1].Value).ToList();
        var n = Regex.Matches(txt, Regex.Escape(ConvergeMark)).Count;
        if (n == 0)
        {
            return R("水位收敛点", "block", false, $"水位无「{ConvergeMark.Trim()}…rNNN」行",
                ["--close 会写这一行；现在缺 ⇒ 机器对账无操作数"], "收尾铁则 step 0 的机器对账需要有 r 号可比");
        }

        var detail = new List<string>();
        var revNow = ArchiveRev();
        if (hits.Count > 0 && revNow is int rn && int.Parse(hits[0], CultureInfo.InvariantCulture) < rn)
        {
            var gap = rn - int.Parse(hits[0], CultureInfo.InvariantCulture);
            var closed = SessionClosed();
            if (closed == true && gap > WaterGapTolerance)
            {
                return R("水位收敛点", "block", false,
                    $"会话**已收尾**，水位仍落后 {gap} 个提交（r{hits[0]} → r{rn}）⇒ 上一轮收尾**没走完**",
                    [$"判据：closed=true **且** 落后 > {WaterGapTolerance} 个提交（{WaterGapTolerance} 是收尾自己的两个提交）",
                     "修：补齐内容层 → --apply → 提交 → --close（绿才写收敛点）",
                     "为什么不能像旧版那样判「正常」：那等于替中断开脱（坑 #156）"],
                    "收尾不能在中途被打断后**静默**留欠账");
            }

            detail.Add($"收敛点 r{hits[0]} 之后存档又推进到 r{rn}（{gap} 个提交）；会话仍在跑 ⇒ 正常推进（写完收敛点后还会有提交）；硬判据是内容指纹，收尾时 --close 重盖章");
        }

        if (n > 1)
        {
            return R("水位收敛点", "block", false, $"收敛点行重复（{n} 行）⇒ 必须唯一", null,
                "收敛点必须唯一，否则对账读哪一个都可能读错");
        }

        return R("水位收敛点", "block", hits.Count > 0, $"存档 r{(hits.Count > 0 ? hits[0] : "?")}", detail,
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
        foreach (var root in roots ?? [Project, Archive, Live])
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

    private static Res CheckRecurrence()
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

    private static Res CheckPitfallPlacements()
    {
        if (!File.Exists(PlacementFile))
        {
            return R("坑集落点", "block", false, $"未建立 {Path.GetRelativePath(Project, PlacementFile)}",
                ["新坑必须声明落点类别：gate / resident / human / todo"],
                "「写错那一刻模型看得见什么」要有机器判据");
        }

        var gl = LoadGateLedger();
        if (gl is null)
        {
            return R("坑集落点", "block", false, $"账本读不出：{Path.GetRelativePath(Project, PlacementFile)}",
                ["`gate-ledger` 应是强类型 XML（A4）；读不出 ⇒ 先修账本"],
                "「写错那一刻模型看得见什么」要有机器判据");
        }

        var baseline = gl.BaselineMaxId;
        var todosMax = gl.TodosMax;

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
        return R("坑集落点", "block", ok,
            ok
                ? $"声明 {decls.Count} 条（gate {gates} / resident {resident} / human {human} / todo {todos}，todo 基线 {todosMax}）· 新坑 0 条未声明"
                : string.Join("；", problems),
            ok ? null : ["规则：新坑必须声明落点类别；确定性坑应做成闸门（gate）"],
            "「写错那一刻模型看得见什么」要有机器判据（坑 #130 / §十·51）");
    }

    private static string? LastActivityDate()
    {
        var (rc, outText) = Run("svn", ["info", "--show-item", "url", Project]);
        var url = outText.Trim();
        if (rc == 0 && url.StartsWith("http", StringComparison.Ordinal))
        {
            var (rc2, out2) = Run("svn", ["log", "-l", "1", "-q", "-r", "HEAD:1", url]);
            var m = Regex.Match(out2, @"(\d{4}-\d{2}-\d{2})");
            if (rc2 == 0 && m.Success)
            {
                return m.Groups[1].Value;
            }
        }

        return LoadLedger()?.WaterLevelDate;
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

    private static Res CheckSzonePresence()
    {
        var d = LastActivityDate();
        if (string.IsNullOrEmpty(d))
        {
            return R("素材区存在性", "block", false, "拿不到「最后活动日」（SVN 与账本都读不到）",
                ["判据需要它来比对应有的素材区三件"], "应有集合判据（METHODOLOGY §十·16）");
        }

        var problems = new List<string>();
        foreach (var (label, root) in new[] { ("存档区", Archive), ("活版", Live) })
        {
            var p = Path.Combine(root, "memory", d + ".md");
            if (!File.Exists(p))
            {
                problems.Add($"{label}缺 memory/{d}.md");
            }
            else if (new FileInfo(p).Length < 300)
            {
                problems.Add($"{label}的 memory/{d}.md 只有 {new FileInfo(p).Length} B（像占位）");
            }
        }

        var md = d[5..];
        if (!File.Exists(WaterArchive))
        {
            problems.Add("水位正文 不存在");
        }
        else if (!Regex.IsMatch(File.ReadAllText(WaterArchive, Encoding.UTF8), $"^- \\*\\*{Regex.Escape(d)}", RegexOptions.Multiline))
        {
            problems.Add($"水位无 {d} 的当日段（`- **{d}…` 行）");
        }

        var idx = Path.Combine(Archive, "MEMORYINDEX.md");
        if (!File.Exists(idx))
        {
            problems.Add("MEMORYINDEX 不存在");
        }
        else if (!Regex.IsMatch(MemIndexTimeline(File.ReadAllText(idx, Encoding.UTF8)), $@"^\|\s*\**{Regex.Escape(md)}\**\s*\|", RegexOptions.Multiline))
        {
            problems.Add($"MEMORYINDEX 时间线无 {md} 行（`| {md} | …`）");
        }

        var ok = problems.Count == 0;
        return R("素材区存在性", "block", ok,
            ok ? $"{d} 的素材区三件齐全（日记 ×2 · 水位当日段 · 索引当日行）" : string.Join("；", problems),
            ok ? null : ["应有集合判据：闸门管「做了的对不对」，这一项管「该做的有没有做」"],
            "判据按**应有集合**算，不按已有集合算（METHODOLOGY §十·16 / 坑 #137）");
    }

    private static Res CheckLedger()
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

    private static Res CheckWorkerMemory()
    {
        var why = "读得出来才抽象得进去：worker 不自收尾，这一步是它那些坑/候选**唯一的收敛入口**";
        var home = Path.Combine(Project, "whitebox", "workspace", "worker");
        if (!Directory.Exists(home))
        {
            return R("worker 记忆", "report", true, "worker 还没跑过（无落点）", null,
                "worker 不自收尾 ⇒ 它的白板/草稿/踩坑由 master 收尾时读；没跑过就没有可收敛的");
        }

        static int CountXml(string subdir)
        {
            try
            {
                return Directory.EnumerateFiles(subdir).Count(n => n.EndsWith(".xml", StringComparison.Ordinal));
            }
            catch
            {
                return 0;
            }
        }

        var runs = 0;
        var boards = 0;
        var drafts = 0;
        string? latest = null;
        var stack = new Stack<string>();
        stack.Push(home);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                stack.Push(d);
            }

            if (File.Exists(Path.Combine(dir, "state.json")))
            {
                runs++;
                latest = latest is null || string.CompareOrdinal(latest, dir) < 0 ? dir : latest;
            }

            if (Path.GetFileName(dir) == "memory")
            {
                boards += CountXml(Path.Combine(dir, "current-tail.xml"));
                drafts += CountXml(Path.Combine(dir, "draft.xml"));
            }
        }

        var inboxLines = 0;
        foreach (var cand in new[] { Path.Combine(Project, "docs", "WORKER-PITFALLS.md"), Path.Combine(home, "pitfalls-inbox.md") })
        {
            if (File.Exists(cand))
            {
                inboxLines += File.ReadAllLines(cand, Encoding.UTF8).Count(ln => ln.Trim().Length > 0);
            }
        }

        var detail = new List<string>();
        if (latest is not null)
        {
            detail.Add($"最近一次派发：{Path.GetRelativePath(Project, latest)}");
        }

        if (inboxLines > 0)
        {
            detail.Add($"待收敛的 worker 踩坑/交接：{inboxLines} 条（docs/WORKER-PITFALLS.md 或 worker/pitfalls-inbox.md）");
        }

        if (boards > 0 || drafts > 0)
        {
            detail.Add($"白板 {boards} 项 / 草稿 {drafts} 项（<派发>/memory/）");
        }

        return R("worker 记忆", "report", true,
            $"派发 {runs} 次 · 白板 {boards} 项 · 草稿 {drafts} 项 · 待收敛踩坑 {inboxLines} 条", detail, why);
    }

    private static Res CheckRepoReachable()
    {
        var (pr, e1) = RemoteRev(Project);
        var (ar, e2) = RemoteRev(Archive);
        var ok = pr is not null && ar is not null;
        if (ok)
        {
            return R("可达性前置", "block", true, $"远端读数可用（代码仓 r{pr} · 存档区 r{ar}）", null,
                "判据先要能拿到读数：远端查询拿不到数就不能声称收敛");
        }

        return R("可达性前置", "block", false, "拿不到远端读数（超时 / 不可达）⇒ 收尾不可收敛",
            [$"代码仓：{(e1.Length > 0 ? e1 : "ok")}", $"存档区：{(e2.Length > 0 ? e2 : "ok")}",
             "先修可达性（路由 / TUN / 仓库），再收尾；不要跳过本项、更不要改闸门让它跑起来"],
            $"判据先要能拿到读数（超时上限 {SvnTimeout:0}s，`CLOSEOUT_SVN_TIMEOUT` 可调）");
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

    private static List<Res> AllChecks(bool includeLedger)
    {
        var res = new List<Res>();
        res.AddRange(CheckGates());
        res.AddRange(CheckSvnDirtyBoth());
        res.Add(CheckRepoReachable());
        res.Add(CheckRepoDeletions());
        res.Add(CheckUnversioned());
        res.Add(CheckCopyParity());
        res.Add(CheckWaterLevel());
        res.Add(CheckPitfallPlacements());
        res.Add(CheckSzonePresence());
        if (includeLedger)
        {
            res.Add(CheckLedger());
        }

        res.Add(CheckWorkerMemory());
        res.Add(CheckRecurrence());
        return res;
    }

    // ================================================================ entry
    public static int Check(bool xml)
    {
        var results = AllChecks(includeLedger: true);
        var blockRed = results.Count(r => r.Sev == "block" && !r.Ok);
        var reportRed = results.Count(r => r.Sev == "report" && !r.Ok);

        if (xml)
        {
            var report = new CheckReport
            {
                Roots = new Roots { Project = Project, Live = Live, Archive = Archive },
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
                    }),
                ],
                BlockRed = blockRed,
                ReportRed = reportRed,
            };
            Console.WriteLine(DataContractXml.ToDocument(report));
            return blockRed > 0 ? 1 : 0;
        }

        Console.WriteLine("[WB] 收尾流水线 --check（只读）");
        Console.WriteLine($"  PROJECT={Project}\n  LIVE={Live}\n  ARCHIVE={Archive}\n");
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
        Console.WriteLine($"汇总：block 红 {blockRed} 项 · report 红 {reportRed} 项 ⇒ {(blockRed > 0 ? "收尾未收敛（--close 会拒绝写水位）" : "阻塞项全绿（可 --close）")}");
        if (reportRed > 0)
        {
            Console.WriteLine("（report 项红色属**已知欠账/变化**，已上账；本轮不阻断，但不得悄悄变多）");
        }

        return blockRed > 0 ? 1 : 0;
    }

    public static int Where()
    {
        Console.WriteLine("[WB] 收尾流水线 · 根目录（**所有路径基准看这里**）");
        foreach (var (k, v) in new[] { ("PROJECT  代码仓", Project), ("LIVE     本机活版", Live), ("ARCHIVE  WB 存档区", Archive) })
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
    private static string WriteConvergeLine(string archiveRev, string projectRev, int src, int idx, int parityOk, int parityTotal, string fingerprint)
    {
        var line = $"{ConvergeMark}存档 r{archiveRev} · 代码仓 r{projectRev} · 坑集 {src}/{idx} · 活版↔存档区 {parityOk}/{parityTotal} · 指纹 {fingerprint[..12]}";
        foreach (var path in new[] { WaterArchive, WaterLive })
        {
            var lines = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Split('\n')
                .Where(l => !l.StartsWith(ConvergeMark, StringComparison.Ordinal)).ToList();
            var outp = new List<string>();
            var done = false;
            foreach (var ln in lines)
            {
                outp.Add(ln);
                if (!done && ln.StartsWith("## 水位", StringComparison.Ordinal))
                {
                    outp.Add("");
                    outp.Add(line);
                    done = true;
                }
            }

            if (!done)
            {
                outp.Add("");
                outp.Add(line);
            }

            File.WriteAllText(path, string.Join('\n', outp).TrimEnd('\n') + "\n", new UTF8Encoding(false));
        }

        return line;
    }

    /// <summary>顺序闸门：block 全绿 → 才写收敛点（水位 + 账本）。红 ⇒ 拒绝，不写任何东西。</summary>
    public static int Close(bool dry)
    {
        Console.WriteLine("[WB] 收尾流水线 --close（顺序闸门：先绿，再写收敛点）");
        var results = new List<Res>();
        results.AddRange(CheckGates());
        results.AddRange(CheckSvnDirtyBoth());
        results.Add(CheckRepoReachable());
        results.Add(CheckUnversioned());
        results.Add(CheckCopyParity());
        results.Add(CheckWaterLevel());
        results.Add(CheckPitfallPlacements());
        results.Add(CheckSzonePresence());
        results.Add(CheckWorkerMemory());
        results.Add(CheckRecurrence());

        var blockRed = results.Where(r => r.Sev == "block" && !r.Ok && r.Name != Owns[0]).ToList();
        if (blockRed.Count > 0)
        {
            Console.WriteLine("❌ --close 拒绝：block 项还红，收尾未收敛。欠账：");
            foreach (var r in blockRed)
            {
                Console.WriteLine($"   · {r.Name} — {r.Summary}");
            }

            Console.WriteLine("（先 --apply / 提交 / 双向补录，再 --close）");
            return 1;
        }

        var (src, maxid) = CountPitfalls();
        var idxLines = 0;
        if (File.Exists(PitfallsIndex))
        {
            var m = Regex.Match(File.ReadAllText(PitfallsIndex, Encoding.UTF8), @"计数：\*\*(\d+) 条\*\*");
            idxLines = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        var parity = CheckCopyParity();
        var pm = Regex.Match(parity.Summary, @"（(\d+) 个文件）");
        var total = pm.Success ? int.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var fp = FingerprintParts();
        var archiveRev = TreeRev(Archive);
        var projectRev = TreeRev(Project);
        var aRev = archiveRev?.ToString(CultureInfo.InvariantCulture) ?? "?";
        var pRev = projectRev?.ToString(CultureInfo.InvariantCulture) ?? "?";

        if (dry)
        {
            Console.WriteLine($"[dry-run] 将写入收敛点：存档 r{aRev} · 代码仓 r{pRev} · 坑集 {src}/{idxLines} · 活版↔存档区 {total}/{total} · 指纹 {fp[..12]}");
            return 0;
        }

        var line = WriteConvergeLine(aRev, pRev, src, idxLines, total, total, fp);

        var existing = LoadLedger();
        var debt = existing?.ReportDebt is { Count: > 0 } ex
            ? ex
            : new List<DebtItem>
            {
                new() { Name = "L3 往返", Why = "派生落后于 md ⇒ 跑 `AgentRuntime.Cli --knowledge-repo <out> --derive-only`（C# 实现；python 原件已退役）一条命令即可修" },
                new() { Name = "冻结语料", Why = "主人 2026-09-23 令：语料 bump 属记账项、不阻断收尾" },
            };

        SaveLedger(new CloseoutLedger
        {
            Schema = 1,
            Fingerprint = fp,
            LastCloseoutRev = archiveRev,
            LastCloseoutRevProject = projectRev,
            MaxPitfallId = (int)maxid,
            PitfallsSrc = src,
            PitfallsIndex = idxLines,
            ParityFiles = total,
            WaterLevelDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ReportDebt = debt,
        });

        Console.WriteLine($"✅ 收敛点已写：{line}");
        Console.WriteLine($"   水位（两份）：{WaterArchive} / {WaterLive}");
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
        CopyTree(Archive, ar);

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
        var withBang = CheckRepoDeletions().Ok;
        RunHook = (_, _, _, _) => (0, "M       docs/PITFALLS.md\n");
        var withoutBang = CheckRepoDeletions().Ok;
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
        var unverRed = CheckUnversioned().Ok == false;
        RunHook = (file, list, _, _) => file == "svn" && list.Length == 2
            ? (0, IsArchive(list[1]) ? "" : "?       benchmark/runs/20260101-x/meta.xml\n")
            : (0, "");
        var unverGreen = CheckUnversioned().Ok;
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
