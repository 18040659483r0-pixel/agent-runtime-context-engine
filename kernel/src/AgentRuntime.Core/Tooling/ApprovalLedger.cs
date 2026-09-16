using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRuntime.Core.Tooling;

/// <summary>账本里的一条审批记录（**不进 prompt**；谁批的 / 什么动作 / 何时 / 结局）。</summary>
/// <param name="Seq">账本序号（1 起，只追加）。</param>
/// <param name="Turn">轮次（0 起）。</param>
/// <param name="Tool">工具名。</param>
/// <param name="Risk">分级（当时的分级；分级改了账本仍可复算）。</param>
/// <param name="Action">动作原文（**给人看的那一份** —— 例如完整的 write 路径与内容摘要）。</param>
/// <param name="ArgumentsDigest">参数摘要（一次一批的键）。</param>
/// <param name="Decision">结局。</param>
/// <param name="Actor">谁批的（只认 human 的 Approved）。</param>
/// <param name="Reason">人话原因（拒绝的理由必须留痕）。</param>
/// <param name="SessionId">会话标识（可空）。</param>
public sealed record ApprovalEntry(
    long Seq,
    int Turn,
    string Tool,
    ToolRisk Risk,
    string Action,
    string ArgumentsDigest,
    ApprovalDecision Decision,
    string Actor,
    string Reason,
    string? SessionId = null)
{
    /// <summary>一行账本文本（人眼可读、可 diff）。</summary>
    public string Render() =>
        $"#{Seq} turn {Turn} {Tool} [{ToolNames.Describe(Risk)}] {Decision} by {Actor} · {ArgumentsDigest} · {Action}" +
        (string.IsNullOrWhiteSpace(Reason) ? string.Empty : $" · {Reason}");
}

/// <summary>
/// **审批账本**（只追加；**不进 prompt**）。
/// <para>
/// 与「正文与账本分离」同一条纪律：审批是**流程事实**，不是给模型看的内容 ——
/// 它变更后 prompt 必须**逐字节不变**（F5，有断言）。所以本类**没有**任何「贡献到消息列表」的入口，
/// 模块也不引用它来渲染。
/// </para>
/// <para>
/// <b>模型不得自批</b>：<see cref="Record"/> 里有一条硬守卫 ——
/// 只要 <see cref="ApprovalDecision.Approved"/> 的审批者不是 <see cref="ApprovalActors.Human"/>，**抛错**。
/// 这不是纪律而是闸门：将来若有人加了个「模型自评通过」的闸门实现，它**不会**悄悄生效。
/// </para>
/// </summary>
public sealed class ApprovalLedger
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<ApprovalEntry> _entries = [];

    /// <param name="path">落点（JSONL，只追加）；null = 只存内存（测试 / 无落点）。</param>
    public ApprovalLedger(string? path = null)
    {
        Path = string.IsNullOrWhiteSpace(path) ? null : System.IO.Path.GetFullPath(path);
    }

    /// <summary>落点（null = 只存内存）。**不进 prompt**。</summary>
    public string? Path { get; }

    /// <summary>已记条数（= 账本游标）。</summary>
    public int Count => _entries.Count;

    /// <summary>只读视图（调用方拿不到可变集合）。</summary>
    public IReadOnlyList<ApprovalEntry> Entries => _entries;

    /// <summary>记一条（并落盘，若有落点）。</summary>
    public ApprovalEntry Record(
        int turn,
        string tool,
        ToolRisk risk,
        string action,
        string argumentsDigest,
        ApprovalDecision decision,
        string actor,
        string reason,
        string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        // **模型不得自批**（唯一守卫处）：不是 human 的人不许给 Approved。
        if (decision == ApprovalDecision.Approved && !ApprovalActors.IsHuman(actor))
        {
            throw new InvalidDataException(
                $"审批账本拒绝：审批者 \"{actor}\" 不是人（模型输出「批准」不算批准）；" +
                $"只有 \"{ApprovalActors.Human}\" 能给出 Approved —— 见 docs/DESIGN-TOOL-FACE.md §四。");
        }

        var entry = new ApprovalEntry(
            _entries.Count + 1, turn, tool, risk, action, argumentsDigest, decision, actor, reason, sessionId);
        _entries.Add(entry);
        Persist(entry);
        return entry;
    }

    /// <summary>账本正文（诊断 / 面板用；**不进 prompt**）。</summary>
    public IReadOnlyList<string> Render(int? tail = null)
    {
        var entries = tail is { } n && n > 0 && n < _entries.Count
            ? _entries.Skip(_entries.Count - n)
            : _entries;

        var lines = new List<string>
        {
            $"[审批] 账本：{_entries.Count} 条{(Path is null ? "（只存内存）" : $" → {Path}")}",
        };
        lines.AddRange(entries.Select(e => $"[审批] {e.Render()}"));
        return lines;
    }

    private void Persist(ApprovalEntry entry)
    {
        if (Path is null)
        {
            return;
        }

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(new
        {
            seq = entry.Seq,
            turn = entry.Turn,
            tool = entry.Tool,
            risk = entry.Risk,
            action = entry.Action,
            digest = entry.ArgumentsDigest,
            decision = entry.Decision,
            actor = entry.Actor,
            reason = entry.Reason,
            session = entry.SessionId,
        }, WriteOptions);

        File.AppendAllText(Path, json + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
