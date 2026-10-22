using System.Globalization;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using AgentRuntime.Core.Persistence;

namespace CloseoutTool;

// ================================================================ 收尾的「扩展环节」
//
// 主人 2026-10-16 12:2x 定下的口径：
//   · **CORE 收尾（标准模式）对 VCS 零引用** —— 「版本库 / 存档区」这件事整个属于下面的实现；
//   · **不使用任何 SVN / GIT 是一个合法实现**（`NoArchive`），**不是降级分支**；
//   · 我们自己的机器用 SVN，那只是 **CORE 收尾的一个扩展选项**（`SvnArchive`）。
//
// 为什么要有这一刀（旧形态的病，都是真机实测）：
//   ① 模式用一个全局布尔 `SvnLinked`（从“有没有 `.svn`”**推断**）表达 ⇒ 判据靠猜：
//      家目录游离的 `.svn` 副本让它**恒真**，跳过逻辑永不生效。
//   ② 〈没有 VCS〉只能靠**按 check 名把红改成绿**的归一表达（`R()` 里 `n.Contains("存档")‖…`）
//      ⇒ **用字符串当类型**（违反铁则 8「能用一个类型表达清楚的，绝不写成一串分支」）。
//   ③ 于是「有没有存档区」在两个地方各判一次，**必然分叉**：
//      真机 2026-10-15 —— `CheckCopyParity` 的枚举发生在归一**之前** ⇒ 存档区不存在直接抛；
//      `--close` 另抄一份检查清单 ⇒ 与 `--check` 不一致，双双当场崩。
//
// 所以：**检查项是对象、由扩展贡献；读数由扩展提供；动作由扩展承担。**
// CORE 只做两件事：跑 `Checks()`、消费 `Read()`。

/// <summary>收尾的**扩展环节**契约：一切「版本库 / 存档区」的事都归它。</summary>
internal interface ICloseoutArchive
{
    /// <summary>实现名（`none` / `svn`；将来 `git`）。</summary>
    string Name { get; }

    /// <summary>存档区根；**没有存档区 ⇒ <c>null</c>**。
    /// <para>让 CORE 用**类型事实**（而不是 `SvnLinked ? … : …` 三元）决定落点。</para></summary>
    string? ArchiveRoot { get; }

    /// <summary>本扩展贡献的检查项（**对象**，不是名字字符串）。</summary>
    IReadOnlyList<CloseoutCli.Res> Checks();

    /// <summary>本扩展提供的读数。CORE **只消费**，绝不自己去问版本库。</summary>
    ArchiveReadings Read();

    /// <summary>本扩展的**自证**（「声明即自证」）：<c>null</c> = 证明通过；否则 = 为什么证不了。</summary>
    string? Prove();

    /// <summary>知识库落点（收尾要读/写的知识正文在哪儿）。</summary>
    string KnowledgeRoot { get; }

    /// <summary>水位落点（**收尾认定的那一份**；CORE 只写自己活版那份，第二副本由 <see cref="Sync"/> 负责）。</summary>
    string WatermarkPath { get; }

    /// <summary>指针可解析的**额外根**（除 <c>Project</c> 之外还有哪些副本）。</summary>
    IReadOnlyList<string> Roots { get; }

    /// <summary>把 CORE 刚写的收敛点**同步到扩展自己的地盘**（第二副本 / 提交…）。返回要**说给人听**的几行。</summary>
    IReadOnlyList<string> Sync(string convergeLine);
}

/// <summary>CORE 需要的「版本库事实」只有这四个，一次取回。</summary>
internal readonly record struct ArchiveReadings(int? ProjectRev, int? ArchiveRev, string? LastCommitDate, int? ParityTotal);

/// <summary>
/// **不使用任何 VCS**（= 标准模式）：**0 项检查、0 个动作、没有存档区**。
/// <para>注意：这里不是「把 VCS 那几项判绿」，而是**那几项根本不存在**（清单里不多不少）。</para>
/// </summary>
internal sealed class NoArchive : ICloseoutArchive
{
    public string Name => "none";

