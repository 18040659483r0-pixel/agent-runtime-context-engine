using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentRuntime.Models;

/// <summary>
/// **模型侧控制标记**（**唯一声明处 + 唯一剥除口**）—— 它家的 special token 属于**控制**，不属于内容。
/// <para>
/// 现场（2026-10-14 真机）：模型把 DeepSeek 原生工具标记当**正文**吐出来 ——
/// 一类是<b>整套原生块</b>（<c>&lt;｜｜DSML｜｜ invoke name="exec"&gt;</c> +
/// <c>&lt;｜｜DSML｜｜ parameter name="command" string="true"&gt;…</c>），
/// 另一类只是<b>残留一个开场标记</b>（<c>&lt;｜｜DSML｜｜ calls&gt;</c> 一行，后面跟着**正确的** <c>[TOOL]</c> 块）。
/// </para>
/// <para>
/// <b>为什么必须统一剥掉</b>：标记留在正文里 ⇒ 进事件流 ⇒ 下一轮当历史读回去 ⇒ 模型看见自己上次这么写 ⇒
/// <b>自我强化</b>（真机实测：一条 197 事件的会话里 **34 条**含 DSML）。剥除点定在**响应成型处**
/// （客户端反序列化之后）：此后流、屏、下一轮 prompt 看到的都是同一份清洗过的正文（T1 不变量不受影响 ——
/// 预览与实发仍然同源，因为两边用的都是这一份）。
/// </para>
/// <para>
/// ⚠️ **只剥「控制」那一层**：<c>&lt;｜｜DSML｜｜ invoke name="exec"&gt;</c> 剥成 <c>invoke name="exec"&gt;</c>
/// （**payload 一字不动**，形状归一留给 <c>ToolSyntaxNormalizer</c>）；剥完只剩残行的标记（<c>calls&gt;</c> /
/// <c>&lt;/ invoke&gt;</c> 这种**没有属性、没有 payload** 的）连**整行**一起丢。
/// </para>
/// </summary>
public static class ControlTokens
{
    /// <summary>DeepSeek 原生工具标记前缀（形如 <c>&lt;｜｜DSML｜｜ invoke name="read"&gt;</c>）。</summary>
    public const string Dsml = "<｜｜DSML｜｜";

    /// <summary>句子包裹标记（同族 token 泄漏）：它们是**包裹**，不是内容。</summary>
    public static readonly IReadOnlyList<string> Wrappers =
    [
        "<｜begin▁of▁sentence｜>", "<｜end▁of▁sentence｜>",
        "<｜begin_of_sentence｜>", "<｜end_of_sentence｜>",
        // 批 2（2026-10-17）：**原生调用的容器**（同名骨架的“外壳”）—— 它本身不是内容，只剩了 ⇒ 剥掉。
        // ⚠️ 精确串替换：`<tool_call>`（无属性）不会碰到 `<tool_call name="…">`（那是调用本体，由归一器的 InvokePattern 收）。
        "<tool_calls>", "</tool_calls>", "<function_calls>", "</function_calls>",
    ];

    /// <summary>
    /// **剥完只剩一个控制词 + <c>&gt;</c> 的整行**（没有属性、没有 payload）—— 例如
    /// <c>calls&gt;</c>（来自裸标记 <c>&lt;｜｜DSML｜｜ calls&gt;</c>）。
    /// <para>
    /// ⚠️ **只丢「裸标记的残骸」，绝不碰合法的收盘**：<c>&lt;/ invoke&gt;</c> / <c>&lt;/ parameter&gt;</c> 是
    /// <c>[TOOL]</c> 原生块**正经的收盘写法**（形状归一靠它们把 payload 括起来）——
    /// 把 <c>&lt;/ parameter&gt;</c> 当残行丢掉 ⇒ 参数值括不住 ⇒ 块整篇归不出（本条第一版就踩了，5 条夹具当场红）。
    /// 所以：① 无 <c>&lt;</c> 的裸词 + <c>&gt;</c>（裸标记剥完的样子）丢；② 收盘里**永远不是合法收盘**的那几个词
    /// （<c>calls</c> / <c>begin</c> / <c>end</c> / <c>thinking</c>）丢；<c>invoke</c> / <c>call</c> / <c>parameter</c> 的收盘**一个不动**。
    /// </para>
    /// </summary>
    private static readonly Regex ResidueLine = new(
        @"^(?:</\s*(?:calls|begin|end|thinking)\s*>|(?:calls|call|invoke|parameter|begin|end|thinking)>)$",
        RegexOptions.Compiled);

    /// <summary>
    /// 剥掉控制标记（**幂等**；没有可剥的 ⇒ 逐字节原样返回）。
    /// <para>口径：① 句子包裹标记整段删；② <c>&lt;｜｜DSML｜｜</c> / <c>&lt;/｜｜DSML｜｜</c> 前缀删（payload 保留）；
    /// ③ 剥完只剩残骸的行**连行删**（不留空行）。</para>
    /// </summary>
    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var s = text;
        foreach (var wrapper in Wrappers)
        {
            s = s.Replace(wrapper, string.Empty, StringComparison.Ordinal);
        }

        if (!s.Contains(Dsml, StringComparison.Ordinal))
        {
            return s;
        }

        // 收尾那一份的眼睛是 `</`（不是 `<`）—— 只剥开头 ⇒ 收盘标记留在正文里（2026-09-29 当场抓到过）。
        s = s.Replace("</" + Dsml[1..], "</", StringComparison.Ordinal);
        s = s.Replace(Dsml, string.Empty, StringComparison.Ordinal);

        return DropResidueLines(s);
    }

    /// <summary>丢掉「整行只是标记残骸」的那些行（**不换成空行** —— 那会平白多出空行）。</summary>
    private static string DropResidueLines(string s)
    {
        var lines = s.Split('\n');
        var kept = new List<string>(lines.Length);
        var dropped = false;
        foreach (var line in lines)
        {
            if (ResidueLine.IsMatch(line.Trim()))
            {
                dropped = true;
                continue;
            }

            kept.Add(line);
        }

        return dropped ? string.Join('\n', kept) : s;
    }

    /// <summary>这段文本里还有没有控制标记（诊断 / 闸门用）。</summary>
    public static bool HasAny(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        if (text.Contains(Dsml, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var wrapper in Wrappers)
        {
            if (text.Contains(wrapper, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
