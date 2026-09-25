using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AgentRuntime.Core.Configuration;
using AgentRuntime.Core.Protocol;

namespace AgentRuntime.Hosting;

/// <summary>一条收尾件：交了什么 / 还缺什么（<see cref="Status"/> 三态，**不猜**）。</summary>
public sealed record CloseoutItem(string Id, string Title, CloseoutItemStatus Status, string Detail);

/// <summary>收尾件的状态。三态而不是布尔：「没配」与「没交」是两件事，混成 <c>false</c> 会让人去追一个不存在的缺失。</summary>
public enum CloseoutItemStatus
{
    /// <summary>已对齐（交齐了 / 无待处理项）。</summary>
    Ok,

    /// <summary>有东西没交或没收敛（**要处理**）。</summary>
    Pending,

    /// <summary>没配工作区（**无从校验**，只报协议义务）。</summary>
    Unconfigured,

    /// <summary>看不清（文件在但读不出 / 格式不认识）—— 与「没问题」**严格区分**。</summary>
    Unknown,
}

/// <summary>一次收尾件检查的结果（纯数据 + 可打印的行）。</summary>
public sealed record CloseoutChecklistResult(
    string? Workspace,
    IReadOnlyList<CloseoutItem> Items,
    IReadOnlyList<string> Notes,
    string? MemoryWatermark = null,
    int MemoryPending = 0)
{
    /// <summary>已对齐的条数。</summary>
    public int OkCount => Items.Count(i => i.Status == CloseoutItemStatus.Ok);

    /// <summary>要处理的条数（<see cref="CloseoutItemStatus.Pending"/>）。</summary>
    public int PendingCount => Items.Count(i => i.Status == CloseoutItemStatus.Pending);

    /// <summary>扫的这几件是不是都交了（<see cref="CloseoutItemStatus.Pending"/> 为 0）。</summary>
    public bool AllSatisfied => PendingCount == 0;
}

/// <summary>
/// **收尾件检查**（WB 侧 · 协议 v10 第 7 条的**判据**）—— 把「收尾要交什么」从散文变成**看得见的清单**。
/// <para>
/// 边界（刻意的）：
/// <list type="number">
/// <item>**只读**：只看，不写、不改写语料、不晋级；</item>
/// <item>**不复制别的工具的逻辑**：知识库的「态 / 可晋级」判定归 <c>knowledge-repo.py</c>（单一来源）——
/// 这里只报**事实**（版本号 / md 指纹 / 账本指纹 / 两者是否相等）+ 该跑的那条命令；</item>
/// <item>**没配就明说**（<see cref="CloseoutItemStatus.Unconfigured"/>），不假装查过。</item>
/// </list>
/// </para>
/// <para>
/// 为什么要有它：收尾件（记忆抽象 / handoff / 坑 / 晋级）以前只有「记得做」这一层保证，
/// 于是 v9 的 closeout 实际只剩「记账 + 换流」——**协议说了、运行时看不出**（PITFALLS #84 一族的病）。
/// </para>
/// </summary>
public static class CloseoutChecklist
{
    private static readonly Regex DatedFile = new(@"^(?<date>\d{4}-\d{2}-\d{2})(-\d{4})?\.md$", RegexOptions.Compiled);

    /// <summary>流文件名里的日期（<c>stream-20260921-212947.jsonl</c>）—— 它证明「这一天有过会话」。</summary>
    private static readonly Regex StreamDate = new(@"^stream-(?<y>\d{4})(?<m>\d{2})(?<d>\d{2})-\d+\.jsonl$", RegexOptions.Compiled);

    /// <summary><c>AGENTS.md</c> 里被反引号点名的 <c>.md</c>（PITFALLS #92：改名了、指路人没改）。</summary>
    private static readonly Regex PointerToken = new(@"`([A-Za-z0-9_./\-]+\.md)`", RegexOptions.Compiled);