    public string? ArchiveRoot => null;

    public IReadOnlyList<CloseoutCli.Res> Checks() => [];

    public string KnowledgeRoot => Path.Combine(CloseoutCli.Live, "knowledge");

    public string WatermarkPath => Path.Combine(CloseoutCli.Live, "memory", "收尾水位.md");

    public IReadOnlyList<string> Roots => [CloseoutCli.Live];

    public string? Prove() => null;   // 不用任何版本库 ⇒ 没有可证的东西

    public ArchiveReadings Read() => default;

    public IReadOnlyList<string> Sync(string convergeLine) => [];   // 没有第二副本、没有版本库 ⇒ 无事可做
}

/// <summary>**SVN 存档**扩展：把「版本库那几件事」整个收进来（检查项 + 读数 + 提交/同步）。</summary>
internal sealed class SvnArchive : ICloseoutArchive
{
    private static readonly HashSet<string> ParitySkipDirs = [".svn", "worker"];
    private static readonly HashSet<string> ParitySkipFiles = [".DS_Store"];

    private static readonly string[] UnverAllowProject = ["benchmark/runs", "whitebox"];
    private static readonly string[] UnverAllowArchive = ["worker"];

    private const string DeletionsWhy =
        "`!` = 本机缺失但没登记删除 ⇒ 一次 `svn update` 就把文件**恢复回来**（坑 #153）⇒ 跨副本删除必须 `svn delete --force` 再提交";

    private const string UnverWhy =
        "构建输入 / 知识件必须是**已纳版**的：整块新目录漏 `svn add`，在只认 M/D/A/`!` 的旧判据下**静默绿**（坑 #317 —— 整个 `tools/wb-cli` 工程漏库，本机全绿、别端 checkout 必挂）";

