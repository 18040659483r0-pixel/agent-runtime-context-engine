using AgentRuntime.Models;

namespace AgentRuntime.Core.Stream;

/// <summary>
/// 一条**不可变**的会话事件（论文 §4.1 Session Context Append-Only）。
/// <para>
/// 进入流之后：<b>不删除 / 不移动 / 不重排 / 不覆盖</b>。所以它只有 get-only 属性
/// （record + init），没有任何 setter —— **在类型上不给修改的机会**，这不是纪律是设计。
/// </para>
/// <para>
/// <b>身份 ≠ 位置</b>（V4.1）：<see cref="Tag"/> 是**身份**（<c>E001</c>，进出 prompt 都用它），
/// <see cref="Seq"/> 退回**位置**（只进 JSONL 账本，不渲染）—— 分叉时序号重排、标签不变。
/// </para>
/// <para>
/// <b>正文与账本分离</b>（METHODOLOGY §十·10）：进 prompt 的只有 <see cref="Render"/>；
/// 标签（<see cref="Tag"/>）、序号（<see cref="Seq"/>）与来源（<see cref="Source"/>）是账本，用来定位与对账。
/// </para>
/// </summary>
public sealed record SessionEvent
{
    public SessionEvent(long seq, SessionEventKind kind, string text, string? source = null, string? tag = null)
    {
        if (seq < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(seq), "事件序号从 1 开始。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        // 兼容迁移：旧数据（无 tag 字段）⇒ tag = "E" + seq（等价、不炸旧文件）。
        var effectiveTag = string.IsNullOrWhiteSpace(tag) ? EventTag.Format(seq) : tag.Trim();
        if (!EventTag.IsValid(effectiveTag))
        {
            throw new ArgumentException($"非法事件标签：\"{tag}\"（格式 E{{n}}，如 E001）。", nameof(tag));
        }

        Seq = seq;
        Kind = kind;
        Text = text;
        Source = source;
        Tag = effectiveTag;
    }

    /// <summary>位置：**严格连续**（1,2,3,…）—— 只追加流里的排位，只进账本，不渲染。</summary>
    public long Seq { get; }

    /// <summary>身份：session 内不重复的标签（如 <c>E001</c>）；**fork 时随事件原样复制**。</summary>
    public string Tag { get; }

    /// <summary>类型标签（稳定标签，不由物理位置表达）。</summary>
    public SessionEventKind Kind { get; }

    /// <summary>事件正文（进 prompt 的部分）。</summary>
    public string Text { get; }

    /// <summary>来源指针（账本；如 L3 文档相对路径）。可空。</summary>
    public string? Source { get; }

    /// <summary>标签文本，如 <c>[PITFALL]</c>。</summary>
    public string Label => $"[{Kind.ToString().ToUpperInvariant()}]";

    /// <summary>
    /// 进 prompt 的一行：<c>E001 [MEMORY] 某次具体事件</c>（**行首用 Tag**）。
    /// <para>标签直接出现在正文里 ⇒ 模型自报时可以**原样引用**（论文 §4.5 回路的最后一环）。</para>
    /// </summary>
    public string Render() => $"{Tag} {Label} {Text}";

    /// <summary>转成一条消息：用户/助手轮次保持原角色，其余一律 system（只追加，不改角色语义）。</summary>
    public ChatMessage ToChatMessage() => Kind switch
    {
        SessionEventKind.UserInput => ChatMessage.User(Text),
        SessionEventKind.AgentOutput => ChatMessage.Assistant(Text),
        _ => ChatMessage.System(Render()),
    };
}
