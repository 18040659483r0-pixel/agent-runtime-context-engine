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

    protected override string[] AllowedArguments => ["path", "maxLines", "offset", "limit"];

    public override string Describe(ToolArgs args) => $"read {args.Canonical}";

    /// <summary>体检：必填 <c>path</c>（审批之前就要能确定「读哪一个」）。</summary>
    public override void Validate(ToolArgs args)
    {
        base.Validate(args);
        args.RequireString("path");
    }

    protected override async ValueTask<ToolOutcome> RunAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
    {
        var path = ToolPaths.Normalize(args.RequireString("path"));

        var requested = args.OptionalInt("limit") ?? args.OptionalInt("maxLines") ?? context.Limits.MaxReadLines;
        if (requested <= 0)
        {
            throw new ToolUsageException($"maxLines/limit 必须为正整数，实际 {requested}。");
        }

        var from = args.OptionalInt("offset") ?? 1;          // 从 1 起的行号（与 [OC] 同语义）
        if (from < 1)
        {
            throw new ToolUsageException($"offset 必须为从 1 起的行号（正整数），实际 {from}。");
        }

        var limit = Math.Min(requested, context.Limits.AbsoluteMaxReadLines);

        if (!File.Exists(path))
        {
            return ToolOutcome.Failure($"read {args.Canonical} → 失败：文件不存在（{path}）。");
        }

        var all = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        var window = all.Skip(from - 1).ToArray();
        var taken = Math.Min(window.Length, limit);
        var truncatedByLines = window.Length > taken;

        var body = string.Join('\n', window.Take(taken));
        var (clamped, truncatedByChars) = Clamp(body, context.Limits.MaxOutputChars);

        var next = from + taken;                              // 截断时把**下一段起点**写进结果（自解释；不改前缀）
        var tail = truncatedByLines ? $"（截断：上限 {limit} 行 ⇒ 用 offset={next} 接着读）" : string.Empty;
        var summary = from == 1
            ? (truncatedByLines ? $"{taken}/{all.Length} 行{tail}" : $"{all.Length} 行")
            : (taken == 0
                ? $"第 {from} 行起到文件末尾为空（共 {all.Length} 行）"
                : $"第 {from}~{from + taken - 1} 行 / 共 {all.Length} 行{tail}");

        var text = $"read {args.Canonical} → {summary}\n{clamped}";
        return ToolOutcome.Success(text, truncatedByLines || truncatedByChars);
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

        if (!Directory.Exists(path))
        {
            return ValueTask.FromResult(ToolOutcome.Failure($"list {args.Canonical} → 失败：目录不存在（{path}）。"));
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

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);

        return ToolOutcome.Success($"write {args.Canonical} → 已写入 {System.Text.Encoding.UTF8.GetByteCount(content)} 字节（{path}）。");
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
            return ToolOutcome.Failure($"edit {args.Canonical} → 失败：文件不存在（{path}）。");
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
        await File.WriteAllTextAsync(path, updated, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);

        return ToolOutcome.Success(
            $"edit {args.Canonical} → 已替换 1 处（{oldText.Length} 字符 → {newText.Length} 字符；{path}）。");
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