    private static double SvnTimeout =>
        double.TryParse(Environment.GetEnvironmentVariable("CLOSEOUT_SVN_TIMEOUT"), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 45;

    /// <summary>存档区根 —— **只属于扩展**，CORE 不再知道它。
    /// <para>2026-10-19 起：活版 `whitebox/whitebox/workspace` 本身已在公共 SVN 中，
    /// 不再有独立的 `software-company/whitebox-workspace` ⇒ **存档区 = 活版**（同一棵 WC）。</para></summary>
    public static readonly string ArchiveRootPath = CloseoutCli.Live;

    public string Name => "svn";

    public string? ArchiveRoot => ArchiveRootPath;

    /// <summary>
    /// **快速可达性探针**（主人 2026-10-18 令：连不上一次 SVN 就立即退化为不用 SVN）。
    /// <para>每边只给 3 秒（可用 `CLOSEOUT_SVN_PROBE_TIMEOUT` 调）：`svn info` 拿 URL + `svn log -l1`
    /// 拿 r 号；任一失败 ⇒ false。**不**走 45 秒的常规超时，避免一次收尾被一个断网拖几分钟。</para>
    /// </summary>
    internal bool ProbeFast(double seconds, out string why)
    {
        if (!Directory.Exists(ArchiveRootPath))
        {
            why = $"存档区不存在（{ArchiveRootPath}）";
            return false;
        }

        if (!ProbeOne(CloseoutCli.Project, seconds, out var projectWhy))
        {
            why = $"代码仓：{projectWhy}";
            return false;
        }

        if (!ProbeOne(ArchiveRootPath, seconds, out var archiveWhy))
        {
            why = $"存档区：{archiveWhy}";
            return false;
        }

        why = string.Empty;
        return true;
    }

    private static bool ProbeOne(string path, double seconds, out string why)
    {
        var (rc, output) = CloseoutCli.Run("svn", ["info", "--show-item", "url", path], timeoutSec: seconds);
        var url = output.Trim();
        if (rc != 0 || !IsRepoUrl(url))
        {
            why = $"svn info 失败（rc={rc}）：{(url.Length > 100 ? url[..100] : url)}";
            return false;
        }

        var (rc2, log) = CloseoutCli.Run("svn", ["log", "-l", "1", "-q", url], timeoutSec: seconds);
        if (rc2 != 0 || !Regex.IsMatch(log, @"^r\d+", RegexOptions.Multiline))
        {
            var tail = CloseoutCli.TailLines(log, 1);
            why = $"svn log 失败（rc={rc2}）：{(tail.Count > 0 ? tail[0] : "无输出")}";
            return false;
        }

        why = string.Empty;
        return true;
    }

    public string KnowledgeRoot => Path.Combine(ArchiveRootPath, "knowledge");

    public string WatermarkPath => Path.Combine(ArchiveRootPath, "memory", "收尾水位.md");

    public IReadOnlyList<string> Roots => [ArchiveRootPath, CloseoutCli.Live];

    /// <summary>把 CORE 刚写的收敛点镜像到**存档区那份水位**（同一插入算法，规矩仍只在中一处）；
    /// 存档区不在 ⇒ 明说跳过，**不静默**。「提交代码仓 / 更新存档区」那两步仍由人（或以后的批）做。</summary>
    public IReadOnlyList<string> Sync(string convergeLine)
    {
        if (!Directory.Exists(ArchiveRootPath))
        {
            return [$"[存档] 存档区不在（{ArchiveRootPath}）⇒ 未镜像收敛点"];
        }

        CloseoutCli.InsertConvergeLine(WatermarkPath, convergeLine);
        return [$"水位（两份）：{WatermarkPath} / {Path.Combine(CloseoutCli.Live, "memory", "收尾水位.md")}"];
    }

    /// <summary>活版⇄存档区比对**只跑一次**（`Checks()` 与 `Read()` 共用同一份结果）。</summary>
    private CloseoutCli.Res? _parityCache;

    private CloseoutCli.Res Parity() => _parityCache ??= CheckCopyParity();

    /// <summary>顺序 = 旧 `AllChecks()` 里那一段的**原顺序**（未提交×2 → 可达性 → 删除登记 → 未纳版件 → 活版↔存档区）。</summary>
    public IReadOnlyList<CloseoutCli.Res> Checks() =>
    [
        CheckSvnDirty(CloseoutCli.Project, "未提交（代码仓）",
            "代码仓（tools/ docs/ src/ tests/）不得留未提交项；活版目录走 wb-sync、不计入本项", exclude: true),
        CheckSvnDirty(ArchiveRootPath, "未提交（存档区）", "存档区是跨机真相，活版改完必须 push 并提交（wb-sync.sh push）"),
        CheckRepoReachable(),
        CheckRepoDeletions(),
        CheckUnversioned(),
        Parity(),
    ];

    public ArchiveReadings Read()
    {
        var (pr, _) = ProjectRev();
        var (ar, _) = ArchiveRevOf();
        var pm = Regex.Match(Parity().Summary, @"（(\d+) 个文件）");
        return new(pr, ar, LastCommitDate(),
            pm.Success ? int.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture) : null);
    }

    /// <summary>远端读数**每次跑只问一遍**（`Checks()` 与 `Read()` 共用）——
    /// 否则同一行 svn 会被问两遍，而 svn 的代价是**秒级超时**（真机：`--selftest` 的「假 svn 挂起」夹具会因此撞限时）。</summary>
    private (int? Rev, string Why)? _projRev;

    private (int? Rev, string Why)? _archRev;

    private (int? Rev, string Why) ProjectRev() => _projRev ??= RemoteRev(CloseoutCli.Project);

    private (int? Rev, string Why) ArchiveRevOf() => _archRev ??= RemoteRev(ArchiveRootPath);

    public void Sync()
    {
        // 第 5 批：`svn commit`（代码仓 + 存档区）+ 双向补录。现在刻意留空 —— 本批只做「搬移」。
    }

