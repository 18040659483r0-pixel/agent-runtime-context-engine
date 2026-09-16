using System.Text;

namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **审批面**（S2 / S3）—— 一次审批请求的**人看的那一页**。
/// <para>
/// <b>S3（谁渲染）</b>：审批面**只能由 Runtime 自己渲染** —— 本类型由工具/执行器（<see cref="ToolRunner"/>）
/// 从**结构化参数**构造，<b>绝不复用模型输出</b>。理由：模型只要能在回复里写一段"审批提示"，
/// 就能骗人点头；所以审批面与模型文本必须**物理分离**（模型文本进不了本类型的任何一个字段）。
/// </para>
/// <para>
/// <b>S2（要有哪些信息）</b>：① 写哪儿 = <see cref="AbsolutePath"/>（规范化绝对路径，真身）+
/// <see cref="TargetExists"/> + 现有行数/字节；② 写什么 = <see cref="Body"/>（新文件 ⇒ 全文；
/// 已存在 ⇒ diff；<c>edit</c> ⇒ 精确的 oldText → newText 块）；③ 不可逆 = 覆盖时 <see cref="Status"/> 显著标注。
/// 超出预算 ⇒ <see cref="Truncated"/> + <see cref="FullContentPath"/>（**不静默截**）。
/// </para>
/// </summary>
public sealed record ApprovalFace
{
    /// <summary>工具名。</summary>
    public required string Tool { get; init; }

    /// <summary>分级（面板第一行要写清"为什么问你"）。</summary>
    public required ToolRisk Risk { get; init; }

    /// <summary>一行动作摘要（<c>write /abs/path</c>）。</summary>
    public required string Headline { get; init; }

    /// <summary>「状态」行（存在性 / 是否覆盖 / 是否会失败）。</summary>
    public required string Status { get; init; }

    /// <summary>规范化绝对路径（真身）；没有路径参数的工具 = null。</summary>
    public string? AbsolutePath { get; init; }

    /// <summary>目标是否已存在（存在 ⇒ 覆盖/修改；不存在 ⇒ 新建）。</summary>
    public bool TargetExists { get; init; }

    /// <summary>现有字节数（存在时给，否则 null）。</summary>
    public long? ExistingBytes { get; init; }

    /// <summary>现有行数（存在时给，否则 null）。</summary>
    public int? ExistingLines { get; init; }

    /// <summary>附加说明行（差异计数 / 替换处数 / 粗粒度提示……）。</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>正文标题（说明下面这一段是什么）。</summary>
    public string BodyCaption { get; init; } = string.Empty;

    /// <summary>正文（全文 / diff / oldText→newText；已按上限截断）。</summary>
    public IReadOnlyList<ApprovalLine> Body { get; init; } = [];

    /// <summary>正文是否被截断（**截断了必须明说** + 给落点）。</summary>
    public bool Truncated { get; init; }

    /// <summary>被截断时，完整内容的落点（Runtime 自己写的文件；不进 prompt）。</summary>
    public string? FullContentPath { get; init; }

    /// <summary>渲染整个审批面（宿主把它原样打到 stderr / TUI 面板）。</summary>
    public IReadOnlyList<string> Render()
    {
        var lines = new List<string>
        {
            $"{ApprovalFaces.Prefix} ⚠️ 需要人工点头（{ToolNames.Describe(Risk)}；一次一批：本次点头只覆盖这一个动作；模型不能自批）",
            $"{ApprovalFaces.Prefix} 工具：{Tool} · {ToolNames.Describe(Risk)}",
            $"{ApprovalFaces.Prefix} 动作：{Headline}",
        };

        if (AbsolutePath is not null)
        {
            lines.Add($"{ApprovalFaces.Prefix} 目标：{AbsolutePath}（规范化绝对路径：真身）");
        }

        lines.Add($"{ApprovalFaces.Prefix} 状态：{Status}");

        foreach (var note in Notes)
        {
            lines.Add($"{ApprovalFaces.Prefix} {note}");
        }

        if (Body.Count > 0)
        {
            lines.Add($"{ApprovalFaces.Prefix} ── {BodyCaption} ──");
            lines.AddRange(Body.Select(line => $"{ApprovalFaces.Prefix}{line.Render()}"));
            lines.Add($"{ApprovalFaces.Prefix} ── 正文结束（审批面如上）──");
        }

        if (Truncated && FullContentPath is not null)
        {
            lines.Add($"{ApprovalFaces.Prefix} ⚠️ 完整内容落点：{FullContentPath}");
        }

        return lines;
    }
}

