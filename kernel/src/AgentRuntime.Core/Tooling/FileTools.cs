using System.Text.RegularExpressions;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// <c>read</c> —— 读文本文件（**带行数上限 + 定点读**；只读 ⇒ 免批）。
/// <para>
/// 路径**不限根目录**（工具面不做围栏，S1）：任何写法都先经 <see cref="ToolPaths.Normalize"/> 变成真身。
/// 上限有两层：模型可以在 <c>maxLines</c> / <c>limit</c> 里要一个数，但**不能越过硬上限**
/// （<see cref="ToolLimits.AbsoluteMaxReadLines"/>）—— 「模型说要读 50 万行」不该由模型说了算。
/// 超限**照读，但明说截断**（不静默截：静默截会让模型以为文件就到这儿了），并给出**下一段的起点**。
/// </para>
/// <para>
/// <b>参数名</b>：<c>limit</c> 与 [OC] 侧 <c>read</c> **同名同语义**（R1：操作步骤数不增加）；
/// <c>maxLines</c> 是旧名，**保留**（协议区里已声明，不许拿工具改名去甩掉它）。两个都给时 <c>limit</c> 优先。
/// </para>
/// <para>
/// <b>为什么有 <c>offset</c>（2026-09-16 G7-D1 实测缺口）</b>：原来只有「从第 1 行起、最多 N 行」——
/// 超过 N 行的文件**后段根本读不到**（模型只能另想办法，实测它去试了未声明的参数、被 fail-closed 拒）。
/// 现补 <c>offset</c> = **从第几行开始（从 1 起）**，与 [OC] 侧同名。
/// </para>
/// </summary>
public sealed class ReadTool : ToolBase
{
    public override string Name => ToolNames.Read;

    protected override string[] AllowedArguments => ["path", "symbol", "maxLines", "offset", "limit"];

    public override string Describe(ToolArgs args) => $"read {args.Canonical}";

    /// <summary>体检：必填 <c>path</c>（审批之前就要能确定「读哪一个」）；<c>symbol</c> 与行窗参数互斥。</summary>
    public override void Validate(ToolArgs args)
    {
        base.Validate(args);
        args.RequireString("path");

        // v19：`symbol`（按名字取整段）与 `offset`/`limit`/`maxLines`（按行窗取一段）是**两种读法**，
        // 同时给就说不清要哪一个 ⇒ 当场拒（fail-closed：宁可让它重发一次，也不猜它想要哪个）。
        if (!string.IsNullOrWhiteSpace(args.OptionalString("symbol"))
            && (args.OptionalInt("offset") is not null
                || args.OptionalInt("limit") is not null
                || args.OptionalInt("maxLines") is not null))
        {
            throw new ToolUsageException(
                "symbol 与 offset/limit/maxLines 不能同时给 —— 要说清是「按名字取整段」（symbol）还是「按行窗取一段」（offset+limit），二选一。");
        }
    }

    /// <summary>
    /// **按符号定位那一整段**（v19 的实质）—— 词边界匹配 + 花括号配平。
    /// <para>
    /// 为什么要它（2026-09-22 实测）：同一道题六跑，<c>SessionLifecycle.cs</c>（582 行）被
    /// <c>read</c> <b>3~9 次</b>，每次一个小窗口顺着往下滑 —— 而每次读都是**一轮完整上下文重发**。
    /// 上限调小（1,000）把页数翻倍、token **+50%**；调大（4,000）体量 +43%、token +27%；
    /// 在结果里附「已读图 / 未读段 / 一次要齐」又**全部更差**（附待办清单 ⇒ 它去读满）。
    /// ⇒ 三个方向都证明：**病不在窗口，而在「要几次才够」** —— 给一个能**一次拿到整段定义**的读法。
    /// </para>
    /// <para>
    /// 启发式，**说清楚了才算诚实**：从**首个匹配行**起找 <c>{</c>（本行往后 3 行内），再花括号配平到收尾。
    /// 字符串 / 注释里的花括号会干扰（C# 的 <c>"{"</c>）⇒ 配不平就**截到文件末并明说**，不编边界。
    /// </para>
    /// </summary>
    /// <returns>起止行（0 基）、匹配处数、以及一句「边界怎么来的 / 哪里不保准」。</returns>
    private static (int Start, int End, int Hits, string? Why) LocateBySymbol(IReadOnlyList<string> lines, string symbol)
    {
        var rx = new Regex(
            $@"(?<![A-Za-z0-9_]){Regex.Escape(symbol)}(?![A-Za-z0-9_])",
            RegexOptions.CultureInvariant);

        var hits = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (rx.IsMatch(lines[i]))
            {
                hits.Add(i);
            }
        }