    /// <summary>
    /// **声明即自证**：声明了 svn ⇒ 必须当场证明「本仓是工作副本」且「仓库 URL 读得到」。
    /// 证不了 ⇒ 返回一句为什么（CORE 把它变成**响亮红**，**绝不静默降级**）。
    /// </summary>
    public string? Prove()
    {
        // 工作副本根不一定是 PROJECT 本身（新库：整仓检出在上一级）。
        // 判据交给 `svn info` 自己向上找 WC —— 比「PROJECT/.svn 是否存在」更准。
        var (rc, outText) = CloseoutCli.Run("svn", ["info", "--show-item", "url", CloseoutCli.Project], timeoutSec: SvnTimeout);
        var url = outText.Trim();
        if (rc != 0 || !IsRepoUrl(url))
        {
            return $"本仓不在 SVN 工作副本内 / 读不到仓库 URL（rc={rc}）：{(url.Length > 120 ? url[..120] : url)}";
        }

        return null;
    }

    // ================================================================ 检查项

    private static CloseoutCli.Res CheckSvnDirty(string target, string name, string note, bool exclude = false)
    {
        var (rc, outText) = CloseoutCli.Run("svn", ["status", target]);
        if (rc != 0)
        {
            return CloseoutCli.R(name, "block", false, $"svn status 失败 rc={rc}", CloseoutCli.TailLines(outText), note);
        }

        var skipPrefix = exclude ? Path.Combine(CloseoutCli.Project, "whitebox") + Path.DirectorySeparatorChar : null;
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

        return CloseoutCli.R(name, "block", dirty.Count == 0,
            dirty.Count == 0 ? "0 项" : $"{dirty.Count} 项：{string.Join(", ", dirty.Take(6))}",
            null, note + $"；未版本化 {unver} 项");
    }

    internal static CloseoutCli.Res CheckRepoDeletions()
    {
        var offenders = new List<string>();
        var unreadable = new List<string>();
        foreach (var (label, root) in new[] { ("代码仓", CloseoutCli.Project), ("存档区", ArchiveRootPath) })
        {
            var (rc, outText) = CloseoutCli.Run("svn", ["status", root]);
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
            return CloseoutCli.R("删除登记", "block", false, $"svn status 读不到：{string.Join("，", unreadable)}", null, DeletionsWhy);
        }

        return CloseoutCli.R("删除登记", "block", offenders.Count == 0,
            offenders.Count == 0 ? "0 项" : $"{offenders.Count} 项：{string.Join(", ", offenders.Take(6))}", null, DeletionsWhy);
    }

    /// <summary>
    /// **未纳版件**闸门（坑 #317 的机器判据）：`svn status` 的 `?`（未纳入版本控制）**也要红** ——
    /// 旧「未提交（代码仓）」只认 M/D/A/`!`，于是「整块新目录没 add」一路静默绿。
    /// 产物子树（<c>UnverAllowProject</c> / <c>UnverAllowArchive</c>）按设计豁免。
    /// 修法 = `svn add --parents` 后**回读提交路径清单**（`wb-cli commit` 内建）。
    /// </summary>
    internal static CloseoutCli.Res CheckUnversioned()
    {
        var offenders = new List<string>();
        var unreadable = new List<string>();
        // 存档区 = 代码仓 / 活版（同一棵 WC）时**只判一次**：否则同一条未纳版会被两套白名单各判一遍
        // ⇒ 正例（`benchmark/runs` 属代码仓豁免）被存档区的白名单误伤（2026-10-20 真机自测）。
        var roots = new List<(string Label, string Root, string[] Allow)>
        {
            ("代码仓", CloseoutCli.Project, UnverAllowProject),
        };
        if (!string.Equals(ArchiveRootPath, CloseoutCli.Project, StringComparison.Ordinal))
        {
            roots.Add(("存档区", ArchiveRootPath, UnverAllowArchive));
        }

        foreach (var (label, root, allow) in roots)
        {
            var (rc, outText) = CloseoutCli.Run("svn", ["status", root]);
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
            return CloseoutCli.R("未纳版件", "block", false, $"svn status 读不到：{string.Join("，", unreadable)}", null, UnverWhy);
        }

        return CloseoutCli.R("未纳版件", "block", offenders.Count == 0,
            offenders.Count == 0 ? "0 项" : $"{offenders.Count} 项未纳版：{string.Join(", ", offenders.Take(6))}",
            offenders.Count == 0
                ? null
                : ["新建目录同样适用 —— `svn add --parents` 后必须**回读提交路径清单**（`wb-cli commit` 内建此步）",
                   $"产物子树按设计豁免：{string.Join("、", UnverAllowProject)} / {string.Join("、", UnverAllowArchive)}"],
            UnverWhy);
    }

