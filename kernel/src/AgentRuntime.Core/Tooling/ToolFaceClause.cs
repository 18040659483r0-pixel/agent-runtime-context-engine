namespace AgentRuntime.Core.Tooling;

/// <summary>
/// **工具面口径的唯一来源**（2026-09-29 主人令：worker 与 master 口径必须对齐，
/// 「只给一段典型用法」证明不够 —— 教就得照 master 那边怎么教的教）。
///
/// <para><b>为什么要有这个类</b>：同一套「怎么写 <c>[TOOL]</c>」此前**有两份文本** ——
/// master 侧在 <c>ToolRunner</c> 的拒绝话术里，worker 侧在worker 协议区里。两处一抄就分叉，
/// 而分叉的后果是**同一个形状一处认、一处不认**（真机：worker 8 个批次 19 次调用全是 <c>read</c>，
/// 因为worker 协议区只教了 <c>read</c>）。这里把口径收成一份，谁教都用它。</para>
///
/// <para><b>组成</b>：<see cref="Shape"/>（形状与写法，与拒绝话术**逐字同一份**）·
/// <see cref="Keys"/>（每把工具要哪些键）· <see cref="Pace"/>（节奏与省字：结果带指针 / 一次要齐 / 先定位再读窗口）。</para>
///
/// <para><b>纪律</b>：改这里 = 同时改到 master 与 worker 两边（这是它的全部意义）；
/// 两侧各有一道闸门（<c>ToolFaceTests</c> / <c>MinimalWorkerTests</c>）钉住「两边都含同一批口径字面」。</para>
/// </summary>
public static class ToolFaceClause
{
    /// <summary>
    /// **形状与写法**（2026-10-07 定案：**一个 XML 元素** · 块头 `[TOOL]` · 元素自定界）。
    /// <para>与 <c>ToolRunner.ToolFaceContract()</c> 的尾部**逐字同一份**（那条是 master 被拒时看到的话术）。</para>
    /// </summary>
    public const string Shape =
        "形状：**一个 XML 元素**（例：`[TOOL] <call><name>read</name><path>/abs/a.txt</path></call>`）——"
        + "成员按**名**取、顺序无关；多行正文直接写在成员里（**逐字**，换行 / 引号 / 正则元字符 / 中日文都不必转义；只有 `&` 与 `<` 要写成 `&amp;` / `&lt;`）；"
        + "很长或很复杂的正文**直接写到文件、把路径传进来**（`<path>…</path>`）；"
        + "**正文里含 `<` 或 `&` 时改用行式零转义写法**：`[TOOL] write` 换行 `path: …` 换行 `content<<` … `>>`（块内逐字，不转义）；"
        + "`<risk>none</risk>`（或 `<risk>可能弄坏什么</risk>`）是可选成员；"
        + "一次回复最多 4 个块；**元素自定界** —— `</call>` 之后的东西不属于它（**别把上下文行抄进回复**）；"
        // 2026-10-15：真机实测模型仍按 JSON 先验写 `[TOOL] read {"path": /abs}`（值没引号）⇒ 整篇拒。
        // 旧话术写「旧的行式/JSON 仍会被归一，但那是后路」= **在邀请它继续用 JSON**；改成直接说清代价。
        + "**不要写 `[TOOL] 名字 {…}`（旧 JSON 形状）** —— 它只在**完全合语法**时才被归一（少一个引号就整篇拒）。";

    /// <summary>每把工具的键（**唯一声明处**：工具名 → 键名数组；<see cref="Keys"/> 与 <see cref="MasterKeys"/> 都由它渲染）。
    /// <para>⚠️ 必须声明在使用者（<see cref="Keys"/> 静态字段）**之前** —— 静态字段初始化按声明次序跑，
    /// 反过来会在 <c>cctor</c> 里拿到 <c>null</c>（2026-09-29 当场：全量测试 20+ 条 TypeInitializationException）。</para></summary>
    public static readonly IReadOnlyDictionary<string, string[]> ToolKeys =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [ToolNames.Read] = ["path", "symbol", "maxLines", "offset", "limit"],
            [ToolNames.List] = ["path"],
            [ToolNames.Write] = ["path", "content"],
            [ToolNames.Edit] = ["path", "oldText", "newText"],
            [ToolNames.Exec] = ["command", "timeoutSeconds"],
            [ToolNames.Index] = ["symbol"],
            [ToolNames.Scope] = ["task"],

            // 网络工具第一批（协议 v75）：键与各工具 ArgumentNames **逐字同一份**（有闸门钉住）。
            [ToolNames.WebFetch] = ["url", "maxChars", "offset", "format"],
            [ToolNames.WebSearch] = ["query", "count", "recency"],
            [ToolNames.HttpRequest] = ["method", "url", "headers", "query", "body", "contentType", "authRef", "timeoutSeconds"],
            [ToolNames.WebDownload] = ["url", "path", "maxBytes", "conflict"],
            [ToolNames.WebSearchExtract] = ["urls", "maxCharsPerPage", "concurrency"],
        };

    /// <summary>
    /// **每把工具要哪些键**（与主协议第 5 条同一份枚举；这里只列**本面可用**的那几把）。
    /// <para>worker 的默认面是 read/list/write/edit/exec（<c>worker.tools</c> 可裁），故不含 index/scope。</para>
    /// <para><b>v31（2026-09-29）</b>：不再手写 —— 由 <see cref="ToolKeys"/> 按 <see cref="ToolNames.WorkerFace"/> **渲染**
    /// （master 侧同理用 <see cref="MasterKeys"/>）。手写的代价已实测：正文写 7 把而运行时只给 2 把（<b>口径分裂</b>），
    /// 模型照着正文点名被拒 ⇒ 一轮白烧。同一件事只能有一处声明（§十·17）。</para>
    /// </summary>
    public static readonly string Keys =
        "每把工具要哪些键：" + KeysText(ToolNames.WorkerFace)
        + "；要看某个定义就**一次按符号读**（read 的 symbol）—— 它直接给整段定义（首行到闭合括号），比一页页翻大文件强。";

    /// <summary>**master 面的键清单**（v31）—— 供协议正文第 5 条**同一份来源**渲染（不再手写）。</summary>
    public static string MasterKeys => KeysText(ToolNames.MasterFace);

    /// <summary>
    /// 把一个**面**渲染成键清单（<c>read{path,…} list{path} …</c>）—— 面里的每一把都必须在
    /// <see cref="ToolKeys"/> 里有声明；缺一条 ⇒ 响亮报（不静默漏教，否则又是「口径分裂」）。
    /// </summary>
    public static string KeysText(IReadOnlyList<string> face)
    {
        var parts = new List<string>();
        foreach (var tool in face)
        {
            if (!ToolKeys.TryGetValue(tool, out var keys))
            {
                throw new InvalidOperationException(
                    $"工具面里的 {tool} 没有在 ToolFaceClause.ToolKeys 里声明键名 —— 键清单是唯一声明处，不得漏教。");
            }

            parts.Add(tool + "{" + string.Join(",", keys) + "}");
        }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// **节奏与省字**（与主协议第 2 / 11 条同一口径）：结果带指针、一次要齐、先定位再读窗口、绝不整份倒。
    /// </summary>
    public const string Pace =
        "结果一律**带上限并给出继续的指针**（用 offset 接着读同一段，不要重读）；"
        + "需要什么**一次要齐**（不是一轮要一个窗口）；**先定位再读窗口**（grep / find 找位置 → 只读那一段），"
        + "**绝不整份倒**；工具结果回来就**接着干**，不要停在原地。";
}
