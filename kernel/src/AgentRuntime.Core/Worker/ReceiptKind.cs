using System;
using System.Collections.Generic;
using System.Linq;

namespace AgentRuntime.Core.Worker;

/// <summary>
/// **回执结论**（封闭 6 类 · 设计稿 <c>docs/DESIGN-INTENT-TYPES.md</c> §四）。
/// <para>用户 2026-09-30 15:34 令："master 想收到的结论也应该是固定的几种情况"。
/// 每类有**自己的必需字段集** ⇒ 终局自验按 <c>kind</c> 取字段，不再要求"四字段齐"
/// （这一条从构造上解掉"失败回执永远满足不了终局判据 ⇒ 死循环"）。</para>
/// </summary>
public enum ReceiptKind
{
    /// <summary>做完了：交付形状齐、依据齐。**禁止**出现 <c>missing:</c> / <c>blocker:</c> / <c>why:</c>。</summary>
    Done,

    /// <summary>做了但**没盖住** <c>criteria</c>：必带 <c>covered=</c> 与 <c>missing=</c>。</summary>
    Partial,

    /// <summary>被环境/工具挡住（如工具被拒）：必带 <c>blocker=</c> 与 <c>tried=</c>。</summary>
    Blocked,

    /// <summary>判定层不许（受保护目标 / 提权 / 凭据）：必带 <c>policy=</c>。</summary>
    Refused,

    /// <summary>证不通：必带 <c>why=</c> 与 <c>basis=</c>。</summary>
    Impossible,

    /// <summary>要人决定：必带 <c>question=</c>。</summary>
    NeedHuman,
}

/// <summary>
/// 回执类的**机械判据唯一处**：规范名 / 必需字段 / 禁止字段 / 旧形状归一 / residue。
/// <para>与 <see cref="DispatchIntents"/> 同一条纪律：只做机械判据，**不裁决语义**。</para>
/// </summary>
public static class ReceiptKinds
{
    public static string Name(ReceiptKind kind) => kind switch
    {
        ReceiptKind.Done => "done",
        ReceiptKind.Partial => "partial",
        ReceiptKind.Blocked => "blocked",
        ReceiptKind.Refused => "refused",
        ReceiptKind.Impossible => "impossible",
        ReceiptKind.NeedHuman => "need_human",
        _ => "?",
    };

    public static ReceiptKind? Parse(string? text)
    {
        var t = (text ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
        foreach (var kind in Enum.GetValues<ReceiptKind>())
        {
            if (string.Equals(Name(kind), t, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return null;
    }

    /// <summary>
    /// 该类的**必需字段**。
    /// <para><b>用户 2026-09-30 16:44 口径</b>：交付形状由**意图唯一决定**（枚举区声明，worker **无裁量**）⇒
    /// <c>done</c> 的必需集 = **该意图的交付形状键 + <c>basis=</c>**；传 <paramref name="intent"/> 即按类核。
    /// 「不需要某个真值」不是省略的理由 —— 那是**选错了意图/criteria**（枚举里表达），运行时**不兜底**。</para></summary>
    public static IReadOnlyList<string> RequiredFields(ReceiptKind kind, DispatchIntent? intent = null) => kind switch
    {
        ReceiptKind.Done => intent is null
            ? ["basis="]
            : [.. DispatchIntents.DeliveryKeys(intent.Kind).Concat(["basis="]).Distinct(StringComparer.Ordinal)],
        ReceiptKind.Partial => ["covered=", "missing="],
        ReceiptKind.Blocked => ["blocker=", "tried="],
        ReceiptKind.Refused => ["policy="],
        ReceiptKind.Impossible => ["why=", "basis="],
        ReceiptKind.NeedHuman => ["question="],
        _ => [],
    };

    /// <summary>该类回执的**含义**（类型层：什么情况该选它）—— 与意图含义同一口径（类型 ↔ 实例）。</summary>
    public static string Meaning(ReceiptKind kind) => kind switch
    {
        ReceiptKind.Done => "the thing asked for was delivered in full: every delivery key of the combination, each backed by an anchor",
        ReceiptKind.Partial => "you did some of it but the completion criterion is not covered yet -- say exactly what is missing",
        ReceiptKind.Blocked => "the environment stopped you (a tool came back denied, a path is unreachable) -- name the blocker and what you tried instead",
        ReceiptKind.Refused => "policy forbids it (protected target, privilege escalation, credentials) -- name the rule",
        ReceiptKind.Impossible => "you proved the path cannot work -- say why, with the evidence you ran",
        ReceiptKind.NeedHuman => "you need a decision or a fact only the Master/human has -- ask one answerable question",
        _ => "",
    };

    /// <summary>该类回执的**实例**（实例层：一个真例）。</summary>
    public static string Instance(ReceiptKind kind) => kind switch
    {
        ReceiptKind.Done => "[RESULT] <receipt><kind>done</kind><answer>the file is 135 bytes over 2 lines, sha 5534879…</answer><basis>/abs/f.txt:1-2 | shasum rc=0</basis></receipt>",
        ReceiptKind.Partial => "[RESULT] <receipt><kind>partial</kind><answer>read 40 of 1881 lines so far</answer><covered>40/1881</covered><missing>lines 41-1881 of /abs/f.txt</missing></receipt>",
        ReceiptKind.Blocked => "[RESULT] <receipt><kind>blocked</kind><answer>the write was denied so the file never appeared</answer><blocker>write denied (tool-denied)</blocker><tried>printf via exec (also denied)</tried></receipt>",
        ReceiptKind.Refused => "[RESULT] <receipt><kind>refused</kind><answer>the target is a credential file</answer><policy>credential-shaped target</policy></receipt>",
        ReceiptKind.Impossible => "[RESULT] <receipt><kind>impossible</kind><answer>the symbol is not in this tree</answer><why>grep over the whole repo returned 0 hits</why><basis>grep -rn rc=0</basis></receipt>",
        ReceiptKind.NeedHuman => "[RESULT] <receipt><kind>need_human</kind><answer>two targets match, I cannot choose</answer><question>which of the two files should be rewritten?</question></receipt>",
        _ => "",
    };

    /// <summary>该类**禁止出现**的字段（互斥体 · 进夹具）。</summary>
    public static IReadOnlyList<string> ForbiddenFields(ReceiptKind kind) => kind switch
    {
        ReceiptKind.Done => ["missing=", "blocker=", "why=", "question="],
        _ => [],
    };

}
