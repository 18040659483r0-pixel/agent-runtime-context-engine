using System.Text;
using AgentRuntime.Core;
using AgentRuntime.Core.Protocol;
using AgentRuntime.Core.Stream;
using AgentRuntime.Models;

namespace AgentRuntime.Modules;

/// <summary>
/// **笔记落盘**（D3-A · 2026-10-09 主人令 · 设计 <c>docs/DESIGN-SPLIT-ON-STOP.md</c> §十）：
/// master 写一个 <c>[NOTE]</c> 块 ⇒ 运行时**确定性**把它追加进当日 <c>memory/YYYY-MM-DD.md</c>
/// （活版根 = <c>lifecycle.workspace</c>）。**零注入、不经派发链**。
///
/// <para><b>为什么要有它</b>：真机一次「记一笔」走了两轮派发、**14 分钟**、且被 worker 压缩改写（不保真）。
/// 「往文件尾追加一段」是机械动作，不该喂慢模型（§铁则 3：让程序做确定的事），也不该让「停下」被它卡住（D3）。</para>
///
/// <para><b>不变量</b>：① 零注入（一个字节都不进 prompt）；② 只追加、不覆盖既有正文；③ 没配活版根 ⇒ **不落盘**（不猜路径）。</para>
/// </summary>
public sealed class NoteModule : RuntimeModuleBase
{
    /// <summary>模块名（配置开关名）。</summary>
    public const string ModuleName = "note";

    private readonly string _workspace;
    private readonly IEventSink? _sink;

    /// <param name="workspace">活版根（<c>lifecycle.workspace</c> 的**绝对**路径）；空/缺 ⇒ 不落盘。</param>
    /// <param name="sink">事件落点（可选）—— 落了盘要在流里看得见（不静默）。</param>
    public NoteModule(string? workspace, IEventSink? sink = null)
    {
        _workspace = workspace ?? string.Empty;
        _sink = sink;
    }

    public override string Name => ModuleName;

    /// <summary>零注入：一个字节都不进 prompt。</summary>
    public override ValueTask ContributeAsync(RuntimeContext context, IList<ChatMessage> messages, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public override ValueTask ObserveAsync(RuntimeContext context, ChatRequest request, ChatResponse response, CancellationToken cancellationToken)
    {
        var text = response.Choices.Count > 0 ? response.Choices[0].Message.Content : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return ValueTask.CompletedTask;
        }

        foreach (var note in Extract(text))
        {
            Record(note);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 取 <c>[NOTE]</c> 块的正文（**唯一解析处**）：块头**在行首**（或紧跟在已知块头之后）；
    /// 正文 = 该行剩余部分 + 直到下一个已知块头 / 空行结束之前的行。
    /// <para>口径与 <c>[TAIL]</c>/<c>[DRAFT]</c> 同族：块头在行首才算块（否则是正文里的回声）。</para>
    /// </summary>
    public static IReadOnlyList<string> Extract(string text)
    {
        var notes = new List<string>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.TrimStart().StartsWith(ProtocolText.NotePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var sb = new StringBuilder(line.TrimStart()[ProtocolText.NotePrefix.Length..].Trim());
            for (var j = i + 1; j < lines.Length; j++)
            {
                var next = lines[j];
                if (IsBlockHeader(next))
                {
                    break;
                }

                i = j;
                sb.Append('\n').Append(next.TrimEnd());
            }

            var note = sb.ToString().Trim();
            if (note.Length > 0)
            {
                notes.Add(note);
            }
        }

        return notes;
    }

    private static bool IsBlockHeader(string line)
    {
        var t = line.TrimStart();
        return ProtocolText.ReportBlockHeaders.Any(h => t.StartsWith(h, StringComparison.Ordinal));
    }

    /// <summary>落盘（唯一写入处）：<c>&lt;workspace&gt;/memory/YYYY-MM-DD.md</c>，只追加。</summary>
    private void Record(string note)
    {
        if (_workspace.Length == 0)
        {
            _sink?.Append(SessionEventKind.Hint, $"[笔记] 未配活版根（lifecycle.workspace）⇒ **未落盘**（这条只留在这里）：{First(note)}");
            return;
        }

        var dir = Path.Combine(_workspace, "memory");
        var name = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var path = Path.Combine(dir, name + ".md");
        try
        {
            Directory.CreateDirectory(dir);
            var body = (File.Exists(path) ? string.Empty : $"# {name}\n") + "\n" + note.TrimEnd() + "\n";
            File.AppendAllText(path, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var indexed = AppendIndexLine(name, note);
            _sink?.Append(SessionEventKind.Hint,
                $"[笔记] 已落盘 {path}（{note.Length} 字符）" + (indexed ? " · 索引已追加" : " · 索引未变"));
        }
        catch (Exception ex)
        {
            // 落盘失败**必须响亮**（不静默）——但**不阻断**本轮（记笔记不是关键路径）。
            _sink?.Append(SessionEventKind.Hint, $"[笔记] 落盘失败（{ex.GetType().Name}：{ex.Message}）—— 未记入 {path}");
        }
    }

    /// <summary>
    /// **[NOTE] 同时记入记忆索引**（主人 2026-10-22：「都记入 memory 和 memory-index」）。
    /// <para>只追加到文件末尾一个**稳定的自动区**（`## 附：[NOTE] 自动追加`），不往主时间线里插行 ——
    /// 主表由收尾维护（按日期排序），自动追加不该改它的次序。同一条（同日期 + 同首行）不重复写。</para>
    /// <para>失败**不阻断**记笔记（索引是派生视图；正文已落盘才算数），但要在流里说清。</para>
    /// </summary>
    /// <returns>真的追加了才 true（重复 ⇒ false）。</returns>
    private bool AppendIndexLine(string day, string note)
    {
        try
        {
            var indexPath = Path.Combine(_workspace, MemoryIndexLiveModule.RelativePath);
            var dir = Path.GetDirectoryName(indexPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            const string Heading = "## 附：[NOTE] 自动追加（按日）";
            var head = First(note).Replace('|', '/').Trim();
            if (head.Length > 80)
            {
                head = head[..80] + "…";
            }

            var line = $"- {day} {DateTimeOffset.Now:HH:mm} — {head}（`memory/{day}.md`）";
            var existing = File.Exists(indexPath) ? File.ReadAllText(indexPath, Encoding.UTF8) : string.Empty;
            if (existing.Contains(line, StringComparison.Ordinal))
            {
                return false;
            }

            var prefix = existing.Length == 0
                ? "# MEMORYINDEX（记忆索引）\n"
                : existing.TrimEnd() + "\n\n";
            var section = existing.Contains(Heading, StringComparison.Ordinal) ? string.Empty : Heading + "\n\n";
            File.WriteAllText(indexPath, prefix + section + line + "\n", new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            _sink?.Append(SessionEventKind.Hint, $"[笔记] 索引追加失败（{ex.GetType().Name}：{ex.Message}）—— 正文已落盘，索引下次收尾补");
            return false;
        }
    }

    private static string First(string note)
    {
        var at = note.IndexOf('\n');
        return at < 0 ? note : note[..at];
    }
}