        if (hits.Count == 0)
        {
            return (0, 0, 0, null);
        }

        var start = hits[0];

        // 找块头：本行或往后 3 行内的第一个 `{`（属性/签名跨行是常事）。
        var open = -1;
        for (var i = start; i < Math.Min(start + 4, lines.Count) && open < 0; i++)
        {
            if (lines[i].Contains('{', StringComparison.Ordinal))
            {
                open = i;
            }
        }

        if (open < 0)
        {
            return (start, start, hits.Count, "这词后面没有 `{`（不是块状定义）⇒ 只给这一行");
        }

        var depth = 0;
        for (var i = open; i < lines.Count; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return (start, i, hits.Count, null);
                    }
                }
            }
        }

        return (start, lines.Count - 1, hits.Count, "花括号没配平（字符串/注释里的 `{` 会干扰）⇒ 边界截到文件末，保准的部分只有前几行");
    }

    protected override async ValueTask<ToolOutcome> RunAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        var path = ToolPaths.Normalize(args.RequireString("path"));

        var symbol = args.OptionalString("symbol")?.Trim();

        if (Directory.Exists(path))
        {
            // 2026-09-22 实测：模型把**目录**当文件读（`read "…/projects/AgentRuntime"`），
            // 旧话术回「文件不存在」⇒ 它以为路径错了、又猜了 5 轮路径。**错要说得具体**。
            return ToolOutcome.Failure($"read {args.Canonical} → 失败：那是个**目录**（用 list 列它）：{path}。");
        }

        if (!File.Exists(path))
        {
            return ToolOutcome.Failure(
                $"read {args.Canonical} → 失败：文件不存在（{path}）；相对路径的**基准目录** = {Environment.CurrentDirectory}（要往外走就用 ../…）。");
        }

        var all = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);

        // 两种读法（二选一，冲突已在 Validate 里拒）：① v19 按符号取整段；② 按行窗取一段。
        int from;
        int limit;
        string? head = null;                                 // 摘要前缀（符号定位时用它说出「取的是哪一段」）
        if (!string.IsNullOrEmpty(symbol))
        {
            var (start, end, hits, why) = LocateBySymbol(all, symbol);
            if (hits == 0)
            {
                return ToolOutcome.Failure(
                    $"read {args.Canonical} → 失败：本文件里没有 `{symbol}` 这个词（共 {all.Length} 行）；"
                    + "先定位（exec 的 grep -n）再读，或直接给 offset。");
            }

            from = start + 1;
            limit = end - start + 1;
            head = $"符号「{symbol}」⇒ 第 {from}~{end + 1} 行 / 共 {all.Length} 行"
                 + (hits > 1 ? $"（本文件共 {hits} 处匹配，这里给**第一处**；要别处就给 offset=那一处行号）" : "（唯一匹配）")
                 + (why is null ? string.Empty : $"；⚠️ {why}");
        }
        else
        {
            var requested = args.OptionalInt("limit") ?? args.OptionalInt("maxLines") ?? context.Limits.MaxReadLines;
            if (requested <= 0)
            {
                throw new ToolUsageException($"maxLines/limit 必须为正整数，实际 {requested}。");
            }

            from = args.OptionalInt("offset") ?? 1;          // 从 1 起的行号（与 [OC] 同语义）
            if (from < 1)
            {
                throw new ToolUsageException($"offset 必须为从 1 起的行号（正整数），实际 {from}。");
            }

            limit = Math.Min(requested, context.Limits.AbsoluteMaxReadLines);
        }

        limit = Math.Min(limit, context.Limits.AbsoluteMaxReadLines);   // 两种读法都不能越过硬上限

        // 逐行装到**字符上限**为止：截断点必须与给出去的续读指针**一致**。
        // 旧写法先取行窗、再用字符窗砍正文 ⇒ 头部写「400/632 行」而实际可见量少得多
        // （PITFALLS「两层截断别拿一层当另一层」的坑：模型按头部行号下结论会漏内容）。
        var window = all.Skip(from - 1).ToArray();
        var taken = Math.Min(window.Length, limit);
        var cap = context.Limits.MaxOutputChars;

        var fitted = 0;
        var used = 0;
        while (fitted < taken)
        {
            var need = window[fitted].Length + (fitted == 0 ? 0 : 1);
            if (used + need > cap)
            {
                break;
            }

            used += need;
            fitted++;
        }

        var more = fitted < window.Length;                   // 还有没给出去的
        var byChars = fitted < taken;                        // 是字符窗先到（而不是行窗）
        var body = string.Join('\n', window.Take(fitted));
        var next = from + fitted;                            // 截断时把**下一段起点**写进结果（自解释；不改前缀）
        var tail = more
            ? $"（截断：{(byChars ? $"字符上限 {cap}" : $"行数上限 {limit}")} ⇒ 用 offset={next} 接着读；全文在 /trace）"
            : string.Empty;
        var summary = head is not null
            ? $"{head}{(fitted == 0 ? $"；⚠️ 这一段开头就放不下（单条结果上限 {cap} 字符）" : string.Empty)}{tail}"
            : (from == 1
                ? (more ? $"{fitted}/{all.Length} 行{tail}" : $"{all.Length} 行")
                : (window.Length == 0
                    ? $"第 {from} 行起到文件末尾为空（共 {all.Length} 行）"
                    : fitted == 0
                        ? $"第 {from} 行起放不下（单条结果上限 {cap} 字符）—— 把窗口改小或提高 offset（共 {all.Length} 行）"
                        : $"第 {from}~{from + fitted - 1} 行 / 共 {all.Length} 行{tail}"));

        var text = $"read {args.Canonical} → {summary}\n{body}";
        return ToolOutcome.Success(text, more);
    }
}

