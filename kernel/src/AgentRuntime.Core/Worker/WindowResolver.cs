using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// 派发前把 window 解析成**绝对路径**（2026-09-27 用户令①）。
/// 根因：master 基准 = 仓库根，worker 的 cwd = whitebox/workspace/worker
/// ⇒ 同一个相对路径两边解析不同（本夜 4/4 派发因此空手："仓库路径未提供"）。
/// 规则：已是绝对路径 ⇒ 原样保留；否则拼上仓库根并规范化。
/// 仓库根 = 从当前目录向上找到含 AgentRuntime.slnx（或 .svn/.git）的那一层。
/// </summary>
public static class WindowResolver
{
    /// <summary>从 <paramref name="startDir"/> 向上找仓库根；找不到返回 null（不猜）。</summary>
    public static string? FindRepoRoot(string? startDir = null)
    {
        // 2026-09-27（A1 根因）：**先找代码根，再退回工作副本根** ——
        // 旧写法「先撞上谁就返回谁」在 `whitebox/workspace`（有 .svn、没有 .slnx）处会**提前命中**
        // ⇒ worker 的工具基准被定成它自己的 workspace（真机：read 相对路径 ⇒
        // `…/whitebox/workspace/SourceIndex.cs` 文件不存在）。判据：base 必须是**能编译的那一层**。
        var d = new DirectoryInfo(startDir ?? Directory.GetCurrentDirectory());
        string? svn = null;
        for (int i = 0; i < 12 && d is not null; i++, d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "AgentRuntime.slnx"))) return d.FullName;
            if (svn is null && Directory.Exists(Path.Combine(d.FullName, ".svn"))) svn = d.FullName;
        }

        return svn;
    }

    /// <summary>展开 <c>$WB_ROOT / $WB_LIVE / $WB_ARCHIVE / ~</c>（A2）。认不出的 <c>$VAR</c> ⇒ **原样保留**（fail-closed，不猜）。
    /// <para>来历：现场 window 写字面量 <c>$WB_ROOT</c> ⇒ worker 只能拒「未提供仓库路径」；令牌不是路径，得先展开。</para></summary>
    public static string Expand(string window)
    {
        if (string.IsNullOrWhiteSpace(window)) return string.Empty;
        var w = window.Trim();
        if (w == "~" || w.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            w = Path.Combine(home, w.Length > 2 ? w[2..] : string.Empty);
        }
        foreach (var name in new[] { "WB_ROOT", "WB_LIVE", "WB_ARCHIVE" })
        {
            var token = "$" + name;
            if (!w.Contains(token, StringComparison.Ordinal)) continue;
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) return w;      // 展不开 ⇒ 原样保留，由 Refusal 响亮拒绝
            w = w.Replace(token, value, StringComparison.Ordinal);
        }
        return w;
    }

    /// <summary>展不开时的响亮拒绝（A2）：返回拒绝理由；能展开 ⇒ <c>null</c>。判据 = 展开后仍留 <c>$</c>。</summary>
    public static string? Refusal(string window)
    {
        var w = Expand(window);
        return w.Contains('$', StringComparison.Ordinal)
            ? $"window 里的环境变量展不开（不是路径）：{window.Trim()} —— 请改用绝对路径，或先设好 $WB_ROOT/$WB_LIVE/$WB_ARCHIVE"
            : null;
    }

    /// <summary>单条：先展开令牌；相对 ⇒ 拼仓库根；**展不开或根找不到 ⇒ 空串**（A2 fail-closed：宁可拒绝，不猜一个目录）。</summary>
    public static string Resolve(string window, string? repoRoot = null)
    {
        if (string.IsNullOrWhiteSpace(window)) return string.Empty;
        var w = Expand(window);
        if (w.Contains('$', StringComparison.Ordinal)) return string.Empty;   // A2：展不开 ⇒ 空（调用方据此拒绝）
        if (Path.IsPathRooted(w)) return Path.GetFullPath(w);
        var root = repoRoot ?? FindRepoRoot();
        if (root is null) return string.Empty;

        var direct = Path.GetFullPath(Path.Combine(root, w));
        if (File.Exists(direct) || Directory.Exists(direct)) return direct;

        // Normalize-first (real machine 2026-09-29): when the joined path does not exist, walk up to
        // 6 ancestor levels and take the first same-shaped path that DOES exist. Live case: master gave
        // `projects/AgentRuntime/docs/PITFALLS.md` (relative to the working-copy root) while base was
        // already `<wc>/projects/AgentRuntime` => naive join doubled the middle segment.
        // Only for relative inputs whose join is missing => write targets (not yet existing) unchanged.
        var d = new DirectoryInfo(root);
        for (int i = 0; i < 6 && d?.Parent is not null; i++)
        {
            d = d.Parent;
            var candidate = Path.GetFullPath(Path.Combine(d.FullName, w));
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }

        return direct;
    }

    /// <summary>**工具基准**（A1）：window 解析所依据的那一层绝对根（worker 的工具基准 = 它，而不是 worker 自己的落点根）。</summary>
    public static string ResolveBase(string? repoRoot = null) => repoRoot ?? FindRepoRoot() ?? string.Empty;

    /// <summary>列表版（派发物里的 Window 就是列表）。</summary>
    public static IReadOnlyList<string> Resolve(IEnumerable<string>? windows, string? repoRoot = null)
    {
        if (windows is null) return Array.Empty<string>();
        return windows.Select(w => Resolve(w, repoRoot)).Where(w => w.Length > 0).ToArray();
    }
    public static IReadOnlyList<string> ResolveMany(System.Collections.Generic.IEnumerable<string>? w, string? root=null) => Resolve(w, root);

    // ---------------- A 方案（2026-09-29 · 用户裁决）：派发地址一律绝对 ----------------

    /// <summary>
    /// **派发前的地址审**：相对 ⇒ 归一成绝对；归一不到、或**相对路径解到的位置不存在** ⇒
    /// **拒绝并给绝对路径建议**。
    /// <para>现场（为什么要它）：连派 4 次「读 handoff/36」都被 worker 如实回 impossible ——
    /// 同一个相对路径在 master 与 worker 两侧解析成不同的位置 ⇒ 12 轮白烧，而**派发前就能判出来**。</para>
    /// <para>返回 <c>null</c> = 通过（<paramref name="resolved"/> = 绝对路径）；否则 = 拒绝理由（含建议）。</para>
    /// <para><b>为什么只对相对输入要求「存在」</b>：绝对路径是 master 的**显式断言**，而现场的病是
    /// 「同一个相对路径两侧解析不同」；绝对但不存在 ⇒ 由调用方**响亮报**（见 <see cref="MissingNote"/>），
    /// 不拒、更不静默 —— 写目标本来就允许尚不存在。</para>
    /// </summary>
    public static string? Audit(string window, out string resolved, string? repoRoot = null, bool guardBase = true)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(window))
        {
            return "window 是空的：给一个**绝对路径**（目录或文件）";
        }

        var unexpandable = Refusal(window);
        if (unexpandable is not null)
        {
            return unexpandable;
        }

        var raw = Expand(window).Trim();
        var root = ResolveBase(repoRoot);
        var abs = Resolve(window, repoRoot);
        if (abs.Length == 0)
        {
            return $"window 解析不到：{raw} —— 基准是 {Show(root)}；请给**绝对路径**（例如 {Suggest(raw, root)}）";
        }

        // **相对路径**：归一成绝对后，要求「它本身存在，或**它所在的那一层**存在」。
        // 为什么不要求「必须存在」（2026-09-29 修正 · 当场 10 条既有测试连带红）：**写目标本来就允许尚不存在**
        // （新建文件是常态）—— 那样会把合法派发一起拒掉。而现场真正的病是**指到另一棵树**：
        // 那种情况下连它所在的那一层都不存在（真机：`handoff/36-…` 在 WB 侧连 `handoff/` 都没有）。
        // ⇒ 判据取「所在层」，正好把「新建到既有目录」与「指到不存在的树」两件事分开。
        if (!Path.IsPathRooted(raw) && !File.Exists(abs) && !Directory.Exists(abs))
        {
            var parent = Path.GetDirectoryName(abs);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
            {
                return $"window 是相对路径，而它连**所在的那一层**都不存在：{abs}（给的是 {raw}）—— "
                     + $"基准是 {Show(root)}；这多半是**指到了另一棵树**（两侧解析不同），请改给**绝对路径**";
            }
        }

        // **绝对路径也不再免检**（用户 2026-10-06 令 · 问题一）：它曾按「master 的显式断言 ⇒ 照收」处理，
        // 真机就栽在这里 —— master 用**自己的 cwd（活版）**把相对地址拼成绝对（`<live>/.stage1/acc/…`），
        // 指向一棵**不存在**的树，运行时照收 ⇒ worker 如实报「无可读文件」，白烧 2 次补发 + 零终局。
        // 判据与相对路径**同一把尺**：本身存在，或它**所在的那一层**存在（新建目标允许尚不存在）。
        if (Path.IsPathRooted(raw) && !File.Exists(abs) && !Directory.Exists(abs))
        {
            var absParent = Path.GetDirectoryName(abs);
            if (string.IsNullOrEmpty(absParent) || !Directory.Exists(absParent))
            {
                return $"window 指向一个**不存在的树**：{abs}（base = {Show(root)}）—— "
                     + "请给**存在的绝对路径**（两侧一律绝对；运行时**不猜基准**、也不代你归一）";
            }
        }

        // 跨树判定（用户 2026-09-29 18:50 令② · 任务 17 的第 ② 条）：**不在 base 下 ⇒ 派发前就拒**。
        // 理由：worker 的**全部相对路径**都按 base 解析 ⇒ 跨树的 window 会让「读得到、写不对」静默发生。
        // **2026-10-09 用户令「B」**：这条守卫**只对写侧（WRITE/EDIT）生效** —— 读类 / EXEC 的 window 只是读源 / 范围提示，
        // 跨树无害（真机：master 读 `/tmp` 里自己刚落的临时件、读父目录，都被这道守卫挡住、白烧轮次）。
        if (guardBase && OutsideBase(abs, root) is { } outside)
        {
            return outside;
        }

        resolved = abs;
        return null;
    }

    /// <summary>该绝对路径是否落在 base 之外（调用方用它落一条**注记** —— 放行不等于不提醒）。</summary>
    public static bool IsOutsideBase(string abs, string? repoRoot = null)
        => OutsideBase(abs, ResolveBase(repoRoot)) is not null;

    /// <summary>跨树判定：<paramref name="abs"/> 必须落在 <paramref name="root"/> 之下（base 为空 ⇒ 不判）。</summary>
    private static string? OutsideBase(string abs, string root)
    {
        if (root.Length == 0)
        {
            return null;
        }

        var b = Path.GetFullPath(root).TrimEnd('/');
        if (string.Equals(abs, b, StringComparison.Ordinal)
            || abs.StartsWith(b + "/", StringComparison.Ordinal))
        {
            return null;
        }

        return $"window 落在 worker 基准树之外：{abs} —— 基准是 {b}；worker 的**全部相对路径**都按基准解析，"
             + "跨树会让「读得到、写不对」静默发生。请改给基准树下的**绝对路径**；确要读那棵树 ⇒ 把 base 指到它";
    }

    /// <summary>绝对路径但目标当前不存在 ⇒ **响亮报**（不拒、不许静默带过去）。没问题 ⇒ <c>null</c>。</summary>
    public static string? MissingNote(string resolved) =>
        resolved.Length > 0 && !File.Exists(resolved) && !Directory.Exists(resolved)
            ? $"window 目标当前不存在：{resolved}（worker 会读不到；若它是**写目标**则无妨）"
            : null;

    /// <summary>建议一条可照抄的绝对路径（解不到时给人看的那个例子）。</summary>
    private static string Suggest(string raw, string root) =>
        root.Length == 0 ? raw : root.TrimEnd('/') + "/" + raw.TrimStart('.', '/');

    /// <summary>基准为空时别显出一个空串（判据文本要能读）。</summary>
    private static string Show(string s) => s.Length == 0 ? "（基准未定）" : s;

    /// <summary>
    /// **任务正文里的路径**（推广 · 2026-09-29）：正文里出现的、看起来是**具体位置**的相对路径 ——
    /// 只**报**、不改。
    /// <para>为什么不改：A2 的口径是「只改 <c>window:</c> 这类有名字段，**绝不对自由文本做正则改写**」；
    /// 所以这里只交回清单，由调用方进流（**不许静默带过去**），改不改、怎么改由 master 定。</para>
    /// <para>为什么不拒：正文里的引用常指向**别的树**（如 [OC] 侧 handoff），拒会变成大量假红。</para>
    /// </summary>
    public static IReadOnlyList<string> AuditTaskPaths(string? task, string? repoRoot = null)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return Array.Empty<string>();
        }

        var root = ResolveBase(repoRoot);
        var bad = new List<string>();
        foreach (var token in TaskPathTokens(task))
        {
            if (Path.IsPathRooted(token))
            {
                continue;
            }

            var abs = Resolve(token, repoRoot);
            if (abs.Length == 0 || (!File.Exists(abs) && !Directory.Exists(abs)))
            {
                bad.Add($"{token} ⇒ 解不到（建议 {Suggest(token, root)}）");
            }
        }

        return bad;
    }

    /// <summary>
    /// 从自由文本里挑「像具体位置」的 token：含 <c>/</c>、不含占位符 <c>&lt;&gt;</c>、
    /// 不是 URL、不是层标号（<c>L1/L2</c> 这种）。
    /// </summary>
    private static IEnumerable<string> TaskPathTokens(string task)
    {
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(task, @"[\w.\-]*/[\w./\-]+"))
        {
            var t = m.Value.TrimEnd('.', ',', ';', ')', '）', '。');
            if (t.Length < 3
                || t.Contains('<') || t.Contains('>')
                || t.Contains("://", StringComparison.Ordinal)
                || System.Text.RegularExpressions.Regex.IsMatch(t, @"^\w\d+(/\w\d+)+$"))
            {
                continue;
            }

            // **形状**：必须是「像路径」的东西。中文正文里的 `派发/worker`、`各节行范围/字符数`
            // 同样含 `/` ⇒ 此前会被报成「解不到的相对路径」（2026-09-29 真机两条假阳性，E045/E059）。
            // 判据取「末段带扩展名 / 显式目录（尾 `/`）/ 显式起点（`./` `../` `/`）」——
            // 都认不出就**不报**（宁可漏报，也不刷噪声：假阳性会把 master 支到错的改法上 —— 同一条纪律）。
            if (!LooksLikePath(t))
            {
                continue;
            }

            yield return t;
        }
    }

    /// <summary>像路径吗：末段带扩展名（≤8 位字母数字）/ 以 <c>/</c> 结尾（目录）/ 以 <c>./</c> <c>../</c> <c>/</c> 起头。</summary>
    private static bool LooksLikePath(string t)
    {
        if (t.StartsWith("./", StringComparison.Ordinal)
            || t.StartsWith("../", StringComparison.Ordinal)
            || t.StartsWith("/", StringComparison.Ordinal)
            || t.EndsWith("/", StringComparison.Ordinal))
        {
            return true;
        }

        var last = t[(t.LastIndexOf('/') + 1)..];
        var dot = last.LastIndexOf('.');
        if (dot <= 0 || dot == last.Length - 1)
        {
            return false;
        }

        var ext = last[(dot + 1)..];
        return ext.Length <= 8 && ext.All(char.IsLetterOrDigit);
    }
}