    /// <summary>活版 ⇄ 存档区逐件比对（整树，非白名单）。</summary>
    private static CloseoutCli.Res CheckCopyParity()
    {
        // 2026-10-19 起存档区 = 活版（同一棵 WC）⇒ 自比自没有意义。**印 N/A，不冒充绿**
        // （§十·57：分母 0 不算绿；否则夹具「改活版 ⇒ 应该红」永远拦不住）。
        if (string.Equals(ArchiveRootPath, CloseoutCli.Live, StringComparison.Ordinal))
        {
            return CloseoutCli.R("活版 ↔ 存档区", "block", true,
                "不适用：存档区 = 活版（同一棵工作副本，无第二副本）",
                ["跨副本一致性由 SVN 本身负责；等重新引入独立存档区时本条自动恢复逐件比对"],
                "两副本同步才需要逐件比对；同一棵树自比自恒绿，是假绿");
        }

        var a = TreeFiles(CloseoutCli.Live);
        var b = TreeFiles(ArchiveRootPath);
        var diff = new List<string>();
        foreach (var rel in a.Intersect(b).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (!SameBytes(Path.Combine(CloseoutCli.Live, rel), Path.Combine(ArchiveRootPath, rel)))
            {
                diff.Add(rel + "（字节不同）");
            }
        }

        diff.AddRange(a.Except(b).OrderBy(x => x, StringComparer.Ordinal).Select(rel => rel + "（活版有、存档区缺）"));
        diff.AddRange(b.Except(a).OrderBy(x => x, StringComparer.Ordinal).Select(rel => rel + "（存档区有、活版缺）"));
        var total = a.Union(b).Count();
        return CloseoutCli.R("活版 ↔ 存档区", "block", diff.Count == 0,
            diff.Count == 0 ? $"全部一致（{total} 个文件）" : $"{diff.Count} 处不一致（共 {total} 件）：{string.Join(", ", diff.Take(5))}",
            diff.Count == 0 ? null : ["逐件比对（整树，非白名单）；哪一侧独有/更新都算欠账"],
            "双向补录：活版 ⇄ 存档区只这一对，整树不得有任何一处不同");
    }

    private CloseoutCli.Res CheckRepoReachable()
    {
        var (pr, e1) = ProjectRev();
        var (ar, e2) = ArchiveRevOf();
        var ok = pr is not null && ar is not null;
        if (ok)
        {
            return CloseoutCli.R("可达性前置", "block", true, $"远端读数可用（代码仓 r{pr} · 存档区 r{ar}）", null,
                "判据先要能拿到读数：远端查询拿不到数就不能声称收敛");
        }

        return CloseoutCli.R("可达性前置", "block", false, "拿不到远端读数（超时 / 不可达）⇒ 收尾不可收敛",
            [$"代码仓：{(e1.Length > 0 ? e1 : "ok")}", $"存档区：{(e2.Length > 0 ? e2 : "ok")}",
             "先修可达性（路由 / TUN / 仓库），再收尾；不要跳过本项、更不要改闸门让它跑起来"],
            $"判据先要能拿到读数（超时上限 {SvnTimeout:0}s，`CLOSEOUT_SVN_TIMEOUT` 可调）");
    }