    /// <summary>扫一遍收尾件（只读）。</summary>
    public static CloseoutChecklistResult Inspect(RuntimeConfiguration config, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(config);

        var workspace = config.Lifecycle.Workspace;
        var notes = new List<string>();

        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return new CloseoutChecklistResult(
                workspace,
                [
                    new CloseoutItem("memory", "会话记忆（水位线之后的）", CloseoutItemStatus.Unconfigured, "未配 lifecycle.workspace ⇒ 不校验"),
                    new CloseoutItem("handoff", "跨端交接件", CloseoutItemStatus.Unconfigured, "同上"),
                    new CloseoutItem("pitfalls", "踩坑集", CloseoutItemStatus.Unconfigured, "同上"),
                    new CloseoutItem("knowledge", "知识库晋级", CloseoutItemStatus.Unconfigured, "同上"),
                    new CloseoutItem("pointers", "顶层文件指针", CloseoutItemStatus.Unconfigured, "同上"),
                ],
                ["未配工作区 ⇒ 只报协议义务（第 7 条），**不假装查过**。配 lifecycle.workspace 即可开校验。"]);
        }

        var memoryItem = MemoryItem(config, workspace, now);
        var items = new List<CloseoutItem>
        {
            memoryItem,
            HandoffItem(workspace, now),
            PitfallsItem(config, now),
            KnowledgeItem(workspace),
            PointerItem(config, workspace),
        };

        var draft = now;   // 占位不用；下面的 note 由调用方补（草稿来自活模块）
        _ = draft;

        notes.Add("R5 草稿：把里面**已定**的挑出来（→ 知识或待办），未定的留给下一个会话 —— 这是最容易漏的一类源。");