/// <summary>
/// **审批面构造器**（S2/S3 的唯一实现处）。
/// <para>
/// 只在这里把「工具 + 参数 + 目标现状」变成人看的那一页；工具自己只说"我是谁、参数是什么"。
/// 因此"审批面里有什么"是**可复算的纯函数 + 一次确定的落盘**，不依赖任何模型输出。
/// </para>
/// </summary>
public static class ApprovalFaces
{
    /// <summary>审批面的行前缀（宿主与测试都按它认行）。</summary>
    public const string Prefix = "[审批]";

    /// <summary>默认审批面（无专属实现的工具 / 未登记工具）：只把参数原文摆出来（截断明说）。</summary>
    public static ApprovalFace Inspect(string tool, ToolRisk risk, ToolArgs args, ToolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(limits);

        var inspection = InspectTarget(args);
        var body = LineDiff
            .SplitLines(args.Canonical)
            .Select(line => new ApprovalLine('*', line))
            .ToArray();

        var status = inspection.Path is null
            ? "无路径参数（该工具的动作原文见下）"
            : inspection.Exists
                ? $"目标已存在（现有 {inspection.Lines} 行 / {inspection.Bytes} 字节）"
                : "目标当前不存在";

        return Compose(
            tool, risk, args, limits,
            headline: inspection.Path is null ? $"{tool} {args.Canonical}" : $"{tool} {inspection.Path}",
            path: inspection.Path,
            status: status,
            notes: [],
            caption: "动作原文（该工具没有专属审批面）",
            body: body,
            existing: inspection);
    }

    /// <summary>新建文件 ⇒ **给全文**（S2 的第二要素）。</summary>
    public static ApprovalFace NewFile(
        string tool, ToolRisk risk, ToolArgs args, ToolLimits limits, string path, string content)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(content);

        var body = LineDiff.SplitLines(content).Select(line => new ApprovalLine('+', line)).ToArray();
        var bytes = Encoding.UTF8.GetByteCount(content);

