namespace AgentRuntime.Core.Worker;

/// <summary>
/// **工具结果里「不可被凝结掉的那一行」**（§13.5 反面判据 ①：结论等价）。
///
/// <para><b>为什么必须有它</b>：结果凝结（A1）会把 <c>ToolResult</c> 的正文整体换成一条凝结文。
/// 但正文里有**别处要吃的事实**：
/// ① 终局断言（P2-1 <c>TaskTerminalAssertions.FactsOf</c>）从 <c>exec</c> 结果里读
///    「命令回显 + <c>exit 0</c>」判 <c>Verification</c>，从 <c>edit</c>/<c>write</c> 结果里读
///    「<c>落点 &lt;path&gt;</c>」判 <c>Mutation</c> / <c>Handoff</c>；
/// ② 人要在屏上看得见「这次改动落在哪」。</para>
///
/// <para><b>所以分流不是「整条换掉」，而是「保事实行 + 换掉大块」**：事实行**逐字**留在流里，
/// 只把大块原料换成凝结文与指针。这正是 §十·34 的口径：一处承载、另一处只留指针 ——
/// 但**指针本身也是正文**，不能把「落在哪」一起删掉。</para>
///
/// <para>纯函数：不读配置、不碰 IO。</para>
/// </summary>
public static class ToolFactLines
{
    /// <summary>事实行的长度上限（一条命令行可能很长；截断只影响可读性，不影响判据的前缀）。</summary>
    public const int MaxFactChars = 400;

    /// <summary>
    /// 取「必须逐字保留」的事实行（取不到 ⇒ 空串，**不猜**）。
    /// <para>只取**首行**：全仓的结果形状都保证「结论在前、原料在后」
    /// （<c>ExecTool.BuildBody</c> 先写 <c>exec … → exit N</c>；<c>FileTools</c> 先写 <c>… → 落点 …</c>），
    /// 所以首行就是判据要吃的那一行。</para>
    /// </summary>
    public static string PrefixOf(string? text, string? source)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var firstLine = FirstLine(text);
        if (firstLine.Length == 0)
        {
            return string.Empty;
        }

        var name = (source ?? string.Empty).Trim().ToLowerInvariant();
        var keep = name switch
        {
            "exec" => LooksLikeExecFact(firstLine),
            "edit" or "write" => firstLine.Contains("落点", StringComparison.Ordinal),

            // 没有 source（或未认得的工具）：按**形状**判，不按名字判（§十·32：按派生规则查）。
            _ => LooksLikeExecFact(firstLine) || firstLine.Contains("落点", StringComparison.Ordinal),
        };

        if (!keep)
        {
            return string.Empty;
        }

        return firstLine.Length <= MaxFactChars ? firstLine : firstLine[..MaxFactChars] + "…";
    }

    /// <summary><c>exec &lt;cmd&gt; → exit N</c> / <c>exec &lt;cmd&gt; → 超时被终止</c>（超时也算事实）。</summary>
    private static bool LooksLikeExecFact(string line)
        => line.StartsWith("exec ", StringComparison.Ordinal)
           && (line.Contains(" → exit ", StringComparison.Ordinal)
               || line.Contains(" → 超时被终止", StringComparison.Ordinal));

    private static string FirstLine(string text)
    {
        var idx = text.IndexOf('\n');
        var line = idx < 0 ? text : text[..idx];
        return line.TrimEnd('\r').Trim();
    }
}
