using System.Text.RegularExpressions;
using AgentRuntime.Models;

namespace AgentRuntime.Core.Frozen;

/// <summary>
/// **收尾（内置）** —— 主人 2026-10-11 定的形态：
/// <list type="number">
/// <item><b>原料</b>（只读）：**水位线之后「新出现」的内容** —— <c>memory</c> 日记 · 踩坑集条目 · handoff 条目
/// （**三者同类，都是原料**；memory 还承载「用户对铁则区的改动意图」）。</item>
/// <item><b>远端 AI 只做提炼</b>：① 有没有值得固化到顶部知识库的 ⇒ 凝练成 <b>L1/L2/L3/L4</b> 条目；
/// ② 有没有**铁则要新增/改动** ⇒ 按 <b>MD 文件名 + 条目编号</b> 指认。</item>
/// <item><b>运行时接管落点</b>：写 <c>KNOWLEDGE.md</c>（知识）+ 改**铁则区**（按编号直达）+ **提收尾水位线**。</item>
/// </list>
/// <para><b>为什么是文件 IO</b>：整条没有一步需要起进程（铁则 R2 只管起进程）⇒ 运行时可以自己做完
/// 「收集 → 一问 → 写回 → 提水位」，远端 AI 只贡献**抽象出来的知识**。</para>
/// </summary>
public sealed record CloseoutContext(
    string Workspace,
    string? PitfallsPath,
    DateTimeOffset Now,
    string? WatermarkFile = null,
    string WatermarkMarker = "## 水位")
{
    /// <summary>当前收尾水位（<c>YYYY-MM-DD</c>；读不出 ⇒ null ⇒ 原料取不到 ⇒ 不猜）。</summary>
    public string? Watermark { get; init; }
}

/// <summary>**原料**（只读）：水位线之后、还没被收尾覆盖的那一段。</summary>
public interface ICloseoutSource
{
    string Id { get; }

    string Title { get; }

    /// <summary>读：水位线之后的**新出现内容**（只读，不写）。</summary>
    CloseoutSlice Gather(CloseoutContext context);
}

/// <summary>**落点**（运行时写）：收尾结果落到哪个 MD。**只有两个** —— 知识库、铁则区。</summary>
public interface ICloseoutSink
{
    string Id { get; }

    string Title { get; }

    /// <summary>落地的 MD（绝对路径）。</summary>
    string File { get; }

    /// <summary>把模型的收尾结果**写进去**（运行时执行；返回给人看的事实）。</summary>
    IReadOnlyList<string> Apply(string content, IEnumerable<string> allowedRoots);
}

/// <summary>
/// **收尾·后处理步**（写完之后，运行时自己做的机械活：重建派生 / 校验 / 记账）。
/// <para>加一类 ⇒ **加一个实现并注册**（OOP-first 铁则 8：加类型，不改旧类型的分支）。</para>
/// <para><b>与落点（<see cref="ICloseoutSink"/>）的区别</b>：落点是「模型给内容、运行时落盘」；
/// 后处理步是「**内容已定，运行时把它后面的机械活做完**」——典型就是 **B4 搬进 C# 的那几条流水线**。</para>
/// </summary>
public interface ICloseoutStep
{
    string Id { get; }

    string Title { get; }

    /// <summary>本步对当前上下文是否适用（不适用 ⇒ **如实跳过**，不算失败）。</summary>
    bool Applies(CloseoutContext context);

    /// <summary>做 + 返回给人看的事实。</summary>
    IReadOnlyList<string> Run(CloseoutContext context);
}

/// <summary>一份「水位线之后」的原料切片。</summary>
public sealed record CloseoutSlice(
    string Id,
    string Title,
    string File,
    string Watermark,
    IReadOnlyList<string> Pending,
    bool Truncated = false)
{
    public int PendingCount => Pending.Count;
    public bool HasPending => Pending.Count > 0;
}

/// <summary>一次收尾的结果。</summary>
public sealed record CloseoutRun(int MaterialSources, int ModelCalls, IReadOnlyList<string> Lines);

/// <summary>
/// **内置收尾（三段）**：收集原料 → **一次成型问法** → 运行时写回 + 提水位。
/// </summary>
public static class CloseoutJob
{
    /// <summary>单份原料进载荷的行上限（防载荷爆；超了如实标 <c>Truncated</c>）。</summary>
    public const int MaxPendingLinesPerTarget = 400;