        // 记忆水位（工作区那本账）——收尾报告要把它与**会话水位**（Runtime 那本账）并排显示（见 SessionLifecycle）。
        var text = File.ReadAllText(Path.Combine(workspace, "memory", "收尾水位.md"));
        return new CloseoutChecklistResult(
            workspace,
            items,
            notes,
            TryReadWatermark(text),
            string.IsNullOrEmpty(text) ? 0 : Directory.EnumerateFiles(Path.Combine(workspace, "memory"), "*.md")
                .Select(Path.GetFileName)
                .Count(n => n is not null && DatedFile.IsMatch(n) && string.CompareOrdinal(DatedFile.Match(n).Groups["date"].Value, TryReadWatermark(text) ?? "0") > 0));
    }

    /// <summary>把结果渲染成屏上的行（纯文本、可 diff）。</summary>
    public static IReadOnlyList<string> Render(CloseoutChecklistResult result, int draftLines, IReadOnlyList<string> misc)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = new List<string>
        {
            $"[收尾件] 协议第 7 条的**三层**：{string.Join(" → ", CloseoutLayers.All.Select(l => l.Title))}",
            $"[收尾件] 工作区：{result.Workspace ?? "（未配 lifecycle.workspace）"}",
        };

        foreach (var item in result.Items)
        {
            lines.Add($"[收尾件] {Marker(item.Status)} {item.Title}：{item.Detail}");
        }

        lines.Add(misc.Count == 0
            ? $"[收尾件] {Marker(CloseoutItemStatus.Ok)} 未收敛项（misc）：无"
            : $"[收尾件] {Marker(CloseoutItemStatus.Pending)} 未收敛项（misc）：{misc.Count} 条（收尾只上报，不改写语料）");

        foreach (var note in result.Notes)
        {
            lines.Add($"[收尾件] · {note}");
        }

        if (draftLines > 0)
        {
            lines.Add($"[收尾件] · 当前草稿 {draftLines} 行（见上条）。");
        }

        lines.Add(result.PendingCount == 0
            ? $"[收尾件] 小结：{result.Items.Count} 项已对齐 —— 收尾件齐了。"
            : $"[收尾件] 小结：{result.OkCount}/{result.Items.Count} 已对齐，**{result.PendingCount} 项待处理** —— 收尾件不齐不等于不能收尾，但别当它交齐了。");

        return lines;
    }

    private static string Marker(CloseoutItemStatus status) => status switch
    {
        CloseoutItemStatus.Ok => "✅",
        CloseoutItemStatus.Pending => "⏳",
        CloseoutItemStatus.Unconfigured => "➖",
        _ => "❓",
    };

    /// <summary>
    /// 记忆：读水位（<c>memory/收尾水位.md</c>）→ 两件事分开报：
    /// ① **应有而缺失**（PITFALLS #93）：**有过会话的日子**（流文件名里的日期 ∪ 今天）都该有一篇日记，缺就 ⏳；
    /// ② **已有未抽象**：水位之后已存在的日记，提醒抽象进知识库。
    /// <para>为什么要 ①：原来的判据只数「已存在的文件」⇒ 日记根本没写时输出「0 篇」并给 ✅（连续两天的假绿）。</para>
    /// </summary>
    private static CloseoutItem MemoryItem(RuntimeConfiguration config, string workspace, DateTimeOffset now)
    {
        var memory = Path.Combine(workspace, "memory");
        if (!Directory.Exists(memory))
        {
            return new CloseoutItem("memory", "会话记忆（水位线之后的）", CloseoutItemStatus.Unknown, $"找不到 {memory}");
        }

        var watermarkPath = Path.Combine(memory, "收尾水位.md");
        if (!File.Exists(watermarkPath))
        {
            return new CloseoutItem("memory", "会话记忆（水位线之后的）", CloseoutItemStatus.Unknown, "没有 memory/收尾水位.md（水位读不出 ⇒ 不知道整理到哪，**不猜**）");
        }

        var watermark = TryReadWatermark(File.ReadAllText(watermarkPath));
        if (watermark is null)
        {
            return new CloseoutItem("memory", "会话记忆（水位线之后的）", CloseoutItemStatus.Unknown, "水位文件里没找到 YYYY-MM-DD（格式变了？）");
        }

        var newer = Directory.EnumerateFiles(memory, "*.md")
            .Select(Path.GetFileName)
            .Where(n => n is not null && DatedFile.IsMatch(n))
            .Select(n => (Name: n!, Date: DatedFile.Match(n!).Groups["date"].Value))
            .Where(f => string.CompareOrdinal(f.Date, watermark) > 0)
            .Select(f => f.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // ① 应有而缺失：有过会话的日子都该有一篇日记（缺件 ≠ 无件）。
        var existingDates = Directory.EnumerateFiles(memory, "*.md")
            .Select(Path.GetFileName)
            .Where(n => n is not null && DatedFile.IsMatch(n))
            .Select(n => DatedFile.Match(n!).Groups["date"].Value)
            .ToHashSet(StringComparer.Ordinal);
        var expected = SessionDays(config, now, watermark);
        var missing = expected.Where(d => !existingDates.Contains(d)).OrderBy(d => d, StringComparer.Ordinal).ToArray();

        if (missing.Length > 0)
        {
            var detail = $"水位 {watermark}；**缺 {missing.Length} 篇日记**：{Head([.. missing.Select(d => $"{d}.md")], 4)} "
                         + "（有过会话的日子都必须有一篇 —— 缺件与无件不是一回事）";
            return new CloseoutItem("memory", "会话记忆（水位线之后的）", CloseoutItemStatus.Pending, detail);
        }

        var detail2 = newer.Length == 0
            ? $"水位 {watermark}；日记齐了（今天 {now:yyyy-MM-dd}）"
            : $"水位 {watermark}；**晚于水位 {newer.Length} 篇未抽象**：{Head(newer, 4)}";

        return new CloseoutItem(
            "memory",
            "会话记忆（水位线之后的）",
            newer.Length == 0 ? CloseoutItemStatus.Ok : CloseoutItemStatus.Pending,
            detail2);
    }

    /// <summary>
    /// **有过会话的日子**（水位之后、今天之前（含））—— 会话的证据是**流文件**：
    /// 产物目录里每个 <c>stream-YYYYMMDD-HHMMSS.jsonl</c> 都表示那天真跑过；今天则必然有（收尾就发生在今天）。
    /// </summary>
    private static IReadOnlyList<string> SessionDays(RuntimeConfiguration config, DateTimeOffset now, string watermark)
    {
        var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var days = new SortedSet<string>(StringComparer.Ordinal) { today };

        var streamPath = config.Stream?.Path;
        var directory = string.IsNullOrWhiteSpace(streamPath) ? null : Path.GetDirectoryName(Path.GetFullPath(streamPath));
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "stream-*.jsonl"))
            {
                var name = Path.GetFileName(file);
                if (name.Contains("corrupt", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var match = StreamDate.Match(name);
                if (match.Success)
                {
                    days.Add($"{match.Groups["y"].Value}-{match.Groups["m"].Value}-{match.Groups["d"].Value}");
                }
            }
        }

        return days
            .Where(d => string.CompareOrdinal(d, watermark) > 0 && string.CompareOrdinal(d, today) <= 0)
            .ToArray();
    }

    /// <summary>
    /// **顶层文件指针**（PITFALLS #92）：<c>AGENTS.md</c> 里被反引号点名的 <c>.md</c> 必须真存在。
    /// <para>为什么：本端文件改过名（<c>METHODOLOGY.md</c> → <c>KNOWLEDGE.md</c>、<c>memory/INDEX.md</c> → <c>MEMORYINDEX.md</c>），
    /// 而这份副本里的指针没跟着改 ⇒ 模型按铁则去读 → 文件不存在 → 收尾卡死。</para>
    /// <para>裁决口径：先看工作区根，再看项目文档目录（坑集旁边）与仓库根；
    /// **改名 / 缺席字样只在 token 邻近 10 字内生效**（含 <c>⇒</c> · <c>改名</c> · <c>原 </c> · <c>暂无</c> · <c>没有</c> · <c>已并</c>）、
    /// **占位符**（<c>YYYY</c> 等）、以及 <c>workspace/…</c>（SVN 公共工作区口径，不在本机工作区内）一律**不当指针**。</para>
    /// </summary>
    private static CloseoutItem PointerItem(RuntimeConfiguration config, string workspace)
    {
        var agents = Path.Combine(workspace, "AGENTS.md");
        if (!File.Exists(agents))
        {
            return new CloseoutItem("pointers", "顶层文件指针", CloseoutItemStatus.Unknown, $"找不到 {agents}");
        }

        var docsDirectory = string.IsNullOrWhiteSpace(config.Lifecycle.Pitfalls)
            ? null
            : Path.GetDirectoryName(Path.GetFullPath(config.Lifecycle.Pitfalls));
        var repoRoot = docsDirectory is null ? null : Path.GetDirectoryName(docsDirectory);

        var checkedCount = 0;
        var missing = new List<string>();
        foreach (var line in File.ReadLines(agents))
        {
            foreach (Match match in PointerToken.Matches(line))
            {
                var token = match.Groups[1].Value;
                if (token.Contains("YYYY", StringComparison.Ordinal) || token.Contains("NNN", StringComparison.Ordinal)
                    || token.Contains('<') || token.Contains('…')
                    || token.StartsWith("workspace/", StringComparison.Ordinal))
                {
                    continue;                       // 占位符 / 公共工作区口径
                }

                // ⚠️ 豁免必须**逐 token 看邻近字样**（行级豁免会把真实漂移一起豁免掉：
                //    历史那行「其余一切内容见 `METHODOLOGY.md` 顶部（原 `INDEX.md` 已并为其薄壳）」含「已并」——
                //    行级豁免会让 METHODOLOGY.md / memory/INDEX.md 两个真缺的指针隐身）。
                if (IsRenameOrAbsenceContext(line, match.Index, match.Length))
                {
                    continue;
                }

                checkedCount++;
                if (!Exists(token, workspace, repoRoot, docsDirectory))
                {
                    missing.Add(token);
                }
            }
        }

        var distinct = missing.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        return distinct.Length == 0
            ? new CloseoutItem("pointers", "顶层文件指针", CloseoutItemStatus.Ok, $"AGENTS.md 点名的 {checkedCount} 个 .md 都在")
            : new CloseoutItem(
                "pointers",
                "顶层文件指针",
                CloseoutItemStatus.Pending,
                $"AGENTS.md 点名 {checkedCount} 个，**缺 {distinct.Length} 个**：{Head(distinct, 4)}（改名了就得改指针）");
    }

    private static bool Exists(string token, string workspace, string? repoRoot, string? docsDirectory) =>
        File.Exists(Path.Combine(workspace, token))
        || (repoRoot is not null && File.Exists(Path.Combine(repoRoot, token)))
        || (docsDirectory is not null && File.Exists(Path.Combine(docsDirectory, Path.GetFileName(token))));

    /// <summary>这只 token 的**前后 10 字**里有「改名 / 原 / 旧 / 暂无 / 没有 / 已并 / ⇒」这类字样 ⇒ 它不是指针（旧名或明说没有）。</summary>
    private static bool IsRenameOrAbsenceContext(string line, int index, int length)
    {
        var from = Math.Max(0, index - 10);
        var to = Math.Min(line.Length, index + length + 10);
        var window = line[from..to];
        foreach (var marker in new[] { "⇒", "改名", "原 ", "原名", "旧名", "暂无", "没有", "已并", "废弃", "（旧）" })
        {
            if (window.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>handoff：今天（会话日）的条目数 —— 每个 task 一件。</summary>
    private static CloseoutItem HandoffItem(string workspace, DateTimeOffset now)
    {
        var handoff = Path.Combine(workspace, "handoff");
        if (!Directory.Exists(handoff))
        {
            return new CloseoutItem("handoff", "跨端交接件（每个 task 一件）", CloseoutItemStatus.Unknown, $"找不到 {handoff}");
        }

        var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var files = Directory.EnumerateFiles(handoff)
            .Select(p => (Name: Path.GetFileName(p), Modified: File.GetLastWriteTime(p).Date))
            .Where(f => !f.Name.StartsWith(".", StringComparison.Ordinal))
            .ToArray();

        // 主判据 = **文件名里带今天的日期**（交接件的命名约定就是带日期）。
        // 不拿「改过时间」当判据（目录里随便动一下文件就会把它算成交了 —— 那是假绿）。
        var todayFiles = files
            .Where(f => f.Name.Contains(today, StringComparison.Ordinal))
            .Select(f => f.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // 没带日期但今天动过的，另外报（它是线索，不是证据）。
        var touched = files.Count(f => !f.Name.Contains(today, StringComparison.Ordinal) && f.Modified == now.Date);

        var detail = todayFiles.Length == 0
            ? $"今天（{today}）0 件 —— 若本会话有跨端结论，这里应当有一条"
            : $"今天（{today}）{todayFiles.Length} 件：{Head(todayFiles, 3)}";

        if (touched > 0)
        {
            detail += $"；另有 {touched} 个今天改过但名字不带日期的文件（核对一下是不是该改名的交接件）";
        }

        return new CloseoutItem(
            "handoff",
            "跨端交接件（每个 task 一件）",
            todayFiles.Length == 0 ? CloseoutItemStatus.Pending : CloseoutItemStatus.Ok,
            detail);
    }

    /// <summary>踩坑集：今天的条目数（按条目正文里的日期认）。</summary>
    private static CloseoutItem PitfallsItem(RuntimeConfiguration config, DateTimeOffset now)
    {
        var path = config.Lifecycle.Pitfalls;
        if (string.IsNullOrWhiteSpace(path))
        {
            return new CloseoutItem("pitfalls", "踩坑集", CloseoutItemStatus.Unconfigured, "未配 lifecycle.pitfalls ⇒ 不校验");
        }

        if (!File.Exists(path))
        {
            return new CloseoutItem("pitfalls", "踩坑集", CloseoutItemStatus.Unknown, $"找不到 {path}");
        }

        var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var count = File.ReadLines(path).Count(l => l.StartsWith("## ", StringComparison.Ordinal) && l.Contains(today, StringComparison.Ordinal));

        var detail = count == 0
            ? $"今天（{today}）0 条 —— 若今天没踩坑，这行就是「无需交」，但那是**判断**不是忽略"
            : $"今天（{today}）{count} 条（{path}）";

        return new CloseoutItem(
            "pitfalls",
            "踩坑集",
            count == 0 ? CloseoutItemStatus.Pending : CloseoutItemStatus.Ok,
            detail);
    }

    /// <summary>
    /// 知识库：**只报事实**（版本 / md 指纹 / 账本指纹 / 是否相等）+ 该跑的命令。
    /// <para>态与晋级的判定归 <c>knowledge-repo.py</c>（单一来源）—— 这里不复制它。</para>
    /// </summary>
    private static CloseoutItem KnowledgeItem(string workspace)
    {
        var knowledge = Path.Combine(workspace, "knowledge");
        var md = Path.Combine(knowledge, "knowledge.md");
        if (!File.Exists(md))
        {
            return new CloseoutItem("knowledge", "知识库晋级", CloseoutItemStatus.Unknown, $"找不到 {md}");
        }

        var versions = Directory.Exists(Path.Combine(knowledge, "versions"))
            ? Directory.EnumerateFiles(Path.Combine(knowledge, "versions"), "knowledge-v*.md").Count()
            : 0;

        var mdSha = Sha256Prefix(md);
        var ledgerSha = ReadLedgerSha(Path.Combine(knowledge, ".ledger.json"));

        var detail = ledgerSha is null
            ? $"版本快照 {versions} 个 · md 指纹 {mdSha} · **账本缺**（无法比对）⇒ 判定与晋级归工具"
            : $"版本快照 {versions} 个 · md 指纹 {mdSha} · 账本指纹 {ledgerSha}（{(mdSha == ledgerSha ? "相等 ⇒ 已收集，晋级会产生新版本" : "不等 ⇒ **有未收集的改动**")}）";

        return new CloseoutItem(
            "knowledge",
            "知识库晋级",
            mdSha == ledgerSha ? CloseoutItemStatus.Ok : CloseoutItemStatus.Pending,
            detail + "；判定/晋级：`python3 tools/skill-repo/knowledge-repo.py --out <knowledge> --closeout|--promote`");
    }

    /// <summary>水位文件里第一个 <c>YYYY-MM-DD</c>（"当前水位：截至 2026-09-17" 这类写法都认）。</summary>
    internal static string? TryReadWatermark(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.Contains("当前水位", StringComparison.Ordinal) && !line.Contains("last_organized", StringComparison.Ordinal))
            {
                continue;
            }

            var match = Regex.Match(line, @"\d{4}-\d{2}-\d{2}");
            if (match.Success)
            {
                return match.Value;
            }
        }

        return null;
    }

    private static string Sha256Prefix(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream))[..12].ToLowerInvariant();
    }

    private static string? ReadLedgerSha(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("current", out var current)
                && current.TryGetProperty("sha256", out var sha) && sha.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var value = sha.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value[..12].ToLowerInvariant();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        return null;
    }

    private static string Head(IReadOnlyList<string> names, int count) =>
        names.Count <= count
            ? string.Join('、', names)
            : string.Join('、', names.Take(count)) + $"…（共 {names.Count}）";
}