/// <summary>
/// <c>list</c> —— 列目录（只读 ⇒ 免批）。目录带尾斜杠，排序稳定（可复算）。
/// </summary>
public sealed class ListTool : ToolBase
{
    public override string Name => ToolNames.List;

    protected override string[] AllowedArguments => ["path"];

    public override string Describe(ToolArgs args) => $"list {args.Canonical}";

    protected override ValueTask<ToolOutcome> RunAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        var path = ToolPaths.Normalize(args.OptionalString("path") ?? ".");

        if (File.Exists(path))
        {
            return ValueTask.FromResult(ToolOutcome.Failure($"list {args.Canonical} → 失败：那是个**文件**（用 read 读它）：{path}。"));
        }

        if (!Directory.Exists(path))
        {
            return ValueTask.FromResult(ToolOutcome.Failure(
                $"list {args.Canonical} → 失败：目录不存在（{path}）；相对路径的**基准目录** = {Environment.CurrentDirectory}（要往外走就用 ../…）。"));
        }

        var entries = Directory.EnumerateFileSystemEntries(path)
            .Select(e => Directory.Exists(e) ? Path.GetFileName(e) + "/" : Path.GetFileName(e))
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToArray();

        var truncated = entries.Length > context.Limits.MaxListEntries;
        var body = string.Join('\n', entries.Take(context.Limits.MaxListEntries));
        var (clamped, truncatedByChars) = Clamp(body, context.Limits.MaxOutputChars);