    /// <summary>回写块的块头 / 块尾（解析边界；唯一声明处）。</summary>
    public const string WriteHeader = "[CLOSEOUT]";
    public const string WriteFooter = "[/CLOSEOUT]";

    /// <summary>**收集**（只读）：逐份读原料（顺序 = 注册顺序）。</summary>
    public static IReadOnlyList<CloseoutSlice> Gather(IEnumerable<ICloseoutSource> sources, CloseoutContext context)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(context);

        // 水位在这里**解析一次**（调用方不必自备）—— 原料的切片口径统一由它决定。
        context = context with { Watermark = context.Watermark ?? ReadWatermark(context) };
        return [.. sources.Select(s => s.Gather(context))];
    }

    /// <summary>读水位（<c>WatermarkMarker</c> 之后第一条非空行里的 <c>YYYY-MM-DD</c>）；读不出 ⇒ null（不猜）。</summary>
    public static string? ReadWatermark(CloseoutContext context)
    {
        if (string.IsNullOrWhiteSpace(context.WatermarkFile) || !File.Exists(context.WatermarkFile))
        {
            return null;
        }

        // 只认**运行时自己写的那行**（锚之后第一条非空行）里的日期 —— 别拿文件里任一日期充数（历史/示例会误判）。
        var lines = File.ReadAllText(context.WatermarkFile).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var at = Array.FindIndex(lines, l => l.Trim().StartsWith(context.WatermarkMarker.Trim(), StringComparison.Ordinal));
        if (at < 0)
        {
            return null;
        }

        // ① 先认**运行时自己写的那条带标签行**（唯一来源；跨版本稳定）。读不出再退 ②。
        var labeled = lines.FirstOrDefault(l => l.Contains("知识水位（运行时写）", StringComparison.Ordinal));
        if (labeled is not null)
        {
            var m0 = Regex.Match(labeled, @"\d{4}-\d{2}-\d{2}");
            if (m0.Success)
            {
                return m0.Value;
            }
        }

        // ② 老口径：锚之后第一条非空行里的日期（读不出 ⇒ null ⇒ 调用方 **fail-closed**，绝不静默跳过）。
        for (var i = at + 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var match = Regex.Match(lines[i], @"\d{4}-\d{2}-\d{2}");
            return match.Success ? match.Value : null;
        }

        return null;
    }

    /// <summary>
    /// **一次成型问法**（主人点名「问法很重要」）：两问 ——
    /// ① 有没有值得固化到顶部知识库的（有 ⇒ 凝练成 L1/L2/L3/L4 条目）；② 有没有铁则要新增/改动（给文件名 + 编号）。
    /// </summary>
    public static string BuildAsk(IReadOnlyList<CloseoutSlice> material, IReadOnlyList<ICloseoutSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(material);

        var text = new System.Text.StringBuilder();
        text.AppendLine("收尾。下面是**你上次收尾之后新出现**的全部材料（只读给你看，不要回写材料）。");
        text.AppendLine("只回你要给的**新条目**，别复述材料：");
        text.AppendLine("  **① 知识树**：从材料里提炼**新增**知识，按一条链一组：");
        text.AppendLine("     先写 L4 证据，再向上抽象 L3 处理单元 → L2 问题形状 → L1 法则：");
        text.AppendLine("     `<T1 layer=\"4\" src=\"来源路径\">证据要点</T1>`");
        text.AppendLine("     `<T2 layer=\"3\" next=\"T1\">处理单元</T2>`");
        text.AppendLine("     `<T3 layer=\"2\" next=\"T2\">问题形状</T3>`");
        text.AppendLine("     `<T4 layer=\"1\" next=\"T3\">一句法则</T4>`");
        text.AppendLine("     临时 id 全批唯一（T1..Tn 不重复）；只给新增；不要重复已有 L1~L4；没有就写「本批无新增」。");
        text.AppendLine("  **② 铁则**：有没有铁则要新增或改动？有 ⇒ 直接写出**那一条铁则**（用「铁则 <编号> · <标题>」起头，正文紧随）。");
        text.AppendLine("没有就明说「本批无新增」——**不要为了交差硬凑**。");
        text.AppendLine();
        text.AppendLine("回写格式（每类一个块）：");
        foreach (var sink in sinks)
        {
            text.AppendLine($"  {WriteHeader} {sink.Id}      …{sink.Title}的新条目（逐字）…");
            text.AppendLine($"  {WriteFooter}");
        }

        text.AppendLine();
        foreach (var slice in material)
        {
            text.AppendLine($"—— 材料：{slice.Title}（{slice.Id}）· 水位线 {slice.Watermark} · 新增 {slice.PendingCount}")
                ;
            if (!slice.HasPending)
            {
                text.AppendLine("   （水位线之后没有新增）");
                continue;
            }

            foreach (var line in slice.Pending)
            {
                text.AppendLine("   | " + line);
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>解析回写块 ⇒ (落点 id → 内容)。**不认识 id 的块不写它的落点**。</summary>
    public static IReadOnlyList<(string Id, string Content)> ParseWrites(string? modelText, IReadOnlyList<ICloseoutSink> sinks)
    {
        if (string.IsNullOrWhiteSpace(modelText))
        {
            return [];
        }

        var known = sinks.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var pattern = new Regex(
            $@"{Regex.Escape(WriteHeader)}\s*(?<id>[A-Za-z0-9_\-]+)\s*\n(?<body>.*?)\n?\s*{Regex.Escape(WriteFooter)}",
            RegexOptions.Singleline);

        var writes = new List<(string, string)>();
        foreach (Match match in pattern.Matches(modelText))
        {
            var id = match.Groups["id"].Value.Trim();
            if (!known.Contains(id))
            {
                continue;
            }

            var body = match.Groups["body"].Value.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
            if (body.Contains("本批无新增", StringComparison.Ordinal))
            {
                continue;   // 明说没有 ⇒ 不写（不许把「无新增」当内容写进去）
            }

            writes.Add((id, body));
        }

        return writes;
    }

    /// <summary>
    /// **一次做完**：收集原料 → 一次模型调用 → 运行时写回**两个落点** → **提水位**。
    /// **原料为空 ⇒ 不调模型**；没给出块的落点 ⇒ **那份就是没改**（不拿旧内容充数）。
    /// </summary>
    public static async Task<CloseoutRun> RunAsync(
        IModelClient client,
        IReadOnlyList<ICloseoutSource> sources,
        IReadOnlyList<ICloseoutSink> sinks,
        CloseoutContext context,
        IEnumerable<string> allowedRoots,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ICloseoutStep>? steps = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        var roots = allowedRoots.ToArray();
        var watermark = context.Watermark ?? ReadWatermark(context);
        context = context with { Watermark = watermark };

        var material = Gather(sources, context);
        var fresh = material.Where(s => s.HasPending).ToArray();
        var lines = new List<string>();
        var calls = 0;

        if (fresh.Length > 0)
        {
            var response = await client
                .CompleteAsync(new ChatRequest { Messages = [ChatMessage.User(BuildAsk(material, sinks))] }, cancellationToken)
                .ConfigureAwait(false);
            calls = 1;

            var writes = ParseWrites(response.FirstText(), sinks);
            lines.Add($"[收尾·内置] **一次调用**（{client.Name}）· 原料 {fresh.Length} 份 · 回写 {writes.Count} 个落点");
            foreach (var (id, content) in writes)
            {
                var sink = sinks.First(s => s.Id == id);
                lines.AddRange(sink.Apply(content, roots));
            }

            foreach (var sink in sinks.Where(s => writes.All(w => w.Id != s.Id)))
            {
                lines.Add($"[收尾·内置] 落点 {sink.Id}：**本批无新增**（不拿旧内容充数）。");
            }
        }
        else
        {
            lines.Add("[收尾·内置] 水位线之后**没有新材料** ⇒ **不调模型**（省一次钱 / 一次冷前缀）。");
        }

        // **后处理步**（B4：把原来「远端 AI 点 exec 起 python」的那几条流水线改为**运行时进程内**跑）。
        foreach (var step in steps ?? [])
        {
            if (!step.Applies(context))
            {
                lines.Add($"[收尾·{step.Id}] 不适用（{step.Title}：输入不在）⇒ **跳过**。");
                continue;
            }

            lines.AddRange(step.Run(context));
        }

        // **水位线由运行时提高到最新位置**（不是模型给的 —— 机械事实归运行时）。
        // **但材料被截断时绝不推进**（2026-10-11 主人令）：上限之外的部分会被静默跳过 ⇒ 推进水位 = 假干净。
        var truncated = material.Where(s => s.Truncated).Select(s => s.Id).ToArray();
        if (!string.IsNullOrWhiteSpace(context.WatermarkFile) && File.Exists(context.WatermarkFile))
        {
            if (truncated.Length > 0)
            {
                lines.Add($"[收尾·内置] ⚠️ 材料被**截断**（{string.Join(" / ", truncated)} 超过 {MaxPendingLinesPerTarget} 条上限）"
                          + " ⇒ **不推进水位线**（否则上限之外的部分会被静默跳过）；下一轮收尾接着做。");
            }
            else
            {
                var line = $"- **知识水位（运行时写）**：已收敛至 {context.Now:yyyy-MM-dd} @ {context.Now:yyyy-MM-dd HH:mm}（水位线由运行时提高）";
                CorpusWriter.ReplaceLabeledOrInsertAfter(context.WatermarkFile, "知识水位（运行时写）", context.WatermarkMarker, line, roots);
                lines.Add($"[收尾·内置] 水位线（运行时写）：{context.WatermarkFile}");
            }
        }

        return new CloseoutRun(fresh.Length, calls, lines);
    }
}

// ───────────────────────── 内置原料（只读） ─────────────────────────

/// <summary>原料：**memory 日记**（水位线之后新出现的；并承载「铁则区改动意图」）。</summary>
public sealed class MemorySource(string workspace) : ICloseoutSource
{
    private static readonly Regex DatedFile = new(@"^(?<date>\d{4}-\d{2}-\d{2})(-\d{4})?\.md$", RegexOptions.Compiled);

    public string Id => "memory";
    public string Title => "会话记忆（水位线之后）";

    public CloseoutSlice Gather(CloseoutContext context)
    {
        var dir = Path.Combine(context.Workspace, "memory");
        var pending = new List<string>();
        if (Directory.Exists(dir))
        {
            // **fail-closed（2026-10-11）**：水位**读不出**时**全取**（宁可多做）——
            // 旧口径是「读不出 ⇒ 空 ⇒ 没有新材料 ⇒ 不调模型」，再自己写一条「已收敛至今天」⇒ **假干净**。
            var files = string.IsNullOrWhiteSpace(context.Watermark)
                ? Newer(dir, "0000-00-00").TakeLast(5).ToList()
                : Newer(dir, context.Watermark);
            foreach (var name in files)
            {
                pending.Add($"### {name}");
                var text = File.ReadAllText(Path.Combine(dir, name));
                pending.AddRange(text.Replace("\r\n", "\n").Split('\n').Take(100));
            }
        }

        return Slice(Id, Title, dir, context, pending);
    }

    internal static List<string> Newer(string dir, string watermark) =>
        [.. Directory.EnumerateFiles(dir, "*.md")
            .Select(Path.GetFileName)
            .Where(n => n is not null && DatedFile.IsMatch(n))
            .Select(n => (Name: n!, Date: DatedFile.Match(n!).Groups["date"].Value))
            .Where(f => string.CompareOrdinal(f.Date, watermark) > 0)
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => f.Name)];

    internal static CloseoutSlice Slice(string id, string title, string file, CloseoutContext context, List<string> pending)
    {
        var truncated = pending.Count > CloseoutJob.MaxPendingLinesPerTarget;
        return new CloseoutSlice(id, title, file, context.Watermark ?? "（读不出）",
            truncated ? [.. pending.Take(CloseoutJob.MaxPendingLinesPerTarget)] : pending, truncated);
    }
}

/// <summary>原料：**踩坑集**（水位线之后新增的条目 —— 按条目正文里的日期认）。</summary>
public sealed class PitfallsSource(string path) : ICloseoutSource
{
    private static readonly Regex DatedPitfall = new(@"^(?<n>#+\s*#?\d+.*?)(?=^#|\z)", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);

    public string Id => "pitfalls";
    public string Title => "踩坑集（水位线之后）";

    public CloseoutSlice Gather(CloseoutContext context)
    {
        var pending = new List<string>();
        if (context.Watermark is { Length: > 0 } wm && File.Exists(path))
        {
            var text = File.ReadAllText(path);
            foreach (Match m in DatedPitfall.Matches(text))
            {
                var block = m.Value.Trim();
                if (block.Contains(wm, StringComparison.Ordinal) is false
                    && block.Length > 0
                    && block.Any(char.IsDigit))
                {
                    // 条目正文里带「YYYY-MM-DD」，只取**晚于水位**的那些。
                    var dates = Regex.Matches(block, @"\d{4}-\d{2}-\d{2}").Select(x => x.Value);
                    if (dates.Any(d => string.CompareOrdinal(d, wm) > 0))
                    {
                        pending.AddRange(block.Replace("\r\n", "\n").Split('\n').Take(60));
                        pending.Add(string.Empty);
                    }
                }
            }
        }

        return MemorySource.Slice(Id, Title, path, context, pending);
    }
}

/// <summary>原料：**handoff 条目**（水位线之后新增的文件）。</summary>
public sealed class HandoffSource(string workspace) : ICloseoutSource
{
    public string Id => "handoff";
    public string Title => "handoff 条目（水位线之后）";

    public CloseoutSlice Gather(CloseoutContext context)
    {
        var dir = Path.Combine(context.Workspace, "handoff");
        var pending = new List<string>();
        if (context.Watermark is { Length: > 0 } wm && Directory.Exists(dir))
        {
            var files = Directory.EnumerateFiles(dir)
                .Select(Path.GetFileName)
                .Where(n => n is not null && n.Contains('-') && !n.StartsWith('.'))
                .Where(n => Regex.Matches(n!, @"\d{4}-\d{2}-\d{2}").Any(x => string.CompareOrdinal(x.Value, wm) > 0))
                .OrderBy(n => n, StringComparer.Ordinal)!;
            foreach (var name in files)
            {
                pending.Add($"### {name}");
                pending.AddRange(File.ReadAllText(Path.Combine(dir, name)).Replace("\r\n", "\n").Split('\n').Take(80));
            }
        }

        return MemorySource.Slice(Id, Title, dir, context, pending);
    }
}

// ───────────────────────── 内置落点（运行时写） ─────────────────────────

/// <summary>
/// 落点：**铁则区**（源文件 = <c>AGENTS.md</c>；frozen <c>rules/global.md</c> 由它派生）。
/// <para>**按编号直达**：模型给的是「铁则 &lt;编号&gt; · &lt;标题&gt; + 正文」；已存在同号 ⇒ **整段替换**，
/// 新号 ⇒ 插到最后一条铁则之后（不猜别处）。</para>
/// </summary>
public sealed class RulesSink(string workspace) : ICloseoutSink
{
    private static readonly Regex RuleHead = new(@"^##\s+.*?铁则\s*(?<n>\d+)\s*[·:：]", RegexOptions.Compiled | RegexOptions.Multiline);

    public string Id => "rules";
    public string Title => "铁则区（AGENTS.md）";
    public string File { get; } = Path.Combine(workspace, "AGENTS.md");

    public IReadOnlyList<string> Apply(string content, IEnumerable<string> allowedRoots)
    {
        var roots = allowedRoots.ToArray();
        var lines = System.IO.File.ReadAllText(File).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();

        // 把模型给的正文按「铁则 N」切成若干条。
        var blocks = SplitRules(content);
        if (blocks.Count == 0)
        {
            return [$"[收尾·写] rules：给的正文里没有「铁则 <编号>」条目 ⇒ **一条也没改**（不猜）。"];
        }

        var facts = new List<string>();
        foreach (var (number, block) in blocks)
        {
            var at = lines.FindIndex(l => RuleHead.IsMatch(l) && RuleHead.Match(l).Groups["n"].Value == number);
            var blockLines = block.TrimEnd('\n').Split('\n');
            if (at >= 0)
            {
                var end = at + 1;
                while (end < lines.Count && !lines[end].StartsWith("## ", StringComparison.Ordinal))
                {
                    end++;
                }

                lines.RemoveRange(at, end - at);
                lines.InsertRange(at, blockLines);
                facts.Add($"[收尾·写] rules：**替换** 铁则 {number}（{Path.GetFileName(File)}）");
            }
            else
            {
                var last = lines.FindLastIndex(l => RuleHead.IsMatch(l));
                var insertAt = last < 0 ? lines.Count : last + 1;
                lines.InsertRange(insertAt, [.. blockLines, ""]);
                facts.Add($"[收尾·写] rules：**新增** 铁则 {number}（{Path.GetFileName(File)}，插在最后一条铁则之后）");
            }
        }

        CorpusWriter.Write(File, string.Join('\n', lines), roots);
        return facts;
    }

    /// <summary>把正文按「## … 铁则 N …」切成 (编号, 整段)。</summary>
    internal static List<(string Number, string Block)> SplitRules(string content)
    {
        var result = new List<(string, string)>();
        var matches = RuleHead.Matches(content);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : content.Length;
            result.Add((matches[i].Groups["n"].Value, content[start..end]));
        }

        return result;
    }
}
