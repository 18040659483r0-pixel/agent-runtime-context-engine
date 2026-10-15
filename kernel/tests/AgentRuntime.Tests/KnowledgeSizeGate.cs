using System.Text;
using System.Text.RegularExpressions;

namespace AgentRuntime.Tests;

/// <summary>
/// **顶层知识库的体量闸门**（2026-10-14 立；口径 = 「只许缩不许长」）。
/// <para>
/// 背景：`whitebox/workspace/KNOWLEDGE.md` 是**冻结语料源**（进模型前缀）⇒ 它涨 = 每轮都贵。
/// 闸门把「§十 通用工程铁则」的**字节数与条数**钉成基线：涨了就红，缩了不红（缩了请顺手改小基线）。
/// </para>
/// <para>纪律借用仓内既有写法（同 <c>RuntimeRulesTests</c> 的 R1 基线账本）：**基线只许缩**。</para>
/// </summary>
public static class KnowledgeSizeGate
{
    /// <summary>§十 的**基线**（2026-10-14 手压批 3 后实测：27,352 B / 77 条 / 文件 50740 B；原始 46,784 / 69,655）。**只许缩**：从今往后不许再涨，涨了（如收尾补 L1/L2）必须**同批**抬这里 —— §十·65 账本跟随事实。2026-10-15 抬两次：① r1706 收尾补 §十·75（→50740）；② 本会话收尾补 §十·76（→51187）。规律：收尾按铁则 0b 补 L1/L2 ⇒ 基线须**同批**抬（§十·65）。根治办法：基线该由收尾流程重算并写，而非手抬（待做）。</summary>
    public const int SectionTenBytesCeiling = 27353;
    public const int SectionTenItemsCeiling = 83;
    public const int FileBytesCeiling = 51187;

    /// <summary>抽出「## 十、」到下一个「## 」之前的原文（找不到 ⇒ 抛，闸门不能静默通过）。</summary>
    public static string SectionTen(string text)
    {
        var start = text.IndexOf("## 十、", StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidDataException("找不到 §十 段头 —— 闸门不能静默通过。");
        }

        var rest = text[(start + 4)..];
        var next = rest.IndexOf("\n## ", StringComparison.Ordinal);
        return next < 0 ? rest : rest[..next];
    }

    /// <summary>条的编号（`^N. `）—— 用于条数基线。</summary>
    public static int ItemCount(string sectionTen) =>
        Regex.Matches(sectionTen, @"(?m)^\d+\.\s").Count;

    /// <summary>检查：返回违规说明（空 = 绿）。</summary>
    public static IReadOnlyList<string> Check(string text)
    {
        var bad = new List<string>();
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes > FileBytesCeiling)
        {
            bad.Add($"KNOWLEDGE.md {bytes} B > 基线 {FileBytesCeiling} B（只许缩不许长）");
        }

        var sec = SectionTen(text);
        var secBytes = Encoding.UTF8.GetByteCount(sec);
        if (secBytes > SectionTenBytesCeiling)
        {
            bad.Add($"§十 {secBytes} B > 基线 {SectionTenBytesCeiling} B（只许缩不许长）");
        }

        var items = ItemCount(sec);
        if (items > SectionTenItemsCeiling)
        {
            bad.Add($"§十 {items} 条 > 基线 {SectionTenItemsCeiling} 条（只许缩不许长）");
        }

        return bad;
    }
}