    // ================================================================ 读数

    /// <summary>
    /// 「拿到的是一个**仓库 URL**、不是一句报错」—— **唯一声明处**。
    /// <para>scheme 三族都要认：`http(s)://`（内网 VisualSVN）· `svn://` / `svn+ssh://`（svnserve）·
    /// `file://`（**本机仓库** —— 2026-10-15 主人令：给本地版配一份本地 SVN 作存档区）。</para>
    /// </summary>
    private static bool IsRepoUrl(string url) =>
        url.StartsWith("http", StringComparison.Ordinal)
        || url.StartsWith("svn", StringComparison.Ordinal)
        || url.StartsWith("file://", StringComparison.Ordinal);

    /// <summary>**只问远端**：该 URL 上的最新提交号；拿不到 ⇒ (null, 起因)。绝不回落本地读数。</summary>
    private static (int? Rev, string Why) RemoteRev(string path)
    {
        var (rc, outText) = CloseoutCli.Run("svn", ["info", "--show-item", "url", path], timeoutSec: SvnTimeout);
        var url = outText.Trim();
        if (rc != 0 || !IsRepoUrl(url))
        {
            return (null, $"取不到 URL（rc={rc}）：{(url.Length > 120 ? url[..120] : url)}");
        }

        var (rc2, out2) = CloseoutCli.Run("svn", ["log", "-l", "1", "-q", "-r", "HEAD:1", url], timeoutSec: SvnTimeout);
        var m = Regex.Match(out2.Trim(), @"^r(\d+)", RegexOptions.Multiline);
        if (rc2 != 0 || !m.Success)
        {
            var t = CloseoutCli.TailLines(out2, 1);
            return (null, t.Count > 0 ? t[0] : "远端查询无输出");
        }

        return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), "");
    }

    /// <summary>远端「最后活动日」（提交日的日期部分）；拿不到 ⇒ null（CORE 再用账本兜底）。</summary>
    private static string? LastCommitDate()
    {
        var (rc, outText) = CloseoutCli.Run("svn", ["info", "--show-item", "url", CloseoutCli.Project]);
        var url = outText.Trim();
        if (rc == 0 && IsRepoUrl(url))
        {
            var (rc2, out2) = CloseoutCli.Run("svn", ["log", "-l", "1", "-q", "-r", "HEAD:1", url]);
            var m = Regex.Match(out2, @"(\d{4}-\d{2}-\d{2})");
            if (rc2 == 0 && m.Success)
            {
                return m.Groups[1].Value;
            }
        }

        return null;
    }

    // ================================================================ 文件树/字节比对

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

        return CloseoutCli.Sha256File(a) == CloseoutCli.Sha256File(b);
    }
}

/// <summary>`tools/closeout/archive.xml` 的强类型形状（与项目同一族：`DataContractXml`）。</summary>
[DataContract(Name = "CloseoutArchiveConfig", Namespace = "")]
internal sealed class CloseoutArchiveConfig
{
    [DataMember(Order = 0)] public string Kind { get; set; } = "none";
}

/// <summary>解析结果：用哪个实现、声明了什么、以及要**说给人听**的那句话。</summary>
/// <param name="Impl">选中的实现。</param>
/// <param name="Kind">`none` / `svn`。</param>
/// <param name="Declared">是否来自**声明文件**（false = 老布局，按 `.svn` 推断）。</param>
/// <param name="Note">给人看的说明（推断出来的话要说出来）。</param>
internal sealed record ArchiveSelection(ICloseoutArchive Impl, string Kind, bool Declared, string Note);

/// <summary>扩展实现的**选择**（主人 2026-10-16 12:2x：模式由**声明**决定，不再从 `.svn` 猜）。</summary>
internal static class CloseoutArchiveFactory
{
    /// <summary>仅自证用：强制按「连 SVN」算（只影响**未声明时**的推断）。</summary>
    internal static bool SvnLinkedOverride;