        return Compose(
            tool, risk, args, limits,
            headline: $"{tool} {path}",
            path: path,
            status: "目标当前不存在 ⇒ 将**新建**文件",
            notes: [$"内容：全文 {body.Length} 行 / {bytes} 字节"],
            caption: "完整内容（新文件 ⇒ 全文）",
            body: body,
            existing: new TargetInspection(path, false, 0, 0),
            fullText: content);
    }

    /// <summary>覆盖已存在文件 ⇒ **给 diff + 显著标注"将覆盖"**（S2 的第三要素：不可逆性）。</summary>
    public static ApprovalFace Overwrite(
        string tool, ToolRisk risk, ToolArgs args, ToolLimits limits, string path, string oldText, string newText)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(oldText);
        ArgumentNullException.ThrowIfNull(newText);

        var bytes = Encoding.UTF8.GetByteCount(oldText);
        var oldLines = LineDiff.LineCount(oldText);
        var newLines = LineDiff.LineCount(newText);

        if (bytes > limits.MaxDiffBytes)
        {
            // 过大：**不读不 diff**，只给大小 + 粗粒度行数（明说"未逐行计算"）。
            var head = LineDiff.SplitLines(newText)
                .Take(limits.MaxApprovalLines)
                .Select(line => new ApprovalLine('+', line))
                .ToArray();

            return Compose(
                tool, risk, args, limits,
                headline: $"{tool} {path}",
                path: path,
                status: $"⚠️ 将**覆盖已存在文件**（现有 {oldLines} 行 / {bytes} 字节）",
                notes:
                [
                    $"差异：⚠️ 文件过大（{bytes} 字节 > {limits.MaxDiffBytes}）⇒ **未逐行计算**；" +
                    $"中段整块替换（粗粒度上界：- {oldLines} 行 / + {newLines} 行）",
                ],
                caption: "即将写入的内容（前若干行；完整内容见落点）",
                body: head,
                existing: new TargetInspection(path, true, oldLines, bytes),
                fullText: newText);
        }

        var diff = LineDiff.Compute(oldText, newText, limits.MaxDiffCells, contextLines: 3);
        var alignment = diff.Exact ? "逐行对齐（精确）" : "⚠️ 内容过大，未逐行对齐（粗粒度上界）";

        return Compose(
            tool, risk, args, limits,
            headline: $"{tool} {path}",
            path: path,
            status: $"⚠️ 将**覆盖已存在文件**（现有 {oldLines} 行 / {bytes} 字节）",
            notes: [$"差异：+{diff.Added} 行 / -{diff.Removed} 行（{oldLines} 行 → {newLines} 行；{alignment}）"],
            caption: "差异（- 现有 / + 即将写入 /   未变）",
            body: [.. diff.Script],
            existing: new TargetInspection(path, true, oldLines, bytes),
            fullText: newText,
            fullScript: diff.Script);
    }

    /// <summary><c>edit</c> ⇒ **精确的 oldText → newText 块**（S2）。</summary>
    public static ApprovalFace Edit(
        string tool, ToolRisk risk, ToolArgs args, ToolLimits limits, string path,
        string oldText, string newText, int occurrences, bool exists, long bytes, int lines)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var body = new List<ApprovalLine>();
        foreach (var line in LineDiff.SplitLines(oldText))
        {
            body.Add(new ApprovalLine('-', line));
        }

        body.Add(new ApprovalLine('*', "── 以上是 oldText（将被删除）／以下是 newText（将被写入）──"));
        foreach (var line in LineDiff.SplitLines(newText))
        {
            body.Add(new ApprovalLine('+', line));
        }

        var status = exists
            ? $"⚠️ 将**修改已存在文件**（现有 {lines} 行 / {bytes} 字节）"
            : "目标当前不存在 ⇒ 该动作会失败（不会改动任何文件）";

        var notes = exists
            ? new List<string>
            {
                $"替换：oldText 唯一匹配 {occurrences} 处；{LineDiff.LineCount(oldText)} 行 / {LineDiff.LineCount(newText)} 行",
            }
            : [$"替换：oldText {LineDiff.LineCount(oldText)} 行 / newText {LineDiff.LineCount(newText)} 行"];

        return Compose(
            tool, risk, args, limits,
            headline: $"{tool} {path}",
            path: path,
            status: status,
            notes: notes,
            caption: "oldText → newText（精确替换块）",
            body: body,
            existing: new TargetInspection(path, exists, lines, bytes),
            fullText: newText,
            fullScript: body);
    }

    /// <summary>目标不存在（<c>write</c> 之外的动作会遇到）：照原样说清"会失败"，仍然按流程问人。</summary>
    public static ApprovalFace Missing(
        string tool, ToolRisk risk, ToolArgs args, ToolLimits limits, string path)
    {
        ArgumentNullException.ThrowIfNull(limits);

        return Compose(
            tool, risk, args, limits,
            headline: $"{tool} {path}",
            path: path,
            status: "⚠️ 目标当前不存在 ⇒ 该动作会失败（不会改动任何文件）",
            notes: [],
            caption: "动作原文",
            body: LineDiff.SplitLines(args.Canonical).Select(line => new ApprovalLine('*', line)).ToArray(),
            existing: new TargetInspection(path, false, 0, 0));
    }

    /// <summary>读一个目标的现状（<c>path</c> 字段缺省 ⇒ 什么都没读）。</summary>
    public static TargetInspection InspectTarget(ToolArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var raw = args.OptionalString("path");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new TargetInspection(null, false, 0, 0);
        }

        var path = ToolPaths.Normalize(raw);

        if (Directory.Exists(path))
        {
            return new TargetInspection(path, true, 0, 0);
        }

        if (!File.Exists(path))
        {
            return new TargetInspection(path, false, 0, 0);
        }

        var info = new FileInfo(path);
        return new TargetInspection(path, true, LineDiff.LineCount(File.ReadAllText(path)), info.Length);
    }

    /// <summary>目标的现状（规范化路径 + 存在性 + 行数/字节；目录不记行数）。</summary>
    /// <param name="Path">规范化绝对路径（真身）；无 <c>path</c> 字段 = null。</param>
    /// <param name="Exists">是否已存在。</param>
    /// <param name="Lines">现有行数。</param>
    /// <param name="Bytes">现有字节数。</param>
    public sealed record TargetInspection(string? Path, bool Exists, int Lines, long Bytes);

    /// <summary>
    /// <c>exec</c> ⇒ **完整命令原文 + 工作目录 + 超时**（S2：人要看见真要跑的那条命令，不是摘要）。
    /// <para>命令不做 diff（它没有"目标文件"）；正文就是**逐字的命令**。</para>
    /// <para><paramref name="extraNotes"/> / <paramref name="extraBody"/>：must-ask 档的附加面
    /// （如展开提交信息全文）——经过同一条**截断口径**（截断必明说），**不是拼接后绕过预算**。</para>
    /// </summary>
    public static ApprovalFace Command(
        string tool, ToolRisk risk, ToolArgs args, ToolLimits limits,
        string command, string workingDirectory, int timeoutSeconds,
        IReadOnlyList<string>? extraNotes = null,
        IReadOnlyList<ApprovalLine>? extraBody = null,
        string? extraFullText = null)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var body = LineDiff.SplitLines(command)
            .Select(line => new ApprovalLine('*', line))
            .ToList();
        if (extraBody is { Count: > 0 })
        {
            body.AddRange(extraBody);
        }

        var notes = new List<string>
        {
            $"命令：{LineDiff.LineCount(command)} 行 / {Encoding.UTF8.GetByteCount(command)} 字节",
            "注意：命令能做的不止改文件（联网 / 起进程 / 改系统状态）——**请逐字读完再点头**。",
        };
        if (extraNotes is { Count: > 0 })
        {
            notes.AddRange(extraNotes);
        }

        var fullText = extraFullText is null ? command : command + extraFullText;

        return Compose(
            tool, risk, args, limits,
            headline: $"{tool} {command}",
            path: null,
            status: $"⚠️ 将在**本机**执行（工作目录 {workingDirectory}；超时 {timeoutSeconds}s）",
            notes: notes,
            caption: extraBody is { Count: > 0 } ? "完整命令原文（逐字）+ must-ask 附加面" : "完整命令原文（逐字）",
            body: body,
            existing: new TargetInspection(null, true, 0, 0),
            fullText: fullText);
    }

    private static ApprovalFace Compose(
        string tool,
        ToolRisk risk,
        ToolArgs args,
        ToolLimits limits,
        string headline,
        string? path,
        string status,
        IReadOnlyList<string> notes,
        string caption,
        IReadOnlyList<ApprovalLine> body,
        TargetInspection existing,
        string? fullText = null,
        IReadOnlyList<ApprovalLine>? fullScript = null)
    {
        var truncated = body.Count > limits.MaxApprovalLines;
        var shown = truncated ? body.Take(limits.MaxApprovalLines).ToArray() : body;
        var all = new List<string>(notes);

        string? landing = null;
        if (truncated)
        {
            all.Add($"⚠️ 审批面已截断：仅显示前 {limits.MaxApprovalLines} 行（共 {body.Count} 行）");
            landing = WriteLanding(tool, args, limits, path, caption, fullText, fullScript ?? body);
        }

        if (!existing.Exists && path is not null)
        {
            return new ApprovalFace
            {
                Tool = tool,
                Risk = risk,
                Headline = headline,
                Status = status,
                AbsolutePath = path,
                TargetExists = false,
                ExistingBytes = null,
                ExistingLines = null,
                Notes = all,
                BodyCaption = caption,
                Body = shown,
                Truncated = truncated,
                FullContentPath = landing,
            };
        }

        return new ApprovalFace
        {
            Tool = tool,
            Risk = risk,
            Headline = headline,
            Status = status,
            AbsolutePath = path,
            TargetExists = true,
            ExistingBytes = existing.Bytes,
            ExistingLines = existing.Lines,
            Notes = all,
            BodyCaption = caption,
            Body = shown,
            Truncated = truncated,
            FullContentPath = landing,
        };
    }

    /// <summary>
    /// 把**完整内容 / 完整差异**写到落点（S2：截断了就要给"去哪儿看全文"）。
    /// <para>只写我们自己的一页（<see cref="ToolLimits.PreviewDirectory"/>），**不碰目标文件**；
    /// 文件名 = 本次动作的摘要（确定性，可复算）；**不含墙钟时间**（重跑逐字节相同）。</para>
    /// </summary>
    private static string WriteLanding(
        string tool,
        ToolArgs args,
        ToolLimits limits,
        string? path,
        string caption,
        string? fullText,
        IReadOnlyList<ApprovalLine> script)
    {
        Directory.CreateDirectory(limits.PreviewDirectory);

        var digest = ApprovalDigest.Of(tool, args.Canonical, path ?? string.Empty);
        var target = Path.Combine(limits.PreviewDirectory, digest + ".txt");

        var text = new StringBuilder();
        text.Append("# AgentRuntime 审批面 · 完整内容落点（由 Runtime 渲染；不进 prompt）\n");
        text.Append($"tool: {tool}\n");
        text.Append($"path: {path ?? "(无)"}\n");
        text.Append($"digest: {digest}\n");
        text.Append($"caption: {caption}\n");

        if (fullText is not null)
        {
            text.Append("--- 待写入正文（逐字节）---\n");
            text.Append(fullText);
            if (!fullText.EndsWith('\n'))
            {
                text.Append('\n');
            }
        }

        text.Append("--- 完整差异 / 完整正文（逐行）---\n");
        foreach (var line in script)
        {
            text.Append(line.Render()).Append('\n');
        }

        File.WriteAllText(target, text.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return target;
    }
}