        var summary = truncated
            ? $"{context.Limits.MaxListEntries}/{entries.Length} 项（截断：上限 {context.Limits.MaxListEntries} 项）"
            : $"{entries.Length} 项";

        var text = $"list {args.Canonical} → {summary}\n{clamped}";
        return ValueTask.FromResult(ToolOutcome.Success(text, truncated || truncatedByChars));
    }
}

/// <summary>
/// <c>write</c> —— 写文件（**需批**）。父目录不存在则创建（`Directory.CreateDirectory` 是幂等的）。
/// <para>
/// 覆盖已有文件是**破坏性**的，所以它落在 <see cref="ToolRisk.Mutating"/>；一次点头只覆盖这一次，
/// 且审批面必须给出 **diff + "将覆盖" 标注**（S2）。
/// </para>
/// </summary>
public sealed class WriteTool : ToolBase
{
    public override string Name => ToolNames.Write;

    protected override string[] AllowedArguments => ["path", "content"];

    public override string Describe(ToolArgs args) =>
        $"write {args.Canonical}";

    /// <summary>体检：必填 <c>path</c> 与 <c>content</c>（content 允许为空串 —— 那是「写空文件」，语义无歧义）。</summary>
    public override void Validate(ToolArgs args)
    {
        base.Validate(args);
        args.RequireString("path");
        args.RequireString("content");
    }

    /// <summary>审批面：新文件 ⇒ 全文；已存在 ⇒ diff（+ "将覆盖" 标注）。</summary>
    public override ApprovalFace Preview(ToolArgs args, ToolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(limits);

        var path = ToolPaths.Normalize(args.RequireString("path"));
        var content = args.RequireString("content");

        return File.Exists(path)
            ? ApprovalFaces.Overwrite(Name, Risk, args, limits, path, File.ReadAllText(path), content)
            : ApprovalFaces.NewFile(Name, Risk, args, limits, path, content);
    }

    protected override async ValueTask<ToolOutcome> RunAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        var path = ToolPaths.Normalize(args.RequireString("path"));
        var content = args.RequireString("content");

        // 2026-09-23：写「一个目录」过去会直接抛 UnauthorizedAccessException（难读）⇒ 明说 + 点明基准目录。
        if (Directory.Exists(path))
        {
            return ToolOutcome.Failure(
                $"write {args.Canonical} → 失败：那是个**目录**（要写文件就得给文件名）：{path}；"
                + $"相对路径的**基准目录** = {Environment.CurrentDirectory}（要往外走就用 ../…）。");
        }

        var existed = File.Exists(path);
        var before = existed ? FileEvidence.Sha256(path) : null;
        var hadBytes = existed ? new FileInfo(path).Length : 0;

        var directory = Path.GetDirectoryName(path);
        var madeDirectory = false;
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            madeDirectory = true;
        }

        await File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);

        // 结果**先说落点与体量**，参数回显放后面：写大正文时回显会把结论淹掉，
        // 一眼看不到「写到哪儿、变了没有」⇒ 2026-09-23 那次「写入像成功了、其实没进仓库」就是这么来的。
        var bytes = System.Text.Encoding.UTF8.GetByteCount(content);
        var after = FileEvidence.Sha256(path);
        var verdict = !existed ? "新建"
            : before == after ? "覆盖（与写入前逐字节相同）"
            : $"覆盖（{hadBytes} → {new FileInfo(path).Length} 字节）";

        var text = $"write → 落点 {path} · {bytes} 字节 · {verdict} · sha {FileEvidence.Short(before)}→{FileEvidence.Short(after)}"
                 + $"\n（参数回显：{args.Canonical}）";

        if (madeDirectory)
        {
            text += $"\n⚠️ 落点的上级目录是本次**新建**的：{directory}"
                  + $"（相对路径是按**工具 cwd** = {Environment.CurrentDirectory} 拼出来的 —— 先确认这就是你要的位置，坑 #135）";
        }

        if (FileEvidence.IsTempArea(path))
        {
            text += $"\n⚠️ 落点在**系统临时区**（{FileEvidence.TempRootOf(path)}）：正文 / 知识**不该写这里**（只放中间产物）；"
                  + "要进仓库请用绝对路径写进项目根 / 活版 / 存档区。";
        }

        return ToolOutcome.Success(text);
    }
}