    private static readonly ICloseoutArchive None = new NoArchive();

    private static readonly ICloseoutArchive Svn = new SvnArchive();

    private static bool _probed;

    private static ArchiveSelection? _fallback;

    /// <summary>测试用：清掉进程内的「已探针 / 降级」状态。</summary>
    internal static void ResetProbe()
    {
        _probed = false;
        _fallback = null;
    }

    /// <summary>
    /// **一次探针 + 自动退化**（主人 2026-10-18 令）：
    /// 声明 svn 但第一次探不到 ⇒ 本进程**立即改走 `NoArchive`**（标准的「不用任何 VCS」模式），
    /// 之后的收尾不再问 SVN、不再等超时；降级理由写在「存档声明」这一项里（可见，不静默）。
    /// </summary>
    public static ArchiveSelection ResolveWithFallback(double probeSeconds = 3)
    {
        // 自证 / 负例需要「强制按 svn 语义」跑：显式关掉自动降级（只有测试会设这个变量）。
        if (Environment.GetEnvironmentVariable("CLOSEOUT_NO_SVN_FALLBACK") == "1")
        {
            return Resolve();
        }

        if (!_probed)
        {
            _probed = true;
            var selected = Resolve();
            if (selected.Kind == "svn"
                && selected.Impl is SvnArchive svn
                && !svn.ProbeFast(probeSeconds, out var why))
            {
                _fallback = new ArchiveSelection(
                    None,
                    "none",
                    true,
                    $"SVN 不可达 ⇒ 本次收尾自动降级为 none（{why}）");
            }
        }

        return _fallback ?? Resolve();
    }

    /// <summary>声明文件（**唯一一处**）：`tools/closeout/archive.xml` —— 随工具走（副本也带着）。</summary>
    public static string DeclarationPath => Path.Combine(CloseoutCli.Project, "tools", "closeout", "archive.xml");

    /// <summary>每次解析（文件小；不缓存 ⇒ 没有「启动时算一次、之后改不动」的隐性状态）。</summary>
    public static ArchiveSelection Resolve()
    {
        if (File.Exists(DeclarationPath))
        {
            try
            {
                var cfg = DataContractXml.ReadFile<CloseoutArchiveConfig>(DeclarationPath);
                return (cfg?.Kind ?? "none").Trim().ToLowerInvariant() switch
                {
                    "svn" => new ArchiveSelection(Svn, "svn", true, "已声明：SVN 存档"),
                    "none" => new ArchiveSelection(None, "none", true, "已声明：不使用任何版本库"),
                    var other => new ArchiveSelection(None, "none", true, $"已声明：{other}（未知 kind ⇒ 按 none 走）"),
                };
            }
            catch (Exception ex)
            {
                // 声明**读不出来**也**不许静默**：响亮说出来，按 none 走（并请人修）。
                return new ArchiveSelection(None, "none", true,
                    $"声明文件读不出来（{ex.GetType().Name}）⇒ 按 none 走 —— 请修 {DeclarationPath}");
            }
        }

        // 老布局：没声明 ⇒ 按本仓根有没有 `.svn` **推断**，并把「这是推断」**写在第一项上**（不许静默）。
        var inferred = SvnLinkedOverride
            || Environment.GetEnvironmentVariable("CLOSEOUT_FORCE_SVN") == "1"
            || Directory.Exists(Path.Combine(CloseoutCli.Project, ".svn"));
        return inferred
            ? new ArchiveSelection(Svn, "svn", false, $"未声明 ⇒ 按本仓根 .svn 推断为 svn（建议补 {DeclarationPath}）")
            : new ArchiveSelection(None, "none", false, $"未声明 ⇒ 按本仓根无 .svn 推断为 none（建议补 {DeclarationPath}）");
    }
}