/// <summary>
/// <c>edit</c> —— 精确替换（**需批**）。
/// <para>
/// 与「主人在自己工作区里改文件」同一条纪律：<b>必须唯一匹配</b> ——
/// 匹配 0 次 ⇒ 失败（没改），2 次以上 ⇒ **拒绝**（判不出该改哪一处；改错一处比不改更糟）。
/// 审批面给的是**精确的 oldText → newText 块**（S2）。
/// </para>
/// </summary>
public sealed class EditTool : ToolBase
{
    public override string Name => ToolNames.Edit;

    protected override string[] AllowedArguments => ["path", "oldText", "newText"];

    public override string Describe(ToolArgs args) => $"edit {args.Canonical}";

    /// <summary>体检：三个字段必填（审批之前就要能确定「改哪个文件的哪一段」）。</summary>
    public override void Validate(ToolArgs args)
    {
        base.Validate(args);
        args.RequireString("path");
        args.RequireString("oldText");
        args.RequireString("newText");
    }

    /// <summary>审批面：精确的 oldText → newText 块 + 现有行数/字节。</summary>
    public override ApprovalFace Preview(ToolArgs args, ToolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(limits);

        var path = ToolPaths.Normalize(args.RequireString("path"));
        var oldText = args.RequireString("oldText");
        var newText = args.RequireString("newText");

        if (!File.Exists(path))
        {
            return ApprovalFaces.Edit(Name, Risk, args, limits, path, oldText, newText, 0, false, 0, 0);
        }

        var content = File.ReadAllText(path);
        var info = new FileInfo(path);

        return ApprovalFaces.Edit(
            Name, Risk, args, limits, path, oldText, newText,
            CountOccurrences(content, oldText), true, info.Length, LineDiff.LineCount(content));
    }

    protected override async ValueTask<ToolOutcome> RunAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        var path = ToolPaths.Normalize(args.RequireString("path"));
        var oldText = args.RequireString("oldText");
        var newText = args.RequireString("newText");

        if (oldText.Length == 0)
        {
            throw new ToolUsageException("edit 的 oldText 不能为空（空串会在任意位置匹配 ⇒ 判不出替换哪里）⇒ 拒绝。");
        }

        if (!File.Exists(path))
        {
            return ToolOutcome.Failure(
                $"edit {args.Canonical} → 失败：文件不存在（{path}）；"
                + $"相对路径的**基准目录** = {Environment.CurrentDirectory}（要往外走就用 ../…）。");
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var occurrences = CountOccurrences(content, oldText);

        if (occurrences == 0)
        {
            return ToolOutcome.Failure($"edit {args.Canonical} → 失败：oldText 在文件里找不到（一处都没匹配），未改动。");
        }

        if (occurrences > 1)
        {
            throw new ToolUsageException(
                $"edit 的 oldText 在文件里出现了 {occurrences} 次（不唯一）⇒ 判不出改哪一处 ⇒ 拒绝（请给更长的唯一片段）。");
        }

        var updated = content.Replace(oldText, newText, StringComparison.Ordinal);
        var before = FileEvidence.Sha256(path);
        await File.WriteAllTextAsync(path, updated, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
        var after = FileEvidence.Sha256(path);

        var text = $"edit → 落点 {path} · 替换 1 处（{oldText.Length} 字符 → {newText.Length} 字符）"
                 + $" · sha {FileEvidence.Short(before)}→{FileEvidence.Short(after)}"
                 + $"\n（参数回显：{args.Canonical}）";
        if (FileEvidence.IsTempArea(path))
        {
            text += $"\n⚠️ 落点在**系统临时区**（{FileEvidence.TempRootOf(path)}）：正文 / 知识**不该写这里**。";
        }

        return ToolOutcome.Success(text);
    }

    private static int CountOccurrences(string content, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
